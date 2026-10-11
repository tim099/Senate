// 區塊職責：**自動 Commit 頁** —— 把機器自動生成的檔分群，勾選後每群各自成一筆 commit，訊息自動生成。
// 物理意義：TASK-0340。規則與引擎在 SCP_Core
//           （SCP_AutoCommitRules／SCP_AutoCommitConfig／SCP_AutoCommit），本頁只剩「畫、勾、按」；
//           `senate cmd auto-commit` 走同一支引擎 ⇒ 同一個檔在頁面與 Cmd 被分到同一群，這是結構不是巧合。
//           ⭐ Tim 2026-09-30 兩條拍板：
//             ① **不再分「AgentCommands 本層／Persona 信件庫」兩種模式** —— 全部 repo 一次掃完、一張清單。
//             ② **不再判斷 persona 是否在線** —— 自動 commit 管理的部分不應該手動 commit。
//           **按鈕觸發，不是背景全自動**（Tim 2026-08-07）：按下去之前分群結果與完整檔案清單全攤在畫面上。
// 數值影響：掃描唯讀；commit 只寫各 repo 自己的 history（不 push、不動父層 pointer）。
//           掃描與提交都丟背景 job（會重畫的宿主）或就地跑完（純文字／指令驅動的宿主）。
//           持久化兩格：「掃描根目錄」與「具名群的預設勾選」，顯式按「儲存本頁設定」才寫（SenatePageStore）。
//           ⭐ 掃描根目錄可設（TASK-0462，Tim 2026-10-07「參考 SubmoduleSyncPage 可以設定根目錄」）：
//             草稿欄位 → 按「套用」才生效 → 生效值住 session（跨 process）→ 存檔記住上一次；沒設過＝資料根。
//             換根＝換戳記＋清待確認 ⇒ 對 A 勾的東西不會被帶去提交 B。
// ⚠ 勾選的 id 帶**掃描戳記**（`autocommit/<stamp>/<repo>/<key>`）：CLI 每次呼叫都是新 process、勾選住 session，
//   而語意是「特殊群的一次性勾選，掃描一次用完即棄」（預設值是裝填好的槍）。
//   沒有 SetToggle 可以把它清掉 ⇒ 換一個戳記，舊勾選就自然失效；具名群的預設由存檔值重新給。
// ⚠ 提交走**兩段式確認**（共用層沒有 modal；兩段式用既有節點組出來 ⇒ 四種驅動方式天生就會，CLI 可重放）。
// @doc-sync: <SCP_Core>/Docs~/AutoCommit.md（本頁的操作與設定編輯區）
using SCP.Core.Git;
using SCP.Core.Gui;
using SCP.Core.Paths;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class AutoCommitPage : SCP_GuiToolPage
{
    public const string PageKey = "auto-commit";

    readonly SenateModel m_Model;

    /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
    public AutoCommitPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "自動 Commit";

    /// <summary>跟 Submodule 狀態頁同一組 —— 它們都在回答「這些 repo 現在什麼狀態、要不要動手」。</summary>
    public override string? MenuGroup => "工具";

    // ── session 裡的內部狀態（跨 process 活得下來）───────────────────
    const string StampId = "autocommit/stamp";
    const string PendingId = "autocommit/pending";
    const string CfgGenId = "autocommit/cfg/gen";
    const string CfgPickId = "autocommit/cfg/pick";
    /// <summary>掃描根目錄的**草稿**（打字欄位）。按「套用」才寫進 <see cref="RootAppliedId"/>。</summary>
    const string RootFieldId = "autocommit/root";
    /// <summary>
    /// 掃描根目錄的**生效值** —— 住 session，跨 process 活得下來（同 SubmoduleSyncPage 的 `submodule/root/applied`）。
    /// ⚠ 內部狀態不是畫面元件 ⇒ `--set` 會被擋；CLI 換根走 `--set autocommit/root=…` ＋ `--click autocommit/root/apply`，
    /// 跟人在畫面上做的動作完全一樣。
    /// </summary>
    const string RootAppliedId = "autocommit/root/applied";

    /// <summary>檔案清單最多畫幾行 —— 酒館訊息一天數百檔，全畫會把版面撐爛；其餘只報數。</summary>
    const int FileListCap = 300;

    // ── 頁面狀態 ─────────────────────────────────────────────────────
    AutoCommitJob? m_Job;
    /// <summary>上一張掃描照片。null ＝ 還沒掃過（跟「掃過但全乾淨」不同形）。</summary>
    AutoCommitProgress? m_Scan;
    /// <summary>上一輪提交的結果（給報告區；null ＝ 這個 session 還沒提交過）。</summary>
    AutoCommitProgress? m_LastCommit;
    /// <summary>擋下動作時要說的那句（null ＝ 沒有）。</summary>
    string? m_Message;
    /// <summary>正在跑的 job 對著哪個根（起 job 時記下）。</summary>
    string m_JobRoot = "";
    /// <summary><see cref="m_Scan"/> 是對哪個根拍的。⛔ 跟生效值對不上的照片不畫、不拿來提交。</summary>
    string m_ScanRoot = "";
    /// <summary>上一次「套用」被擋下的理由（null ＝ 沒被擋過）。草稿留著，不替他清掉。</summary>
    string? m_RootRejected;
    /// <summary>上一次「貼上」的結果（純顯示）。</summary>
    string? m_PasteMessage;

    // ===========================================================
    // 持久化：具名群的預設勾選（顯式儲存才寫）
    // ===========================================================
    /// <summary>
    /// 存檔形狀。key 一律是 <c>&lt;repo 顯示名&gt;:&lt;群 key&gt;</c>。
    /// <para>記「跟規則預設不一樣的那幾格」而不是記全部 —— 規則新增一群時，舊存檔不會把它靜默關掉。</para>
    /// <para>⚠ 特殊群（__other／__other_untracked／__subptr）**永遠不持久化**：它們每次的內容都不同，
    /// 上次的授權不該自動延續到這次。</para>
    /// </summary>
    public sealed class SavedSettings
    {
        /// <summary>存檔的掃描根目錄。空字串 ＝ 跟著資料根（選回資料根時存成空，資料根搬家時才不會指著舊家）。</summary>
        public string Root { get; set; } = "";
        public List<string> DisabledGroups { get; set; } = new();
        public List<string> EnabledGroups { get; set; } = new();
    }

    SavedSettings m_Saved = new();
    string? m_SaveMessage;

    // ── 設定檔編輯區（.ucl_autocommit.json）的快取 ──
    sealed class ConfigEntry
    {
        public string Root = "";
        public string Name = "";
        public SCP_AutoCommitConfigLoad Load = new();
    }
    readonly List<ConfigEntry> m_Configs = new();
    string? m_ConfigMessage;

    public override void OnPush()
    {
        base.OnPush();
        // 小 IO（兩個設定檔層級的讀取），不跑 git —— 掃描由 DrawContent 驅動（頁面目錄會建一次實例再丟掉，建構子不碰磁碟）。
        m_Saved = SenatePageStore.Load<SavedSettings>(m_Model.RepoRoot, PageKey,
            iWarn => m_SaveMessage = $"⚠ {iWarn}") ?? new SavedSettings();
        // 設定編輯區要讀「生效的根」底下的 .gitmodules，而生效值住 session ⇒ 等 DrawContent 拿到 g 再讀（見 m_ConfigRoot）。
    }

    // ===========================================================
    // 掃描根目錄（TASK-0462）
    // ===========================================================
    // 區塊職責：「掃哪個 repo」的單一真相源 —— session 生效值 ＞ 存檔值 ＞ 資料根。
    // 物理意義：掃描根 ＝ 引擎 Discover 的「父層」那一格（它自己＋它 .gitmodules 裡有設定檔的 submodule）。
    //           信件庫只在 letters 根**落在掃描根底下**時一起掃 —— 對著 LY 掃卻收 Valhalla 的信件庫，
    //           會把兩棵樹的提交混在同一張確認單上。
    // ⚠ 資料根／letters 根本身仍然**不在本頁存**（問宿主那一格，見 ISCP_GuiAppContext）；本頁存的只是「這頁要掃哪個根」。

    /// <summary>宿主給的資料根（取不到就是空字串）。</summary>
    string DataRootValue
    {
        get
        {
            SCP_PathResolution aData = m_Model.AgentCommandsRoot;
            return string.IsNullOrEmpty(aData.Error) ? aData.Value ?? "" : "";
        }
    }

    /// <summary>根目錄的預設：存檔值 ＞ 資料根。所有讀 <see cref="RootAppliedId"/> 的 fallback 都走這裡（只寫一份）。</summary>
    string DefaultRoot => m_Saved.Root is { Length: > 0 } aRoot ? aRoot : DataRootValue;

    string AppliedRoot(SCP_Ui g) => g.FieldValue(RootAppliedId, DefaultRoot);

    /// <summary>現在的根是**誰決定的** —— 畫面上要說得出來（驗收②）。</summary>
    string RootOrigin(SCP_Ui g)
    {
        if (g.FieldValue(RootAppliedId, "").Length > 0) return "本頁套用的值（這個 session）";
        if (m_Saved.Root.Length > 0) return $"存檔值（{SenatePageStore.DefaultPath(m_Model.RepoRoot)}）";
        SCP_PathResolution aData = m_Model.AgentCommandsRoot;
        return $"預設＝資料根（{aData.Origin}）";
    }

    /// <summary>
    /// 根目錄能不能用。回 null ＝ 可以；否則是給人讀的理由。
    /// <para>⛔ 判準是「目錄在 **且** 它是 repo 根（有 `.git` 目錄或檔）」：
    /// repo 底下的子目錄 git 也跑得動，但 Discover 讀的是**那一層的** `.gitmodules`，給子目錄會安靜地少掃一截。</para>
    /// </summary>
    static string? RootProblem(string iRoot)
    {
        if (iRoot.Length == 0) return "路徑是空的";
        if (!Directory.Exists(iRoot)) return $"這個路徑不存在：{iRoot}";
        string aGit = Path.Combine(iRoot, ".git");
        if (!Directory.Exists(aGit) && !File.Exists(aGit)) return $"這不是 git repo 的根（底下沒有 .git）：{iRoot}";
        return null;
    }

    bool TryRoots(SCP_Ui g, out SCP_DataRoot oScanRoot, out SCP_LettersRoot? oLetters, out string oWhy)
    {
        oScanRoot = default;
        oLetters = null;
        string aRoot = AppliedRoot(g);
        if (aRoot.Length == 0)
        {
            SCP_PathResolution aData = m_Model.AgentCommandsRoot;
            oWhy = "沒有設掃描根目錄，而預設的 AgentCommands 資料根也取不到："
                   + (string.IsNullOrEmpty(aData.Error) ? "是空的" : aData.Error);
            return false;
        }
        if (RootProblem(aRoot) is string aProblem) { oWhy = aProblem; return false; }
        oScanRoot = new SCP_DataRoot(aRoot);
        SCP_PathResolution aLetters = m_Model.LettersRoot;
        if (string.IsNullOrEmpty(aLetters.Error) && !string.IsNullOrEmpty(aLetters.Value) && IsUnder(aLetters.Value, aRoot))
            oLetters = new SCP_LettersRoot(aLetters.Value);
        oWhy = "";
        return true;
    }

    static string NormPath(string iPath) => string.IsNullOrEmpty(iPath) ? "" : iPath.Replace('\\', '/').TrimEnd('/');

    static bool SameRoot(string iA, string iB) => string.Equals(NormPath(iA), NormPath(iB), StringComparison.OrdinalIgnoreCase);

    static bool IsUnder(string iPath, string iRoot)
    {
        string aPath = NormPath(iPath), aRoot = NormPath(iRoot);
        return aRoot.Length > 0 && (aPath.Equals(aRoot, StringComparison.OrdinalIgnoreCase)
                                    || aPath.StartsWith(aRoot + "/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把貼進來的路徑洗乾淨：只去掉包住整串的引號與前後空白（同 SubmoduleSyncPage.CleanPath）。</summary>
    static string CleanPath(string iText)
    {
        string aText = iText.Trim();
        if (aText.Length >= 2 && aText[0] == '"' && aText[aText.Length - 1] == '"')
            aText = aText.Substring(1, aText.Length - 2).Trim();
        return aText;
    }

    /// <summary>
    /// 換根（已驗過）。**換根就作廢**：新戳記（舊勾選全部失效）、清待確認、丟掉舊照片 ⇒ 下一幀對新根重掃。
    /// ⛔ 不帶著對 A 的勾選去提交 B —— 那種錯不會報錯，commit 會照樣成功。
    /// </summary>
    void ApplyRoot(SCP_Ui g, string iRoot)
    {
        string aOld = AppliedRoot(g);
        g.SetField(RootAppliedId, iRoot);
        g.SetField(RootFieldId, iRoot);
        m_RootRejected = null;
        if (SameRoot(aOld, iRoot)) return;
        g.SetField(PendingId, "");
        g.SetField(StampId, NewStamp());
        m_Scan = null;
        m_ScanRoot = "";
        m_LastCommit = null;
        m_Message = $"・掃描根目錄已換（{aOld} → {iRoot}）：之前的勾選與待確認的提交**已作廢**，下面是對新根重掃的結果。";
    }

    /// <summary>
    /// 根目錄選擇（比照 SubmoduleSyncPage.DrawTargetPicker）：打字是**草稿**，按「套用」才生效；
    /// 「改回資料根」是點選 ⇒ 立即生效。
    /// <para>⚠ 執行中不畫操作鈕（同工具列的判準）：提交跑到一半換根，Harvest 回來的重掃會對著新根，
    /// 而報告講的是舊根 —— 兩者混在同一張畫面上。</para>
    /// </summary>
    void DrawRootPicker(SCP_Ui g)
    {
        string aApplied = AppliedRoot(g);
        string aDraft = g.TextField("掃描根目錄（repo 根）", aApplied, RootFieldId);
        if (m_Job != null) return;

        string aData = DataRootValue;
        using (g.Row())
        {
            // 只在真的不是資料根時才畫 —— 一顆按下去等於沒事的鈕看起來像壞的。
            if (aData.Length > 0 && !SameRoot(aApplied, aData) && g.Button("↩ 改回資料根", "autocommit/root/data"))
            {
                if (RootProblem(aData) is string aBad) m_RootRejected = "沒有套用：" + aBad;
                else { ApplyRoot(g, aData); aApplied = aData; aDraft = aData; }
            }
            // ImGui 的 InputText 在這個宿主吃不到 Ctrl+V（見 SubmoduleSyncPage）⇒ 貼上鈕；只填草稿、不套用。
            if (SCP_GuiHost.ReadClipboard != null && g.Button("⇣ 貼上", "autocommit/root/paste"))
            {
                SCP_ClipboardRead aRead = SCP_GuiHost.ReadClipboard();
                if (aRead.Ok && aRead.Text.Length > 0) g.SetField(RootFieldId, CleanPath(aRead.Text));
                m_PasteMessage = aRead.Message;
            }
        }
        if (m_PasteMessage != null) g.Note($"　{m_PasteMessage}");

        if (!SameRoot(aDraft, aApplied))
        {
            bool aPending = true;
            using (g.Row())
            {
                if (g.Button("✓ 套用並重新掃描", "autocommit/root/apply"))
                {
                    // ⛔ 路徑不對就**不套用、什麼都不寫**；草稿留著（多半只是打錯一個字）。
                    string aClean = CleanPath(aDraft);
                    if (RootProblem(aClean) is string aBad) m_RootRejected = "沒有套用：" + aBad;
                    else { ApplyRoot(g, aClean); aPending = false; }
                }
                if (g.Button("✗ 放棄改動", "autocommit/root/discard"))
                {
                    g.SetField(RootFieldId, aApplied);
                    m_RootRejected = null;
                    aPending = false;
                }
            }
            if (aPending)
                g.Note($"　⚠ **上面的改動還沒生效** —— 下面的掃描與勾選仍然是「{aApplied}」。按「套用」才會換。");
        }
        // 被擋下的套用一定要說出來 —— 靜默不套用比套用一個壞值更難查（畫面完全沒變化）。
        if (m_RootRejected != null) g.Note($"　⚠ {m_RootRejected}");
    }

    // ===========================================================
    // 工具列
    // ===========================================================
    protected override void TopBarButtons(SCP_Ui g)
    {
        // 執行中一律**不畫**操作鈕（共用層沒有 disabled，而一顆按了沒事的鈕看起來像壞的）。
        if (m_Job != null) { g.Label($"⏳ {m_Job.Label} 執行中…"); return; }
        if (g.Button("重新掃描", "autocommit/rescan"))
        {
            g.SetField(PendingId, "");
            StartScan(g, iNewStamp: true);
        }
        if (g.Button("儲存本頁設定（根目錄＋預設勾選）", "autocommit/save")) SaveDefaults(g);
    }

    // ===========================================================
    // 掃描 / 提交 的排程
    // ===========================================================
    static string NewStamp() => DateTime.UtcNow.Ticks.ToString("x");

    /// <summary>這一輪的掃描戳記（沒有就發一個並寫進 session）。</summary>
    static string Stamp(SCP_Ui g)
    {
        string aStamp = g.FieldValue(StampId, "");
        if (aStamp.Length > 0) return aStamp;
        aStamp = NewStamp();
        g.SetField(StampId, aStamp);
        return aStamp;
    }

    void StartScan(SCP_Ui g, bool iNewStamp)
    {
        if (m_Job != null) return;
        if (!TryRoots(g, out var aData, out var aLetters, out string aWhy)) { m_Message = "✗ " + aWhy; return; }
        if (iNewStamp) g.SetField(StampId, NewStamp());
        m_JobRoot = aData.Value;
        m_Job = AutoCommitJob.Scan(aData, aLetters);
        m_Job.Start();
        // 不重畫的宿主**必須同步等完** —— 那一側畫幾趟就結束 process，丟背景等於什麼都不會發生。
        if (!SCP_GuiHost.RedrawsContinuously) { m_Job.WaitForExit(); Harvest(g); }
    }

    void StartCommit(SCP_Ui g, List<AutoCommitPick> iPicks)
    {
        if (m_Job != null) return;
        if (!TryRoots(g, out var aData, out var aLetters, out string aWhy)) { m_Message = "✗ " + aWhy; return; }
        // 確認單上的勾選是對 m_ScanRoot 拍的 ⇒ 根在那之後換過就不提交（換根時已作廢，這裡是第二道）。
        if (!SameRoot(m_ScanRoot, aData.Value))
        {
            m_Message = $"✗ 掃描根目錄換過了（勾選是對 {m_ScanRoot} 做的，現在是 {aData.Value}）—— **沒有提交**，請重新勾選。";
            return;
        }
        m_LastCommit = null;
        m_JobRoot = aData.Value;
        m_Job = AutoCommitJob.Commit(aData, aLetters, iPicks);
        m_Job.Start();
        if (!SCP_GuiHost.RedrawsContinuously) { m_Job.WaitForExit(); Harvest(g); }
    }

    /// <summary>job 跑完就把結果搬進頁面（**UI 執行緒做**，背景不直接寫頁面狀態）。提交完一律重掃 —— 報告說「收好了」不算數。</summary>
    void Harvest(SCP_Ui g)
    {
        if (m_Job == null) return;
        AutoCommitProgress aSnap = m_Job.Snapshot();
        if (!aSnap.Finished) return;
        bool aWasCommit = m_Job.IsCommit;
        m_Job = null;
        if (aWasCommit)
        {
            m_LastCommit = aSnap;
            StartScan(g, iNewStamp: true);    // 新戳記 ⇒ 這一輪的一次性勾選用完即棄
        }
        else
        {
            m_Scan = aSnap;
            m_ScanRoot = m_JobRoot;
        }
    }

    // ===========================================================
    // 勾選
    // ===========================================================
    static string ToggleId(string iStamp, SCP_AutoCommitRepo iRepo, string iKey) => $"autocommit/{iStamp}/{iRepo.Name}/{iKey}";

    static bool IsSpecial(string iKey) => Array.IndexOf(SCP_AutoCommitRules.NeverAutoKeys, iKey) >= 0;

    static string SavedKey(SCP_AutoCommitRepo iRepo, string iKey) => iRepo.Name + ":" + iKey;

    /// <summary>一群的預設勾選：特殊群永遠 false；具名群 ＝ 規則的 DefaultOn，再套存檔覆寫。</summary>
    bool DefaultFor(SCP_AutoCommitRepo iRepo, string iKey)
    {
        if (IsSpecial(iKey)) return false;
        string aSaved = SavedKey(iRepo, iKey);
        if (m_Saved.DisabledGroups.Contains(aSaved)) return false;
        if (m_Saved.EnabledGroups.Contains(aSaved)) return true;
        return iRepo.DefaultOnGroups().Contains(iKey);
    }

    /// <summary>現在勾著、而且能提交的群（順序＝Discover 的順序：子 repo 在前、父層最後）。</summary>
    List<AutoCommitPick> CollectPicks(SCP_Ui g)
    {
        var aPicks = new List<AutoCommitPick>();
        if (m_Scan?.Repos == null) return aPicks;
        string aStamp = Stamp(g);
        foreach (var aRepo in m_Scan.Repos)
        {
            if (!aRepo.CanCommit) continue;
            foreach (string aKey in aRepo.OrderedGroupKeys())
                if (g.ToggleValue(ToggleId(aStamp, aRepo, aKey), DefaultFor(aRepo, aKey)))
                    aPicks.Add(new AutoCommitPick(aRepo.Name, aKey));
        }
        return aPicks;
    }

    void SaveDefaults(SCP_Ui g)
    {
        // ① 根目錄：存**生效值**（它只能經「套用」寫進來，套用時驗過）。選回資料根存成空 ＝ 跟著資料根。
        string aRoot = AppliedRoot(g);
        m_Saved.Root = SameRoot(aRoot, DataRootValue) ? "" : aRoot;
        string aRootNote = m_Saved.Root.Length > 0 ? $"根目錄＝{m_Saved.Root}" : "根目錄＝跟著資料根";

        // ② 預設勾選：只有「照片就是這個根的」才存 —— 拿 A 的勾選存成預設，B 的同名群會被它改掉。
        if (m_Scan?.Repos == null || !SameRoot(m_ScanRoot, aRoot))
        {
            var (aOk0, aMsg0) = SenatePageStore.Save(m_Model.RepoRoot, PageKey, m_Saved);
            m_SaveMessage = aOk0
                ? $"✓ 已儲存（{aRootNote}）；⚠ 這個根還沒有掃描結果 —— 預設勾選**沒動**，維持舊存檔"
                : "✗ 儲存失敗：" + aMsg0;
            return;
        }
        string aStamp = Stamp(g);
        int aChanged = 0;
        foreach (var aRepo in m_Scan.Repos)
        {
            if (!aRepo.CanCommit) continue;
            var aRuleOn = aRepo.DefaultOnGroups();
            foreach (string aKey in aRepo.OrderedGroupKeys())
            {
                if (IsSpecial(aKey)) continue;              // 特殊群永遠不持久化
                bool aOn = g.ToggleValue(ToggleId(aStamp, aRepo, aKey), DefaultFor(aRepo, aKey));
                string aSaved = SavedKey(aRepo, aKey);
                bool aHadD = m_Saved.DisabledGroups.Remove(aSaved);
                bool aHadE = m_Saved.EnabledGroups.Remove(aSaved);
                // 只記跟規則預設不一樣的那一格
                if (aOn != aRuleOn.Contains(aKey)) (aOn ? m_Saved.EnabledGroups : m_Saved.DisabledGroups).Add(aSaved);
                bool aHasNow = m_Saved.DisabledGroups.Contains(aSaved) || m_Saved.EnabledGroups.Contains(aSaved);
                if (aHadD || aHadE || aHasNow) aChanged++;
            }
        }
        var (aOk, aMsg) = SenatePageStore.Save(m_Model.RepoRoot, PageKey, m_Saved);
        m_SaveMessage = aOk
            ? $"✓ 已儲存（{aRootNote}）；預設勾選跟規則預設不同的 {m_Saved.DisabledGroups.Count + m_Saved.EnabledGroups.Count} 格（"
              + "只存**這一輪畫面上有出現**的群，沒出現的群維持舊存檔）"
            : "✗ 儲存失敗：" + aMsg;
    }

    // ===========================================================
    // 內容
    // ===========================================================
    protected override void DrawContent(SCP_Ui g)
    {
        Harvest(g);

        g.Note("把**機器自動生成的檔**分群、每群一筆 commit。範圍＝掃描根目錄本層＋每個 persona 信件庫"
               + $"＋根目錄底下帶 `{SCP_AutoCommitConfig.FileName}` 的 submodule（一次掃完，不分模式）。");
        g.Note("　⛔ 不 push、不動父層 pointer；走純 git commit（無 trailer、無酒館公告、不領薪）。"
               + "有作者的工作產出別走這裡（那要走 `senate cmd commit`）。");
        g.Note("　⚠ 親筆檔（收尾信・碎片・素描本濃縮・opinions…）落在「未分類」，**永不自動收** —— "
               + "分界是「這個檔誰在寫」，不是「這個人在不在線」。");

        DrawRootPicker(g);
        if (m_SaveMessage != null) g.Note(m_SaveMessage);
        if (m_Message != null) g.Note(m_Message);

        if (!TryRoots(g, out var aScanRoot, out var aLetters, out string aWhy))
        {
            g.Note("⚠ 本頁**沒有資料來源** ⇒ 這不是「沒有東西要收」，是「量不到」。原因：" + aWhy);
            if (AppliedRoot(g).Length == 0)
                g.Note("· 預設的資料根要去「路徑管理」頁設（`senate ui --page paths`）；或在上面直接填一個 repo 根。");
            return;
        }
        string aRootNow = aScanRoot.Value;
        g.Note($"　▶ 掃描根目錄：**{aRootNow}**　—— 由{RootOrigin(g)}決定");
        g.Note($"　資料根：{m_Model.AgentCommandsRoot.Value}（{m_Model.AgentCommandsRoot.Origin}）");
        g.Note(aLetters.HasValue
            ? $"　letters 根：{m_Model.LettersRoot.Value}（{m_Model.LettersRoot.Origin}）—— 在掃描根底下，信件庫一起掃"
            : $"　letters 根：{m_Model.LettersRoot.Value}（{m_Model.LettersRoot.Origin}）—— **不在**掃描根底下 ⇒ 本輪**不掃信件庫**（⛔ 不等於信件庫全乾淨）");
        if (!SameRoot(aRootNow, DataRootValue))
            g.Note("　⚠ 掃描根不是資料根：父層仍套 AgentCommands 那張寫死的分群表 —— 它不認得的檔落在「未分類」，**永不自動收**。");

        // 設定編輯區跟著生效的根（只在換根時重讀一次 —— Draw 裡平常零 IO）
        if (!SameRoot(m_ConfigRoot, aRootNow)) ReloadConfigs(aRootNow);
        // 照片不是這個根拍的（換根、或別的 process 改了 session 裡的生效值）⇒ 不畫、不提交，重拍
        if (m_Scan != null && !SameRoot(m_ScanRoot, aRootNow))
        {
            m_Scan = null;
            g.SetField(PendingId, "");
            g.SetField(StampId, NewStamp());
        }

        // 還沒有照片就先拍一張（會重畫的宿主丟背景、這一幀先畫「掃描中」）
        if (m_Scan == null && m_Job == null) StartScan(g, iNewStamp: false);

        DrawJobStatus(g);
        DrawLastCommit(g);
        DrawConfigEditor(g);
        g.Separator();

        if (m_Scan == null) return;              // 還在掃（進度已經畫在上面）
        if (m_Scan.Error != null)
        {
            g.Note("✗ **掃描本身炸了**（不是某一個 repo 失敗）：" + m_Scan.Error);
            return;
        }
        DrawActions(g);
        DrawRepos(g);
    }

    void DrawJobStatus(SCP_Ui g)
    {
        if (m_Job == null) return;
        AutoCommitProgress aSnap = m_Job.Snapshot();
        using (g.Box($"⏳ {m_Job.Label} 執行中　{aSnap.Done} / {aSnap.Total}"))
        {
            if (aSnap.Current.Length > 0) g.Note(aSnap.Current);
            g.Note("⚠ **沒有取消鈕**：git 跑到一半被 kill 可能留下 `index.lock`，那個殘局比多等一會兒貴得多。");
        }
    }

    void DrawLastCommit(SCP_Ui g)
    {
        if (m_LastCommit is not AutoCommitProgress aResult) return;
        using (g.Box($"上一輪 commit：✓{aResult.OkCount} ⏭{aResult.SkipCount} ✗{aResult.FailCount}"
                     + (aResult.MismatchCount > 0 ? $"　⚠ 對帳不符 {aResult.MismatchCount}" : "")))
        {
            if (aResult.Error != null) g.Note("✗ **批次本身炸了**（不是某一群失敗）：" + aResult.Error);
            var aRows = aResult.Results ?? new List<SCP_AutoCommitGroupResult>();
            if (aRows.Count > 0)
            {
                using (g.Table("repo", "群", "結果"))
                {
                    foreach (var aRow in aRows)
                    {
                        string aOutcome = aRow.Ok ? $"✓ {aRow.Sha}" + (aRow.Extra.Count > 0 ? $"　⚠ 多帶 {aRow.Extra.Count} 檔" : "")
                                        : aRow.Skipped ? "⏭ " + aRow.Error
                                        : "✗ " + aRow.Error + (aRow.IsLocked ? "（index.lock：別人正握著 index，等一下重跑就好）" : "");
                        g.TableRow(aRow.RepoName, aRow.Key, aOutcome);
                    }
                }
            }
            using (var aFold = g.Fold($"完整經過（{aResult.Log.Count} 行）", "autocommit/commit-log", iDefaultOpen: false))
            {
                if (aFold.Open)
                    foreach (string aLine in aResult.Log) g.Note(aLine);
            }
            if (aResult.OkCount > 0)
                g.Note("　↳ 未 push、父層 pointer 未動。父層的 submodule pointer 會出現在下面 AgentCommands 的"
                       + "「巢狀 submodule pointer」群（一次性勾選）—— bump 與否仍是人的決定。");
            g.Note("　⚠ 這份報告說「收好了」不算數 —— 下面是**跑完之後重掃**的狀態，那才是讀數。");
        }
    }

    void DrawActions(SCP_Ui g)
    {
        if (m_Job != null) return;
        var aPicks = CollectPicks(g);
        bool aPending = g.FieldValue(PendingId, "") == "1";

        if (!aPending)
        {
            using (g.Row())
            {
                if (g.Button($"Commit 勾選群組（{aPicks.Count} 筆 commit）", "autocommit/commit"))
                {
                    if (aPicks.Count == 0) m_Message = "・沒有勾選任何可提交的群 —— 沒有東西要 commit。";
                    else { m_Message = null; g.SetField(PendingId, "1"); aPending = true; }
                }
            }
            if (!aPending) return;
        }

        // ── 待確認 ── 攤出每一筆 commit 的訊息與檔數，再給確定／取消。
        using (g.Box($"確認自動 Commit？將建立 {aPicks.Count} 筆 commit（每群一筆，具名 stage、⛔ 不 git add -A）"))
        {
            if (aPicks.Count == 0)
            {
                g.Note("・（重新整理後已經沒有勾選的群了）");
            }
            else
            {
                using (g.Table("repo", "commit 訊息"))
                {
                    foreach (var aPick in aPicks)
                    {
                        var aRepo = m_Scan!.Repos!.Find(r => r.Name == aPick.RepoName);
                        g.TableRow(aPick.RepoName, aRepo?.MessageOf(aPick.Key) ?? aPick.Key);
                    }
                }
            }
            g.Note("　只 commit 各自本層 —— 不 push、不動父層 pointer。提交前每個 repo 會**重掃一次**"
                   + "（index 在確認之後被動過的話，守衛量的是現在）。");
            using (g.Row())
            {
                if (aPicks.Count > 0 && g.Button($"⚠ 確定 Commit（{aPicks.Count} 筆）", "autocommit/confirm"))
                {
                    g.SetField(PendingId, "");
                    StartCommit(g, aPicks);
                }
                if (g.Button("取消", "autocommit/confirm-cancel")) g.SetField(PendingId, "");
            }
        }
    }

    void DrawRepos(SCP_Ui g)
    {
        var aRepos = m_Scan!.Repos ?? new List<SCP_AutoCommitRepo>();
        foreach (string aNote in m_Scan.Notes) g.Note(aNote);

        int aDirty = 0, aEphemeral = 0;
        var aClean = new List<string>();
        foreach (var aRepo in aRepos)
        {
            aEphemeral += aRepo.Ephemeral;
            bool aShow = aRepo.CandidateCount > 0 || aRepo.Blocked.Length > 0 || aRepo.Disabled;
            if (aShow) aDirty++;
            else aClean.Add(aRepo.Name);
        }
        g.Label($"掃描：{aRepos.Count} 個 repo（{aDirty} 個要看）");
        if (aDirty == 0)
            g.Note("✓ 全部乾淨（ephemeral 除外）—— 沒有可 commit 的自動生成檔。");

        string aStamp = Stamp(g);
        foreach (var aRepo in aRepos)
        {
            bool aShow = aRepo.CandidateCount > 0 || aRepo.Blocked.Length > 0 || aRepo.Disabled;
            if (aShow) DrawRepo(g, aRepo, aStamp);
        }

        if (aClean.Count > 0) g.Note($"✓ 乾淨的 {aClean.Count} 個：{string.Join("、", aClean)}");
        if (aEphemeral > 0)
            g.Note($"⛔ ephemeral 已排除 {aEphemeral} 個（log / wait 旗標 / _last_op / DebugLogs…）—— 永遠不進 commit");
    }

    void DrawRepo(SCP_Ui g, SCP_AutoCommitRepo iRepo, string iStamp)
    {
        string aBranch = iRepo.Branch.Length > 0 ? "／" + iRepo.Branch : "";
        // ⚠ 頁面文字不用 U+FFFF 以上的字（📁 之類）：16 位元 ImWchar 畫不出來，會變 `?`（TASK-0342 缺字守衛點名）。
        using (g.Box($"▸ {iRepo.Name}　（{iRepo.SourceLabel}{aBranch}）　候選 {iRepo.CandidateCount} 檔", "autocommit/repo/" + iRepo.Name))
        {
            if (iRepo.Disabled)
            {
                g.Note($"⏸ 設定為停用（Enabled=false）—— 到上方「⚙ Submodule 自動提交設定」開啟：{iRepo.ConfigPath}");
                return;
            }
            if (iRepo.Blocked.Length > 0)
            {
                // 擋下的 repo 不列群組 —— 它的內容此刻不能被 commit
                g.Note("⛔ " + iRepo.Blocked);
                return;
            }
            if (iRepo.PreStaged.Count > 0)
            {
                g.Note($"⛔ 掃描前 index 已有 {iRepo.PreStaged.Count} 個 staged 檔 —— 它們會被併進第一個群並掛上那個群的訊息（BUG-30）。"
                       + "請先自己 commit 或 unstage，再重新掃描：");
                foreach (string aLine in SCP_AutoCommit.Preview(iRepo.PreStaged)) g.Note("　　" + aLine);
                return;
            }
            foreach (string aKey in iRepo.OrderedGroupKeys())
            {
                var aFiles = iRepo.Groups[aKey];
                g.Toggle($"{iRepo.LabelOf(aKey)}　({aFiles.Count} 檔)", DefaultFor(iRepo, aKey), ToggleId(iStamp, iRepo, aKey));
                g.Note($"　訊息：{iRepo.MessageOf(aKey)}");
                using (var aFold = g.Fold($"檔案清單（{aFiles.Count}）", $"autocommit/files/{iRepo.Name}/{aKey}", iDefaultOpen: false))
                {
                    if (!aFold.Open) continue;
                    // 「自動」的前提是按之前看得到它要帶走什麼 —— 上限只為了不撐爛版面，超過的一定報數。
                    for (int i = 0; i < aFiles.Count && i < FileListCap; ++i) g.Note("　" + aFiles[i]);
                    if (aFiles.Count > FileListCap) g.Note($"　…另有 {aFiles.Count - FileListCap} 筆未列（完整清單：`senate cmd auto-commit` 的 scan 輸出）");
                }
            }
        }
    }

    // ===========================================================
    // ⚙ Submodule 自動提交設定（.ucl_autocommit.json）—— 可編輯
    // ===========================================================
    // 區塊職責：把各 submodule 自己宣告的分群設定攤在畫面上，可以選、改、存、開關。
    // 物理意義：Tim 2026-08-21 拍板兩段 —— ① 規則由該 repo 自己宣告 ② 沒有設定檔的選了可以建一份，**預設停用**
    //          （「選了一個 submodule」不等於「同意開始自動 commit 它」）。
    // 數值影響：讀檔只在 OnPush 與「重新載入／建立／存檔」之後 —— Draw 裡零 IO。
    //          編輯中的值住 session（inspector 的欄位 id 帶「代次」，放棄改動＝換一個代次）。
    //          ⚠ 讀不出來的設定**不會被覆蓋** —— 覆蓋掉的是別人寫的東西，而那筆改動沒有地方留得住。
    /// <summary>設定編輯區目前列的是哪個根的 submodule（跟掃描根對不上就重讀）。</summary>
    string m_ConfigRoot = "";

    void ReloadConfigs(string iRoot)
    {
        m_Configs.Clear();
        m_ConfigRoot = iRoot;
        SCP_PathResolution aLetters = m_Model.LettersRoot;
        if (RootProblem(iRoot) != null) return;
        string aLettersRoot = (aLetters.Value ?? "").Replace('\\', '/').TrimEnd('/');
        foreach (string aDir in SCP_AutoCommitConfig.ListSubmodulePaths(iRoot))
        {
            // 信件庫的規則寫死在 PersonaGroupDefs，不走設定檔 ⇒ 不列進來（列了反而是在邀請人造第二份規則）。
            if (aLettersRoot.Length > 0 && (aDir.Equals(aLettersRoot, StringComparison.OrdinalIgnoreCase)
                                            || aDir.StartsWith(aLettersRoot + "/", StringComparison.OrdinalIgnoreCase))) continue;
            m_Configs.Add(new ConfigEntry { Root = aDir, Name = Path.GetFileName(aDir), Load = SCP_AutoCommitConfig.Load(aDir) });
        }
    }

    static string StatusIcon(ConfigEntry iEntry) => iEntry.Load.State switch
    {
        SCP_AutoCommitConfigState.Error => "⛔ 設定壞掉",
        SCP_AutoCommitConfigState.Missing => "— 尚無設定檔",
        _ => iEntry.Load.Config!.Enabled ? "✅ 已啟用" : "⏸ 停用",
    };

    void DrawConfigEditor(SCP_Ui g)
    {
        int aEnabled = m_Configs.Count(c => c.Load.State == SCP_AutoCommitConfigState.Ok && c.Load.Config!.Enabled);
        using (var aFold = g.Fold($"⚙ Submodule 自動提交設定（{m_Configs.Count} 個 submodule／{aEnabled} 個已啟用）",
                                  "autocommit/cfg", iDefaultOpen: false))
        {
            if (!aFold.Open) return;
            g.Note($"檔名 `{SCP_AutoCommitConfig.FileName}`（放各 repo 根）—— 有這個檔**且已啟用**才會被收。"
                   + "只吃前綴清單（不吃 regex）；ephemeral 與特殊群由判定順序擋在分群之前，設定檔掀不動。");
            if (m_ConfigMessage != null) g.Note(m_ConfigMessage);
            if (g.Button("重新載入", "autocommit/cfg/reload")) { ReloadConfigs(m_ConfigRoot); BumpCfgGen(g); m_ConfigMessage = null; }
            if (m_Configs.Count == 0)
            {
                g.Note("（掃描根目錄的 .gitmodules 裡沒有任何非信件庫的 submodule）");
                return;
            }

            using (g.Table("submodule", "狀態"))
                foreach (var aEntry in m_Configs) g.TableRow(aEntry.Name, StatusIcon(aEntry));

            var aNames = m_Configs.ConvertAll(c => c.Name);
            string aPick = g.Dropdown("目標 submodule", aNames, aNames[0], CfgPickId);
            var aSelected = m_Configs.Find(c => c.Name == aPick);
            if (aSelected == null) return;

            g.Note(SCP_AutoCommitConfig.PathOf(aSelected.Root));
            switch (aSelected.Load.State)
            {
                case SCP_AutoCommitConfigState.Error:
                    g.Note("⛔ 讀取失敗：" + aSelected.Load.Error);
                    g.Note("修好檔案再按「重新載入」。**不會**自動覆蓋壞檔 —— 覆蓋掉的是別人寫的設定。");
                    return;
                case SCP_AutoCommitConfigState.Missing:
                    g.Note("尚無設定檔。建立之後**預設停用** —— 要開始自動 commit 這個 repo，得再顯式打開 Enabled。");
                    if (g.Button("➕ 建立設定檔（預設停用）", "autocommit/cfg/create"))
                    {
                        var (aOk, aMsg) = SCP_AutoCommitConfig.CreateDefault(aSelected.Name).Save(aSelected.Root);
                        m_ConfigMessage = aOk ? aMsg + "（Enabled=false、尚未分群）" : "⛔ 建立失敗：" + aMsg;
                        ReloadConfigs(m_ConfigRoot);
                        BumpCfgGen(g);
                    }
                    return;
            }

            // 整個物件交給 inspector 反射繪製（Enabled 開關也在裡面）—— 加欄位時這一頁一行都不用改。
            var aConfig = aSelected.Load.Config!;
            string aGen = g.FieldValue(CfgGenId, "0");
            SCP_GuiInspector.Draw(g, aConfig, $"autocommit/cfg/{aGen}/{aSelected.Name}");

            var aErrors = aConfig.Validate();
            foreach (string aError in aErrors) g.Note("⚠ " + aError);
            using (g.Row())
            {
                // 不合法就不畫存檔鈕（共用層沒有 disabled）—— 理由已經逐條列在上面。
                if (aErrors.Count == 0 && g.Button("存檔", "autocommit/cfg/save"))
                {
                    var (aOk, aMsg) = aConfig.Save(aSelected.Root);
                    m_ConfigMessage = aOk ? aMsg : "⛔ " + aMsg;
                    ReloadConfigs(m_ConfigRoot);
                    BumpCfgGen(g);
                    if (aOk) { g.SetField(PendingId, ""); StartScan(g, iNewStamp: true); }   // 開關／分群變了 ⇒ 掃描結果跟著變
                }
                if (g.Button("↩ 放棄改動", "autocommit/cfg/discard")) { ReloadConfigs(m_ConfigRoot); BumpCfgGen(g); m_ConfigMessage = null; }
            }
            if (aErrors.Count > 0) g.Note("（有不合法的欄位，存檔鈕已隱藏）");
        }
    }

    /// <summary>換一個編輯代次 ⇒ session 裡的舊編輯值不再套到重新讀進來的物件上。</summary>
    static void BumpCfgGen(SCP_Ui g)
    {
        int.TryParse(g.FieldValue(CfgGenId, "0"), out int aGen);
        g.SetField(CfgGenId, (aGen + 1).ToString());
    }
}
