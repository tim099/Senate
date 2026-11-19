// 區塊職責：「跨主機連線」頁「自我測試」格的對拍（FAR-64）—— 不起節點、不起子行程，只驗頁面怎麼整理逐項讀數。
// 物理意義：每一格驗一個「錯了也不會叫」的地方：
//           ① 計數：結尾的「✓ 全部通過」／「✗ 沒過 N 項」是總結不算一項（跟 selftest 那一列同一套算法）
//           ② 沒過的排最上面，同一組照原本的順序；③ 備註（節點紀錄、暫存資料夾位置）不丟、排在最後
//           ④ 沒拿到結果 ⇒ 寫「沒有拿到結果」，⛔ 不畫成「通過 0／沒過 0」；逐項都過但結束碼不是 0 ⇒ 不當成通過
//           ⑤ 畫面上沒有 emoji；⑥ 逐項檔的位置：環境變數有給就寫那裡，沒給照舊寫系統暫存資料夾
//           ⑦ 頁面點名的兩個項目名還在 selftest 目錄裡（頁面那邊是字串，改名時編譯器不會叫）
// 數值影響：純記憶體；⑥ 暫時設行程的環境變數，跑完還原。
#nullable enable
using Senate.Cli.Pages;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow PeerLinkSelfTestView()
    {
        const string aName = "跨主機連線頁「自我測試」格：計數／沒過的排最上面／備註不丟／沒結果不冒充通過／逐項檔位置（FAR-64）";
        var aFails = new List<string>();
        var aReads = new List<string>();
        DateTime aT0 = new DateTime(2026, 11, 17, 11, 2, 3);

        // ①②③⑤
        string[] aLines =
        {
            "✓ 主機開埠　〔listening=True port=1234〕",
            "✗ 第一次接上不回放　〔replayed=3〕",
            "✓ 主機 ↔ 另一端　〔ok〕",
            "",
            "✗ 不迴圈　〔loops=1 🔴〕",
            "✓ 在線名單　〔n=2〕",
            "暫存資料夾留著給你看：/tmp/senate-peer-selftest-x",
            "✗ 沒過 2 項：第一次接上不回放、不迴圈",
            "✓ 全部通過",
        };
        var o = PeerLinkSelfTest.Summarize("連線測試", aLines, 1, "", aT0, TimeSpan.FromSeconds(47.4));
        aReads.Add($"通過 {o.Pass}／沒過 {o.Fail}");
        if (o.Pass != 3 || o.Fail != 2) aFails.Add($"計數 通過 {o.Pass}／沒過 {o.Fail}（期望 3／2：結尾總結不算一項）");
        if (o.Headline != "通過 3 項／沒過 2 項　11:02:03 開始，花了 47 秒") aFails.Add("摘要：" + o.Headline);
        string[] aOrder = { "第一次接上不回放", "不迴圈", "主機開埠", "主機 ↔ 另一端", "在線名單" };
        bool aOrderOk = o.Rows.Count == aOrder.Length && o.Rows.Zip(aOrder).All(x => x.First.Contains(x.Second, StringComparison.Ordinal));
        aReads.Add("順序 " + string.Join(" ｜ ", o.Rows.Select(r => r.Split('　')[0])));
        if (!aOrderOk) aFails.Add("沒過的沒排在最上面，或同一組的順序變了");
        if (o.Notes.Count != 1 || !o.Notes[0].Contains("暫存資料夾留著", StringComparison.Ordinal)) aFails.Add("備註：" + string.Join(" ｜ ", o.Notes));
        if (o.Ok) aFails.Add("有沒過的項目卻判成通過");
        string aAll = o.Headline + string.Join("", o.Rows) + string.Join("", o.Notes);
        if (aAll.Contains('✓') || aAll.Contains('✗') || aAll.Any(char.IsSurrogate)) aFails.Add("畫面上還有 emoji：" + aAll);
        if (!o.Rows[0].StartsWith("[不行] ", StringComparison.Ordinal) || !o.Rows[2].StartsWith("[好] ", StringComparison.Ordinal)) aFails.Add("✓／✗ 沒換成 [好]／[不行]");

        // ④ 沒拿到結果
        foreach (var (aWhat, aIn, aProblem) in new (string, string[]?, string)[]
                 {
                     ("沒有檔案", null, "子行程結束碼 2，但沒有產生逐項檔\n--- stderr 最後幾行 ---\n✗ `--only X` 一格都沒選到"),
                     ("空檔", Array.Empty<string>(), ""),
                     ("只有備註", new[] { "暫存資料夾留著給你看：/tmp/x" }, ""),
                 })
        {
            var e = PeerLinkSelfTest.Summarize("Task Server 測試", aIn, 2, aProblem, aT0, TimeSpan.FromSeconds(3));
            if (e.GotResult || e.Ok || !e.Headline.StartsWith("沒有拿到結果", StringComparison.Ordinal) || e.Headline.Contains("通過 0", StringComparison.Ordinal) || e.Notes.Count == 0)
                aFails.Add($"{aWhat}：{e.Headline}（備註 {e.Notes.Count} 行）");
        }
        var aNoFile = PeerLinkSelfTest.Summarize("x", null, 2, "子行程結束碼 2，但沒有產生逐項檔\n--- stderr 最後幾行 ---\n✗ 一格都沒選到", aT0, TimeSpan.Zero);
        if (!aNoFile.Notes.Any(n => n.Contains("[不行] 一格都沒選到", StringComparison.Ordinal))) aFails.Add("沒有檔案時 stderr 沒列出來：" + string.Join(" ｜ ", aNoFile.Notes));
        var aBadExit = PeerLinkSelfTest.Summarize("x", new[] { "✓ a　〔1〕", "✓ 全部通過" }, 1, "", aT0, TimeSpan.Zero);
        if (aBadExit.Ok || !aBadExit.Notes.Any(n => n.Contains("結束碼是 1", StringComparison.Ordinal))) aFails.Add("逐項都過但結束碼 1 被判成通過");
        var aGood = PeerLinkSelfTest.Summarize("x", new[] { "✓ a　〔1〕", "✓ b　〔2〕", "✓ 全部通過" }, 0, "", aT0, TimeSpan.Zero);
        if (!aGood.Ok || aGood.Pass != 2 || aGood.Notes.Count != 0) aFails.Add($"全過的那一份：ok={aGood.Ok} 通過 {aGood.Pass} 備註 {aGood.Notes.Count}");

        // ⑥ 逐項檔位置
        string aDir = Path.Combine(Path.GetTempPath(), "senate-peer-selftest-page-probe");
        string aTmp = Path.Combine(Path.GetTempPath(), PeerLinkReportFile);
        if (PeerReportPath(PeerLinkReportFile, aDir) != Path.Combine(aDir, PeerLinkReportFile)) aFails.Add("有給資料夾卻沒寫進去");
        if (PeerReportPath(PeerLinkReportFile, null) != aTmp || PeerReportPath(PeerLinkReportFile, " ") != aTmp) aFails.Add("沒給資料夾時不是原本的位置");
        string? aKeep = Environment.GetEnvironmentVariable(PeerReportDirEnv);
        try
        {
            Environment.SetEnvironmentVariable(PeerReportDirEnv, aDir);
            string aWith = PeerReportPath(PeerTaskReportFile);
            Environment.SetEnvironmentVariable(PeerReportDirEnv, null);
            string aWithout = PeerReportPath(PeerTaskReportFile);
            aReads.Add($"有設 → {aWith}；沒設 → {aWithout}");
            if (aWith != Path.Combine(aDir, PeerTaskReportFile) || aWithout != Path.Combine(Path.GetTempPath(), PeerTaskReportFile))
                aFails.Add("環境變數沒被讀到，或沒設時位置變了");
        }
        finally { Environment.SetEnvironmentVariable(PeerReportDirEnv, aKeep); }

        // ⑦ 頁面點名的項目名
        var aKeys = CatalogKeys();
        foreach (var p in new[] { PeerLinkSelfTest.Link, PeerLinkSelfTest.TaskServer })
            if (!aKeys.Contains(p.Key)) aFails.Add($"頁面點名的 {p.Key} 不在 selftest 目錄裡");

        return new CheckRow(aName,
            aFails.Count == 0 ? string.Join("；", aReads) : "沒過：" + string.Join("；", aFails),
            aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
