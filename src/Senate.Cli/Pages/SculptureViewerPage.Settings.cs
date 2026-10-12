// 區塊職責：雕刻觀測頁的三份設定 class（TASK-0504）—— 匯出設定、作品篩選、作品觀測（草稿＋關聯）；畫面由 SCP_GuiInspector 自動畫。
// 物理意義：⭐ 文字模式每道指令都是新的頁面實例 ⇒ 設定 class 存成 session 欄位裡的 JSON（SCP_JsonMapper），
//          每輪先讀回、Inspector 改到了才寫回 —— 跟頁面其他欄位一樣跨指令記得。
//          Inspector 的 id 前綴是契約：`--click sculpt/export/Format=vox`、`--set sculpt/filter/Keyword=deck`。
// 數值影響：只讀寫 session 欄位，零 IO。
using System.Globalization;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Reflect;
using SCP.Core.Sculpture;
using Senate.Core;

namespace Senate.Cli.Pages;

/// <summary>匯出格式（值名＝CLI 的 <c>format=</c>）。</summary>
public enum SculptExportFormat { obj, vox }

/// <summary>觀測頁的匯出設定（成員名就是畫面上的標籤與 id）。</summary>
public sealed class SculptExportSettings
{
    public SculptExportFormat Format { get; set; } = SculptExportFormat.obj;
    /// <summary>obj 合併同色共面（merge=greedy）；關掉 ＝ 逐 voxel 面（merge=none）。vox 不吃。</summary>
    public bool MergeFaces { get; set; } = true;
    /// <summary>匯出資料夾；空 ＝ 預設（Sculpture/exports）。</summary>
    /// <remarks>⚠ Inspector 把清空的字串欄位寫成 null（參考型別）⇒ setter 收成空字串，讀的人不必每處判 null。</remarks>
    public string OutDir { get => m_OutDir; set => m_OutDir = value ?? ""; }
    string m_OutDir = "";
}

/// <summary>工具列作品下拉的篩選條件（成員名就是畫面上的標籤與 id）。</summary>
public sealed class SculptWorkFilter
{
    /// <summary>只列主作品（沒標 <c>sub</c> 的；TASK-0505）。</summary>
    public bool MainOnly { get; set; } = true;
    /// <summary>只列這位作者的；空 ＝ 不限。（null 收成空字串，理由同 <see cref="SculptExportSettings.OutDir"/>）</summary>
    public string Owner { get => m_Owner; set => m_Owner = value ?? ""; }
    /// <summary>ID 或名稱含這段字（不分大小寫）；空 ＝ 不限。</summary>
    public string Keyword { get => m_Keyword; set => m_Keyword = value ?? ""; }
    string m_Owner = "", m_Keyword = "";
}

/// <summary>個人作品的觀測設定：草稿＋關聯文字欄（成員名就是畫面上的標籤與 id）。</summary>
public sealed class SculptWorkViewSettings
{
    /// <summary>草稿模式：寫入類的刀送 draft=1（落草稿）、觀測看「作品＋草稿」。預設開（Tim 2026-10-12）。</summary>
    public bool Draft { get; set; } = true;
    /// <summary>只組這些關聯 links=（none｜all｜路徑,…；巢狀用 /；空 ＝ 照每條的開關）。</summary>
    public string Links { get => m_Links; set => m_Links = value ?? ""; }
    /// <summary>拿掉這些關聯 hide_links=（路徑,…｜self ＝ 不畫本作品自己的格）。</summary>
    public string HideLinks { get => m_HideLinks; set => m_HideLinks = value ?? ""; }
    string m_Links = "", m_HideLinks = "";
}

/// <summary>預覽的設定：檢視倍率（滾輪縮放）的範圍、渲染畫面的顯示大小（成員名就是畫面上的標籤與 id）。</summary>
public sealed class SculptPreviewSettings
{
    public const double DefaultViewScaleMin = 0.05, DefaultViewScaleMax = 50;
    /// <summary>渲染端收的上限（SenateSculptRenderer：ViewScale 需在 (0, 1000]）。</summary>
    public const double RendererViewScaleMax = 1000;
    /// <summary>渲染畫面顯示邊長（邏輯 px）的預設與範圍。</summary>
    public const int DefaultViewSide = 720, MinViewSide = 160, MaxViewSide = 4096;
    public double ViewScaleMin { get; set; } = DefaultViewScaleMin;
    public double ViewScaleMax { get; set; } = DefaultViewScaleMax;
    /// <summary>渲染畫面在頁面上的顯示邊長（邏輯 px；圖照原比例縮進這個方框）。⚠ 只管顯示，輸出解析度是手動區的 width／height。</summary>
    public int ViewSide { get; set; } = DefaultViewSide;
}

