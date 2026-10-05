// 區塊職責：**閱讀心得頁**（TASK-0401）—— 全庫瀏覽（媒材 kind → 作品 → persona）、作品入口搜尋、追回檢視與產生追回檔。
// 物理意義：Unity `UCL_ReadingNotesManagePage` 的對應。數字與文字全部來自 SCP_Library*（與 `senate cmd library` 同源）：
//           追回檢視＝`SCP_LibraryRecall.RenderRecall`（op=recall 的渲染本體）；產生追回檔＝`WriteRecallBrief`（op=recall 寫的同一個檔）。
//           ⇒ 頁面與指令不可能對同一份讀者資料說出兩種話。
// 數值影響：檢視純讀；唯一的寫入是「產生追回檔」（覆寫該 persona 的 `cmd/reading_recall_<media>.md`，機械產物本來就每次重生成）。
// 🩸 守衛：
//   ① 顯式 key 一律帶 `reading/` 前綴（顯式 key 是全域的，沒前綴會跟別頁靜默共用同一格）。
//   ② 追回文字用 TextArea 顯示，⚠ 它的值會被**同 id 的輸入覆寫**：id 必須包含 media／reader／全文開關，
//      否則換看另一位讀者時畫面還是上一份（而且看起來完全正常）。
//   ③ 搜尋要把「正本幾筆／Archive 幾筆／隱藏幾筆已遷移」分開報，⛔ 不只報總數（TASK-0169 ②：
//      「1 筆全是 Archive」與「1 筆是正本」下一步相反）。
//   ④ 根解析不到就明說，⛔ 不寫死後備路徑。
// @doc-sync: <Senate>/Docs 的 Library 文件（`senate cmd doc --arg op=show --arg name=Library`）
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Books;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Library;
using SCP.Core.Paths;

namespace Senate.Cli.Pages;

public sealed class ReadingNotesPage : SCP_GuiToolPage
{
    public const string PageKey = "reading";
    const string KindKey = "reading/sel/kind";
    const string MediaKey = "reading/sel/media";
    const string ReaderKey = "reading/sel/reader";
    const string QueryKey = "reading/query";

    sealed class Hit
    {
        public string Kind = "";
        public string Title = "";
        public string Detail = "";
        public string Path = "";
        /// <summary>只有正本（Library）命中才有；Archive 是 legacy 唯讀，沒有 reader root。</summary>
        public string MediaId = "";
        public List<string> Readers = new List<string>();
    }

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string m_LettersRoot = "";
    string? m_LoadError;
    bool m_Loaded;
    List<SCP_MediaEntry> m_Entries = new List<SCP_MediaEntry>();

    // 瀏覽
    string m_Kind = "";
    string m_MediaLabel = "";
    string m_Reader = "";

    // 搜尋
    string m_Query = "";
    bool m_ShowMigrated;
    string m_Status = "輸入作品名稱後按搜尋。搜尋只讀 metadata；Archive 內容仍須人工整理後遷移。";
    readonly List<Hit> m_Hits = new List<Hit>();
    readonly Dictionary<string, string> m_HitReader = new Dictionary<string, string>();

    // 追回檢視（同一時間只展開一份）
    string m_RecallMedia = "";
    string m_RecallReader = "";
    string m_RecallText = "";
    bool m_RecallFull;
    string m_LastBriefPath = "";

    public ReadingNotesPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => "閱讀心得";
    public override string? MenuGroup => "閱讀";

    /// <summary>
    /// 帶著書名開頁（給漫畫庫頁「開啟閱讀心得頁」用，TASK-0402）。
    /// <para>書名為空就只開頁、不搜尋 —— 空字串搜尋會把全庫撈出來。</para>
    /// </summary>
    public ReadingNotesPage(SenateModel iModel, string iTitle) : this(iModel)
    {
        m_PendingQuery = (iTitle ?? "").Trim();
    }

    string m_PendingQuery = "";

    public override void OnPush()
    {
        base.OnPush();
        Load();
        if (m_PendingQuery.Length > 0 && m_LoadError == null)
        {
            m_Query = m_PendingQuery;
            Search();
        }
        m_PendingQuery = "";
    }

