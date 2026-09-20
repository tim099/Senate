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
        string? aId = ParseId(iArgs, out string aIdErr);
        if (aIdErr.Length > 0) return Usage(2, aIdErr);

        switch (aSub)
        {
            // start 不指名 ⇒ `main`：起一顆是明確的動作，有預設不會讓人損失什麼。
            case "start":
                return ServerHost.RunForeground(iRepoRoot, aId ?? ServerIds.Default,
                                                Console.WriteLine, Console.Error.WriteLine);
            // 🔴 stop 不指名則**不猜**：只有一顆在跑就停它，兩顆以上擋下並要求指名。
            //   ⛔ 預設停 `main` 是一把裝填好的槍：人想停酒館那顆而停掉銀行那顆，而兩者的輸出同形。
            case "stop": return Stop(iRepoRoot, aId, HasFlag(iArgs, "--all"));
            case "status": return Status(iRepoRoot, aId);
            case "list": return List(iRepoRoot);
            case "": return Usage(2, "server 少了子指令");
            default: return Usage(2, $"server 認不得的子指令 '{aSub}'");
        }
    }

    /// <summary>`--id &lt;serverId&gt;`；沒給回 null（「沒指名」與「指名了 main」不同形 —— stop 靠這一格分辨）。</summary>
    static string? ParseId(string[] iArgs, out string oErr)
    {
        oErr = "";
        for (int i = 0; i < iArgs.Length; i++)
        {
            if (!string.Equals(iArgs[i], "--id", StringComparison.Ordinal)) continue;
            if (i + 1 >= iArgs.Length) { oErr = "--id 後面沒有值"; return null; }
            try { return ServerIds.Normalize(iArgs[i + 1]); }
            catch (ArgumentException e) { oErr = e.Message; return null; }
        }
        return null;
    }

    /// <summary>停哪一顆。不指名時只有「唯一一顆在跑」才動手；兩顆以上擋下並列出來。</summary>
    static bool HasFlag(string[] iArgs, string iFlag)
    {
        foreach (string a in iArgs) if (string.Equals(a, iFlag, StringComparison.Ordinal)) return true;
        return false;
    }

    static int Stop(string iRepoRoot, string? iId, bool iAll)
    {
        if (iId != null && iAll)
            return Usage(2, "--id 與 --all 不能同時給 —— 它們讓同一道指令有兩個不同的答案。");
        if (iId != null) return ServerHost.Stop(iRepoRoot, iId, Console.WriteLine, Console.Error.WriteLine);

        var aRunning = new List<string>();
        foreach (string aKnown in ServerHost.KnownIds(iRepoRoot))
            if (ServerHost.Probe(iRepoRoot, aKnown).IsRunning) aRunning.Add(aKnown);

        // ⭐ `--all`：給 build 腳本用的。
        //   它需要的不是「停某一顆」，是「把所有鎖著 exe 的都放掉」——
        //   而漏停一顆的樣子是 publish 撞 `Access to the path ... is denied`，而錯誤訊息不會說是誰。
        //   ⛔ 而交互使用時仍然不推薦它：「全部停」跟「停錯一顆」的差別只在你有沒有想清楚。
        if (iAll)
        {
            if (aRunning.Count == 0) { Console.WriteLine("· 沒有任何 Server 在跑。"); }
            int aWorst = 0;
            foreach (string aOne in aRunning)
            {
                Console.WriteLine($"── 停 [{aOne}] ──");
                int aCode = ServerHost.Stop(iRepoRoot, aOne, Console.WriteLine, Console.Error.WriteLine);
                if (aCode != 0) aWorst = aCode;   // ⚠ 一顆停不掉就是非零，⛔ 不向下取最好的那一顆
            }
            Console.WriteLine($"🔢 server_stopped_count = {aRunning.Count}");
            return aWorst;
        }

        if (aRunning.Count > 1)
        {
            Console.Error.WriteLine($"✗ 有 {aRunning.Count} 顆 Server 在跑（" + string.Join("、", aRunning) + "）⇒ **要指名**。");
            Console.Error.WriteLine("  senate server stop --id <哪一顆>。⛔ 不替你選一顆 —— 停錯那顆的樣子跟停對一模一樣。");
            Console.Error.WriteLine("  全部停掉：senate server stop --all（build 腳本走這條）");
            Console.WriteLine("🔢 server_state = ambiguous_needs_id");
            return 2;
        }
        // 0 顆在跑也走 `main`：`Stop` 本來就是幂等的（build 腳本每次都呼叫它），
        // 而順手清殘留心跳檔那一格還是要發生。
        return ServerHost.Stop(iRepoRoot, aRunning.Count == 1 ? aRunning[0] : ServerIds.Default,
                               Console.WriteLine, Console.Error.WriteLine);
    }

    /// <summary>列出這棵樹上看得到的每一顆（含沒在跑的）。</summary>
    static int List(string iRepoRoot)
    {
        List<string> aIds = ServerHost.KnownIds(iRepoRoot);
        int aAlive = 0;
        Console.WriteLine($"· 看得到 {aIds.Count} 顆（registry ∪ 心跳檔）");
        foreach (string aId in aIds)
        {
            ServerStatus s = ServerHost.Probe(iRepoRoot, aId);
            if (s.IsRunning) aAlive++;
            string aState = s.IsRunning ? (s.HeartbeatFresh ? "running" : "stale_heartbeat") : "not_running";
            Console.WriteLine($"    {aId,-12} {aState,-16} pid={(s.Alive?.Pid.ToString() ?? "-"),-8} build={s.Heartbeat?.BuildId ?? "-"}");
        }
        Console.WriteLine($"🔢 server_alive_count = {aAlive}");
        return aAlive > 0 ? 0 : 3;
    }

    static int Status(string iRepoRoot, string? iId)
    {
        // 不指名 ⇒ 全部列出來。⛔ 不預設只看 `main`：
        //   那樣的話「酒館那顆挂了」跟「一切正常」在畫面上同形。
        if (iId == null && ServerHost.KnownIds(iRepoRoot).Count > 1) return List(iRepoRoot);
        string aId = iId ?? ServerIds.Default;
        ServerStatus s = ServerHost.Probe(iRepoRoot, aId);
        Console.WriteLine($"· 本 CLI build={s.MyBuildId}　serverId={aId}");

        if (s.Alive == null)
        {
            // 「沒在跑」與「認不出來」不同形：後者有東西，只是沒辦法說它是不是 Server。
            if (s.Unverifiable.Count > 0)
            {
                Console.WriteLine($"？ 沒有 Alive 的 Server，但 registry 裡有 {s.Unverifiable.Count} 筆 `{ServerHost.TagFor(aId)}` 身分驗不出來"
                                  + $"（pid={string.Join(",", s.Unverifiable.ConvertAll(r => r.Pid.ToString()))}）");
                Console.WriteLine("  出口：senate ui --click home/open/process（ProcessAdminPage 看那幾筆）");
            }
            else
            {
                Console.WriteLine("・Server 沒在跑");
            }
            if (s.Heartbeat != null)
                Console.WriteLine($"⚠ 但心跳檔還在（pid={s.Heartbeat.Pid}，{DescribeAge(s)}）—— 上一顆沒收乾淨；`senate server stop` 會順手清掉");
            Console.WriteLine($"  啟動：senate server start{(aId == ServerIds.Default ? "" : " --id " + aId)}（前景，開一個終端機掛著）");
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
        PrintLanes(iRepoRoot, aId);
        Console.WriteLine("🔢 server_state = running");
        return 0;
    }

    /// <summary>Server 根底下每條 lane 的 trigger 狀態（idle／pending／running）與殘量 —— 對應 `ucmd status` 那張表。</summary>
    static void PrintLanes(string iRepoRoot, string iServerId)
    {
        string aServerRoot = SenatePaths.ServerRoot(iRepoRoot, iServerId);
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
        Console.Error.WriteLine("  senate server stop --all  把所有在跑的都停掉（build 腳本用；交互使用請指名）");
        Console.Error.WriteLine("  senate server list    列出看得到的每一顆（registry ∪ 心跳檔）");
        Console.Error.WriteLine("  共用旗標：--id <serverId>（預設 `main`；stop 不指名而有兩顆以上在跑 ⇒ 擋下要求指名）");
        return iCode;
    }
}
