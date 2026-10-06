// 區塊職責：指令外層／內層與分類（TASK-0427）的自我對拍。
// 物理意義：help 預設只列外層、依分類分組 —— 會安靜壞掉的地方有三種：
//           ① 一支指令沒分類（或內層的父指令不存在）⇒ 從 help 上消失，跟「沒有這支」同形；
//           ② 一支被標成內層、卻沒有任何入口的回傳指到它 ⇒ 藏了就再也找不到；
//           ③ 內層清單跟拍板的不一樣（多標了就藏錯、少標了就沒藏）。
// 數值影響：①③ 讀註冊表；② 掃 SCP_Core/Runtime 與 src 的原始碼（找不到原始碼 ⇒ 那一格跳過，⛔ 不判綠）。
#nullable enable
using System.Text.RegularExpressions;
using SCP.Core.Cmd;

namespace Senate.Cli;

public static partial class SelfTest
{
    /// <summary>拍板的內層（TASK-0427，Tim 2026-10-06）：內層名 → 父指令名。</summary>
    static readonly Dictionary<string, string> s_ExpectedInner = new(StringComparer.Ordinal)
    {
        ["morning-brief"] = "morning-wake", ["morning-intro"] = "morning-wake", ["morning-catchup"] = "morning-wake",
        ["goodnight-portrait"] = "goodnight-check", ["goodnight-letter"] = "goodnight-check", ["goodnight-sleep"] = "goodnight-check",
        ["free-time-activity"] = "free-time",
        ["portrait-next"] = "consolidate", ["portrait-fold"] = "consolidate",
        ["tavern-inbox-ack"] = "tavern-catchup",
    };

    static CheckRow CmdTierRegistry()
    {
        const string aName = "指令層級與分類（註冊表）：外層都有已知分類／內層父指令存在且不繞圈／內層清單＝拍板的 10 支／help 只列外層、各組加總＝總數／查分類、打錯分類、help 內層（TASK-0427）";
        try
        {
            var aFails = new List<string>();
            SCP_CmdRegistry.Discover();
            IReadOnlyList<SCP_Cmd> aAll = SCP_CmdRegistry.All();

            // ① 每一支都解析得到已知分類（內層沿父鏈）
            foreach (SCP_Cmd c in aAll)
            {
                if (!c.IsInner && c.Category.Length == 0) aFails.Add($"🔴 外層 `{c.Name}` 沒填分類");
                else if (!SCP_CmdCategory.IsKnown(SCP_CmdCategory.Of(c))) aFails.Add($"🔴 `{c.Name}` 的分類解不出來（{(c.IsInner ? "父指令 " + c.Parent : c.Category)}）");
                if (c.IsInner && c.Category.Length > 0) aFails.Add($"內層 `{c.Name}` 自己填了分類（該跟父指令走）");
            }

            // ③ 內層清單＝拍板
            var aInner = aAll.Where(c => c.IsInner).ToDictionary(c => c.Name, c => SCP_CmdCategory.RootOf(c));
            foreach (var kv in s_ExpectedInner)
                if (!aInner.TryGetValue(kv.Key, out string? aRoot)) aFails.Add($"🔴 `{kv.Key}` 該是內層（屬於 {kv.Value}）卻沒標");
                else if (aRoot != kv.Value) aFails.Add($"`{kv.Key}` 的流程起點是 `{aRoot}`（期望 `{kv.Value}`）");
            foreach (string n in aInner.Keys) if (!s_ExpectedInner.ContainsKey(n)) aFails.Add($"🔴 `{n}` 被標成內層，但不在拍板清單上");

            // help 預設：只列外層、各組加總＝外層數、外層＋內層＝總數
            var aHelp = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal));
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(x => x.Key == k).Value ?? "";
            int aEntry = int.Parse(V(aHelp, "entry_count")), aInnerN = int.Parse(V(aHelp, "inner_count"));
            if (aEntry + aInnerN != aAll.Count || V(aHelp, "listed_count") != aEntry.ToString()) aFails.Add($"help 計數對不上：外層 {aEntry}＋內層 {aInnerN}≠{aAll.Count} 或列出 {V(aHelp, "listed_count")}");
            string aText = string.Join("\n", aHelp.Lines);
            if (aText.Contains("未分類")) aFails.Add("🔴 help 有「未分類」組");
            // 內層在清單上有兩種長相：當外層列（`  名 `）或縮排在父指令底下（`    ↳ 名`）—— 兩種都不准出現在預設 help
            foreach (string n in s_ExpectedInner.Keys)
                if (aHelp.Lines.Any(l => l.StartsWith("  " + n + " ") || l.StartsWith("    ↳ " + n))) aFails.Add($"🔴 內層 `{n}` 出現在預設 help 上");
            foreach (SCP_Cmd c in aAll.Where(c => !c.IsInner))
                if (!aHelp.Lines.Any(l => l.StartsWith("  " + c.Name + " "))) aFails.Add($"🔴 外層 `{c.Name}` 沒出現在 help 上");

