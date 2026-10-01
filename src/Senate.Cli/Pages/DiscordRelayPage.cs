// 區塊職責：**Discord 轉發設定頁**（TASK-0320）—— In／Out 開關、頻道分類 → webhook（可多條）、persona 頭像網址。
// 物理意義：概念取自 Unity `UCL_ChatTavernAdminPage` 與 `UCL_ControlPanelPage` 的酒館後台（Tim 2026-09-28）。
//           讀寫全走 `SCP_DiscordConfigStore`；webhook 本身的新增／刪除在「Discord Webhook」頁。
// 數值影響：
//   · 開關寫 `discord_config.json`，由酒館 Server 讀（Tim：簡易開關，不處理啟動）。
//   · 分類的勾選欄 key 帶世代號 ⇒ 每次重新讀取都對齊檔案現值（🩸 欄位倉會跨次保存，不對齊的話按儲存會把舊勾選寫回去）。
//   · 「檢查全部頭像」逐一 GET ⇒ 背景執行緒。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。
#nullable enable
using SCP.Core.Discord;
using SCP.Core.Gui;
using SCP.Core.Letters;
using SCP.Core.Tavern;

namespace Senate.Cli.Pages;

public sealed class DiscordRelayPage : SCP_GuiToolPage
{
    public const string PageKey = "discord-relay";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string m_LettersRoot = "";
    SCP_DiscordConfig m_Cfg = new();
    List<SCP_ChannelCategory> m_Cats = new();
    List<SCP_ChannelInfo> m_Channels = new();
    string? m_Message;
    int m_Gen;
    /// <summary>persona → 上次檢查的結果（200／404…）；背景工作寫、畫面讀。</summary>
    Dictionary<string, string> m_AvatarCheck = new();

    Task? m_Job;
    volatile string? m_JobResult;

    public DiscordRelayPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "Discord 轉發設定";
    public override string? MenuGroup => "酒館";

    public override void OnPush() { base.OnPush(); Reload(); }

