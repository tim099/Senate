// 淨室驗收（TASK-0565）：SCP_ServerCmdClient 寫 queue.json 的換檔 —— 先 Delete 再 Move 那一格「不存在」。
//   ① 反向對照：照舊寫法的原語（tmp 寫好 → Delete queue → 行程死在這裡）⇒ queue.json 一定要不存在、資料只在 tmp；
//      下一次 Submit 從空骨架寫回 ⇒ 原本排著的 N 筆**全沒了**（重現不出來 ＝ 這一格什麼都沒證明，判失敗）
//   ② 新寫法死在同一個點（換檔之前丟例外，SCP_ServerCmdClient.BeforeQueueSwap）⇒ queue.json 一直在、還是舊版那 N 筆、沒有 tmp 殘檔；
//      下一次 Submit ⇒ N＋1
//   ③ 多個寫入端（同一把鎖）同時 Submit ⇒ 每一筆都在、一筆都沒被洗掉
// ⚠ 射程：本格量的是**這一支**寫入端（SCP_ServerCmdClient，Unity 與 SCP_Core 的呼叫端走它）；
//   Senate.Core 的 AgentCmdClient.SaveQueue 是 File.Move(overwrite) 一步換檔、ServerExecutor 走 SCP_SharedFile.Replace，兩支不在本格裡。
using System.Text;
using SCP.Core.Proc;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow QueueSwapNoMissingWindow0565()
    {
        const string name = "queue.json 換檔沒有「不存在」那一格（TASK-0565）：🔴 舊寫法死在 Delete 與 Move 之間要洗掉排著的指令／新寫法死在同一點 queue 原封不動／多個寫入端同時送一筆都不少（淨室）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        string root = Path.Combine(Path.GetTempPath(), "senate-selftest-0565-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        const string lane = "selftest0565";
        const int pre = 5;
        try
        {
            int Count(string q) => File.Exists(q) ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(q))!["Commands"]!.AsArray().Count : -1;
            string[] Tmps(string q) => Directory.GetFiles(Path.GetDirectoryName(q)!, Path.GetFileName(q) + ".tmp*");
            var args = new Dictionary<string, string> { ["k"] = "v" };

            // ── ① 反向對照：舊寫法的原語，死在 Delete 之後 ──
            string rootOld = Path.Combine(root, "old");
            string qOld = SCP_ServerCmdClient.QueuePath(rootOld, lane);
            for (int i = 0; i < pre; i++) SCP_ServerCmdClient.Submit(rootOld, lane, "SelfTest", args);
            int beforeOld = Count(qOld);
            {
                string payload = File.ReadAllText(qOld);          // 下一次寫回的內容（這裡用原樣，量的是換檔那一格，不是內容）
                string tmp = qOld + ".tmp.old000";
                File.WriteAllText(tmp, payload, new UTF8Encoding(false));
                if (File.Exists(qOld)) File.Delete(qOld);
                // ← 舊版 WriteAtomic 的下一行是 File.Move(tmp, queue)；行程死在這裡
            }
            bool oldMissing = !File.Exists(qOld);
            bool oldTmpHasData = Tmps(qOld).Length == 1;
            SCP_ServerCmdClient.Submit(rootOld, lane, "SelfTest", args);
            int afterOld = Count(qOld);
            bool oldLost = afterOld == 1;
            Check(beforeOld == pre, $"① 前置：先送 {pre} 筆，queue 裡是 {beforeOld}");
            Check(oldMissing, "① 舊寫法死在 Delete 之後，queue.json 應該不存在（重現不出來 ⇒ 這一格沒有證明力）");
            Check(oldTmpHasData, "① 舊寫法死在中間，資料應該只在 tmp（tmp 檔數 " + Tmps(qOld).Length + "）");
            Check(oldLost, $"① 舊寫法：下一次 Submit 從空骨架寫回，應該只剩 1 筆（實際 {afterOld}）");
            readings.Add($"① 舊寫法死在中間：queue 不存在 {oldMissing}、tmp {Tmps(qOld).Length} 份；下一次 Submit 之後 {beforeOld} → **{afterOld}** 筆（排著的 {beforeOld} 筆被洗掉）");

            // ── ② 新寫法死在同一個點 ──
            string rootNew = Path.Combine(root, "new");
            string qNew = SCP_ServerCmdClient.QueuePath(rootNew, lane);
            for (int i = 0; i < pre; i++) SCP_ServerCmdClient.Submit(rootNew, lane, "SelfTest", args);
            int beforeNew = Count(qNew);
            bool threw = false;
            SCP_ServerCmdClient.BeforeQueueSwap = p => { if (p == qNew) throw new IOException("selftest：模擬行程死在換檔之前"); };
            try { SCP_ServerCmdClient.Submit(rootNew, lane, "SelfTest", args); }
            catch (IOException) { threw = true; }
            finally { SCP_ServerCmdClient.BeforeQueueSwap = null; }
            bool newExists = File.Exists(qNew);
            int midNew = Count(qNew);
            int tmpLeft = Tmps(qNew).Length;
            SCP_ServerCmdClient.Submit(rootNew, lane, "SelfTest", args);
            int afterNew = Count(qNew);
            Check(threw, "② 縫沒有被叫到（BeforeQueueSwap 沒丟出來 ⇒ 量的不是換檔那一格）");
            Check(newExists && midNew == beforeNew, $"② 新寫法死在換檔之前：queue 應該原封不動 {beforeNew} 筆（存在 {newExists}、{midNew} 筆）");
            Check(tmpLeft == 0, $"② tmp 殘檔應該清掉（剩 {tmpLeft}）");
            Check(afterNew == beforeNew + 1, $"② 下一次 Submit 應該是 {beforeNew + 1} 筆（實際 {afterNew}）");
            readings.Add($"② 新寫法死在換檔之前：queue 存在 {newExists}、{midNew} 筆、tmp 殘檔 {tmpLeft}；下一次 Submit 之後 **{afterNew}** 筆");

            // ── ③ 多個寫入端同時送 ──
            string rootRace = Path.Combine(root, "race");
            string qRace = SCP_ServerCmdClient.QueuePath(rootRace, lane);
            const int threads = 4, per = 15;
            var errs = new System.Collections.Concurrent.ConcurrentBag<string>();
            var ids = new System.Collections.Concurrent.ConcurrentBag<string>();
            var list = new List<Thread>();
            for (int t = 0; t < threads; t++)
                list.Add(new Thread(() =>
                {
                    for (int i = 0; i < per; i++)
                    {
                        try { ids.Add(SCP_ServerCmdClient.Submit(rootRace, lane, "SelfTest", args)); }
                        catch (Exception e) { errs.Add(e.GetType().Name + "：" + e.Message); }
                    }
                }));
            foreach (var th in list) th.Start();
            foreach (var th in list) th.Join();
            string raceText = File.ReadAllText(qRace);
            int landed = ids.Count(id => raceText.Contains(id));
            Check(errs.IsEmpty, "③ Submit 丟了例外：" + string.Join("｜", errs.Take(3)));
            Check(landed == threads * per && Count(qRace) == threads * per, $"③ 送出 {threads * per}，queue 裡找得到 {landed}、共 {Count(qRace)} 筆");
            readings.Add($"③ {threads} 條執行緒 × {per} 筆同時送：queue 裡 **{landed}／{threads * per}**、例外 {errs.Count}");
        }
        catch (Exception e) { failures.Add("例外：" + e.GetType().Name + "：" + e.Message); }
        finally
        {
            SCP_ServerCmdClient.BeforeQueueSwap = null;
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
        string reading = string.Join("／", readings) + (failures.Count == 0 ? "" : "　✗ " + string.Join("；", failures));
        return new CheckRow(name, reading, failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
