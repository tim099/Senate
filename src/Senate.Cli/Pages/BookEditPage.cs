// 區塊職責：**書籍編輯頁**（TASK-0403）—— 編輯全文書 `Books/<slug>/NNN.txt` 的章節、新增章節。
// 物理意義：Unity `UCL_BookEditPage` 的對應。不列進入口選單（`MenuGroup => null`），由「書店」頁的「編輯書籍」帶 slug 進來。
// 數值影響：唯一的寫入是章節 `.txt`（存檔／新增空章）。
// 🩸 守衛：
//   ① **行尾保留原檔的**：IMGUI／ImGui 的多行輸入只產生 `\n`；原檔是 CRLF 的話存回要還原，
//      否則「內容一樣、逐位元組不同」會讓整章在 git 裡翻紅，而沒有任何一層會喊（同 SCP_Core 規範 §8）。
//   ② **不覆寫既有章**（新增章撞名 ⇒ 拒絕並說出來）；**有未存改動時不准換章**（Unity 版只印 warning 然後吞掉改動）。
//   ③ TextArea 的值會被同 id 的輸入覆寫 ⇒ id 含 slug＋章名；「還原」用 `SetField` 把輸入欄位寫回原文。
//   ④ slug 只能是 `Books/` 底下的一層資料夾名（擋 `..`／路徑分隔符），⛔ 不讓頁面被帶去編輯別處的檔。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Books;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class BookEditPage : SCP_GuiToolPage
{
    public const string PageKey = "bookedit";

    readonly SenateModel m_Model;
    string m_Slug = "";
    string m_BookDir = "";
    string? m_LoadError;
    bool m_Loaded;
    List<string> m_Chapters = new List<string>();
    string m_Selected = "";
    /// <summary>已載入的原文（<c>\n</c> 行尾正規化後）—— 與輸入欄位的值比對才知道有沒有改。</summary>
    string m_Original = "";
    bool m_CrLf;
    string m_NewNum = "";
    string m_Status = "";

    public BookEditPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => m_Slug.Length == 0 ? "書籍編輯" : "書籍編輯：" + m_Slug;
    public override string? MenuGroup => null;

    /// <summary>帶書進頁（push 之前呼叫）。</summary>
    public void SetBook(string iSlug)
    {
        m_Slug = (iSlug ?? "").Trim();
        m_Loaded = false;
        m_Selected = "";
    }

    string FieldKey => $"bookedit/text/{m_Slug}/{m_Selected}";

    void Load()
    {
        m_Loaded = true;
        m_LoadError = null;
        m_Chapters = new List<string>();
        m_Original = "";
        string aDataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(aDataRoot) || !Directory.Exists(aDataRoot))
        { m_LoadError = $"解析不到 AgentCommands 資料根（{m_Model.AgentCommandsRoot.Error ?? aDataRoot}）—— 到「路徑管理」頁設定"; return; }
        if (m_Slug.Length == 0) { m_LoadError = "沒有指定書 —— 從「書店」頁的「編輯書籍」進來"; return; }
        // ④ slug 只能是 Books/ 底下的一層資料夾名
        if (m_Slug.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || m_Slug == "." || m_Slug == "..")
        { m_LoadError = $"書名（slug）不合法：`{m_Slug}`"; return; }
        m_BookDir = SCP_BooksDonations.BookDir(aDataRoot, m_Slug);
        if (!Directory.Exists(m_BookDir)) { m_LoadError = $"書籍目錄不存在：{m_BookDir}"; return; }

        foreach (string f in Directory.GetFiles(m_BookDir, "*.txt")) m_Chapters.Add(Path.GetFileName(f));
        m_Chapters.Sort(StringComparer.Ordinal);
        if ((m_Selected.Length == 0 || !m_Chapters.Contains(m_Selected)) && m_Chapters.Count > 0) m_Selected = m_Chapters[0];
        LoadSelected();
    }

    void LoadSelected()
    {
        m_Original = "";
        m_CrLf = false;
        if (m_Selected.Length == 0) return;
        try
        {
            string aRaw = File.ReadAllText(Path.Combine(m_BookDir, m_Selected), Encoding.UTF8);
            m_CrLf = aRaw.Contains("\r\n");
            m_Original = aRaw.Replace("\r\n", "\n");
        }
        catch (Exception e) { m_Status = $"✗ 讀取章節失敗 {m_Selected}：{e.Message}"; }
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (!m_Loaded) Load();
        if (m_BookDir.Length > 0) OpenFolderButton(iUi, m_BookDir, "bookedit/open-dir", "開啟書籍資料夾");
        if (iUi.Button("重新讀取", "bookedit/reload"))
        {
            Load();
            if (m_Selected.Length > 0) iUi.SetField(FieldKey, m_Original);   // 丟棄緩衝改動
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (!m_Loaded) Load();
        if (m_LoadError != null) { g.Label("⚠ " + m_LoadError); return; }
        if (m_Status.Length > 0) g.Note(m_Status);

        g.Label($"《{m_Slug}》　章節 {m_Chapters.Count}");
        m_NewNum = g.TextField("新章號（空白＝接在最大號後面）", m_NewNum, "bookedit/new-num");
        if (g.Button("新增章節", "bookedit/btn/add")) AddChapter(g);
        if (m_Chapters.Count == 0) { g.Note("（這本書還沒有任何章節 .txt）"); return; }

        string aPick = g.Dropdown("章節", m_Chapters, m_Chapters.Contains(m_Selected) ? m_Selected : m_Chapters[0], "bookedit/sel/chapter");
        string aNow = g.TextArea("內容", m_Original, FieldKey, 28).Replace("\r\n", "\n");
        bool aDirty = aNow != m_Original;

        if (aPick != m_Selected)
        {
            // ② 有未存改動就不准換章 —— Unity 版只印 warning，然後那份改動就沒了
            if (aDirty)
            {
                m_Status = $"⚠ 《{m_Selected}》有未存的改動：先存檔或還原再換章（已留在原章）";
                g.SetField("bookedit/sel/chapter/value", m_Selected);   // 下拉的值也要退回去，不然標題寫新章、內容卻是舊章
            }
            else { m_Selected = aPick; m_Status = ""; LoadSelected(); }
            return;
        }

        g.Note($"正在編輯 `{m_Selected}`{(aDirty ? "　＊有未存改動" : "")}　｜　{aNow.Length} 字　｜　行尾 {(m_CrLf ? "CRLF" : "LF")}（存檔時保留）");
        if (aDirty)
        {
            if (g.Button("存檔", "bookedit/btn/save")) Save(aNow);
            if (g.Button("還原（丟棄改動）", "bookedit/btn/revert")) g.SetField(FieldKey, m_Original);
        }
    }

    void Save(string iText)
    {
        try
        {
            string aPath = Path.Combine(m_BookDir, m_Selected);
            string aOut = m_CrLf ? iText.Replace("\n", "\r\n") : iText;
            File.WriteAllText(aPath, aOut, new UTF8Encoding(false));
            // 回讀再報，不報記憶體值
            LoadSelected();
            m_Status = m_Original == iText
                ? $"✓ 已存檔：{aPath}（{iText.Length} 字，回讀一致）"
                : $"⚠ 已寫入但回讀不一致：{aPath} —— 請重新讀取確認";
        }
        catch (Exception e) { m_Status = $"✗ 存檔失敗 {m_Selected}：{e.Message}"; }
    }

    void AddChapter(SCP_Ui g)
    {
        int aNum;
        if (m_NewNum.Trim().Length > 0)
        {
            if (!int.TryParse(m_NewNum.Trim(), out aNum) || aNum < 0) { m_Status = $"✗ 章號須為 ≥0 的整數：`{m_NewNum}`"; return; }
        }
        else
        {
            int aMax = -1;
            foreach (string c in m_Chapters)
                if (int.TryParse(Path.GetFileNameWithoutExtension(c), out int n) && n > aMax) aMax = n;
            aNum = aMax + 1;
        }
        string aName = aNum.ToString("D3") + ".txt";
        string aPath = Path.Combine(m_BookDir, aName);
        if (File.Exists(aPath)) { m_Status = $"⚠ 章節已存在，不覆寫：{aName}"; return; }
        try
        {
            File.WriteAllText(aPath, "", new UTF8Encoding(false));
            m_Status = $"✓ 新增章節：{aPath}";
            m_NewNum = "";
            g.SetField("bookedit/new-num", "");
            m_Selected = aName;
            Load();
        }
        catch (Exception e) { m_Status = $"✗ 新增章節失敗 {aName}：{e.Message}"; }
    }
}
