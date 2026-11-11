// 區塊職責：**主機的對外位址**（TASK-0011）—— 請路由器開一個對外的埠（UPnP）、查對外 IP、量 NAT 的行為（STUN）。
// 物理意義（Tim 2026-11-11：「需要不限制區網的連線」「做自動開埠」）：
//   · 主機有公網 IP、路由器支援 UPnP ⇒ 啟動主機時請路由器把對外的埠轉到這台，公告裡多一個 `ws://<公網 IP>:<埠>/peer`。
//     另一端只要能連出去，在哪個網路都連得進來；不必裝任何東西，也不經過第三方。
//   · 路由器回報的對外 IP 不是公網位址 ⇒ 路由器外面還有一層 NAT，開了也連不進來 ⇒ 不公告、說出來。
//   · 租期：先要 1 小時、定時續租；路由器只收永久的才給永久。關主機／Server 收工時刪掉。
//     ⚠ Server 被硬砍時那一筆會留到租期到（永久的就一直留著）。
//   · STUN 只用在 `senate cmd peer --arg op=probe`（量這台的 NAT 適不適合當主機／之後打洞），連線本身不靠它。
// 數值影響：UPnP 是對**區網裡的路由器**說話（SSDP 多播＋HTTP）；STUN 是對公用伺服器送一個 UDP 封包。
//           ⛔ 本檔只在主機模式（或有人下 probe）時被呼叫；另一端模式與關著時一個封包都不送。
#nullable enable
using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Senate.Core;

public sealed class PeerGateway
{
    public string ControlUrl = "", ServiceType = "", RouterIp = "", LocalIp = "", Server = "";
}

public sealed class PeerPortMapping
{
    public string Client = "", Description = "";
    public int InternalPort;
}

public sealed class PeerStunResult
{
    public string PublicIp = "", Error = "";
    public int LocalPort;
    public List<int> MappedPorts = new();
    public int Replies;
    /// <summary>對每一台伺服器都是同一個對外位址 ⇒ 打洞容易。</summary>
    public bool EndpointIndependent;
    public bool PortPreserved;
}

public static class SenatePeerNat
{
    public const string MappingDescription = "Senate peer link";
    public const int LeaseSeconds = 3600;
    const int HttpTimeoutSec = 5;
    const string IgdSt = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";

    // ── 純函式（自我測試直接驗這幾支）─────────────────────────────────

