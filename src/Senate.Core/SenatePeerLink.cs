// 區塊職責：**跨主機連線的本體**（TASK-0011）—— 主機開埠收連線、另一端連上主機；握手、酒館訊息串接、在線名單。
// 物理意義（Tim 2026-11-11：一端當主機，另一端收到 Discord 上的公告後連到主機；訊息都送到主機，由主機轉發）：
//   · **星狀**：主機（mode=host）開一個 WebSocket 埠；每個另一端（mode=client）連一條線到主機。
//     另一端發的酒館訊息 → 主機寫進自己的酒館 → 主機再送給其他另一端。主機自己的訊息 → 送給每一個另一端。
//   · **握手**：另一端拿著公告裡的 token（HMAC 證明「我看得到那則公告」）與自己的簽章；主機用公告裡那把公鑰簽章
//     （另一端據此確認對面真的是貼公告的那一台）。臨時 ECDH 導出兩把金鑰，之後每個 frame 都加密。
//     ⚠ 能連上主機的人 ＝ 看得到那則 Discord 公告的人。信任的邊界是那個 Discord 頻道。
//   · **訊息**：對面的訊息進來時是本機的一則新訊息（拿本機的 seq），sender_id=`peer:<節點名>:<原 sender_id>`、
//     meta.source=`peer` 並帶 `peer_id`／`peer_host`／`peer_room`／`peer_seq`。
//     ⛔ 轉進來的不再轉回原節點（不迴圈）；不發薪、不觸發酒保（那兩處認 `peer:` 開頭）。
//   · **游標**：收的一方記「這個節點的這個頻道收到第幾號」（`peer_state.json`）。連上時把它告訴對方，對方從下一號開始送
//     ⇒ 斷線期間的訊息重連後補上、同一則不會進來兩次。第一次接上不回放歷史；積壓超過 <see cref="MaxBacklog"/> 則跳到最新並說出來。
//   · **在線名單**：每一端把自己的 persona lock 掃出來的名單送給主機；主機把「自己＋其他另一端」的名單送給每個另一端。
//     收到的名單寫進 `peer_presence.json`（⛔ 不寫進本機的 lock）。
// 數值影響：網路與酒館寫入都在背景執行緒 ⇒ ⛔ 不擋 Server 的心跳。同一個錯誤只印一次。
// ⚠ 伺服端不用 HttpListener：在 Windows 上它要管理員先登記網址才能聽非本機位址。這裡自己處理 WebSocket 的升級握手，
//   之後交給 .NET 內建的 WebSocket（`WebSocket.CreateFromStream`）。
#nullable enable
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Discord;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed class SenatePeerLink : IDisposable
{
    public const int MaxBacklog = 500;
    public const int BatchSize = 50;
    public const int MaxClients = 16;
    public const int MaxFrameBytes = 1 << 20;
    public const string WsPath = "/peer";
    public const string StatusFileName = "peer_status.json";
    const int HandshakeTimeoutSec = 10;
    const int DialTimeoutSec = 6;
    const int PresenceEverySec = 30;

    public sealed class Hooks
    {
        /// <summary>把一則訊息寫進本機酒館（Server 裡是 `tavern-write`）。回（成功、本機 seq、錯誤）。</summary>
        public Func<string, SCP_JsonData, (bool Ok, int Seq, string Error)> WriteTavern = (_, _) => (false, 0, "沒有接寫入端");
        public Action<string> Out = _ => { };
        public Action<string> Err = _ => { };
        /// <summary>在本機酒館留一則系統訊息（接上了）。沒接 ⇒ 不留。⛔ 內容不放 token、位址。</summary>
        public Action<string>? Note;
    }

    sealed class Session
    {
        public WebSocket Ws = null!;
        public string NodeId = "", NodeName = "", Via = "";
        public bool ServerSide;                       // true ＝ 我是主機，對面是另一端
        public SenatePeerCrypto.FrameCipher Tx = null!, Rx = null!;
        public readonly SemaphoreSlim SendLock = new(1, 1);
        public readonly CancellationTokenSource Cts = new();
        public readonly Dictionary<string, int> SendPos = new(StringComparer.Ordinal);
        public volatile bool SyncReceived;
        public volatile bool Closed;
        public List<PeerNodePresence> Remote = new();
        public string PresenceSent = "";
        public DateTime PresenceAt = DateTime.MinValue;
        public DateTime Since = DateTime.UtcNow;
        public int MsgIn, MsgOut;
        // 這個節點「從 Discord 進來過的訊息」的索引（頻道 → 對方的 seq → 內文開頭）；只在補送時用，20 秒重建一次
        public readonly Dictionary<string, (DateTime At, Dictionary<string, List<string>> Map)> DiscordCopies = new(StringComparer.Ordinal);
    }

    readonly string m_DataRoot;
    readonly Hooks m_Hooks;
    readonly object m_Gate = new();
    readonly Dictionary<string, Session> m_Sessions = new(StringComparer.Ordinal);
    PeerIdentity? m_Me;
    PeerConfig m_Cfg = new();
    TcpListener? m_Listener;
    CancellationTokenSource? m_ListenCts;
    string m_ListenKey = "";
    Task? m_Step;
    DateTime m_NextStep = DateTime.MinValue;
    Task? m_Dial;
    DateTime m_NextDial = DateTime.MinValue;
    int m_DialFails;
    string m_DialAnnTs = "";
    List<PeerOnline> m_LocalOnline = new();
    string m_LastWarn = "";
    string m_PresenceSig = "";
    DateTime m_PresenceAt = DateTime.MinValue;
    string m_StatusSig = "";
    DateTime m_StatusAt = DateTime.MinValue;
    volatile bool m_Disposed;

    /// <summary>宿主（連線工作）要顯示在狀態裡的額外欄位：通道網址、公告有沒有貼出去…。</summary>
    public readonly Dictionary<string, string> Extra = new(StringComparer.Ordinal);

    public SenatePeerLink(string iDataRoot, Hooks iHooks) { m_DataRoot = iDataRoot; m_Hooks = iHooks; }

    public PeerConfig Config => m_Cfg;
    public string MyId => m_Me?.Id ?? "";
    public bool Listening => m_Listener != null;
    public int SessionCount { get { lock (m_Gate) return m_Sessions.Count; } }

    /// <summary>現在真的連著的節點 id。</summary>
    public List<string> ConnectedNodeIds() => Snapshot().Where(s => !s.Closed).Select(s => s.NodeId).ToList();

    int m_DiscordDupSkipped;
    /// <summary>補送時因為「同一則已經從 Discord 進來過」而沒有再寫一次的訊息數。</summary>
    public int DiscordDupSkipped => m_DiscordDupSkipped;

    readonly Dictionary<string, DateTime> m_NotedAt = new(StringComparer.Ordinal);

    int m_DupDropped;
    /// <summary>因為「這個號碼已經收過」而丟掉的訊息數。</summary>
    public int DupDropped => m_DupDropped;

    /// <summary>自我測試用：把每條線「送到第幾號」往回撥 ⇒ 下一輪會把最後幾則重送一次（對方應該全部擋下）。</summary>
    internal void DebugRewind(string iRoom, int iBy)
    {
        foreach (Session s in Snapshot())
            lock (s.SendPos)
                if (s.SendPos.TryGetValue(iRoom, out int p)) s.SendPos[iRoom] = Math.Max(0, p - iBy);
    }

    // ── 驅動 ─────────────────────────────────────────────────────────

    /// <summary>Server 的服務迴圈每一圈叫一次：每秒在背景跑一輪 <see cref="Step"/>（上一輪還沒完就不疊）。</summary>
    public void Tick()
    {
        DateTime aNow = DateTime.UtcNow;
        if (m_Disposed || aNow < m_NextStep || m_Step is { IsCompleted: false }) return;
        m_NextStep = aNow.AddSeconds(1);
        m_Step = Task.Run(() =>
        {
            try { Step(); }
            catch (Exception e) { WarnOnce("跨主機連線：這一輪炸了（" + e.GetType().Name + "：" + e.Message + "）—— 下一輪再試"); }
        });
    }

    /// <summary>一輪：重讀設定 → 依模式開關埠／撥號 → 送新訊息與在線名單 → 寫狀態檔。</summary>
    public void Step()
    {
        PeerConfig c = SenatePeerStore.LoadConfig(m_DataRoot);
        if (c.Error.Length > 0) WarnOnce("跨主機連線：" + c.Error + " ⇒ 當成關閉");
        bool aWasOff = m_Cfg.Mode == "off";
        m_Cfg = c;
        if (c.Mode == "off")
        {
            StopListener();
            CloseAll(s => true, "模式關閉");
            if (!aWasOff) m_Hooks.Out("· 跨主機連線：已關");
            WritePresence();
            WriteStatus();
            return;
        }
        m_Me ??= SenatePeerStore.LoadOrCreateIdentity(m_DataRoot);
        if (c.Mode == "host")
        {
            CloseAll(s => !s.ServerSide, "改當主機");
            EnsureListener(c);
        }
        else
        {
            StopListener();
            CloseAll(s => s.ServerSide, "改當另一端");
            EnsureDial();
        }
        m_LocalOnline = LocalOnline();   // 一輪掃一次，每條線共用
        foreach (Session s in Snapshot())
        {
            try { Pump(s); SendPresence(s); }
            catch (Exception e) { Close(s, "送出失敗（" + e.GetType().Name + "：" + e.Message + "）"); }
        }
        WritePresence();
        WriteStatus();
    }

    List<Session> Snapshot() { lock (m_Gate) return m_Sessions.Values.ToList(); }

    void WarnOnce(string iMsg)
    {
        if (iMsg == m_LastWarn) return;
        m_LastWarn = iMsg;
        m_Hooks.Err("⚠ " + iMsg);
    }

    public void Dispose()
    {
        m_Disposed = true;
        StopListener();
        CloseAll(s => true, "收工");
    }

    // ── 主機：開埠 ───────────────────────────────────────────────────

    void EnsureListener(PeerConfig c)
    {
        string aKey = c.Bind + ":" + c.Port;
        if (m_Listener != null && m_ListenKey == aKey) return;
        StopListener();
        try
        {
            IPAddress aAddr = c.Bind == "0.0.0.0" || c.Bind.Length == 0 ? IPAddress.Any : IPAddress.Parse(c.Bind);
            var l = new TcpListener(aAddr, c.Port);
            l.Start();
            m_Listener = l;
            m_ListenKey = aKey;
            m_ListenCts = new CancellationTokenSource();
            CancellationToken ct = m_ListenCts.Token;
            _ = Task.Run(() => AcceptLoop(l, ct));
            m_Hooks.Out($"· 跨主機連線：主機開始收連線（{aKey}，節點名 {c.NodeName}，id {m_Me!.Id}）");
        }
        catch (Exception e)
        {
            WarnOnce($"跨主機連線：埠 {aKey} 開不了（{e.GetType().Name}：{e.Message}）⇒ 主機沒有在收連線");
        }
    }

    void StopListener()
    {
        if (m_Listener == null) return;
        try { m_ListenCts?.Cancel(); } catch { }
        try { m_Listener.Stop(); } catch { }
        m_Listener = null;
        m_ListenKey = "";
    }

    async Task AcceptLoop(TcpListener iListener, CancellationToken iCt)
    {
        while (!iCt.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await iListener.AcceptTcpClientAsync(iCt); }
            catch (Exception) { break; }
            _ = Task.Run(() => ServeAsync(c, iCt));
        }
    }

    async Task ServeAsync(TcpClient iClient, CancellationToken iCt)
    {
        Session? s = null;
        try
        {
            iClient.NoDelay = true;
            NetworkStream aStream = iClient.GetStream();
            using var aHs = CancellationTokenSource.CreateLinkedTokenSource(iCt);
            aHs.CancelAfter(TimeSpan.FromSeconds(HandshakeTimeoutSec));
            if (!await UpgradeAsync(aStream, aHs.Token)) return;
            WebSocket ws = WebSocket.CreateFromStream(aStream,
                new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(20) });
            s = await HandshakeServerAsync(ws, aHs.Token);
            if (s == null) { try { ws.Abort(); } catch { } return; }
            s.Via = (iClient.Client.RemoteEndPoint?.ToString()) ?? "";
            Register(s);
            await ReceiveLoop(s);
        }
        catch (Exception e)
        {
            if (s != null) Close(s, e.GetType().Name + "：" + e.Message);
        }
        finally { try { iClient.Dispose(); } catch { } }
    }

    /// <summary>讀 HTTP 升級請求、回 101。不是對 <see cref="WsPath"/> 的 WebSocket 升級 ⇒ 回 404 並回 false。</summary>
    static async Task<bool> UpgradeAsync(NetworkStream iStream, CancellationToken iCt)
    {
        var aBuf = new List<byte>(1024);
        byte[] aOne = new byte[1];
        while (aBuf.Count < 8192)
        {
            int n = await iStream.ReadAsync(aOne.AsMemory(0, 1), iCt);
            if (n == 0) return false;
            aBuf.Add(aOne[0]);
            int k = aBuf.Count;
            if (k >= 4 && aBuf[k - 4] == 13 && aBuf[k - 3] == 10 && aBuf[k - 2] == 13 && aBuf[k - 1] == 10) break;
        }
        string[] aLines = Encoding.ASCII.GetString(aBuf.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        string aKey = "";
        bool aUpgrade = false;
        foreach (string l in aLines.Skip(1))
        {
            int i = l.IndexOf(':');
            if (i <= 0) continue;
            string aName = l.Substring(0, i).Trim(), aVal = l.Substring(i + 1).Trim();
            if (aName.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) aKey = aVal;
            else if (aName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase)) aUpgrade = aVal.Contains("websocket", StringComparison.OrdinalIgnoreCase);
        }
        string[] aReq = aLines.Length > 0 ? aLines[0].Split(' ') : Array.Empty<string>();
        bool aOk = aReq.Length >= 2 && aReq[0] == "GET" && aReq[1].StartsWith(WsPath, StringComparison.Ordinal) && aUpgrade && aKey.Length > 0;
        if (!aOk)
        {
            byte[] aNo = Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await iStream.WriteAsync(aNo, iCt);
            return false;
        }
        string aAccept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(aKey + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        byte[] aYes = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + aAccept + "\r\n\r\n");
        await iStream.WriteAsync(aYes, iCt);
        return true;
    }

    // ── 握手 ─────────────────────────────────────────────────────────

    static async Task SendTextAsync(WebSocket ws, SCP_JsonData j, CancellationToken ct)
        => await ws.SendAsync(Encoding.UTF8.GetBytes(j.ToJson(false)), WebSocketMessageType.Text, true, ct);

    static async Task<SCP_JsonData?> RecvTextAsync(WebSocket ws, CancellationToken ct)
    {
        byte[] aBuf = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(aBuf, ct);
            if (r.MessageType != WebSocketMessageType.Text) return null;
            ms.Write(aBuf, 0, r.Count);
            if (ms.Length > 65536) return null;
            if (r.EndOfMessage) break;
        }
        try { return SCP_JsonParser.Parse(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length)); }
        catch (Exception) { return null; }
    }

    static SCP_JsonData Obj(string iType)
    {
        var j = SCP_JsonData.NewObject();
        j.Set("t", iType);
        return j;
    }

    static string Transcript(string cId, string cEph, string cNonce, string sId, string sEph, string sNonce)
        => string.Join("|", SenatePeerCrypto.Proto, cId, cEph, cNonce, sId, sEph, sNonce);

    async Task<Session?> HandshakeServerAsync(WebSocket ws, CancellationToken ct)
    {
        async Task<Session?> Refuse(string iWhy)
        {
            SCP_JsonData n = Obj("no");
            n.Set("why", iWhy);
            try { await SendTextAsync(ws, n, ct); } catch { }
            WarnOnce("跨主機連線：拒絕了一條連線 —— " + iWhy);
            return null;
        }

        PeerIdentity aMe = m_Me!;
        SCP_JsonData? h = await RecvTextAsync(ws, ct);
        if (h == null || h.GetString("t", "") != "hello" || h.GetInt("v", 0) != 1) return await Refuse("開頭不是這個協定");
        string cId = h.GetString("id", ""), cSign = h.GetString("sign", ""), cEph = h.GetString("eph", ""), cNonce = h.GetString("nonce", "");
        string cName = SenatePeerStore.CleanName(h.GetString("name", ""));
        bool aIdOk;
        try { aIdOk = cId.Length > 0 && SenatePeerCrypto.IdOf(cSign) == cId; } catch (Exception) { aIdOk = false; }
        if (!aIdOk || cEph.Length == 0 || cNonce.Length < 8) return await Refuse("對方的身分欄位不對");
        if (cId == aMe.Id) return await Refuse("連到自己了");

        using var aEph = new SenatePeerCrypto.Ephemeral();
        string sNonce = SenatePeerCrypto.RandomToken(16);
        string T = Transcript(cId, cEph, cNonce, aMe.Id, aEph.Pub, sNonce);
        SCP_JsonData r = Obj("hello");
        r.Set("id", aMe.Id); r.Set("name", m_Cfg.NodeName); r.Set("eph", aEph.Pub); r.Set("nonce", sNonce);
        r.Set("sig", SenatePeerCrypto.Sign(aMe.SignPriv, Encoding.UTF8.GetBytes(T)));
        await SendTextAsync(ws, r, ct);

        SCP_JsonData? a = await RecvTextAsync(ws, ct);
        if (a == null || a.GetString("t", "") != "auth") return await Refuse("沒有收到驗證");
        string aToken = SenatePeerStore.HostToken(m_DataRoot);
        if (!SenatePeerCrypto.FixedEquals(a.GetString("proof", ""), SenatePeerCrypto.Hmac(aToken, T)))
            return await Refuse($"節點 {cName}（{cId}）的 token 不對 —— 它拿的可能是舊的公告");
        if (!SenatePeerCrypto.Verify(cSign, Encoding.UTF8.GetBytes(T + "|client"), a.GetString("sig", "")))
            return await Refuse($"節點 {cName}（{cId}）的簽章驗不過");
        bool aFull;
        lock (m_Gate) aFull = m_Sessions.Count >= MaxClients && !m_Sessions.ContainsKey(cId);
        if (aFull) return await Refuse($"已經有 {MaxClients} 條連線了");

        (byte[] aC2S, byte[] aS2C) = aEph.Derive(cEph, cNonce, sNonce);
        await SendTextAsync(ws, Obj("ok"), ct);
        SenatePeerStore.NoteNode(m_DataRoot, cId, cName, cSign, null);
        return new Session
        {
            Ws = ws, NodeId = cId, NodeName = cName, ServerSide = true,
            Tx = new SenatePeerCrypto.FrameCipher(aS2C), Rx = new SenatePeerCrypto.FrameCipher(aC2S),
        };
    }

    async Task<(Session? S, string Error)> HandshakeClientAsync(WebSocket ws, PeerAnnounce iAnn, CancellationToken ct)
    {
        PeerIdentity aMe = m_Me!;
        using var aEph = new SenatePeerCrypto.Ephemeral();
        string cNonce = SenatePeerCrypto.RandomToken(16);
        SCP_JsonData h = Obj("hello");
        h.Set("v", 1); h.Set("id", aMe.Id); h.Set("name", m_Cfg.NodeName); h.Set("sign", aMe.SignPub); h.Set("eph", aEph.Pub); h.Set("nonce", cNonce);
        await SendTextAsync(ws, h, ct);

        SCP_JsonData? r = await RecvTextAsync(ws, ct);
        if (r == null) return (null, "主機沒有回應握手");
        if (r.GetString("t", "") == "no") return (null, "主機拒絕：" + r.GetString("why", ""));
        if (r.GetString("t", "") != "hello") return (null, "主機的回應不是這個協定");
        string sId = r.GetString("id", ""), sEph = r.GetString("eph", ""), sNonce = r.GetString("nonce", "");
        if (sId != iAnn.Id) return (null, $"對面的 id（{sId}）不是公告的那一台（{iAnn.Id}）");
        string T = Transcript(aMe.Id, aEph.Pub, cNonce, sId, sEph, sNonce);
        if (!SenatePeerCrypto.Verify(iAnn.Sign, Encoding.UTF8.GetBytes(T), r.GetString("sig", "")))
            return (null, "主機的簽章驗不過 ⇒ 對面不是貼公告的那一台");

        SCP_JsonData a = Obj("auth");
        a.Set("sig", SenatePeerCrypto.Sign(aMe.SignPriv, Encoding.UTF8.GetBytes(T + "|client")));
        a.Set("proof", SenatePeerCrypto.Hmac(iAnn.Token, T));
        await SendTextAsync(ws, a, ct);
        SCP_JsonData? k = await RecvTextAsync(ws, ct);
        if (k == null) return (null, "主機沒有回應驗證");
        if (k.GetString("t", "") != "ok") return (null, "主機拒絕：" + k.GetString("why", "（沒有說原因）"));

        (byte[] aC2S, byte[] aS2C) = aEph.Derive(sEph, cNonce, sNonce);
        SenatePeerStore.NoteNode(m_DataRoot, sId, SenatePeerStore.CleanName(r.GetString("name", iAnn.Name)), iAnn.Sign, iAnn.Webhooks);
        return (new Session
        {
            Ws = ws, NodeId = sId, NodeName = SenatePeerStore.CleanName(r.GetString("name", iAnn.Name)), ServerSide = false,
            Tx = new SenatePeerCrypto.FrameCipher(aC2S), Rx = new SenatePeerCrypto.FrameCipher(aS2C),
        }, "");
    }

    // ── 另一端：撥號 ─────────────────────────────────────────────────

    void EnsureDial()
    {
        PeerAnnounce? aAnn;
        try { aAnn = SenatePeerStore.LoadAnnounce(m_DataRoot); }
        catch (Exception e) { WarnOnce("跨主機連線：" + e.Message); return; }
        Session? aCur = Snapshot().FirstOrDefault(s => !s.ServerSide);
        if (aCur != null)
        {
            // 公告換了一台主機 ⇒ 舊的那條線收掉，下一輪連新的
            if (aAnn != null && aAnn.Id != aCur.NodeId) Close(aCur, "公告換了一台主機");
            return;
        }
        if (aAnn == null) { WarnOnce("跨主機連線：等主機的公告（還沒有收到過）"); return; }
        if (aAnn.Ts != m_DialAnnTs)
        {
            // 公告換了（新的位址或新的一場）⇒ 之前連不上累積的等待不算數，馬上試
            m_DialAnnTs = aAnn.Ts;
            m_DialFails = 0;
            m_NextDial = DateTime.MinValue;
        }
        if (aAnn.Closed) { WarnOnce($"跨主機連線：主機 {aAnn.Name} 已經關閉連線，等下一則公告"); return; }
        if (aAnn.Id == m_Me!.Id) { WarnOnce("跨主機連線：收到的公告是這一台自己的 ⇒ 不連"); return; }
        if (aAnn.Addrs.Count == 0) { WarnOnce($"跨主機連線：主機 {aAnn.Name} 的公告裡沒有位址"); return; }
        if (m_Dial is { IsCompleted: false } || DateTime.UtcNow < m_NextDial) return;
        m_Dial = Task.Run(() => DialAsync(aAnn));
    }

    async Task DialAsync(PeerAnnounce iAnn)
    {
        string aLast = "";
        foreach (string aAddr in iAnn.Addrs)
        {
            if (m_Disposed) return;
            var ws = new ClientWebSocket();
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(DialTimeoutSec + HandshakeTimeoutSec));
                using (var aConn = CancellationTokenSource.CreateLinkedTokenSource(ct.Token))
                {
                    aConn.CancelAfter(TimeSpan.FromSeconds(DialTimeoutSec));
                    await ws.ConnectAsync(new Uri(aAddr), aConn.Token);
                }
                (Session? s, string aErr) = await HandshakeClientAsync(ws, iAnn, ct.Token);
                if (s == null) { aLast = aAddr + "：" + aErr; try { ws.Abort(); } catch { } ws.Dispose(); continue; }
                s.Via = aAddr;
                m_DialFails = 0;
                Register(s);
                _ = Task.Run(() => ReceiveLoop(s));
                return;
            }
            catch (Exception e)
            {
                aLast = aAddr + "：" + (e is OperationCanceledException ? "逾時" : e.GetType().Name + "：" + (e.InnerException ?? e).Message);
                try { ws.Abort(); } catch { }
                ws.Dispose();
            }
        }
        m_DialFails++;
        int aWait = Math.Min(30, 1 << Math.Min(m_DialFails, 5));
        m_NextDial = DateTime.UtcNow.AddSeconds(aWait);
        WarnOnce($"跨主機連線：連不上主機 {iAnn.Name}（試了 {iAnn.Addrs.Count} 個位址；最後一個 —— {aLast}）⇒ {aWait} 秒後再試");
    }

    // ── 連線的登記、收、送、關 ───────────────────────────────────────

    void Register(Session s)
    {
        Session? aOld;
        lock (m_Gate)
        {
            m_Sessions.TryGetValue(s.NodeId, out aOld);
            m_Sessions[s.NodeId] = s;
        }
        if (aOld != null) Close(aOld, "同一個節點重新連上，舊的那條收掉", iRemove: false);
        m_LastWarn = "";
        m_Hooks.Out($"· 🌐 跨主機連線：{(s.ServerSide ? "另一端" : "主機")} {s.NodeName}（{s.NodeId}）連上了" + (s.Via.Length > 0 ? "　經 " + s.Via : ""));
        // 酒館裡留一則（同一個節點 5 分鐘內只留一次：線不穩的時候不洗版）
        bool aNote;
        lock (m_NotedAt)
        {
            aNote = !m_NotedAt.TryGetValue(s.NodeId, out DateTime aLast) || (DateTime.UtcNow - aLast).TotalMinutes >= 5;
            if (aNote) m_NotedAt[s.NodeId] = DateTime.UtcNow;
        }
        if (aNote)
        {
            try { m_Hooks.Note?.Invoke($"🌐 跨主機連線：跟{(s.ServerSide ? "另一端" : "主機")} **{s.NodeName}** 接上了 —— 酒館訊息與在線名單開始互通。"); }
            catch (Exception) { }
        }

        // 告訴對方：我要哪些頻道、每個頻道我收到第幾號、我的 Discord webhook 是哪幾條
        SCP_JsonData j = Obj("sync");
        j.Set("name", m_Cfg.NodeName);
        j.Set("rooms", SenatePeerStore.Arr(m_Cfg.Rooms));
        var aHave = SCP_JsonData.NewObject();
        foreach (string aRoom in m_Cfg.Rooms) aHave.Set(aRoom, SenatePeerStore.GetCursor(m_DataRoot, s.NodeId, aRoom));
        j.Set("have", aHave);
        j.Set("webhooks", SenatePeerStore.Arr(SCP_DiscordInbound.TryOwnWebhookIds(m_DataRoot, out HashSet<string> aOwn) ? aOwn : new HashSet<string>()));
        Send(s, j);
    }

    bool Send(Session s, SCP_JsonData j)
    {
        if (s.Closed) return false;
        try
        {
            if (!s.SendLock.Wait(TimeSpan.FromSeconds(15))) { Close(s, "送出卡住超過 15 秒"); return false; }
            try
            {
                byte[] aFrame = s.Tx.Encrypt(j.ToJson(false));
                using var ct = CancellationTokenSource.CreateLinkedTokenSource(s.Cts.Token);
                ct.CancelAfter(TimeSpan.FromSeconds(15));
                s.Ws.SendAsync(aFrame, WebSocketMessageType.Binary, true, ct.Token).GetAwaiter().GetResult();
                return true;
            }
            finally { s.SendLock.Release(); }
        }
        catch (Exception e)
        {
            Close(s, "送不出去（" + e.GetType().Name + "：" + e.Message + "）");
            return false;
        }
    }

    async Task ReceiveLoop(Session s)
    {
        string aWhy = "對面關了";
        byte[] aBuf = new byte[65536];
        try
        {
            while (!s.Closed)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await s.Ws.ReceiveAsync(aBuf, s.Cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(aBuf, 0, r.Count);
                    if (ms.Length > MaxFrameBytes) { aWhy = "frame 超過上限"; return; }
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Binary) continue;
                string? aText = s.Rx.Decrypt(ms.GetBuffer(), (int)ms.Length);
                if (aText == null) { aWhy = "frame 解不開（金鑰不對或被改過）"; return; }
                SCP_JsonData j;
                try { j = SCP_JsonParser.Parse(aText); } catch (Exception) { aWhy = "frame 不是 JSON"; return; }
                string? aStop = Handle(s, j);
                if (aStop != null) { aWhy = aStop; return; }
            }
        }
        catch (Exception e) { if (!s.Closed) aWhy = e.GetType().Name + "：" + (e.InnerException ?? e).Message; }
        finally { Close(s, aWhy); }
    }

    void Close(Session s, string iWhy, bool iRemove = true)
    {
        if (s.Closed) return;
        s.Closed = true;
        try { s.Cts.Cancel(); } catch { }
        try { s.Ws.Abort(); } catch { }
        try { s.Ws.Dispose(); } catch { }
        if (iRemove)
            lock (m_Gate)
                if (m_Sessions.TryGetValue(s.NodeId, out Session? aCur) && ReferenceEquals(aCur, s)) m_Sessions.Remove(s.NodeId);
        if (!m_Disposed || iWhy != "收工")
            m_Hooks.Out($"· 🌐 跨主機連線：跟 {s.NodeName}（{s.NodeId}）的線斷了 —— {iWhy}（收 {s.MsgIn} 則、送 {s.MsgOut} 則）");
        if (!s.ServerSide) m_NextDial = DateTime.UtcNow.AddSeconds(2);
    }

    void CloseAll(Func<Session, bool> iWhich, string iWhy)
    {
        foreach (Session s in Snapshot()) if (iWhich(s)) Close(s, iWhy);
    }

    // ── 收到的 frame ─────────────────────────────────────────────────

    /// <summary>回非 null ⇒ 這條線要收掉（原因）。</summary>
    string? Handle(Session s, SCP_JsonData j)
    {
        switch (j.GetString("t", ""))
        {
            case "sync": OnSync(s, j); return null;
            case "msg": return OnMsg(s, j);
            case "pos":
            {
                string aRoom = j.GetString("room", "");
                if (m_Cfg.Rooms.Contains(aRoom))
                    SenatePeerStore.SetCursor(m_DataRoot, s.NodeId, aRoom, j.GetInt("seq", -1), j.GetBool("base", false));
                return null;
            }
            case "presence": OnPresence(s, j); return null;
            case "bye": return "對面說再見：" + j.GetString("why", "");
            default: return null;   // 不認得的 frame 略過（新版多送的欄位不該讓舊版斷線）
        }
    }

    int LastSeq(string iRoom)
    {
        try
        {
            List<SCP_TavernMessage> t = SCP_TavernRead.Tail(m_DataRoot, iRoom, 1);
            return t.Count > 0 ? t[0].Seq : 0;
        }
        catch (Exception) { return 0; }
    }

    void OnSync(Session s, SCP_JsonData j)
    {
        List<string> aTheirs = SenatePeerStore.Strs(j["rooms"]);
        SenatePeerStore.NoteNode(m_DataRoot, s.NodeId, s.NodeName, "", SenatePeerStore.Strs(j["webhooks"]));
        lock (s.SendPos)
        {
            s.SendPos.Clear();
            foreach (string aRoom in m_Cfg.Rooms)
            {
                if (!aTheirs.Contains(aRoom)) continue;
                int aHave = j["have"].IsObject ? j["have"].GetInt(aRoom, -1) : -1;
                int aLast = LastSeq(aRoom);
                if (aHave < 0 || aHave > aLast)
                {
                    // 第一次接上（或我這邊的頻道比對方記得的還短）⇒ 從現在開始，⛔ 不回放歷史
                    SCP_JsonData p = Obj("pos");
                    p.Set("room", aRoom); p.Set("seq", aLast); p.Set("base", true);
                    Send(s, p);
                    m_Hooks.Out($"· 跨主機連線：{aRoom} → {s.NodeName} 從 seq {aLast} 之後開始送（不回放歷史）");
                    aHave = aLast;
                }
                s.SendPos[aRoom] = aHave;
            }
        }
        s.SyncReceived = true;
    }

    string? OnMsg(Session s, SCP_JsonData j)
    {
        string aRoom = j.GetString("room", "");
        int aSeq = j.GetInt("seq", -1);
        if (!m_Cfg.Rooms.Contains(aRoom) || aSeq < 0) return null;
        if (aSeq <= SenatePeerStore.GetCursor(m_DataRoot, s.NodeId, aRoom))               // 已經收過 ⇒ 不重複寫
        {
            Interlocked.Increment(ref m_DupDropped);
            return null;
        }

        string aOriginId = s.NodeId, aOriginName = s.NodeName;
        if (j["origin"].IsObject && s.ServerSide == false)   // 只有主機可以替別的節點轉發
        {
            aOriginId = j["origin"].GetString("id", s.NodeId);
            aOriginName = SenatePeerStore.CleanName(j["origin"].GetString("name", s.NodeName));
        }
        if (aOriginId == m_Me!.Id)
        {
            SenatePeerStore.SetCursor(m_DataRoot, s.NodeId, aRoom, aSeq);   // 我自己的訊息繞回來了：不寫，但游標照推
            return null;
        }
        // 線沒接著的那段時間，這一則可能已經從 Discord 進來過 ⇒ 不再寫一次，但游標照推
        int aOriginSeq = aOriginId == s.NodeId ? aSeq : j.GetInt("oseq", -1);
        if (aOriginSeq >= 0 && CameViaDiscord(s, aRoom, aOriginSeq, j.GetString("body", ""), j.GetString("ts", "")))
        {
            SenatePeerStore.SetCursor(m_DataRoot, s.NodeId, aRoom, aSeq);
            Interlocked.Increment(ref m_DiscordDupSkipped);
            return null;
        }

        string aBody = j.GetString("body", "");
        int aRefs = j.GetInt("refs", 0);
        if (aRefs > 0) aBody += (aBody.Length > 0 ? "\n" : "") + $"-# 📎 {aOriginName} 那邊的附件 {aRefs} 個沒有帶過來";
        string aDn = j.GetString("dn", "");
        string aSid = j.GetString("sid", "");

        var aMeta = SCP_JsonData.NewObject();
        aMeta.Set("source", "peer");
        aMeta.Set("relay", "senate");
        aMeta.Set("peer_id", aOriginId);
        aMeta.Set("peer_host", aOriginName);
        if (aOriginId != s.NodeId) aMeta.Set("peer_via", s.NodeName);
        aMeta.Set("peer_room", aRoom);
        aMeta.Set("peer_seq", aSeq.ToString());
        aMeta.Set("peer_sender", aSid);
        aMeta.Set("peer_dn", aDn);
        if (j.GetString("persona", "").Length > 0) aMeta.Set("peer_persona", j.GetString("persona", ""));
        if (j.GetString("tag", "").Length > 0) aMeta.Set("tag", j.GetString("tag", ""));

        var m = SCP_JsonData.NewObject();
        m.Set("sender_id", "peer:" + aOriginName + ":" + aSid);
        m.Set("sender_name", (aDn.Length > 0 ? aDn : aSid) + "〔" + aOriginName + "〕");
        m.Set("kind", "chat");
        m.Set("body", aBody);
        if (j.GetString("ts", "").Length > 0) m.Set("ts", j.GetString("ts", ""));
        m.Set("meta", aMeta);

        (bool aOk, int aLocalSeq, string aErr) = m_Hooks.WriteTavern(aRoom, m);
        if (!aOk)
        {
            // ⛔ 不推游標、不跳過：收掉這條線，重連之後對方會從這一則重送
            WarnOnce($"跨主機連線：{aOriginName} 的 {aRoom} seq {aSeq} 寫不進本機酒館 —— {aErr}");
            return "寫不進本機酒館（重連後從這一則重送）";
        }
        SenatePeerStore.SetCursor(m_DataRoot, s.NodeId, aRoom, aSeq);
        s.MsgIn++;
        string aPreview = aBody.Replace('\n', ' ');
        m_Hooks.Out($"· 📥 {aOriginName} → {aRoom} seq {aLocalSeq}：{(aPreview.Length > 40 ? aPreview.Substring(0, 40) + "…" : aPreview)}");
        return null;
    }

    static string Head(string iBody)
    {
        string t = (iBody ?? "").Trim();
        return t.Length <= 24 ? t : t.Substring(0, 24);
    }

    /// <summary>
    /// 這一則是不是已經從 Discord 進來過。判準：本機最近的訊息裡有一則「Discord 同伴」訊息，
    /// 它記的對方頻道與對方 seq 跟這一則相同、內文開頭也相同（Discord 那一份可能被拆段，所以只比開頭）。
    /// ⚠ 只對「這條線接上之前發的」訊息查：接上之後 Discord 那一路就不收這個節點的了，不會有第二份。
    /// </summary>
    bool CameViaDiscord(Session s, string iRoom, int iOriginSeq, string iBody, string iTs)
    {
        if (DateTime.TryParse(iTs, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime t)
            && t > s.Since.AddSeconds(15)) return false;
        Dictionary<string, List<string>> aMap;
        lock (s.DiscordCopies)
        {
            if (!s.DiscordCopies.TryGetValue(iRoom, out var aHave) || (DateTime.UtcNow - aHave.At).TotalSeconds > 20)
            {
                var aNew = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                try
                {
                    foreach (SCP_TavernMessage m in SCP_TavernRead.Tail(m_DataRoot, iRoom, 400))
                    {
                        if (!m.Meta.TryGetValue("discord_peer", out string? aPeer) || aPeer != "true") continue;
                        if (!m.Meta.TryGetValue("peer_room", out string? aPr) || aPr != iRoom) continue;
                        if (!m.Meta.TryGetValue("peer_seq", out string? aPs) || string.IsNullOrEmpty(aPs)) continue;
                        if (!aNew.TryGetValue(aPs, out List<string>? l)) aNew[aPs] = l = new List<string>();
                        l.Add(Head(m.Body));
                    }
                }
                catch (Exception) { }   // 讀不到 ⇒ 當成沒有（寧可多一則，不要漏一則）
                aHave = (DateTime.UtcNow, aNew);
                s.DiscordCopies[iRoom] = aHave;
            }
            aMap = aHave.Map;
        }
        if (!aMap.TryGetValue(iOriginSeq.ToString(), out List<string>? aHeads)) return false;
        string aMine = (iBody ?? "").Trim();
        return aHeads.Any(h => h.Length > 0 && aMine.StartsWith(h, StringComparison.Ordinal));
    }

    void OnPresence(Session s, SCP_JsonData j)
    {
        var aList = new List<PeerNodePresence>();
        if (j["nodes"].IsArray)
            foreach (SCP_JsonData n in j["nodes"])
            {
                var p = new PeerNodePresence { Id = n.GetString("id", ""), Name = SenatePeerStore.CleanName(n.GetString("name", "")) };
                // 另一端只能報它自己（⛔ 不能替別的節點報名單）
                if (s.ServerSide && p.Id != s.NodeId) continue;
                if (p.Id == m_Me!.Id) continue;
                if (n["online"].IsArray)
                    foreach (SCP_JsonData o in n["online"])
                        p.Online.Add(new PeerOnline { Name = o.GetString("name", ""), Agent = o.GetString("agent", ""), Status = o.GetString("status", "") });
                aList.Add(p);
            }
        s.Remote = aList;
    }

    // ── 送：新訊息 ───────────────────────────────────────────────────

    void Pump(Session s)
    {
        if (!s.SyncReceived || s.Closed) return;
        List<string> aRooms;
        lock (s.SendPos) aRooms = s.SendPos.Keys.ToList();
        string aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(m_DataRoot)).Value;
        foreach (string aRoom in aRooms)
        {
            int aPos;
            lock (s.SendPos) aPos = s.SendPos[aRoom];
            int aLast = LastSeq(aRoom);
            if (aLast <= aPos) continue;
            if (aLast - aPos > MaxBacklog)
            {
                WarnOnce($"跨主機連線：{aRoom} → {s.NodeName} 積壓 {aLast - aPos} 則（上限 {MaxBacklog}）⇒ 跳到 seq {aLast}，seq {aPos + 1}～{aLast} 沒送");
                aPos = aLast;
            }
            else
            {
                int aTo = Math.Min(aLast, aPos + BatchSize);
                foreach (SCP_TavernMessage m in SCP_TavernRead.Range(m_DataRoot, aRoom, aPos + 1, aTo).OrderBy(x => x.Seq))
                {
                    SCP_JsonData? f = BuildMsg(s, aRoom, m, aLetters);
                    if (f == null) continue;
                    if (!Send(s, f)) return;
                    s.MsgOut++;
                }
                aPos = aTo;
            }
            // 這一批裡沒送的（系統訊息、轉進來的）也要讓對方的游標往前，不然每次重連都從同一個地方重掃
            SCP_JsonData p = Obj("pos");
            p.Set("room", aRoom); p.Set("seq", aPos);
            if (!Send(s, p)) return;
            lock (s.SendPos) s.SendPos[aRoom] = aPos;
        }
    }

    /// <summary>這一則要不要送給這條線；要 ⇒ 回 frame。只送 chat；轉進來的只由主機轉給「不是原節點」的另一端。</summary>
    SCP_JsonData? BuildMsg(Session s, string iRoom, SCP_TavernMessage m, string iLetters)
    {
        string aKind = string.IsNullOrEmpty(m.Kind) ? "chat" : m.Kind;
        if (aKind != "chat" || string.IsNullOrEmpty(m.Body)) return null;
        m.Meta.TryGetValue("source", out string? aSrc);
        bool aFromPeer = (m.SenderId ?? "").StartsWith("peer:", StringComparison.Ordinal) || (aSrc ?? "").StartsWith("peer", StringComparison.Ordinal);

        SCP_JsonData f = Obj("msg");
        string aSid = m.SenderId ?? "", aPersona = m.SenderPersona ?? "", aDn;
        if (aFromPeer)
        {
            if (!s.ServerSide) return null;                                   // 另一端⛔不把收到的再送回主機
            m.Meta.TryGetValue("peer_id", out string? aOriginId);
            if (string.IsNullOrEmpty(aOriginId) || aOriginId == s.NodeId) return null;   // ⛔ 不送回原節點
            m.Meta.TryGetValue("peer_host", out string? aOriginName);
            m.Meta.TryGetValue("peer_sender", out string? aOrigSid);
            m.Meta.TryGetValue("peer_persona", out string? aOrigPersona);
            m.Meta.TryGetValue("peer_dn", out string? aOrigDn);
            aSid = aOrigSid ?? ""; aPersona = aOrigPersona ?? ""; aDn = aOrigDn ?? "";
            var o = SCP_JsonData.NewObject();
            o.Set("id", aOriginId!); o.Set("name", aOriginName ?? "");
            f.Set("origin", o);
            // 原節點那邊的 seq：收的一方拿它跟「從 Discord 進來的那一份」對重
            if (m.Meta.TryGetValue("peer_seq", out string? aOSeq) && int.TryParse(aOSeq, out int aOSeqN)) f.Set("oseq", aOSeqN);
        }
        else if (SCP_TavernMentions.IsExternalRelay(m.Meta, m.SenderId))
        {
            if (!m_Cfg.ForwardExternal) return null;                          // Discord 轉進來的：預設不送（對面自己會從 Discord 收）
            aDn = m.HasSenderName ? m.SenderName : aSid;
        }
        else
        {
            try { aDn = SCP_TavernDisplay.Resolve(iLetters, m).Name; }
            catch (Exception) { aDn = m.HasSenderName ? m.SenderName : aSid; }
        }
        f.Set("room", iRoom); f.Set("seq", m.Seq); f.Set("body", m.Body); f.Set("ts", m.Ts ?? "");
        f.Set("sid", aSid); f.Set("persona", aPersona); f.Set("dn", aDn);
        if (m.Meta.TryGetValue("tag", out string? aTag) && !string.IsNullOrEmpty(aTag)) f.Set("tag", aTag);
        if (m.Refs != null && m.Refs.Count > 0) f.Set("refs", m.Refs.Count);
        return f;
    }

    // ── 在線名單 ─────────────────────────────────────────────────────

    List<PeerOnline> LocalOnline()
    {
        var aList = new List<PeerOnline>();
        try
        {
            SCP_LettersRoot aRoot = SCP_DataPaths.Letters(new SCP_DataRoot(m_DataRoot));
            foreach (SCP_PersonaStatus p in SCP_PersonaLetters.Scan(aRoot.Value).Personas)
            {
                if (p.Online != SCP_PersonaOnline.Online) continue;
                string aStatus = "";
                try
                {
                    string aPath = SCP_LettersPaths.NowStatusPath(aRoot, p.Name);
                    if (File.Exists(aPath))
                    {
                        SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                        // 跟酒館追讀同一個判準：對不上這一場 lock 的是上一場的殘留
                        if (j.GetString("session_key", "") == p.SessionKey && j.GetString("locked_at", "") == p.LockedAt)
                            aStatus = j.GetString("now_status", "");
                    }
                }
                catch (Exception) { }
                aList.Add(new PeerOnline { Name = p.Name, Agent = p.Agent, Status = aStatus });
            }
        }
        catch (Exception) { }   // 掃不到 ⇒ 送空名單（對面顯示「沒有人在線」；這台自己的追讀會說讀取失敗）
        aList.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return aList;
    }

    static SCP_JsonData NodeJson(PeerNodePresence n)
    {
        var o = SCP_JsonData.NewObject();
        o.Set("id", n.Id); o.Set("name", n.Name);
        var a = SCP_JsonData.NewArray();
        foreach (PeerOnline p in n.Online)
        {
            var x = SCP_JsonData.NewObject();
            x.Set("name", p.Name); x.Set("agent", p.Agent); x.Set("status", p.Status);
            a.Add(x);
        }
        o.Set("online", a);
        return o;
    }

    void SendPresence(Session s)
    {
        if (!s.SyncReceived || s.Closed) return;
        var aNodes = SCP_JsonData.NewArray();
        aNodes.Add(NodeJson(new PeerNodePresence { Id = m_Me!.Id, Name = m_Cfg.NodeName, Online = m_LocalOnline }));
        if (s.ServerSide)   // 主機：把其他另一端的名單也帶給它
            foreach (Session o in Snapshot())
                if (!ReferenceEquals(o, s))
                    foreach (PeerNodePresence n in o.Remote) aNodes.Add(NodeJson(n));
        string aSig = aNodes.ToJson(false);
        if (aSig == s.PresenceSent && (DateTime.UtcNow - s.PresenceAt).TotalSeconds < PresenceEverySec) return;
        SCP_JsonData j = Obj("presence");
        j.Set("nodes", aNodes);
        if (!Send(s, j)) return;
        s.PresenceSent = aSig;
        s.PresenceAt = DateTime.UtcNow;
    }

    void WritePresence()
    {
        List<Session> aAll = Snapshot();
        var aNodes = new List<PeerNodePresence>();
        foreach (Session s in aAll) aNodes.AddRange(s.Remote);
        string aLink = m_Cfg.Mode == "off" ? "off" : aAll.Count > 0 ? "up" : "down";
        string aVia = m_Cfg.Mode == "client" ? (aAll.FirstOrDefault()?.NodeName ?? "") : "";
        string aSig = aLink + "|" + string.Join(",", aNodes.Select(n => NodeJson(n).ToJson(false)));
        bool aExists = File.Exists(SenatePeerStore.PresencePath(m_DataRoot));
        if (aLink == "off" && (!aExists || aSig == m_PresenceSig)) return;        // 從沒開過 ⇒ 不產生檔；關著 ⇒ 只寫一次
        if (aSig == m_PresenceSig && aExists && (DateTime.UtcNow - m_PresenceAt).TotalSeconds < 10) return;
        try
        {
            SenatePeerStore.WritePresence(m_DataRoot, m_Cfg.Mode, aLink, aVia, aNodes);
            m_PresenceSig = aSig;
            m_PresenceAt = DateTime.UtcNow;
        }
        catch (Exception e) { WarnOnce("跨主機連線：在線名單寫不進去 —— " + e.Message); }
    }

    // ── 位址與狀態 ───────────────────────────────────────────────────

    /// <summary>主機在區網裡可以被連的位址（每張啟用中的網卡的 IPv4）。只聽本機 ⇒ 只回本機那一個。</summary>
    public static List<string> LanAddresses(PeerConfig c)
    {
        var l = new List<string>();
        if (c.Bind != "0.0.0.0" && c.Bind.Length > 0) { l.Add($"ws://{c.Bind}:{c.Port}{WsPath}"); return l; }
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = a.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    l.Add($"ws://{ip}:{c.Port}{WsPath}");
                }
            }
        }
        catch (Exception) { }
        return l.Distinct().ToList();
    }

    void WriteStatus()
    {
        // 從沒開過的機器 ⇒ 不產生任何檔（⛔ 不讓每一台都長出一個 peer 資料夾）
        if (m_Cfg.Mode == "off" && !Directory.Exists(SenatePeerStore.Dir(m_DataRoot))) return;
        var j = SCP_JsonData.NewObject();
        j.Set("mode", m_Cfg.Mode); j.Set("name", m_Cfg.NodeName); j.Set("id", m_Me?.Id ?? "");
        j.Set("listening", m_Listener != null ? m_ListenKey : "");
        j.Set("last_warn", m_LastWarn);
        j.Set("dial_fails", m_DialFails);
        var aSess = SCP_JsonData.NewArray();
        foreach (Session s in Snapshot())
        {
            var o = SCP_JsonData.NewObject();
            o.Set("id", s.NodeId); o.Set("name", s.NodeName); o.Set("side", s.ServerSide ? "client" : "host"); o.Set("via", s.Via);
            o.Set("since", s.Since.ToString("yyyy-MM-ddTHH:mm:ssZ")); o.Set("in", s.MsgIn); o.Set("out", s.MsgOut);
            aSess.Add(o);
        }
        j.Set("sessions", aSess);
        lock (Extra) foreach (KeyValuePair<string, string> kv in Extra) j.Set(kv.Key, kv.Value);
        string aSig = j.ToJson(false);
        if (aSig == m_StatusSig && (DateTime.UtcNow - m_StatusAt).TotalSeconds < 5) return;
        j.Set("updated_at", SenatePeerStore.NowIso());
        try
        {
            string aPath = SenatePeerStore.Dir(m_DataRoot) + "/" + StatusFileName;
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            File.WriteAllText(aPath + ".tmp", j.ToJson(true) + "\n", new UTF8Encoding(false));
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aPath + ".tmp", aPath);
            m_StatusSig = aSig;
            m_StatusAt = DateTime.UtcNow;
        }
        catch (Exception) { }   // 狀態檔只給人看
    }
}
