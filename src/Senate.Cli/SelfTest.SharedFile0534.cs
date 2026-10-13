// 淨室驗收（TASK-0534）：作品 json 的讀與換檔 —— SCP_SharedFile。
// 同一個行程裡兩條 thread 就重現得出跨行程那一種撞法（共享模式是跟著 handle 走的，不是跟著行程）：
//   ① 反向對照：舊的讀法（File.ReadAllText ＝ FileShare.Read）狂讀 ＋ 換檔不重試 ⇒ 一定要撞得到（撞不到 ＝ 壓測沒能力，這一格判失敗）
//   ② 同一個舊讀法 ＋ 換檔有上限地重試 ⇒ 0 筆失敗
//   ③ 新的讀法（ReadWrite｜Delete）狂讀 ＋ 換檔不重試 ⇒ 0 筆失敗（讀的人不擋換檔）
//   ④ 目標唯讀 ⇒ 一次就失敗（⛔ 不重試到放棄）、訊息帶目標路徑、目標內容不變
//   ⑤ 讀不存在的檔照丟 FileNotFoundException（⛔ 不吞成空字串）；有 BOM 的檔讀出來跟 File.ReadAllText 一樣
//   ⑥ 哪些錯算「一瞬間被擋」：32／1175 是；不存在、唯讀不是
using System.Text;
using SCP.Core.Io;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SharedFileCleanRoom()
    {
        const string name = "作品 json 換檔（TASK-0534）：🔴 舊讀法＋不重試要撞得到／舊讀法＋重試 0 失敗／新讀法＋不重試 0 失敗／唯讀一次就失敗且帶路徑／被開著時指名誰（Restart Manager）／不存在照丟、BOM 一致／暫時性錯誤的分類（淨室）";
        var failures = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        string dir = Path.Combine(Path.GetTempPath(), "senate-selftest-0534-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var readings = new List<string>();
        try
        {
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, "ropes.json");
            string body = "{\n  \"schema\": 1,\n  \"ropes\": [" + string.Join(",", Enumerable.Range(0, 400).Select(i => "{\"name\":\"r" + i + "\"}")) + "]\n}\n";
            File.WriteAllText(target, body, new UTF8Encoding(false));

            // 一輪：讀取端 thread 狂讀到 writes 寫完；回（換檔失敗幾筆、重試總次數）
            (int Fail, int Retries) Round(int writes, bool sharedReader, int attempts)
            {
                int fail = 0, retries = 0;
                void OnC(string p, int a, int h, string who) { if (p == target) Interlocked.Increment(ref retries); }
                SCP_SharedFile.Contention += OnC;
                var stop = new ManualResetEventSlim(false);
                var reader = new Thread(() =>
                {
                    while (!stop.IsSet)
                    {
                        try { if (sharedReader) SCP_SharedFile.ReadAllText(target, 1); else File.ReadAllText(target, new UTF8Encoding(false)); }
                        catch (IOException) { }   // 讀取端被換檔擋到是另一回事（⑤ 量讀的那一側），這裡只量寫
                    }
                }) { IsBackground = true };
                reader.Start();
                try
                {
                    for (int i = 0; i < writes; i++)
                    {
                        string tmp = target + "." + i + ".tmp";
                        File.WriteAllText(tmp, body, new UTF8Encoding(false));
                        try { SCP_SharedFile.Replace(tmp, target, attempts); }
                        catch (IOException) { fail++; }
                        finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                    }
                }
                finally { stop.Set(); reader.Join(5000); SCP_SharedFile.Contention -= OnC; }
                return (fail, retries);
            }

            var a = Round(400, false, 1);
            readings.Add("① 舊讀法＋不重試 撞 " + a.Fail + "／400");
            Check(a.Fail > 0, "🔴 反向對照：舊讀法狂讀、換檔不重試，400 筆一筆都沒撞到 ⇒ 這個壓測量不出差別");
            var b = Round(400, false, SCP_SharedFile.DefaultReplaceAttempts);
            readings.Add("② 舊讀法＋重試 失敗 " + b.Fail + "（重試 " + b.Retries + " 次）");
            Check(b.Fail == 0, "舊讀法＋重試仍有 " + b.Fail + " 筆換檔失敗");
            var c = Round(400, true, 1);
            readings.Add("③ 新讀法＋不重試 撞 " + c.Fail);
            Check(c.Fail == 0, "新讀法（ReadWrite｜Delete）狂讀時，不重試的換檔仍撞 " + c.Fail + " 筆");

            // ④ 唯讀：一次就失敗、帶路徑、內容不變
            string ro = Path.Combine(dir, "models.json");
            File.WriteAllText(ro, "old\n", new UTF8Encoding(false));
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            string roTmp = ro + ".tmp";
            File.WriteAllText(roTmp, "new\n", new UTF8Encoding(false));
            int roRetries = 0;
            void OnRo(string p, int at, int h, string who) { if (p == ro) roRetries++; }
            SCP_SharedFile.Contention += OnRo;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            string? roMsg = null;
            try { SCP_SharedFile.Replace(roTmp, ro); }
            catch (IOException e) { roMsg = e.Message; }
            finally { SCP_SharedFile.Contention -= OnRo; }
            sw.Stop();
            File.SetAttributes(ro, FileAttributes.Normal);
            readings.Add("④ 唯讀 " + (roMsg == null ? "沒失敗" : "失敗") + "、重試 " + roRetries + "、" + sw.ElapsedMilliseconds + " ms");
            Check(roMsg != null, "目標唯讀，換檔應該失敗");
            Check(roMsg == null || roMsg.Contains(ro), "失敗訊息沒有目標路徑：" + roMsg);
            Check(roRetries == 0 && sw.ElapsedMilliseconds < 500, "唯讀不該重試（重試 " + roRetries + " 次、" + sw.ElapsedMilliseconds + " ms）");
            Check(File.ReadAllText(ro) == "old\n", "唯讀換檔失敗後，目標內容變了");

            // ④b 指名：一條 thread 把目標開著不放（FileShare.Read）⇒ 換檔失敗的訊息要寫出「當下開著的」是本行程（pid 對得上）；
            //     反向對照：沒人開著時問 ⇒ 回「問的時候已經沒人開著」（⛔ 不能編一個名字出來）
            string held = Path.Combine(dir, "links.json");
            File.WriteAllText(held, "old" + Environment.NewLine, new UTF8Encoding(false));
            string heldTmp = held + ".tmp";
            File.WriteAllText(heldTmp, "new" + Environment.NewLine, new UTF8Encoding(false));
            string? heldMsg = null, firstWho = null;
            void OnHeld(string p, int at, int h, string who) { if (p == held && at == 1) firstWho = who; }
            SCP_SharedFile.Contention += OnHeld;
            using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try { SCP_SharedFile.Replace(heldTmp, held, 2); }
                catch (IOException e) { heldMsg = e.Message; }
            }
            SCP_SharedFile.Contention -= OnHeld;
            string myPid = "pid " + Environment.ProcessId;
            readings.Add("④b 被開著時指名 " + (heldMsg != null && heldMsg.Contains(myPid) ? "對" : "錯"));
            Check(heldMsg != null && heldMsg.Contains(myPid), "被本行程開著時，失敗訊息應指名 " + myPid + "：" + heldMsg);
            Check(firstWho != null && firstWho.Contains(myPid), "第一次被擋的讀數應指名 " + myPid + "：" + firstWho);
            Check(SCP_FileHolders.Describe(held) == SCP_FileHolders.NobodyNow, "沒人開著時應回「沒人」：" + SCP_FileHolders.Describe(held));
            try { File.Delete(heldTmp); } catch { }

            // ⑤ 不存在照丟；BOM 跟 File.ReadAllText 一樣
            bool threw = false;
            try { SCP_SharedFile.ReadAllText(Path.Combine(dir, "nope.json")); } catch (FileNotFoundException) { threw = true; }
            Check(threw, "讀不存在的檔應丟 FileNotFoundException");
            string bom = Path.Combine(dir, "bom.json");
            File.WriteAllText(bom, "{\"甲\":1}\n", new UTF8Encoding(true));
            Check(SCP_SharedFile.ReadAllText(bom) == File.ReadAllText(bom, new UTF8Encoding(false)), "有 BOM 的檔讀出來跟 File.ReadAllText 不一樣");

            // ⑥ 分類
            Check(SCP_SharedFile.IsTransient(new IOException("x", unchecked((int)0x80070020))), "共享衝突 32 應算暫時性");
            Check(SCP_SharedFile.IsTransient(new IOException("x", unchecked((int)0x80070497))), "1175（無法移除要被取代的檔案）應算暫時性");
            Check(!SCP_SharedFile.IsTransient(new FileNotFoundException("x")), "不存在不該算暫時性");
            Check(!SCP_SharedFile.IsTransient(new UnauthorizedAccessException("x")), "權限不該算暫時性");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + ": " + e.Message); }
        finally { try { foreach (var f in Directory.GetFiles(dir)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(dir, true); } catch { } }
        string reading = string.Join("／", readings);
        return new(name, failures.Count == 0 ? reading : string.Join("；", failures) + "｜" + reading, failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
