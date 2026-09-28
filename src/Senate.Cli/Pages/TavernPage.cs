// 區塊職責：**酒館訊息頁**（Senate 版，TASK-0317 ①）—— 選房、分頁看訊息；參考 Unity `UCL_ChatTavernPage` 的呈現。
// 物理意義：讀取走 `SCP_TavernRead`（索引定址），「一則訊息怎麼顯示」全交給 `SCP_TavernDisplay`（唯一判準，之後 Discord 轉發共用）
//           ⇒ 本頁只畫，⛔ 不自己判寄件人、不自己找頭像。不需要 Unity Editor。
// 數值影響：純讀。每 2 秒看一次該房的 `_seq.txt`，有新訊息且停在最新一頁才重讀（同 Unity 版的節奏）。
// ⚠ 本頁**不發文**：發文走 `senate cmd tavern-post`（要不要在這裡加發文框，TASK-0317 註明動工時再問 Tim）。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）；訊息本文裡的 emoji 是原文，畫成方框也照印。
#nullable enable
using System.Globalization;
using SCP.Core.Gui;
using SCP.Core.Tavern;

namespace Senate.Cli.Pages;

public sealed class TavernPage : SCP_GuiToolPage
{
    public const string PageKey = "tavern";
    public const int PageSize = 10;
    const float AvatarSide = 48f;
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string m_LettersRoot = "";
    string? m_Error;
    List<string> m_Rooms = new();
    string m_Room = "tavern";
    /// <summary>0 ＝ 最新一頁。</summary>
    int m_Page;
    int m_LastSeq;
    DateTime m_NextPollUtc = DateTime.MinValue;
    List<SCP_TavernDisplayRow> m_Rows = new();
    List<string> m_AllRooms = new();
    bool m_ShowArchived;

    public TavernPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "酒館訊息";
    public override string? MenuGroup => "酒館";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    void Reload()
    {
        m_Error = null;
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        m_LettersRoot = m_Model.LettersRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        { m_Error = $"找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定"; m_Rows.Clear(); return; }
        if (string.IsNullOrEmpty(m_LettersRoot) || !Directory.Exists(m_LettersRoot))
            m_Error = $"找不到信件夾根（{m_LettersRoot}）⇒ **頭像全部畫成預設圖**（不是它們沒有頭像）";

        m_AllRooms = SCP_TavernRead.EnumerateRoomIds(m_DataRoot);
        FilterRooms();
        if (m_Rooms.Count > 0 && !m_Rooms.Contains(m_Room)) m_Room = m_Rooms.Contains("tavern") ? "tavern" : m_Rooms[0];
        LoadPage();
    }

    void LoadPage()
    {
        m_LastSeq = SCP_TavernRooms.ReadCurrentSeq(m_DataRoot, m_Room);
        int aTo = m_LastSeq - m_Page * PageSize;
        if (aTo < 1) { m_Rows = new(); return; }
        int aFrom = Math.Max(1, aTo - PageSize + 1);
        var aMsgs = SCP_TavernRead.Range(m_DataRoot, m_Room, aFrom, aTo);
        m_Rows = SCP_TavernDisplay.ResolveAll(m_LettersRoot, aMsgs);
    }

    /// <summary>
    /// 房間選單：封存的頻道（TASK-0318，`SCP_TavernChannels`）預設不列 —— 勾「顯示封存」才列。
    /// ⚠ 正在看的那一房即使封存了也留在選單裡（不然選單顯示的值會不在清單上）。
    /// </summary>
    void FilterRooms()
    {
        m_Rooms = m_ShowArchived
            ? new List<string>(m_AllRooms)
            : m_AllRooms.Where(r => r == m_Room || !SCP_TavernChannels.IsArchived(m_DataRoot, r)).ToList();
    }

    int PageCount => Math.Max(1, (m_LastSeq + PageSize - 1) / PageSize);

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", "tavern/btn/reload")) Reload();
        bool aShow = iUi.Toggle("顯示封存", m_ShowArchived, "tavern/show_archived");
        if (aShow != m_ShowArchived) { m_ShowArchived = aShow; FilterRooms(); }
        if (m_Rooms.Count > 0)
        {
            string aPick = iUi.Dropdown("房間", m_Rooms, m_Room, "tavern/sel/room");
            if (aPick.Length > 0 && aPick != m_Room) { m_Room = aPick; m_Page = 0; LoadPage(); }
        }
        if (iUi.Button("最新", "tavern/btn/newest") && m_Page != 0) { m_Page = 0; LoadPage(); }
        if (iUi.Button("較新", "tavern/btn/newer") && m_Page > 0) { m_Page--; LoadPage(); }
        if (iUi.Button("較舊", "tavern/btn/older") && m_Page + 1 < PageCount) { m_Page++; LoadPage(); }
        iUi.Label($"｜第 {m_Page + 1} / {PageCount} 頁（每頁 {PageSize} 則，最新 seq {m_LastSeq}）");
    }

    protected override void DrawContent(SCP_Ui g)
    {
        PollForNew();
        g.Title($"【酒館】{m_Room}");
        if (m_Error != null) g.Note("[注意] " + m_Error);
        if (m_Rows.Count == 0) { g.Note("（這一頁沒有訊息）"); return; }

        // 由舊到新（跟聊天軟體一樣，最新的在最下面）。
        foreach (SCP_TavernDisplayRow r in m_Rows) DrawRow(g, r);
    }

    /// <summary>停在最新一頁時才自動接新訊息 —— 翻到舊頁還在看的時候，畫面不該自己跳走。</summary>
    void PollForNew()
    {
        if (m_Page != 0 || m_Error != null && m_Rows.Count == 0) return;
        if (DateTime.UtcNow < m_NextPollUtc) return;
        m_NextPollUtc = DateTime.UtcNow + PollInterval;
        if (SCP_TavernRooms.ReadCurrentSeq(m_DataRoot, m_Room) != m_LastSeq) LoadPage();
    }

    static void DrawRow(SCP_Ui g, SCP_TavernDisplayRow r)
    {
        SCP_TavernMessage m = r.Message;
        using (g.IdScope("msg" + m.Seq))
        using (g.Row())
        {
            g.Image(r.AvatarPath, AvatarSide, r.Persona.Length > 0 ? r.Persona : r.Name);
            using (g.Column())
            {
                string aWho = r.SenderKind switch
                {
                    SCP_TavernSenderKind.External => r.Name + "（外部）",
                    SCP_TavernSenderKind.Unknown => r.Name + "（不明寄件人）",
                    _ => r.Name,
                };
                string aReply = m.ReplyTo.HasValue ? $"　回覆 #{m.ReplyTo.Value.ToString(CultureInfo.InvariantCulture)}" : "";
                string aKind = m.Kind is "" or "chat" ? "" : $"　[{m.Kind}]";
                g.Label($"{aWho}　{r.TimeLocal}　#{m.Seq}{aKind}{aReply}");
                g.Paragraph(m.Body);
                foreach (SCP_TavernRef aRef in m.Refs)
                    g.Note("附件：" + (aRef.Label.Length > 0 ? aRef.Label + " — " : "") + aRef.Path);
                if (r.MetaText.Length > 0) g.Note(r.MetaText);
            }
        }
        g.Separator();
    }
}
