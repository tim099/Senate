// 區塊職責：**Discord Webhook 管理頁**（TASK-0320）—— 新增（驗證）／重新驗證／停用／刪除 webhook，從舊設定匯入 Main。
// 物理意義：Tim 2026-09-28：「Webhook 有自己的管理選單，驗證後的 Webhook 緩存對應的頻道名稱」。
//           讀寫全走 `SCP_DiscordConfigStore`（與 `cmd discord-relay` 同一份）；分類要綁哪些 webhook 在「Discord 轉發設定」頁。
// 數值影響：
//   · URL 輸入欄是**密碼欄**（遮罩、不落盤、按完清空）—— webhook 被破解影響不大，但「不裸存」包含不留在畫面狀態裡。
//   · 驗證／匯入要連 Discord ⇒ 丟背景執行緒（⛔ 不卡畫面），結果下一幀出現。
//   · 刪除要二段確認；還有分類綁著的擋下並說是哪些分類。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。⛔ 畫面上任何地方都不印 webhook URL。
#nullable enable
using SCP.Core.Discord;
using SCP.Core.Gui;
using SCP.Core.Tavern;

namespace Senate.Cli.Pages;

public sealed class DiscordWebhookPage : SCP_GuiToolPage
{
    public const string PageKey = "discord-webhooks";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    SCP_DiscordConfig m_Cfg = new();
    string? m_Message;
    string m_PendingDelete = "";
    int m_Gen;

    Task? m_Job;
    string m_JobName = "";
    volatile string? m_JobResult;

    public DiscordWebhookPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "Discord Webhook";
    public override string? MenuGroup => "酒館";

    public override void OnPush() { base.OnPush(); Reload(); }

    void Reload()
    {
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) return;
        SCP_TavernChannels.EnsureMainChannel(m_DataRoot, out _);
        m_Cfg = SCP_DiscordConfigStore.Load(m_DataRoot);
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
        if (m_Job != null && m_Job.IsCompleted) { m_Message = m_JobResult; m_Job = null; Reload(); }
        if (iUi.Button("重新讀取", "dhook/btn/reload")) { Reload(); m_Message = "已重新讀取"; }
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) return;
        if (Busy) { iUi.Label($"｜{m_JobName}中…"); return; }
        if (m_Cfg.Webhooks.Count > 0 && iUi.Button("全部重新驗證", "dhook/btn/verify_all"))
        {
            string aRoot = m_DataRoot;
            List<string> aIds = m_Cfg.Webhooks.Select(w => w.Id).ToList();
            StartJob("重新驗證", () =>
            {
                int aOk = aIds.Count(id => SCP_DiscordConfigStore.TryReverify(aRoot, id, out _));
                return $"重新驗證完成：{aOk}/{aIds.Count} 條 ok";
            });
        }
        if (iUi.Button("從 notify_config 匯入 Main", "dhook/btn/import"))
        {
            string aRoot = m_DataRoot;
            StartJob("匯入", () => string.Join("；", SCP_DiscordConfigStore.ImportMainFromNotifyConfig(aRoot, out _)));
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【Discord Webhook】");
        g.Note("webhook 的 URL 以寫死的金鑰加密存檔（discord_webhooks.enc），畫面上只顯示 Server／頻道名稱。要哪個分類同步到哪幾條，在「Discord 轉發設定」頁勾。");
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            g.Note($"[錯誤] 找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定");
            return;
        }
        if (m_Message != null) g.Note(m_Message);
        if (m_Cfg.Error.Length > 0) { g.Note("[錯誤] " + m_Cfg.Error + " —— 修好之前不會覆寫它"); return; }

        DrawList(g);
        g.Separator();
        DrawAdd(g);
    }

    void DrawList(SCP_Ui g)
    {
        g.Label($"webhook：{m_Cfg.Webhooks.Count} 條");
        if (m_Cfg.Webhooks.Count == 0) g.Note("（還沒有 webhook）");
        foreach (SCP_DiscordWebhookInfo w in m_Cfg.Webhooks)
        {
            List<string> aCats = m_Cfg.CategoryWebhooks.Where(kv => kv.Value.Contains(w.Id)).Select(kv => kv.Key).ToList();
            using (g.Box(w.Describe()))
            {
                g.Note($"id {w.Id}　｜　{(w.Enabled ? "啟用" : "[停用]")}　｜　驗證：{(w.VerifyStatus.Length > 0 ? w.VerifyStatus : "沒驗過")}{(w.VerifiedAt.Length > 0 ? "（" + w.VerifiedAt + "）" : "")}"
                       + $"　｜　分類：{(aCats.Count > 0 ? string.Join("、", aCats) : "（沒有分類綁它 ⇒ 不會送）")}");
                using (g.Row())
                {
                    if (!Busy && g.Button("重新驗證", "dhook/btn/verify/" + w.Id))
                    {
                        string aRoot = m_DataRoot, aId = w.Id;
                        StartJob("驗證", () => SCP_DiscordConfigStore.TryReverify(aRoot, aId, out string? e) ? $"{aId} 驗證 ok" : $"[失敗] {aId}：{e}");
                    }
                    if (g.Button(w.Enabled ? "停用" : "啟用", "dhook/btn/enable/" + w.Id))
                    {
                        m_Message = SCP_DiscordConfigStore.TrySetWebhookEnabled(m_DataRoot, w.Id, !w.Enabled, out string? aErr)
                            ? $"{w.Describe()} 已{(!w.Enabled ? "啟用" : "停用")}" : "[未寫入] " + aErr;
                        Reload();
                    }
                    if (m_PendingDelete == w.Id)
                    {
                        if (g.Button("確認刪除（URL 一起刪掉，回不來）", "dhook/btn/del_confirm/" + w.Id))
                        {
                            m_Message = SCP_DiscordConfigStore.TryRemoveWebhook(m_DataRoot, w.Id, out string? aErr)
                                ? $"已刪除 {w.Describe()}" : "[未寫入] " + aErr;
                            m_PendingDelete = "";
                            Reload();
                        }
                        if (g.Button("取消", "dhook/btn/del_cancel/" + w.Id)) m_PendingDelete = "";
                    }
                    else if (g.Button("刪除", "dhook/btn/del/" + w.Id)) m_PendingDelete = w.Id;
                }
            }
        }
    }

    void DrawAdd(SCP_Ui g)
    {
        g.Label("新增 webhook");
        g.Note("在 Discord 的頻道設定 → 整合 → Webhook 複製網址貼上。會先驗證（GET 那個網址），通過才存。");
        string aUrl = g.PasswordField("Webhook URL", $"dhook/{m_Gen}/url");
        string aLabel = g.TextField("自訂名稱（選填）", "", $"dhook/{m_Gen}/label");
        if (Busy || !g.Button("驗證並新增", "dhook/btn/add")) return;
        string aRoot = m_DataRoot, aU = aUrl, aL = aLabel;
        g.SetField($"dhook/{m_Gen}/url{SCP_Ui.MaskedIdSuffix}", "");
        m_Gen++;
        StartJob("驗證並新增", () => SCP_DiscordConfigStore.TryAddWebhook(aRoot, aU, aL, out string id, out string? e)
            ? $"已加入 webhook {id}：{SCP_DiscordConfigStore.Load(aRoot).Webhooks.First(x => x.Id == id).Describe()}"
            : "[未寫入] " + e);
    }
}