public sealed partial class SculptureViewerPage
{
    const string ExportCfgKey = P + "export", FilterCfgKey = P + "filter", ViewCfgKey = P + "view", PreviewCfgKey = P + "preview";
    const string SExportCfg = P + "state/export_cfg", SFilterCfg = P + "state/filter_cfg", SViewCfg = P + "state/view_cfg", SPreviewCfg = P + "state/preview_cfg";
    SculptPreviewSettings? m_PreviewCfg;
    string? m_PreviewCfgJson;

    /// <summary>
    /// 預覽設定 —— 滾輪在圖上（比畫設定的 Inspector 先跑）⇒ 讀的時候先套這一輪的輸入（同 <see cref="ViewCfg"/>）。
    /// <para>跨重開保留（Tim 2026-10-12）：session 那一格是這一場的真相；磁碟（SculptureWorkPrefs.PreviewJson）只在
    /// 這一場還沒有值時拿來填一次，之後 session 的 JSON 一變就寫回磁碟（⛔ 不每幀寫：比的是上次落盤的字串）。</para>
    /// </summary>
    SculptPreviewSettings PreviewCfg(SCP_Ui g)
    {
        SeedPreviewFromDisk(g);
        var cfg = LoadCfg(g, SPreviewCfg, ref m_PreviewCfg, ref m_PreviewCfgJson);
        if (ApplyPending(g, cfg, PreviewCfgKey)) SaveCfg(g, SPreviewCfg, cfg, ref m_PreviewCfgJson);
        PersistPreview(g);
        return cfg;
    }

    /// <summary>上次落盤（或從磁碟讀到）的預覽設定 JSON；null ＝ 這個頁面實例還沒讀過磁碟。</summary>
    string? m_PreviewDiskJson;

    void SeedPreviewFromDisk(SCP_Ui g)
    {
        if (m_PreviewDiskJson != null) return;
        m_PreviewDiskJson = SculptureWorkPrefs.ReadPreviewJson(m_Model.RepoRoot, out string? err);
        if (err != null) m_Message = "⚠ 預覽設定讀不了（照預設）：" + err;
        if (g.FieldValue(SPreviewCfg, "").Length == 0 && m_PreviewDiskJson.Length > 0) g.SetField(SPreviewCfg, m_PreviewDiskJson);
    }

    /// <summary>session 的預覽設定跟上次落盤的不同 ⇒ 寫回磁碟。寫失敗也記成「已處理」，⛔ 不每幀重試（說一次就好）。</summary>
    void PersistPreview(SCP_Ui g)
    {
        string json = g.FieldValue(SPreviewCfg, "");
        if (json.Length == 0 || json == m_PreviewDiskJson) return;
        m_PreviewDiskJson = json;
        var (ok, msg) = SculptureWorkPrefs.SavePreviewJson(m_Model.RepoRoot, json);
        if (!ok) m_Message = "⚠ 預覽設定存不進磁碟（這一場照用，重開會回預設）：" + msg;
    }

    /// <summary>
    /// 設定 → 實際用的倍率範圍（純函數，淨室直接測）。不合法（不是正數、超過渲染端上限、最小大於最大）⇒ 整組退回預設，
    /// 並回一句為什麼 —— ⛔ 不靜默修成「看起來能用」的值（使用者會以為自己設的生效了）。
    /// </summary>
    public static (double Min, double Max, string? Why) ViewScaleRange(SculptPreviewSettings iCfg)
    {
        double lo = iCfg.ViewScaleMin, hi = iCfg.ViewScaleMax, cap = SculptPreviewSettings.RendererViewScaleMax;
        string? why = !(lo > 0) || double.IsInfinity(lo) ? "ViewScaleMin 要是正數"
                    : !(hi > 0) || hi > cap ? "ViewScaleMax 要在 (0, " + cap + "]（渲染端的上限）"
                    : lo > hi ? "ViewScaleMin 大於 ViewScaleMax"
                    : null;
        return why == null ? (lo, hi, null) : (SculptPreviewSettings.DefaultViewScaleMin, SculptPreviewSettings.DefaultViewScaleMax, why + " ⇒ 先用預設 " + SculptPreviewSettings.DefaultViewScaleMin + "–" + SculptPreviewSettings.DefaultViewScaleMax);
    }

