// 區塊職責：**跨主機連線的對拍**（TASK-0011）—— 暫存資料夾裡起一台主機與三個另一端，走本機迴路把整條路跑一遍。
// 物理意義：握手（token 不對／冒充主機都連不上）、第一次接上不回放、主機 ↔ 另一端、主機轉發給其他另一端、不迴圈、
//           斷線期間的訊息重連後補上、同一則重送只落一則、在線名單、轉進來的不發薪／不觸發酒保／Discord 不重送。
//           本體在 `Cmd_Peer.RunSelfTest`（`senate cmd peer --arg op=selftest` 跑的是同一支）。
// 數值影響：只寫系統暫存資料夾（通過就刪；沒過留著並印出位置）；開一個本機迴路的埠。
//           ⛔ 不碰真的資料根、不連 Discord、不開 Cloudflare 通道。約 20～40 秒。
//           `PeerTaskCleanRoom`（FAR-53）：Task 指令經連線送到 Task Server；本體在 `SenatePeerTaskSelfTest.Run`（FAR-58 加上限與限頻）。
//           逐項讀數檔：預設寫系統暫存資料夾；有設 `SENATE_PEER_SELFTEST_REPORT_DIR` 就寫那個資料夾（FAR-64：
//           後台「跨主機連線」頁另開子行程跑，每次給自己的資料夾，讀回來列在頁面上）。檔名不變。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    internal const string PeerReportDirEnv = "SENATE_PEER_SELFTEST_REPORT_DIR";
    internal const string PeerLinkReportFile = "senate-peer-selftest-last.txt";
    internal const string PeerTaskReportFile = "senate-peertask-selftest-last.txt";

    /// <summary>逐項讀數檔寫在哪：環境變數有給就用它，沒給（或空白）照舊寫系統暫存資料夾。</summary>
    internal static string PeerReportPath(string iFile, string? iDir)
        => Path.Combine(string.IsNullOrWhiteSpace(iDir) ? Path.GetTempPath() : iDir, iFile);

    static string PeerReportPath(string iFile) => PeerReportPath(iFile, Environment.GetEnvironmentVariable(PeerReportDirEnv));

    static CheckRow PeerLinkCleanRoom()
    {
        const string aName = "跨主機連線：握手／訊息經主機轉發／游標補送／在線名單（TASK-0011）";
        var aLines = new List<string>();
        List<string> aFails = Cmd_Peer.RunSelfTest(aLines);
        string aReport = PeerReportPath(PeerLinkReportFile);
        try { File.WriteAllLines(aReport, aLines); } catch (Exception) { aReport = "（寫不出來）"; }
        int aPass = aLines.Count(l => l.StartsWith("✓ ", StringComparison.Ordinal) && l != "✓ 全部通過");
        return new CheckRow(aName,
            aFails.Count == 0
                ? $"{aPass} 項全過（含反向：token 不對連不上、冒充主機不連、重送只落一則、不迴圈）；逐項讀數在 {aReport}"
                : $"沒過 {aFails.Count} 項：{string.Join("；", aFails)}　〔" + string.Join(" ｜ ", aLines.Where(l => l.StartsWith("✗ ", StringComparison.Ordinal))) + "〕",
            aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }

    static CheckRow PeerTaskCleanRoom()
    {
        const string aName = "跨主機連線：Task 指令送到 Task Server（寫只在那一台／連不到說出來／簽章與允許名單／擋重送／上限與限頻）（FAR-53、FAR-58）";
        var aLines = new List<string>();
        List<string> aFails = SenatePeerTaskSelfTest.Run(aLines);
        string aReport = PeerReportPath(PeerTaskReportFile);
        try { File.WriteAllLines(aReport, aLines); } catch (Exception) { aReport = "（寫不出來）"; }
        int aPass = aLines.Count(l => l.StartsWith("✓ ", StringComparison.Ordinal) && l != "✓ 全部通過");
        return new CheckRow(aName,
            aFails.Count == 0
                ? $"{aPass} 項全過（含反向：不在線 exit 6、不回 exit 7、不在名單 exit 1、被改過不回、同一個 rid 只寫一次、同時最多 4 筆、擋下限頻）；逐項讀數在 {aReport}"
                : $"沒過 {aFails.Count} 項：{string.Join("；", aFails)}　〔" + string.Join(" ｜ ", aLines.Where(l => l.StartsWith("✗ ", StringComparison.Ordinal))) + "〕",
            aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
