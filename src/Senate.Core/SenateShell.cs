// 區塊職責：把路徑丟給作業系統的檔案總管（宿主能力，共用層碰不到 —— 見 SCP_GuiHost）。
// 物理意義：「開啟原始碼所在位置」這個動作的效果**發生在另一個視窗**，
//           所以本檔的每一條路徑都回一行**人可讀的結果** —— 成功也要有話說。
//           沒有那行字的話，「開起來了」「路徑不存在」「這台機器沒有桌面」三種結果同形。
// 數值影響：唯讀（只是啟動一個外部程式）。不會建立、修改或刪除任何檔案。
using System.Diagnostics;
using System.Runtime.InteropServices;
using SCP.Core.Gui;   // SCP_ClipboardRead（讀剪貼簿的三格結果）

namespace Senate.Core;

public static class SenateShell
{
    /// <summary>
    /// 做一顆「在檔案總管裡顯示這個路徑」的委派，掛給 <c>SCP_GuiHost.RevealInFileManager</c>。
    /// <para>iRepoRoot 是**救援用**的：頁面帶的原始碼路徑是編譯那台機器烤進去的絕對路徑，
    /// 換一台機器（或搬過資料夾）就不存在了 ⇒ 用檔名在 repo 裡找回來。</para>
    /// </summary>
    public static Func<string, string> MakeRevealer(string iRepoRoot)
        => iPath => Reveal(iPath, iRepoRoot);

