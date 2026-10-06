// 區塊職責：**persona 管理頁**（TASK-0424）—— 先在 TopBar 選一位既有的 persona，再看／改她的設定。
// 物理意義：取代 Unity 的 UCL_PersonaAgentAdminPage 與舊的 persona 顯示資料頁（PersonaDisplayPage，本頁吸收）。
//   ⛔ 本頁**不建 persona、不建 agent**：建立一律走 `senate cmd persona-create`（Tim 2026-10-06）——
//      頁面另組一份身分欄就是第二份實作（Unity 那頁當年就是這樣）。
//   讀寫全走 SCP_Core：身分欄 `SCP_PersonaProfileWrite`、顯示資料 `SCP_PersonaDisplay`、
//   型號與信箱的解析 `SCP_AgentModelRegistry`／`SCP_AgentEmail` ⇒ 本頁只畫與收輸入，不需要 Unity Editor。
// 數值影響：
//   · 身分欄：寫 `letters/<p>/profile/<欄>.md`＋一行審計（`AwakenInit/_persona_write_audit.jsonl`，署名＝「署名」欄）；值清空＝unset（刪檔）。
//   · actual_agent：在線時同一步改 lock 與 profile（`SetLockActualAgent`），離線時只改 profile。
//   · 顯示資料：`color.md`／`avatar_url.md`／`avatar.png`。**已有頭像時換頭像要二段確認**。
//   · 推導欄（status／wake_count／agent…）與結構欄（vector、lineage）只讀 —— 前者真相源在別處，後者不該手改。
// ⚠ 視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。
#nullable enable
using SCP.Core.Bank;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Letters;

namespace Senate.Cli.Pages;