            // 查分類：內層縮排在父指令底下；`help 酒館` 同義；打錯分類 ⇒ exit 2 並列出分類
            var aCat = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal) { ["category"] = SCP_CmdCategory.Tavern });
            if (!aCat.Ok || !aCat.Lines.Any(l => l.StartsWith("  tavern-catchup ")) || !aCat.Lines.Any(l => l.StartsWith("    ↳ tavern-inbox-ack")))
                aFails.Add("查分類「酒館」沒有把 tavern-inbox-ack 縮排在 tavern-catchup 底下");
            var aByName = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = SCP_CmdCategory.Tavern });
            if (!aByName.Ok || string.Join("\n", aByName.Lines) != string.Join("\n", aCat.Lines)) aFails.Add("`help 酒館` 與 `help --arg category=酒館` 不同");
            var aBad = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal) { ["category"] = "不存在的分類" });
            if (aBad.ExitCode != 2 || !aBad.Lines.Any(l => l.Contains(SCP_CmdCategory.Bank))) aFails.Add("🔴 打錯分類沒有 exit 2＋列出現有分類");

            // help <內層>：照常印、說出從哪裡開始；help <父>：列出子流程
            var aDetail = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "morning-catchup" });
            if (!aDetail.Ok || !aDetail.Lines.Any(l => l.Contains("屬於 `morning-wake`")) || !aDetail.Lines.Any(l => l.StartsWith("參數")))
                aFails.Add("help 內層沒照常印參數或沒說父指令");
            var aParent = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = "morning-wake" });
            if (!aParent.Lines.Any(l => l.Contains("morning-brief、morning-catchup、morning-intro"))) aFails.Add("help 父指令沒列出子流程");

            // all=1 ⇒ 內層也列
            var aAllHelp = SCP_CmdRegistry.Dispatch("help", new Dictionary<string, string>(StringComparer.Ordinal) { ["all"] = "1" });
            if (!aAllHelp.Lines.Any(l => l.StartsWith("    ↳ morning-catchup"))) aFails.Add("all=1 沒列出內層");

            return new CheckRow(aName,
                aFails.Count == 0 ? $"外層 {aEntry}／內層 {aInnerN}＝{aAll.Count}；全有已知分類；內層＝拍板 10 支；預設 help 不列內層、外層全在；查分類／help 分類名／打錯分類 exit 2／help 內層與父指令都對" : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow CmdTierPointedTo()
    {
        const string aName = "內層一定有人指路（原始碼掃描）：每支內層至少被一處別的程式碼以可執行的形式引用（InvokeOf<類別>(／Invoke(\"名／Cmd(\"名／`cmd 名`）（TASK-0427）";
        try
        {
            string aRoot = SenateRepoRoot();
            string[] aDirs = { Path.Combine(aRoot, "SCP_Core", "Runtime"), Path.Combine(aRoot, "src") };
            if (!aDirs.All(Directory.Exists)) return new CheckRow(aName, "找不到原始碼（" + string.Join("、", aDirs) + "）⇒ 量不到，⛔ 不判綠", CheckResult.Skipped);
            var aFiles = aDirs.SelectMany(d => Directory.GetFiles(d, "*.cs", SearchOption.AllDirectories))
                              .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                                       && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                                       && !Path.GetFileName(f).StartsWith("SelfTest", StringComparison.Ordinal))
                              .Select(f => (Path: f, Lines: File.ReadAllLines(f))).ToList();
            var aNameDecl = new Regex("override string Name\\s*=>\\s*\"([a-z0-9-]+)\"");

            var aFails = new List<string>();
            var aCounts = new List<string>();
            SCP_CmdRegistry.Discover();
            foreach (SCP_Cmd c in SCP_CmdRegistry.All().Where(c => c.IsInner))
            {
                string aClass = c.GetType().Name;
                var aRef = new Regex("InvokeOf<([\\w.]+\\.)?" + aClass + ">\\(|(cmd |Invoke\\(\"|Cmd\\(\")" + Regex.Escape(c.Name) + "(?![\\w-])");
                int aHits = 0;
                foreach (var (aPath, aLines) in aFiles)
                {
                    // 子指令自己的檔：只有它一支 ⇒ 整檔不算（自己指自己不是指路）；一檔多支 ⇒ 只排除它的 Name／Example 行
                    var aDeclared = aLines.Select(l => aNameDecl.Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value).ToList();
                    if (aDeclared.Count == 1 && aDeclared[0] == c.Name) continue;
                    foreach (string l in aLines)
                        if (aRef.IsMatch(l) && !l.Contains("Example =>") && !l.TrimStart().StartsWith("//")) aHits++;
                }
                aCounts.Add(c.Name + " " + aHits);
                if (aHits == 0) aFails.Add($"🔴 內層 `{c.Name}` 沒有任何程式碼指到它 ⇒ 藏了就找不到");
            }
            return new CheckRow(aName,
                aFails.Count == 0 ? "每支內層都有人指路：" + string.Join("、", aCounts) : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }
}
