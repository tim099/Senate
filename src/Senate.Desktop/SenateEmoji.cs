// 區塊職責：**彩色 emoji**（TASK-0356）—— 把 U+FFFF 以上的 emoji 借放到 BMP 私用區，畫成彩色字形塞進 ImGui 的 atlas。
// 物理意義：本 build 的 ImWchar 是 16 位元（ImGui.NET 1.90.8.1 綁定寫死 ushort），U+FFFF 以上的字**結構上進不了 atlas**，
//           ImGui 會安靜地畫成 `?`。改 32 位元＝重編 cimgui＋fork 綁定 ⇒ 不走。
//           ⇒ 繞法：每個 emoji 配一個私用碼位（U+E000 起），自己畫圖寫進 atlas；畫字前把字串裡的 emoji 換成那個碼位。
//           顏色來自 `seguiemj.ttf` 的 COLR v0 分層（v1 字型也附 v0 記錄）＋ CPAL 第 0 組調色盤：
//           每層是一個單色外框，用 stb_truetype 畫出覆蓋率、乘上該層顏色、由下往上疊。
// 數值影響：啟動時一次預載（不做中途擴充）。字串轉換只在 GuiImGuiRenderer 畫字前做 —— 頁面碼與資料都不變。
//   ⚠ 不支援：ZWJ 組合（拆成單顆）、膚色修飾（拿掉）、輸入框打 emoji。
#nullable enable
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using StbTrueTypeSharp;

namespace Senate.Desktop;

public sealed class SenateEmoji
{
    /// <summary>私用區起點。ImGui 用 U+E000–F8FF 這段沒有人會打的區間，跟任何字型的真字不撞。</summary>
    public const int PuaFirst = 0xE000;
    public const int PuaLast = 0xF8FF;

    /// <summary>一個要畫的 emoji：原碼位、借放的私用碼位、字型裡的 glyph、以及它的彩色分層（沒有分層 ⇒ 單層、用前景色）。</summary>
    public sealed class Entry
    {
        public int Codepoint;
        public char Pua;
        public int GlyphId;
        public (int Glyph, uint Rgba)[] Layers = Array.Empty<(int, uint)>();
    }

    readonly byte[] m_Font;
    GCHandle m_Pin;
    readonly StbTrueType.stbtt_fontinfo m_Info = new();
    readonly Dictionary<int, Entry> m_ByCodepoint = new();

    public string FontPath { get; }
    public IReadOnlyCollection<Entry> Entries => m_ByCodepoint.Values;
    public int ColoredCount { get; private set; }

    SenateEmoji(string iPath, byte[] iFont) { FontPath = iPath; m_Font = iFont; }

    /// <summary>
    /// 讀字型、建對照表。失敗回 null 並在 <paramref name="oWhy"/> 說為什麼 —— ⛔ 不丟例外：
    /// emoji 是加分項，載不到時視窗照常開，缺字守衛照常點名（TASK-0356 驗收 ③）。
    /// </summary>
    public static unsafe SenateEmoji? Load(string iPath, out string oWhy)
    {
        oWhy = "";
        if (!File.Exists(iPath)) { oWhy = "找不到 emoji 字型 " + iPath; return null; }
        byte[] aBytes;
        try { aBytes = File.ReadAllBytes(iPath); }
        catch (Exception e) { oWhy = "讀不了 " + iPath + "：" + e.Message; return null; }

        var aSelf = new SenateEmoji(iPath, aBytes);
        // ⚠ stb 只記指標不複製資料 ⇒ 字型 bytes 必須一直釘住（被 GC 移動的症狀是畫出垃圾，不是崩潰）
        aSelf.m_Pin = GCHandle.Alloc(aBytes, GCHandleType.Pinned);
        if (StbTrueType.stbtt_InitFont(aSelf.m_Info, (byte*)aSelf.m_Pin.AddrOfPinnedObject(), 0) == 0)
        { aSelf.m_Pin.Free(); oWhy = "stb 讀不了這顆字型 " + iPath; return null; }

        if (!aSelf.TryBuildTable(out oWhy)) { aSelf.m_Pin.Free(); return null; }
        return aSelf;
    }

    // ── 字型表 ────────────────────────────────────────────────

