// 區塊職責：把「這個 repo 的 submodule 現在是什麼狀態」收成一份可顯示、可批次的讀數 ——
//           **頁面與 CLI 吃同一份**，不各寫一套。
// 物理意義：多層 submodule 專案的日常痛點是「submodule update 之後全員 detached HEAD、
//           分支跑掉、誰 ahead 誰 behind 沒人一眼看得到」。
//           ⚠ 這一份是**照片**（拍攝當下的狀態），所以它只准用來**顯示與決定要不要動手**，
//             絕不能拿來當安全線 —— 真正的 dirty / branch 判斷由 SCP_GitSync 在動手前現場重問。
//             （🩸 兩次點擊之間，Unity Editor 會 import asset、寫 .meta、存 scene ⇒
//               照片乾淨、現在髒了的話，「dirty 就跳過」的承諾會靜默失效，而報告照印 ✓。）
// 數值影響：Scan 唯讀（fetch 是顯式開關，只動 remote-tracking ref）。
//           RunBatch 會改變狀態 —— 它的每一步都轉給 SCP_GitSync，本檔只負責**順序與範圍**。
using SCP.Core.Git;

namespace Senate.Core;

/// <summary>目標 branch 是**哪一層**解析出來的 —— 顯示這個是因為「它為什麼想切到 X」一定會被問。</summary>
public enum TargetBranchSource
{
    /// <summary>
    /// 五層都給不出答案 ⇒ 沒有目標，這一顆會被跳過。
    /// <para>⚠ 2026-09-16 之前這裡寫的是「不是『用目前所在』」——
    /// TASK-0225 之後「目前所在」**成為其中一層**（只接手盲猜那一格）。
    /// 現在真的落到 None 的只剩：detached HEAD、或問不到分支。</para>
    /// </summary>
    None = 0,

    /// <summary>逐項覆寫（本次執行指定）。</summary>
    Override,

    /// <summary><c>.gitmodules</c> 的 <c>branch =</c>（git 原生欄位，入版控）。</summary>
    Gitmodules,

    /// <summary>本次執行的全域預設。</summary>
    GlobalDefault,

    /// <summary>
    /// **目前所在的那條分支**（TASK-0225）。只在顯式三層都空時才輪到，⛔ 不贏任何顯式指定。
    /// <para>它與 <see cref="Heuristic"/> 的關係**不是單純的先後**：兩者一致時記成啟發式
    /// （保留「這是猜的」這個資訊）；**衝突、或啟發式猜不出來時才由它接手** ——
    /// 因為衝突就代表啟發式猜錯了，而這一邊是讀到的事實。</para>
    /// <para>⚠ detached HEAD 不算（那不是一條分支）⇒ 那種情況照舊走啟發式。</para>
    /// </summary>
    Current,

    /// <summary>啟發式（見 <see cref="SCP_GitSubmodule.HeuristicBranch"/>）。</summary>
    Heuristic,
}

/// <summary>一顆 submodule 的照片：靜態身分 ＋ 掃描當下的活讀數 ＋ 解析出來的目標。</summary>
public sealed class SubmoduleReading
{
    public required SCP_GitSubmoduleEntry Entry { get; init; }

    /// <summary>絕對路徑（root ＋ 相對路徑）。</summary>
    public required string AbsPath { get; init; }

    /// <summary>目前 branch；detached 是 <see cref="SCP_Git.DetachedHead"/>，問不到是 null。</summary>
    public string? CurrentBranch { get; init; }

    public SCP_GitDirtyState Dirty { get; init; }
    public SCP_GitAheadBehind AheadBehind { get; init; }
    public List<string> Remotes { get; init; } = new();
    public SCP_GitBranchList Branches { get; init; }

    /// <summary>上次 fetch 距今；null ＝ 沒 fetch 過。ahead/behind 的新鮮度就是這個值。</summary>
    public TimeSpan? FetchAge { get; init; }

    public string TargetBranch { get; init; } = "";
    public TargetBranchSource TargetSource { get; init; }

    public bool IsDetached => CurrentBranch == null || CurrentBranch == SCP_Git.DetachedHead;

    /// <summary>已經在目標分支上（目標空的時候不算 —— 沒有目標就沒有「對齊」可言）。</summary>
    public bool OnTarget => TargetBranch.Length > 0 && CurrentBranch == TargetBranch;

    /// <summary>fetch 新鮮度的一句話（逐列標，不用一句全域警語 —— 那會把剛 fetch 的跟三天沒動的混為一談）。</summary>
    public string FetchAgeText
    {
        get
        {
            if (Entry.Uninitialized) return "-";
            if (FetchAge is not TimeSpan aAge) return "未 fetch 過";
            if (aAge.TotalHours < 1) return $"{(int)aAge.TotalMinutes}m 前";
            if (aAge.TotalDays < 1) return $"{(int)aAge.TotalHours}h 前";
            return $"{(int)aAge.TotalDays}d 前";
        }
    }
}