    /// <summary>
    /// 設定 → 實際用的顯示邊長（純函數，淨室直接測）。超出範圍 ⇒ 退回預設並回一句為什麼（⛔ 不靜默夾成「看起來能用」的值）。
    /// </summary>
    public static (float Side, string? Why) DisplaySide(SculptPreviewSettings iCfg)
    {
        int v = iCfg.ViewSide;
        if (v >= SculptPreviewSettings.MinViewSide && v <= SculptPreviewSettings.MaxViewSide) return (v, null);
        return (SculptPreviewSettings.DefaultViewSide,
                "ViewSide 要在 " + SculptPreviewSettings.MinViewSide + "–" + SculptPreviewSettings.MaxViewSide + "（收到 " + v + "）⇒ 先用預設 " + SculptPreviewSettings.DefaultViewSide);
    }

    /// <summary>滾輪一格 ×1.25（往下 ÷1.25），夾在範圍內（純函數）。</summary>
    public static double WheelViewScale(double iCurrent, double iWheel, double iMin, double iMax)
        => Math.Max(iMin, Math.Min(iMax, iCurrent * Math.Pow(1.25, iWheel)));
    /// <summary>關聯文字欄的 id（＝ Inspector 畫出來的那兩格）—— 自動渲染認它們的「編輯完成」。</summary>
    const string FLinks = ViewCfgKey + "/" + nameof(SculptWorkViewSettings.Links), FHideLinks = ViewCfgKey + "/" + nameof(SculptWorkViewSettings.HideLinks);

    SculptExportSettings? m_ExportCfg;
    string? m_ExportCfgJson;
    SculptWorkFilter? m_FilterCfg;
    string? m_FilterCfgJson;
    SculptWorkViewSettings? m_ViewCfg;
    string? m_ViewCfgJson;

    /// <summary>
    /// 從 session 欄位讀回設定（JSON 沒變就沿用同一個物件）。讀不了 ⇒ 預設值 —— 那一格存的是本頁自己寫的 JSON，
    /// 壞掉只會是舊版或手改，回預設比卡住整頁好，而且下一次改設定就會整份覆寫。
    /// </summary>
    internal static T LoadCfg<T>(SCP_Ui g, string iStateId, ref T? ioCache, ref string? ioJson) where T : class, new()
    {
        string json = g.FieldValue(iStateId, "");
        if (ioCache != null && json == ioJson) return ioCache;
        var cfg = new T();
        if (json.Length > 0)
        {
            try { SCP_JsonMapper.Populate(cfg, SCP_JsonData.Parse(json)); }
            catch (Exception) { cfg = new T(); }
        }
        ioCache = cfg; ioJson = json;
        return cfg;
    }

    internal static void SaveCfg<T>(SCP_Ui g, string iStateId, T iCfg, ref string? ioJson) where T : class
    {
        string json = SCP_JsonMapper.ToJson(iCfg).ToJson(false);
        g.SetField(iStateId, json);
        ioJson = json;
    }

    SculptExportSettings ExportCfg(SCP_Ui g) => LoadCfg(g, SExportCfg, ref m_ExportCfg, ref m_ExportCfgJson);
    SculptWorkFilter FilterCfg(SCP_Ui g) => LoadCfg(g, SFilterCfg, ref m_FilterCfg, ref m_FilterCfgJson);

    /// <summary>觀測設定 —— 會影響渲染 ⇒ 讀的時候先把這一輪的輸入套進去（見 <see cref="ApplyPending"/>）。</summary>
    SculptWorkViewSettings ViewCfg(SCP_Ui g)
    {
        var cfg = LoadCfg(g, SViewCfg, ref m_ViewCfg, ref m_ViewCfgJson);
        if (ApplyPending(g, cfg, ViewCfgKey)) SaveCfg(g, SViewCfg, cfg, ref m_ViewCfgJson);
        return cfg;
    }

