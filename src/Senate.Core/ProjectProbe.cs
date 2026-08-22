// 區塊職責：對一個「被關聯的專案」取讀數 —— 它在不在、是不是 git repo、在哪個分支、多髒、
//           資料根解析到哪裡、Unity Editor 現在有沒有在跑。
// 物理意義：後台第一頁要回答的問題是「我管的這幾個專案現在是什麼狀態」。
//           ⚠ 每一格都要能分辨**三態**：沒設定 / 設定了但不存在 / 存在且可用。
//             把它們壓成「不可用」是這套系統最貴的錯誤形狀（LY 專案 2026-08-21：
//             查無帳戶被 GetBalance 回成 0，於是「不存在」長得跟「餘額零」一樣）。
// 數值影響：純讀（git status / File.Exists / mtime），不寫任何檔。
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
    /// <summary>酒保 daemon 的心跳檔（Unity Editor 的 update 迴圈活著時每 0.5 秒摸一次）。</summary>
    public const string HeartbeatRelPath = "ChatTavern/bartender/_heartbeat.txt";

    /// <summary>心跳多久沒動就視為 Editor 沒在 tick。0.5s 節拍 ⇒ 4 秒是很寬鬆的判準。</summary>
    public static readonly TimeSpan HeartbeatStaleAfter = TimeSpan.FromSeconds(4);

    public static ProjectReading Probe(SenateProject iProject)
    {
        string aName = string.IsNullOrWhiteSpace(iProject.Name) ? "(未命名)" : iProject.Name;

        if (string.IsNullOrWhiteSpace(iProject.Root))
            return new ProjectReading(aName, "", ProbeState.NotConfigured, null, null, 0, null, false, null, iProject.Enabled);

        string aRoot = iProject.Root.Replace('\\', '/').TrimEnd('/');
        if (!Directory.Exists(aRoot))
            return new ProjectReading(aName, aRoot, ProbeState.Missing, null, null, 0, null, false, null, iProject.Enabled);

        if (!GitCli.IsRepo(aRoot))
            return new ProjectReading(aName, aRoot, ProbeState.NotGitRepo, null, null, 0, null, false, null, iProject.Enabled);

        string? aDataRoot = ResolveAgentCommandsRoot(aRoot, iProject.AgentCommandsRoot);
        bool aDataRootExists = aDataRoot != null && Directory.Exists(aDataRoot);

        string? aHbText = null;
        bool aEditorAlive = false;
        if (aDataRootExists)
        {
            string aHb = Path.Combine(aDataRoot!, HeartbeatRelPath.Replace('/', Path.DirectorySeparatorChar));
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
            GitCli.Branch(aRoot),
            GitCli.DirtyCount(aRoot),
            GitCli.StagedPaths(aRoot).Count,
            aDataRoot, aDataRootExists,
            aHbText, iProject.Enabled)
        { EditorLikelyRunning = aEditorAlive };
    }

    /// <summary>
    /// 解析 AgentCommands 資料根：<c>"auto"</c>／空 → 先讀 pointer 檔
    /// <c>&lt;root&gt;/.agentcommands_root.local</c>，沒有則 <c>&lt;root&gt;/AgentCommands</c>。
    /// <para>⚠ 只有這兩個位置。**不猜第三個** —— 猜錯的症狀是寫進另一棵資料樹而且不報錯。</para>
    /// </summary>
    public static string? ResolveAgentCommandsRoot(string iProjectRoot, string iSetting)
    {
        string s = (iSetting ?? "").Trim();
        if (s.Length > 0 && !s.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return Path.IsPathRooted(s) ? s.Replace('\\', '/') : Path.Combine(iProjectRoot, s).Replace('\\', '/');

        string aPointer = Path.Combine(iProjectRoot, ".agentcommands_root.local");
        if (File.Exists(aPointer))
        {
            foreach (string raw in File.ReadAllLines(aPointer))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                return line.Replace('\\', '/').TrimEnd('/');
            }
        }
        return $"{iProjectRoot}/AgentCommands";
    }
}
