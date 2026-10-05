// 區塊職責：**漫畫庫頁**（TASK-0402）—— 外部漫畫庫的作品清單、卷話明細，以及把未建檔的作品初始化成 Library media。
// 物理意義：Unity `UCL_LibraryManagePage` 漫畫區的對應。資料全部來自 `SCP_LibraryComics.ScanExternalComics`
//           （與 `senate cmd library op=comics` 同一支）；漫畫庫根走 `SenateModel.ComicRoot`（`SCP_PathId.ComicRoot`，
//           與指令同一個解析入口）。⛔ 本頁**不存路徑**：路徑設定只住路徑管理頁（TASK-0400 的設定格），這裡只讀、只導過去。
// 數值影響：掃描結果在開頁／按「重新掃描」時載一次，⛔ 不每幀掃碟。唯一的寫入是「初始化 Library media」
//           （`SCP_LibraryInit.MediaInit`，與 `op=media_init` 同一支），而且**先預覽、確認才寫**。
// 🩸 守衛：
//   ① 初始化的 persona 要**明確選**（工具列），⛔ 不代取；期待度固定 3（中性 —— 工具不替本人表態）。
//      （Unity 版寫死 persona＝apex-one、期待度＝5，兩個值都是替人表態，這裡刻意不沿用。）
//   ② 顯式 key 一律帶 `comics/` 前綴；掃描／根的錯誤要**照實說**（連同「舊快照有值」那句提示），⛔ 不畫成空清單。
//   ③ 「未建檔」「來源失聯」「已同步」三態處置不同，狀態要寫在每一列上。
// @doc-sync: <SCP_Core>/Docs~/Library.md（頁面段）
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Gui;
using SCP.Core.Library;
using SCP.Core.Paths;

namespace Senate.Cli.Pages;

public sealed class ComicLibraryPage : SCP_GuiToolPage
{
    public const string PageKey = "comics";
    const string SeriesKey = "comics/sel/series";
    const string VolumeKey = "comics/sel/volume";
    const string PersonaKey = "comics/sel/persona";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string m_LettersRoot = "";
    string m_ComicRoot = "";
    string? m_RootError;
    string? m_LoadError;
    string? m_ScanWarning;
    bool m_Loaded;
    List<SCP_ExternalComicSeries> m_Series = new List<SCP_ExternalComicSeries>();
    List<string> m_Personas = new List<string>();

    string m_SelectedSlug = "";
    string m_VolumeLabel = "";
    string m_Persona = "";
    /// <summary>非空 ＝ 正在預覽「初始化這個 slug」，等人按確認。</summary>
    string m_PendingInitSlug = "";
    string m_Status = "";

    public ComicLibraryPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => "漫畫庫";
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
        m_RootError = null;
        m_ScanWarning = null;
        m_Series = new List<SCP_ExternalComicSeries>();
        m_PendingInitSlug = "";

        // ⛔ 不寫死後備路徑：解析不到就明說
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        m_LettersRoot = m_Model.LettersRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        { m_LoadError = $"解析不到 AgentCommands 資料根（{m_Model.AgentCommandsRoot.Error ?? m_DataRoot}）—— 到「路徑管理」頁設定"; return; }
        if (string.IsNullOrEmpty(m_LettersRoot) || !Directory.Exists(m_LettersRoot))
        { m_LoadError = $"解析不到 letters 根（{m_Model.LettersRoot.Error ?? m_LettersRoot}）—— 到「路徑管理」頁設定"; return; }

        var aPool = new List<string>();
        foreach (string p in SCP.Core.Letters.SCP_PersonaProfile.PoolNames(m_LettersRoot, w => { })) aPool.Add(p);
        m_Personas = aPool;

        SCP_PathResolution aComic = m_Model.ComicRoot;
        m_ComicRoot = aComic.Value;
        if (aComic.Error != null) { m_RootError = aComic.Error; return; }
        if (string.IsNullOrEmpty(m_ComicRoot) || !Directory.Exists(m_ComicRoot))
        { m_RootError = $"外部漫畫庫根不存在或未掛載：{m_ComicRoot}"; return; }

