// 區塊職責：畫布合併的自我對拍（TASK-0444）。
// 物理意義：合併是寫進 append-only 事實源的動作 ⇒ 寫錯就收不回來。每一格驗一個「錯了也不會叫」的地方：
//           ① 目標設定尺寸蓋不住平移後的範圍 ⇒ 擋下、零寫入
//           ② 平移後落在目標原本已畫的格子上 ⇒ 擋下、零寫入
//           🔴 ③ 寫入後：來源自己重播的每一格在 (x+dx,y+dy) 顏色一致、來源沒畫的格子目標也是空的；
//              共同祖先（兩邊同檔名同內容）照樣平移畫上去；目標原本的點不動
//           ④ claims 撞名加「（tag）」、notes 平移；重跑 ⇒ 0 筆新寫入
// 數值影響：temp 目錄造兩張畫布，跑完刪。⛔ 不碰真實畫布。
#nullable enable
using SCP.Core.Canvas;
using SCP.Core.Cmd;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow CanvasMergeCleanRoom()
    {
        const string aName = "畫布合併：尺寸不夠擋／蓋到目標已畫擋／寫入後逐格對拍（含共同祖先、反方向）／claims 撞名加標記・notes 平移／重跑 0 筆（淨室，TASK-0444）";
        string d = Path.Combine(Path.GetTempPath(), "senate_canvas0444_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        try
        {
            string aData = Path.Combine(d, "Bar", "AgentCommands");
            var aDst = new SCP_CanvasPaths(Path.Combine(aData, "Canvas"));
            var aSrc = new SCP_CanvasPaths(Path.Combine(d, "LY_Canvas"));
            void Ev(SCP_CanvasPaths iP, string iRel, string iTs, string iPixels)
            {
                string aPath = Path.Combine(iP.Events, iRel);
                Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
                File.WriteAllText(aPath, "{\"ts\":\"" + iTs + "\",\"persona\":\"probe\",\"pixels\":" + iPixels + "}");
            }
            // 共同祖先：兩邊同檔名同內容
            string aShared = "[{\"x\":5,\"y\":5,\"color\":9}]";
            Ev(aDst, "2026-01-01/000001_000_aaaaaa.json", "2026-01-01T00:00:01.000Z", aShared);
            Ev(aSrc, "2026-01-01/000001_000_aaaaaa.json", "2026-01-01T00:00:01.000Z", aShared);
            Ev(aDst, "2026-01-02/000001_000_bbbbbb.json", "2026-01-02T00:00:01.000Z", "[{\"x\":10,\"y\":10,\"color\":3}]");
            // 來源：同一格先 1 後 2（last-write-wins 要保留）
            Ev(aSrc, "2026-01-03/000001_000_cccccc.json", "2026-01-03T00:00:01.000Z", "[{\"x\":20,\"y\":30,\"color\":1}]");
            Ev(aSrc, "2026-01-03/000002_000_dddddd.json", "2026-01-03T00:00:02.000Z", "[{\"x\":20,\"y\":30,\"color\":2},{\"x\":2047,\"y\":2047,\"color\":4}]");
            File.WriteAllText(aDst.Claims, "{\"claims\":[{\"id\":\"c1\",\"persona\":\"kotoko\",\"title\":\"心\",\"status\":\"done\",\"region\":{\"x\":5,\"y\":5,\"w\":2,\"h\":2}}]}");
            File.WriteAllText(aSrc.Claims, "{\"claims\":[{\"id\":\"c1\",\"persona\":\"kotoko\",\"title\":\"心\",\"status\":\"done\",\"region\":{\"x\":5,\"y\":5,\"w\":2,\"h\":2}},"
                                          + "{\"id\":\"c9\",\"persona\":\"summit\",\"title\":\"山\",\"status\":\"active\",\"region\":{\"x\":20,\"y\":30,\"w\":3,\"h\":3}}]}");
            Directory.CreateDirectory(aSrc.Notes);
            File.WriteAllText(aSrc.NoteFile("summit"), "{\"persona\":\"summit\",\"notes\":[{\"id\":\"n1\",\"title\":\"稜線\",\"target_region\":{\"x\":100,\"y\":200,\"w\":5,\"h\":5}}]}");

            SCP_CmdResult Merge(bool iConfirm, string iDx = "2048") =>
                SCP_CmdRegistry.Dispatch("canvas", new Dictionary<string, string>
                {
                    ["data_root"] = aData, ["op"] = "merge", ["from"] = aSrc.Root, ["dx"] = iDx, ["dy"] = "0", ["tag"] = "LY",
                    ["confirm"] = iConfirm ? "1" : "0",
                });
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";
            int EventCount() => SCP_CanvasEvents.ScanManifest(aDst).Count;

            // ① 尺寸不夠
            var r1 = Merge(true);
            if (r1.ExitCode != 2 || EventCount() != 2) aFails.Add($"🔴 目標還是 2048 寬就合併（exit {r1.ExitCode}、事件 {EventCount()}）");
            SCP_CmdRegistry.Dispatch("canvas", new Dictionary<string, string> { ["data_root"] = aData, ["op"] = "size", ["width"] = "4096", ["height"] = "2048" });

            // ② 蓋到目標已畫（dx=5 ⇒ (5,5)→(10,5)? 不撞；用 dx=5,dy=5 讓 (5,5)→(10,10) 撞）
            var r2 = SCP_CmdRegistry.Dispatch("canvas", new Dictionary<string, string>
            { ["data_root"] = aData, ["op"] = "merge", ["from"] = aSrc.Root, ["dx"] = "5", ["dy"] = "5", ["tag"] = "LY", ["confirm"] = "1" });
            if (r2.ExitCode != 2 || V(r2, "overlap_cells") == "0" || EventCount() != 2) aFails.Add($"🔴 會蓋到目標已畫的格子沒被擋（exit {r2.ExitCode} overlap={V(r2, "overlap_cells")}）");

            // 試算零寫入
            var rDry = Merge(false);
            if (rDry.ExitCode != 0 || V(rDry, "to_write") != "3" || EventCount() != 2) aFails.Add($"試算 exit {rDry.ExitCode} to_write={V(rDry, "to_write")}、事件 {EventCount()}（期望 0／3／2）");

            // 🔴 ③ 寫入＋對拍
            var r3 = Merge(true);
            var s = SCP_CanvasBuffer.Build(aDst);
            int At(int x, int y) => s.Mask[y * s.Width + x] != 0 ? s.Buffer[y * s.Width + x] : -1;
            if (r3.ExitCode != 0 || V(r3, "mismatch_cells") != "0" || V(r3, "verified_cells") != "3")
                aFails.Add($"🔴 合併 exit {r3.ExitCode} 對拍 verified={V(r3, "verified_cells")} mismatch={V(r3, "mismatch_cells")}（期望 0／3／0）");
            if (At(2048 + 5, 5) != 9) aFails.Add("🔴 共同祖先沒有平移畫到右半邊");
            if (At(2048 + 20, 30) != 2) aFails.Add($"🔴 同一格先 1 後 2 合併後是 {At(2048 + 20, 30)}（last-write-wins 沒保留）");
            if (At(4095, 2047) != 4) aFails.Add("右下角 (2047,2047)→(4095,2047) 沒畫到");
            if (At(5, 5) != 9 || At(10, 10) != 3) aFails.Add("🔴 目標原本的點被動到");

            // ④ claims／notes／重跑
            var aEx = SCP_CanvasExhibits.FromClaims(SCP.Core.Json.SCP_JsonParser.Parse(File.ReadAllText(aDst.Claims))["claims"], s.Size);
            if (SCP_CanvasExhibits.Find(aEx, "心（LY）")?.RegionText != "2053,5,2,2" || SCP_CanvasExhibits.Find(aEx, "心")?.RegionText != "5,5,2,2"
                || SCP_CanvasExhibits.Find(aEx, "山")?.RegionText != "2068,30,3,3")
                aFails.Add("claims：撞名沒加（LY）、或範圍沒平移、或沒撞名的被改名");
            string aNote = File.Exists(aDst.NoteFile("summit")) ? File.ReadAllText(aDst.NoteFile("summit")) : "";
            if (!aNote.Contains("n1-ly") || !aNote.Contains("2148")) aFails.Add("notes 沒合併或 target_region 沒平移");
            var r4 = Merge(true);
            if (r4.ExitCode != 0 || V(r4, "to_write") != "0" || V(r4, "already_merged") != "3" || V(r4, "claims_added") != "0" || V(r4, "notes_added") != "0")
                aFails.Add($"🔴 重跑不是 0 筆：to_write={V(r4, "to_write")} already={V(r4, "already_merged")} claims={V(r4, "claims_added")} notes={V(r4, "notes_added")}");

            return new CheckRow(aName,
                aFails.Count == 0
                    ? "尺寸不夠擋、蓋到已畫擋（皆零寫入）；試算 3 筆零寫入；寫入後對拍 3 格 0 不一致、共同祖先畫到右半、last-write-wins 保留、左半不動；心→心（LY）、山平移、notes 平移；重跑 0 筆（temp 目錄）"
                    : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
