// 區塊職責：記錄 CLI 等 queue 檔案鎖逾時的現場，供下一次 TASK-0314 類故障排查。
// 物理意義：逾時發生在 Cmd 入列前，沒有 cmd_id，也不屬於已執行 Cmd 的 _cmd_errors；
//           只看殘留的 .lock 檔無法知道當時誰持有 OS 檔案握把。
// 數值影響：每次逾時寫一份獨立報告；寫檔失敗不蓋掉原始錯誤，仍由呼叫端回 exit 3。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SCP.Core.Io;
using Senate.Core;

namespace Senate.Cli;

internal static class QueueLockErrorLog
{
    const string DirName = "_queue_lock_errors";

    internal static string? Write(string iRepoRoot, SCP_FileLockTimeoutException iError, string[] iArgs)
    {
        DateTime aNow = DateTime.Now;
        string aName = $"{aNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}-{Guid.NewGuid():N}.md";
        string? aPrimaryError = null;
        foreach (string aDir in new[]
                 {
                     Path.Combine(SenatePaths.RuntimeDir(iRepoRoot), DirName),
                     Path.Combine(Path.GetTempPath(), "senate" + DirName),
                 })
        {
            try
            {
                Directory.CreateDirectory(aDir);
                string aPath = Path.Combine(aDir, aName);
                File.WriteAllText(aPath, Render(iError, iArgs, aNow, aPrimaryError), new UTF8Encoding(false));
                return aPath;
            }
            catch (Exception e) { aPrimaryError ??= $"{aDir}: {e.GetType().Name}: {e.Message}"; }
        }
        return null;
    }

    internal static string Render(SCP_FileLockTimeoutException iError, string[] iArgs, DateTime iNow, string? iPrimaryError = null)
    {
        var aText = new StringBuilder();
        aText.AppendLine("# Queue lock timeout (TASK-0314)");
        aText.AppendLine();
        if (iPrimaryError != null) aText.AppendLine($"> Primary report directory failed: {iPrimaryError}\n");
        aText.AppendLine($"- Time: {iNow.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)} / {iNow.ToUniversalTime():O}");
        aText.AppendLine($"- Operation: {Operation(iArgs)}");
        aText.AppendLine($"- Lock path: `{iError.LockPath}`");
        aText.AppendLine($"- Wait limit: {iError.TimeoutSec.ToString(CultureInfo.InvariantCulture)} seconds");
        aText.AppendLine($"- Caller: pid {Environment.ProcessId}, user {Environment.UserName}, build {ServerHost.BuildId}");
        aText.AppendLine($"- Executable: `{Environment.ProcessPath}`");
        aText.AppendLine("- Submission: rejected before this call wrote to the queue; retry after investigating the lock");
        aText.AppendLine();
        aText.AppendLine("## Lock file snapshot");
        aText.AppendLine(LockFileSnapshot(iError.LockPath));
        aText.AppendLine("File existence and timestamps do not identify the process holding the OS lock.");
        aText.AppendLine();
        aText.AppendLine("## Senate process candidates");
        aText.AppendLine(ProcessCandidates());
        aText.AppendLine("These processes are candidates only; this report cannot identify the lock owner.");
        aText.AppendLine();
        aText.AppendLine("## Exception and inner error");
        aText.AppendLine("```");
        aText.AppendLine(iError.ToString());
        aText.AppendLine("```");
        return aText.ToString();
    }

    static string Operation(string[] iArgs)
    {
        // 只留位置參數：--arg / --arg-file 可能含訊息內文、憑證或路徑，不放入報告。
        return string.Join(" ", iArgs.Take(3).TakeWhile(iPart => !iPart.StartsWith('-')));
    }

    static string LockFileSnapshot(string iPath)
    {
        try
        {
            var aFile = new FileInfo(iPath);
            if (!aFile.Exists) return "- The lock file is absent at report time.";
            return $"- Exists; size={aFile.Length}; created={aFile.CreationTimeUtc:O}; modified={aFile.LastWriteTimeUtc:O} (UTC).";
        }
        catch (Exception e) { return $"- Metadata unavailable: {e.GetType().Name}: {e.Message}"; }
    }

    static string ProcessCandidates()
    {
        try
        {
            var aLines = new List<string>();
            foreach (Process aProcess in Process.GetProcesses())
            {
                using (aProcess)
                {
                    string aName;
                    try { aName = aProcess.ProcessName; }
                    catch (Exception) { continue; }
                    if (!aName.StartsWith("senate", StringComparison.OrdinalIgnoreCase)) continue;
                    string aStart;
                    try { aStart = aProcess.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
                    catch (Exception e) { aStart = $"unavailable ({e.GetType().Name})"; }
                    aLines.Add($"- {aName}: pid={aProcess.Id}, started={aStart} UTC");
                }
            }
            return aLines.Count == 0 ? "- No Senate process was visible at report time." : string.Join("\n", aLines);
        }
        catch (Exception e) { return $"- Process snapshot unavailable: {e.GetType().Name}: {e.Message}"; }
    }
}
