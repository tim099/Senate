// 區塊職責：TASK-0369（積壓超過回捲上限 ⇒ 游標永久卡死）的自我對拍。
// 物理意義：三格各驗一個「錯了也不會叫」的地方：
//           ① 出口：skip_backlog 時交**最新**那批、水位推到最新、跳過的段落有點名（至少幾則、從哪一則之前）。
//           ② 不變：積壓在上限內時，帶不帶 skip 結果逐筆相同（由舊到新）、跳過標記為 false。
//           🔴 ③ 反向：不帶 skip 時照舊拒推（NewestTs＝null、Truncated＝true）、游標一格不動。
// 數值影響：在 temp 目錄造 BACKLOG_SCAN_CAP＋100 則訊息（走沒有索引的退化路徑），跑完刪；⛔ 不碰真實資料根。
#nullable enable
using SCP.Core.Tavern;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TavernBacklogSkipCleanRoom()
    {
        const string aName = "酒館游標：積壓超過回捲上限 ⇒ skip_backlog 推到最新並點名跳過段／上限內行為不變／不帶照舊拒推（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_cursor_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int aTotal = SCP_TavernCursor.BACKLOG_SCAN_CAP + 100;
            string aDir = Path.Combine(aTmp, "ChatTavern", "rooms", "tavern", "messages", "2026-01-01");
            Directory.CreateDirectory(aDir);
            var aBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string Ts(int i) => aBase.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 1; i <= aTotal; i++)
                File.WriteAllText(Path.Combine(aDir, i.ToString("D8") + ".json"),
                    "{\"ts\":\"" + Ts(i) + "\",\"sender_persona\":\"probe\",\"sender_id\":\"probe\",\"kind\":\"chat\",\"body\":\"m" + i + "\"}");
            const string aWho = "reader";
            const int aLimit = SCP_TavernCursor.SCAN_LIMIT;

            // 🔴 ③ 游標在第 1 則 ⇒ 積壓 aTotal-1 則（超過上限）。不帶 skip ⇒ 拒推，且游標不動。
            SCP_TavernCursor.WriteCursor(aTmp, aWho, Ts(1));
            SCP_TavernCursor.ReadUnread(aTmp, aWho, "tavern", out string? aN0, out bool aT0);
            var aB0 = SCP_TavernCatchup.Build(aTmp, aTmp, aWho, "tavern", 1, true, false, 1);
            var (_, aAdv0) = SCP_TavernCatchup.AdvanceAfterWrite(aTmp, aWho, aB0, true);
            bool aRefuse = aN0 == null && aT0 && aAdv0 == null && SCP_TavernCursor.ReadCursor(aTmp, aWho) == Ts(1);

            // ① 帶 skip ⇒ 交最新 SCAN_LIMIT 則、水位＝最後一則、跳過段點名
            var aGot = SCP_TavernCursor.ReadUnread(aTmp, aWho, "tavern", true, out string? aN1, out bool aT1,
                                                   out SCP_TavernBacklogSkip aSkip);
            int aFirstKept = aTotal - aLimit + 1;
            bool aExit = aGot.Count == aLimit && aGot[0].Seq == aFirstKept && aN1 == Ts(aTotal) && !aT1
                         && aSkip.Applied && aSkip.FirstKeptSeq == aFirstKept && aSkip.FromCursorTs == Ts(1)
                         && aSkip.SkippedInWindowAtLeast == SCP_TavernCursor.BACKLOG_SCAN_CAP - aLimit;
            // ① 端到端：Build＋AdvanceAfterWrite 真的把游標推到最新（讀回），簡報裡有點名那一段
            var aB1 = SCP_TavernCatchup.Build(aTmp, aTmp, aWho, "tavern", 1, true, false, 1, true);
            var (aLine1, aAdv1) = SCP_TavernCatchup.AdvanceAfterWrite(aTmp, aWho, aB1, true);
            bool aEndToEnd = aAdv1 == Ts(aTotal) && SCP_TavernCursor.ReadCursor(aTmp, aWho) == Ts(aTotal)
                             && aB1.Body.Contains("seq " + aFirstKept + " 之前") && aLine1.Contains("跳過了");

            // ② 上限內：游標在倒數第 100 則前 ⇒ 100 則未讀。帶不帶 skip 逐筆相同（由舊到新），且沒有跳
            string aCur2 = Ts(aTotal - 100);
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "2", aCur2);
            var aPlain = SCP_TavernCursor.ReadUnread(aTmp, aWho + "2", "tavern", out string? aN2, out bool aT2);
            var aWith = SCP_TavernCursor.ReadUnread(aTmp, aWho + "2", "tavern", true, out string? aN3, out bool aT3,
                                                    out SCP_TavernBacklogSkip aSkip2);
            bool aSame = aPlain.Count == aWith.Count && aN2 == aN3 && aT2 == aT3 && !aSkip2.Applied
                         && aPlain.Count == aLimit && aPlain[0].Seq == aTotal - 99;
            for (int i = 0; aSame && i < aPlain.Count; i++) aSame = aPlain[i].Seq == aWith[i].Seq;

            bool aOk = aRefuse && aExit && aEndToEnd && aSame;
            return new CheckRow(aName,
                $"🔴 不帶 skip⇒拒推且游標不動={aRefuse}／skip⇒交最新 {aGot.Count} 則、首則 seq {(aGot.Count > 0 ? aGot[0].Seq : 0)}（期望 {aFirstKept}）、"
                + $"跳過至少 {aSkip.SkippedInWindowAtLeast}={aExit}／端到端推到最新且點名={aEndToEnd}／上限內帶不帶 skip 逐筆相同={aSame}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }
}
