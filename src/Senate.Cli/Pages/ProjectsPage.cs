// 區塊職責：**專案關聯頁** —— 增刪改 senate.local.json 的 projects[]，逐列附探測讀數。
// 物理意義：「Senate 管哪些專案」是這套後台一切功能的對象宣告（cmd 派遣／submodule／doctor
//           全吃它）。設定頁（自動繪製）改得動它，但那是「整份設定的泛用編輯器」——
//           新增一個專案要自己展 fold、按 ＋、逐欄填；而這件事常做且做錯的代價高
//           （root 打錯 ⇒ cmd 派到不存在的資料樹，症狀是永遠 pending）。
//           ⇒ 本頁做窄而順的那條路：貼路徑 → 探測 → 新增 → 儲存，每列當場亮讀數。
// 數值影響：編輯的是自己讀進來的 draft，按「儲存」才寫回（走 SenateConfig.Save ——
//           與設定頁同一支，未知欄位與 "//" 註解照樣保留）。真相源仍是 senate.local.json，
//           **本頁不另立檔案**：同一份資料兩個檔就是漂移的起點。
// ⚠ 與 SettingsPage 的分工：那頁是「所有設定的最後手段」，本頁只管 projects[] ——
//   兩頁寫的是同一份檔、同一支 Save，改完互相看得到（都在 OnPush 重讀）。
using SCP.Core.Gui;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class ProjectsPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;
    readonly string m_ConfigPath;

    /// <summary>編輯中的那一份（按儲存才寫回）。null ＝ 讀不到／壞掉，畫面要說出來。</summary>
    SenateConfig? m_Draft;
    string? m_LoadError;
    bool m_Dirty;
    string? m_Message;

    public ProjectsPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
        m_ConfigPath = SenateConfig.DefaultPath(iModel.RepoRoot);
    }

    public override string Key => PageKey;
    public const string PageKey = "projects";
    public override string Title => "專案關聯";
    public override string? MenuGroup => "設定";

    /// <summary>讀檔在 OnPush 不在建構子 —— 頁面目錄會建一次實例只為了讀標題（同 SettingsPage）。</summary>
    public override void OnPush() { base.OnPush(); Load(); }

    void Load()
    {
        m_LoadError = null;
        m_Dirty = false;
        try
        {
            m_Draft = SenateConfig.Load(m_ConfigPath);
            if (m_Draft == null) m_LoadError = $"還沒有 {Path.GetFileName(m_ConfigPath)} —— 先跑 `senate init`";
        }
        catch (InvalidDataException e)
        {
            // 壞檔不拿空白頂上 —— 「檔壞了」長得像「還沒設定」時，儲存就是不可逆的覆寫。
            m_Draft = null;
            m_LoadError = $"設定檔讀不了，本頁不提供編輯（檔案沒有被動過）：{e.Message}";
        }
    }

    protected override void DrawContent(SCP_Ui g)
    {
        if (m_Draft == null)
        {
            g.Note($"⚠ {m_LoadError}");
            if (g.Button("重新讀取", "projects/reload")) Load();
            return;
        }

        g.Note($"這裡改的是 `{Path.GetFileName(m_ConfigPath)}` 的 projects[]（與「設定」頁同一份檔）——"
               + "按「儲存」才寫回，探測讀數是現場的不是存檔的。");

        // ── 既有專案：逐列欄位 ＋ 現場探測 ─────────────────────────────
        for (int i = 0; i < m_Draft.Projects.Count; i++)
        {
            SenateProject aProj = m_Draft.Projects[i];
            string aId = $"projects/{i}";
            // ⚠ 索引式 id：移除之後後面每列的 id 位移（欄位值可能跟到隔壁）——
            //   跟 inspector 清單同一個代價，移除後這一輪立刻 return 重畫。
            using (g.Box($"[{i}] {(aProj.Name.Length > 0 ? aProj.Name : "（未命名）")}{(aProj.Enabled ? "" : "　（停用）")}"))
            {
                string aName = g.TextField("名稱", aProj.Name, aId + "/name");
                if (aName != aProj.Name) { aProj.Name = aName; m_Dirty = true; }

                string aRoot = g.TextField("root（專案 git repo 根）", aProj.Root, aId + "/root");
                if (aRoot != aProj.Root) { aProj.Root = CleanPath(aRoot); m_Dirty = true; }

                bool aEnabled = g.Toggle("啟用", aProj.Enabled, aId + "/enabled");
                if (aEnabled != aProj.Enabled) { aProj.Enabled = aEnabled; m_Dirty = true; }

                // 現場探測 —— 讀的是**欄位現值**（含未儲存的草稿），所以打錯路徑當場就紅。
                ProjectReading aReading = ProjectProbe.Probe(aProj);
                g.Label($"　探測：{StateText(aReading.State)}"
                        + (aReading.AgentCommandsRoot != null
                            ? $"　資料根 {aReading.AgentCommandsRoot}{(aReading.AgentCommandsRootExists ? " ✓" : " ⚠不存在")}"
                            : "")
                        + (aReading.EditorLikelyRunning ? "　Editor 在跑" : ""));

                if (g.Button("✕ 移除（要按儲存才落檔）", aId + "/remove"))
                {
                    m_Draft.Projects.RemoveAt(i);
                    m_Dirty = true;
                    m_Message = "・已從草稿移除一列 —— 按「儲存」才會寫回檔案";
                    return;   // 結構變了，這一輪不再往下畫（index 已位移）
                }
            }
        }
        if (m_Draft.Projects.Count == 0) g.Note("（目前沒有任何專案）");

        // ── 新增：貼路徑 → 探測 → 加進草稿 ────────────────────────────
        g.Space();
        using (g.Box("＋ 新增專案"))
        {
            string aNewPath = g.TextField("專案根路徑（如 D:/Unity/LY）", "", "projects/new/path");
            string aClean = CleanPath(aNewPath);
            if (aClean.Length > 0 && !Directory.Exists(aClean))
                g.Note($"　⚠ 路徑不存在：{aClean}（新增會被擋 —— cmd 派到不存在的資料樹是靜默 pending）");
            if (g.Button("加入草稿", "projects/new/add"))
            {
                if (aClean.Length == 0) m_Message = "⚠ 先填路徑再按新增";
                else if (!Directory.Exists(aClean)) m_Message = $"⚠ 沒有加：路徑不存在（{aClean}）";
                else if (m_Draft.Projects.Any(p => SamePath(p.Root, aClean)))
                    m_Message = "⚠ 沒有加：這個路徑已經在清單裡";
                else
                {
                    m_Draft.Projects.Add(new SenateProject
                    {
                        Name = Path.GetFileName(aClean.TrimEnd('/', '\\')),
                        Root = aClean,
                        Enabled = true,
                    });
                    m_Dirty = true;
                    g.SetField("projects/new/path", "");
                    m_Message = $"・已加進草稿（名稱先用資料夾名，可改）—— 按「儲存」才落檔";
                }
            }
        }

        // ── 儲存 ────────────────────────────────────────────────────
        g.Space();
        using (g.Row())
        {
            if (g.Button(m_Dirty ? "儲存（有未存的改動）" : "儲存", "projects/save")) Save();
            if (g.Button("放棄改動", "projects/revert"))
            {
                Load();
                m_Message = "・已重新讀取檔案（未儲存的改動丟掉了，檔案沒有被動過）";
            }
        }
        if (m_Dirty) g.Note("⚠ 有改動還沒儲存 —— 離開這一頁就會丟掉（本頁刻意不自動存）");
        if (m_Message != null) g.Note(m_Message);
    }

    void Save()
    {
        if (m_Draft == null) return;
        var aErrors = m_Draft.Validate();
        if (aErrors.Count > 0)
        {
            m_Message = "⚠ 沒有儲存 —— 設定有問題：" + string.Join("；", aErrors);
            return;
        }
        try { m_Draft.Save(m_ConfigPath); }
        catch (Exception e)
        {
            m_Message = $"⚠ 寫檔失敗：{e.GetType().Name}: {e.Message}";
            return;
        }
        // 回讀確認 —— 寫入端會替自己說謊。
        try
        {
            SenateConfig? aBack = SenateConfig.Load(m_ConfigPath);
            int aGot = aBack?.Projects.Count ?? -1;
            m_Message = aGot == m_Draft.Projects.Count
                ? $"✓ 已存進 {Path.GetFileName(m_ConfigPath)}（回讀確認 {aGot} 個專案）"
                : $"⚠ 寫進去了但回讀是 {aGot} 個（期望 {m_Draft.Projects.Count}）—— 有第二個寫入者？";
            m_Dirty = false;
        }
        catch (InvalidDataException e) { m_Message = $"⚠ 寫完之後回讀失敗（檔案可能壞了）：{e.Message}"; }
    }

    /// <summary>去掉包住整串的引號與前後空白（檔案總管「複製路徑」帶雙引號 —— submodule 頁同一課）。</summary>
    static string CleanPath(string iRaw)
    {
        string s = (iRaw ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        return s.Replace('\\', '/').TrimEnd('/');
    }

    static bool SamePath(string iA, string iB)
        => string.Equals(CleanPath(iA), CleanPath(iB), StringComparison.OrdinalIgnoreCase);

    static string StateText(ProbeState iState) => iState switch
    {
        ProbeState.Ok => "✓ 可用",
        ProbeState.Missing => "⚠ 路徑不存在",
        ProbeState.NotGitRepo => "⚠ 不是 git repo",
        ProbeState.NotConfigured => "（未設定）",
        _ => iState.ToString(),
    };
}