/// <summary>一次掃描的結果。</summary>
public sealed class SubmoduleScanResult
{
    public required string Root { get; init; }

    /// <summary>掃到的 submodule。</summary>
    public List<SubmoduleReading> Items { get; init; } = new();

    /// <summary>
    /// 掃描本身有沒有成功。
    /// <para>⚠ <c>Ok=true</c> ＋ 空清單 ＝ **這個 repo 真的沒有 submodule**；
    /// <c>Ok=false</c> ＝ 問不到。兩者不得同形 —— 壓成「空清單」之後，
    /// 一個壞掉的 repo 會看起來完全正常。</para>
    /// </summary>
    public bool Ok { get; init; }

    public string Error { get; init; } = "";

    /// <summary>掃描過程中的警告（fetch 失敗之類）—— 不致命，但要看得見。</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>這次掃描有沒有先 fetch（決定 ahead/behind 能不能當即時值看）。</summary>
    public bool Fetched { get; init; }
}

/// <summary>批次執行一輪的結果（逐 repo 一筆）。</summary>
public sealed record SubmoduleSyncRow(string Label, SCP_GitSyncOutcome Outcome, string Summary);

public static class SubmoduleScan
{
    /// <summary>本層所有 git 呼叫的登記 tag（見 SCP_ProcessRegistry）。</summary>
    public const string ProcessTag = "senate_submodule_sync";

    /// <summary>
    /// 掃描 <paramref name="iRoot"/> 底下所有 submodule（含巢狀）。
    /// <para><paramref name="iFetch"/>：先逐顆 fetch 再讀。分成開關而不是永遠 fetch ——
    /// **掃描要快、fetch 要準，那是兩個不同的問題**（掃描每次進頁自動跑，fetch 走網路由人顯式要求）。</para>
    /// <para><paramref name="iOverrides"/>：相對路徑 → 指定 branch（最高優先）。</para>
    /// </summary>
    public static SubmoduleScanResult Scan(string iRoot, bool iFetch = false,
        string? iGlobalDefault = null, IReadOnlyDictionary<string, string>? iOverrides = null,
        Action<string>? iProgress = null)
    {
        var aWarnings = new List<string>();

        if (string.IsNullOrWhiteSpace(iRoot) || !Directory.Exists(iRoot))
            return new SubmoduleScanResult { Root = iRoot ?? "", Ok = false, Error = $"路徑不存在：{iRoot}" };
        if (!SCP_Git.IsRepo(iRoot))
            return new SubmoduleScanResult { Root = iRoot, Ok = false, Error = $"不是 git repo：{iRoot}" };

        using var aScope = SCP_Git.Scope(ProcessTag, nameof(SubmoduleScan));

        if (!SCP_GitSubmodule.TryStatus(iRoot, iRecursive: true, out var aEntries, out string aErr))
            return new SubmoduleScanResult { Root = iRoot, Ok = false, Error = $"git submodule status 失敗：{aErr}" };

        SCP_GitSubmodule.FillGitmodulesBranch(iRoot, aEntries);

        var aItems = new List<SubmoduleReading>();
        foreach (var aEntry in aEntries)
        {
            string aAbs = Path.Combine(iRoot, aEntry.Path.Replace('/', Path.DirectorySeparatorChar));

            // 未 init 的沒有工作目錄可問 —— 逐項讀數全部跳過，但**這一列仍然要列出來**
            //（「內容不在本機」是一個要看見的狀態，不是一個該消失的列）。
            if (aEntry.Uninitialized)
            {
                aItems.Add(new SubmoduleReading { Entry = aEntry, AbsPath = aAbs });
                continue;
            }

            iProgress?.Invoke(aEntry.Path);

            if (iFetch)
            {
                var aFetch = SCP_GitSync.Fetch(aAbs);
                if (!aFetch.Ok) aWarnings.Add($"⚠ fetch 失敗 {aEntry.Path}：{aFetch.ReasonLine}");
            }

            var aBranches = SCP_GitRepo.Branches(aAbs);
            aEntry.HeuristicBranch = SCP_GitSubmodule.HeuristicBranch(aEntry.Path, aBranches);

            string? aOverride = null;
            iOverrides?.TryGetValue(aEntry.Path, out aOverride);
            // ⚠ 只問一次 git：下面 `CurrentBranch` 那格用同一個值。
            //   分兩次問的話，兩次之間 HEAD 可能已經動了 ⇒ 「目標」與「目前」會各自描述不同的時刻，
            //   而畫面上它們並排、看起來像同一個瞬間的兩欄。
            string? aCurrent = SCP_GitRepo.Branch(aAbs);
            var (aTarget, aSource) = ResolveTarget(aEntry, aOverride, iGlobalDefault, aCurrent);

            DateTime? aLastFetch = SCP_GitRepo.LastFetchUtc(aAbs);

            aItems.Add(new SubmoduleReading
            {
                Entry = aEntry,
                AbsPath = aAbs,
                CurrentBranch = aCurrent,
                Dirty = SCP_GitRepo.DirtyState(aAbs),
                AheadBehind = SCP_GitRepo.AheadBehind(aAbs),
                Remotes = SCP_GitRepo.Remotes(aAbs),
                Branches = aBranches,
                FetchAge = aLastFetch is DateTime aTime ? DateTime.UtcNow - aTime : null,
                TargetBranch = aTarget,
                TargetSource = aSource,
            });
        }

        return new SubmoduleScanResult
        {
            Root = iRoot, Ok = true, Items = aItems, Warnings = aWarnings, Fetched = iFetch,
        };
    }

