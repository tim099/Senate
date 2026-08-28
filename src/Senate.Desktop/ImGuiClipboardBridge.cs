// 區塊職責：把 ImGui 的剪貼簿 callback 接到真的剪貼簿上 —— 讓 **Ctrl+C / Ctrl+V 在每一個
//           `InputText` 都能用**。
// 物理意義：🩸 這條線原本**從來沒有被接上**（`SetClipboardTextFn` / `GetClipboardTextFn`
//           在整個 repo 零命中；Silk.NET 的 `ImGuiController` 不會自己設）。
//           症狀是視窗模式下每一個輸入框都貼不上，而它**不報錯** ——
//           使用者按 Ctrl+V 什麼都沒發生，看起來像「這個欄位只能手打」。
//           （Tim 2026-08-28 在 Submodule 頁的 repo 路徑欄回報，而那一格只是它最先被踩到的地方。）
// 數值影響：安裝之後 ImGui 的貼上會**同步**呼叫 Win32 剪貼簿（微秒級）；
//           複製會**覆蓋**使用者的剪貼簿（那是 Ctrl+C 本來的語意）。
// ⚠ 為什麼要有這一層薄橋而不直接在 SenateWindow 裡寫：這裡處理的是
//   **非受管記憶體與 delegate 存活期**，那兩件事寫錯的症狀都不是編譯錯誤
//   （一個是貼出垃圾、一個是隨機 crash）⇒ 收在一個檔裡，旁邊寫清楚判準。
using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Senate.Core;

namespace Senate.Desktop;

/// <summary>ImGui ↔ 系統剪貼簿的橋。<see cref="Install"/> 一次就好（重複呼叫是安全的）。</summary>
public static class ImGuiClipboardBridge
{
    // ⚠ ImGui.NET 1.90.x 的 callback 簽章（C 端是 `const char* (*)(void*)` 與 `void (*)(void*, const char*)`）。
    //   ⚠ 1.91 之後這兩格搬到 `ImGui.GetPlatformIO().Platform_*`，升版時要跟著改 ——
    //     接錯地方的症狀是**靜默無效**（callback 不會被呼叫，也不會有人抱怨），
    //     所以 Install 回傳一行診斷，讓「有沒有真的接上」有讀數。
    delegate IntPtr GetClipboardTextFn(IntPtr iUserData);
    delegate void SetClipboardTextFn(IntPtr iUserData, IntPtr iText);

    // ⭐ **必須存成 static 欄位**：`Marshal.GetFunctionPointerForDelegate` 產生的指標
    //   **不會**讓 delegate 本身活著。放區域變數的話 GC 隨時可以回收它，
    //   然後 ImGui 某次貼上會跳進一塊已經不是函式的記憶體 —— 症狀是隨機 crash，
    //   而且離「安裝」那一行已經很遠，查不回來。
    static GetClipboardTextFn? s_Get;
    static SetClipboardTextFn? s_Set;

    /// <summary>
    /// 上一次交給 ImGui 的 UTF-8 字串。
    /// <para>⚠ ImGui 的契約是「你回一個指標，我讀完就不管了」——**它不會替我們釋放**。
    /// 所以這塊記憶體由我們保管，並在**下一次**讀取時才釋放（ImGui 是同步讀完才返回，
    /// 所以上一塊在那個時候一定已經沒人在看）。</para>
    /// <para>⚠ 不能每次讀完立刻釋放：那會在 ImGui 還在讀的時候把地板抽掉。</para>
    /// </summary>
    static IntPtr s_Buffer = IntPtr.Zero;

    /// <summary>已經接上了嗎（重複 Install 不會重複配置 delegate）。</summary>
    public static bool Installed { get; private set; }

    /// <summary>
    /// 兩個 callback 各被呼叫過幾次。
    /// <para>⭐ 這是**區分「ImGui 沒呼叫我」與「呼叫了但貼不進去」的唯一讀數**。
    /// 🩸 2026-08-28：兩層自我對拍都過、指標也讀回非零，而 Tim 實測 Ctrl+V 仍然沒反應 ——
    /// 「按鈕的貼上 OK」把範圍切到了「ImGui 收不到那個組合鍵」這一段。
    /// 少了這個計數器，那個判斷只能靠猜。</para>
    /// </summary>
    public static int GetCalls { get; private set; }

    public static int SetCalls { get; private set; }

