// 區塊職責：`cmd coding --arg op=end` 的**編譯閘（Senate 側那把尺）** —— 跑一次 `dotnet build`。
// 物理意義：兩個宿主的尺**不同形，而且不可以合成一把**（TASK-0058 A/B/C 拍板附註）：
//           Unity 側是 `unity-compile-status`（tracker ＋ ErrorLog 對帳），這一側是 .NET 的編譯。
//           （2026-09-10 更名：舊名 `check_compile` 那支 python 已整支刪除。）
//           硬湊一把共用的尺，會讓其中一邊量的**不是它自己的編譯**。
// 數值影響：跑一次 `dotnet build`（秒級，`-v quiet`）。**不寫任何檔**、不動 session ——
//           它只回一個判定，關場是呼叫端的事。
//
// ⭐ Unity 那把尺（TASK-0454：Unity 側的施工場入口退場後搬來這裡）：
//   施工範圍碰到這一場的 Unity 專案（資料根的上一層）⇒ **另外**讀那個專案的 `.compile_status.json`。
//   兩把尺**各自判、各自報**，⛔ 不合成一個數字；任一把紅 ⇒ 整體紅。
//   Unity 那把的三格照搬原本 Unity 側的閘：① 不在編譯中 ② 讀數晚於開場（檔案 mtime）③ 0 errors。
//   🩸 ② 擋的是「開場前那份綠燈」—— 它真實、格式正確，只是沒涵蓋本場改的東西。
//   ⚠ 它擋不到「開場後改了檔又沒 recompile」⇒ 組件比原始碼舊的檔數（stale）印進讀數當提醒，⛔ 不當閘
//     （同時在場的人改的檔也會算進去，拿它擋會擋到別人的進度）。
//
// 🩸 為什麼這一格量的**不是** `build.sh`（而且不能是）：
//    `build.sh` 會停 Server、殺掉開著的 senate 視窗，然後覆寫 `publish/senate.exe`
//    —— 而那正是**當下正在執行的那個檔**（Access denied，D10 那族的血證）。
//    ⇒ 從 `senate.exe` 裡面跑它是物理上不成立的。
//    ⇒ 本閘的射程只有「**編譯過不過**」，而**出廠驗收是人要另外跑的那一格** ——
//      判定字串必須把這句話帶著走，否則「編譯綠」會被讀成「可以交付了」。
using System.Diagnostics;
using System.IO;
using SCP.Core.Compile;
using SCP.Core.Session;

namespace Senate.Core;

/// <summary>Senate 側的 Coding 退出閘：`dotnet build` 綠了才放行。</summary>
public static class SenateCodingExitGate
{
    /// <summary>這把尺量到的射程 —— **跟著判定一起回**，不要讓結論離開它的定語。</summary>
    public const string Scope = "`dotnet build`（本 repo 的編譯）；⛔ **不含 `build.sh` 出廠驗收**"
        + "（它會覆寫正在執行的 senate.exe，從 CLI 裡跑不了）—— 那一格請自己跑一次。";

    /// <summary>Unity 那把尺的射程。</summary>
    public const string UnityScope = "Unity `.compile_status.json`（不在編譯中／晚於開場／0 errors）；"
        + "⚠ 開場後改了檔又沒 recompile 的話它看不到 —— 要比到你這次的改動請先跑 `senate cmd unity-recompile`";

    /// <summary>裝上閘。<paramref name="iRepoRoot"/> ＝ 要編譯哪個 repo。</summary>
    public static void Install(string iRepoRoot)
        => SCP_CodingExitGateHost.Gate = iReq => Run(iRepoRoot, iReq);

    static SCP_CodingExitVerdict Run(string iRepoRoot, SCP_CodingExitRequest iReq)
    {
        SCP_CodingExitVerdict aDotnet = RunDotnet(iRepoRoot);
        string? aUnityRoot = UnityProjectInScope(iReq);
        if (aUnityRoot == null) return aDotnet;

        SCP_CodingExitVerdict aUnity = RunUnity(aUnityRoot, iReq);
        return new SCP_CodingExitVerdict(
            aDotnet.Green && aUnity.Green,
            $"dotnet {(aDotnet.Green ? "🟢" : "🔴")} {aDotnet.Summary}　｜　Unity {(aUnity.Green ? "🟢" : "🔴")} {aUnity.Summary}",
            Scope + "　＋　" + UnityScope);
    }

