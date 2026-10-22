// 區塊職責：閱讀筆記（`<資料根>/BookNotes`：閱讀庫 Library/、書的草稿）跟遠端 git repo 同步（`sync.bookNotes`，Tim 2026-10-22：
//           「https://github.com/tim099/BookNotes.git 接下來是Booknotes 可以把妳剛才的閱讀心得加進去」；
//           同一輪的前提：「最小功能repo內 缺的資料夾&設定都能在使用時自動補上」「不能自動解的要抱錯通知手動處理」）。
// 物理意義：本機的 `BookNotes/` **就是**遠端那條 branch 的工作副本 —— library／book 讀寫的路徑一行都不用改。
//           用之前先拉（節流：每 PullEverySec 秒最多抓一次）；寫完把改動收成一個 commit 推上去。
//           筆記幾乎都是「各人寫各人的檔」⇒ 兩台同時寫，rebase 就接得上；
//           兩邊改了同一個檔而 rebase 接不上 ⇒ **不自動解**：rebase 退回、本機的提交留著（不丟，之後照樣再試），
//           寫一份衝突報告（兩邊各改了哪些檔、怎麼手動解）＋備份 ref，交回通知（同一個衝突只通知一次）。
//           ⭐ 已經有 `BookNotes/`、可是還不是 git repo（設同步之前就在這台寫過）⇒ **就地接上**：
//             本機獨有的檔收成一筆推上去；遠端獨有的拉下來；兩邊都有而內容不同 ⇒ 以遠端為準，本機那份備份起來並回報。
// ⚠ 只在 CLI 那一邊跑（library／book 本來就是本地跑的 Cmd），⛔ 不掛 Server 背景工作：
//   🩸 2026-10-22 實測：經 WMI 脫樹起的 Server 一跑 git 同步，就被 Microsoft Defender 的行為偵測
//   （Behavior:Win32/Ransomware!GenRemote.X1）整顆砍掉（細節：Task_Management §12）。
//   同一顆 repo 可能有好幾個 CLI 同時在跑 ⇒ git 操作一律在跨行程的具名 mutex 裡。
#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Books;
using SCP.Core.Git;
using SCP.Core.Letters;
using static Senate.Core.RepoSyncGit;

namespace Senate.Core;

public enum RepoSyncState
{
    /// <summary>沒設定遠端 ⇒ 照舊只寫本機。</summary>
    Off,
    /// <summary>跟遠端一樣（或這次在節流內、沒去抓）。</summary>
    UpToDate,
    /// <summary>已推上遠端。</summary>
    Pushed,
    /// <summary>已在本機提交、還沒推上去（斷網／沒權限／遠端一直在動／別的行程正在同步）—— 下次用到時再推。</summary>
    Pending,
    /// <summary>自動解不了的衝突 ⇒ 要人處理（本機提交還在、備份 ref ＋ 衝突報告）。</summary>
    Conflict,
    /// <summary>本機那一份跟設定對不上（origin 或 branch 不同）⇒ 不同步。</summary>
    Mismatch,
    /// <summary>clone／git 本身失敗。</summary>
    Failed,
}

/// <summary>一次同步的回報 —— 入口把 <see cref="Lines"/> 印給人看、<see cref="Notices"/> 發酒館。</summary>
public sealed class RepoSyncReport
{
    public RepoSyncState State = RepoSyncState.Off;
    public List<string> Lines = new();
    /// <summary>要讓相關的人知道的事（衝突、接上時被遠端版本取代的檔）。</summary>
    public List<string> Notices = new();
    /// <summary>衝突報告的路徑（只有 <see cref="RepoSyncState.Conflict"/> 有）。</summary>
    public string ConflictFile = "";
    /// <summary>這次有沒有真的去抓遠端（節流內 ⇒ false）。</summary>
    public bool Fetched;

    public string StateWord => State switch
    {
        RepoSyncState.Off => "off",
        RepoSyncState.UpToDate => "up_to_date",
        RepoSyncState.Pushed => "pushed",
        RepoSyncState.Pending => "pending",
        RepoSyncState.Conflict => "conflict",
        RepoSyncState.Mismatch => "mismatch",
        _ => "failed",
    };
}

/// <summary>本機那一份的讀數（`booknotes-sync op=status` 與路徑管理頁用；只有 <c>iFetch</c> 才走網路）。</summary>
public sealed record BookNotesRepoStatus(bool Configured, bool Exists, bool IsRepo, string Origin, string Branch,
                                         int Ahead, int Behind, int Dirty, string Note);

public static class SenateBookNotesSync
{
    /// <summary>「用之前先拉」的節流：同一顆 repo 每這麼多秒最多抓一次遠端（一次閱讀會連跑好幾支 library）。</summary>
    public const int PullEverySec = 60;