    /// <summary>
    /// 目標 branch 四層解析 ＋ **它是哪一層來的**。
    /// <para>回傳來源不是裝飾：使用者看到「它想把我切到 Dev」時的第一個問題是「憑什麼」，
    /// 而一個算好的答案不帶來源的話，人只能猜 —— 猜錯的代價是把別人的分支切掉。</para>
    /// </summary>
    public static (string Target, TargetBranchSource Source) ResolveTarget(
        SCP_GitSubmoduleEntry iEntry, string? iOverride, string? iGlobalDefault,
        string? iCurrentBranch = null)
    {
        if (!string.IsNullOrEmpty(iOverride)) return (iOverride!, TargetBranchSource.Override);
        if (!string.IsNullOrEmpty(iEntry.GitmodulesBranch))
            return (iEntry.GitmodulesBranch, TargetBranchSource.Gitmodules);
        if (!string.IsNullOrEmpty(iGlobalDefault)) return (iGlobalDefault!, TargetBranchSource.GlobalDefault);

        // ⭐ TASK-0225：**目前所在**排在盲猜之前。
        // 🩸 為什麼加這一層（2026-09-16 實測，掃 `D:\Unity\LY` 共 25 顆）：
        //   24 顆「目前＝目標」，只有 `AgentCommands` 不符 —— 它在 `LY` 分支上，
        //   而啟發式的規則是「只有一條分支就用它／否則 master／沒 master 才 main」
        //   ⇒ 猜成 `main`，那一列的 push／pull 就打到錯的分支。
        //   繞法都很貴：改全域預設會弄壞另外 24 顆，逐項覆寫要每次重打。
        // ⚠ 它**只贏盲猜，不贏任何顯式指定**：上面三層都是人（或 git）明講的目標，
        //   而本頁的用途正是「把 submodule **搬到**目標分支上」——
        //   ⛔ 讓「目前所在」蓋過顯式指定等於把這個功能做沒，那正是原設計那句
        //   「不拿目前所在頂替」擋的事。這一層只接手它**本來就要用猜的**那一格。
        // ⚠ detached HEAD 不適用：那時 `SCP_GitRepo.Branch` 回的是 `DetachedHead` 哨兵，
        //   它不是一條分支名 ⇒ 排除掉，讓它照舊落到「解析不到」。
        //   🩸 不排除的話會拿一個哨兵字串當 branch 名去跑 git，而那種錯要到 git 指令才會叫。
        bool aHasCurrent = !string.IsNullOrEmpty(iCurrentBranch)
                           && iCurrentBranch != SCP_Git.DetachedHead;

        // 啟發式猜得出來、而且**跟現況一致** ⇒ 照舊記成「啟發式」。
        // 🩸 為什麼不讓「目前所在」無條件贏（2026-09-16 實測否證了我的第一版）：
        //   第一版寫成「目前所在排在啟發式之前」，結果 25 列**全部**變成「目前所在」——
        //   因為絕大多數 repo 的現況本來就等於啟發式猜的那條。
        //   ⇒ 啟發式那層變成死碼，而畫面對每一列都說「目標＝你現在在的地方」，
        //     那正是原設計那句「拿目前所在頂替 ＝ 等於沒有這個功能」在防的事。
        //   ⇒ 所以收窄成：**只在兩者衝突時**才由現況接手（衝突＝啟發式猜錯了）。
        if (!string.IsNullOrEmpty(iEntry.HeuristicBranch)
            && (!aHasCurrent || iEntry.HeuristicBranch == iCurrentBranch))
            return (iEntry.HeuristicBranch, TargetBranchSource.Heuristic);

        // 走到這裡有兩種：啟發式猜不出來，或它猜的跟現況不一樣。
        // 兩種都由**讀到的事實**接手 —— 它至少保證 push／pull 打得到一條真的存在的分支。
        if (aHasCurrent) return (iCurrentBranch!, TargetBranchSource.Current);

        return ("", TargetBranchSource.None);
    }

