// 區塊職責：`senate cmd library|book` 前後的閱讀筆記同步（sync.bookNotes）—— 跑之前拉、跑完 commit＋push。
// 物理意義：這兩支本來就是**本地跑**的 Cmd ⇒ 同步也在 CLI 這一邊做（⛔ 不交給 Server：經 WMI 起的 Server
//           跑 git 同步會被 Defender 砍，見 SenateBookNotesSync 檔頭）。沒設遠端 ⇒ 整段不存在（一行都不印）。
// 數值影響：跑之前每分鐘最多抓一次遠端；跑完有改動才 commit＋push（讀的 op 只多一次 `git status`）。
//           過程印在 stderr（給人看），狀態印成 `🔢 booknotes_sync`（給程式讀）；衝突通知署 `persona` 的名發酒館。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

static class BookNotesSyncHook
{
    static readonly HashSet<string> s_Cmds = new(StringComparer.OrdinalIgnoreCase) { "library", "book" };

    /// <summary>commit 訊息第一行用得到的參數（照這個順序，有給才放）。</summary>
    static readonly string[] s_SubjectKeys = { "media_id", "work_id", "chapter_id", "character_id", "book", "slug", "persona" };

    public sealed record Target(string DataRoot, RepoSyncTarget Repo);

    /// <summary>這一支要不要同步（是 library／book、而且 sync.bookNotes 兩格都填了、資料根解得出來）。</summary>
    public static Target? Resolve(string iRepoRoot, string iCmdName)
    {
        if (!s_Cmds.Contains(iCmdName)) return null;
        SenateConfig? aCfg;
        try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot)); }
        catch (Exception) { return null; }   // 設定檔壞掉由 CmdScp 自己那一格擋、自己說
        RepoSyncTarget? aRepo = RepoSyncTarget.From(aCfg?.Sync.BookNotes);
        if (aCfg == null || aRepo == null) return null;
        string? aRoot = SenatePathBinding.ResolveDataRoot(aCfg, out string? aErr);
        if (aRoot == null)
        {
            Console.Error.WriteLine($"⚠ 📚 BookNotes 同步：資料根解不出來（{aErr}）⇒ 這次不同步");
            return null;
        }
        return new Target(aRoot.Replace('\\', '/').TrimEnd('/'), aRepo);
    }

    public static void Before(Target iT, IReadOnlyDictionary<string, string> iArgs)
    {
        RepoSyncReport r;
        try { r = SenateBookNotesSync.BeforeUse(iT.DataRoot, iT.Repo); }
        catch (Exception e) { Console.Error.WriteLine($"⚠ 📚 BookNotes 同步（跑之前）炸了：{e.GetType().Name}: {e.Message} —— 照本機的版本繼續"); return; }
        Print(iT, r, iArgs);
    }

    /// <summary>跑完：有改動就收成一筆推上去。回傳給 `🔢 booknotes_sync` 用的狀態字。</summary>
    public static string After(Target iT, string iCmdName, IReadOnlyDictionary<string, string> iArgs)
    {
        RepoSyncReport r;
        try { r = SenateBookNotesSync.AfterWrite(iT.DataRoot, iT.Repo, Subject(iCmdName, iArgs), Arg(iArgs, "persona")); }
        catch (Exception e) { Console.Error.WriteLine($"⚠ 📚 BookNotes 同步（跑完）炸了：{e.GetType().Name}: {e.Message} —— 改動留在本機，下次用到時再收"); return "failed"; }
        Print(iT, r, iArgs);
        return r.StateWord;
    }

    static void Print(Target iT, RepoSyncReport iR, IReadOnlyDictionary<string, string> iArgs)
    {
        var aLines = new List<string>(iR.Lines);
        SenateBookNotesSync.Notify(iT.DataRoot, Arg(iArgs, "persona"), iR, aLines);
        foreach (string l in aLines) Console.Error.WriteLine("📚 BookNotes 同步：" + l);
    }

    /// <summary>`library(note_chapter) comic-sousou-no-frieren 0003 Abigail` 這種形狀 —— 看 log 就知道是哪一次寫的。</summary>
    static string Subject(string iCmdName, IReadOnlyDictionary<string, string> iArgs)
    {
        string aOp = Arg(iArgs, "op");
        var aParts = new List<string> { iCmdName.ToLowerInvariant() + (aOp.Length > 0 ? $"({aOp})" : "") };
        foreach (string k in s_SubjectKeys)
        {
            string v = Arg(iArgs, k);
            if (v.Length > 0 && v.Length <= 80 && !v.Contains('\n')) aParts.Add(v);
        }
        return string.Join(" ", aParts);
    }

    static string Arg(IReadOnlyDictionary<string, string> iArgs, string iKey)
        => iArgs.TryGetValue(iKey, out string? v) ? v.Trim() : "";
}
