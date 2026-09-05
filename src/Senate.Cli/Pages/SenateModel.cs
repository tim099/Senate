// 區塊職責：**整個後台共用的 model** —— 環境／專案讀數、顯示參數、repo 根，以及改尺寸這個動作。
// 物理意義：頁面每幀都會被重畫，但**探測不可以每幀都跑**（那會每秒對每個專案跑好幾次 git）。
//           ⚠ 本類別原名 `DoctorModel` —— 那個名字在只有 Doctor 一頁的時候是對的，
//           但入口頁／尺寸頁／設定頁都吃它之後就變成一個比事實小的名字（"這是誰的 model？"）。
//           改名不是美觀問題：名字比事實小會讓下一個人在它旁邊再開第二份 app 級狀態。
//           ⇒ 讀數住在 model 裡，只有在被要求時才刷新。這也讓「按下重新取讀數」有一個真的效果，
//           而不是一顆按了沒事發生的裝飾。
// 數值影響：Refresh() 會跑 git status／stat 心跳檔（唯讀）。RefreshCount 是讀數 ——
//           agent 或人按了之後可以確認「真的重跑了」，而不是靠畫面看起來一樣就以為沒動。
using System.Reflection;
using Senate.Core;
using SCP.Core.Git;
using SCP.Core.Gui;
using SCP.Core.Paths;
using SCP.Core.Prefs;

namespace Senate.Cli.Pages;

public sealed class SenateModel : ISCP_GuiAppContext
{
    readonly string m_RepoRoot;

    public EnvReading Env { get; private set; }
    public List<ProjectReading> Projects { get; private set; }
    public bool ConfigBroken { get; private set; }

    /// <summary>刷新過幾次（含建構那次）。給「按了到底有沒有生效」一個可讀的證據。</summary>
    public int RefreshCount { get; private set; }

    /// <summary>
    /// 顯示參數（尺寸／間距／字級）。**在建構時讀一次**，不跟著 Refresh 重載 ——
    /// 重載會把使用者這一輪剛換、還沒存成功的尺寸悄悄吃掉。
    /// </summary>
    public SCP_GuiStyle Style { get; }

    /// <summary>repo 根（要寫回設定檔的頁面需要它 —— 別再從別的地方推導一次）。</summary>
    public string RepoRoot => m_RepoRoot;

    /// <summary>上一次改尺寸的結果（成功或失敗都要有話說；null ＝ 這次還沒人改過）。</summary>
    public string? StyleMessage { get; private set; }

    /// <summary>
    /// 專案層設定（<c>ISCP_GuiAppContext</c> 的一格）。
    /// <para>⚠ 這裡是**唯一**決定「prefs 落在哪個檔」的地方 —— 頁面拿到的是介面，
    /// 它不知道也不該知道檔名。搬進 SCP_Core 的頁面因此不會綁死在 Senate 的檔案佈局上。</para>
    /// </summary>
    public ISCP_Prefs Prefs { get; }

    /// <summary>
    /// 尺寸頁底下那幾句**只在 Senate 為真**的註腳。
    /// <para>⚠ 它們刻意不寫在 SCP_GuiStylePage 裡：`--scale` 是 CLI 的旗標、
    /// `senate.local.json` 是這個宿主的檔名 —— Unity 那側讀到會去找一個不存在的東西，
    /// 而那種假話不報錯。</para>
    /// </summary>
    public IReadOnlyList<string> StyleNotes { get; } = new[]
    {
        "純文字輸出的寬度不吃這個 scale —— 終端機的一格是字元不是像素（要調用 --width）。",
        "常設值存在 senate.local.json 的 ui 區塊；--scale / --size 是一次性覆寫，不寫回檔案。",
    };

