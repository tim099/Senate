// 淨室驗收（TASK-0533）：Server 寫心跳時目標檔被別人開著 ⇒ 不掛、跳過這一拍。
// 同一個行程裡用 handle 就重現得出跨行程那一種撞法（共享模式跟著 handle 走）：
//   ① 反向對照：用 FileShare.Read（不給 Delete）開著心跳檔，舊的寫法 File.Move(tmp, path, overwrite) ⇒ 一定要丟例外（丟不出來 ＝ 這個淨室重現不了，判失敗）
//   ② 同一個 handle 一直開著，ServerHost.TryBeat ⇒ ⛔ 不丟、回一句原因；心跳檔內容不變、暫存檔收掉；這一拍 1 秒內回來（重試 BeatReplaceAttempts 次就放棄）
//   ③ handle 開著 60 ms 後放掉 ⇒ TryBeat 重試過得去、內容換成新的（5 次重試合計睡 150 ms）
//   ④ 讀的一側給 ReadWrite｜Delete（SCP_SharedFile.ReadAllText 的開法）開著 ⇒ TryBeat 一次就過（重試 0 次）
//   ⑤ 讀取端 thread 狂讀 ＋ 連寫 300 拍：兩輪都 ⛔ 不丟；新讀法（Delete 共享）0 拍寫不成、重試 0。
//     舊讀法（File.ReadAllText）那一輪只記讀數 —— 緊迴圈狂讀比真的讀取端兇得多，有上限的重試擋不保證（同一台機器量過 0／300 與 2／300）
//   ⑥ 不是 IO／權限的錯照丟（空路徑 ⇒ ArgumentException）；大聲說的間隔 ＝ 過期門檻那一拍
using System.Text;
using SCP.Core.Io;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow ServerBeatCleanRoom0533()
    {
        const string name = "Server 心跳換檔被擋（TASK-0533）：🔴 舊寫法被開著要丟／TryBeat 不丟且內容不變／放開後重試過得去／Delete 共享一次就過／狂讀＋連寫 0 拍失敗／別種錯照丟（淨室）";
        var failures = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        string dir = Path.Combine(Path.GetTempPath(), "senate-selftest-0533-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var readings = new List<string>();
        try
        {
            Directory.CreateDirectory(dir);
            string hb = Path.Combine(dir, "_server_heartbeat.json");
            string Beat(int i) => "{\"beat_at_utc\":\"" + i + "\",\"pid\":1}\n";
            File.WriteAllText(hb, Beat(0), new UTF8Encoding(false));

            // ① 反向對照：舊寫法
            string tmp0 = hb + ".tmp";
            File.WriteAllText(tmp0, Beat(1));
            string oldErr = "";
            using (new FileStream(hb, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { File.Move(tmp0, hb, overwrite: true); }
                catch (Exception e) { oldErr = e.GetType().Name; }
            }
            try { if (File.Exists(tmp0)) File.Delete(tmp0); } catch { }
            readings.Add("① 舊寫法被開著：" + (oldErr.Length > 0 ? oldErr : "沒丟"));
            Check(oldErr.Length > 0, "🔴 反向對照：FileShare.Read 開著心跳檔，舊寫法 File.Move(overwrite) 沒丟例外 ⇒ 這個淨室重現不了 Server 掛掉的那一格");
            File.WriteAllText(hb, Beat(0), new UTF8Encoding(false));

            // ② 一直開著：TryBeat 不丟、內容不變、暫存檔收掉
            string? err2 = null; Exception? thrown2 = null;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long describeMs;
            using (new FileStream(hb, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { err2 = ServerHost.TryBeat(hb, Beat(2)); }
                catch (Exception e) { thrown2 = e; }
                sw.Stop();
                var swD = System.Diagnostics.Stopwatch.StartNew();
                SCP_FileHolders.Describe(hb);
                describeMs = swD.ElapsedMilliseconds;
            }
            readings.Add("②b 問一次誰開著（Restart Manager）" + describeMs + " ms");
            readings.Add("② 一直開著：" + (thrown2 != null ? "丟了 " + thrown2.GetType().Name : err2 == null ? "寫成了" : "回原因") + "（" + sw.ElapsedMilliseconds + " ms）");
            Check(sw.ElapsedMilliseconds < 3000, "心跳檔一直被開著時，TryBeat 那一拍花了 " + sw.ElapsedMilliseconds + " ms 才回來（要低於過期門檻 4 秒、留 1 秒餘裕；預期約 150 ms 重試＋一次問誰開著，忙的時候後者量過 600 ms）");
            Check(thrown2 == null, "TryBeat 在心跳檔被開著時丟了例外：" + thrown2?.GetType().Name + "：" + thrown2?.Message);
            Check(err2 != null, "心跳檔一直被 FileShare.Read 開著，TryBeat 卻回報寫成了");
            Check(err2 == null || err2.Contains(hb), "TryBeat 回的原因沒帶心跳檔路徑：" + err2);
            Check(File.ReadAllText(hb) == Beat(0), "寫不成的那一拍改到了心跳檔內容");
            Check(!File.Exists(hb + ".tmp"), "寫不成的那一拍留下了暫存檔");

            // ③ 開著 150 ms 後放掉 ⇒ 重試過得去
            string? err3 = "未跑";
            using (var hold = new FileStream(hb, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var t = new Thread(() => { Thread.Sleep(60); hold.Dispose(); }) { IsBackground = true };
                t.Start();
                err3 = ServerHost.TryBeat(hb, Beat(3));
                t.Join(5000);
            }
            readings.Add("③ 60 ms 後放開：" + (err3 == null ? "寫成了" : "沒寫成"));
            Check(err3 == null, "心跳檔 60 ms 後放開，TryBeat 的重試仍沒過：" + err3);
            Check(File.ReadAllText(hb) == Beat(3), "重試過了，心跳檔內容卻不是新的那一拍");

            // ④ Delete 共享的讀取端開著 ⇒ 一次就過
            int retries4 = 0;
            void On4(string p, int a, int h, string who) { if (p == hb) Interlocked.Increment(ref retries4); }
            SCP_SharedFile.Contention += On4;
            string? err4;
            try
            {
                using (new FileStream(hb, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    err4 = ServerHost.TryBeat(hb, Beat(4));
            }
            finally { SCP_SharedFile.Contention -= On4; }
            readings.Add("④ Delete 共享開著：" + (err4 == null ? "寫成了" : "沒寫成") + "、重試 " + retries4);
            Check(err4 == null && retries4 == 0, "讀取端給了 Delete 共享，TryBeat 仍被擋（重試 " + retries4 + "）：" + err4);

            // ⑤ 狂讀 ＋ 連寫：舊讀法、新讀法
            (int Fail, int Retries, int Threw) Round(bool shared, int beats)
            {
                int fail = 0, retries = 0, threw = 0;
                void OnC(string p, int a, int h, string who) { if (p == hb) Interlocked.Increment(ref retries); }
                SCP_SharedFile.Contention += OnC;
                var stop = new ManualResetEventSlim(false);
                var reader = new Thread(() =>
                {
                    while (!stop.IsSet)
                    {
                        try { if (shared) SCP_SharedFile.ReadAllText(hb, 1); else File.ReadAllText(hb); }
                        catch (IOException) { }
                    }
                }) { IsBackground = true };
                reader.Start();
                try { for (int i = 0; i < beats; i++) { try { if (ServerHost.TryBeat(hb, Beat(100 + i)) != null) fail++; } catch (Exception) { threw++; } } }
                finally { stop.Set(); reader.Join(5000); SCP_SharedFile.Contention -= OnC; }
                return (fail, retries, threw);
            }
            var r5a = Round(false, 300);
            var r5b = Round(true, 300);
            readings.Add("⑤ 舊讀法狂讀：寫不成 " + r5a.Fail + "／300（重試 " + r5a.Retries + "）；新讀法：寫不成 " + r5b.Fail + "／300（重試 " + r5b.Retries + "）");
            Check(r5a.Threw == 0 && r5b.Threw == 0, "狂讀時 TryBeat 丟了例外（舊讀法 " + r5a.Threw + "、新讀法 " + r5b.Threw + " 拍）—— Server 會掛");
            Check(r5b.Fail == 0 && r5b.Retries == 0, "新讀法狂讀時仍有 " + r5b.Fail + " 拍寫不成、重試 " + r5b.Retries + " 次");

            // ⑥ 別種錯照丟；大聲說的間隔
            Exception? thrown6 = null;
            try { ServerHost.TryBeat("", "x"); } catch (Exception e) { thrown6 = e; }
            Check(thrown6 is ArgumentException, "空路徑應該照丟 ArgumentException，實際：" + (thrown6?.GetType().Name ?? "沒丟"));
            Check(ServerHost.BeatFailLoudEvery == 8, "大聲說的間隔應 ＝ 4 秒 ÷ 0.5 秒 ＝ 8，實際 " + ServerHost.BeatFailLoudEvery);
        }
        catch (Exception e) { failures.Add("例外：" + e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
        string reading = string.Join("／", readings);
        return new(name, failures.Count == 0 ? reading : string.Join("；", failures) + "｜" + reading, failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
