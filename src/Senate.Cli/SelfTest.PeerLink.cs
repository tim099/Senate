// 區塊職責：**跨主機連線的對拍**（TASK-0011）—— 暫存資料夾裡起一台主機與三個另一端，走本機迴路把整條路跑一遍。
// 物理意義：握手（token 不對／冒充主機都連不上）、第一次接上不回放、主機 ↔ 另一端、主機轉發給其他另一端、不迴圈、
//           斷線期間的訊息重連後補上、同一則重送只落一則、在線名單、轉進來的不發薪／不觸發酒保／Discord 不重送。
//           本體在 `Cmd_Peer.RunSelfTest`（`senate cmd peer --arg op=selftest` 跑的是同一支）。
// 數值影響：只寫系統暫存資料夾（通過就刪；沒過留著並印出位置）；開一個本機迴路的埠。
//           ⛔ 不碰真的資料根、不連 Discord、不開 Cloudflare 通道。約 20～40 秒。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow PeerLinkCleanRoom()
    {
        const string aName = "跨主機連線：握手／訊息經主機轉發／游標補送／在線名單（TASK-0011）";
        var aLines = new List<string>();
        List<string> aFails = Cmd_Peer.RunSelfTest(aLines);
        string aReport = Path.Combine(Path.GetTempPath(), "senate-peer-selftest-last.txt");
        try { File.WriteAllLines(aReport, aLines); } catch (Exception) { aReport = "（寫不出來）"; }
        int aPass = aLines.Count(l => l.StartsWith("✓ ", StringComparison.Ordinal) && l != "✓ 全部通過");
        return new CheckRow(aName,
            aFails.Count == 0
                ? $"{aPass} 項全過（含反向：token 不對連不上、冒充主機不連、重送只落一則、不迴圈）；逐項讀數在 {aReport}"
                : $"沒過 {aFails.Count} 項：{string.Join("；", aFails)}　〔" + string.Join(" ｜ ", aLines.Where(l => l.StartsWith("✗ ", StringComparison.Ordinal))) + "〕",
            aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
