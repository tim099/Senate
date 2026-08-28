// 區塊職責：剪貼簿的**讀與寫** —— Windows 走 Win32、其他平台委給既有的 process 路徑。
// 物理意義：這一層存在的理由是**速度**，不是功能。
//           `SenateShell.Copy`／`Paste` 走外部 process（clip.exe / PowerShell / pbpaste），
//           而那要付 300〜500ms 的啟動成本。掛在按鈕上可以接受，
//           但 **ImGui 的 Ctrl+V callback 不行** —— 那是使用者按下組合鍵的那一幀，
//           卡半秒的貼上會被讀成「這個視窗當掉了」。
//           ⇒ Windows 上直接呼叫 Win32 剪貼簿 API（微秒級）。
// 數值影響：Get 唯讀。Set 會**覆蓋使用者的剪貼簿** —— 那是不可逆的（沒有人保存舊內容），
//           所以只在使用者明確要求複製時呼叫。
// ⚠ 這裡的 P/Invoke **不需要 unsafe**（全部走 IntPtr ＋ Marshal），所以本專案不必開
//   AllowUnsafeBlocks —— 需要 unsafe 的東西留在最外層（Senate.Desktop）。
// ⚠ 非 Windows 刻意**不自己實作**：pbpaste／xclip 已經在 SenateShell 有一份，
//   在這裡再寫一份等於同一件事有兩個實作，而它們會漂。
using System.Diagnostics;
using System.Runtime.InteropServices;
using SCP.Core.Gui;

namespace Senate.Core;

/// <summary>剪貼簿讀寫（Windows 走 Win32，其他平台走 <see cref="SenateShell"/> 的 process 路徑）。</summary>
public static class SenateClipboard
{
    const uint CF_UNICODETEXT = 13;
    const uint GMEM_MOVEABLE = 0x0002;

