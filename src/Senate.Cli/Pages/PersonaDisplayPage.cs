// 區塊職責：**persona 顯示資料頁**（TASK-0317 ④）—— 看／改每個 persona 的頭像與顏色。
// 物理意義：資料住 `<letters>/<persona>/profile/`（`avatar.png`／`color.md`），讀寫全走 `SCP_PersonaDisplay`
//           ⇒ 本頁只畫與收輸入。顯示名一律是 persona id（Tim 2026-09-28），所以這裡沒有「改名」。
// 數值影響：
//   · 改顏色：寫 `color.md`（空＝刪掉，回到「沒設」）。
//   · 換頭像：把指定的 PNG 複製成 `avatar.png`。**已有頭像時要二段確認**（蓋掉之後舊圖回不來，除非在 git 裡）。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。
#nullable enable
using SCP.Core.Gui;
using SCP.Core.Letters;

namespace Senate.Cli.Pages;

public sealed class PersonaDisplayPage : SCP_GuiToolPage
{
    public const string PageKey = "persona-display";
    const string SelId = "pdisp/sel/persona";
    const string ColorId = "pdisp/edit/color";
    const string AvatarSrcId = "pdisp/edit/avatar_src";
    const string PendingId = "pdisp/pending";
    const string AvatarUrlId = "pdisp/edit/avatar_url";

    readonly SenateModel m_Model;
    string m_LettersRoot = "";
    List<string> m_Personas = new();
    string m_Sel = "";
    string? m_Message;

    /// <summary>
    /// 進頁後第一幀要把欄位倉對齊成**現在的資料**。
    /// 🩸 實測 2026-09-28：欄位值會跨次保存（session），上一次留下的空顏色欄一開頁就在 ⇒ 直接按「儲存」就把別人的顏色刪了。
    /// </summary>
    bool m_SyncFields = true;

