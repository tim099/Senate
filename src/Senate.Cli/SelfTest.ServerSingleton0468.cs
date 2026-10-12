// 區塊職責：資料根層級的 Server 單例（TASK-0468）—— 兩個暫存 repo 根指向同一棵暫存資料根。
// 物理意義：2026-10-08 一份 Senate 副本在同一棵資料根上起了第二顆 tavern ⇒ Discord 雙送、Inbound 互搶游標。
//           原本的單例鎖住在各自 repo 的 runtime，互相看不到。本格量「住在資料根上的那幾道」：
//           鎖擋得住、擋下時說得出握著的是誰、名片不蓋活的、只刪自己的、名片主人轉換只出聲一次。
// 數值影響：只寫暫存樹；鎖是真的獨佔開檔（同一 process 內第二個 FileShare.None 一樣拿不到）。
//           ⚠ 真的起兩顆 Server process 的那次另外量（見 TASK-0468 留言）。
using System.Globalization;
using System.Text;
using Senate.Core;
using SCP.Core.Proc;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow DataRootServerSingleton0468()
    {
        const string aName = "資料根層級的 Server 單例（同根同 id 擋、名片不蓋活的、只刪自己的）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_singleton0468_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        var aReads = new List<string>();
        void Check(bool iOk, string iWhy) { if (!iOk) aFails.Add(iWhy); }
        FileStream? aLockA = null, aLockB = null, aLockProbe = null, aLockOther = null;
        try
        {
            string aData = Path.Combine(aTmp, "data").Replace('\\', '/');
            string aOther = Path.Combine(aTmp, "data-test").Replace('\\', '/');
            string aRepoA = Path.Combine(aTmp, "repoA").Replace('\\', '/');
            string aRepoB = Path.Combine(aTmp, "repoB").Replace('\\', '/');
            foreach (string d in new[] { aData, aOther, aRepoA + "/SenateData/runtime", aRepoB + "/SenateData/runtime" })
                Directory.CreateDirectory(d);
            const string aId = "tavern";

            void Beat(string iHb, double iAgoSeconds)
            {
                string aAt = DateTime.UtcNow.AddSeconds(-iAgoSeconds).ToString("o", CultureInfo.InvariantCulture);
                File.WriteAllText(iHb, "{\"beat_at_utc\":\"" + aAt + "\"}", new UTF8Encoding(false));
            }
            SCP_ServerEndpointInfo Card(string iRepo, int iPid) => new SCP_ServerEndpointInfo
            {
                ServerId = aId, Pid = iPid, BuildId = "test",
                StartedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ServerRoot = iRepo + "/SenateData/runtime/server-tavern",
                HeartbeatPath = iRepo + "/SenateData/runtime/_server_heartbeat.tavern.json",
                StaleSeconds = 4.0, RepoRoot = iRepo,
            };

            // ① repoA 先起：拿鎖、心跳、寫名片
            aLockA = SCP_ServerEndpoint.TryAcquireLock(aData, aId, 300, out string aWhyA);
            SCP_ServerEndpointInfo aCardA = Card(aRepoA, 11111);
            Beat(aCardA.HeartbeatPath, 0.1);
            var (aResA, _) = SCP_ServerEndpoint.Write(aData, aCardA);
            Check(aLockA != null && aResA == SCP_EndpointWrite.Written, "① repoA 拿不到鎖或寫不了名片：" + aWhyA);

            // ② repoB 同根同 id ⇒ 擋下，而且說得出握著的是誰（pid＋repo 根）
            aLockB = SCP_ServerEndpoint.TryAcquireLock(aData, aId, 300, out string aWhyB);
            Check(aLockB == null, "② repoB 同根同 id 竟然拿到鎖");
            Check(aWhyB.Contains("pid=11111") && aWhyB.Contains(aRepoA), "② 擋下的訊息沒有指出握著的 pid／repo 根：" + aWhyB);
            aReads.Add("② 擋下：" + aWhyB);

            // ③ 名片不蓋活的：repoB 的名片（另一個 pid）寫不上去，檔案一個位元組都沒變
            string aCardPath = SCP_ServerEndpoint.PathFor(aData, aId);
            string aBefore = File.ReadAllText(aCardPath);
            SCP_ServerEndpointInfo aCardB = Card(aRepoB, 22222);
            Beat(aCardB.HeartbeatPath, 0.1);
            var (aResB, aMsgB) = SCP_ServerEndpoint.Write(aData, aCardB);
            Check(aResB == SCP_EndpointWrite.RefusedOtherAlive && File.ReadAllText(aCardPath) == aBefore,
                  "③ 活著的名片被蓋了（" + aResB + "）");

            // ④ 只刪自己的：repoB 收工刪不掉 repoA 的名片
            SCP_ServerEndpoint.Delete(aData, aId, aCardB.Pid, aCardB.StartedAtUtc);
            Check(File.Exists(aCardPath) && File.ReadAllText(aCardPath) == aBefore, "④ 別顆的名片被刪了");

            // ⑤ 名片主人：repoB 不是主人 ⇒ 停手，連量兩次只出聲一次
            var aState = new ServerHost.CardOwnerState { IsOwner = true };
            int aWarn = 0;
            ServerHost.CheckCardOwner(aData, aCardB, aState, _ => { }, _ => aWarn++);
            ServerHost.CheckCardOwner(aData, aCardB, aState, _ => { }, _ => aWarn++);
            Check(!aState.IsOwner && aWarn == 1, $"⑤ 名片主人判錯或警告次數不是 1（owner={aState.IsOwner}，警告 {aWarn} 次）");

            // ⑥ 換一個 id（測試用）⇒ 放行；⑦ 同 id 指另一棵資料根 ⇒ 放行
            aLockProbe = SCP_ServerEndpoint.TryAcquireLock(aData, "probe", 300, out _);
            aLockOther = SCP_ServerEndpoint.TryAcquireLock(aOther, aId, 300, out _);
            Check(aLockProbe != null, "⑥ 同根換 id 被擋了");
            Check(aLockOther != null, "⑦ 另一棵資料根同 id 被擋了");

            // ⑧ repoA 死掉（放鎖、心跳過期）⇒ repoB 拿得到鎖、蓋得掉遺體、成為主人
            aLockA!.Dispose(); aLockA = null;
            Beat(aCardA.HeartbeatPath, 30);
            aLockB = SCP_ServerEndpoint.TryAcquireLock(aData, aId, 300, out string aWhyB2);
            ServerHost.CheckCardOwner(aData, aCardB, aState, _ => { }, _ => { });
            Check(aLockB != null && aState.IsOwner && ServerHost.IsCardMine(aData, aCardB),
                  "⑧ 前一顆死了之後接不了手：" + aWhyB2);

            // ⑨ 管理頁的讀數：資料根上的全部名片，repo 根看得到是 repoB
            var aAll = SCP_ServerEndpoint.ProbeAll(aData);
            Check(aAll.Count == 1 && aAll[0].ServerId == aId && aAll[0].Probe.Info?.RepoRoot == aRepoB,
                  "⑨ ProbeAll 讀數不對：" + string.Join("、", aAll.ConvertAll(c => c.ServerId + "@" + c.Probe.Info?.RepoRoot)));

            // ⑩ 舊名片（schema 1，沒有 repo_root）⇒ 由 server_root 推回 repo 根
            File.WriteAllText(SCP_ServerEndpoint.PathFor(aData, "old"),
                "{\"server_id\":\"old\",\"pid\":1,\"server_root\":\"D:/X/Senate/SenateData/runtime/server\",\"heartbeat_path\":\"\"}");
            var aOld = SCP_ServerEndpoint.Probe(aData, "old");
            Check(aOld.Info?.RepoRoot == "D:/X/Senate", "⑩ 舊名片推不回 repo 根：" + aOld.Info?.RepoRoot);

            aReads.Add("③ " + aMsgB);
            string aReading = (aFails.Count == 0 ? "①～⑩ 全過：" : "✗ " + string.Join("／", aFails) + "　讀數：")
                              + string.Join("　", aReads);
            return new CheckRow(aName, aReading, aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + "：" + e.Message, CheckResult.Fail); }
        finally
        {
            foreach (FileStream? f in new[] { aLockA, aLockB, aLockProbe, aLockOther }) f?.Dispose();
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { }
        }
    }
}
