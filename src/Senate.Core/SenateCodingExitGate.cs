// 區塊職責：Coding 退場閘依每條施工路徑找工程、選建置方式、各自回報（TASK-0463）。
// 物理意義：資料根只存 session，不代表施工工程；無法辨認或無法執行都擋下。
// 數值影響：.NET 跑 dotnet build；Unity 問已開啟 Editor 的官方 CLI。不改 session。
//
// 🩸 為什麼這一格量的**不是** `build.sh`（而且不能是）：
//    `build.sh` 會停 Server、殺掉開著的 senate 視窗，然後覆寫 `publish/senate.exe`
//    —— 而那正是**當下正在執行的那個檔**（Access denied，D10 那族的血證）。
//    ⇒ 從 `senate.exe` 裡面跑它是物理上不成立的。
//    ⇒ 本閘的射程只有「**編譯過不過**」，而**出廠驗收是人要另外跑的那一格** ——
//      判定字串必須把這句話帶著走，否則「編譯綠」會被讀成「可以交付了」。
using System.Diagnostics;
using System.Globalization;
using SCP.Core.Json;
using SCP.Core.Session;

namespace Senate.Core;

/// <summary>施工路徑的工程編譯全部過關才放行；未知與未驗都不是綠燈。</summary>
public static class SenateCodingExitGate
{
    public const string Scope = "依每條施工路徑驗所屬工程（dotnet build／Unity CLI）；不含 build.sh 出廠驗收。";

    public sealed record BuildTarget(string Kind, string Root, string Input);
    public sealed record CommandReading(int ExitCode, string Output, string Error);

    /// <summary>宿主根只為相容入口保留，不用它替未宣告的施工路徑猜工程。</summary>
    public static void Install(string iRepoRoot)
        => SCP_CodingExitGateHost.Gate = iReq => Run(iReq);

    /// <summary>Unity 工程的產生式 csproj 不代表 Editor 的編譯，先辨認 ProjectSettings。</summary>
    public static BuildTarget FindTarget(string iPath)
    {
        if (!Path.IsPathFullyQualified(iPath)) throw new InvalidOperationException("施工路徑必須是絕對路徑");
        string aPath = Path.GetFullPath(iPath);
        if (!File.Exists(aPath) && !Directory.Exists(aPath))
            throw new InvalidOperationException("施工路徑不存在：" + aPath);
        string aDir = File.Exists(aPath) ? Path.GetDirectoryName(aPath)! : aPath;
        var aDirs = new List<string>();
        for (DirectoryInfo? d = new(aDir); d != null; d = d.Parent) aDirs.Add(d.FullName);
        foreach (string d in aDirs)
            if (Directory.Exists(Path.Combine(d, "ProjectSettings"))) return new("unity", d, d);

        if (File.Exists(aPath) && IsBuildFile(aPath)) return new("dotnet", aDir, aPath);
        foreach (string d in aDirs)
        {
            string[] aFiles = Directory.GetFiles(d).Where(IsBuildFile).OrderBy(x => x, StringComparer.Ordinal).ToArray();
            if (aFiles.Length == 0) continue;
            string[] aSolutions = aFiles.Where(x => Path.GetExtension(x) is ".sln" or ".slnx").ToArray();
            string[] aChoice = aSolutions.Length > 0 ? aSolutions : aFiles;
            if (aChoice.Length != 1) throw new InvalidOperationException("工程不唯一，請將施工路徑指到所屬工程：" + d);
            return new("dotnet", d, aChoice[0]);
        }
        throw new InvalidOperationException("這條路徑不知道怎麼驗（找不到工程）");
    }

    static bool IsBuildFile(string iPath)
        => Path.GetExtension(iPath).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj" or ".sln" or ".slnx";

