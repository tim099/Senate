// 區塊職責：**雕刻觀測頁**（TASK-0377）——
//           展品導覽、手動相機與光影、渲染設定檔、切片、2D→3D 貼圖預覽、匯出 obj/vox。
// 物理意義：⭐ **出圖一律 spawn 自己這顆 exe**（`Environment.ProcessPath`）跑 `cmd sculpture --arg op=view --arg out=<本頁暫存>`
//           （Tim 2026-10-02：跟引擎同一條管線）。理由兩條：
//             ① GLFW／GL context 不能從 GUI 的背景執行緒建 ⇒ 在本行程 inline 渲染會卡死或拿不到 context；
//             ② 本頁按出來的圖跟 agent 打 `senate cmd sculpture` 拿到的圖**必然逐位元同一條路**。
//           ⛔ 不 Process.Start python（引擎已在 SCP_Core）。
//           **讀**（展品清單、設定檔清單、作用中、疊加結果、skybox 檔名）在本行程直讀 SCP_Core（純讀）；
//           **寫**設定檔也走 spawn `cmd sculpture --arg op=render-profile …` —— 驗證與回報跟 CLI 同一份。
//           2D 貼圖預覽走 in-process `SCP_CmdRegistry.Dispatch("canvas", op=view)`（純讀、不碰 GL），
//           圖路徑讀它回的 `path_t`，⛔ 不自己拼。
// 數值影響：IO 只在進頁（OnPush ⇒ 下一幀重讀）、換 persona／換設定檔層、按鈕事件；⛔ 不在 ctor、不每幀。
//           暫存圖寫 `SenateData/runtime/sculpture_page/`（view.png／slice.png），⛔ 不寫進 AgentCommands。
//           視窗模式（會持續重畫）⇒ 背景跑、畫面不卡；文字模式（CLI 單次 render）⇒ 同步跑，按下那一趟就看得到結果。
//           結果（圖路徑／renderer／layers／紀錄）存進 session 欄位 ⇒ 文字模式跨指令也接得上。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元 ⇒ 方框，TASK-0356）；子行程輸出裡的 emoji 是原文，照印。
// ⚠ 「空白 ＝ 沿用」：手動欄位留空就不送那個參數 ⇒ 由設定檔疊加鏈（內建 → 共用作用中 → 個人作用中）決定。
//   三態的開關（陰影／AO／投影／地板）因此用下拉（沿用／開／關），⛔ 不用二態勾選（二態表達不了「沿用」）。
// ⚠ 「目前對象」（Tim 2026-10-02）：渲染展品 ⇒ 展品 id；全景 ⇒ 全景；選展品 ⇒ 那件展品；region 欄**編輯完成**（Enter／離開欄位／--set）且有填 ⇒ 那個 region。
//   之後的環繞／切投影／手動渲染都重渲**同一個對象**（展品 ⇒ 帶 exhibit=<id> ＋相機欄位）——
//   ⛔ 只是「region 欄有字」不換對象（自動渲染每動一下都重渲 ⇒ 按了全景再拖滑桿會默默跳回那個 region）；
//   ⛔ region 欄清空也不把正在看的展品換成全景（只有原本看 region 時才回全景）。對象存 session ⇒ 文字模式跨指令也記得。
// ⭐ 自動渲染（Tim 2026-10-02）：進頁畫一張目前對象（預設全景）；之後參數一改就重渲 —— 不必按「渲染」。
//   觸發：滑桿／下拉／persona／設定檔鏈／對象變了，或文字欄「編輯完成」（Enter／離開欄位；⛔ 不是每打一個字）。
//   合併：同時間只跑一張；跑的途中又變了 ⇒ 記一筆「還要再畫」，畫完再用**最新**的參數補一張（latest wins）。
//   ⛔ 渲染永遠不在 GUI 執行緒跑（視窗模式背景 Task；文字模式本來就是單次同步）。
#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Proc;
using SCP.Core.Sculpture;

namespace Senate.Cli.Pages;

public sealed partial class SculptureViewerPage : SCP_GuiToolPage
{
    public const string PageKey = "sculpture";
    /// <summary>渲染畫面的顯示邊長（邏輯 px；圖照原比例縮進這個框）—— 住預覽設定的 ViewSide（Tim 2026-10-12：可調）。</summary>
    float ViewSide(SCP_Ui g) => DisplaySide(PreviewCfg(g)).Side;
    const float SmallSide = 320f;
    /// <summary>子行程最多等多久（一次全景渲染 + 重播事件，秒級；給寬一點）。</summary>
    const int CliTimeoutMs = 180000;
    const string ProcTag = "senate_sculpture_page";
    const string None = "-";
    const string PageTempDirName = "sculpture_page";

    // ── id（契約：CLI 的 --click／--set 用的就是這些字）──────────────
    const string P = "sculpt/";
    public const string PersonaSel = P + "sel/persona";
    public const string ExhibitSel = P + "sel/exhibit";
    const string FRegion = P + "f/region", FExclude = P + "f/exclude", FProjection = P + "sel/projection";
    const string FYaw = P + "f/yaw", FPitch = P + "f/pitch", FRoll = P + "f/roll";
    const string FTarget = P + "f/target", FEye = P + "f/eye", FDistance = P + "f/distance", FFov = P + "f/fov", FZoom = P + "f/zoom";
    const string FAmbient = P + "f/ambient", FAo = P + "sel/ao", FShadow = P + "sel/shadow", FLightDir = P + "f/light_dir";
    const string FLights = P + "f/lights", FLightNew = P + "f/light_new";
    const string FSkybox = P + "sel/skybox", FSkyYaw = P + "f/skybox_yaw", FSkyTilt = P + "f/skybox_tilt", FWidth = P + "f/width", FHeight = P + "f/height";
    const string FFitUpscale = P + "sel/fit_upscale";
    const string FFloor = P + "sel/floor", FFloorTex = P + "sel/floor_texture", FFloorTile = P + "f/floor_tile", FFloorZ = P + "f/floor_z";
    const string FFloorFull = P + "sel/floor_full_grid", FFloorMargin = P + "f/floor_margin", FFloorMarginRatio = P + "f/floor_margin_ratio", FFloorColor = P + "f/floor_color", FFloorFade = P + "f/floor_fade";
    const string ProfScope = P + "sel/prof_scope", ProfSaveName = P + "f/prof_name";
    const string ProfCopyScope = P + "sel/prof_copy_scope", ProfCopyName = P + "f/prof_copy_name";
    const string Pending = P + "pending";
    const string FSliceRegion = P + "f/slice_region", FSliceAxis = P + "f/slice_axis";
    const string FStampRegion = P + "f/stamp_region", FStampAt = P + "f/stamp_at", FStampFacing = P + "f/stamp_facing", FStampThick = P + "f/stamp_thickness";
    // 結果（頁面自己寫的 session 欄位；畫面上沒有元件 ⇒ 不會被 --set 動到）
    const string SViewPath = P + "state/view_path", SViewInfo = P + "state/view_info", SRenderer = P + "state/renderer", SLayers = P + "state/layers";
    const string SSlicePath = P + "state/slice_path", SSliceInfo = P + "state/slice_info";
    const string SStampPath = P + "state/stamp_path", SStampCmd = P + "state/stamp_cmd", SStampInfo = P + "state/stamp_info";
    const string SExportPath = P + "state/export_path", SLog = P + "state/log", SExhibitSeen = P + "state/exhibit_seen";
    /// <summary>目前對象：<c>full</c>｜<c>exhibit:&lt;id&gt;</c>｜<c>region:&lt;x1..x2,y1..y2,z1..z2&gt;</c>（見檔頭）。</summary>
    const string SSubject = P + "state/subject";
    const string SubjectFull = "full", SubjectExhibit = "exhibit:", SubjectRegion = "region:";

    /// <summary>非空預設值的欄位 —— 摺起來時讀值（FieldValue）與畫出來時（TextField）要用同一個預設，否則兩條路給不同的值。</summary>
    /// <summary>
    /// 一條「可沿用」的滑桿：欄位空白 ＝ 沿用設定檔鏈（滑桿停在鏈上的值）；一動就變成覆寫；「沿用」鈕清掉覆寫。
    /// <para>範圍取 CLI 的驗證契約（例：pitch −89..89、skybox_tilt −89..89）—— 比契約窄的話，鏈上合法的值會被畫在端點上而看不出來。</para>
    /// </summary>
    sealed record SliderSpec(string Name, string Id, string Label, double Min, double Max, string Format,
                             Func<SCP_SculptRenderParams, double> Chain);

    static readonly SliderSpec[] s_CamSliders =
    {
        new("yaw", FYaw, "yaw（度）", 0, 360, "0", p => p.YawDeg),
        new("pitch", FPitch, "pitch（度）", -89, 89, "0", p => p.PitchDeg),
        new("roll", FRoll, "roll（度）", -180, 180, "0", p => p.RollDeg),
        new("fov", FFov, "fov（透視，度）", 5, 120, "0", p => p.FovDeg),
    };
    static readonly SliderSpec[] s_LightSliders =
    {
        new("ambient", FAmbient, "ambient", 0, 1, "0.00", p => p.Ambient),
    };
    static readonly SliderSpec[] s_SkySliders =
    {
        new("skybox_yaw", FSkyYaw, "skybox_yaw（度）", -180, 180, "0", p => p.SkyboxYawDeg),
        new("skybox_tilt", FSkyTilt, "skybox_tilt（度）", -89, 89, "0", p => p.SkyboxTiltDeg),
    };

    /// <summary>
    /// 文字欄參數：只在「編輯完成」（Enter／離開欄位／CLI `--set`）時觸發自動重渲 ——
    /// 每打一個字就畫一張的話，打「10..20」的途中會先畫出「1」「10.」那些不完整的值（而且會報錯）。
    /// </summary>
    static readonly string[] s_TextParamIds =
    {
        FRegion, FExclude, FTarget, FEye, FDistance, FZoom, FLightDir, FWidth, FHeight,
        FFloorTile, FFloorZ, FFloorMargin, FFloorMarginRatio, FFloorColor, FFloorFade, FLinks, FHideLinks,
    };

    static readonly Dictionary<string, string> s_Defaults = new(StringComparer.Ordinal)
    {
        [FSliceAxis] = "z+",
        [FStampRegion] = "1000,1000,9,6",
        [FStampAt] = "10,10,10",
        [FStampFacing] = "z+",
        [FStampThick] = "1",
    };

    readonly SenateModel m_Model;
    bool m_Dirty = true;
    string m_LoadedPersona = "\0";
    string m_LoadedScope = "";
    string m_DataRoot = "";
    string m_LettersRoot = "";
    string? m_Error;
    readonly List<(string Id, SCP_JsonData Data)> m_Exhibits = new();
    List<string> m_Personas = new();
    List<string> m_Skyboxes = new();
    List<string> m_Floors = new();
    // 設定檔（目前選的那一層）
    string? m_ScopeDir;
    List<string> m_Profiles = new();
    string? m_ProfActive;
    readonly Dictionary<string, string> m_ProfJson = new(StringComparer.Ordinal);
    // 疊加結果（選中的 persona）
    SCP_SculptRenderParams? m_Resolved;
    List<string> m_ResolvedLayers = new();
    string m_ResolveError = "";

    Task<Outcome>? m_Job;
    string m_JobLabel = "";
    DateTime m_JobStartUtc;
    string? m_Message;

    // ── 自動渲染（見檔頭）────────────────────────────────────
    /// <summary>進頁後第一次 DrawContent 要畫一張（OnPush 只立旗標 —— ⛔ 不在 OnPush／ctor 做 IO）。</summary>
    bool m_OpenRender;
    /// <summary>上一輪看到的「觸發用」讀數（非文字參數 ＋ persona ＋ 設定檔鏈）；null ＝ 這個頁面實例還沒畫過（文字模式每道指令都是新實例）。</summary>
    string? m_PrevTrigger;
    string? m_PrevSubject;
    /// <summary>剛由工作結果寫回的對象 —— 那不是使用者改的，⛔ 不觸發（不然每張展品圖畫完都會再補一張）。</summary>
    string? m_SubjectFromJob;
    /// <summary>還要再畫一張（工作跑的途中又變了）；<see cref="m_AutoForce"/> ＝ 就算參數跟上一張一樣也畫（進頁、按了「渲染」）。</summary>
    bool m_AutoWanted, m_AutoForce;
    string m_AutoWhy = "";
    /// <summary>最近一次**手動路徑**（RunManual）送出的完整參數讀數 —— 補畫前比對，一樣就不再畫。</summary>
    string? m_LastStartedSig;
    DateTime m_LastAutoStartUtc;
    /// <summary>視窗模式兩張自動渲染之間至少隔多久（拖曳中的節流；工作本身的耗時是第二道節流）。</summary>
    static readonly TimeSpan AutoMinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>一個動作的結果：紀錄 ＋ 要寫回 session 的欄位 ＋ 要不要重讀磁碟。</summary>
    sealed class Outcome
    {
        public string Log = "";
        public readonly Dictionary<string, string> Fields = new(StringComparer.Ordinal);
        public bool Reload;
    }

    public SculptureViewerPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "雕刻觀測";
    public override string? MenuGroup => "內容";

    public override void OnPush()
    {
        base.OnPush();
        m_Dirty = true;   // ⛔ 不在這裡讀（拿不到畫面上選的 persona）—— 下一幀 DrawContent 開頭讀
        m_OpenRender = true;   // 進頁畫一張（真的畫在 DrawContent 的 AutoRender；文字模式只在還沒有圖時畫）
    }

