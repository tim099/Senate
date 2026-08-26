// 區塊職責：Git Submodule 狀態頁 —— **唯讀**。列出目標 repo 的所有 submodule：
//           現在在哪條 branch、目標是哪條（以及那個目標是哪一層解析出來的）、髒不髒、
//           領先落後多少、那把尺有多舊。
// 物理意義：概念取自 Unity 端的 UCL_GitSubmoduleSyncPage，但**這一頁刻意先不放寫入鈕**。
//           理由是宿主形狀不同，不是保守：CLI 一次呼叫一顆 process，
//           一輪 fetch＋pull＋push 跨十幾個 submodule 是分鐘級的事，
//           塞進「按鈕按下去那一幀」在 CLI 模式做不到 ⇒ 會變成一顆按了沒事的鈕，
//           而那比沒有鈕糟。寫入端走 `senate submodule sync`（同步跑完印報告），
//           本頁把那一行指令**印出來**，讓看得到狀態的人知道下一步要打什麼。
// 數值影響：掃描唯讀。`--toggle submodule/fetch` 開著時會先逐顆 fetch（只動 remote-tracking ref，
//           不碰工作目錄），關著時 ahead/behind 以上次 fetch 為準 —— 而那個新鮮度**逐列標**。
// ⚠ 顏色：共用層沒有顏色 API（一份頁面碼要同時輸出純文字，顏色傳不過去）⇒
//   狀態一律用字形（⛔ / ⚠ / ✓ / ⏭）表達。那不是降級，是讓四種 renderer 講同一句話。
using SCP.Core.Git;
using SCP.Core.Gui;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class SubmoduleSyncPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;

    /// <summary>上一次掃描的照片。null ＝ 還沒掃過（跟「掃過但沒有 submodule」不同形）。</summary>
    SubmoduleScanResult? m_Scan;

    /// <summary>掃描時用的參數快照 —— 表格要說出「這份照片是用什麼設定拍的」。</summary>
    string m_ScannedDefaultBranch = "";

    /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
    public SubmoduleSyncPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "submodule";

    public override string Title => "Submodule 狀態";

    /// <summary>列進入口頁的「診斷」組（跟 Doctor 同一組 —— 它們回答的都是「現在是什麼狀態」）。</summary>
    public override string? MenuGroup => "診斷";

    /// <summary>
    /// 掃描在 <c>OnPush</c> 不在建構子 —— 頁面目錄為了讀標題會建一次實例再丟掉，
    /// 建構子跑 git 的話「列出有哪些頁」就會去跑一輪 submodule status。
    /// </summary>
    public override void OnPush()
    {
        base.OnPush();
        Rescan(iFetch: false);
    }

    protected override void ToolBarButtons(SCP_Ui g)
    {
        if (g.Button("重新掃描", "submodule/rescan")) Rescan(iFetch: false);
        // fetch 分開一顆：掃描要快（進頁面自動跑），fetch 走網路且 submodule 多時要數十秒。
        // ahead/behind 的準確度依賴 fetch，所以按鈕文案把這件事講明。
        if (g.Button("Fetch 全部後掃描", "submodule/rescan-fetch")) Rescan(iFetch: true);
    }

    protected override void DrawContent(SCP_Ui g)
    {
        string aTarget = DrawTargetPicker(g);
        string aDefaultBranch = g.TextField("全域預設 branch", m_ScannedDefaultBranch, "submodule/default-branch");

        g.Note("目標 branch 解析順序：指定 ＞ .gitmodules 的 branch 欄 ＞ 上面這格 ＞ 啟發式"
               + "（只有一條分支就用它／否則 master，沒 master 才 main）。四層都空 ⇒ 那一列跳過，"
               + "**不會拿「目前所在」頂替**。");

        // 設定改了就要重掃 —— 全域預設會改變每一列的目標，而一張「用舊設定拍的照片」
        // 配上新設定的說明文字，是最容易讓人看錯的組合。
        if (aDefaultBranch != m_ScannedDefaultBranch || (m_Scan != null && m_Scan.Root != aTarget))
        {
            m_ScannedDefaultBranch = aDefaultBranch;
            Rescan(iFetch: false, iRoot: aTarget);
        }

        g.Space();

        if (m_Scan == null) { g.Note("還沒掃描。按上面的「重新掃描」。"); return; }

        if (!m_Scan.Ok)
        {
            // 「問不到」跟「沒有 submodule」不得同形 —— 這一格就是那個分界。
            g.Note($"✗ 掃描失敗：{m_Scan.Error}");
            return;
        }

        using (g.Box($"{m_Scan.Root}"))
        {
            g.Note(m_Scan.Fetched
                ? "已 fetch ⇒ 下面的 ahead/behind 是即時值"
                : "未 fetch ⇒ ahead/behind 以各列自己「上次 fetch」為準（逐列標在最後一欄）");
        }

        if (m_Scan.Items.Count == 0)
        {
            g.Note("這個 repo 沒有 submodule（掃描成功，真的是零）。");
            return;
        }

        using (g.Table("submodule", "目前", "目標", "來源", "工作區", "↑ahead ↓behind", "remote", "上次 fetch"))
        {
            foreach (var aItem in m_Scan.Items)
            {
                g.TableRow(
                    aItem.Entry.Path,
                    CurrentText(aItem),
                    aItem.TargetBranch.Length > 0 ? aItem.TargetBranch : "—",
                    SubmoduleScan.SourceText(aItem.TargetSource),
                    DirtyText(aItem),
                    AheadBehindText(aItem),
                    aItem.Remotes.Count == 0 ? (aItem.Entry.Uninitialized ? "-" : "⚠ 無")
                        : (aItem.Remotes.Count > 1 ? "⇈ " + string.Join(" / ", aItem.Remotes) : aItem.Remotes[0]),
                    aItem.FetchAgeText);
            }
        }

        DrawNotes(g);
        DrawNextStep(g);
    }

    /// <summary>
    /// 掃哪個 repo。
    /// <para>候選＝Senate 自己 ＋ 設定檔裡**啟用且可用**的專案。⚠ 停用／路徑不存在的仍然列出來
    /// 並標明原因 —— 「我關掉它」「設定壞了」「沒設定過」是三件事。</para>
    /// </summary>
    string DrawTargetPicker(SCP_Ui g)
    {
        var aOptions = new List<SCP_GuiOption>();
        aOptions.Add(new SCP_GuiOption(m_Model.RepoRoot, $"Senate 自己（{m_Model.RepoRoot}）"));
        foreach (var aProject in m_Model.Projects)
        {
            string aLabel = aProject.State switch
            {
                ProbeState.Ok => aProject.Enabled ? aProject.Name : $"{aProject.Name}（停用）",
                ProbeState.Missing => $"{aProject.Name}（路徑不存在）",
                ProbeState.NotGitRepo => $"{aProject.Name}（非 git repo）",
                _ => $"{aProject.Name}（未設定）",
            };
            if (aProject.Root.Length > 0) aOptions.Add(new SCP_GuiOption(aProject.Root, aLabel));
        }
        return g.Dropdown("掃哪個 repo", aOptions, m_Scan?.Root ?? m_Model.RepoRoot, "submodule/target");
    }

    /// <summary>逐列的說明 —— 表格說「是什麼」，這裡說「所以會怎樣」。</summary>
    void DrawNotes(SCP_Ui g)
    {
        if (m_Scan == null) return;
        foreach (string aWarning in m_Scan.Warnings) g.Note(aWarning);

        foreach (var aItem in m_Scan.Items)
        {
            string aPath = aItem.Entry.Path;
            if (aItem.Entry.Uninitialized)
            {
                g.Note($"⛔ {aPath}：未 init（內容不在本機）—— 任何操作都會跳過它。"
                       + "要它進來：git submodule update --init");
                continue;
            }
            if (aItem.Entry.Flag == SCP_GitSubmoduleFlag.Conflict)
                g.Note($"⛔ {aPath}：有合併衝突 —— 人要先處理，工具一律不碰。");
            if (aItem.Entry.Flag == SCP_GitSubmoduleFlag.Unknown)
                g.Note($"⚠ {aPath}：認不得的 submodule status 旗標 —— 保守當成不要動它。");
            if (aItem.Entry.Flag == SCP_GitSubmoduleFlag.ShaMismatch)
                g.Note($"⚠ {aPath}：目前 SHA 與父層記的 gitlink 不同 ⇒ **父層還沒 bump**，"
                       + "同事 pull 父層拿到的還是舊版。");
            if (aItem.TargetSource == TargetBranchSource.None)
                g.Note($"⏭ {aPath}：解析不到目標 branch（四層全空）—— 會被跳過。"
                       + "填上面那格，或在 .gitmodules 寫 branch =。");
            if (aItem.Dirty == SCP_GitDirtyState.Dirty)
                g.Note($"⚠ {aPath}：dirty（有未 commit 的追蹤檔修改）⇒ 切 branch 與 pull 都會**跳過它**。"
                       + "push 不受影響（推的是已 commit 的東西）。");
            if (aItem.Dirty == SCP_GitDirtyState.Unknown)
                g.Note($"⚠ {aPath}：git status 問不到 —— 狀態不明一律當成擋下處理。");
            if (aItem.IsDetached && aItem.TargetBranch.Length > 0)
                g.Note($"⛔ {aPath}：detached HEAD ⇒ pull 不會動它（pull 不負責移動 branch）。"
                       + $"要一次到位：連 --checkout 一起跑（會先確認 HEAD 已在 {aItem.TargetBranch} 歷史上才切）。");
            if (!aItem.AheadBehind.Known && !aItem.IsDetached)
                g.Note($"⚠ {aPath}：沒有 upstream ⇒ ahead/behind **未知**（不是 0 —— 0 的意思是對齊）。");
            if (aItem.Remotes.Count == 0)
                g.Note($"⚠ {aPath}：沒有設定任何 remote ⇒ push 沒地方推，會跳過並列出。");
        }
    }

    /// <summary>
    /// 下一步該打什麼。
    /// <para>本頁不放寫入鈕（見檔頭），所以**必須把等價指令印出來** ——
    /// 一個「只能看不能動」的畫面如果不指路，看得到問題的人就卡在這裡了。</para>
    /// </summary>
    void DrawNextStep(SCP_Ui g)
    {
        if (m_Scan == null) return;
        g.Space();
        using (g.Box("要動手的話（寫入端在 CLI，不在這一頁）"))
        {
            string aRoot = m_Scan.Root;
            string aBranchArg = m_ScannedDefaultBranch.Length > 0 ? $" --branch {m_ScannedDefaultBranch}" : "";
            g.Note($"只把本地弄到最新（不碰遠端）：senate submodule sync --root \"{aRoot}\"{aBranchArg} --checkout --pull");
            g.Note($"推出去（會寫遠端 ⇒ 必須加 --yes 明示）：senate submodule sync --root \"{aRoot}\"{aBranchArg} --push --yes");
            g.Note("先看它打算做什麼：把 --checkout/--pull/--push 換成 status，或加 --dry-run。");
        }
    }

    void Rescan(bool iFetch, string? iRoot = null)
    {
        string aRoot = iRoot ?? m_Scan?.Root ?? m_Model.RepoRoot;
        m_Scan = SubmoduleScan.Scan(aRoot, iFetch,
            string.IsNullOrWhiteSpace(m_ScannedDefaultBranch) ? null : m_ScannedDefaultBranch);
    }

    static string CurrentText(SubmoduleReading iItem)
    {
        if (iItem.Entry.Uninitialized) return "⛔ 未 init";
        if (iItem.CurrentBranch == null) return "⚠ 問不到";
        if (iItem.CurrentBranch == SCP_Git.DetachedHead) return "⛔ detached";
        return iItem.OnTarget ? "✓ " + iItem.CurrentBranch : "⚠ " + iItem.CurrentBranch;
    }

    static string DirtyText(SubmoduleReading iItem)
    {
        if (iItem.Entry.Uninitialized) return "-";
        return iItem.Dirty switch
        {
            SCP_GitDirtyState.Clean => "✓ 乾淨",
            SCP_GitDirtyState.Dirty => "⚠ dirty",
            _ => "⚠ 問不到",
        };
    }

    static string AheadBehindText(SubmoduleReading iItem)
    {
        if (iItem.Entry.Uninitialized) return "-";
        // ⚠ 未知不顯示 0 —— 0 是「對齊」，那是一個答案。
        if (!iItem.AheadBehind.Known) return "未知";
        return $"↑{iItem.AheadBehind.Ahead} ↓{iItem.AheadBehind.Behind}";
    }
}
