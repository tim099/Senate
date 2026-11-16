// 區塊職責：`peer-rpc` —— 把一筆服務請求經跨主機連線送到另一台、等結果（FAR-53）；以及「Task Server 在哪一台」的判斷。
// 物理意義：連線只活在**酒館 Server** 裡（SenatePeerJob），CLI 是跑完就結束的行程，碰不到那條線
//           ⇒ 本支是一支 ServerDelegateCmd，指定給酒館 Server（lane `peer-rpc-<persona>`）：CLI 打它 ⇒ 排進酒館 Server 的佇列 ⇒
//           酒館 Server 用手上的連線送 `req`、等 `res`。依 persona 分 lane（FAR-58）：同 lane 依序、不同 lane 同時跑
//           ⇒ Task Server 回得慢時，同一台別的 persona 不會被卡 30 秒就回 queue_busy；同一個 persona 還是一條。
//           入口 `task`（寫單）與 `tasks`（讀單）在 Task Server 是別台時改走這一支；⛔ 那時候絕不在本機寫。
// 數值影響：一次呼叫 ＝ 一筆 req／res。等結果的時間預設 60 秒（`wait`）。
// ⚠ 結果怎麼判（跟 `task` 的三態一致）：
//   · `peer_outcome=not_sent` ⇒ 確定沒送出（線沒開、Server 那台不在線、名單上沒有它、主機轉不過去）⇒ 一定沒寫
//   · `peer_outcome=unknown` ⇒ 送出了但沒等到結果 ⇒ 不知道（⛔ 先回讀，別直接重打）
//   · `peer_outcome=refused` ⇒ Server 那台擋下（允許名單、簽章、指令範圍）⇒ 一定沒寫
//   · `peer_outcome=done` ⇒ Server 那台跑完，`peer_exit` 是它的 exit code
//   本支自己的委派沒成（酒館 Server 起不來…）⇒ 沒有 `peer_outcome`，看 `delegate_failure`。
#nullable enable
using System.Text;
using System.Text.Json;
using SCP.Core.Cmd;
using SCP.Core.Proc;

namespace Senate.Core;

public sealed class Cmd_PeerRpc : ServerDelegateCmd
{
    public override string Name => "peer-rpc";
    public override string Category => SCP_CmdCategory.Tavern;

    public override string Summary =>
        "跨主機連線：把一筆服務請求（目前只有 task／tasks）送到另一台執行、等結果 —— 由酒館 Server 執行（內部通道，人請打 `task`／`tasks`）";

    public override string Details =>
        "⚠ 內部通道：`task`／`tasks` 在 Task Server 是別台時自己會走這一支。\n"
        + "⚠ 結果看 🔢 peer_outcome：not_sent（一定沒寫）／unknown（不知道，先回讀）／refused（被擋下，一定沒寫）／done（看 peer_exit）。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("peer-rpc --arg to=<節點 id> --arg cmd=tasks --arg-file args_json=<檔>");

    protected override string ServerId => SCP_ServerIds.Tavern;

    public const string RpcLane = "peer-rpc";
    protected override string Lane(SCP_CmdArgs iArgs) => LaneOf(iArgs.Get("args_json"));
    /// <summary>lane 名稱只拿來分開排隊，⛔ 不代表身分。</summary>
    protected override bool IsPersonaLane(string iLane) => false;
    internal bool LaneIsPersona(string iLane) => IsPersonaLane(iLane);

