// 區塊職責：**書店頁**（TASK-0403）—— 藏書架（依系列）、全文書庫、捐贈簿、捐贈表單、推薦書單。
// 物理意義：Unity `UCL_LibraryManagePage` 書店那幾區的對應（漫畫區在「漫畫庫」頁、舊筆記索引區由「閱讀心得」頁的全庫瀏覽取代）。
//           資料全部來自 SCP_Books*（與 `senate cmd book` 的 shelf／series／donations 同源）：藏書架用 `SCP_BooksShelf.LoadShelf`，
//           捐贈簿用 `SCP_BooksDonations.LoadDonations`。⛔ 本頁不存路徑、不自己解析 `_donation.json` 欄位。
// 數值影響：唯一動錢的是「捐贈」，且**先預覽、確認才扣**（走 `SCP_BooksOps.Donate`，與 `book op=donate` 同一支）；其餘純讀。
// 🩸 守衛：
//   ① 捐贈的 bank（錢從誰的帳出）與 persona 要**明確填**，⛔ 不代取（猜錯是扣別人的錢）；沒裝書店閘就明說，不假裝成功。
//   ② 壞檔數要出現在數字旁邊（「共 N 本」靜默吸收讀不到的列，跟「讀空目錄不報錯」同族）。
//   ③ 下游下拉的 key 綁上游身分（Dropdown 的值存在 key/value，換上游後舊值不在新清單裡 ⇒ 標題與實際脫鉤）。
// @doc-sync: <SCP_Core>/Docs~/Library.md（頁面段）
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Books;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Library;

namespace Senate.Cli.Pages;

public sealed class BookshopPage : SCP_GuiToolPage
{
    public const string PageKey = "bookshop";
    const string KindKey = "bookshop/sel/kind";
    const string FullBookKey = "bookshop/sel/fullbook";

    sealed class FullBook
    {
        public string Slug = "";
        public string Title = "";
        public int ChapterCount;
        public bool HasNotes;
        public bool IsDonated;
        public string Donor = "";
        public string DonorPersona = "";
        public int Tokens;
        public string DonatedAt = "";
        public string Note = "";
    }

    sealed class Recommend
    {
        public string Title = "";
        public string Author = "";
        public string Status = "";
        public string BookId = "";
        public string Synopsis = "";
        public string AddedDate = "";
    }

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string? m_LoadError;
    bool m_Loaded;
    List<SCP_ShelfBook> m_Shelf = new List<SCP_ShelfBook>();
    List<SCP_BookSeriesEntry> m_Series = new List<SCP_BookSeriesEntry>();
    string? m_SeriesError;
    readonly List<string> m_Warnings = new List<string>();
    List<FullBook> m_FullBooks = new List<FullBook>();
    List<SCP_JsonData> m_Donations = new List<SCP_JsonData>();
    List<Recommend> m_Recommends = new List<Recommend>();

    string m_KindFilter = "";
    string m_FullBookSlug = "";

    // 捐贈表單
    string m_DonateBook = "";
    string m_DonateBank = "";
    string m_DonatePersona = "";
    string m_DonateTokens = SCP_BooksOps.DonationBasePrice.ToString();
    bool m_DonatePreview;
    string m_DonateMsg = "";

    public BookshopPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => "書店";
    public override string? MenuGroup => "閱讀";

    public override void OnPush()
    {
        base.OnPush();
        Load();
    }

    void Load()
    {
        m_Loaded = true;
        m_LoadError = null;
        m_Warnings.Clear();
        m_Shelf = new List<SCP_ShelfBook>();
        m_Series = new List<SCP_BookSeriesEntry>();
        m_SeriesError = null;
        m_FullBooks = new List<FullBook>();
        m_Donations = new List<SCP_JsonData>();
        m_Recommends = new List<Recommend>();
        m_DonatePreview = false;

        // ⛔ 不寫死後備路徑：解析不到就明說
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        { m_LoadError = $"解析不到 AgentCommands 資料根（{m_Model.AgentCommandsRoot.Error ?? m_DataRoot}）—— 到「路徑管理」頁設定"; return; }

        m_Shelf = SCP_BooksShelf.LoadShelf(m_DataRoot, m_Warnings);
        m_Series = SCP_BooksShelf.LoadSeries(m_DataRoot, out m_SeriesError);
        m_Donations = SCP_BooksDonations.LoadDonations(m_DataRoot, m_Warnings);
        LoadFullBooks();
        LoadRecommends();
    }