    /// <summary>在檔案總管裡顯示 iPath（可能是檔或資料夾）。回傳一行人可讀的結果。</summary>
    public static string Reveal(string iPath, string? iRepoRoot = null)
    {
        if (string.IsNullOrWhiteSpace(iPath)) return "⚠ 沒有路徑可以開";

        string aTarget = iPath;
        string aNote = "";

        if (!File.Exists(aTarget) && !Directory.Exists(aTarget))
        {
            // 兩種情況都會走到這裡，而它們的**訊息必須分得出來**：
            //   ① 傳進來的是純檔名（呼叫端只知道類別名）⇒ 本來就要用找的
            //   ② 傳進來的是絕對路徑但不存在（編譯那台機器的路徑）⇒ 這是救援
            bool aBareName = iPath.IndexOf('/') < 0 && iPath.IndexOf('\\') < 0;
            string? aFound = FindInRepo(Path.GetFileName(aTarget), iRepoRoot, out string aWhy);
            if (aFound == null)
                return aBareName
                    ? $"⚠ 在 repo 裡找不到 {aTarget} —— {aWhy}"
                    : $"⚠ 找不到 {aTarget}（編譯時的路徑在這台機器上不存在）—— {aWhy}";
            // ⚠ 救援要說出來：悄悄開到另一個同名檔，跟開對了長得一樣
            aNote = aBareName ? "（依類別名在 repo 裡找到的）" : "（編譯時的路徑不存在，改用 repo 裡的同名檔）";
            aTarget = aFound;
        }

        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // /select 會**選取**那個檔而不只是開資料夾 —— 差別在於使用者不必自己在一堆檔裡找
                if (File.Exists(aTarget)) Start("explorer.exe", $"/select,\"{aTarget}\"");
                else Start("explorer.exe", $"\"{aTarget}\"");
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Start("open", File.Exists(aTarget) ? $"-R \"{aTarget}\"" : $"\"{aTarget}\"");
            }
            else
            {
                // Linux 的檔案總管沒有共通的「選取某個檔」旗標 ⇒ 只開資料夾，並且說出這件事
                string aDir = File.Exists(aTarget) ? Path.GetDirectoryName(aTarget)! : aTarget;
                Start("xdg-open", $"\"{aDir}\"");
                return $"✓ 已開啟資料夾 {aDir}{aNote}（這個平台不支援「選取檔案」，只開到資料夾）";
            }
        }
        catch (Exception e)
        {
            // headless／遠端桌面／沒有檔案總管 —— 全部走這裡，而且要分辨得出是哪一種
            return $"⚠ 開不起來（{e.GetType().Name}: {e.Message}）—— 路徑是 {aTarget}";
        }

        return $"✓ 已在檔案總管顯示 {aTarget}{aNote}";
    }

    /// <summary>
    /// 在 repo 裡找同名檔。
    /// <para>⚠ 找到**多個**時回 null 而不是挑第一個 —— 挑錯的症狀是「開到另一個同名檔」，
    /// 而那跟開對了長得一模一樣。⚠ 跳過 bin／obj（那裡面的複本不是人要看的碼）。</para>
    /// </summary>
    static string? FindInRepo(string iFileName, string? iRepoRoot, out string oWhy)
    {
        oWhy = "";
        if (string.IsNullOrEmpty(iFileName)) { oWhy = "連檔名都取不出來"; return null; }
        if (string.IsNullOrEmpty(iRepoRoot) || !Directory.Exists(iRepoRoot))
        {
            oWhy = "而且沒有 repo 根可以拿來找";
            return null;
        }

        List<string> aHits;
        try
        {
            aHits = Directory.EnumerateFiles(iRepoRoot, iFileName, SearchOption.AllDirectories)
                .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                         && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .Take(5).ToList();
        }
        catch (Exception e) { oWhy = $"在 repo 裡找的時候出錯（{e.GetType().Name}）"; return null; }

        if (aHits.Count == 0) { oWhy = $"repo 裡也沒有叫 {iFileName} 的檔"; return null; }
        if (aHits.Count > 1)
        {
            oWhy = $"repo 裡有 {aHits.Count} 個同名檔，不猜是哪一個：{string.Join(" / ", aHits)}";
            return null;
        }
        return aHits[0];
    }

    /// <summary>
    /// 把一段字放進剪貼簿（<c>clip.exe</c> / <c>pbcopy</c> / <c>xclip</c>）。回傳一行人可讀的結果。
    /// <para>⚠ **失敗時訊息裡一定要帶著那段字** —— 這條路存在的理由就是「讓人知道那是什麼」，
    /// 複製不了的時候把它印在畫面上，價值一點都沒有少。</para>
    /// <para>⚠ 非 ASCII 在 <c>clip.exe</c> 上可能會走樣（它吃的是主控台編碼）。
    /// 目前唯一的呼叫端傳的是 C# 類別名（純 ASCII），所以沒有踩到 —— 但這是前提不是保證。</para>
    /// </summary>
    public static string Copy(string iText)
    {
        if (string.IsNullOrEmpty(iText)) return "⚠ 沒有東西可以複製";

        string aExe, aArgs;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) { aExe = "clip.exe"; aArgs = ""; }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { aExe = "pbcopy"; aArgs = ""; }
        else { aExe = "xclip"; aArgs = "-selection clipboard"; }

        try
        {
            var aPsi = new ProcessStartInfo(aExe, aArgs)
            {
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using Process aProc = Process.Start(aPsi)
                ?? throw new InvalidOperationException($"{aExe} 啟動不了");
            aProc.StandardInput.Write(iText);
            aProc.StandardInput.Close();

            // ⚠ 不能無限等：剪貼簿工具卡住的話，整個 UI 會跟著停在那一幀而且沒有人知道為什麼
            if (!aProc.WaitForExit(3000))
                return $"⚠ {aExe} 沒有在 3 秒內結束（沒有強制砍它）—— 類別名是 {iText}";
            if (aProc.ExitCode != 0)
                return $"⚠ {aExe} 回 exit {aProc.ExitCode} —— 類別名是 {iText}";
        }
        catch (Exception e)
        {
            return $"⚠ 複製不了（{e.GetType().Name}: {e.Message}）—— 類別名是 {iText}";
        }
        return $"✓ 已複製到剪貼簿：{iText}";
    }

    /// <summary>
    /// 從剪貼簿讀一段字（Windows 走 PowerShell <c>Get-Clipboard</c>、macOS <c>pbpaste</c>、
    /// Linux <c>xclip -o</c>）。
    /// <para>⚠ Windows 沒有 <c>clip.exe</c> 的反向工具 ⇒ 只能繞 PowerShell，
    /// 而那要付約半秒的啟動成本。**所以這條路只掛在按鈕上，不放進每幀會跑的路徑。**</para>
    /// <para>⚠ 編碼要顯式釘 UTF-8（前綴設 <c>OutputEncoding</c> ＋ 這邊 <c>StandardOutputEncoding</c>）——
    /// 不釘的話中文路徑會變成一串問號，而那看起來像「剪貼簿裡本來就是亂碼」。</para>
    /// </summary>
    public static SCP_ClipboardRead Paste()
    {
        var aOut = new SCP_ClipboardRead();

        string aExe, aArgs;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            aExe = "powershell";
            aArgs = "-NoProfile -NonInteractive -Command "
                    + "\"[Console]::OutputEncoding=[Text.Encoding]::UTF8; Get-Clipboard -Raw\"";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) { aExe = "pbpaste"; aArgs = ""; }
        else { aExe = "xclip"; aArgs = "-selection clipboard -o"; }

        try
        {
            var aPsi = new ProcessStartInfo(aExe, aArgs)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            using Process aProc = Process.Start(aPsi)
                ?? throw new InvalidOperationException($"{aExe} 啟動不了");
            string aText = aProc.StandardOutput.ReadToEnd();
            // 同 Copy 的理由：不能無限等，卡住的話整個 UI 停在那一幀而且沒人知道為什麼。
            if (!aProc.WaitForExit(5000))
            {
                aOut.Message = $"⚠ {aExe} 沒有在 5 秒內結束（沒有強制砍它）";
                return aOut;
            }
            if (aProc.ExitCode != 0)
            {
                aOut.Message = $"⚠ {aExe} 回 exit {aProc.ExitCode}";
                return aOut;
            }
            aOut.Ok = true;
            aOut.Text = aText.Trim('\r', '\n', ' ', '\t');
            aOut.Message = aOut.Text.Length == 0
                ? "・剪貼簿是空的（讀到了，裡面沒東西）"
                : $"✓ 讀到 {aOut.Text.Length} 個字元";
        }
        catch (Exception e)
        {
            aOut.Message = $"⚠ 讀不到剪貼簿（{e.GetType().Name}: {e.Message}）";
        }
        return aOut;
    }

    static void Start(string iExe, string iArgs)
    {
        // UseShellExecute=false：explorer/open/xdg-open 本身就是執行檔，不需要再繞一層 shell
        Process.Start(new ProcessStartInfo(iExe, iArgs) { UseShellExecute = false });
    }
}