    void Reload()
    {
        m_Gen++;
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        m_LettersRoot = m_Model.LettersRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) return;
        SCP_TavernChannels.EnsureMainChannel(m_DataRoot, out _);
        m_Cfg = SCP_DiscordConfigStore.Load(m_DataRoot);
        m_Cats = SCP_TavernChannels.LoadCategories(m_DataRoot);
        m_Channels = SCP_TavernChannels.ListChannels(m_DataRoot, false);
    }

    bool Busy => m_Job != null && !m_Job.IsCompleted;

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        OpenFolderButton(iUi, m_DataRoot.Length > 0 ? SCP_DiscordPaths.Dir(m_DataRoot) : null, "drelay/open-dir");
        if (m_Job != null && m_Job.IsCompleted) { m_Message = m_JobResult; m_Job = null; }
        if (iUi.Button("重新讀取", "drelay/btn/reload")) { Reload(); m_Message = "已重新讀取"; }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【Discord 轉發設定】");
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            g.Note($"[錯誤] 找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定");
            return;
        }
        if (m_Message != null) g.Note(m_Message);
        if (m_Cfg.Error.Length > 0) { g.Note("[錯誤] " + m_Cfg.Error + " —— 修好之前不會覆寫它"); return; }

        DrawSwitches(g);
        g.Separator();
        DrawCategories(g);
        g.Separator();
        DrawAvatars(g);
    }

    void DrawSwitches(SCP_Ui g)
    {
        using (g.Box("收發開關（由酒館 Server 讀）"))
        {
            foreach (bool aInbound in new[] { true, false })
            {
                bool aOn = aInbound ? m_Cfg.InboundEnabled : m_Cfg.OutboundEnabled;
                string aName = aInbound ? "Inbound（Discord → 酒館）" : "Outbound（酒館 → Discord）";
                using (g.Row())
                {
                    g.Label($"{aName}：{(aOn ? "開" : "關")}");
                    if (g.Button(aOn ? "關掉" : "打開", aInbound ? "drelay/btn/sw_in" : "drelay/btn/sw_out"))
                    {
                        m_Message = SCP_DiscordConfigStore.TrySetSwitch(m_DataRoot, aInbound, !aOn, out string? aErr)
                            ? $"{aName} 已{(!aOn ? "打開" : "關掉")}" : "[未寫入] " + aErr;
                        Reload();
                    }
                }
            }
            g.Note("⚠ 收發本體（酒館 Server 裡真正去收、去送的那段）還在做（TASK-0316）—— 在那之前開關只是設定，不會真的收發。");
        }
    }

    void DrawCategories(SCP_Ui g)
    {
        g.Label("頻道分類 → Webhook（一個分類可以同步到多條；未分類的頻道不送）");
        if (m_Cfg.Webhooks.Count == 0) g.Note("還沒有任何 webhook ⇒ 先到「Discord Webhook」頁新增（或從 notify_config 匯入 Main）。");
        foreach (SCP_ChannelCategory cat in m_Cats)
        {
            List<string> aBound = m_Cfg.CategoryWebhooks.TryGetValue(cat.Name, out List<string>? l) ? l : new List<string>();
            List<string> aRooms = m_Channels.Where(c => string.Equals(c.Settings.Category, cat.Name, StringComparison.OrdinalIgnoreCase)).Select(c => c.Room).ToList();
            string aTitle = $"{cat.Name}{(cat.Name == SCP_TavernChannels.MainCategory ? "（保留）" : "")}　⇒ {(aBound.Count == 0 ? "不送" : aBound.Count + " 條 webhook")}　｜　頻道：{(aRooms.Count > 0 ? string.Join("、", aRooms) : "（沒有）")}";
            using var aFold = g.Fold(aTitle, "drelay/fold/cat/" + cat.Name, aBound.Count > 0 || cat.Name == SCP_TavernChannels.MainCategory);
            if (!aFold.Open) continue;
            var aPicked = new List<string>();
            foreach (SCP_DiscordWebhookInfo w in m_Cfg.Webhooks)
            {
                bool aOn = g.Toggle(w.Describe() + (w.Enabled ? "" : "　[停用]") + (w.VerifyStatus is "ok" or "" ? "" : "　[" + w.VerifyStatus + "]"),
                                    aBound.Contains(w.Id), $"drelay/{m_Gen}/bind/{cat.Name}/{w.Id}");
                if (aOn) aPicked.Add(w.Id);
            }
            foreach (string aMissing in aBound.Where(id => m_Cfg.Webhooks.All(w => w.Id != id)))
                g.Note($"[注意] 綁著一條已經不在清單裡的 webhook（{aMissing}）—— 按儲存會把它拿掉");
            if (m_Cfg.Webhooks.Count > 0 && g.Button($"儲存 {cat.Name}", "drelay/btn/save_cat/" + cat.Name))
            {
                m_Message = SCP_DiscordConfigStore.TrySetCategoryWebhooks(m_DataRoot, cat.Name, aPicked, out string? aErr)
                    ? $"{cat.Name} ⇒ {(aPicked.Count == 0 ? "不送" : aPicked.Count + " 條 webhook")}" : "[未寫入] " + aErr;
                Reload();
            }
        }
        foreach (string aOrphan in m_Cfg.CategoryWebhooks.Keys.Where(k => m_Cats.All(c => !string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))))
            g.Note($"[注意] 「{aOrphan}」已經不是頻道分類，但設定裡還綁著 webhook —— 重新建這個分類，或用 `cmd discord-relay --arg op=bind --arg category={aOrphan} --arg ids=` 清掉");
    }

    void DrawAvatars(SCP_Ui g)
    {
        using var aFold = g.Fold("persona 在 Discord 上的頭像網址", "drelay/fold/avatar", false);
        if (!aFold.Open) return;
        g.Note("Discord 只收公開網址。persona 自己填了（「persona 顯示資料」頁）就用那個，沒填就用下面的範本（{persona} 換成 id）。");
        string aT = g.TextField("範本", g.FieldValue($"drelay/{m_Gen}/tpl", m_Cfg.AvatarUrlTemplate), $"drelay/{m_Gen}/tpl");
        using (g.Row())
        {
            if (g.Button("儲存範本", "drelay/btn/save_tpl"))
            {
                m_Message = SCP_DiscordConfigStore.TrySetAvatarTemplate(m_DataRoot, aT, out string? aErr) ? "已儲存範本" : "[未寫入] " + aErr;
                Reload();
            }
            if (g.Button("回到預設範本", "drelay/btn/reset_tpl"))
            {
                m_Message = SCP_DiscordConfigStore.TrySetAvatarTemplate(m_DataRoot, "", out string? aErr) ? "已回到預設範本" : "[未寫入] " + aErr;
                Reload();
            }
            if (!Busy && Directory.Exists(m_LettersRoot) && g.Button("檢查全部頭像網址", "drelay/btn/check"))
            {
                string aLetters = m_LettersRoot, aTpl = m_Cfg.AvatarUrlTemplate;
                var aResult = new Dictionary<string, string>();
                m_AvatarCheck = aResult;
                m_Job = Task.Run(() =>
                {
                    int aBad = 0;
                    foreach (string p in SCP_PersonaDisplay.ListPersonas(aLetters))
                    {
                        int aCode = SCP_DiscordConfigStore.ProbeUrl(SCP_DiscordConfigStore.ResolveAvatarUrl(aLetters, p, aTpl, out _), out string? e);
                        lock (aResult) aResult[p] = aCode == 200 ? "200" : (aCode > 0 ? "HTTP " + aCode : e ?? "連不上");
                        if (aCode != 200) aBad++;
                    }
                    m_JobResult = aBad == 0 ? "頭像網址全部拿得到" : $"有 {aBad} 個 persona 的頭像網址拿不到（Discord 會改用 webhook 自己的頭像）";
                });
            }
            if (Busy) g.Label("｜檢查中…");
        }
        if (!Directory.Exists(m_LettersRoot)) { g.Note($"[注意] 找不到信件夾根（{m_LettersRoot}）"); return; }
        using (g.Table("persona", "來源", "網址", "檢查"))
        {
            foreach (string p in SCP_PersonaDisplay.ListPersonas(m_LettersRoot))
            {
                string aUrl = SCP_DiscordConfigStore.ResolveAvatarUrl(m_LettersRoot, p, m_Cfg.AvatarUrlTemplate, out bool aExplicit);
                string aCheck;
                lock (m_AvatarCheck) aCheck = m_AvatarCheck.TryGetValue(p, out string? c) ? c : "-";
                g.TableRow(p, aExplicit ? "自填" : "範本", aUrl, aCheck);
            }
        }
    }
}
