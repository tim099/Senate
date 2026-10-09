// 區塊職責：後台「球面繪製」頁（prototype，TASK-0467）—— 預覽可繪製球面、用經緯度畫點／線／多邊形／油漆桶、Undo、改底色。
// 物理意義：寫入一律走 `cmd globe`（同一支 Cmd，in-process Dispatch）—— 頁面 ⛔ 不自己改格子；
//          預覽在 in-process 讀狀態後 CPU 渲染成 RGBA，**直接放進記憶體影像登記處**（SCP_GuiImageStore）給視窗變貼圖 ——
//          ⛔ 不經過檔案：view.png 被別的程式鎖住（預覽軟體／同步工具／防毒）時，舊做法寫不進去、畫面停在舊圖而沒有任何一層喊。
// 數值影響：每次寫入成功、或視角參數變了，就重渲一張；渲染在背景跑，畫面先留上一張。
//          TopBar 的「輸出」也走 `cmd globe op=render export=1`（檔名與資料夾由 SCP_GlobePaths 決定），在背景跑、不擋畫面。
// GPU（TASK-0470）：視窗宿主登記了球面畫家（SCP_GuiGpuViews.CanPaint）且「GPU 透視」開著 ⇒ 預覽改走 `gpu:`：
//          頁面只放場景（狀態＋透視鏡頭＋疊圖開關），像素由視窗的 GL 畫、格子快取在 GPU 上（只上傳變了的分塊），⛔ 不讀回 CPU。
//          宿主不能畫（文字模式、SENATE_GLOBE_GPU=off）或回報失敗 ⇒ 退回上面那條 CPU 預覽，並把原因印在頁面上。
//          ⚠ CLI 的 render／export 不走這裡，仍是 CPU（格子回讀的正本）。
#nullable enable
using System.Globalization;
using System.Threading.Tasks;
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Globe;
using SCP.Core.Gui;
using SCP.Core.Paths;

namespace Senate.Cli.Pages;

public sealed class GlobeViewerPage : SCP_GuiToolPage
{
    public const string PageKey = "globe";
    const float ViewSide = 720f;
    const string P = "globe/";
    const string FLat = P + "f/center_lat", FLon = P + "f/center_lon", FZoom = P + "f/zoom", FGrat = P + "f/graticule";
    const string TSeams = P + "t/seams", TGrat = P + "t/graticule", TZones = P + "t/zones", TGpu = P + "t/gpu";
    const string FZoneId = P + "f/zone_id", FZoneTitle = P + "f/zone_title", FZoneBbox = P + "f/zone_bbox", FZoneStatus = P + "f/zone_status";
    const string FColor = P + "f/color", FBase = P + "f/base";
    const string FPLat = P + "f/p_lat", FPLon = P + "f/p_lon", FRadius = P + "f/radius", FWidth = P + "f/width", FMax = P + "f/max_cells";
    const string FPoints = P + "f/points", FPersona = P + "f/persona";
    const string FExportSize = P + "f/export_size", FMapWidth = P + "f/map_width";
    const string SLog = P + "state/log";

    static readonly Dictionary<string, string> s_Defaults = new(StringComparer.Ordinal)
    {
        [FLat] = "23.7", [FLon] = "121", [FZoom] = "1", [FGrat] = "10",
        [FColor] = "#2E8B57", [FBase] = "#0049AA",
        [FPLat] = "23.7", [FPLon] = "121", [FRadius] = "0", [FWidth] = "0", [FMax] = "200000",
        [FPoints] = "", [FPersona] = "Tim",
        [FExportSize] = "2048", [FMapWidth] = "4096",
    };

    /// <summary>台灣本島輪廓（粗略，逆時針；prototype 試畫用）。</summary>
    public const string TaiwanOutline =
        "25.30,121.54;25.15,121.75;25.01,122.00;24.70,121.85;24.58,121.87;23.98,121.62;23.50,121.50;"
        + "23.10,121.40;22.75,121.17;22.40,120.95;21.90,120.85;21.93,120.72;22.37,120.60;22.62,120.27;"
        + "23.00,120.10;23.45,120.15;24.00,120.40;24.25,120.52;24.80,120.92;25.05,121.10;25.18,121.40";