    const int NetTimeoutMs = 120_000;
    const int MaxPushTries = 4;
    const int LockWaitSec = 60;
    const string StateDirName = "senate-booknotes-sync";
    const string BackupKind = "booknotes-sync";
    const string ExcludeWhat = "閱讀筆記是另一個 repo（sync.bookNotes）";

    public static string Dir(string iDataRoot) => SCP_BookStore.BookNotesRoot(iDataRoot);

    // ===========================================================
    // 區塊職責：用之前（CLI 跑 library／book 之前；`booknotes-sync op=sync` 用 iPullEverySec=0）。
    // 物理意義：缺目錄 ⇒ clone；有、但不是 repo ⇒ 就地接上；然後拉（節流）；有還沒推上去的 ⇒ 推。
    //           拉不到（斷網）⇒ Pending，照本機的版本繼續 —— ⛔ 不擋閱讀。
    // ===========================================================
    public static RepoSyncReport BeforeUse(string iDataRoot, RepoSyncTarget iTarget, int iPullEverySec = PullEverySec)
    {
        var r = new RepoSyncReport();
        using MutexLock? aLock = MutexLock.Acquire(Dir(iDataRoot), r);
        if (aLock == null) return r;
        if (!Ensure(iDataRoot, iTarget, r)) return r;
        string aDir = Dir(iDataRoot);
        if (!FetchDue(aDir, iPullEverySec))
        {
            if (r.State == RepoSyncState.Off) r.State = RepoSyncState.UpToDate;
            return r;
        }
        Pull(aDir, iTarget, r);
        if (r.State is RepoSyncState.Pending or RepoSyncState.Failed or RepoSyncState.Conflict) return r;
        (int aAhead, _) = Diverge(aDir, iTarget);
        if (aAhead > 0) Push(aDir, iTarget, r);
        else if (r.State == RepoSyncState.Off) r.State = RepoSyncState.UpToDate;
        return r;
    }

    // ===========================================================
    // 區塊職責：寫完（CLI 跑完 library／book 之後）—— 有改動就收成一個 commit，推。
    // 邊界：沒有改動、也沒有還沒推的 ⇒ UpToDate（讀的 op 走這條）；推不上去 ⇒ Pending（已在本機提交）。
    // ===========================================================
    public static RepoSyncReport AfterWrite(string iDataRoot, RepoSyncTarget iTarget, string iSubject, string iPersona)
    {
        var r = new RepoSyncReport();
        using MutexLock? aLock = MutexLock.Acquire(Dir(iDataRoot), r);
        if (aLock == null) return r;
        if (!Ensure(iDataRoot, iTarget, r)) return r;
        string aDir = Dir(iDataRoot);
        Commit(aDir, iSubject, iPersona, r);
        (int aAhead, _) = Diverge(aDir, iTarget);
        if (aAhead == 0)
        {
            if (r.State == RepoSyncState.Off) r.State = RepoSyncState.UpToDate;
            return r;
        }
        Push(aDir, iTarget, r);
        return r;
    }

    /// <summary>本機那一份的讀數。<c>iFetch</c> ⇒ 先抓一次遠端（走網路），否則只看本機已知的。</summary>
    public static BookNotesRepoStatus Status(string iDataRoot, RepoSyncTarget? iTarget, bool iFetch)
    {
        string aDir = Dir(iDataRoot);
        bool aExists = Directory.Exists(aDir);
        bool aRepo = aExists && IsRepoRoot(aDir);
        if (iTarget == null)
            return new BookNotesRepoStatus(false, aExists, aRepo, "", "", 0, 0, 0, "沒設定遠端（sync.bookNotes）⇒ 只寫本機");
        if (!aExists)
            return new BookNotesRepoStatus(true, false, false, "", "", 0, 0, 0, "還沒 clone —— 第一次用到（library／book）時會自動 clone");
        if (!aRepo)
            return new BookNotesRepoStatus(true, true, false, "", "", 0, 0, 0, "BookNotes 在、但還不是 git repo —— 第一次用到時會就地接上遠端");
        var r = new RepoSyncReport();
        using MutexLock? aLock = MutexLock.Acquire(aDir, r);
        if (aLock == null) return new BookNotesRepoStatus(true, true, true, "", "", 0, 0, 0, string.Join(" ", r.Lines));
        if (iFetch && Git(aDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec).Ok) MarkFetched(aDir);
        string aOrigin = Git(aDir, SCP_Git.DefaultTimeoutMs, "remote", "get-url", "origin").StdOut.Trim();
        string aBranch = Git(aDir, SCP_Git.DefaultTimeoutMs, "rev-parse", "--abbrev-ref", "HEAD").StdOut.Trim();
        (int aAhead, int aBehind) = Diverge(aDir, iTarget);
        int aDirty = Git(aDir, SCP_Git.DefaultTimeoutMs, "status", "--porcelain").OutLines().Count;
        string? aOpen = OpenConflictFile(aDir);
        string aNote = !SameRepo(aOrigin, iTarget.Repo) ? "⚠ origin 跟設定的 repo 不同 ⇒ 不同步"
                     : aBranch != iTarget.Branch ? $"⚠ 現在在 `{aBranch}`，設定是 `{iTarget.Branch}` ⇒ 不同步"
                     : aOpen != null ? $"✗ 有一個還沒解的衝突（報告：`{aOpen}`）"
                     : aAhead > 0 ? $"有 {aAhead} 筆還沒推上去"
                     : aBehind > 0 ? $"落後遠端 {aBehind} 筆（下一次用到會拉）"
                     : "跟遠端一致";
        return new BookNotesRepoStatus(true, true, true, aOrigin, aBranch, aAhead, aBehind, aDirty, aNote);
    }

