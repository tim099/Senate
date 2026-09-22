// 區塊職責：`senate server start|stop|status` 的 CLI 半邊 —— 解析動詞、印讀數、翻 exit code。
// 物理意義：生命週期本體在 Senate.Core/ServerHost（零 Console 依賴，之後 GUI 要接同一份）。
//           這裡只做「人打了什麼」→「呼叫哪一支」→「印成什麼樣」。
// 數值影響：exit code —— start：0 正常退出／1 已有 Server 或身分驗不出／70 登記不了；
//           stop：0 停掉或本來就沒在跑／1 停不掉；status：0 活著且心跳新鮮／3 沒在跑或心跳停了。
//           ⚠ status 的 3 對齊委派 Cmd「沒有結果」那格（腳本已經在吃 0/1/2/3 四格，不發明第五格）。
using Senate.Core;
using SCP.Core.Proc;

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
                return ServerHost.RunForeground(iRepoRoot, aId ?? SCP_ServerIds.Default,
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
            try { return SCP_ServerIds.Normalize(iArgs[i + 1]); }
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
        return ServerHost.Stop(iRepoRoot, aRunning.Count == 1 ? aRunning[0] : SCP_ServerIds.Default,
                               Console.WriteLine, Console.Error.WriteLine);
    }

    /// <summary>列出這棵樹上看得到的每一顆（含沒在跑的）。</summary>
    static int List(string iRepoRoot)
    {
        List<string> aIds = ServerHost.KnownIds(iRepoRoot);
        int aAlive = 0, aTotalPending = 0, aTotalOrphan = 0;
        Console.WriteLine($"· 看得到 {aIds.Count} 顆（registry ∪ 心跳檔）");
        foreach (string aId in aIds)
        {
            ServerStatus s = ServerHost.Probe(iRepoRoot, aId);
            if (s.IsRunning) aAlive++;
            string aState = s.IsRunning ? (s.HeartbeatFresh ? "running" : "stale_heartbeat") : "not_running";
            Console.WriteLine($"    {aId,-12} {aState,-16} pid={(s.Alive?.Pid.ToString() ?? "-"),-8} build={s.Heartbeat?.BuildId ?? "-"}");
            (int aPend, int aOrphan, List<string> aNotes) = ProbeQueueResidue(iRepoRoot, aId);
            if (aPend > 0 || aOrphan > 0)
            {
                Console.WriteLine($"      queue 殘量：待消化 {aPend} 筆／**執行器讀不到 {aOrphan} 筆**");
                foreach (string n in aNotes) Console.WriteLine("  " + n);
                aTotalPending += aPend;
                aTotalOrphan += aOrphan;
            }
        }
        Console.WriteLine($"🔢 server_alive_count = {aAlive}");
        // ⚠ 兩個數字分開報 —— 「還沒跑完」與「永遠不會被跑」的處置不同：
        //   前者等它，後者要有人去搬走或改執行器。壓成一個「殘量」會讓後者一直被當成前者。
        Console.WriteLine($"🔢 queue_pending = {aTotalPending}");
        Console.WriteLine($"🔢 queue_unreachable = {aTotalOrphan}");
        return aAlive > 0 ? 0 : 3;
    }

    static int Status(string iRepoRoot, string? iId)
    {
        // 不指名 ⇒ 全部列出來。⛔ 不預設只看 `main`：
        //   那樣的話「酒館那顆挂了」跟「一切正常」在畫面上同形。
        if (iId == null && ServerHost.KnownIds(iRepoRoot).Count > 1) return List(iRepoRoot);
        string aId = iId ?? SCP_ServerIds.Default;
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
            Console.WriteLine($"  啟動：senate server start{(aId == SCP_ServerIds.Default ? "" : " --id " + aId)}（前景，開一個終端機掛著）");
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

    /// <summary>
    /// 這顆 Server 的執行器根底下**還沒被消化的 queue 筆數**，以及**它根本不會去看的那些檔**。
    /// <para>🩸 為什麼要這一格（TASK-0106 D10 驗收最後一條）：切回 `editor` 之後，
    /// Server 那側可能還躺著幾筆沒跑完的 —— 而在這之前**沒有任何一支指令讀得出那個數字**。
    /// ⇒ 「切回來了」與「切回來了但那邊還躺著三筆」在畫面上同形。</para>
    /// <para>⭐ 第二格更貴：執行器只認 <c>queues/&lt;dir&gt;/pending.trigger</c>，
    /// 而協議還有一種**子分道**寫法（<c>pending-&lt;lane&gt;.trigger</c>，`SCP_DataPaths.SplitQueueId`）。
    /// 那種檔案合法、寫得出去、而執行器**永遠不會碰它** ——
    /// 2026-09-21 實測：等 15 秒逾時，queue 檔好好躺在磁碟上，沒有任何一層說不認得。
    /// ⇒ 這裡把它數出來並點名，⛔ 不修執行器（那不在本單射程）。</para>
    /// </summary>
    static (int Pending, int Orphan, List<string> Notes) ProbeQueueResidue(string iRepoRoot, string iServerId)
    {
        var aNotes = new List<string>();
        int aPending = 0, aOrphan = 0;
        string aQueues = Path.Combine(SenatePaths.ServerRoot(iRepoRoot, iServerId), "queues");
        if (!Directory.Exists(aQueues)) return (0, 0, aNotes);

        foreach (string aDir in Directory.GetDirectories(aQueues))
        {
            string aLane = Path.GetFileName(aDir);
            foreach (string aFile in Directory.GetFiles(aDir, "queue*.json"))
            {
                int aCount = CountQueueEntries(aFile);
                if (aCount <= 0) continue;
                bool aSubLane = !string.Equals(Path.GetFileName(aFile), "queue.json", StringComparison.Ordinal);
                if (aSubLane)
                {
                    aOrphan += aCount;
                    aNotes.Add($"　⛔ **執行器不會碰它**：{aLane}/{Path.GetFileName(aFile)}（{aCount} 筆）"
                               + " —— 子分道檔（`queue-<lane>.json`），而 `ServerExecutor.Tick` 只認"
                               + " `queues/<dir>/pending.trigger`。這些會**永遠躺在那裡而不出聲**。");
                }
                else
                {
                    aPending += aCount;
                    aNotes.Add($"　· 待消化：{aLane}（{aCount} 筆）");
                }
            }
        }
        return (aPending, aOrphan, aNotes);
    }

    static int CountQueueEntries(string iPath)
    {
        try
        {
            SCP.Core.Json.SCP_JsonData aJson = SCP.Core.Json.SCP_JsonParser.Parse(File.ReadAllText(iPath));
            return aJson.Contains("Commands") ? aJson["Commands"].Count : 0;
        }
        catch (Exception) { return 0; }
    }

}
