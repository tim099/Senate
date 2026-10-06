// 區塊職責：酒館游標「積壓超過回捲上限」的自我對拍（TASK-0369 → TASK-0407 改成自動）＋回捲上限設定。
// 物理意義：每一格驗一個「錯了也不會叫」的地方：
//           ① 自動：不帶任何參數，積壓超過上限時，上限內照常由舊到新交付（窗口第一則起）、游標往前、更舊的那段不讀並**點名**（至少幾則、哪一則之前）。
//           ② 只發生一次：游標進到窗口裡之後，下一趟照常往前讀，不再報跳過。
//           🔴 ③ 反向：積壓在上限內 ⇒ 一則都不跳；**剛好**落在上限邊緣（更舊那端沒有比游標新的）⇒ 也一則都不跳。
//           ④ 上限讀設定：設了就用、沒設用預設並明說「用預設值」、不合法照預設跑並說出原因（⛔ 不夾值）。
//           ⑤ 設定讀寫：存檔擋不合法且零寫入、預設值不寫進檔、其他鍵保留、讀回比對。
// 數值影響：在 temp 目錄造 BACKLOG_SCAN_CAP＋100 則訊息（走沒有索引的退化路徑），跑完刪；⛔ 不碰真實資料根。
#nullable enable
using SCP.Core.Tavern;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TavernBacklogSkipCleanRoom()
    {
        const string aName = "酒館游標：積壓超過回捲上限 ⇒ 自動：上限內照讀、更舊的不讀並點名／只跳一次／上限內與剛好邊緣不跳／上限讀設定（淨室，TASK-0407）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_cursor_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int aCapDefault = SCP_TavernCursor.BACKLOG_SCAN_CAP;
            int aTotal = aCapDefault + 100;
            string aDir = Path.Combine(aTmp, "ChatTavern", "rooms", "tavern", "messages", "2026-01-01");
            Directory.CreateDirectory(aDir);
            var aBase = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            string Ts(int i) => aBase.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 1; i <= aTotal; i++)
                File.WriteAllText(Path.Combine(aDir, i.ToString("D8") + ".json"),
                    "{\"ts\":\"" + Ts(i) + "\",\"sender_persona\":\"probe\",\"sender_id\":\"probe\",\"kind\":\"chat\",\"body\":\"m" + i + "\"}");
            const string aWho = "reader";
            const int aLimit = SCP_TavernCursor.SCAN_LIMIT;

            // ① 游標在第 1 則 ⇒ 積壓 aTotal-1 則（超過上限）。窗口＝最新 aCapDefault 則＝seq 101..aTotal。
            SCP_TavernCursor.WriteCursor(aTmp, aWho, Ts(1));
            var aGot = SCP_TavernCursor.ReadUnread(aTmp, aWho, "tavern", aCapDefault, out string? aN1, out bool aT1, out SCP_TavernBacklogSkip aSkip);
            int aFirstKept = aTotal - aCapDefault + 1;   // 101
            bool aAuto = aGot.Count == aLimit && aGot[0].Seq == aFirstKept && aGot[aLimit - 1].Seq == aFirstKept + aLimit - 1   // 窗口第一則起、由舊到新
                         && aN1 == Ts(aFirstKept + aLimit - 1) && aT1                                                           // 水位＝這批最後一則；還有更多
                         && aSkip.Applied && aSkip.FirstKeptSeq == aFirstKept && aSkip.FromCursorTs == Ts(1)
                         && aSkip.SkippedAtLeast == aLimit;                                                                     // 窗口外探到的 60 則（更舊的沒數到）

            // ① 端到端：不帶任何參數的 Build＋AdvanceAfterWrite 真的把游標推到那批的最後一則（讀回），簡報裡點名跳過的那段與回捲上限
            var aB1 = SCP_TavernCatchup.Build(aTmp, aTmp, aWho, "tavern", 1, true, false, 1);
            var (aLine1, aAdv1) = SCP_TavernCatchup.AdvanceAfterWrite(aTmp, aWho, aB1, true);
            bool aEndToEnd = aAdv1 == Ts(aFirstKept + aLimit - 1) && SCP_TavernCursor.ReadCursor(aTmp, aWho) == Ts(aFirstKept + aLimit - 1)
                             && aB1.Body.Contains("seq " + aFirstKept + " 之前") && aLine1.Contains("跳過了")
                             && aB1.Body.Contains("**" + aCapDefault + "** 則（用預設值");

            // ② 只跳一次：游標現在在窗口裡 ⇒ 下一趟照常往前讀（窗口剩下的那段），不再報跳過
            var aNext = SCP_TavernCursor.ReadUnread(aTmp, aWho, "tavern", aCapDefault, out string? aN2, out bool aT2, out SCP_TavernBacklogSkip aSkip2);
            int aRemain = aTotal - (aFirstKept + aLimit - 1);
            int aTake2 = Math.Min(aLimit, aRemain);
            bool aOnce = !aSkip2.Applied && aNext.Count == aTake2 && aNext[0].Seq == aFirstKept + aLimit
                         && aT2 == (aRemain > aLimit) && aN2 == Ts(aFirstKept + aLimit + aTake2 - 1);

            // 🔴 ③ 反向：積壓在上限內（上限少 10 則）⇒ 一則都不跳，由舊到新
            int aBacklogIn = aCapDefault - 10;
            int aTake3 = Math.Min(aLimit, aBacklogIn);
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "2", Ts(aTotal - aBacklogIn));
            var aIn = SCP_TavernCursor.ReadUnread(aTmp, aWho + "2", "tavern", aCapDefault, out string? aN3, out bool aT3, out SCP_TavernBacklogSkip aSkip3);
            bool aWithin = !aSkip3.Applied && aIn.Count == aTake3 && aIn[0].Seq == aTotal - aBacklogIn + 1
                           && aT3 == (aBacklogIn > aLimit) && aN3 == Ts(aTotal - aBacklogIn + aTake3);
            // 🔴 ③ 剛好在邊緣：游標＝窗口前一則（ts 相等不算未讀）⇒ 未讀剛好 aCapDefault 則、窗口外沒有比游標新的 ⇒ 不跳
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "3", Ts(aTotal - aCapDefault));
            var aEdge = SCP_TavernCursor.ReadUnread(aTmp, aWho + "3", "tavern", aCapDefault, out _, out _, out SCP_TavernBacklogSkip aSkip4);
            bool aEdgeOk = !aSkip4.Applied && aEdge.Count == aLimit && aEdge[0].Seq == aFirstKept;

            // ④ 上限讀設定：寫 80 ⇒ 窗口＝最新 80 則、簡報說「設定檔」；寫不合法值（0）⇒ 照預設並說出原因（不夾值）
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "4", Ts(1));
            Directory.CreateDirectory(Path.Combine(aTmp, "ChatTavern"));
            File.WriteAllText(SCP_TavernRenderSettings.PathOf(aTmp), "{\"backlog_scan_cap\": 80}");
            var aB4 = SCP_TavernCatchup.Build(aTmp, aTmp, aWho + "4", "tavern", 1, true, false, 1);
            int aFirstFile = aTotal - 80 + 1;
            bool aCapFromFile = aB4.Skip.Applied && aB4.Skip.FirstKeptSeq == aFirstFile && aB4.Body.Contains("**80** 則（設定檔");
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "5", Ts(1));
            File.WriteAllText(SCP_TavernRenderSettings.PathOf(aTmp), "{\"backlog_scan_cap\": 0}");
            var aB5 = SCP_TavernCatchup.Build(aTmp, aTmp, aWho + "5", "tavern", 1, true, false, 1);
            bool aBadFallsBack = aB5.Skip.Applied && aB5.Skip.FirstKeptSeq == aFirstKept   // 照預設，不是夾成下限
                                 && aB5.Body.Contains("**" + aCapDefault + "** 則（用預設值") && aB5.Body.Contains("回捲上限不合法");

            // 🔴 ④' 上限比一批小（TASK-0408：合法 1～10000）⇒ 窗口只有最新 10 則，⛔ 不撐回 SCAN_LIMIT
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "7", Ts(1));
            var aSmall = SCP_TavernCursor.ReadUnread(aTmp, aWho + "7", "tavern", 10, out string? aN7, out bool aT7, out SCP_TavernBacklogSkip aSkip7);
            bool aSmallCap = aSmall.Count == 10 && aSmall[0].Seq == aTotal - 9 && aN7 == Ts(aTotal) && !aT7
                             && aSkip7.Applied && aSkip7.FirstKeptSeq == aTotal - 9;

            // ⑤ 舊參數：skip_backlog 還能帶、沒有作用（結果與不帶相同），簡報說一句
            SCP_TavernCursor.WriteCursor(aTmp, aWho + "6", Ts(1));
            File.Delete(SCP_TavernRenderSettings.PathOf(aTmp));
            var aB6 = SCP_TavernCatchup.Build(aTmp, aTmp, aWho + "6", "tavern", 1, true, false, 1, true);
            bool aLegacy = aB6.Skip.Applied && aB6.Skip.FirstKeptSeq == aFirstKept && aB6.Body.Contains("skip_backlog` 已不需要");

            bool aOk = aAuto && aEndToEnd && aOnce && aWithin && aEdgeOk && aCapFromFile && aBadFallsBack && aSmallCap && aLegacy;
            return new CheckRow(aName,
                $"自動（不帶參數）：首批從窗口第一則 seq {(aGot.Count > 0 ? aGot[0].Seq : 0)}（期望 {aFirstKept}）起、點名跳過至少 {aSkip.SkippedAtLeast}（期望 {aLimit}）={aAuto}"
                + $"／端到端推進＋點名＋說「用預設值」={aEndToEnd}／只跳一次={aOnce}"
                + $"／🔴 上限內不跳={aWithin}、剛好邊緣不跳={aEdgeOk}"
                + $"／上限讀設定（80⇒窗口 {aFirstFile} 起）={aCapFromFile}、🔴 不合法照預設不夾值={aBadFallsBack}"
                + $"／🔴 上限 10 只讀最新 10 則（實際 {aSmall.Count} 則、首則 seq {(aSmall.Count > 0 ? aSmall[0].Seq : 0)}）={aSmallCap}／舊 skip_backlog 無作用且說明={aLegacy}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    /// <summary>回捲上限的設定讀寫（TASK-0407）：沒設過／設了／不合法三態不同形；存檔擋不合法且零寫入；預設值不落檔；其他鍵保留。</summary>
    static CheckRow TavernBacklogCapSettings()
    {
        const string aName = "酒館設定：回捲上限 沒設過／設了／不合法 三態不同形、存檔擋不合法零寫入、預設值不落檔、其他鍵保留（淨室，TASK-0407）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_capset_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(aTmp, "ChatTavern"));
            string aPath = SCP_TavernRenderSettings.PathOf(aTmp);
            // 沒設過（檔不存在）⇒ 預設、IsDefault、無錯誤
            var s0 = SCP_TavernRenderSettings.Read(aTmp, out bool aEx0, out string? aE0);
            bool aUnset = !aEx0 && s0.BacklogCap == SCP_TavernRenderSettings.DefaultBacklogCap && s0.BacklogCapIsDefault && aE0 == null;
            // 存預設值（檔不存在）⇒ 不寫 backlog_scan_cap 這個鍵
            bool aW0 = SCP_TavernRenderSettings.Write(aTmp, s0, out _);
            bool aDefaultNotPersisted = aW0 && !File.ReadAllText(aPath).Contains(SCP_TavernRenderSettings.KeyBacklogCap);
            // 設 1000 ⇒ 讀回 1000、不是預設
            var s1 = s0.Clone(); s1.BacklogCap = 1000;
            bool aW1 = SCP_TavernRenderSettings.Write(aTmp, s1, out _);
            var r1 = SCP_TavernRenderSettings.Read(aTmp, out _, out string? aE1);
            bool aSet = aW1 && r1.BacklogCap == 1000 && !r1.BacklogCapIsDefault && aE1 == null;
            // 🔴 不合法（太小／太大）⇒ 存檔擋下、檔位元組不變
            string aBefore = File.ReadAllText(aPath);
            var bad = r1.Clone(); bad.BacklogCap = SCP_TavernRenderSettings.MinBacklogCap - 1;
            var bad2 = r1.Clone(); bad2.BacklogCap = SCP_TavernRenderSettings.MaxBacklogCap + 1;
            bool aRefuse = !SCP_TavernRenderSettings.Write(aTmp, bad, out string? aWe1) && !SCP_TavernRenderSettings.Write(aTmp, bad2, out _)
                           && File.ReadAllText(aPath) == aBefore && (aWe1 ?? "").Contains("回捲上限");
            // 其他鍵保留（人手加的）
            File.WriteAllText(aPath, "{\"message_body_clip\":600,\"_人加的\":7,\"backlog_scan_cap\":1000}");
            var r2 = SCP_TavernRenderSettings.Read(aTmp, out _, out _); r2.BacklogCap = 2000;
            bool aKeep = SCP_TavernRenderSettings.Write(aTmp, r2, out _) && File.ReadAllText(aPath).Contains("_人加的");
            // 🔴 讀到壞值（檔裡手寫 0 / 字串）⇒ 照預設、IsDefault、錯誤說出來；⛔ 不夾成下限
            File.WriteAllText(aPath, "{\"backlog_scan_cap\": 0}");
            var r3 = SCP_TavernRenderSettings.Read(aTmp, out _, out string? aE3);
            File.WriteAllText(aPath, "{\"backlog_scan_cap\": \"很多\"}");
            var r4 = SCP_TavernRenderSettings.Read(aTmp, out _, out string? aE4);
            bool aBad = r3.BacklogCap == SCP_TavernRenderSettings.DefaultBacklogCap && r3.BacklogCapIsDefault && (aE3 ?? "").Contains("不合法")
                        && r4.BacklogCap == SCP_TavernRenderSettings.DefaultBacklogCap && r4.BacklogCapIsDefault && (aE4 ?? "").Contains("不合法");
            // 壞 JSON ⇒ 預設＋讀不了（跟「沒設過」分開說）；此時存檔不覆寫壞檔
            File.WriteAllText(aPath, "{ 壞掉");
            var r5 = SCP_TavernRenderSettings.Read(aTmp, out bool aEx5, out string? aE5);
            bool aBroken = aEx5 && r5.BacklogCap == SCP_TavernRenderSettings.DefaultBacklogCap && aE5 != null
                           && !SCP_TavernRenderSettings.Write(aTmp, r5, out _) && File.ReadAllText(aPath) == "{ 壞掉";

            bool aOk = aUnset && aDefaultNotPersisted && aSet && aRefuse && aKeep && aBad && aBroken;
            return new CheckRow(aName,
                $"沒設過⇒預設={aUnset}／預設值不落檔={aDefaultNotPersisted}／設 1000 讀回={aSet}／🔴 不合法存檔擋且位元組不變={aRefuse}"
                + $"／其他鍵保留={aKeep}／🔴 讀到壞值照預設不夾值={aBad}／壞 JSON 讀不了且不覆寫={aBroken}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }
}
