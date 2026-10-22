// 區塊職責：任務單（`<資料根>/Tasks`）跟遠端 git repo 同步（`sync.tasks`，Tim 2026-10-22：
//           「先做task的測試看看 https://github.com/tim099/Tasks.git」「讓task有新增 或改動時自動commit」「同時push」
//           「發生衝突時會需要能解(不能自動解的要抱錯通知手動處理)」「例如task編號衝突(兩邊同時開新的task)」）。
// 物理意義：本機的 `Tasks/` **就是**遠端那條 branch 的工作副本 —— 讀的人（`tasks`、後台頁）一行都不用改。
//           一次寫入 ＝ 先拉（fetch＋快轉）→ 寫單（`SCP_TaskOps.Run`）→ commit → push。
//           push 被拒（別台先推了）⇒ fetch → rebase；rebase 撞了 ⇒ **重放**：
//             把還沒推上去的操作（每一筆 commit 旁邊記著 op／persona／參數）在遠端最新版上重跑一次。
//             ⭐ 兩邊同時開單撞號就是這樣解掉的：重跑 create 時配號器看到遠端已經佔了那一號，自然拿下一號。
//           重放不安全的（整段覆寫的 op，而遠端也改了同一張單）、或重跑被閘擋下的 ⇒ **不自動解**：
//             我的版本留在備份 ref、寫一份衝突報告（含重跑指令），回報失敗並交回 `sync_conflict` 通知（入口發酒館）。
// 數值影響：每次寫入多兩次網路往返（fetch、push）；推不上去（斷網、沒權限）⇒ 已在本機提交，回報 Pending，
//           背景工作（`SenateTaskRepoSyncJob`）之後再推 —— **推上去之前，新單的號碼都可能還會變**。
// ⚠ 只在寫入端（Senate Server 的 task lane）與它的背景工作裡呼叫；git 操作一律在 s_Lock 裡（同一顆 repo 不准兩條執行緒一起動 index）。
#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Json;
using SCP.Core.Git;
using SCP.Core.Paths;
using SCP.Core.Tasks;
using static Senate.Core.RepoSyncGit;

namespace Senate.Core;

/// <summary>同步到哪裡（<c>sync.tasks</c> 兩格都填才有）。</summary>
public sealed record TaskRepoTarget(string Repo, string Branch)
{
    public static TaskRepoTarget? From(SenateConfig? iConfig)
        => iConfig != null && iConfig.Sync.Tasks.IsSet
            ? new TaskRepoTarget(iConfig.Sync.Tasks.Repo.Trim(), iConfig.Sync.Tasks.Branch.Trim())
            : null;

    public string RemoteRef => "origin/" + Branch;
    public string RefSpec => $"+refs/heads/{Branch}:refs/remotes/origin/{Branch}";
}

public enum TaskSyncState
{
    /// <summary>沒設定遠端 ⇒ 照舊只寫本機。</summary>
    Off,
    /// <summary>本來就跟遠端一樣，沒有東西要推。</summary>
    UpToDate,
    /// <summary>已推上遠端。</summary>
    Pushed,
    /// <summary>已在本機提交、還沒推上去（斷網／沒權限／遠端一直在動）—— 之後會再推。</summary>
    Pending,
    /// <summary>自動解不了的衝突 ⇒ 要人處理（備份 ref ＋ 衝突報告）。</summary>
    Conflict,
    /// <summary>本機的 Tasks 跟設定對不上（不是 git repo、origin 或 branch 不同）⇒ 不同步。</summary>
    Mismatch,
    /// <summary>clone／git 本身失敗。</summary>
    Failed,
}

/// <summary>一次同步的回報 —— 寫入端把 <see cref="Lines"/> 接進回傳檔、<see cref="Notices"/> 交給入口發酒館。</summary>
public sealed class TaskSyncReport
{
    public TaskSyncState State = TaskSyncState.Off;
    public List<string> Lines = new();
    public List<SCP_TaskNotice> Notices = new();
    /// <summary>衝突報告的路徑（只有 <see cref="TaskSyncState.Conflict"/> 有）。</summary>
    public string ConflictFile = "";

    public string StateWord => State switch
    {
        TaskSyncState.Off => "off",
        TaskSyncState.UpToDate => "up_to_date",
        TaskSyncState.Pushed => "pushed",
        TaskSyncState.Pending => "pending",
        TaskSyncState.Conflict => "conflict",
        TaskSyncState.Mismatch => "mismatch",
        _ => "failed",
    };
}

/// <summary>本機那一份的讀數（`task-sync op=status` 與路徑管理頁用；只有 <c>iFetch</c> 才走網路）。</summary>
public sealed record TaskRepoStatus(bool Configured, bool Exists, bool IsRepo, string Origin, string Branch,
                                    int Ahead, int Behind, int Dirty, string Note);

public static class SenateTaskRepoSync
{
    /// <summary>每一筆寫入端 commit 旁邊記的操作 —— 重放就靠它（住 git 目錄裡，不入版控）。</summary>
    public sealed record JournalEntry(string Op, string Persona, Dictionary<string, string> Args, int Index, int Wrote);

