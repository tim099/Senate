// 區塊職責：**畫布觀測頁**（TASK-0443）—— 2D 共用像素畫布的後台觀測，對照雕刻觀測（SculptureViewerPage）。
//           全景／已畫範圍、展品導覽（展品 ＝ 同標題的宣稱區域，見 SCP_CanvasExhibits）、手動區域。
// 物理意義：⭐ **全部 in-process 純讀**：`SCP_CanvasBuffer.Build`（快取命中約 0.2 秒）＋ `SCP_CanvasPng` 編碼。
//           跟雕刻頁不同，這裡**不 spawn 子行程**——2D 渲染不碰 GL，沒有「GUI 執行緒建不了 context」那個理由；
//           而走同一組 SCP_Core 函式，本頁的圖跟 `senate cmd canvas --arg op=view` 的圖是同一條路。
//           ⛔ 不走 `canvas op=view`：它要 persona、圖寫進那個人的 `letters/<P>/cmd/` —— 後台看圖不該寫別人的信件夾。
// 數值影響：讀 `Canvas/`（events／快取／claims.json）；圖只寫 `SenateData/runtime/canvas_page/view.png`。
//           ⛔ 不寫事實檔（events／claims／notes）。唯一可能動到的是**衍生快取**（`_canvas_cache.*`）：
//           快取過期時 `Build` 會 replay 並存回 —— 跟 `canvas op=view` 同一條路、同一個結果，快取本來就可隨時丟棄重建。
//           讀與渲染只在按鈕／進頁時跑；視窗模式背景跑（畫面不卡），文字模式同步跑（按下那一趟就看得到結果）。
// ⚠ 放大一律最近鄰（`SCP_CanvasPng` 整數複製）—— 視窗的貼圖是線性濾波，像素圖交給它放大會糊成一片，
//   所以本頁先把圖放大到接近顯示尺寸再交出去，⛔ 不靠 ImageFit 放大。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元 ⇒ 方框，TASK-0356）。
// ⭐ TASK-0445：畫布尺寸跟著 snapshot 走（設定值 ∨ 已畫範圍），⛔ 不寫死 2048。
#nullable enable
using System.Globalization;
using System.Threading.Tasks;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Gui;
using SCP.Core.Paths;

namespace Senate.Cli.Pages;

public sealed class CanvasViewerPage : SCP_GuiToolPage
{
    public const string PageKey = "canvas";
    /// <summary>顯示框邊長（邏輯 px）。自動倍率讓圖的長邊接近它。</summary>
    public const int ViewSide = 720;
    /// <summary>自動倍率上限（16×16 的小作品放大到 45 倍沒有意義，也只是把檔案變大）。</summary>
    public const int MaxAutoScale = 32;
    const string PageTempDirName = "canvas_page";

    // ── id（契約：CLI 的 --click／--set 用的就是這些字）──────────────
    const string P = "canvas/";
    public const string ExhibitSel = P + "sel/exhibit";
    public const string StatusSel = P + "sel/status";
    public const string FRegion = P + "f/region", FScale = P + "f/scale", FPad = P + "f/pad";
    public const string FTransparent = P + "tg/transparent";
    // 結果（頁面自己寫的 session 欄位）
    public const string SViewPath = P + "state/view_path", SViewInfo = P + "state/view_info", SSnapInfo = P + "state/snap_info";
    const string SLog = P + "state/log";

