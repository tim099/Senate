// 區塊職責：**Discord Inbound 的常駐工作**（TASK-0316 ④）—— 掛在酒館那顆 Server 的服務迴圈上，輪流輪詢每個接了的 Discord 頻道。
// 物理意義：
//   · 開關：`ChatTavern/discord/discord_config.json` 的 `inbound.enabled`（Tim 2026-09-28：簡易開關，不處理啟動）——
//     每一圈讀一次，關掉就不再輪（正在跑的那一輪做完為止）。
//   · 一次只輪一個頻道（round-robin），每 `PollIntervalSec` 秒一個 ⇒ N 個頻道的最壞延遲 ≈ N × 間隔。
//   · 收到的訊息交給 `tavern-write`（in-process Dispatch）—— 酒館這顆本來就是唯一寫入端；@ 通知、詞典、封存閘都在那條路上。
//   · **寫成功才推游標**：中途有一則寫不進去 ⇒ 游標停在它前一則，下一輪重抓（⛔ 不跳過）。
// 數值影響：只在背景執行緒做網路與寫入 ⇒ ⛔ 不擋心跳。同一個錯誤連續出現只印一次（⛔ 不洗 log）。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Discord;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenateDiscordInboundJob
{
    public const int PollIntervalSec = 2;

    static Task? s_Running;
    static DateTime s_NextUtc = DateTime.MinValue;
    static int s_Index;
    static bool s_WasEnabled;
    static string s_LastWarn = "";

    public static bool IsRunning => s_Running is { IsCompleted: false };

    public static void Tick(string iDataRoot, string iRepoRoot, Action<string> iOut, Action<string> iErr)
    {
        DateTime aNow = DateTime.UtcNow;
        if (aNow < s_NextUtc || IsRunning) return;
        s_NextUtc = aNow.AddSeconds(PollIntervalSec);

        SCP_DiscordConfig aCfg = SCP_DiscordConfigStore.Load(iDataRoot);
        if (!aCfg.InboundEnabled)
        {
            if (s_WasEnabled) iOut("· Discord Inbound：已關（discord_config.json inbound.enabled=false）");
            s_WasEnabled = false;
            return;
        }
        if (!s_WasEnabled) iOut("· Discord Inbound：開始輪詢（每 " + PollIntervalSec + " 秒一個頻道）");
        s_WasEnabled = true;

        if (!SCP_DiscordBot.Status(iDataRoot).Ready) { WarnOnce(iErr, "Discord Inbound 開著，但讀不到 Bot token ⇒ 沒有輪詢（到後台「Discord Bot」頁設定）"); return; }
        List<SCP_DiscordRoute> aRoutes = SCP_DiscordInboundConfig.LoadRoutes(iDataRoot, out string? aRouteErr).Where(r => r.Enabled).ToList();
        if (aRouteErr != null) { WarnOnce(iErr, "Discord Inbound：對應表讀不了 —— " + aRouteErr); return; }
        if (aRoutes.Count == 0) { WarnOnce(iErr, "Discord Inbound 開著，但沒有任何啟用的頻道對應"); return; }

        SCP_DiscordRoute aRoute = aRoutes[s_Index++ % aRoutes.Count];
        SCP_DiscordWhitelist aWl = SCP_DiscordInboundConfig.LoadWhitelist(iDataRoot);
        s_Running = Task.Run(() => RunOne(iDataRoot, iRepoRoot, aRoute, aWl, iOut, iErr));
    }

    static void RunOne(string iDataRoot, string iRepoRoot, SCP_DiscordRoute iRoute, SCP_DiscordWhitelist iWl, Action<string> iOut, Action<string> iErr)
    {
        try
        {
            SCP_DiscordPollResult r = SCP_DiscordInbound.PollOnce(iDataRoot, iRepoRoot, iRoute, iWl);
            if (!r.Ok)
            {
                SCP_DiscordInbound.NoteStatus(iDataRoot, iRoute.ChannelId, r.Error);
                WarnOnce(iErr, $"Discord Inbound：{iRoute.Label}（{iRoute.ChannelId}）輪詢失敗 —— {r.Error}");
                return;
            }
            if (r.Baseline)
            {
                SCP_DiscordInbound.NoteStatus(iDataRoot, iRoute.ChannelId, "");
                if (r.NewestId.Length > 0) iOut($"· Discord Inbound：{iRoute.Label} 建立起點（{r.CursorNote.Trim()}；歷史不回放）");
                return;
            }
            if (r.CursorNote.Length > 0) iOut($"· Discord Inbound：{iRoute.Label} {r.CursorNote}");

            int aRelayed = 0;
            string aLastOk = "";
            string aFail = "";
            foreach (SCP_DiscordInboundItem it in r.Items)
            {
                SCP_CmdResult w = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>
                {
                    ["data_root"] = iDataRoot,
                    ["room"] = it.Room,
                    ["msg_json"] = SCP_JsonWriter.Write(it.MsgJson),
                });
                if (!w.Ok) { aFail = $"寫不進 {it.Room}（exit {w.ExitCode}）：{string.Join(" ", w.Lines.Take(2))}"; break; }
                aRelayed++;
                aLastOk = it.DiscordMsgId;
                string aSeq = w.Values.FirstOrDefault(kv => kv.Key == "seq").Value ?? "?";
                iOut($"· 📥 Discord → {it.Room} seq {aSeq}：{it.Preview}");
            }
            // 全部寫成 ⇒ 推到這批最新的一則（含被略過的）；中途失敗 ⇒ 推到最後寫成的那則（那之後的下一輪重抓）
            string aCommit = aFail.Length == 0 ? r.NewestId : aLastOk;
            SCP_DiscordInbound.CommitCursor(iDataRoot, iRoute.ChannelId, aCommit, aRelayed, r.CursorNote);
            SCP_DiscordInbound.NoteStatus(iDataRoot, iRoute.ChannelId, aFail);
            if (aFail.Length > 0) WarnOnce(iErr, $"Discord Inbound：{iRoute.Label} —— {aFail}（游標停在它前一則，下一輪重試）");
            if (r.Skipped.Count > 0 && r.Skipped.Any(s => s.Contains("empty-content")))
                WarnOnce(iErr, $"Discord Inbound：{iRoute.Label} 有真人訊息內容是空的 —— 檢查 Bot 的 MESSAGE CONTENT intent");
        }
        catch (Exception e)
        {
            WarnOnce(iErr, $"Discord Inbound：{iRoute.Label} 這一輪炸了（{e.GetType().Name}：{e.Message}）—— 下一輪再試");
        }
    }

    static void WarnOnce(Action<string> iErr, string iMsg)
    {
        if (iMsg == s_LastWarn) return;
        s_LastWarn = iMsg;
        iErr("⚠ " + iMsg);
    }
}