    const string JournalDirName = "senate-task-journal";
    const string ConflictDirName = "senate-task-sync";
    const int NetTimeoutMs = 120_000;
    const int MaxPushTries = 4;

    /// <summary>
    /// 重放永遠安全的 op：它們是「加一筆」（留言、參與者、關聯、sha、收工）或配新號（create），
    /// 在對方的新版上重跑一次，結果就是「兩邊的都在」。
    /// <para>⛔ 不在這裡的（update／resolve／sweep、不帶 expect_text 的 check）是**整段覆寫**或靠序號定位 ——
    /// 遠端也動了同一張單時重跑會蓋掉對方，所以那種情況交給人。</para>
    /// </summary>
    static readonly HashSet<string> s_AppendOps = new(StringComparer.Ordinal)
        { "create", "comment", "assign", "unassign", "link", "claim", "commit", "wrapup", "wrapup_skip" };

    static readonly object s_Lock = new();

    public static string TasksDir(string iDataRoot) => SCP_TaskIO.Dir(new SCP_DataRoot(iDataRoot));

    // ===========================================================
    // 區塊職責：寫入端的一次寫入（取代直接呼叫 `SCP_TaskOps.Run`）。
    // 邊界：
    //   · 沒設遠端 ⇒ 跟以前一模一樣（只寫本機，State=Off）。
    //   · 本次這一筆在重放時改了號 ⇒ 回傳**重放後**的結果（入口公告的就是新號碼）。
    //   · 本次這一筆重放不成 ⇒ ExitCode=1、Wrote=0（它不在共用板上），衝突通知在 Sync.Notices。
    //   · `iPreSync=false` 只給淨室測試模擬「還沒看到對方就開了單」。
    // ===========================================================
    public static (SCP_TaskOpResult Result, TaskSyncReport Sync) RunWrite(string iDataRoot, TaskRepoTarget? iTarget,
        string iOp, string iPersona, IReadOnlyDictionary<string, string> iArgs, bool iPreSync = true)
    {
        var aSync = new TaskSyncReport();
        lock (s_Lock)
        {
            string aDir = TasksDir(iDataRoot);
            bool aGit = iTarget != null && Ensure(iDataRoot, iTarget, aSync);
            if (iTarget != null && !Directory.Exists(aDir))
                return (Refused("✗ 任務板還沒 clone 下來 ⇒ 這一筆沒寫",
                    "設了遠端（sync.tasks）卻沒有本機那一份 —— 照舊寫的話會長出一個從 0001 開始的空任務板，接上遠端時整排撞號。",
                    aSync), aSync);
            if (aGit && iPreSync) PullBeforeWrite(aDir, iTarget!, aSync);

            SCP_TaskOpResult r = SCP_TaskOps.Run(new SCP_DataRoot(iDataRoot), iOp, iPersona, iArgs);
            if (!aGit) return (r, aSync);
            if (r.ExitCode != 0 || r.Wrote == 0)
            {
                if (aSync.State == TaskSyncState.Off) aSync.State = TaskSyncState.UpToDate;
                return (r, aSync);
            }

            string? aSha = Commit(aDir, iOp, iPersona, r, aSync);
            if (aSha == null) { aSync.State = TaskSyncState.Failed; return (r, aSync); }
            WriteJournal(aDir, aSha, new JournalEntry(iOp, iPersona, new Dictionary<string, string>(iArgs), r.Index, r.Wrote));

            var aOutcome = Push(iDataRoot, aDir, iTarget!, aSync, true, aSha);
            if (aOutcome.NotApplied.Contains(aSha)) MarkNotApplied(r, aSync);
            else if (aOutcome.Replayed.TryGetValue(aSha, out SCP_TaskOpResult? aNew)) r = aNew;
            return (r, aSync);
        }
    }

    /// <summary>寫入前就擋下（零寫入）的結果 —— 形狀跟 `SCP_TaskOps.Run` 擋下時一樣，入口照「被擋下」回報。</summary>
    static SCP_TaskOpResult Refused(string iHeadline, string iWhy, TaskSyncReport iSync)
    {
        var r = new SCP_TaskOpResult { ExitCode = 1, Headline = iHeadline };
        r.Values["wrote"] = "0";   // 入口靠 wrote 分辨「寫入端判定了」與「寫入端沒回話」
        r.Report.AppendLine("## ❌ 失敗");
        r.Report.AppendLine("- reason: " + iWhy);
        foreach (string l in iSync.Lines) r.Report.AppendLine("- " + l);
        return r;
    }

