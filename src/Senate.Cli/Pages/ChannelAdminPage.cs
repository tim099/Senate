// 區塊職責：**頻道管理頁**（TASK-0318）—— 頻道分類清單（先新增才能選）、每個頻道的分類與封存。
// 物理意義：讀寫全走 `SCP_TavernChannels`（與 `cmd channel` 同一份）⇒ 本頁只畫與收輸入。
//           頻道分類給 TASK-0316 Discord Outbound 依頻道路由用（Tim 2026-09-28：依頻道分類，不是依訊息分類）。
// 數值影響：
//   · 新增／刪除分類：寫 `ChatTavern/channel_categories.json`（刪除時還有頻道在用 ⇒ 擋下）。
// 版面：選頻道／分類下拉／儲存分類放在**頂列**（Tim 2026-09-28），內容區是分類清單、選中頻道的狀態與封存、全部頻道表。
//   · 設分類：寫 `<房間資料夾>/channel.json`。
//   · 封存：把整個房間資料夾搬到 `ChatTavern/rooms_archive/`（取消封存搬回來）；⛔ 不刪任何訊息。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。
#nullable enable
using System.Globalization;
using SCP.Core.Gui;
using SCP.Core.Tavern;

namespace Senate.Cli.Pages;

public sealed class ChannelAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "channels";
    const string SelId = "chan/sel/room";
    const string ShowArchivedId = "chan/show_archived";
    const string EditCategoryId = "chan/edit/category";
    const string NewNameId = "chan/cat/new_name";
    const string NewDescId = "chan/cat/new_desc";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    List<SCP_ChannelInfo> m_All = new();
    List<SCP_ChannelCategory> m_Cats = new();
    string? m_CatError;
    string m_Sel = "";
    string? m_Message;

    /// <summary>
    /// 進頁／換頻道後第一幀把「分類」下拉對齊成**那個頻道現在的值**。
    /// 🩸 同 PersonaDisplayPage（2026-09-28）：欄位值會跨次保存，不對齊的話按「儲存」會把上一個頻道的分類寫到這一個身上。
    /// </summary>
    bool m_SyncFields = true;

    public ChannelAdminPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "頻道管理";
    public override string? MenuGroup => "酒館";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    void Reload()
    {
        m_SyncFields = true;
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) { m_All = new(); m_Cats = new(); return; }
        m_Cats = SCP_TavernChannels.LoadCategories(m_DataRoot, out m_CatError);
        m_All = SCP_TavernChannels.ListChannels(m_DataRoot, true);
        if (m_All.Count > 0 && m_All.All(c => c.Room != m_Sel)) m_Sel = m_All.Any(c => c.Room == "tavern") ? "tavern" : m_All[0].Room;
    }

    SCP_ChannelInfo? Selected => m_All.FirstOrDefault(c => c.Room == m_Sel);

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (m_SyncFields)
        {
            m_SyncFields = false;
            iUi.SetField(EditCategoryId + "/value", Selected?.Settings.Category ?? "");
        }
        if (iUi.Button("重新讀取", "chan/btn/reload")) { Reload(); m_Message = "已重新讀取"; }
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot)) return;

        // 選頻道＋分類＋儲存放在頂列（Tim 2026-09-28）：往下捲到頻道表時還按得到。
        bool aShowArchived = iUi.ToggleValue(ShowArchivedId, false);
        List<string> aRooms = m_All.Where(c => aShowArchived || !c.Settings.Archived || c.Room == m_Sel).Select(c => c.Room).ToList();
        if (aRooms.Count == 0) return;
        string aPick = iUi.Dropdown("頻道", aRooms, m_Sel, SelId);
        if (aPick.Length > 0 && aPick != m_Sel)
        {
            m_Sel = aPick;
            // 換頻道 ⇒ 分類下拉換成新那個的值（欄位倉會留著上一個的選擇）
            iUi.SetField(EditCategoryId + "/value", Selected?.Settings.Category ?? "");
        }
        SCP_ChannelInfo? aInfo = Selected;
        if (aInfo == null || m_Cats.Count == 0) return;   // 沒有分類 ⇒ 內容區會說「先新增」

        var aOptions = new List<SCP_GuiOption> { new SCP_GuiOption("", "(未分類)") };
        aOptions.AddRange(m_Cats.Select(c => new SCP_GuiOption(c.Name)));
        string aCat = iUi.Dropdown("分類", aOptions, aInfo.Settings.Category, EditCategoryId);
        if (iUi.Button("儲存分類", "chan/btn/save_cat"))
        {
            m_Message = SCP_TavernChannels.TrySetCategory(m_DataRoot, aInfo.Room, aCat, out string? aErr)
                ? (aCat.Length == 0 ? $"{aInfo.Room} 改成未分類" : $"{aInfo.Room} 的分類 ＝ {aCat}")
                : "[未寫入] " + aErr;
            Reload();
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【頻道管理】");
        g.Note("頻道分類要先在下面的「分類清單」新增，才能給頻道選用。封存會把整個頻道資料夾搬到 rooms_archive/（訊息不刪），封存後酒館訊息頁看不到、也不能在那裡發文；隨時可以取消封存搬回來。");
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            g.Note($"[錯誤] 找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定");
            return;
        }
        if (m_Message != null) g.Note(m_Message);

        DrawCategories(g);
        g.Separator();
        DrawEditor(g);
        g.Separator();
        DrawTable(g);
    }

    void DrawCategories(SCP_Ui g)
    {
        using (var aFold = g.Fold($"分類清單（{m_Cats.Count}）", "chan/fold/cats"))
        {
            if (!aFold.Open) return;
            if (m_CatError != null) g.Note("[注意] " + m_CatError + " —— 修好之前不會覆寫它");
            if (m_Cats.Count == 0) g.Note("（還沒有任何分類）");
            foreach (SCP_ChannelCategory c in m_Cats)
            {
                int aUsed = m_All.Count(x => string.Equals(x.Settings.Category, c.Name, StringComparison.OrdinalIgnoreCase));
                using (g.IdScope("cat/" + c.Name))
                using (g.Row())
                {
                    g.Label($"{c.Name}　（{aUsed} 個頻道）{(c.Description.Length > 0 ? "　" + c.Description : "")}");
                    if (g.Button("刪除", "chan/btn/del_cat"))
                    {
                        m_Message = SCP_TavernChannels.TryRemoveCategory(m_DataRoot, c.Name, out string? aErr)
                            ? $"已刪除分類 {c.Name}" : "[未寫入] " + aErr;
                        Reload();
                    }
                }
            }
            g.TextField("新分類名稱（例如 Main、TRPG）", g.FieldValue(NewNameId, ""), NewNameId);
            g.TextField("說明（可留空）", g.FieldValue(NewDescId, ""), NewDescId);
            if (g.Button("新增分類", "chan/btn/add_cat"))
            {
                string aName = g.FieldValue(NewNameId, "").Trim();
                if (SCP_TavernChannels.TryAddCategory(m_DataRoot, aName, g.FieldValue(NewDescId, ""), out string? aErr))
                {
                    m_Message = $"已新增分類 {aName}";
                    g.SetField(NewNameId, "");
                    g.SetField(NewDescId, "");
                }
                else m_Message = "[未寫入] " + aErr;
                Reload();
            }
        }
    }

    void DrawEditor(SCP_Ui g)
    {
        SCP_ChannelInfo? aInfo = Selected;
        if (aInfo == null) { g.Note("（沒有頻道）"); return; }

        g.Label($"編輯：{aInfo.Room}{(aInfo.Name.Length > 0 && aInfo.Name != aInfo.Room ? "（" + aInfo.Name + "）" : "")}");
        g.Note($"最新 seq {aInfo.LastSeq}　｜　最後活動 {FormatTs(aInfo.LastTs)}　｜　{(aInfo.Settings.Archived ? "已封存（" + FormatTs(aInfo.Settings.ArchivedAt) + "）" : "未封存")}");
        if (aInfo.ArchiveConflict) g.Note($"[注意] rooms/ 與 rooms_archive/ 都有「{aInfo.Room}」—— 封存後又有人在 rooms/ 長出同名房；取消封存會被擋下，要人工處理");
        if (aInfo.CategoryMissing) g.Note($"[注意] 現在的分類「{aInfo.Settings.Category}」不在分類清單裡 —— 請重新選一個");

        g.Note(m_Cats.Count == 0
            ? "還沒有任何分類 ⇒ 先在上面的「分類清單」新增，才能給頻道選用。"
            : $"分類：{(aInfo.Settings.Category.Length == 0 ? "(未分類)" : aInfo.Settings.Category)}　—— 在頂列選分類後按「儲存分類」");

        if (g.Button(aInfo.Settings.Archived ? "取消封存" : "封存這個頻道", "chan/btn/archive"))
        {
            bool aWant = !aInfo.Settings.Archived;
            m_Message = SCP_TavernChannels.TrySetArchived(m_DataRoot, aInfo.Room, aWant, out string? aErr)
                ? (aWant ? $"{aInfo.Room} 已封存（整個資料夾已搬到 rooms_archive/）" : $"{aInfo.Room} 已取消封存")
                : "[未寫入] " + aErr;
            Reload();
        }
    }

    void DrawTable(SCP_Ui g)
    {
        bool aShowArchived = g.Toggle("顯示封存的頻道", g.ToggleValue(ShowArchivedId, false), ShowArchivedId);
        int aArchived = m_All.Count(c => c.Settings.Archived);
        List<SCP_ChannelInfo> aRows = m_All.Where(c => aShowArchived || !c.Settings.Archived)
            .OrderByDescending(c => c.LastTs, StringComparer.Ordinal).ToList();
        g.Label($"全部頻道：{m_All.Count}（封存 {aArchived}）—— 依最後活動排序");
        using (g.Table("頻道", "分類", "封存", "最新 seq", "最後活動"))
        {
            foreach (SCP_ChannelInfo c in aRows)
            {
                string aCat = c.Settings.Category.Length == 0 ? "-"
                    : c.CategoryMissing ? c.Settings.Category + "（不在清單）" : c.Settings.Category;
                g.TableRow(c.Room, aCat, c.ArchiveConflict ? "撞名" : c.Settings.Archived ? "封存" : "", c.LastSeq.ToString(CultureInfo.InvariantCulture), FormatTs(c.LastTs));
            }
        }
    }

    static string FormatTs(string iTs)
    {
        if (string.IsNullOrEmpty(iTs)) return "-";
        return DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aUtc)
            ? aUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : iTs;
    }
}
