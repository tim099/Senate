// 區塊職責：**跨主機連線的磁碟那一層**（TASK-0011）—— 設定、本機身分、連線狀態（主機公告／節點／游標）、另一邊的在線名單。
// 物理意義：
//   · 全部住 `<機密資料夾>/peer/`（預設 `Secret/peer/`）：資料根會用 git 同步到別台，而這些檔**每台都不一樣**
//     （誰當主機、私鑰、對方的位址）⇒ 放在 git 一律忽略的那個資料夾，⛔ 不進版控。
//   · `peer_config.json`：模式（off／host／client）、節點名、埠、要串的頻道、通道開關。每一輪重讀 ⇒ 改完不必重啟 Server。
//   · `peer_identity.json`：這台的簽章金鑰。第一次用到時產生。
//   · `peer_state.json`：主機這一場的 token、收到的主機公告、認識的節點（每個節點在每個頻道收到第幾號）。
//   · `peer_presence.json`：另一邊在線的人（酒館追讀的「在線」會讀它；讀法在 SCP_Core 的 `SCP_PeerPresence`）。
// 數值影響：每次存檔都是「寫暫存檔 → 換檔」；狀態檔的讀改寫在同一把鎖裡（同一顆 Server 內）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Secret;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed class PeerConfig
{
    public string Mode = "off";            // off｜host｜client
    public string Name = "";               // 節點名；空 ⇒ 電腦名稱
    public int Port = 47800;
    public string Bind = "0.0.0.0";
    public string Tunnel = "auto";         // auto｜off
    public string PortMap = "auto";        // auto（主機啟動時請路由器開對外的埠，UPnP）｜off
    public string CloudflaredPath = "";    // 空 ⇒ 從 PATH 找
    public string PublicUrl = "";          // 自己開了通訊埠轉發時填（例：ws://203.0.113.5:47800/peer）
    public string AnnounceWebhookId = "";  // 空 ⇒ 主頻道的分類綁的 webhook
    public bool ForwardExternal;           // Discord 轉進來的訊息要不要也送到另一邊（預設不送：另一邊自己會從 Discord 收）
    public List<string> Rooms = new() { SCP_TavernChannels.MainChannelId };
    public string Error = "";

    public string NodeName => SenatePeerStore.CleanName(Name.Length > 0 ? Name : Environment.MachineName);
}

/// <summary>主機貼到 Discord 的公告（另一端靠它連線）。</summary>
public sealed class PeerAnnounce
{
    public string Id = "", Name = "", Sign = "", Token = "", Ts = "";
    public List<string> Addrs = new();
    public List<string> Webhooks = new();   // 主機自己的 Discord webhook id —— 另一端據此不再從 Discord 重收主機的訊息
    public bool Closed;

    public SCP_JsonData ToJson()
    {
        var j = SCP_JsonData.NewObject();
        j.Set("v", 1); j.Set("id", Id); j.Set("name", Name); j.Set("sign", Sign); j.Set("token", Token); j.Set("ts", Ts);
        j.Set("addrs", SenatePeerStore.Arr(Addrs)); j.Set("webhooks", SenatePeerStore.Arr(Webhooks));
        if (Closed) j.Set("closed", true);
        return j;
    }

    public static PeerAnnounce? FromJson(SCP_JsonData j)
    {
        if (!j.IsObject || j.GetInt("v", 0) != 1) return null;
        var a = new PeerAnnounce
        {
            Id = j.GetString("id", ""), Name = SenatePeerStore.CleanName(j.GetString("name", "")), Sign = j.GetString("sign", ""),
            Token = j.GetString("token", ""), Ts = j.GetString("ts", ""), Closed = j.GetBool("closed", false),
            Addrs = SenatePeerStore.Strs(j["addrs"]), Webhooks = SenatePeerStore.Strs(j["webhooks"]),
        };
        // ⛔ id 必須真的是那把公鑰的指紋：不然別人可以拿自己的公鑰冒用主機的 id
        if (a.Id.Length == 0 || a.Sign.Length == 0 || a.Token.Length == 0) return null;
        try { if (SenatePeerCrypto.IdOf(a.Sign) != a.Id) return null; } catch (Exception) { return null; }
        return a;
    }

    public const string Marker = "SENATE-HOST1";

    /// <summary>公告代碼（貼在 Discord 訊息裡的那一段；也可以手動交給 `op=join`）。</summary>
    public string ToCode() => Marker + " " + SenatePeerCrypto.B64(Encoding.UTF8.GetBytes(ToJson().ToJson(false)));

    static readonly System.Text.RegularExpressions.Regex s_Code = new(Marker + @"\s+([A-Za-z0-9_\-]{20,})");

