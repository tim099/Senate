// 區塊職責：**Server 沒在跑就把它拉起來**（TASK-0209 A4／A5，Tim 2026-09-14 拍板）。
// 物理意義：D20 ⑦ 原本是「手動啟動，沒跑就 exit 3 印指令，不做降級路」。
//           銀行 ledger 搬進 Server 之後，那條規矩的代價會落在每一次扣款上
//           （早安自介、commit 領薪、發文計酬、跨日保管費全部要人先開一個終端機）
//           ⇒ Tim 翻掉 ⑦ 的「手動」那半。**「不降級」那半沒有翻** —— 本檔只負責把 Server 拉起來，
//           拉不起來仍然 exit 3，⛔ 絕不改成本地跑（本地跑＝第二個寫入者）。
// 數值影響：最多起一個子行程；寫一份啟動 log。不碰 queue、不碰 ledger。
//
// 🩸 設計判準（每一條都是「錯的時候長什麼樣」決定的）：
//   ① **「確保有一顆」不是「起我的那顆」**：N 顆 CLI 同時發現沒在跑 ⇒ N 顆一起 spawn，
//      而單例鎖（A2）只讓一顆活。其餘那幾顆的 CLI **不該報錯** —— 它們要等的是「有沒有一顆好了」，
//      不是「我 spawn 的那顆好了沒」。⇒ 等待條件一律是 `Probe().IsRunning`，不看 pid。
//      ⛔ 沒有 A2 的話這裡就是**自動製造**四個寫入者（2026-09-14 實測：拿掉鎖，4 顆全登記成功）。
//   ② **「正在啟動」與「啟動失敗」必須不同形**（A4 條文）：兩者都是「現在還沒有 Server」，
//      而處置相反（再等 ／ 去看 log）。⇒ 分成兩個 delegate_failure 值與兩段不同的話。
//   ③ **啟動期的 log 一定要落檔**（A5）：detached 子行程沒有 stdout 可以給人看，
//      而「它為什麼沒起來」只活在那幾行裡。⛔ 不落檔的話那一格是零讀數。
//   ④ **不重試、不退避**：拉一次拉不起來就交給人。自動重試會把「環境壞了」變成一個
//      每次都慢 N 秒、而且永遠不報錯的黑洞。
using System.Diagnostics;
using System.Globalization;

namespace Senate.Core;

/// <summary>拉起 Server 的結果 —— 三態，⛔ 不是 bool（「還沒好」與「起不來」處置相反）。</summary>
public enum ServerAutoStartOutcome
{
    /// <summary>本來就在跑（沒做任何事）。</summary>
    AlreadyRunning,

    /// <summary>拉起來了，而且已經可以收工作。</summary>
    Started,

    /// <summary>spawn 出去了，但等到逾時仍然沒有 Server 上線 —— **不知道它是慢還是死了**。</summary>
    TimedOut,

    /// <summary>連 spawn 都失敗（找不到執行檔、權限…）——**確定沒起來**。</summary>
    SpawnFailed,
}

public sealed class ServerAutoStartReport
{
    public ServerAutoStartOutcome Outcome;

    /// <summary>人讀的原因（Failed／TimedOut 時一定有話說；空字串是 bug）。</summary>
    public string Detail = "";

    /// <summary>啟動 log 的路徑（spawn 成功就一定有）—— 失敗時 CLI 要指得出這個檔。</summary>
    public string? LogPath;

    public bool Ok => Outcome == ServerAutoStartOutcome.AlreadyRunning || Outcome == ServerAutoStartOutcome.Started;
}

public static class ServerAutoStart
{
    /// <summary>等 Server 上線的上限。啟動要載 assembly ＋ Discover() ⇒ 給寬一點，但不能無限（無限＝掛住）。</summary>
    public const int ReadyTimeoutMs = 20000;

    const int PollMs = 200;

    /// <summary>
    /// 確保有一顆 Server 在跑；沒有就拉一顆起來，等到它能收工作為止。
    /// <para>⚠ 等的是「**有沒有一顆**」不是「我 spawn 的那顆」—— 見檔頭判準①。</para>
    /// </summary>
    public static ServerAutoStartReport Ensure(string iRepoRoot, Action<string>? iLog = null)
    {
        var aReport = new ServerAutoStartReport();
        if (ServerHost.Probe(iRepoRoot).IsRunning)
        {
            aReport.Outcome = ServerAutoStartOutcome.AlreadyRunning;
            return aReport;
        }

        // 🩸 兩格實測（2026-09-14）決定了 log 的形狀，兩格都是「最需要它的時候它不在」：
        //   ① 共用一份 `_server_start.log` ⇒ 4 顆同時 spawn 時 `WriteAllText` 把前三份**截斷覆蓋**。
        //   ② 改成一顆一份之後仍然空的：輸出原本是**接到 parent 的管線**，
        //      而 parent（CLI）跑完就退 ⇒ **非同步讀取器跟著死**，
        //      那三顆「拿不到單例鎖」的訊息一個字都沒留下。
        //   ⇒ 結論：**log 由 child 自己寫**（`ServerHost.RunForeground` 自帶 tee），parent 不接管線。
        //     parent 只負責記下 child 的 pid，好指得出**那一份**。
        if (!TrySpawn(iRepoRoot, out int aChildPid, out string aSpawnErr))
        {
            aReport.Outcome = ServerAutoStartOutcome.SpawnFailed;
            aReport.Detail = aSpawnErr;
            return aReport;
        }
        aReport.LogPath = ServerHost.StartLogPath(iRepoRoot, aChildPid);
        iLog?.Invoke($"⤷ Server 沒在跑 ⇒ 已拉起一顆，等它上線（最多 {ReadyTimeoutMs / 1000}s）…");

        var aSw = Stopwatch.StartNew();
        while (aSw.ElapsedMilliseconds < ReadyTimeoutMs)
        {
            Thread.Sleep(PollMs);
            // ⚠ 條件是 IsRunning（registry 判定），不是「我那顆 pid 活著」：
            //   輸掉單例鎖的那幾顆會自己退出，而真正上線的是別人 spawn 的那顆 —— 那也算數。
            if (ServerHost.Probe(iRepoRoot).IsRunning)
            {
                aReport.Outcome = ServerAutoStartOutcome.Started;
                aReport.Detail = $"{aSw.ElapsedMilliseconds} ms";
                return aReport;
            }
        }

        aReport.Outcome = ServerAutoStartOutcome.TimedOut;
        aReport.Detail = $"等了 {ReadyTimeoutMs / 1000}s 仍然沒有 Server 上線";
        return aReport;
    }