    // ===========================================================
    // 區塊職責：讀磁碟（唯讀，in-process）—— 展品、persona、skybox、設定檔層、疊加結果。
    // 物理意義：只在 m_Dirty（進頁／重新讀取／寫完設定檔）或換 persona／換層時跑。
    // ===========================================================
    void Reload(string iPersona, string iScope)
    {
        // 作品與展品清單與 persona／層無關：只換 persona（進頁第二幀下拉補上預設值就是這種）⛔ 不重讀（TASK-0473 第二次凍結）
        // 工具列這一輪剛讀過（EnsureWorks，兩個下拉都在工具列）⇒ 不再讀第二次
        bool aWorks = (m_Dirty && !m_WorksFresh) || m_WorksRoot != (m_Model.AgentCommandsRoot.Value ?? "");
        m_WorksFresh = false;
        m_Dirty = false;
        m_LoadedPersona = iPersona;
        m_LoadedScope = iScope;
        m_Error = null;
        m_DataRoot = m_Model.AgentCommandsRoot.Value ?? "";
        m_LettersRoot = m_Model.LettersRoot.Value ?? "";
        m_Skyboxes = new(); m_Floors = new(); m_Profiles = new(); m_ProfJson.Clear();
        m_ScopeDir = null; m_ProfActive = null; m_Resolved = null; m_ResolvedLayers = new(); m_ResolveError = "";
        if (m_DataRoot.Length == 0 || !Directory.Exists(m_DataRoot))
        {
            m_Error = $"找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定";
            return;
        }
        var aData = new SCP_DataRoot(m_DataRoot);
        if (aWorks) { ReloadWorks(aData); ReloadExhibits(aData); }

        m_Personas = m_LettersRoot.Length > 0 && Directory.Exists(m_LettersRoot) ? SCP_PersonaDisplay.ListPersonas(m_LettersRoot) : new();

        string aSky = SCP_SculptRenderProfiles.SkyboxesDir(aData);
        if (Directory.Exists(aSky))
            foreach (string f in Directory.GetFiles(aSky))
            {
                string aExt = Path.GetExtension(f).ToLowerInvariant();
                if (aExt == ".png" || aExt == ".jpg" || aExt == ".jpeg" || aExt == ".hdr") m_Skyboxes.Add(Path.GetFileName(f));
            }
        m_Skyboxes.Sort(StringComparer.OrdinalIgnoreCase);
        string aFloors = SCP_SculptRenderProfiles.FloorsDir(aData);
        if (Directory.Exists(aFloors))
            foreach (string f in Directory.GetFiles(aFloors))
            {
                string aExt = Path.GetExtension(f).ToLowerInvariant();
                if (aExt == ".png" || aExt == ".jpg" || aExt == ".jpeg") m_Floors.Add(Path.GetFileName(f));
            }
        m_Floors.Sort(StringComparer.OrdinalIgnoreCase);

        SCP_LettersRoot? aLetters = m_LettersRoot.Length > 0 ? new SCP_LettersRoot(m_LettersRoot) : null;
        string? aPersona = iPersona.Length > 0 ? iPersona : null;
        try
        {
            if (iScope == "shared") m_ScopeDir = SCP_SculptRenderProfiles.ScopeDir(SCP_SculptProfileScope.Shared, aData, aLetters, null);
            else if (aPersona != null && aLetters != null)
                m_ScopeDir = SCP_SculptRenderProfiles.ScopeDir(SCP_SculptProfileScope.Persona, aData, aLetters, aPersona);
            if (m_ScopeDir != null)
            {
                m_Profiles = SCP_SculptRenderProfiles.List(m_ScopeDir);
                m_ProfActive = SCP_SculptRenderProfiles.GetActive(m_ScopeDir);
                foreach (string n in m_Profiles)
                    m_ProfJson[n] = SCP_SculptRenderProfiles.TryLoad(m_ScopeDir, n, out SCP_JsonData j, out string aErr)
                        ? j.ToJson(true) : "（讀不了）" + aErr;
            }
        }
        catch (Exception e) { m_Error = "設定檔層解不出來：" + e.Message; }

        if (SCP_SculptRenderProfiles.TryResolve(aData, aLetters, aPersona, out SCP_SculptRenderParams aP, out List<string> aLayers, out string aWhy))
        { m_Resolved = aP; m_ResolvedLayers = aLayers; }
        else m_ResolveError = aWhy;
    }

    string SculptureDir => m_DataRoot.Length > 0 ? SCP_SculptRenderProfiles.SculptureDir(new SCP_DataRoot(m_DataRoot)) : "";
    string DefaultExportDir => m_DataRoot.Length > 0 ? new SCP_SculptPaths(new SCP_DataRoot(m_DataRoot)).Exports : "";
    string PageTempDir => Path.Combine(SenatePaths.RuntimeDir(m_Model.RepoRoot), PageTempDirName);

    // ===========================================================
    // 區塊職責：工具列（兩排，TASK-0504）—— 第一排：重新讀取／匯出（格式在匯出設定）／開 Sculpture 資料夾／開匯出資料夾／開暫存資料夾；
    //          第二排：空間與作品下拉。兩排都在 base 的 TopBar 範圍裡 ⇒ 不跟內容一起捲。
    // ===========================================================
    protected override void DrawTopBar(SCP_Ui iUi)
    {
        base.DrawTopBar(iUi);   // 第一排：導覽鈕 ＋ TopBarButtons（EnsureWorks 在那裡，第二排用得到）
        using (iUi.Row()) WorkTopBar(iUi);
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", P + "btn/reload")) { m_Dirty = true; m_Message = "已重新讀取"; }
        EnsureWorks();   // 匯出鈕與作品／展品下拉都要問清單
        ExportButton(iUi);
        ProjectionButton(iUi);
        // ⚠ TopBar 先於 DrawContent 畫 ⇒ 第一次 Reload 前 m_DataRoot 還是空的，直接問 model
        string aRoot = m_Model.AgentCommandsRoot.Value ?? "";
        OpenFolderButton(iUi, aRoot.Length > 0 ? SCP_SculptRenderProfiles.SculptureDir(new SCP_DataRoot(aRoot)) : null, P + "btn/open-dir");
        OpenFolderButton(iUi, ExportDirFor(iUi, aRoot), P + "btn/open-export", "開啟匯出資料夾");
        OpenFolderButton(iUi, Directory.Exists(PageTempDir) ? PageTempDir : null, P + "btn/open-temp", "開啟暫存圖資料夾");
    }

    /// <summary>
    /// 「開啟匯出資料夾」開哪裡：匯出設定的 OutDir ＞ 選了個人作品 ⇒ 那件作品的 `works/&lt;id&gt;/exports`（引擎預設寫這裡）
    /// ＞ 上一次匯出檔那一層 ＞ 資料根的 `Sculpture/exports`（共用展區）。
    /// 🩸 以前作品也走「上一次匯出檔那一層」：換作品會清掉那一格、匯出跑完才填回 ⇒ 匯出中按下去開到 `Sculpture/exports`（Tim 2026-10-12）。
    /// </summary>
    string? ExportDirFor(SCP_Ui g, string iRoot)
    {
        string aOut = ExportCfg(g).OutDir.Trim();
        if (aOut.Length > 0) return aOut;
        if (iRoot.Length == 0) return null;
        if (WorkExportDir(g, iRoot) is string aWorkDir) return aWorkDir;
        string aLast = g.FieldValue(SExportPath, "");
        return aLast.Length > 0 ? Path.GetDirectoryName(aLast) : new SCP_SculptPaths(new SCP_DataRoot(iRoot)).Exports;
    }

    /// <summary>選了個人作品 ⇒ 引擎匯出那件作品的預設資料夾（`works/&lt;id&gt;/exports`）；否則 null。</summary>
    string? WorkExportDir(SCP_Ui g, string iRoot)
        => iRoot.Length > 0 && PersonalSpace(g) && SelectedWork(g) is { Length: > 0 } aWork ? new SCP_SculptWorks(new SCP_DataRoot(iRoot)).SpacePaths(aWork).Exports : null;

    protected override void DrawContent(SCP_Ui g)
    {
        PumpJob(g);
        string aPersona = g.FieldValue(PersonaSel + "/value", None);
        if (aPersona == None) aPersona = "";
        string aScope = g.FieldValue(ProfScope + "/value", "shared");
        if (m_Dirty || aPersona != m_LoadedPersona || aScope != m_LoadedScope) Reload(aPersona, aScope);
        // ⚠ 放在畫任何區塊**之前**：文字模式的渲染是同步的 ⇒ 這一趟底下的結果區就看得到新圖
        //   （放在最後的話，`--set` 那一趟畫的是舊圖，要再下一道指令才看得到）。
        //   空間／作品下拉在工具列（先於這裡畫）⇒ 這一輪選的，這裡就看得到。
        CheckWorkSelection(g);
        ApplyProjectionFlip(g);   // 工具列按的；鏈這時已經讀好
        bool aHasSubject = !PersonalSpace(g) || SelectedWork(g).Length > 0;
        if (aHasSubject)
        {
            AutoRender(g, aPersona);
            DrawResult(g);   // 渲染圖在最上面（TASK-0504）
        }

        if (m_Error != null) g.Note("[注意] " + m_Error);
        if (m_Job != null)
            g.Note($"執行中：{m_JobLabel}（{(DateTime.UtcNow - m_JobStartUtc).TotalSeconds:0} 秒；子行程跑 `senate cmd`，完成後自動更新）");
        if (m_Message != null) g.Note(m_Message);

        var aPersonaOpts = new List<SCP_GuiOption> { new(None, "（不指定 —— 只用共用設定）") };
        foreach (string p in m_Personas) aPersonaOpts.Add(new SCP_GuiOption(p));
        g.Dropdown("persona（個人設定檔層／貼圖預覽）", aPersonaOpts, None, PersonaSel);

        DrawWorkSelector(g);
        if (PersonalSpace(g))
        {
            DrawWorks(g, aPersona);
            if (SelectedWork(g).Length == 0) { DrawLog(g); return; }
        }
        else DrawExhibits(g, aPersona);
        DrawManual(g, aPersona);
        DrawProfiles(g, aPersona, aScope);
        DrawSlice(g, aPersona);
        DrawStamp(g, aPersona);
        DrawExport(g);
        DrawLog(g);
    }

    // ===========================================================
    // 區塊職責：展品 —— 工具列第二排選展品（帶入 region／exclude、換對象）；內容區是渲染展品／全景與選中那件的說明。
    // 物理意義：關鍵操作放折疊外層（收合也能按）；preset 由引擎讀，⛔ 本頁不重組 preset 參數。
    //          展品清單跟作品清單同一個時機讀（工具列 EnsureWorks）—— 下拉在工具列，等內容區讀就來不及了。
    // ===========================================================
    string m_ExhibitsError = "";

    void ReloadExhibits(SCP_DataRoot iData)
    {
        m_Exhibits.Clear(); m_ExhibitsError = "";
        try
        {
            foreach (var kv in new SCP_SculptEngine(iData).LoadExhibits()) m_Exhibits.Add((kv.Key, kv.Value));
        }
        catch (Exception e) { m_ExhibitsError = "展品讀不了：" + e.GetType().Name + ": " + e.Message; }
    }

    string FirstExhibit => m_Exhibits.Count > 0 ? m_Exhibits[0].Id : "";

    /// <summary>工具列第二排（共用展區）：展品下拉 —— 作品下拉在展區的對應（TASK-0504）。選了別件 ⇒ 手動區的 region／exclude 帶入 preset、對象換成它。</summary>
    void ExhibitTopBar(SCP_Ui g)
    {
        // 選項只留「名稱（id）｜作者」：工具列放不下 region 與說明 —— 那兩樣在內容區的展品導覽
        var aOpts = m_Exhibits.Select(e => new SCP_GuiOption(e.Id, $"{S(e.Data, "title")} ({e.Id})｜{S(e.Data, "author")}")).ToList();
        string aPick = g.Dropdown("展品（" + m_Exhibits.Count + "）", aOpts, FirstExhibit, ExhibitSel);
        // 第一次畫不蓋（那是使用者留下的值）；「上一次看到的展品」存 session（不是成員）⇒ 文字模式每道指令是新實例也判得出「換了」
        string aSeen = g.FieldValue(SExhibitSeen, "");
        if (aPick == aSeen) return;
        var aEx = m_Exhibits.Find(e => e.Id == aPick);
        if (aSeen.Length > 0 && aEx.Id != null)
        {
            g.SetField(FRegion, S(aEx.Data, "region")); g.SetField(FExclude, S(aEx.Data, "exclude_color"));
            g.SetField(SSubject, SubjectExhibit + aPick);   // 選了別件 ⇒ 對象換成它
            // 直接要一張：「換了對象」靠上一輪的讀數比，文字模式每道指令是新實例比不出來（同作品下拉的 WorkSeen）
            RequestRender("換了展品", iForce: true);
        }
        g.SetField(SExhibitSeen, aPick);
    }

    void DrawExhibits(SCP_Ui g, string iPersona)
    {
        if (m_ExhibitsError.Length > 0) g.Note("[注意] " + m_ExhibitsError);
        string aSel = g.FieldValue(ExhibitSel + "/value", FirstExhibit);
        using (g.Row())
        {
            if (g.Button("渲染展品", P + "btn/render-exhibit"))
            {
                if (aSel.Length == 0 || !m_Exhibits.Exists(e => e.Id == aSel)) m_Message = "沒有選展品（或展品清單是空的）⇒ 這次沒有動作";
                else RunView(g, "渲染展品 " + aSel, new() { ["exhibit"] = aSel }, iPersona, SubjectExhibit + aSel);
            }
            if (g.Button("全景", P + "btn/render-all")) RunView(g, "全景", new(), iPersona, SubjectFull);
        }
        using var aFold = g.Fold($"展品導覽（{m_Exhibits.Count} 件）", P + "fold/exhibits", iDefaultOpen: true);
        if (!aFold.Open) return;
        if (m_Exhibits.Count == 0) { g.Note("（沒有展品 —— `senate cmd sculpture --arg op=exhibit --arg sub=register …` 登錄）"); return; }
        var aCur = m_Exhibits.Find(e => e.Id == aSel);
        if (aCur.Id != null)
        {
            g.Label($"《{S(aCur.Data, "title")}》{aCur.Id}｜by {S(aCur.Data, "author")}｜region {S(aCur.Data, "region")}"
                    + (S(aCur.Data, "exclude_color").Length > 0 ? "｜exclude " + S(aCur.Data, "exclude_color") : ""));
            if (S(aCur.Data, "description").Length > 0) g.Paragraph(S(aCur.Data, "description"));
        }
        g.Note("展品下拉在上方工具列；選了展品會把它的 region／exclude 帶進手動區（手動渲染與匯出跟著走）。");
    }

