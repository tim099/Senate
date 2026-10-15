// 淨室驗收（TASK-0616）：關聯的 meta material ⇒ 匯出 .vxk 的檔頭 materials（每一格的材質）——
// ① 🔴 沒有關聯寫 material ⇒ 檔頭沒有 materials 鍵（跟以前逐欄相同）。
// ② 寫了 ⇒ ids 照字典序、voxels 每種幾格、runs 解回來每一格的材質跟預期逐格相同：
//    自己寫的照自己、沒寫的照上一層（巢狀：mid 寫 teak ⇒ mid 自己的格 teak；mid 底下的 inner 寫 copper ⇒ copper）、都沒寫 ⇒ 0（船的預設材質）；
//    被蓋掉的格照「已有的格贏」（材質跟著先放的那一格）。
// ③ 格子那一段（每格座標、顏色、checksum）跟沒寫 material 時逐格相同。
// ④ 🔴 反向對照：meta=none 清掉 ⇒ 檔頭又沒有 materials 鍵、格子照舊。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureMaterialCleanRoom()
    {
        const string name = "雕刻關聯材質：🔴 沒寫 ⇒ vxk 沒有 materials 鍵／寫了 ⇒ ids、每種幾格、runs 逐格對（自己的、照上一層、巢狀覆蓋、被蓋掉照先放的）／格子逐格不變／🔴 清掉 ⇒ 又沒有鍵（淨室，TASK-0616）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(new SculptProbeRenderer());
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "材質 " + id), ("commission_ref", "user:mat:" + id), ("account", "acct")).ExitCode == 0;
            bool Box(string id, int x1, int x2, int y1, int y2, int z1, int z2, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()),
                            ("z1", z1.ToString()), ("z2", z2.ToString()), ("color", color.ToString())).ExitCode == 0;
            bool Link(string work, string nm, string src, string at)
                => room.Run(("op", "work"), ("sub", "link"), ("work", work), ("persona", "p"), ("name", nm), ("source_work", src), ("at", at)).ExitCode == 0;
            SCP_CmdResult Meta(string work, string nm, string meta)
                => room.Run(("op", "work"), ("sub", "meta"), ("work", work), ("persona", "p"), ("part", "link"), ("name", nm), ("meta", meta));

            // host 24³：自己 4×4×1（色 200）；plate 4×1×2（色 100）放兩次（p1、p2）；mid 6×6×3：自己 2×2×1（色 50）＋ inner → plate
            // p3 → plate 放在 host 自己的格上（重疊：host 的格贏 ⇒ 那幾格照 host ＝ 0）
            Check(Create("host", "24") && Create("plate", "4,1,2") && Create("mid", "6,6,3"), "三件作品建立");
            Check(Box("host", 0, 3, 0, 3, 0, 0, 200) && Box("plate", 0, 3, 0, 0, 0, 1, 100) && Box("mid", 0, 1, 0, 1, 0, 0, 50), "格子");
            Check(Link("mid", "inner", "plate", "0,3,0"), "mid ← inner（plate）");
            Check(Link("host", "p1", "plate", "10,0,0") && Link("host", "p2", "plate", "10,5,0") && Link("host", "m1", "mid", "0,10,0") && Link("host", "p3", "plate", "0,0,0"), "host 的四條關聯");

            SCP_JsonData? Export(string f, out List<(int X, int Y, int Z, int Color)> oVox)
            {
                oVox = new List<(int, int, int, int)>();
                var r = room.Run(("op", "export"), ("format", "vxk"), ("work", "host"), ("out", Out(f)), ("include", "parts,meta"));
                if (r.ExitCode != 0 || !File.Exists(Out(f))) { failures.Add("匯出 " + f + "：" + SculptRoom.All(r)); return null; }
                var (h, v) = SCP_SculptVxk.Unpack(File.ReadAllBytes(Out(f)));
                oVox = v;
                return h;
            }
            static List<(int, int, int, int)> Sorted(List<(int X, int Y, int Z, int Color)> v) { var s = new List<(int, int, int, int)>(v); s.Sort(); return s; }

            // ── ① 沒寫 ──
            var h0 = Export("before.vxk", out var v0);
            Check(h0 != null && !h0["materials"].Exists, "① 🔴 沒有關聯寫 material ⇒ 檔頭沒有 materials 鍵");

            // ── ② 寫了 ──
            Check(Meta("host", "p1", "{\"material\":\"iron\"}").ExitCode == 0 && Meta("host", "m1", "{\"material\":\"teak\"}").ExitCode == 0
                  && Meta("host", "p3", "{\"material\":\"iron\"}").ExitCode == 0 && Meta("mid", "inner", "{\"material\":\"copper\"}").ExitCode == 0, "寫 material（p1 iron、p3 iron、m1 teak、mid 的 inner copper）");
            var h1 = Export("after.vxk", out var v1);
            if (h1 != null)
            {
                SCP_JsonData M = h1["materials"];
                Check(M.Exists, "② 寫了 ⇒ 檔頭有 materials");
                var ids = new List<string>();
                if (M["ids"].Exists) foreach (SCP_JsonData x in M["ids"]) ids.Add(x.AsString());
                Check(string.Join(",", ids) == "copper,iron,teak", "② ids 照字典序：" + string.Join(",", ids));
                long sx = h1["size"][0].AsLong(), sy = h1["size"][1].AsLong();
                var at = new Dictionary<long, (int Color, int Mat)>();
                foreach (var v in v1) at[v.X + sx * (v.Y + sy * v.Z)] = (v.Color, 0);
                long prev = 0;
                var runs = new List<long>();
                if (M["runs"].Exists) foreach (SCP_JsonData x in M["runs"]) runs.Add(x.AsLong());
                Check(runs.Count % 3 == 0, "② runs 三個一組");
                for (int i = 0; i + 2 < runs.Count; i += 3)
                {
                    long s = prev + runs[i];
                    for (long L = s; L < s + runs[i + 1]; L++) if (at.TryGetValue(L, out var c)) at[L] = (c.Color, (int)runs[i + 2]);
                    prev = s + runs[i + 1];
                }
                // 預期：p1（x 10..13、y 0、z 0..1）iron；p2 0；m1 自己（x 0..1、y 10..11、z 0）teak；m1/inner（x 0..3、y 13、z 0..1）copper；
                //       p3 跟 host 自己的格重疊的 z=0 那層 host 贏 ⇒ 0；p3 的 z=1 那層 iron
                int Want(int x, int y, int z, int col)
                {
                    if (col == 200) return 0;
                    if (y == 0 && x >= 10) return 2;
                    if (y == 5) return 0;
                    if (y == 10 || y == 11) return 3;
                    if (y == 13) return 1;
                    if (y == 0 && z == 1) return 2;
                    return -1;
                }
                int bad = 0, n = 0;
                var cnt = new int[4];
                foreach (var v in v1)
                {
                    int got = at[v.X + sx * (v.Y + sy * v.Z)].Mat, want = Want(v.X, v.Y, v.Z, v.Color);
                    n++; cnt[got]++;
                    if (got != want) { bad++; if (bad <= 3) failures.Add($"② ({v.X},{v.Y},{v.Z}) 色 {v.Color}：材質 {got}，該是 {want}"); }
                }
                Check(bad == 0, "② 每一格的材質跟預期逐格相同（錯 " + bad + "／" + n + "）");
                Check(cnt[1] == 8 && cnt[2] == 12 && cnt[3] == 4, $"② 每種幾格：copper {cnt[1]}（8）、iron {cnt[2]}（p1 8 ＋ p3 上層 4）、teak {cnt[3]}（4）");
                Check(M["voxels"]["copper"].AsLong() == cnt[1] && M["voxels"]["iron"].AsLong() == cnt[2] && M["voxels"]["teak"].AsLong() == cnt[3], "② 檔頭的 voxels 跟解回來的一樣");
                readings.Add($"{n} 格：copper {cnt[1]}、iron {cnt[2]}、teak {cnt[3]}、預設 {cnt[0]}；runs {runs.Count / 3} 段");
                // ── ③ 格子不變 ──
                Check(Sorted(v0).SequenceEqual(Sorted(v1)) && h0 != null && h0["checksum"].AsLong() == h1["checksum"].AsLong(), "③ 格子那一段（座標、顏色、checksum）跟沒寫 material 時相同");
            }

            // ── ④ 清掉 ──
            Check(Meta("host", "p1", "none").ExitCode == 0 && Meta("host", "m1", "none").ExitCode == 0 && Meta("host", "p3", "none").ExitCode == 0 && Meta("mid", "inner", "none").ExitCode == 0, "清掉 meta");
            var h2 = Export("cleared.vxk", out var v2);
            Check(h2 != null && !h2["materials"].Exists && Sorted(v2).SequenceEqual(Sorted(v0)), "④ 🔴 清掉 ⇒ 又沒有 materials 鍵、格子照舊");
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