    sealed class Outcome
    {
        public string Log = "";
        public readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal);
    }

    readonly SenateModel m_Model;
    bool m_Dirty = true;
    bool m_OpenRender;
    string m_DataRoot = "";
    string? m_Error;
    List<SCP_CanvasExhibit> m_Exhibits = new();
    string m_SizeText = "";
    Task<Outcome>? m_Job;
    string m_JobLabel = "";
    DateTime m_JobStartUtc;
    string? m_Message;

    public CanvasViewerPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "畫布觀測";
    public override string? MenuGroup => "內容";

    public override void OnPush()
    {
        base.OnPush();
        m_Dirty = true;
        m_OpenRender = true;   // 進頁畫一張「已畫範圍」（文字模式只在還沒有圖時畫）
    }

    string PageTempDir => Path.Combine(SenatePaths.RuntimeDir(m_Model.RepoRoot), PageTempDirName);
    string CanvasDir => m_DataRoot.Length > 0 ? new SCP_CanvasPaths(new SCP_DataRoot(m_DataRoot)).Root : "";

    // ===========================================================
    // 區塊職責：讀展品清單（claims.json，輕）。畫布本體（4 MiB×2）只在渲染時才讀，⛔ 不常駐在頁面上。
    // ===========================================================
    void Reload()
    {
        m_Dirty = false;
        m_Error = null;
        m_Exhibits = new();
        m_DataRoot = m_Model.AgentCommandsRoot.Value ?? "";
        if (m_DataRoot.Length == 0 || !Directory.Exists(m_DataRoot))
        { m_Error = $"找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定"; return; }
        var aPaths = new SCP_CanvasPaths(new SCP_DataRoot(m_DataRoot));
        SCP_CanvasSizeInfo aSize = SCP_CanvasSettings.Resolve(aPaths);
        m_SizeText = aSize.Describe();
        if (!SCP_CanvasExhibits.TryLoad(aPaths, aSize.Effective, out List<SCP_CanvasExhibit> aList, out string aErr))
        { m_Error = "展品讀不了：" + aErr; return; }
        m_Exhibits = aList;
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", P + "btn/reload")) { m_Dirty = true; m_Message = "已重新讀取"; }
        string aRoot = m_Model.AgentCommandsRoot.Value ?? "";
        OpenFolderButton(iUi, aRoot.Length > 0 ? new SCP_CanvasPaths(new SCP_DataRoot(aRoot)).Root : null, P + "btn/open-dir");
        OpenFolderButton(iUi, Directory.Exists(PageTempDir) ? PageTempDir : null, P + "btn/open-temp", "開啟暫存圖資料夾");
    }

    protected override void DrawContent(SCP_Ui g)
    {
        PumpJob(g);
        if (m_Dirty) Reload();
        // 進頁畫一張（放在畫任何區塊之前：文字模式同步跑 ⇒ 這一趟底下的結果區就看得到）
        if (m_OpenRender)
        {
            m_OpenRender = false;
            if (m_Error == null && (SCP_GuiHost.RedrawsContinuously || g.FieldValue(SViewPath, "").Length == 0))
                RunRender(g, "已畫範圍", RenderTarget.Painted, null);
        }

        if (m_Error != null) g.Note("[注意] " + m_Error);
        if (m_Job != null) g.Note($"執行中：{m_JobLabel}（{(DateTime.UtcNow - m_JobStartUtc).TotalSeconds:0} 秒）");
        if (m_Message != null) g.Note(m_Message);
        if (m_SizeText.Length > 0) g.Label("尺寸：" + m_SizeText);
        string aSnapInfo = g.FieldValue(SSnapInfo, "");
        if (aSnapInfo.Length > 0) g.Label(aSnapInfo);

        using (g.Row())
        {
            if (g.Button("已畫範圍", P + "btn/render-painted")) RunRender(g, "已畫範圍", RenderTarget.Painted, null);
            if (g.Button("全景（整張畫布）", P + "btn/render-full")) RunRender(g, "全景", RenderTarget.Full, null);
        }
        DrawExhibits(g);
        DrawManual(g);
        DrawResult(g);
        DrawLog(g);
    }

    // ===========================================================
    // 區塊職責：展品導覽 —— 篩選（全部／active／done）、選一件、渲染它（外框可往外留幾格）。
    // ===========================================================
    void DrawExhibits(SCP_Ui g)
    {
        string aStatus = g.Dropdown("狀態", new List<SCP_GuiOption>
        {
            new("all", "全部"), new("active", "active（還在畫）"), new("done", "done（完成）"),
        }, "all", StatusSel);
        var aShown = m_Exhibits.FindAll(e => aStatus == "all" || e.StatusText == aStatus);
        var aOpts = new List<SCP_GuiOption>();
        foreach (SCP_CanvasExhibit e in aShown)
            aOpts.Add(new SCP_GuiOption(e.Id, $"[{e.StatusText}] {e.Id}｜{string.Join("、", e.Personas)}｜{e.RegionText}"
                                              + (e.ClaimIds.Count > 1 ? $"｜{e.ClaimIds.Count} 筆宣稱" : "")));
        string aFirst = aShown.Count > 0 ? aShown[0].Id : "";
        string aSel = g.FieldValue(ExhibitSel + "/value", aFirst);
        using (g.Row())
        {
            if (g.Button("渲染展品", P + "btn/render-exhibit"))
            {
                SCP_CanvasExhibit? aEx = SCP_CanvasExhibits.Find(m_Exhibits, aSel);
                if (aEx == null) m_Message = "沒有選展品（或展品清單是空的）⇒ 這次沒有動作";
                else RunRender(g, "展品 " + aEx.Id, RenderTarget.Exhibit, aEx);
            }
        }
        using var aFold = g.Fold($"展品導覽（{aShown.Count}／{m_Exhibits.Count} 件；展品 id ＝ 宣稱區域的標題，同標題合成一件）",
                                 P + "fold/exhibits", iDefaultOpen: true);
        if (!aFold.Open) return;
        if (m_Exhibits.Count == 0) { g.Note("（沒有展品 —— `senate cmd canvas --arg op=claim --arg sub=add --arg title=<標題> …` 宣稱一塊就有）"); return; }
        if (aShown.Count == 0) { g.Note("（這個狀態沒有展品）"); return; }
        string aPick = g.Dropdown("展品", aOpts, aFirst, ExhibitSel);
        g.TextField("外框往外留幾格（pad）", "2", FPad);
        SCP_CanvasExhibit? aCur = SCP_CanvasExhibits.Find(m_Exhibits, aPick);
        if (aCur == null) return;
        g.Label($"作者：{string.Join("、", aCur.Personas)}　狀態：{aCur.StatusText}　範圍：{aCur.RegionText}");
        g.Label($"宣稱：{string.Join("、", aCur.ClaimIds)}" + (aCur.CreatedAt.Length > 0 ? $"　最早 {aCur.CreatedAt}" : ""));
        g.Note($"指令：senate cmd canvas --arg op=view --arg persona=<你> --arg exhibit=\"{aCur.Id}\"");
    }

    // ===========================================================
    // 區塊職責：手動區域 —— x,y,w,h ＋ 倍率（空白＝自動）＋ 透明底（沒畫過的格透明）。
    // ===========================================================
    void DrawManual(SCP_Ui g)
    {
        using var aFold = g.Fold("手動區域", P + "fold/manual", iDefaultOpen: false);
        if (!aFold.Open) return;
        g.TextField("region（x,y,w,h）", "1000,1000,64,64", FRegion);
        g.TextField($"倍率（空白＝自動，讓長邊接近 {ViewSide}px；上限 {MaxAutoScale}）", "", FScale);
        g.Toggle("透明底（沒畫過的格透明；渲染展品／已畫範圍也套用）", false, FTransparent);
        if (g.Button("渲染手動區域", P + "btn/render-manual")) RunRender(g, "手動區域", RenderTarget.Manual, null);
    }

    void DrawResult(SCP_Ui g)
    {
        string aPath = g.FieldValue(SViewPath, "");
        using var aFold = g.Fold("渲染結果", P + "fold/result", iDefaultOpen: true);
        if (!aFold.Open) return;
        if (aPath.Length == 0) { g.Note("（還沒渲染 —— 按「已畫範圍」「全景」「渲染展品」或「渲染手動區域」）"); return; }
        g.Label(g.FieldValue(SViewInfo, ""));
        g.Note("檔案：" + aPath + (m_Job != null ? "（執行中 —— 下面是上一張）" : ""));
        g.ImageFit(aPath, ViewSide, "畫布渲染圖 " + Path.GetFileName(aPath));
    }

    void DrawLog(SCP_Ui g)
    {
        string aLog = g.FieldValue(SLog, "");
        if (aLog.Length == 0) return;
        using var aFold = g.Fold("紀錄", P + "fold/log", iDefaultOpen: false);
        if (!aFold.Open) return;
        foreach (string aLine in aLog.Split('\n')) g.Label(aLine);
    }

    // ===========================================================
    // 區塊職責：渲染 —— 讀畫布 → 算範圍與倍率 → 編 PNG → 寫暫存。範圍與倍率在**按下那一刻**從畫面讀好，
    //           背景工作只拿值（⛔ 不在背景執行緒碰 SCP_Ui）。
    // ===========================================================
    enum RenderTarget { Painted, Full, Exhibit, Manual }

    void RunRender(SCP_Ui g, string iLabel, RenderTarget iTarget, SCP_CanvasExhibit? iExhibit)
    {
        if (m_DataRoot.Length == 0) { m_Message = "沒有資料根 ⇒ 這次沒有動作"; return; }
        string aRegionRaw = g.FieldValue(FRegion, "1000,1000,64,64");
        string aScaleRaw = g.FieldValue(FScale, "").Trim();
        string aPadRaw = g.FieldValue(FPad, "2").Trim();
        bool aTransparent = g.ToggleValue(FTransparent, false);
        string aCanvasDir = CanvasDir;
        string aOut = Path.Combine(PageTempDir, "view.png");

        // 範圍參數先驗（同步）：打錯字要當場說，⛔ 不丟進背景再回一個看不懂的例外
        int aMx = 0, aMy = 0, aMw = 0, aMh = 0, aPad = 0, aFixedScale = 0;
        // 同步只驗**格式**（打錯字當場說）；夾進畫布要等背景拿到 snapshot 的實際尺寸
        if (iTarget == RenderTarget.Manual
            && !TryParseRegion(aRegionRaw, new SCP_CanvasSize(SCP_CanvasSpec.MaxSide, SCP_CanvasSpec.MaxSide), out aMx, out aMy, out aMw, out aMh, out string aWhy))
        { m_Message = "region 不合法：" + aWhy + " ⇒ 這次沒有動作"; return; }
        if (iTarget == RenderTarget.Exhibit && (!int.TryParse(aPadRaw.Length == 0 ? "0" : aPadRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out aPad) || aPad < 0))
        { m_Message = "pad 要是 ≥0 的整數：" + aPadRaw + " ⇒ 這次沒有動作"; return; }
        if (aScaleRaw.Length > 0 && (!int.TryParse(aScaleRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out aFixedScale) || aFixedScale < 1 || aFixedScale > 64))
        { m_Message = "倍率要是 1..64 的整數（或空白＝自動）：" + aScaleRaw + " ⇒ 這次沒有動作"; return; }

        Start(g, iLabel, () =>
        {
            var aPaths = new SCP_CanvasPaths(aCanvasDir);
            SCP_CanvasSnapshot aSnap = SCP_CanvasBuffer.Build(aPaths);
            SCP_CanvasSize aSize = aSnap.Size;
            int aPainted = CountPainted(aSnap.Mask, aSize, 0, 0, aSize.Width, aSize.Height);
            string aSnapLine = $"畫布：事件檔 {aSnap.EventFiles}／快取 {aSnap.Path}"
                               + (aSnap.ReplayedEvents > 0 ? $"（replay {aSnap.ReplayedEvents}）" : "")
                               + $"／已畫 {aPainted:N0} 格（{(aPainted * 100.0 / aSize.Area).ToString("0.####", CultureInfo.InvariantCulture)}%）"
                               + $"／{aSnap.SizeInfo.Describe()}";
            int x, y, w, h;
            switch (iTarget)
            {
                case RenderTarget.Full: x = 0; y = 0; w = aSize.Width; h = aSize.Height; break;
                case RenderTarget.Exhibit: SCP_CanvasExhibits.Padded(iExhibit!, aPad, aSize, out x, out y, out w, out h); break;
                case RenderTarget.Manual:
                    if (!TryParseRegion($"{aMx},{aMy},{aMw},{aMh}", aSize, out x, out y, out w, out h, out string aClipWhy))
                        return new Outcome { Log = $"[{iLabel}] region 不合法：{aClipWhy}" };
                    break;
                default:
                    if (!PaintedBounds(aSnap.Mask, aSize, out x, out y, out w, out h))
                    { x = 0; y = 0; w = aSize.Width; h = aSize.Height; }
                    break;
            }
            int aScale = aFixedScale > 0 ? aFixedScale : AutoScale(w, h);
            byte[] aPng = aTransparent
                ? SCP_CanvasPng.EncodeRgba(aSnap.Buffer, aSnap.Mask, x, y, w, h, aSize.Width, aScale, out _)
                : SCP_CanvasPng.EncodeRgb(aSnap.Buffer, x, y, w, h, aSize.Width, aScale);
            Directory.CreateDirectory(Path.GetDirectoryName(aOut)!);
            File.WriteAllBytes(aOut, aPng);
            int aInside = CountPainted(aSnap.Mask, aSize, x, y, w, h);
            var o = new Outcome();
            o.Fields[SViewPath] = aOut;
            o.Fields[SViewInfo] = $"{DateTime.Now:HH:mm:ss}　{iLabel}　region {x},{y},{w},{h}　×{aScale}（{w * aScale}×{h * aScale}px）"
                                  + $"　範圍內已畫 {aInside:N0}／{w * h:N0} 格" + (aTransparent ? "　透明底" : "");
            o.Log = $"[{iLabel}] {o.Fields[SViewInfo]}\n{aSnapLine}\n→ {aOut}";
            o.Fields[SSnapInfo] = aSnapLine;
            return o;
        });
    }

    // ── 純函式（selftest 直接打）─────────────────────────────

    /// <summary>自動倍率：長邊放大到不超過 <see cref="ViewSide"/>，至少 1、至多 <see cref="MaxAutoScale"/>。</summary>
    public static int AutoScale(int iW, int iH)
    {
        int aLong = Math.Max(1, Math.Max(iW, iH));
        return Math.Clamp(ViewSide / aLong, 1, MaxAutoScale);
    }

    /// <summary>已畫格的外框（mask 非 0）。整張沒畫過回 false。</summary>
    public static bool PaintedBounds(byte[] iMask, SCP_CanvasSize iSize, out int oX, out int oY, out int oW, out int oH)
    {
        int x1 = int.MaxValue, y1 = int.MaxValue, x2 = -1, y2 = -1;
        for (int y = 0; y < iSize.Height; y++)
        {
            int aRow = y * iSize.Width;
            for (int x = 0; x < iSize.Width; x++)
            {
                if (iMask[aRow + x] == 0) continue;
                if (x < x1) x1 = x;
                if (x > x2) x2 = x;
                if (y < y1) y1 = y;
                y2 = y;
            }
        }
        if (x2 < 0) { oX = oY = 0; oW = oH = 0; return false; }
        oX = x1; oY = y1; oW = x2 - x1 + 1; oH = y2 - y1 + 1;
        return true;
    }

    static int CountPainted(byte[] iMask, SCP_CanvasSize iSize, int iX, int iY, int iW, int iH)
    {
        int n = 0;
        for (int y = iY; y < iY + iH; y++)
        {
            int aRow = y * iSize.Width;
            for (int x = iX; x < iX + iW; x++) if (iMask[aRow + x] != 0) n++;
        }
        return n;
    }

    /// <summary>x,y,w,h → 夾進畫布的範圍。起點越界、寬高 ≤0 ⇒ false（⛔ 不替人挪到合法的地方）。</summary>
    public static bool TryParseRegion(string iRaw, SCP_CanvasSize iSize, out int oX, out int oY, out int oW, out int oH, out string oWhy)
    {
        oX = oY = oW = oH = 0; oWhy = "";
        string[] aParts = (iRaw ?? "").Split(',');
        int[] v = new int[4];
        if (aParts.Length != 4) { oWhy = "要 4 個整數 x,y,w,h"; return false; }
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(aParts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v[i]))
            { oWhy = "第 " + (i + 1) + " 個不是整數：" + aParts[i]; return false; }
        if (v[2] <= 0 || v[3] <= 0) { oWhy = "w／h 要 >0"; return false; }
        if (!iSize.InBounds(v[0], v[1])) { oWhy = $"起點 ({v[0]},{v[1]}) 在畫布 {iSize} 外"; return false; }
        oX = v[0]; oY = v[1];
        oW = Math.Min(v[0] + v[2], iSize.Width) - v[0];
        oH = Math.Min(v[1] + v[3], iSize.Height) - v[1];
        return true;
    }

    // ── 背景工作（同 SculptureViewerPage 的形狀）─────────────────────

    void Start(SCP_Ui g, string iLabel, Func<Outcome> iJob)
    {
        if (m_Job != null) { m_Message = "前一筆（" + m_JobLabel + "）還沒完 ⇒ 這次沒有動作"; return; }
        m_Message = null;
        if (SCP_GuiHost.RedrawsContinuously)
        {
            m_JobLabel = iLabel;
            m_JobStartUtc = DateTime.UtcNow;
            m_Job = Task.Run(iJob);
            return;
        }
        Outcome o;
        try { o = iJob(); }
        catch (Exception e) { o = new Outcome { Log = $"[{iLabel}] 那一步炸了：{e.GetType().Name}: {e.Message}" }; }
        Apply(g, o);
    }

    void PumpJob(SCP_Ui g)
    {
        if (m_Job == null || !m_Job.IsCompleted) return;
        Task<Outcome> aJob = m_Job;
        m_Job = null;
        Outcome o;
        try { o = aJob.Result; }
        catch (Exception e) { o = new Outcome { Log = $"[{m_JobLabel}] 那一步炸了：{e.GetType().Name}: {(e.InnerException ?? e).Message}" }; }
        Apply(g, o);
    }

    void Apply(SCP_Ui g, Outcome o)
    {
        foreach (var kv in o.Fields) g.SetField(kv.Key, kv.Value);
        g.SetField(SLog, o.Log);
        int aNl = o.Log.IndexOf('\n');
        m_Message = aNl > 0 ? o.Log.Substring(0, aNl) : o.Log;
    }
}
