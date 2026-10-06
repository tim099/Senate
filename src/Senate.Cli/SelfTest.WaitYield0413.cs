// 區塊職責：`tavern-wait` 遇到出廠會讓路的自我對拍（TASK-0413）。
// 物理意義：等待中的 tavern-wait 握著 publish/senate.exe；不讓路的話出廠會被擋、或被 build.sh 直接 Kill，
//           而被 Kill 的那位拿到的是一個說不出原因的中斷。每一格驗一個「錯了也不會叫」的地方：
//           ① 對照：沒有旗標 ⇒ 照常逾時（exit 4）
//           ② 開始前就在出廠 ⇒ 不等（exit 5），回傳給一行帶 from_seq 的重開指令
//           🔴 ③ 等到一半出現旗標 ⇒ 下一輪就讓路（exit 5），⛔ 不是等到逾時
//           ④ 過期的旗標（build.sh 半路死掉留下的）⇒ 不讓路（判準與 BuildGuard 同一份）
//           ⑤ 同一輪裡有人回話也有旗標 ⇒ 回話優先（那一則不會丟）
// 數值影響：temp 目錄造資料根與旗標，跑完刪；BuildFlagPath 跑完還原。⛔ 不碰真實 repo 的旗標。約 6 秒。
#nullable enable
using SCP.Core.Cmd;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TavernWaitYieldsToBuild()
    {
        const string aName = "tavern-wait 出廠讓路：沒旗標照常逾時／開始前在出廠不等／等到一半出現旗標就讓路（exit 5）／過期旗標不讓路／同一輪回話優先（淨室，TASK-0413）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_waityield_" + Guid.NewGuid().ToString("N")[..8]);
        Func<string?> aSaved = Cmd_TavernWait.BuildFlagPath;
        try
        {
            string aRoom = Path.Combine(aTmp, "ChatTavern", "rooms", "tavern");
            string aMsgDir = Path.Combine(aRoom, "messages", "2026-01-01");
            Directory.CreateDirectory(aMsgDir);
            string aSeqFile = Path.Combine(aRoom, "_seq.txt");
            File.WriteAllText(aSeqFile, "10");
            string aFlag = Path.Combine(aTmp, "_build_in_progress.flag");
            Cmd_TavernWait.BuildFlagPath = () => aFlag;

            SCP_CmdResult Wait(string iTimeout, string iPoll = "0.5") =>
                SCP_CmdRegistry.Dispatch("tavern-wait", new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["data_root"] = aTmp, ["persona"] = "probe", ["timeout"] = iTimeout, ["poll"] = iPoll, ["heartbeat"] = "0" });
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";
            string Text(SCP_CmdResult r) => string.Join("\n", r.Lines);

            // ① 對照
            var r1 = Wait("1");
            bool aControl = r1.ExitCode == 4 && V(r1, "interrupted") == "";

            // ② 開始前就在出廠
            File.WriteAllText(aFlag, "x");
            var r2 = Wait("30");
            bool aBefore = r2.ExitCode == Cmd_TavernWait.ExitBuildYield && V(r2, "interrupted") == "build"
                           && long.Parse(V(r2, "waited_ms")) < 2000 && Text(r2).Contains("--arg from_seq=10") && Text(r2).Contains("不是");
            File.Delete(aFlag);

            // 🔴 ③ 等到一半出現旗標
            var aT3 = new Thread(() => { Thread.Sleep(1200); File.WriteAllText(aFlag, "x"); });
            aT3.Start();
            var r3 = Wait("30");
            aT3.Join();
            long aMs3 = long.Parse(V(r3, "waited_ms") is { Length: > 0 } s3 ? s3 : "-1");
            bool aMid = r3.ExitCode == Cmd_TavernWait.ExitBuildYield && aMs3 >= 1000 && aMs3 < 5000 && Text(r3).Contains("讓路");
            File.Delete(aFlag);

            // ④ 過期旗標
            File.WriteAllText(aFlag, "x");
            File.SetLastWriteTimeUtc(aFlag, DateTime.UtcNow - BuildGuard.StaleAfter - TimeSpan.FromMinutes(1));
            var r4 = Wait("1");
            bool aStale = r4.ExitCode == 4;
            File.Delete(aFlag);

            // ⑤ 同一輪：一則 @ 我的訊息與旗標同時出現 ⇒ 回話優先
            var aT5 = new Thread(() =>
            {
                Thread.Sleep(1000);
                File.WriteAllText(Path.Combine(aMsgDir, "00000011.json"),
                    "{\"seq\":11,\"ts\":\"2026-01-01T00:00:11.000Z\",\"sender_persona\":\"other\",\"sender_id\":\"other\",\"kind\":\"chat\",\"body\":\"@probe 在嗎\"}");
                File.WriteAllText(aFlag, "x");
                File.WriteAllText(aSeqFile, "11");
            });
            aT5.Start();
            var r5 = Wait("30", "2");
            aT5.Join();
            bool aReplyFirst = r5.ExitCode == 0 && V(r5, "replied") == "1" && V(r5, "hit_seq") == "11";

            bool aOk = aControl && aBefore && aMid && aStale && aReplyFirst;
            return new CheckRow(aName,
                $"沒旗標 exit {r1.ExitCode}（期望 4）={aControl}／開始前在出廠 exit {r2.ExitCode}（期望 5）且給重開指令={aBefore}"
                + $"／🔴 等到一半出現旗標 exit {r3.ExitCode}、等了 {aMs3}ms（期望 5、1～5 秒）={aMid}"
                + $"／過期旗標不讓路 exit {r4.ExitCode}={aStale}／同一輪回話優先 exit {r5.ExitCode} hit={V(r5, "hit_seq")}={aReplyFirst}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally
        {
            Cmd_TavernWait.BuildFlagPath = aSaved;
            try { Directory.Delete(aTmp, true); } catch (Exception) { }
        }
    }
}
