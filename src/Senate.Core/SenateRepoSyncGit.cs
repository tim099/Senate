// 區塊職責：「資料樹裡的一個資料夾 ＝ 遠端某個 repo 某條 branch 的工作副本」的共用零件
//           —— 任務單（sync.tasks，SenateTaskRepoSync）與閱讀筆記（sync.bookNotes，SenateBookNotesSync）都用這裡。
// 物理意義：判「同一個 repo」、補 git 身分、分辨「推送被拒」與「斷網」、把巢狀 repo 排除出資料樹 ——
//           這幾格兩份同步各抄一份就是兩個會漂的版本（漂了的樣子：一邊認得 `.git` 後綴、另一邊不認得，
//           於是同一份設定在 A 那邊同步、在 B 那邊說「origin 不同」）。⇒ 只住這裡。
// ⚠ 這裡**不鎖**：誰呼叫誰負責把同一顆 repo 的 git 操作串起來（任務單在 Server 的 task lane 裡、閱讀筆記用跨行程的具名 mutex）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Git;

namespace Senate.Core;

/// <summary>同步到哪裡（<see cref="SyncTargetSettings"/> 兩格都填才有）。</summary>
public sealed record RepoSyncTarget(string Repo, string Branch)
{
    public static RepoSyncTarget? From(SyncTargetSettings? iSettings)
        => iSettings != null && iSettings.IsSet ? new RepoSyncTarget(iSettings.Repo.Trim(), iSettings.Branch.Trim()) : null;

    public string RemoteRef => "origin/" + Branch;
    public string RefSpec => $"+refs/heads/{Branch}:refs/remotes/origin/{Branch}";
}

internal static class RepoSyncGit
{
    public static SCP_GitResult Git(string iDir, int iTimeoutMs, params string[] iArgs) => SCP_Git.RunTimeout(iDir, iTimeoutMs, iArgs);

    /// <summary>
    /// 會產生 commit 的指令（commit、rebase）。這台機器沒設 git 身分 ⇒ 用 `Senate` 頂上 ——
    /// 🩸 新機器上第一次寫單就會撞「Please tell me who you are」，而那時單子已經寫在本機了。
    /// </summary>
    public static SCP_GitResult GitId(string iDir, int iTimeoutMs, params string[] iArgs)
    {
        bool aHasName = Git(iDir, SCP_Git.DefaultTimeoutMs, "config", "user.name").StdOut.Trim().Length > 0;
        bool aHasMail = Git(iDir, SCP_Git.DefaultTimeoutMs, "config", "user.email").StdOut.Trim().Length > 0;
        if (aHasName && aHasMail) return Git(iDir, iTimeoutMs, iArgs);
        var aArgs = new List<string>();
        if (!aHasName) { aArgs.Add("-c"); aArgs.Add("user.name=Senate"); }
        if (!aHasMail) { aArgs.Add("-c"); aArgs.Add("user.email=senate@localhost"); }
        aArgs.AddRange(iArgs);
        return Git(iDir, iTimeoutMs, aArgs.ToArray());
    }