    /// <summary>
    /// 頁面發現要掃的 assembly —— **顯式列出**，不掃「現在載了哪些」。
    /// <para>SCP_Core（框架頁）＋ Senate.Cli（宿主自己的頁）兩顆。加了新的頁面 assembly 要補在這裡；
    /// 補漏的代價只是那顆不被檢查，不會壞掉。</para>
    /// </summary>
    public IReadOnlyList<Assembly> PageAssemblies { get; } = new[]
    {
        typeof(SCP_GuiToolPage).Assembly,   // SCP_Core
        typeof(SenateModel).Assembly,       // Senate.Cli
    };

    /// <summary>
    /// SCP_Core 在這個宿主的位置。Senate 是 `&lt;repo&gt;/SCP_Core`（submodule 掛載點）。
    /// <para>⚠ 這是**這個宿主**的事實，不是通則 —— Unity 那側會是別的路徑。</para>
    /// </summary>
    public string CoreRoot => Path.Combine(m_RepoRoot, "SCP_Core").Replace('\\', '/');

    /// <summary>
    /// Senate 自己 —— skill 的預設安裝對象（早安流程之後要在**這裡**跑）。
    /// <para>⚠ EditorRunning 給 null：這不是 Unity 專案，「量不到」比「沒在跑」誠實。</para>
    /// </summary>
    public SCP_GuiProjectRef HostProject => new SCP_GuiProjectRef("Senate（本專案）", m_RepoRoot);

    /// <summary>
    /// 可以被安裝的專案 —— 就是設定檔裡管的那批（走 Probe 的結果，順便帶 Editor 心跳）。
    /// <para>⚠ 只列狀態 Ok 的：指向不存在磁碟的專案不該出現在「要裝進誰」的下拉裡。</para>
    /// </summary>
    public IReadOnlyList<SCP_GuiProjectRef> ManagedProjects
    {
        get
        {
            var aList = new List<SCP_GuiProjectRef>();
            foreach (ProjectReading p in Projects)
            {
                if (p.State != ProbeState.Ok) continue;
                aList.Add(new SCP_GuiProjectRef(p.Name, p.Root, p.EditorLikelyRunning));
            }
            return aList;
        }
    }

    public SenateModel(string iRepoRoot)
    {
        m_RepoRoot = iRepoRoot;
        Env = null!;
        Projects = new List<ProjectReading>();
        // 「哪個 section 住哪個檔」是**宿主的決定** —— 頁面只看得到 ISCP_Prefs。
        // awakening 走轉接頭而不是直接寫 senate.local.json：那個檔的寫入端只能有一個
        //（SenateConfig.Save 有它的 Extra 欄位保留，兩個寫入端會互相吃掉對方的東西）。
        Prefs = new SCP_RoutedPrefs(new SCP_JsonPrefs(SenatePageStore.DefaultPath(iRepoRoot)))
            .Route(SenateAwakeningPrefs.SectionName, new SenateAwakeningPrefs(iRepoRoot));
        Style = SenateUiStore.Load(iRepoRoot, w => Console.Error.WriteLine($"⚠ {w}"));
        Refresh();
    }

    /// <summary>
    /// 套用使用者選的尺寸並**寫回設定檔**。
    /// <para>兩件事顯式分開做：先改記憶中的 style（這一輪就生效），再存檔（下次也生效）——
    /// 存檔失敗時不回滾，但把失敗說出來：「這次有效、下次沒有」比「安靜地都沒有」好查。</para>
    /// </summary>
    public void ApplySize(SCP_GuiSize iSize)
    {
        Style.SetPreset(iSize);
        var (aOk, aMsg) = SenateUiStore.Save(m_RepoRoot, Style);
        StyleMessage = (aOk ? "✓ " : "⚠ ") + aMsg;
    }

    /// <summary>
    /// <see cref="ISCP_GuiAppContext.ApplyStyle"/> 的實作 —— 就是 <see cref="ApplySize"/>。
    /// <para>⚠ 保留兩個名字是**過渡**：介面用 <c>ApplyStyle</c>（框架語彙），
    /// 既有呼叫端還在用 <c>ApplySize</c>。頁面全部改吃介面之後刪掉 <c>ApplySize</c>，
    /// 不要讓兩個名字長期並存 —— 兩個入口遲早會有一個漏掉新加的動作。</para>
    /// </summary>
    public void ApplyStyle(SCP_GuiSize iSize) { ApplySize(iSize); }

