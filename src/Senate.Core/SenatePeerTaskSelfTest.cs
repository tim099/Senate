// 區塊職責：**Task Server 的對拍**（FAR-53）—— 暫存資料夾裡起一台主機 A、兩個另一端 B 與 C（C 是 Task Server），走本機迴路。
// 物理意義：照 `PeerLinkCleanRoom` 的做法（綁 127.0.0.1、通道與 UPnP 關、不連 Discord），逐項驗：
//           ① B 開單 ⇒ 只寫在 C（經 `task` 入口，走 Task Server 那條分支）　② 主機 A 送給 C ⇒ 也只寫在 C
//           ③ B 讀單（`tasks`）⇒ 讀到的是 C 的單　④ C 不在線 ⇒ exit 6、三台都沒有新檔　⑤ C 收了不回 ⇒ exit 7
//           ⑥ B 不在允許名單 ⇒ exit 1、C 沒有新檔　⑦ 主機改了轉送中的請求 ⇒ C 驗章不過、不執行、不回（exit 7）
//           ⑧ 同一個 rid 送兩次 ⇒ 只寫一次、第二次回第一次的結果　⑨ 舊版收到 req ⇒ 略過、不斷線、聊天照通
//           ⑦之二 用另一把金鑰冒充 B ⇒ 擋下、不回　⑦之三 帶 scope ⇒ Task Server 自己擋　⑦之四 persona=../…、memory_topic=../… 或絕對路徑 ⇒ 擋下、資料根外面沒東西
//           ⑦之五 RefuseReason 被改壞（測試開關）⇒ 呼叫到的也只是只記錄的執行端
//           FAR-58：⑩ timeout 壓到 300 秒　⑪ 同時最多 4 筆，第 5 筆「忙碌」exit 6、同一個 rid 之後重送會執行
//           ⑫ 不在允許名單的連送 30 筆 ⇒ 最多回 10 筆、log 最多 1 行　⑬ 簽章驗不過的連送 30 筆 ⇒ 一筆都不回、log 最多 1 行
//           ⑭ `peer-rpc` 依 persona 分 lane
//           外加：設定寫壞了 ⇒ 不知道寫哪 ⇒ 擋下。
//           執行請求的掛鉤：`task` 直接對 C 的暫存資料根跑寫入端（淨室裡沒有 Server）；`tasks` 走正式的那一支。
//           ⛔ 直接呼叫 `SenateTaskServer.Serve` 的地方一律傳只記錄的執行端：正式的 `task` 會起 Server、發通知。
// 數值影響：只寫系統暫存資料夾（通過就刪；沒過留著並印出位置）；開一個本機迴路的埠。約 40～80 秒。
// ⚠ 擋下的回覆每個 from 每分鐘最多 10 筆：B 在允許名單時被擋的（⑪ 的忙碌、⑦之三、⑦之四）加起來要留在 10 筆以內。
#nullable enable
using System.Net;
using System.Net.Sockets;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Tasks;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenatePeerTaskSelfTest
{
    sealed class Node : IDisposable
    {
        public string Root = "", Name = "", Id = "";
        public SenatePeerLink Link = null!;
        public List<string> Log = new();
        /// <summary>非 null ⇒ 執行端先卡在這裡（⑪：讓請求同時在跑）。</summary>
        public volatile ManualResetEventSlim? Hold;
        public void Dispose() => Link.Dispose();
    }

    static int LogCount(Node n, string iText)
    {
        lock (n.Log) return n.Log.Count(l => l.Contains(iText));
    }

    static string LastLog(Node n, string iText)
    {
        lock (n.Log) return n.Log.LastOrDefault(l => l.Contains(iText)) ?? "（沒有）";
    }

    /// <summary>同時送 <paramref name="iCount"/> 筆（各自一條執行緒、各等 <paramref name="iWaitSec"/> 秒）。</summary>
    static List<PeerRpcResult> Flood(Node iFrom, string iTo, string iCmd, Dictionary<string, string> iArgs, int iCount, int iWaitSec)
    {
        var aOut = new PeerRpcResult[iCount];
        var aThreads = new List<Thread>();
        for (int i = 0; i < iCount; i++)
        {
            int k = i;
            var t = new Thread(() => aOut[k] = iFrom.Link.Request(iTo, SenatePeerLink.SvcTask, iCmd, iArgs, TimeSpan.FromSeconds(iWaitSec))) { IsBackground = true };
            aThreads.Add(t);
            t.Start();
        }
        foreach (Thread t in aThreads) t.Join(TimeSpan.FromSeconds(iWaitSec + 15));
        return aOut.Select(x => x ?? new PeerRpcResult { Outcome = PeerRpcOutcome.Unknown, Why = "執行緒沒結束" }).ToList();
    }

    const int PeerRpcMaxRunning = SenatePeerLink.RpcMaxRunning;

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>淨室的執行端：`task` 直接對這一台的暫存資料根跑寫入端；`tasks` 走正式那一支（資料根換成這一台的）。</summary>
    static PeerRpcReply Serve(Node n, PeerRpcCall c)
    {
        n.Hold?.Wait(TimeSpan.FromSeconds(30));
        if (c.Cmd != "task") return SenateTaskServer.Serve(n.Root, n.Name, c);
        var aArgs = new Dictionary<string, string>(c.Args, StringComparer.Ordinal);
        aArgs.Remove("op"); aArgs.Remove("persona"); aArgs.Remove("timeout");   // 入口 `task` 自己的參數，不是寫入端的
        (SCP_TaskOpResult r, TaskSyncReport s) = SenateTaskRepoSync.RunWrite(n.Root, null, c.Args.GetValueOrDefault("op", ""), c.Args.GetValueOrDefault("persona", ""), aArgs);
        SCP_CmdResult w = Cmd_TaskWrite.BuildResult(r, s, null, "⤷ 任務單寫入端：淨室（" + n.Name + "）");
        var aReply = new PeerRpcReply { Exit = w.ExitCode };
        aReply.Lines.AddRange(w.Lines);
        aReply.Values.AddRange(w.Values);
        return aReply;
    }

    static Node MakeNode(string iBase, string iName, string iMode, int iPort)
    {
        var n = new Node { Root = Path.Combine(iBase, iName).Replace('\\', '/'), Name = iName };
        Directory.CreateDirectory(n.Root);
        n.Id = SenatePeerStore.LoadOrCreateIdentity(n.Root).Id;
        SenatePeerStore.SaveConfig(n.Root, new PeerConfig { Mode = iMode, Name = iName, Port = iPort > 0 ? iPort : 47800, Bind = "127.0.0.1", Tunnel = "off", PortMap = "off" });
        n.Link = new SenatePeerLink(n.Root, new SenatePeerLink.Hooks
        {
            Out = s => { lock (n.Log) n.Log.Add(s); },
            Err = s => { lock (n.Log) n.Log.Add(s); },
            WriteTavern = (aRoom, aJson) =>
            {
                SCP_TavernMessage m = SCP_TavernRead.FromJson(aJson, aRoom, 0, "");
                SCP_TavernWriteResult w = SCP_TavernWriter.WriteMessage(n.Root, aRoom, m);
                return (w.Wrote, w.Seq, w.Detail);
            },
            Serve = c => Serve(n, c),
        });
        return n;
    }

    static void SetCfg(Node n, Action<PeerConfig> iChange)
    {
        PeerConfig c = SenatePeerStore.LoadConfig(n.Root);
        iChange(c);
        SenatePeerStore.SaveConfig(n.Root, c);
    }

    static int TaskCount(Node n)
    {
        string d = SCP_TaskIO.TasksDir(new SCP_DataRoot(n.Root));
        return Directory.Exists(d) ? Directory.GetFiles(d, "*.md").Length : 0;
    }

    /// <summary>這台全部單子的內容（依檔名排序串起來）：比前後有沒有被改過。</summary>
    static string TaskSnapshot(Node n)
    {
        string d = SCP_TaskIO.TasksDir(new SCP_DataRoot(n.Root));
        if (!Directory.Exists(d)) return "";
        return string.Join("\n", Directory.GetFiles(d, "*.md").OrderBy(p => p, StringComparer.Ordinal).Select(p => p + "\n" + File.ReadAllText(p)));
    }

    static bool Until(Func<bool> iCond, int iSeconds, params Node[] iNodes)
    {
        DateTime aEnd = DateTime.UtcNow.AddSeconds(iSeconds);
        while (DateTime.UtcNow < aEnd)
        {
            foreach (Node n in iNodes) { try { n.Link.Step(); } catch (Exception e) { lock (n.Log) n.Log.Add("Step 例外：" + e.Message); } }
            if (iCond()) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    static void Settle(int iRounds, params Node[] iNodes)
    {
        for (int i = 0; i < iRounds; i++)
        {
            foreach (Node n in iNodes) n.Link.Step();
            Thread.Sleep(100);
        }
    }

    static string V(SCP_CmdResult r, string k) => SenateTaskServer.Value(r, k);

    /// <summary>自我測試本體（`senate selftest --only PeerTaskCleanRoom`）。回沒過的項目；逐項讀數寫進 <paramref name="oLines"/>。</summary>
    public static List<string> Run(List<string> oLines)
    {
        string aBase = Path.Combine(Path.GetTempPath(), "senate-peertask-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var aFails = new List<string>();
        void Check(string iWhat, bool iOk, string iReading)
        {
            oLines.Add((iOk ? "✓ " : "✗ ") + iWhat + "　〔" + iReading + "〕");
            if (!iOk) aFails.Add(iWhat);
        }
        var aNodes = new List<Node>();
        Func<TaskServerRoute, string, Dictionary<string, string>, int, SCP_CmdResult>? aKeepOverride = SenateTaskServer.SendOverride;
        Node? aFrom = null;
        try
        {
            int aPort = FreePort();
            Node A = MakeNode(aBase, "hostA", "host", aPort);
            Node B = MakeNode(aBase, "nodeB", "client", 0);
            Node C = MakeNode(aBase, "nodeC", "client", 0);
            aNodes.AddRange(new[] { A, B, C });
            Node[] All = { A, B, C };
            SetCfg(C, c => { c.TaskServer = PeerConfig.Self; c.TaskClients = new List<string> { B.Id, A.Id }; });
            SetCfg(B, c => c.TaskServer = C.Id);
            SetCfg(A, c => c.TaskServer = C.Id);

            // 入口（task／tasks）送出去那一步：淨室裡沒有酒館 Server ⇒ 直接拿送出那一台的連線本體送
            SenateTaskServer.SendOverride = (aRoute, aCmd, aArgs, aWait) =>
                SenateTaskServer.ToCmdResult(aFrom!.Link.Request(aRoute.Id, SenatePeerLink.SvcTask, aCmd, aArgs, TimeSpan.FromSeconds(aWait)));

            A.Link.Step();
            PeerIdentity aIdA = SenatePeerStore.LoadOrCreateIdentity(A.Root);
            var aAnn = new PeerAnnounce
            {
                Id = aIdA.Id, Name = "hostA", Sign = aIdA.SignPub, Token = SenatePeerStore.HostToken(A.Root), Ts = SenatePeerStore.NowIso(),
                Addrs = new List<string> { $"ws://127.0.0.1:{aPort}{SenatePeerLink.WsPath}" },
            };
            SenatePeerStore.AcceptAnnounce(B.Root, aAnn);
            SenatePeerStore.AcceptAnnounce(C.Root, aAnn);
            bool aUp = Until(() => A.Link.SessionCount == 2 && B.Link.SeesService(C.Id, SenatePeerLink.SvcTask) && A.Link.SeesService(C.Id, SenatePeerLink.SvcTask), 30, All);
            Check("三台連上；B 與主機 A 的在線名單上看得到 C 是 Task Server", aUp,
                $"host_sessions={A.Link.SessionCount} B 看得到={B.Link.SeesService(C.Id, SenatePeerLink.SvcTask)} A 看得到={A.Link.SeesService(C.Id, SenatePeerLink.SvcTask)}");

            SCP_CmdResult Task(Node iFrom, params (string K, string V)[] iArgs)
            {
                aFrom = iFrom;
                var d = new Dictionary<string, string>(StringComparer.Ordinal) { ["data_root"] = iFrom.Root };
                foreach (var (k, v) in iArgs) d[k] = v;
                return SCP_CmdRegistry.Dispatch("task", d);
            }

            // ① B 開單（走 `task` 入口）⇒ 只寫在 C
            SCP_CmdResult r1 = Task(B, ("op", "create"), ("persona", "Bob"), ("title", "B 開的單"), ("criteria", "- [ ] 有寫在 C"));
            Check("① B 開單 ⇒ 只寫在 Task Server C（A、B 都沒有）",
                r1.ExitCode == 0 && V(r1, "wrote") == "1" && V(r1, "index").Length > 0 && TaskCount(C) == 1 && TaskCount(A) == 0 && TaskCount(B) == 0
                && r1.Lines.Any(l => l.Contains("於 Task Server nodeC")),
                $"exit={r1.ExitCode} wrote={V(r1, "wrote")} index={V(r1, "index")} 單數 A/B/C={TaskCount(A)}/{TaskCount(B)}/{TaskCount(C)}　{r1.Lines.FirstOrDefault(l => l.Contains("Task Server"))}");

            // ② 主機 A 送給 C
            SCP_CmdResult r2 = Task(A, ("op", "create"), ("persona", "Alice"), ("title", "主機開的單"), ("criteria", "- [ ] 也寫在 C"));
            Check("② 主機 A 下指令 ⇒ 一樣只寫在 C", r2.ExitCode == 0 && V(r2, "wrote") == "1" && TaskCount(C) == 2 && TaskCount(A) == 0,
                $"exit={r2.ExitCode} 單數 A/B/C={TaskCount(A)}/{TaskCount(B)}/{TaskCount(C)}");

            // ②之二 claim 帶 scope：Coding 場開在 B、scope 不送到 C（送了 C 會用 B 的路徑再開一場）
            string aScope = Path.Combine(aBase, "scope-b").Replace('\\', '/');
            Directory.CreateDirectory(aScope);
            SCP_CmdResult r2b = Task(B, ("op", "claim"), ("persona", "Bob"), ("index", V(r1, "index")), ("scope", aScope));
            var aCodingB = SCP.Core.Session.SCP_ActivitySessionStore.Load<SCP.Core.Session.SCP_CodingSession>(new SCP_DataRoot(B.Root), "Bob", SCP.Core.Session.SCP_ActivitySessionKind.Coding);
            var aCodingC = SCP.Core.Session.SCP_ActivitySessionStore.Load<SCP.Core.Session.SCP_CodingSession>(new SCP_DataRoot(C.Root), "Bob", SCP.Core.Session.SCP_ActivitySessionKind.Coding);
            Check("②之二 claim 帶 scope ⇒ 認領寫在 C、Coding 場只開在 B", r2b.ExitCode == 0 && V(r2b, "wrote") == "1" && aCodingB != null && aCodingB.active && (aCodingC == null || !aCodingC.active),
                $"exit={r2b.ExitCode} wrote={V(r2b, "wrote")} B 有場={aCodingB?.active} C 有場={aCodingC?.active}　{r2b.Lines.FirstOrDefault(l => l.StartsWith("✗") || l.StartsWith("⛔"))}");

            // ③ B 讀單 ⇒ C 的單
            aFrom = B;
            var aRaw = new Dictionary<string, string>(StringComparer.Ordinal) { ["data_root"] = B.Root, ["status"] = "all" };
            SCP_CmdResult? r3 = SenateTaskServer.TryForwardRead(aRaw, new[] { "status" });
            string aList = r3 == null ? "" : string.Join(" ⏎ ", r3.Lines);
            Check("③ B 讀單（tasks）⇒ 讀到的是 C 的兩張", r3 != null && r3.ExitCode == 0 && aList.Contains("B 開的單") && aList.Contains("主機開的單") && aList.Contains("於 Task Server"),
                r3 == null ? "（沒有轉送，照本機讀了）" : $"exit={r3.ExitCode} {(aList.Length > 300 ? aList.Substring(0, 300) + "…" : aList)}");
            var aRawOut = new Dictionary<string, string>(aRaw) { ["out_json"] = Path.Combine(aBase, "x.json") };
            SCP_CmdResult? r3b = SenateTaskServer.TryForwardRead(aRawOut, new[] { "status", "out_json" });
            Check("③之二 反向：Task 在別台時 out_json 直接說不行（不寫一個空檔）", r3b != null && r3b.ExitCode == 2 && !File.Exists(Path.Combine(aBase, "x.json")),
                $"exit={r3b?.ExitCode}");

            // `peer` 指令那一側（只動暫存資料根）：設定、現況、在線名單
            SCP_CmdResult Peer(string iRoot, params (string K, string V)[] iArgs)
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal) { ["data_root"] = iRoot };
                foreach (var (k, v) in iArgs) d[k] = v;
                return SCP_CmdRegistry.Dispatch("peer", d);
            }
            SCP_CmdResult p1 = Peer(B.Root, ("op", "set"), ("task_server", "那一台"));
            SCP_CmdResult p2 = Peer(B.Root, ("op", "set"), ("task_clients", "abc"));
            Check("peer op=set：task_server／task_clients 寫錯 ⇒ exit 2，設定不動", p1.ExitCode == 2 && p2.ExitCode == 2 && SenatePeerStore.LoadConfig(B.Root).TaskServer == C.Id,
                $"exit {p1.ExitCode}／{p2.ExitCode}　讀回 task_server={SenatePeerStore.LoadConfig(B.Root).TaskServer}");
            string aStatusB = string.Join(" ⏎ ", Peer(B.Root).Lines);
            string aOnlineB = string.Join(" ⏎ ", Peer(B.Root, ("op", "online")).Lines);
            Check("peer status／online：B 看得到「Task → nodeC 在線」，在線名單標出 Task Server",
                aStatusB.Contains("Task → nodeC（" + C.Id + "）　在線") && aOnlineB.Contains("nodeC（" + C.Id + "）［Task Server］"),
                $"status：{aStatusB.Split(" ⏎ ").FirstOrDefault(l => l.Contains("服務"))}　online：{aOnlineB.Split(" ⏎ ").FirstOrDefault(l => l.Contains("在線的節點"))}");
            string aD = Path.Combine(aBase, "nodeD").Replace('\\', '/');
            Directory.CreateDirectory(aD);
            SCP_CmdResult p3 = Peer(aD, ("op", "set"), ("task_server", "self"), ("task_clients", B.Id.ToUpperInvariant() + " , " + A.Id));
            PeerConfig aDc = SenatePeerStore.LoadConfig(aD);
            string aStatusD = string.Join(" ⏎ ", Peer(aD).Lines);
            Check("peer op=set task_server=self：讀回是 Task Server、允許名單轉小寫去空白；有提醒「單子不會自動搬」",
                p3.ExitCode == 0 && aDc.ServesTask && aDc.TaskClients.SequenceEqual(new[] { B.Id, A.Id }) && p3.Lines.Any(l => l.Contains("不會自動搬"))
                && aStatusD.Contains("這台是 Task Server") && SenatePeerStore.LoadConfig(aD).Mode == "off",
                $"exit={p3.ExitCode} task_server={aDc.TaskServer} clients={string.Join(",", aDc.TaskClients)} mode={aDc.Mode}");

            // ⑧ 同一個 rid 送兩次 ⇒ 只寫一次
            int aBefore8 = TaskCount(C);
            var aArgs8 = new Dictionary<string, string> { ["op"] = "create", ["persona"] = "Bob", ["title"] = "重送的單", ["criteria"] = "- [ ] 只有一張" };
            PeerRpcResult a8 = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs8, TimeSpan.FromSeconds(20), "selftest-rid-0008");
            PeerRpcResult b8 = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs8, TimeSpan.FromSeconds(20), "selftest-rid-0008");
            string I(PeerRpcResult x) => x.Reply.Values.FirstOrDefault(kv => kv.Key == "index").Value ?? "";
            Check("⑧ 反向：同一個 rid 送兩次 ⇒ 只寫一次，第二次回第一次的結果",
                a8.Outcome == PeerRpcOutcome.Done && b8.Outcome == PeerRpcOutcome.Done && TaskCount(C) == aBefore8 + 1 && I(a8).Length > 0 && I(a8) == I(b8) && C.Link.RpcServed >= 1,
                $"第一次={a8.OutcomeWord} 第二次={b8.OutcomeWord} index {I(a8)}／{I(b8)} C 的單 {aBefore8} → {TaskCount(C)}");

            // 直接呼叫 Serve 用的執行端：⛔ 只記錄、不執行（正式的會起 Server、發通知）
            var aRec = new List<(string Cmd, Dictionary<string, string> Args)>();
            SCP_CmdResult Record(string iCmd, Dictionary<string, string> iArgs)
            {
                lock (aRec) aRec.Add((iCmd, new Dictionary<string, string>(iArgs, StringComparer.Ordinal)));
                return new SCP_CmdResult { ExitCode = 0 };
            }
            PeerRpcReply ServeC(Dictionary<string, string> iArgs) => SenateTaskServer.Serve(C.Root, C.Name, new PeerRpcCall
            {
                Svc = SenatePeerLink.SvcTask, Cmd = "task", FromId = B.Id, FromName = B.Name, Args = new(iArgs, StringComparer.Ordinal),
            }, Record);

            // ⑩ timeout 上限：大於 300（含 Infinity）⇒ 300；30 照原樣；沒帶不加；解不開 ⇒ 拿掉
            var aGot10 = new List<string>();
            foreach (string? t in new[] { "99999", "Infinity", "30", null, "abc" })
            {
                var a = new Dictionary<string, string> { ["op"] = "create", ["persona"] = "Bob", ["title"] = "x" };
                if (t != null) a["timeout"] = t;
                aRec.Clear();
                ServeC(a);
                aGot10.Add(aRec.Count == 1 ? aRec[0].Args.GetValueOrDefault("timeout", "（沒有）") : $"（記到 {aRec.Count} 筆）");
            }
            Check("⑩ Task Server 收到的 timeout 壓到 300 秒以下：99999／Infinity ⇒ 300，30 照原樣，沒帶不加，解不開拿掉",
                aGot10.SequenceEqual(new[] { "300", "300", "30", "（沒有）", "（沒有）" }) && aRec.Count == 1 && aRec[0].Args.GetValueOrDefault("data_root") == C.Root,
                $"99999／Infinity／30／沒帶／abc ⇒ {string.Join("／", aGot10)}");
            aRec.Clear();

            // ⑪ 同時最多 4 筆：C 的執行端先卡住，B 送 4 筆佔滿，第 5 筆「忙碌」⇒ 沒執行（6）；放行後 4 筆都完成，同一個 rid 重送會執行
            var aArgs11 = new Dictionary<string, string> { ["status"] = "all" };
            int aServed11 = C.Link.RpcServed;
            C.Hold = new ManualResetEventSlim(false);
            List<PeerRpcResult>? aRes11 = null;
            var aT11 = new Thread(() => aRes11 = Flood(B, C.Id, "tasks", aArgs11, PeerRpcMaxRunning, 30)) { IsBackground = true };
            aT11.Start();
            bool aFull11 = Until(() => C.Link.RpcRunning == PeerRpcMaxRunning, 15, All);
            PeerRpcResult a11 = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "tasks", aArgs11, TimeSpan.FromSeconds(10), "selftest-rid-0011");
            SCP_CmdResult c11 = SenateTaskServer.ToCmdResult(a11);
            int aRunning11 = C.Link.RpcRunning;
            C.Hold.Set();
            aT11.Join(TimeSpan.FromSeconds(45));
            C.Hold = null;
            bool aAllDone11 = aRes11 != null && aRes11.All(x => x.Outcome == PeerRpcOutcome.Done);
            int aServedDelta11 = C.Link.RpcServed - aServed11;
            PeerRpcResult b11 = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "tasks", aArgs11, TimeSpan.FromSeconds(20), "selftest-rid-0011");
            Check($"⑪ 同時最多 {PeerRpcMaxRunning} 筆：第 {PeerRpcMaxRunning + 1} 筆拿到「忙碌」（exit 6，沒執行）；放行後前 {PeerRpcMaxRunning} 筆都完成；同一個 rid 之後重送會執行",
                aFull11 && a11.Outcome == PeerRpcOutcome.NotSent && a11.Busy && c11.ExitCode == 6 && aRunning11 == PeerRpcMaxRunning
                && aAllDone11 && aServedDelta11 == PeerRpcMaxRunning && b11.Outcome == PeerRpcOutcome.Done,
                $"佔滿={aFull11} 第 5 筆={a11.OutcomeWord} busy={a11.Busy} exit={c11.ExitCode} 當時在跑={aRunning11} 前 4 筆={string.Join(",", aRes11?.Select(x => x.OutcomeWord) ?? Array.Empty<string>())} "
                + $"C 執行數多 {aServedDelta11} 重送={b11.OutcomeWord}　{c11.Lines.FirstOrDefault()}");

            // ⑥ B 不在允許名單
            SetCfg(C, c => c.TaskClients = new List<string> { A.Id });
            Settle(3, All);
            int aBefore6 = TaskCount(C);
            SCP_CmdResult r6 = Task(B, ("op", "create"), ("persona", "Bob"), ("title", "不該寫進去"), ("criteria", "- [ ] x"));
            Check("⑥ 反向：B 不在 C 的允許名單 ⇒ exit 1，C 沒有新檔", r6.ExitCode == 1 && V(r6, "wrote") == "0" && TaskCount(C) == aBefore6 && TaskCount(B) == 0 && C.Link.RpcRefused > 0,
                $"exit={r6.ExitCode} wrote={V(r6, "wrote")} C 的單 {aBefore6} → {TaskCount(C)}　{r6.Lines.FirstOrDefault(l => l.Contains("允許名單"))}");

            // ⑫ 驗過身分、被擋下的連送：B 一分鐘內送 30 筆 ⇒ 擋下的回覆最多 10 筆（其餘不回 ⇒ 7），C 的 log 最多 1 行，計數照算
            int aRef12 = C.Link.RpcRefused, aLog12 = LogCount(C, "擋下"), aBefore12 = TaskCount(C);
            List<PeerRpcResult> r12 = Flood(B, C.Id, "task", new Dictionary<string, string> { ["op"] = "create", ["persona"] = "Bob", ["title"] = "連送", ["criteria"] = "- [ ] x" }, 30, 3);
            int aReplied12 = r12.Count(x => x.Outcome == PeerRpcOutcome.Refused), aSilent12 = r12.Count(x => x.Outcome == PeerRpcOutcome.Unknown);
            bool aCounted12 = Until(() => C.Link.RpcRefused - aRef12 == 30, 5, All);
            int aLogs12 = LogCount(C, "擋下") - aLog12;
            Check($"⑫ 反向：不在允許名單的 B 一分鐘內連送 30 筆 ⇒ 擋下的回覆最多 {SenatePeerLink.RefuseRepliesPerWindow} 筆、C 的 log 最多 1 行，30 筆都有算進 rpc_refused、沒有新檔",
                aReplied12 >= 1 && aReplied12 <= SenatePeerLink.RefuseRepliesPerWindow && aSilent12 == 30 - aReplied12 && aCounted12 && aLogs12 <= 1 && TaskCount(C) == aBefore12,
                $"有回={aReplied12} 沒回={aSilent12} rpc_refused 多 {C.Link.RpcRefused - aRef12} log 多 {aLogs12} 行 C 的單 {aBefore12} → {TaskCount(C)}");
            SetCfg(C, c => c.TaskClients = new List<string> { B.Id, A.Id });
            Settle(3, All);

            // ⑦ 主機改了轉送中的請求
            A.Link.DebugTamperForward = j =>
            {
                if (j.GetString("t", "") != "req") return j;
                SCP_JsonData x = SCP_JsonParser.Parse(j.ToJson(false));
                x.Set("args", x.GetString("args", "").Replace("原本的標題", "被主機改過的標題"));
                return x;
            };
            // 簽章驗不過 ⇒ C 不回（FAR-58）⇒ B 等到逾時拿 7；直接用連線本體送、只等 3 秒（走入口要多等 20 秒）
            int aBefore7 = TaskCount(C), aRef7 = C.Link.RpcRefused;
            var aArgs7 = new Dictionary<string, string> { ["op"] = "create", ["persona"] = "Bob", ["title"] = "原本的標題", ["criteria"] = "- [ ] x" };
            SCP_CmdResult r7 = SenateTaskServer.ToCmdResult(B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs7, TimeSpan.FromSeconds(3)));
            A.Link.DebugTamperForward = null;
            Check("⑦ 反向：主機改了轉送中的請求 ⇒ C 驗章不過、不執行、不回 ⇒ B 拿到 exit 7（不知道）；C 的 log 有一行「簽章驗不過」",
                r7.ExitCode == 7 && TaskCount(C) == aBefore7 && C.Link.RpcRefused == aRef7 + 1 && LogCount(C, "簽章驗不過") >= 1,
                $"exit={r7.ExitCode} C 的單 {aBefore7} → {TaskCount(C)} rpc_refused 多 {C.Link.RpcRefused - aRef7}　C 的 log：{LastLog(C, "簽章")}");

            // ⑦之二 冒充：另一把金鑰簽、from 填允許名單上的 B（pub 填自己的，或照抄 B 的）
            string aForger = Path.Combine(aBase, "forger").Replace('\\', '/');
            Directory.CreateDirectory(aForger);
            PeerIdentity aIdD = SenatePeerStore.LoadOrCreateIdentity(aForger);
            string aPubB = SenatePeerStore.LoadOrCreateIdentity(B.Root).SignPub;
            int aBefore7b = TaskCount(C), aServed7b = C.Link.RpcServed, aRef7b = C.Link.RpcRefused;
            var aArgs7b = new Dictionary<string, string> { ["op"] = "create", ["persona"] = "Bob", ["title"] = "冒充 B 的單", ["criteria"] = "- [ ] x" };
            B.Link.DebugForgeKey = (aIdD.SignPriv, aIdD.SignPub);
            PeerRpcResult a7b = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs7b, TimeSpan.FromSeconds(3));
            B.Link.DebugForgeKey = (aIdD.SignPriv, aPubB);
            PeerRpcResult b7b = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs7b, TimeSpan.FromSeconds(3));
            B.Link.DebugForgeKey = null;
            Check("⑦之二 反向：用另一把金鑰簽、from 填 B（pub 填自己的／照抄 B 的）⇒ C 擋下、不執行、不回（7），沒有新檔",
                a7b.Outcome == PeerRpcOutcome.Unknown && b7b.Outcome == PeerRpcOutcome.Unknown && C.Link.RpcRefused == aRef7b + 2 && C.Link.RpcLastRefused.Contains("簽章")
                && TaskCount(C) == aBefore7b && C.Link.RpcServed == aServed7b,
                $"pub 自己的={a7b.OutcomeWord} pub 抄 B={b7b.OutcomeWord} rpc_refused 多 {C.Link.RpcRefused - aRef7b} C 的單 {aBefore7b} → {TaskCount(C)} C 執行數 {aServed7b} → {C.Link.RpcServed}　{C.Link.RpcLastRefused}");

            // ⑬ 簽章驗不過的連送 30 筆 ⇒ 一筆都不回、C 的 log 最多 1 行、計數照算
            int aRef13 = C.Link.RpcRefused, aLog13 = LogCount(C, "擋下"), aBefore13 = TaskCount(C), aServed13 = C.Link.RpcServed;
            B.Link.DebugForgeKey = (aIdD.SignPriv, aIdD.SignPub);
            List<PeerRpcResult> r13 = Flood(B, C.Id, "task", aArgs7b, 30, 3);
            B.Link.DebugForgeKey = null;
            bool aCounted13 = Until(() => C.Link.RpcRefused - aRef13 == 30, 5, All);
            int aLogs13 = LogCount(C, "擋下") - aLog13;
            Check("⑬ 反向：簽章驗不過的連送 30 筆 ⇒ C 一筆都不回（都是 7）、log 最多 1 行，30 筆都有算進 rpc_refused、沒有執行",
                r13.All(x => x.Outcome == PeerRpcOutcome.Unknown) && aCounted13 && aLogs13 <= 1 && TaskCount(C) == aBefore13 && C.Link.RpcServed == aServed13,
                $"結果={string.Join(",", r13.GroupBy(x => x.OutcomeWord).Select(g => g.Key + "×" + g.Count()))} rpc_refused 多 {C.Link.RpcRefused - aRef13} log 多 {aLogs13} 行 C 執行數 {aServed13} → {C.Link.RpcServed}");

            // ⑦之三 Task Server 端自己擋 scope：B 自己組一筆帶 scope 的 claim（繞過入口的拿掉）
            string aEvilScope = Path.Combine(aBase, "scope-evil").Replace('\\', '/');
            Directory.CreateDirectory(aEvilScope);
            var aArgs7c = new Dictionary<string, string> { ["op"] = "claim", ["persona"] = "Mallory", ["index"] = V(r1, "index"), ["scope"] = aEvilScope };
            int aServed7c = C.Link.RpcServed;
            PeerRpcResult a7c = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs7c, TimeSpan.FromSeconds(20));
            aRec.Clear();
            PeerRpcReply s7c = ServeC(aArgs7c);
            var aCodingC7c = SCP.Core.Session.SCP_ActivitySessionStore.Load<SCP.Core.Session.SCP_CodingSession>(new SCP_DataRoot(C.Root), "Mallory", SCP.Core.Session.SCP_ActivitySessionKind.Coding);
            Check("⑦之三 反向：別台送來帶 scope 的 claim ⇒ 連線上擋下、Serve 也擋下（exit 1，執行端一筆都沒收到），C 上沒有 Coding 場",
                a7c.Outcome == PeerRpcOutcome.Refused && a7c.Why.Contains("scope") && s7c.Exit == 1 && aRec.Count == 0 && (aCodingC7c == null || !aCodingC7c.active) && C.Link.RpcServed == aServed7c,
                $"連線={a7c.OutcomeWord} Serve exit={s7c.Exit} 執行端收到 {aRec.Count} 筆 C 有場={aCodingC7c?.active}　{a7c.Why}");

            // ⑦之四 persona 帶 ../、memory_topic 帶 ../ 或絕對路徑 ⇒ 擋下，資料根外面不多任何東西，C 的單沒被改
            string aTok = "far57-escape-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            int aBefore7d = TaskCount(C), aServed7d = C.Link.RpcServed;
            string aSnap7d = TaskSnapshot(C);
            var aCases7d = new List<(string Key, Dictionary<string, string> Args)>();
            foreach (string aEvil in new[] { "../" + aTok, "../../../../" + aTok, "..\\" + aTok })
                aCases7d.Add(("persona", new Dictionary<string, string> { ["op"] = "create", ["persona"] = aEvil, ["title"] = "逃出去", ["criteria"] = "- [ ] x" }));
            // memory_topic 存進單子，之後 wrapup 帶 why ⇒ WorkMemory/<topic>/（絕對路徑會讓 Path.Combine 丟掉前面的根）
            foreach (string aEvil in new[] { "../" + aTok, Path.GetFullPath(Path.Combine(aBase, aTok)) })
                aCases7d.Add(("memory_topic", new Dictionary<string, string> { ["op"] = "update", ["persona"] = "Bob", ["index"] = V(r1, "index"), ["memory_topic"] = aEvil }));
            var aOuts7d = new List<string>();
            foreach (var (aKey, aArgs7d) in aCases7d)
            {
                PeerRpcResult x = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", aArgs7d, TimeSpan.FromSeconds(20));
                aOuts7d.Add(x.Outcome == PeerRpcOutcome.Refused && x.Why.Contains(aKey) ? "擋" : x.OutcomeWord + "（" + x.Why + "）");
                aRec.Clear();
                PeerRpcReply s = ServeC(aArgs7d);
                aOuts7d.Add(s.Exit == 1 && aRec.Count == 0 ? "擋" : $"Serve exit={s.Exit} 執行端收到 {aRec.Count} 筆");
            }
            // 往上跳幾層都只會落在 C 的資料根的某個祖先（看第一層）或暫存資料夾底下（整棵看）
            var aLeaks = Directory.GetFileSystemEntries(aBase, "*" + aTok + "*", SearchOption.AllDirectories).ToList();
            for (DirectoryInfo? d = new DirectoryInfo(C.Root).Parent; d != null; d = d.Parent)
                try { aLeaks.AddRange(Directory.GetFileSystemEntries(d.FullName, "*" + aTok + "*")); } catch (Exception) { }
            bool aSnapSame7d = TaskSnapshot(C) == aSnap7d;
            Check("⑦之四 反向：persona=../…、memory_topic=../… 或絕對路徑 ⇒ 連線上擋下、正式的 Serve 也擋下（exit 1），C 的單沒被改，哪裡都沒有多出檔案或資料夾",
                aOuts7d.All(o => o == "擋") && aSnapSame7d && aLeaks.Count == 0 && TaskCount(C) == aBefore7d && C.Link.RpcServed == aServed7d,
                $"結果={string.Join("／", aOuts7d)} C 的單沒變={aSnapSame7d} 多出來的={string.Join("、", aLeaks)}");

            // ⑦之五 RefuseReason 被改壞（測試開關讓它一律放行）⇒ Serve 走到執行那一步，但呼叫到的是只記錄的版本：
            //        ⛔ 不起 Server、不發通知，C 的單和 Coding 場都沒動
            string aSnap7e = TaskSnapshot(C);
            aRec.Clear();
            var aExits7e = new List<int>();
            try
            {
                SenateTaskServer.DebugRefuseNothing = true;
                aExits7e.Add(ServeC(aArgs7c).Exit);
                foreach (var (_, aArgs7d) in aCases7d) aExits7e.Add(ServeC(aArgs7d).Exit);
            }
            finally { SenateTaskServer.DebugRefuseNothing = false; }
            int aRec7e = aRec.Count;
            bool aStillRefuses7e = SenateTaskServer.RefuseReason(aArgs7c) != null;
            var aCodingC7e = SCP.Core.Session.SCP_ActivitySessionStore.Load<SCP.Core.Session.SCP_CodingSession>(new SCP_DataRoot(C.Root), "Mallory", SCP.Core.Session.SCP_ActivitySessionKind.Coding);
            Check("⑦之五 反向：RefuseReason 被改壞（一律放行）⇒ 自我測試呼叫到的只有只記錄的執行端：C 的單、Coding 場都沒動；開關關回去後照常擋",
                aRec7e == 1 + aCases7d.Count && aExits7e.All(x => x == 0) && TaskSnapshot(C) == aSnap7e && (aCodingC7e == null || !aCodingC7e.active) && aStillRefuses7e,
                $"執行端收到 {aRec7e} 筆（應 {1 + aCases7d.Count}）exit={string.Join(",", aExits7e)} C 的單沒變={TaskSnapshot(C) == aSnap7e} C 有場={aCodingC7e?.active} 關回去後照擋={aStillRefuses7e}");
            aRec.Clear();

            // ⑭ `peer-rpc` 依 persona 分 lane；名字沒帶、不合法 ⇒ 共用的 `peer-rpc`；lane 名稱不代表身分
            string L(string? p) => Cmd_PeerRpc.LaneOf(p == null ? "{}" : System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["persona"] = p }));
            var aCmd14 = new Cmd_PeerRpc();
            var aLanes14 = new[] { L("Alice"), L("Bob"), L(null), L("../x"), L("a/b"), Cmd_PeerRpc.LaneOf("{ 壞掉"), Cmd_PeerRpc.LaneOf("{\"persona\":1}") };
            Check("⑭ peer-rpc 依 persona 分 lane：Alice／Bob 各一條；沒帶、../x、a/b、args 壞掉 ⇒ peer-rpc；lane 不代表身分",
                aLanes14.SequenceEqual(new[] { "peer-rpc-Alice", "peer-rpc-Bob", "peer-rpc", "peer-rpc", "peer-rpc", "peer-rpc", "peer-rpc" })
                && !aCmd14.LaneIsPersona("peer-rpc-Alice") && !aCmd14.LaneIsPersona("peer-rpc"),
                $"{string.Join("／", aLanes14)} 代表身分={aCmd14.LaneIsPersona("peer-rpc-Alice")}");

            // ⑤ C 收了不回 ⇒ 不知道
            C.Link.DebugSwallowRequests = true;
            int aBefore5 = TaskCount(C);
            SCP_CmdResult r5 = Task(B, ("op", "create"), ("persona", "Bob"), ("title", "等不到結果"), ("criteria", "- [ ] x"), ("timeout", "3"));
            C.Link.DebugSwallowRequests = false;
            Check("⑤ C 收到請求但不回 ⇒ B 拿到 exit 7（不知道），沒有說成「沒寫」", r5.ExitCode == 7 && V(r5, "wrote").Length == 0 && TaskCount(C) == aBefore5,
                $"exit={r5.ExitCode} {r5.Lines.FirstOrDefault()}");

            // ⑨ 舊版收到 req ⇒ 略過、不斷線、聊天照通
            C.Link.DebugOldVersion = true;
            PeerRpcResult a9 = B.Link.Request(C.Id, SenatePeerLink.SvcTask, "task", new Dictionary<string, string> { ["op"] = "create", ["persona"] = "Bob", ["title"] = "舊版" }, TimeSpan.FromSeconds(3));
            SCP_TavernWriter.WriteMessage(B.Root, SCP_TavernChannels.MainChannelId,
                new SCP_TavernMessage { SenderId = "claudecode", SenderPersona = "Bob", Kind = "chat", Body = "舊版也聽得到" });
            bool aChat = Until(() => SCP_TavernRead.Tail(C.Root, SCP_TavernChannels.MainChannelId, 50).Any(m => m.Body == "舊版也聽得到"), 15, All);
            C.Link.DebugOldVersion = false;
            Check("⑨ 舊版節點收到 req ⇒ 略過、不斷線（請求那一端是「不知道」，聊天照通）", a9.Outcome == PeerRpcOutcome.Unknown && aChat && C.Link.SessionCount == 1 && A.Link.SessionCount == 2,
                $"請求={a9.OutcomeWord} 聊天到 C={aChat} C 的連線={C.Link.SessionCount} 主機連線={A.Link.SessionCount}");

            // ④ C 斷線 ⇒ 確定沒寫，三台都沒有新檔
            SetCfg(C, c => c.Mode = "off");
            Until(() => A.Link.SessionCount == 1 && !B.Link.SeesService(C.Id, SenatePeerLink.SvcTask), 15, All);
            int aBefore4 = TaskCount(C);
            SCP_CmdResult r4 = Task(B, ("op", "create"), ("persona", "Bob"), ("title", "C 不在"), ("criteria", "- [ ] x"));
            Check("④ 反向：Task Server 不在線 ⇒ exit 6，A、B、C 都沒有新檔", r4.ExitCode == 6 && TaskCount(C) == aBefore4 && TaskCount(A) == 0 && TaskCount(B) == 0,
                $"exit={r4.ExitCode} 單數 A/B/C={TaskCount(A)}/{TaskCount(B)}/{TaskCount(C)}　{r4.Lines.FirstOrDefault()}");

            // 這台的連線關了 ⇒ 送不過去，也不在本機寫
            SetCfg(B, c => c.Mode = "off");
            B.Link.Step();
            SCP_CmdResult r4b = Task(B, ("op", "create"), ("persona", "Bob"), ("title", "B 自己關了"), ("criteria", "- [ ] x"));
            Check("④之二 反向：這台的連線關著、task_server 指到別台 ⇒ exit 6，⛔ 不在本機寫", r4b.ExitCode == 6 && TaskCount(B) == 0,
                $"exit={r4b.ExitCode} B 的單={TaskCount(B)}　{r4b.Lines.FirstOrDefault()}");

            // 設定寫壞了 ⇒ 不知道寫哪 ⇒ 擋下
            string aBad = Path.Combine(aBase, "badcfg").Replace('\\', '/');
            Directory.CreateDirectory(aBad);
            Directory.CreateDirectory(SenatePeerStore.Dir(aBad));
            File.WriteAllText(SenatePeerStore.ConfigPath(aBad), "{\"mode\":\"client\",\"task_server\":\"那一台\"}");
            TaskServerRoute aBadRoute = SenateTaskServer.Resolve(aBad);
            File.WriteAllText(SenatePeerStore.ConfigPath(aBad), "{ 這不是 JSON");
            TaskServerRoute aBrokenRoute = SenateTaskServer.Resolve(aBad);
            Check("反向：task_server 寫壞了、或設定檔讀不了 ⇒ 擋下（不當成本機）", aBadRoute.Blocked != null && aBrokenRoute.Blocked != null,
                $"寫壞了：{aBadRoute.Blocked}　讀不了：{aBrokenRoute.Blocked}");
        }
        catch (Exception e)
        {
            aFails.Add("例外");
            oLines.Add("✗ 測試中途炸了：" + e.GetType().Name + "：" + e.Message);
            oLines.Add(e.StackTrace ?? "");
        }
        finally
        {
            SenateTaskServer.SendOverride = aKeepOverride;
            SenateTaskServer.DebugRefuseNothing = false;
            foreach (Node n in aNodes) { try { n.Dispose(); } catch { } }
            if (aFails.Count > 0)
            {
                oLines.Add("── 各節點的紀錄（沒過才印）──");
                foreach (Node n in aNodes) lock (n.Log) foreach (string x in n.Log.TakeLast(12)) oLines.Add($"[{n.Name}] {x}");
                oLines.Add("暫存資料夾留著給你看：" + aBase);
            }
            else { try { Directory.Delete(aBase, true); } catch { } }
        }
        oLines.Add(aFails.Count == 0 ? "✓ 全部通過" : "✗ 沒過 " + aFails.Count + " 項：" + string.Join("、", aFails));
        return aFails;
    }
}
