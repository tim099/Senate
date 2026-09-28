// 區塊職責：**Discord Bot 設定頁**（TASK-0319）—— Bot 憑證、Bot 加入的 Server／頻道、Discord 頻道 → 酒館頻道對應、Inbound 白名單。
// 物理意義：概念取自 Unity `UCL_PlurkAdminPage`（Tim 2026-09-28）：在後台直接貼憑證，⛔ 不再手動編檔。
//           讀寫全走 `SCP_DiscordBot`／`SCP_DiscordInboundConfig`（與 `cmd discord-bot` 同一份）⇒ 本頁只畫與收輸入。
//           Unity 端 Inbound 已廢棄（Tim 2026-09-28：之後只維護 Senate 版）；Inbound 本身的 Senate 版是 TASK-0316 ④。
// 數值影響：
//   · 「加密並安裝」：一步寫出 `discord_bot_token.enc` ＋ 本機明文（Tim：不用跑兩遍）。密碼欄遮罩、不落盤、按完清空。
//   · 「測試連線」「重新整理 Server 清單」會連 Discord ⇒ 丟背景執行緒跑（最多幾十秒，⛔ 不卡畫面）；結果下一幀才出現。
//   · 對應：寫 `ChatTavern/discord_channel_routing.json`（其他欄位原樣保留）；白名單：寫 `ChatTavern/discord_inbound_whitelist.json`。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。⛔ 畫面上任何地方都不印 token。
#nullable enable
using SCP.Core.Discord;
using SCP.Core.Gui;
using SCP.Core.Tavern;

namespace Senate.Cli.Pages;

public sealed class DiscordBotPage : SCP_GuiToolPage
{
    public const string PageKey = "discord-bot";
    const string RoomId = "dbot/edit/room";
    const string OnId = "dbot/edit/on";
    const string SrcId = "dbot/edit/src";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    SCP_DiscordTokenStatus m_Status = new();
    SCP_DiscordGuildCache m_Cache = new();
    List<SCP_DiscordRoute> m_Routes = new();
    string? m_RouteError;
    SCP_DiscordWhitelist m_Whitelist = new();
    List<string> m_Rooms = new();
    string? m_Message;
    /// <summary>密碼／token 欄的世代號：清空輸入時換一代（同 SCP_GuiSecretPage）。</summary>
    int m_Gen;

    /// <summary>正在編輯的 Discord 頻道 id（空＝沒選）。</summary>
    string m_SelChannel = "";
    string m_SelGuild = "";
    string m_SelLabel = "";

    // 背景工作（測試連線／重新整理）—— 一次只跑一件
    Task? m_Job;
    string m_JobName = "";
    volatile string? m_JobResult;

