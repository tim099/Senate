// 區塊職責：環境／專案關聯的**診斷頁** —— 用 Gui（中間層）畫，一份頁面碼四種輸出。
// 物理意義：這一頁刻意只做「取讀數並攤開」。它同時是三件事的證明：
//           ① 撰寫端手感是 GUILayout（一頁一個方法、從上往下寫）
//           ② 同一份碼可以輸出成純文字（現在）與 ImGui 視窗（之後），頁面碼一行都不用改
//           ③ 沒有視窗也能驗收 UI —— 文字輸出可以 diff、可以貼給人看
// 數值影響：唯讀。它不改任何設定，也不動任何 repo 的 index。
using Senate.Core;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class DoctorPage : SCP_GuiToolPage
{
    readonly SenateModel m_Model;

    /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
    public DoctorPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public const string PageKey = "doctor";

    /// <summary>標題交給 controller 畫（麵包屑也吃它）—— 所以這裡**不含**取讀數次數，那是讀數不是身分。</summary>
    public override string Title => "Senate 環境檢查";

    /// <summary>列進入口頁的「診斷」組。</summary>
    public override string? MenuGroup => "診斷";

    EnvReading m_Env => m_Model.Env;
    IReadOnlyList<ProjectReading> m_Projects => m_Model.Projects;

    /// <summary>
    /// 工具列的鈕 —— 這一頁的兩個動作都是「對這一頁做的事」，所以放工具列而不是內容底部。
    /// ⚠ id 沿用舊的（`doctor/refresh` / `doctor/open-config`）：那是契約，
    /// 版面搬家不可以順手換掉別人腳本裡的字。
    /// </summary>
    protected override void ToolBarButtons(SCP_Ui g)
    {
        if (g.Button("重新取讀數", "doctor/refresh")) m_Model.Refresh();
        if (g.Button("開啟設定檔", "doctor/open-config")) OpenConfig();
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note($"第 {m_Model.RefreshCount} 次取讀數");

        using (g.Box("執行環境"))
        {
            using (g.Table("項目", "讀數", "判定"))
            {
                g.TableRow(".NET SDK（dotnet --version）", m_Env.DotnetSdkVersion ?? "(問不到)",
                    m_Env.DotnetSdkVersion == null ? "✗" : "✓");
                g.TableRow("執行期（Environment.Version）", m_Env.RuntimeVersion, "·");
                g.TableRow("git", m_Env.GitVersion?.ToString() ?? "(問不到)", m_Env.GitOkForPathspec ? "✓" : "✗");
                g.TableRow("git ≥ 2.25（--pathspec-from-file）",
                    m_Env.GitOkForPathspec ? "支援" : "不支援 —— pathspec 提交那條護欄會失效",
                    m_Env.GitOkForPathspec ? "✓" : "✗");
                g.TableRow("設定檔", m_Env.ConfigPath, m_Env.ConfigExists ? "✓" : "尚未 init");
            }
            if (!m_Env.ConfigExists)
                g.Note("還沒有 senate.local.json —— 跑 `senate init` 會從 config/senate.local.example.json 生一份（不覆寫既有檔）");
        }

        g.Space();
        g.Title($"關聯的專案（{m_Projects.Count}）");

        if (m_Projects.Count == 0)
        {
            g.Note("設定檔裡沒有任何專案。編輯 senate.local.json 的 projects[] 加上專案根目錄。");
            g.Note("⚠ 「沒設定」與「設定了但路徑不存在」是兩件事 —— 後者會在下面列成 Missing，不會靜默消失。");
            return;
        }

        using (g.Table("專案", "狀態", "分支", "工作區", "index", "Editor", "資料根"))
        {
            foreach (var p in m_Projects)
            {
                g.TableRow(
                    p.Enabled ? p.Name : $"{p.Name}（停用）",
                    StateText(p.State),
                    p.Branch ?? "-",
                    p.DirtyCount is int d ? $"{d} 筆改動" : "-",
                    p.StagedCount > 0 ? $"⚠ {p.StagedCount} 已 staged" : "乾淨",
                    p.EditorLikelyRunning ? $"在跑（{p.EditorHeartbeatAgeText}）" : (p.EditorHeartbeatAgeText ?? "-"),
                    p.AgentCommandsRootExists ? "✓" : (p.AgentCommandsRoot == null ? "-" : "✗ 不存在"));
            }
        }

        foreach (var p in m_Projects)
        {
            if (p.State == ProbeState.Ok && p.StagedCount > 0)
                g.Note($"{p.Name}：index 已有 {p.StagedCount} 個 staged 檔 ⇒ 自動 commit 會**擋下這個 repo**（先自己 commit 或 unstage）");
            if (p.State == ProbeState.Ok && p.EditorLikelyRunning)
                g.Note($"{p.Name}：Unity Editor 正在 tick ⇒ 自動 commit 會讓它做，本工具不動 index（不動別人正在寫的東西）");
            if (p.State == ProbeState.Missing)
                g.Note($"{p.Name}：設定的 root 不存在（{p.Root}）—— 這是設定壞了，不是「這個專案沒事」");
            if (p.State == ProbeState.NotGitRepo)
                g.Note($"{p.Name}：{p.Root} 不是 git repo");
            if (p.State == ProbeState.Ok && !p.AgentCommandsRootExists && p.AgentCommandsRoot != null)
                g.Note($"{p.Name}：資料根解析到 {p.AgentCommandsRoot}，但那個目錄不存在");
        }

    }

    void OpenConfig()
    {
        // 開檔案總管／預設編輯器。⚠ headless 環境會失敗 —— 失敗要說出來，不要當作按了沒事
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = m_Env.ConfigPath,
                UseShellExecute = true,
            });
        }
        catch (Exception e) { Console.Error.WriteLine($"⚠ 開啟設定檔失敗：{e.Message}"); }
    }

    static string StateText(ProbeState s) => s switch
    {
        ProbeState.Ok => "可用",
        ProbeState.Missing => "路徑不存在",
        ProbeState.NotGitRepo => "非 git repo",
        _ => "未設定",
    };
}
