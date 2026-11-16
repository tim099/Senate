// 區塊職責：**跨主機連線上的服務請求**（FAR-53）—— 把 Task 指令送到 Task Server 那一台執行、結果傳回來。
// 物理意義（Tim 2026-11-16：主機只是被連的那一端；Task 等服務要放哪一台可以手動切，指令直接串到那一台）：
//   · 兩種 frame：`req`（請求）、`res`（結果），走既有的加密連線。主機看到 `to` 不是自己 ⇒ 原封不動轉給那條線；
//     轉不過去 ⇒ 回 `rpcfail`（請求端據此說「確定沒寫」）。舊版收到不認得的種類會略過（Handle 的 default）⇒ 不斷線，
//     請求端等不到結果 ⇒「不知道」。
//   · **每個 req／res 都用發送者自己的身分金鑰簽章**：連線的加密只保護每一段線；Server 是另一端時請求會經過主機，
//     主機看得到也改得了 ⇒ 收的一方驗 `IdOf(pub) == from` 與簽章 ⇒ 主機改不了內容、也冒充不了別的節點。
//   · 擋重放：`ts` 差超過 2 分鐘拒收；Server 記住 10 分鐘內看過的 (from, rid)，看過的不再執行，回上一次的結果。
//   · Server 那一端只在這幾件都成立時執行：這台設成 Task Server（`task_server=self`）、`from` 在 `task_clients`、
//     `svc=task`、`cmd` 是 `task` 或 `tasks`。其餘一律回「被擋下」（exit 1，什麼都沒寫）並記一筆。
//   · 擋下的限頻（FAR-58）：簽章還沒驗過（rid 不對、簽章驗不過）⇒ ⛔ 一律不回（`from` 是對方自己填的，回了等於誰都能叫這台簽東西），
//     log 每條線每分鐘 1 行；驗過的 ⇒ 依 `from` 每分鐘最多回 10 筆、log 1 行（不在允許名單的共用一份：身分可以無限生）。
//     超過的只計數（`rpc_refused`），下一次寫 log 時一起報。
//   · 同時最多執行 RpcMaxRunning 筆（FAR-58）：滿了 ⇒ 回簽章過的「忙碌」（`busy`＋`refused`），rid 不記 ⇒ 同一個 rid 重送會執行。
//     新版請求端判成「確定沒寫」（exit 6）；舊版只看得懂 `refused` ⇒ exit 1，一樣是什麼都沒寫。
// 數值影響：請求端在呼叫者的執行緒上等（酒館 Server 的 `peer-rpc-<persona>` lane）；執行端在背景執行緒跑 ⇒ ⛔ 不擋收訊迴圈。
//           回傳的行數與回傳檔內文有上限（超過就截斷並註明），frame 沿用 1 MB 上限。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Json;

namespace Senate.Core;

/// <summary>別台送來、要在這台執行的一筆服務請求。</summary>
public sealed class PeerRpcCall
{
    public string Svc = "", Cmd = "", FromId = "", FromName = "";
    public Dictionary<string, string> Args = new(StringComparer.Ordinal);
}

/// <summary>執行完的結果（送回請求端）。</summary>
public sealed class PeerRpcReply
{
    public int Exit;
    public List<string> Lines = new();
    public List<KeyValuePair<string, string>> Values = new();
    public string Report = "";   // 回傳檔的內文（請求端那台沒有這個檔，帶過去給它落一份）
}

public enum PeerRpcOutcome
{
    NotSent,   // 確定沒送出（或主機說轉不過去）⇒ 一定沒寫
    Unknown,   // 送出了，但沒等到結果 ⇒ 不知道寫了沒
    Refused,   // Server 那一台擋下 ⇒ 一定沒寫
    Done,      // Server 那一台跑完了，結果在 Reply
}

