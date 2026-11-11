// 區塊職責：`senate cmd peer` —— 跨主機連線（TASK-0011）的開關、設定、狀態、手動交換公告代碼、自我測試。
// 物理意義：真正連線的是酒館 Server 裡的 SenatePeerJob（每一輪重讀設定檔）；本支只**改設定檔與狀態檔**、讀它寫的狀態。
//           ⇒ 改完不必重啟 Server。後台「跨主機連線」頁按的是同一組操作。
// 數值影響：host／client／off／set／join／forget 會寫 `<機密資料夾>/peer/` 底下的檔；status／code／online 純讀；
//           selftest 只動暫存資料夾（⛔ 不碰真的酒館、不連 Discord、不開通道）。
// ⚠ exit code：0 成功；2 用法錯；4 量不到（設定檔／狀態檔讀不了）；5 自我測試有沒過的。
#nullable enable
using System.Net;
using System.Net.Sockets;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Discord;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed class Cmd_Peer : SCP_Cmd
{
    public override string Name => "peer";
    public override string Category => SCP_CmdCategory.Tavern;

    public override string Summary => "跨主機連線：一台當主機（host）、另一台連上去（client）—— 在線名單互通、酒館訊息經主機轉發；status 看現況";

    public override string Details =>
        "主機開著時會把「連我的位址」貼到 Discord（主頻道的分類綁的 webhook）；另一端的 Discord Inbound 收到就自動連上。\n"
        + "沒有 Discord 也可以手動：主機 `op=code` 印出公告代碼 ⇒ 另一端 `op=join --arg-file code=<檔>`。\n"
        + "⚠ 看得到公告的人就連得上主機：公告要貼在只有自己人的頻道。\n"
        + "⚠ 連線由酒館 Server 執行；它沒在跑，改了設定也不會有動靜（`op=status` 會說）。";

    public override string Example => SCP_CmdRegistry.Invoke("peer --arg op=host");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "status（預設）｜host（當主機，換一場新的 token）｜client（當另一端）｜off｜set（只改設定）｜code（主機：印公告代碼）｜join（另一端：收下公告代碼）｜forget（忘掉手上的公告）｜online（另一邊在線的人）｜probe（量這台的對外網路：路由器能不能開埠、有沒有公網 IP、NAT 的行為；唯讀）｜selftest",
            iDefault: "status", iChoices: new[] { "status", "host", "client", "off", "set", "code", "join", "forget", "online", "probe", "selftest" }),
        new SCP_CmdArgSpec("name", "host／client／set：這台的節點名（英數與 -_.，最多 24 字；空 ⇒ 電腦名稱）"),
        new SCP_CmdArgSpec("port", "host／set：主機收連線的埠（預設 47800）"),
        new SCP_CmdArgSpec("bind", "host／set：主機聽哪個位址（預設 0.0.0.0＝全部網卡；127.0.0.1＝只給通道用）"),
        new SCP_CmdArgSpec("tunnel", "host／set：auto（找得到 cloudflared 就開臨時通道）｜off", iChoices: new[] { "auto", "off" }),
        new SCP_CmdArgSpec("port_map", "host／set：auto（主機啟動時請路由器開對外的埠，UPnP；關主機時收回）｜off", iChoices: new[] { "auto", "off" }),
        new SCP_CmdArgSpec("cloudflared_path", "set：cloudflared 執行檔的路徑（空 ⇒ 從 PATH 找）"),
        new SCP_CmdArgSpec("public_url", "set：自己開了通訊埠轉發時的對外位址（例：ws://203.0.113.5:47800/peer）；`-` ⇒ 清掉"),
        new SCP_CmdArgSpec("announce_webhook_id", "set：公告貼到哪一條 webhook（空 ⇒ 主頻道的分類綁的那幾條）；`-` ⇒ 清掉"),
        new SCP_CmdArgSpec("rooms", "set：要串的頻道，逗號分隔（預設只有主頻道）"),
        new SCP_CmdArgSpec("forward_external", "set：1 ⇒ Discord 轉進來的訊息也送到另一邊（預設 0）", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("code", "join：主機的公告代碼（`SENATE-HOST1 …`）。長 ⇒ 走 `--arg-file`"),
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（沒給 ⇒ 用設定檔那一格）"),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aOp = iArgs.Get("op");
        if (aOp == "selftest") return SelfTest();
        string aData = iArgs.Get("data_root");
        if (aData.Length == 0 || !Directory.Exists(aData)) return SCP_CmdResult.Fail(2, $"✗ 解不出資料根（{aData}）—— 用 `senate cmd paths` 看");
        try
        {
            return aOp switch
            {
                "host" or "client" or "off" or "set" => Configure(aData, aOp, iArgs),
                "code" => Code(aData),
                "join" => Join(aData, iArgs.Get("code")),
                "forget" => Forget(aData),
                "online" => Online(aData),
                "probe" => Probe(aData),
                _ => Status(aData),
            };
        }
        catch (InvalidOperationException e) { return SCP_CmdResult.Fail(4, "✗ " + e.Message); }
    }

    // ── 設定 ─────────────────────────────────────────────────────────

    static SCP_CmdResult Configure(string iData, string iOp, SCP_CmdArgs iArgs)
    {
        PeerConfig c = SenatePeerStore.LoadConfig(iData);
        if (c.Error.Length > 0) return SCP_CmdResult.Fail(4, "✗ " + c.Error, "⛔ 不覆寫讀不了的設定檔：先把它修好或移走。");
        string aOld = c.Mode;
        if (iOp != "set") c.Mode = iOp;
        if (iArgs.Get("name").Length > 0) c.Name = SenatePeerStore.CleanName(iArgs.Get("name"));
        if (iArgs.Get("port").Length > 0)
        {
            if (!int.TryParse(iArgs.Get("port"), out int p) || p < 1 || p > 65535) return SCP_CmdResult.Fail(2, "✗ port 要是 1～65535 的整數：" + iArgs.Get("port"));
            c.Port = p;
        }
        if (iArgs.Get("bind").Length > 0)
        {
            if (!IPAddress.TryParse(iArgs.Get("bind"), out _)) return SCP_CmdResult.Fail(2, "✗ bind 要是 IP 位址：" + iArgs.Get("bind"));
            c.Bind = iArgs.Get("bind");
        }
        if (iArgs.Get("tunnel").Length > 0) c.Tunnel = iArgs.Get("tunnel");
        if (iArgs.Get("port_map").Length > 0) c.PortMap = iArgs.Get("port_map");
        if (iArgs.Get("cloudflared_path").Length > 0) c.CloudflaredPath = Dash(iArgs.Get("cloudflared_path"));
        if (iArgs.Get("public_url").Length > 0)
        {
            string u = Dash(iArgs.Get("public_url"));
            if (u.Length > 0 && !(u.StartsWith("ws://", StringComparison.Ordinal) || u.StartsWith("wss://", StringComparison.Ordinal)))
                return SCP_CmdResult.Fail(2, "✗ public_url 要以 ws:// 或 wss:// 開頭：" + u);
            c.PublicUrl = u;
        }
        if (iArgs.Get("announce_webhook_id").Length > 0) c.AnnounceWebhookId = Dash(iArgs.Get("announce_webhook_id"));
        if (iArgs.Get("rooms").Length > 0)
        {
            List<string> aRooms = iArgs.Get("rooms").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
            if (aRooms.Count == 0) return SCP_CmdResult.Fail(2, "✗ rooms 是空的");
            foreach (string r in aRooms)
                if (!SCP_TavernChannels.ChannelExists(iData, r)) return SCP_CmdResult.Fail(2, $"✗ 沒有這個頻道：'{r}'（⛔ 不代為建立）");
            c.Rooms = aRooms;
        }
        if (iArgs.Get("forward_external").Length > 0) c.ForwardExternal = iArgs.Get("forward_external") == "1";

        // 當主機 ⇒ 換一場新的 token（舊的公告作廢）；身分金鑰第一次用到時產生
        PeerIdentity aMe = SenatePeerStore.LoadOrCreateIdentity(iData);
        if (iOp == "host") SenatePeerStore.HostToken(iData, iRenew: true);
        SenatePeerStore.SaveConfig(iData, c);
        PeerConfig aBack = SenatePeerStore.LoadConfig(iData);   // 讀回：畫面上的要是磁碟上的

        var r2 = SCP_CmdResult.Success($"✓ 已寫入 {SenatePeerStore.ConfigPath(iData)}（讀回 mode={aBack.Mode}，原本 {aOld}）");
        r2.Lines.Add($"· 這台：{aBack.NodeName}（id {aMe.Id}）　埠 {aBack.Port}　聽 {aBack.Bind}　對外埠 {aBack.PortMap}　通道 {aBack.Tunnel}　頻道 {string.Join("、", aBack.Rooms)}");
        if (aBack.Mode == "host")
        {
            r2.Lines.Add("· 主機：酒館 Server 下一輪就會開埠，並把連線位址貼到 Discord（主頻道的分類綁的 webhook）。");
            if (aBack.PortMap == "auto" && !aBack.Bind.StartsWith("127.", StringComparison.Ordinal))
                r2.Lines.Add("· 對外埠：會請路由器把對外的 " + aBack.Port + " 埠轉到這台（UPnP），公網位址會寫進公告；關主機時收回。不要 ⇒ `--arg port_map=off`。");
            if (aBack.Tunnel == "auto" && SenatePeerTunnel.Find(aBack.CloudflaredPath).Length == 0)
                r2.Lines.Add("⚠ 這台找不到 cloudflared ⇒ **不會開通道**，只公告區網位址；不在同一個區網的另一端連不進來。");
            r2.Lines.Add("· 幾秒後看 `senate cmd peer`（status）。沒有 Discord 的話：`senate cmd peer --arg op=code` 把代碼手動交給另一端。");
        }
        else if (aBack.Mode == "client")
            r2.Lines.Add(SenatePeerStore.LoadAnnounce(iData) == null
                ? "· 另一端：等主機的公告（Discord Inbound 收到就會連）；或用 `op=join` 手動收下代碼。"
                : "· 另一端：手上已經有主機的公告，酒館 Server 下一輪就會去連。");
        r2.AddValue("mode", aBack.Mode);
        r2.AddValue("node_id", aMe.Id);
        r2.AddValue("wrote", "1");
        return r2;
    }

    static string Dash(string s) => s == "-" ? "" : s;

    // ── 公告代碼 ─────────────────────────────────────────────────────

    static SCP_CmdResult Code(string iData)
    {
        PeerConfig c = SenatePeerStore.LoadConfig(iData);
        if (c.Mode != "host") return SCP_CmdResult.Fail(2, $"✗ 這台的模式是 {c.Mode}，不是主機 ⇒ 沒有公告代碼（先 `op=host`）");
        string aCode = SenatePeerStore.HostGet(iData, "code");
        if (aCode.Length == 0)
            return SCP_CmdResult.Fail(4, "✗ 主機模式開著，但公告代碼還沒產生 —— 酒館 Server 還沒跑到那一輪（或沒在跑）。幾秒後再試；`op=status` 看它在不在。");
        PeerAnnounce? a = PeerAnnounce.FromText(aCode);
        var r = SCP_CmdResult.Success("# 主機的公告代碼（交給另一端：`senate cmd peer --arg op=join --arg-file code=<存成檔>`）");
        r.Lines.Add("⚠ 拿到這段代碼的人就連得上這台主機 —— 只交給自己人。");
        r.Lines.Add("");
        r.Lines.Add(aCode);
        r.Lines.Add("");
        if (a != null) r.Lines.Add($"· 裡面的位址（{a.Addrs.Count} 個）：{string.Join("　", a.Addrs)}");
        r.AddValue("addrs", (a?.Addrs.Count ?? 0).ToString());
        return r;
    }

    static SCP_CmdResult Join(string iData, string iCode)
    {
        if (iCode.Trim().Length == 0) return SCP_CmdResult.Fail(2, "✗ 沒有給代碼（`--arg-file code=<檔>`）");
        PeerAnnounce? a = PeerAnnounce.FromText(iCode);
        if (a == null) return SCP_CmdResult.Fail(2, "✗ 這段不是主機的公告代碼（找不到 `" + PeerAnnounce.Marker + " …`，或內容解不開）");
        PeerIdentity aMe = SenatePeerStore.LoadOrCreateIdentity(iData);
        if (a.Id == aMe.Id) return SCP_CmdResult.Fail(2, "✗ 這是這一台自己的公告 ⇒ 不收");
        if (a.Closed) return SCP_CmdResult.Fail(2, $"✗ 這是主機 {a.Name}「已關閉」的公告 ⇒ 不收");
        bool aTaken = SenatePeerStore.AcceptAnnounce(iData, a);
        PeerConfig c = SenatePeerStore.LoadConfig(iData);
        if (c.Error.Length > 0) return SCP_CmdResult.Fail(4, "✗ " + c.Error);
        string aOld = c.Mode;
        if (c.Mode != "client") { c.Mode = "client"; SenatePeerStore.SaveConfig(iData, c); }
        var r = SCP_CmdResult.Success(aTaken
            ? $"✓ 收下主機 {a.Name}（id {a.Id}）的公告：{a.Addrs.Count} 個位址"
            : $"· 手上已經有主機 {a.Name} 一樣新或更新的公告 ⇒ 沒有換");
        r.Lines.Add($"· 模式：{aOld} → client。酒館 Server 下一輪就會去連；幾秒後看 `senate cmd peer`。");
        r.AddValue("accepted", aTaken ? "1" : "0");
        r.AddValue("mode", "client");
        return r;
    }

    static SCP_CmdResult Forget(string iData)
    {
        bool aHad = SenatePeerStore.LoadAnnounce(iData) != null;
        SenatePeerStore.ClearAnnounce(iData);
        var r = SCP_CmdResult.Success(aHad ? "✓ 已忘掉手上的主機公告（另一端模式開著的話，會等下一則公告）" : "· 手上本來就沒有主機公告");
        r.AddValue("had", aHad ? "1" : "0");
        return r;
    }

    // ── 狀態 ─────────────────────────────────────────────────────────

    static SCP_JsonData? ReadStatusFile(string iData, out double oAge)
    {
        oAge = double.MaxValue;
        string p = SenatePeerStore.Dir(iData) + "/" + SenatePeerLink.StatusFileName;
        try
        {
            if (!File.Exists(p)) return null;
            SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(p, Encoding.UTF8));
            if (DateTime.TryParse(j.GetString("updated_at", ""), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime t))
                oAge = (DateTime.UtcNow - t).TotalSeconds;
            return j;
        }
        catch (Exception) { return null; }
    }

    public static List<string> StatusLines(string iData, out int oExit)
    {
        oExit = 0;
        var l = new List<string>();
        PeerConfig c = SenatePeerStore.LoadConfig(iData);
        l.Add("# 跨主機連線");
        if (c.Error.Length > 0) { l.Add("⚠ " + c.Error); oExit = 4; }
        string aId = File.Exists(SenatePeerStore.IdentityPath(iData)) ? SenatePeerStore.LoadOrCreateIdentity(iData).Id : "（還沒產生）";
        l.Add($"· 模式：{ModeText(c.Mode)}　這台：{c.NodeName}（id {aId}）");
        l.Add($"· 設定：埠 {c.Port}　聽 {c.Bind}　對外埠 {c.PortMap}　通道 {c.Tunnel}　頻道 {string.Join("、", c.Rooms)}"
              + (c.PublicUrl.Length > 0 ? "　對外位址 " + c.PublicUrl : "") + (c.ForwardExternal ? "　Discord 轉進來的也送" : ""));

        SCP_JsonData? s = ReadStatusFile(iData, out double aAge);
        bool aFresh = s != null && aAge <= 15;
        if (s == null) l.Add(c.Mode == "off"
            ? "· 執行狀態：關著（沒有在收連線，也沒有連出去）"
            : "· 執行狀態：**沒有讀數** —— 酒館 Server 還沒跑到這個工作，或沒在跑（`senate server status --id tavern`）");
        else if (!aFresh) l.Add($"· 執行狀態：**{aAge:0} 秒前的舊讀數** —— 酒館 Server 現在沒有在跑這個工作（下面是它最後一次寫的）");
        if (s != null)
        {
            if (s.GetString("mode", "") != c.Mode) l.Add($"  ⚠ Server 上次跑的模式是 {s.GetString("mode", "")}，設定檔已經是 {c.Mode}（還沒輪到）");
            if (s.GetString("listening", "").Length > 0) l.Add("· 收連線：" + s.GetString("listening", ""));
            if (s.GetString("addrs", "").Length > 0) l.Add("· 公告的位址：" + s.GetString("addrs", "").Replace(",", "　"));
            if (s.GetString("port_map", "").Length > 0) l.Add("· 對外埠（路由器）：" + s.GetString("port_map", ""));
            if (s.GetString("tunnel", "").Length > 0) l.Add("· 通道：" + s.GetString("tunnel", ""));
            if (s.GetString("announce", "").Length > 0) l.Add("· 公告：" + s.GetString("announce", ""));
            int n = s["sessions"].IsArray ? s["sessions"].Count : 0;
            l.Add($"· 連線：{n} 條" + (aFresh ? "" : "（舊讀數）"));
            if (s["sessions"].IsArray)
                foreach (SCP_JsonData x in s["sessions"])
                    l.Add($"  - {(x.GetString("side", "") == "host" ? "主機" : "另一端")} {x.GetString("name", "")}（{x.GetString("id", "")}）　經 {x.GetString("via", "")}　自 {x.GetString("since", "")}　收 {x.GetInt("in", 0)} 則／送 {x.GetInt("out", 0)} 則");
            if (s.GetString("last_warn", "").Length > 0) l.Add("· 最後一則警告：" + s.GetString("last_warn", ""));
        }
        if (c.Mode == "client")
        {
            PeerAnnounce? a = SenatePeerStore.LoadAnnounce(iData);
            l.Add(a == null ? "· 主機公告：還沒有收到過"
                : $"· 主機公告：{a.Name}（id {a.Id}）{(a.Closed ? "　**已關閉**" : "")}　{a.Ts}　{a.Addrs.Count} 個位址");
        }
        return l;
    }

    static string ModeText(string m) => m == "host" ? "主機（host）" : m == "client" ? "另一端（client）" : "關（off）";

    static SCP_CmdResult Status(string iData)
    {
        List<string> l = StatusLines(iData, out int aExit);
        var r = new SCP_CmdResult { ExitCode = aExit };
        r.Lines.AddRange(l);
        PeerConfig c = SenatePeerStore.LoadConfig(iData);
        SCP_JsonData? s = ReadStatusFile(iData, out double aAge);
        r.AddValue("mode", c.Mode);
        r.AddValue("server_fresh", s != null && aAge <= 15 ? "1" : "0");
        r.AddValue("sessions", s != null && aAge <= 15 && s["sessions"].IsArray ? s["sessions"].Count.ToString() : "unknown");
        return r;
    }

    static SCP_CmdResult Online(string iData)
    {
        var sb = new StringBuilder();
        SCP_PeerPresence.AppendTo(sb, iData);
        var r = SCP_CmdResult.Success();
        if (sb.Length == 0) r.Lines.Add("· 這台沒有開跨主機連線（或從來沒開過）⇒ 沒有另一邊的名單");
        else r.Lines.AddRange(sb.ToString().TrimEnd().Split('\n').Select(x => x.TrimEnd('\r')));
        SCP_PeerPresenceView v = SCP_PeerPresence.Read(iData);
        r.AddValue("live", v.Live ? "1" : "0");
        r.AddValue("remote_online", v.Live ? v.Nodes.Sum(n => n.Online.Count).ToString() : "unknown");
        return r;
    }

    // ── 量這台的對外網路（唯讀：不開埠、不改路由器）──────────────────────

    static string MaskIp(string iIp)
    {
        string[] a = iIp.Split('.');
        return a.Length == 4 ? a[0] + "." + a[1] + ".x.x" : iIp;
    }

    static SCP_CmdResult Probe(string iData)
    {
        PeerConfig c = SenatePeerStore.LoadConfig(iData);
        var r = SCP_CmdResult.Success("# 這台的對外網路（唯讀：不開埠、不改路由器；IP 只印前兩段）");
        PeerGateway? g = SenatePeerNat.Discover(out string aFindErr);
        string aExt = "";
        bool aExtPublic = false;
        if (g == null) r.Lines.Add("· 路由器（UPnP）：找不到 —— " + aFindErr);
        else
        {
            r.Lines.Add($"· 路由器（UPnP）：{g.RouterIp}　{g.Server}　這台在它底下是 {g.LocalIp}");
            aExt = SenatePeerNat.ExternalIp(g, out string aIpErr);
            aExtPublic = aExt.Length > 0 && SenatePeerNat.IsPublicIPv4(aExt);
            r.Lines.Add(aExt.Length == 0 ? "· 路由器回報的對外 IP：拿不到 —— " + aIpErr
                : $"· 路由器回報的對外 IP：{MaskIp(aExt)}（{(aExtPublic ? "公網位址" : "**不是公網位址** ⇒ 路由器外面還有一層 NAT")}）");
            PeerPortMapping? m = SenatePeerNat.GetMapping(g, c.Port);
            r.Lines.Add(m == null || m.Client.Length == 0 ? $"· 對外的 {c.Port} 埠（TCP）：現在沒有轉給任何一台"
                : $"· 對外的 {c.Port} 埠（TCP）：現在轉給 {m.Client}:{m.InternalPort}（{m.Description}）{(m.Client == g.LocalIp ? "　← 這台" : "　← **別台**")}");
        }
        PeerStunResult s = SenatePeerNat.StunProbe();
        if (s.Error.Length > 0) r.Lines.Add("· STUN：" + s.Error);
        else
        {
            r.Lines.Add($"· STUN（{s.Replies} 台回應）：外面看到 {MaskIp(s.PublicIp)}，本機埠 {s.LocalPort} → 對外埠 {string.Join("／", s.MappedPorts)}");
            r.Lines.Add("  " + (s.EndpointIndependent ? "每一台看到的都是同一個位址（好打洞的那一種）" : "每一台看到的不一樣，或只有一台回應（對稱型或量不準 ⇒ 打洞多半不成）")
                        + (s.PortPreserved ? "；對外埠跟本機埠同號" : ""));
            if (aExt.Length > 0) r.Lines.Add("  路由器回報的對外 IP 跟 STUN 看到的" + (aExt == s.PublicIp ? "**相同** ⇒ 只有一層 NAT" : "**不同** ⇒ 中間還有一層"));
        }
        string aVerdict = g != null && aExtPublic ? "這台當主機：可以自動開埠（port_map=auto），另一端從別的網路連得進來"
            : s.Error.Length == 0 && SenatePeerNat.IsPublicIPv4(s.PublicIp) && g == null ? "這台當主機：路由器不給自動開埠 ⇒ 要手動在路由器做通訊埠轉發，再設 public_url；或用通道"
            : "這台當主機：別的網路連不進來 ⇒ 讓另一台當主機，或用通道";
        r.Lines.Add("⇒ " + aVerdict);
        r.AddValue("upnp", g != null ? "1" : "0");
        r.AddValue("router_public", aExtPublic ? "1" : "0");
        r.AddValue("stun_replies", s.Replies.ToString());
        r.AddValue("endpoint_independent", s.Error.Length > 0 ? "unknown" : s.EndpointIndependent ? "1" : "0");
        r.AddValue("can_auto_map", g != null && aExtPublic ? "1" : "0");
        return r;
    }

    // ── 自我測試（暫存資料夾裡起三個節點，走本機迴路）────────────────────

    sealed class Node : IDisposable
    {
        public string Root = "", Name = "";
        public SenatePeerLink Link = null!;
        public List<string> Log = new();
        public void Dispose() => Link.Dispose();
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static Node MakeNode(string iBase, string iName, string iMode, int iPort)
    {
        var n = new Node { Root = Path.Combine(iBase, iName).Replace('\\', '/'), Name = iName };
        Directory.CreateDirectory(n.Root);
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
        });
        return n;
    }

    static int Post(Node n, string iPersona, string iBody)
    {
        SCP_TavernWriteResult w = SCP_TavernWriter.WriteMessage(n.Root, SCP_TavernChannels.MainChannelId,
            new SCP_TavernMessage { SenderId = "claudecode", SenderPersona = iPersona, Kind = "chat", Body = iBody });
        if (!w.Wrote) throw new InvalidOperationException("測試用的訊息寫不進去：" + w.Detail);
        return w.Seq;
    }

    static List<SCP_TavernMessage> All(Node n)
    {
        try { return SCP_TavernRead.Tail(n.Root, SCP_TavernChannels.MainChannelId, 1000).OrderBy(m => m.Seq).ToList(); }
        catch (Exception) { return new List<SCP_TavernMessage>(); }
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

    static SCP_CmdResult SelfTest()
    {
        var r = new SCP_CmdResult();
        List<string> aFails = RunSelfTest(r.Lines);
        r.ExitCode = aFails.Count == 0 ? 0 : 5;
        r.AddValue("failed", aFails.Count.ToString());
        return r;
    }

    /// <summary>自我測試本體（`senate selftest --only PeerLinkCleanRoom` 也跑這一支）。回沒過的項目；逐項讀數寫進 <paramref name="oLines"/>。</summary>
    public static List<string> RunSelfTest(List<string> oLines)
    {
        string aBase = Path.Combine(Path.GetTempPath(), "senate-peer-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        var r = new { Lines = oLines };
        var aFails = new List<string>();
        void Check(string iWhat, bool iOk, string iReading)
        {
            r.Lines.Add((iOk ? "✓ " : "✗ ") + iWhat + "　〔" + iReading + "〕");
            if (!iOk) aFails.Add(iWhat);
        }
        var aNodes = new List<Node>();
        try
        {
            int aPort = FreePort();
            Node A = MakeNode(aBase, "hostA", "host", aPort);
            Node B = MakeNode(aBase, "nodeB", "client", 0);
            Node C = MakeNode(aBase, "nodeC", "client", 0);
            Node D = MakeNode(aBase, "nodeD", "client", 0);
            aNodes.AddRange(new[] { A, B, C, D });
            string aRoom = SCP_TavernChannels.MainChannelId;

            // 主機上有一個在線的人（假的 lock）
            SCP_LettersRoot aLetA = SCP_DataPaths.Letters(new SCP_DataRoot(A.Root));
            string aLock = SCP_LettersPaths.SessionLockPath(aLetA, "Alice");
            Directory.CreateDirectory(Path.GetDirectoryName(aLock)!);
            File.WriteAllText(aLock, "{\"agent\":\"ClaudeCode\",\"session_key\":\"k\",\"locked_at\":\"t\"}");

            // 連上之前主機已經有 3 則 ⇒ 不該回放
            for (int i = 1; i <= 3; i++) Post(A, "Alice", "連線前的舊訊息 " + i);
            A.Link.Step();
            Check("主機開埠", A.Link.Listening, "listening=" + A.Link.Listening + " port=" + aPort);

            PeerIdentity aIdA = SenatePeerStore.LoadOrCreateIdentity(A.Root);
            var aAnn = new PeerAnnounce
            {
                Id = aIdA.Id, Name = "hostA", Sign = aIdA.SignPub, Token = SenatePeerStore.HostToken(A.Root), Ts = SenatePeerStore.NowIso(),
                Addrs = new List<string> { "ws://127.0.0.1:1" + SenatePeerLink.WsPath, $"ws://127.0.0.1:{aPort}{SenatePeerLink.WsPath}" },   // 第一個位址是壞的 ⇒ 要會試下一個
            };
            PeerAnnounce? aBack = PeerAnnounce.FromText("前面有別的字\n`" + aAnn.ToCode() + "`");
            Check("公告代碼來回", aBack != null && aBack.Id == aAnn.Id && aBack.Token == aAnn.Token && aBack.Addrs.Count == 2, "addrs=" + (aBack?.Addrs.Count ?? -1));
            var aForged = new PeerAnnounce { Id = aAnn.Id, Name = "x", Sign = SenatePeerCrypto.NewKeyPair().Pub, Token = "t", Ts = aAnn.Ts };
            Check("反向：id 跟公鑰對不上的公告不收", PeerAnnounce.FromText(aForged.ToCode()) == null, "parsed=" + (PeerAnnounce.FromText(aForged.ToCode()) != null));

            // ① B 連上主機
            SenatePeerStore.AcceptAnnounce(B.Root, aBack!);
            bool aUp = Until(() => A.Link.SessionCount == 1 && B.Link.SessionCount == 1, 20, A, B);
            Check("另一端連上主機（第一個位址壞的、第二個通）", aUp, $"host_sessions={A.Link.SessionCount} client_sessions={B.Link.SessionCount}");
            Settle(8, A, B);
            Check("第一次接上不回放歷史", All(B).Count == 0, "B 的訊息數=" + All(B).Count);

            // ② 主機 → 另一端
            int aSeqA = Post(A, "Alice", "主機說的話");
            bool aGot = Until(() => All(B).Any(m => m.Body == "主機說的話"), 10, A, B);
            SCP_TavernMessage? aAtB = All(B).FirstOrDefault(m => m.Body == "主機說的話");
            Check("主機的訊息到另一端", aGot, "B 收到=" + aGot);
            Check("轉進來的訊息帶來源", aAtB != null && aAtB.SenderId == "peer:hostA:claudecode" && aAtB.Meta.GetValueOrDefault("source") == "peer"
                                         && aAtB.Meta.GetValueOrDefault("peer_seq") == aSeqA.ToString() && aAtB.Meta.GetValueOrDefault("peer_id") == aIdA.Id,
                $"sender_id={aAtB?.SenderId} source={aAtB?.Meta.GetValueOrDefault("source")} peer_seq={aAtB?.Meta.GetValueOrDefault("peer_seq")}（主機 seq {aSeqA}）");

            // ③ 另一端 → 主機，而且不繞回來
            Post(B, "Bob", "另一端說的話");
            bool aGotA = Until(() => All(A).Any(m => m.Body == "另一端說的話"), 10, A, B);
            Check("另一端的訊息到主機", aGotA, "A 收到=" + aGotA);
            Settle(10, A, B);
            Check("反向：不迴圈（B 自己那則沒有被送回來）", All(B).Count(m => m.Body == "另一端說的話") == 1 && All(A).Count(m => m.Body == "另一端說的話") == 1,
                $"B 有 {All(B).Count(m => m.Body == "另一端說的話")} 則、A 有 {All(A).Count(m => m.Body == "另一端說的話")} 則");

            // ④ 第二個另一端：主機轉發
            SenatePeerStore.AcceptAnnounce(C.Root, aBack!);
            bool aUpC = Until(() => A.Link.SessionCount == 2 && C.Link.SessionCount == 1, 20, A, B, C);
            Check("第二個另一端連上", aUpC, "host_sessions=" + A.Link.SessionCount);
            Post(B, "Bob", "B 給大家的話");
            bool aFwd = Until(() => All(C).Any(m => m.Body == "B 給大家的話"), 10, A, B, C);
            SCP_TavernMessage? aAtC = All(C).FirstOrDefault(m => m.Body == "B 給大家的話");
            Check("主機把另一端的訊息轉給其他另一端", aFwd && aAtC != null && aAtC.Meta.GetValueOrDefault("peer_host") == "nodeB" && aAtC.Meta.GetValueOrDefault("peer_via") == "hostA",
                $"C 收到={aFwd} peer_host={aAtC?.Meta.GetValueOrDefault("peer_host")} peer_via={aAtC?.Meta.GetValueOrDefault("peer_via")} sender_id={aAtC?.SenderId}");
            Settle(10, A, B, C);
            Check("反向：轉發不重複", All(B).Count(m => m.Body == "B 給大家的話") == 1 && All(C).Count(m => m.Body == "B 給大家的話") == 1 && All(A).Count(m => m.Body == "B 給大家的話") == 1,
                $"A/B/C 各有 {All(A).Count(m => m.Body == "B 給大家的話")}/{All(B).Count(m => m.Body == "B 給大家的話")}/{All(C).Count(m => m.Body == "B 給大家的話")} 則");

            // ⑤ 在線名單
            bool aPres = Until(() =>
            {
                SCP_PeerPresenceView v = SCP_PeerPresence.Read(B.Root);
                return v.Live && v.Nodes.Any(n => n.Name == "hostA" && n.Online.Any(p => p.Name == "Alice")) && v.Nodes.Any(n => n.Name == "nodeC");
            }, 10, A, B, C);
            SCP_PeerPresenceView aView = SCP_PeerPresence.Read(B.Root);
            Check("在線名單：另一端看得到主機上的人與別的節點", aPres,
                "live=" + aView.Live + " nodes=" + string.Join("；", aView.Nodes.Select(n => n.Name + "[" + string.Join(",", n.Online.Select(p => p.Name)) + "]")));
            var aSb = new StringBuilder();
            SCP_PeerPresence.AppendTo(aSb, B.Root);
            Check("酒館追讀的段落有另一台主機的人", aSb.ToString().Contains("**Alice**（ClaudeCode）〔hostA〕"), aSb.ToString().Replace("\r", "").Replace("\n", " ⏎ ").Trim());

            // ⑥ 斷線期間的訊息，重連後補；不重複
            SenatePeerConfigSet(C.Root, "off");
            Until(() => A.Link.SessionCount == 1, 10, A, B, C);
            int aSeqGone = Post(A, "Alice", "C 不在的時候 1");
            Post(B, "Bob", "C 不在的時候 2");
            // 線斷著的時候，主機的那一則已經從 Discord 進到 C（模擬 Discord Inbound 寫下的同伴訊息）
            var aViaDiscord = new SCP_TavernMessage { SenderId = "discord:222", SenderName = "Alice", Kind = "chat", Body = "C 不在的時候 1" };
            aViaDiscord.Meta["source"] = "discord"; aViaDiscord.Meta["discord_peer"] = "true"; aViaDiscord.Meta["discord_webhook_id"] = "222";
            aViaDiscord.Meta["peer_room"] = aRoom; aViaDiscord.Meta["peer_seq"] = aSeqGone.ToString();
            SCP_TavernWriter.WriteMessage(C.Root, aRoom, aViaDiscord);
            Settle(10, A, B, C);
            int aBefore = All(C).Count;
            SCP_PeerPresenceView aOffView = SCP_PeerPresence.Read(C.Root);
            Check("關掉之後另一邊的名單不算數", !aOffView.Live, "link=" + aOffView.Link + " live=" + aOffView.Live);
            SenatePeerConfigSet(C.Root, "client");
            bool aCatch = Until(() => All(C).Any(m => m.Body == "C 不在的時候 1") && All(C).Any(m => m.Body == "C 不在的時候 2"), 20, A, B, C);
            Settle(10, A, B, C);
            Check("重連後補上斷線期間的訊息；已經從 Discord 進來的那一則不再寫第二次",
                aCatch && All(C).Count == aBefore + 1 && All(C).Count(m => m.Body == "C 不在的時候 1") == 1 && C.Link.DiscordDupSkipped == 1,
                $"C 原本 {aBefore} 則（含 Discord 進來的 1 則）、現在 {All(C).Count} 則（應該只多 1）；「C 不在的時候 1」有 {All(C).Count(m => m.Body == "C 不在的時候 1")} 則；對重略過 {C.Link.DiscordDupSkipped} 則");

            // ⑦ 同一則重送 ⇒ 只落一則（把 C 手上的游標往回撥是做不到的 ⇒ 直接讓主機重送：收掉連線、把主機記的起點留著）
            int aCountC = All(C).Count;
            int aCursor = SenatePeerStore.GetCursor(C.Root, aIdA.Id, aRoom);
            SenatePeerConfigSet(C.Root, "off");
            Until(() => A.Link.SessionCount == 1, 10, A, B, C);
            SenatePeerConfigSet(C.Root, "client");
            Until(() => A.Link.SessionCount == 2, 20, A, B, C);
            Settle(10, A, B, C);
            Check("反向：重連沒有新訊息 ⇒ 一則都沒多", All(C).Count == aCountC, $"C {aCountC} → {All(C).Count} 則；游標 {aCursor} → {SenatePeerStore.GetCursor(C.Root, aIdA.Id, aRoom)}");
            int aDupBefore = C.Link.DupDropped + B.Link.DupDropped, aCountB = All(B).Count;
            A.Link.DebugRewind(aRoom, 4);   // 主機把最後 4 則重送一次
            Until(() => C.Link.DupDropped + B.Link.DupDropped > aDupBefore, 10, A, B, C);
            Settle(10, A, B, C);
            Check("反向：同一則重送 ⇒ 被擋下、一則都沒多", C.Link.DupDropped + B.Link.DupDropped > aDupBefore && All(C).Count == aCountC && All(B).Count == aCountB,
                $"擋下 {C.Link.DupDropped + B.Link.DupDropped - aDupBefore} 則；C {aCountC} → {All(C).Count} 則、B {aCountB} → {All(B).Count} 則");

            // ⑧ token 不對的連不上、一則都進不來
            var aBad = new PeerAnnounce { Id = aAnn.Id, Name = aAnn.Name, Sign = aAnn.Sign, Token = SenatePeerCrypto.RandomToken(24), Ts = SenatePeerStore.NowIso(), Addrs = new List<string> { aAnn.Addrs[1] } };
            SenatePeerStore.AcceptAnnounce(D.Root, aBad);
            Post(A, "Alice", "D 不該看到這一則");
            Settle(25, A, B, C, D);
            bool aRefused;
            lock (A.Log) aRefused = A.Log.Any(x => x.Contains("token 不對"));
            Check("反向：token 不對的另一端連不上", D.Link.SessionCount == 0 && A.Link.SessionCount == 2 && aRefused, $"D 的連線={D.Link.SessionCount} 主機連線數={A.Link.SessionCount} 主機有說拒絕={aRefused}");
            Check("反向：連不上的那一台一則都沒進來", All(D).Count == 0, "D 的訊息數=" + All(D).Count);

            // ⑨ 冒充主機（公告裡的公鑰不是真的那一把）⇒ 另一端自己不連
            (string _, string aFakePub) = SenatePeerCrypto.NewKeyPair();
            var aFake = new PeerAnnounce { Id = SenatePeerCrypto.IdOf(aFakePub), Name = "fake", Sign = aFakePub, Token = aAnn.Token, Ts = SenatePeerStore.NowIso(), Addrs = new List<string> { aAnn.Addrs[1] } };
            SenatePeerStore.AcceptAnnounce(D.Root, aFake);
            Settle(25, A, B, C, D);
            bool aMismatch;
            lock (D.Log) aMismatch = D.Log.Any(x => x.Contains("不是公告的那一台"));
            Check("反向：對面不是公告的那一台 ⇒ 不連", D.Link.SessionCount == 0 && aMismatch, $"D 的連線={D.Link.SessionCount} 有說原因={aMismatch}");

            // ⑩ 周邊的規則
            Check("轉進來的不發薪", !SCP_TavernPayroll.IsRealAgentSender("peer:hostA:claudecode") && SCP_TavernPayroll.IsRealAgentSender("claudecode"),
                "peer:…=" + SCP_TavernPayroll.IsRealAgentSender("peer:hostA:claudecode") + " claudecode=" + SCP_TavernPayroll.IsRealAgentSender("claudecode"));
            Check("轉進來的算外部中繼（Discord 不重送）", aAtB != null && SCP_TavernMentions.IsExternalRelay(aAtB.Meta, aAtB.SenderId), "IsExternalRelay=" + (aAtB != null && SCP_TavernMentions.IsExternalRelay(aAtB.Meta, aAtB.SenderId)));
            var aAsk = new SCP_TavernMessage { SenderId = "peer:hostA:claudecode", Kind = "chat", Body = "@" + SenateBartender.PersonaId + " 來一杯" };
            var aAskLocal = new SCP_TavernMessage { SenderId = "claudecode", Kind = "chat", Body = "@" + SenateBartender.PersonaId + " 來一杯" };
            Check("轉進來的不觸發酒保", SenateBartender.Decide(aAsk, null) == BartenderAction.None && SenateBartender.Decide(aAskLocal, null) == BartenderAction.Mention,
                "peer=" + SenateBartender.Decide(aAsk, null) + " 本機=" + SenateBartender.Decide(aAskLocal, null));
            Check("通道網址讀得出來", SenatePeerTunnel.ToWs(SenatePeerTunnel.ParseUrl("2026-11-11T05:00:00Z INF |  https://quiet-river-1234.trycloudflare.com  |")) == "wss://quiet-river-1234.trycloudflare.com/peer"
                                      && SenatePeerTunnel.ParseUrl("INF Starting tunnel") == "",
                SenatePeerTunnel.ToWs(SenatePeerTunnel.ParseUrl("INF |  https://quiet-river-1234.trycloudflare.com  |")));

            // ⑩之二 對外位址那一層的純函式（⛔ 不碰路由器、不送 STUN）
            Check("公網位址判定", SenatePeerNat.IsPublicIPv4("60.248.1.2") && !SenatePeerNat.IsPublicIPv4("192.168.14.1") && !SenatePeerNat.IsPublicIPv4("10.0.0.5")
                                  && !SenatePeerNat.IsPublicIPv4("172.20.1.1") && !SenatePeerNat.IsPublicIPv4("100.72.0.9") && !SenatePeerNat.IsPublicIPv4("abc"),
                $"60.248.1.2={SenatePeerNat.IsPublicIPv4("60.248.1.2")} 192.168.14.1={SenatePeerNat.IsPublicIPv4("192.168.14.1")} 100.72.0.9（電信商多層 NAT）={SenatePeerNat.IsPublicIPv4("100.72.0.9")}");
            const string aDesc = "<root><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:Layer3Forwarding:1</serviceType><controlURL>/ctl/L3F</controlURL></service></serviceList>"
                                 + "<deviceList><device><serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><serviceId>x</serviceId><controlURL>/ctl/IPConn</controlURL></service></serviceList></device></deviceList></device></root>";
            bool aDescOk = SenatePeerNat.TryParseDescription(aDesc, "http://192.168.14.1:5000/rootDesc.xml", out string aSvcT, out string aCtlU);
            Check("路由器描述：找出對外連線的服務與控制網址", aDescOk && aSvcT.EndsWith("WANIPConnection:1", StringComparison.Ordinal) && aCtlU == "http://192.168.14.1:5000/ctl/IPConn"
                                                              && !SenatePeerNat.TryParseDescription("<root/>", "http://x/", out _, out _),
                $"service={aSvcT} control={aCtlU}");
            Check("路由器的錯誤讀得出來", SenatePeerNat.SoapError("<s:Fault><detail><UPnPError><errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription></UPnPError></detail></s:Fault>") == "718 ConflictInMappingEntry"
                                          && SenatePeerNat.SoapError("<u:GetExternalIPAddressResponse><NewExternalIPAddress>1.2.3.4</NewExternalIPAddress></u:GetExternalIPAddressResponse>") == ""
                                          && SenatePeerNat.XmlValue("<a><NewExternalIPAddress>1.2.3.4</NewExternalIPAddress></a>", "NewExternalIPAddress") == "1.2.3.4",
                SenatePeerNat.SoapError("<errorCode>718</errorCode><errorDescription>ConflictInMappingEntry</errorDescription>"));
            byte[] aTxnT = new byte[12];
            byte[] aStunT = new byte[32];
            aStunT[1] = 1; aStunT[3] = 12; aStunT[4] = 0x21; aStunT[5] = 0x12; aStunT[6] = 0xA4; aStunT[7] = 0x42;
            aStunT[21] = 0x20; aStunT[23] = 8; aStunT[25] = 1;
            aStunT[26] = (byte)((58207 >> 8) ^ 0x21); aStunT[27] = (byte)((58207 & 0xFF) ^ 0x12);
            aStunT[28] = 60 ^ 0x21; aStunT[29] = 248 ^ 0x12; aStunT[30] = 1 ^ 0xA4; aStunT[31] = 2 ^ 0x42;
            (string Ip, int Port)? aStunBack = SenatePeerNat.ParseStunReply(aStunT, 32, aTxnT);
            Check("STUN 回應解得出對外位址", aStunBack != null && aStunBack.Value.Ip == "60.248.1.2" && aStunBack.Value.Port == 58207
                                             && SenatePeerNat.ParseStunReply(aStunT, 32, new byte[] { 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }) == null,
                aStunBack == null ? "（解不出來）" : aStunBack.Value.Ip + ":" + aStunBack.Value.Port);

            // ⑪ Discord 那一側與常駐工作（⛔ 不連 Discord：暫存資料根沒有 webhook，公告一定貼不出去 —— 驗的就是它會說出來、而且代碼照樣產生）
            string aDisc = Path.Combine(aBase, "disc").Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(aDisc, "ChatTavern", "discord"));
            File.WriteAllText(Path.Combine(aDisc, "ChatTavern", "discord", SCP_DiscordPaths.ConfigFileName), "{\"webhooks\":[{\"id\":\"111\",\"enabled\":true}]}");
            var aRoute = new SCP_DiscordRoute { ChannelId = "9", TavernRoom = aRoom, Label = "probe" };
            SCP_JsonData aHookMsg = SCP_JsonParser.Parse("{\"id\":\"1554045138668486738\",\"content\":\"午安\",\"webhook_id\":\"222\",\"author\":{\"id\":\"222\",\"username\":\"gura\",\"bot\":true}}");
            string aFree = SCP_DiscordInbound.Convert(aDisc, aDisc, aRoute, new SCP_DiscordWhitelist(), aHookMsg, out SCP_DiscordInboundItem? aFreeItem, false);
            HashSet<string> aKeep = SCP_DiscordInbound.LinkedWebhookIds;
            SCP_DiscordInbound.LinkedWebhookIds = new HashSet<string>(StringComparer.Ordinal) { "222" };
            string aLinkedWhy = SCP_DiscordInbound.Convert(aDisc, aDisc, aRoute, new SCP_DiscordWhitelist(), aHookMsg, out SCP_DiscordInboundItem? aLinkedItem, false);
            SCP_DiscordInbound.LinkedWebhookIds = aKeep;
            Check("已經接上的節點，它在 Discord 上的訊息不再收一次（沒接上的照收）", aFreeItem != null && aLinkedItem == null && aLinkedWhy.StartsWith("linked-peer-webhook", StringComparison.Ordinal),
                $"沒接上：收={aFreeItem != null}（{aFree}）　接上：收={aLinkedItem != null}（{aLinkedWhy}）");

            int aPortH = FreePort();
            string aRootH = Path.Combine(aBase, "hostJob").Replace('\\', '/');
            Directory.CreateDirectory(aRootH);
            SenatePeerStore.SaveConfig(aRootH, new PeerConfig { Mode = "host", Name = "hostJob", Port = aPortH, Bind = "127.0.0.1", Tunnel = "auto", PortMap = "off", CloudflaredPath = Path.Combine(aBase, "no-such-cloudflared.exe") });
            var aJobLog = new List<string>();
            Action<string> aJobOut = s => { lock (aJobLog) aJobLog.Add(s); };
            Node J = MakeNode(aBase, "nodeJ", "client", 0);
            // J 自己有一條 Discord webhook（id 777）⇒ 連上主機時會報給主機
            Directory.CreateDirectory(Path.Combine(J.Root, "ChatTavern", "discord"));
            File.WriteAllText(Path.Combine(J.Root, "ChatTavern", "discord", SCP_DiscordPaths.ConfigFileName), "{\"webhooks\":[{\"id\":\"777\",\"enabled\":true}]}");
            aNodes.Add(J);
            try
            {
                DateTime aEnd = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < aEnd && SenatePeerStore.HostGet(aRootH, "code").Length == 0) { SenatePeerJob.Tick(aRootH, aJobOut, aJobOut); Thread.Sleep(200); }
                string aCode = SenatePeerStore.HostGet(aRootH, "code");
                PeerAnnounce? aJobAnn = PeerAnnounce.FromText(aCode);
                Check("常駐工作：主機模式產生公告代碼", aJobAnn != null && aJobAnn.Addrs.Count == 1 && aJobAnn.Addrs[0] == $"ws://127.0.0.1:{aPortH}{SenatePeerLink.WsPath}",
                    "addrs=" + (aJobAnn == null ? "（沒有代碼）" : string.Join(",", aJobAnn.Addrs)));
                aEnd = DateTime.UtcNow.AddSeconds(10);
                bool aSaidTunnel = false, aSaidPost = false;
                while (DateTime.UtcNow < aEnd && !(aSaidTunnel && aSaidPost))
                {
                    SenatePeerJob.Tick(aRootH, aJobOut, aJobOut);
                    Thread.Sleep(200);
                    lock (aJobLog) { aSaidTunnel = aJobLog.Any(x => x.Contains("找不到 cloudflared")); aSaidPost = aJobLog.Any(x => x.Contains("主機公告沒有貼出去") && x.Contains("op=code")); }
                }
                Check("反向：沒裝 cloudflared 會說出來（不是靜默不開通道）", aSaidTunnel, "有說=" + aSaidTunnel);
                Check("反向：公告貼不出去會說出來，並指到手動的路", aSaidPost, "有說=" + aSaidPost);

                string aPosted = "📡 **Senate 主機開放連線** · hostJob\n`" + aCode + "`";
                SenatePeerJob.OnDiscordSignals(aRootH, new[] { aPosted }, aJobOut, aJobOut);
                SenatePeerJob.OnDiscordSignals(J.Root, new[] { aPosted, "SENATE-HOST1 這不是代碼這不是代碼這不是代碼" }, aJobOut, aJobOut);
                PeerAnnounce? aAtJ = SenatePeerStore.LoadAnnounce(J.Root);
                Check("Discord 上的公告：另一端收下、主機自己不收自己的", aAtJ != null && aJobAnn != null && aAtJ.Id == aJobAnn.Id && SenatePeerStore.LoadAnnounce(aRootH) == null,
                    $"另一端手上的主機 id={aAtJ?.Id}　主機自己手上有公告={SenatePeerStore.LoadAnnounce(aRootH) != null}");
                aEnd = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < aEnd && J.Link.SessionCount == 0) { SenatePeerJob.Tick(aRootH, aJobOut, aJobOut); J.Link.Step(); Thread.Sleep(200); }
                Check("另一端照公告連上常駐工作開的主機", J.Link.SessionCount == 1, "另一端的連線=" + J.Link.SessionCount);

                // Discord 那一路收不收，看的是線有沒有**接著**
                aEnd = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < aEnd && !SCP_DiscordInbound.LinkedWebhookIds.Contains("777")) { SenatePeerJob.Tick(aRootH, aJobOut, aJobOut); J.Link.Step(); Thread.Sleep(200); }
                bool aSkipUp = SCP_DiscordInbound.LinkedWebhookIds.Contains("777");
                SenatePeerConfigSet(J.Root, "off");
                aEnd = DateTime.UtcNow.AddSeconds(15);
                while (DateTime.UtcNow < aEnd && SCP_DiscordInbound.LinkedWebhookIds.Contains("777")) { SenatePeerJob.Tick(aRootH, aJobOut, aJobOut); J.Link.Step(); Thread.Sleep(200); }
                bool aSkipDown = SCP_DiscordInbound.LinkedWebhookIds.Contains("777");
                Check("線接著 ⇒ 那個節點在 Discord 上的訊息略過；線斷了 ⇒ 照收（主機這一側）", aSkipUp && !aSkipDown, $"接著時略過={aSkipUp}　斷了之後還略過={aSkipDown}");
                SenatePeerJob.Stop();

                // 另一端這一側：手上有主機的公告、但連不上 ⇒ ⛔ 不可以略過主機在 Discord 上的訊息（2026-11-11 實地咬到的）
                string aRootK = Path.Combine(aBase, "clientJob").Replace('\\', '/');
                Directory.CreateDirectory(aRootK);
                SenatePeerStore.SaveConfig(aRootK, new PeerConfig { Mode = "client", Name = "clientJob", Bind = "127.0.0.1", Tunnel = "off", PortMap = "off" });
                (string _, string aPubK) = SenatePeerCrypto.NewKeyPair();
                SenatePeerStore.AcceptAnnounce(aRootK, new PeerAnnounce
                {
                    Id = SenatePeerCrypto.IdOf(aPubK), Name = "farHost", Sign = aPubK, Token = "t", Ts = SenatePeerStore.NowIso(),
                    Addrs = new List<string> { "ws://127.0.0.1:1" + SenatePeerLink.WsPath }, Webhooks = new List<string> { "888" },
                });
                for (int i = 0; i < 20; i++) { SenatePeerJob.Tick(aRootK, aJobOut, aJobOut); Thread.Sleep(200); }
                Check("反向：手上有公告但沒連上 ⇒ 主機在 Discord 上的訊息照收", !SCP_DiscordInbound.LinkedWebhookIds.Contains("888"), "略過名單裡有主機的 webhook=" + SCP_DiscordInbound.LinkedWebhookIds.Contains("888"));
            }
            finally { SenatePeerJob.Stop(); }
        }
        catch (Exception e)
        {
            aFails.Add("例外");
            r.Lines.Add("✗ 測試中途炸了：" + e.GetType().Name + "：" + e.Message);
            r.Lines.Add(e.StackTrace ?? "");
        }
        finally
        {
            foreach (Node n in aNodes) { try { n.Dispose(); } catch { } }
            if (aFails.Count > 0)
            {
                r.Lines.Add("── 各節點的紀錄（沒過才印）──");
                foreach (Node n in aNodes) lock (n.Log) foreach (string x in n.Log.TakeLast(12)) r.Lines.Add($"[{n.Name}] {x}");
                r.Lines.Add("暫存資料夾留著給你看：" + aBase);
            }
            else { try { Directory.Delete(aBase, true); } catch { } }
        }
        r.Lines.Add(aFails.Count == 0 ? "✓ 全部通過" : "✗ 沒過 " + aFails.Count + " 項：" + string.Join("、", aFails));
        return aFails;
    }

    static void SenatePeerConfigSet(string iRoot, string iMode)
    {
        PeerConfig c = SenatePeerStore.LoadConfig(iRoot);
        c.Mode = iMode;
        SenatePeerStore.SaveConfig(iRoot, c);
    }
}