    // ===========================================================
    // 區塊職責：確保本機那一份可以同步 —— 缺目錄 ⇒ clone；在、但不是 repo ⇒ 就地接上；是 repo ⇒ 核對 origin／branch。
    // 物理意義：對不上就**不同步**（照舊讀寫本機）—— ⛔ 不替人改 origin、不切 branch、不碰停在一半的 rebase。
    // ===========================================================
    static bool Ensure(string iDataRoot, RepoSyncTarget iTarget, RepoSyncReport ioR)
    {
        string aDir = Dir(iDataRoot);
        if (!Directory.Exists(iDataRoot))
        {
            ioR.State = RepoSyncState.Failed;
            ioR.Lines.Add($"✗ 資料根不存在：{iDataRoot} —— 不 clone（⛔ 不在一個不存在的地方長出筆記庫）");
            return false;
        }
        if (!Directory.Exists(aDir))
        {
            SCP_GitResult c = Git(iDataRoot, NetTimeoutMs, "clone", "--branch", iTarget.Branch, "--", iTarget.Repo, aDir);
            if (c.Ok)
            {
                ioR.Lines.Add($"✓ 第一次用到 ⇒ 已從 `{iTarget.Repo}`（{iTarget.Branch}）clone 到 `{aDir}`");
                MarkFetched(aDir);
            }
            else if (RemoteHasNoBranch(iDataRoot, iTarget))
            {
                // 遠端連得上、只是還沒有這條 branch（全新的空 repo）⇒ 長一個空的出來接上，第一筆寫入時推上去
                Directory.CreateDirectory(aDir);
                if (!Adopt(aDir, iTarget, ioR)) return false;
            }
            else
            {
                ioR.State = RepoSyncState.Failed;
                ioR.Lines.Add($"✗ clone 失敗（{iTarget.Repo} {iTarget.Branch}）：{c.ReasonLine} —— 照舊寫本機；下次用到時會就地接上");
                if (Directory.Exists(aDir) && !IsRepoRoot(aDir) && !Directory.EnumerateFileSystemEntries(aDir).Any()) Directory.Delete(aDir);
                return false;
            }
        }
        else if (!IsRepoRoot(aDir))
        {
            if (!Adopt(aDir, iTarget, ioR)) return false;
        }

        string aOrigin = Git(aDir, SCP_Git.DefaultTimeoutMs, "remote", "get-url", "origin").StdOut.Trim();
        if (!SameRepo(aOrigin, iTarget.Repo))
        {
            ioR.State = RepoSyncState.Mismatch;
            ioR.Lines.Add($"⚠ BookNotes 的 origin 是 `{aOrigin}`，設定是 `{iTarget.Repo}` ⇒ **不同步**（⛔ 不替人改 origin）");
            return false;
        }
        string aBranch = Git(aDir, SCP_Git.DefaultTimeoutMs, "rev-parse", "--abbrev-ref", "HEAD").StdOut.Trim();
        if (aBranch != iTarget.Branch)
        {
            ioR.State = RepoSyncState.Mismatch;
            ioR.Lines.Add($"⚠ BookNotes 現在在 `{aBranch}`，設定是 `{iTarget.Branch}` ⇒ **不同步**（⛔ 不替人切 branch）");
            return false;
        }
        if (InRebase(aDir))
        {
            ioR.State = RepoSyncState.Failed;
            ioR.Lines.Add($"✗ BookNotes 停在一次沒做完的 rebase（多半是有人在手動解衝突）⇒ 先不同步。解完 `git -C {aDir} rebase --continue`（或 `--abort`）再來");
            return false;
        }
        string? aExcl = ExcludeFromParent(iDataRoot, aDir, ExcludeWhat);
        if (aExcl != null) ioR.Lines.Add(aExcl);
        return true;
    }