    // ===========================================================
    // 區塊職責：手動相機與光影 —— 欄位一對一映射到 `op=view` 的參數；空白 ＝ 不送（沿用設定檔鏈）。
    // ===========================================================
    void DrawManual(SCP_Ui g, string iPersona)
    {
        using (g.Row())
        {
            if (g.Button("渲染", P + "btn/render")) RequestRender("按了「渲染」", iForce: true);
            Orbit(g, "yaw -45", FYaw, -45, iPersona);
            Orbit(g, "yaw -15", FYaw, -15, iPersona);
            Orbit(g, "yaw +15", FYaw, 15, iPersona);
            Orbit(g, "yaw +45", FYaw, 45, iPersona);
            Orbit(g, "pitch -10", FPitch, -10, iPersona);
            Orbit(g, "pitch +10", FPitch, 10, iPersona);
        }
        // 滑桿放在摺疊外層、預設展開（Tim 2026-10-02：拿滑桿調鏡頭，放開就重渲）
        using (var aCam = g.Fold("鏡頭滑桿（放開就重渲）", P + "fold/sliders", iDefaultOpen: true))
        {
            if (aCam.Open)
            {
                g.Note("「沿用中」＝ 不送這個參數、滑桿停在設定檔鏈的值；一拖就變成覆寫，按「沿用」清掉覆寫。"
                       + "（⚠ 對象是展品時，展品自己的 preset 可能再蓋過鏈上的值 —— 以結果區的 layers 為準）");
                foreach (SliderSpec s in s_CamSliders) InheritSlider(g, s);
                foreach (SliderSpec s in s_LightSliders) InheritSlider(g, s);
                foreach (SliderSpec s in s_SkySliders) InheritSlider(g, s);
            }
        }
        using var aFold = g.Fold("相機與光影（手動）", P + "fold/manual", iDefaultOpen: false);
        if (!aFold.Open) return;
        g.Note("空白 ＝ 不送這個參數（沿用設定檔鏈：內建 → 共用作用中 → 個人作用中）。快速環繞鈕會改欄位並立刻重渲。文字欄按 Enter／離開欄位就重渲。");
        Field(g, "region（x1..x2,y1..y2,z1..z2；空＝全空間）", FRegion);
        Field(g, "exclude_color（c,c,…）", FExclude);
        Tri(g, "投影", FProjection, "orthographic", "正交 orthographic", "perspective", "透視 perspective");
        // ⚠ 文字欄不放進同一個 Row（ImGui 的欄位標籤會疊在下一格上 —— 2026-10-02 截圖實測）
        // yaw／pitch／roll／fov／ambient／skybox_yaw／skybox_tilt 在上面的滑桿區（同一個欄位 id，不再有文字欄）
        Field(g, "target（x,y,z｜auto）", FTarget); Field(g, "eye（x,y,z｜auto）", FEye);
        Field(g, "distance（透視；auto）", FDistance); Field(g, "zoom（正交；auto）", FZoom);
        using (g.Row())
        {
            Tri(g, "AO", FAo, "1", "開", "0", "關");
            Tri(g, "陰影", FShadow, "1", "開", "0", "關");
        }
        Field(g, "light_dir（x,y,z；給了 ⇒ 這次渲染換成一盞白光）", FLightDir);

        // 燈光清單：view 一次性渲染只吃 light_dir（契約）；多盞燈要存成設定檔才生效 ⇒ 清單住在這裡、存設定檔時帶走
        string aLights = g.FieldValue(FLights, "");
        var aList = aLights.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        g.Label($"燈光清單（存設定檔用；{aList.Count} 盞）—— ⚠ 一次性渲染吃不到這份，請「存成設定檔」後用它渲染");
        if (aList.Count > 0)
            using (g.Table("#", "dir;color;intensity;shadow"))
                for (int i = 0; i < aList.Count; i++) g.TableRow((i + 1).ToString(CultureInfo.InvariantCulture), aList[i]);
        using (g.Row())
        {
            string aNew = Field(g, "新燈（x,y,z[;#rrggbb[;強度[;shadow 0|1]]]）", FLightNew);
            if (g.Button("加入燈", P + "btn/light-add"))
            {
                if (aNew.Length == 0 || aNew.Split(';').Length > 4) m_Message = "新燈格式：x,y,z[;#rrggbb[;強度[;shadow 0|1]]]（最多四段）⇒ 這次沒加";
                else { aList.Add(aNew); g.SetField(FLights, string.Join("|", aList)); g.SetField(FLightNew, ""); }
            }
            if (g.Button("清空燈", P + "btn/light-clear")) g.SetField(FLights, "");
            if (m_Resolved != null && g.Button("從目前疊加結果帶入", P + "btn/light-from-chain"))
                g.SetField(FLights, string.Join("|", m_Resolved.Lights.Select(LightSpec)));
        }
        if (m_Resolved != null)
        {
            g.Label($"目前疊加結果的燈（{m_Resolved.Lights.Count} 盞）：");
            foreach (SCP_SculptLight l in m_Resolved.Lights) g.Label("  · " + LightSpec(l));
        }

        var aSky = new List<SCP_GuiOption> { new(None, "（沿用）"), new(SCP_SculptRenderProfiles.SkyboxBuiltin, "builtin（渲染器內建天空）"), new("none", "none（純色背景）") };
        foreach (string s in m_Skyboxes) aSky.Add(new SCP_GuiOption(s));
        g.Dropdown("skybox", aSky, None, FSkybox);
        Field(g, "width（px）", FWidth); Field(g, "height（px）", FHeight);
        Tri(g, "自動框住可放大（fit_upscale；展品／region 預設開）", FFitUpscale, "1", "開", "0", "關");

        // 地板：空白／沿用 ＝ 不送 ⇒ 沿用設定檔鏈（沒寫開關的層不改開關 —— 只換貼圖也行）
        g.Label("地板");
        Tri(g, "地板", FFloor, "on", "開", "off", "關");
        var aFloorOpts = new List<SCP_GuiOption> { new(None, "（沿用）"), new(SCP_SculptRenderProfiles.FloorBuiltin, "builtin（量尺網格：每 1／16／64 格）") };
        foreach (string s in m_Floors) aFloorOpts.Add(new SCP_GuiOption(s));
        g.Dropdown("地板貼圖（Sculpture/floors/）", aFloorOpts, None, FFloorTex);
        Field(g, "floor_tile（貼圖每幾格重複一次；網格忽略）", FFloorTile);
        Field(g, "floor_z（地板高度，世界 z）", FFloorZ);
        Tri(g, "範圍", FFloorFull, "1", "整個 0..256 空間", "0", "voxel 外框＋margin");
        Field(g, "floor_margin_ratio（外擴 ＝ 作品最長邊 × 比例；0 ＝ 固定）", FFloorMarginRatio);
        Field(g, "floor_margin（外擴上限格數）", FFloorMargin);
        Field(g, "floor_color（#RRGGBB 色調）", FFloorColor);
        Field(g, "floor_fade（邊緣淡出 0..0.5）", FFloorFade);
        if (m_Resolved != null) g.Label("目前疊加結果的地板：" + FloorSpec(m_Resolved));
    }

    /// <summary>
    /// 一條可沿用的滑桿（見 <see cref="SliderSpec"/>）：欄位空白 ⇒ 滑桿停在鏈上的值並標「沿用中」；
    /// 有值 ⇒ 標覆寫並給一顆「沿用」鈕（清空欄位 ⇒ 下一輪自動重渲）。
    /// </summary>
    void InheritSlider(SCP_Ui g, SliderSpec iSpec)
    {
        double aChain = iSpec.Chain(m_Resolved ?? new SCP_SculptRenderParams());
        bool aOverride = g.FieldValue(iSpec.Id, "").Trim().Length > 0;
        using (g.Row())
        {
            g.Slider(iSpec.Label, iSpec.Id, iSpec.Min, iSpec.Max, aChain, iSpec.Format);
            if (aOverride)
            {
                if (g.Button("沿用", P + "btn/inherit/" + iSpec.Name)) g.SetField(iSpec.Id, "");
                g.Note("覆寫中（鏈上是 " + SCP_Ui.FormatSlider(aChain, iSpec.Format) + "）");
            }
            else g.Note("沿用中（鏈）");
        }
    }

    /// <summary>
    /// 工具列的「切換 正交／透視」（TASK-0504）：鈕只記一筆，內容區讀完設定檔鏈之後才切 ——
    /// 欄位沿用中（空白）時要看鏈上是哪一種，而鏈在內容區才讀（工具列先畫，文字模式第一輪 m_Resolved 還是空的）。
    /// </summary>
    bool m_FlipProjection;

    void ProjectionButton(SCP_Ui g)
    {
        if (PersonalSpace(g) && SelectedWork(g).Length == 0) return;   // 同匯出鈕：個人區沒選作品就沒有對象
        if (g.Button("切換 正交／透視", P + "btn/proj-flip")) m_FlipProjection = true;
    }

    void ApplyProjectionFlip(SCP_Ui g)
    {
        if (!m_FlipProjection) return;
        m_FlipProjection = false;
        string aNow = g.FieldValue(FProjection + "/value", None);
        if (aNow == None) aNow = m_Resolved?.Projection == SCP_SculptProjection.Perspective ? "perspective" : "orthographic";
        g.SetField(FProjection + "/value", aNow == "perspective" ? "orthographic" : "perspective");
        RequestRender("切換投影 → " + g.FieldValue(FProjection + "/value"), iForce: true);
    }