    /// <summary>每條路徑保留獨立結果；同一工程只跑一次，後面的路徑沿用同次讀數。</summary>
    public static SCP_CodingExitVerdict Run(SCP_CodingExitRequest iRequest,
        Func<string, IReadOnlyList<string>, string, CommandReading>? iRunner = null, Action? iPollWait = null)
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
                    try
                    {
                        aVerdict = aTarget.Kind == "unity"
                            ? RunUnity(aTarget, iRunner ?? Execute, iPollWait ?? (() => Thread.Sleep(1000)))
                            : RunDotnet(aTarget, iRunner ?? Execute);
                    }
                    catch (Exception e) { aVerdict = new(false, "未驗：" + e.GetType().Name + ": " + e.Message, Scope); }
                    aCache.Add(aTarget.Input, aVerdict);
                }
                aAllGreen &= aVerdict.Green;
                aRows.Add($"{aPath} → {aTarget.Kind} {aTarget.Input}：{(aVerdict.Green ? "通過" : "未通過")}；{aVerdict.Summary}");
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

    // 已知 Unity CLI schema 走 typed mapper；必需欄位額外檢查，缺欄位不借預設值過關。
    public sealed class UnityEnvelope
    {
        public bool success;
        public UnityData data = new();
        public List<UnityError> errors = new();
    }
    public sealed class UnityData { public UnityResult result = new(); }
    public sealed class UnityError { public string message = ""; }
    public sealed class UnityResult
    {
        public string status = "";
        public long cursor;
        public string session = "";
        public bool compilationFailed;
        public List<UnityLog> entries = new();
    }
    public sealed class UnityLog { public string message = ""; }

    static UnityEnvelope ReadUnity(CommandReading iRead, string iRequired)
    {
        var aJson = SCP_JsonParser.Parse(iRead.Output);
        var aReply = new UnityEnvelope();
        var aOptions = new SCP_JsonMapOptions();
        SCP_JsonMapper.Populate(aReply, aJson, aOptions);
        _ = aJson[nameof(UnityEnvelope.success)].AsBool();
        if (iRead.ExitCode != 0 || !aReply.success || aReply.errors.Count > 0)
            throw new InvalidOperationException("Unity CLI 未驗：exit " + iRead.ExitCode + "；"
                + string.Join("；", aReply.errors.Select(x => x.message)) + iRead.Error);
        if (aOptions.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("；", aOptions.Diagnostics));
        var aResult = aJson[nameof(UnityEnvelope.data)][nameof(UnityData.result)];
        if (iRequired == nameof(UnityResult.compilationFailed)) _ = aResult[iRequired].AsBool();
        else if (iRequired == nameof(UnityResult.cursor)) _ = aResult[iRequired].AsLong();
        else _ = aResult[iRequired].AsString();
        return aReply;
    }

    static SCP_CodingExitVerdict RunUnity(BuildTarget iTarget,
        Func<string, IReadOnlyList<string>, string, CommandReading> iRunner, Action iPollWait)
    {
        CommandReading Call(string iCommand, params string[] iExtra)
            => iRunner("unity", new[] { "--json", "--no-banner", "command", iCommand,
                "--project-path", iTarget.Root, "--timeout", "10" }.Concat(iExtra).ToArray(), iTarget.Root);
        UnityResult aBaseline = ReadUnity(Call("console_status"), nameof(UnityResult.cursor)).data.result;
        string aCursor = aBaseline.cursor.ToString(CultureInfo.InvariantCulture);
        ReadUnity(Call("recompile"), nameof(UnityResult.status));
        var aWatch = Stopwatch.StartNew();
        string aLastConnection = "";
        for (int i = 0; i < 120 && aWatch.Elapsed < TimeSpan.FromMinutes(2); i++)
        {
            CommandReading aReading = Call("recompile_status");
            // Domain reload 可短暫失聯，只重試讀取，不重送 recompile；到期仍是未驗。
            if (aReading.ExitCode == 6)
            {
                aLastConnection = aReading.Output.Trim() + aReading.Error;
                iPollWait();
                continue;
            }
            UnityEnvelope aReply = ReadUnity(aReading, nameof(UnityResult.compilationFailed));
            string aStatus = aReply.data.result.status;
            if (aStatus is "completed" or "up_to_date")
            {
                if (!aReply.data.result.compilationFailed)
                    return new(true, "Unity " + aStatus + "；compilationFailed=false"
                        + (aStatus == "up_to_date" ? "（Editor 當前讀數，沒有新重編）" : ""), Scope);
                try
                {
                    string[] aExtra = aBaseline.session.Length > 0
                        ? new[] { "--level", "error", "--since", aCursor, "--since_session", aBaseline.session, "--tail", "0" }
                        : new[] { "--level", "error", "--since", aCursor, "--tail", "0" };
                    UnityEnvelope aErrors = ReadUnity(Call("console", aExtra), nameof(UnityResult.cursor));
                    string aFirst = aErrors.data.result.entries.Select(x => x.message).FirstOrDefault(x => x.Contains("error CS", StringComparison.Ordinal)) ?? "本趟未取得 error CS 明細（既有錯誤可能早於游標）";
                    return new(false, "Unity " + aStatus + "；compilationFailed=true；" + aFirst, Scope);
                }
                catch (Exception e) { return new(false, "compilationFailed=true；明細讀取失敗：" + e.Message, Scope); }
            }
            if (aStatus is not ("idle" or "triggered" or "compiling"))
                return new(false, "Unity 未驗：不認得狀態 " + aStatus, Scope);
            iPollWait();
        }
        return new(false, "Unity 未驗：等待編譯完成逾時（2 分鐘）；" + aLastConnection, Scope);
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
