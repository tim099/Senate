// 區塊職責：**圖檔 → GL 貼圖**的快取（TASK-0317：酒館頁與 persona 設定頁的頭像）。
// 物理意義：PNG 由 StbImageSharp 解碼（純 C#，沒有原生 DLL）→ 長邊縮到 MaxSide → 上傳成 RGBA 貼圖。
//           以「路徑＋檔案修改時間」當鍵：後台換了頭像，下一幀就重載，⛔ 不必重開視窗。
// 數值影響：一張 1024² 的頭像原圖 4MB 顯存 ⇒ 縮到 256² 只要 256KB；20 幾個 persona 同時在畫面上也只是幾 MB。
// ⚠ 讀不了的檔**也快取失敗結果**（同一個 mtime 不重試）—— 不然一張壞圖會讓每一幀都去解碼一次。
//   而失敗要**看得見**：renderer 會畫佔位框，滑鼠提示印原因（Error）。
#nullable enable
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Senate.Desktop;

public sealed class SenateTextureCache : IDisposable
{
    /// <summary>上傳前把長邊縮到這麼多像素。頭像在畫面上最大也就一百多 px，放原圖只是浪費顯存。</summary>
    public const int MaxSide = 256;

    public sealed class Entry
    {
        public uint Handle;
        public int Width;
        public int Height;
        public DateTime WriteTimeUtc;
        public string? Error;
    }

    readonly GL m_Gl;
    readonly Dictionary<string, Entry> m_Map = new(StringComparer.OrdinalIgnoreCase);

    public SenateTextureCache(GL iGl) { m_Gl = iGl; }

    /// <summary>「原解析度」那一路的長邊上限（整張預覽用；超過才縮，⛔ 不是頭像那條 256）。</summary>
    public const int FullMaxSide = 4096;

    /// <summary>拿一張貼圖；讀不了回的 Entry 帶 Error、Handle=0。路徑空 ⇒ null（＝本來就沒有圖）。</summary>
    /// <param name="iFull">true ＝ 原解析度（長邊 ≤ <see cref="FullMaxSide"/>）；false ＝ 頭像縮圖（≤ <see cref="MaxSide"/>）。兩者分開快取。</param>
    public Entry? Get(string iPath, bool iFull = false)
    {
        if (string.IsNullOrEmpty(iPath)) return null;
        string aKey = iFull ? "full|" + iPath : iPath;
        DateTime aMtime;
        try { aMtime = File.Exists(iPath) ? File.GetLastWriteTimeUtc(iPath) : DateTime.MinValue; }
        catch (Exception) { aMtime = DateTime.MinValue; }

        if (m_Map.TryGetValue(aKey, out Entry? aOld) && aOld.WriteTimeUtc == aMtime) return aOld;
        if (aOld != null && aOld.Handle != 0) m_Gl.DeleteTexture(aOld.Handle);

        var aNew = new Entry { WriteTimeUtc = aMtime };
        if (aMtime == DateTime.MinValue) aNew.Error = "檔案不存在：" + iPath;
        else
        {
            try { Upload(iPath, aNew, iFull ? FullMaxSide : MaxSide); }
            catch (Exception e) { aNew.Error = $"讀不了（{e.GetType().Name}: {e.Message}）"; }
        }
        m_Map[aKey] = aNew;
        return aNew;
    }

    unsafe void Upload(string iPath, Entry ioEntry, int iMaxSide)
    {
        ImageResult aImg;
        using (var aFs = File.OpenRead(iPath)) aImg = ImageResult.FromStream(aFs, ColorComponents.RedGreenBlueAlpha);

        (byte[] aPixels, int aW, int aH) = Downscale(aImg.Data, aImg.Width, aImg.Height, iMaxSide);

        uint aTex = m_Gl.GenTexture();
        m_Gl.BindTexture(TextureTarget.Texture2D, aTex);
        m_Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        m_Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        m_Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        m_Gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        m_Gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        fixed (byte* p = aPixels)
            m_Gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)aW, (uint)aH, 0,
                            PixelFormat.Rgba, PixelType.UnsignedByte, p);
        m_Gl.BindTexture(TextureTarget.Texture2D, 0);

        ioEntry.Handle = aTex;
        ioEntry.Width = aW;
        ioEntry.Height = aH;
    }

    /// <summary>面積平均縮圖（長邊 ≤ <paramref name="iMax"/>）。已經夠小就原樣回傳。</summary>
    static (byte[] data, int w, int h) Downscale(byte[] iSrc, int iW, int iH, int iMax)
    {
        int aLong = Math.Max(iW, iH);
        if (aLong <= iMax) return (iSrc, iW, iH);
        double aK = (double)aLong / iMax;
        int aW = Math.Max(1, (int)Math.Round(iW / aK));
        int aH = Math.Max(1, (int)Math.Round(iH / aK));
        var aDst = new byte[aW * aH * 4];
        for (int y = 0; y < aH; y++)
        {
            int aY0 = (int)(y * aK), aY1 = Math.Min(iH, Math.Max(aY0 + 1, (int)((y + 1) * aK)));
            for (int x = 0; x < aW; x++)
            {
                int aX0 = (int)(x * aK), aX1 = Math.Min(iW, Math.Max(aX0 + 1, (int)((x + 1) * aK)));
                long r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int sy = aY0; sy < aY1; sy++)
                {
                    int aRow = sy * iW * 4;
                    for (int sx = aX0; sx < aX1; sx++)
                    {
                        int i = aRow + sx * 4;
                        r += iSrc[i]; g += iSrc[i + 1]; b += iSrc[i + 2]; a += iSrc[i + 3]; n++;
                    }
                }
                int o = (y * aW + x) * 4;
                aDst[o] = (byte)(r / n); aDst[o + 1] = (byte)(g / n); aDst[o + 2] = (byte)(b / n); aDst[o + 3] = (byte)(a / n);
            }
        }
        return (aDst, aW, aH);
    }

    public void Dispose()
    {
        foreach (Entry e in m_Map.Values) if (e.Handle != 0) m_Gl.DeleteTexture(e.Handle);
        m_Map.Clear();
    }
}