    void Load()
    {
        m_Loaded = true;
        m_LoadError = null;
        m_Entries = new List<SCP_MediaEntry>();

        // ⛔ 不寫死後備路徑：解析不到就明說
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        m_LettersRoot = m_Model.LettersRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        { m_LoadError = $"解析不到 AgentCommands 資料根（{m_Model.AgentCommandsRoot.Error ?? m_DataRoot}）—— 到「路徑管理」頁設定"; return; }
        if (string.IsNullOrEmpty(m_LettersRoot) || !Directory.Exists(m_LettersRoot))
        { m_LoadError = $"解析不到 letters 根（{m_Model.LettersRoot.Error ?? m_LettersRoot}）—— 到「路徑管理」頁設定"; return; }

        m_Entries = SCP_LibraryCatalog.ListMediaEntries(m_DataRoot);
        m_Entries.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (!m_Loaded) Load();
        OpenFolderButton(iUi, m_DataRoot.Length > 0 ? SCP_BookStore.BookNotesRoot(m_DataRoot) : null, "reading/open-dir", "開啟 BookNotes");
        if (iUi.Button("重新讀取", "reading/reload")) Load();
        if (m_LoadError == null) iUi.Label($"｜Library 共 {m_Entries.Count} 部媒材");
    }

    static string KindOf(SCP_MediaEntry iEntry) => string.IsNullOrEmpty(iEntry.MediaKind) ? "(未標)" : iEntry.MediaKind;

    static string LabelOf(SCP_MediaEntry iEntry) => $"{iEntry.Title}　({iEntry.MediaId})";

    protected override void DrawContent(SCP_Ui g)
    {
        if (!m_Loaded) Load();
        if (m_LoadError != null) { g.Label("⚠ " + m_LoadError); return; }

        using (var aFold = g.Fold("🗂 全庫瀏覽", "reading/fold/browse", iDefaultOpen: true))
            if (aFold.Open) DrawBrowse(g);
        using (var aFold = g.Fold("🔎 作品入口搜尋", "reading/fold/search", iDefaultOpen: true))
            if (aFold.Open) DrawSearch(g);

        if (m_RecallText.Length > 0) DrawRecall(g);
    }

    // ── 第一～三層：媒材 kind → 作品 → persona ─────────────────────────
    void DrawBrowse(SCP_Ui g)
    {
        if (m_Entries.Count == 0) { g.Note("（Library 沒有任何 media）"); return; }

        var aKinds = new List<string>();
        foreach (SCP_MediaEntry e in m_Entries) { string k = KindOf(e); if (!aKinds.Contains(k)) aKinds.Add(k); }
        aKinds.Sort(StringComparer.Ordinal);
        string aKind = g.Dropdown("媒材", aKinds, aKinds.Contains(m_Kind) ? m_Kind : aKinds[0], KindKey);

        var aMedias = m_Entries.FindAll(e => KindOf(e) == aKind);
        var aLabels = aMedias.ConvertAll(LabelOf);
        if (aKind != m_Kind) { m_Kind = aKind; m_MediaLabel = ""; m_Reader = ""; }
        if (aMedias.Count == 0) { g.Note("（此媒材下沒有筆記）"); return; }

        string aLabel = g.Dropdown("筆記", aLabels, aLabels.Contains(m_MediaLabel) ? m_MediaLabel : aLabels[0],
                                MediaKey + "/" + aKind);   // 🩸 key 綁上游：Dropdown 的值存在 key/value，換 kind 後舊值不在新清單裡 ⇒ 標題寫「不在清單裡」而實際拿第 0 項
        if (!aLabels.Contains(aLabel)) aLabel = aLabels[0];
        if (aLabel != m_MediaLabel) { m_MediaLabel = aLabel; m_Reader = ""; }
        SCP_MediaEntry aMedia = aMedias[Math.Max(0, aLabels.IndexOf(aLabel))];

        if (aMedia.Readers.Count == 0) { g.Note("（這個 media 還沒有任何 reader root —— 還沒有人讀過）"); return; }
        string aReader = g.Dropdown("persona", aMedia.Readers, aMedia.Readers.Contains(m_Reader) ? m_Reader : aMedia.Readers[0],
                                  ReaderKey + "/" + aMedia.MediaId);   // 同上：key 綁上游（作品）
        m_Reader = aReader;

        bool aOpen = m_RecallMedia == aMedia.MediaId && m_RecallReader == aReader && m_RecallText.Length > 0;
        if (g.Button(aOpen ? "✕ 收合" : "📖 檢視心得", "reading/btn/browse-recall"))
        {
            if (aOpen) CloseRecall(); else LoadRecall(aMedia.MediaId, aReader);
        }
    }

