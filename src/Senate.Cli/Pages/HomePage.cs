// 區塊職責：**入口頁**（stack 的最底層）—— 調介面尺寸 ＋ 進到其他頁。
// 物理意義：概念取自 Unity 端的 UCL_EditorMenuPage（一排功能鈕 ＋ 一個「Page 選擇器」下拉）。
//           ⭐ 但取得清單的方式不同：UCL 反射掃 assembly 找 ShowInPageMenu==true 的子類，
//           這裡問**頁面目錄**（顯式登記）。理由寫在 SCP_GuiPageCatalog 的檔頭：
//           反射掃出來的清單會隨「哪些 assembly 剛好載入」而變，而那個差異不報錯。
// 數值影響：按尺寸會**寫回 senate.local.json**（走 SenateModel.ApplySize）；其餘只是導覽。
// ⚠ 它自己 MenuGroup = null ⇒ 不會出現在自己的清單裡（不然會有一顆「開啟入口頁」的鈕，
//   按下去 push 第二個入口頁 —— 能跑，但那是一個誰也不想要的畫面）。
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class HomePage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;
    readonly SCP_GuiPageCatalog m_Catalog;

    /// <summary>上一次「開啟」的結果（成功也要有話說 —— 按了沒事與按了成功不得同形）。</summary>
    string? m_Message;

    // `: base()` 不是裝飾 —— 它讓 [CallerFilePath] 把**這個檔**的路徑烤進 SourceFilePath。
    // ⚠ 隱式的 base() 拿到的是 null（實測，見 SCP_GuiToolPage.SourceFilePath 的血證），
    //   那時會退回「用類別名去 repo 裡找」—— 還是找得到，只是不精確。
    public HomePage(SenateModel iModel, SCP_GuiPageCatalog iCatalog) : base()
    {
        m_Model = iModel;
        m_Catalog = iCatalog;
    }

    public override string Key => PageKey;
    public const string PageKey = "home";

    public override string Title => "Senate 後台";

    /// <summary>null ＝ 不列進自己的清單（見檔頭）。</summary>
    public override string? MenuGroup => null;

    // 最底層那頁：兩顆導覽鈕都沒有去處，畫出來只會是「按了沒事」。
    protected override bool ShowBackButton => false;
    protected override bool ShowHomeButton => false;

    protected override void ToolBarButtons(SCP_Ui g)
    {
        // 對應 UCL 選單上那顆「↻」—— 丟掉目錄的中繼資料快取後重新探測
        if (g.Button("↻ 重掃頁面清單", "home/reload")) m_Catalog.Invalidate();
    }

    protected override void DrawContent(SCP_Ui g)
    {
        DrawSizeSection(g);
        g.Space();
        DrawPageSection(g);
    }

    // ── 介面尺寸 ──────────────────────────────────────────────
    // 為什麼入口頁也放一份：換尺寸是「畫面太小看不清楚」時要做的事，
    // 而那個當下人正卡在入口頁 —— 把它藏在兩層之後等於沒有。
    // ⚠ 但仍然保留獨立的「介面尺寸」頁（它有字級／文字寬那些說明），兩邊共用同一顆 style 物件。
    void DrawSizeSection(SCP_Ui g)
    {
        using (g.Box("介面尺寸"))
        {
            SCP_GuiSize? aPick = m_Model.Style.DrawPicker(g, "home/size");
            if (aPick.HasValue) m_Model.ApplySize(aPick.Value);

            g.Label(m_Model.Style.Describe());
            if (m_Model.StyleMessage != null) g.Note(m_Model.StyleMessage);
        }
    }

    // ── 頁面入口 ──────────────────────────────────────────────
    void DrawPageSection(SCP_Ui g)
    {
        using (g.Box("頁面"))
        {
            foreach (string d in m_Catalog.Diagnostics) g.Note($"⚠ 頁面清單：{d}");

            if (m_Catalog.Entries.Count == 0)
            {
                g.Note("目前沒有任何頁面登記進清單 —— 頁面要覆寫 MenuGroup（非 null）才會出現在這裡。");
                return;
            }

            // ① 分組篩選：空字串 ＝ 全部（不是「沒有分組的那一組」—— 這個 app 的每一頁都有分組名）
            var aGroupOptions = new List<SCP_GuiOption> { new SCP_GuiOption("", "全部") };
            foreach (string grp in m_Catalog.Groups)
                aGroupOptions.Add(new SCP_GuiOption(grp, grp.Length == 0 ? "(未分組)" : grp));

            string aGroup = g.Dropdown("分組", aGroupOptions, "", "home/group");
            List<SCP_GuiPageEntry> aEntries = m_Catalog.InGroup(aGroup);

            if (aEntries.Count == 0)
            {
                // 選了一個現在沒有頁的分組（重掃之後可能發生）—— 說出來，不要畫一片空白
                g.Note($"分組「{aGroup}」現在沒有任何頁面。");
                return;
            }

            // ② 可搜尋的下拉 ＋ 開啟鈕（頁面多起來時走這條）
            var aPageOptions = new List<SCP_GuiOption>(aEntries.Count);
            foreach (SCP_GuiPageEntry e in aEntries) aPageOptions.Add(new SCP_GuiOption(e.Key, e.Label));



            using (g.Row())
            {
                bool aOpen = g.Button("開啟", "home/open");
                string aPick = g.Dropdown("頁面", aPageOptions, aEntries[0].Key, "home/page");

                // 換了分組之後，上一次選的頁可能已經不在清單裡 ⇒ 退回這一組的第一個並說出來
                // （靜默改掉選擇的話，「開啟」會開到一個使用者沒有選的頁）
                if (!Contains(aEntries, aPick))
                {
                    g.Note($"上次選的「{aPick}」不在這個分組裡 —— 開啟鈕現在指向 {aEntries[0].Key}");
                    aPick = aEntries[0].Key;
                }
                if (aOpen) Open(aPick);
            }
            if (m_Message != null) g.Note(m_Message);

            // ③ 直達鈕：現在頁面還少，一顆一顆列比「點開下拉再選」快
            //    （id 用 page key 不用序號 —— 清單順序會隨分組／標題改變）
            g.Space();
            g.Label("直接進入：");
            using (g.Row())
            {
                foreach (SCP_GuiPageEntry e in aEntries)
                    if (g.Button(e.Title.Length > 0 ? e.Title : e.Key, "home/open/" + e.Key)) Open(e.Key);
            }
        }
    }

    void Open(string iKey)
    {
        SCP_GuiPage? aPage = m_Catalog.Create(iKey);
        if (aPage == null)
        {
            // 目錄裡沒有 ⇒ 這是登記漏了，不是使用者按錯。說出 key，別只說「失敗」
            m_Message = $"⚠ 開不了「{iKey}」—— 頁面目錄裡沒有這個 key（現有：{string.Join(" / ", m_Catalog.AllKeys)}）";
            return;
        }
        m_Message = null;
        Controller?.Push(aPage);
    }

    static bool Contains(List<SCP_GuiPageEntry> iEntries, string iKey)
    {
        foreach (SCP_GuiPageEntry e in iEntries) if (e.Key == iKey) return true;
        return false;
    }
}