    public static string SourceText(TargetBranchSource iSource) => iSource switch
    {
        TargetBranchSource.Override => "指定",
        TargetBranchSource.Gitmodules => ".gitmodules",
        TargetBranchSource.GlobalDefault => "全域預設",
        TargetBranchSource.Current => "目前所在",
        TargetBranchSource.Heuristic => "啟發式",
        _ => "解析不到",
    };

    /// <summary>
    /// 對掃到的 submodule 跑一輪 checkout / pull / push。
    /// <para><b>順序：由深到淺，root 最後。</b> parent 的 bump commit 引用 child 的 SHA ——
    /// 先推 parent 的話別人 pull 下來會拿到指向遠端還不存在的 commit 的 gitlink，
    /// 而且是靜默壞（只有 clone / update 的人才會發現）。</para>
    /// <para>⚠ 本方法**不做**任何安全判斷：dirty / branch / remote 全部由 SCP_GitSync
    /// 在動手前現場重問。這裡只決定「範圍」與「順序」——
    /// 讓「照片」與「決定」的界線落在一個看得見的地方。</para>
    /// <para><paramref name="iIncludeRoot"/>：root 一起 pull / push。
    /// **root 永遠不切 branch** —— 專案根換分支該是人自己下的動作。</para>
    /// </summary>
    public static List<SubmoduleSyncRow> RunBatch(SubmoduleScanResult iScan, SCP_GitSyncOptions iOptions,
        bool iIncludeRoot = false, IReadOnlyCollection<string>? iOnly = null,
        Action<string>? iLog = null)
    {
        var aRows = new List<SubmoduleSyncRow>();
        using var aScope = SCP_Git.Scope(ProcessTag, nameof(SubmoduleScan));

        var aTargets = new List<SubmoduleReading>();
        foreach (var aItem in iScan.Items)
        {
            if (iOnly != null && !iOnly.Contains(aItem.Entry.Path)) continue;
            if (aItem.Entry.Uninitialized)
            {
                // 未 init 不是失敗也不是成功，是「內容不在本機」⇒ 列出來跳過，不靜默消失。
                aRows.Add(new SubmoduleSyncRow(aItem.Entry.Path, SCP_GitSyncOutcome.Skipped, "⏭ 未 init"));
                continue;
            }
            aTargets.Add(aItem);
        }
        // 深 → 淺
        aTargets.Sort((iA, iB) => iB.Entry.Depth.CompareTo(iA.Entry.Depth));

        foreach (var aItem in aTargets)
        {
            var aRes = SCP_GitSync.Apply(aItem.AbsPath, aItem.Entry.Path, aItem.TargetBranch, iOptions, iLog);
            aRows.Add(new SubmoduleSyncRow(aItem.Entry.Path, aRes.Outcome, aRes.Summary));
        }

        if (iIncludeRoot && (iOptions.Pull || iOptions.Push))
        {
            string? aRootBranch = SCP_GitRepo.Branch(iScan.Root);
            var aRootOptions = new SCP_GitSyncOptions
            {
                Checkout = false,               // root 永遠不切
                Pull = iOptions.Pull,
                Push = iOptions.Push,
                PushAllRemotes = iOptions.PushAllRemotes,
                PullRemote = iOptions.PullRemote,
            };
            if (aRootBranch == null || aRootBranch == SCP_Git.DetachedHead)
            {
                iLog?.Invoke($"⏭ (root) 目前是 {(aRootBranch ?? "問不到")} —— root 不自動切，請自行處理");
                aRows.Add(new SubmoduleSyncRow("(root)", SCP_GitSyncOutcome.Skipped, "⏭ root detached"));
            }
            else
            {
                // root 的「目標」就是它現在所在的分支（因為不切）—— 這樣 pull / push 的
                // 「不在目標 branch」那道檢查對 root 永遠成立，而不是永遠擋住它。
                var aRes = SCP_GitSync.Apply(iScan.Root, "(root)", aRootBranch, aRootOptions, iLog);
                aRows.Add(new SubmoduleSyncRow("(root)", aRes.Outcome, aRes.Summary));
            }
        }

        return aRows;
    }
}
