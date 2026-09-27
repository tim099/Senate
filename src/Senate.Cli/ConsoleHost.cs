// 區塊職責：判斷「這次是被雙擊打開的，還是在終端機裡跑的」，並在前者把 console 藏起來。
// 物理意義：🩸 Tim 實撞（2026-08-22）：雙擊 senate.exe 會「閃一下就關」——
//           因為它是 console app、沒帶參數時預設跑 doctor，印完就結束。
//           使用者的期待是「雙擊 ＝ 開介面」，而那個期待完全合理。
//           ⇒ 但**不能**因此把「沒參數 ＝ 開視窗」寫死：在終端機裡打 `senate.exe`
//             期待的是文字輸出（腳本與 CI 也是）。兩種情境要分辨得出來，不是二選一。
// 數值影響：判準用 `GetConsoleProcessList` —— 從 Explorer 雙擊時，這個 console 是本行程專屬
//           （附著的行程數 ＝ 1）；從 cmd／PowerShell／Git Bash 跑時，shell 也附在同一個 console
//           （≥ 2）。這是 Windows 上這件事唯一可靠的讀數，不是猜。
// ⚠ 只在 Windows 有意義；其他平台一律回 false（那邊沒有「雙擊 console app」這個情境）。
using System.Runtime.InteropServices;

namespace Senate.Cli;

public static class ConsoleHost
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetConsoleProcessList(uint[] oProcessList, uint iCount);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr iHwnd, int iCmdShow);

    const int SW_HIDE = 0;

    /// <summary>
    /// 這個 console 是不是只有我一個行程附著（＝從檔案總管雙擊開的）。
    /// <para>在 cmd / PowerShell / Git Bash 裡執行時，shell 也附在同一個 console ⇒ 回 false。</para>
    /// </summary>
    public static bool LaunchedFromExplorer()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            var aBuffer = new uint[8];
            uint aCount = GetConsoleProcessList(aBuffer, (uint)aBuffer.Length);
            // 0 ＝ 問不到（沒有 console／被重導）⇒ **不當成雙擊**：
            // 猜錯的方向要選「照舊印文字」，因為那個錯是看得見的；反過來會莫名開一個視窗。
            return aCount == 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ===========================================================
    // 區塊職責：雙擊開介面時**不帶 console** —— 用 CREATE_NO_WINDOW 把自己重生一份，本體立刻退出。
    // 物理意義：Tim 2026-09-26「Senate.exe 預設不開 Console」。
    // 🩸 為什麼 HideConsoleWindow 不夠（2026-09-26 實測）：預設終端機是 Windows Terminal 時，
    //   console 會被轉交給 WT ⇒ `GetConsoleWindow()` 拿到的是看不見的 PseudoConsoleWindow，
    //   藏它等於沒藏 —— `cmd /c start senate.exe` 之後 WT 仍開著一個標題 `senate.exe` 的視窗。
    //   ⇒ 唯一不靠「哪一個終端機在接手」的做法，是**一開始就不要有那個 console**。
    // 數值影響：多生一個行程（GUI 本體），本行程回 0 退出。子行程帶 <see cref="ChildEnv"/>，
    //   ⛔ 它不會再重生一次（它自己的隱藏 console 也只有它一個行程附著 ⇒ 不擋就是無限重生）。
    // ===========================================================
    public const string ChildEnv = "SENATE_NO_CONSOLE_CHILD";

    /// <summary>
    /// 重生一份沒有 console 的自己來跑 <paramref name="iArgs"/>。
    /// 回 true ＝ 子行程起來了、呼叫端應該直接退出；回 false ＝ 沒重生（原因在 <paramref name="oWhy"/>），照舊往下跑。
    /// </summary>
    public static bool TryRelaunchWithoutConsole(string[] iArgs, out string oWhy)
    {
        oWhy = "";
        if (!OperatingSystem.IsWindows()) { oWhy = "非 Windows"; return false; }
        if (Environment.GetEnvironmentVariable(ChildEnv) == "1") { oWhy = "本行程就是重生出來的那一份"; return false; }
        string? aExe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(aExe)) { oWhy = "拿不到自己的執行檔路徑"; return false; }
        try
        {
            var aPsi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = aExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (string a in iArgs) aPsi.ArgumentList.Add(a);
            aPsi.Environment[ChildEnv] = "1";
            using var aChild = System.Diagnostics.Process.Start(aPsi);
            if (aChild == null) { oWhy = "Process.Start 回 null"; return false; }
            return true;
        }
        catch (Exception e) { oWhy = e.GetType().Name + ": " + e.Message; return false; }
    }

    /// <summary>把 console 視窗藏起來（雙擊開 GUI 時用，免得黑窗卡在後面）。</summary>
    public static void HideConsoleWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            IntPtr aHwnd = GetConsoleWindow();
            if (aHwnd != IntPtr.Zero) ShowWindow(aHwnd, SW_HIDE);
        }
        catch (Exception) { /* 藏不起來不是錯 —— 視窗照樣會開 */ }
    }
}