    /// <summary>從一段文字裡找公告代碼。找不到／解不開 ⇒ null。</summary>
    public static PeerAnnounce? FromText(string iText)
    {
        var m = s_Code.Match(iText ?? "");
        if (!m.Success) return null;
        try { return FromJson(SCP_JsonParser.Parse(Encoding.UTF8.GetString(SenatePeerCrypto.UnB64(m.Groups[1].Value)))); }
        catch (Exception) { return null; }
    }
}

public sealed class PeerIdentity
{
    public string Id = "", SignPriv = "", SignPub = "";
}

public sealed class PeerOnline
{
    public string Name = "", Agent = "", Status = "";
}

public sealed class PeerNodePresence
{
    public string Id = "", Name = "";
    public List<PeerOnline> Online = new();
}

public static class SenatePeerStore
{
    public const string DirName = "peer";
    public const string ConfigFileName = "peer_config.json";
    public const string IdentityFileName = "peer_identity.json";
    public const string StateFileName = "peer_state.json";

    static readonly object s_Lock = new();

    public static string Dir(string iDataRoot) => SCP_SecretStore.ResolveDir(iDataRoot, out _) + "/" + DirName;
    public static string ConfigPath(string iDataRoot) => Dir(iDataRoot) + "/" + ConfigFileName;
    public static string IdentityPath(string iDataRoot) => Dir(iDataRoot) + "/" + IdentityFileName;
    public static string StatePath(string iDataRoot) => Dir(iDataRoot) + "/" + StateFileName;
    public static string PresencePath(string iDataRoot) => SCP_PeerPresence.PathOf(iDataRoot);

    public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>節點名只留英數與 `-_.`（它會進 sender_id 與檔案內容）；最多 24 字；空 ⇒ `node`。</summary>
    public static string CleanName(string iName)
    {
        var sb = new StringBuilder();
        foreach (char c in iName ?? "")
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
        string s = sb.ToString();
        if (s.Length > 24) s = s.Substring(0, 24);
        return s.Length == 0 ? "node" : s;
    }

    public static SCP_JsonData Arr(IEnumerable<string> iItems)
    {
        var a = SCP_JsonData.NewArray();
        foreach (string s in iItems) a.Add(s);
        return a;
    }

    public static List<string> Strs(SCP_JsonData j)
    {
        var l = new List<string>();
        if (j.IsArray) foreach (SCP_JsonData x in j) if (x.IsString && x.AsString().Length > 0) l.Add(x.AsString());
        return l;
    }

    static SCP_JsonData? ReadJson(string iPath, out string? oError)
    {
        oError = null;
        try
        {
            if (!File.Exists(iPath)) return null;
            return SCP_JsonParser.Parse(File.ReadAllText(iPath, Encoding.UTF8));
        }
        catch (Exception e) { oError = e.GetType().Name + "：" + e.Message; return null; }
    }