    /// <summary>是不是公網 IPv4：私有網段、電信商多層 NAT（100.64/10）、連結本機、迴路都不算。</summary>
    public static bool IsPublicIPv4(string iIp)
    {
        if (!IPAddress.TryParse(iIp, out IPAddress? a) || a.AddressFamily != AddressFamily.InterNetwork) return false;
        byte[] b = a.GetAddressBytes();
        if (b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] >= 224) return false;
        if (b[0] == 192 && b[1] == 168) return false;
        if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return false;
        if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return false;
        if (b[0] == 169 && b[1] == 254) return false;
        return true;
    }

    /// <summary>從路由器的裝置描述裡找出「對外連線」那個服務與它的控制網址。</summary>
    public static bool TryParseDescription(string iXml, string iLocation, out string oServiceType, out string oControlUrl)
    {
        oServiceType = oControlUrl = "";
        Match m = Regex.Match(iXml ?? "",
            @"<serviceType>\s*(urn:schemas-upnp-org:service:WAN(?:IP|PPP)Connection:\d)\s*</serviceType>.*?<controlURL>\s*([^<\s]+)\s*</controlURL>",
            RegexOptions.Singleline);
        if (!m.Success) return false;
        oServiceType = m.Groups[1].Value;
        try { oControlUrl = new Uri(new Uri(iLocation), m.Groups[2].Value).ToString(); }
        catch (Exception) { return false; }
        return true;
    }

    public static string XmlValue(string iXml, string iTag)
    {
        Match m = Regex.Match(iXml ?? "", "<" + Regex.Escape(iTag) + @"[^>]*>([^<]*)</" + Regex.Escape(iTag) + ">");
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value.Trim()) : "";
    }

    /// <summary>SOAP 錯誤 ⇒ `718 ConflictInMappingEntry` 這種；不是錯誤 ⇒ 空字串。</summary>
    public static string SoapError(string iXml)
    {
        string aCode = XmlValue(iXml, "errorCode");
        return aCode.Length == 0 ? "" : (aCode + " " + XmlValue(iXml, "errorDescription")).Trim();
    }

    public static string LocationOf(string iSsdpReply)
    {
        Match m = Regex.Match(iSsdpReply ?? "", @"(?im)^location:\s*(\S+)");
        return m.Success ? m.Groups[1].Value : "";
    }

    // ── UPnP ─────────────────────────────────────────────────────────

    static readonly HttpClient s_Http = new() { Timeout = TimeSpan.FromSeconds(HttpTimeoutSec) };

    static List<IPAddress> LocalIPv4()
    {
        var l = new List<IPAddress>();
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    if (a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
                        l.Add(a.Address);
            }
        }
        catch (Exception) { }
        return l;
    }

    /// <summary>在區網裡找支援 UPnP 的路由器。找不到 ⇒ null（<paramref name="oError"/> 說原因）。</summary>
    public static PeerGateway? Discover(out string oError, int iWaitMs = 2500)
    {
        oError = "";
        byte[] aMsg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: " + IgdSt + "\r\n\r\n");
        List<IPAddress> aLocals = LocalIPv4();
        if (aLocals.Count == 0) { oError = "這台沒有啟用中的 IPv4 網卡"; return null; }
        string aLast = "區網裡沒有路由器回應 UPnP 探索（路由器沒開 UPnP，或不支援）";
        foreach (IPAddress aLocal in aLocals)
        {
            try
            {
                using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(aLocal, 0));
                s.ReceiveTimeout = iWaitMs;
                var aTo = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);
                s.SendTo(aMsg, aTo);
                s.SendTo(aMsg, aTo);
                byte[] aBuf = new byte[4096];
                DateTime aEnd = DateTime.UtcNow.AddMilliseconds(iWaitMs);
                while (DateTime.UtcNow < aEnd)
                {
                    EndPoint aFrom = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(aBuf, ref aFrom); } catch (SocketException) { break; }
                    string aReply = Encoding.ASCII.GetString(aBuf, 0, n);
                    string aLoc = LocationOf(aReply);
                    if (aLoc.Length == 0) continue;
                    string aXml;
                    try { aXml = s_Http.GetStringAsync(aLoc).GetAwaiter().GetResult(); }
                    catch (Exception e) { aLast = "路由器的裝置描述讀不到（" + e.GetType().Name + "）"; continue; }
                    if (!TryParseDescription(aXml, aLoc, out string aSvc, out string aCtl)) { aLast = "路由器有回應，但描述裡沒有對外連線的服務"; continue; }
                    Match aSrv = Regex.Match(aReply, @"(?im)^server:\s*(.+?)\s*$");
                    return new PeerGateway
                    {
                        ControlUrl = aCtl, ServiceType = aSvc, RouterIp = ((IPEndPoint)aFrom).Address.ToString(),
                        LocalIp = aLocal.ToString(), Server = aSrv.Success ? aSrv.Groups[1].Value : "",
                    };
                }
            }
            catch (Exception e) { aLast = "UPnP 探索出錯（" + e.GetType().Name + "：" + e.Message + "）"; }
        }
        oError = aLast;
        return null;
    }

    /// <summary>打一支 SOAP。回（回應本文、錯誤）；錯誤空 ＝ 成功。</summary>
    static (string Body, string Error) Soap(PeerGateway g, string iAction, params (string Name, string Value)[] iArgs)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>");
        sb.Append("<u:").Append(iAction).Append(" xmlns:u=\"").Append(g.ServiceType).Append("\">");
        foreach ((string n, string v) in iArgs) sb.Append('<').Append(n).Append('>').Append(WebUtility.HtmlEncode(v)).Append("</").Append(n).Append('>');
        sb.Append("</u:").Append(iAction).Append("></s:Body></s:Envelope>");
        try
        {
            using var aReq = new HttpRequestMessage(HttpMethod.Post, g.ControlUrl);
            aReq.Content = new StringContent(sb.ToString(), Encoding.UTF8, "text/xml");
            aReq.Headers.TryAddWithoutValidation("SOAPAction", "\"" + g.ServiceType + "#" + iAction + "\"");
            using HttpResponseMessage aRes = s_Http.Send(aReq);
            string aBody = aRes.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            string aErr = SoapError(aBody);
            if (aErr.Length > 0) return (aBody, aErr);
            return aRes.IsSuccessStatusCode ? (aBody, "") : (aBody, "HTTP " + (int)aRes.StatusCode);
        }
        catch (Exception e) { return ("", e.GetType().Name + "：" + (e.InnerException ?? e).Message); }
    }

    /// <summary>路由器回報的對外 IP。拿不到 ⇒ 空字串。</summary>
    public static string ExternalIp(PeerGateway g, out string oError)
    {
        (string aBody, oError) = Soap(g, "GetExternalIPAddress");
        return oError.Length > 0 ? "" : XmlValue(aBody, "NewExternalIPAddress");
    }

    /// <summary>對外的這個埠（TCP）現在轉給誰。沒有 ⇒ null。</summary>
    public static PeerPortMapping? GetMapping(PeerGateway g, int iExternalPort)
    {
        (string aBody, string aErr) = Soap(g, "GetSpecificPortMappingEntry",
            ("NewRemoteHost", ""), ("NewExternalPort", iExternalPort.ToString()), ("NewProtocol", "TCP"));
        if (aErr.Length > 0) return null;
        return new PeerPortMapping
        {
            Client = XmlValue(aBody, "NewInternalClient"), Description = XmlValue(aBody, "NewPortMappingDescription"),
            InternalPort = int.TryParse(XmlValue(aBody, "NewInternalPort"), out int p) ? p : 0,
        };
    }

    /// <summary>
    /// 請路由器把對外的 <paramref name="iPort"/>（TCP）轉到這台的同一個埠。回錯誤；成功 ⇒ null，<paramref name="oLease"/> 是實際的租期（0＝永久）。
    /// ⛔ 那個埠已經轉給別台 ⇒ 不搶，回報是誰。
    /// </summary>
    public static string? AddMapping(PeerGateway g, int iPort, out int oLease)
    {
        oLease = LeaseSeconds;
        PeerPortMapping? aHave = GetMapping(g, iPort);
        if (aHave != null && aHave.Client.Length > 0 && aHave.Client != g.LocalIp)
            return $"對外的 {iPort} 埠已經轉給 {aHave.Client}（{aHave.Description}）⇒ 沒有動它；換一個埠，或到路由器上處理";
        string Try(int iLease)
        {
            (_, string e) = Soap(g, "AddPortMapping",
                ("NewRemoteHost", ""), ("NewExternalPort", iPort.ToString()), ("NewProtocol", "TCP"),
                ("NewInternalPort", iPort.ToString()), ("NewInternalClient", g.LocalIp), ("NewEnabled", "1"),
                ("NewPortMappingDescription", MappingDescription), ("NewLeaseDuration", iLease.ToString()));
            return e;
        }
        string aErr = Try(LeaseSeconds);
        if (aErr.StartsWith("725", StringComparison.Ordinal)) { oLease = 0; aErr = Try(0); }   // 這台路由器只收永久的
        return aErr.Length == 0 ? null : aErr;
    }

    /// <summary>刪掉那一筆。⛔ 只刪「轉給這台」的；轉給別台的不動。回錯誤；成功或本來就沒有 ⇒ null。</summary>
    public static string? DeleteMapping(PeerGateway g, int iPort)
    {
        PeerPortMapping? aHave = GetMapping(g, iPort);
        if (aHave == null) return null;
        if (aHave.Client != g.LocalIp) return $"對外的 {iPort} 埠現在轉給 {aHave.Client}，不是這台 ⇒ 沒有刪";
        (_, string aErr) = Soap(g, "DeletePortMapping",
            ("NewRemoteHost", ""), ("NewExternalPort", iPort.ToString()), ("NewProtocol", "TCP"));
        return aErr.Length == 0 ? null : aErr;
    }

    // ── STUN（只給 probe 用）─────────────────────────────────────────

    public static readonly (string Host, int Port)[] StunServers =
    {
        ("stun.l.google.com", 19302), ("stun1.l.google.com", 19302), ("stun.cloudflare.com", 3478),
    };

    /// <summary>STUN 回應 ⇒ 對外位址。解不出來 ⇒ null。</summary>
    public static (string Ip, int Port)? ParseStunReply(byte[] iData, int iLength, byte[] iTxn)
    {
        if (iLength < 20 || !iData.AsSpan(8, 12).SequenceEqual(iTxn)) return null;
        int i = 20;
        while (i + 4 <= iLength)
        {
            int t = BinaryPrimitives.ReadUInt16BigEndian(iData.AsSpan(i)), l = BinaryPrimitives.ReadUInt16BigEndian(iData.AsSpan(i + 2));
            if (i + 4 + l > iLength) break;
            ReadOnlySpan<byte> v = iData.AsSpan(i + 4, l);
            if (l >= 8 && v[1] == 1)
            {
                if (t == 0x0020)   // XOR-MAPPED-ADDRESS
                {
                    int p = BinaryPrimitives.ReadUInt16BigEndian(v.Slice(2)) ^ 0x2112;
                    byte[] ip = { (byte)(v[4] ^ 0x21), (byte)(v[5] ^ 0x12), (byte)(v[6] ^ 0xA4), (byte)(v[7] ^ 0x42) };
                    return (new IPAddress(ip).ToString(), p);
                }
                if (t == 0x0001)   // MAPPED-ADDRESS
                    return (new IPAddress(v.Slice(4, 4).ToArray()).ToString(), BinaryPrimitives.ReadUInt16BigEndian(v.Slice(2)));
            }
            i += 4 + l + ((4 - l % 4) % 4);
        }
        return null;
    }

    /// <summary>用同一個本機 UDP 埠問幾台公用 STUN 伺服器：外面看到的是哪個位址、每一台看到的是不是同一個。</summary>
    public static PeerStunResult StunProbe(int iWaitMs = 2000)
    {
        var r = new PeerStunResult();
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, 0));
            s.ReceiveTimeout = iWaitMs;
            r.LocalPort = ((IPEndPoint)s.LocalEndPoint!).Port;
            var aIps = new HashSet<string>();
            foreach ((string aHost, int aPort) in StunServers)
            {
                try
                {
                    IPAddress? aAddr = Dns.GetHostAddresses(aHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                    if (aAddr == null) continue;
                    byte[] aTxn = RandomNumberGenerator.GetBytes(12);
                    byte[] aReq = new byte[20];
                    BinaryPrimitives.WriteUInt16BigEndian(aReq, 0x0001);
                    BinaryPrimitives.WriteUInt32BigEndian(aReq.AsSpan(4), 0x2112A442);
                    aTxn.CopyTo(aReq, 8);
                    s.SendTo(aReq, new IPEndPoint(aAddr, aPort));
                    byte[] aBuf = new byte[1024];
                    EndPoint aFrom = new IPEndPoint(IPAddress.Any, 0);
                    int n = s.ReceiveFrom(aBuf, ref aFrom);
                    (string Ip, int Port)? m = ParseStunReply(aBuf, n, aTxn);
                    if (m == null) continue;
                    r.Replies++;
                    aIps.Add(m.Value.Ip);
                    r.MappedPorts.Add(m.Value.Port);
                    r.PublicIp = m.Value.Ip;
                }
                catch (Exception) { }   // 這一台沒回 ⇒ 問下一台
            }
            if (r.Replies == 0) { r.Error = "沒有任何一台 STUN 伺服器回應（UDP 出不去，或被擋）"; return r; }
            r.EndpointIndependent = r.Replies >= 2 && aIps.Count == 1 && r.MappedPorts.Distinct().Count() == 1;
            r.PortPreserved = r.MappedPorts.All(p => p == r.LocalPort);
        }
        catch (Exception e) { r.Error = e.GetType().Name + "：" + e.Message; }
        return r;
    }
}