        m_Series = SCP_LibraryComics.ScanExternalComics(m_DataRoot, m_ComicRoot, out m_ScanWarning);
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (!m_Loaded) Load();
        if (m_ComicRoot.Length > 0 && Directory.Exists(m_ComicRoot))
            OpenFolderButton(iUi, m_ComicRoot, "comics/open-root", "開啟漫畫根目錄");
        if (iUi.Button("重新掃描", "comics/reload")) Load();
        if (iUi.Button("路徑管理", "comics/to-paths")) Controller?.Push(new PathsPage(m_Model));
        if (m_LoadError != null) return;
        m_Persona = iUi.Dropdown("初始化用 persona", m_Personas, m_Personas.Contains(m_Persona) ? m_Persona : "", PersonaKey);
        iUi.Label(m_Persona.Length == 0 ? "｜（未選：只有「初始化」需要）" : $"｜初始化署名：{m_Persona}");
    }

    static string StatusMark(SCP_ComicMatchStatus iStatus) => iStatus switch
    {
        SCP_ComicMatchStatus.Synced => "🟢 已在 Library 建檔",
        SCP_ComicMatchStatus.MissingSource => "🟡 來源失聯",
        _ => "⚪ 未建檔",
    };

    protected override void DrawContent(SCP_Ui g)
    {
        if (!m_Loaded) Load();
        if (m_LoadError != null) { g.Label("⚠ " + m_LoadError); return; }

        g.Note("外部漫畫庫的路徑設定只住「路徑管理」頁（外部漫畫庫根）；本頁只讀、不存路徑。");
        if (m_RootError != null) { g.Label("⚠ " + m_RootError); return; }
        g.Label($"漫畫庫：`{m_ComicRoot}`　共 {m_Series.Count} 個系列");
        if (m_ScanWarning != null) g.Note("⚠ " + m_ScanWarning);
        if (m_Status.Length > 0) g.Note(m_Status);
        if (m_Series.Count == 0) { g.Note("（目錄內沒有找到任何漫畫子資料夾）"); return; }

        var aLabels = new List<string>();
        foreach (SCP_ExternalComicSeries s in m_Series) aLabels.Add($"{StatusMark(s.Status).Substring(0, 2)} {s.SeriesName}　({s.MediaId})");
        int aSelectedIndex = m_Series.FindIndex(s => s.Slug == m_SelectedSlug);
        if (aSelectedIndex < 0) aSelectedIndex = 0;
        string aLabel = g.Dropdown("挑選漫畫", aLabels, aLabels[aSelectedIndex], SeriesKey);
        int aIndex = aLabels.IndexOf(aLabel);
        if (aIndex < 0)
        {
            aIndex = aSelectedIndex;
            // 狀態標記屬於顯示值，初始化後會改變；把下拉快取同步成新標籤，避免停在「不在清單裡」。
            g.SetField(SeriesKey + "/value", aLabels[aIndex]);
        }
        SCP_ExternalComicSeries aSeries = m_Series[aIndex];
        if (aSeries.Slug != m_SelectedSlug)
        {
            m_SelectedSlug = aSeries.Slug;
            m_VolumeLabel = "";
            m_PendingInitSlug = "";
        }

        g.Separator();
        g.Label($"{aSeries.SeriesName}　[{StatusMark(aSeries.Status)}]　`{aSeries.MediaId}`");
        if (aSeries.RegisteredTitle.Length > 0 && aSeries.RegisteredTitle != aSeries.SeriesName)
            g.Note($"Library 書名：{aSeries.RegisteredTitle}");
        g.Label($"卷數 {aSeries.Volumes.Count} 卷　｜　總話數 {aSeries.TotalChapters} 話　｜　總圖片 {aSeries.TotalPages} 張");

        DrawVolumes(g, aSeries);
        DrawActions(g, aSeries);
    }

    void DrawVolumes(SCP_Ui g, SCP_ExternalComicSeries iSeries)
    {
        if (iSeries.Volumes.Count == 0) return;
        using (var aFold = g.Fold($"卷話明細（{iSeries.Volumes.Count} 卷）", "comics/fold/volumes", iDefaultOpen: iSeries.Volumes.Count <= 8))
        {
            if (!aFold.Open) return;
            using (g.Table("卷", "資料夾", "話", "頁"))
                foreach (SCP_ExternalComicVolume v in iSeries.Volumes)
                {
                    string aRange = v.Chapters.Count > 0
                        ? $"{v.Chapters[0]} ~ {v.Chapters[v.Chapters.Count - 1]}（{v.Chapters.Count} 話）" : "0 話";
                    g.TableRow("Vol." + v.VolumeLabel, v.FolderName, aRange, v.PageCount.ToString());
                }
        }

        var aVols = new List<string>();
        foreach (SCP_ExternalComicVolume v in iSeries.Volumes) aVols.Add($"Vol.{v.VolumeLabel}　{v.FolderName}");
        string aVol = g.Dropdown("卷", aVols, aVols.Contains(m_VolumeLabel) ? m_VolumeLabel : aVols[0],
                                VolumeKey + "/" + iSeries.Slug);   // key 綁上游（作品）：Dropdown 的值存在 key/value，換作品後舊值不在新清單裡
        m_VolumeLabel = aVol;
        SCP_ExternalComicVolume aPick = iSeries.Volumes[Math.Max(0, aVols.IndexOf(aVol))];
        OpenFolderRow(g, aPick.FolderPath);
    }

    void OpenFolderRow(SCP_Ui g, string iDir)
    {
        // 宿主沒裝開檔案總管的能力就不畫（同工具列那顆的判斷），不顯示一顆按了沒事的鈕
        if (SCP_GuiHost.RevealInFileManager == null) { g.Note("資料夾：" + iDir); return; }
        if (g.Button("開啟該卷資料夾", "comics/btn/open-volume"))
            m_Status = RevealFolder(iDir) is { Length: > 0 } aMsg ? aMsg : "";
    }

    void DrawActions(SCP_Ui g, SCP_ExternalComicSeries iSeries)
    {
        g.Separator();
        switch (iSeries.Status)
        {
            case SCP_ComicMatchStatus.Synced:
                if (g.Button("到閱讀心得頁（搜這部）", "comics/btn/notes"))
                    Controller?.Push(new ReadingNotesPage(m_Model,
                        iSeries.RegisteredTitle.Length > 0 ? iSeries.RegisteredTitle : iSeries.SeriesName));
                break;
            case SCP_ComicMatchStatus.MissingSource:
                g.Note("Library 有這部、但外部資料夾找不到 —— 可能是漫畫庫沒掛載，或資料夾改名。既有心得仍在。");
                if (g.Button("檢視既有閱讀心得", "comics/btn/notes-missing"))
                    Controller?.Push(new ReadingNotesPage(m_Model, iSeries.RegisteredTitle));
                break;
            default:
                DrawInit(g, iSeries);
                break;
        }
    }

    // ── 初始化 Library media：預覽 → 確認才寫 ─────────────────────────────
    void DrawInit(SCP_Ui g, SCP_ExternalComicSeries iSeries)
    {
        bool aPreviewing = m_PendingInitSlug == iSeries.Slug;
        if (!aPreviewing)
        {
            g.Note("這部還沒在 Library 建檔。初始化會建立 work／media 與初始讀者（先預覽，確認才寫）。");
            if (g.Button("初始化 Library media…", "comics/btn/init-preview"))
            {
                if (m_Persona.Length == 0) m_Status = "⚠ 先從工具列選「初始化用 persona」—— 工具不替人挑署名";
                else { m_PendingInitSlug = iSeries.Slug; m_Status = ""; }
            }
            return;
        }

        g.Label("📥 將建立（尚未寫入）：");
        g.Note($"work_id：`{iSeries.Slug}`");
        g.Note($"media_id：`{iSeries.MediaId}`　media_kind：`comic`");
        g.Note($"作品名：{iSeries.SeriesName}");
        g.Note($"初始讀者：{m_Persona}　期待度：3（中性 —— 工具不替本人表態，本人自己改）");
        g.Note($"落點：`{SCP_LibraryStore.MediaRoot(m_DataRoot, iSeries.MediaId)}`");
        if (g.Button("確認建檔", "comics/btn/init-confirm")) DoInit(iSeries);
        if (g.Button("取消", "comics/btn/init-cancel")) m_PendingInitSlug = "";
    }

    void DoInit(SCP_ExternalComicSeries iSeries)
    {
        string aPersona = m_Persona;
        // 與 `library` 指令同一道檢查：persona 必須是已存在的身分（有 profile/），拼錯不能長出第二棵信件樹
        if (aPersona.Length == 0 || !SCP_LibraryStore.IsValidId(aPersona)
            || !Directory.Exists(SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(m_LettersRoot), aPersona)))
        { m_Status = $"✗ persona 不存在或格式不合法：`{aPersona}`（沒有寫入任何東西）"; m_PendingInitSlug = ""; return; }

        string aLog = SCP_LibraryInit.MediaInit(new SCP_LettersRoot(m_LettersRoot), m_DataRoot,
            iSeries.Slug, iSeries.MediaId, "comic", aPersona, iSeries.SeriesName, "", "", 3,
            null, null, out string? aErr);
        m_PendingInitSlug = "";
        if (aErr != null) { m_Status = "✗ 初始化失敗：" + aErr; return; }
        m_Status = "✓ 已建檔：" + aLog.Trim().Replace('\n', ' ');
        Load();   // 重掃：狀態會從 ⚪ 變 🟢（讀回，不是假設）
    }
}