    // ===========================================================
    // 區塊職責：不寫單、只同步（`task-sync op=sync`、背景工作）。
    // 物理意義：缺目錄 ⇒ clone；有本機提交 ⇒ 推（`iAllowReplay` 才重放 —— 背景工作不重放：
    //           重放產生的通知要有人看，那屬於有人在場的入口）；落後 ⇒ 快轉。
    // ===========================================================
    public static TaskSyncReport Sync(string iDataRoot, TaskRepoTarget iTarget, bool iAllowReplay)
    {
        var aSync = new TaskSyncReport();
        lock (s_Lock)
        {
            if (!Ensure(iDataRoot, iTarget, aSync)) return aSync;
            string aDir = TasksDir(iDataRoot);
            PullBeforeWrite(aDir, iTarget, aSync);
            if (aSync.State is TaskSyncState.Pending or TaskSyncState.Failed) return aSync;   // 抓不到遠端就不推
            (int aAhead, _) = AheadBehind(aDir, iTarget.RemoteRef);
            if (aAhead == 0) { aSync.State = TaskSyncState.UpToDate; return aSync; }
            Push(iDataRoot, aDir, iTarget, aSync, iAllowReplay, null);
            return aSync;
        }
    }

    /// <summary>本機那一份的讀數。<c>iFetch</c> ⇒ 先抓一次遠端（走網路），否則只看本機已知的。</summary>
    public static TaskRepoStatus Status(string iDataRoot, TaskRepoTarget? iTarget, bool iFetch)
    {
        string aDir = TasksDir(iDataRoot);
        bool aExists = Directory.Exists(aDir);
        bool aRepo = aExists && IsRepoRoot(aDir);
        if (iTarget == null)
            return new TaskRepoStatus(false, aExists, aRepo, "", "", 0, 0, 0, "沒設定遠端（sync.tasks）⇒ 只寫本機");
        if (!aExists)
            return new TaskRepoStatus(true, false, false, "", "", 0, 0, 0, "還沒 clone —— 第一次用到時（開單、或 Server 背景工作）會自動 clone");
        if (!aRepo)
            return new TaskRepoStatus(true, true, false, "", "", 0, 0, 0, "Tasks 存在但不是 git repo ⇒ 不同步");
        lock (s_Lock)
        {
            if (iFetch) Git(aDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec);
            string aOrigin = Git(aDir, SCP_Git.DefaultTimeoutMs, "remote", "get-url", "origin").StdOut.Trim();
            string aBranch = Git(aDir, SCP_Git.DefaultTimeoutMs, "rev-parse", "--abbrev-ref", "HEAD").StdOut.Trim();
            (int aAhead, int aBehind) = AheadBehind(aDir, iTarget.RemoteRef);
            int aDirty = Git(aDir, SCP_Git.DefaultTimeoutMs, "status", "--porcelain").OutLines().Count;
            string aNote = !SameRepo(aOrigin, iTarget.Repo) ? "⚠ origin 跟設定的 repo 不同 ⇒ 不同步"
                         : aBranch != iTarget.Branch ? $"⚠ 現在在 `{aBranch}`，設定是 `{iTarget.Branch}` ⇒ 不同步"
                         : aAhead > 0 ? $"有 {aAhead} 筆還沒推上去"
                         : aBehind > 0 ? $"落後遠端 {aBehind} 筆（下一次同步會快轉）"
                         : "跟遠端一致";
            return new TaskRepoStatus(true, true, true, aOrigin, aBranch, aAhead, aBehind, aDirty, aNote);
        }
    }

    // ===========================================================
    // 區塊職責：確保本機那一份可以同步 —— 缺目錄 ⇒ clone；在 ⇒ 核對它真的是設定的那個 repo、那條 branch。
    // 物理意義：⭐ 設了遠端就**先 clone、不建空目錄**：先長出一個空的 Tasks/、再接上已經有單的遠端，
    //           會從 0001 開始整排撞號。對不上就**不同步**（寫照舊落本機）—— ⛔ 不替人改 origin、不切 branch。
    // ===========================================================
    static bool Ensure(string iDataRoot, TaskRepoTarget iTarget, TaskSyncReport ioSync)
    {
        string aDir = TasksDir(iDataRoot);
        if (!Directory.Exists(aDir))
        {
            if (!Directory.Exists(iDataRoot))
            {
                ioSync.State = TaskSyncState.Failed;
                ioSync.Lines.Add($"✗ 資料根不存在：{iDataRoot} —— 不 clone（⛔ 不在一個不存在的地方長出任務板）");
                return false;
            }
            SCP_GitResult c = Git(iDataRoot, NetTimeoutMs, "clone", "--branch", iTarget.Branch, "--", iTarget.Repo, aDir);
            if (!c.Ok)
            {
                ioSync.State = TaskSyncState.Failed;
                ioSync.Lines.Add($"✗ clone 失敗（{iTarget.Repo} {iTarget.Branch}）：{c.ReasonLine}");
                return false;
            }
            ioSync.Lines.Add($"✓ 第一次用到 ⇒ 已從 `{iTarget.Repo}`（{iTarget.Branch}）clone 到 `{aDir}`");
        }
        if (!IsRepoRoot(aDir))
        {
            ioSync.State = TaskSyncState.Mismatch;
            ioSync.Lines.Add($"⚠ `{aDir}` 已經存在但不是 git repo ⇒ **不同步**（照舊只寫本機）。要接遠端：把它移開，下一次用到會自動 clone。");
            return false;
        }
        string aOrigin = Git(aDir, SCP_Git.DefaultTimeoutMs, "remote", "get-url", "origin").StdOut.Trim();
        if (!SameRepo(aOrigin, iTarget.Repo))
        {
            ioSync.State = TaskSyncState.Mismatch;
            ioSync.Lines.Add($"⚠ Tasks 的 origin 是 `{aOrigin}`，設定是 `{iTarget.Repo}` ⇒ **不同步**（⛔ 不替人改 origin）");
            return false;
        }
        string aBranch = Git(aDir, SCP_Git.DefaultTimeoutMs, "rev-parse", "--abbrev-ref", "HEAD").StdOut.Trim();
        if (aBranch != iTarget.Branch)
        {
            ioSync.State = TaskSyncState.Mismatch;
            ioSync.Lines.Add($"⚠ Tasks 現在在 `{aBranch}`，設定是 `{iTarget.Branch}` ⇒ **不同步**（⛔ 不替人切 branch）");
            return false;
        }
        if (Directory.Exists(Path.Combine(GitDir(aDir), "rebase-merge")) || Directory.Exists(Path.Combine(GitDir(aDir), "rebase-apply")))
        {
            ioSync.State = TaskSyncState.Failed;
            ioSync.Lines.Add($"✗ Tasks 停在一次沒做完的 rebase ⇒ 不同步。先看 `git -C {aDir} status`，處理完（或 `rebase --abort`）再來");
            return false;
        }
        ExcludeFromParent(iDataRoot, aDir, ioSync);
        return true;
    }

