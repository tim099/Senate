// 區塊職責：selftest「預設跑哪些」的設定（TASK-0397）＋選取引擎。
// 物理意義：項目會愈來愈多，全跑要數分鐘，而其中不少是「某次搬遷／某一頁」的完成驗收 ⇒ 預設只跑**常駐**的那批。
//   三種狀態（`SenateData/config/selftest.json`，人編輯的）：
//     常駐 Standing ＝ 預設會跑（`enabled` 列了、或登記時標 important 的新項目）
//     關閉 Closed   ＝ 預設不跑（`disabled` 列了）；`--all` 或 `--only` 點名時照跑
//     新   New      ＝ config 沒列過、也沒標 important ⇒ **跑一次**；通過就自動進 `disabled`（測後關閉）
// ⚠ 「新」跑了沒過（Fail／Skipped／沒有任何一列）不關 ⇒ 下次還會跑，直到真的通過一次。
//   理由：跳過的項目沒有讀數，把它關掉等於把「沒測」寫成「測過」。
// ⚠ `disabled` 與 `enabled` 同時列了某一項 ⇒ **disabled 贏**（人明確關掉的優先）。
// ⛔ 壞檔不冒充「沒設定過」：config 讀不懂就丟例外、由呼叫端大聲說，不退回預設。
// ⛔ 本檔不刪任何一支測試 —— 「不預設跑」與「廢除」是兩件事（Tim 2026-10-05）。
using System.Text.Json;
using System.Text.Json.Nodes;
using Senate.Core;

namespace Senate.Cli;

public enum SelfTestStatus { Standing, Closed, New }

public sealed class SelfTestConfig
{
    public string Path { get; }
    public HashSet<string> Enabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Disabled { get; } = new(StringComparer.OrdinalIgnoreCase);
    JsonObject _root;

    SelfTestConfig(string iPath, JsonObject iRoot) { Path = iPath; _root = iRoot; }

    public static string PathFor(string iConfigDir) => System.IO.Path.Combine(iConfigDir, "selftest.json");

    /// <summary>
    /// 讀設定。檔不存在 ⇒ 用 <paramref name="iCoreKeys"/>（常駐名單）＋當下目錄**一次性播種**並寫檔：
    /// 常駐＝核心名單裡真的存在的項目；其餘當下已存在的項目全進 disabled（⇒ 之後新增的才會是「新」）。
    /// </summary>
    public static SelfTestConfig Load(string iPath, IReadOnlyList<string> iCatalogKeys, IReadOnlyCollection<string> iCoreKeys, out bool oSeeded)
    {
        oSeeded = false;
        if (File.Exists(iPath))
        {
            JsonObject aRoot;
            try { aRoot = JsonNode.Parse(File.ReadAllText(iPath)) as JsonObject ?? throw new JsonException("根不是物件"); }
            catch (JsonException e) { throw new InvalidOperationException($"selftest config 讀不懂（{iPath}）：{e.Message} —— ⛔ 不退回預設，請修檔或刪掉它讓它重新播種"); }
            var aCfg = new SelfTestConfig(iPath, aRoot);
            Fill(aCfg.Enabled, aRoot["enabled"]);
            Fill(aCfg.Disabled, aRoot["disabled"]);
            return aCfg;
        }

        var aCore = new HashSet<string>(iCoreKeys, StringComparer.OrdinalIgnoreCase);
        var aNew = new SelfTestConfig(iPath, new JsonObject
        {
            ["_說明"] = "selftest 預設跑哪些。enabled＝常駐；disabled＝預設不跑（--all／--only 照跑）；兩邊都沒列的新項目會跑一次，通過就自動進 disabled。"
                      + "同一項兩邊都列 ⇒ disabled 贏。要讓新測試常駐：登記時標 important，或把它的 key 放進 enabled。",
        });
        foreach (string k in iCatalogKeys)
            (aCore.Contains(k) ? aNew.Enabled : aNew.Disabled).Add(k);
        aNew.Save();
        oSeeded = true;
        return aNew;
    }

    static void Fill(HashSet<string> iSet, JsonNode? iNode)
    {
        if (iNode is not JsonArray aArr) return;
        foreach (JsonNode? n in aArr) if (n is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s)) iSet.Add(s.Trim());
    }

    public SelfTestStatus StatusOf(string iKey, bool iImportant)
    {
        if (Disabled.Contains(iKey)) return SelfTestStatus.Closed;
        if (Enabled.Contains(iKey)) return SelfTestStatus.Standing;
        return iImportant ? SelfTestStatus.Standing : SelfTestStatus.New;
    }

    /// <summary>寫回（先寫暫存檔再換名；其他鍵原樣保留）。</summary>
    public void Save()
    {
        _root["enabled"] = ToArray(Enabled);
        _root["disabled"] = ToArray(Disabled);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!);
        string aTmp = Path + ".tmp";
        File.WriteAllText(aTmp, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");
        File.Move(aTmp, Path, true);
    }

    static JsonArray ToArray(HashSet<string> iSet)
    {
        var a = new JsonArray();
        foreach (string s in iSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) a.Add(s);
        return a;
    }
}