    /// <summary>
    /// `args_json` 裡的 `persona` 合法 ⇒ `peer-rpc-&lt;persona&gt;`；沒帶、解不開、名字不合法 ⇒ `peer-rpc`。
    /// ⚠ lane 會變成酒館 Server 佇列底下的資料夾名 ⇒ 除了 persona 名字的規則，也不收這個系統檔名不能用的字元。
    /// </summary>
    internal static string LaneOf(string iArgsJson)
    {
        try
        {
            Dictionary<string, string>? a = JsonSerializer.Deserialize<Dictionary<string, string>>(iArgsJson);
            string p = a != null && a.TryGetValue("persona", out string? v) && v != null ? v.Trim() : "";
            if (p.Length <= 64 && SCP.Core.Letters.SCP_RegisteredMail.IsValidPersonaName(p) && p.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                return RpcLane + "-" + p;
        }
        catch (Exception) { }
        return RpcLane;
    }

    /// <summary>酒館 Server 剛起來時，線還沒接上：在這段時間內「對方不在線」多等一下再判。</summary>
    const int WarmupSec = 30;
    const int WarmupWaitSec = 15;

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("to", "要送到哪一台（節點 id，16 碼）", iRequired: true),
                new SCP_CmdArgSpec("svc", "服務（目前只有 task）", iDefault: SenatePeerLink.SvcTask, iChoices: new[] { SenatePeerLink.SvcTask }),
                new SCP_CmdArgSpec("cmd", "要在那一台跑的指令", iRequired: true, iChoices: SenatePeerLink.TaskCmds),
                new SCP_CmdArgSpec("args_json", "指令的參數（JSON 物件，值一律字串）。⛔ 不帶路徑類參數（data_root 等由那一台自己算）", iRequired: true),
                new SCP_CmdArgSpec("wait", "等那一台回結果的秒數（預設 60）", iDefault: "60"),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aTo = iArgs.Get("to").Trim().ToLowerInvariant();
        string aSvc = iArgs.Get("svc").Trim();
        string aCmd = iArgs.Get("cmd").Trim();
        if (!int.TryParse(iArgs.Get("wait"), out int aWait) || aWait < 1) aWait = 60;
        Dictionary<string, string>? aArgs;
        try { aArgs = JsonSerializer.Deserialize<Dictionary<string, string>>(iArgs.Get("args_json")); }
        catch (Exception e) { return SCP_CmdResult.Fail(2, "✗ args_json 解析不了：" + e.Message); }
        if (aArgs == null) return SCP_CmdResult.Fail(2, "✗ args_json 是 null —— 要的是一個 JSON 物件");

        PeerRpcResult r;
        DateTime aEnd = DateTime.UtcNow.AddSeconds(WarmupWaitSec);
        while (true)
        {
            SenatePeerLink? aLink = SenatePeerJob.Link;
            r = aLink == null
                ? new PeerRpcResult { Outcome = PeerRpcOutcome.NotSent, ServerId = aTo, ServerName = aTo, Why = "這台的酒館 Server 沒有在跑跨主機連線（peer 模式是 off，或這顆不是拿著名片的那一顆）" }
                : aLink.Request(aTo, aSvc, aCmd, aArgs, TimeSpan.FromSeconds(aWait));
            // 剛起來（線還在接）而對方「不在線」⇒ 再等一下；其他結果馬上回
            bool aWarm = aLink == null || aLink.Age.TotalSeconds < WarmupSec;
            if (r.Outcome != PeerRpcOutcome.NotSent || !aWarm || DateTime.UtcNow >= aEnd) break;
            Thread.Sleep(500);
        }
        return SenateTaskServer.ToCmdResult(r);
    }
}

/// <summary>Task Server 在哪一台（FAR-53）。</summary>
public sealed class TaskServerRoute
{
    public bool Remote;
    public string Id = "", Name = "";
    /// <summary>非 null ⇒ 不知道該寫在哪 ⇒ ⛔ 一律不寫（exit 6），原因在這裡。</summary>
    public string? Blocked;
    public string Label => Name.Length > 0 && Name != Id ? $"{Name}（{Id}）" : Id;
}