    /// <summary>
    /// 寫之前先拉：抓遠端；有沒提交的改動先收成一筆（沒有操作紀錄 ⇒ 之後撞了只能交給人）；
    /// 落後就快轉、兩邊都有新的就先 rebase（撞了留給 push 那一步重放）。
    /// <para>⭐ 這一步讓配號器在大多數時候就看到遠端最新的號碼 —— 撞號只剩「幾秒內兩邊同時開」那一種。</para>
    /// </summary>
    static void PullBeforeWrite(string iDir, TaskRepoTarget iTarget, TaskSyncReport ioSync)
    {
        CommitLeftovers(iDir, ioSync);
        SCP_GitResult f = Git(iDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec);
        if (!f.Ok)
        {
            ioSync.State = TaskSyncState.Pending;
            ioSync.Lines.Add($"⚠ 抓不到遠端（{f.ReasonLine}）—— 先照本機的版本寫；**推上去之前，新單的號碼可能還會變**");
            return;
        }
        (int aAhead, int aBehind) = AheadBehind(iDir, iTarget.RemoteRef);
        if (aBehind == 0) return;
        if (aAhead == 0)
        {
            SCP_GitResult m = Git(iDir, SCP_Git.DefaultTimeoutMs, "merge", "--ff-only", iTarget.RemoteRef);
            ioSync.Lines.Add(m.Ok ? $"· 先從遠端拉了 {aBehind} 筆（快轉）" : $"⚠ 快轉失敗（{m.ReasonLine}）—— 照本機的版本寫");
            return;
        }
        SCP_GitResult rb = GitId(iDir, SCP_Git.DefaultTimeoutMs, "rebase", iTarget.RemoteRef);
        if (rb.Ok) { ioSync.Lines.Add($"· 本機有 {aAhead} 筆沒推、遠端有 {aBehind} 筆新的 ⇒ 先 rebase 接上"); return; }
        Git(iDir, SCP_Git.DefaultTimeoutMs, "rebase", "--abort");
        ioSync.Lines.Add($"· 本機有 {aAhead} 筆沒推、跟遠端的 {aBehind} 筆撞了 ⇒ 留給推送那一步重放");
    }

    /// <summary>
    /// 一次推送的結果，**以「最初那一筆」的 sha 為鍵**：重放會換 sha，而重放可能不只一輪
    /// （重放完再推又被拒）—— 不追回最初那一筆，第二輪改的號就回不到呼叫端手上。
    /// </summary>
    sealed class PushOutcome
    {
        public Dictionary<string, SCP_TaskOpResult> Replayed = new(StringComparer.Ordinal);
        public HashSet<string> NotApplied = new(StringComparer.Ordinal);
        readonly Dictionary<string, string> m_Root = new(StringComparer.Ordinal);

        public string Root(string iSha) => m_Root.TryGetValue(iSha, out string? r) ? r : iSha;
        public void Moved(string iFrom, string iTo) => m_Root[iTo] = Root(iFrom);
    }