    /// <summary>
    /// 就地接上：`BookNotes/` 已經有東西、但還不是 git repo（設同步之前就在這台寫過）。
    /// <para>做法：init → 抓遠端 → 把 HEAD／index 指到遠端那一版（工作區不動）→ 兩邊都有而內容不同的先備份本機那份 →
    /// 工作區的已追蹤檔一律還原成遠端版（含本機沒有的）→ 剩下的就是本機獨有的 ⇒ 收成一筆。</para>
    /// <para>⛔ 抓不到遠端 ⇒ 把剛建的 `.git` 收掉、什麼都不動（下次用到再試）—— 不留一個半接上的 repo。</para>
    /// </summary>
    static bool Adopt(string iDir, RepoSyncTarget iTarget, RepoSyncReport ioR)
    {
        ioR.Lines.Add($"· `{iDir}` 已經在、但還不是 git repo ⇒ 就地接上 `{iTarget.Repo}`（{iTarget.Branch}）");
        bool Undo(string iWhy)
        {
            DeleteTreeForce(Path.Combine(iDir, ".git"));
            ioR.State = RepoSyncState.Failed;
            ioR.Lines.Add($"✗ 接不上：{iWhy} —— 已收掉剛建的 `.git`，本機的檔一個都沒動；下次用到時再試");
            return false;
        }
        if (!Git(iDir, SCP_Git.DefaultTimeoutMs, "init", "-q", "-b", iTarget.Branch).Ok) return Undo("git init 失敗");
        if (!Git(iDir, SCP_Git.DefaultTimeoutMs, "remote", "add", "origin", iTarget.Repo).Ok) return Undo("加不上 origin");
        SCP_GitResult f = Git(iDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec);
        bool aEmptyRemote = !f.Ok && RemoteHasNoBranch(iDir, iTarget);
        if (!f.Ok && !aEmptyRemote) return Undo($"抓不到遠端（{f.ReasonLine}）");
        if (f.Ok) MarkFetched(iDir);

        var aDiffer = new List<string>();
        int aFromRemote = 0;
        string aBackupDir = "";
        if (!aEmptyRemote)
        {
            if (!Git(iDir, SCP_Git.DefaultTimeoutMs, "reset", "-q", iTarget.RemoteRef).Ok) return Undo("reset 到遠端版失敗");
            aDiffer = Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--name-only", "--diff-filter=M").OutLines();
            aFromRemote = Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--name-only", "--diff-filter=D").OutLines().Count;
            if (aDiffer.Count > 0)
            {
                string aStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                aBackupDir = Path.Combine(GitDir(iDir), StateDirName, "adopt-" + aStamp).Replace('\\', '/');
                foreach (string aRel in aDiffer)
                {
                    string aTo = Path.Combine(aBackupDir, aRel);
                    Directory.CreateDirectory(Path.GetDirectoryName(aTo)!);
                    File.Copy(Path.Combine(iDir, aRel), aTo, true);
                }
            }
            if (aDiffer.Count > 0 || aFromRemote > 0)
            {
                SCP_GitResult co = Git(iDir, SCP_Git.DefaultTimeoutMs, "checkout", "--", ".");
                if (!co.Ok) ioR.Lines.Add($"⚠ 還原成遠端版時出錯（{co.ReasonLine}）—— 去看 `git -C {iDir} status`");
            }
        }
        Git(iDir, SCP_Git.DefaultTimeoutMs, "add", "-A", "--", ".");
        int aLocalOnly = Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--cached", "--name-only").OutLines().Count;
        if (aLocalOnly > 0)
        {
            SCP_GitResult c = GitId(iDir, SCP_Git.DefaultTimeoutMs, "commit", "-q", "-m",
                $"booknotes: 接上遠端 —— 收編這台原本就有的 {aLocalOnly} 個檔 (auto)\n");
            if (!c.Ok) ioR.Lines.Add($"⚠ 收編本機的檔時 commit 失敗（{c.ReasonLine}）—— 下次同步會再收");
        }
        ioR.Lines.Add(aEmptyRemote
            ? $"✓ 已接上（遠端還沒有 `{iTarget.Branch}` ⇒ 這台的 {aLocalOnly} 個檔就是第一版，等一下推上去）"
            : $"✓ 已接上：遠端的檔拉下來 {aFromRemote} 個（這台原本沒有）、這台獨有的 {aLocalOnly} 個收成一筆（等一下推上去）"
              + (aDiffer.Count > 0 ? $"、兩邊都有而內容不同的 {aDiffer.Count} 個用遠端版" : ""));
        if (aDiffer.Count > 0)
        {
            ioR.Lines.Add($"⚠ 兩邊都有、內容不同 ⇒ 用了遠端的版本，這台原本的那份備份在 `{aBackupDir}`：{string.Join("、", aDiffer)}");
            ioR.Notices.Add($"⚠ **BookNotes 接上遠端時，有 {aDiffer.Count} 個檔兩邊內容不同** ⇒ 先用了遠端的版本：{string.Join("、", aDiffer.Select(x => "`" + x + "`"))}。"
                          + $"這台原本的版本備份在 `{aBackupDir}` —— 要用這台的那份就複製回去，下次寫筆記時會一起推上去。");
        }
        return true;
    }

