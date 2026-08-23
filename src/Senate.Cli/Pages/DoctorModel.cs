// 區塊職責：Doctor 頁的資料 —— 讀數 ＋ 一顆「重新取讀數」的動作。
// 物理意義：頁面每幀都會被重畫，但**探測不可以每幀都跑**（那會每秒對每個專案跑好幾次 git）。
//           ⇒ 讀數住在 model 裡，只有在被要求時才刷新。這也讓「按下重新取讀數」有一個真的效果，
//           而不是一顆按了沒事發生的裝飾。
// 數值影響：Refresh() 會跑 git status／stat 心跳檔（唯讀）。RefreshCount 是讀數 ——
//           agent 或人按了之後可以確認「真的重跑了」，而不是靠畫面看起來一樣就以為沒動。
using Senate.Core;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class DoctorModel
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

    public DoctorModel(string iRepoRoot)
    {
        m_RepoRoot = iRepoRoot;
        Env = null!;
        Projects = new List<ProjectReading>();
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
            GitCli.Version(), aCfgPath, File.Exists(aCfgPath));

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