    /// <summary>
    /// 把這一輪 session 裡的勾選與欄位值先套進設定物件（只讀欄位，不畫任何東西）。
    /// <para>為什麼要有：渲染圖在最上面 ⇒ 自動渲染比畫設定的 Inspector 先跑；不先套的話，`--set` 那一輪用舊值畫，
    /// 之後也不會補（文字模式每道指令一個新實例，「參數變了」只在同一個實例裡比得出來）。</para>
    /// <para>解析走 Inspector 同一支 <c>SCP_Reflect.TryParse</c>；解析不了 ⇒ 不套（Inspector 畫的時候會說為什麼）。
    /// enum 鈕的點擊只有 Inspector 認得 ⇒ 不在這裡（觀測設定沒有 enum）。</para>
    /// </summary>
    internal static bool ApplyPending(SCP_Ui g, object iCfg, string iKey)
    {
        bool changed = false;
        foreach (SCP_MemberSchema m in SCP_Reflect.SchemaOf(iCfg).Members)
        {
            if (!m.CanWrite) continue;
            string id = iKey + "/" + m.Name;
            object? cur = m.Get(iCfg);
            if (m.Kind == SCP_ValueKind.Bool)
            {
                bool old = cur is bool b && b, now = g.ToggleValue(id, old);
                if (now != old && m.TrySet(iCfg, now, out _)) changed = true;
            }
            else if (m.Kind is SCP_ValueKind.Text or SCP_ValueKind.Integer or SCP_ValueKind.Decimal)
            {
                string old = Convert.ToString(cur, CultureInfo.InvariantCulture) ?? "", now = g.FieldValue(id, old);
                if (now != old && SCP_Reflect.TryParse(m.Type, now, m.IsNullable || !m.Type.IsValueType, out object? v, out _)
                    && m.TrySet(iCfg, v, out _)) changed = true;
            }
        }
        return changed;
    }

    /// <summary>換作品 ⇒ 關聯文字欄清空（那是上一件的關聯名）：物件、session JSON、Inspector 欄位三處一起清，否則欄位裡的舊字下一輪會被套回來。</summary>
    void ClearViewLinks(SCP_Ui g)
    {
        var cfg = ViewCfg(g);
        cfg.Links = ""; cfg.HideLinks = "";
        SaveCfg(g, SViewCfg, cfg, ref m_ViewCfgJson);
        g.SetField(FLinks, ""); g.SetField(FHideLinks, "");
    }

    /// <summary>Inspector 畫一份設定；改到了就寫回 session。回傳畫不出來／寫不進去的紀錄。</summary>
    static List<string> DrawCfg<T>(SCP_Ui g, T iCfg, string iKey, string iStateId, ref string? ioJson) where T : class
    {
        SCP_InspectorResult r = SCP_GuiInspector.Draw(g, iCfg, iKey);
        if (r.Changed) SaveCfg(g, iStateId, iCfg, ref ioJson);
        return r.Notes;
    }

    /// <summary>
    /// 篩選作品清單（純函數，淨室直接測）：主作品＝沒標 <c>sub</c>（⛔ 不看 parent_work —— 那是計費的任務歸屬，TASK-0505）；
    /// 作者完全相符（不分大小寫）；關鍵字比 ID、名稱與標籤。
    /// </summary>
    public static List<SCP_SculptWork> FilterWorks(IReadOnlyList<SCP_SculptWork> iWorks, SculptWorkFilter iFilter)
    {
        string owner = iFilter.Owner.Trim(), key = iFilter.Keyword.Trim();
        var outList = new List<SCP_SculptWork>();
        foreach (var w in iWorks)
        {
            if (iFilter.MainOnly && w.IsSub) continue;
            if (owner.Length > 0 && !w.owner.Equals(owner, StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Length > 0 && w.id.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0
                && w.title.IndexOf(key, StringComparison.OrdinalIgnoreCase) < 0
                && !w.tags.Exists(t => t.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            outList.Add(w);
        }
        return outList;
    }
}
