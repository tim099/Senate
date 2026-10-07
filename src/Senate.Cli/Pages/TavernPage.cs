// 區塊職責：**酒館訊息頁**（Senate 版，TASK-0317 ①）—— 選房、分頁看訊息。
// 物理意義：讀取走 `SCP_TavernRead`（索引定址），「一則訊息怎麼顯示」全交給 `SCP_TavernDisplay`（唯一判準，之後 Discord 轉發共用）
//           ⇒ 本頁只畫，⛔ 不自己判寄件人、不自己找頭像。不需要 Unity Editor。
// 數值影響：純讀。每 2 秒看一次該房的 `_seq.txt`，有新訊息且停在最新一頁才重讀（同 Unity 版的節奏）。
// 捲動（Tim 2026-09-30）：最新的在最下面 ⇒ 打開／重新讀取／「最新」／換房間時**捲到底**；停在最新一頁時**黏底**
//   （本來在底 ⇒ 新訊息進來跟著捲；捲上去看舊的 ⇒ 不跳）。「在不在底」由 renderer 判（`SCP_Ui.FollowContentBottom`）。
//   🩸 之前沒有這段：自動重讀其實一直在跑，但畫面停在最上面（最舊那則）⇒ 看起來像「不會顯示最新訊息」。
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
    /// <summary>下一次畫面要無條件捲到底（一次性）。</summary>
    bool m_ScrollToBottom;
    List<SCP_TavernDisplayRow> m_Rows = new();

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

        // 封存的頻道（TASK-0318）已經搬到 `rooms_archive/` ⇒ 這裡本來就列不到；要看就到「頻道管理」取消封存。
        m_Rooms = SCP_TavernRead.EnumerateRoomIds(m_DataRoot);
        if (m_Rooms.Count > 0 && !m_Rooms.Contains(m_Room)) m_Room = m_Rooms.Contains("tavern") ? "tavern" : m_Rooms[0];
        LoadPage();
        m_ScrollToBottom = true;
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

    int PageCount => Math.Max(1, (m_LastSeq + PageSize - 1) / PageSize);

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", "tavern/btn/reload")) Reload();
        OpenFolderButton(iUi, m_DataRoot.Length > 0 ? SCP_TavernRooms.RoomDir(m_DataRoot, m_Room) : null, "tavern/open-dir");
        if (m_Rooms.Count > 0)
        {
            string aPick = iUi.Dropdown("房間", m_Rooms, m_Room, "tavern/sel/room");
            if (aPick.Length > 0 && aPick != m_Room) { m_Room = aPick; m_Page = 0; LoadPage(); m_ScrollToBottom = true; }
        }
        if (iUi.Button("最新", "tavern/btn/newest")) { m_Page = 0; LoadPage(); m_ScrollToBottom = true; }
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
        foreach (SCP_TavernDisplayRow r in m_Rows) DrawRow(g, r, m_DataRoot);

        if (m_ScrollToBottom) { g.ScrollContentToBottom(); m_ScrollToBottom = false; }
        else if (m_Page == 0) g.FollowContentBottom();
    }

    /// <summary>停在最新一頁時才自動接新訊息 —— 翻到舊頁還在看的時候，畫面不該自己跳走。</summary>
    void PollForNew()
    {
        if (m_Page != 0 || m_Error != null && m_Rows.Count == 0) return;
        if (DateTime.UtcNow < m_NextPollUtc) return;
        m_NextPollUtc = DateTime.UtcNow + PollInterval;
        if (SCP_TavernRooms.ReadCurrentSeq(m_DataRoot, m_Room) != m_LastSeq) LoadPage();
    }

    static void DrawRow(SCP_Ui g, SCP_TavernDisplayRow r, string iDataRoot)
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
                    g.Note("附件：" + (aRef.Label.Length > 0 ? aRef.Label + " — " : "") + SCP_TavernRefPath.Resolve(iDataRoot, aRef.Path));
                if (r.MetaText.Length > 0) g.Note(r.MetaText);
            }
        }
        g.Separator();
    }
}