    /// <summary>同一個 repo？（大小寫、結尾斜線、`.git` 後綴、反斜線都不算差別）</summary>
    public static bool SameRepo(string iA, string iB)
    {
        static string N(string s)
        {
            s = s.Trim().Replace('\\', '/').TrimEnd('/');
            if (s.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.ToLowerInvariant();
        }
        return N(iA) == N(iB);
    }

    /// <summary>這個目錄**本身**是一顆 repo 的根（⛔ 不用 `SCP_Git.IsRepo` —— 它住在資料樹裡，問它會回答「在 repo 裡」）。</summary>
    public static bool IsRepoRoot(string iDir)
        => Directory.Exists(Path.Combine(iDir, ".git")) || File.Exists(Path.Combine(iDir, ".git"));

    public static string GitDir(string iDir) => Git(iDir, SCP_Git.DefaultTimeoutMs, "rev-parse", "--absolute-git-dir").StdOut.Trim();
    public static string RevParse(string iDir, string iRev) => Git(iDir, SCP_Git.DefaultTimeoutMs, "rev-parse", iRev).StdOut.Trim();
    public static string Short(string iSha) => iSha.Length > 7 ? iSha.Substring(0, 7) : iSha;

    /// <summary>停在一次沒做完的 rebase？（那時任何自動動作都會把人手上的半套狀態弄得更亂 ⇒ 呼叫端一律停手）</summary>
    public static bool InRebase(string iDir)
    {
        string aGitDir = GitDir(iDir);
        return aGitDir.Length > 0
            && (Directory.Exists(Path.Combine(aGitDir, "rebase-merge")) || Directory.Exists(Path.Combine(aGitDir, "rebase-apply")));
    }

    public static (int Ahead, int Behind) AheadBehind(string iDir, string iRemoteRef)
    {
        SCP_GitResult r = Git(iDir, SCP_Git.DefaultTimeoutMs, "rev-list", "--left-right", "--count", "HEAD..." + iRemoteRef);
        string[] p = r.StdOut.Trim().Split('\t', ' ');
        return r.Ok && p.Length >= 2
               && int.TryParse(p[0], out int a) && int.TryParse(p[^1], out int b) ? (a, b) : (0, 0);
    }

    /// <summary>push 被拒是因為遠端比我新（要先拉）—— 跟斷網、沒權限分開：前者 rebase，後者等之後再推。</summary>
    public static bool IsRejected(SCP_GitResult iPush)
    {
        string m = iPush.Message;
        return m.Contains("[rejected]", StringComparison.Ordinal) || m.Contains("non-fast-forward", StringComparison.Ordinal)
            || m.Contains("fetch first", StringComparison.Ordinal);
    }

    /// <summary>把 <paramref name="iHead"/> 存成 `refs/senate/&lt;iKind&gt;/backup-&lt;時間&gt;`（自動動作要丟掉本機版本之前一律先存）。</summary>
    public static string BackupRef(string iDir, string iHead, string iKind)
    {
        string aRef = $"refs/senate/{iKind}/backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        Git(iDir, SCP_Git.DefaultTimeoutMs, "update-ref", aRef, iHead);
        return aRef;
    }

    /// <summary>
    /// 資料樹本身是 git repo、而它把 <paramref name="iDir"/> 列成未追蹤 ⇒ 加進它的 info/exclude（本機、不入版控），
    /// 免得巢狀 repo 被當成未追蹤的東西收進資料樹。每次同步都問一次（已經不列了 ⇒ 什麼都不做、回 null）。
    /// <para>🩸 2026-10-22：第一版用 `git check-ignore` 判「已經忽略了」—— 目標是巢狀 repo 時它會讀到
    /// 那個 repo **自己的** .gitignore，回一個錯的「有」，於是 exclude 沒加，Valhalla 照樣列 `?? Tasks/`。
    /// ⇒ 判準改成症狀本身：資料樹的 status 有沒有把它列成未追蹤；加完再問一次（回讀）。</para>
    /// </summary>
    /// <returns>要印給人看的一行（加了、或加了還列著）；什麼都沒做 ⇒ null。</returns>
    public static string? ExcludeFromParent(string iDataRoot, string iDir, string iWhat)
    {
        SCP_GitResult aTop = Git(iDataRoot, SCP_Git.DefaultTimeoutMs, "rev-parse", "--show-toplevel");
        if (!aTop.Ok) return null;
        string aRel = Path.GetRelativePath(aTop.StdOut.Trim(), iDir).Replace('\\', '/');      // exclude 那一行：相對 repo 根
        string aSpec = Path.GetRelativePath(iDataRoot, iDir).Replace('\\', '/') + "/";       // pathspec：相對工作目錄
        if (aRel.StartsWith("..", StringComparison.Ordinal)) return null;
        bool Untracked() => Git(iDataRoot, SCP_Git.DefaultTimeoutMs, "status", "--porcelain", "--untracked-files=normal", "--", aSpec)
                            .OutLines().Any(l => l.StartsWith("?? ", StringComparison.Ordinal));
        if (!Untracked()) return null;
        string aExclude = Git(iDataRoot, SCP_Git.DefaultTimeoutMs, "rev-parse", "--git-path", "info/exclude").StdOut.Trim();
        if (aExclude.Length == 0) return null;
        if (!Path.IsPathRooted(aExclude)) aExclude = Path.Combine(iDataRoot, aExclude);
        Directory.CreateDirectory(Path.GetDirectoryName(aExclude)!);
        File.AppendAllText(aExclude, $"\n# Senate：{iWhat}，不收進資料樹\n/{aRel}/\n", new UTF8Encoding(false));
        return Untracked()
            ? $"⚠ 資料樹的 info/exclude 加了 `/{aRel}/`，可是它還是把 {aSpec} 列成未追蹤 —— 去看 `{aExclude}`"
            : $"· 資料樹的 info/exclude 加上 `/{aRel}/`（本機，不入版控；回讀：已不列成未追蹤）";
    }
}