    /// <summary>
    /// 把 callback 掛上 ImGui。回傳一行**診斷**（給截圖／CLI 留讀數用）。
    /// <para>⚠ 一定要有讀數：接錯版本或沒接上的症狀是「Ctrl+V 安靜地沒反應」，
    /// 而那跟「這個宿主本來就不支援」長得一模一樣。</para>
    /// </summary>
    public static string Install(ImGuiIOPtr iIo)
    {
        try
        {
            s_Get = OnGetClipboardText;
            s_Set = OnSetClipboardText;
            iIo.GetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(s_Get);
            iIo.SetClipboardTextFn = Marshal.GetFunctionPointerForDelegate(s_Set);

            // 讀回來才算數（寫入端會替自己說謊）—— 兩個指標都非零才算接上。
            bool aOk = iIo.GetClipboardTextFn != IntPtr.Zero && iIo.SetClipboardTextFn != IntPtr.Zero;
            Installed = aOk;
            return aOk
                ? "剪貼簿：已接上 ImGui（Ctrl+C / Ctrl+V 可用）"
                : "⚠ 剪貼簿：設了但讀回來是 0 —— 這個 ImGui 版本可能把 callback 搬到 PlatformIO 了";
        }
        catch (Exception e)
        {
            Installed = false;
            return $"⚠ 剪貼簿：接不上（{e.GetType().Name}: {e.Message}）—— Ctrl+V 不會有反應";
        }
    }

    /// <summary>
    /// 走**兩個 callback 本身**做一次 round-trip —— 驗的是 ImGui 那一側的介面，不是剪貼簿。
    /// <para>⭐ 為什麼需要它：`Install` 只能證明「指標設上去了」，
    /// 而真正容易寫錯的是 **marshalling**：UTF-8 編碼、結尾的 NUL、以及那塊記憶體在
    /// ImGui 讀它的時候還活著。那三格寫錯的症狀分別是「貼出亂碼」「貼出一串垃圾」「隨機 crash」，
    /// **沒有一個是編譯錯誤**，而且都要等使用者真的按 Ctrl+V 才會發生。</para>
    /// <para>⚠ 這條路**會覆蓋剪貼簿**（它就是在測寫入），所以只給 opt-in 的自我對拍用。</para>
    /// </summary>
    public static (bool Ok, string Reading) SelfCheck(string iProbe)
    {
        // ① 透過 Set callback 寫進去（模擬 ImGui 的 Ctrl+C）
        IntPtr aIn = Marshal.StringToCoTaskMemUTF8(iProbe);
        try { OnSetClipboardText(IntPtr.Zero, aIn); }
        finally { Marshal.FreeCoTaskMem(aIn); }

        // ② 透過 Get callback 讀回來（模擬 ImGui 的 Ctrl+V）
        IntPtr aOut = OnGetClipboardText(IntPtr.Zero);
        if (aOut == IntPtr.Zero) return (false, "Get callback 回 IntPtr.Zero（＝ImGui 會當成剪貼簿是空的）");

        string aBack = Marshal.PtrToStringUTF8(aOut) ?? "";
        if (aBack != iProbe)
            return (false, $"經過兩個 callback 之後字**變了**（寫 {iProbe.Length} 字元、讀 {aBack.Length} 字元）");

        // ③ NUL 結尾 —— C 端靠它判斷長度，少一個位元組就會讀到別人的記憶體。
        int aBytes = Encoding.UTF8.GetByteCount(iProbe);
        byte aTerminator = Marshal.ReadByte(aOut, aBytes);
        if (aTerminator != 0)
            return (false, $"UTF-8 結尾少了 NUL（第 {aBytes} 個位元組是 {aTerminator}）—— C 端會讀過頭");

        return (true, $"兩個 callback 逐字對拍相同（{iProbe.Length} 字元 / {aBytes} 位元組，UTF-8 結尾有 NUL）");
    }

    /// <summary>ImGui 要貼上時呼叫（回一塊活著的 UTF-8 記憶體）。</summary>
    static IntPtr OnGetClipboardText(IntPtr iUserData)
    {
        GetCalls++;
        try
        {
            string aText = SenateClipboard.ReadTextOrEmpty();

            // 先釋放上一塊（見 s_Buffer 的判準），再配新的。
            if (s_Buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(s_Buffer);
                s_Buffer = IntPtr.Zero;
            }

            byte[] aBytes = Encoding.UTF8.GetBytes(aText);
            // +1 給結尾的 NUL —— C 端靠它判斷長度，少一個位元組就會讀到別人的記憶體。
            s_Buffer = Marshal.AllocHGlobal(aBytes.Length + 1);
            Marshal.Copy(aBytes, 0, s_Buffer, aBytes.Length);
            Marshal.WriteByte(s_Buffer, aBytes.Length, 0);
            return s_Buffer;
        }
        catch
        {
            // ⚠ **絕對不能讓例外飛回 C 端**（native → managed 邊界上那是 undefined behavior，
            //   而 ImGui 沒有地方接它）⇒ 最壞的情況回 Zero，ImGui 會當成「剪貼簿是空的」。
            return IntPtr.Zero;
        }
    }

    /// <summary>ImGui 要複製時呼叫。</summary>
    static void OnSetClipboardText(IntPtr iUserData, IntPtr iText)
    {
        SetCalls++;
        try
        {
            string aText = Marshal.PtrToStringUTF8(iText) ?? "";
            if (aText.Length > 0) SenateClipboard.Write(aText);
        }
        catch
        {
            // 同上：不讓例外穿過 native 邊界。複製失敗頂多是「Ctrl+C 沒作用」，
            // 而讓例外飛出去會整個 process 死。
        }
    }
}
