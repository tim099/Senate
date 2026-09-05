// 區塊職責：`cmd coding --arg op=end` 的**編譯閘（Senate 側那把尺）** —— 跑一次 `dotnet build`。
// 物理意義：兩個宿主的尺**不同形，而且不可以合成一把**（TASK-0058 A/B/C 拍板附註）：
//           Unity 側是 `check_compile`（tracker ＋ ErrorLog 對帳），這一側是 .NET 的編譯。
//           硬湊一把共用的尺，會讓其中一邊量的**不是它自己的編譯**。
// 數值影響：跑一次 `dotnet build`（秒級，`-v quiet`）。**不寫任何檔**、不動 session ——
//           它只回一個判定，關場是呼叫端的事。
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

/// <summary>Senate 側的 Coding 退出閘：`dotnet build` 綠了才放行。</summary>
public static class SenateCodingExitGate
{
    /// <summary>這把尺量到的射程 —— **跟著判定一起回**，不要讓結論離開它的定語。</summary>
    public const string Scope = "`dotnet build`（本 repo 的編譯）；⛔ **不含 `build.sh` 出廠驗收**"
        + "（它會覆寫正在執行的 senate.exe，從 CLI 裡跑不了）—— 那一格請自己跑一次。";

    /// <summary>裝上閘。<paramref name="iRepoRoot"/> ＝ 要編譯哪個 repo。</summary>
    public static void Install(string iRepoRoot)
        => SCP_CodingExitGateHost.Gate = () => Run(iRepoRoot);

    static SCP_CodingExitVerdict Run(string iRepoRoot)
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
