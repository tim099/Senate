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
        // 🩸 TASK-0356 順手：酒館頁的書名「刺客正傳Ⅱ」裡的 `Ⅱ`（U+2161）住這一段，原本畫成 `?`（缺字守衛點名）。
        0x2150, 0x218F,   // 數字形式（Ⅰ Ⅱ Ⅲ ½）
        0x2190, 0x21FF,   // 箭頭（→ ⇒）
        0x2200, 0x22FF,   // 數學運算子（≥ ≤ ≠ ∈）
        // 🩸 TASK-0342：`⏳ ⏸ ⏭` 住這一段，而它原本不在表上 ⇒ 全頁的「執行中」都畫成 `?`（截圖實測）。
        0x2300, 0x23FF,   // 雜項技術符號（⏳ ⏸ ⏭ ⌂）
        0x2460, 0x24FF,   // 圈號英數（① ② ③ —— 驗收格、步驟編號常用）
        0x2500, 0x257F,   // 製表符（─ ┌ └ ⇒ 文字模式的框線，GUI 偶爾也會出現）
        0x25A0, 0x25FF,   // 幾何圖形（■ ▶ ●）
        0x2600, 0x26FF,   // 雜項符號（⚠ ⛔ ⭐ 的鄰居）
        0x2700, 0x27BF,   // Dingbats（✓ ✗ ✅）
        // 🩸 TASK-0342 全頁缺字掃描：bank 頁的 `⤷`（U+2937，「由 Server 執行」那種指路箭頭）住這一段。
        0x2900, 0x297F,   // 補充箭頭 B（⤷ ⤴）
        0x2B00, 0x2BFF,   // 雜項符號與箭頭（⭐ 2B50）
        0x3000, 0x303F,   // CJK 標點（。「」）
        // 🩸 TASK-0342：中點 `・`（U+30FB）在片假名區而不在 CJK 標點區 ⇒ 原本畫成 `?`。
        0x3040, 0x30FF,   // 平假名／片假名（含 ・ U+30FB）
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
    public static FontSet Configure(ImGuiIOPtr iIo, string? iCjkFontPath, SCP_GuiStyle iStyle, SenateEmoji? iEmoji = null)
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

        string aEmojiNote = iEmoji == null ? AtlasNote(iIo) : AddEmoji(iIo, iEmoji, aSet, aBody, aTitle);

        aSet.Description = string.Join(" + ", aLoaded)
            + $"｜字級 本文 {aBody:0.#} / 標題 {aTitle:0.#}（scale {iStyle.Scale:0.##}）" + aEmojiNote;
        return aSet;
    }

    // ===========================================================
    // 區塊職責：**彩色 emoji 寫進 atlas**（TASK-0356）—— 每顆字型（本文／標題）各掛一份私用碼位的自訂字形格。
    // 物理意義：順序是硬的 —— ① 字型都加完 ② AddCustomRectFontGlyph 登記格子 ③ Build ④ 取 RGBA32 寫圖 ⑤ 設 Colored。
    //           ④ 必須在 Silk 上傳 atlas 之前：本函式在 onConfigureIO 裡跑，Silk 之後呼叫 GetTexDataAsRGBA32
    //           拿到的是**同一塊**已轉好的 RGBA 緩衝（ImGui 不會重轉）⇒ 我們寫進去的像素會被上傳。
    // ⚠ ⑤ Colored 位元**不經過 ImGui.NET 的 ImFontGlyph**：綁定把 bitfield `Colored:1 Visible:1 Codepoint:30`
    //   生成成三個 uint（48 bytes，原生是 40）⇒ 照綁定的欄位寫會寫到別的地方，而且不報錯（reflection 實測）。
    //   ⇒ 直接改原生指標的第 0 位元。沒設的話 ImGui 會拿文字色去乘 emoji，彩色變成文字色的濃淡。
    // ===========================================================
    /// <summary>沒有 emoji 時也印 atlas 尺寸 —— 那是有 emoji 時多付了多少的基準（Silk 之後拿的是這一份，不會重建）。</summary>
    static string AtlasNote(ImGuiIOPtr iIo)
    {
        iIo.Fonts.GetTexDataAsRGBA32(out IntPtr _, out int aW, out int aH);
        return $"｜atlas {aW}×{aH}";
    }

    static unsafe string AddEmoji(ImGuiIOPtr iIo, SenateEmoji iEmoji, FontSet iSet, float iBody, float iTitle)
    {
        var aFonts = new List<(ImFontPtr Font, float Px)> { (iSet.Body, iBody) };
        if (iSet.Title.NativePtr != iSet.Body.NativePtr) aFonts.Add((iSet.Title, iTitle));

        var aRects = new List<(int Id, SenateEmoji.Entry E, float Px, ImFontPtr Font)>();
        foreach (var (aFont, aPx) in aFonts)
            foreach (SenateEmoji.Entry e in iEmoji.Entries)
            {
                var (w, h) = iEmoji.CellSize(e, aPx);
                int aId = iIo.Fonts.AddCustomRectFontGlyph(aFont, e.Pua, w, h, w, System.Numerics.Vector2.Zero);
                aRects.Add((aId, e, aPx, aFont));
            }

        if (!iIo.Fonts.Build()) return "｜⚠ emoji：atlas 建不出來（格子太多？）—— 這次沒有彩色 emoji";
        iIo.Fonts.GetTexDataAsRGBA32(out IntPtr aPixels, out int aTexW, out int aTexH);
        uint* aDst = (uint*)aPixels;
        foreach (var (aId, e, aPx, aFont) in aRects)
        {
            ImFontAtlasCustomRectPtr r = iIo.Fonts.GetCustomRectByIndex(aId);
            // iBgra：Silk 把這張貼圖當 BGRA 上傳（見 SenateEmoji.Rasterize）
            iEmoji.Rasterize(e, aPx, aDst + r.Y * aTexW + r.X, aTexW, r.Width, r.Height, iBgra: true);
            ImFontGlyph* g = aFont.FindGlyphNoFallback(e.Pua).NativePtr;
            if (g != null) *(uint*)g |= 1u;   // Colored（見上）
        }
        return $"｜emoji {iEmoji.Entries.Count} 顆（彩色 {iEmoji.ColoredCount}）× {aFonts.Count} 字級，atlas {aTexW}×{aTexH}";
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

        // 合併符號字型：MergeMode 讓缺的 glyph 從後面幾顆補進**剛剛那一顆**的 atlas。
        // ⚠ 已經有的 glyph 不會被後面那顆覆蓋（ImGui 的 merge 只補缺）⇒ 順序＝優先序。
        // 🩸 TASK-0342：這裡原本合併完第一顆就 `break`（理由是「一顆補齊就夠」）——
        //   而「補齊」從來沒有被量過：Segoe UI Emoji **一次都沒被合併**。
        //   ⇒ 兩顆都合；還缺什麼由 GuiImGuiRenderer 的缺字守衛點名，⛔ 不再靠推論。
        // ⚠ 射程：本 build 的 ImWchar 是 16 位元（範圍表是 ushort[]）⇒ U+FFFF 以上的字（📁 💾 🟢）**經這條路**進不來，
        //   合幾顆字型都一樣。那些字改走 SenateEmoji（借放私用區＋自己畫彩色字形，見 AddEmoji，TASK-0356）。
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
