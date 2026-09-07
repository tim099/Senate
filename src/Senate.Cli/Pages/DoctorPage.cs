// 區塊職責：環境／專案關聯的**診斷頁** —— 用 Gui（中間層）畫，一份頁面碼四種輸出。
// 物理意義：這一頁刻意只做「取讀數並攤開」。它同時是三件事的證明：
//           ① 撰寫端手感是 GUILayout（一頁一個方法、從上往下寫）
//           ② 同一份碼可以輸出成純文字（現在）與 ImGui 視窗（之後），頁面碼一行都不用改
//           ③ 沒有視窗也能驗收 UI —— 文字輸出可以 diff、可以貼給人看
// 數值影響：唯讀。它不改任何設定，也不動任何 repo 的 index。
using Senate.Core;
using SCP.Core.Git;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class DoctorPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;

    /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
    public DoctorPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "doctor";

    /// <summary>標題交給 controller 畫（麵包屑也吃它）—— 所以這裡**不含**取讀數次數，那是讀數不是身分。</summary>
    public override string Title => "Senate 環境檢查";

    /// <summary>列進入口頁的「診斷」組。</summary>
    public override string? MenuGroup => "診斷";

    EnvReading m_Env => m_Model.Env;
    IReadOnlyList<ProjectReading> m_Projects => m_Model.Projects;

    /// <summary>
    /// 工具列的鈕 —— 這一頁的兩個動作都是「對這一頁做的事」，所以放工具列而不是內容底部。
    /// ⚠ id 沿用舊的（`doctor/refresh` / `doctor/open-config`）：那是契約，
    /// 版面搬家不可以順手換掉別人腳本裡的字。
    /// </summary>
    protected override void ToolBarButtons(SCP_Ui g)
    {
        if (g.Button("重新取讀數", "doctor/refresh")) m_Model.Refresh();
        if (g.Button("開啟設定檔", "doctor/open-config")) OpenConfig();
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note($"第 {m_Model.RefreshCount} 次取讀數");

        using (g.Box("執行環境"))
        {
            using (g.Table("項目", "讀數", "判定"))
            {
                // ⚠ 這一列排最前面（TASK-0138）：doctor 該回答的第一個問題是
                //   「**你現在跑的是哪一顆**」，而不是「這台機器裝了什麼」。
                // 🩸 為什麼補它：2026-09-06 @basecamp 修好一個缺陷、@summit 回驗，
                //   而活體讀數**沒有跟著變** —— 那一刻「修法沒生效」與「二進位還沒重建」
                //   在讀數上完全同形。分開它們的是去 stat `senate.exe` 的 mtime 再跟 commit 時間比，
                //   而那個量法**不在任何指路牌上**（`senate --version` 還回「認不得的指令」）。
                //   ⇒ 代價落在下一個回驗的人身上，所以擋在這裡。
                // 📌 值不是新造的：`ServerHost.BuildId` 早就在（AssemblyInformationalVersion，
                //   由 build.sh／build.ps1 在 publish 時塞入 git SHA＋時間）。它讀的是
                //   **本執行檔自己**，跟 Server 無關 —— 在此之前它只在 Server 卡片上露過臉。
                // ⚠ `unversioned` 不是壞掉，是**定語**：`dotnet run`（Debug）就會是它，
                //   而「Debug 在跑」正是最需要被看見的那一種 ⇒ 判定欄標 `· Debug` 不標 ✗。
                string aBuild = ServerHost.BuildId;
                g.TableRow("本執行檔 build（AssemblyInformationalVersion）", aBuild,
                    aBuild == "unversioned" ? "· Debug" : "✓");
                // ⚠ 上面那一列只回答「我是哪一顆」；**它不會說這顆是不是舊的**。
                // 🩸 TASK-0138 QA（basecamp 2026-09-07）指出的那半：
                //   知道要懷疑的人可以自己拿它去比 HEAD —— 而這張單存在的理由正是
                //   **人不會想到要懷疑**（summit 09-06 那天就是不知道要問）。⇒ 那一步改成工具自己做。
                // ⛔ 落後**不擋任何事**（原單拍板：這一格要的是看得見，不是攔下來）——
                //   落後常常是合法的（別人剛推了不相干的 commit）。
                (string aVs, string aVsVerdict) = BuildVsHead(aBuild);
                g.TableRow("　↳ 對照目前 HEAD", aVs, aVsVerdict);
                g.TableRow(".NET SDK（dotnet --version）", m_Env.DotnetSdkVersion ?? "(問不到)",
                    m_Env.DotnetSdkVersion == null ? "✗" : "✓");
                g.TableRow("執行期（Environment.Version）", m_Env.RuntimeVersion, "·");
                g.TableRow("git", m_Env.GitVersion?.ToString() ?? "(問不到)", m_Env.GitOkForPathspec ? "✓" : "✗");
                g.TableRow("git ≥ 2.25（--pathspec-from-file）",
                    m_Env.GitOkForPathspec ? "支援" : "不支援 —— pathspec 提交那條護欄會失效",
                    m_Env.GitOkForPathspec ? "✓" : "✗");
                g.TableRow("設定檔", m_Env.ConfigPath, m_Env.ConfigExists ? "✓" : "尚未 init");
            }
            if (!m_Env.ConfigExists)
                g.Note("還沒有 SenateData/config/senate.local.json —— 跑 `senate init` 會從同目錄的 senate.local.example.json 生一份（不覆寫既有檔）");
        }

        g.Space();
        g.Title($"關聯的專案（{m_Projects.Count}）");

        if (m_Projects.Count == 0)
        {
            g.Note("設定檔裡沒有任何專案。編輯 SenateData/config/senate.local.json 的 projects[] 加上專案根目錄。");
            g.Note("⚠ 「沒設定」與「設定了但路徑不存在」是兩件事 —— 後者會在下面列成 Missing，不會靜默消失。");
            return;
        }

        using (g.Table("專案", "狀態", "分支", "工作區", "index", "Editor", "資料根"))
        {
            foreach (var p in m_Projects)
            {
                g.TableRow(
                    p.Enabled ? p.Name : $"{p.Name}（停用）",
                    StateText(p.State),
                    p.Branch ?? "-",
                    p.DirtyCount is int d ? $"{d} 筆改動" : "-",
                    p.StagedCount > 0 ? $"⚠ {p.StagedCount} 已 staged" : "乾淨",
                    p.EditorLikelyRunning ? $"在跑（{p.EditorHeartbeatAgeText}）" : (p.EditorHeartbeatAgeText ?? "-"),
                    p.AgentCommandsRootExists ? "✓" : (p.AgentCommandsRoot == null ? "-" : "✗ 不存在"));
            }
        }

        foreach (var p in m_Projects)
        {
            if (p.State == ProbeState.Ok && p.StagedCount > 0)
                g.Note($"{p.Name}：index 已有 {p.StagedCount} 個 staged 檔 ⇒ 自動 commit 會**擋下這個 repo**（先自己 commit 或 unstage）");
            if (p.State == ProbeState.Ok && p.EditorLikelyRunning)
                g.Note($"{p.Name}：Unity Editor 正在 tick ⇒ 自動 commit 會讓它做，本工具不動 index（不動別人正在寫的東西）");
            if (p.State == ProbeState.Missing)
                g.Note($"{p.Name}：設定的 root 不存在（{p.Root}）—— 這是設定壞了，不是「這個專案沒事」");
            if (p.State == ProbeState.NotGitRepo)
                g.Note($"{p.Name}：{p.Root} 不是 git repo");
            if (p.State == ProbeState.Ok && !p.AgentCommandsRootExists && p.AgentCommandsRoot != null)
                g.Note($"{p.Name}：資料根解析到 {p.AgentCommandsRoot}，但那個目錄不存在");
        }

    }

    void OpenConfig()
    {
        // 開檔案總管／預設編輯器。⚠ headless 環境會失敗 —— 失敗要說出來，不要當作按了沒事
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = m_Env.ConfigPath,
                UseShellExecute = true,
            });
        }
        catch (Exception e) { Console.Error.WriteLine($"⚠ 開啟設定檔失敗：{e.Message}"); }
    }

    static string StateText(ProbeState s) => s switch
    {
        ProbeState.Ok => "可用",
        ProbeState.Missing => "路徑不存在",
        ProbeState.NotGitRepo => "非 git repo",
        _ => "未設定",
    };

    // ===========================================================
    // 區塊職責：把 build stamp 的 SHA 拿去跟**本機這個 repo 的 HEAD** 比，回 (讀數, 判定)。
    //
    // 物理意義：TASK-0138 的建議 (B)。上一列說「我是哪一顆」，這一列說「那一顆是不是舊的」。
    //
    // ⚠ 五種答案**刻意各自不同形** —— 這一格最貴的錯是把「我不知道」印成「沒落後」：
    //   ① `unversioned`（Debug 組建）      ⇒ 沒有 SHA 可比，判定 `·`
    //   ② 找不到 repo（exe 被複製到別處）  ⇒ 「問不到」，判定 `·`　⛔ 不是 ✓
    //   ③ 那顆 SHA 不在這個 repo 裡        ⇒ 「不是這個 repo 建的」，判定 `·`
    //   ④ ＝ HEAD                          ⇒ ✓
    //   ⑤ 落後 N 顆                        ⇒ ⚠（**只標記，不擋**）
    //
    // 數值影響：純讀（`git rev-parse` / `cat-file -e` / `rev-list --count`），不寫任何檔。
    //   repo 位置由**本執行檔所在目錄往上找**（最多五層）—— exe 通常住在 `<repo>/publish/`。
    //   ⛔ 不寫死路徑：寫死的那一行在別台機器上會安靜地指到不存在的地方。
    // ===========================================================
    static (string Reading, string Verdict) BuildVsHead(string iBuildId)
    {
        if (iBuildId == "unversioned")
            return ("Debug 組建沒有嵌 SHA ⇒ 沒有東西可比（⛔ 這不是「沒落後」）", "·");

        // stamp 形狀是 `<sha>[-dirty].<UTC>`；取第一個 `.` 之前、去掉 `-dirty`。
        string aSha = iBuildId.Split('.')[0];
        bool aDirty = aSha.EndsWith("-dirty", StringComparison.Ordinal);
        if (aDirty) aSha = aSha.Substring(0, aSha.Length - "-dirty".Length);
        if (aSha.Length == 0)
            return ($"stamp `{iBuildId}` 解不出 SHA ⇒ 不猜", "·");

        string? aRepo = FindRepoUpwards(AppContext.BaseDirectory, 5);
        if (aRepo == null)
            return ("找不到本執行檔所屬的 git repo（被複製到別處？）⇒ **問不到**，不是沒落後", "·");

        var aHeadR = SCP_Git.Run(aRepo, "rev-parse", "--short", "HEAD");
        if (!aHeadR.Ok)
            return ($"問不到 HEAD（{SCP_Git.ReasonLine(aHeadR.StdErr)}）", "·");
        string aHead = SCP_Git.FirstLine(aHeadR.StdOut);

        // 那顆 SHA 在不在這個 repo 裡 —— 不在就代表這顆 exe 不是這裡建的，
        // ⛔ 那時候比「落後幾顆」是拿兩條不同的歷史相減，答案沒有意義。
        var aHasR = SCP_Git.Run(aRepo, "cat-file", "-e", aSha + "^{commit}");
        if (!aHasR.Ok)
            return ($"`{aSha}` 不在這個 repo（{Path.GetFileName(aRepo)}）裡 ⇒ 不是這裡建的，無法比較", "·");

        if (string.Equals(aSha, aHead, StringComparison.OrdinalIgnoreCase))
            return ($"＝目前 HEAD `{aHead}`" + (aDirty ? "（build 當時工作區是髒的）" : ""), "✓");

        var aCntR = SCP_Git.Run(aRepo, "rev-list", "--count", aSha + "..HEAD");
        string aCnt = aCntR.Ok ? SCP_Git.FirstLine(aCntR.StdOut) : "?";
        // 反方向也要說得出來：HEAD 在那顆之前（別人 reset 過／checkout 到舊點）也是「不一致」，
        // 而它跟「落後」的處置不同 —— 前者要問發生什麼事，後者只要重 build。
        var aAheadR = SCP_Git.Run(aRepo, "rev-list", "--count", "HEAD.." + aSha);
        string aAhead = aAheadR.Ok ? SCP_Git.FirstLine(aAheadR.StdOut) : "?";
        if (aCnt == "0" && aAhead != "0")
            return ($"這顆 exe **比 HEAD 新** {aAhead} 顆（HEAD 是 `{aHead}`）—— 工作區被切回舊點？", "⚠");
        return ($"**落後 {aCnt} 顆**：build `{aSha}` → HEAD `{aHead}`"
                + (aDirty ? "（且 build 當時工作區是髒的）" : "")
                + " ⇒ 要最新行為請重 build（⛔ 落後本身不是錯）", "⚠");
    }

    /// <summary>從 <paramref name="iStart"/> 往上找第一個 git repo；找不到回 null（⛔ 不回空字串當「找到了」）。</summary>
    static string? FindRepoUpwards(string iStart, int iMaxLevels)
    {
        try
        {
            var aDir = new DirectoryInfo(iStart);
            for (int i = 0; i <= iMaxLevels && aDir != null; ++i, aDir = aDir.Parent)
                if (SCP_Git.IsRepo(aDir.FullName)) return aDir.FullName;
        }
        catch { /* 路徑問不到就當找不到 —— 這一格回 null 比丟例外有用 */ }
        return null;
    }
}