    public PersonaDisplayPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "persona 顯示資料";
    public override string? MenuGroup => "酒館";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    void Reload()
    {
        m_SyncFields = true;
        m_LettersRoot = m_Model.LettersRoot.Value;
        m_Personas = SCP_PersonaDisplay.ListPersonas(m_LettersRoot);
        if (m_Personas.Count > 0 && !m_Personas.Contains(m_Sel)) m_Sel = m_Personas[0];
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (m_SyncFields)
        {
            m_SyncFields = false;
            iUi.SetField(PendingId, "");
            iUi.SetField(AvatarSrcId, "");
            iUi.SetField(ColorId, m_Sel.Length > 0 ? SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel).ColorHex : "");
            iUi.SetField(AvatarUrlId, m_Sel.Length > 0 ? SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel).AvatarUrl : "");
        }
        if (iUi.Button("重新讀取", "pdisp/btn/reload")) { Reload(); m_Message = "已重新讀取"; }
        if (m_Personas.Count > 0)
        {
            string aPick = iUi.Dropdown("persona", m_Personas, m_Sel, SelId);
            if (aPick.Length > 0 && aPick != m_Sel)
            {
                m_Sel = aPick;
                // 換人 ⇒ 待確認一起清掉（留著的話，對上一個人上膛的「覆寫頭像」會用在這個人身上）。
                iUi.SetField(PendingId, "");
                // 顏色欄換成新那位的值 —— 欄位倉會留著上一個人打的字，不換的話按「儲存」會把它寫到這個人身上。
                iUi.SetField(ColorId, SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel).ColorHex);
                iUi.SetField(AvatarUrlId, SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel).AvatarUrl);
            }
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【persona 顯示資料】");
        g.Note("住在各 persona 的信件夾 `profile/`：頭像 avatar.png、顏色 color.md。顯示名一律是 persona id。");
        g.Note("沒有頭像就畫預設圖、沒有顏色就用預設色 —— ⛔ 不借 agent 或別人的（Tim 2026-09-28）。");
        if (string.IsNullOrEmpty(m_LettersRoot) || !Directory.Exists(m_LettersRoot))
        {
            g.Note($"[錯誤] 找不到信件夾根（{m_LettersRoot}）—— 到「路徑管理」頁設定");
            return;
        }
        if (m_Message != null) g.Note(m_Message);

        if (m_Sel.Length > 0) DrawEditor(g, SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel));
        g.Separator();

        g.Label($"全部（{m_Personas.Count}）");
        foreach (string p in m_Personas)
        {
            SCP_PersonaDisplayInfo aInfo = SCP_PersonaDisplay.Get(m_LettersRoot, p);
            using (g.IdScope("row/" + p))
            using (g.Row())
            {
                g.Image(aInfo.AvatarPath, 40f, p);
                using (g.Column())
                {
                    g.Label(p);
                    g.Note(DescribeColor(aInfo) + "　｜　" + (aInfo.HasAvatar ? "有頭像" : "沒有頭像（畫預設圖）"));
                }
            }
        }
    }

    void DrawEditor(SCP_Ui g, SCP_PersonaDisplayInfo iInfo)
    {
        using (g.Row())
        {
            g.Image(iInfo.AvatarPath, 96f, iInfo.Persona);
            using (g.Column())
            {
                g.Label($"編輯：{iInfo.Persona}");
                g.Note(DescribeColor(iInfo));
                g.Note(iInfo.HasAvatar ? "頭像：" + iInfo.AvatarPath : "頭像：（沒有）");
            }
        }

        g.TextField("顏色 #RRGGBB（留空＝刪掉、回到沒設）", g.FieldValue(ColorId, iInfo.ColorHex), ColorId);
        if (g.Button("儲存顏色", "pdisp/btn/save_color"))
        {
            string aHex = g.FieldValue(ColorId, "").Trim();
            m_Message = SCP_PersonaDisplay.TrySetColor(m_LettersRoot, iInfo.Persona, aHex, out string? aErr)
                ? (aHex.Length == 0 ? $"{iInfo.Persona} 的顏色已刪掉（回到沒設）" : $"{iInfo.Persona} 的顏色 ＝ {aHex.ToUpperInvariant()}")
                : "[未寫入] " + aErr;
        }

        // Discord 用的公開頭像網址（TASK-0320）：Discord 只收公開網址 ⇒ 本機的 avatar.png 給不了它
        g.TextField("Discord 頭像網址（https；留空＝用「Discord 轉發設定」頁的範本）", g.FieldValue(AvatarUrlId, iInfo.AvatarUrl), AvatarUrlId);
        if (g.Button("儲存頭像網址", "pdisp/btn/save_avatar_url"))
        {
            string aUrl = g.FieldValue(AvatarUrlId, "").Trim();
            m_Message = SCP_PersonaDisplay.TrySetAvatarUrl(m_LettersRoot, iInfo.Persona, aUrl, out string? aUErr)
                ? (aUrl.Length == 0 ? $"{iInfo.Persona} 的頭像網址已刪掉（回到範本）" : $"{iInfo.Persona} 的頭像網址 ＝ {aUrl}")
                : "[未寫入] " + aUErr;
        }

        g.TextField("換頭像：PNG／JPEG 圖檔的完整路徑", g.FieldValue(AvatarSrcId, ""), AvatarSrcId);
        string aPending = g.FieldValue(PendingId, "");
        string aSrc = g.FieldValue(AvatarSrcId, "").Trim();
        if (aPending == "overwrite:" + iInfo.Persona)
        {
            if (g.Button($"確認覆寫 {iInfo.Persona} 的頭像？（舊圖回不來，除非在 git 裡）", "pdisp/btn/confirm_avatar"))
            {
                m_Message = SCP_PersonaDisplay.TrySetAvatar(m_LettersRoot, iInfo.Persona, aSrc, true, out string? aErr)
                    ? $"{iInfo.Persona} 的頭像已換成 {aSrc}" : "[未寫入] " + aErr;
                g.SetField(PendingId, "");
            }
            if (g.Button("取消", "pdisp/btn/cancel_avatar")) g.SetField(PendingId, "");
        }
        else if (g.Button("換頭像", "pdisp/btn/set_avatar"))
        {
            if (iInfo.HasAvatar) { g.SetField(PendingId, "overwrite:" + iInfo.Persona); m_Message = "已經有頭像 ⇒ 請再按一次確認"; }
            else
                m_Message = SCP_PersonaDisplay.TrySetAvatar(m_LettersRoot, iInfo.Persona, aSrc, false, out string? aErr)
                    ? $"{iInfo.Persona} 的頭像已設成 {aSrc}" : "[未寫入] " + aErr;
        }
    }

    static string DescribeColor(SCP_PersonaDisplayInfo iInfo)
        => iInfo.ColorInvalidRaw.Length > 0 ? $"顏色：[壞掉] color.md 內容不是 #RRGGBB（'{iInfo.ColorInvalidRaw}'）"
         : iInfo.ColorHex.Length > 0 ? "顏色：" + iInfo.ColorHex
         : "顏色：（沒設，用預設色）";
}