public sealed class PeerRpcResult
{
    public PeerRpcOutcome Outcome;
    /// <summary>Server 那一台收到了，但同時在跑的已滿 ⇒ 沒有執行（Outcome 是 NotSent）。</summary>
    public bool Busy;
    public string Why = "";
    public string ServerId = "", ServerName = "";
    public PeerRpcReply Reply = new();

    public string OutcomeWord => Outcome switch
    {
        PeerRpcOutcome.NotSent => "not_sent",
        PeerRpcOutcome.Unknown => "unknown",
        PeerRpcOutcome.Refused => "refused",
        _ => "done",
    };
}

public sealed partial class SenatePeerLink
{
    public const string SvcTask = "task";
    public static readonly string[] TaskCmds = { "task", "tasks" };
    public const int RpcSkewSec = 120;
    const int RpcSeenSec = 600;
    const int RpcMaxLinesChars = 100_000;
    const int RpcMaxReportChars = 120_000;
    /// <summary>同時最多執行幾筆：`task` 寫入最後都排 main Server 同一條 `task-write` lane，開再多也只是排隊。</summary>
    public const int RpcMaxRunning = 4;
    const int RefuseWindowSec = 60;
    /// <summary>驗過身分、被擋下的：每個 `from` 每分鐘最多回幾筆。</summary>
    public const int RefuseRepliesPerWindow = 10;

    sealed class Pending
    {
        public string To = "", ToName = "";
        public Session Route = null!;
        public readonly TaskCompletionSource<PeerRpcResult> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    sealed class SeenRpc
    {
        public DateTime At;
        public SCP_JsonData? Res;   // null ＝ 還在跑
    }

    /// <summary>擋下的限頻：一個 key 一窗一分鐘。</summary>
    sealed class RefuseWindow
    {
        public DateTime Start;
        public int Replies;
        public bool Logged;
        public int Muted;   // 擋下了、但限頻沒寫 log 的（下一次寫 log 時一起報）
    }

    readonly Dictionary<string, Pending> m_Pending = new(StringComparer.Ordinal);
    readonly Dictionary<string, SeenRpc> m_Seen = new(StringComparer.Ordinal);
    readonly Dictionary<string, RefuseWindow> m_RefuseWin = new(StringComparer.Ordinal);
    int m_RpcServed, m_RpcRefused, m_RpcRunning;
    string m_RpcLastRefused = "";
    readonly DateTime m_CreatedUtc = DateTime.UtcNow;

    /// <summary>這個連線本體起來多久了（酒館 Server 剛起來時，線還沒接上，請求端會多等一下）。</summary>
    public TimeSpan Age => DateTime.UtcNow - m_CreatedUtc;

    public int RpcServed => m_RpcServed;
    public int RpcRefused => m_RpcRefused;
    public int RpcRunning => Volatile.Read(ref m_RpcRunning);
    public string RpcLastRefused => m_RpcLastRefused;

    // ── 自我測試用的開關 ─────────────────────────────────────────────
    /// <summary>當成舊版：收到 req／res 一律略過（不斷線）。</summary>
    internal bool DebugOldVersion;
    /// <summary>收下請求、記住 rid，但不執行、不回（模擬 Server 那一端跑到一半斷掉）。</summary>
    internal bool DebugSwallowRequests;
    /// <summary>主機轉送時改內容（模擬中間人）。</summary>
    internal Func<SCP_JsonData, SCP_JsonData>? DebugTamperForward;
    /// <summary>送請求時 `from` 照填這台，簽章改用這把金鑰、`pub` 改填這個（模擬冒充別台）。</summary>
    internal (string Priv, string Pub)? DebugForgeKey;

    List<string> MyServes()
        => m_Cfg.ServesTask && m_Cfg.TaskServerError.Length == 0 ? new List<string> { SvcTask } : new List<string>();

    /// <summary>在線名單裡找某個節點（每條線上對方報的名單；主機那條線也帶其他另一端）。</summary>
    PeerNodePresence? FindNode(string iId)
    {
        foreach (Session s in Snapshot())
        {
            if (s.Closed) continue;
            PeerNodePresence? n = s.Remote.FirstOrDefault(x => x.Id == iId);
            if (n != null) return n;
        }
        return null;
    }