    /// <summary>
    /// 起一個 detached 的 `server start`。
    /// <para>⚠ 用**這顆 CLI 自己**的執行方式去起（exe 就起 exe、`dotnet x.dll` 就起 dotnet）——
    /// 起錯一顆的症狀是 build id 不符，而那個錯訊息會把人帶去查一個不存在的版本問題。</para>
    /// </summary>
    static bool TrySpawn(string iRepoRoot, out int oChildPid, out string oErr)
    {
        oErr = ""; oChildPid = 0;
        try
        {
            Directory.CreateDirectory(SenatePaths.RuntimeDir(iRepoRoot));
            PruneOldLogs(SenatePaths.RuntimeDir(iRepoRoot));
        }
        catch (Exception e) { oErr = $"runtime 目錄備不起來（{e.GetType().Name}: {e.Message}）"; return false; }

        string? aExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(aExe)) { oErr = "拿不到自己的執行檔路徑（Environment.ProcessPath 是空的）"; return false; }

        var aInfo = new ProcessStartInfo
        {
            FileName = aExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            // ⛔ 刻意**不** redirect：接到這裡的管線會在本 process 退出時一起死
            //   （實測②）。child 自己寫檔才留得住。
            WorkingDirectory = iRepoRoot,
        };

        // `dotnet senate.dll …` 這條路：ProcessPath 是 dotnet 本身 ⇒ 第一個參數要把 dll 帶回去。
        bool aViaDotnet = Path.GetFileNameWithoutExtension(aExe)
                              .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (aViaDotnet)
        {
            string? aDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(aDll)) { oErr = "走 `dotnet <dll>` 但找不到入口 dll 的路徑"; return false; }
            aInfo.ArgumentList.Add(aDll);
        }
        aInfo.ArgumentList.Add("server");
        aInfo.ArgumentList.Add("start");

        try
        {
            Process? aProc = Process.Start(aInfo);
            if (aProc == null) { oErr = $"Process.Start 回 null（{aExe}）"; return false; }
            oChildPid = aProc.Id;
            return true;
        }
        catch (Exception e) { oErr = $"{e.GetType().Name}: {e.Message}"; return false; }
    }

    /// <summary>
    /// 只留最新幾份啟動 log。⚠ 不清的話它會**無聲長大**（每次 Server 沒開就多一份）；
    /// 留太少又會讓「上一次為什麼沒起來」消失 —— 而那正是要查的那一份。
    /// </summary>
    const int KeepLogs = 5;

    static void PruneOldLogs(string iRuntimeDir)
    {
        try
        {
            var aFiles = new List<FileInfo>();
            foreach (string aPath in Directory.GetFiles(iRuntimeDir, "_server_start_*.log"))
                aFiles.Add(new FileInfo(aPath));
            if (aFiles.Count <= KeepLogs) return;
            aFiles.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = KeepLogs; i < aFiles.Count; i++)
                try { aFiles[i].Delete(); } catch (Exception) { /* 刪不掉不該擋住啟動 */ }
        }
        catch (Exception) { /* 清不了就算了 —— 只是佔空間，不影響正確性 */ }
    }

    /// <summary>把三態翻成 CLI 要說的話 —— **「還沒好」與「起不來」的出口不同**（檔頭判準②）。</summary>
    public static void Explain(ServerAutoStartReport iReport, List<string> iLines)
    {
        switch (iReport.Outcome)
        {
            case ServerAutoStartOutcome.TimedOut:
                iLines.Add($"✗ 自動啟動：{iReport.Detail} —— 這一筆**沒有送出**。");
                iLines.Add("  ⚠ 這是「**不知道**」不是「起不來」：它可能還在載入，也可能已經死了。");
                iLines.Add($"  下一步：先看啟動 log → {iReport.LogPath}");
                iLines.Add("        再看現況 → `senate server status`（它上線了就直接重跑這一行）");
                break;
            case ServerAutoStartOutcome.SpawnFailed:
                iLines.Add($"✗ 自動啟動**連子行程都沒起來**：{iReport.Detail} —— 這一筆**沒有送出**。");
                iLines.Add("  ⚠ 這是「**確定沒起來**」：不必等，環境有問題。");
                iLines.Add("  下一步：手動跑一次 `senate server start`，看它印什麼。");
                break;
        }
    }
}
