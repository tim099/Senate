// 區塊職責：行程級的**崩潰報告** —— 任何沒被接住的例外，在行程結束前落一份檔。
// 物理意義：Tim 2026-09-27：senate.exe 兩次彈出「應用程式發生例外狀況 (0xe0434352)」，
//           而 Windows 事件記錄／`_cmd_errors`／Editor.log 三條路**都沒有它**（summit 同日量）——
//           `_cmd_errors` 只收 Cmd 回報的失敗，Cmd 外面（UI、啟動、背景執行緒）炸掉就什麼都不留，
//           對話框按掉之後連是哪一顆行程都查不到。⇒ 這一層補的是「Cmd 外面」。
// 數值影響：寫 `SenateData/runtime/_crash/<時間>-<宿主>-<pid>.md`（例外全文、命令列、build、cwd、有沒有 Console）。
//           ⛔ **不吞例外、不改崩潰行為**：記完就讓它照原樣結束 —— 吞掉會把一個大聲的壞掉變成安靜的壞掉。
//           寫檔失敗一律吞（崩潰處理器自己再丟例外，只會蓋掉原本那一個）。
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Senate.Core;

public static class CrashLog
{
    public const string DirName = "_crash";

    static int s_Installed;
    static string s_Dir = "";
    static string s_Host = "";

    /// <summary>崩潰報告的目錄（<c>SenateData/runtime/_crash/</c>）。</summary>
    public static string Dir(string iRepoRoot) => Path.Combine(SenatePaths.RuntimeDir(iRepoRoot), DirName);

    /// <summary>
    /// 裝上行程級的例外記錄。重複呼叫只有第一次生效。
    /// <para>⚠ 要在**最早**的時間點裝（見兩個 exe 的 <c>CrashLogInit</c>：ModuleInitializer，比 Main 還早）——
    /// 晚裝的話，在它之前炸掉的那一段就回到「什麼都不留」。</para>
    /// </summary>
    /// <param name="iHost">宿主定語（<c>senate</c>／<c>senate-server</c>）—— 報告檔名與內文都帶，分得出是哪一顆。</param>
    public static void Install(string iRepoRoot, string iHost)
    {
        if (Interlocked.Exchange(ref s_Installed, 1) == 1) return;
        s_Dir = Dir(iRepoRoot);
        s_Host = iHost;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write(e.ExceptionObject as Exception, "AppDomain.UnhandledException", e.IsTerminating);
        // 沒被 await 的 Task 例外：預設**不會**讓行程結束，但它是「做到一半安靜停掉」的來源 ⇒ 記下來、不改行為。
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Write(e.Exception, "TaskScheduler.UnobservedTaskException", false);
    }

    /// <summary>
    /// 寫一份報告並回傳路徑；兩處都寫不出來回 null（⛔ 不丟例外）。
    /// <para>🩸 主目錄寫不進去時退到**本行程使用者的 TEMP**：2026-09-27 那一族崩潰的成因就是
    /// 沙箱身分（<c>CodexSandboxOffline</c>）碰不到共用目錄 —— 只寫主目錄的話，最需要報告的那一次正好寫不出來。</para>
    /// </summary>
    /// <param name="iHeadline">標題括號裡那句；不給就依 <paramref name="iTerminating"/> 選「行程結束／背景例外」。</param>
    public static string? Write(Exception? iEx, string iSource, bool iTerminating, string? iHeadline = null)
    {
        DateTime aNow = DateTime.Now;
        int aPid = Environment.ProcessId;
        string aName = $"{aNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}-{s_Host}-{aPid}.md";
        string? aPrimaryError = null;
        foreach (string aDir in new[] { s_Dir, Path.Combine(Path.GetTempPath(), "senate" + DirName) })
        {
            try
            {
                Directory.CreateDirectory(aDir);
                string aPath = Path.Combine(aDir, aName);
                File.WriteAllText(aPath, Render(iEx, iSource, iTerminating, aNow, aPid, aPrimaryError, iHeadline), new UTF8Encoding(false));
                try { Console.Error.WriteLine($"💥 崩潰報告{(aPrimaryError != null ? "（⚠ 主目錄寫不進去，落在 TEMP）" : "")}：{aPath}"); }
                catch (Exception) { }   // 沒有 Console 時這行本身也可能丟
                return aPath;
            }
            catch (Exception e) { aPrimaryError ??= $"{aDir}：{e.GetType().Name}: {e.Message}"; }
        }
        return null;
    }

    static string Render(Exception? iEx, string iSource, bool iTerminating, DateTime iNow, int iPid, string? iPrimaryError,
                         string? iHeadline)
    {
        var aSb = new StringBuilder();
        aSb.AppendLine($"# 💥 {s_Host} 崩潰（{iHeadline ?? (iTerminating ? "行程結束" : "未結束 —— 背景例外")}）");
        aSb.AppendLine();
        if (iPrimaryError != null)
            aSb.AppendLine($"> ⚠ 本報告落在 TEMP 退路 —— 主目錄寫不進去（{iPrimaryError}）。那一行本身可能就是線索。\n");
        aSb.AppendLine($"- **使用者**: {Environment.UserName}");
        aSb.AppendLine($"- **時間**: {iNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} (local)");
        aSb.AppendLine($"- **來源**: `{iSource}`");
        aSb.AppendLine($"- **pid**: {iPid}");
        aSb.AppendLine($"- **build**: {SafeBuildId()}");
        aSb.AppendLine($"- **exe**: `{Environment.ProcessPath}`");
        aSb.AppendLine($"- **命令列**: `{string.Join(" ", Environment.GetCommandLineArgs())}`");
        aSb.AppendLine($"- **cwd**: `{Environment.CurrentDirectory}`");
        // 有沒有 Console 是一條要被看見的軸：無 Console 重開的那一顆碰 Console API 會丟例外，而它的崩潰長得跟別的一樣。
        aSb.AppendLine($"- **有 Console**: {HasConsole()}");
        aSb.AppendLine();
        aSb.AppendLine("## 例外");
        aSb.AppendLine("```");
        aSb.AppendLine(iEx?.ToString() ?? "(ExceptionObject 不是 System.Exception —— 非 CLR 例外)");
        aSb.AppendLine("```");
        return aSb.ToString();
    }

    static string SafeBuildId()
    {
        try { return ServerHost.BuildId; } catch (Exception e) { return $"（讀不到：{e.GetType().Name}）"; }
    }

    static string HasConsole()
    {
        try { return Console.IsOutputRedirected ? "stdout 被導向" : (Console.WindowWidth > 0 ? "是" : "否"); }
        catch (Exception e) { return $"否（{e.GetType().Name}）"; }
    }
}
