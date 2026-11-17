// 區塊職責：「跨主機連線」頁的「自我測試」格（FAR-64）—— 另開一個 senate 子行程跑 `selftest --only <項目>`，
//           把逐項讀數讀回來，整理成「摘要＋沒過的排最上面」給頁面畫。
// 物理意義：⛔ 不在後台這個行程裡直接呼叫測試本體：PeerTaskCleanRoom 跑的 40～80 秒會換掉全域的 SenateTaskServer.SendOverride，
//           ⑦之五 還會打開 DebugRefuseNothing ⇒ 同一個行程裡任務頁送出的 task 指令會被送進測試的暫存節點。
//           放在子行程裡，這些全域狀態跟著它一起結束；它當掉或卡住也不拖垮後台。
//           結果經 `SENATE_PEER_SELFTEST_REPORT_DIR` 交回：每次一個新的資料夾 ⇒ 不會讀到終端機同時在跑的那一份。
// 數值影響：每次起一個子行程（上限 5 分鐘，逾時連子行程一起砍）；資料夾讀完就刪。
// ⚠ 起不來／逾時／沒產生檔案 ⇒ 照實寫「沒有拿到結果」，⛔ 不畫成「通過 0／沒過 0」（看起來像沒事）。
#nullable enable
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Proc;

namespace Senate.Cli.Pages;

internal static class PeerLinkSelfTest
{
    internal sealed record Pick(string Key, string Label, string Eta, string ReportFile);

    // Key 是 selftest 的項目名（那兩支是 SelfTest 的 private 方法，nameof 拿不到 ⇒ PeerLinkSelfTestView 驗它還在目錄裡）
    internal static readonly Pick Link = new("PeerLinkCleanRoom", "連線測試", "約 20～40 秒", SelfTest.PeerLinkReportFile);
    internal static readonly Pick TaskServer = new("PeerTaskCleanRoom", "Task Server 測試", "約 40～80 秒", SelfTest.PeerTaskReportFile);

    internal sealed record Outcome(string Label, bool GotResult, bool Ok, int Pass, int Fail, string Headline, List<string> Rows, List<string> Notes);

    const int TimeoutMs = 5 * 60 * 1000;
    const int StderrTail = 20;
    const string ProcTag = "senate_peer_selftest_page";
    static readonly Regex s_FailSummary = new(@"^✗ 沒過 \d+ 項：", RegexOptions.CultureInvariant);

    /// <summary>
    /// 逐項讀數 → 摘要＋列表（純函式，頁面和 selftest 用同一支）。
    /// <para>計數跟 `selftest` 那一列一樣：結尾的「✓ 全部通過」／「✗ 沒過 N 項：…」是總結，不算一項。</para>
    /// <para>不是 ✓／✗ 開頭的行（節點紀錄、暫存資料夾位置）當備註，排在最後。</para>
    /// </summary>
    internal static Outcome Summarize(string iLabel, IReadOnlyList<string>? iLines, int iExit, string iProblem, DateTime iStart, TimeSpan iTook)
    {
        var aFail = new List<string>();
        var aPass = new List<string>();
        var aNotes = new List<string>();
        foreach (string aRaw in iLines ?? Array.Empty<string>())
        {
            string l = aRaw.TrimEnd('\r');
            if (l.Trim().Length == 0) continue;
            if (l == "✓ 全部通過" || s_FailSummary.IsMatch(l)) continue;
            if (l.StartsWith("✓ ", StringComparison.Ordinal)) aPass.Add(Show(l));
            else if (l.StartsWith("✗ ", StringComparison.Ordinal)) aFail.Add(Show(l));
            else aNotes.Add(Show(l));
        }
        var aProblem = iProblem.Split('\n').Select(x => x.TrimEnd('\r')).Where(x => x.Trim().Length > 0).Select(Show).ToList();
        string aWhen = $"{iStart:HH:mm:ss} 開始，花了 {(int)Math.Round(iTook.TotalSeconds)} 秒";

        if (aPass.Count + aFail.Count == 0)
        {
            if (aProblem.Count == 0) aProblem.Add("子行程結束碼 " + iExit + "，逐項檔裡一項都沒有");
            return new Outcome(iLabel, false, false, 0, 0, "沒有拿到結果　" + aWhen, new List<string>(), aProblem.Concat(aNotes).ToList());
        }

        bool aOk = aFail.Count == 0 && iExit == 0;
        if (aFail.Count == 0 && iExit != 0) aProblem.Insert(0, $"[注意] 逐項都過了，但子行程結束碼是 {iExit} ⇒ 不當成通過");
        return new Outcome(iLabel, true, aOk, aPass.Count, aFail.Count, $"通過 {aPass.Count} 項／沒過 {aFail.Count} 項　{aWhen}",
                           aFail.Concat(aPass).ToList(), aProblem.Concat(aNotes).ToList());
    }