public sealed record SelfTestPlan(int Selected, int Standing, int New, int Closed, int Total);

public sealed record SelfTestRun(List<CheckRow> Rows, List<string> AutoClosed);

public static partial class SelfTest
{
    /// <summary>
    /// 要跑哪些。<paramref name="iOnly"/> 給了 ⇒ 名稱／群命中的**全部**照跑（點名優先於 config）；
    /// <paramref name="iEverything"/>（--all）⇒ 全部；否則 ⇒ 常駐＋新。
    /// </summary>
    static List<(Entry E, SelfTestStatus S)> Select(IEnumerable<Entry> iAll, string iOnly, bool iEverything, SelfTestConfig? iCfg)
    {
        var aOut = new List<(Entry, SelfTestStatus)>();
        foreach (Entry e in iAll)
        {
            SelfTestStatus s = iCfg?.StatusOf(e.Key, e.Important) ?? SelfTestStatus.Standing;
            if (!string.IsNullOrWhiteSpace(iOnly)) { if (Matches(e, iOnly)) aOut.Add((e, s)); }
            else if (iEverything || s != SelfTestStatus.Closed) aOut.Add((e, s));
        }
        return aOut;
    }

    static SelfTestPlan PlanOf(List<(Entry E, SelfTestStatus S)> iSel, int iTotal)
        => new(iSel.Count, iSel.Count(x => x.S == SelfTestStatus.Standing), iSel.Count(x => x.S == SelfTestStatus.New),
               iSel.Count(x => x.S == SelfTestStatus.Closed), iTotal);

    public static SelfTestPlan Plan(IReadOnlyList<ProjectReading> iProjects, string iOnly, bool iEverything, SelfTestConfig iCfg)
    {
        var aCat = Catalog(iProjects);
        return PlanOf(Select(aCat, iOnly, iEverything, iCfg), aCat.Count);
    }

    /// <summary>登記表裡的項目、群與目前狀態（給 `--list`）。</summary>
    public static List<(string Key, string Group, SelfTestStatus Status, bool Important)> ListWithStatus(IReadOnlyList<ProjectReading> iProjects, SelfTestConfig iCfg)
        => Catalog(iProjects).Select(e => (e.Key, e.Group, iCfg.StatusOf(e.Key, e.Important), e.Important)).ToList();

    public static List<string> CatalogKeys(IReadOnlyList<ProjectReading> iProjects) => Catalog(iProjects).Select(e => e.Key).ToList();

    public static SelfTestRun Run(IReadOnlyList<ProjectReading> iProjects, string iOnly, bool iEverything, SelfTestConfig iCfg)
        => Execute(Catalog(iProjects), iOnly, iEverything, iCfg);