public static class SenateTaskServer
{
    /// <summary>
    /// 送一筆到 Task Server 的那一步（預設 ＝ `peer-rpc`，經酒館 Server 的連線）。
    /// ⚠ 只給自我測試換：淨室裡沒有酒館 Server，直接接到暫存資料夾裡的連線本體。
    /// </summary>
    public static Func<TaskServerRoute, string, Dictionary<string, string>, int, SCP_CmdResult>? SendOverride;

    /// <summary>`req` 裡不帶、由 Task Server 那一台自己算的參數（路徑都是那一台的）。</summary>
    public static readonly string[] LocalOnlyArgs =
        { "data_root", "letters_root", "bank_root", "repo_root", "glossary_root", "activities_root", "out_json", SCP_CmdArgs.ForwardedExplicitKey };

    /// <summary>
    /// Task Server 那一端**帶了就擋**的參數（⛔ 不靠請求端自律）：`scope` 是請求端那一台的絕對路徑，
    /// 在 Task Server 上會走 `claim` 的開 Coding 場那一步 ⇒ 用別台給的路徑、替任何 persona 開一場。
    /// </summary>
    public static readonly string[] RefusedArgs = { "scope" };

    /// <summary>
    /// 值是名字的參數：會被拼進 Task Server 的路徑 ⇒ 名字要合法（只能一層、不能是絕對路徑）。
    /// persona 類 ⇒ `Letters/&lt;persona&gt;/…`；`memory_topic` 存進單子，之後 `wrapup` 帶 `why` 時 ⇒ `WorkMemory/&lt;topic&gt;/`。
    /// </summary>
    static readonly string[] NameArgs = { "persona", "target_persona", "assignee", "memory_topic" };

    /// <summary>
    /// ⚠ 只給自我測試：<see cref="RefuseReason"/> 一律放行（確認「就算它被改壞，自我測試也只呼叫到只記錄的執行端」）。
    /// 打開的一方要用 try/finally 關回去。
    /// </summary>
    internal static bool DebugRefuseNothing;

    /// <summary>別台帶來的 `timeout`（Task Server 這邊等自己的寫入端多久）最多幾秒。</summary>
    public const int MaxRemoteTimeoutSec = 300;

    /// <summary>
    /// 別台帶來的 `timeout` 壓到 <see cref="MaxRemoteTimeoutSec"/> 以下：大於上限的（含 Infinity）⇒ 上限；
    /// 解不開、不是正數 ⇒ 拿掉（照寫入端的預設 60 秒）；沒帶 ⇒ 不加。一律寫成整數（哪個語系都讀得一樣）。
    /// </summary>
    internal static void ClampTimeout(Dictionary<string, string> ioArgs)
    {
        if (!ioArgs.TryGetValue("timeout", out string? v)) return;
        if (!double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)
            || double.IsNaN(d) || d <= 0)
        {
            ioArgs.Remove("timeout");
            return;
        }
        int aSec = d >= MaxRemoteTimeoutSec ? MaxRemoteTimeoutSec : (int)Math.Ceiling(d);
        ioArgs["timeout"] = aSec.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 別台送來的參數這一台收不收：不收 ⇒ 回原因（⇒ exit 1，什麼都不跑）；收 ⇒ null。
    /// 跨主機連線收到 `req`、以及 <see cref="Serve"/> 都會先過這一關。
    /// </summary>
    public static string? RefuseReason(IReadOnlyDictionary<string, string> iArgs)
    {
        if (DebugRefuseNothing) return null;
        foreach (string k in RefusedArgs)
            if (iArgs.ContainsKey(k)) return $"不收 {k}（那是請求端那一台的路徑；Coding 場只開在請求端）";
        foreach (string k in NameArgs)
            if (iArgs.TryGetValue(k, out string? v) && v.Trim().Length > 0 && !SCP.Core.Letters.SCP_RegisteredMail.IsValidPersonaName(v))
                return $"{k} 的名字不合法（不能有 / \\ : 或 ..）";
        return null;
    }