    void LoadFullBooks()
    {
        string aBooks = SCP_BooksDonations.BooksRoot(m_DataRoot);
        string aNotes = SCP_BookStore.BookNotesRoot(m_DataRoot);
        if (!Directory.Exists(aBooks)) return;
        var aDirs = new List<string>(Directory.GetDirectories(aBooks));
        aDirs.Sort(StringComparer.Ordinal);
        foreach (string aDir in aDirs)
        {
            string aSlug = Path.GetFileName(aDir);
            if (aSlug.StartsWith("_", StringComparison.Ordinal) || aSlug == SCP_BooksDonations.TipsDirName) continue;
            var aFb = new FullBook
            {
                Slug = aSlug,
                ChapterCount = SCP_BookStore.CountProseIn(aDir),   // `NNN_v2.txt` 不算一章（TASK-0436）
                HasNotes = Directory.Exists(Path.Combine(aNotes, aSlug)),
            };
            string aDpath = Path.Combine(aDir, SCP_BooksDonations.DonationFileName);
            if (File.Exists(aDpath))
            {
                SCP_JsonData? aDj = SCP_LibraryIO.LoadJson(aDpath, out string? aErr);
                if (aDj == null) m_Warnings.Add($"`{aSlug}/{SCP_BooksDonations.DonationFileName}` 讀取失敗：{aErr}");
                else
                {
                    aFb.Title = aDj.GetString("title", "");
                    aFb.Donor = aDj.GetString("donor", "");
                    aFb.DonorPersona = aDj.GetString("donor_persona", "");
                    aFb.Tokens = aDj.GetInt("tokens", 0);
                    aFb.DonatedAt = aDj.GetString("donated_at", "");
                    aFb.Note = aDj.GetString("note", "");
                    aFb.IsDonated = aFb.Donor.Length > 0;
                }
            }
            if (aFb.Title.Length == 0) aFb.Title = aSlug;
            m_FullBooks.Add(aFb);
        }
    }

    /// <summary>推薦書單：優先 `_recommended/` 一 rec 一檔；退回舊單檔 `_recommended.json` 的 recommendations[]。</summary>
    void LoadRecommends()
    {
        string aNotes = SCP_BookStore.BookNotesRoot(m_DataRoot);
        void Add(SCP_JsonData? r)
        {
            if (r == null || !r.IsObject) return;
            string aTitle = r.GetString("title", "");
            if (aTitle.Length == 0) return;
            m_Recommends.Add(new Recommend
            {
                Title = aTitle, Author = r.GetString("author", ""), Status = r.GetString("status", ""),
                BookId = r.GetString("book_id", ""), Synopsis = r.GetString("synopsis", ""),
                AddedDate = r.GetString("added_date", ""),
            });
        }
        string aDir = Path.Combine(aNotes, "_recommended");
        if (Directory.Exists(aDir))
        {
            foreach (string f in Directory.GetFiles(aDir, "*.json"))
            {
                SCP_JsonData? aJ = SCP_LibraryIO.LoadJson(f, out string? aErr);
                if (aJ == null) m_Warnings.Add($"推薦 `{Path.GetFileName(f)}` 讀取失敗：{aErr}");
                else Add(aJ);
            }
        }
        else
        {
            string aOld = Path.Combine(aNotes, "_recommended.json");
            SCP_JsonData? aJ = File.Exists(aOld) ? SCP_LibraryIO.LoadJson(aOld, out _) : null;
            if (aJ != null && aJ.IsObject && aJ.Contains("recommendations") && aJ["recommendations"].IsArray)
                for (int i = 0; i < aJ["recommendations"].Count; i++) Add(aJ["recommendations"][i]);
        }
        m_Recommends.Sort((a, b) =>
        {
            int c = string.CompareOrdinal(a.AddedDate, b.AddedDate);
            return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title);
        });
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (!m_Loaded) Load();
        OpenFolderButton(iUi, m_DataRoot.Length > 0 ? SCP_BooksDonations.BooksRoot(m_DataRoot) : null, "bookshop/open-dir", "開啟 Books 資料夾");
        if (iUi.Button("重新讀取", "bookshop/reload")) Load();
        if (iUi.Button("閱讀心得頁", "bookshop/to-notes")) Controller?.Push(new ReadingNotesPage(m_Model));
        if (m_LoadError == null) iUi.Label($"｜藏書 {m_Shelf.Count} 本");
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (!m_Loaded) Load();
        if (m_LoadError != null) { g.Label("⚠ " + m_LoadError); return; }