    // 區塊職責：資料根 —— **與「路徑管理」頁、`senate cmd paths` 同一個來源**。
    // 物理意義：走 `SCP_PathRegistry.Resolve` ＋ `SenatePathBinding.StoredOf`，
    //           而那兩支正是那一頁與那支 Cmd 用的 ⇒ 三個地方**不可能對同一格給出不同的值**。
    // 數值影響：每次讀都重載設定檔（一個小 json）—— 刻意不快取：
    //           使用者在「路徑管理」頁改完值切回來，快取會讓他看到舊的而以為沒存進去。
    // ⚠ 不吞 `Error`：兩個啟用專案 ⇒ 資料根不唯一，那是**狀態壞了**，不是「沒設定」。
    //   靜默挑一個的症狀是「路徑全對，只是屬於別的專案」。
    public SCP_PathResolution AgentCommandsRoot => ResolvePath(SCP_PathId.AgentCommandsRoot);

    // ⚠ 信件庫根**支援 `auto`** ⇒ 讀取端不准讀原始值。
    //   🩸 2026-09-05：「登入狀態」頁原本自己 `Prefs.Read(awakening.lettersRoot)`，
    //   填 `auto` 時它會拿字面 `"auto"` 去掃目錄 ⇒ 畫面說「這裡真的還沒有人」，
    //   而同一台的 CLI 解得出真正的路徑（兩邊都不報錯）。
    public SCP_PathResolution LettersRoot => ResolvePath(SCP_PathId.LettersRoot);

    /// <summary>解一格路徑 —— 上面那幾格共用的實作（多一格路徑不必再抄一次設定檔三態）。</summary>
    SCP_PathResolution ResolvePath(SCP_PathId iId)
    {
        SenateConfig? aCfg;
        try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(m_RepoRoot)); }
        catch (InvalidDataException e)
        {
            return new SCP_PathResolution("", "設定檔讀不了", e.Message);
        }
        if (aCfg == null)
            return new SCP_PathResolution("", "沒有設定檔",
                $"還沒有 {Path.GetFileName(SenateConfig.DefaultPath(m_RepoRoot))} —— 先跑 `senate init`");
        return SCP_PathRegistry.Resolve(iId, iInner => SenatePathBinding.StoredOf(aCfg, iInner));
    }

    public void Refresh()
    {
        string aCfgPath = SenateConfig.DefaultPath(m_RepoRoot);
        SenateConfig? aCfg = null;
        ConfigBroken = false;
        try { aCfg = SenateConfig.Load(aCfgPath); }
        catch (InvalidDataException e)
        {
            ConfigBroken = true;
            Console.Error.WriteLine($"✗ {e.Message}");
        }

        Env = new EnvReading(DotnetCli.SdkVersion(), DotnetCli.RuntimeVersion,
            SCP_Git.Version(), aCfgPath, File.Exists(aCfgPath));

        var aList = new List<ProjectReading>();
        if (aCfg != null)
        {
            foreach (string err in aCfg.Validate()) Console.Error.WriteLine($"⚠ 設定：{err}");
            foreach (var p in aCfg.Projects) aList.Add(ProjectProbe.Probe(p));
        }
        Projects = aList;
        RefreshCount++;
    }
}

/// <summary>
/// 環境讀數（跟專案無關的那半）。住在 model 這一層而不是頁面那一層 ——
/// 它是資料，不是某一頁的私有東西。
/// </summary>
public sealed record EnvReading(
    string? DotnetSdkVersion,
    string RuntimeVersion,
    Version? GitVersion,
    string ConfigPath,
    bool ConfigExists)
{
    public bool GitOkForPathspec => GitVersion != null && GitVersion >= SCP_Git.MinVersionForPathspecFromFile;
}
