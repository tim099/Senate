// 區塊職責：把 OpenGL framebuffer 存成 PNG。
// 物理意義：⭐ 這支存在的唯一理由是**讓 GUI 有讀數**。原生視窗沒辦法被 CI 或 agent 用眼睛看，
//           於是「有沒有畫出來」「中文是不是方塊」「表格有沒有錯位」全都只能靠人回報。
//           落成圖檔之後，那些變成可以被任何人（包括不在現場的人）檢查的證據。
// 數值影響：純輸出。編碼走 `SCP_CanvasPng`（SCP_Core）——**不引入影像套件**這個原始理由沒變，
//           變的是「不引入」不必靠每個呼叫端各寫一份。
//
// 🩸 2026-09-08（TASK-0114 ①）：本檔原本自己帶一整顆 PNG 編碼器（`EncodePng` ＋ CRC 表 ＋ adler32，
//    約 100 行），而 `SCP_Core` 裡有一顆同功能的 `SCP_CanvasPng` —— 而它的 XML 註解自己寫著
//    「給非畫布來源用（例：畫面截圖）」，**本檔就是那個用途**。
//    ⛔ 兩份都活、都對、甚至逐位元組相同（兩顆底下同一個 `DeflateStream(Optimal)`）——
//    那正是《無錨引用》：**「我錨在哪一份」不寫在任何讀數上**，於是改了一邊不會有任何一層出聲。
//    ⇒ 刪掉本檔那一份，改呼叫共用層。`Senate.Desktop.csproj` 早就 ProjectReference 了 `SCP_Core`。
//
// ⚠ glReadPixels 讀回來的第一列是**畫面最下面那一列**（OpenGL 原點在左下）——
//   不翻轉就會得到一張上下顛倒的圖，而那不會報錯。
//   📌 翻轉**留在本檔**（下面 `Capture` 裡那個迴圈），因為 `EncodeRgbaRows` 吃的是左上原點 ——
//   那是 OpenGL 的座標慣例，不是 PNG 的事，所以它屬於呼叫端而不屬於編碼器。
//   🩸 這是本次搬遷唯一有機會做錯的地方（舊的 `EncodePng` 內部順手翻，新的不翻）⇒
//   驗收用了兩道翻轉哨兵：G 通道只跟 y 有關、alpha 上下半不同（見 TASK-0114 留言的對拍讀數）。
using SCP.Core.Canvas;
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

        // 左下原點（OpenGL）→ 左上原點（PNG）。⚠ 見檔頭：這一步不能省，而它省掉不會報錯。
        int aStride = iWidth * 4;
        var aTopDown = new byte[aPixels.Length];
        // ⚠ 必須寫全名 `System.Buffer` —— 裸 `Buffer` 在本檔會撞 `Silk.NET.OpenGL.Buffer`（CS0104）。
        for (int y = 0; y < iHeight; y++)
            System.Buffer.BlockCopy(aPixels, (iHeight - 1 - y) * aStride, aTopDown, y * aStride, aStride);

        string? aDir = Path.GetDirectoryName(Path.GetFullPath(iPath));
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllBytes(iPath, SCP_CanvasPng.EncodeRgbaRows(aTopDown, iWidth, iHeight));
    }
}
