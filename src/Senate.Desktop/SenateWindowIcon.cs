// 區塊職責：把視窗／工作列那顆 icon 設成 exe 自己那顆（`<ApplicationIcon>` 埋進去的 Win32 資源）。
// 物理意義：⭐ 檔案圖示與視窗圖示是**兩條路**，而兩條都會被人叫做「icon」——
//           `<ApplicationIcon>` 只管檔案總管裡那顆；開窗之後標題欄與工作列那顆走的是
//           HWND 的 icon（`WM_SETICON`／class icon）。前者對了不代表後者會跟著對。
// 🩸 為什麼需要這一支（2026-09-02 實測）：GLFW 在 Win32 是去撈**名為** `GLFW_ICON` 的資源
//           （`glfw3.dll` 內含該寬字串），而 .NET apphost 埋的那顆是**數字 ID 32512、沒有名字** ⇒
//           名字對不上，GLFW 退回系統預設。讀數：`GetClassLongPtr(hwnd, GCLP_HICON)` 回傳的 handle
//           與 `LoadIcon(NULL, IDI_APPLICATION)` **完全相同**，且標題欄像素就是那顆通用視窗圖示。
// ⚠ 這裡刻意**不解 ICO／不嵌第二份圖檔**：`senate.ico` 六格全是 PNG 壓縮，自己解要拖一個
//           PNG decoder 進來；而 exe 裡本來就有那顆資源 ⇒ 圖檔源仍然只有一個入口
//           （`Senate.Cli.csproj` 的 `<ApplicationIcon>`），這支只是把它接到 HWND 上。
using System.Runtime.InteropServices;
using Silk.NET.Windowing;

namespace Senate.Desktop;

/// <summary>
/// 視窗 icon 的安裝端。回傳的字串是**讀數**不是 ✓ ——
/// 呼叫端要印出來（跟字型／剪貼簿同一個規矩：「以為設好了」是這一族最常見的錯）。
/// </summary>
public static class SenateWindowIcon
{
    const uint IMAGE_ICON = 1;
    const uint LR_DEFAULTCOLOR = 0;
    const uint WM_SETICON = 0x0080;
    const int ICON_SMALL = 0;
    const int ICON_BIG = 1;
    const int SM_CXICON = 11;
    const int SM_CYICON = 12;
    const int SM_CXSMICON = 49;
    const int SM_CYSMICON = 50;