    void Orbit(SCP_Ui g, string iLabel, string iField, double iDelta, string iPersona)
    {
        if (!g.Button(iLabel, P + "btn/orbit/" + iLabel.Replace(" ", "").Replace("+", "p").Replace("-", "m"))) return;
        double aBase = iField == FYaw ? (m_Resolved?.YawDeg ?? 45) : (m_Resolved?.PitchDeg ?? 30);
        string aCur = g.FieldValue(iField, "").Trim();
        if (aCur.Length > 0 && !double.TryParse(aCur, NumberStyles.Float, CultureInfo.InvariantCulture, out aBase))
        { m_Message = $"{iField} 現值不是數字（{aCur}）⇒ 這次沒轉"; return; }
        double aNew = aBase + iDelta;
        if (iField == FYaw) { aNew %= 360; if (aNew < 0) aNew += 360; }
        else aNew = Math.Max(-89, Math.Min(89, aNew));
        g.SetField(iField, aNew.ToString("0.##", CultureInfo.InvariantCulture));
        RunManual(g, $"環繞 {iLabel} → {aNew.ToString("0.##", CultureInfo.InvariantCulture)}", iPersona);
    }

    /// <summary>手動欄位 → view 參數（空白不送）。</summary>
    Dictionary<string, string> ManualViewArgs(SCP_Ui g)
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string iArg, string iId) { string v = g.FieldValue(iId, "").Trim(); if (v.Length > 0) a[iArg] = v; }
        void PutSel(string iArg, string iId) { string v = g.FieldValue(iId + "/value", None); if (v != None && v.Length > 0) a[iArg] = v; }
        Put("region", FRegion); Put("exclude_color", FExclude);
        PutSel("projection", FProjection);
        Put("yaw", FYaw); Put("pitch", FPitch); Put("roll", FRoll);
        Put("target", FTarget); Put("eye", FEye); Put("distance", FDistance); Put("fov", FFov); Put("zoom", FZoom);
        Put("ambient", FAmbient); PutSel("ao", FAo); PutSel("shadow", FShadow); Put("light_dir", FLightDir);
        PutSel("skybox", FSkybox); Put("skybox_yaw", FSkyYaw); Put("skybox_tilt", FSkyTilt); Put("width", FWidth); Put("height", FHeight);
        PutSel("fit_upscale", FFitUpscale);
        PutSel("floor", FFloor); PutSel("floor_texture", FFloorTex); Put("floor_tile", FFloorTile); Put("floor_z", FFloorZ);
        PutSel("floor_full_grid", FFloorFull); Put("floor_margin", FFloorMargin); Put("floor_margin_ratio", FFloorMarginRatio); Put("floor_color", FFloorColor); Put("floor_fade", FFloorFade);
        return a;
    }

    /// <summary>
    /// 手動渲染（含環繞／切投影／自動渲染）：手動欄位 ＋ **目前對象**（見檔頭）—— 對象說了算，region 欄**不在這裡**改對象。
    /// <para>🩸 舊規則是「region 有填 ⇒ 對象改成那個 region」：自動渲染上線後（每動一下滑桿就走這裡），
    /// 按了「全景」再拖一下 yaw ⇒ 對象默默跳回選展品時帶進來的那個 region（2026-10-02 文字模式實測）。
    /// ⇒ 改成 region 欄「編輯完成」那一刻才換對象（<see cref="RegionCommitted"/>）；選展品 ⇒ 對象＝那件展品。</para>
    /// </summary>
    void RunManual(SCP_Ui g, string iLabel, string iPersona)
    {
        Dictionary<string, string> a = ManualRequest(g, iPersona, out string aSubject);
        // 正在跑 ⇒ 排一筆（畫完用最新參數補畫），⛔ 不丟掉這次的要求
        if (m_Job != null) { RequestRender(iLabel, iForce: true); return; }
        m_LastStartedSig = RequestSig(a, iPersona);
        RunView(g, iLabel + "｜" + SubjectText(aSubject), a, iPersona, aSubject);
    }

    /// <summary>手動路徑這一刻會送出的參數（手動欄位 ＋ 目前對象，規則見 <see cref="RunManual"/>）。純讀，不啟動任何東西。</summary>
    Dictionary<string, string> ManualRequest(SCP_Ui g, string iPersona, out string oSubject)
    {
        Dictionary<string, string> a = ManualViewArgs(g);
        a.Remove("region");   // 範圍由對象決定（見上）
        string aSubject = g.FieldValue(SSubject, SubjectFull);
        if (aSubject.StartsWith(SubjectExhibit, StringComparison.Ordinal)) a["exhibit"] = aSubject.Substring(SubjectExhibit.Length);
        else if (aSubject.StartsWith(SubjectRegion, StringComparison.Ordinal)) a["region"] = aSubject.Substring(SubjectRegion.Length);
        AddWorkTarget(g, a);
        oSubject = aSubject;
        return a;
    }

    /// <summary>
    /// region 欄編輯完成 ⇒ 換對象：有填 ⇒ 那個 region（例外：目前是展品、而 region 就是那件展品的 ⇒ 仍是展品，
    /// 用展品的 preset 打光／鏡頭）；清空 ⇒ 原本看的是 region 才回全景（⛔ 不把正在看的展品默默換成整個空間）。
    /// </summary>
    void RegionCommitted(SCP_Ui g)
    {
        string aRegion = g.FieldValue(FRegion, "").Trim();
        string aSubject = g.FieldValue(SSubject, SubjectFull);
        string aNew = aSubject;
        if (aRegion.Length > 0)
        {
            string? aExId = aSubject.StartsWith(SubjectExhibit, StringComparison.Ordinal) ? aSubject.Substring(SubjectExhibit.Length) : null;
            var aEx = aExId != null ? m_Exhibits.Find(e => e.Id == aExId) : default;
            aNew = aEx.Id != null && S(aEx.Data, "region") == aRegion ? aSubject : SubjectRegion + aRegion;
        }
        else if (aSubject.StartsWith(SubjectRegion, StringComparison.Ordinal)) aNew = SubjectFull;
        if (aNew != aSubject) g.SetField(SSubject, aNew);
    }

    /// <summary>一組要求的讀數（鍵排序後串起來）—— 比對「這張跟上一張是不是同一組參數」。</summary>
    static string RequestSig(Dictionary<string, string> iArgs, string iPersona)
        => "persona=" + iPersona + ";" + string.Join(";", iArgs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value));

    // ===========================================================
    // 區塊職責：自動渲染（見檔頭）—— 每輪 DrawContent 開頭跑一次：判斷要不要畫、合併連發、節流。
    // 物理意義：觸發讀數 ＝ 非文字參數（滑桿／下拉）＋ persona ＋ 設定檔鏈的疊加結果；對象另外比（工作寫回的不算）。
    //           文字欄只認「編輯完成」事件（SCP_Ui.Committed）。滑桿兩條都認：拖曳中（節流）與放開（一定補最後一張）。
    // 數值影響：同時間最多一張；視窗模式兩張之間 ≥ AutoMinInterval；參數跟上一張一模一樣 ⇒ 不畫（除非強制）。
    //           文字模式（每道指令一個新頁面實例）⇒ 進頁只在**還沒有圖**時畫（⛔ 不讓每一道 `ui` 指令都花幾秒重渲）。
    // ===========================================================
    void AutoRender(SCP_Ui g, string iPersona)
    {
        if (g.Committed(FRegion)) RegionCommitted(g);   // ⚠ 先換對象再取讀數 ⇒ 這一輪就畫新對象
        string aTrigger = TriggerSig(g, iPersona);
        string aSubject = g.FieldValue(SSubject, SubjectFull);
        if (m_OpenRender)
        {
            m_OpenRender = false;
            string aPath = g.FieldValue(SViewPath, "");
            if (SCP_GuiHost.RedrawsContinuously || aPath.Length == 0 || !File.Exists(aPath)) RequestRender("進頁", iForce: true);
        }
        if (m_PrevTrigger != null && aTrigger != m_PrevTrigger) RequestRender("參數變了");
        if (m_PrevSubject != null && aSubject != m_PrevSubject && aSubject != m_SubjectFromJob) RequestRender("換了對象");
        foreach (string aId in CommitIds()) if (g.Committed(aId)) { RequestRender("編輯完成 " + aId.Substring(P.Length)); break; }
        m_PrevTrigger = aTrigger;
        m_PrevSubject = aSubject;
        m_SubjectFromJob = null;

        // GPU 即時預覽（TASK-0472）：畫面每幀由 DrawGpuResult 跟著欄位走 ⇒ ⛔ 不 spawn 出圖（要求照收、直接清掉）
        if (UseGpu(g)) { m_AutoWanted = false; m_AutoForce = false; m_AutoWhy = ""; return; }
        if (!g.FoldOpen(FResultFold)) return;   // 「渲染結果」收起來 ⇒ 不畫；要求留著，展開那一輪再畫（Tim 2026-10-12）
        if (!m_AutoWanted || m_Job != null) return;   // 正在跑 ⇒ 等它跑完（PumpJob 收掉之後的那一輪再來）
        if (SCP_GuiHost.RedrawsContinuously && DateTime.UtcNow - m_LastAutoStartUtc < AutoMinInterval) return;
        Dictionary<string, string> a = ManualRequest(g, iPersona, out _);
        bool aSame = RequestSig(a, iPersona) == m_LastStartedSig;
        string aWhy = m_AutoWhy;
        bool aForce = m_AutoForce;
        m_AutoWanted = false; m_AutoForce = false; m_AutoWhy = "";
        if (aSame && !aForce) return;                  // 跟上一張同一組參數（例：環繞鈕已經畫過）⇒ 不再畫
        m_LastAutoStartUtc = DateTime.UtcNow;
        RunManual(g, "自動渲染（" + aWhy + "）", iPersona);
    }

    /// <summary>記一筆「要畫」（合併：多次要求只留一筆，原因串起來；強制旗標取 OR）。</summary>
    void RequestRender(string iWhy, bool iForce = false)
    {
        m_AutoWanted = true;
        m_AutoForce |= iForce;
        if (!m_AutoWhy.Contains(iWhy, StringComparison.Ordinal)) m_AutoWhy = m_AutoWhy.Length == 0 ? iWhy : m_AutoWhy + "＋" + iWhy;
    }

    /// <summary>會發「編輯完成」的參數欄位：文字欄 ＋ 滑桿。</summary>
    static IEnumerable<string> CommitIds()
    {
        foreach (string s in s_TextParamIds) yield return s;
        foreach (SliderSpec s in s_CamSliders) yield return s.Id;
        foreach (SliderSpec s in s_LightSliders) yield return s.Id;
        foreach (SliderSpec s in s_SkySliders) yield return s.Id;
    }

    /// <summary>觸發讀數：滑桿＋下拉（⛔ 不含文字欄 —— 那條只認編輯完成）＋ persona ＋ 設定檔鏈的疊加結果。</summary>
    string TriggerSig(SCP_Ui g, string iPersona)
    {
        var sb = new StringBuilder();
        sb.Append("persona=").Append(iPersona).Append(';');
        foreach (string aId in CommitIds())
            if (Array.IndexOf(s_TextParamIds, aId) < 0) sb.Append(aId).Append('=').Append(g.FieldValue(aId, "").Trim()).Append(';');
        foreach (string aSel in new[] { FProjection, FAo, FShadow, FSkybox, FFitUpscale, FFloor, FFloorTex, FFloorFull })
            sb.Append(aSel).Append('=').Append(g.FieldValue(aSel + "/value", None)).Append(';');
        // 設定檔鏈變了（「使用」別組、存了新設定檔、換層）⇒ 圖也該跟著換
        sb.Append("chain=").Append(string.Join(">", m_ResolvedLayers)).Append('|');
        if (m_Resolved != null) foreach (string s in Describe(m_Resolved)) sb.Append(s).Append('|');
        return sb.ToString();
    }

    /// <summary>對象 → 給人看的字（結果區標頭用）。</summary>
    string SubjectText(string iSubject)
    {
        if (iSubject.StartsWith(SubjectWork, StringComparison.Ordinal)) return "個人作品 " + iSubject.Substring(SubjectWork.Length);
        if (iSubject.StartsWith(SubjectExhibit, StringComparison.Ordinal))
        {
            string aId = iSubject.Substring(SubjectExhibit.Length);
            var aEx = m_Exhibits.Find(e => e.Id == aId);
            return aEx.Id != null ? $"展品《{S(aEx.Data, "title")}》（{aId}）" : $"展品 {aId}（清單裡找不到）";
        }
        if (iSubject.StartsWith(SubjectRegion, StringComparison.Ordinal)) return "region " + iSubject.Substring(SubjectRegion.Length);
        return "全景";
    }

    // ===========================================================
    // 區塊職責：view —— spawn `cmd sculpture --arg op=view --arg out=<暫存>/view.png …`，讀回 path／renderer／layers。
    // ===========================================================
    void RunView(SCP_Ui g, string iLabel, Dictionary<string, string> iArgs, string iPersona, string? iSubject = null)
    {
        // GPU 即時預覽：只換對象（「渲染展品」「全景」照樣有效），畫面由 DrawGpuResult 跟上 ⇒ ⛔ 不 spawn
        if (UseGpu(g)) { if (iSubject != null) g.SetField(SSubject, iSubject); m_Message = iLabel + "（GPU 即時預覽）"; return; }
        string aOut = Path.Combine(PageTempDir, "view.png");
        var aArgs = new Dictionary<string, string>(iArgs, StringComparer.Ordinal) { ["op"] = "view", ["out"] = aOut };
        AddWorkTarget(g, aArgs);
        if (iPersona.Length > 0 && !aArgs.ContainsKey("persona")) aArgs["persona"] = iPersona;
        Start(g, iLabel, () =>
        {
            Directory.CreateDirectory(PageTempDir);
            CliRun r = RunCli(aArgs);
            var o = new Outcome { Log = r.Log(iLabel) };
            if (r.Exit != 0) return o;
            string aPath = r.Value("path", aOut);
            if (!File.Exists(aPath)) { o.Log += "\n[注意] exit 0 但圖不在：" + aPath; return o; }
            o.Fields[SViewPath] = aPath;
            if (iSubject != null) o.Fields[SSubject] = iSubject;
            o.Fields[SRenderer] = r.Value("renderer", "（沒回報）");
            o.Fields[SLayers] = r.Value("layers", "（沒回報）");
            o.Fields[SViewInfo] = $"{DateTime.Now:HH:mm:ss}　{iLabel}　{r.Value("width", "?")}×{r.Value("height", "?")}"
                                  + $"　voxel {r.Value("visible_voxels", "?")}／{r.Value("total_voxels", "?")}"
                                  + (r.Value("out_of_range_colors", "0") is var oor && oor != "0" ? $"　越界色 {oor}" : "")
                                  + (r.Value("sha256", "").Length >= 12 ? "　sha256 " + r.Value("sha256", "").Substring(0, 12) : "");
            return o;
        });
    }

    // ===========================================================
    // 區塊職責：GPU 即時預覽（TASK-0472）—— 視窗宿主登記了雕刻畫家 ⇒ 不 spawn：場景（voxel＋參數）在行程內準備一次、
    //          網格由宿主快取在 GPU 上；換視角（拖曳 yaw、滾輪倍率、相機滑桿）只改相機欄位、當幀重畫。
    // 物理意義：三層快取 —— ① voxel：作品／展品／region／排除色沒變且內容逐顆相同 ⇒ 沿用**同一個清單物件**（宿主網格快取的鍵）；
    //          ② 參數：其他欄位變了 ⇒ 背景重新準備場景（同一支 Cmd_Sculpture.TryPrepareView，疊層規則只有一份），網格照用；
    //          ③ 相機（yaw／pitch／roll／fov／zoom／distance＋檢視倍率）：每幀套在參數副本上，什麼都不重建。
    //          場景準備時**拿掉相機鍵** ⇒ plan 手上是鏈／展品給的相機；欄位有值才覆寫（清空 ＝ 回到鏈上的值）。
    // 數值影響：宿主不能畫（文字模式、SENATE_SCULPT_GPU=off）、或宿主回報這一幀失敗 ⇒ 退回 spawn 出圖，原因印在頁面上。
    //          沒有開關（Tim 2026-10-12「取消 GPU 即時渲染的選項，直接根據渲染結果是否摺疊來決定是否渲染」）：
    //          「渲染結果」收起來 ⇒ 不準備場景、不畫、不 spawn；展開 ⇒ 照最新參數畫。
    //          合併同色面一律開（MergeFaces）；CLI `op=view` 不受影響（它不開）。
    // ===========================================================
    const string GpuKey = "sculpture/gpu-view";
    const string FResultFold = P + "fold/result";
    const string FViewScale = P + "f/view_scale";
    static readonly string[] s_CamKeys = { "yaw", "pitch", "roll", "fov", "zoom", "distance" };
    static readonly string[] s_VoxelKeys = { "work", "exhibit", "region", "exclude_color", "draft", "links", "hide_links" };
    static bool GpuPainterAvailable => SCP_GuiGpuViews.CanPaint(typeof(SCP_SculptGpuScene));

    sealed record PlanSnap(string SceneSig, string VoxelSig, IReadOnlyList<SCP_SculptVoxel> Voxels, SCP_SculptRenderParams Params,
                           string Layers, int Total, double Ms, bool VoxelsReused);
    volatile PlanSnap? m_Plan;
    Task<(PlanSnap? Snap, string? Error)>? m_PlanJob;
    string m_PlanJobSig = "";
    string? m_PlanError;
    string m_GpuSceneSig = "";
    long m_GpuPut;

    /// <summary>這一幀要不要走 GPU：宿主能畫 ＋ 宿主沒有回報「這一版畫失敗」。</summary>
    bool UseGpu(SCP_Ui g)
    {
        if (!GpuPainterAvailable) return false;
        return !(SCP_GuiGpuViews.TryGetStatus(GpuKey, out SCP_GuiGpuStatus s) && s.Version == m_GpuPut && s.Error != null);
    }

    /// <summary>畫 GPU 結果區；不走 GPU（含失敗）⇒ 回 false，呼叫端畫舊的檔案結果區。</summary>
    bool DrawGpuResult(SCP_Ui g, string iPersona)
    {
        if (!GpuPainterAvailable) { g.Note("這個宿主沒有 GPU 即時預覽（文字模式，或環境變數 SENATE_SCULPT_GPU=off）⇒ 用 spawn 出圖。"); return false; }
        if (SCP_GuiGpuViews.TryGetStatus(GpuKey, out SCP_GuiGpuStatus aSt) && aSt.Version == m_GpuPut && aSt.Error != null)
        { g.Note("GPU 預覽這一幀失敗 ⇒ 退回 spawn 出圖：" + aSt.Error); return false; }

        // 收起來 ⇒ 不準備場景、不畫（展開才照最新參數畫；拖曳／滾輪說明在圖下方）
        using var aFold = g.Fold("渲染結果（GPU 即時；右鍵／中鍵拖曳旋轉、左鍵拖曳水平平移、滾輪縮放）", FResultFold, iDefaultOpen: true);
        if (!aFold.Open) return true;

        Dictionary<string, string> aReq = ManualRequest(g, iPersona, out string aSubject);
        if (iPersona.Length > 0) aReq["persona"] = iPersona;
        PartsSel? aPartsSel = PartsRequest(aReq);   // TASK-0509：子模型分開畫 ⇒ 子模型的開關改成每幀的顯示
        var aCam = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string k in s_CamKeys) if (aReq.Remove(k, out string? v)) aCam[k] = v;
        string aSceneSig = RequestSig(aReq, iPersona);
        // 分開畫時關聯選取不改 voxel（主作品只帶自己的格）⇒ 不進 voxel 簽章（切關聯不換對象、不重置鏡頭）
        string aVoxelSig = string.Join(";", s_VoxelKeys.Where(k => aPartsSel == null || (k != "links" && k != "hide_links"))
                                                     .Select(k => k + "=" + (aReq.TryGetValue(k, out string? v) ? v : "")))
                           + (aPartsSel != null ? ";parts=1" : "");
        PumpPlan();
        PlanSnap? aPlan = m_Plan;
        if ((aPlan == null || aPlan.SceneSig != aSceneSig) && m_PlanJob == null && m_PlanJobSig != aSceneSig + "|failed") StartPlan(aReq, aSceneSig, aVoxelSig);

        if (aPlan == null)
        {
            // 還沒有圖 ⇒ 說明照舊放在這裡（有圖時文字資訊一律在圖下方，Tim 2026-10-12）
            g.Label("目前對象：" + SubjectText(aSubject));
            if (m_PlanError != null) g.Note("[場景準備失敗] " + m_PlanError);
            g.Note(m_PlanError == null ? "第一次準備場景中…" : "沒有可畫的場景。");
            return true;
        }

        // ③0 作品載入後、切換正交／透視後各重置一次鏡頭（Tim 2026-10-12，等同按「重置鏡頭」）：看的是**載入的場景**，不是欄位 ——
        //     換對象／換投影到新場景準備好之間畫的還是舊場景，那段時間框住、釘住的是舊的；等新場景到了才重置，它才會被重新框住。
        //     只比作品／展品／region＋投影 ⇒ 同一件作品雕刻、換其他參數都不會重置。投影由工具列鈕或手動區下拉改，兩條路都走這裡。
        string aLoaded = RefitKey(aPlan.VoxelSig, aPlan.Params.Projection);
        if (aLoaded != m_RefitLoaded)
        {
            m_RefitLoaded = aLoaded;
            m_Pin = null;
            m_PutPinned = true;   // 上一版是舊場景 ⇒ 它交回的鏡頭不准拿來釘（這一幀下面會照新場景重放一版自動框的）
            if (g.FieldValue(FViewScale, "1") != "1") g.SetField(FViewScale, "1");
        }

        // ③ 相機：每幀套在副本上
        SCP_SculptRenderParams p = aPlan.Params.Clone();
        // ③00 子模型的顯示（TASK-0509）：每幀照開關算，⛔ 不動場景的來源與矩陣 ⇒ 渲染器不重建網格
        string aVisSig = "";
        string? aPartsNote = null;
        if (p.Parts != null)
        {
            PartsSel aSel = aPartsSel ?? new PartsSel("", "", "", "");
            SCP_SculptParts.Shown aShown = SCP_SculptParts.Visibility(p.Parts, SCP_SculptLinkSelection.Parse(aSel.Links, aSel.HideLinks),
                                                                      SCP_SculptModelSelection.Parse(aSel.Models, aSel.HideModels));
            p.PartVisible = aShown.Visible;
            p.OwnVisible = aShown.Own;
            aVisSig = ";vis=" + (aShown.Own ? "1" : "0") + new string(aShown.Visible.Select(b => b ? '1' : '0').ToArray());
            var aNotes = new List<string>(p.Parts.Notes);
            if (aShown.Unknown.Count > 0) aNotes.Add("開關點名、但這次沒有：" + string.Join("、", aShown.Unknown));
            int aShownCount = aShown.Visible.Count(b => b);
            aPartsNote = $"子模型分開畫：{p.Parts.Sources.Count} 份網格、{p.Parts.Instances.Count} 個實例（顯示 {aShownCount}"
                         + (aShown.Own ? "" : "，主作品自己的格藏起來") + "）—— 開關只切顯示、不重建網格；跟主作品貼合處各自完整（不剔除）"
                         + (aNotes.Count > 0 ? "　⚠ " + string.Join("；", aNotes) : "");
        }
        p.MergeFaces = true;
        string? aBad = ApplyCamera(p, aCam, g.FieldValue(FViewScale, "1"));
        if (aBad != null) { g.Note("[相機欄位] " + aBad); return true; }

        // ③b 鏡頭只框一次（TASK-0501）：換對象／換投影／按「重置鏡頭」才讓渲染器框；框完那一刻把它交回的鏡頭釘住，之後照原樣送回去
        string aPinKey = aSubject + "|" + p.Projection;
        if (aPinKey != m_PinKey) { m_Pin = null; m_PinKey = aPinKey; }
        if (SCP_GuiGpuViews.TryGetStatus(GpuKey, out SCP_GuiGpuStatus aSt0) && aSt0.Version == m_GpuPut && aSt0.Error == null && aSt0.Result is SCP_SculptResolvedCamera aCamBack)
        {
            m_ViewCam = aCamBack;
            if (m_Pin == null && m_PutPinKey == aPinKey && !m_PutPinned) m_Pin = CopyCamera(aCamBack);   // 這一版是自動框的 ⇒ 釘住它
        }
        bool aPinned = m_Pin != null && ApplyPin(p, m_Pin, aCam);

        string aCamSig = string.Join(";", aCam.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value)) + ";vs=" + p.ViewScale.ToString("R", CultureInfo.InvariantCulture)
                         + (aPinned ? ";pin=" + PinSig(m_Pin!) : ";pin=auto");
        // ③c 帆（TASK-0503）：持續模擬開著 ⇒ 每 SailUpdateSeconds 秒（不是每幀；Tim「不用頻繁模擬」）照真實經過的時間把 rig 往前推（固定 dt 補步），
        //     一次最多花約 SailBudgetMs 毫秒的步數 —— 帆多、算不完就慢動作（⛔ 不改 dt、不拖垮畫面）；推過才換一版場景
        string aSailSig = "";
        string? aSailInfo = null;   // 圖畫完才印（文字資訊在圖下方）
        if (p.SailRig is SCP_SculptSailRig aRig)
        {
            long aNow = Stopwatch.GetTimestamp();
            bool aLive = g.ToggleValue(TSailLive, true);
            double aSince = (aNow - m_SailTick) / (double)Stopwatch.Frequency;
            if (!ReferenceEquals(m_SailRigSeen, aRig) || !aLive) { m_SailRigSeen = aRig; m_SailTick = aNow; }
            else if (aSince >= SailUpdateSeconds)
            {
                int aMax = aRig.LastStepMs > 0 ? Math.Clamp((int)(SailBudgetMs / aRig.LastStepMs), 1, 12) : 1;
                aRig.Advance(aSince, aMax);
                m_SailTick = aNow;
            }
            p.Sails = aRig.SailDrawGeoms();   // TASK-0530：帶貼圖的帆展開成每色一份
            p.SailRopes = aRig.RopeGeoms(true);
            aSailSig = ";sail=" + aRig.Steps.ToString(CultureInfo.InvariantCulture);
            aSailInfo = $"帆：模擬到 {aRig.Time:0.00} 秒（{aRig.Steps:N0} 步，每步 {aRig.LastStepMs:0.###} ms）"
                        + (aLive ? $"　持續模擬中（每 {SailUpdateSeconds:0.#} 秒更新一次、一次最多約 {SailBudgetMs:0} ms；算不完就慢動作）" : "　持續模擬關著 ⇒ 停在這一刻");
        }
        string aGpuSig = RuntimeHelpers_Id(aPlan) + "|" + aCamSig + aSailSig + aVisSig;
        if (aGpuSig != m_GpuSceneSig || !SCP_GuiGpuViews.Has(GpuKey))
        {
            m_GpuPut = SCP_GuiGpuViews.Put(GpuKey, new SCP_SculptGpuScene(aPlan.Voxels, p), p.Width, p.Height);
            m_GpuSceneSig = aGpuSig;
            m_PutPinKey = aPinKey; m_PutPinned = aPinned;
        }
        SCP_GuiPointer? aPtr = g.ImageInteractive(SCP_GuiGpuViews.Ref(GpuKey), ViewSide(g), "雕刻即時預覽", P + "img/gpu");
        if (aPtr != null)
        {
            // 右鍵／中鍵：繞著目前的中心轉（像 Blender 抓著物體轉）—— 拖過整張圖寬 ＝ yaw 180°、整張圖高 ＝ pitch 180°；欄位空白 ⇒ 從鏈上的值起算
            double rx = aPtr.RightDragX + aPtr.MiddleDragX, ry = aPtr.RightDragY + aPtr.MiddleDragY;
            if (rx != 0 || ry != 0)
            {
                double aYaw0 = FieldOr(g, FYaw, aPlan.Params.YawDeg), aPitch0 = FieldOr(g, FPitch, aPlan.Params.PitchDeg);
                var (aYaw, aPitch) = OrbitAngles(aYaw0, aPitch0, rx, ry);
                g.SetField(FYaw, aYaw.ToString("0.#", CultureInfo.InvariantCulture));
                g.SetField(FPitch, aPitch.ToString("0.#", CultureInfo.InvariantCulture));
            }
            // 左鍵：平移畫面中心 —— 只在地面上動（世界 X／Y），高度不變；要先有釘住的中心（第一幀框完才有）
            if ((aPtr.DragX != 0 || aPtr.DragY != 0) && m_Pin != null && m_ViewCam != null)
                PanPin(m_Pin, m_ViewCam, aPtr.DragX, aPtr.DragY, p.ViewScale);
            if (aPtr.Wheel != 0)
            {
                var (aMin, aMax, _) = ViewScaleRange(PreviewCfg(g));
                double vs = WheelViewScale(p.ViewScale, aPtr.Wheel, aMin, aMax);
                g.SetField(FViewScale, vs.ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        using (g.Row())
        {
            var (aLo, aHi, _) = ViewScaleRange(PreviewCfg(g));
            g.Label($"檢視倍率 ×{p.ViewScale:0.###}（滾輪範圍 {aLo:0.###}–{aHi:0.###}）");
            if (g.Button("倍率歸 1", P + "btn/view-scale-reset")) g.SetField(FViewScale, "1");
            // 重新框住（像 Blender 的 View All）：丟掉釘住的中心與距離、倍率回 1，下一幀渲染器重框一次
            if (g.Button("重置鏡頭（重新框住）", P + "btn/camera-refit")) { m_Pin = null; g.SetField(FViewScale, "1"); }
        }
        using (var aPrev = g.Fold("預覽設定（檢視倍率範圍、畫面大小）", P + "fold/preview-settings", iDefaultOpen: false))
        {
            if (aPrev.Open)
            {
                g.Note("ViewScaleMin／ViewScaleMax：滾輪縮放的下限與上限（預設 " + SculptPreviewSettings.DefaultViewScaleMin + "–" + SculptPreviewSettings.DefaultViewScaleMax
                       + "；上限最多 " + SculptPreviewSettings.RendererViewScaleMax + "）。只管滾輪，現在的倍率不會被改。");
                g.Note("ViewSide：渲染畫面在頁面上的顯示邊長（px，" + SculptPreviewSettings.MinViewSide + "–" + SculptPreviewSettings.MaxViewSide + "，預設 " + SculptPreviewSettings.DefaultViewSide
                       + "）。只管顯示；**輸出解析度**是「相機與光影（手動）」的 width／height —— 顯示比解析度大會被放大而變糊。");
                foreach (string n in DrawCfg(g, PreviewCfg(g), PreviewCfgKey, SPreviewCfg, ref m_PreviewCfgJson)) g.Note("（自動繪製）" + n);
            }
        }
        if (ViewScaleRange(PreviewCfg(g)).Why is string aRangeWhy) g.Note("[預覽設定] " + aRangeWhy);
        if (DisplaySide(PreviewCfg(g)).Why is string aSideWhy) g.Note("[預覽設定] " + aSideWhy);
        // 文字資訊在圖下方（Tim 2026-10-12）
        g.Label("目前對象：" + SubjectText(aSubject));
        if (m_PlanError != null) g.Note("[場景準備失敗] " + m_PlanError);
        if (m_PlanJob != null) g.Note("準備場景中…（作品／參數變了；畫面是上一版）");
        g.Label("layers：" + aPlan.Layers);
        g.Label($"場景準備 {aPlan.Ms:0} ms（voxel {(aPlan.VoxelsReused ? "沿用同一份" : "新的一份")}，{aPlan.Voxels.Count:N0}／{aPlan.Total:N0}）");
        if (aSailInfo != null) g.Note(aSailInfo);
        if (aPartsNote != null) g.Note(aPartsNote);
        g.Note(m_Pin != null
            ? $"鏡頭已釘住：中心 ({m_Pin.TargetX:0.#}, {m_Pin.TargetY:0.#}, {m_Pin.TargetZ:0.#})" + (p.Projection == SCP_SculptProjection.Perspective ? $"、距離 {m_Pin.Distance:0.#}" : $"、縮放 {m_Pin.Zoom:0.###}")
              + " —— 轉、拖、縮放都不再重新框住（換對象／換投影／按重置才重框）"
            : "鏡頭這一幀自動框住中（框完就釘住）");
        if (SCP_GuiGpuViews.TryGetStatus(GpuKey, out aSt) && aSt.Error == null && aSt.Info.Length > 0) g.Note(aSt.Info);
        return true;
    }

    // ── TASK-0503：持續模擬的帆 —— 上一幀的時間戳與那時推的是哪一個 rig（換了 plan ⇒ 新的 rig 從它自己的那一刻起算，不補舊的時間）──
    long m_SailTick;
    SCP_SculptSailRig? m_SailRigSeen;
    /// <summary>持續模擬多久更新一次（秒）與一次最多花多少毫秒算（Tim 2026-10-11「不用頻繁模擬 模擬頻率可以降低」）。</summary>
    const double SailUpdateSeconds = 0.1, SailBudgetMs = 8;

    /// <summary>子模型的開關（TASK-0509）：跟 CLI 同名的四格，分開畫時改成每幀的顯示。</summary>
    sealed record PartsSel(string Links, string HideLinks, string Models, string HideModels);

    /// <summary>
    /// GPU 預覽的請求改成子模型分開畫（TASK-0509）：個人作品、沒有 region／exclude_color ⇒ 送 parts=1，回這一幀的開關；否則回 null（照舊合併）。
    /// models 兩格不送（模型只切顯示）；links 兩格只在作品有繩或帆時才送 —— 繩與帆照關聯選取算（那一半這次不拆：切關聯會重算繩，但不碰任何網格）。
    /// </summary>
    PartsSel? PartsRequest(Dictionary<string, string> ioReq)
    {
        if (!ioReq.TryGetValue("work", out string? aWork) || aWork.Length == 0 || aWork.StartsWith("__", StringComparison.Ordinal)) return null;
        if (ioReq.ContainsKey("region") || ioReq.ContainsKey("exclude_color")) return null;
        bool aKeepLinks = (RopeRows(aWork).Rows?.Count ?? 0) > 0 || (SailRows(aWork).Rows?.Count ?? 0) > 0;
        string Take(string k, bool iRemove) { string v = ioReq.TryGetValue(k, out string? x) ? x : ""; if (iRemove) ioReq.Remove(k); return v; }
        var aSel = new PartsSel(Take("links", !aKeepLinks), Take("hide_links", !aKeepLinks), Take("models", true), Take("hide_models", true));
        ioReq["parts"] = "1";
        return aSel;
    }

    /// <summary>上一次「載入後重置鏡頭」時的場景對象（見 <see cref="PlanSubject"/>）；"\0" ＝ 這個頁面實例還沒載入過。</summary>
    string m_RefitLoaded = "\0";

    /// <summary>「這一版場景要不要重置鏡頭」的鍵（純函數）：對象（<see cref="PlanSubject"/>）＋投影 —— 任一個變了就重置一次。</summary>
    public static string RefitKey(string iVoxelSig, SCP_SculptProjection iProjection) => PlanSubject(iVoxelSig) + "|" + iProjection;

    /// <summary>場景屬於哪個對象（純函數）：voxel 簽章裡的 work／exhibit／region 三格 —— 草稿、關聯開關、排除色變了都不算換對象。</summary>
    public static string PlanSubject(string iVoxelSig)
        => string.Join(";", iVoxelSig.Split(';').Where(k => k.StartsWith("work=", StringComparison.Ordinal)
                                                           || k.StartsWith("exhibit=", StringComparison.Ordinal)
                                                           || k.StartsWith("region=", StringComparison.Ordinal)));

    // ── TASK-0501：釘住的鏡頭（只在視窗行程裡活著 —— 視窗以外的宿主不走 GPU 預覽）──
    /// <summary>釘住的鏡頭（中心＋距離／縮放）；null ＝ 下一幀讓渲染器自動框。</summary>
    SCP_SculptResolvedCamera? m_Pin;
    /// <summary>釘住的是哪個對象＋投影（換了就丟掉）。</summary>
    string m_PinKey = "";
    /// <summary>最近一幀渲染器交回的鏡頭（平移用它的畫面方向）。</summary>
    SCP_SculptResolvedCamera? m_ViewCam;
    /// <summary>最近一次 Put 的那一版：屬於哪個對象＋投影、是不是已經釘住送出去的（只有「自動框的那一版」交回的鏡頭才拿來釘）。</summary>
    string m_PutPinKey = "";
    bool m_PutPinned;

    static double FieldOr(SCP_Ui g, string iId, double iFallback)
    {
        string v = g.FieldValue(iId, "").Trim();
        return v.Length > 0 && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : iFallback;
    }

    public static SCP_SculptResolvedCamera CopyCamera(SCP_SculptResolvedCamera c) => new()
    {
        Projection = c.Projection, TargetX = c.TargetX, TargetY = c.TargetY, TargetZ = c.TargetZ, Distance = c.Distance, Zoom = c.Zoom,
        WorldPerImageWidth = c.WorldPerImageWidth, WorldPerImageHeight = c.WorldPerImageHeight,
        RightX = c.RightX, RightY = c.RightY, RightZ = c.RightZ, UpX = c.UpX, UpY = c.UpY, UpZ = c.UpZ, ForwardX = c.ForwardX, ForwardY = c.ForwardY, ForwardZ = c.ForwardZ,
    };

    static string PinSig(SCP_SculptResolvedCamera c) => string.Join(",", new[] { c.TargetX, c.TargetY, c.TargetZ, c.Distance, c.Zoom }.Select(d => d.ToString("R", CultureInfo.InvariantCulture)));

    /// <summary>
    /// 把釘住的鏡頭套上去（純函數，淨室直接測）：中心 ＋ 透視距離／正交縮放。給了這兩格，渲染器就不再框。
    /// 不套的情況 ⇒ 回 false：手動給了鏡頭位置（eye 決定一切）。手動欄位給了 zoom／distance ⇒ 那一格以欄位為準（中心照釘）。
    /// </summary>
    public static bool ApplyPin(SCP_SculptRenderParams p, SCP_SculptResolvedCamera iPin, IReadOnlyDictionary<string, string> iCam)
    {
        if (p.EyeX.HasValue) return false;
        p.TargetX = iPin.TargetX; p.TargetY = iPin.TargetY; p.TargetZ = iPin.TargetZ;
        if (p.Projection == SCP_SculptProjection.Perspective) { if (!iCam.ContainsKey("distance")) p.Distance = iPin.Distance; }
        else if (!iCam.ContainsKey("zoom")) p.Zoom = iPin.Zoom;
        return true;
    }

    /// <summary>右鍵／中鍵拖曳 → yaw／pitch（純函數）：抓著物體轉 —— 往右拖物體往右轉（yaw 減）、往下拖看得更從上面（pitch 加）；pitch 夾在 ±89。</summary>
    public static (double Yaw, double Pitch) OrbitAngles(double iYaw, double iPitch, double iDragX, double iDragY)
    {
        double yaw = (iYaw - iDragX * 180) % 360; if (yaw < 0) yaw += 360;
        double pitch = Math.Max(-89, Math.Min(89, iPitch + iDragY * 180));
        return (yaw, pitch);
    }

    /// <summary>
    /// 左鍵拖曳 → 平移釘住的中心（純函數，就地改 <paramref name="ioPin"/>）：**只動 X／Y**（地面上），Z 一字不動（Tim「只影響水平位置」）。
    /// 抓著地面拖：往右拖 ⇒ 中心往畫面左邊走；往下拖 ⇒ 中心往畫面深處走。距離換算用這一幀注視點深度上「整張圖對應的世界長度」÷ 檢視倍率；
    /// 往深處那一軸再除以俯角的 sin（地面被斜看會縮短），太平的時候夾住，免得一拖飛出去。
    /// </summary>
    public static void PanPin(SCP_SculptResolvedCamera ioPin, SCP_SculptResolvedCamera iView, double iDragX, double iDragY, double iViewScale)
    {
        double vs = iViewScale > 0 ? iViewScale : 1;
        double w = iView.WorldPerImageWidth / vs, h = iView.WorldPerImageHeight / vs;
        // 畫面右方投到地面
        double rx = iView.RightX, ry = iView.RightY, rl = Math.Sqrt(rx * rx + ry * ry);
        if (rl > 1e-6) { ioPin.TargetX -= iDragX * w * rx / rl; ioPin.TargetY -= iDragX * w * ry / rl; }
        // 畫面深處投到地面：上方的水平分量（俯看）與視線的水平分量（平看）指同一個方向，取比較長的那個
        double ux = iView.UpX, uy = iView.UpY, ul = Math.Sqrt(ux * ux + uy * uy);
        double fx = iView.ForwardX, fy = iView.ForwardY, fl = Math.Sqrt(fx * fx + fy * fy);
        double dx, dy;
        if (ul >= fl && ul > 1e-6) { dx = ux / ul; dy = uy / ul; } else if (fl > 1e-6) { dx = fx / fl; dy = fy / fl; } else return;
        double sinPitch = Math.Max(0.25, ul);   // 上方的水平分量 ＝ sin(俯角)
        ioPin.TargetX += iDragY * h / sinPitch * dx; ioPin.TargetY += iDragY * h / sinPitch * dy;
    }

    /// <summary>plan 物件的身分（換 plan ⇒ 一定重放場景）。</summary>
    static string RuntimeHelpers_Id(object o) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o).ToString(CultureInfo.InvariantCulture);

    /// <summary>相機欄位（字串，同 CLI 的值）套到參數上；zoom／distance 給 `auto` ⇒ 回到自動框住。壞值 ⇒ 回原因、不套。</summary>
    static string? ApplyCamera(SCP_SculptRenderParams p, Dictionary<string, string> iCam, string iViewScale)
    {
        foreach (var kv in iCam)
        {
            string v = kv.Value.Trim();
            bool aAuto = v.Equals("auto", StringComparison.OrdinalIgnoreCase);
            if (!aAuto && !double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return kv.Key + " 不是數字：" + v;
            double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
            switch (kv.Key)
            {
                case "yaw": if (!aAuto) p.YawDeg = d; break;
                case "pitch": if (!aAuto) p.PitchDeg = d; break;
                case "roll": if (!aAuto) p.RollDeg = d; break;
                case "fov": if (!aAuto) p.FovDeg = d; break;
                case "zoom": p.Zoom = aAuto ? null : d; break;
                case "distance": p.Distance = aAuto ? null : d; break;
            }
        }
        if (!double.TryParse(iViewScale.Trim().Length == 0 ? "1" : iViewScale, NumberStyles.Float, CultureInfo.InvariantCulture, out double vs) || !(vs > 0))
            return "檢視倍率不是正數：" + iViewScale;
        p.ViewScale = vs;
        return null;
    }

    /// <summary>② 背景準備場景（同 CLI 的疊層）；voxel 跟上一份逐顆相同 ⇒ 換成上一份的清單物件（宿主網格快取命中）。</summary>
    void StartPlan(Dictionary<string, string> iReq, string iSceneSig, string iVoxelSig)
    {
        var aRaw = new Dictionary<string, string>(iReq, StringComparer.Ordinal) { ["data_root"] = m_DataRoot };
        if (m_LettersRoot.Length > 0) aRaw["letters_root"] = m_LettersRoot;
        PlanSnap? aPrev = m_Plan;
        m_PlanJobSig = iSceneSig;
        Func<(PlanSnap?, string?)> aJob = () =>
        {
            var sw = Stopwatch.StartNew();
            if (!Cmd_Sculpture.TryPrepareView(aRaw, out SCP_SculptViewPlan aPlan, out string aLayers, out string aErr)) return (null, aErr);
            IReadOnlyList<SCP_SculptVoxel> aVox = aPlan.Voxels;
            bool aReused = false;
            if (aPrev != null && aPrev.VoxelSig == iVoxelSig && aPrev.Voxels.Count == aVox.Count)
            {
                bool aSame = true;
                for (int i = 0; i < aVox.Count && aSame; i++)
                {
                    SCP_SculptVoxel a = aVox[i], b = aPrev.Voxels[i];
                    aSame = a.X == b.X && a.Y == b.Y && a.Z == b.Z && a.Color == b.Color;
                }
                if (aSame) { aVox = aPrev.Voxels; aReused = true; }
            }
            return (new PlanSnap(iSceneSig, iVoxelSig, aVox, aPlan.Params, aLayers, aPlan.TotalVoxels, sw.Elapsed.TotalMilliseconds, aReused), null);
        };
        if (SCP_GuiHost.RedrawsContinuously) { m_PlanJob = Task.Run(aJob); return; }
        var r = aJob();
        FinishPlan(r.Item1, r.Item2);
    }

    void PumpPlan()
    {
        if (m_PlanJob == null || !m_PlanJob.IsCompleted) return;
        var aJob = m_PlanJob;
        m_PlanJob = null;
        try { var r = aJob.Result; FinishPlan(r.Snap, r.Error); }
        catch (Exception e) { FinishPlan(null, (e.InnerException ?? e).Message); }
    }

    void FinishPlan(PlanSnap? iSnap, string? iError)
    {
        if (iSnap != null) { m_Plan = iSnap; m_PlanError = null; }
        else { m_PlanError = iError; m_PlanJobSig += "|failed"; }   // 同一組參數失敗過 ⇒ 不每幀重試（改了參數才再試）
    }

    void DrawResult(SCP_Ui g)
    {
        string aGpuPersona = g.FieldValue(PersonaSel + "/value", None);
        if (DrawGpuResult(g, aGpuPersona == None ? "" : aGpuPersona)) return;
        string aPath = g.FieldValue(SViewPath, "");
        using var aFold = g.Fold("渲染結果", FResultFold, iDefaultOpen: true);
        if (!aFold.Open) return;   // 收起來 ⇒ 自動渲染也不 spawn（AutoRender 先問 g.FoldOpen）
        if (aPath.Length == 0) { g.Note("（還沒渲染 —— 按「全景」「渲染展品」或「渲染」）"); return; }
        g.ImageFit(aPath, ViewSide(g), "雕刻渲染圖 " + Path.GetFileName(aPath));
        // 文字資訊在圖下方（Tim 2026-10-12）
        if (m_Job != null) g.Note("渲染中…（" + m_JobLabel + "）—— 上面是上一張" + (m_AutoWanted ? "；畫完會用最新參數再補一張" : ""));
        g.Label("目前對象：" + SubjectText(g.FieldValue(SSubject, SubjectFull)) + "（環繞／切投影／手動渲染都重渲這個對象）");
        g.Label("renderer：" + g.FieldValue(SRenderer, "?"));
        g.Label("layers：" + g.FieldValue(SLayers, "?"));
        g.Label(g.FieldValue(SViewInfo, ""));
        g.Note("檔案：" + aPath);
    }

    // ===========================================================
    // 區塊職責：渲染設定檔 —— 層（共用／個人）、清單（標作用中）、用／一次性渲染／複製／刪除／重設、存手動參數。
    // 物理意義：讀 in-process；**寫一律 spawn `op=render-profile`**（驗證與回報跟 CLI 同一份）。
    // ===========================================================
    void DrawProfiles(SCP_Ui g, string iPersona, string iScope)
    {
        string aHead = $"渲染設定檔（{(iScope == "shared" ? "共用" : "個人 " + (iPersona.Length > 0 ? iPersona : "—"))}層：{m_Profiles.Count} 組，作用中 {m_ProfActive ?? "（未設）"}）";
        using var aFold = g.Fold(aHead, P + "fold/profiles", iDefaultOpen: false);
        if (!aFold.Open) return;
        g.Dropdown("層", new List<SCP_GuiOption> { new("shared", "共用（Sculpture/）"), new("persona", "個人（letters/<P>/sculpture/）") }, "shared", ProfScope);

        g.Label("疊加結果（這一刻渲染會用的鏈；persona 取上面選的那位）：");
        if (m_ResolveError.Length > 0) g.Note("[疊不出來] " + m_ResolveError);
        else if (m_Resolved != null)
        {
            g.Label("  " + string.Join(" → ", m_ResolvedLayers));
            foreach (string s in Describe(m_Resolved)) g.Label("  " + s);
        }

        if (iScope == "persona" && iPersona.Length == 0) { g.Note("個人層要先在上面選 persona。"); return; }
        if (m_ScopeDir == null) { g.Note("這一層解不出資料夾。"); return; }
        g.Note("資料夾：" + m_ScopeDir);
        string aScopeArg = iScope;
        string aPend = g.FieldValue(Pending, "");
        if (m_Profiles.Count == 0) g.Note("（這一層還沒有設定檔）");
        foreach (string n in m_Profiles)
        {
            bool aActive = n == m_ProfActive;
            using (var aItem = g.Fold((aActive ? "[作用中] " : "") + n, P + "fold/prof/" + iScope + "/" + n, iDefaultOpen: false))
            {
                using (g.Row())
                {
                    if (!aActive && g.Button("使用", P + "btn/prof-use/" + n))
                        RunProfile(g, $"設定檔 use {n}", new() { ["sub"] = "use", ["scope"] = aScopeArg, ["name"] = n }, iPersona);
                    if (g.Button("用它渲染一次", P + "btn/prof-view/" + n))
                        RunView(g, $"用設定檔 {iScope}/{n} 渲染", new() { ["profile"] = n, ["profile_scope"] = aScopeArg }, iPersona);
                    if (g.Button("複製到 →", P + "btn/prof-copy/" + n))
                    {
                        string aToScope = g.FieldValue(ProfCopyScope + "/value", iScope);
                        string aTo = g.FieldValue(ProfCopyName, "").Trim();
                        if (!SCP_SculptRenderProfiles.IsValidName(aTo)) m_Message = "複製目標名要是小寫英數／_／-（1..40 字）⇒ 這次沒有動作";
                        else if (aToScope == "persona" && iPersona.Length == 0) m_Message = "複製到個人層要先選 persona ⇒ 這次沒有動作";
                        else RunProfile(g, $"設定檔 copy {iScope}/{n} → {aToScope}/{aTo}",
                            new() { ["sub"] = "copy", ["from_scope"] = aScopeArg, ["from"] = n, ["to_scope"] = aToScope, ["to"] = aTo }, iPersona);
                    }
                    Armed(g, aPend, "delete/" + iScope + "/" + n, aActive ? null : "刪除", () =>
                        RunProfile(g, $"設定檔 delete {n}", new() { ["sub"] = "delete", ["scope"] = aScopeArg, ["name"] = n }, iPersona));
                    Armed(g, aPend, "reset/" + iScope + "/" + n, "重設為空", () =>
                        RunProfile(g, $"設定檔 reset {n}", new() { ["sub"] = "reset", ["scope"] = aScopeArg, ["name"] = n }, iPersona));
                }
                if (aItem.Open) g.Paragraph(m_ProfJson.TryGetValue(n, out string? j) ? j : "");
            }
        }
        if (iScope == "persona" && m_ProfActive != null && g.Button("停用個人層（全跟共用走）", P + "btn/prof-use-none"))
            RunProfile(g, "個人層 use none", new() { ["sub"] = "use", ["scope"] = "persona", ["name"] = "none" }, iPersona);
        g.Separator();
        g.Dropdown("複製目標層", new List<SCP_GuiOption> { new("shared", "共用"), new("persona", "個人") }, iScope, ProfCopyScope);
        Field(g, "複製目標名（按某一組的「複製到 →」）", ProfCopyName);
        using (g.Row())
        {
            string aName = Field(g, "設定檔名（小寫英數／_／-）", ProfSaveName);
            if (g.Button("把目前手動參數存成設定檔", P + "btn/prof-save"))
            {
                if (!SCP_SculptRenderProfiles.IsValidName(aName)) m_Message = "設定檔名要是小寫英數／_／-（1..40 字）⇒ 這次沒有存";
                else
                {
                    Dictionary<string, string> aSet = ManualProfileArgs(g, out List<string> aSkipped);
                    if (aSet.Count == 0) m_Message = "手動欄位全是空白 ⇒ 沒有東西可存（要建空設定檔請用 reset）";
                    else
                    {
                        aSet["sub"] = "set"; aSet["scope"] = aScopeArg; aSet["name"] = aName;
                        RunProfile(g, $"設定檔 set {iScope}/{aName}（{aSet.Count - 3} 鍵）"
                                      + (aSkipped.Count > 0 ? "；不屬於設定檔、沒存：" + string.Join("、", aSkipped) : ""), aSet, iPersona);
                    }
                }
            }
        }
        g.Note("存的是手動區「有填」的欄位（空白＝不寫、沿用下層）；燈光清單有東西 ⇒ 整組換掉；只填 light_dir ⇒ 換成那一盞。region／exclude 不屬於設定檔。");
    }

    /// <summary>二段確認鈕（第一次上膛、第二次執行）。iLabel null ＝ 不畫（例：作用中的不給刪 —— CLI 也會擋，這裡先讓人看到）。</summary>
    void Armed(SCP_Ui g, string iPend, string iKey, string? iLabel, Action iDo)
    {
        if (iLabel == null) return;
        bool aArmed = iPend == iKey;
        if (!g.Button(aArmed ? $"確定{iLabel}？再按一次" : iLabel, P + "btn/" + iKey)) return;
        if (!aArmed) { g.SetField(Pending, iKey); return; }
        g.SetField(Pending, "");
        iDo();
    }

    /// <summary>手動欄位 → `render-profile set` 的鍵（只取設定檔認得的那些）。</summary>
    Dictionary<string, string> ManualProfileArgs(SCP_Ui g, out List<string> oSkipped)
    {
        Dictionary<string, string> aView = ManualViewArgs(g);
        oSkipped = new List<string>();
        var a = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in aView)
        {
            switch (kv.Key)
            {
                case "region": case "exclude_color": oSkipped.Add(kv.Key); break;
                case "light_dir": break;   // 下面跟燈光清單一起處理
                default: a[kv.Key] = kv.Value; break;
            }
        }
        string aLights = g.FieldValue(FLights, "").Trim();
        if (aLights.Length > 0) { a["light_clear"] = "1"; a["light_add"] = aLights; }
        else if (aView.TryGetValue("light_dir", out string? aDir))
        {
            a["light_clear"] = "1";
            a["light_add"] = aDir + ";#ffffff;1" + (aView.TryGetValue("shadow", out string? s) ? ";" + s : "");
        }
        return a;
    }

    void RunProfile(SCP_Ui g, string iLabel, Dictionary<string, string> iArgs, string iPersona)
    {
        var aArgs = new Dictionary<string, string>(iArgs, StringComparer.Ordinal) { ["op"] = "render-profile" };
        bool aNeedsPersona = aArgs.TryGetValue("scope", out string? s) && s == "persona"
                             || aArgs.TryGetValue("from_scope", out string? f) && f == "persona"
                             || aArgs.TryGetValue("to_scope", out string? t) && t == "persona";
        if (aNeedsPersona && iPersona.Length > 0) aArgs["persona"] = iPersona;
        Start(g, iLabel, () => new Outcome { Log = RunCli(aArgs).Log(iLabel), Reload = true });
    }

    // ===========================================================
    // 區塊職責：切片（3D→2D，voxel 色原樣當像素色；免費唯讀）。
    // ===========================================================
    void DrawSlice(SCP_Ui g, string iPersona)
    {
        using (g.Row())
        {
            if (g.Button("產生切片", P + "btn/slice"))
            {
                string aRegion = V(g, FSliceRegion);
                if (aRegion.Length == 0) m_Message = "切片需要 region（x1..x2,y1..y2,z1..z2）—— 可按「帶入手動區 region」⇒ 這次沒有動作";
                else
                {
                    string aOut = Path.Combine(PageTempDir, "slice.png");
                    var aArgs = new Dictionary<string, string> { ["op"] = "slice", ["region"] = aRegion, ["out"] = aOut };
                    AddWorkTarget(g, aArgs);
                    string aAxis = V(g, FSliceAxis);
                    if (aAxis.Length > 0) aArgs["axis"] = aAxis;
                    if (iPersona.Length > 0) aArgs["persona"] = iPersona;
                    Start(g, "切片 " + aRegion, () =>
                    {
                        Directory.CreateDirectory(PageTempDir);
                        CliRun r = RunCli(aArgs);
                        var o = new Outcome { Log = r.Log("切片") };
                        if (r.Exit != 0) return o;
                        string aPath = r.Value("path", aOut);
                        if (!File.Exists(aPath)) { o.Log += "\n[注意] exit 0 但圖不在：" + aPath; return o; }
                        o.Fields[SSlicePath] = aPath;
                        o.Fields[SSliceInfo] = $"{DateTime.Now:HH:mm:ss}　region {aRegion}　axis {(aAxis.Length > 0 ? aAxis : "z+")}"
                                               + $"　non_transparent_pixels {r.Value("non_transparent_pixels", "?")}";
                        return o;
                    });
                }
            }
            if (g.Button("帶入手動區 region", P + "btn/slice-from-manual")) g.SetField(FSliceRegion, g.FieldValue(FRegion, ""));
        }
        using var aFold = g.Fold("切片（3D→2D PNG）", P + "fold/slice", iDefaultOpen: false);
        if (!aFold.Open) return;
        g.Note("voxel 顏色原樣當像素色（不打光／不投影／不混色）；空處透明。厚度＝region 在法線軸上的跨度，>1 時前覆蓋後。");
        Field(g, "region（x1..x2,y1..y2,z1..z2）", FSliceRegion);
        Field(g, "axis（x+ x- y+ y- z+ z-）", FSliceAxis);
        string aPathNow = g.FieldValue(SSlicePath, "");
        if (aPathNow.Length == 0) return;
        g.Label(g.FieldValue(SSliceInfo, ""));
        g.Note("檔案：" + aPathNow);
        g.ImageFit(aPathNow, SmallSide, "切片 " + Path.GetFileName(aPathNow));
    }

    // ===========================================================
    // 區塊職責：2D→3D 貼圖預覽 —— in-process canvas op=view（純讀），出 path_t 與 non_transparent_pixels，組 stamp2d 指令。
    // 物理意義：本頁**只做預覽那一半**；真正落子走 `senate cmd sculpture --arg op=stamp2d`（收費）—— 頁面直接落子＝繞過收銀台。
    // ⚠ scale 顯式給 1：non_transparent_pixels 是放大後數的 ⇒ scale>1 會讓 expect_pixels 膨脹成 scale² 倍。
    // ===========================================================
    void DrawStamp(SCP_Ui g, string iPersona)
    {
        string aCmd = g.FieldValue(SStampCmd, "");
        using (g.Row())
        {
            if (g.Button("產生貼圖預覽", P + "btn/stamp")) RunStamp(g, iPersona);
            if (aCmd.Length > 0 && SCP_GuiHost.CopyToClipboard != null && g.Button("複製貼圖指令", P + "btn/stamp-copy"))
                m_Message = SCP_GuiHost.CopyToClipboard(aCmd);
        }
        using var aFold = g.Fold("2D→3D 貼圖預覽", P + "fold/stamp", iDefaultOpen: false);
        if (!aFold.Open) return;
        g.Note("唯讀免費（in-process canvas op=view，圖寫進 persona 自己的 letters/<P>/cmd/）；落子仍走 `senate cmd sculpture --arg op=stamp2d`。");
        Field(g, "來源區域 x,y,w,h（2D 畫布座標）", FStampRegion);
        Field(g, "at（圖左上角貼在 3D 的 x,y,z）", FStampAt);
        Field(g, "facing（貼片法線 x+ x- y+ y- z+ z-）", FStampFacing);
        Field(g, "thickness（沿法線擠出層數）", FStampThick);
        if (aCmd.Length > 0) g.Paragraph(aCmd);
        string aPath = g.FieldValue(SStampPath, "");
        if (aPath.Length == 0) return;
        g.Label(g.FieldValue(SStampInfo, ""));
        g.Note("檔案：" + aPath + "（透明＝未繪製，不會變 voxel）");
        g.ImageFit(aPath, SmallSide, "貼圖預覽 " + Path.GetFileName(aPath));
    }

    void RunStamp(SCP_Ui g, string iPersona)
    {
        string aRegion = V(g, FStampRegion);
        if (!TryXywh(aRegion, out int x, out int y, out int w, out int h))
        { m_Message = $"來源區域要是 x,y,w,h 四個整數（w,h > 0）—— 收到「{aRegion}」⇒ 這次沒有動作"; return; }
        if (iPersona.Length == 0) { m_Message = "貼圖預覽要先選 persona（canvas view 的圖寫進那位的 letters/<P>/cmd/；stamp2d 也要記帳人）⇒ 這次沒有動作"; return; }
        string aAt = V(g, FStampAt), aFacing = V(g, FStampFacing), aThick = V(g, FStampThick);
        string aWorkArg = PersonalSpace(g) ? "--arg work=" + SelectedWork(g) + " " : "";
        string aData = m_DataRoot, aLetters = m_LettersRoot;
        Start(g, "貼圖預覽 " + aRegion, () =>
        {
            var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["op"] = "view", ["region"] = aRegion, ["scale"] = "1", ["persona"] = iPersona, ["data_root"] = aData,
            };
            if (aLetters.Length > 0) aArgs["letters_root"] = aLetters;
            SCP_CmdResult r = SCP_CmdRegistry.Dispatch("canvas", aArgs);
            var o = new Outcome { Log = $"[{DateTime.Now:HH:mm:ss} 貼圖預覽 · in-process canvas] exit {r.ExitCode}\n" + string.Join("\n", r.Lines).Trim() };
            o.Fields[SStampCmd] = "";
            if (!r.Ok) return o;
            string aPathT = "", aOpaque = "";
            foreach (var kv in r.Values)
            {
                if (kv.Key == "path_t") aPathT = kv.Value;
                if (kv.Key == "non_transparent_pixels") aOpaque = kv.Value;
            }
            // 拿不到讀數 ⇒ 不組指令（沒有閘門的指令比不給更糟）
            if (aPathT.Length == 0 || !int.TryParse(aOpaque, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aN))
            { o.Log += "\n[注意] 讀不到 path_t／non_transparent_pixels ⇒ 不組指令"; return o; }
            o.Fields[SStampPath] = aPathT;
            o.Fields[SStampInfo] = $"{DateTime.Now:HH:mm:ss}　{w}×{h}　non_transparent_pixels {aN}";
            o.Fields[SStampCmd] = $"senate cmd sculpture --arg op=stamp2d --arg persona={iPersona} "
                                  + aWorkArg
                                  + $"--arg src_x1={x} --arg src_y1={y} --arg src_x2={x + w - 1} --arg src_y2={y + h - 1} "
                                  + $"--arg at={aAt} --arg facing={aFacing} --arg thickness={aThick} --arg expect_pixels={aN}";
            return o;
        });
    }

    // ===========================================================
    // 區塊職責：匯出（範圍＝手動區 region／exclude；檔名由引擎產生，⛔ 本頁不組檔名）。
    // 物理意義：工具列只有一顆「匯出」（TASK-0504）；格式、合併同色面、資料夾都在匯出設定（SculptExportSettings，Inspector 自動畫）。
    //   「開啟匯出資料夾」本來就在工具列，而它在沒設資料夾時就開上一次匯出檔那一層 ⇒ 不另放第二顆。
    // ===========================================================
    void ExportButton(SCP_Ui g)
    {
        // 個人區沒選到作品時內容區整段不畫 ⇒ 工具列也不給按（不准變成對展區匯出）
        if (PersonalSpace(g) && SelectedWork(g).Length == 0) return;
        SculptExportSettings aCfg = ExportCfg(g);
        string aFmt = aCfg.Format.ToString();
        bool aObj = aCfg.Format == SculptExportFormat.obj;
        string aKind = "." + aFmt + (aObj ? (aCfg.MergeFaces ? "（合併）" : "（不合併）") : "");
        if (!g.Button("匯出 " + aKind, P + "btn/export")) return;
        string aPersona = g.FieldValue(PersonaSel + "/value", None);
        if (aPersona == None) aPersona = "";
        var aArgs = new Dictionary<string, string> { ["op"] = "export", ["format"] = aFmt };
        AddWorkTarget(g, aArgs);
        string aRegion = g.FieldValue(FRegion, "").Trim(), aEx = g.FieldValue(FExclude, "").Trim(), aDir = aCfg.OutDir.Trim();
        if (aRegion.Length > 0) aArgs["region"] = aRegion;
        if (aEx.Length > 0) aArgs["exclude_color"] = aEx;
        if (aDir.Length > 0) aArgs["out_dir"] = aDir;
        if (aPersona.Length > 0) aArgs["persona"] = aPersona;
        // 合併＝merge=greedy（同色共面合併）；不合併＝merge=none（逐 voxel 面，要 watertight 時用）。vox 不吃。
        if (aObj) aArgs["merge"] = aCfg.MergeFaces ? "greedy" : "none";
        string aLabel = "匯出 " + aKind + (aRegion.Length > 0 ? " region " + aRegion : "（全空間）");
        Start(g, aLabel, () =>
        {
            CliRun r = RunCli(aArgs);
            var o = new Outcome { Log = r.Log(aLabel) };
            if (r.Exit == 0 && r.Value("path", "").Length > 0) o.Fields[SExportPath] = r.Value("path", "");
            return o;
        });
    }

    void DrawExport(SCP_Ui g)
    {
        using var aFold = g.Fold("匯出設定（匯出鈕在上方工具列）", P + "fold/export", iDefaultOpen: false);
        if (!aFold.Open) return;
        g.Note("範圍＝手動區的 region／exclude_color（空 region＝全空間）。");
        g.Note("Format：obj／vox｜MergeFaces（只有 obj）：勾著＝同色共面合成矩形、頂點共用（面數大減），不勾＝逐 voxel 面（要 watertight／3D 列印時用）｜OutDir：空＝預設 " + (WorkExportDir(g, m_DataRoot) ?? DefaultExportDir) + "（個人作品 ⇒ 那件作品的 exports；共用展區 ⇒ Sculpture/exports）");
        foreach (string n in DrawCfg(g, ExportCfg(g), ExportCfgKey, SExportCfg, ref m_ExportCfgJson)) g.Note("（自動繪製）" + n);
        string aPath = g.FieldValue(SExportPath, "");
        if (aPath.Length > 0) g.Label("上一次匯出：" + aPath);
    }

    // ===========================================================
    // 區塊職責：紀錄 —— 上一個動作的完整輸出（含指令列、exit、stderr 尾段），可一鍵複製貼給 agent 排錯。
    // ===========================================================
    void DrawLog(SCP_Ui g)
    {
        string aLog = g.FieldValue(SLog, "");
        if (aLog.Length == 0) return;
        using var aFold = g.Fold("紀錄（上一個動作）", P + "fold/log", iDefaultOpen: true);
        if (!aFold.Open) return;
        if (SCP_GuiHost.CopyToClipboard != null && g.Button("複製紀錄", P + "btn/log-copy")) m_Message = SCP_GuiHost.CopyToClipboard(aLog);
        g.Paragraph(aLog);
    }

    // ── 背景工作（同 TaskManagerPage 的形狀）─────────────────────

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
        // 不會重畫的宿主（CLI 單次 render）：背景跑等於把答案丟掉 —— 這裡同步。
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
        if (o.Fields.TryGetValue(SSubject, out string? aSubj)) m_SubjectFromJob = aSubj;   // 工作寫回的對象 ⛔ 不觸發自動渲染
        string aLog = o.Log.Length > 12000 ? o.Log.Substring(0, 12000) + "\n…（截斷）" : o.Log;
        g.SetField(SLog, aLog);
        int aNl = aLog.IndexOf('\n');
        m_Message = aNl > 0 ? aLog.Substring(0, aNl) : aLog;
        if (o.Reload) m_Dirty = true;
    }

    // ── spawn 自己這顆 exe 跑 `cmd sculpture` ─────────────────────

    sealed class CliRun
    {
        public int Exit;
        public string CommandLine = "", Stdout = "", Stderr = "";
        public bool TimedOut;
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public string Value(string iKey, string iFallback) => Values.TryGetValue(iKey, out string? v) && v.Length > 0 ? v : iFallback;

        public string Log(string iLabel)
        {
            var sb = new StringBuilder();
            sb.Append($"[{DateTime.Now:HH:mm:ss} {iLabel}] exit {Exit}" + (TimedOut ? "（逾時，已砍掉子行程）" : "") + "\n");
            sb.Append("$ " + CommandLine + "\n");
            sb.Append(Stdout.TrimEnd());
            // Cmd 說「給了而從來沒被讀」⇒ 這一格本頁以為會生效、實際沒有 —— 放大講（常見原因：Cmd 端還不認得這個參數）
            if (Values.TryGetValue("unread_args", out string? aUnread) && aUnread.Length > 0)
                sb.Append("\n[注意] Cmd 沒有讀這些參數（沒生效）：" + aUnread);
            if (Exit != 0 && Stderr.Trim().Length > 0)
            {
                string[] aLines = Stderr.TrimEnd().Split('\n');
                sb.Append("\n--- stderr（尾段 " + Math.Min(40, aLines.Length) + " 行）---\n");
                sb.Append(string.Join("\n", aLines.Skip(Math.Max(0, aLines.Length - 40))).TrimEnd());
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 跑 `&lt;自己&gt; cmd sculpture --arg k=v …`，讀回 stdout 的 `🔢 key = value` 行當機讀值。
    /// <para>⚠ 用 `Environment.ProcessPath`（版本天生一致）；`dotnet senate.dll` 啟動時 ProcessPath 是 dotnet ⇒ 補上 dll 當第一個參數。</para>
    /// <para>⚠ data_root／letters_root 不帶 —— CLI 會從設定補（與 agent 打的同一條路）。</para>
    /// </summary>
    CliRun RunCli(Dictionary<string, string> iArgs)
    {
        var r = new CliRun();
        string? aExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(aExe)) { r.Exit = -1; r.Stdout = "拿不到自己的執行檔路徑（Environment.ProcessPath 是空的）"; return r; }
        var aPsi = new ProcessStartInfo(aExe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = m_Model.RepoRoot,
        };
        var aShown = new List<string>();
        if (Path.GetFileNameWithoutExtension(aExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string aDll = typeof(SculptureViewerPage).Assembly.Location;
            aPsi.ArgumentList.Add(aDll);
        }
        aPsi.ArgumentList.Add("cmd"); aPsi.ArgumentList.Add("sculpture");
        aShown.Add("senate cmd sculpture");
        using var aFiles = new WorkArgumentFiles();
        foreach (var kv in iArgs)
        {
            if (kv.Key == "notes" || kv.Key == "todo")
            {
                string path = aFiles.Add(kv.Value);
                aPsi.ArgumentList.Add("--arg-file");
                aPsi.ArgumentList.Add(kv.Key + "=" + path);
                aShown.Add("--arg-file " + kv.Key + "=<筆記檔>");
                continue;
            }
            aPsi.ArgumentList.Add("--arg");
            aPsi.ArgumentList.Add(kv.Key + "=" + kv.Value);
            aShown.Add("--arg " + (kv.Value.IndexOfAny(new[] { ' ', ';', '|', '"' }) >= 0 ? $"\"{kv.Key}={kv.Value}\"" : kv.Key + "=" + kv.Value));
        }
        r.CommandLine = string.Join(" ", aShown);
        var aOut = new StringBuilder();
        var aErr = new StringBuilder();
        using var aProc = new Process { StartInfo = aPsi };
        aProc.OutputDataReceived += (_, e) => { if (e.Data != null) lock (aOut) aOut.AppendLine(e.Data); };
        aProc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (aErr) aErr.AppendLine(e.Data); };
        try { aProc.Start(); }
        catch (Exception e) { r.Exit = -1; r.Stdout = "子行程起不來：" + e.GetType().Name + ": " + e.Message; return r; }
        using (SCP_ProcessRegistry.RegisterScope(aProc, ProcTag, "雕刻觀測頁：" + r.CommandLine, nameof(SculptureViewerPage)))
        {
            aProc.BeginOutputReadLine();
            aProc.BeginErrorReadLine();
            if (!aProc.WaitForExit(CliTimeoutMs))
            {
                r.TimedOut = true;
                try { aProc.Kill(entireProcessTree: true); } catch { /* 已經結束 */ }
                aProc.WaitForExit(5000);
            }
            else aProc.WaitForExit();   // 等非同步讀完最後幾行
        }
        r.Exit = r.TimedOut ? -2 : aProc.ExitCode;
        r.Stdout = aOut.ToString();
        r.Stderr = aErr.ToString();
        foreach (string aLine in r.Stdout.Split('\n'))
        {
            string l = aLine.TrimEnd('\r');
            const string Mark = "🔢 ";
            if (!l.StartsWith(Mark, StringComparison.Ordinal)) continue;
            int eq = l.IndexOf(" = ", StringComparison.Ordinal);
            if (eq < 0) continue;
            r.Values[l.Substring(Mark.Length, eq - Mark.Length).Trim()] = l.Substring(eq + 3);
        }
        return r;
    }

    // ── 小工具 ─────────────────────────────────────────────────

    static string Def(string iId) => s_Defaults.TryGetValue(iId, out string? d) ? d : "";
    static string V(SCP_Ui g, string iId) => g.FieldValue(iId, Def(iId)).Trim();
    static string Field(SCP_Ui g, string iLabel, string iId) => g.TextField(iLabel, Def(iId), iId).Trim();

    /// <summary>三態下拉（沿用／A／B）—— value 「-」＝ 不送。</summary>
    static void Tri(SCP_Ui g, string iLabel, string iId, string iA, string iALabel, string iB, string iBLabel)
        => g.Dropdown(iLabel, new List<SCP_GuiOption> { new(None, "（沿用）"), new(iA, iALabel), new(iB, iBLabel) }, None, iId);

    static string S(SCP_JsonData d, string iKey)
    {
        SCP_JsonData v = d[iKey];
        if (!v.Exists || v.IsNull) return "";
        return v.IsString ? v.AsString() : v.ToJson(false);
    }

    static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    static string LightSpec(SCP_SculptLight l)
        => $"{F(l.DirX)},{F(l.DirY)},{F(l.DirZ)};#{l.R:x2}{l.G:x2}{l.B:x2};{F(l.Intensity)};{(l.CastShadow ? 1 : 0)}";

    static IEnumerable<string> Describe(SCP_SculptRenderParams p)
    {
        string Opt(double? v) => v.HasValue ? F(v.Value) : "auto";
        string Vec(double? x, double? y, double? z) => x.HasValue && y.HasValue && z.HasValue ? $"{F(x.Value)},{F(y.Value)},{F(z.Value)}" : "auto";
        yield return $"camera：{p.Projection}　yaw {F(p.YawDeg)}　pitch {F(p.PitchDeg)}　roll {F(p.RollDeg)}　target {Vec(p.TargetX, p.TargetY, p.TargetZ)}"
                     + $"　eye {Vec(p.EyeX, p.EyeY, p.EyeZ)}　distance {Opt(p.Distance)}　fov {F(p.FovDeg)}　zoom {Opt(p.Zoom)}";
        yield return $"光影：ambient {F(p.Ambient)}　AO {(p.AmbientOcclusion ? "開" : "關")}　陰影 {(p.Shadow ? "開" : "關")}　燈 {p.Lights.Count} 盞"
                     + (p.Lights.Count > 0 ? "（" + string.Join("｜", p.Lights.Select(LightSpec)) + "）" : "");
        yield return $"skybox：{p.Skybox ?? "builtin"}　skybox_yaw {F(p.SkyboxYawDeg)}　skybox_tilt {F(p.SkyboxTiltDeg)}　背景 #{p.BgR:x2}{p.BgG:x2}{p.BgB:x2}　尺寸 {p.Width}×{p.Height}"
                     + (p.FitUpscale ? "　fit_upscale 開" : "");
        yield return "地板：" + FloorSpec(p);
    }

    static string FloorSpec(SCP_SculptRenderParams p)
    {
        SCP_JsonData f = SCP_SculptRenderProfiles.DescribeFloor(p);
        string aTex = f["texture"].AsString();
        return (f["enabled"].AsBool() ? "開" : "關（以下是記住的欄位）")
               + $"　貼圖 {(aTex == SCP_SculptRenderProfiles.FloorBuiltin ? aTex : Path.GetFileName(aTex))}　tile {F(f["tile_size"].AsDouble())}"
               + $"　z {F(f["z"].AsDouble())}　{(f["full_grid"].AsBool() ? "整個空間" : "外框＋" + F(f["margin"].AsDouble()))}"
               + $"　色調 {f["color"].AsString()}　淡出 {F(f["fade"].AsDouble())}";
    }

    static bool TryXywh(string iText, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        string[] a = iText.Split(',');
        if (a.Length != 4) return false;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(a[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
        x = n[0]; y = n[1]; w = n[2]; h = n[3];
        return w > 0 && h > 0;
    }
}