    static void WriteJson(string iPath, SCP_JsonData iJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iPath) ?? ".");
        File.WriteAllText(iPath + ".tmp", iJson.ToJson(true) + "\n", new UTF8Encoding(false));
        SCP.Core.Io.SCP_TextFile.ReplaceOrMove(iPath + ".tmp", iPath);
    }

    // ── 設定 ─────────────────────────────────────────────────────────

    /// <summary>檔不在 ⇒ 預設值（mode=off）。檔在但讀不了 ⇒ mode=off 並帶 <see cref="PeerConfig.Error"/>（⛔ 不猜）。</summary>
    public static PeerConfig LoadConfig(string iDataRoot)
    {
        var c = new PeerConfig();
        SCP_JsonData? j = ReadJson(ConfigPath(iDataRoot), out string? aErr);
        if (aErr != null) { c.Error = "設定檔讀不了（" + ConfigPath(iDataRoot) + "）：" + aErr; return c; }
        if (j == null || !j.IsObject) return c;
        string aMode = j.GetString("mode", "off");
        c.Mode = aMode == "host" || aMode == "client" ? aMode : "off";
        c.Name = j.GetString("name", "");
        c.Port = j.GetInt("port", c.Port);
        c.Bind = j.GetString("bind", c.Bind);
        c.Tunnel = j.GetString("tunnel", "auto") == "off" ? "off" : "auto";
        c.PortMap = j.GetString("port_map", "auto") == "off" ? "off" : "auto";
        c.CloudflaredPath = j.GetString("cloudflared_path", "");
        c.PublicUrl = j.GetString("public_url", "");
        c.AnnounceWebhookId = j.GetString("announce_webhook_id", "");
        c.ForwardExternal = j.GetBool("forward_external", false);
        List<string> aRooms = Strs(j["rooms"]);
        if (aRooms.Count > 0) c.Rooms = aRooms;
        if (c.Port < 1 || c.Port > 65535) { c.Error = "port 不在 1～65535：" + c.Port; c.Mode = "off"; }
        return c;
    }

    public static void SaveConfig(string iDataRoot, PeerConfig c)
    {
        var j = SCP_JsonData.NewObject();
        j.Set("mode", c.Mode); j.Set("name", c.Name); j.Set("port", c.Port); j.Set("bind", c.Bind); j.Set("tunnel", c.Tunnel); j.Set("port_map", c.PortMap);
        j.Set("cloudflared_path", c.CloudflaredPath); j.Set("public_url", c.PublicUrl); j.Set("announce_webhook_id", c.AnnounceWebhookId);
        j.Set("forward_external", c.ForwardExternal); j.Set("rooms", Arr(c.Rooms));
        lock (s_Lock) WriteJson(ConfigPath(iDataRoot), j);
    }

    // ── 身分 ─────────────────────────────────────────────────────────

    /// <summary>讀這台的身分；沒有就產生一把並存起來。檔在但讀不了 ⇒ 丟例外（⛔ 不默默換一把：那會讓對方認不得這台）。</summary>
    public static PeerIdentity LoadOrCreateIdentity(string iDataRoot)
    {
        lock (s_Lock)
        {
            SCP_JsonData? j = ReadJson(IdentityPath(iDataRoot), out string? aErr);
            if (aErr != null) throw new InvalidOperationException("身分檔讀不了（" + IdentityPath(iDataRoot) + "）：" + aErr);
            if (j != null && j.IsObject && j.GetString("sign_priv", "").Length > 0)
            {
                string aPub = j.GetString("sign_pub", "");
                return new PeerIdentity { SignPriv = j.GetString("sign_priv", ""), SignPub = aPub, Id = SenatePeerCrypto.IdOf(aPub) };
            }
            (string aPriv, string aNewPub) = SenatePeerCrypto.NewKeyPair();
            var n = SCP_JsonData.NewObject();
            n.Set("id", SenatePeerCrypto.IdOf(aNewPub)); n.Set("sign_pub", aNewPub); n.Set("sign_priv", aPriv); n.Set("created_at", NowIso());
            WriteJson(IdentityPath(iDataRoot), n);
            return new PeerIdentity { SignPriv = aPriv, SignPub = aNewPub, Id = SenatePeerCrypto.IdOf(aNewPub) };
        }
    }

    // ── 狀態（主機這一場、收到的公告、節點與游標）─────────────────────

    static SCP_JsonData LoadStateNoLock(string iDataRoot)
    {
        SCP_JsonData? j = ReadJson(StatePath(iDataRoot), out string? aErr);
        if (aErr != null) throw new InvalidOperationException("狀態檔讀不了（" + StatePath(iDataRoot) + "）：" + aErr);
        if (j == null || !j.IsObject) j = SCP_JsonData.NewObject();
        if (!j["nodes"].IsObject) j.Set("nodes", SCP_JsonData.NewObject());
        return j;
    }

    public static SCP_JsonData ReadState(string iDataRoot) { lock (s_Lock) return LoadStateNoLock(iDataRoot); }

    static void Mutate(string iDataRoot, Action<SCP_JsonData> iChange)
    {
        lock (s_Lock)
        {
            SCP_JsonData s = LoadStateNoLock(iDataRoot);
            iChange(s);
            WriteJson(StatePath(iDataRoot), s);
        }
    }

    /// <summary>主機這一場的 token；沒有就開一場新的。<paramref name="iRenew"/>=true ⇒ 一定換新的（舊的公告作廢）。</summary>
    public static string HostToken(string iDataRoot, bool iRenew = false)
    {
        string aToken = "";
        Mutate(iDataRoot, s =>
        {
            SCP_JsonData h = s["host"].IsObject ? s["host"] : SCP_JsonData.NewObject();
            aToken = h.GetString("token", "");
            if (aToken.Length == 0 || iRenew)
            {
                aToken = SenatePeerCrypto.RandomToken(24);
                h.Set("token", aToken); h.Set("started_at", NowIso());
                h.Remove("announced"); h.Remove("code"); h.Remove("code_key");
            }
            s.Set("host", h);
        });
        return aToken;
    }

    /// <summary>主機這一場的其他欄位（`code`＝目前的公告代碼、`code_key`＝它對應的位址串、`announced`＝已經貼到 Discord 的位址串）。</summary>
    public static string HostGet(string iDataRoot, string iKey) => ReadState(iDataRoot)["host"].GetString(iKey, "");

    public static void HostSet(string iDataRoot, string iKey, string iValue)
        => Mutate(iDataRoot, s =>
        {
            SCP_JsonData h = s["host"].IsObject ? s["host"] : SCP_JsonData.NewObject();
            h.Set(iKey, iValue);
            s.Set("host", h);
        });

    public static PeerAnnounce? LoadAnnounce(string iDataRoot)
    {
        SCP_JsonData a = ReadState(iDataRoot)["announce"];
        return a.IsObject ? PeerAnnounce.FromJson(a) : null;
    }

    /// <summary>
    /// 收下一則主機公告。比手上的舊（ts 比較早或一樣）⇒ 不收，回 false。
    /// ⚠ 換了一台主機（id 不同）照收：以最新的公告為準，一次只連一台主機。
    /// </summary>
    public static bool AcceptAnnounce(string iDataRoot, PeerAnnounce iNew)
    {
        bool aTaken = false;
        Mutate(iDataRoot, s =>
        {
            string aOld = s["announce"].IsObject ? s["announce"].GetString("ts", "") : "";
            if (aOld.Length > 0 && string.CompareOrdinal(iNew.Ts, aOld) <= 0) return;
            s.Set("announce", iNew.ToJson());
            aTaken = true;
        });
        return aTaken;
    }

    public static void ClearAnnounce(string iDataRoot) => Mutate(iDataRoot, s => s.Remove("announce"));

    /// <summary>記住一個節點（名字、公鑰、它的 Discord webhook id）。</summary>
    public static void NoteNode(string iDataRoot, string iId, string iName, string iSign, IEnumerable<string>? iWebhooks)
        => Mutate(iDataRoot, s =>
        {
            SCP_JsonData n = s["nodes"][iId].IsObject ? s["nodes"][iId] : SCP_JsonData.NewObject();
            n.Set("name", iName);
            if (iSign.Length > 0) n.Set("sign", iSign);
            if (iWebhooks != null) n.Set("webhooks", Arr(iWebhooks));
            n.Set("last_seen", NowIso());
            if (!n["cursors"].IsObject) n.Set("cursors", SCP_JsonData.NewObject());
            s["nodes"].Set(iId, n);
        });

    /// <summary>這個節點的這個頻道，我收到第幾號了；沒收過 ⇒ -1。</summary>
    public static int GetCursor(string iDataRoot, string iNodeId, string iRoom)
    {
        SCP_JsonData c = ReadState(iDataRoot)["nodes"][iNodeId]["cursors"];
        return c.IsObject ? c.GetInt(iRoom, -1) : -1;
    }

    /// <summary>游標只會往前；<paramref name="iForce"/>=true（對方重新定起點）才照給的值設。</summary>
    public static void SetCursor(string iDataRoot, string iNodeId, string iRoom, int iSeq, bool iForce = false)
        => Mutate(iDataRoot, s =>
        {
            SCP_JsonData n = s["nodes"][iNodeId].IsObject ? s["nodes"][iNodeId] : SCP_JsonData.NewObject();
            SCP_JsonData c = n["cursors"].IsObject ? n["cursors"] : SCP_JsonData.NewObject();
            if (iForce || iSeq > c.GetInt(iRoom, -1)) c.Set(iRoom, iSeq);
            n.Set("cursors", c);
            s["nodes"].Set(iNodeId, n);
        });

    /// <summary>所有認識的節點的 Discord webhook id（主機用：不再從 Discord 重收它們的訊息）。</summary>
    public static HashSet<string> KnownNodeWebhooks(string iDataRoot)
    {
        var aSet = new HashSet<string>(StringComparer.Ordinal);
        SCP_JsonData aNodes = ReadState(iDataRoot)["nodes"];
        foreach (string k in aNodes.Keys) foreach (string w in Strs(aNodes[k]["webhooks"])) aSet.Add(w);
        return aSet;
    }

    // ── 另一邊的在線名單 ─────────────────────────────────────────────

    public static void WritePresence(string iDataRoot, string iRole, string iLink, string iVia, IEnumerable<PeerNodePresence> iNodes)
    {
        var j = SCP_JsonData.NewObject();
        j.Set("updated_at", NowIso()); j.Set("role", iRole); j.Set("link", iLink); j.Set("via", iVia);
        var aNodes = SCP_JsonData.NewArray();
        foreach (PeerNodePresence n in iNodes)
        {
            var o = SCP_JsonData.NewObject();
            o.Set("id", n.Id); o.Set("name", n.Name);
            var aOn = SCP_JsonData.NewArray();
            foreach (PeerOnline p in n.Online)
            {
                var x = SCP_JsonData.NewObject();
                x.Set("name", p.Name); x.Set("agent", p.Agent); x.Set("status", p.Status);
                aOn.Add(x);
            }
            o.Set("online", aOn);
            aNodes.Add(o);
        }
        j.Set("nodes", aNodes);
        lock (s_Lock) WriteJson(PresencePath(iDataRoot), j);
    }
}
