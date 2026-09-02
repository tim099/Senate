// 區塊職責：`senate server start|stop|status` 的 CLI 半邊 —— 解析動詞、印讀數、翻 exit code。
// 物理意義：生命週期本體在 Senate.Core/ServerHost（零 Console 依賴，之後 GUI 要接同一份）。
//           這裡只做「人打了什麼」→「呼叫哪一支」→「印成什麼樣」。
// 數值影響：exit code —— start：0 正常退出／1 已有 Server 或身分驗不出／70 登記不了；
//           stop：0 停掉或本來就沒在跑／1 停不掉；status：0 活著且心跳新鮮／3 沒在跑或心跳停了。
//           ⚠ status 的 3 對齊委派 Cmd「沒有結果」那格（腳本已經在吃 0/1/2/3 四格，不發明第五格）。
using Senate.Core;

namespace Senate.Cli;

static class ServerCommand
{
    public static int Run(string iRepoRoot, string[] iArgs)
    {
        string aSub = iArgs.Length > 1 ? iArgs[1].ToLowerInvariant() : "";
        switch (aSub)
        {
            case "start": return ServerHost.RunForeground(iRepoRoot, Console.WriteLine, Console.Error.WriteLine);
            case "stop": return ServerHost.Stop(iRepoRoot, Console.WriteLine, Console.Error.WriteLine);
            case "status": return Status(iRepoRoot);
            case "": return Usage(2, "server 少了子指令");
            default: return Usage(2, $"server 認不得的子指令 '{aSub}'");
        }
    }

    static int Status(string iRepoRoot)
    {
        ServerStatus s = ServerHost.Probe(iRepoRoot);
        Console.WriteLine($"· 本 CLI build={s.MyBuildId}");

        if (s.Alive == null)
        {
            // 「沒在跑」與「認不出來」不同形：後者有東西，只是沒辦法說它是不是 Server。
            if (s.Unverifiable.Count > 0)
            {
                Console.WriteLine($"？ 沒有 Alive 的 Server，但 registry 裡有 {s.Unverifiable.Count} 筆 `{ServerHost.Tag}` 身分驗不出來"
                                  + $"（pid={string.Join(",", s.Unverifiable.ConvertAll(r => r.Pid.ToString()))}）");
                Console.WriteLine("  出口：senate ui --click home/open/process（ProcessAdminPage 看那幾筆）");
            }
            else
            {
                Console.WriteLine("・Server 沒在跑");
            }
            if (s.Heartbeat != null)
                Console.WriteLine($"⚠ 但心跳檔還在（pid={s.Heartbeat.Pid}，{DescribeAge(s)}）—— 上一顆沒收乾淨；`senate server stop` 會順手清掉");
            Console.WriteLine("  啟動：senate server start（前景，開一個終端機掛著）");
            Console.WriteLine("🔢 server_state = not_running");
            return 3;
        }

        Console.WriteLine($"● Server 在跑　pid={s.Alive.Pid}　start={s.Alive.StartTimeUtcText}　registered_by={s.Alive.RegisteredBy}");
        if (s.Heartbeat == null)
        {
            Console.WriteLine($"⚠ 心跳讀不到：{s.HeartbeatError}（process 活著但沒在跳 ＝ 卡住，不是正常）");
            Console.WriteLine("🔢 server_state = alive_no_heartbeat");
            return 3;
        }
        Console.WriteLine($"· 心跳 {DescribeAge(s)}　build={s.Heartbeat.BuildId}　started={s.Heartbeat.StartedAtUtc}");
        if (!s.HeartbeatFresh)
        {
            Console.WriteLine($"⚠ 心跳超過 {ServerHost.HeartbeatStaleSeconds:0} 秒沒跳 ⇒ 視為卡住。出口：senate server stop（等不到自退會 kill）");
            Console.WriteLine("🔢 server_state = stale_heartbeat");
            return 3;
        }
        if (!s.BuildMatches)
        {
            // 兩顆 exe 在畫面上長得一模一樣 —— 這行是唯一分得出來的地方。
            Console.WriteLine($"⚠ 版本不符：Server build={s.Heartbeat.BuildId}，本 CLI build={s.MyBuildId} ⇒ 先 `senate server stop` 再 `start`，別讓舊的那顆替新的跑。");
            Console.WriteLine("🔢 server_state = running_build_mismatch");
            return 0;
        }
        PrintLanes(iRepoRoot);
        Console.WriteLine("🔢 server_state = running");
        return 0;
    }

    /// <summary>Server 根底下每條 lane 的 trigger 狀態（idle／pending／running）與殘量 —— 對應 `ucmd status` 那張表。</summary>
    static void PrintLanes(string iRepoRoot)
    {
        string aServerRoot = SenatePaths.ServerRoot(iRepoRoot);
        string aQueues = SCP.Core.Paths.SCP_DataPaths.Queues(new SCP.Core.Paths.SCP_DataRoot(aServerRoot));
        if (!Directory.Exists(aQueues)) { Console.WriteLine($"· 分道：（還沒有任何 lane）　根={aServerRoot}"); return; }
        string[] aDirs = Directory.GetDirectories(aQueues);
        Console.WriteLine($"· 分道 {aDirs.Length} 條　根={aServerRoot}");
        foreach (string aDir in aDirs)
        {
            string aLane = Path.GetFileName(aDir);
            string aState = AgentCmdClient.TriggerState(aServerRoot, aLane);
            int aCount = 0;
            try
            {
                string aQ = AgentCmdClient.QueuePath(aServerRoot, aLane);
                if (File.Exists(aQ) && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(aQ)) is System.Text.Json.Nodes.JsonObject aObj
                    && aObj["Commands"] is System.Text.Json.Nodes.JsonArray aArr) aCount = aArr.Count;
            }
            catch { aCount = -1; }   // 壞檔：印 -1 不印 0 —— 「讀不了」跟「空的」不可同形
            Console.WriteLine($"    {aLane,-16} {aState,-8} 殘量 {(aCount < 0 ? "讀不了" : aCount.ToString())}");
        }
    }

    static string DescribeAge(ServerStatus s)
    {
        double? aAge = s.Heartbeat?.AgeSeconds();
        return aAge.HasValue ? $"{aAge.Value:0.0} 秒前" : "時間戳解析不了";
    }

    static int Usage(int iCode, string iError)
    {
        Console.Error.WriteLine($"✗ {iError}");
        Console.Error.WriteLine("  senate server start   前景常駐（Ctrl+C 停）；已有一顆在跑會拒絕");
        Console.Error.WriteLine("  senate server stop    請它自退，5 秒等不到才 kill；沒在跑也 exit 0");
        Console.Error.WriteLine("  senate server status  身分／心跳／build id 三格分開印；沒在跑 exit 3");
        return iCode;
    }
}
