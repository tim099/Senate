// 區塊職責：字型載入 —— 中文字型 ＋ 符號字型合併。
// 物理意義：🩸 第一版只載中文字型（GetGlyphRangesChineseFull）就宣告「中文可以顯示」——
//           截圖一看，中文確實好了，但 `✓ ≥ ⇒ ⚠` 全變成 `?`。
//           那份 range 只涵蓋 CJK 與基本標點，**箭頭／數學符號／雜項符號不在裡面**，
//           而缺字不會報錯，只會安靜地畫成 `?`。
//           ⇒ 判準：字型不是「有沒有載」，是「**這一頁實際用到的每一個字元**有沒有 glyph」。
// 數值影響：合併第二顆字型（Segoe UI Symbol）補符號區。pinned handle 存成 static ——
//           font atlas 是在本函式回來之後才建的，range 陣列在那之前被 GC 移動就會拿到垃圾。
// 字級來源：SCP_GuiStyle（顯示參數的單一來源）—— 這裡不再自己決定 18f 是多少。
using System.Runtime.InteropServices;
using ImGuiNET;
using SCP.Core.Gui;

namespace Senate.Desktop;

public static class SenateFonts
{
    // ⚠ 一定要活到 atlas 建完（實務上就是整個 app 生命週期）——
    //   讓它被回收的症狀不是崩潰，是字型隨機缺字。
    static readonly List<GCHandle> s_Pinned = new();

    /// <summary>本後台實際會用到的字元區塊。加符號進 UI 文字前，先確認它落在這些區間裡。</summary>
    static readonly ushort[] s_Ranges =
    {
        0x0020, 0x00FF,   // 基本拉丁 ＋ Latin-1（含 · × ÷）
        0x2000, 0x206F,   // 一般標點（— … ‧）
        0x2190, 0x21FF,   // 箭頭（→ ⇒）
        0x2200, 0x22FF,   // 數學運算子（≥ ≤ ≠ ∈）
        0x2500, 0x257F,   // 製表符（─ ┌ └ ⇒ 文字模式的框線，GUI 偶爾也會出現）
        0x25A0, 0x25FF,   // 幾何圖形（■ ▶ ●）
        0x2600, 0x26FF,   // 雜項符號（⚠ ⛔ ⭐ 的鄰居）
        0x2700, 0x27BF,   // Dingbats（✓ ✗ ✅）
        0x2B00, 0x2BFF,   // 雜項符號與箭頭（⭐ 2B50）
        0x3000, 0x303F,   // CJK 標點（。「」）
        0x4E00, 0x9FFF,   // CJK 統一漢字
        0xFF00, 0xFFEF,   // 全角形式
        0,                // 結尾必須是 0（ImGui 靠它判斷長度）
    };

    static readonly string[] s_SymbolFonts =
    {
        @"C:\Windows\Fonts\seguisym.ttf",   // Segoe UI Symbol
        @"C:\Windows\Fonts\seguiemj.ttf",   // Segoe UI Emoji
    };

    /// <summary>
    /// 這一次載到的字型。<c>Body</c> 是預設字型（第一顆），<c>Title</c> 是標題用的大一號。
    /// <para>⚠ 兩顆是**各自一個字級**的獨立 atlas 條目 —— ImGui 的字級不能事後乘倍率放大而不模糊，
    /// 所以「標題比本文大」必須在載入時就決定。⇒ 改 scale 要重開視窗，這件事得說出來。</para>
    /// </summary>
    public sealed class FontSet
    {
        public ImFontPtr Body;
        public ImFontPtr Title;
        public string Description = "";
    }

    /// <summary>
    /// 依 <see cref="SCP_GuiStyle"/> 載入本文與標題兩個字級（含符號字型合併）。
    /// 回傳實際載到什麼 —— 沒載到要說，不要裝作正常。
    /// </summary>
    public static FontSet Configure(ImGuiIOPtr iIo, string? iCjkFontPath, SCP_GuiStyle iStyle)
    {
        var aLoaded = new List<string>();
        IntPtr aRanges = Pin(s_Ranges);
        var aSet = new FontSet();

        float aBody = iStyle.FontSize;
        float aTitle = iStyle.TitleFontSize;

        aSet.Body = AddOne(iIo, iCjkFontPath, aBody, aRanges, aLoaded);
        // 標題只在真的比本文大時才多載一顆（同字級載兩次是白吃 atlas 空間）
        aSet.Title = aTitle > aBody + 0.5f
            ? AddOne(iIo, iCjkFontPath, aTitle, aRanges, aLoaded)
            : aSet.Body;

        aSet.Description = string.Join(" + ", aLoaded)
            + $"｜字級 本文 {aBody:0.#} / 標題 {aTitle:0.#}（scale {iStyle.Scale:0.##}）";
        return aSet;
    }

    /// <summary>載一顆字型 ＋ 合併符號字型。回傳的是**本文那一顆**的 handle（符號是 merge 進去的）。</summary>
    static ImFontPtr AddOne(ImGuiIOPtr iIo, string? iCjkFontPath, float iSize, IntPtr iRanges, List<string> oLoaded)
    {
        ImFontPtr aFont;
        if (iCjkFontPath != null && File.Exists(iCjkFontPath))
        {
            aFont = iIo.Fonts.AddFontFromFileTTF(iCjkFontPath, iSize, null, iRanges);
            oLoaded.Add($"{Path.GetFileName(iCjkFontPath)}@{iSize:0.#}");
        }
        else
        {
            aFont = iIo.Fonts.AddFontDefault();
            oLoaded.Add("(內建 ASCII 字型 —— 中文會是方塊)");
        }

        // 合併符號字型：MergeMode 讓缺的 glyph 從第二顆補進**剛剛那一顆**的 atlas
        foreach (string aPath in s_SymbolFonts)
        {
            if (!File.Exists(aPath)) continue;
            unsafe
            {
                var aCfg = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig())
                {
                    MergeMode = true,
                };
                iIo.Fonts.AddFontFromFileTTF(aPath, iSize, aCfg, iRanges);
            }
            oLoaded.Add(Path.GetFileName(aPath) + "(merge)");
            break;   // 一顆補齊就夠，多合併只是白吃 atlas 空間
        }
        return aFont;
    }

    static IntPtr Pin(ushort[] iArray)
    {
        var h = GCHandle.Alloc(iArray, GCHandleType.Pinned);
        s_Pinned.Add(h);
        return h.AddrOfPinnedObject();
    }
}