    // ===========================================================
    // 區塊職責：推 —— 被拒 ⇒ fetch＋rebase；rebase 撞了 ⇒ 重放（`iAllowReplay`）。
    // 邊界：斷網／沒權限 ⇒ Pending（已在本機提交）；連續被拒 MaxPushTries 次 ⇒ Pending。
    // ===========================================================
    static PushOutcome Push(string iDataRoot, string iDir, TaskRepoTarget iTarget, TaskSyncReport ioSync,
                            bool iAllowReplay, string? iCurrentSha)
    {
        var aOut = new PushOutcome();
        for (int aTry = 0; aTry < MaxPushTries; ++aTry)
        {
            SCP_GitResult p = Git(iDir, NetTimeoutMs, "push", "origin", "HEAD:refs/heads/" + iTarget.Branch);
            if (p.Ok)
            {
                if (ioSync.State != TaskSyncState.Conflict) ioSync.State = TaskSyncState.Pushed;
                ioSync.Lines.Add($"✓ 已推上 `{iTarget.Repo}`（{iTarget.Branch}）：{p.ReasonLine}");
                return aOut;
            }
            if (!IsRejected(p))
            {
                if (ioSync.State != TaskSyncState.Conflict) ioSync.State = TaskSyncState.Pending;
                ioSync.Lines.Add($"⚠ 推不上去（{p.ReasonLine}）—— **已在本機提交**，Server 背景工作會再推；推上去之前新單的號碼可能還會變");
                return aOut;
            }
            SCP_GitResult f = Git(iDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec);
            if (!f.Ok)
            {
                ioSync.State = TaskSyncState.Pending;
                ioSync.Lines.Add($"⚠ 推送被拒、又抓不到遠端（{f.ReasonLine}）—— 已在本機提交，之後再推");
                return aOut;
            }
            SCP_GitResult rb = GitId(iDir, SCP_Git.DefaultTimeoutMs, "rebase", iTarget.RemoteRef);
            if (rb.Ok) { ioSync.Lines.Add("· 遠端有新的提交 ⇒ rebase 接上（沒有衝突），再推一次"); continue; }
            Git(iDir, SCP_Git.DefaultTimeoutMs, "rebase", "--abort");
            if (!iAllowReplay)
            {
                ioSync.State = TaskSyncState.Pending;
                ioSync.Lines.Add("⚠ 本機的提交跟遠端撞了 —— 背景工作不重放；跑 `senate cmd task-sync --arg op=sync` 或下一次寫單時處理");
                return aOut;
            }
            ioSync.Lines.Add("· rebase 撞到衝突 ⇒ 改用**重放**：把還沒推上去的操作在遠端最新版上重跑一次");
            if (!Replay(iDataRoot, iDir, iTarget, ioSync, aOut, iCurrentSha) && aOut.Replayed.Count == 0) return aOut;
        }
        if (ioSync.State != TaskSyncState.Conflict) ioSync.State = TaskSyncState.Pending;
        ioSync.Lines.Add($"⚠ 連續 {MaxPushTries} 次推不上去（遠端一直在動）—— 已在本機提交，之後再推");
        return aOut;
    }