        using (var aFold = g.Fold("📚 藏書架（依系列）", "bookshop/fold/shelf", iDefaultOpen: true))
            if (aFold.Open) DrawShelf(g);
        using (var aFold = g.Fold($"📖 全文書庫（{m_FullBooks.Count}）", "bookshop/fold/full", iDefaultOpen: true))
            if (aFold.Open) DrawFullBooks(g);
        using (var aFold = g.Fold($"🎁 捐贈簿（{m_Donations.Count}）", "bookshop/fold/donations", iDefaultOpen: false))
            if (aFold.Open) DrawDonations(g);
        using (var aFold = g.Fold("💝 捐贈（會扣 token）", "bookshop/fold/donate", iDefaultOpen: false))
            if (aFold.Open) DrawDonateForm(g);
        using (var aFold = g.Fold($"📝 推薦書單（{m_Recommends.Count}）", "bookshop/fold/recommend", iDefaultOpen: false))
            if (aFold.Open) DrawRecommends(g);

        if (m_Warnings.Count > 0)
        {
            g.Separator();
            g.Label($"⚠ 讀取失敗 {m_Warnings.Count} 筆（上面的數字沒有包含它們）：");
            foreach (string w in m_Warnings) g.Note("・" + w);
        }
    }

    // ── 藏書架 ───────────────────────────────────────────────────────
    void DrawShelf(SCP_Ui g)
    {
        var aKinds = new List<string> { "" };
        foreach (string k in SCP_BooksClassification.AllKindKeys.Split('|')) aKinds.Add(k);
        var aOptions = new List<SCP_GuiOption>();
        foreach (string k in aKinds) aOptions.Add(new SCP_GuiOption(k, k.Length == 0 ? "（全部）" : k));
        m_KindFilter = g.Dropdown("種類篩選", aOptions, m_KindFilter, KindKey);

        List<SCP_ShelfBook> aBooks = m_Shelf;
        if (m_KindFilter.Length > 0 && SCP_BooksClassification.TryParseKind(m_KindFilter, out SCP_BookKind aKf))
            aBooks = aBooks.FindAll(b => b.Kind == aKf);
        if (aBooks.Count == 0) { g.Note("（架上沒有符合條件的書）"); return; }

        var aGroups = SCP_BooksShelf.GroupBySeries(aBooks);
        int aMulti = aGroups.FindAll(x => x.Value.Count > 1).Count;
        g.Label($"共 {aBooks.Count} 本／{aGroups.Count} 個系列（其中 {aMulti} 個是多冊系列，其餘為單本自成一系列）"
                + (m_Warnings.Count > 0 ? $"，另有 {m_Warnings.Count} 筆讀取失敗 ⚠ 見頁尾" : ""));
        if (m_SeriesError != null) g.Note("⚠ " + m_SeriesError);

        using (g.Table("種類", "系列", "冊", "id（閱讀用）", "書名", "作者／捐贈者", "章", "日期"))
            foreach (var grp in aGroups)
            {
                bool aSingle = grp.Value.Count == 1 && string.IsNullOrEmpty(grp.Value[0].Series);
                string aSeriesName = aSingle ? "（單本）" : SCP_BooksClassification.SeriesPath(m_Series, grp.Key);
                foreach (SCP_ShelfBook b in grp.Value)
                    g.TableRow(SCP_BooksClassification.KindLabel(b.Kind), aSeriesName, b.Volume > 0 ? b.Volume.ToString() : "—",
                               b.Book, "《" + b.Title + "》", b.Persona, b.Chapters.ToString(), b.Date);
            }
        g.Note("改分類：`senate cmd book --arg op=classify --arg book=<id> --arg kind=… --arg series=… --arg volume=…`");
    }

    // ── 全文書庫 ─────────────────────────────────────────────────────
    void DrawFullBooks(SCP_Ui g)
    {
        if (m_FullBooks.Count == 0) { g.Note($"（{SCP_BooksDonations.BooksRoot(m_DataRoot)} 底下沒有任何書）"); return; }
        var aLabels = m_FullBooks.ConvertAll(b => $"{b.Title} ({b.Slug})");
        int aIdx = m_FullBooks.FindIndex(b => b.Slug == m_FullBookSlug);
        string aLabel = g.Dropdown("全文書", aLabels, aLabels[aIdx < 0 ? 0 : aIdx], FullBookKey);
        if (!aLabels.Contains(aLabel)) aLabel = aLabels[0];
        FullBook aFb = m_FullBooks[aLabels.IndexOf(aLabel)];
        m_FullBookSlug = aFb.Slug;

        g.Label($"{aFb.Title}　`{aFb.Slug}`　章數 {aFb.ChapterCount}");
        if (aFb.IsDonated)
        {
            g.Note($"捐贈：{aFb.Donor}{(aFb.DonorPersona.Length > 0 ? " / " + aFb.DonorPersona : "")}　{aFb.Tokens} token　{aFb.DonatedAt}");
            if (aFb.Note.Length > 0) g.Note(aFb.Note);
        }
        else g.Note("（這本不是捐贈來的 —— 館內自產或尚未登記）");
        g.Note(aFb.HasNotes ? "有對應的閱讀筆記（BookNotes）" : "沒有對應的閱讀筆記");

        if (g.Button("編輯書籍", "bookshop/btn/edit"))
        {
            var aEdit = new BookEditPage(m_Model);
            aEdit.SetBook(aFb.Slug);
            Controller?.Push(aEdit);
        }
    }

    // ── 捐贈簿 ───────────────────────────────────────────────────────
    void DrawDonations(SCP_Ui g)
    {
        if (m_Donations.Count == 0) { g.Note("（還沒有捐贈紀錄）"); return; }
        using (g.Table("書名", "slug", "捐贈者", "token", "日期", "備註"))
            foreach (SCP_JsonData d in m_Donations)
            {
                string aPersona = d.GetString(SCP_BooksDonations.Key_DonorPersona, "");
                string aNote = d.GetString(SCP_BooksDonations.Key_Note, "");
                g.TableRow(d.GetString(SCP_BooksDonations.Key_Title, ""), d.GetString(SCP_BooksDonations.Key_Book, ""),
                           d.GetString(SCP_BooksDonations.Key_Donor, "?") + (aPersona.Length > 0 ? " / " + aPersona : ""),
                           d.GetInt(SCP_BooksDonations.Key_Tokens, 0) + " / " + SCP_BooksOps.DonationBasePrice,
                           d.GetString(SCP_BooksDonations.Key_DonatedAt, d.GetString(SCP_BooksDonations.Key_PublishedAt, "?")),
                           aNote.Length > 60 ? aNote.Substring(0, 60) + "…" : aNote);
            }
        g.Note("詳細報表：`senate cmd book --arg op=donations`／`--arg op=tips`");
    }

    // ── 捐贈表單：預覽 → 確認才扣 ────────────────────────────────────────
    void DrawDonateForm(SCP_Ui g)
    {
        g.Note("把 `Books/<slug>/` 這本書登記為捐贈，**會從指定帳戶扣 token**（先預覽，確認才扣）。同一本書不重捐，要再給錢走打賞。");
        m_DonateBook = g.TextField("書（slug）", m_DonateBook, "bookshop/donate/book");
        m_DonateBank = g.TextField("bank（錢從誰的帳出）", m_DonateBank, "bookshop/donate/bank");
        m_DonatePersona = g.TextField("persona（捐贈者署名）", m_DonatePersona, "bookshop/donate/persona");
        m_DonateTokens = g.TextField("token 數", m_DonateTokens, "bookshop/donate/tokens");
        if (m_DonateMsg.Length > 0) g.Note(m_DonateMsg);

        if (!m_DonatePreview)
        {
            if (g.Button("預覽捐贈…", "bookshop/btn/donate-preview"))
            {
                string? aWhy = CheckDonateInput(out _);
                if (aWhy != null) m_DonateMsg = "⚠ " + aWhy;
                else { m_DonatePreview = true; m_DonateMsg = ""; }
            }
            return;
        }
        string? aErr = CheckDonateInput(out int aTokens);
        if (aErr != null) { m_DonatePreview = false; m_DonateMsg = "⚠ " + aErr; return; }
        g.Label($"💸 將從 `{m_DonateBank.Trim()}` 扣 {aTokens} token，把《{m_DonateBook.Trim()}》登記為 {m_DonatePersona.Trim()} 的捐贈（尚未執行）");
        if (g.Button("確認捐贈（扣款）", "bookshop/btn/donate-confirm")) DoDonate(aTokens);
        if (g.Button("取消", "bookshop/btn/donate-cancel")) m_DonatePreview = false;
    }

    string? CheckDonateInput(out int oTokens)
    {
        oTokens = 0;
        if (m_DonateBook.Trim().Length == 0) return "缺書（slug）";
        if (m_DonateBank.Trim().Length == 0) return "缺 bank —— 錢從誰的帳出不能猜（猜錯是扣別人的錢）";
        if (m_DonatePersona.Trim().Length == 0) return "缺 persona —— 錢包綁 persona，署名也要它";
        if (!int.TryParse(m_DonateTokens.Trim(), out oTokens) || oTokens <= 0) return $"token 數須為正整數：`{m_DonateTokens}`";
        return null;
    }

    void DoDonate(int iTokens)
    {
        m_DonatePreview = false;
        SCP_IBooksGateway? aGate = SCP_BooksGatewayHost.For(m_DataRoot);
        if (aGate == null)
        {
            // ⚠ 沒裝閘 ≠ 成功：兩件事必須不同形
            m_DonateMsg = "✗ 這個宿主沒有裝上書店閘 ⇒ **沒有扣款、沒有登記**。改用 `senate cmd book --arg op=donate …`";
            return;
        }
        string? aOut = SCP_BooksOps.Donate(m_DataRoot, aGate, m_DonateBook.Trim(), m_DonateBank.Trim(), m_DonatePersona.Trim(),
                                           "", iTokens, "", out string? aBroadcast, out string? aError);
        if (aOut == null) { m_DonateMsg = "✗ 捐贈失敗：" + (aError ?? "（沒有給理由）"); return; }
        m_DonateMsg = "✅ " + aOut.Trim().Replace('\n', ' ')
                      + (string.IsNullOrEmpty(aBroadcast) ? "" : "　（廣播稿本頁不自動發，見 `book op=donate` 的輸出）");
        Load();   // 重讀：新捐贈要出現在簿上（讀回，不是假設）
    }

    // ── 推薦書單 ─────────────────────────────────────────────────────
    void DrawRecommends(SCP_Ui g)
    {
        if (m_Recommends.Count == 0) { g.Note("（沒有推薦書）"); return; }
        foreach (Recommend r in m_Recommends)
        {
            g.Label($"{r.Title}　{r.Author}　[{r.Status}]{(r.BookId.Length > 0 ? "　✓ " + r.BookId : "")}");
            if (r.Synopsis.Length > 0) g.Note(r.Synopsis.Length > 140 ? r.Synopsis.Substring(0, 140) + "…" : r.Synopsis);
        }
    }
}