    /// <summary>照這台的 `peer_config.json` 判斷 Task 要寫在哪一台。</summary>
    public static TaskServerRoute Resolve(string iDataRoot)
    {
        var r = new TaskServerRoute();
        PeerConfig c = SenatePeerStore.LoadConfig(iDataRoot);
        if (c.ReadFailed) { r.Blocked = "跨主機連線的設定檔讀不了 ⇒ 不知道 Task Server 是哪一台（" + c.Error + "）"; return r; }
        if (c.TaskServerError.Length > 0) { r.Blocked = c.TaskServerError; return r; }
        if (c.TaskServer.Length == 0 || c.ServesTask) return r;
        string aMe = File.Exists(SenatePeerStore.IdentityPath(iDataRoot)) ? SenatePeerStore.LoadOrCreateIdentity(iDataRoot).Id : "";
        if (c.TaskServer == aMe) return r;   // 指到自己 ＝ 本機
        r.Remote = true;
        r.Id = c.TaskServer;
        r.Name = KnownName(iDataRoot, c.TaskServer);
        if (c.Mode == "off") r.Blocked = $"task_server 指到 {r.Label}，但這台的跨主機連線是關的（mode=off）⇒ 送不過去";
        return r;
    }

    /// <summary>節點名：直接連過的記在狀態檔；經主機才看得到的（另一端看另一端）從在線名單找。都沒有 ⇒ 空。</summary>
    public static string KnownName(string iDataRoot, string iId)
    {
        try
        {
            string n = SenatePeerStore.ReadState(iDataRoot)["nodes"][iId].GetString("name", "");
            if (n.Length > 0) return n;
            return SCP.Core.Tavern.SCP_PeerPresence.Read(iDataRoot).Nodes.FirstOrDefault(x => x.Id == iId)?.Name ?? "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>把一筆送到 Task Server，回 `peer-rpc` 形狀的結果。</summary>
    public static SCP_CmdResult Send(TaskServerRoute iRoute, string iCmd, Dictionary<string, string> iArgs, int iWaitSec)
    {
        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in iArgs) if (Array.IndexOf(LocalOnlyArgs, kv.Key) < 0) aArgs[kv.Key] = kv.Value;
        if (SendOverride != null) return SendOverride(iRoute, iCmd, aArgs, iWaitSec);
        return SCP_CmdRegistry.Dispatch("peer-rpc", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["to"] = iRoute.Id,
            ["svc"] = SenatePeerLink.SvcTask,
            ["cmd"] = iCmd,
            ["args_json"] = JsonSerializer.Serialize(aArgs),
            ["wait"] = iWaitSec.ToString(),
            // 本機等酒館 Server 的時間要比它等對面的久：不然對面回來之前這邊先放棄，判成「不知道」
            ["timeout"] = (iWaitSec + 30).ToString(),
        });
    }

    /// <summary>連線本體的結果 ⇒ `peer-rpc` 的回傳（exit：done ⇒ 對面的；refused ⇒ 1；not_sent ⇒ 6；unknown ⇒ 7）。</summary>
    public static SCP_CmdResult ToCmdResult(PeerRpcResult r)
    {
        var aRes = new SCP_CmdResult();
        aRes.AddValue("peer_outcome", r.OutcomeWord);
        aRes.AddValue("peer_server_id", r.ServerId);
        aRes.AddValue("peer_server", r.ServerName);
        switch (r.Outcome)
        {
            case PeerRpcOutcome.NotSent:
                aRes.ExitCode = 6;
                aRes.Lines.Add((r.Busy ? "✗ Task Server 沒有執行這一筆：" : "✗ 沒有送到 Task Server：") + r.Why);
                if (r.Busy) aRes.AddValue("peer_busy", "1");
                break;
            case PeerRpcOutcome.Unknown:
                aRes.ExitCode = 7;
                aRes.Lines.Add("⛔ 不知道 Task Server 有沒有執行：" + r.Why);
                break;
            case PeerRpcOutcome.Refused:
                aRes.ExitCode = 1;
                aRes.Lines.AddRange(r.Reply.Lines);
                aRes.AddValue("peer_why", r.Why);
                break;
            default:
                aRes.ExitCode = r.Reply.Exit;
                aRes.Lines.AddRange(r.Reply.Lines);
                aRes.AddValue("peer_exit", r.Reply.Exit.ToString());
                foreach (var kv in r.Reply.Values) aRes.AddValue("r." + kv.Key, kv.Value);
                if (r.Reply.Report.Length > 0) aRes.AddValue("peer_report", r.Reply.Report);
                break;
        }
        return aRes;
    }