    /// <summary>
    /// .NET apphost 把 <c>&lt;ApplicationIcon&gt;</c> 存成的 <c>RT_GROUP_ICON</c> 資源 ID。
    /// <para>⚠ 這是**量出來的**，不是推的：`senate.exe` 的 `.rsrc` 目錄樹裡 `RT_GROUP_ICON`
    /// 只有一個項目、ID = 32512（`RT_ICON` 則有 1..6 六格，對應六個尺寸）。
    /// 數值與 <c>IDI_APPLICATION</c> 相同是巧合 —— 這裡用它是因為它是**本 exe 模組內**的資源 ID，
    /// 傳的 <c>hInstance</c> 不是 NULL，撈到的就不會是系統那顆。</para>
    /// </summary>
    const int APPHOST_ICON_ID = 32512;

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr LoadImageW(IntPtr iInstance, IntPtr iName, uint iType, int iCx, int iCy, uint iLoad);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageW(IntPtr iWnd, uint iMsg, IntPtr iWParam, IntPtr iLParam);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int iIndex);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GetModuleHandleW(string? iModuleName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr FindResourceW(IntPtr iModule, IntPtr iName, IntPtr iType);

    /// <summary>`RT_GROUP_ICON` —— `LoadImage(IMAGE_ICON)` 撈的是這個型別，不是 `RT_ICON`(3)。</summary>
    static readonly IntPtr RT_GROUP_ICON = new IntPtr(14);

    /// <summary>
    /// 把 exe 自己那顆 icon 掛到視窗上。回傳一行讀數（成功說掛了幾顆、哪個尺寸；失敗說**哪一格**失敗）。
    /// <para>⚠ 失敗一律回一句說得出原因的話 —— 安靜地不做事是這一族的隱身衣。</para>
    /// </summary>
    public static string Apply(IWindow? iWindow)
    {
        if (iWindow == null) return "視窗 icon：未設（window 是 null）";

        // 非 Windows 宿主：GLFW 在 X11／Wayland 走的是完全另一條路（`glfwSetWindowIcon` 的點陣，
        // 或桌面環境自己去對 .desktop 檔）。那兩條我**沒有讀數** ⇒ 不碰、也不宣稱。
        if (!OperatingSystem.IsWindows())
            return "視窗 icon：跳過（非 Windows 宿主 —— X11／Wayland 那條路本專案沒有讀數）";

        var aNative = iWindow.Native?.Win32;
        if (aNative == null)
            return "視窗 icon：未設（拿不到 Win32 native handle —— 這顆窗不是 Win32 窗？）";

        IntPtr aHwnd = aNative.Value.Hwnd;
        if (aHwnd == IntPtr.Zero) return "視窗 icon：未設（HWND ＝ 0）";

        // ⚠ hInstance **不要用 native 給的那顆**。
        // 🩸 第一版我寫「native 的 HInstance 優先，0 才退回 GetModuleHandle(null)」，實測失敗：
        //    `LoadImage` 回 0 且 Win32 error = **1813（RESOURCE_TYPE_NOT_FOUND）** ——
        //    缺的是**型別**不是 ID，也就是那顆 hInstance 指的模組裡根本沒有 icon 資源
        //    （GLFW 給的是它註冊視窗類別用的 instance，不是 exe 的資源模組）。
        //    同一時間直接對 `senate.exe` 檔案跑 `FindResource(RT_GROUP_ICON, 32512)` 是**撈得到**的，
        //    而找不到 ID 時的錯誤碼是 1814 ⇒ 1813 vs 1814 正好分得出「餵錯模組」與「ID 錯」。
        // ⇒ 資源住在主模組（apphost），所以就問主模組，不做「兩個候選試到通」的排列。
        IntPtr aModule = GetModuleHandleW(null);
        if (aModule == IntPtr.Zero) return "視窗 icon：未設（拿不到 exe 的 hInstance）";

        IntPtr aId = new IntPtr(APPHOST_ICON_ID);

        // 先問「這顆資源在不在」再去載 —— 分開之後，失敗訊息才講得出是哪一格壞：
        // 不在（1814）＝ csproj 沒把圖示編進來；在而載不起來 ＝ 載入本身的問題。
        if (FindResourceW(aModule, aId, RT_GROUP_ICON) == IntPtr.Zero)
            return $"視窗 icon：未設（主模組裡沒有 RT_GROUP_ICON #{APPHOST_ICON_ID}，"
                 + $"Win32 error {Marshal.GetLastWin32Error()}）—— 檢查 Senate.Cli.csproj 的 <ApplicationIcon>";
        IntPtr aBig = LoadImageW(aModule, aId, IMAGE_ICON,
                                 GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON), LR_DEFAULTCOLOR);
        IntPtr aSmall = LoadImageW(aModule, aId, IMAGE_ICON,
                                   GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON), LR_DEFAULTCOLOR);

        if (aBig == IntPtr.Zero && aSmall == IntPtr.Zero)
            return $"視窗 icon：未設（資源 #{APPHOST_ICON_ID} 在，但 LoadImage 兩個尺寸都失敗，"
                 + $"Win32 error {Marshal.GetLastWin32Error()}）";

        // ⚠ 大小兩顆各自送：工作列與 Alt-Tab 用 big、標題欄用 small。
        //    只送一顆的症狀是「有些地方對了有些沒對」，而那看起來像快取沒更新。
        if (aBig != IntPtr.Zero) SendMessageW(aHwnd, WM_SETICON, new IntPtr(ICON_BIG), aBig);
        if (aSmall != IntPtr.Zero) SendMessageW(aHwnd, WM_SETICON, new IntPtr(ICON_SMALL), aSmall);

        string aWhich = (aBig != IntPtr.Zero, aSmall != IntPtr.Zero) switch
        {
            (true, true) => "big＋small 兩顆",
            (true, false) => "只有 big（small 撈不到）",
            _ => "只有 small（big 撈不到）",
        };
        return $"視窗 icon：已掛 {aWhich}（資源 #{APPHOST_ICON_ID} @ hInstance 0x{aModule:X}，"
             + $"big {GetSystemMetrics(SM_CXICON)}px／small {GetSystemMetrics(SM_CXSMICON)}px）";
    }
}
