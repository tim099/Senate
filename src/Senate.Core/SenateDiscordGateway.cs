// 區塊職責：**Discord Gateway 連線**（TASK-0316 ④）—— 讓 Bot 在 Discord 上**顯示上線**，狀態欄列出**現在在線的 persona**，
//           並在接了的頻道有新訊息時叫 Inbound 立刻輪那個頻道。
// 物理意義：
//   · 上線綠點是 Gateway 專屬功能：REST 再勤，Bot 在 Discord 上永遠是離線（Unity 版同一條，2026-07-28）。
//   · Tim 2026-09-28：「Inbound 開啟時復刻之前 Unity 端功能 —— Bot 顯示上線，同時資訊欄顯示當前上線的所有 persona」。
//     ⇒ 生命週期跟 Inbound 開關連動：開 ⇒ 連、關 ⇒ **主動斷**（不斷的話 Discord 要等心跳逾時才標離線，綠點會殘留幾十秒）。
//   · 在線名單：信件夾每個 persona 的 lock（`SCP_PersonaLetters.ReadPersonaLock`，⛔ 不另立規則）。
//     讀不到或沒人 ⇒ 顯示橋名 `ChatTavern ⇄ Discord`（⛔ 不顯示「無人在線」—— 讀不到不等於沒人）。
//   · intents 只要 GUILDS｜GUILD_MESSAGES（⛔ 不要 privileged 的 MESSAGE_CONTENT／GUILD_MEMBERS）：
//     這裡的 MESSAGE_CREATE 只當「這個頻道有動靜」的訊號，內容照樣由 REST 讀 ⇒ **收訊只有一條路、游標只有一份**。
//   · 不做 RESUME：斷線就退避重連（1→30 秒）重新 IDENTIFY，空窗期的訊息由 REST 輪詢補回。
// 數值影響：一條 WebSocket ＋ 每 heartbeat_interval（Discord 給，約 41s）一次心跳。
//           presence 每 60 秒檢查一次、**名單變了才送**（Discord 限每分鐘 5 次）。狀態寫 `discord/discord_gateway_status.json` 給後台讀。
//           close 4004（token 錯）／4014（intents 不被允許）⇒ 停 5 分鐘再試並說出原因。⛔ token 不進任何 log。
#nullable enable
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using SCP.Core.Discord;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace Senate.Core;

public static class SenateDiscordGateway
{
    const string GatewayUrl = "wss://gateway.discord.gg/?v=10&encoding=json";
    const int Intents = (1 << 0) | (1 << 9);   // GUILDS | GUILD_MESSAGES（都不是 privileged）
    const string PresenceFallback = "ChatTavern ⇄ Discord";
    public const string StatusFileName = "discord_gateway_status.json";

    static CancellationTokenSource? s_Cts;
    static Task? s_Loop;
    static string s_DataRoot = "";
    static Action<string> s_Out = _ => { };
    static Action<string> s_Err = _ => { };

    /// <summary>有動靜的頻道 id（MESSAGE_CREATE）—— Inbound 下一輪先輪它們。</summary>
    public static readonly ConcurrentQueue<string> Nudges = new();

    public static bool Running => s_Loop is { IsCompleted: false };

    /// <summary>每一圈叫一次：<paramref name="iWant"/>＝Inbound 開著且讀得到 token。狀態沒變就什麼都不做。</summary>
    public static void Sync(bool iWant, string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        if (iWant == Running) return;
        if (iWant)
        {
            s_DataRoot = iDataRoot; s_Out = iOut; s_Err = iErr;
            s_Cts = new CancellationTokenSource();
            CancellationToken aCt = s_Cts.Token;
            s_Loop = Task.Run(() => LoopAsync(aCt));
            iOut("· Discord Gateway：連線中（Bot 即將顯示上線）");
        }
        else Stop("Inbound 關掉了");
    }

    /// <summary>主動斷線（Bot 立刻轉離線）。Server 收工時也會叫。</summary>
    public static void Stop(string iWhy)
    {
        if (s_Cts == null) return;
        try { s_Cts.Cancel(); } catch { }
        try { s_Loop?.Wait(TimeSpan.FromSeconds(5)); } catch { }
        s_Cts = null;
        s_Loop = null;
        WriteStatus(false, "", "已斷線：" + iWhy, "");
        s_Out("· Discord Gateway：已斷線（" + iWhy + "；Bot 轉離線）");
    }

