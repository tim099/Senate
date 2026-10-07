// 區塊職責：對一個「被關聯的專案」取讀數 —— 它在不在、是不是 git repo、在哪個分支、多髒、
//           資料根解析到哪裡、Unity Editor 現在有沒有在跑。
// 物理意義：後台第一頁要回答的問題是「我管的這幾個專案現在是什麼狀態」。
//           ⚠ 每一格都要能分辨**三態**：沒設定 / 設定了但不存在 / 存在且可用。
//             把它們壓成「不可用」是這套系統最貴的錯誤形狀（LY 專案 2026-08-21：
//             查無帳戶被 GetBalance 回成 0，於是「不存在」長得跟「餘額零」一樣）。
// 數值影響：純讀（git status / File.Exists / mtime），不寫任何檔。
//           git 讀數走 SCP_Core 的共用層（SCP_GitRepo）—— 本專案**不自己開第二份 git 封裝**：
//           護欄（core.quotepath / GIT_TERMINAL_PROMPT / 逾時 kill / process 登記）只能有一個落點，
//           而第二份實作漏掉其中一格的症狀全是靜默的。
using SCP.Core.Git;
using SCP.Core.Paths;

namespace Senate.Core;

public enum ProbeState { NotConfigured, Missing, NotGitRepo, Ok }

public sealed record ProjectReading(
    string Name,
    string Root,
    ProbeState State,
    string? Branch,
    int? DirtyCount,
    int StagedCount,
    string? AgentCommandsRoot,
    bool AgentCommandsRootExists,
    string? EditorHeartbeatAgeText,
    bool Enabled)
{
    /// <summary>Editor 在 tick ⇒ 這個專案的 index **現在不該由外部工具動**。</summary>
    public bool EditorLikelyRunning { get; init; }
}

public static class ProjectProbe
{
    /// <summary>酒保 daemon 的心跳檔（Unity Editor 的 update 迴圈活著時每 0.5 秒摸一次）—— 在 `&lt;酒館根&gt;/bartender/` 底下。</summary>
    static string HeartbeatPath(string iDataRoot)
        => Path.Combine(SCP.Core.Paths.SCP_DataPaths.ChatTavern(new SCP.Core.Paths.SCP_DataRoot(iDataRoot)), "bartender", "_heartbeat.txt");

    /// <summary>心跳多久沒動就視為 Editor 沒在 tick。0.5s 節拍 ⇒ 4 秒是很寬鬆的判準。</summary>
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(4);

    /// <param name="iDataRoot">
    /// 全域資料根（呼叫端用唯一入口 `SenatePathBinding.ResolveDataRoot` 解好再給；null ＝ 解不出來）。
    /// ⚠ TASK-0390：資料根**不屬於任何專案**，本函式不再自己推（舊的 pointer 檔／`&lt;專案&gt;/AgentCommands` 那條已刪）。
    /// </param>
    public static ProjectReading Probe(SenateProject iProject, string? iDataRoot)
    {
        string aName = string.IsNullOrWhiteSpace(iProject.Name) ? "(未命名)" : iProject.Name;

        if (string.IsNullOrWhiteSpace(iProject.Root))
            return new ProjectReading(aName, "", ProbeState.NotConfigured, null, null, 0, null, false, null, iProject.Enabled);

        string aRoot = iProject.Root.Replace('\\', '/').TrimEnd('/');
        if (!Directory.Exists(aRoot))
            return new ProjectReading(aName, aRoot, ProbeState.Missing, null, null, 0, null, false, null, iProject.Enabled);

        if (!SCP_Git.IsRepo(aRoot))
            return new ProjectReading(aName, aRoot, ProbeState.NotGitRepo, null, null, 0, null, false, null, iProject.Enabled);

        string? aDataRoot = string.IsNullOrWhiteSpace(iDataRoot) ? null : iDataRoot!.Replace('\\', '/').TrimEnd('/');
        bool aDataRootExists = aDataRoot != null && Directory.Exists(aDataRoot);

        string? aHbText = null;
        bool aEditorAlive = false;
        if (aDataRootExists)
        {
            string aHb = HeartbeatPath(aDataRoot!);
            if (File.Exists(aHb))
            {
                TimeSpan aAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(aHb);
                aEditorAlive = aAge <= HeartbeatStaleAfter;
                aHbText = aAge.TotalSeconds < 90
                    ? $"{aAge.TotalSeconds:F1} 秒前"
                    : $"{aAge.TotalMinutes:F0} 分鐘前";
            }
            else
            {
                // 「沒有心跳檔」與「心跳很舊」不同形：前者可能是這個專案沒裝那套 daemon。
                aHbText = "(無心跳檔)";
            }
        }

        return new ProjectReading(
            aName, aRoot, ProbeState.Ok,
            SCP_GitRepo.Branch(aRoot),
            // ⚠ ChangeCount **含 untracked**（顯示用的那把尺）。安全線要用的是
            //   SCP_GitRepo.DirtyState（不含 untracked）—— 兩把尺不得互相代用。
            SCP_GitRepo.ChangeCount(aRoot),
            SCP_GitRepo.StagedPaths(aRoot).Count,
            aDataRoot, aDataRootExists,
            aHbText, iProject.Enabled)
        { EditorLikelyRunning = aEditorAlive };
    }

    // ⛔ 2026-10-07（TASK-0390）刪掉 `ResolveAgentCommandsRoot`（pointer 檔／`<專案>/AgentCommands` 的第二份算式）：
    //   資料根只有一個入口 —— `SenatePathBinding.ResolveDataRoot`（走描述表）。
}
