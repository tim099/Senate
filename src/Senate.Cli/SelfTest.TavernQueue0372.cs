// 區塊職責：TASK-0372（酒館 Server 不在時發文排進它的 queue）的自我對拍。
// 物理意義：三格各驗一個「錯了也不會叫」的地方：
//           🔴 ① 端到端：拋棄式 repo 根上量不到可用的 Server ⇒ WriteOrQueue 回 queued=1、exit 0、**沒有 seq**，
//              而那一筆真的落在**酒館** Server 的 queue（lane=tavern）、trigger 在、msg_json 原封、timeout 沒被送進去。
//           ② 判定第四態：Queued 跟 Posted／NotPosted／Unresolved 兩兩不同、`Posted` 是 false、帶得出 cmd_id。
//           ③ 反向：寫入端給了 seq ⇒ 原樣回、不標 queued（IsQueued 只認自己那個值）。
// ⚠ 射程（2026-10-02 實測）：線上兩顆 Server 開著時，這一格走的是 `build_unknown`（登記表看得到那顆活著的酒館 Server、
//   拋棄式根裡沒有它的心跳）⇒ **`not_running`／autostart 那幾條沒有在這裡走到**；它們的分類由 TASK-0297 那格驗、
//   活體由「停掉酒館 Server 再對 exe 發一則」驗。⛔ 別把這一格讀成「Server 不在」那條路的讀數。
// 數值影響：RepoRootProvider 暫時指到 temp ⇒ 寫的是**假的** Server 根，⛔ 不碰線上兩顆 Server 的 queue；跑完刪。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Letters;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TavernQueueWhenServerDownCleanRoom()
    {
        const string aName = "酒館 Server 用不了 ⇒ 發文排進它的 queue：queued=1／沒有 seq／落在 tavern lane／判定第四態不同形（淨室，TASK-0372）";
        var aFails = new List<string>();
        Func<string>? aSaved = ServerDelegateCmd.RepoRootProvider;
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_tq_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aRoot);
            ServerDelegateCmd.RepoRootProvider = () => aRoot;
            const string aProbe = "probe-0372-body";
            var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["data_root"] = Path.Combine(aRoot, "AgentCommands"),
                ["room"] = "tavern",
                ["msg_json"] = "{\"sender_persona\":\"Template\",\"sender_id\":\"Template\",\"kind\":\"chat\",\"body\":\"" + aProbe + "\"}",
                ["timeout"] = "1",
            };

            // 🔴 ① 端到端
            SCP_CmdResult aR = SenateTavernWrite.WriteOrQueue(aArgs);
            string aCmdId = SenateTavernWrite.Value(aR, "queued_cmd_id");
            string aWhy = SenateTavernWrite.Value(aR, "queued_because");
            if (!SenateTavernWrite.IsQueued(aR)) aFails.Add($"沒排隊（exit {aR.ExitCode}，delegate_failure={SenateTavernWrite.Value(aR, "delegate_failure")}）");
            if (aR.ExitCode != 0) aFails.Add("排隊卻不是 exit 0（呼叫端會把它讀成失敗然後補發）");
            if (SenateTavernWrite.Value(aR, "seq").Length > 0) aFails.Add("排隊卻有 seq（那會被讀成已發）");
            if (!ServerDelegateCmd.ShouldQueueForLater(aWhy)) aFails.Add($"queued_because='{aWhy}' 不在可排清單裡");
            string aServerRoot = SenatePaths.ServerRoot(aRoot, SCP.Core.Proc.SCP_ServerIds.Tavern);
            string aQueue = AgentCmdClient.QueuePath(aServerRoot, Cmd_TavernWrite.TavernLane);
            string aTrigger = AgentCmdClient.TriggerPath(aServerRoot, Cmd_TavernWrite.TavernLane);
            string aText = File.Exists(aQueue) ? File.ReadAllText(aQueue) : "";
            if (aCmdId.Length == 0 || !aText.Contains(aCmdId)) aFails.Add("酒館 Server 的 tavern lane queue 裡找不到這一筆");
            if (!aText.Contains(aProbe)) aFails.Add("queue 裡的 msg_json 不是原封那一份");
            if (aText.Contains("\"timeout\"")) aFails.Add("timeout（CLI 端參數）被送進 queue 了");
            if (!File.Exists(aTrigger)) aFails.Add("pending.trigger 不在 ⇒ Server 起來不會接手");

            // ② 判定第四態
            SCP_TavernPostVerdict aQ = SCP_TavernPostVerdict.Queued("probe", "cmd-x");
            var aOthers = new[] { SCP_TavernPostVerdict.Good("g", "1"), SCP_TavernPostVerdict.Bad("b"), SCP_TavernPostVerdict.Unknown("u", "h") };
            if (aQ.Outcome != SCP_TavernPostOutcome.Queued || aQ.Posted || aQ.Seq.Length > 0 || aQ.QueuedCmdId != "cmd-x")
                aFails.Add("Queued 判定形狀不對");
            foreach (var o in aOthers)
                if (o.Outcome == aQ.Outcome || o.QueuedCmdId.Length > 0) aFails.Add("Queued 跟 " + o.Outcome + " 同形");

            // ③ 反向：有 seq 的結果不會被標成 queued
            var aGood = new SCP_CmdResult();
            aGood.AddValue("seq", "42");
            if (SenateTavernWrite.IsQueued(aGood)) aFails.Add("有 seq 的結果被判成已排隊");

            return new CheckRow(aName,
                aFails.Count == 0
                    ? $"🔴 端到端排隊（because={aWhy}、exit 0、無 seq、tavern lane 有這一筆、trigger 在、msg 原封、不帶 timeout）＋第四態兩兩不同形＋反向 全過"
                    : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally
        {
            ServerDelegateCmd.RepoRootProvider = aSaved;
            try { Directory.Delete(aRoot, true); } catch { }
        }
    }
}
