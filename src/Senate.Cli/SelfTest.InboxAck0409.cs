// 區塊職責：inbox 歸檔（`tavern-inbox-ack`，TASK-0409 取代 python `inbox_ack.py`）的自我對拍。
// 物理意義：每一格驗一個「錯了也不會叫」的地方：
//           ① 檔頭提示指向現在能跑的指令（⛔ 不再是已刪的 `tavern_query.py`／UCL 那支 `inbox_ack.py`）
//           ② 歸檔：條目進 `_archive.md`、讀回 inbox 剩 0 筆；重跑是 no-op（archive 位元組不變）；
//              清空後檔頭照樣在，舊檔頭（python 指路）在歸檔時換成現役指令
//           ③ 打錯 owner ⇒「沒有這份 inbox」，⛔ 不說成「已清空」；歸檔後再被 @ 照常附加
//           ④ all_rooms 掃每一房（不只 tavern／hideout）
//           🔴 ⑤ 鎖：寫入端的鎖被握著時歸檔**兩個檔都不動**並出聲 —— python 版不拿鎖，這一格就是它缺的那把
// 數值影響：在 temp 目錄造資料根，跑完刪；⛔ 不碰真實資料根。⑤ 會等一次鎖逾時（約 3 秒）。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Tavern;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TavernInboxAckCleanRoom()
    {
        const string aName = "inbox 歸檔（tavern-inbox-ack）：提示指向現役指令／歸檔讀回 0 筆、重跑不重複／打錯 owner 不說成已清空／all_rooms 掃每一房／🔴 鎖被握著兩檔不動（淨室，TASK-0409）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_inboxack_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            const string aWho = "probe";
            SCP_TavernInbox.Append(aTmp, "tavern", aWho, 11, "💬 a @妳", "第一則");
            SCP_TavernInbox.Append(aTmp, "tavern", aWho, 12, "💬 b @妳", "第二則");
            string aInbox = SCP_TavernInbox.InboxPath(aTmp, "tavern", aWho);
            string aArchive = SCP_TavernInbox.ArchivePath(aTmp, "tavern", aWho);

            // ① 檔頭提示
            string aHead = File.ReadAllText(aInbox);
            string aAckName = SCP_CmdRegistry.NameOf<SCP_Cmd_TavernInboxAck>();
            bool aHint = aHead.Contains(aAckName + " --arg owner=" + aWho) && aHead.Contains("--arg kind=seq")
                         && !aHead.Contains("inbox_ack.py") && !aHead.Contains("tavern_query.py");

            // ② 歸檔＋讀回；重跑 no-op
            var r1 = SCP_TavernInbox.Ack(aTmp, "tavern", aWho);
            string aArc1 = File.Exists(aArchive) ? File.ReadAllText(aArchive) : "";
            bool aAck = r1.Error == null && r1.Existed && r1.Archived == 2 && r1.RemainingAfter == 0
                        && aArc1.Contains("[seq=11]") && aArc1.Contains("[seq=12]") && File.Exists(aInbox);
            var r2 = SCP_TavernInbox.Ack(aTmp, "tavern", aWho);
            bool aIdem = r2.Error == null && r2.Existed && r2.Archived == 0 && File.ReadAllText(aArchive) == aArc1;
            // ②' 清空後檔頭照樣在（只有新建檔才寫檔頭 ⇒ 清空時要自己補）；舊檔頭（python 指路）在歸檔時被換掉
            bool aHeadKept = File.ReadAllText(aInbox).Contains(aAckName + " --arg owner=" + aWho);
            string aOldInbox = SCP_TavernInbox.InboxPath(aTmp, "tavern", "old");
            File.WriteAllText(aOldInbox, "> 📥 **old** 的 inbox\n> 處理完跑 `inbox_ack.py` 歸檔；要看被截斷的全文跑 `tavern_query.py seq <N> --full`。\n\n## [seq=5] 💬 舊 (2026-01-01 00:00:00 +08)\n_at 2026-01-01T00:00:00.000Z_\n");
            var rOld = SCP_TavernInbox.Ack(aTmp, "tavern", "old");
            string aOldAfter = File.ReadAllText(aOldInbox);
            bool aOldHeadReplaced = rOld.Archived == 1 && !aOldAfter.Contains("inbox_ack.py") && aOldAfter.Contains(aAckName + " --arg owner=old");

            // ③ 打錯 owner；歸檔後再被 @
            var r3 = SCP_TavernInbox.Ack(aTmp, "tavern", "nobody");
            bool aMissing = !r3.Existed && r3.Error == null && !File.Exists(SCP_TavernInbox.ArchivePath(aTmp, "tavern", "nobody"));
            SCP_TavernInbox.Append(aTmp, "tavern", aWho, 13, "💬 c @妳", "歸檔後才來的");
            bool aAfter = File.ReadAllText(aInbox).Contains("[seq=13]") && !File.ReadAllText(aInbox).Contains("[seq=11]");

            // ④ all_rooms：tavern（seq 13）＋ hideout ＋ 另一房 demo，經 Cmd 入口
            SCP_TavernInbox.Append(aTmp, "hideout", aWho, 21, "💬 h @妳", "");
            SCP_TavernInbox.Append(aTmp, "demo", aWho, 31, "💬 d @妳", "");
            var aRes = SCP_CmdRegistry.Dispatch(aAckName, new Dictionary<string, string>(StringComparer.Ordinal)
                { ["data_root"] = aTmp, ["owner"] = aWho, ["all_rooms"] = "1" });
            string V(string k) => aRes.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";
            bool aAll = aRes.Ok && V("rooms") == "3" && V("archived") == "3" && V("failed_rooms") == "0"
                        && File.ReadAllText(SCP_TavernInbox.ArchivePath(aTmp, "demo", aWho)).Contains("[seq=31]");

            // 🔴 ⑤ 鎖被握著（模擬寫入端正在附加）⇒ Error、inbox 與 archive 位元組都不變
            SCP_TavernInbox.Append(aTmp, "tavern", aWho, 14, "💬 e @妳", "");
            string aInBefore = File.ReadAllText(aInbox), aArcBefore = File.ReadAllText(aArchive);
            SCP_InboxAckResult r5;
            using (new FileStream(aInbox + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                r5 = SCP_TavernInbox.Ack(aTmp, "tavern", aWho);
            bool aLock = r5.Error != null && r5.Error.StartsWith("沒動任何檔")
                         && File.ReadAllText(aInbox) == aInBefore && File.ReadAllText(aArchive) == aArcBefore;

            bool aOk = aHint && aAck && aIdem && aHeadKept && aOldHeadReplaced && aMissing && aAfter && aAll && aLock;
            return new CheckRow(aName,
                $"提示指向 `{aAckName}`／tavern-query={aHint}／歸檔 {r1.Archived} 筆（期望 2）讀回剩 {r1.RemainingAfter}={aAck}／重跑不重複={aIdem}"
                + $"／清空後檔頭還在={aHeadKept}、舊檔頭（python 指路）被換掉={aOldHeadReplaced}"
                + $"／打錯 owner 回「沒有」={aMissing}／歸檔後照常附加={aAfter}"
                + $"／all_rooms 房數 {V("rooms")}（期望 3）歸檔 {V("archived")}={aAll}／🔴 鎖被握著兩檔不動={aLock}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }
}
