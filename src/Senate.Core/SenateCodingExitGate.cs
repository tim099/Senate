// 區塊職責：Coding 退場閘依每條施工路徑找所屬 .NET 工程、各自回報（TASK-0463）。
// 物理意義：資料根只存 session，不代表施工工程；無法辨認或無法執行都擋下。
// 數值影響：每個工程跑一次 dotnet build。不改 session。
//
// 🩸 為什麼這一格量的**不是** `build.sh`（而且不能是）：
//    `build.sh` 會停 Server、殺掉開著的 senate 視窗，然後覆寫 `publish/senate.exe`
//    —— 而那正是**當下正在執行的那個檔**（Access denied，D10 那族的血證）。
//    ⇒ 從 `senate.exe` 裡面跑它是物理上不成立的。
//    ⇒ 本閘的射程只有「**編譯過不過**」，而**出廠驗收是人要另外跑的那一格** ——
//      判定字串必須把這句話帶著走，否則「編譯綠」會被讀成「可以交付了」。
using System.Diagnostics;
using SCP.Core.Session;

namespace Senate.Core;

/// <summary>施工路徑的工程編譯全部過關才放行；未知與未驗都不是綠燈。</summary>
public static class SenateCodingExitGate
{
    public const string Scope = "依每條施工路徑驗所屬 .NET 工程（dotnet build）；不含 build.sh 出廠驗收。";

    public sealed record BuildTarget(string Root, string Input);
    public sealed record CommandReading(int ExitCode, string Output, string Error);

    /// <summary>宿主根只為相容入口保留，不用它替未宣告的施工路徑猜工程。</summary>
    public static void Install(string iRepoRoot)
        => SCP_CodingExitGateHost.Gate = iReq => Run(iReq);

    /// <summary>從施工路徑往上找最近一層的 .NET 工程；同層有 sln 就用 sln，仍不唯一或找不到都擋下。</summary>
    public static BuildTarget FindTarget(string iPath)
    {
        if (!Path.IsPathFullyQualified(iPath)) throw new InvalidOperationException("施工路徑必須是絕對路徑");
        string aPath = Path.GetFullPath(iPath);
        if (!File.Exists(aPath) && !Directory.Exists(aPath))
            throw new InvalidOperationException("施工路徑不存在：" + aPath);
        string aDir = File.Exists(aPath) ? Path.GetDirectoryName(aPath)! : aPath;
        var aDirs = new List<string>();
        for (DirectoryInfo? d = new(aDir); d != null; d = d.Parent) aDirs.Add(d.FullName);
        if (File.Exists(aPath) && IsBuildFile(aPath)) return new(aDir, aPath);
        foreach (string d in aDirs)
        {
            string[] aFiles = Directory.GetFiles(d).Where(IsBuildFile).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (aFiles.Length == 0) continue;
            string[] aSolutions = aFiles.Where(x => Path.GetExtension(x) is ".sln" or ".slnx").ToArray();
            string[] aChoice = aSolutions.Length > 0 ? aSolutions : aFiles;
            if (aChoice.Length != 1) throw new InvalidOperationException("工程不唯一，請將施工路徑指到所屬工程：" + d);
            return new(d, aChoice[0]);
        }
        throw new InvalidOperationException("這條路徑不知道怎麼驗（找不到 .NET 工程）");
    }

    static bool IsBuildFile(string iPath)
        => Path.GetExtension(iPath).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx";

    /// <summary>每條路徑保留獨立結果；同一工程只跑一次，後面的路徑沿用同次讀數。</summary>
    public static SCP_CodingExitVerdict Run(SCP_CodingExitRequest iRequest,
        Func<string, IReadOnlyList<string>, string, CommandReading>? iRunner = null)
    {
        if (string.IsNullOrWhiteSpace(iRequest.Scope))
            return new(false, "未宣告施工路徑 ⇒ 不知道怎麼驗，未驗編譯", Scope);
        var aRows = new List<string>();
        var aCache = new Dictionary<string, SCP_CodingExitVerdict>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        bool aAllGreen = true;
        foreach (string aPart in iRequest.Scope.Split('|'))
        {
            string aPath = aPart.Trim();
            try
            {
                BuildTarget aTarget = FindTarget(aPath);
                if (!aCache.TryGetValue(aTarget.Input, out var aVerdict))
                {
                    try { aVerdict = RunDotnet(aTarget, iRunner ?? Execute); }
                    catch (Exception e) { aVerdict = new(false, "未驗：" + e.GetType().Name + ": " + e.Message, Scope); }
                    aCache.Add(aTarget.Input, aVerdict);
                }
                aAllGreen &= aVerdict.Green;
                aRows.Add($"{aPath} → dotnet {aTarget.Input}：{(aVerdict.Green ? "通過" : "未通過")}；{aVerdict.Summary}");
            }
            catch (Exception e)
            {
                aAllGreen = false;
                aRows.Add(aPath + "：未驗；" + e.Message);
            }
        }
        return new(aAllGreen, string.Join("\n", aRows), Scope);
    }

    static SCP_CodingExitVerdict RunDotnet(BuildTarget iTarget,
        Func<string, IReadOnlyList<string>, string, CommandReading> iRunner)
    {
        var aSw = Stopwatch.StartNew();
        CommandReading aRead = iRunner("dotnet", new[] { "build", iTarget.Input, "--nologo", "-v", "quiet" }, iTarget.Root);
        aSw.Stop();
        string aError = FirstErrorLine(aRead.Output) ?? FirstErrorLine(aRead.Error) ?? aRead.Error.Trim();
        return new(aRead.ExitCode == 0, $"exit {aRead.ExitCode}／{aSw.Elapsed.TotalSeconds:0.0}s"
            + (aRead.ExitCode != 0 ? "；" + (aError.Length > 0 ? aError : "未取得錯誤明細") : ""), Scope);
    }

    /// <summary>並行排空 stdout/stderr 避免滿管卡死；啟動失敗與逾時都回未驗。</summary>
    static CommandReading Execute(string iExe, IReadOnlyList<string> iArgs, string iDirectory)
    {
        var aPsi = new ProcessStartInfo(iExe)
        {
            WorkingDirectory = iDirectory, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string a in iArgs) aPsi.ArgumentList.Add(a);
        using var aProc = Process.Start(aPsi) ?? throw new InvalidOperationException("起不了 " + iExe);
        Task<string> aOut = aProc.StandardOutput.ReadToEndAsync(), aErr = aProc.StandardError.ReadToEndAsync();
        if (!aProc.WaitForExit(180_000))
        {
            aProc.Kill(entireProcessTree: true);
            aProc.WaitForExit();
            throw new TimeoutException(iExe + " 未驗：執行逾時（3 分鐘）");
        }
        return new(aProc.ExitCode, aOut.GetAwaiter().GetResult(), aErr.GetAwaiter().GetResult());
    }

    /// <summary>抓第一條像編譯錯誤的行。⚠ 抓不到就回 null（**不要編一句看起來像錯誤的話**）。</summary>
    static string? FirstErrorLine(string iText)
    {
        if (string.IsNullOrEmpty(iText)) return null;
        foreach (string aLine in iText.Split('\n'))
        {
            string aTrim = aLine.Trim();
            if (aTrim.Contains(": error ", System.StringComparison.Ordinal)) return aTrim;
        }
        return null;
    }
}
