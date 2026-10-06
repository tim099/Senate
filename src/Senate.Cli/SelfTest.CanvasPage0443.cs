// 區塊職責：畫布觀測頁與 2D 展品的自我對拍（TASK-0443）。
// 物理意義：每一格驗一個「錯了也不會叫」的地方：
//           ① 展品 ＝ 同標題的宣稱合成一件、範圍取聯集外框；標題空白不併成一件「無標題」；任一筆 active ⇒ active
//           ② `canvas op=exhibit` 列得出來、`op=view --arg exhibit=<標題>` 用的是那件的外框（＋pad）；
//              反向對照：region 與 exhibit 同時給 ⇒ 擋；找不到的標題 ⇒ 擋、不退回全景
//           ③ 頁面純函式：已畫外框、自動倍率（長邊接近顯示框、有上下限）、region 夾進畫布／起點越界擋下
// 數值影響：temp 目錄造資料根與信件根，跑完刪。⛔ 不碰真實畫布。
#nullable enable
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using Senate.Cli.Pages;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow CanvasExhibitsCleanRoom()
    {
        const string aName = "2D 展品＝同標題宣稱合成一件（聯集外框）／op=exhibit 與 view exhibit=／畫布觀測頁的外框・倍率・region（淨室，TASK-0443）";
        string d = Path.Combine(Path.GetTempPath(), "senate_canvas0443_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        try
        {
            // ① 合併規則
            string aClaims = "{\"claims\":["
                + "{\"id\":\"a1\",\"persona\":\"kotoko\",\"title\":\"心\",\"status\":\"done\",\"region\":{\"x\":10,\"y\":10,\"w\":4,\"h\":4}},"
                + "{\"id\":\"a2\",\"persona\":\"kotoko\",\"title\":\" 心 \",\"status\":\"active\",\"region\":{\"x\":12,\"y\":8,\"w\":6,\"h\":3}},"
                + "{\"id\":\"b1\",\"persona\":\"basecamp\",\"title\":\"\",\"status\":\"done\",\"region\":{\"x\":100,\"y\":100,\"w\":2,\"h\":2}},"
                + "{\"id\":\"b2\",\"persona\":\"basecamp\",\"title\":\"\",\"status\":\"done\",\"region\":{\"x\":200,\"y\":200,\"w\":2,\"h\":2}},"
                + "{\"id\":\"c1\",\"persona\":\"summit\",\"title\":\"邊角\",\"status\":\"done\",\"region\":{\"x\":2040,\"y\":2040,\"w\":20,\"h\":20}}"
                + "]}";
            var aList = SCP_CanvasExhibits.FromClaims(SCP_JsonParser.Parse(aClaims)["claims"]);
            var aHeart = SCP_CanvasExhibits.Find(aList, "心");
            if (aList.Count != 4) aFails.Add($"🔴 展品 {aList.Count} 件（期望 4：心／claim:b1／claim:b2／邊角）");
            if (aHeart == null || aHeart.RegionText != "10,8,8,6" || aHeart.ClaimIds.Count != 2 || !aHeart.AnyActive)
                aFails.Add("🔴 同標題（前後空白不同）沒合成一件，或外框／狀態不對：" + (aHeart?.RegionText ?? "null"));
            if (SCP_CanvasExhibits.Find(aList, "claim:b1") == null || SCP_CanvasExhibits.Find(aList, "claim:b2") == null)
                aFails.Add("🔴 標題空白的兩筆被併成一件");
            if (SCP_CanvasExhibits.Find(aList, "邊角")?.RegionText != "2040,2040,8,8") aFails.Add("超出畫布的外框沒夾進來");

            // ② 指令（淨室資料根，沒有事件 ⇒ 空白畫布）
            string aData = Path.Combine(d, "AgentCommands");
            string aLetters = Path.Combine(d, "letters");
            Directory.CreateDirectory(Path.Combine(aData, "Canvas", "events"));
            Directory.CreateDirectory(Path.Combine(aLetters, "probe", "profile"));
            File.WriteAllText(Path.Combine(aData, "Canvas", "claims.json"), aClaims);
            SCP_CmdResult Canvas(Dictionary<string, string> iArgs)
            {
                iArgs["data_root"] = aData;
                return SCP_CmdRegistry.Dispatch("canvas", iArgs);
            }
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";
            var rList = Canvas(new() { ["op"] = "exhibit" });
            if (rList.ExitCode != 0 || V(rList, "exhibit_total") != "4" || V(rList, "exhibit_active") != "1")
                aFails.Add($"🔴 op=exhibit exit {rList.ExitCode} total={V(rList, "exhibit_total")} active={V(rList, "exhibit_active")}（期望 0／4／1）");
            var rView = Canvas(new() { ["op"] = "view", ["persona"] = "probe", ["letters_root"] = aLetters, ["exhibit"] = "心", ["pad"] = "1", ["scale"] = "2" });
            if (rView.ExitCode != 0 || V(rView, "region") != "9,7,10,8" || V(rView, "width") != "20" || V(rView, "height") != "16")
                aFails.Add($"🔴 view exhibit=心 pad=1 exit {rView.ExitCode} region={V(rView, "region")} {V(rView, "width")}×{V(rView, "height")}（期望 9,7,10,8／20×16）");
            var rBoth = Canvas(new() { ["op"] = "view", ["persona"] = "probe", ["letters_root"] = aLetters, ["exhibit"] = "心", ["region"] = "0,0,4,4" });
            if (rBoth.ExitCode != 2) aFails.Add($"反向對照：region＋exhibit 同時給沒被擋（exit {rBoth.ExitCode}）");
            var rMiss = Canvas(new() { ["op"] = "view", ["persona"] = "probe", ["letters_root"] = aLetters, ["exhibit"] = "不存在的作品" });
            if (rMiss.ExitCode != 2) aFails.Add($"🔴 反向對照：找不到的展品沒被擋（exit {rMiss.ExitCode}；會退回全景）");

            // ③ 頁面純函式
            var aMask = new byte[SCP_CanvasSpec.Area];
            bool aEmpty = !CanvasViewerPage.PaintedBounds(aMask, out _, out _, out _, out _);
            aMask[5 * SCP_CanvasSpec.Width + 7] = 1;
            aMask[9 * SCP_CanvasSpec.Width + 3] = 1;
            bool aBox = CanvasViewerPage.PaintedBounds(aMask, out int bx, out int by, out int bw, out int bh) && bx == 3 && by == 5 && bw == 5 && bh == 5;
            if (!aEmpty) aFails.Add("空白畫布回報有已畫外框");
            if (!aBox) aFails.Add($"🔴 已畫外框錯：{bx},{by},{bw},{bh}（期望 3,5,5,5）");
            int s16 = CanvasViewerPage.AutoScale(16, 16), s2048 = CanvasViewerPage.AutoScale(2048, 2048), s1 = CanvasViewerPage.AutoScale(1, 1);
            if (s16 != 32 || s2048 != 1 || s1 != CanvasViewerPage.MaxAutoScale) aFails.Add($"自動倍率 16→{s16}（32）／2048→{s2048}（1）／1→{s1}（上限）");
            bool rClip = CanvasViewerPage.TryParseRegion("2000,2000,100,100", out _, out _, out int cw, out int ch, out _) && cw == 48 && ch == 48;
            bool rOut = !CanvasViewerPage.TryParseRegion("3000,0,10,10", out _, out _, out _, out _, out _);
            if (!rClip) aFails.Add("region 超出畫布沒夾進來");
            if (!rOut) aFails.Add("🔴 起點在畫布外沒被擋");

            return new CheckRow(aName,
                aFails.Count == 0
                    ? "同標題合一（心＝10,8,8,6、2 筆、active）／空白標題不併／外框夾進畫布；op=exhibit 4 件 active 1；view exhibit=心 pad=1 ⇒ 9,7,10,8 ×2；region＋exhibit 擋、找不到擋；外框／倍率／region 夾值（temp 目錄）"
                    : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