    static async Task LoopAsync(CancellationToken iCt)
    {
        int aBackoff = 1;
        while (!iCt.IsCancellationRequested)
        {
            string aToken = SCP_DiscordBot.ResolveTokenForHost(s_DataRoot);
            if (aToken.Length == 0) { WriteStatus(false, "", "讀不到 Bot token", ""); await Delay(30, iCt); continue; }
            using var ws = new ClientWebSocket();
            int aCloseCode = 0;
            try
            {
                await ws.ConnectAsync(new Uri(GatewayUrl), iCt);
                SCP_JsonData aHello = SCP_JsonParser.Parse(await ReceiveAsync(ws, iCt) ?? "{}");
                int aHbMs = Math.Max(5000, (int)aHello["d"].GetLong("heartbeat_interval", 41250));
                string aPresence = PresenceText();
                await SendAsync(ws, BuildIdentify(aToken, aPresence), iCt);

                long aSeq = 0;
                using var aHbCts = CancellationTokenSource.CreateLinkedTokenSource(iCt);
                Task aHb = HeartbeatAsync(ws, aHbMs, () => aSeq, aHbCts.Token);
                Task aPres = PresenceAsync(ws, aPresence, aHbCts.Token);
                string aBot = "";
                while (!iCt.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    string? aText = await ReceiveAsync(ws, iCt);
                    if (aText == null) break;
                    SCP_JsonData j;
                    try { j = SCP_JsonParser.Parse(aText); } catch { continue; }
                    int aOp = (int)j.GetLong("op", -1);
                    if (j["s"].Type == SCP_JsonType.Number) aSeq = j.GetLong("s", aSeq);
                    if (aOp == 0)
                    {
                        string t = j.GetString("t", "");
                        if (t == "READY")
                        {
                            aBot = j["d"]["user"].GetString("username", "");
                            aBackoff = 1;
                            WriteStatus(true, aBot, "", aPresence);
                            s_Out($"· Discord Gateway：✓ 已上線（Bot {aBot}；狀態欄：{aPresence}）");
                        }
                        else if (t == "MESSAGE_CREATE")
                        {
                            string aCh = j["d"].GetString("channel_id", "");
                            bool aFromBot = j["d"]["author"].GetBool("bot", false) || j["d"].GetString("webhook_id", "").Length > 0;
                            if (aCh.Length > 0 && !aFromBot) Nudges.Enqueue(aCh);
                        }
                    }
                    else if (aOp == 1) await SendAsync(ws, "{\"op\":1,\"d\":" + (aSeq > 0 ? aSeq.ToString() : "null") + "}", iCt);
                    else if (aOp == 7 || aOp == 9) break;   // RECONNECT／INVALID_SESSION ⇒ 重連（重新 IDENTIFY）
                }
                aHbCts.Cancel();
                aCloseCode = (int)(ws.CloseStatus ?? 0);
                try { await Task.WhenAll(aHb, aPres); } catch { }
                if (iCt.IsCancellationRequested)
                {
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                    return;
                }
            }
            catch (OperationCanceledException) when (iCt.IsCancellationRequested)
            {
                try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
                return;
            }
            catch (Exception e) { WriteStatus(false, "", "連線中斷：" + e.GetType().Name + ": " + e.Message, ""); }

            if (aCloseCode == 4004 || aCloseCode == 4014)
            {
                string aWhy = aCloseCode == 4004 ? "close 4004：token 不對 ⇒ 到「Discord Bot」頁重設" : "close 4014：intents 不被允許 ⇒ 檢查 Developer Portal";
                WriteStatus(false, "", aWhy, "");
                s_Err("⚠ Discord Gateway：" + aWhy + "（5 分鐘後再試）");
                await Delay(300, iCt);
                continue;
            }
            WriteStatus(false, "", $"斷線（close {aCloseCode}）⇒ {aBackoff} 秒後重連", "");
            await Delay(aBackoff, iCt);
            aBackoff = Math.Min(30, aBackoff * 2);
        }
    }

    static async Task HeartbeatAsync(ClientWebSocket ws, int iMs, Func<long> iSeq, CancellationToken iCt)
    {
        try
        {
            // 第一拍加抖動（Discord 建議）：避免一堆 client 同一刻心跳
            await Task.Delay((int)(iMs * new Random().NextDouble()), iCt);
            while (!iCt.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                long s = iSeq();
                await SendAsync(ws, "{\"op\":1,\"d\":" + (s > 0 ? s.ToString() : "null") + "}", iCt);
                await Task.Delay(iMs, iCt);
            }
        }
        catch { /* 心跳斷了 ⇒ 收訊迴圈會看到連線關閉 */ }
    }