    /// <summary>
    /// 引擎本體（吃任意項目清單，給自測用）：先選、再跑，跑完把「新而且真的通過」的項目寫進 disabled。
    /// ⚠ 「通過」＝至少一列、且每一列都是 Pass；Fail／Skipped 都不關。
    /// </summary>
    /// <summary>
    /// 選取機制本身（TASK-0397）。🩸 它壞了的樣子是「該跑的測試安靜地沒跑，而失敗 0」⇒ 所以它自己常駐（important）。
    /// 用合成項目驗：播種／常駐與關閉的分流／新項目跑一次／通過才自動關／fail 與 skipped 不關／important 不關／
    /// disabled 贏過 enabled／--only 與 --all 無視 config／壞檔不冒充沒設定／其他鍵原樣保留。
    /// </summary>
    static CheckRow SelfTestSelectionAndAutoClose()
    {
        const string aName = "selftest 選取：常駐／關閉／新（跑一次、通過才自動關）、important 不關、disabled 贏、--only／--all 無視 config（淨室）";
        string aTmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "senate_selftest_cfg_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            string aPath = SelfTestConfig.PathFor(aTmp);
            static Entry E(string k, CheckResult r, bool imp = false)
                => new Entry(k, "t", () => new[] { new CheckRow(k, "", r) }, imp);
            Entry eA = E("A", CheckResult.Pass), eB = E("B", CheckResult.Pass);
            var aOld = new[] { eA, eB };
            // ① 播種：核心 A 常駐、B 關閉
            var cfg = SelfTestConfig.Load(aPath, aOld.Select(x => x.Key).ToList(), new[] { "A" }, out bool aSeeded);
            bool aSeed = aSeeded && cfg.Enabled.SetEquals(new[] { "A" }) && cfg.Disabled.SetEquals(new[] { "B" }) && File.Exists(aPath);
            // ② 新增四種新項目 + 一個 important
            Entry nPass = E("N_pass", CheckResult.Pass), nFail = E("N_fail", CheckResult.Fail), nSkip = E("N_skip", CheckResult.Skipped),
                  nImp = E("N_imp", CheckResult.Pass, imp: true);
            // 🔴 半套：同一個項目兩列，一列過、一列敗 ⇒ 不准關（單列的項目分不出「全部過」與「任一過」）
            Entry nPartial = new Entry("N_partial", "t", () => new[]
                { new CheckRow("N_partial/1", "", CheckResult.Pass), new CheckRow("N_partial/2", "", CheckResult.Fail) });
            var aAll = new[] { eA, eB, nPass, nFail, nSkip, nImp, nPartial };
            var r1 = Execute(aAll, "", false, cfg);
            var ran1 = r1.Rows.Select(r => r.Name).ToHashSet();
            bool aRan1 = ran1.SetEquals(new[] { "A", "N_pass", "N_fail", "N_skip", "N_imp", "N_partial/1", "N_partial/2" });   // B（關閉）不跑
            bool aClose1 = r1.AutoClosed.SequenceEqual(new[] { "N_pass" })                        // 只有「新而且通過」的被關
                           && cfg.Disabled.Contains("N_pass") && !cfg.Disabled.Contains("N_fail")
                           && !cfg.Disabled.Contains("N_skip") && !cfg.Disabled.Contains("N_imp") && !cfg.Disabled.Contains("N_partial");
            // ③ 寫回檔：重讀後第二趟不再跑 N_pass；fail／skip 還在跑
            var cfg2 = SelfTestConfig.Load(aPath, aAll.Select(x => x.Key).ToList(), new[] { "A" }, out bool aSeeded2);
            var ran2 = Execute(aAll, "", false, cfg2).Rows.Select(r => r.Name).ToHashSet();
            bool aPersist = !aSeeded2 && cfg2.Disabled.Contains("N_pass") && ran2.SetEquals(new[] { "A", "N_fail", "N_skip", "N_imp", "N_partial/1", "N_partial/2" });
            // ④ disabled 贏過 enabled（兩邊都列）
            cfg2.Enabled.Add("A"); cfg2.Disabled.Add("A");
            bool aDisWins = cfg2.StatusOf("A", false) == SelfTestStatus.Closed
                            && !Execute(aAll, "", false, cfg2).Rows.Any(r => r.Name == "A");
            // ⑤ --only 點名無視 config（B 是關閉的卻照跑）；--all 全跑
            bool aOnly = Execute(aAll, "B", false, cfg2).Rows.Select(r => r.Name).SequenceEqual(new[] { "B" });
            bool aEverything = Execute(aAll, "", true, cfg2).Rows.Count == aAll.Length + 1;   // nPartial 兩列
            // ⑥ 其他鍵原樣保留（人手加的）
            string aText = File.ReadAllText(aPath);
            File.WriteAllText(aPath, aText.Replace("\"enabled\"", "\"_人加的\": 7,\n  \"enabled\""));
            var cfg3 = SelfTestConfig.Load(aPath, aAll.Select(x => x.Key).ToList(), new[] { "A" }, out _);
            cfg3.Save();
            bool aKeepExtra = File.ReadAllText(aPath).Contains("_人加的");
            // ⑦ 壞檔不冒充沒設定：丟例外，且檔沒被覆蓋
            File.WriteAllText(aPath, "{ 壞掉");
            bool aBad;
            try { SelfTestConfig.Load(aPath, new[] { "A" }, new[] { "A" }, out _); aBad = false; }
            catch (InvalidOperationException) { aBad = File.ReadAllText(aPath) == "{ 壞掉"; }
            bool aOk = aSeed && aRan1 && aClose1 && aPersist && aDisWins && aOnly && aEverything && aKeepExtra && aBad;
            return new CheckRow(aName,
                $"播種={aSeed}／預設只跑常駐＋新={aRan1}／🔴 只有新而且通過的自動關（fail／skip／半套／important 不關）={aClose1}／寫回後下一趟不再跑={aPersist}"
                + $"／disabled 贏={aDisWins}／--only 點名關閉項照跑={aOnly}／--all 全跑={aEverything}／其他鍵保留={aKeepExtra}／壞檔擋且不覆蓋={aBad}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    internal static SelfTestRun Execute(IEnumerable<Entry> iAll, string iOnly, bool iEverything, SelfTestConfig iCfg)
    {
        var aRows = new List<CheckRow>();
        var aClosed = new List<string>();
        foreach (var (e, s) in Select(iAll, iOnly, iEverything, iCfg))
        {
            List<CheckRow> aMine = e.Run().ToList();
            aRows.AddRange(aMine);
            if (s == SelfTestStatus.New && aMine.Count > 0 && aMine.All(r => r.Result == CheckResult.Pass))
            {
                iCfg.Enabled.Remove(e.Key);
                iCfg.Disabled.Add(e.Key);
                aClosed.Add(e.Key);
            }
        }
        if (aClosed.Count > 0) iCfg.Save();
        return new SelfTestRun(aRows, aClosed);
    }
}