    /// <summary>
    /// 領先／落後幾筆。⚠ 本機還沒有遠端那條的追蹤 ref（遠端是空的、還沒推過）⇒ 本機每一筆都算領先 ——
    /// 🩸 照算 `HEAD...origin/x` 的話 rev-list 失敗、回 (0,0)，於是「第一版」永遠不會被推上去。
    /// </summary>
    static (int Ahead, int Behind) Diverge(string iDir, RepoSyncTarget iTarget)
    {
        if (Git(iDir, SCP_Git.DefaultTimeoutMs, "rev-parse", "--verify", "--quiet", "refs/remotes/" + iTarget.RemoteRef).Ok)
            return AheadBehind(iDir, iTarget.RemoteRef);
        SCP_GitResult c = Git(iDir, SCP_Git.DefaultTimeoutMs, "rev-list", "--count", "HEAD");
        return (c.Ok && int.TryParse(c.StdOut.Trim(), out int n) ? n : 0, 0);
    }

    /// <summary>遠端連得上、只是還沒有這條 branch？（全新的空 repo）—— 連不上 ⇒ false（那是另一種失敗）。</summary>
    static bool RemoteHasNoBranch(string iCwd, RepoSyncTarget iTarget)
    {
        SCP_GitResult ls = Git(iCwd, NetTimeoutMs, "ls-remote", "--heads", iTarget.Repo, iTarget.Branch);
        return ls.Ok && ls.StdOut.Trim().Length == 0;
    }

    // ===========================================================
    // 區塊職責：拉 —— 工作區有沒提交的先收；抓遠端；落後 ⇒ 快轉；兩邊都有新的 ⇒ rebase；接不上 ⇒ 衝突（交給人）。
    // ===========================================================
    static void Pull(string iDir, RepoSyncTarget iTarget, RepoSyncReport ioR)
    {
        CommitLeftovers(iDir, ioR);
        SCP_GitResult f = Git(iDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec);
        if (!f.Ok && RemoteHasNoBranch(iDir, iTarget))
        {
            // 連得上、只是遠端還沒有這條 branch（全新的空 repo）⇒ 沒東西可拉，這台的就是第一版（推那一步建出來）
            ioR.Fetched = true;
            MarkFetched(iDir);
            return;
        }
        if (!f.Ok)
        {
            ioR.State = RepoSyncState.Pending;
            ioR.Lines.Add($"⚠ 抓不到遠端（{f.ReasonLine}）—— 先照本機的版本");
            return;
        }
        ioR.Fetched = true;
        MarkFetched(iDir);
        (int aAhead, int aBehind) = Diverge(iDir, iTarget);
        if (aBehind == 0) return;
        if (aAhead == 0)
        {
            SCP_GitResult m = Git(iDir, SCP_Git.DefaultTimeoutMs, "merge", "--ff-only", "-q", iTarget.RemoteRef);
            if (m.Ok) { ioR.Lines.Add($"· 從遠端拉了 {aBehind} 筆（快轉）"); ClearOpenConflict(iDir); }
            else ioR.Lines.Add($"⚠ 快轉失敗（{m.ReasonLine}）—— 照本機的版本");
            return;
        }
        if (GitId(iDir, SCP_Git.DefaultTimeoutMs, "rebase", "-q", iTarget.RemoteRef).Ok)
        {
            ioR.Lines.Add($"· 本機有 {aAhead} 筆沒推、遠端有 {aBehind} 筆新的 ⇒ rebase 接上（沒有衝突）");
            ClearOpenConflict(iDir);
            return;
        }
        Git(iDir, SCP_Git.DefaultTimeoutMs, "rebase", "--abort");
        ReportConflict(iDir, iTarget, ioR);
    }

    // ===========================================================
    // 區塊職責：推 —— 被拒 ⇒ 抓＋rebase 再推；rebase 接不上 ⇒ 衝突（本機提交留著）；斷網／沒權限 ⇒ Pending。
    // ===========================================================
    static void Push(string iDir, RepoSyncTarget iTarget, RepoSyncReport ioR)
    {
        for (int aTry = 0; aTry < MaxPushTries; ++aTry)
        {
            SCP_GitResult p = Git(iDir, NetTimeoutMs, "push", "origin", "HEAD:refs/heads/" + iTarget.Branch);
            if (p.Ok)
            {
                ioR.State = RepoSyncState.Pushed;
                ioR.Lines.Add($"✓ 已推上 `{iTarget.Repo}`（{iTarget.Branch}）：{p.ReasonLine}");
                ClearOpenConflict(iDir);
                return;
            }
            if (!IsRejected(p))
            {
                ioR.State = RepoSyncState.Pending;
                ioR.Lines.Add($"⚠ 推不上去（{p.ReasonLine}）—— **已在本機提交**，下次用到時再推");
                return;
            }
            SCP_GitResult f = Git(iDir, NetTimeoutMs, "fetch", "origin", iTarget.RefSpec);
            if (!f.Ok)
            {
                ioR.State = RepoSyncState.Pending;
                ioR.Lines.Add($"⚠ 推送被拒、又抓不到遠端（{f.ReasonLine}）—— 已在本機提交，下次用到時再推");
                return;
            }
            MarkFetched(iDir);
            if (GitId(iDir, SCP_Git.DefaultTimeoutMs, "rebase", "-q", iTarget.RemoteRef).Ok)
            {
                ioR.Lines.Add("· 遠端有新的提交 ⇒ rebase 接上（沒有衝突），再推一次");
                continue;
            }
            Git(iDir, SCP_Git.DefaultTimeoutMs, "rebase", "--abort");
            ReportConflict(iDir, iTarget, ioR);
            return;
        }
        ioR.State = RepoSyncState.Pending;
        ioR.Lines.Add($"⚠ 連續 {MaxPushTries} 次推不上去（遠端一直在動）—— 已在本機提交，下次用到時再推");
    }

