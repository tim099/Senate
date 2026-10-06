// 區塊職責：畫布尺寸參數化的自我對拍（TASK-0445）。
// 物理意義：尺寸從常數改成設定檔之後，要守的不變式是「已畫的點不會因為畫布變小而消失」。每一格驗一個「錯了也不會叫」的地方：
//           ① 沒有設定檔 ⇒ 2048×2048（同舊行為）
//           🔴 ② 有一點畫在 2048 外（例如合併進來的、或曾經擴大過）而設定是預設 ⇒ 實際尺寸被撐大、那一點在 mask 裡
//           ③ 擴大：`op=size` 寫入 ⇒ 實際尺寸變大、新範圍的 region／放點座標合法
//           🔴 ④ 反向對照（縮小）：`op=size` 設到已畫範圍以下 ⇒ exit 2、設定檔位元組不變
//           🔴 ⑤ 手改設定檔變小 ⇒ 實際尺寸仍蓋住已畫的點、clamped_up=1
//           ⑥ 快取記尺寸：同尺寸第二次讀走 Hit；尺寸變了 ⇒ 全重建（不硬讀舊長度的 blob）
// 數值影響：temp 目錄造資料根，跑完刪。⛔ 不碰真實畫布。
#nullable enable
using SCP.Core.Canvas;
using SCP.Core.Cmd;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow CanvasSizeCleanRoom()
    {
        const string aName = "畫布尺寸參數化：預設 2048／已畫範圍撐大實際尺寸／op=size 擴大／縮到已畫範圍以下擋下零寫入／手改變小仍蓋住已畫的點／快取記尺寸（淨室，TASK-0445）";
        string d = Path.Combine(Path.GetTempPath(), "senate_canvas0445_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        try
        {
            string aData = Path.Combine(d, "AgentCommands");
            var aPaths = new SCP_CanvasPaths(Path.Combine(aData, "Canvas"));
            string aDay = Path.Combine(aPaths.Events, "2026-01-01");
            Directory.CreateDirectory(aDay);
            SCP_CmdResult Canvas(Dictionary<string, string> iArgs)
            {
                iArgs["data_root"] = aData;
                return SCP_CmdRegistry.Dispatch("canvas", iArgs);
            }
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";

            // ① 沒有設定檔、一點在預設範圍內
            File.WriteAllText(Path.Combine(aDay, "000001_000_aaaaaa.json"),
                "{\"ts\":\"2026-01-01T00:00:01.000Z\",\"persona\":\"probe\",\"pixels\":[{\"x\":10,\"y\":20,\"color\":5}]}");
            var s1 = SCP_CanvasBuffer.Build(aPaths);
            if (!s1.Size.Equals(SCP_CanvasSize.Default) || s1.SizeInfo.FromFile)
                aFails.Add($"沒有設定檔時實際尺寸 {s1.Size}（期望 2048×2048、非來自設定檔）");

            // 🔴 ② 一點畫在 2048 外 ⇒ 撐大
            File.WriteAllText(Path.Combine(aDay, "000002_000_bbbbbb.json"),
                "{\"ts\":\"2026-01-01T00:00:02.000Z\",\"persona\":\"probe\",\"pixels\":[{\"x\":3000,\"y\":100,\"color\":7}]}");
            var s2 = SCP_CanvasBuffer.Build(aPaths);
            bool aFar = s2.Width == 3001 && s2.Height == 2048 && s2.Mask[100 * s2.Width + 3000] != 0 && s2.Buffer[100 * s2.Width + 3000] == 7;
            if (!aFar) aFails.Add($"🔴 畫在 2048 外的點：實際尺寸 {s2.Size}（期望 3001×2048）、點在 mask={aFar}（快取路 {s2.Path}）");

            // ③ 擴大
            var rGrow = Canvas(new() { ["op"] = "size", ["width"] = "4096", ["height"] = "2048" });
            var s3 = SCP_CanvasBuffer.Build(aPaths);
            if (rGrow.ExitCode != 0 || V(rGrow, "effective") != "4096x2048" || !s3.Size.Equals(new SCP_CanvasSize(4096, 2048)))
                aFails.Add($"擴大到 4096×2048：exit {rGrow.ExitCode} effective={V(rGrow, "effective")}、snapshot {s3.Size}");
            if (!SCP_CanvasPlace.TryParsePixels("", "4000", "2000", "5", s3.Size, out _, out string aPlaceWhy))
                aFails.Add("🔴 擴大後新範圍放不了點：" + aPlaceWhy);
            if (s3.Mask[20 * s3.Width + 10] == 0 || s3.Mask[100 * s3.Width + 3000] == 0) aFails.Add("🔴 擴大後舊點不見了（換尺寸重建時位置算錯）");

            // 🔴 ④ 縮到已畫範圍以下 ⇒ 擋下、零寫入
            byte[] aBefore = File.ReadAllBytes(aPaths.Settings);
            var rShrink = Canvas(new() { ["op"] = "size", ["width"] = "2048" });
            if (rShrink.ExitCode != 2 || !File.ReadAllBytes(aPaths.Settings).SequenceEqual(aBefore))
                aFails.Add($"🔴 縮到 2048 寬（已畫到 3000）沒被擋或設定檔被改了（exit {rShrink.ExitCode}）");
            var rShrinkOk = Canvas(new() { ["op"] = "size", ["width"] = "3001" });
            if (rShrinkOk.ExitCode != 0 || V(rShrinkOk, "configured") != "3001x2048")
                aFails.Add($"縮到剛好蓋住已畫範圍（3001）應該放行：exit {rShrinkOk.ExitCode} configured={V(rShrinkOk, "configured")}");

            // 🔴 ⑤ 手改設定檔變小
            File.WriteAllText(aPaths.Settings, "{\"width\":1000,\"height\":1000}");
            var s5 = SCP_CanvasBuffer.Build(aPaths);
            var rLook = Canvas(new() { ["op"] = "size" });
            bool aCovered = s5.Width == 3001 && s5.Height == 1000 && s5.Mask[100 * s5.Width + 3000] != 0 && s5.Mask[20 * s5.Width + 10] != 0;
            if (!aCovered || V(rLook, "clamped_up") != "1")
                aFails.Add($"🔴 手改設定成 1000×1000：實際 {s5.Size}（期望 3001×1000、兩點都在）clamped_up={V(rLook, "clamped_up")}");

            // ⑥ 快取記尺寸
            var s6 = SCP_CanvasBuffer.Build(aPaths);
            File.WriteAllText(aPaths.Settings, "{\"width\":3500,\"height\":1500}");
            var s7 = SCP_CanvasBuffer.Build(aPaths);
            if (s6.Path != SCP_CanvasCachePath.Hit) aFails.Add($"同尺寸第二次讀沒走快取（{s6.Path}）");
            if (s7.Path != SCP_CanvasCachePath.FullRebuild || !s7.Size.Equals(new SCP_CanvasSize(3500, 1500)) || !s7.RebuildReason.Contains("尺寸"))
                aFails.Add($"🔴 尺寸變了沒重建：{s7.Path} {s7.Size}「{s7.RebuildReason}」");

            return new CheckRow(aName,
                aFails.Count == 0
                    ? $"預設 {s1.Size}；2048 外一點 ⇒ {s2.Size} 且點在；op=size 4096×2048 生效、新範圍可放點、舊點在；縮到 2048 擋下且檔不變、縮到 3001 放行；手改 1000×1000 ⇒ {s5.Size}、clamped_up=1；同尺寸 Hit、換尺寸全重建（temp 目錄）"
                    : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