    // ===========================================================
    // 區塊職責：重放 —— 先把現況存成備份 ref，退回遠端最新版，逐筆重跑本機沒推上去的操作。
    // 物理意義：create 重跑就重新配號（撞號自動解）；之後同一批裡引用舊號的操作（留言、關聯、epic）照改號表換成新號。
    //           第一筆不能安全重放的 ⇒ 它和它之後的都**不套**（後面的可能依賴它），寫衝突報告、交回 sync_conflict 通知。
    // 回傳：全部套上 ⇒ true。
    // ===========================================================
    static bool Replay(string iDataRoot, string iDir, TaskRepoTarget iTarget, TaskSyncReport ioSync, PushOutcome ioOut, string? iCurrentSha)
    {
        string aRemote = iTarget.RemoteRef;
        string aHead = RevParse(iDir, "HEAD");
        string aBase = Git(iDir, SCP_Git.DefaultTimeoutMs, "merge-base", "HEAD", aRemote).StdOut.Trim();
        List<string> aLocal = Git(iDir, SCP_Git.DefaultTimeoutMs, "rev-list", "--reverse", aRemote + "..HEAD").OutLines();
        var aRemoteTouched = new HashSet<string>(
            Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--name-only", aBase, aRemote).OutLines(), StringComparer.OrdinalIgnoreCase);

        var aItems = new List<(string Sha, JournalEntry? J, List<string> Files)>();
        foreach (string aSha in aLocal)
            aItems.Add((aSha, ReadJournal(iDir, aSha),
                Git(iDir, SCP_Git.DefaultTimeoutMs, "diff-tree", "--no-commit-id", "--name-only", "-r", aSha).OutLines()));

        // 有一筆不是寫入端記的（手改後收成的、舊的）⇒ 沒有東西可以重跑 ⇒ 一筆都不動，整批交給人
        int aNoJournal = aItems.FindIndex(x => x.J == null);
        if (aNoJournal >= 0)
        {
            ReportConflict(iDir, iTarget, ioSync, ioOut, aHead, aItems, aNoJournal,
                $"提交 `{Short(aItems[aNoJournal].Sha)}` 不是寫入端記錄的（沒有操作紀錄可以重放）", iCurrentSha, iReset: false);
            return false;
        }

        string aBackup = BackupRef(iDir, aHead, "task-sync");
        Git(iDir, SCP_Git.DefaultTimeoutMs, "reset", "--hard", aRemote);
        var aRenumber = new Dictionary<int, int>();
        var aRoot = new SCP_DataRoot(iDataRoot);
        for (int i = 0; i < aItems.Count; ++i)
        {
            var (aSha, aJ, aFiles) = aItems[i];
            JournalEntry j = aJ!;
            var aArgs = Remap(j.Args, aRenumber);
            string? aWhy = UnsafeReason(j, aFiles, aRemoteTouched);
            SCP_TaskOpResult? r = null;
            if (aWhy == null)
            {
                r = SCP_TaskOps.Run(aRoot, j.Op, j.Persona, aArgs);
                if (r.ExitCode != 0) aWhy = $"在遠端最新版上重跑 `{j.Op}` 被擋下：{r.Headline}";
            }
            if (aWhy != null)
            {
                ReportConflict(iDir, iTarget, ioSync, ioOut, aHead, aItems, i, aWhy, iCurrentSha, iReset: true, aBackup);
                return false;
            }
            if (r!.Wrote == 0)
            {
                // 冪等：遠端那邊已經是這個樣子（例：別台也認領了同一張）⇒ 沒東西要寫，不算衝突
                ioSync.Lines.Add($"· `{j.Op}` TASK-{j.Index:0000} 在遠端最新版上已經是這個樣子 ⇒ 不用重寫");
                ioOut.Replayed[ioOut.Root(aSha)] = r;
                continue;
            }
            if (j.Op == "create" && j.Index > 0 && r!.Index != j.Index)
            {
                aRenumber[j.Index] = r.Index;
                ioSync.Lines.Add($"🔀 TASK-{j.Index:0000} → **TASK-{r.Index:0000}**（遠端已經有 {j.Index:0000}：兩邊同時開單撞號 ⇒ 自動改號）");
                // 本次這一筆還沒公告過 ⇒ 入口公告的就是新號；之前那幾筆早就公告了舊號 ⇒ 要說一聲
                if (ioOut.Root(aSha) != iCurrentSha)
                    ioSync.Notices.Add(new SCP_TaskNotice
                    {
                        Kind = "sync_renumber",
                        TaskId = $"TASK-{r.Index:0000}",
                        Body = $"🔀 **TASK-{j.Index:0000} 改號為 TASK-{r.Index:0000}**：兩邊同時開單撞號（遠端已經有 {j.Index:0000}）。"
                             + $"之前提到 TASK-{j.Index:0000}「{TitleOf(aRoot, r.Index)}」的地方（commit 訊息、酒館）指的是這張新號。",
                    });
            }
            string? aNewSha = Commit(iDir, j.Op, j.Persona, r, ioSync);
            if (aNewSha != null)
            {
                WriteJournal(iDir, aNewSha, j with { Args = aArgs, Index = r.Index });
                ioOut.Moved(aSha, aNewSha);
            }
            ioOut.Replayed[ioOut.Root(aSha)] = r;
        }
        ioSync.Lines.Add($"✓ 重放完成：{aItems.Count} 筆都套在遠端最新版上了（重放前的版本留在 `{aBackup}`）");
        return true;
    }

    /// <summary>
    /// 這一筆不能自動重放的原因（null ＝ 可以）。判準：「加一筆」的 op 永遠可以；整段覆寫的 op
    /// 只有在遠端**沒碰**它改的那幾張單時才可以（否則重跑會蓋掉對方的改動）。
    /// </summary>
    static string? UnsafeReason(JournalEntry iJ, List<string> iFiles, HashSet<string> iRemoteTouched)
    {
        if (s_AppendOps.Contains(iJ.Op)) return null;
        if (iJ.Op == "check" && iJ.Args.TryGetValue("expect_text", out string? e) && e.Trim().Length > 0) return null;
        var aHit = iFiles.Where(f => !f.EndsWith("_index.txt", StringComparison.OrdinalIgnoreCase) && iRemoteTouched.Contains(f)).ToList();
        if (aHit.Count == 0) return null;
        string aWhat = iJ.Op == "check" ? "`check` 沒帶 expect_text（序號會位移）" : $"`{iJ.Op}` 會整段覆寫";
        return $"{aWhat}，而遠端也改了同一張（{string.Join("、", aHit)}）—— 自動重放會蓋掉對方的改動";
    }

    /// <summary>同一批裡引用了被改號的單 ⇒ 換成新號（index／target／epic_id）。</summary>
    static Dictionary<string, string> Remap(Dictionary<string, string> iArgs, Dictionary<int, int> iRenumber)
    {
        var aOut = new Dictionary<string, string>(iArgs, StringComparer.Ordinal);
        if (iRenumber.Count == 0) return aOut;
        foreach (string k in new[] { "index", "target", "epic_id" })
        {
            if (!aOut.TryGetValue(k, out string? v)) continue;
            int n = SCP_TaskStore.ParseTaskRef(v);
            if (n > 0 && iRenumber.TryGetValue(n, out int m)) aOut[k] = m.ToString(CultureInfo.InvariantCulture);
        }
        return aOut;
    }

    /// <summary>
    /// 自動解不了 ⇒ 交給人：衝突報告（每一筆沒套上的操作＋可以直接重跑的指令）、備份 ref、`sync_conflict` 通知。
    /// <para>已經重放成功的那幾筆照樣推上去（它們是對的）；沒套上的 ⇒ <see cref="PushOutcome.NotApplied"/>。</para>
    /// </summary>
    static void ReportConflict(string iDir, TaskRepoTarget iTarget, TaskSyncReport ioSync, PushOutcome ioOut, string iHead,
        List<(string Sha, JournalEntry? J, List<string> Files)> iItems, int iFrom, string iWhy, string? iCurrentSha,
        bool iReset, string? iBackup = null)
    {
        string aBackup = iBackup ?? BackupRef(iDir, iHead, "task-sync");
        string aStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string aFile = Path.Combine(GitDir(iDir), ConflictDirName, $"conflict_{aStamp}.md").Replace('\\', '/');
        var sb = new StringBuilder();
        sb.AppendLine($"# 任務同步衝突 {aStamp}");
        sb.AppendLine();
        sb.AppendLine($"- 遠端：`{iTarget.Repo}`（{iTarget.Branch}）");
        sb.AppendLine($"- 原因：{iWhy}");
        sb.AppendLine($"- 我的版本（衝突前的本機 HEAD）：`{aBackup}` ⇒ 看某一張：`git -C {iDir} show {aBackup}:tasks/<NNNN>.md`");
        sb.AppendLine(iReset
            ? $"- 共用板現在是遠端最新版{(iFrom > 0 ? $" ＋ 已重放成功的前 {iFrom} 筆" : "")}；下面這幾筆**沒有套上**："
            : "- 本機什麼都沒動（還停在衝突前），下面這幾筆**還沒推上去**：");
        sb.AppendLine();
        for (int i = iFrom; i < iItems.Count; ++i)
        {
            var (aSha, aJ, aFiles) = iItems[i];
            // 退回了遠端版 ⇒ 這幾筆不在共用板上；沒退（iReset=false）⇒ 還在本機、只是推不上去
            if (iReset) ioOut.NotApplied.Add(ioOut.Root(aSha));
            sb.AppendLine($"## `{Short(aSha)}` {(aJ == null ? "（沒有操作紀錄）" : $"op={aJ.Op} persona={aJ.Persona} TASK-{aJ.Index:0000}")}");
            sb.AppendLine($"- 動到：{string.Join("、", aFiles)}");
            if (aJ != null)
            {
                sb.AppendLine("- 參數（`args_json`）：");
                sb.AppendLine("```json");
                sb.AppendLine(JsonSerializer.Serialize(aJ.Args, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                sb.AppendLine("```");
                sb.AppendLine($"- 看過共用板上的現況、確定還要做 ⇒ 把上面那段存成檔，重跑：`senate cmd task-write --arg data_root=<資料根> --arg op={aJ.Op} --arg persona={aJ.Persona} --arg-file args_json=<檔>`");
            }
            sb.AppendLine();
        }
        try { Directory.CreateDirectory(Path.GetDirectoryName(aFile)!); File.WriteAllText(aFile, sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception e) { ioSync.Lines.Add($"⚠ 衝突報告沒寫成（{e.Message}）—— 備份 ref 還在：`{aBackup}`"); }

        ioSync.State = TaskSyncState.Conflict;
        ioSync.ConflictFile = aFile;
        ioSync.Lines.Add($"✗ **同步衝突，要手動處理**：{iWhy}");
        ioSync.Lines.Add($"  我的版本留在 `{aBackup}`；沒套上的 {iItems.Count - iFrom} 筆與重跑指令在 `{aFile}`");
        var aFirst = iItems[iFrom].J;
        ioSync.Notices.Add(new SCP_TaskNotice
        {
            Kind = "sync_conflict",
            TaskId = aFirst != null && aFirst.Index > 0 ? $"TASK-{aFirst.Index:0000}" : "",
            Body = $"⚠ **任務同步衝突，需要手動處理**：{iWhy}。"
                 + $"{(ioOut.Root(iItems[iFrom].Sha) == iCurrentSha ? "這一筆起" : "之前沒推上去的")} {iItems.Count - iFrom} 筆操作"
                 + $"{(iReset ? "**沒有套上共用板**" : "**還推不上去**（留在本機）")}；"
                 + $"我的版本留在 `{aBackup}`，細節與重跑指令：`{aFile}`",
        });
    }

    /// <summary>本次這一筆沒套上共用板 ⇒ 結果改成失敗（入口照「被擋下、零寫入」回報，衝突通知照發）。</summary>
    static void MarkNotApplied(SCP_TaskOpResult ioR, TaskSyncReport iSync)
    {
        ioR.ExitCode = 1;
        ioR.Wrote = 0;
        ioR.Values["wrote"] = "0";
        ioR.Headline = "✗ 這一筆沒有套上共用板（任務同步衝突，要手動處理）";
        ioR.Notices.Clear();
        ioR.Memory = null;
        ioR.Report.AppendLine();
        ioR.Report.AppendLine("## ❌ 同步衝突");
        ioR.Report.AppendLine("- 本機寫好了，可是推上遠端時跟別台的改動撞了，而且不能自動解 ⇒ **這一筆不在共用板上**。");
        if (iSync.ConflictFile.Length > 0) ioR.Report.AppendLine($"- 衝突報告（我的版本、重跑指令）：`{iSync.ConflictFile}`");
    }

    // ── commit ─────────────────────────────────────────────────

    /// <summary>把這一筆收成一個 commit（沒有東西可收 ⇒ null）。訊息第一行：`task(<op>): TASK-NNNN <摘要> (auto)`。</summary>
    static string? Commit(string iDir, string iOp, string iPersona, SCP_TaskOpResult iR, TaskSyncReport ioSync)
    {
        SCP_GitResult a = Git(iDir, SCP_Git.DefaultTimeoutMs, "add", "-A", "--", ".");
        if (!a.Ok) { ioSync.Lines.Add($"⚠ git add 失敗：{a.ReasonLine}"); return null; }
        if (Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--cached", "--quiet").Exit == 0) return null;
        string aHead = iR.Headline.TrimStart('✓', '✗', ' ');
        if (aHead.Length > 90) aHead = aHead.Substring(0, 90) + "…";
        string aId = iR.Index > 0 ? $"TASK-{iR.Index:0000} " : "";
        string aMsg = $"task({iOp}): {aId}{aHead} (auto)\n\npersona: {iPersona}\nop: {iOp}\n";
        SCP_GitResult c = GitId(iDir, SCP_Git.DefaultTimeoutMs, "commit", "-q", "-m", aMsg);
        if (!c.Ok) { ioSync.Lines.Add($"⚠ git commit 失敗：{c.ReasonLine}"); return null; }
        string aSha = RevParse(iDir, "HEAD");
        ioSync.Lines.Add($"· 已提交 `{Short(aSha)}` task({iOp}) {aId.Trim()}");
        return aSha;
    }

    /// <summary>工作區有沒提交的改動（上一次寫到一半、有人手改）⇒ 先收成一筆，免得被下一筆一起帶走而說不清是誰的。</summary>
    static void CommitLeftovers(string iDir, TaskSyncReport ioSync)
    {
        if (Git(iDir, SCP_Git.DefaultTimeoutMs, "status", "--porcelain").OutLines().Count == 0) return;
        Git(iDir, SCP_Git.DefaultTimeoutMs, "add", "-A", "--", ".");
        SCP_GitResult c = GitId(iDir, SCP_Git.DefaultTimeoutMs, "commit", "-q", "-m",
            "chore(task): 收編沒提交的改動 (auto)\n\n寫入端在寫下一筆之前發現工作區不乾淨 —— 這一筆沒有操作紀錄，撞了只能交給人。\n");
        if (c.Ok) ioSync.Lines.Add("⚠ Tasks 工作區有沒提交的改動 ⇒ 先收成一筆（沒有操作紀錄，之後撞了要手動處理）");
    }

    // ── 操作紀錄（住 git 目錄、不入版控）─────────────────────

    static void WriteJournal(string iDir, string iSha, JournalEntry iEntry)
    {
        string aDir = Path.Combine(GitDir(iDir), JournalDirName);
        Directory.CreateDirectory(aDir);
        File.WriteAllText(Path.Combine(aDir, iSha + ".json"), JsonSerializer.Serialize(iEntry), new UTF8Encoding(false));
    }

    static JournalEntry? ReadJournal(string iDir, string iSha)
    {
        string aPath = Path.Combine(GitDir(iDir), JournalDirName, iSha + ".json");
        if (!File.Exists(aPath)) return null;
        try { return JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(aPath)); }
        catch (JsonException) { return null; }
    }

    // ── 雜項 ───────────────────────────────────────────────────

    /// <summary>資料樹是 git repo ⇒ 把 Tasks/ 排除出去（本體在 <see cref="RepoSyncGit.ExcludeFromParent"/>）。</summary>
    static void ExcludeFromParent(string iDataRoot, string iTasksDir, TaskSyncReport ioSync)
    {
        string? aLine = RepoSyncGit.ExcludeFromParent(iDataRoot, iTasksDir, "任務單是另一個 repo（sync.tasks）");
        if (aLine != null) ioSync.Lines.Add(aLine);
    }

    static string TitleOf(SCP_DataRoot iRoot, int iIndex) => SCP_TaskIO.Find(iRoot, iIndex)?.title ?? "";
}
