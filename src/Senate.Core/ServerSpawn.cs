// 區塊職責：**怎麼生出那顆 Server 行程** —— 宿主側（Senate）的那一半。
// 物理意義：TASK-0267 切線（Tim 2026-09-22 拍板把 AutoStart 搬進 SCP_Core）。
//           決策迴圈（等待／四態）住共用層 `SCP.Core.Proc.SCP_ServerAutoStart`；
//           本檔留下的是**在另一個宿主底下沒有受詞**的那幾格：
//           `Environment.ProcessPath`（.NET 6+）、`OperatingSystem.IsWindows()`（.NET 5+）、
//           WMI 脫樹、`dotnet <dll>` 那條回退路、以及 Senate 自己的 runtime 目錄。
//           ⛔ netstandard2.1 沒有前兩個 API，而 Unity 那側的 `ProcessPath` 是 `Unity.exe`。
// 數值影響：最多起一個子行程；寫一份啟動 log（由 child 自己寫）。不碰 queue、不碰 ledger。
//
// 📌 這一刀怎麼下的：**「判斷與等待」共用，「怎麼生出行程」不共用。**
//    判準是「這個概念在兩個宿主底下是不是同一件事」，⛔ 不是「誰需要它」。
#nullable enable
using System.Diagnostics;
using System.Text;               // TASK-0204 脫樹那段要組命令列字串
using SCP.Core.Proc;

namespace Senate.Core;

/// <summary>Server 行程的生成（宿主側）—— 決策在 <see cref="SCP_ServerAutoStart"/>。</summary>
public static class ServerSpawn
{
    /// <summary>
    /// 起一個 detached 的 `server start`。
    /// <para>⚠ 用**這顆 CLI 自己**的執行方式去起（exe 就起 exe、`dotnet x.dll` 就起 dotnet）——
    /// 起錯一顆的症狀是 build id 不符，而那個錯訊息會把人帶去查一個不存在的版本問題。</para>
    /// </summary>
    public static bool TrySpawn(string iRepoRoot, string iServerId, out int oChildPid, out string oErr)
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