    // ── 搜尋 ─────────────────────────────────────────────────────────
    void DrawSearch(SCP_Ui g)
    {
        m_Query = g.TextField("書名", m_Query, QueryKey);
        bool aMigrated = g.Toggle("顯示已遷移的 Archive（預設隱藏）", m_ShowMigrated, "reading/toggle/migrated");
        bool aToggled = aMigrated != m_ShowMigrated;
        m_ShowMigrated = aMigrated;
        // 切換即重搜 —— 開關與清單對不上是最誤導人的畫面
        if (g.Button("搜尋", "reading/btn/search") || (aToggled && m_Query.Trim().Length > 0)) Search();
        g.Note(m_Status);

        g.Label($"📚 搜尋結果（{m_Hits.Count}）");
        if (m_Hits.Count == 0) { g.Note("（尚未搜尋，或沒有以 metadata 標題命中的入口）"); return; }
        int aIndex = 0;
        foreach (Hit h in m_Hits)
        {
            using (g.IdScope("reading/hit/" + (aIndex++)))
            using (g.Box())
            {
                g.Label($"{h.Kind}　{h.Title}");
                g.Note(h.Detail);
                g.Note(h.Path);
                DrawHitRecallRow(g, h);
            }
        }
    }

    void DrawHitRecallRow(SCP_Ui g, Hit iHit)
    {
        if (iHit.MediaId.Length == 0)
        {
            // Archive（legacy）：說清楚為什麼不能追回，別讓人以為鈕壞了
            g.Note("（legacy —— 遷移到新格式後才可追回；遷移走 `library op=scan`／`authored_migrate`，本頁不代辦）");
            return;
        }
        if (iHit.Readers.Count == 0) { g.Note("（尚無任何 reader root —— 這個 media 還沒有人讀過）"); return; }

        m_HitReader.TryGetValue(iHit.MediaId, out string? aCur);
        string aReader = g.Dropdown("追回 persona", iHit.Readers,
                                    aCur != null && iHit.Readers.Contains(aCur) ? aCur : iHit.Readers[0],
                                    "reading/sel/hit-reader/" + iHit.MediaId);
        m_HitReader[iHit.MediaId] = aReader;
        bool aOpen = m_RecallMedia == iHit.MediaId && m_RecallReader == aReader && m_RecallText.Length > 0;
        if (g.Button(aOpen ? "✕ 收合" : "📖 追回", "reading/btn/hit-recall/" + iHit.MediaId))
        {
            if (aOpen) CloseRecall(); else LoadRecall(iHit.MediaId, aReader);
        }
    }

    void Search()
    {
        m_Hits.Clear();
        string aQuery = m_Query.Trim();
        if (aQuery.Length == 0) { m_Status = "請先輸入作品名稱。"; return; }

        // Library 先、Archive 後：新版才有追回等可操作功能，排頂端；Archive 是遷移參考，沉底。
        foreach (SCP_MediaEntry e in m_Entries)
        {
            bool aHit = Matches(aQuery, e.Title, e.MediaId, e.WorkId);
            for (int i = 0; !aHit && i < e.Aliases.Count; i++) aHit = Matches(aQuery, e.Aliases[i]);
            if (!aHit) continue;
            m_Hits.Add(new Hit
            {
                Kind = "Library（正本）",
                Title = e.Title,
                Detail = $"media: {e.MediaId}　work: {e.WorkId}　kind: {KindOf(e)}　讀者 {e.Readers.Count} 位",
                Path = SCP.Core.Library.SCP_LibraryStore.MediaRoot(m_DataRoot, e.MediaId),
                MediaId = e.MediaId,
                Readers = new List<string>(e.Readers),
            });
        }
        // ⛔ 正本命中數要單獨算：總數說不出「全是 Archive」與「有正本」的差別，而兩者的下一步相反。
        int aLibrary = m_Hits.Count;
        int aHidden = SearchArchive(aQuery);
        int aArchive = m_Hits.Count - aLibrary;
        m_Status = $"「{aQuery}」找到 {m_Hits.Count} 個入口（正本 {aLibrary} 筆／Archive {aArchive} 筆）"
                   + (aHidden > 0 ? $"（另隱藏 {aHidden} 筆已遷移 Archive —— 勾上方開關顯示）" : "")
                   + "。Archive 結果只供人工確認與遷移，不會被新流程讀取。"
                   + (aLibrary == 0 && aArchive > 0
                       ? "\n⚠ **正本 0 筆** —— 可能是「真的還沒遷移」，也可能是「查的字面不在正本的 title／aliases 裡」。"
                         + "遷移前先用 work_id／media_id 再查一次，別直接當成未遷移。"
                       : "");
    }