    static async Task PresenceAsync(ClientWebSocket ws, string iInitial, CancellationToken iCt)
    {
        string aLast = iInitial;
        try
        {
            while (!iCt.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(60_000, iCt);
                string aNow = PresenceText();
                if (aNow == aLast) continue;   // ⛔ 名單沒變就不送（Discord 限每分鐘 5 次）
                await SendAsync(ws, "{\"op\":3,\"d\":" + PresenceObject(aNow, true) + "}", iCt);
                aLast = aNow;
                WriteStatus(true, "", "", aNow);
                s_Out("· Discord Gateway：狀態欄更新 ⇒ " + aNow);
            }
        }
        catch { /* presence 只是門面 —— 失敗不能拖垮連線 */ }
    }

    /// <summary>在線 persona（有 lock 的），排序後用「, 」接；讀不到或沒人 ⇒ 橋名。上限 128 字（Discord activity name）。</summary>
    public static string PresenceText()
    {
        try
        {
            string aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(s_DataRoot)).Value;
            List<string> aNames = SCP_PersonaDisplay.ListPersonas(aLetters)
                .Where(p => p != SCP_PersonaDisplay.SystemPersona && p != SCP_PersonaDisplay.TavernKeeperPersona)
                .Where(p => SCP_PersonaLetters.ReadPersonaLock(aLetters, p) != null)
                .ToList();
            if (aNames.Count == 0) return PresenceFallback;
            string aJoined = string.Join(", ", aNames);
            return aJoined.Length <= 110 ? aJoined : aJoined.Substring(0, 107) + $"…（{aNames.Count} 人）";
        }
        catch { return PresenceFallback; }
    }

    static string PresenceObject(string iText, bool iUpdate)
    {
        var p = SCP_JsonData.NewObject();
        p.Set("status", "online");
        p.Set("afk", false);
        p.Set("since", iUpdate ? SCP_JsonData.NewNull() : (SCP_JsonData)0);
        var a = SCP_JsonData.NewObject();
        a.Set("name", iText);
        a.Set("type", 3);   // Watching —— 「正在觀看 gura, basecamp」（同 Unity 版）
        var arr = SCP_JsonData.NewArray(); arr.Add(a);
        p.Set("activities", arr);
        return SCP_JsonWriter.Write(p);
    }

    static string BuildIdentify(string iToken, string iPresence)
    {
        var d = SCP_JsonData.NewObject();
        d.Set("token", iToken);
        d.Set("intents", Intents);
        var props = SCP_JsonData.NewObject();
        props.Set("os", "windows"); props.Set("browser", "senate"); props.Set("device", "senate-tavern-server");
        d.Set("properties", props);
        d.Set("presence", SCP_JsonParser.Parse(PresenceObject(iPresence, false)));
        var j = SCP_JsonData.NewObject();
        j.Set("op", 2);
        j.Set("d", d);
        return SCP_JsonWriter.Write(j);
    }

    static async Task SendAsync(ClientWebSocket ws, string iJson, CancellationToken iCt)
    {
        byte[] b = Encoding.UTF8.GetBytes(iJson);
        await ws.SendAsync(new ArraySegment<byte>(b), WebSocketMessageType.Text, true, iCt);
    }

    static async Task<string?> ReceiveAsync(ClientWebSocket ws, CancellationToken iCt)
    {
        var buf = new byte[16 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(new ArraySegment<byte>(buf), iCt);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
            if (ms.Length > 8 * 1024 * 1024) throw new InvalidOperationException("gateway frame 太大");
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static async Task Delay(int iSec, CancellationToken iCt)
    {
        try { await Task.Delay(iSec * 1000, iCt); } catch (OperationCanceledException) { }
    }

    static void WriteStatus(bool iConnected, string iBot, string iError, string iPresence)
    {
        try
        {
            string aPath = SCP_DiscordPaths.Dir(s_DataRoot) + "/" + StatusFileName;
            SCP_JsonData j = File.Exists(aPath) ? SCP_JsonParser.Parse(File.ReadAllText(aPath)) : SCP_JsonData.NewObject();
            j.Set("connected", iConnected);
            j.Set("updated_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            if (iBot.Length > 0) j.Set("bot_name", iBot);
            j.Set("last_error", iError);
            if (iPresence.Length > 0) j.Set("presence", iPresence);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
            File.WriteAllText(aPath, j.ToJson(true), new UTF8Encoding(false));
        }
        catch { /* 狀態檔寫不出去不影響連線 */ }
    }
}