    /// <summary>
    /// `peer-rpc` 的結果 ⇒ 入口該回的 exit：done ⇒ 對面的 exit；refused ⇒ 1；not_sent ⇒ 6；unknown ⇒ 7。
    /// 本支自己的委派沒成（沒有 `peer_outcome`）⇒ 逾時／不明是 7，其他是 6（送不進酒館 Server ＝ 沒送出）。
    /// </summary>
    public static int ExitOf(SCP_CmdResult r, out string oOutcome)
    {
        oOutcome = Value(r, "peer_outcome");
        switch (oOutcome)
        {
            case "done": return int.TryParse(Value(r, "peer_exit"), out int x) ? x : 7;
            case "refused": return 1;
            case "not_sent": return 6;
            case "unknown": return 7;
        }
        string aFail = Value(r, "delegate_failure");
        oOutcome = aFail is "timeout" or "unknown" or "" ? "unknown" : "not_sent";
        return oOutcome == "unknown" ? 7 : 6;
    }

    /// <summary>對面回的值（`peer-rpc` 加了 `r.` 前綴，避免跟本支自己的值混在一起）。</summary>
    public static IEnumerable<KeyValuePair<string, string>> RemoteValues(SCP_CmdResult r)
    {
        foreach (var kv in r.Values)
            if (kv.Key.StartsWith("r.", StringComparison.Ordinal)) yield return new KeyValuePair<string, string>(kv.Key.Substring(2), kv.Value);
    }

    public static string Value(SCP_CmdResult r, string k)
    {
        foreach (var kv in r.Values) if (kv.Key == k) return kv.Value;
        return "";
    }

    // ── Task Server 那一端：執行別台送來的請求 ─────────────────────────