    /// <summary>Archive 是歷史原件：只讀每個 entry 的 book.json 標題來定位資料夾，絕不讀 chapters／characters。回傳「因已遷移而隱藏」的命中數。</summary>
    int SearchArchive(string iQuery)
    {
        string aArchive = Path.Combine(SCP_BookStore.BookNotesRoot(m_DataRoot), SCP_LibraryScan.ArchiveDirName);
        if (!Directory.Exists(aArchive)) return 0;
        HashSet<string> aMigrated = SCP_LibraryScan.LoadMigratedArchiveSlugs(m_DataRoot);
        int aHidden = 0;
        foreach (string aDir in Directory.GetDirectories(aArchive))
        {
            string aSlug = Path.GetFileName(aDir);
            string aBook = Path.Combine(aDir, "book.json");
            if (aSlug.StartsWith("_", StringComparison.Ordinal) || !File.Exists(aBook)) continue;
            SCP_JsonData? aData = SCP_LibraryIO.LoadJson(aBook, out _);
            if (aData == null) continue;
            string aTitle = aData.GetString("title", "");
            string aOriginal = aData.GetString("title_original", "");
            if (!Matches(iQuery, aTitle, aOriginal, aSlug)) continue;
            bool aIsMigrated = aMigrated.Contains(aSlug);
            if (!m_ShowMigrated && aIsMigrated) { aHidden++; continue; }
            m_Hits.Add(new Hit
            {
                Kind = aIsMigrated ? "Archive（legacy，唯讀 · ✅ 已遷移）" : "Archive（legacy，唯讀）",
                Title = aTitle.Length == 0 ? aSlug : aTitle,
                Detail = $"slug: {aSlug}　原文: {aOriginal}",
                Path = aDir,
            });
        }
        return aHidden;
    }

    static bool Matches(string iQuery, params string[] iValues)
    {
        foreach (string v in iValues)
            if (!string.IsNullOrEmpty(v) && v.IndexOf(iQuery, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
    }

    // ── 追回檢視 ─────────────────────────────────────────────────────
    void LoadRecall(string iMediaId, string iReader)
    {
        m_RecallMedia = iMediaId;
        m_RecallReader = iReader;
        m_LastBriefPath = "";   // 換檢視對象就失效 —— 開檔鈕只指向「這次」產的檔
        string? aText = SCP_LibraryRecall.RenderRecall(m_DataRoot, iMediaId, iReader, m_RecallFull, out string? aErr);
        m_RecallText = aText ?? $"✗ 追回讀取失敗：{aErr}";
    }

    void CloseRecall()
    {
        m_RecallMedia = "";
        m_RecallReader = "";
        m_RecallText = "";
        m_LastBriefPath = "";
    }

    void DrawRecall(SCP_Ui g)
    {
        g.Separator();
        g.Label($"📖 追回檢視　{m_RecallMedia} / {m_RecallReader}");
        bool aFull = g.Toggle("round 全文（預設只列索引，全文動輒數千行）", m_RecallFull, "reading/toggle/full");
        if (aFull != m_RecallFull) { m_RecallFull = aFull; LoadRecall(m_RecallMedia, m_RecallReader); }   // 切換即重讀，別讓畫面跟開關對不上
        if (g.Button("產生追回檔", "reading/btn/write-brief"))
        {
            string? aPath = SCP_LibraryRecall.WriteRecallBrief(new SCP_LettersRoot(m_LettersRoot), m_DataRoot,
                                                              m_RecallMedia, m_RecallReader, true, out string? aErr);
            m_Status = aPath != null ? $"✓ 追回檔已寫出：{aPath}" : $"✗ 追回檔寫出失敗：{aErr}";
            m_LastBriefPath = aPath ?? "";
        }
        if (m_LastBriefPath.Length > 0) g.Note("追回檔：" + m_LastBriefPath);
        if (g.Button("✕ 關閉", "reading/btn/close-recall")) { CloseRecall(); return; }

        // ⚠ id 含 media／reader／全文開關：TextArea 的值會被同 id 的輸入覆寫，id 不變＝換人看還是上一份
        g.TextArea("追回內容（唯讀檢視）", m_RecallText, $"reading/recall/{m_RecallMedia}/{m_RecallReader}/{(m_RecallFull ? "full" : "idx")}", 24);
    }
}