    readonly SenateModel m_Model;
    string? m_Message;
    string m_Status = "";
    bool m_Initialized;
    List<string> m_ZoneLines = new();
    bool m_Dirty = true;
    Task<string>? m_Render;
    string m_RenderedSig = "", m_RenderingSig = "";
    int m_Version;   // 每次寫入成功 +1 ⇒ 簽名變了 ⇒ 重渲

    public GlobeViewerPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "球面繪製";
    public override string? MenuGroup => "內容";

    public override void OnPush() { base.OnPush(); m_Dirty = true; }

    string DataRoot => m_Model.AgentCommandsRoot.Value ?? "";
    /// <summary>預覽圖在記憶體影像登記處的 key（Image 節點用 <see cref="SCP_GuiImageStore.Ref"/> 取它）。</summary>
    const string ViewKey = "globe/view";

    // ── 工具列 ─────────────────────────────────────────────
    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", P + "btn/reload")) { m_Dirty = true; m_Version++; }
        if (iUi.Button("↶ Undo", P + "btn/undo")) Run(iUi, "Undo", new() { ["op"] = "undo", ["persona"] = V(iUi, FPersona) });
        if (m_Export == null)
        {
            if (iUi.Button("輸出目前視角", P + "btn/export-view")) StartExport(iUi, "輸出目前視角", ExportArgs(iUi, false));
            if (iUi.Button("輸出世界地圖", P + "btn/export-map")) StartExport(iUi, "輸出世界地圖", ExportArgs(iUi, true));
        }
        string aRoot = DataRoot;
        SCP_GlobePaths? aPaths = aRoot.Length > 0 ? new SCP_GlobePaths(new SCP_DataRoot(aRoot)) : null;
        OpenFolderButton(iUi, aPaths?.Root, P + "btn/open-dir");
        OpenFolderButton(iUi, aPaths?.ExportsDir, P + "btn/open-exports", "開啟輸出資料夾");
    }

    // ── 輸出（TopBar）──────────────────────────────────────
    /// <summary>背景跑的那一次輸出；跑完前 TopBar 不給再按（兩張同時跑只是多吃一倍記憶體）。</summary>
    Task<SCP_CmdResult>? m_Export;
    string m_ExportLabel = "";

    /// <summary>用畫面上同一組開關（經緯線／施工區框線／面接縫）；視角輸出另外帶中心與 zoom。</summary>
    Dictionary<string, string> ExportArgs(SCP_Ui g, bool iMap)
    {
        var a = new Dictionary<string, string>
        {
            ["op"] = "render", ["export"] = "1",
            ["projection"] = iMap ? SCP_GlobeView.ProjEquirect : SCP_GlobeView.ProjOrtho,
            ["size"] = V(g, iMap ? FMapWidth : FExportSize),
            ["graticule"] = g.ToggleValue(TGrat, true) ? V(g, FGrat) : "0",
            ["zones"] = g.ToggleValue(TZones, true) ? "1" : "0",
            ["seams"] = g.ToggleValue(TSeams) ? "1" : "0",
        };
        if (!iMap) { a["center"] = V(g, FLat) + "," + V(g, FLon); a["zoom"] = V(g, FZoom); }
        return a;
    }

    void StartExport(SCP_Ui g, string iLabel, Dictionary<string, string> iArgs)
    {
        m_ExportLabel = iLabel;
        Func<SCP_CmdResult> aJob = () =>
        {
            try { return Dispatch(iArgs); }
            catch (Exception e) { return SCP_CmdResult.Fail(1, "炸了：" + e.GetType().Name + ": " + e.Message); }
        };
        if (SCP_GuiHost.RedrawsContinuously) { m_Export = Task.Run(aJob); m_Message = iLabel + "中…"; }
        else FinishExport(g, aJob());
    }

    void PumpExport(SCP_Ui g)
    {
        if (m_Export == null || !m_Export.IsCompleted) return;
        SCP_CmdResult r = m_Export.Result;   // aJob 自己接住例外 ⇒ 這裡不會丟
        m_Export = null;
        FinishExport(g, r);
    }

    /// <summary>輸出不改格子 ⇒ ⛔ 不動 m_Version（不然預覽會白白重渲一張）。</summary>
    void FinishExport(SCP_Ui g, SCP_CmdResult r)
    {
        g.SetField(SLog, $"[{DateTime.Now:HH:mm:ss} {m_ExportLabel}] exit {r.ExitCode}\n{string.Join("\n", r.Lines)}");
        m_Message = (r.ExitCode == 0 ? "" : "✗ ") + (r.Lines.FirstOrDefault(l => l.Trim().Length > 0) ?? m_ExportLabel);
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (m_Dirty) ReloadStatus();
        PumpExport(g);
        if (m_Message != null) g.Note(m_Message);
        if (m_Status.Length > 0) g.Label(m_Status);
        if (!m_Initialized)
        {
            g.Note("球面還沒建立。建立後每面 2048×2048（6 面約 2516 萬格，約 4.9 km／格），沒畫過的格子顯示底色。");
            if (g.Button("建立球面（N=2048，底色海水藍）", P + "btn/init")) Run(g, "建立球面", new() { ["op"] = "init" });
            DrawLog(g);
            return;
        }
        DrawView(g);
        DrawPaint(g);
        DrawLog(g);
    }

    void ReloadStatus()
    {
        m_Dirty = false;
        if (DataRoot.Length == 0) { m_Status = "找不到 AgentCommands 資料根 —— 到「路徑管理」頁設定"; m_Initialized = false; return; }
        SCP_CmdResult r = Dispatch(new() { ["op"] = "status" });
        m_Initialized = r.ExitCode == 0;
        m_ZoneLines = m_Initialized ? Dispatch(new() { ["op"] = "zone", ["sub"] = "list" }).Lines : new();
        m_Status = string.Join("\n", r.Lines.Where(l => l.Trim().Length > 0));
    }

    // ── 視角與預覽 ─────────────────────────────────────────
    void DrawView(SCP_Ui g)
    {
        using (var aFold = g.Fold("視角", P + "fold/view", iDefaultOpen: false))
        {
            if (aFold.Open)
            {
                Field(g, "中心緯度", FLat);
                Field(g, "中心經度", FLon);
                Field(g, "zoom（1＝整個半球）", FZoom);
                Field(g, "經緯線間隔（度）", FGrat);
                g.Note("TopBar 的輸出用目前的視角與下面三個開關；圖存在球面資料根的 exports/。");
                Field(g, "輸出目前視角：邊長（px，16–4096）", FExportSize);
                Field(g, "輸出世界地圖：寬（px，16–8192；高＝寬／2；8192＝赤道一格一像素）", FMapWidth);
            }
        }
        using (g.Row())
        {
            g.Toggle("經緯線", true, TGrat);
            g.Toggle("施工區框線", true, TZones);
            g.Toggle("面接縫", false, TSeams);
            if (GpuPainterAvailable) g.Toggle("GPU 透視（即時）", true, TGpu);
        }
        using (g.Row())
        {
            if (g.Button("◀ 西 15°", P + "btn/w")) Nudge(g, FLon, -15 / Zoom(g));
            if (g.Button("東 15° ▶", P + "btn/e")) Nudge(g, FLon, 15 / Zoom(g));
            if (g.Button("▲ 北 15°", P + "btn/n")) Nudge(g, FLat, 15 / Zoom(g));
            if (g.Button("▼ 南 15°", P + "btn/s")) Nudge(g, FLat, -15 / Zoom(g));
            if (g.Button("放大 ×2", P + "btn/zin")) g.SetField(FZoom, F(Zoom(g) * 2));
            if (g.Button("縮小 ÷2", P + "btn/zout")) g.SetField(FZoom, F(Math.Max(0.5, Zoom(g) / 2)));
            if (g.Button("台灣特寫", P + "btn/tw")) { g.SetField(FLat, "23.7"); g.SetField(FLon, "121"); g.SetField(FZoom, "12"); }
            if (g.Button("整顆", P + "btn/whole")) g.SetField(FZoom, "1");
        }
        g.Note("在圖上拖曳＝轉動地球、滾輪＝縮放（視窗模式）。");
        if (DrawGpuView(g)) return;
        PumpRender(g);
        if (SCP_GuiImageStore.Has(ViewKey))
        {
            SCP_GuiPointer? aPtr = g.ImageInteractive(SCP_GuiImageStore.Ref(ViewKey), ViewSide, "球面預覽", P + "img/view");
            if (aPtr != null) ApplyPointer(g, aPtr);
        }
        string aSig = ViewSig(g);
        if (aSig != m_RenderedSig && aSig != m_RenderingSig) StartRender(g, aSig);
        if (m_Render != null) g.Note("渲染中…（畫面是上一張）");
    }

    /// <summary>
    /// 上一次讀好的狀態（版本＝m_Version）；背景執行緒寫、繪圖執行緒讀，⛔ 只整份換、不就地改。
    /// ⚠ 用參考型別而不是 nullable tuple：多欄位的 struct 跨執行緒賦值不保證原子，讀到一半新一半舊的不會報錯。
    /// </summary>
    sealed record StateSnap(int Version, SCP_GlobeState State, List<SCP_GlobeZone> Zones);
    volatile StateSnap? m_StateCache;

    /// <summary>拖曳中（低解析度快速重渲）；放開後簽名變了 ⇒ 補一張全解析度。</summary>
    bool m_Dragging;

    // ── GPU 即時預覽（TASK-0470）──────────────────────────
    public const string GpuKey = "globe/gpu-view";
    static bool GpuPainterAvailable => SCP_GuiGpuViews.CanPaint(typeof(SCP_GlobeGpuScene));
    Task? m_StateLoad;
    string? m_StateLoadError;
    string m_GpuSig = "";
    long m_GpuPut;

    /// <summary>
    /// 走 GPU 就畫完回 true；不走（宿主不能畫／開關關著／這一幀的場景被宿主回報失敗）回 false ⇒ 呼叫端走 CPU 預覽。
    /// 不走的原因印在頁面上（「GPU 壞了」跟「本來就沒有 GPU」要分得開）。
    /// </summary>
    bool DrawGpuView(SCP_Ui g)
    {
        if (!GpuPainterAvailable) { g.Note("這個宿主沒有 GPU 畫面（文字模式，或環境變數 SENATE_GLOBE_GPU=off）⇒ 用 CPU 正交預覽。"); return false; }
        if (!g.ToggleValue(TGpu, true)) { g.Note("GPU 透視關著 ⇒ 用 CPU 正交預覽。"); return false; }
        EnsureStateLoaded();
        if (m_StateLoadError != null) { g.Note("讀球面狀態失敗：" + m_StateLoadError); return false; }
        var aCached = m_StateCache;
        if (aCached == null) { g.Note("讀取球面狀態中…"); return true; }

        if (!TryD(V(g, FLat), out double la) || !TryD(V(g, FLon), out double lo) || !TryD(V(g, FZoom), out double z) || !TryD(V(g, FGrat), out double gr))
        { g.Note("視角欄位要是數字"); return true; }
        var cam = new SCP_GlobeCamera
        {
            CenterLat = Math.Max(-89.9, Math.Min(89.9, la)), CenterLon = lo,
            Zoom = Math.Max(SCP_GlobeCamera.MinZoom, Math.Min(SCP_GlobeCamera.MaxZoom, z)),
            Width = (int)ViewSide, Height = (int)ViewSide,
        };
        bool aZones = g.ToggleValue(TZones, true);
        double aGrat = g.ToggleValue(TGrat, true) ? gr : 0;
        bool aSeams = g.ToggleValue(TSeams);
        string aSig = string.Join("|", F(cam.CenterLat), F(cam.CenterLon), F(cam.Zoom), F(aGrat), aZones ? "1" : "0", aSeams ? "1" : "0", aCached.Version.ToString(CultureInfo.InvariantCulture));
        if (aSig != m_GpuSig)
        {
            var aScene = new SCP_GlobeGpuScene(aCached.State, aCached.Version, cam, aGrat, aSeams,
                                               aZones ? aCached.Zones : new List<SCP_GlobeZone>());
            m_GpuPut = SCP_GuiGpuViews.Put(GpuKey, aScene, cam.Width, cam.Height);
            m_GpuSig = aSig;
        }
        // 宿主對**這一版**回報失敗 ⇒ 這一幀退回 CPU，原因照印（換了視角／狀態就會重試）
        if (SCP_GuiGpuViews.TryGetStatus(GpuKey, out SCP_GuiGpuStatus aSt) && aSt.Version == m_GpuPut && aSt.Error != null)
        {
            g.Note("GPU 預覽失敗 ⇒ 退回 CPU：" + aSt.Error);
            return false;
        }
        SCP_GuiPointer? aPtr = g.ImageInteractive(SCP_GuiGpuViews.Ref(GpuKey), ViewSide, "球面預覽（GPU）", P + "img/gpu");
        if (aPtr != null) ApplyPointer(g, aPtr);
        if (SCP_GuiGpuViews.TryGetStatus(GpuKey, out aSt) && aSt.Error == null) g.Note(aSt.Info);
        if (m_StateLoad != null) g.Note("重新讀取球面狀態中…（畫面是上一版）");
        return true;
    }

    /// <summary>狀態版本變了就在背景重讀（GPU 與 CPU 兩條路共用 m_StateCache；⛔ 只整份換）。</summary>
    void EnsureStateLoaded()
    {
        if (m_StateLoad != null)
        {
            if (!m_StateLoad.IsCompleted) return;
            try { m_StateLoad.Wait(); m_StateLoadError = null; }
            catch (Exception e) { m_StateLoadError = (e.InnerException ?? e).Message; }
            m_StateLoad = null;
        }
        if (m_StateCache != null && m_StateCache.Version == m_Version) return;
        if (m_LoadFailedVersion == m_Version) return;   // 同一版讀失敗過 ⇒ 不每幀重試（按「重新讀取」會換版本）
        int aVersion = m_Version;
        string aRoot = DataRoot;
        Action aJob = () =>
        {
            var store = new SCP_GlobeStore(new SCP_GlobePaths(new SCP_DataRoot(aRoot)));
            try { m_StateCache = new StateSnap(aVersion, store.Load(), new SCP_GlobeZones(store.Paths).List()); }
            catch { m_LoadFailedVersion = aVersion; throw; }
        };
        if (SCP_GuiHost.RedrawsContinuously) m_StateLoad = Task.Run(aJob);
        else
        {
            try { aJob(); m_StateLoadError = null; } catch (Exception e) { m_StateLoadError = e.Message; }
        }
    }
    int m_LoadFailedVersion = int.MinValue;

    void ApplyPointer(SCP_Ui g, SCP_GuiPointer p)
    {
        m_Dragging = p.Dragging;
        if (!TryD(V(g, FLat), out double la)) la = 0;
        if (!TryD(V(g, FLon), out double lo)) lo = 0;
        double z = Zoom(g);
        if (p.DragX != 0 || p.DragY != 0)
        {
            GlobeDrag(la, lo, z, p.DragX, p.DragY, out la, out lo);
            g.SetField(FLat, F(la)); g.SetField(FLon, F(lo));
        }
        if (p.Wheel != 0) g.SetField(FZoom, F(GlobeWheelZoom(z, p.Wheel)));
    }

    /// <summary>
    /// 拖曳換算（純函式，selftest 驗）：拖過整張圖的寬 ＝ 轉過畫面看得到的直徑（2/zoom 個球半徑 ≈ 弧度）。
    /// 往右拖 ⇒ 地球跟著往右轉 ⇒ 中心往西；往下拖 ⇒ 中心往北。經度照緯度放大，讓手感在高緯也一致。
    /// </summary>
    public static void GlobeDrag(double iLat, double iLon, double iZoom, double iDx, double iDy, out double oLat, out double oLon)
    {
        double k = 2.0 / Math.Max(iZoom, 0.01) * 180 / Math.PI;
        oLat = Math.Max(-89, Math.Min(89, iLat + iDy * k));
        double lo = iLon - iDx * k / Math.Max(Math.Cos(iLat * Math.PI / 180), 0.2);
        while (lo > 180) lo -= 360;
        while (lo < -180) lo += 360;
        oLon = lo;
    }

    /// <summary>滾輪一格 ×1.25（往上捲放大），夾在 0.5..400。</summary>
    public static double GlobeWheelZoom(double iZoom, double iWheel) => Math.Max(0.5, Math.Min(400, iZoom * Math.Pow(1.25, iWheel)));

    string ViewSig(SCP_Ui g) => string.Join("|", V(g, FLat), V(g, FLon), V(g, FZoom), V(g, FGrat), g.ToggleValue(TGrat, true) ? "1" : "0", g.ToggleValue(TZones, true) ? "1" : "0", g.ToggleValue(TSeams) ? "1" : "0", m_Dragging ? "drag" : "full", m_Version.ToString(CultureInfo.InvariantCulture));

    void StartRender(SCP_Ui g, string iSig)
    {
        if (m_Render != null) return;
        if (!TryD(V(g, FLat), out double la) || !TryD(V(g, FLon), out double lo) || !TryD(V(g, FZoom), out double z) || !TryD(V(g, FGrat), out double gr))
        { m_Message = "視角欄位要是數字"; m_RenderedSig = iSig; return; }
        var v = new SCP_GlobeView { CenterLat = Math.Max(-90, Math.Min(90, la)), CenterLon = lo, Zoom = z, Graticule = g.ToggleValue(TGrat, true) ? gr : 0, Seams = g.ToggleValue(TSeams), Size = m_Dragging ? 360 : 720 };
        bool aZones = g.ToggleValue(TZones, true);
        int aVersion = m_Version;
        string aRoot = DataRoot;
        m_RenderingSig = iSig;
        Func<string> aJob = () =>
        {
            var store = new SCP_GlobeStore(new SCP_GlobePaths(new SCP_DataRoot(aRoot)));
            // 拖曳時每幀都在渲染 ⇒ 狀態只在版本變了（寫入成功／重新讀取）才重讀，其餘沿用上一份（渲染只讀不寫）
            StateSnap? aCached = m_StateCache;
            if (aCached == null || aCached.Version != aVersion)
                aCached = m_StateCache = new StateSnap(aVersion, store.Load(), new SCP_GlobeZones(store.Paths).List());
            if (aZones) v.Zones = aCached.Zones;
            byte[] rgba = SCP_GlobeRender.RenderRgba(aCached.State, v);
            SCP_GuiImageStore.Put(ViewKey, rgba, v.Width, v.Height);   // 整份換、不就地改（渲染執行緒與繪圖執行緒各讀各的）
            return "";
        };
        if (SCP_GuiHost.RedrawsContinuously) m_Render = Task.Run(aJob);
        else
        {
            try { aJob(); } catch (Exception e) { m_Message = "渲染失敗：" + e.Message; }
            m_RenderedSig = iSig; m_RenderingSig = "";
        }
    }

    void PumpRender(SCP_Ui g)
    {
        if (m_Render == null || !m_Render.IsCompleted) return;
        try { m_Render.Wait(); }
        catch (Exception e) { m_Message = "渲染失敗：" + (e.InnerException ?? e).Message; }
        m_Render = null;
        m_RenderedSig = m_RenderingSig;
        m_RenderingSig = "";
    }

    // ── 繪製 ───────────────────────────────────────────────
    void DrawPaint(SCP_Ui g)
    {
        using (var aFold = g.Fold("畫筆", P + "fold/paint", iDefaultOpen: true))
        {
            if (aFold.Open)
            {
                Field(g, "persona", FPersona);
                Field(g, "顏色（#RRGGBB 或 r,g,b 全彩；empty＝擦回底色）", FColor);
                g.Note("點與油漆桶用下面的經緯度；線與多邊形用點列（lat,lon;lat,lon;…，一行一點也可以）。畫錯了按上方 ↶ Undo。");
                Field(g, "緯度", FPLat);
                Field(g, "經度", FPLon);
                Field(g, "點的半徑（格）", FRadius);
                Field(g, "油漆桶上限（格）", FMax);
                using (g.Row())
                {
                    if (g.Button("畫點", P + "btn/point"))
                        Run(g, "畫點", Common(g, "point", new() { ["lat"] = V(g, FPLat), ["lon"] = V(g, FPLon), ["radius"] = V(g, FRadius) }));
                    if (g.Button("油漆桶（從這個經緯度）", P + "btn/fill"))
                        Run(g, "油漆桶", Common(g, "fill", new() { ["lat"] = V(g, FPLat), ["lon"] = V(g, FPLon), ["max_cells"] = V(g, FMax) }));
                }
                g.TextArea("點列", Def(FPoints), FPoints, 6);
                Field(g, "線寬半徑（格）", FWidth);
                using (g.Row())
                {
                    if (g.Button("畫線", P + "btn/line"))
                        Run(g, "畫線", Common(g, "line", new() { ["points"] = V(g, FPoints), ["width"] = V(g, FWidth) }));
                    if (g.Button("多邊形填色", P + "btn/polygon"))
                        Run(g, "多邊形填色", Common(g, "polygon", new() { ["points"] = V(g, FPoints) }));
                    if (g.Button("點列＝台灣輪廓", P + "btn/tw-outline")) g.SetField(FPoints, TaiwanOutline.Replace(";", ";\n"));
                }
                g.Note("橡皮擦：擦回底色（大海），參數同上面那一種畫法；擦錯了一樣可以 Undo。");
                using (g.Row())
                {
                    if (g.Button("擦點", P + "btn/erase-point"))
                        Run(g, "擦點", Erase(g, "point", new() { ["lat"] = V(g, FPLat), ["lon"] = V(g, FPLon), ["radius"] = V(g, FRadius) }));
                    if (g.Button("擦線", P + "btn/erase-line"))
                        Run(g, "擦線", Erase(g, "line", new() { ["points"] = V(g, FPoints), ["width"] = V(g, FWidth) }));
                    if (g.Button("擦多邊形", P + "btn/erase-polygon"))
                        Run(g, "擦多邊形", Erase(g, "polygon", new() { ["points"] = V(g, FPoints) }));
                    if (g.Button("擦連通區（從這個經緯度）", P + "btn/erase-fill"))
                        Run(g, "擦連通區", Erase(g, "fill", new() { ["lat"] = V(g, FPLat), ["lon"] = V(g, FPLon), ["max_cells"] = V(g, FMax) }));
                }
            }
        }
        DrawZones(g);
        using (var aFold = g.Fold("底色", P + "fold/base", iDefaultOpen: false))
        {
            if (aFold.Open)
            {
                g.Note("底色＝沒畫過的格子顯示的顏色（海水）。改它不動任何格子。");
                Field(g, "底色（#RRGGBB）", FBase);
                if (g.Button("套用底色", P + "btn/base")) Run(g, "改底色", new() { ["op"] = "base", ["color"] = V(g, FBase) });
            }
        }
    }

    Dictionary<string, string> Erase(SCP_Ui g, string iShape, Dictionary<string, string> iArgs)
    {
        iArgs["op"] = "erase";
        iArgs["shape"] = iShape;
        iArgs["persona"] = V(g, FPersona);
        return iArgs;
    }

    // ── 施工區 ─────────────────────────────────────────────
    void DrawZones(SCP_Ui g)
    {
        using var aFold = g.Fold("施工區（誰在哪裡畫什麼；可重疊、不擋人）", P + "fold/zones", iDefaultOpen: true);
        if (!aFold.Open) return;
        foreach (string l in m_ZoneLines) g.Label(l);
        Field(g, "施工區 id（小寫英數、-、_）", FZoneId);
        Field(g, "名稱（例：創造日本）", FZoneTitle);
        Field(g, "範圍 南,西,北,東（度；西 > 東 ＝ 跨 180°）", FZoneBbox);
        Field(g, "狀態（active／paused／done，更新時用）", FZoneStatus);
        using (g.Row())
        {
            string aId = V(g, FZoneId), aMe = V(g, FPersona);
            if (g.Button("開施工區", P + "btn/zone-add"))
                Run(g, "開施工區", new() { ["op"] = "zone", ["sub"] = "add", ["persona"] = aMe, ["id"] = aId, ["title"] = V(g, FZoneTitle), ["bbox"] = V(g, FZoneBbox) });
            if (g.Button("加入", P + "btn/zone-join"))
                Run(g, "加入施工區", new() { ["op"] = "zone", ["sub"] = "join", ["persona"] = aMe, ["id"] = aId });
            if (g.Button("更新（名稱／範圍／狀態）", P + "btn/zone-update"))
            {
                var a = new Dictionary<string, string> { ["op"] = "zone", ["sub"] = "update", ["persona"] = aMe, ["id"] = aId };
                if (V(g, FZoneTitle).Length > 0) a["title"] = V(g, FZoneTitle);
                if (V(g, FZoneBbox).Length > 0) a["bbox"] = V(g, FZoneBbox);
                if (V(g, FZoneStatus).Length > 0) a["status"] = V(g, FZoneStatus);
                Run(g, "更新施工區", a);
            }
            if (g.Button("看這一區", P + "btn/zone-goto") && SCP_GlobeZones.TryParseBbox(V(g, FZoneBbox), out double s, out double w, out double n, out double e, out _))
            {
                double lon = w <= e ? (w + e) / 2 : (w + e + 360) / 2;
                if (lon > 180) lon -= 360;
                double span = Math.Max(n - s, (w <= e ? e - w : e + 360 - w) * Math.Cos((s + n) / 2 * Math.PI / 180));
                g.SetField(FLat, F((s + n) / 2)); g.SetField(FLon, F(lon));
                g.SetField(FZoom, F(Math.Max(1, Math.Min(200, 80 / Math.Max(span, 0.1)))));
            }
        }
    }

    Dictionary<string, string> Common(SCP_Ui g, string iOp, Dictionary<string, string> iArgs)
    {
        iArgs["op"] = iOp;
        iArgs["persona"] = V(g, FPersona);
        iArgs["color"] = V(g, FColor);
        return iArgs;
    }

    // ── Cmd ────────────────────────────────────────────────
    SCP_CmdResult Dispatch(Dictionary<string, string> iArgs)
    {
        iArgs["data_root"] = DataRoot;
        return SCP_CmdRegistry.Dispatch("globe", iArgs);
    }

    void Run(SCP_Ui g, string iLabel, Dictionary<string, string> iArgs)
    {
        SCP_CmdResult r;
        try { r = Dispatch(iArgs); }
        catch (Exception e) { r = SCP_CmdResult.Fail(1, "炸了：" + e.GetType().Name + ": " + e.Message); }
        string aBody = string.Join("\n", r.Lines);
        g.SetField(SLog, $"[{DateTime.Now:HH:mm:ss} {iLabel}] exit {r.ExitCode}\n{aBody}");
        m_Message = (r.ExitCode == 0 ? "" : "✗ ") + (r.Lines.FirstOrDefault(l => l.Trim().Length > 0) ?? iLabel);
        if (r.ExitCode == 0) { m_Version++; m_Dirty = true; }
    }

    void DrawLog(SCP_Ui g)
    {
        string aLog = g.FieldValue(SLog, "");
        if (aLog.Length == 0) return;
        using var aFold = g.Fold("紀錄（上一個動作）", P + "fold/log", iDefaultOpen: true);
        if (aFold.Open) g.Paragraph(aLog);
    }

    // ── 小工具 ─────────────────────────────────────────────
    static string Def(string iId) => s_Defaults.TryGetValue(iId, out string? d) ? d : "";
    static string V(SCP_Ui g, string iId) => g.FieldValue(iId, Def(iId)).Trim();
    static string Field(SCP_Ui g, string iLabel, string iId) => g.TextField(iLabel, Def(iId), iId).Trim();
    static bool TryD(string s, out double d) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d);
    static double Zoom(SCP_Ui g) => TryD(V(g, FZoom), out double z) && z > 0 ? z : 1;
    static void Nudge(SCP_Ui g, string iId, double iDelta)
    {
        double v = TryD(V(g, iId), out double d) ? d : 0;
        v += iDelta;
        if (iId == FLat) v = Math.Max(-89, Math.Min(89, v));
        else { while (v > 180) v -= 360; while (v < -180) v += 360; }
        g.SetField(iId, F(v));
    }
    static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
}
