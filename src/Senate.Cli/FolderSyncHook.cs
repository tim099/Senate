// 區塊職責：`senate cmd <X>` 前後的資料夾同步（sync.bookNotes、sync.workMemory）—— X 在某個 FolderSyncSpec.HookCmds 裡 ⇒ 跑之前拉、跑完 commit＋push。
// 物理意義：那幾支（library、book、work-memory、task 的入口）本來就是**本地跑**的 ⇒ 同步也在 CLI 這一邊做
//           （⛔ 不交給 Server：經 WMI 起的 Server 跑 git 同步會被 Defender 砍，見 SenateFolderSync 檔頭）。沒設遠端 ⇒ 整段不存在（一行都不印）。
// 數值影響：跑之前每分鐘最多抓一次遠端；跑完有改動才 commit＋push（讀的 op 只多一次 `git status`）。
//           過程印在 stderr（給人看），狀態印成 `🔢 <slug>_sync`（給程式讀）；衝突通知署 `persona` 的名發酒館。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

static class FolderSyncHook
{
    /// <summary>commit 訊息第一行用得到的參數（照這個順序，有給才放）。</summary>
    static readonly string[] s_SubjectKeys = { "media_id", "work_id", "chapter_id", "character_id", "book", "slug", "topic", "index", "persona" };

    public sealed record Target(SenateFolderSync Sync, string DataRoot, RepoSyncTarget Repo);

    /// <summary>這一支要同步哪幾個資料夾（在 HookCmds 裡、`sync.&lt;名&gt;` 兩格都填了、資料根解得出來）。</summary>
    public static List<Target> Resolve(string iRepoRoot, string iCmdName)
    {
        var aOut = new List<Target>();
        var aSpecs = FolderSyncSpec.All.Where(s => s.Hooks(iCmdName)).ToList();
        if (aSpecs.Count == 0) return aOut;
        SenateConfig? aCfg;
        try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot)); }
        catch (Exception) { return aOut; }   // 設定檔壞掉由 CmdScp 自己那一格擋、自己說
        if (aCfg == null) return aOut;
        string? aRoot = null, aErr = null;
        foreach (FolderSyncSpec aSpec in aSpecs)
        {
            RepoSyncTarget? aRepo = aSpec.TargetOf(aCfg);
            if (aRepo == null) continue;
            aRoot ??= SenatePathBinding.ResolveDataRoot(aCfg, out aErr);
            if (aRoot == null)
            {
                Console.Error.WriteLine($"⚠ 🔄 {aSpec.DirName} 同步：資料根解不出來（{aErr}）⇒ 這次不同步");
                continue;
            }
            aOut.Add(new Target(new SenateFolderSync(aSpec), aRoot.Replace('\\', '/').TrimEnd('/'), aRepo));
        }
        return aOut;
    }

    public static void Before(List<Target> iTargets, IReadOnlyDictionary<string, string> iArgs)
    {
        foreach (Target t in iTargets)
        {
            RepoSyncReport r;
            try { r = t.Sync.BeforeUse(t.DataRoot, t.Repo); }
            catch (Exception e) { Console.Error.WriteLine($"⚠ 🔄 {t.Sync.Spec.DirName} 同步（跑之前）炸了：{e.GetType().Name}: {e.Message} —— 照本機的版本繼續"); continue; }
            Print(t, r, iArgs);
        }
    }

    /// <summary>跑完：有改動就收成一筆推上去，每個資料夾印一行 `🔢 &lt;slug&gt;_sync`。</summary>
    public static void After(List<Target> iTargets, string iCmdName, IReadOnlyDictionary<string, string> iArgs)
    {
        foreach (Target t in iTargets)
        {
            string aWord;
            try
            {
                RepoSyncReport r = t.Sync.AfterWrite(t.DataRoot, t.Repo, Subject(iCmdName, iArgs), Arg(iArgs, "persona"));
                Print(t, r, iArgs);
                aWord = r.StateWord;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"⚠ 🔄 {t.Sync.Spec.DirName} 同步（跑完）炸了：{e.GetType().Name}: {e.Message} —— 改動留在本機，下次用到時再收");
                aWord = "failed";
            }
            Console.WriteLine($"🔢 {t.Sync.Spec.Slug}_sync = {aWord}");
        }
    }

    static void Print(Target iT, RepoSyncReport iR, IReadOnlyDictionary<string, string> iArgs)
    {
        var aLines = new List<string>(iR.Lines);
        iT.Sync.Notify(iT.DataRoot, Arg(iArgs, "persona"), iR, aLines);
        foreach (string l in aLines) Console.Error.WriteLine($"🔄 {iT.Sync.Spec.DirName} 同步：" + l);
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