    bool TryBuildTable(out string oWhy)
    {
        oWhy = "";
        var aTables = ReadTableDirectory();
        if (!aTables.TryGetValue("cmap", out var aCmap)) { oWhy = "字型沒有 cmap 表"; return false; }
        Dictionary<int, int> aCps = ReadCmap12(aCmap.Off);
        if (aCps.Count == 0) { oWhy = "cmap 沒有第 12 型子表（U+FFFF 以上的字對不到 glyph）"; return false; }

        Dictionary<int, (int, uint)[]> aColr = new();
        if (aTables.TryGetValue("COLR", out var aC) && aTables.TryGetValue("CPAL", out var aP))
            aColr = ReadColrV0(aC.Off, ReadCpal0(aP.Off));

        int aNext = PuaFirst;
        foreach (var kv in aCps.OrderBy(k => k.Key))
        {
            if (!IsEmojiCodepoint(kv.Key)) continue;
            if (aNext > PuaLast) { oWhy = "私用區用完（" + (PuaLast - PuaFirst + 1) + " 格）—— 後面的 emoji 沒有收"; break; }
            var aEntry = new Entry { Codepoint = kv.Key, Pua = (char)aNext++, GlyphId = kv.Value };
            if (aColr.TryGetValue(kv.Value, out var aLayers)) { aEntry.Layers = aLayers; ColoredCount++; }
            else aEntry.Layers = new[] { (kv.Value, 0xFFFFFFFFu) };   // 沒有分層 ⇒ 單色外框（白色，畫的時候不染色）
            m_ByCodepoint[kv.Key] = aEntry;
        }
        return true;
    }

    /// <summary>只收 U+FFFF 以上、而且是 emoji／符號區的字（數學字母那種 SMP 字不是 emoji，收了只是佔 atlas）。</summary>
    static bool IsEmojiCodepoint(int iCp)
        => iCp >= 0x1F000 && iCp <= 0x1FAFF && !IsSkinTone(iCp);

    static bool IsSkinTone(int iCp) => iCp >= 0x1F3FB && iCp <= 0x1F3FF;

    ushort U16(int iOff) => BinaryPrimitives.ReadUInt16BigEndian(m_Font.AsSpan(iOff, 2));
    uint U32(int iOff) => BinaryPrimitives.ReadUInt32BigEndian(m_Font.AsSpan(iOff, 4));

    Dictionary<string, (int Off, int Len)> ReadTableDirectory()
    {
        var aOut = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        int n = U16(4);
        for (int i = 0; i < n; i++)
        {
            int r = 12 + 16 * i;
            string aTag = Encoding.ASCII.GetString(m_Font, r, 4);
            aOut[aTag] = ((int)U32(r + 8), (int)U32(r + 12));
        }
        return aOut;
    }

    /// <summary>cmap 第 12 型（平台 3／編碼 10）：分段的 32 位元碼位 → glyph。</summary>
    Dictionary<int, int> ReadCmap12(int iCmap)
    {
        var aOut = new Dictionary<int, int>();
        int n = U16(iCmap + 2);
        for (int i = 0; i < n; i++)
        {
            int r = iCmap + 4 + 8 * i;
            if (U16(r) != 3 || U16(r + 2) != 10) continue;
            int aSub = iCmap + (int)U32(r + 4);
            if (U16(aSub) != 12) continue;
            uint aGroups = U32(aSub + 12);
            for (uint gi = 0; gi < aGroups; gi++)
            {
                int g = aSub + 16 + (int)gi * 12;
                uint aStart = U32(g), aEnd = U32(g + 4), aGlyph = U32(g + 8);
                for (uint cp = aStart; cp <= aEnd; cp++) aOut[(int)cp] = (int)(aGlyph + (cp - aStart));
            }
            break;
        }
        return aOut;
    }

    /// <summary>CPAL 第 0 組調色盤，回 RGBA（R 在最低位元組，同 ImGui 的 IM_COL32）。</summary>
    uint[] ReadCpal0(int iCpal)
    {
        int aEntries = U16(iCpal + 2);
        int aRecords = (int)U32(iCpal + 8);
        int aFirst = U16(iCpal + 12);
        var aOut = new uint[aEntries];
        for (int i = 0; i < aEntries; i++)
        {
            int c = iCpal + aRecords + (aFirst + i) * 4;   // 記錄是 B G R A
            aOut[i] = (uint)(m_Font[c + 2] | (m_Font[c + 1] << 8) | (m_Font[c] << 16) | (m_Font[c + 3] << 24));
        }
        return aOut;
    }

