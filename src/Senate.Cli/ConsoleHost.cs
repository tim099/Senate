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