    /// <summary>
    /// 自動解不了 ⇒ 交給人：備份 ref、衝突報告（兩邊各改了哪些檔、還沒推上去的提交、手動解的步驟）、通知。
    /// <para>⚠ 本機的提交**留在原地**（rebase 已退回）：照常寫筆記沒關係，新的也會留在本機，只是推不上去。</para>
    /// <para>同一個衝突（本機 HEAD ＋ 遠端那一版都沒變）只寫一次報告、只通知一次 —— 每跑一支 library 就發一則是噪音。</para>
    /// </summary>
    static void ReportConflict(string iDir, RepoSyncTarget iTarget, RepoSyncReport ioR)
    {
        string aHead = RevParse(iDir, "HEAD");
        string aRemote = RevParse(iDir, iTarget.RemoteRef);
        string aKey = aHead + ".." + aRemote;
        ioR.State = RepoSyncState.Conflict;

        string aMarker = Path.Combine(GitDir(iDir), StateDirName, "open_conflict.txt");
        string[] aOld = File.Exists(aMarker) ? File.ReadAllLines(aMarker) : Array.Empty<string>();
        if (aOld.Length >= 2 && aOld[0] == aKey && File.Exists(aOld[1]))
        {
            ioR.ConflictFile = aOld[1];
            ioR.Lines.Add($"✗ BookNotes 還卡著同一個衝突（本機的提交還在、推不上去）—— 報告：`{aOld[1]}`");
            return;
        }

        string aBase = Git(iDir, SCP_Git.DefaultTimeoutMs, "merge-base", "HEAD", iTarget.RemoteRef).StdOut.Trim();
        var aMine = Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--name-only", aBase, "HEAD").OutLines();
        var aTheirs = new HashSet<string>(Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--name-only", aBase, iTarget.RemoteRef).OutLines(),
                                          StringComparer.OrdinalIgnoreCase);
        var aBoth = aMine.Where(aTheirs.Contains).ToList();
        var aCommits = Git(iDir, SCP_Git.DefaultTimeoutMs, "log", "--reverse", "--format=%h %s", iTarget.RemoteRef + "..HEAD").OutLines();
        string aBackup = BackupRef(iDir, aHead, BackupKind);
        string aStamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string aFile = Path.Combine(GitDir(iDir), StateDirName, $"conflict_{aStamp}.md").Replace('\\', '/');

        var sb = new StringBuilder();
        sb.AppendLine($"# BookNotes 同步衝突 {aStamp}");
        sb.AppendLine();
        sb.AppendLine($"- 遠端：`{iTarget.Repo}`（{iTarget.Branch}），本機：`{iDir}`");
        sb.AppendLine($"- 兩邊都改了、rebase 接不上的檔：{(aBoth.Count > 0 ? string.Join("、", aBoth.Select(x => "`" + x + "`")) : "（git 沒指出是哪幾個 —— 照下面的步驟 rebase 一次就會看到）")}");
        sb.AppendLine($"- 我的版本（衝突時的本機 HEAD）：`{aBackup}`；遠端那一版：`{Short(aRemote)}`");
        sb.AppendLine("- 現況：rebase 已退回，**本機的提交還在、推不上去**；照常寫筆記沒關係（新的也會留在本機），解完一起推。");
        sb.AppendLine();
        sb.AppendLine("## 還沒推上去的提交");
        foreach (string c in aCommits) sb.AppendLine($"- {c}");
        sb.AppendLine();
        sb.AppendLine("## 手動解");
        sb.AppendLine("```bash");
        sb.AppendLine($"git -C \"{iDir}\" rebase {iTarget.RemoteRef}            # 停在衝突的那一筆");
        sb.AppendLine($"git -C \"{iDir}\" status                                  # 看哪幾個檔衝突");
        sb.AppendLine($"git -C \"{iDir}\" show {iTarget.RemoteRef}:<檔>          # 遠端那一份；本機那一份：git show {aBackup}:<檔>");
        sb.AppendLine("# 改好衝突的檔（兩邊的內容都留、或挑一邊）之後：");
        sb.AppendLine($"git -C \"{iDir}\" add <檔> && git -C \"{iDir}\" rebase --continue");
        sb.AppendLine("senate cmd booknotes-sync --arg op=sync                  # 推上去");
        sb.AppendLine("```");
        sb.AppendLine($"- 不要了（以遠端為準、丟掉本機這幾筆）：`git -C \"{iDir}\" reset --hard {iTarget.RemoteRef}`（本機的版本仍在 `{aBackup}`）");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(aFile)!);
            File.WriteAllText(aFile, sb.ToString(), new UTF8Encoding(false));
            File.WriteAllLines(aMarker, new[] { aKey, aFile }, new UTF8Encoding(false));
        }
        catch (Exception e) { ioR.Lines.Add($"⚠ 衝突報告沒寫成（{e.Message}）—— 備份 ref 還在：`{aBackup}`"); }

        ioR.ConflictFile = aFile;
        ioR.Lines.Add($"✗ **BookNotes 同步衝突，要手動處理**：兩邊都改了 {(aBoth.Count > 0 ? string.Join("、", aBoth) : "同一個地方")}，rebase 接不上");
        ioR.Lines.Add($"  本機的 {aCommits.Count} 筆提交還在（推不上去）；我的版本也留在 `{aBackup}`；手動解的步驟：`{aFile}`");
        ioR.Notices.Add($"⚠ **BookNotes 同步衝突，需要手動處理**：這台跟遠端都改了 {(aBoth.Count > 0 ? string.Join("、", aBoth.Select(x => "`" + x + "`")) : "同一個地方")}，"
                      + $"自動 rebase 接不上。這台的 {aCommits.Count} 筆筆記還在本機、**還沒推上去**（不會丟）；步驟在 `{aFile}`");
    }

    static string? OpenConflictFile(string iDir)
    {
        string aMarker = Path.Combine(GitDir(iDir), StateDirName, "open_conflict.txt");
        if (!File.Exists(aMarker)) return null;
        string[] l = File.ReadAllLines(aMarker);
        return l.Length >= 2 ? l[1] : null;
    }

    static void ClearOpenConflict(string iDir)
    {
        string aMarker = Path.Combine(GitDir(iDir), StateDirName, "open_conflict.txt");
        try { if (File.Exists(aMarker)) File.Delete(aMarker); } catch { /* 留著只會讓下一次少發一則通知 */ }
    }

    /// <summary>
    /// 把 <see cref="RepoSyncReport.Notices"/> 發到酒館（署名 <paramref name="iPersona"/>）；過程與結果一行一行接到 <paramref name="oLines"/>。
    /// <para>⛔ 發不出去要說「沒發」，不可以印成發過了 —— 衝突沒人知道，本機的筆記就一直推不上去。</para>
    /// </summary>
    public static void Notify(string iDataRoot, string iPersona, RepoSyncReport iR, List<string> oLines)
    {
        if (iR.Notices.Count == 0) return;
        SCP_ITavernPostGateway? aGate = iPersona.Length > 0 ? SCP_TavernPostGatewayHost.Create(iDataRoot) : null;
        if (aGate == null)
        {
            oLines.Add(iPersona.Length == 0
                ? "⚠ 沒有 persona（不知道署誰的名）⇒ **酒館通知沒發** —— 內容如上，要自己去講一聲"
                : "⚠ 本宿主沒有登記發文閘 ⇒ **酒館通知沒發** —— 內容如上，要自己去講一聲");
            return;
        }
        var aMeta = new Dictionary<string, string>(StringComparer.Ordinal) { ["tag"] = "booknotes", ["kind"] = "sync" };
        foreach (string aBody in iR.Notices)
        {
            var aLines = new List<string>();
            SCP_TavernPostVerdict v = aGate.Post(iPersona, aBody, aMeta, aLines);
            if (v.Outcome == SCP_TavernPostOutcome.Posted) oLines.Add($"📣 酒館通知已發 seq {v.Seq}");
            else if (v.Outcome == SCP_TavernPostOutcome.Queued) oLines.Add($"📥 酒館通知已排隊（{v.Detail}）—— ⛔ 不要補發");
            else
            {
                oLines.Add(v.Outcome == SCP_TavernPostOutcome.NotPosted
                    ? $"⚠ 酒館通知**確定沒發**（{v.Detail}）—— 內容如上，要自己去講一聲"
                    : $"⚠ 酒館通知**結果不明**（{v.Detail}）—— ⛔ 別補發，先回讀：{v.RecheckHint}");
                foreach (string l in aLines) oLines.Add("  │ " + l);
            }
        }
    }

    // ── commit ─────────────────────────────────────────────────

    /// <summary>把這一次的改動收成一個 commit（沒有改動 ⇒ null）。訊息第一行：`booknotes: &lt;subject&gt; (auto)`。</summary>
    static string? Commit(string iDir, string iSubject, string iPersona, RepoSyncReport ioR)
    {
        SCP_GitResult a = Git(iDir, SCP_Git.DefaultTimeoutMs, "add", "-A", "--", ".");
        if (!a.Ok) { ioR.Lines.Add($"⚠ git add 失敗：{a.ReasonLine}"); return null; }
        if (Git(iDir, SCP_Git.DefaultTimeoutMs, "diff", "--cached", "--quiet").Exit == 0) return null;
        string aSubject = iSubject.Length > 100 ? iSubject.Substring(0, 100) + "…" : iSubject;
        string aMsg = $"booknotes: {aSubject} (auto)\n" + (iPersona.Length > 0 ? $"\npersona: {iPersona}\n" : "");
        SCP_GitResult c = GitId(iDir, SCP_Git.DefaultTimeoutMs, "commit", "-q", "-m", aMsg);
        if (!c.Ok) { ioR.Lines.Add($"⚠ git commit 失敗：{c.ReasonLine}"); return null; }
        string aSha = RevParse(iDir, "HEAD");
        ioR.Lines.Add($"· 已提交 `{Short(aSha)}` booknotes: {aSubject}");
        return aSha;
    }

    /// <summary>工作區有沒提交的改動（上一支寫到一半、有人手改）⇒ 先收成一筆，免得被下一筆一起帶走而說不清是哪一次的。</summary>
    static void CommitLeftovers(string iDir, RepoSyncReport ioR)
    {
        if (Git(iDir, SCP_Git.DefaultTimeoutMs, "status", "--porcelain").OutLines().Count == 0) return;
        Git(iDir, SCP_Git.DefaultTimeoutMs, "add", "-A", "--", ".");
        SCP_GitResult c = GitId(iDir, SCP_Git.DefaultTimeoutMs, "commit", "-q", "-m",
            "chore(booknotes): 收編沒提交的改動 (auto)\n\n用之前發現工作區不乾淨（上一次沒同步到、或有人手改）。\n");
        if (c.Ok) ioR.Lines.Add("· BookNotes 有沒提交的改動 ⇒ 先收成一筆");
    }

    // ── 節流 ───────────────────────────────────────────────────

    static string FetchStampPath(string iDir) => Path.Combine(GitDir(iDir), StateDirName, "last_fetch");

    static bool FetchDue(string iDir, int iEverySec)
    {
        if (iEverySec <= 0) return true;
        string p = FetchStampPath(iDir);
        return !File.Exists(p) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(p)).TotalSeconds >= iEverySec;
    }

    static void MarkFetched(string iDir)
    {
        try
        {
            string p = FetchStampPath(iDir);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        }
        catch { /* 寫不進去只是下一次會多抓一次 */ }
    }

    /// <summary>刪一棵樹 —— git 的物件檔是唯讀的，`Directory.Delete` 直接刪會被擋（Windows）。</summary>
    static void DeleteTreeForce(string iDir)
    {
        try
        {
            if (!Directory.Exists(iDir)) return;
            foreach (string f in Directory.GetFiles(iDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(iDir, true);
        }
        catch { }
    }

    /// <summary>
    /// 跨行程的鎖（同一顆 BookNotes 一次只准一個 senate 動 git）—— 具名 mutex，名字由目錄的絕對路徑算出來。
    /// <para>等不到（別的行程同步太久）⇒ 這次不同步、回報 Pending（⛔ 不擋閱讀；下次用到時再同步）。</para>
    /// </summary>
    sealed class MutexLock : IDisposable
    {
        readonly Mutex m_Mutex;
        MutexLock(Mutex iMutex) { m_Mutex = iMutex; }

        public static MutexLock? Acquire(string iDir, RepoSyncReport ioR)
        {
            string aKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(iDir).ToLowerInvariant())))[..16];
            var m = new Mutex(false, @"Local\Senate.BookNotesSync." + aKey);
            try
            {
                if (m.WaitOne(TimeSpan.FromSeconds(LockWaitSec))) return new MutexLock(m);
            }
            catch (AbandonedMutexException) { return new MutexLock(m); }   // 上一個拿著的行程死了 ⇒ 鎖已經是我的
            m.Dispose();
            ioR.State = RepoSyncState.Pending;
            ioR.Lines.Add($"⚠ 另一個 senate 正在同步 BookNotes（等了 {LockWaitSec} 秒還沒好）⇒ 這次不同步，下次用到時再同步");
            return null;
        }

        public void Dispose()
        {
            try { m_Mutex.ReleaseMutex(); } catch (ApplicationException) { }
            m_Mutex.Dispose();
        }
    }
}