    /// <summary>COLR v0：base glyph → 由下往上的分層（glyph, 顏色）。調色盤索引 0xFFFF ＝ 前景色（這裡用白）。</summary>
    Dictionary<int, (int, uint)[]> ReadColrV0(int iColr, uint[] iPalette)
    {
        var aOut = new Dictionary<int, (int, uint)[]>();
        int aBaseN = U16(iColr + 2);
        int aBase = iColr + (int)U32(iColr + 4);
        int aLayer = iColr + (int)U32(iColr + 8);
        for (int i = 0; i < aBaseN; i++)
        {
            int r = aBase + 6 * i;
            int aGlyph = U16(r), aFirst = U16(r + 2), aCount = U16(r + 4);
            var aLayers = new (int, uint)[aCount];
            for (int k = 0; k < aCount; k++)
            {
                int l = aLayer + 4 * (aFirst + k);
                int aPal = U16(l + 2);
                aLayers[k] = (U16(l), aPal == 0xFFFF || aPal >= iPalette.Length ? 0xFFFFFFFFu : iPalette[aPal]);
            }
            aOut[aGlyph] = aLayers;
        }
        return aOut;
    }

    // ── 畫圖 ──────────────────────────────────────────────────

    /// <summary>某個字級下一格的排法：縮放、格子寬高、以及把分層外框置中要加的位移。</summary>
    readonly struct Layout
    {
        public readonly float Scale; public readonly int W, H, Ox, Oy;
        public Layout(float s, int w, int h, int ox, int oy) { Scale = s; W = w; H = h; Ox = ox; Oy = oy; }
    }

    /// <summary>
    /// 🩸 第一版用 `ScaleForPixelHeight`、基線放在字型 ascent ⇒ 🎟 🏦 🚀 💾 頂端被切（probe 截圖實測）：
    ///   這顆字型的 ascent／descent 跟 emoji 實際畫的範圍不一致。
    /// ⇒ 改成：em 尺寸換算 → 取所有分層的聯集外框 → 比字級高就再縮 → 置中放進「寬＝advance、高＝字級」的格子。
    ///   不靠字型的垂直度量，所以換一顆 emoji 字型也不會切。
    /// </summary>
    unsafe Layout LayoutFor(Entry iEntry, float iPx)
    {
        float aScale = StbTrueType.stbtt_ScaleForMappingEmToPixels(m_Info, iPx);
        var (x0, y0, x1, y1) = Bounds(iEntry, aScale);
        if (y1 - y0 > iPx && y1 > y0)
        {
            aScale *= iPx / (y1 - y0);
            (x0, y0, x1, y1) = Bounds(iEntry, aScale);
        }
        int aAdv, aLsb;
        StbTrueType.stbtt_GetGlyphHMetrics(m_Info, iEntry.GlyphId, &aAdv, &aLsb);
        int aW = Math.Max(1, Math.Max((int)MathF.Ceiling(aAdv * aScale), x1 - x0));
        int aH = Math.Max(1, (int)MathF.Ceiling(iPx));
        return new Layout(aScale, aW, aH, (aW - (x1 - x0)) / 2 - x0, (aH - (y1 - y0)) / 2 - y0);
    }

    unsafe (int, int, int, int) Bounds(Entry iEntry, float iScale)
    {
        int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = int.MinValue, by1 = int.MinValue;
        foreach (var (aGlyph, _) in iEntry.Layers)
        {
            int x0, y0, x1, y1;
            StbTrueType.stbtt_GetGlyphBitmapBox(m_Info, aGlyph, iScale, iScale, &x0, &y0, &x1, &y1);
            if (x1 <= x0 || y1 <= y0) continue;
            bx0 = Math.Min(bx0, x0); by0 = Math.Min(by0, y0); bx1 = Math.Max(bx1, x1); by1 = Math.Max(by1, y1);
        }
        return bx0 == int.MaxValue ? (0, 0, 0, 0) : (bx0, by0, bx1, by1);
    }

    /// <summary>某個字級下一格的寬與高（＝ atlas 要留多大的格子，寬也是 advance）。</summary>
    public (int W, int H) CellSize(Entry iEntry, float iPx)
    {
        Layout l = LayoutFor(iEntry, iPx);
        return (l.W, l.H);
    }