    // emoji 換成文字；其他 U+FFFF 以上的字拿掉（視窗模式畫成方框 —— TASK-0356）
    static string Show(string s)
    {
        string a = PeerLinkPage.Plain(s);
        var sb = new StringBuilder(a.Length);
        for (int i = 0; i < a.Length; i++)
        {
            if (char.IsHighSurrogate(a[i]) && i + 1 < a.Length && char.IsLowSurrogate(a[i + 1])) { i++; continue; }
            sb.Append(a[i]);
        }
        return sb.ToString();
    }

    /// <summary>跑一個項目：開新資料夾 → 子行程 `selftest --only` → 讀回逐項檔 → 刪資料夾。不丟例外。</summary>
    internal static Outcome Run(string iRepoRoot, Pick iPick)
    {
        DateTime aStart = DateTime.Now;
        var aWatch = Stopwatch.StartNew();
        string aDir = Path.Combine(Path.GetTempPath(), "senate-peer-selftest-page-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        int aExit = -1;
        string aProblem = "";
        List<string>? aLines = null;
        try
        {
            Directory.CreateDirectory(aDir);
            (aExit, aProblem, string aErrTail) = RunChild(iRepoRoot, iPick.Key, aDir);
            string aReport = SelfTest.PeerReportPath(iPick.ReportFile, aDir);
            if (File.Exists(aReport)) aLines = File.ReadAllLines(aReport).ToList();
            else if (aProblem.Length == 0) aProblem = $"子行程結束碼 {aExit}，但沒有產生逐項檔（{iPick.ReportFile}）";
            if (aExit != 0 && aErrTail.Length > 0) aProblem += "\n--- stderr 最後幾行 ---\n" + aErrTail;
        }
        catch (Exception e) { aProblem = "跑的時候出錯了：" + e.GetType().Name + ": " + e.Message; }
        finally { try { if (Directory.Exists(aDir)) Directory.Delete(aDir, true); } catch { /* 刪不掉就留著，下次開機系統會清 */ } }
        return Summarize(iPick.Label, aLines, aExit, aProblem, aStart, aWatch.Elapsed);
    }

    // 同 SculptureViewerPage.RunCli：用 Environment.ProcessPath（版本天生一致）；`dotnet senate.dll` 啟動時補上 dll。
    static (int Exit, string Problem, string ErrTail) RunChild(string iRepoRoot, string iKey, string iReportDir)
    {
        string? aExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(aExe)) return (-1, "拿不到自己的執行檔路徑（Environment.ProcessPath 是空的）", "");
        var aPsi = new ProcessStartInfo(aExe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = iRepoRoot,
        };
        if (Path.GetFileNameWithoutExtension(aExe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            aPsi.ArgumentList.Add(typeof(PeerLinkSelfTest).Assembly.Location);
        aPsi.ArgumentList.Add("selftest");
        aPsi.ArgumentList.Add("--only");
        aPsi.ArgumentList.Add(iKey);
        aPsi.Environment[SelfTest.PeerReportDirEnv] = iReportDir;

        var aErr = new List<string>();
        using var aProc = new Process { StartInfo = aPsi };
        aProc.OutputDataReceived += (_, _) => { };   // 表格不用；讀掉免得管線塞住
        aProc.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (aErr) aErr.Add(e.Data); };
        try { aProc.Start(); }
        catch (Exception e) { return (-1, "子行程起不來：" + e.GetType().Name + ": " + e.Message, ""); }
        bool aTimedOut = false;
        using (SCP_ProcessRegistry.RegisterScope(aProc, ProcTag, "跨主機連線頁：selftest --only " + iKey, nameof(PeerLinkPage)))
        {
            aProc.BeginOutputReadLine();
            aProc.BeginErrorReadLine();
            if (!aProc.WaitForExit(TimeoutMs))
            {
                aTimedOut = true;
                try { aProc.Kill(entireProcessTree: true); } catch { /* 已經結束 */ }
                aProc.WaitForExit(5000);
            }
            else aProc.WaitForExit();   // 等非同步讀完最後幾行
        }
        string aTail;
        lock (aErr) aTail = string.Join("\n", aErr.Skip(Math.Max(0, aErr.Count - StderrTail))).TrimEnd();
        if (aTimedOut) return (-2, $"逾時（超過 {TimeoutMs / 60000} 分鐘），已連子行程一起砍掉", aTail);
        return (aProc.ExitCode, "", aTail);
    }
}
