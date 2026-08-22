// 區塊職責：把 OpenGL framebuffer 存成 PNG。
// 物理意義：⭐ 這支存在的唯一理由是**讓 GUI 有讀數**。原生視窗沒辦法被 CI 或 agent 用眼睛看，
//           於是「有沒有畫出來」「中文是不是方塊」「表格有沒有錯位」全都只能靠人回報。
//           落成圖檔之後，那些變成可以被任何人（包括不在現場的人）檢查的證據。
// 數值影響：純輸出。自己寫 PNG 編碼器是為了**不引入影像套件**
//           （PNG = zlib 壓縮的掃描線；zlib 就是 deflate 加上兩行 header 與 adler32 檢查碼，
//           .NET 內建 DeflateStream 就夠）。
// ⚠ glReadPixels 讀回來的第一列是**畫面最下面那一列**（OpenGL 原點在左下）——
//   不翻轉就會得到一張上下顛倒的圖，而那不會報錯。
using System.IO.Compression;
using Silk.NET.OpenGL;

namespace Senate.Desktop;

public static class SenateScreenshot
{
    public static unsafe void Capture(GL iGl, int iWidth, int iHeight, string iPath)
    {
        if (iWidth <= 0 || iHeight <= 0) throw new ArgumentException("framebuffer 尺寸不合法");

        var aPixels = new byte[iWidth * iHeight * 4];
        fixed (byte* p = aPixels)
        {
            iGl.PixelStore(PixelStoreParameter.PackAlignment, 1);
            iGl.ReadPixels(0, 0, (uint)iWidth, (uint)iHeight,
                PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        string? aDir = Path.GetDirectoryName(Path.GetFullPath(iPath));
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllBytes(iPath, EncodePng(aPixels, iWidth, iHeight));
    }

    /// <summary>RGBA（左下原點）→ PNG（左上原點）。</summary>
    static byte[] EncodePng(byte[] iRgbaBottomUp, int iWidth, int iHeight)
    {
        // ① 掃描線：每列前面加一個 filter type byte（0 = None），順便上下翻轉
        var aRaw = new byte[(iWidth * 4 + 1) * iHeight];
        int aDst = 0;
        for (int y = iHeight - 1; y >= 0; y--)
        {
            aRaw[aDst++] = 0;
            System.Buffer.BlockCopy(iRgbaBottomUp, y * iWidth * 4, aRaw, aDst, iWidth * 4);
            aDst += iWidth * 4;
        }

        // ② zlib：header(0x78 0x01) + deflate(raw) + adler32(raw)
        byte[] aDeflate;
        using (var ms = new MemoryStream())
        {
            using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(aRaw, 0, aRaw.Length);
            aDeflate = ms.ToArray();
        }
        var aZlib = new byte[aDeflate.Length + 6];
        aZlib[0] = 0x78; aZlib[1] = 0x01;
        System.Buffer.BlockCopy(aDeflate, 0, aZlib, 2, aDeflate.Length);
        uint aAdler = Adler32(aRaw);
        aZlib[^4] = (byte)(aAdler >> 24); aZlib[^3] = (byte)(aAdler >> 16);
        aZlib[^2] = (byte)(aAdler >> 8); aZlib[^1] = (byte)aAdler;

        // ③ 組 PNG
        using var aOut = new MemoryStream();
        aOut.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

        var aIhdr = new byte[13];
        WriteBe(aIhdr, 0, (uint)iWidth);
        WriteBe(aIhdr, 4, (uint)iHeight);
        aIhdr[8] = 8;      // bit depth
        aIhdr[9] = 6;      // color type: RGBA
        aIhdr[10] = 0; aIhdr[11] = 0; aIhdr[12] = 0;
        WriteChunk(aOut, "IHDR", aIhdr);
        WriteChunk(aOut, "IDAT", aZlib);
        WriteChunk(aOut, "IEND", Array.Empty<byte>());
        return aOut.ToArray();
    }

    static void WriteChunk(Stream oStream, string iType, byte[] iData)
    {
        var aLen = new byte[4];
        WriteBe(aLen, 0, (uint)iData.Length);
        oStream.Write(aLen, 0, 4);

        var aTypeBytes = new byte[4];
        for (int i = 0; i < 4; i++) aTypeBytes[i] = (byte)iType[i];
        oStream.Write(aTypeBytes, 0, 4);
        oStream.Write(iData, 0, iData.Length);

        uint aCrc = Crc32(aTypeBytes, iData);
        var aCrcBytes = new byte[4];
        WriteBe(aCrcBytes, 0, aCrc);
        oStream.Write(aCrcBytes, 0, 4);
    }

    static void WriteBe(byte[] oBuffer, int iOffset, uint iValue)
    {
        oBuffer[iOffset] = (byte)(iValue >> 24);
        oBuffer[iOffset + 1] = (byte)(iValue >> 16);
        oBuffer[iOffset + 2] = (byte)(iValue >> 8);
        oBuffer[iOffset + 3] = (byte)iValue;
    }

    static uint Adler32(byte[] iData)
    {
        uint a = 1, b = 0;
        foreach (byte x in iData)
        {
            a = (a + x) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }

    static readonly uint[] s_CrcTable = BuildCrcTable();

    static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    static uint Crc32(byte[] iA, byte[] iB)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte x in iA) c = s_CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (byte x in iB) c = s_CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