    /// <summary>在線名單上看得到這個節點、而且它說自己提供這個服務。</summary>
    public bool SeesService(string iId, string iSvc) => FindNode(iId)?.Serves.Contains(iSvc) == true;

    /// <summary>送給某個節點要走哪條線：直接連著 ⇒ 那條；這台是另一端 ⇒ 交給主機轉。</summary>
    Session? RouteTo(string iId)
    {
        Session? s;
        lock (m_Gate) m_Sessions.TryGetValue(iId, out s);
        if (s != null && !s.Closed && s.SyncReceived) return s;
        if (m_Cfg.Mode != "client") return null;
        return Snapshot().FirstOrDefault(x => !x.ServerSide && !x.Closed && x.SyncReceived);
    }

    /// <summary>簽章的內文：每一格前面帶長度（⛔ 不用單純的分隔符號 —— 內文裡本來就可能有那個符號，欄位會被挪動）。</summary>
    static string Canon(params string[] iFields)
    {
        var sb = new StringBuilder(SenatePeerCrypto.Proto);
        foreach (string f in iFields) sb.Append('|').Append(f.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(f);
        return sb.ToString();
    }

    static string ReqText(string rid, string svc, string from, string to, string ts, string cmd, string args)
        => Canon("req", rid, svc, from, to, ts, cmd, args);

    static string ResText(string rid, string from, string to, string ts, string exit, string body)
        => Canon("res", rid, from, to, ts, exit, body);

    static bool VerifySigned(SCP_JsonData j, string iFrom, string iText)
    {
        string aPub = j.GetString("pub", "");
        try { if (aPub.Length == 0 || SenatePeerCrypto.IdOf(aPub) != iFrom) return false; }
        catch (Exception) { return false; }
        return SenatePeerCrypto.Verify(aPub, Encoding.UTF8.GetBytes(iText), j.GetString("sig", ""));
    }

    // ── 請求端 ───────────────────────────────────────────────────────

    /// <summary>
    /// 把一筆請求送到 <paramref name="iTo"/> 並等結果。⛔ 不會在本機執行任何東西。
    /// 回的四種結果見 <see cref="PeerRpcOutcome"/>。<paramref name="iRid"/> 只給自我測試（同一個 rid 送兩次）。
    /// </summary>
    public PeerRpcResult Request(string iTo, string iSvc, string iCmd, IReadOnlyDictionary<string, string> iArgs, TimeSpan iTimeout, string? iRid = null)
    {
        var r = new PeerRpcResult { ServerId = iTo, ServerName = iTo };
        PeerRpcResult Fail(PeerRpcOutcome o, string why) { r.Outcome = o; r.Why = why; return r; }

        if (m_Disposed) return Fail(PeerRpcOutcome.NotSent, "連線已經收掉了");
        if (m_Cfg.Mode == "off") return Fail(PeerRpcOutcome.NotSent, "這台的跨主機連線是關的（mode=off）");
        PeerIdentity? aMe = m_Me;
        if (aMe == null) return Fail(PeerRpcOutcome.NotSent, "這台的連線還沒起來");
        if (iTo == aMe.Id) return Fail(PeerRpcOutcome.NotSent, "目標就是這台自己（要在這台寫，task_server 設 self 或留空）");
        PeerNodePresence? aNode = FindNode(iTo);
        if (aNode == null) return Fail(PeerRpcOutcome.NotSent, $"{iTo} 不在線上 —— 連線上看不到它");
        r.ServerName = aNode.Name;
        if (!aNode.Serves.Contains(iSvc))
            return Fail(PeerRpcOutcome.NotSent, $"{aNode.Name}（{iTo}）在線，但它沒有說自己是 Task Server（那台的 task_server 不是 self）");
        Session? aRoute = RouteTo(iTo);
        if (aRoute == null) return Fail(PeerRpcOutcome.NotSent, $"找不到通往 {aNode.Name} 的線");

        var aArgs = SCP_JsonData.NewObject();
        foreach (var kv in iArgs) aArgs.Set(kv.Key, kv.Value);
        string aArgsJson = aArgs.ToJson(false);
        string aRid = iRid ?? SenatePeerCrypto.RandomToken(12);
        string aTs = SenatePeerStore.NowIso();
        SCP_JsonData f = Obj("req");
        f.Set("rid", aRid); f.Set("svc", iSvc); f.Set("from", aMe.Id); f.Set("to", iTo); f.Set("ts", aTs); f.Set("cmd", iCmd);
        (string Priv, string Pub) aKey = DebugForgeKey ?? (aMe.SignPriv, aMe.SignPub);
        f.Set("args", aArgsJson); f.Set("pub", aKey.Pub);
        f.Set("sig", SenatePeerCrypto.Sign(aKey.Priv, Encoding.UTF8.GetBytes(ReqText(aRid, iSvc, aMe.Id, iTo, aTs, iCmd, aArgsJson))));
        if (Encoding.UTF8.GetByteCount(f.ToJson(false)) > MaxFrameBytes - 4096)
            return Fail(PeerRpcOutcome.NotSent, "請求太大（超過一個 frame 的上限 1 MB）");

        var p = new Pending { To = iTo, ToName = aNode.Name, Route = aRoute };
        lock (m_Pending)
        {
            if (m_Pending.ContainsKey(aRid)) return Fail(PeerRpcOutcome.NotSent, "同一個 rid 已經在等結果了");
            m_Pending[aRid] = p;
        }
        try
        {
            if (aRoute.Closed) return Fail(PeerRpcOutcome.NotSent, "要送的時候線已經斷了");
            Send(aRoute, f);   // 送不出去 ⇒ Close ⇒ FailPendingVia 把這一筆判成「不知道」（可能送出一部分）
            if (!p.Done.Task.Wait(iTimeout))
                return Fail(PeerRpcOutcome.Unknown, $"送出了，等了 {iTimeout.TotalSeconds:0} 秒沒有收到 {aNode.Name} 的結果");
            PeerRpcResult aGot = p.Done.Task.Result;
            if (aGot.ServerName.Length == 0) aGot.ServerName = aNode.Name;
            return aGot;
        }
        finally { lock (m_Pending) m_Pending.Remove(aRid); }
    }

    /// <summary>線斷了 ⇒ 走這條線還在等的請求一律「不知道」（請求可能已經送到）。</summary>
    void FailPendingVia(Session s, string iWhy)
    {
        List<Pending> aHit;
        lock (m_Pending) aHit = m_Pending.Values.Where(p => ReferenceEquals(p.Route, s)).ToList();
        foreach (Pending p in aHit)
            p.Done.TrySetResult(new PeerRpcResult { Outcome = PeerRpcOutcome.Unknown, ServerId = p.To, ServerName = p.ToName, Why = "等結果的時候線斷了（" + iWhy + "）" });
    }

    // ── 收到的 req／res／rpcfail ─────────────────────────────────────

    void OnRpcFrame(Session s, SCP_JsonData j)
    {
        if (DebugOldVersion || m_Me == null) return;
        string aType = j.GetString("t", "");
        string aTo = j.GetString("to", "");
        if (aTo != m_Me.Id)
        {
            // 主機替另一端轉：原封不動（簽章由兩端自己驗，主機改了就驗不過）
            if (m_Cfg.Mode != "host" || !s.ServerSide || aType == "rpcfail") return;
            Session? aDest;
            lock (m_Gate) m_Sessions.TryGetValue(aTo, out aDest);
            if (aDest != null && !aDest.Closed && aDest.ServerSide && !ReferenceEquals(aDest, s))
            {
                SCP_JsonData aFwd = DebugTamperForward?.Invoke(j) ?? j;
                if (Send(aDest, aFwd)) return;
            }
            if (aType == "req")
            {
                SCP_JsonData n = Obj("rpcfail");
                n.Set("rid", j.GetString("rid", "")); n.Set("to", j.GetString("from", ""));
                n.Set("why", $"主機 {m_Cfg.NodeName} 轉不過去：{aTo} 現在沒有連在主機上");
                Send(s, n);
            }
            return;
        }
        switch (aType)
        {
            case "req": OnReq(s, j); break;
            case "res": OnRes(j); break;
            case "rpcfail": OnRpcFail(s, j); break;
        }
    }

    void OnRpcFail(Session s, SCP_JsonData j)
    {
        Pending? p;
        lock (m_Pending) m_Pending.TryGetValue(j.GetString("rid", ""), out p);
        // 只有「我送出去的那條線上的主機」可以說轉不過去
        if (p == null || s.ServerSide || !ReferenceEquals(p.Route, s)) return;
        p.Done.TrySetResult(new PeerRpcResult { Outcome = PeerRpcOutcome.NotSent, ServerId = p.To, ServerName = p.ToName, Why = j.GetString("why", "主機轉不過去") });
    }

    void OnRes(SCP_JsonData j)
    {
        string aRid = j.GetString("rid", "");
        Pending? p;
        lock (m_Pending) m_Pending.TryGetValue(aRid, out p);
        if (p == null) return;   // 晚到的、或不是我在等的
        string aFrom = j.GetString("from", ""), aTs = j.GetString("ts", ""), aExit = j.GetString("exit", ""), aBody = j.GetString("body", "");
        if (aFrom != p.To)
        {
            WarnOnce($"跨主機連線：收到一筆結果，但不是我送去的那一台回的（等 {p.To}、來的是 {aFrom}）⇒ 不收");
            return;
        }
        if (!VerifySigned(j, aFrom, ResText(aRid, aFrom, m_Me!.Id, aTs, aExit, aBody)))
        {
            WarnOnce($"跨主機連線：{p.ToName} 回的結果簽章驗不過（在路上被改過？）⇒ 不收");
            return;
        }
        var aReply = new PeerRpcReply { Exit = int.TryParse(aExit, NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ? x : 70 };
        bool aRefused = false, aBusy = false;
        string aWhy = "", aName = p.ToName;
        try
        {
            SCP_JsonData b = SCP_JsonParser.Parse(aBody);
            aReply.Lines = SenatePeerStore.Strs(b["lines"]);
            if (b["values"].IsArray)
                foreach (SCP_JsonData kv in b["values"])
                    if (kv.IsArray && kv.Count == 2) aReply.Values.Add(new KeyValuePair<string, string>(kv[0].AsString(), kv[1].AsString()));
            aReply.Report = b.GetString("report", "");
            aRefused = b.GetBool("refused", false);
            aBusy = aRefused && b.GetBool("busy", false);
            aWhy = b.GetString("why", "");
            if (b.GetString("server_name", "").Length > 0) aName = SenatePeerStore.CleanName(b.GetString("server_name", ""));
        }
        catch (Exception e)
        {
            // 簽章對、內文卻解不開 ⇒ 它確實跑了（或擋了），只是看不懂 ⇒ 不知道
            p.Done.TrySetResult(new PeerRpcResult { Outcome = PeerRpcOutcome.Unknown, ServerId = p.To, ServerName = aName, Why = "結果的內文解不開（" + e.Message + "）" });
            return;
        }
        // 忙碌 ⇒ 那一台確定沒執行 ⇒ 跟「沒送到」一樣是 exit 6（稍後再試是安全的）
        p.Done.TrySetResult(new PeerRpcResult
        {
            Outcome = aBusy ? PeerRpcOutcome.NotSent : aRefused ? PeerRpcOutcome.Refused : PeerRpcOutcome.Done,
            Busy = aBusy, ServerId = p.To, ServerName = aName, Why = aWhy, Reply = aReply,
        });
    }

    void OnReq(Session s, SCP_JsonData j)
    {
        string aRid = j.GetString("rid", ""), aSvc = j.GetString("svc", ""), aFrom = j.GetString("from", ""), aTs = j.GetString("ts", "");
        string aCmd = j.GetString("cmd", ""), aArgs = j.GetString("args", "");
        string aFromName = FindNode(aFrom)?.Name ?? (aFrom == s.NodeId ? s.NodeName : aFrom);

        // iVerified：簽章驗過（`from` 是真的）⇒ 可以回（限頻）；沒驗過 ⇒ ⛔ 一律不回，只計數、log 限頻
        void Refuse(string iWhy, bool iVerified = true, bool iBusy = false)
        {
            Interlocked.Increment(ref m_RpcRefused);
            m_RpcLastRefused = $"{SenatePeerStore.NowIso()} {aFromName}（{aFrom}）{aCmd}：{iWhy}";
            // 限頻的 key：沒驗過 ⇒ 依這條線；驗過 ⇒ 依 from，但不在允許名單的共用一份（新身分隨時生得出來，各給一份等於沒限）
            string aKey = !iVerified ? "link:" + s.NodeId
                : m_Cfg.TaskClients.Contains(aFrom) ? "from:" + aFrom : "from:*";
            (bool aReplyOk, bool aLogOk, int aMuted) = RefuseGate(aKey, iVerified ? RefuseRepliesPerWindow : 0);
            if (aLogOk)
                m_Hooks.Err($"⚠ 跨主機連線：擋下 {aFromName}（{aFrom}）送來的 {aSvc}/{aCmd} —— {iWhy}"
                    + (iVerified ? "" : "（沒驗過身分 ⇒ 不回）")
                    + (aMuted > 0 ? $"（這之前另外擋下 {aMuted} 筆，限頻沒寫 log）" : ""));
            if (!aReplyOk) return;
            var aReply = new PeerRpcReply { Exit = iBusy ? 6 : 1 };
            aReply.Lines.Add(iBusy
                ? $"✗ Task Server {m_Cfg.NodeName} 沒有執行這一筆：{iWhy}（什麼都沒寫）"
                : $"⛔ Task Server {m_Cfg.NodeName} 擋下這一筆：{iWhy}（什麼都沒寫）");
            Send(s, BuildRes(aRid, aFrom, aReply, true, iWhy, iBusy));
        }

        if (aRid.Length < 8 || aRid.Length > 64) { Refuse("請求的 rid 不對", iVerified: false); return; }
        if (!VerifySigned(j, aFrom, ReqText(aRid, aSvc, aFrom, m_Me!.Id, aTs, aCmd, aArgs)))
        {
            Refuse("簽章驗不過（請求在路上被改過，或不是 from 那一台送的）", iVerified: false);
            return;
        }
        if (!DateTime.TryParse(aTs, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aAt)
            || Math.Abs((DateTime.UtcNow - aAt).TotalSeconds) > RpcSkewSec)
        {
            Refuse($"請求的時間跟這台差超過 {RpcSkewSec / 60} 分鐘（可能是重送舊的請求，或兩台的時鐘不準）");
            return;
        }

        string aKey = aFrom + "|" + aRid;
        SCP_JsonData? aCached = null;
        bool aSeen;
        lock (m_Seen)
        {
            DateTime aCut = DateTime.UtcNow.AddSeconds(-RpcSeenSec);
            foreach (string k in m_Seen.Where(kv => kv.Value.At < aCut).Select(kv => kv.Key).ToList()) m_Seen.Remove(k);
            aSeen = m_Seen.TryGetValue(aKey, out SeenRpc? aOld);
            if (aSeen) aCached = aOld!.Res;
        }
        if (aSeen)
        {
            // 同一筆再來一次 ⇒ ⛔ 不再執行；跑完了就回上一次的結果，還在跑就不理（請求端會等到它的那一份或逾時）
            if (aCached != null) Send(s, aCached);
            return;
        }

        if (!m_Cfg.ServesTask) { Refuse("這台不是 Task Server（task_server 不是 self）"); return; }
        if (m_Cfg.TaskServerError.Length > 0) { Refuse("這台的 Task Server 設定有問題：" + m_Cfg.TaskServerError); return; }
        if (aSvc != SvcTask || Array.IndexOf(TaskCmds, aCmd) < 0) { Refuse($"只收 task／tasks 指令（收到 svc={aSvc} cmd={aCmd}）"); return; }
        if (!m_Cfg.TaskClients.Contains(aFrom)) { Refuse($"{aFromName}（{aFrom}）不在允許名單（task_clients）"); return; }
        Func<PeerRpcCall, PeerRpcReply>? aServe = m_Hooks.Serve;
        if (aServe == null) { Refuse("這台沒有接執行端"); return; }

        var aCall = new PeerRpcCall { Svc = aSvc, Cmd = aCmd, FromId = aFrom, FromName = aFromName };
        try
        {
            SCP_JsonData a = SCP_JsonParser.Parse(aArgs);
            if (!a.IsObject) throw new FormatException("不是 JSON 物件");
            foreach (string k in a.Keys) if (a[k].IsString) aCall.Args[k] = a[k].AsString();
        }
        catch (Exception e) { Refuse("參數解不開（" + e.Message + "）"); return; }
        string? aBadArgs = SenateTaskServer.RefuseReason(aCall.Args);
        if (aBadArgs != null) { Refuse(aBadArgs); return; }

        // 同時在跑的滿了 ⇒ 不執行；rid ⛔ 不記進「看過的」⇒ 請求端用同一個 rid 重送還是會執行
        if (!TryTakeRunSlot())
        {
            Refuse($"Task Server 忙碌中（同時最多 {RpcMaxRunning} 筆）⇒ 這筆沒有執行，可以稍後再試", iBusy: true);
            return;
        }
        var aEntry = new SeenRpc { At = DateTime.UtcNow };
        bool aDup;
        lock (m_Seen)
        {
            aDup = m_Seen.ContainsKey(aKey);
            if (!aDup) m_Seen[aKey] = aEntry;
        }
        if (aDup || DebugSwallowRequests) { Interlocked.Decrement(ref m_RpcRunning); return; }

        _ = Task.Run(() =>
        {
            PeerRpcReply aReply;
            try { aReply = aServe(aCall); }
            catch (Exception e)
            {
                aReply = new PeerRpcReply { Exit = 70 };
                aReply.Lines.Add($"✗ Task Server {m_Cfg.NodeName} 執行時炸了：{e.GetType().Name}：{e.Message} —— 寫了沒有不確定，先回讀");
            }
            finally { Interlocked.Decrement(ref m_RpcRunning); }
            SCP_JsonData aRes = BuildRes(aRid, aFrom, aReply, false, "");
            lock (m_Seen) aEntry.Res = aRes;
            Interlocked.Increment(ref m_RpcServed);
            m_Hooks.Out($"· 🛰 跨主機連線：替 {aFromName} 跑了 {aCmd}（exit {aReply.Exit}）");
            Send(s, aRes);   // 原路回去：經主機來的，主機照 `to` 轉回請求端
        });
    }

    /// <summary>佔一個執行的位子；滿了（已經 RpcMaxRunning 筆在跑）⇒ false。</summary>
    bool TryTakeRunSlot()
    {
        while (true)
        {
            int n = Volatile.Read(ref m_RpcRunning);
            if (n >= RpcMaxRunning) return false;
            if (Interlocked.CompareExchange(ref m_RpcRunning, n + 1, n) == n) return true;
        }
    }

    /// <summary>
    /// 擋下的這一筆要不要回、要不要寫 log：每個 key 每分鐘最多回 <paramref name="iReplyCap"/> 筆、寫 1 行 log。
    /// 回的 Muted ＞ 0 ⇒ 這行 log 要一起報「這之前另外擋下幾筆」（限頻時沒寫 log 的）。
    /// </summary>
    (bool Reply, bool Log, int Muted) RefuseGate(string iKey, int iReplyCap)
    {
        DateTime aNow = DateTime.UtcNow;
        lock (m_RefuseWin)
        {
            // 很久沒動的收掉（key 只有線、允許名單上的節點、加一個共用的 ⇒ 本來就不多）
            foreach (string k in m_RefuseWin.Where(kv => (aNow - kv.Value.Start).TotalSeconds > RefuseWindowSec * 10).Select(kv => kv.Key).ToList())
                m_RefuseWin.Remove(k);
            if (!m_RefuseWin.TryGetValue(iKey, out RefuseWindow? w)) m_RefuseWin[iKey] = w = new RefuseWindow { Start = aNow };
            else if ((aNow - w.Start).TotalSeconds >= RefuseWindowSec) { w.Start = aNow; w.Replies = 0; w.Logged = false; }
            bool aReply = w.Replies < iReplyCap;
            if (aReply) w.Replies++;
            if (w.Logged) { w.Muted++; return (aReply, false, 0); }
            w.Logged = true;
            int aMuted = w.Muted;
            w.Muted = 0;
            return (aReply, true, aMuted);
        }
    }

    SCP_JsonData BuildRes(string iRid, string iTo, PeerRpcReply iReply, bool iRefused, string iWhy, bool iBusy = false)
    {
        var b = SCP_JsonData.NewObject();
        var aLines = SCP_JsonData.NewArray();
        int aChars = 0, aCut = 0;
        foreach (string l in iReply.Lines)
        {
            if (aChars + l.Length > RpcMaxLinesChars) { aCut++; continue; }
            aChars += l.Length;
            aLines.Add(l);
        }
        if (aCut > 0) aLines.Add($"⚠ 後面還有 {aCut} 行太長沒有帶過來（完整內容在 {m_Cfg.NodeName} 上）");
        b.Set("lines", aLines);
        var aValues = SCP_JsonData.NewArray();
        foreach (var kv in iReply.Values)
        {
            var pair = SCP_JsonData.NewArray();
            pair.Add(kv.Key); pair.Add(kv.Value);
            aValues.Add(pair);
        }
        b.Set("values", aValues);
        string aReport = iReply.Report ?? "";
        if (aReport.Length > RpcMaxReportChars)
            aReport = aReport.Substring(0, RpcMaxReportChars) + $"\n\n⚠（回傳檔太長，後面截掉了；完整的在 {m_Cfg.NodeName} 上）\n";
        b.Set("report", aReport);
        if (iRefused) b.Set("refused", true);
        if (iBusy) b.Set("busy", true);   // 新版判「沒執行」（6）；舊版看不懂，只看 refused（1）—— 兩種都是什麼都沒寫
        b.Set("why", iWhy);
        b.Set("server_name", m_Cfg.NodeName);
        string aBody = b.ToJson(false);
        string aTs = SenatePeerStore.NowIso();
        string aExit = iReply.Exit.ToString(CultureInfo.InvariantCulture);
        SCP_JsonData f = Obj("res");
        f.Set("rid", iRid); f.Set("from", m_Me!.Id); f.Set("to", iTo); f.Set("ts", aTs); f.Set("exit", aExit); f.Set("body", aBody);
        f.Set("pub", m_Me.SignPub);
        f.Set("sig", SenatePeerCrypto.Sign(m_Me.SignPriv, Encoding.UTF8.GetBytes(ResText(iRid, m_Me.Id, iTo, aTs, aExit, aBody))));
        return f;
    }

    void AppendRpcStatus(SCP_JsonData j)
    {
        j.Set("task_server", m_Cfg.TaskServer);
        j.Set("rpc_served", m_RpcServed);
        j.Set("rpc_running", RpcRunning);
        j.Set("rpc_refused", m_RpcRefused);
        j.Set("rpc_last_refused", m_RpcLastRefused);
    }
}
