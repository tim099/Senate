// 淨室驗收（TASK-0511 子繩、TASK-0512 繩分組）——
// 0511 ① 位置：rope:母繩/比例 落在母繩**實際的曲線**上 —— 拉直的母繩 ⇒ 手算內插；下垂的母繩 ⇒ 比例 0.5 ＝ 懸鏈線最低點（另外用牛頓法解一次，不借實作）；
//         比例 1 ＝ 母繩的 to（逐位元）；子繩兩端拉直（長 ＝ 直線距離、下垂 0）、3 邊管子（比一般繩 8 邊省）。
//      ② 跟著動：搬關聯、換母繩端點 ⇒ 子繩跟著走（不重下子繩）；子繩上再掛子繩；跨關聯掛在被關聯作品自己的繩上；
//         綁帆角的母繩模擬完換了形狀 ⇒ 子繩落在母繩那一刻的曲線上、歸在「會動」那一類。
//      ③ 🔴 擋：沒有這條母繩／比例超出 0..1／子繩 slack ≠ 1／子繩另一端綁帆角／改成繞成環 ⇒ exit 2、ropes.json 逐字不變；
//         手寫進檔的環 ⇒ 觀測時環上每條都 broken 並印出環；母繩上還掛著子繩 ⇒ unrope 擋下、列出子繩、零寫入。
//      ④ 顯示跟著母繩：母繩 hide_ropes／enabled=0／所在關聯沒組進來 ⇒ 子繩跟著不畫（巢狀也跟）。
// 0512 ⑤ 組：sub=rope group=、sub=ropegroup assign=（regex）／enabled=0|1；組關著 ⇒ 組裡（連同子組）disabled；開回 ⇒ 檔裡不留空設定。
//      ⑥ 觀測：rope_groups=（只畫）／hide_rope_groups=（拿掉）照全路徑（關聯路徑/組路徑）、連同子組；打錯 ⇒ 擋；渲染器收到的 ＝ ok 的那幾條。
//      ⑦ schema：用到組或子繩 ⇒ 2；都沒用 ⇒ 1（沒更新的 senate.exe 照樣讀）。觀測頁的組開關（純函數）。
//      ⑧ ⛔ 一格 voxel 都不動。
using System.Globalization;
using Senate.Cli.Pages;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureRopeChildrenCleanRoom()
    {
        const string name = "雕刻子繩：rope:母繩/比例 落在母繩實際曲線上（拉直手算、下垂對拍牛頓法）＋兩端拉直 3 邊管／搬關聯・換母繩端點・巢狀・跨關聯・帆模擬完都跟著走／"
                            + "🔴 沒有母繩・比例越界・slack≠1・綁帆角・繞成環 ⇒ 擋且零寫入、手寫的環觀測列 broken、母繩上有子繩 ⇒ unrope 擋／母繩不畫 ⇒ 子繩跟著不畫（淨室，TASK-0511）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            var probe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(probe);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "繩 " + id), ("commission_ref", "user:rope:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Dot(string id, int x, int y, int z, int color)
                => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            List<SCP_SculptRopeReading> Ropes(string id, string links = "", string hide = "", string hideRopes = "")
                => SCP_SculptRopes.Resolve(store, id, SCP_SculptLinks.Tree(store, id), SCP_SculptLinkSelection.Parse(links, hide), SCP_SculptRopeSelection.Parse("", hideRopes));
            SCP_SculptRopeReading? Rope(string id, string path, string links = "", string hide = "", string hideRopes = "") => Ropes(id, links, hide, hideRopes).Find(r => r.Path == path);
            bool Near(double[]? p, double x, double y, double z, double tol = 1e-9) => p != null && Math.Abs(p[0] - x) < tol && Math.Abs(p[1] - y) < tol && Math.Abs(p[2] - z) < tol;
            string Fmt(double[]? p) => SCP_SculptRopes.Fmt(p);
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');

            // 桅 mast 3×3×16（固定點 top (1,1,15)、自己的繩 mr top → (2,2,0)）；船 ship 30×12×20，桅放 at 10,4,1
            Check(Create("mast", "3,3,16") && Dot("mast", 1, 1, 0, 50).ExitCode == 0 && Create("ship", "30,12,20") && Dot("ship", 29, 11, 0, 60).ExitCode == 0, "桅、船");
            Check(Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "10,4,1")).ExitCode == 0, "船關聯桅 main @10,4,1");
            int evShip = SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count;
            string ownShip = string.Join(";", store.Engine(store.Load("ship"), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));
            Check(Sub("mast", "anchor", ("name", "top"), ("at", "1,1,15")).ExitCode == 0, "桅固定點 top");
            foreach (var (n, at) in new[] { ("a0", "0,5,2"), ("a1", "20,5,2"), ("b0", "2,1,2"), ("b1", "20,1,2") })
                Check(Sub("ship", "anchor", ("name", n), ("at", at)).ExitCode == 0, "船固定點 " + n);

            // ── ① 位置 ──────────────────────────────────────────────────────
            // 下垂的母繩 s：a0 → a1（同高、水平距 20、slack 1.2 ⇒ L 24）；比例 0.5 ＝ 最低點：x 中間、z ＝ 2.5 − a(cosh(10/a)−1)（牛頓法另外解）
            double a = 10;
            for (int i = 0; i < 100; i++) { double f = 2 * a * Math.Sinh(10 / a) - 24, df = 2 * Math.Sinh(10 / a) - 20 / a * Math.Cosh(10 / a); a -= f / df; }
            double sagExpect = a * (Math.Cosh(10 / a) - 1);
            Check(Sub("ship", "rope", ("name", "s"), ("from", "a0"), ("to", "a1"), ("slack", "1.2")).ExitCode == 0, "母繩 s（下垂）");
            var addM = Sub("ship", "rope", ("name", "m"), ("from", "rope:s/0.5"), ("to", "10.5,5.5,15.5"));
            Check(addM.ExitCode == 0, "子繩 m：rope:s/0.5 → 座標：" + SculptRoom.All(addM));
            var rm = Rope("ship", "m");
            Check(rm != null && rm.Child && rm.From != null && Math.Abs(rm.From[0] - 10.5) < 1e-9 && Math.Abs(rm.From[1] - 5.5) < 1e-9 && Math.Abs(rm.From[2] - (2.5 - sagExpect)) < 0.02,
                  "下垂母繩比例 0.5 ＝ 最低點 (10.5,5.5," + (2.5 - sagExpect).ToString("0.###") + ")（牛頓法）：" + Fmt(rm?.From));
            Check(rm != null && Math.Abs(rm.Length - rm.Chord) < 1e-12 && rm.MaxSag == 0 && rm.Geom != null && rm.Geom.Sides == SCP_SculptRopes.ChildSides && rm.Geom.PointCount == 2,
                  "子繩兩端拉直：長 ＝ 直線距離、下垂 0、兩個點、3 邊管：" + rm?.Length + "/" + rm?.Chord + "/" + rm?.Geom?.Sides);
            readings.Add("下垂母繩中點 z " + (rm?.From?[2] ?? double.NaN).ToString("0.###", CultureInfo.InvariantCulture) + "（牛頓法 " + (2.5 - sagExpect).ToString("0.###", CultureInfo.InvariantCulture) + "）");
            // 拉直的兩根 shroud：a0 → main/top、b0 → main/top；ratline 掛在兩根的 0.5 ⇒ 兩根的中點（手算）
            // top 格心 (1.5,1.5,15.5) + at(10,4,1) ⇒ (11.5,5.5,16.5)；a0 格心 (0.5,5.5,2.5)；b0 (2.5,1.5,2.5)
            Check(Sub("ship", "rope", ("name", "sh1"), ("from", "a0"), ("to", "main/top")).ExitCode == 0 && Sub("ship", "rope", ("name", "sh2"), ("from", "b0"), ("to", "main/top")).ExitCode == 0, "兩根 shroud");
            Check(Sub("ship", "rope", ("name", "rat"), ("from", "rope:sh1/0.5"), ("to", "rope:sh2/0.5"), ("thickness", "0.3")).ExitCode == 0, "ratline：rope:sh1/0.5 → rope:sh2/0.5");
            var rr = Rope("ship", "rat");
            Check(rr != null && Near(rr.From, 6, 5.5, 9.5) && Near(rr.To, 7, 3.5, 9.5), "拉直母繩的比例 0.5 ＝ 兩端中點 (6,5.5,9.5)→(7,3.5,9.5)：" + Fmt(rr?.From) + "→" + Fmt(rr?.To));
            var sh1 = Rope("ship", "sh1");
            Check(sh1 != null && rr != null && Rope("ship", "rat2") == null, "前提");
            Check(Sub("ship", "rope", ("name", "end1"), ("from", "rope:sh1/1"), ("to", "b1")).ExitCode == 0 && Rope("ship", "end1") is { } e1 && sh1 != null
                  && e1.From!.SequenceEqual(sh1.To!), "比例 1 ＝ 母繩的 to（逐位元）");
            // 管子：子繩 3 邊 ⇒ 一段 3 個四邊形（一般細繩 8 個），繞序照樣從外面看逆時針（幾何判）
            SCP_SculptRopes.Tube(rr!.Geom!, 8, out var tPos, out _, out var tQuads);
            SCP_SculptRopes.Tube(new SCP_SculptRopeGeom { Points = rr.Geom!.Points, Radius = rr.Geom.Radius }, 8, out _, out _, out var tQuads8);
            int badWinding = 0;
            foreach (int[] q in tQuads)
            {
                double[] p0 = tPos[q[0]], p1 = tPos[q[1]], p3 = tPos[q[3]];
                int ring = q[0] / 3;
                double[] c = { rr.Geom.Points[ring * 3], rr.Geom.Points[ring * 3 + 1], rr.Geom.Points[ring * 3 + 2] };
                double e1x = p1[0] - p0[0], e1y = p1[1] - p0[1], e1z = p1[2] - p0[2], e2x = p3[0] - p0[0], e2y = p3[1] - p0[1], e2z = p3[2] - p0[2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                if (nx * (p0[0] - c[0]) + ny * (p0[1] - c[1]) + nz * (p0[2] - c[2]) <= 0) badWinding++;
            }
            Check(tQuads.Count == 3 && tQuads8.Count == 8 && badWinding == 0, "子繩管子 3 個四邊形（同一條當一般繩是 8 個）、繞序全對：" + tQuads.Count + "/" + tQuads8.Count + "／反了 " + badWinding);
            readings.Add("ratline 三角形 " + tQuads.Count * 2 + "（一般繩 " + tQuads8.Count * 2 + "）");

            // ── ② 跟著動 ────────────────────────────────────────────────────
            Check(Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "12,4,1"), ("overwrite", "1")).ExitCode == 0, "搬桅到 12,4,1");
            // top ⇒ (13.5,5.5,16.5)：sh1 中點 (7,5.5,9.5)、sh2 中點 (8,3.5,9.5)
            Check(Near(Rope("ship", "rat")?.From, 7, 5.5, 9.5) && Near(Rope("ship", "rat")?.To, 8, 3.5, 9.5), "搬關聯 ⇒ 母繩動、ratline 跟著走（不重下）：" + Fmt(Rope("ship", "rat")?.From) + "→" + Fmt(Rope("ship", "rat")?.To));
            Check(Sub("ship", "rope", ("name", "sh2"), ("from", "b1"), ("overwrite", "1")).ExitCode == 0, "換 sh2 的下端到 b1");
            // b1 格心 (20.5,1.5,2.5)、top (13.5,5.5,16.5) ⇒ 中點 (17,3.5,9.5)
            Check(Near(Rope("ship", "rat")?.To, 17, 3.5, 9.5), "換母繩端點 ⇒ ratline 那一端跟著走 (17,3.5,9.5)：" + Fmt(Rope("ship", "rat")?.To));
            Check(Sub("ship", "rope", ("name", "rat2"), ("from", "rope:rat/0.5"), ("to", "5,5,5")).ExitCode == 0, "子繩上再掛子繩 rat2");
            var r2 = Rope("ship", "rat2");
            Check(r2 != null && Near(r2.From, 12, 4.5, 9.5) && r2.Depth == 2, "巢狀：rat2 的 from ＝ rat 的中點 (12,4.5,9.5)、深度 2：" + Fmt(r2?.From) + "／" + r2?.Depth);
            // 跨關聯：掛在桅自己的繩 main/mr 上（mr 在桅裡 top → (2,2,0)，換到船：(13.5,5.5,16.5) → (14.5,6.5,1.5)）
            Check(Sub("mast", "rope", ("name", "mr"), ("from", "top"), ("to", "2,2,0")).ExitCode == 0, "桅自己的繩 mr");
            Check(Sub("ship", "rope", ("name", "mc"), ("from", "rope:main/mr/0.5"), ("to", "a1")).ExitCode == 0 && Near(Rope("ship", "mc")?.From, 14, 6, 9),
                  "跨關聯掛在 main/mr 的中點 (14,6,9)：" + Fmt(Rope("ship", "mc")?.From));

            // 帆：綁帆角的母繩模擬完換了形狀 ⇒ 子繩落在那一刻的曲線上、歸在「會動」那一類
            Check(Create("rig", "48,48,48") && Sub("rig", "update", ("meters_per_voxel", "0.1")).ExitCode == 0 && Dot("rig", 0, 0, 0, 60).ExitCode == 0, "帆的淨室作品 rig");
            foreach (var (n, at) in new[] { ("tp", "5,24,44"), ("ts", "35,24,44"), ("cleat", "40,14,20") })
                Check(Sub("rig", "anchor", ("name", n), ("at", at)).ExitCode == 0, "rig 固定點 " + n);
            Check(Sub("rig", "sail", ("name", "topsail"), ("corners", "tp|ts|35,24,32|5,24,32"), ("bind", "0"), ("free", "2,3"), ("res", "10")).ExitCode == 0
                  && Sub("rig", "rope", ("name", "sheet"), ("from", "sail:topsail/2"), ("to", "cleat")).ExitCode == 0
                  && Sub("rig", "rope", ("name", "tail"), ("from", "rope:sheet/0.5"), ("to", "1,1,1")).ExitCode == 0, "topsail＋sheet（綁 free 角）＋掛在 sheet 中點的子繩 tail");
            SCP_SculptSailRig SailRig(double seconds)
            {
                var rig = SCP_SculptSails.Rig(store, "rig", SCP_SculptLinks.Tree(store, "rig"), new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                              new SCP_SculptSailEnv { Wind = new double[] { 0, 8, 0 }, MetersPerVoxel = 0.1 });
                rig.Simulate(seconds);
                return rig;
            }
            var rig0 = SailRig(0); var rig3 = SailRig(3);
            var tail0 = rig0.Ropes.Find(r => r.Path == "tail")!; var tail3 = rig3.Ropes.Find(r => r.Path == "tail")!; var sheet3 = rig3.Ropes.Find(r => r.Path == "sheet")!;
            // 獨立量：tail 的 from 要在 sheet 那一刻的折線上、而且從 sheet 起點沿線量過去剛好一半長（逐段找最近的那一段，不借 PointAt）
            double total = 0, along = double.NaN, best = double.MaxValue;
            double[] tf = tail3.From!;
            for (int i = 0; i + 1 < sheet3.Line!.Length / 3; i++)
            {
                double[] p = { sheet3.Line[i * 3], sheet3.Line[i * 3 + 1], sheet3.Line[i * 3 + 2] }, q = { sheet3.Line[i * 3 + 3], sheet3.Line[i * 3 + 4], sheet3.Line[i * 3 + 5] };
                double sx = q[0] - p[0], sy = q[1] - p[1], sz = q[2] - p[2], sl = Math.Sqrt(sx * sx + sy * sy + sz * sz);
                double u = sl < 1e-12 ? 0 : Math.Max(0, Math.Min(1, ((tf[0] - p[0]) * sx + (tf[1] - p[1]) * sy + (tf[2] - p[2]) * sz) / (sl * sl)));
                double dx = p[0] + sx * u - tf[0], dy = p[1] + sy * u - tf[1], dz = p[2] + sz * u - tf[2];
                double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (d < best) { best = d; along = total + sl * u; }
                total += sl;
            }
            Check(best < 1e-6 && Math.Abs(along - total / 2) < 1e-6, "帆模擬 3 秒後：tail 的 from 在 sheet 那一刻的曲線上（離線 " + best.ToString("0.#######") + "）、沿線剛好一半（" + along.ToString("0.###") + "／" + total.ToString("0.###") + "）");
            double moved = Math.Sqrt(Enumerable.Range(0, 3).Sum(k => (tail3.From![k] - tail0.From![k]) * (tail3.From[k] - tail0.From[k])));
            Check(moved > 0.5 && tail3.FollowsSail && rig3.RopeGeoms(true).Exists(g => g.Path == "tail") && !rig3.RopeGeoms(false).Exists(g => g.Path == "tail"),
                  "帆把 sheet 吹動 ⇒ tail 跟著走了 " + moved.ToString("0.##") + " 格、歸在會動那一類（觀測頁每幀重算）");
            readings.Add("帆吹動 tail " + moved.ToString("0.##") + " 格");

            // ── ③ 🔴 擋下：零寫入 ──────────────────────────────────────────
            string ropesBefore = File.ReadAllText(store.SpacePaths("ship").RopesFile);
            Check(Sub("ship", "rope", ("name", "bad"), ("from", "rope:nope/0.5"), ("to", "a0")).ExitCode == 2, "🔴 沒有這條母繩 ⇒ 擋");
            Check(Sub("ship", "rope", ("name", "bad"), ("from", "rope:sh1/1.5"), ("to", "a0")).ExitCode == 2 && Sub("ship", "rope", ("name", "bad"), ("from", "rope:sh1"), ("to", "a0")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "bad"), ("from", "rope:sh1/-0.1"), ("to", "a0")).ExitCode == 2, "🔴 比例 1.5／沒寫比例／−0.1 ⇒ 擋");
            Check(Sub("ship", "rope", ("name", "bad"), ("from", "rope:sh1/0.5"), ("to", "a0"), ("slack", "1.2")).ExitCode == 2 && Sub("ship", "rope", ("name", "rat"), ("slack", "1.1")).ExitCode == 2,
                  "🔴 子繩 slack ≠ 1 ⇒ 擋（不算物理）");
            var withSail = Sub("ship", "rope", ("name", "bad"), ("from", "rope:sh1/0.5"), ("to", "sail:x/0"));
            Check(withSail.ExitCode == 2 && SculptRoom.All(withSail).Contains("子繩"), "🔴 子繩另一端綁帆角 ⇒ 擋並說是子繩的規矩：" + SculptRoom.All(withSail));
            var cyc = Sub("ship", "rope", ("name", "rat"), ("from", "rope:rat2/0.5"), ("overwrite", "1"));
            Check(cyc.ExitCode == 2 && SculptRoom.All(cyc).Contains("繞成環"), "🔴 rat 改掛到自己的子繩 rat2 上（繞成環）⇒ 擋並印出環：" + SculptRoom.All(cyc));
            Check(Sub("ship", "rope", ("name", "self"), ("from", "rope:self/0.5"), ("to", "a0")).ExitCode == 2, "🔴 掛在自己身上 ⇒ 擋");
            Check(File.ReadAllText(store.SpacePaths("ship").RopesFile) == ropesBefore, "擋下的全部零寫入（ropes.json 逐字不變）");
            // 手寫進檔的環（繞過 CLI）⇒ 觀測時環上每條都 broken、印出環；其他繩照常
            var file = SCP_SculptRopes.ReadRopes(store.SpacePaths("ship"));
            file.ropes.Add(new SCP_SculptRope { name = "cy1", from = "rope:cy2/0.5", to = "a0" });
            file.ropes.Add(new SCP_SculptRope { name = "cy2", from = "rope:cy1/0.5", to = "a1" });
            SCP_SculptRopes.SaveRopes(store.SpacePaths("ship"), file);
            var cy1 = Rope("ship", "cy1"); var cy2 = Rope("ship", "cy2");
            Check(cy1?.State == SCP_SculptRopeReading.Broken && cy2?.State == SCP_SculptRopeReading.Broken && cy1.Reason.Contains("繞成環") && cy2.Reason.Contains("繞成環")
                  && Rope("ship", "rat")?.State == SCP_SculptRopeReading.Ok, "手寫的環 ⇒ 兩條都 broken、原因印出環；其他繩照常：" + cy1?.Reason + "／" + cy2?.Reason);
            var vCyc = Run("ship", "view", ("out", Out("cyc.png")));
            Check(vCyc.ExitCode == 0 && V(vCyc, "ropes_broken") == "2" && SculptRoom.All(vCyc).Contains("🔴"), "觀測照樣出圖、大聲列出 2 條 broken：" + V(vCyc, "ropes_broken"));
            File.WriteAllText(store.SpacePaths("ship").RopesFile, ropesBefore);
            // 母繩上還掛著子繩 ⇒ unrope 擋下、列出子繩、零寫入；先拿掉子繩才拿得掉母繩
            var unSh1 = Sub("ship", "unrope", ("name", "sh1"));
            Check(unSh1.ExitCode == 2 && V(unSh1, "ropes_hanging") == "2" && SculptRoom.All(unSh1).Contains("ship → 繩 `rat`") && SculptRoom.All(unSh1).Contains("ship → 繩 `end1`")
                  && File.ReadAllText(store.SpacePaths("ship").RopesFile) == ropesBefore, "🔴 unrope sh1（rat、end1 掛著）⇒ 擋、列出兩條、零寫入：" + V(unSh1, "ropes_hanging"));
            Check(Sub("mast", "unrope", ("name", "mr")).ExitCode == 2, "🔴 unrope 桅的 mr（船上的 mc 用關聯路徑掛著）⇒ 全庫掃到、擋");
            Check(Sub("ship", "unrope", ("name", "end1")).ExitCode == 0 && Sub("ship", "unrope", ("name", "mc")).ExitCode == 0 && Sub("mast", "unrope", ("name", "mr")).ExitCode == 0,
                  "拿掉子繩 end1、mc 之後 ⇒ mr 拿得掉");

            // ── ④ 顯示跟著母繩 ──────────────────────────────────────────────
            var hid = Rope("ship", "rat", hideRopes: "sh1");
            Check(hid?.State == SCP_SculptRopeReading.Hidden && hid.Reason.Contains("母繩 `sh1`") && Rope("ship", "rat2", hideRopes: "sh1")?.State == SCP_SculptRopeReading.Hidden,
                  "hide_ropes=sh1 ⇒ rat 跟著不畫（說是母繩）、掛在 rat 上的 rat2 也跟著：" + hid?.Reason);
            Check(Sub("ship", "rope", ("name", "sh1"), ("enabled", "0")).ExitCode == 0 && Rope("ship", "rat")?.State == SCP_SculptRopeReading.Hidden
                  && Sub("ship", "rope", ("name", "sh1"), ("enabled", "1")).ExitCode == 0, "母繩 enabled=0 ⇒ 子繩不畫；開回");
            // rope_groups 只點子繩那一組（母繩只是沒被點到）⇒ 子繩照畫（點名只畫 ratlines 就看得到 ratlines）；母繩被明點 hide_ropes ⇒ 照樣跟著藏
            Check(Sub("ship", "rope", ("name", "rat"), ("group", "rungs")).ExitCode == 0 && Sub("ship", "rope", ("name", "rat2"), ("group", "rungs")).ExitCode == 0, "rat、rat2 歸進 rungs");
            var only = SCP_SculptRopes.Resolve(store, "ship", SCP_SculptLinks.Tree(store, "ship"), new SCP_SculptLinkSelection(), SCP_SculptRopeSelection.Parse("", "", "rungs", ""));
            var onlyHide = SCP_SculptRopes.Resolve(store, "ship", SCP_SculptLinks.Tree(store, "ship"), new SCP_SculptLinkSelection(), SCP_SculptRopeSelection.Parse("", "sh1", "rungs", ""));
            Check(only.Find(r => r.Path == "rat")?.State == SCP_SculptRopeReading.Ok && only.Find(r => r.Path == "rat2")?.State == SCP_SculptRopeReading.Ok
                  && only.Find(r => r.Path == "sh1")?.State == SCP_SculptRopeReading.Hidden && onlyHide.Find(r => r.Path == "rat")?.State == SCP_SculptRopeReading.Hidden,
                  "rope_groups=rungs ⇒ rat、rat2 照畫（母繩 sh1 只是沒被點到）；再加 hide_ropes=sh1 ⇒ rat 跟著藏");
            var ln = Rope("ship", "rat", links: "none");
            Check(ln?.State == SCP_SculptRopeReading.Hidden && ln.LinkHidden, "links=none（母繩綁的桅沒組進來）⇒ 子繩也算沒組進來（LinkHidden）");
            var view = Run("ship", "view", ("out", Out("v1.png")), ("hide_ropes", "sh2"));
            int okCount = Ropes("ship", hideRopes: "sh2").Count(r => r.State == SCP_SculptRopeReading.Ok);
            Check(view.ExitCode == 0 && probe.Last != null && probe.Last.Ropes.Count == okCount && !probe.Last.Ropes.Exists(g => g.Path == "rat" || g.Path == "rat2")
                  && V(view, "ropes_child") == "3", "view hide_ropes=sh2 ⇒ 渲染器收到的 ＝ ok 的 " + okCount + " 條、rat／rat2 不在裡面；ropes_child=3（m、rat、rat2）：" + probe.Last?.Ropes.Count + "／" + V(view, "ropes_child"));
            var view2 = Run("ship", "view", ("out", Out("v2.png")));
            var ratGeom = probe.Last?.Ropes.Find(g => g.Path == "rat");
            Check(view2.ExitCode == 0 && ratGeom != null && ratGeom.Sides == SCP_SculptRopes.ChildSides, "view：渲染器收到的子繩帶 3 邊");

            // ── ⑧ 一格 voxel 都不動 ─────────────────────────────────────────
            string ownShipAfter = string.Join(";", store.Engine(store.Load("ship"), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));
            Check(ownShipAfter == ownShip && SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count == evShip, "⛔ 全部子繩操作之後，船自己的格、events 數都沒變");
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    static CheckRow SculptRopeChildGpuCleanRoom()
    {
        const string name = "雕刻子繩 GPU：同一條繩只把管子改成 3 邊（子繩）⇒ 網格重建、繩的三角形變少、照樣畫得出來（淨室，TASK-0511）";
        var failures = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new Senate.Desktop.SenateSculptRenderer();
        try
        {
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++) vox.Add(new SCP_SculptVoxel(x, y, 0, 146));
            double[] pts = { 2.5, 10.5, 8.5, 17.5, 10.5, 8.5 };
            var p8 = new SCP_SculptRenderParams { Width = 256, Height = 256, Skybox = SCP_SculptRenderParams.SkyboxNone };
            p8.Ropes = new List<SCP_SculptRopeGeom> { new SCP_SculptRopeGeom { Path = "t", Points = pts, Radius = 0.8, R = 255, G = 0, B = 0 } };
            if (!r.TryRender(vox, p8, out byte[] a8, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "8 邊那張就失敗了：" + err, CheckResult.Fail);
            int tri8 = r.LastReading.RopeTriangles;
            int Red(byte[] img) { int n = 0; for (int k = 0; k < img.Length; k += 4) if (img[k] > 120 && img[k + 1] < 60 && img[k + 2] < 60) n++; return n; }
            var p3 = p8.Clone();
            p3.Ropes = new List<SCP_SculptRopeGeom> { new SCP_SculptRopeGeom { Path = "t", Points = pts, Radius = 0.8, R = 255, G = 0, B = 0, Sides = SCP_SculptRopes.ChildSides } };
            Check(r.TryRender(vox, p3, out byte[] a3, out err), "3 邊那張失敗：" + err);
            var rd = r.LastReading;
            Check(rd.MeshRebuilt && rd.RopeTriangles > 0 && rd.RopeTriangles < tri8, "只改邊數 ⇒ 網格重建（⛔ 不拿舊網格）、三角形變少：" + tri8 + " → " + rd.RopeTriangles + "／重建 " + rd.MeshRebuilt);
            Check(Red(a3) > 50, "3 邊的繩照樣看得到（紅色 " + Red(a3) + "，8 邊 " + Red(a8) + "）");
            return new CheckRow(name, failures.Count == 0 ? "繩三角形 8 邊 " + tri8 + " → 3 邊 " + rd.RopeTriangles + "；紅色 " + Red(a8) + " → " + Red(a3) : string.Join("；", failures),
                                failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
    }

    static CheckRow SculptureRopeGroupsCleanRoom()
    {
        const string name = "雕刻繩分組：sub=rope group=・sub=ropegroup assign（regex）／enabled 平常關一組（連同子組）、開回不留空設定／rope_groups・hide_rope_groups 照全路徑（含關聯）／"
                            + "🔴 打錯組名・regex 沒符合・空組 ⇒ 擋且零寫入／schema 用到才是 2／觀測頁組開關（淨室，TASK-0512）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            var probe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(probe);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "繩 " + id), ("commission_ref", "user:rope:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Dot(string id, int x, int y, int z, int color)
                => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            List<SCP_SculptRopeReading> Ropes(string id) => SCP_SculptRopes.Resolve(store, id, SCP_SculptLinks.Tree(store, id), new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection());
            SCP_SculptRopeReading? Rope(string id, string path) => Ropes(id).Find(r => r.Path == path);
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            string Drawn() => string.Join(",", (probe.Last?.Ropes ?? new List<SCP_SculptRopeGeom>()).ConvertAll(g => g.Path).OrderBy(x => x, StringComparer.Ordinal));
            int Schema(string id) => SCP_SculptRopes.ReadRopes(store.SpacePaths(id)).schema;
            string Report(SCP_CmdResult r) => string.Join(Environment.NewLine, r.Outputs.Where(File.Exists).Select(f => File.ReadAllText(f)));   // 報告寫在回傳檔（Lines 只有摘要）

            Check(Create("mast", "3,3,10") && Dot("mast", 1, 1, 0, 50).ExitCode == 0 && Create("ship", "20,10,12") && Dot("ship", 19, 9, 0, 60).ExitCode == 0
                  && Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "10,4,1")).ExitCode == 0, "桅、船、關聯 main");
            int evShip = SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count;
            foreach (var n in new[] { "r1", "r2", "r3", "r4" })
                Check(Sub("ship", "rope", ("name", n), ("from", "0,0,0"), ("to", "5,5,5")).ExitCode == 0, "繩 " + n);
            Check(Sub("mast", "rope", ("name", "m1"), ("from", "0,0,0"), ("to", "2,2,9"), ("group", "stays")).ExitCode == 0, "桅自己的繩 m1（組 stays）");
            Check(Schema("ship") == 1 && File.ReadAllText(store.SpacePaths("ship").RopesFile).Contains("\"schema\": 1"), "⑦ 還沒用到組 ⇒ schema 1（沒更新的 senate.exe 照樣讀）");

            // ⑤ 分組
            Check(Sub("ship", "rope", ("name", "r1"), ("group", "standing/shrouds")).ExitCode == 0 && Sub("ship", "rope", ("name", "r2"), ("group", "/standing/shrouds/")).ExitCode == 0
                  && Sub("ship", "rope", ("name", "r3"), ("group", "running")).ExitCode == 0, "sub=rope group=（前後的 / 去掉）");
            Check(Rope("ship", "r2")?.Rope.group == "standing/shrouds" && Schema("ship") == SCP_SculptRopes.SchemaWithGroups, "存成 standing/shrouds；用到組 ⇒ schema 2");
            string before = File.ReadAllText(store.SpacePaths("ship").RopesFile);
            Check(Sub("ship", "rope", ("name", "r4"), ("group", "a:b")).ExitCode == 2 && Sub("ship", "rope", ("name", "r4"), ("group", "all")).ExitCode == 2, "🔴 組名有 `:`／保留字 ⇒ 擋");
            Check(Sub("ship", "ropegroup", ("name", "lines"), ("assign", "^nomatch$")).ExitCode == 2, "🔴 assign 一條都沒符合 ⇒ 擋");
            Check(Sub("ship", "ropegroup", ("name", "ghost"), ("enabled", "0")).ExitCode == 2, "🔴 空組（沒有繩）設開關 ⇒ 擋（⛔ 打錯組名不會變成一筆沒人用的設定）");
            Check(Sub("ship", "ropegroup", ("name", "lines"), ("assign", "([")).ExitCode == 2 && Sub("ship", "ropegroup", ("name", "lines")).ExitCode == 2, "🔴 壞 regex／沒要改什麼 ⇒ 擋");
            Check(File.ReadAllText(store.SpacePaths("ship").RopesFile) == before, "擋下的全部零寫入（ropes.json 逐字不變）");
            var asg = Sub("ship", "ropegroup", ("name", "lines"), ("assign", "^r[34]$"));
            Check(asg.ExitCode == 0 && V(asg, "ropes_assigned") == "2" && Rope("ship", "r3")?.Rope.group == "lines" && Rope("ship", "r4")?.Rope.group == "lines", "assign=^r[34]$ ⇒ r3、r4 歸進 lines（r3 從 running 換過來）");
            var off = Sub("ship", "ropegroup", ("name", "standing"), ("enabled", "0"));
            Check(off.ExitCode == 0 && V(off, "rope_group_enabled") == "0" && V(off, "rope_group_ropes") == "2", "standing 平常關（含子組 2 條）");
            Check(Rope("ship", "r1")?.State == SCP_SculptRopeReading.Disabled && Rope("ship", "r1")!.Reason.Contains("組 `standing`") && Rope("ship", "r3")?.State == SCP_SculptRopeReading.Ok,
                  "關上層組 ⇒ 子組 standing/shrouds 的 r1 disabled（說是哪一組）；lines 照畫");
            var v0 = Run("ship", "view", ("out", Out("g0.png")));
            Check(v0.ExitCode == 0 && V(v0, "ropes_disabled") == "2" && Drawn() == "main/m1,r3,r4", "view：r1、r2 不畫（disabled 2）；畫 main/m1,r3,r4：" + Drawn());
            Check(Sub("ship", "ropegroup", ("name", "standing/shrouds"), ("enabled", "0")).ExitCode == 0 && Rope("ship", "r2")!.Reason.Contains("組 `standing`")
                  && Sub("ship", "ropegroup", ("name", "standing/shrouds"), ("enabled", "1")).ExitCode == 0, "巢狀組名 standing/shrouds 也設得了（上層 standing 還關著 ⇒ 原因照報上層）");
            Check(Sub("ship", "ropegroup", ("name", "standing"), ("enabled", "1")).ExitCode == 0 && SCP_SculptRopes.ReadRopes(store.SpacePaths("ship")).groups.Count == 0
                  && Rope("ship", "r1")?.State == SCP_SculptRopeReading.Ok, "開回 ⇒ 檔裡不留空設定（groups 0）、r1 照畫");
            Check(Sub("ship", "ropegroup", ("name", "lines"), ("note", "running rigging")).ExitCode == 0 && SCP_SculptRopes.ReadRopes(store.SpacePaths("ship")).groups.Count == 1, "備註會留（groups 1）");

            // ⑥ 觀測：只畫／拿掉幾組（全路徑；關聯裡的寫 關聯/組）
            Check(Run("ship", "view", ("out", Out("g1.png")), ("rope_groups", "standing")).ExitCode == 0 && Drawn() == "r1,r2", "rope_groups=standing ⇒ 只畫 r1,r2（連同子組）：" + Drawn());
            Check(Run("ship", "view", ("out", Out("g2.png")), ("hide_rope_groups", "standing/shrouds,main/stays")).ExitCode == 0 && Drawn() == "r3,r4", "hide_rope_groups=standing/shrouds,main/stays ⇒ 只剩 r3,r4：" + Drawn());
            Check(Run("ship", "view", ("out", Out("g3.png")), ("rope_groups", "main/stays,lines")).ExitCode == 0 && Drawn() == "main/m1,r3,r4", "rope_groups=main/stays,lines（跨關聯）：" + Drawn());
            Check(Run("ship", "view", ("out", Out("g4.png")), ("hide_rope_groups", "nope")).ExitCode == 2 && Run("ship", "view", ("out", Out("g5.png")), ("rope_groups", "standin")).ExitCode == 2
                  && Run("ship", "view", ("out", Out("g6.png")), ("rope_groups", "stays")).ExitCode == 2, "🔴 打錯組名／前綴不完整／關聯裡的組沒帶關聯路徑 ⇒ 擋");
            // stats 不畫繩：rope_groups 跟 hide_ropes 一樣 ⇒ 照常跑完、但大聲說「給了而從來沒被讀」（⛔ 不假裝生效）
            var stRg = Run("ship", "stats", ("rope_groups", "lines"));
            Check(stRg.ExitCode == 0 && SculptRoom.All(stRg).Contains("rope_groups") && SculptRoom.All(stRg).Contains("從來沒被讀"), "stats 帶 rope_groups ⇒ 照常跑、警告沒被讀：" + SculptRoom.All(stRg));
            var list = Sub("ship", "ropes");
            Check(list.ExitCode == 0 && V(list, "rope_groups") == "4" && V(list, "ropes_ungrouped") == "0" && Report(list).Contains("`standing/shrouds`：2 條"),
                  "sub=ropes 列 4 個組（standing、standing/shrouds、lines、main/stays）：" + V(list, "rope_groups"));

            // ⑦ 觀測頁的組開關（純函數）
            var rows = SculptureViewerPage.RopeGroupRows(Ropes("ship"));
            string rowText = string.Join(";", rows.ConvertAll(r => r.Path + "@" + r.Depth + "#" + r.Count));
            Check(rowText == "lines@0#2;main/stays@0#1;standing@0#2;standing/shrouds@1#2", "組樹（父在子前）：" + rowText);
            var paths = rows.ConvertAll(r => r.Path);
            Check(SculptureViewerPage.RopeGroupToggleArgs(paths, _ => true) == ""
                  && SculptureViewerPage.RopeGroupToggleArgs(paths, x => x != "standing" && x != "standing/shrouds") == "standing"
                  && SculptureViewerPage.RopeGroupToggleArgs(paths, x => x != "standing/shrouds" && x != "lines") == "lines,standing/shrouds", "觀測頁組開關 → hide_rope_groups（上層取消就不重送子組）");

            // ⑧ 一格 voxel 都不動
            Check(SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count == evShip, "⛔ 分組操作不進 events");
            readings.Add("4 組、畫 " + Drawn());
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