    /// <summary>
    /// 在這台（Task Server）跑別台送來的 `task`／`tasks`。資料根用這台自己的；回傳檔的路徑改成「在 這台 上：路徑」，
    /// 內文帶回去（請求端那台沒有這個檔）。⚠ `task` 走它原本的路：酒館通知、工作記憶都在這一台做。
    /// <paramref name="iRun"/>：真正執行指令的那一步（預設 `SCP_CmdRegistry.Dispatch`）。
    /// ⚠ 只給自我測試換成只記錄、不執行的版本：正式的會起 Server、發通知（FAR-58）。
    /// </summary>
    public static PeerRpcReply Serve(string iDataRoot, string iNodeName, PeerRpcCall iCall,
        Func<string, Dictionary<string, string>, SCP_CmdResult>? iRun = null)
    {
        string? aBad = RefuseReason(iCall.Args);
        if (aBad != null)
        {
            var aNo = new PeerRpcReply { Exit = 1 };
            aNo.Lines.Add($"⛔ Task Server {iNodeName} 擋下這一筆：{aBad}（什麼都沒寫）");
            return aNo;
        }
        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in iCall.Args) if (Array.IndexOf(LocalOnlyArgs, kv.Key) < 0) aArgs[kv.Key] = kv.Value;
        ClampTimeout(aArgs);   // 請求帶的 timeout 會交給寫入端：⛔ 不讓別台叫這台等多久都可以
        aArgs["data_root"] = iDataRoot;
        SCP_CmdResult r = iRun != null ? iRun(iCall.Cmd, aArgs) : SCP_CmdRegistry.Dispatch(iCall.Cmd, aArgs);
        var aReply = new PeerRpcReply { Exit = r.ExitCode };
        aReply.Lines.AddRange(r.Lines);
        foreach (var kv in r.Values) aReply.Values.Add(kv);
        foreach (string aPath in r.Outputs)
        {
            aReply.Lines.Add($"📄 回傳檔（在 {iNodeName} 上）：{aPath}");
            if (aReply.Report.Length > 0) continue;
            try { if (File.Exists(aPath)) aReply.Report = File.ReadAllText(aPath, Encoding.UTF8); }
            catch (Exception e) { aReply.Lines.Add($"⚠ 回傳檔讀不了（{e.GetType().Name}）—— 內文沒有帶回去"); }
        }
        return aReply;
    }

    // ── 讀單（`tasks`）────────────────────────────────────────────────

    static readonly string[] ReadArgs = { "index", "persona", "mine", "status", "type", "project", "wrapup" };

    /// <summary>
    /// `senate cmd tasks` 在 Task Server 是別台時：改送過去讀，回對面的結果。Task 在本機 ⇒ null（照舊在本機讀）。
    /// <paramref name="iUserKeys"/>：使用者自己打的參數名（⛔ 宿主補的 data_root 不算）。
    /// </summary>
    public static SCP_CmdResult? TryForwardRead(Dictionary<string, string> iRawArgs, IReadOnlyCollection<string> iUserKeys)
    {
        if (!iRawArgs.TryGetValue("data_root", out string? aRoot) || string.IsNullOrEmpty(aRoot) || !Directory.Exists(aRoot)) return null;
        TaskServerRoute aRoute = Resolve(aRoot.Replace('\\', '/').TrimEnd('/'));
        if (!aRoute.Remote && aRoute.Blocked == null) return null;
        if (aRoute.Blocked != null)
            return SCP_CmdResult.Fail(6, "✗ 讀不了任務單：" + aRoute.Blocked, "  ⛔ 不改讀本機那一份（那可能是舊的，而它看起來正常）");
        if (iUserKeys.Contains("out_json"))
            return SCP_CmdResult.Fail(2, $"✗ Task 在 {aRoute.Label}，out_json 沒辦法用（檔案會寫在那一台）—— 拿掉 out_json 再跑");
        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in ReadArgs) if (iRawArgs.TryGetValue(k, out string? v)) aArgs[k] = v;
        SCP_CmdResult aRpc = Send(aRoute, "tasks", aArgs, 60);
        int aExit = ExitOf(aRpc, out string aOutcome);
        var r = new SCP_CmdResult { ExitCode = aExit };
        r.AddValue("task_server", aRoute.Id);
        if (aOutcome == "done")
        {
            r.Lines.Add($"⤷ 於 Task Server {Label(aRoute, aRpc)} 讀單（這台的 Tasks 不是最新的，不讀）");
            r.Lines.AddRange(aRpc.Lines);
            foreach (var kv in RemoteValues(aRpc)) r.AddValue(kv.Key, kv.Value);
            return r;
        }
        r.Lines.Add(aOutcome == "refused"
            ? $"⛔ Task Server {Label(aRoute, aRpc)} 擋下了讀單"
            : $"✗ 沒讀到 Task Server {Label(aRoute, aRpc)} 上的任務單（{aOutcome}）");
        foreach (string l in aRpc.Lines) r.Lines.Add("  │ " + l);
        return r;
    }

    /// <summary>結果裡帶的節點名（對面自己報的）優先；沒有 ⇒ 這台記得的名字。</summary>
    public static string Label(TaskServerRoute iRoute, SCP_CmdResult iRpc)
    {
        string aName = Value(iRpc, "peer_server");
        return aName.Length > 0 && aName != iRoute.Id ? $"{aName}（{iRoute.Id}）" : iRoute.Label;
    }
}
