// 區塊職責：**Discord Outbound 的常駐工作**（TASK-0316 ③）—— 掛在酒館那顆 Server 上，把新訊息送到各頻道分類綁的 webhook。
// 物理意義：
//   · 開關：`discord_config.json` 的 `outbound.enabled`，每一圈讀（Tim 2026-09-28：簡易開關）。
//   · 每 `IntervalSec` 秒一輪：所有「沒封存、有分類、分類綁了 webhook」的頻道各跑一次 `SCP_DiscordOutbound.SendNew`
//     —— 游標、第一次接上不回放、爆量保護、⛔ 不 @ 人、⛔ 不回送 Discord 轉進來的，都在那一層。
// 數值影響：網路在背景執行緒 ⇒ ⛔ 不擋心跳；POST 之間的節流與 429 退避在 SCP 層。同一個錯誤只印一次。
#nullable enable
using SCP.Core.Discord;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenateDiscordOutboundJob
{
    public const int IntervalSec = 2;
    public const int MaxBacklog = 50;

    static Task? s_Running;
    static DateTime s_NextUtc = DateTime.MinValue;
    static bool s_WasEnabled;
    static string s_LastWarn = "";

    public static bool IsRunning => s_Running is { IsCompleted: false };

    public static void Tick(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        DateTime aNow = DateTime.UtcNow;
        if (aNow < s_NextUtc || IsRunning) return;
        s_NextUtc = aNow.AddSeconds(IntervalSec);

        SCP_DiscordConfig aCfg = SCP_DiscordConfigStore.Load(iDataRoot);
        if (!aCfg.OutboundEnabled)
        {
            if (s_WasEnabled) iOut("· Discord Outbound：已關（discord_config.json outbound.enabled=false）");
            s_WasEnabled = false;
            return;
        }
        if (!s_WasEnabled) iOut("· Discord Outbound：開始（每 " + IntervalSec + " 秒掃一次有分類的頻道）");
        s_WasEnabled = true;

        s_Running = Task.Run(() =>
        {
            try
            {
                string aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(iDataRoot)).Value;
                foreach (SCP_ChannelInfo ch in SCP_TavernChannels.ListChannels(iDataRoot, false))
                {
                    if (ch.Settings.Category.Length == 0) continue;
                    SCP_DiscordBackfillReport r = SCP_DiscordOutbound.SendNew(iDataRoot, aLetters, ch.Room, MaxBacklog,
                        s => iOut("· 📤 " + s));
                    foreach (string p in r.Problems) WarnOnce(iErr, "Discord Outbound：" + p);
                }
            }
            catch (Exception e) { WarnOnce(iErr, $"Discord Outbound 這一輪炸了（{e.GetType().Name}：{e.Message}）—— 下一輪再試"); }
        });
    }

    static void WarnOnce(Action<string> iErr, string iMsg)
    {
        if (iMsg == s_LastWarn) return;
        s_LastWarn = iMsg;
        iErr("⚠ " + iMsg);
    }
}