    /// <summary>
    /// 把一格畫進 RGBA32 緩衝（straight alpha，R 在最低位元組）。分層由下往上疊，聯集外框置中（見 <see cref="LayoutFor"/>）。
    /// </summary>
    /// <param name="iBgra">目的地是 BGRA 位元組順序（B 在最低位元組）。
    /// 🩸 Silk.NET 的 ImGui 後端把字型貼圖以 `RGBA8`／**`BGRA`** 上傳（IL 讀 `Texture..ctor` 實測）——
    ///   白色字形對調看不出來，彩色 emoji 會 R／B 互換（🔁 藍底畫成橘底，截圖實測）。</param>
    public unsafe void Rasterize(Entry iEntry, float iPx, uint* iDst, int iStride, int iW, int iH, bool iBgra = false)
    {
        Layout l = LayoutFor(iEntry, iPx);
        float aScale = l.Scale;

        foreach (var (aGlyph, aRgba) in iEntry.Layers)
        {
            int x0, y0, x1, y1;
            StbTrueType.stbtt_GetGlyphBitmapBox(m_Info, aGlyph, aScale, aScale, &x0, &y0, &x1, &y1);
            int gw = x1 - x0, gh = y1 - y0;
            if (gw <= 0 || gh <= 0) continue;
            byte[] aCov = new byte[gw * gh];
            fixed (byte* p = aCov) StbTrueType.stbtt_MakeGlyphBitmap(m_Info, p, gw, gh, gw, aScale, aScale, aGlyph);

            float sr = (aRgba & 0xFF) / 255f, sg = ((aRgba >> 8) & 0xFF) / 255f, sb = ((aRgba >> 16) & 0xFF) / 255f;
            float sa = ((aRgba >> 24) & 0xFF) / 255f;
            if (iBgra) (sr, sb) = (sb, sr);   // 目的地的讀寫用同一套順序 ⇒ 只要對調來源
            for (int y = 0; y < gh; y++)
            {
                int dy = l.Oy + y0 + y;
                if (dy < 0 || dy >= iH) continue;
                for (int x = 0; x < gw; x++)
                {
                    int dx = l.Ox + x0 + x;
                    if (dx < 0 || dx >= iW) continue;
                    float a = aCov[y * gw + x] / 255f * sa;
                    if (a <= 0f) continue;
                    uint* d = iDst + dy * iStride + dx;
                    float dr = (*d & 0xFF) / 255f, dg = ((*d >> 8) & 0xFF) / 255f, db = ((*d >> 16) & 0xFF) / 255f;
                    float da = ((*d >> 24) & 0xFF) / 255f;
                    float oa = a + da * (1f - a);
                    float or = (sr * a + dr * da * (1f - a)) / oa;
                    float og = (sg * a + dg * da * (1f - a)) / oa;
                    float ob = (sb * a + db * da * (1f - a)) / oa;
                    *d = (uint)(B(or) | (B(og) << 8) | (B(ob) << 16) | (B(oa) << 24));
                }
            }
        }
    }

    static int B(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);

    // ── 換字 ──────────────────────────────────────────────────

    /// <summary>
    /// 畫字前的轉換：U+FFFF 以上收得到的 emoji ⇒ 私用碼位；變體選擇符 U+FE0E／FE0F、ZWJ U+200D、膚色修飾 ⇒ 拿掉
    /// （它們本身沒有字形，留著只會多畫一個 `?`）。收不到的字原樣留著 —— 讓缺字守衛照常點名。
    /// <para>⚡ 沒有任何需要換的字時回傳原字串（不配置）—— 每幀每個節點都會走這裡。</para>
    /// </summary>
    public string Translate(string iText)
    {
        if (string.IsNullOrEmpty(iText) || !NeedsTranslate(iText)) return iText;
        var aSb = new StringBuilder(iText.Length);
        for (int i = 0; i < iText.Length; i++)
        {
            char c = iText[i];
            if (c == '︎' || c == '️' || c == '‍') continue;
            if (char.IsHighSurrogate(c) && i + 1 < iText.Length && char.IsLowSurrogate(iText[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, iText[i + 1]);
                i++;
                if (IsSkinTone(cp)) continue;
                if (m_ByCodepoint.TryGetValue(cp, out var e)) aSb.Append(e.Pua);
                else aSb.Append(c).Append(iText[i]);
                continue;
            }
            aSb.Append(c);
        }
        return aSb.ToString();
    }

    static bool NeedsTranslate(string iText)
    {
        foreach (char c in iText)
            if (char.IsSurrogate(c) || c == '︎' || c == '️' || c == '‍') return true;
        return false;
    }

    /// <summary>這個碼位有沒有被收進來（給缺字守衛用）。</summary>
    public bool Covers(int iCodepoint) => m_ByCodepoint.ContainsKey(iCodepoint);
}
