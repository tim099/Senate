// 區塊職責：**跨主機連線的常駐工作**（TASK-0011）—— 掛在酒館那顆 Server 的服務迴圈上，跟 Discord 收發同一顆。
// 物理意義：
//   · 連線本體是 `SenatePeerLink`（開埠／撥號／訊息／在線名單）；本檔補上跟外面有關的三件事：
//     ① **主機公告**：主機把「現在可以連我的位址＋這一場的 token＋公鑰」貼到 Discord（主頻道的分類綁的 webhook）。
//        位址變了（例如通道網址換了）就重貼；主機關掉時貼一則「已關閉」。
//     ② **Cloudflare 臨時通道**：主機模式而且 `tunnel=auto` ⇒ 找得到 cloudflared 就開通道，把通道網址一起公告。
//        找不到 ⇒ **說出來**（只公告區網位址），⛔ 不靜默、不代為安裝。
//     ③ **收公告**：Discord Inbound 輪詢時遇到帶公告標記的訊息，原文交到 <see cref="OnDiscordSignals"/>
//        （⛔ 那種訊息不寫進酒館：裡面有 token 與位址，而酒館的訊息檔會進版控）。
//   · 已經接上的節點的 Discord webhook id 交給 `SCP_DiscordInbound.LinkedWebhookIds` ⇒ 它們的訊息不再從 Discord 重收一次。
//   · 開關：`peer_config.json` 的 `mode`，每一輪讀 ⇒ 後台頁或 `senate cmd peer` 改完不必重啟 Server。
// 數值影響：全部在背景執行緒 ⇒ ⛔ 不擋心跳。同一個錯誤只印一次。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Discord;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenatePeerJob
{
    const int ChoreIntervalSec = 2;
    const int TunnelWaitSec = 20;
    const int AnnounceRetrySec = 30;

    static SenatePeerLink? s_Link;
    static string s_Root = "";
    static readonly SenatePeerTunnel s_Tunnel = new();
    static Task? s_Chore;
    static DateTime s_NextChore = DateTime.MinValue;
    static DateTime s_NextAnnounce = DateTime.MinValue;
    static DateTime s_NextTunnelStart = DateTime.MinValue;
    static bool s_WasHost;

    // 路由器開埠（UPnP）的狀態：只有主機模式會動
    const int MapRenewSec = 1200;
    const int MapRetrySec = 60;
    static PeerGateway? s_Gateway;
    static int s_MappedPort;
    static bool s_MapMine;                 // 這一顆 Server 請路由器開成過（收的時候才去刪）
    static string s_MapAddr = "";
    static DateTime s_MapNext = DateTime.MinValue;
    static string s_LastWarn = "";

    public static void Tick(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        if (s_Link == null || s_Root != iDataRoot)
        {
            s_Link?.Dispose();
            s_Root = iDataRoot;
            s_Link = new SenatePeerLink(iDataRoot, new SenatePeerLink.Hooks
            {
                Out = iOut, Err = iErr,
                // 酒館這顆本來就是唯一寫入端；@ 通知、封存閘都在 tavern-write 那條路上
                WriteTavern = (aRoom, aMsg) =>
                {
                    SCP_CmdResult w = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>
                    {
                        ["data_root"] = iDataRoot, ["room"] = aRoom, ["msg_json"] = SCP_JsonWriter.Write(aMsg),
                    });
                    string aSeq = w.Values.FirstOrDefault(kv => kv.Key == "seq").Value ?? "";
                    return (w.Ok, int.TryParse(aSeq, out int n) ? n : 0, w.Ok ? "" : $"exit {w.ExitCode}：{string.Join(" ", w.Lines.Take(2))}");
                },
            });
        }
        s_Link.Tick();

        DateTime aNow = DateTime.UtcNow;
        if (aNow < s_NextChore || s_Chore is { IsCompleted: false }) return;
        s_NextChore = aNow.AddSeconds(ChoreIntervalSec);
        SenatePeerLink aLink = s_Link;
        s_Chore = Task.Run(() =>
        {
            try { Chores(iDataRoot, aLink, iOut, iErr); }
            catch (Exception e) { WarnOnce(iErr, "跨主機連線：公告／通道這一輪炸了（" + e.GetType().Name + "：" + e.Message + "）—— 下一輪再試"); }
        });
    }

    /// <summary>Server 收工：線收掉、通道關掉。⛔ 不貼「已關閉」公告 —— Server 重啟很常見，另一端自己會重連。</summary>
    public static void Stop()
    {
        try { s_Link?.Dispose(); } catch { }
        s_Link = null;
        try { s_Tunnel.Stop(); } catch { }
        try { DropPortMap(null); } catch { }
        SCP_DiscordInbound.LinkedWebhookIds = new HashSet<string>(StringComparer.Ordinal);
    }

    static void WarnOnce(Action<string> iErr, string iMsg)
    {
        if (iMsg == s_LastWarn) return;
        s_LastWarn = iMsg;
        iErr("⚠ " + iMsg);
    }

    static void SetExtra(SenatePeerLink iLink, string iKey, string iValue)
    {
        lock (iLink.Extra) iLink.Extra[iKey] = iValue;
    }

    static void Chores(string iDataRoot, SenatePeerLink iLink, Action<string> iOut, Action<string> iErr)
    {
        PeerConfig c = SenatePeerStore.LoadConfig(iDataRoot);

        // 已經用連線接上的節點 ⇒ 它們的 Discord webhook 訊息不再重收
        var aLinked = new HashSet<string>(StringComparer.Ordinal);
        if (c.Mode == "host") aLinked = SenatePeerStore.KnownNodeWebhooks(iDataRoot);
        else if (c.Mode == "client")
        {
            PeerAnnounce? a = SenatePeerStore.LoadAnnounce(iDataRoot);
            if (a != null && !a.Closed) foreach (string w in a.Webhooks) aLinked.Add(w);
        }
        SCP_DiscordInbound.LinkedWebhookIds = aLinked;

        if (c.Mode != "host")
        {
            if (s_Tunnel.Running) { s_Tunnel.Stop(); iOut("· 跨主機連線：通道已關"); }
            DropPortMap(iOut);
            // 主機時期的欄位清掉：不清的話切成另一端之後 status 還印著舊的公告位址
            SetExtra(iLink, "tunnel", ""); SetExtra(iLink, "addrs", ""); SetExtra(iLink, "announce", ""); SetExtra(iLink, "port_map", "");
            if (s_WasHost)
            {
                s_WasHost = false;
                // 主機關掉 ⇒ 貼一則「已關閉」，另一端就不會一直重試
                if (SenatePeerStore.HostGet(iDataRoot, "announced").Length > 0)
                {
                    string? aErr = Post(iDataRoot, c, BuildAnnounce(iDataRoot, c, new List<string>(), true), true);
                    if (aErr == null) iOut("· 跨主機連線：已在 Discord 公告主機關閉");
                    SenatePeerStore.HostSet(iDataRoot, "announced", "");
                }
            }
            return;
        }
        s_WasHost = true;
        if (!iLink.Listening) return;   // 埠還沒開起來（原因在連線本體那邊已經說了）

        List<string> aAddrs = SenatePeerLink.LanAddresses(c);
        if (c.PublicUrl.Length > 0) aAddrs.Add(c.PublicUrl);

        // 對外的埠（UPnP）：只聽本機位址時開了也沒有用 ⇒ 不開
        bool aLoopback = c.Bind.StartsWith("127.", StringComparison.Ordinal);
        if (c.PortMap == "auto" && !aLoopback)
        {
            string aPub = EnsurePortMap(c, iLink, iOut, iErr);
            if (aPub.Length > 0 && !aAddrs.Contains(aPub)) aAddrs.Add(aPub);
        }
        else
        {
            DropPortMap(iOut);
            SetExtra(iLink, "port_map", c.PortMap == "off" ? "（關）" : "（只聽本機位址 ⇒ 不開）");
        }

        if (c.Tunnel == "auto")
        {
            string aExe = SenatePeerTunnel.Find(c.CloudflaredPath);
            if (aExe.Length == 0)
            {
                WarnOnce(iErr, "跨主機連線：找不到 cloudflared ⇒ **沒有開通道**，只公告區網位址（不在同一個區網的另一端連不進來）。"
                               + "裝好之後不必重啟，下一輪就會開；不想用通道就把 tunnel 設成 off");
                SetExtra(iLink, "tunnel", "（沒裝 cloudflared）");
            }
            else
            {
                if (!s_Tunnel.Running && DateTime.UtcNow >= s_NextTunnelStart)
                {
                    s_NextTunnelStart = DateTime.UtcNow.AddSeconds(30);
                    string? aErr = s_Tunnel.Start(aExe, c.Port);
                    if (aErr != null) WarnOnce(iErr, "跨主機連線：cloudflared 起不來 —— " + aErr);
                    else iOut("· 跨主機連線：正在開 Cloudflare 臨時通道…");
                }
                if (s_Tunnel.Running && s_Tunnel.Url.Length == 0)
                {
                    if ((DateTime.UtcNow - s_Tunnel.StartedUtc).TotalSeconds < TunnelWaitSec) return;   // 等網址出來再公告，免得貼兩次
                    WarnOnce(iErr, $"跨主機連線：cloudflared 起了 {TunnelWaitSec} 秒還沒給網址（最後一行：{s_Tunnel.LastLine}）⇒ 先只公告其他位址");
                }
                if (s_Tunnel.Url.Length > 0) aAddrs.Add(SenatePeerTunnel.ToWs(s_Tunnel.Url));
                SetExtra(iLink, "tunnel", s_Tunnel.Url);
            }
        }
        else
        {
            if (s_Tunnel.Running) s_Tunnel.Stop();
            SetExtra(iLink, "tunnel", "（關）");
        }

        string aKey = string.Join(",", aAddrs);
        if (aKey != SenatePeerStore.HostGet(iDataRoot, "code_key"))
        {
            // 位址變了 ⇒ 公告代碼跟著換（手動交給另一端用的也是這一份：`senate cmd peer --arg op=code`）
            SenatePeerStore.HostSet(iDataRoot, "code", BuildAnnounce(iDataRoot, c, aAddrs, false).ToCode());
            SenatePeerStore.HostSet(iDataRoot, "code_key", aKey);
            s_NextAnnounce = DateTime.MinValue;
        }
        SetExtra(iLink, "addrs", aKey);
        if (aKey == SenatePeerStore.HostGet(iDataRoot, "announced") || DateTime.UtcNow < s_NextAnnounce) return;
        s_NextAnnounce = DateTime.UtcNow.AddSeconds(AnnounceRetrySec);
        PeerAnnounce? aAnn = PeerAnnounce.FromText(SenatePeerStore.HostGet(iDataRoot, "code"));
        if (aAnn == null) return;
        string? aPostErr = Post(iDataRoot, c, aAnn, false);
        if (aPostErr == null)
        {
            SenatePeerStore.HostSet(iDataRoot, "announced", aKey);
            iOut($"· 📡 跨主機連線：已在 Discord 公告主機位址（{aAddrs.Count} 個）");
            SetExtra(iLink, "announce", "已貼到 Discord");
        }
        else
        {
            WarnOnce(iErr, "跨主機連線：主機公告沒有貼出去 —— " + aPostErr
                           + "。另一端可以改用手動：這台跑 `senate cmd peer --arg op=code`，把印出來的代碼交給另一端的 `op=join`");
            SetExtra(iLink, "announce", "沒貼出去：" + aPostErr);
        }
    }

    /// <summary>
    /// 請路由器開對外的埠並續租。回要公告的位址（`ws://公網 IP:埠/peer`）；開不成 ⇒ 空字串並說出原因。
    /// 成功後每 <see cref="MapRenewSec"/> 秒續一次；失敗每 <see cref="MapRetrySec"/> 秒再試。
    /// </summary>
    static string EnsurePortMap(PeerConfig c, SenatePeerLink iLink, Action<string> iOut, Action<string> iErr)
    {
        if (DateTime.UtcNow < s_MapNext && s_MappedPort == c.Port) return s_MapAddr;
        if (s_MappedPort != 0 && s_MappedPort != c.Port) DropPortMap(iOut);   // 埠改了 ⇒ 舊的那一筆收掉

        string Fail(string iWhy)
        {
            s_MapNext = DateTime.UtcNow.AddSeconds(MapRetrySec);
            s_MappedPort = c.Port;   // 記住試過的埠（下面 s_MapAddr 是空的 ⇒ 不會被當成開成了）
            s_MapAddr = "";
            WarnOnce(iErr, "跨主機連線：路由器的對外埠沒有開成 —— " + iWhy + " ⇒ 公告裡沒有公網位址（不在同一個區網的另一端要靠通道或手動設 public_url）");
            SetExtra(iLink, "port_map", "沒開成：" + iWhy);
            return "";
        }

        if (s_Gateway == null)
        {
            s_Gateway = SenatePeerNat.Discover(out string aFindErr);
            if (s_Gateway == null) return Fail(aFindErr);
        }
        string aExt = SenatePeerNat.ExternalIp(s_Gateway, out string aIpErr);
        if (aExt.Length == 0) { s_Gateway = null; return Fail("路由器不回報對外 IP（" + aIpErr + "）"); }   // 下一次重新找路由器
        if (!SenatePeerNat.IsPublicIPv4(aExt))
            return Fail("路由器回報的對外 IP 不是公網位址 ⇒ 路由器外面還有一層 NAT，開了也連不進來");
        string? aErr = SenatePeerNat.AddMapping(s_Gateway, c.Port, out int aLease);
        if (aErr != null) return Fail(aErr);

        bool aFirst = !s_MapMine;
        s_MapMine = true;
        s_MappedPort = c.Port;
        s_MapAddr = "ws://" + aExt + ":" + c.Port + SenatePeerLink.WsPath;
        s_MapNext = DateTime.UtcNow.AddSeconds(MapRenewSec);
        string aLeaseText = aLease == 0 ? "永久（這台路由器只收永久的；關主機時會刪）" : "租期 " + aLease + " 秒，定時續租";
        if (aFirst) iOut($"· 跨主機連線：路由器已開對外的 {c.Port} 埠（TCP）→ {s_Gateway.LocalIp}:{c.Port}；{aLeaseText}");
        SetExtra(iLink, "port_map", $"已開：對外 {c.Port} → {s_Gateway.LocalIp}:{c.Port}（{(aLease == 0 ? "永久" : "租期 " + aLease + " 秒")}）");
        return s_MapAddr;
    }

    /// <summary>
    /// 把請路由器開的那一筆收回來（只刪轉給這台的）。這一顆 Server 沒開成過 ⇒ 什麼都不做。
    /// ⚠ 續租失敗之後 <see cref="s_MapMine"/> 還是 true：那一筆可能還在路由器上，收的時候照樣去刪。
    /// </summary>
    static void DropPortMap(Action<string>? iOut)
    {
        if (s_MapMine && s_Gateway != null && s_MappedPort != 0)
        {
            int aPort = s_MappedPort;
            string? aErr = SenatePeerNat.DeleteMapping(s_Gateway, aPort);
            iOut?.Invoke(aErr == null ? $"· 跨主機連線：路由器對外的 {aPort} 埠已收回" : $"⚠ 跨主機連線：路由器對外的 {aPort} 埠沒有收回 —— {aErr}（到路由器上看一眼）");
        }
        s_MapMine = false; s_MappedPort = 0; s_MapAddr = ""; s_MapNext = DateTime.MinValue;
    }

    static PeerAnnounce BuildAnnounce(string iDataRoot, PeerConfig c, List<string> iAddrs, bool iClosed)
    {
        PeerIdentity aMe = SenatePeerStore.LoadOrCreateIdentity(iDataRoot);
        return new PeerAnnounce
        {
            Id = aMe.Id, Name = c.NodeName, Sign = aMe.SignPub, Token = SenatePeerStore.HostToken(iDataRoot), Ts = SenatePeerStore.NowIso(),
            Addrs = iAddrs, Closed = iClosed,
            Webhooks = SCP_DiscordInbound.TryOwnWebhookIds(iDataRoot, out HashSet<string> aOwn) ? aOwn.ToList() : new List<string>(),
        };
    }

    /// <summary>把公告貼到 Discord。回錯誤；成功（至少一條 webhook 收下）⇒ null。⛔ webhook 網址不出現在錯誤訊息裡。</summary>
    static string? Post(string iDataRoot, PeerConfig c, PeerAnnounce iAnn, bool iClosed)
    {
        SCP_DiscordConfig aCfg = SCP_DiscordConfigStore.Load(iDataRoot);
        if (aCfg.Error.Length > 0) return "Discord 設定讀不了：" + aCfg.Error;
        List<string> aIds;
        if (c.AnnounceWebhookId.Length > 0) aIds = new List<string> { c.AnnounceWebhookId };
        else
        {
            string aCat = SCP_TavernChannels.LoadSettings(iDataRoot, SCP_TavernChannels.MainChannelId).Category;
            aIds = aCat.Length > 0 && aCfg.CategoryWebhooks.TryGetValue(aCat, out List<string>? l) ? l : new List<string>();
        }
        List<SCP_DiscordWebhookInfo> aHooks = aCfg.Webhooks.Where(w => aIds.Contains(w.Id) && w.Enabled).ToList();
        if (aHooks.Count == 0)
            return c.AnnounceWebhookId.Length > 0
                ? "設定指定的 webhook 不在或沒啟用"
                : "主頻道 `" + SCP_TavernChannels.MainChannelId + "` 的分類沒有綁啟用中的 webhook";
        Dictionary<string, string> aUrls = SCP_DiscordConfigStore.LoadWebhookUrls(iDataRoot, out string? aUrlErr);
        if (aUrlErr != null) return aUrlErr;

        string aText = iClosed
            ? $"📴 **Senate 主機已關閉連線** · {iAnn.Name}\n`{iAnn.ToCode()}`"
            : $"📡 **Senate 主機開放連線** · {iAnn.Name}\n-# 另一台 Senate 開著「跨主機連線」的另一端模式就會自動連上。這一則由程式讀取，請不要編輯。\n`{iAnn.ToCode()}`";
        var p = SCP_JsonData.NewObject();
        p.Set("content", aText);
        p.Set("username", SCP_DiscordOutbound.SanitizeUsername("Senate 主機 " + iAnn.Name));
        var am = SCP_JsonData.NewObject();
        am.Set("parse", SCP_JsonData.NewArray());
        p.Set("allowed_mentions", am);
        string aPayload = p.ToJson(false);

        int aOk = 0;
        string aLast = "";
        foreach (SCP_DiscordWebhookInfo w in aHooks)
        {
            if (!aUrls.TryGetValue(w.Id, out string? aUrl)) { aLast = w.Describe() + "：密文裡沒有它的網址"; continue; }
            if (SCP_DiscordOutbound.TryPost(aUrl, aPayload, out string? aErr)) aOk++;
            else aLast = w.Describe() + "：" + aErr;
        }
        return aOk > 0 ? null : aLast;
    }

    /// <summary>
    /// Discord Inbound 輪詢到帶公告標記的訊息 ⇒ 原文交到這裡。收下最新的一則；這台是另一端模式的話，連線本體下一輪就會去連。
    /// </summary>
    public static void OnDiscordSignals(string iDataRoot, IEnumerable<string> iContents, Action<string> iOut, Action<string> iErr)
    {
        foreach (string aText in iContents)
        {
            try
            {
                PeerAnnounce? a = PeerAnnounce.FromText(aText);
                if (a == null) { WarnOnce(iErr, "跨主機連線：Discord 上有一則主機公告解不開（格式不對或被改過）⇒ 略過"); continue; }
                if (a.Id == SenatePeerStore.LoadOrCreateIdentity(iDataRoot).Id) continue;   // 自己的
                if (!SenatePeerStore.AcceptAnnounce(iDataRoot, a)) continue;                  // 比手上的舊
                string aMode = SenatePeerStore.LoadConfig(iDataRoot).Mode;
                iOut(a.Closed
                    ? $"· 📴 跨主機連線：主機 {a.Name} 公告關閉"
                    : $"· 📡 跨主機連線：收到主機 {a.Name} 的公告（{a.Addrs.Count} 個位址）"
                      + (aMode == "client" ? " ⇒ 連過去" : $" —— 這台的模式是 {aMode}，沒有連（要連：後台「跨主機連線」頁按「當另一端」，或 `senate cmd peer --arg op=client`）"));
            }
            catch (Exception e) { WarnOnce(iErr, "跨主機連線：收公告時出錯 —— " + e.GetType().Name + "：" + e.Message); }
        }
    }
}