    /// <summary>
    /// <c>OpenClipboard</c> 的重試次數。
    /// <para>物理意義：剪貼簿是**全機唯一**的資源，同一時間只有一個 process 開得起來。
    /// 別的程式（輸入法、剪貼簿管理員、Office）剛好開著的話第一次會失敗，而那是**常態不是錯誤**。
    /// ⚠ 不重試的話症狀是「Ctrl+V 有時候沒反應」，而那種間歇性失敗最難被回報清楚。</para>
    /// </summary>
    const int OpenRetries = 6;
    const int OpenRetryDelayMs = 15;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr iOwner);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr GetClipboardData(uint iFormat);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetClipboardData(uint iFormat, IntPtr iMem);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool IsClipboardFormatAvailable(uint iFormat);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalAlloc(uint iFlags, UIntPtr iBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalLock(IntPtr iMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalUnlock(IntPtr iMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalFree(IntPtr iMem);

    static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// 讀剪貼簿。
    /// <para>⚠ 三格分開回報（<see cref="SCP_ClipboardRead"/>）—— 「剪貼簿是空的」與
    /// 「我讀不到剪貼簿」不得同形：壓成一個空字串之後，一個壞掉的能力會看起來像
    /// 「使用者沒複製東西」，而那會讓人一直重按。</para>
    /// </summary>
    public static SCP_ClipboardRead Read()
    {
        if (!IsWindows) return SenateShell.Paste();

        var aOut = new SCP_ClipboardRead();
        try
        {
            // ⚠ 先問格式再開：剪貼簿裡是圖片／檔案清單時 CF_UNICODETEXT 不存在，
            //   那不是失敗，是「沒有文字可以貼」—— 兩者要分得出來。
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                aOut.Ok = true;
                aOut.Text = "";
                aOut.Message = "・剪貼簿裡沒有文字（可能是圖片或檔案）";
                return aOut;
            }
            if (!TryOpen())
            {
                aOut.Message = "⚠ 開不了剪貼簿（別的程式正佔用它）—— 再按一次通常就好";
                return aOut;
            }
            try
            {
                IntPtr aHandle = GetClipboardData(CF_UNICODETEXT);
                if (aHandle == IntPtr.Zero)
                {
                    aOut.Message = "⚠ 剪貼簿有文字格式但取不到內容（GetClipboardData 回 0）";
                    return aOut;
                }
                IntPtr aPtr = GlobalLock(aHandle);
                if (aPtr == IntPtr.Zero)
                {
                    aOut.Message = "⚠ 鎖不住剪貼簿的記憶體（GlobalLock 回 0）";
                    return aOut;
                }
                try
                {
                    aOut.Text = Marshal.PtrToStringUni(aPtr) ?? "";
                    aOut.Ok = true;
                    aOut.Message = aOut.Text.Length == 0
                        ? "・剪貼簿是空的（讀到了，裡面沒東西）"
                        : $"✓ 讀到 {aOut.Text.Length} 個字元";
                }
                finally
                {
                    GlobalUnlock(aHandle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception e)
        {
            aOut.Ok = false;
            aOut.Message = $"⚠ 讀不到剪貼簿（{e.GetType().Name}: {e.Message}）";
        }
        return aOut;
    }

    /// <summary>
    /// 寫剪貼簿。回傳一行人可讀的結果（**成功也要有話說** —— 效果不在這個畫面上）。
    /// <para>⚠ 這會覆蓋使用者原本的剪貼簿內容，而那是不可逆的。</para>
    /// </summary>
    public static string Write(string iText)
    {
        if (string.IsNullOrEmpty(iText)) return "⚠ 沒有東西可以複製";
        if (!IsWindows) return SenateShell.Copy(iText);

        IntPtr aMem = IntPtr.Zero;
        try
        {
            if (!TryOpen()) return "⚠ 開不了剪貼簿（別的程式正佔用它）—— 再按一次通常就好";
            try
            {
                if (!EmptyClipboard()) return "⚠ 清不掉舊的剪貼簿內容（EmptyClipboard 失敗）";

                // +1 是結尾的 NUL；×2 是 UTF-16 每字元兩個位元組。
                var aChars = (iText + "\0").ToCharArray();
                aMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)(aChars.Length * 2));
                if (aMem == IntPtr.Zero) return "⚠ 配不到記憶體（GlobalAlloc 回 0）";

                IntPtr aPtr = GlobalLock(aMem);
                if (aPtr == IntPtr.Zero) return "⚠ 鎖不住剛配的記憶體（GlobalLock 回 0）";
                try { Marshal.Copy(aChars, 0, aPtr, aChars.Length); }
                finally { GlobalUnlock(aMem); }

                if (SetClipboardData(CF_UNICODETEXT, aMem) == IntPtr.Zero)
                    return "⚠ 交不出去（SetClipboardData 回 0）—— 剪貼簿沒有被改";

                // ⭐ SetClipboardData **成功之後所有權轉移給系統** ⇒ 這塊記憶體不可以由我們釋放。
                //   釋放它的症狀不是報錯，是別的程式貼出一段垃圾（use-after-free 的另一端）。
                aMem = IntPtr.Zero;
                return $"✓ 已複製到剪貼簿（{iText.Length} 個字元）";
            }
            finally
            {
                CloseClipboard();
            }
        }
        catch (Exception e)
        {
            return $"⚠ 複製不了（{e.GetType().Name}: {e.Message}）—— 內容是 {iText}";
        }
        finally
        {
            // 只在「還沒交出去」時釋放（交出去的已被設成 Zero）。
            if (aMem != IntPtr.Zero) GlobalFree(aMem);
        }
    }

    /// <summary>只回文字（讀不到就空字串）—— 給 ImGui 的 callback 用，那裡沒有地方放訊息。</summary>
    public static string ReadTextOrEmpty()
    {
        SCP_ClipboardRead aRead = Read();
        return aRead.Ok ? aRead.Text : "";
    }

    static bool TryOpen()
    {
        for (int i = 0; i < OpenRetries; ++i)
        {
            if (OpenClipboard(IntPtr.Zero)) return true;
            Thread.Sleep(OpenRetryDelayMs);
        }
        return false;
    }
}
