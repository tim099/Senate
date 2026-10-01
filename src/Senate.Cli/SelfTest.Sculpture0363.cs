// 區塊職責：TASK-0363（雕刻搬到 Senate）的自我對拍 —— 只驗**不碰錢、不碰引擎**的那幾格。
// 物理意義：這幾格錯了都不會叫：
//           ① 預授權的分母（clamp 後體積、PNG 寬高）—— 量錯 ⇒ 門擋錯人，或讓超額的一刀過去。
//           ② 費率 ⌈n/100⌉ 的邊界（0／1／100／101）。
//           ③ 引擎回報解析：撈不到 JSON 時**回 null**，⛔ 不回全零物件（那會讓「引擎沒印」看起來像「什麼都沒放下」）。
//           收費／分享那幾格要真的 Server 與 python ⇒ 走 exe 實跑（單上的驗收），不在這裡假裝驗過。
// 數值影響：只在 temp 目錄寫一張 PNG，跑完刪；⛔ 不碰真實資料根。
#nullable enable
using SCP.Core.Canvas;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureContractCleanRoom()
    {
        const string aName = "雕刻：預授權分母（clamp 體積／PNG 寬高）、費率邊界、引擎回報撈不到就是 null（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_sculpt_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            // ① 兩角任意順序 ＋ clamp 0..255：x 5..3 ⇒ 3..5（3 格）、y 0..0（1 格）、z 250..300 ⇒ 250..255（6 格）
            int x1 = 5, x2 = 3, y1 = 0, y2 = 0, z1 = 250, z2 = 300;
            int aVol = Cmd_Sculpture.ClampedVolume(ref x1, ref x2, ref y1, ref y2, ref z1, ref z2);
            bool aClamp = aVol == 18 && x1 == 3 && x2 == 5 && z2 == 255;
            // 🔴 整段在界外 ⇒ 0（⛔ 不准算成負數或 1）
            int a1 = 300, a2 = 400, b1 = 0, b2 = 0, c1 = 0, c2 = 0;
            bool aOutside = Cmd_Sculpture.ClampedVolume(ref a1, ref a2, ref b1, ref b2, ref c1, ref c2) == 0;

            // ② 費率邊界
            bool aRate = Cmd_Sculpture.CeilDiv(0, 100) == 0 && Cmd_Sculpture.CeilDiv(1, 100) == 1
                         && Cmd_Sculpture.CeilDiv(100, 100) == 1 && Cmd_Sculpture.CeilDiv(101, 100) == 2;

            // ① PNG 寬高由本檔自己量（3x2）；🔴 不是 PNG ⇒ false（⛔ 不猜預設值）
            string aPng = Path.Combine(aTmp, "probe.png");
            File.WriteAllBytes(aPng, SCP_CanvasPng.EncodeRgb(new byte[6], 0, 0, 3, 2, 3));
            bool aSize = Cmd_Sculpture.TryReadPngSize(aPng, out int aW, out int aH) && aW == 3 && aH == 2;
            string aFake = Path.Combine(aTmp, "fake.png");
            File.WriteAllText(aFake, "not a png at all, just text");
            bool aFakeRejected = !Cmd_Sculpture.TryReadPngSize(aFake, out _, out _);

            // ③ 引擎回報：前面有人話、後面是 JSON ⇒ 撈得到；🔴 只有人話 ⇒ null
            var aJson = Cmd_Sculpture.ParseEngineJson("🎨 doing\n{\n  \"status\": \"success\",\n  \"placed_count\": 7\n}\n");
            bool aParsed = aJson != null && aJson.GetInt("placed_count", -1) == 7;
            bool aNoJson = Cmd_Sculpture.ParseEngineJson("❌ 體積過大警告：單次 box 最大允許 1,000,000 voxels") == null;

            bool aOk = aClamp && aOutside && aRate && aSize && aFakeRejected && aParsed && aNoJson;
            return new CheckRow(aName,
                $"clamp 體積={aVol}（期望 18）／🔴 全在界外⇒0={aOutside}／費率邊界={aRate}／PNG 寬高={aW}x{aH}"
                + $"／🔴 假 PNG 拒絕={aFakeRejected}／JSON 撈得到={aParsed}／🔴 沒 JSON⇒null={aNoJson}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }
}