    // ===========================================================
    // 區塊職責：這一場的範圍有沒有碰到它的 Unity 專案。
    // 物理意義：⚠ 舊假設（TASK-0390 待改）：資料根是 `<專案>/AgentCommands` ⇒ Unity 專案根是上一層 —— 資料根搬到 Valhalla 後不成立；有 `Assets/` 才是 Unity 專案。
    //          範圍沒宣告（＝全域）視為碰到 —— 安全側，同施工場「不宣告＝全域獨佔」的判準。
    // ===========================================================
    static string? UnityProjectInScope(SCP_CodingExitRequest iReq)
    {
        if (iReq.DataRoot.Length == 0) return null;
        string aProj = Path.GetDirectoryName(iReq.DataRoot.TrimEnd('/', '\\')) ?? "";
        if (aProj.Length == 0 || !Directory.Exists(Path.Combine(aProj, "Assets"))) return null;
        if (iReq.Scope.Trim().Length == 0) return aProj;
        return SCP_SessionScope.Overlaps(iReq.Scope, aProj) ? aProj : null;
    }

    static SCP_CodingExitVerdict RunUnity(string iProjectRoot, SCP_CodingExitRequest iReq)
    {
        string aDataRoot = iReq.DataRoot;
        SCP_UnityCompileRead aRead = SCP_UnityCompile.Read(aDataRoot);
        if (!aRead.Found || aRead.Status == null)
            return new SCP_CodingExitVerdict(false,
                $"沒有讀數（`{aRead.Path}`）—— tracker 沒跑過或讀不動，先 `senate cmd unity-recompile`"
                + (aRead.Error.Length > 0 ? "：" + aRead.Error : ""), UnityScope);
        if (aRead.Status.in_progress)
            return new SCP_CodingExitVerdict(false, "還在編譯中（`in_progress=true`）—— 結果尚未定案", UnityScope);

        System.DateTime? aStart = SCP_ActivitySession.ParseIsoToLocal(iReq.StartTs);
        System.DateTime aStampLocal = aRead.WriteTimeUtc.ToLocalTime();
        if (aStart.HasValue && aStampLocal < aStart.Value)
            return new SCP_CodingExitVerdict(false,
                $"讀數是**開場前**量的（{aStampLocal:yyyy-MM-dd HH:mm:ss} < 開場 {aStart.Value:yyyy-MM-dd HH:mm:ss}）"
                + "—— 沒涵蓋本場改的東西，先 `senate cmd unity-recompile`", UnityScope);

        int aErrors = aRead.Status.total_errors;
        SCP_UnityCompile.SCP_UnityStaleResult aStale = SCP_UnityCompile.StaleSources(iProjectRoot);
        string aStaleNote = !aStale.Measured
            ? "　stale 沒量到（" + aStale.Error + "）"
            : aStale.StaleCount == 0 ? "　stale 0"
            : $"　⚠ stale {aStale.StaleCount}（有 .cs 比組件新 —— 改了沒 recompile？）";
        if (aErrors > 0)
            return new SCP_CodingExitVerdict(false, $"**{aErrors}** 個編譯錯誤（讀數 {aStampLocal:HH:mm:ss}）" + aStaleNote, UnityScope);
        return new SCP_CodingExitVerdict(true, $"0 errors（讀數 {aStampLocal:HH:mm:ss}，晚於開場）" + aStaleNote, UnityScope);
    }

    static SCP_CodingExitVerdict RunDotnet(string iRepoRoot)
    {
        var aSw = Stopwatch.StartNew();
        var aPsi = new ProcessStartInfo("dotnet", "build --nologo -v quiet")
        {
            WorkingDirectory = iRepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var aProc = Process.Start(aPsi);
        if (aProc == null)
        {
            // ⚠ 起不來**不是紅燈也不是綠燈** —— 它是「這把尺沒量成」，要說得出來。
            return new SCP_CodingExitVerdict(false, "起不了 `dotnet`（PATH 上沒有？）—— **這不是編譯結果**", Scope);
        }
        string aOut = aProc.StandardOutput.ReadToEnd();
        string aErr = aProc.StandardError.ReadToEnd();
        aProc.WaitForExit();
        aSw.Stop();

        bool aGreen = aProc.ExitCode == 0;
        // ⚠ 摘要要帶**讀數**（exit code ＋ 耗時 ＋ 第一條錯誤），不要只回 true/false ——
        //   紅燈時讀的人第一個問題是「哪裡紅」，而那句話應該在這裡而不是要他再跑一次。
        string aFirstError = FirstErrorLine(aOut) ?? FirstErrorLine(aErr) ?? "";
        string aSummary = aGreen
            ? $"exit 0／{aSw.Elapsed.TotalSeconds:0.0}s（{iRepoRoot}）"
            : $"exit {aProc.ExitCode}／{aSw.Elapsed.TotalSeconds:0.0}s"
              + (aFirstError.Length > 0 ? $"　第一條：{aFirstError}" : "　（沒解析到錯誤行 —— 自己跑一次看全文）");
        return new SCP_CodingExitVerdict(aGreen, aSummary, Scope);
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