public sealed class PersonaAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "persona";
    const string SelId = "persona/sel";
    const string AuthorId = "persona/author";
    const string ColorId = "persona/edit/color";
    const string AvatarSrcId = "persona/edit/avatar_src";
    const string AvatarUrlId = "persona/edit/avatar_url";
    const string PendingId = "persona/pending";
    const string CharacterId = "persona/edit/character";
    /// <summary>欄位倉目前對齊的是哪一位（存在欄位倉裡，跨次保存）。</summary>
    const string SyncedForId = "persona/synced-for";
    static string FieldId(string iField) => "persona/edit/" + iField;

    /// <summary>頁面上可以直接改的純量身分欄（其餘身分欄是結構欄或建立時的血統，只讀）。</summary>
    static readonly (string Field, string Label)[] s_ScalarFields =
    {
        ("layer_role", "layer_role（自介 Layer 那一句）"),
        ("model", "model（LLM 型號；寫 agent 名會翻成該 agent 的預設型號）"),
        ("actual_agent", "actual_agent（實際承載的桌面工具）"),
        ("email", "email（commit trailer；留空＝回到 agent 預設）"),
        ("plurk_account", "plurk_account"),
    };

    readonly SenateModel m_Model;
    string m_LettersRoot = "";
    string m_DataRoot = "";
    string m_Region = "";
    List<string> m_Personas = new();
    string m_Sel = "";
    string? m_Message;

    // ── 快照：磁碟只在「進頁／重新讀取／換人／寫入成功」時讀 ─────────
    // 🩸 2026-10-06 Tim 實測「開頁嚴重卡頓」：視窗每一幀都重畫，而原本每一幀都對 22 位各跑一次 GetRaw
    //   （profile 每欄一檔＋wakes/ 計數＋lock＋longterm/）與顯示資料，選中的那位再多跑幾次（型號、信箱解析各自又讀一次）
    //   ⇒ 一幀上百次檔案 IO。繪製只讀快照；外部改了磁碟要按「重新讀取」才看得到（頁首有說）。
    sealed class Row { public string Persona = ""; public SCP_JsonData? Raw; public SCP_PersonaDisplayInfo Disp = null!; }
    List<Row> m_Rows = new();
    string m_DetailFor = "";
    SCP_JsonData? m_SelRaw;
    SCP_PersonaDisplayInfo? m_SelDisp;
    SCP_AgentModelResolution? m_SelModel;
    SCP_AgentEmailInfo? m_SelMail;
    bool m_SelFixture;
    string? m_SelWarn;

    /// <summary>
    /// 要不要把欄位倉重新對齊成**這一位現在的資料**。
    /// 🩸 2026-09-28（舊顯示資料頁）：欄位值會跨次保存，不對齊的話按「儲存」會把上一位的值寫到這一位身上。
    /// 🩸 2026-10-06（本頁，TASK-0424）：反過來「每次進頁都對齊」也不行 —— CLI 每一道指令都是一次新的進頁，
    ///   上一道 `--set` 的值在下一道「儲存」之前就被蓋回磁碟值，按下去寫的是舊值（或空值＝清空）。
    /// ⇒ 判準是「欄位倉現在對齊的是不是這一位」（<see cref="SyncedForId"/>，跨次保存）：換人才對齊；
    ///   另外「重新讀取」與存檔成功之後顯式對齊一次。
    /// </summary>
    bool m_SyncFields;

    public PersonaAdminPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "persona 管理";
    public override string? MenuGroup => "酒館";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    void Reload()
    {
        m_LettersRoot = m_Model.LettersRoot.Value;
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        m_Region = m_DataRoot.Length > 0 ? SCP_BankRegion.Read(m_DataRoot, out _) : SCP_BankRegion.DefaultRegion;
        m_Personas = Directory.Exists(m_LettersRoot) ? SCP_PersonaProfile.PoolNames(m_LettersRoot) : new List<string>();
        m_Personas.Sort(StringComparer.OrdinalIgnoreCase);
        if (m_Personas.Count > 0 && !m_Personas.Contains(m_Sel)) m_Sel = m_Personas[0];
        m_Rows = new List<Row>(m_Personas.Count);
        foreach (string p in m_Personas)
            m_Rows.Add(new Row { Persona = p, Raw = SCP_PersonaProfile.GetRaw(m_LettersRoot, p, m_Region), Disp = SCP_PersonaDisplay.Get(m_LettersRoot, p) });
        m_DetailFor = "";   // 選中那位的細節下次用到時重讀
    }

    /// <summary>
    /// 寫入成功之後只重讀**這一位**（清單那一列＋細節）。
    /// ⚠ 不整份重讀：GetRaw 每位約 0.1–0.3 秒（wakes/ 計數、longterm/ 解析隨信數成長），整份重讀＝每存一次卡一下。
    /// </summary>
    void RefreshSelected()
    {
        int i = m_Rows.FindIndex(r => r.Persona == m_Sel);
        if (i >= 0) m_Rows[i] = new Row { Persona = m_Sel, Raw = SCP_PersonaProfile.GetRaw(m_LettersRoot, m_Sel, m_Region), Disp = SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel) };
        m_DetailFor = "";
    }

    /// <summary>選中那一位的細節（只在換人或重讀後算一次）。</summary>
    void EnsureDetail()
    {
        if (m_DetailFor == m_Sel) return;
        m_DetailFor = m_Sel;
        m_SelWarn = null;
        m_SelRaw = SCP_PersonaProfile.GetRaw(m_LettersRoot, m_Sel, m_Region, w => m_SelWarn = "[警告] " + w);
        m_SelDisp = SCP_PersonaDisplay.Get(m_LettersRoot, m_Sel);
        m_SelModel = SCP_AgentModelRegistry.Resolve(m_LettersRoot, m_Sel, m_Region);
        m_SelMail = SCP_AgentEmail.Resolve(m_LettersRoot, m_Sel, m_Region, m_DataRoot);
        m_SelFixture = SCP_PersonaProfile.IsTestFixture(m_LettersRoot, m_Sel);
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        OpenFolderButton(iUi, m_Sel.Length > 0 ? SCP_PersonaDisplay.ProfileDir(m_LettersRoot, m_Sel) : m_LettersRoot, "persona/open-dir");
        if (iUi.Button("重新讀取", "persona/btn/reload")) { Reload(); m_SyncFields = true; m_Message = "已重新讀取"; }
        if (m_Personas.Count > 0)
        {
            string aPick = iUi.Dropdown("persona", m_Personas, m_Sel, SelId);
            if (aPick.Length > 0 && m_Personas.Contains(aPick)) m_Sel = aPick;
        }
        if (m_SyncFields || iUi.FieldValue(SyncedForId, "") != m_Sel)
        {
            m_SyncFields = false;
            SyncFields(iUi);
            iUi.SetField(SyncedForId, m_Sel);
        }
    }

    /// <summary>欄位倉換成目前選的那一位的值；待確認一起清掉（不然對上一位上膛的「覆寫頭像」會用在這一位身上）。</summary>
    void SyncFields(SCP_Ui iUi)
    {
        iUi.SetField(PendingId, "");
        iUi.SetField(AvatarSrcId, "");
        if (m_Sel.Length == 0) return;
        EnsureDetail();
        iUi.SetField(ColorId, m_SelDisp!.ColorHex);
        iUi.SetField(AvatarUrlId, m_SelDisp.AvatarUrl);
        SCP_JsonData? aRaw = m_SelRaw;
        foreach (var (f, _) in s_ScalarFields) iUi.SetField(FieldId(f), Str(aRaw, f));
        iUi.SetField(CharacterId, Str(aRaw, "character"));
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【persona 管理】");
        g.Note("先在上方選一位 persona 再編輯。建立新 persona 不在這頁：senate cmd persona-create（說明：senate cmd doc --arg op=show --arg name=Persona_Create）。");
        g.Note("畫面是進頁時讀的快照；在別處改過磁碟（指令、別的 agent）要按「重新讀取」。");
        if (string.IsNullOrEmpty(m_LettersRoot) || !Directory.Exists(m_LettersRoot))
        {
            g.Note($"[錯誤] 找不到信件夾根（{m_LettersRoot}）—— 到「路徑管理」頁設定");
            return;
        }
        if (m_Message != null) g.Note(m_Message);
        if (m_Sel.Length == 0) { g.Note("（還沒有任何 persona）"); return; }

        string aAuthor = g.TextField("署名（寫入的審計記在誰名下）", "Tim", AuthorId).Trim();
        EnsureDetail();
        if (m_SelWarn != null) g.Note(m_SelWarn);
        SCP_JsonData? aRaw = m_SelRaw;
        if (aRaw == null) { g.Note($"[錯誤] 讀不到 {m_Sel} 的 profile/"); return; }

        DrawSummary(g, aRaw);
        g.Separator();
        DrawDisplay(g, m_SelDisp!);
        g.Separator();
        DrawIdentity(g, aRaw, aAuthor);
        g.Separator();
        DrawAll(g);
    }

    // ── 概況（只讀）──────────────────────────────────────────
    void DrawSummary(SCP_Ui g, SCP_JsonData iRaw)
    {
        g.Label($"概況：{m_Sel}");
        bool aOnline = Str(iRaw, "status") == "online";
        SCP_AgentModelResolution aModel = m_SelModel!;
        SCP_AgentEmailInfo aMail = m_SelMail!;
        string aFork = Str(iRaw, "forked_from");
        using (g.Table("項目", "值"))
        {
            g.TableRow("狀態", aOnline ? "在線（" + Str(iRaw, "last_active") + " 起）" : "離線");
            g.TableRow("wake", iRaw.GetInt("wake_count", 0).ToString());
            g.TableRow("帳號（" + m_Region + " 區綁定）", Or(Str(iRaw, "agent"), "（沒有綁定 —— senate cmd persona-profile --arg op=set_bank）"));
            g.TableRow("型號（trailer）", $"{aModel.Model}　〔{aModel.Source}〕");
            g.TableRow("信箱", $"{aMail.Email}　〔{aMail.Source}〕");
            g.TableRow("fork", aFork.Length > 0 ? aFork + "（" + Str(iRaw, "forked_at") + "）" : "全新（不是 fork）");
            g.TableRow("建立", Or(Str(iRaw, "created_at"), "（沒記）"));
            g.TableRow("測試殼", m_SelFixture ? "是（早安候選不列）" : "否");
        }
    }

    // ── 顯示資料（頭像／顏色／Discord 頭像網址）────────────────
    void DrawDisplay(SCP_Ui g, SCP_PersonaDisplayInfo iInfo)
    {
        using (g.Row())
        {
            g.Image(iInfo.AvatarPath, 96f, iInfo.Persona);
            using (g.Column())
            {
                g.Label("顯示資料");
                g.Note(DescribeColor(iInfo));
                g.Note(iInfo.HasAvatar ? "頭像：" + iInfo.AvatarPath : "頭像：（沒有，畫預設圖）");
            }
        }

        g.TextField("顏色 #RRGGBB（留空＝刪掉、回到沒設）", g.FieldValue(ColorId, iInfo.ColorHex), ColorId);
        if (g.Button("儲存顏色", "persona/btn/save_color"))
        {
            string aHex = g.FieldValue(ColorId, "").Trim();
            m_Message = SCP_PersonaDisplay.TrySetColor(m_LettersRoot, iInfo.Persona, aHex, out string? aErr)
                ? (aHex.Length == 0 ? $"{iInfo.Persona} 的顏色已刪掉（回到沒設）" : $"{iInfo.Persona} 的顏色 ＝ {aHex.ToUpperInvariant()}")
                : "[未寫入] " + aErr;
            RefreshSelected();
        }

        // Discord 只收公開網址 ⇒ 本機的 avatar.png 給不了它（TASK-0320）
        g.TextField("Discord 頭像網址（https；留空＝用「Discord 轉發設定」頁的範本）", g.FieldValue(AvatarUrlId, iInfo.AvatarUrl), AvatarUrlId);
        if (g.Button("儲存頭像網址", "persona/btn/save_avatar_url"))
        {
            string aUrl = g.FieldValue(AvatarUrlId, "").Trim();
            m_Message = SCP_PersonaDisplay.TrySetAvatarUrl(m_LettersRoot, iInfo.Persona, aUrl, out string? aErr)
                ? (aUrl.Length == 0 ? $"{iInfo.Persona} 的頭像網址已刪掉（回到範本）" : $"{iInfo.Persona} 的頭像網址 ＝ {aUrl}")
                : "[未寫入] " + aErr;
            RefreshSelected();
        }

        g.TextField("換頭像：PNG／JPEG 圖檔的完整路徑", g.FieldValue(AvatarSrcId, ""), AvatarSrcId);
        string aPending = g.FieldValue(PendingId, "");
        string aSrc = g.FieldValue(AvatarSrcId, "").Trim();
        if (aPending == "overwrite:" + iInfo.Persona)
        {
            if (g.Button($"確認覆寫 {iInfo.Persona} 的頭像？（舊圖回不來，除非在 git 裡）", "persona/btn/confirm_avatar"))
            {
                m_Message = SCP_PersonaDisplay.TrySetAvatar(m_LettersRoot, iInfo.Persona, aSrc, true, out string? aErr)
                    ? $"{iInfo.Persona} 的頭像已換成 {aSrc}" : "[未寫入] " + aErr;
                g.SetField(PendingId, "");
                RefreshSelected();
            }
            if (g.Button("取消", "persona/btn/cancel_avatar")) g.SetField(PendingId, "");
        }
        else if (g.Button("換頭像", "persona/btn/set_avatar"))
        {
            if (iInfo.HasAvatar) { g.SetField(PendingId, "overwrite:" + iInfo.Persona); m_Message = "已經有頭像 ⇒ 請再按一次確認"; }
            else
                m_Message = SCP_PersonaDisplay.TrySetAvatar(m_LettersRoot, iInfo.Persona, aSrc, false, out string? aErr)
                    ? $"{iInfo.Persona} 的頭像已設成 {aSrc}" : "[未寫入] " + aErr;
            RefreshSelected();
        }
    }

    // ── 身分欄 ───────────────────────────────────────────────
    void DrawIdentity(SCP_Ui g, SCP_JsonData iRaw, string iAuthor)
    {
        g.Label("身分欄（profile/<欄>.md；值清空再儲存＝刪掉那一欄）");
        bool aOnline = Str(iRaw, "status") == "online";
        foreach (var (aField, aLabel) in s_ScalarFields)
        {
            using (g.IdScope("field/" + aField))
            using (g.Row())
            {
                g.TextField(aLabel, g.FieldValue(FieldId(aField), Str(iRaw, aField)), FieldId(aField));
                if (g.Button("儲存", "persona/btn/save/" + aField))
                    m_Message = SaveScalar(aField, g.FieldValue(FieldId(aField), "").Trim(), iAuthor, aField == "actual_agent" && aOnline);
            }
        }

        bool aFixture = m_SelFixture;
        if (g.Button(aFixture ? "取消測試殼標記" : "標成測試殼（早安候選不列）", "persona/btn/test_fixture"))
            m_Message = SaveScalar("test_fixture", aFixture ? "" : "1", iAuthor, false);

        g.TextArea("角色設定（character.md）", g.FieldValue(CharacterId, Str(iRaw, "character")), CharacterId, 16);
        if (g.Button("儲存角色設定", "persona/btn/save/character"))
            m_Message = SaveScalar("character", g.FieldValue(CharacterId, ""), iAuthor, false);

        g.Note("只讀：identity_vector／vector_history／fork_lineage（建立時決定）；帳號綁定走 senate cmd persona-profile --arg op=set_bank。");
    }

    string SaveScalar(string iField, string iValue, string iAuthor, bool iOnlineActualAgent)
    {
        if (iAuthor.Length == 0) return "[未寫入] 署名不可空白（審計要記是誰改的）";
        const string Reason = "persona 管理頁";
        string aWarn, aErr;
        bool aOk;
        if (iValue.Trim().Length == 0)
        {
            aOk = SCP_PersonaProfileWrite.UnsetField(m_LettersRoot, m_DataRoot, m_Sel, iField, iAuthor, Reason, out bool aHad, out aWarn, out aErr);
            if (aOk) { m_SyncFields = true; RefreshSelected(); return $"{m_Sel} 的 {iField} " + (aHad ? "已刪掉" : "本來就沒有（沒有寫入）") + Tail(aWarn); }
            return "[未寫入] " + aErr;
        }
        aOk = iOnlineActualAgent
            ? SCP_PersonaProfileWrite.SetLockActualAgent(m_LettersRoot, m_DataRoot, m_Sel, iValue.Trim(), iAuthor, Reason, out aWarn, out aErr)
            : SCP_PersonaProfileWrite.SetField(m_LettersRoot, m_DataRoot, m_Sel, iField, iValue, iAuthor, Reason, out aWarn, out aErr);
        if (!aOk) return "[未寫入] " + aErr;
        m_SyncFields = true;
        RefreshSelected();
        return $"{m_Sel} 的 {iField} 已儲存" + (iOnlineActualAgent ? "（在線 ⇒ lock 一起改）" : "") + Tail(aWarn);
    }

    // ── 全部（總覽）──────────────────────────────────────────
    void DrawAll(SCP_Ui g)
    {
        // ⛔ 這裡不畫頭像：每一張第一次畫都要解碼（12 張 10 MB ≈ 1.5 秒卡一下），只在選中那一位的「顯示資料」區才載（Tim 2026-10-06）。
        g.Label($"全部（{m_Personas.Count}）");
        using (g.Table("persona", "狀態", "wake", "帳號", "fork", "顏色", "頭像"))
            foreach (Row aRow in m_Rows)
            {
                SCP_JsonData? aRaw = aRow.Raw;
                g.TableRow(aRow.Persona,
                           Str(aRaw, "status") == "online" ? "在線" : "",
                           (aRaw?.GetInt("wake_count", 0) ?? 0).ToString(),
                           Or(Str(aRaw, "agent"), "（沒綁）"),
                           Str(aRaw, "forked_from"),
                           aRow.Disp.ColorInvalidRaw.Length > 0 ? "[壞掉]" : Or(aRow.Disp.ColorHex, "—"),
                           aRow.Disp.HasAvatar ? "有" : "—");
            }
    }

    static string Str(SCP_JsonData? iRaw, string iKey) => iRaw != null && iRaw.TryGetString(iKey, out string v) ? v : "";
    static string Or(string iValue, string iFallback) => iValue.Length > 0 ? iValue : iFallback;
    static string Tail(string iWarn) => iWarn.Length > 0 ? "（⚠ " + iWarn + "）" : "";

    static string DescribeColor(SCP_PersonaDisplayInfo iInfo)
        => iInfo.ColorInvalidRaw.Length > 0 ? $"顏色：[壞掉] color.md 內容不是 #RRGGBB（'{iInfo.ColorInvalidRaw}'）"
         : iInfo.ColorHex.Length > 0 ? "顏色：" + iInfo.ColorHex
         : "顏色：（沒設，用預設色）";
}