        // ⭐ TASK-0209 A7：出貨時 `publish/server/senate-server.exe` 就在旁邊 ⇒ **優先起那顆**。
        //   理由：常駐一整天的那顆不該載著 Silk.NET／cimgui／glfw3（CLI 那顆為了後台頁拖著它們）。
        //   ⚠ 而「起錯一顆」的風險沒有被這一行消掉，它只是**變成看得見的**：
        //     兩顆的 build id 由同一次 build.sh 蓋成同一個值；不一致時 `BuildMatches` 會擋下
        //     並印 build_mismatch（A6 那道閘），⛔ 不是靜默用一顆舊的。
        //   ⛔ 找不到就退回用自己起（開發樹、或還沒跑過 build.sh 的環境）—— 那是降級**路徑**，
        //     不是降級**行為**：兩個入口共用同一份前置與本體（A1）。
        string aSibling = Path.Combine(Path.GetDirectoryName(aExe) ?? "", "server", "senate-server.exe");
        bool aUseSibling = File.Exists(aSibling);
        if (aUseSibling) aExe = aSibling;

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
        // ⚠ 走 sibling 那條時 aExe 已經是真的 exe ⇒ 不可能是 dotnet；這一格只服務「用自己起」那條。
        bool aViaDotnet = !aUseSibling
                          && Path.GetFileNameWithoutExtension(aExe)
                              .Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        if (aViaDotnet)
        {
            string? aDll = System.Reflection.Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrEmpty(aDll)) { oErr = "走 `dotnet <dll>` 但找不到入口 dll 的路徑"; return false; }
            aInfo.ArgumentList.Add(aDll);
        }
        aInfo.ArgumentList.Add("server");
        aInfo.ArgumentList.Add("start");
        // 🔴 TASK-0244：子行程要知道自己是哪一顆。
        //   ⛔ 不傳的話它會起成 `main` —— 而 `main` 可能已經在跑，
        //   於是這一顆拿不到單例鎖自退，而呼叫端等的那顆永遠不會上線。
        //   失效樣子：`autostart_timeout`，而 log 裡寫的是「拿不到單例鎖」—— 兩句話指不到彼此。
        if (!string.Equals(iServerId, SCP_ServerIds.Default, StringComparison.Ordinal))
        {
            aInfo.ArgumentList.Add("--id");
            aInfo.ArgumentList.Add(iServerId);
        }

        // ⭐ TASK-0204：**先試著脫離呼叫端的行程樹**再退回直接 spawn。
        //   `Process.Start` 生出來的是直系子孫 ⇒ 它會跟著呼叫端繼承一整包東西
        //   （handle、環境、而在 Windows 上還有**封裝 app 的套件身分**）。
        //   🩸 血證：Claude Code 桌面版是 MSIX 封裝，agent 的每一條指令都是它的子孫
        //   （實測親代鏈 `Claude.exe(WindowsApps) → claude-code → bash → 這裡`）。
        //   於是這顆常駐 Server 變成「舊版套件還沒結束的行程」⇒ 新版註冊撞 `0x80073D02`
        //   ⇒ 使用者看到的是 **Claude Code 被關掉而且起不來**，而 Server **沒有視窗**，
        //   所以沒有人會想到去關它。（2026-09-16 Tim 第 3+ 次撞到；事件日誌逐格對得上。）
        //   ⇒ 脫樹之後這一整族都不成立，而且**脫樹本身量得到**（回讀 ParentProcessId）——
        //     ⛔ 不像「套件身分」只能推。
        if (TrySpawnDetached(aInfo, out oChildPid, out string aDetachWhy)) return true;

        try
        {
            Process? aProc = Process.Start(aInfo);
            if (aProc == null) { oErr = $"Process.Start 回 null（{aExe}）"; return false; }
            oChildPid = aProc.Id;
            // ⚠ 降級要**出聲**：靜默退回直接 spawn 的話，「脫樹成功」與「脫樹失敗」
            //   在畫面上完全一樣，而它們的後果差一個「Claude 起不起得來」。
            Console.Error.WriteLine("⚠ Server 沒能脫離本行程樹 ⇒ 退回直接 spawn（pid="
                                    + oChildPid + "）。原因：" + aDetachWhy);
            Console.Error.WriteLine("  ⇒ 這顆 Server 是本 process 的子孫。若宿主是封裝 app"
                                    + "（例：Claude Code 桌面版），它會擋住該 app 的更新註冊。"
                                    + "收工前請 `senate server stop`。");
            return true;
        }
        catch (Exception e) { oErr = $"{e.GetType().Name}: {e.Message}"; return false; }
    }

    /// <summary>
    /// 把 `server start` 生在**呼叫端的行程樹之外**（Windows：走 WMI `Win32_Process.Create`，
    /// 新行程的親代是 `WmiPrvSE`，不是我們）。
    /// <para>⚠ 判準是**回讀親代**，不是「指令回了 0」：WMI 回 ReturnValue=0 只代表它受理了。
    /// 拿到 pid 之後再去問一次 `ParentProcessId`，對不上就當沒成功（讓呼叫端退回直接 spawn），
    /// ⛔ 不要讓一個「看起來成功」的脫樹把真正的失敗蓋掉。</para>
    /// <para>⛔ 非 Windows 一律回 false（不是失敗，是這條路不適用）—— 那邊沒有套件身分這回事。</para>
    /// </summary>
    static bool TrySpawnDetached(ProcessStartInfo iInfo, out int oPid, out string oWhy)
    {
        oPid = 0; oWhy = "";
        if (!OperatingSystem.IsWindows()) { oWhy = "非 Windows，這條路不適用"; return false; }

        // 組回一條完整命令列（WMI 只吃字串）。路徑一律加引號 —— 我們的 repo 路徑可以有空白。
        var aCmd = new StringBuilder();
        aCmd.Append('"').Append(iInfo.FileName).Append('"');
        foreach (string aArg in iInfo.ArgumentList) aCmd.Append(" \"").Append(aArg).Append('"');

        // PowerShell 那一層只是**傳令兵**：真正生出 Server 的是 WMI provider，
        // 所以 powershell 自己是不是我們的子孫並不影響結果。
        string aPs =
            "$r = Invoke-CimMethod -ClassName Win32_Process -MethodName Create -Arguments @{"
            + "CommandLine='" + aCmd.ToString().Replace("'", "''") + "';"
            + "CurrentDirectory='" + (iInfo.WorkingDirectory ?? "").Replace("'", "''") + "'};"
            + "if ($r.ReturnValue -ne 0) { 'ERR ' + $r.ReturnValue } else { 'PID ' + $r.ProcessId }";

        try
        {
            var aPsi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = iInfo.WorkingDirectory ?? "",
            };
            aPsi.ArgumentList.Add("-NoProfile");
            aPsi.ArgumentList.Add("-NonInteractive");
            aPsi.ArgumentList.Add("-Command");
            aPsi.ArgumentList.Add(aPs);

            using Process? aP = Process.Start(aPsi);
            if (aP == null) { oWhy = "powershell 起不來（Process.Start 回 null）"; return false; }
            string aOut = aP.StandardOutput.ReadToEnd().Trim();
            if (!aP.WaitForExit(DetachTimeoutMs)) { try { aP.Kill(true); } catch { } oWhy = "WMI 那步逾時"; return false; }
            if (!aOut.StartsWith("PID ", StringComparison.Ordinal)) { oWhy = "WMI 沒有生出行程：" + (aOut.Length > 0 ? aOut : "（零輸出）"); return false; }
            if (!int.TryParse(aOut.Substring(4).Trim(), out int aPid) || aPid <= 0) { oWhy = "WMI 回的 pid 讀不出來：" + aOut; return false; }

            // ⭐ 回讀：新行程的親代**不可以是我們**。對不上就當沒成功。
            int aParent = ParentPidOf(aPid);
            if (aParent == Environment.ProcessId)
            { oWhy = $"生出來了（pid={aPid}）但親代仍是本 process ⇒ 沒有真的脫樹"; return false; }

            oPid = aPid;
            return true;
        }
        catch (Exception e) { oWhy = e.GetType().Name + ": " + e.Message; return false; }
    }

    /// <summary>問一顆行程的親代 pid；問不到回 -1（⛔ 不回 0 —— 0 是 System Idle，那是一個真的 pid）。</summary>
    static int ParentPidOf(int iPid)
    {
        try
        {
            var aPsi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            };
            aPsi.ArgumentList.Add("-NoProfile");
            aPsi.ArgumentList.Add("-NonInteractive");
            aPsi.ArgumentList.Add("-Command");
            aPsi.ArgumentList.Add("(Get-CimInstance Win32_Process -Filter \"ProcessId=" + iPid + "\").ParentProcessId");
            using Process? aP = Process.Start(aPsi);
            if (aP == null) return -1;
            string aOut = aP.StandardOutput.ReadToEnd().Trim();
            if (!aP.WaitForExit(DetachTimeoutMs)) { try { aP.Kill(true); } catch { } return -1; }
            return int.TryParse(aOut, out int aPpid) ? aPpid : -1;
        }
        catch { return -1; }
    }

    /// <summary>脫樹那兩步各自的上限。⚠ 不是 0：WMI 第一次呼叫會載 provider，冷啟要一兩秒。</summary>
    const int DetachTimeoutMs = 10_000;

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
}