    public DiscordBotPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "Discord Bot";
    public override string? MenuGroup => "酒館";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    void Reload()
    {
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) return;
        SCP_TavernChannels.EnsureMainChannel(m_DataRoot, out _);
        m_Status = SCP_DiscordBot.Status(m_DataRoot);
        m_Cache = SCP_DiscordBot.LoadCache(m_DataRoot);
        m_Routes = SCP_DiscordInboundConfig.LoadRoutes(m_DataRoot, out m_RouteError);
        m_Whitelist = SCP_DiscordInboundConfig.LoadWhitelist(m_DataRoot);
        m_Rooms = SCP_TavernChannels.ListChannels(m_DataRoot, false).Select(c => c.Room).ToList();
    }

    bool Busy => m_Job != null && !m_Job.IsCompleted;

    void StartJob(string iName, Func<string> iWork)
    {
        if (Busy) return;
        m_JobName = iName;
        m_JobResult = null;
        m_Job = Task.Run(() =>
        {
            try { m_JobResult = iWork(); }
            catch (Exception e) { m_JobResult = $"[失敗] {iName}：{e.GetType().Name}: {e.Message}"; }
        });
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (m_Job != null && m_Job.IsCompleted)
        {
            m_Message = m_JobResult;
            m_Job = null;
            Reload();
        }
        if (iUi.Button("重新讀取", "dbot/btn/reload")) { Reload(); m_Message = "已重新讀取"; }
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) return;
        if (Busy) { iUi.Label($"｜{m_JobName}中…"); return; }
        if (iUi.Button("測試連線", "dbot/btn/test"))
        {
            string aRoot = m_DataRoot;
            StartJob("測試連線", () => SCP_DiscordBot.TryTestConnection(aRoot, out string n, out string id, out _, out string? e)
                ? $"連上了：Bot {n}（id {id}）" : "[失敗] " + e);
        }
        if (iUi.Button("重新整理 Server 清單", "dbot/btn/refresh"))
        {
            string aRoot = m_DataRoot;
            StartJob("重新整理 Server 清單", () => SCP_DiscordBot.TryRefreshGuilds(aRoot, out SCP_DiscordGuildCache c, out string? e)
                ? $"已重新整理：{c.Guilds.Count} 個 Server、{c.Guilds.Sum(g => g.Channels.Count)} 個文字頻道" : "[失敗] " + e + "（清單沒動）");
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【Discord Bot】");
        g.Note("Unity 端的 Discord Inbound 已廢棄，之後只維護 Senate 這一套。Inbound 本身搬到 Senate 之前（TASK-0316），Discord 的訊息不會進酒館。");
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            g.Note($"[錯誤] 找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定");
            return;
        }
        if (m_Message != null) g.Note(m_Message);

        DrawCredential(g);
        g.Separator();
        DrawEditor(g);
        DrawServers(g);
        g.Separator();
        DrawWhitelist(g);
    }

    // ── 憑證 ─────────────────────────────────────────────────────────
    void DrawCredential(SCP_Ui g)
    {
        using var aFold = g.Fold("Bot 憑證", "dbot/fold/cred", !m_Status.Ready);
        SCP_DiscordTokenStatus s = m_Status;
        g.Note($".enc：{(s.EncExists ? "有" + (s.EncCreatedAt.Length > 0 ? "（" + s.EncCreatedAt + "）" : "") : "沒有")}　｜　"
               + $"本機明文：{(s.PlainExists ? "已安裝" : "沒有")}　｜　環境變數 {SCP_DiscordBot.TokenEnvVar}：{(s.EnvOverride ? "有值（蓋過明文）" : "沒有")}");
        g.Note(s.Ready ? "讀得到 token。按頂列「測試連線」確認它能用。" : "[注意] 讀不到 token ⇒ 在下面貼上 Bot token 並設定密碼。");
        if (s.DirWarning.Length > 0) g.Note("[注意] " + s.DirWarning);
        if (!aFold.Open) return;

        string aToken = g.PasswordField("Bot token（Discord Developer Portal → Bot → Reset Token）", $"dbot/{m_Gen}/token");
        string aPass = g.PasswordField("密碼（加密用；其他機器解密時要輸入它）", $"dbot/{m_Gen}/pass");
        string aPass2 = g.PasswordField("再次確認密碼", $"dbot/{m_Gen}/pass2");
        string aHint = g.TextField("密碼提示（選填；不參與加密）", "", $"dbot/{m_Gen}/hint");
        bool aOverwrite = false;
        if (s.EncExists)
        {
            g.Note("[注意] 已經有 discord_bot_token.enc —— 覆寫之後舊密碼解不開新檔，而舊檔回不來（除非在 git 裡）");
            aOverwrite = g.Toggle("我要覆寫既有 .enc", false, $"dbot/{m_Gen}/overwrite");
        }
        if (!g.Button(s.EncExists ? "加密並安裝（覆寫）" : "加密並安裝", "dbot/btn/set_token")) return;

        if (aPass != aPass2) m_Message = "[未寫入] 兩次密碼不一致";
        else m_Message = SCP_DiscordBot.TrySetToken(m_DataRoot, aToken, aPass, aHint, aOverwrite, out string? aErr)
            ? "已寫入 discord_bot_token.enc 與本機明文（.enc 要自己提交到 Secret repo；明文不會進 git）"
            : "[未寫入] " + aErr;
        ClearInputs(g);
        Reload();
    }

    void ClearInputs(SCP_Ui g)
    {
        foreach (string k in new[] { "token", "pass", "pass2" })
            g.SetField($"dbot/{m_Gen}/{k}{SCP_Ui.MaskedIdSuffix}", "");
        m_Gen++;
    }

    // ── Server 與頻道 ─────────────────────────────────────────────────
    void DrawServers(SCP_Ui g)
    {
        if (m_RouteError != null) g.Note("[注意] 對應表讀不了：" + m_RouteError + " —— 修好之前不會覆寫它");
        if (!m_Cache.Exists)
        {
            g.Note("還沒抓過 Server 清單 ⇒ 按頂列「重新整理 Server 清單」。");
            DrawOrphans(g, new HashSet<string>());
            return;
        }
        g.Label($"Bot {m_Cache.BotName} 加入的 Server：{m_Cache.Guilds.Count}　（清單時間 {m_Cache.FetchedAt}）");
        var aMap = m_Routes.GroupBy(r => r.ChannelId).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        foreach (SCP_DiscordGuild gd in m_Cache.Guilds)
        {
            int aWired = gd.Channels.Count(c => aMap.ContainsKey(c.Id));
            using var aFold = g.Fold($"{gd.Name}　（{gd.Channels.Count} 個文字頻道，已接 {aWired}）", "dbot/fold/guild/" + gd.Id, aWired > 0);
            if (!aFold.Open) continue;
            if (gd.Error.Length > 0) g.Note("[注意] 列不出頻道：" + gd.Error);
            foreach (SCP_DiscordChannel ch in gd.Channels)
            {
                string aTo = aMap.TryGetValue(ch.Id, out SCP_DiscordRoute? r) ? $"→ {r.TavernRoom}{(r.Enabled ? "" : "（關）")}　[{r.SourceClass}]" : "未接";
                using (g.IdScope("ch/" + ch.Id))
                using (g.Row())
                {
                    if (g.Button(ch.Id == m_SelChannel ? "編輯中" : "設定", "dbot/btn/pick")) Select(g, ch.Id, gd.Id, ch.Name);
                    g.Label($"{(ch.ParentName.Length > 0 ? ch.ParentName + " / " : "")}#{ch.Name}　{aTo}");
                }
            }
        }
        DrawOrphans(g, new HashSet<string>(m_Cache.Guilds.SelectMany(x => x.Channels).Select(c => c.Id), StringComparer.Ordinal));
    }

    /// <summary>對應表裡有、但 Bot 看不到（或還沒抓清單）的頻道 —— 照樣可以編輯或移除。</summary>
    void DrawOrphans(SCP_Ui g, HashSet<string> iKnown)
    {
        List<SCP_DiscordRoute> aOrphans = m_Routes.Where(r => !iKnown.Contains(r.ChannelId)).ToList();
        if (aOrphans.Count == 0) return;
        using var aFold = g.Fold(m_Cache.Exists ? $"對應表裡有、Bot 卻看不到的頻道（{aOrphans.Count}）" : $"對應表（{aOrphans.Count}）",
                                 "dbot/fold/orphans", true);
        if (!aFold.Open) return;
        foreach (SCP_DiscordRoute r in aOrphans)
        {
            using (g.IdScope("orphan/" + r.ChannelId))
            using (g.Row())
            {
                if (g.Button(r.ChannelId == m_SelChannel ? "編輯中" : "設定", "dbot/btn/pick")) Select(g, r.ChannelId, r.GuildId, r.Label);
                g.Label($"{r.Label}（{r.ChannelId}）→ {r.TavernRoom}{(r.Enabled ? "" : "（關）")}　[{r.SourceClass}]");
            }
        }
    }

    void Select(SCP_Ui g, string iChannel, string iGuild, string iLabel)
    {
        m_SelChannel = iChannel;
        m_SelGuild = iGuild;
        m_SelLabel = iLabel;
        SCP_DiscordRoute? r = m_Routes.FirstOrDefault(x => x.ChannelId == iChannel);
        // 欄位倉會跨次保存 ⇒ 換頻道時把三個下拉對齊成這一列的現值（沒接過的：主頻道／開／external）
        g.SetField(RoomId + "/value", r?.TavernRoom ?? SCP_TavernChannels.MainChannelId);
        g.SetField(OnId + "/value", r == null || r.Enabled ? "1" : "0");
        g.SetField(SrcId + "/value", r?.SourceClass.Length > 0 ? r.SourceClass : "external");
    }

    void DrawEditor(SCP_Ui g)
    {
        if (m_SelChannel.Length == 0) return;
        SCP_DiscordRoute? r = m_Routes.FirstOrDefault(x => x.ChannelId == m_SelChannel);
        using (g.Box($"設定對應：{(m_SelLabel.Length > 0 ? "#" + m_SelLabel : m_SelChannel)}（{m_SelChannel}）"))
        {
            g.Note(r == null ? "這個頻道還沒接 ⇒ 預設接到主頻道 tavern。" : $"現在：→ {r.TavernRoom}（{(r.Enabled ? "開" : "關")}，{r.SourceClass}，priority {r.Priority}）");
            string aRoom = g.Dropdown("酒館頻道（只列沒封存的）", m_Rooms, r?.TavernRoom ?? SCP_TavernChannels.MainChannelId, RoomId);
            string aOn = g.Dropdown("開關", new List<SCP_GuiOption> { new("1", "開"), new("0", "關") }, r == null || r.Enabled ? "1" : "0", OnId);
            var aSrcs = SCP_DiscordInboundConfig.KnownSourceClasses.Concat(m_Routes.Select(x => x.SourceClass))
                .Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            string aSrc = g.Dropdown("source_class", aSrcs, r?.SourceClass ?? "external", SrcId);
            using (g.Row())
            {
                if (g.Button(r == null ? "接上" : "儲存", "dbot/btn/save_route"))
                {
                    m_Message = SCP_DiscordInboundConfig.TryUpsertRoute(m_DataRoot, m_SelChannel, m_SelGuild, m_SelLabel, aRoom, aOn == "1", aSrc, out string? aErr)
                        ? $"#{m_SelLabel} → {aRoom}（{(aOn == "1" ? "開" : "關")}，{aSrc}）" : "[未寫入] " + aErr;
                    Reload();
                }
                if (r != null && g.Button("移除對應", "dbot/btn/remove_route"))
                {
                    m_Message = SCP_DiscordInboundConfig.TryRemoveRoute(m_DataRoot, m_SelChannel, out string? aErr)
                        ? $"已移除 #{m_SelLabel} 的對應" : "[未寫入] " + aErr;
                    Reload();
                }
                if (g.Button("取消", "dbot/btn/cancel")) m_SelChannel = "";
            }
        }
    }

    // ── 白名單 ───────────────────────────────────────────────────────
    void DrawWhitelist(SCP_Ui g)
    {
        SCP_DiscordWhitelist w = m_Whitelist;
        using var aFold = g.Fold($"Inbound 白名單（{(w.Enabled ? "啟用" : "停用")}，{w.Users.Count} 人）", "dbot/fold/wl", false);
        if (!aFold.Open) return;
        g.Note("啟用時只收下面這些 Discord 帳號的訊息。來源：" + SCP.Core.Cmd.SCP_Cmd_DiscordBot.WhitelistSource(w));
        if (w.Error.Length > 0) { g.Note("[注意] 讀不了：" + w.Error); return; }
        if (g.Button(w.Enabled ? "停用白名單" : "啟用白名單", "dbot/btn/wl_toggle"))
        {
            m_Message = SCP_DiscordInboundConfig.TrySetWhitelistEnabled(m_DataRoot, !w.Enabled, out string? aErr)
                ? $"白名單已{(!w.Enabled ? "啟用" : "停用")}" : "[未寫入] " + aErr;
            Reload();
        }
        foreach (SCP_DiscordWhitelistUser u in w.Users)
        {
            using (g.IdScope("wl/" + u.UserId))
            using (g.Row())
            {
                g.Label($"{u.DisplayName}（{u.UserId}）{(u.Profile.Length > 0 ? "　" + u.Profile : "")}");
                if (g.Button("移除", "dbot/btn/wl_remove"))
                {
                    m_Message = SCP_DiscordInboundConfig.TryRemoveWhitelistUser(m_DataRoot, u.UserId, out string? aErr)
                        ? $"已從白名單移除 {u.DisplayName}" : "[未寫入] " + aErr;
                    Reload();
                }
            }
        }
        string aId = g.TextField("新增：Discord 使用者 id", g.FieldValue("dbot/wl/new_id", ""), "dbot/wl/new_id");
        string aName = g.TextField("顯示名", g.FieldValue("dbot/wl/new_name", ""), "dbot/wl/new_name");
        string aProfile = g.TextField("身分說明（選填）", g.FieldValue("dbot/wl/new_profile", ""), "dbot/wl/new_profile");
        if (g.Button("加入白名單", "dbot/btn/wl_add"))
        {
            if (SCP_DiscordInboundConfig.TryUpsertWhitelistUser(m_DataRoot, aId, aName, aProfile, out string? aErr))
            {
                m_Message = $"已加入白名單：{aName}（{aId.Trim()}）";
                foreach (string k in new[] { "dbot/wl/new_id", "dbot/wl/new_name", "dbot/wl/new_profile" }) g.SetField(k, "");
            }
            else m_Message = "[未寫入] " + aErr;
            Reload();
        }
    }
}
