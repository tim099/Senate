// 淨室驗收（TASK-0503）：雕刻的帆 ——
// ① 定義：四邊形／三角帆、角綁固定點（含關聯路徑）、存在 sails.json、⛔ 一格 voxel 都不動。
// ② bind 的邊：那條邊上的每一個布點模擬完都還在兩角之間的線段上（逐點量，不是只看兩個角）。
// ③ 模擬：同輸入＋同秒數 ⇒ 逐位元同形狀；風向反過來 ⇒ 往另一邊鼓（最鼓量正負號相反、大小相近）；沒有風 ⇒ 平均位移幾乎只往 −z。
// ④ 帆角當繩的端點：free 角被繩拉住（離繩另一端不超過繩長）；改 slack／搬繩的另一端 ⇒ 帆角跟著動；繩畫到帆角模擬完的位置。
// ⑤ 跟著部件走：搬關聯 ⇒ 角跟著動；mirror ⇒ 翻到另一舷（＝固定點換過去的格心）；links=none ⇒ hidden 且不模擬；
//    🔴 刪掉角綁的固定點 ⇒ unanchor 當下全庫報出、之後觀測列 broken（⛔ 不靜默消失）；加回 ⇒ 接上。
// ⑥ 觀測：sails=0／hide_sails ⇒ 不畫（照樣模擬）；打錯 ⇒ 擋；渲染器收到的帆數 ＝ ok 的面數；export obj 帆網格索引在範圍內、繞序跟 vn 同向、mtl 有 Kd。
// ⑦ 讀數：二十四面帆（每邊 24 格）模擬 3 秒，每步幾毫秒。
// ⑧ GPU（另一列，沒有 GPU ⇒ Skipped）：有帆那張圖紅色像素明顯、沒帆幾乎沒有；只換帆形狀 ⇒ voxel 網格沿用（不重建）；只有帆也框得到。
using System.Diagnostics;
using System.Globalization;
using Senate.Core;
using Senate.Desktop;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureSailsCleanRoom()
    {
        const string name = "雕刻帆：四邊形／三角帆綁固定點、⛔ 不動 voxel／bind 的邊逐點在線段上／同輸入同秒數逐位元同形狀、風反向往另一邊鼓、沒風只往下／帆角當繩端點：被拉住、改 slack・搬另一端跟著動／搬關聯・mirror 跟著走、links=none 不模擬、🔴 刪固定點全庫報出・觀測列 broken／sails=0・hide_sails／obj 帆網格／二十四面帆的每步毫秒（淨室，TASK-0503）";
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
                ("commission", "帆 " + id), ("commission_ref", "user:sail:" + id), ("account", "acct")).ExitCode == 0;
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
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            SCP_SculptSailRig Rig(string id, double[] wind, double seconds, string links = "", string hide = "")
            {
                var rig = SCP_SculptSails.Rig(store, id, SCP_SculptLinks.Tree(store, id), SCP_SculptLinkSelection.Parse(links, hide), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                              new SCP_SculptSailEnv { Wind = wind, MetersPerVoxel = store.Load(id).MetersPerVoxel });
                rig.Simulate(seconds);
                return rig;
            }
            SCP_SculptSailReading? Sail(SCP_SculptSailRig r, string path) => r.Sails.Find(s => s.Path == path);
            bool Near(double[]? p, double x, double y, double z, double tol = 1e-9) => p != null && Math.Abs(p[0] - x) < tol && Math.Abs(p[1] - y) < tol && Math.Abs(p[2] - z) < tol;
            double Dist(double[] a, double[] b) => Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            double[] Pt(SCP_SculptSailGeom g, int i) => new[] { g.Points[i * 3], g.Points[i * 3 + 1], g.Points[i * 3 + 2] };
            double[] NoWind = { 0, 0, 0 }, WindY = { 0, 8, 0 }, WindNegY = { 0, -8, 0 };

            // ── 淨室作品：船 ship 48×48×48（一格 0.1 m），四個固定點圍出一面 30×20 的直立方帆（在 y=20 那一片、法線沿 y）──
            Check(Create("ship", "48,48,48") && Sub("ship", "update", ("meters_per_voxel", "0.1")).ExitCode == 0 && Dot("ship", 0, 0, 0, 60).ExitCode == 0, "船（一格 0.1 m）");
            int evShip = SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count;
            string Own(string id) => string.Join(";", store.Engine(store.Load(id), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));
            string ownShip = Own("ship");
            foreach (var (n, at) in new[] { ("yp", "5,20,30"), ("ys", "35,20,30"), ("cs", "35,20,10"), ("cp", "5,20,10"), ("cleat", "40,30,2") })
                Check(Sub("ship", "anchor", ("name", n), ("at", at)).ExitCode == 0, "固定點 " + n);

            // ① 四邊形：上緣（邊 0 ＝ yp→ys）整條釘在 yard 上
            var add = Sub("ship", "sail", ("name", "course"), ("corners", "yp|ys|cs|cp"), ("corner_names", "head_p|head_s|clew_s|clew_p"), ("bind", "0"), ("color", "#FF0000"), ("res", "12"));
            Check(add.ExitCode == 0 && V(add, "sail_corners") == "4", "加四邊形帆 course：" + SculptRoom.All(add));
            Check(File.Exists(store.SpacePaths("ship").SailsFile) && SCP_SculptSails.ReadSails(store.SpacePaths("ship")).sails[0].color == "#ff0000", "存在 sails.json、顏色正規化（小寫 hex）");
            var jib = Sub("ship", "sail", ("name", "jib"), ("corners", "5,30,30|5,30,10|20,40,10"));
            Check(jib.ExitCode == 0 && V(jib, "sail_corners") == "3", "加三角帆 jib（直接給座標）：" + SculptRoom.All(jib));

            // ② bind 的邊：上緣每一個布點（j=0 那一列，res 12 ⇒ 13 點）模擬完都在 (5.5,20.5,30.5)→(35.5,20.5,30.5) 上
            var rigY = Rig("ship", WindY, 2);
            var cY = Sail(rigY, "course")!;
            Check(cY.State == SCP_SculptSailReading.Ok && cY.Geom != null && cY.Resolution == 12 && cY.Particles == 13 * 13, "course 模擬：ok、12 格／邊、169 布點：" + cY.State + "/" + cY.Particles);
            if (cY.Geom != null)
            {
                int off = 0;
                for (int i = 0; i <= 12; i++)
                {
                    double[] q = Pt(cY.Geom, i);
                    if (Math.Abs(q[1] - 20.5) > 1e-9 || Math.Abs(q[2] - 30.5) > 1e-9 || q[0] < 5.5 - 1e-9 || q[0] > 35.5 + 1e-9) off++;
                }
                Check(off == 0, "bind 的邊：上緣 13 個布點全都還在 yard 的線段上（不是只釘兩角）：離線 " + off);
                // 中間那一列（j=6）有鼓出去 ⇒ 證明邊以外的布點是會動的（反向對照：上面那一格不是因為什麼都沒動）
                Check(Math.Abs(Pt(cY.Geom, 6 * 13 + 6)[1] - 20.5) > 0.5, "布的中央有離開平面（y 偏移 " + (Pt(cY.Geom, 6 * 13 + 6)[1] - 20.5).ToString("0.##") + "）");
            }
            // ③ 決定性：同輸入、同秒數 ⇒ 逐位元同形狀
            var rigY2 = Rig("ship", WindY, 2);
            Check(cY.Geom != null && Sail(rigY2, "course")!.Geom!.Points.SequenceEqual(cY.Geom.Points) && rigY2.Steps == 120, "同輸入＋同秒數（2 秒 ＝ 120 步）⇒ 逐位元同形狀");
            var rigY3 = Rig("ship", WindY, 2.5);
            Check(!Sail(rigY3, "course")!.Geom!.Points.SequenceEqual(cY.Geom!.Points), "反向對照：秒數不同 ⇒ 形狀不同（上一格不是因為根本沒在動）");
            // 風反向 ⇒ 往另一邊鼓
            var cN = Sail(Rig("ship", WindNegY, 2), "course")!;
            Check(cY.Billow * cN.Billow < 0 && Math.Abs(Math.Abs(cY.Billow) - Math.Abs(cN.Billow)) < 0.25 * Math.Abs(cY.Billow) && cY.MeanShift[1] > 0 && cN.MeanShift[1] < 0,
                  "風 +y／−y ⇒ 最鼓 " + cY.Billow.ToString("0.##") + "／" + cN.Billow.ToString("0.##") + "（法線 " + SCP_SculptSails.Fmt2(cY.PlaneNormal) + "）、平均位移 y " + cY.MeanShift[1].ToString("0.##") + "／" + cN.MeanShift[1].ToString("0.##"));
            readings.Add("course 風 ±8 m/s 最鼓 " + cY.Billow.ToString("0.##") + "／" + cN.Billow.ToString("0.##") + " 格");
            // 沒風 ⇒ 只受重力往下
            var c0 = Sail(Rig("ship", NoWind, 2), "course")!;
            Check(c0.MeanShift[2] < 0 && Math.Abs(c0.MeanShift[2]) > 3 * Math.Abs(c0.MeanShift[0]) && Math.Abs(c0.MeanShift[2]) > 3 * Math.Abs(c0.MeanShift[1]),
                  "沒風 ⇒ 平均位移幾乎只往 −z：(" + SCP_SculptSails.Fmt2(c0.MeanShift) + ")");
            readings.Add("沒風平均位移 (" + SCP_SculptSails.Fmt2(c0.MeanShift) + ")");

            // ④ 帆角當繩的端點：topsail 下面兩角 free，右下角（序號 2）用 sheet 拉到甲板的 cleat
            // 風往 +y 吹（把帆角吹離 cleat）⇒ sheet 繃住；cleat 在帆的 −y 那側、甲板上
            foreach (var (n, at) in new[] { ("tp", "5,24,44"), ("ts", "35,24,44") })
                Check(Sub("ship", "anchor", ("name", n), ("at", at)).ExitCode == 0, "固定點 " + n);
            Check(Sub("ship", "anchor", ("name", "cleat"), ("at", "40,14,20"), ("overwrite", "1")).ExitCode == 0, "cleat 搬到帆的 −y 側");
            Check(Sub("ship", "sail", ("name", "topsail"), ("corners", "tp|ts|35,24,32|5,24,32"), ("bind", "0"), ("free", "2,3"), ("res", "10")).ExitCode == 0, "topsail（下兩角 free）");
            var sheet = Sub("ship", "rope", ("name", "sheet"), ("from", "sail:topsail/2"), ("to", "cleat"));
            Check(sheet.ExitCode == 0, "繩 sheet：sail:topsail/2 → cleat（slack 1 ＝ 繩長 ＝ 設計時的距離）：" + SculptRoom.All(sheet));
            Check(Sub("ship", "rope", ("name", "typo"), ("from", "sail:topsail/9"), ("to", "cleat")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "typo"), ("from", "sail:nosail/0"), ("to", "cleat")).ExitCode == 2, "🔴 帆角打錯（角 9／沒有這面帆）⇒ 擋");
            Check(Sub("ship", "rope", ("name", "typo"), ("from", "cleat"), ("to", "1,2,3"), ("slack", "0.8")).ExitCode == 2, "🔴 沒綁帆角的繩 slack 0.8 ⇒ 擋（比兩端距離短的繩不存在）");
            double[] cleat = { 40.5, 14.5, 20.5 }, designCorner = { 35.5, 24.5, 32.5 };
            double L = Dist(cleat, designCorner);
            const int C2 = 10 * 11 + 10, C3 = 10 * 11;   // res 10 ⇒ (i=10,j=10) ＝ 角 2、(0,10) ＝ 角 3
            var rT = Rig("ship", WindY, 3);
            var sheetR = rT.Ropes.Find(r => r.Path == "sheet")!;
            var tsail = Sail(rT, "topsail")!;
            double[] corner2 = Pt(tsail.Geom!, C2), corner3 = Pt(tsail.Geom!, C3);
            Check(sheetR.FromSail == "topsail#2" && Near(sheetR.From, corner2[0], corner2[1], corner2[2]), "繩畫到帆角模擬完的位置：" + SCP_SculptRopes.Fmt(sheetR.From) + " ＝ " + SCP_SculptRopes.Fmt(corner2));
            Check(tsail.Tethers == 1 && Dist(corner2, cleat) <= L * 1.01 && Dist(corner2, cleat) >= L * 0.95, "帆角被繃住的繩拉住：離 cleat " + Dist(corner2, cleat).ToString("0.##") + " ≈ 繩長 " + L.ToString("0.##") + "、牽繩 " + tsail.Tethers);
            // 反向對照：角 3 沒綁繩 ⇒ 被風吹得比角 2 遠（+y）
            Check(corner3[1] - 24.5 > corner2[1] - 24.5 + 1, "沒綁繩的 free 角 3 被風吹得比被拉住的角 2 遠：y 位移 " + (corner3[1] - 24.5).ToString("0.#") + " vs " + (corner2[1] - 24.5).ToString("0.#"));
            // 收帆腳索（slack 0.85 ＜ 1：繩比設計時短）⇒ 帆角往 cleat 拉過去
            Check(Sub("ship", "rope", ("name", "sheet"), ("slack", "0.85")).ExitCode == 0, "sheet slack 0.85（綁帆角的繩可以小於 1）");
            double[] corner2b = Pt(Sail(Rig("ship", WindY, 3), "topsail")!.Geom!, C2);
            Check(Dist(corner2b, cleat) < Dist(corner2, cleat) - 1 && Dist(corner2b, cleat) <= 0.85 * L * 1.01, "收帆腳索 ⇒ 帆角往 cleat 靠：" + Dist(corner2, cleat).ToString("0.##") + " → " + Dist(corner2b, cleat).ToString("0.##") + "（繩長 " + (0.85 * L).ToString("0.##") + "）");
            // 搬繩的另一端 ⇒ 帆角跟著動（繩長照新的設計距離 × slack 重算；繃住的那一端跟著走）
            Check(Sub("ship", "anchor", ("name", "cleat"), ("at", "20,14,20"), ("overwrite", "1")).ExitCode == 0, "搬 cleat");
            double[] corner2c = Pt(Sail(Rig("ship", WindY, 3), "topsail")!.Geom!, C2);
            Check(Dist(corner2c, corner2b) > 1, "搬繩的另一端 ⇒ 帆角跟著動：" + SCP_SculptRopes.Fmt(corner2b) + " → " + SCP_SculptRopes.Fmt(corner2c));
            readings.Add("sheet：繃住時帆角離 cleat " + Dist(corner2, cleat).ToString("0.##") + "／繩長 " + L.ToString("0.##") + "；收到 0.85 ⇒ " + Dist(corner2b, cleat).ToString("0.##"));

            // ⑥ 觀測：CLI view 畫 3 面帆、1 條帆上的繩；sails=0／hide_sails；打錯擋
            var view = Run("ship", "view", ("out", Out("v1.png")), ("wind", "0,8,0"), ("sail_seconds", "2"));
            Check(view.ExitCode == 0 && V(view, "sails_drawn") == "3" && probe.Last != null && probe.Last.Sails.Count == 3 && probe.Last.SailRopes.Count == 1 && probe.Last.Ropes.Count == 0,
                  "view：畫 3 面帆、渲染器收到 3 面＋帆上的繩 1 條（不在靜態繩那份）：" + V(view, "sails_drawn") + "/" + probe.Last?.Sails.Count + "/" + probe.Last?.SailRopes.Count + " " + SculptRoom.All(view));
            Check(V(view, "sail_steps") == "120" && V(view, "visible_voxels") == "1", "view：模擬 120 步、帆不是 voxel（可見 voxel 仍是 1）：" + V(view, "sail_steps") + "/" + V(view, "visible_voxels"));
            var courseView = probe.Last?.Sails.Find(s => s.Path == "course");
            Check(courseView != null && courseView.Points.SequenceEqual(cY.Geom!.Points), "CLI view 的 course（wind 0,8,0、2 秒）＝ 直接模擬的那一份（逐位元）");
            var vS0 = Run("ship", "view", ("out", Out("v2.png")), ("sails", "0"));
            Check(vS0.ExitCode == 0 && V(vS0, "sails_drawn") == "0" && V(vS0, "sails_hidden") == "3" && probe.Last!.Sails.Count == 0 && probe.Last.SailRopes.Count == 1,
                  "sails=0 ⇒ 帆不畫、帆上的繩照畫（帆照樣模擬）：" + V(vS0, "sails_hidden"));
            var vHs = Run("ship", "view", ("out", Out("v3.png")), ("hide_sails", "jib"));
            Check(V(vHs, "sails_drawn") == "2" && probe.Last!.Sails.TrueForAll(s => s.Path != "jib"), "hide_sails=jib ⇒ 只畫另外兩面");
            Check(Run("ship", "view", ("out", Out("v4.png")), ("hide_sails", "nope")).ExitCode == 2 && Run("ship", "view", ("out", Out("v5.png")), ("sails", "2")).ExitCode == 2
                  && Run("ship", "view", ("out", Out("v6.png")), ("wind", "1,2")).ExitCode == 2 && Run("ship", "view", ("out", Out("v7.png")), ("sail_seconds", "99")).ExitCode == 2,
                  "🔴 hide_sails 打錯／sails=2／wind 兩個數／sail_seconds 99 ⇒ 擋");
            // 預設風：work sub=wind 寫進作品；觀測沒給 wind ⇒ 用它
            Check(Sub("ship", "wind", ("wind", "0,8,0"), ("sail_seconds", "2")).ExitCode == 0, "work sub=wind 0,8,0、2 秒");
            var vDef = Run("ship", "view", ("out", Out("v8.png")));
            Check(probe.Last!.Sails.Find(s => s.Path == "course")!.Points.SequenceEqual(cY.Geom!.Points), "沒給 wind ⇒ 用作品預設（跟 wind=0,8,0 那張逐位元相同）");
            var wsails = Sub("ship", "sails");
            Check(wsails.ExitCode == 0 && V(wsails, "sail_count") == "3" && V(wsails, "sail_steps") == "120", "sub=sails：3 面、模擬 120 步：" + SculptRoom.All(wsails));

            // 🔴 反向對照：壞參數擋下、零寫入
            string sailsBefore = File.ReadAllText(store.SpacePaths("ship").SailsFile);
            Check(Sub("ship", "sail", ("name", "bad"), ("corners", "yp|ys")).ExitCode == 2
                  && Sub("ship", "sail", ("name", "bad"), ("corners", "yp|ys|cs"), ("bind", "5")).ExitCode == 2
                  && Sub("ship", "sail", ("name", "bad"), ("corners", "yp|ys|cs"), ("color", "300")).ExitCode == 2
                  && Sub("ship", "sail", ("name", "bad"), ("corners", "yp|yz|cs")).ExitCode == 2
                  && Sub("ship", "sail", ("name", "bad"), ("corners", "yp|ys|cs"), ("fullness", "3")).ExitCode == 2
                  && Sub("ship", "sail", ("name", "course"), ("corners", "yp|ys|cs")).ExitCode == 2,
                  "🔴 兩個角／bind 5／color 300／固定點打錯／fullness 3／換角沒給 overwrite ⇒ 擋");
            Check(File.ReadAllText(store.SpacePaths("ship").SailsFile) == sailsBefore, "擋下的全部零寫入（sails.json 逐字不變）");

            // ⑤ 跟著部件走：桅 mast 自己有一面帆（綁桅的固定點），船關聯它 at 10,4,1；再一根 mirror=x
            Check(Create("mast", "10,4,20") && Dot("mast", 5, 1, 0, 50).ExitCode == 0, "桅");
            foreach (var (n, at) in new[] { ("a", "1,1,18"), ("b", "8,1,18"), ("c", "8,1,8"), ("d", "1,1,8") })
                Check(Sub("mast", "anchor", ("name", n), ("at", at)).ExitCode == 0, "桅固定點 " + n);
            Check(Sub("mast", "sail", ("name", "s"), ("corners", "a|b|c|d"), ("bind", "0")).ExitCode == 0, "桅自己的帆 s");
            Check(Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "10,4,1")).ExitCode == 0, "船關聯桅 main @10,4,1");
            var ms = Sail(Rig("ship", NoWind, 0), "main/s");
            Check(ms != null && ms.State == SCP_SculptSailReading.Ok && Near(ms.Corners[0], 11.5, 5.5, 19.5) && Near(ms.Corners[2], 18.5, 5.5, 9.5),
                  "被關聯的桅的帆 main/s：角照 at 換（手算 (11.5,5.5,19.5)…）：" + SCP_SculptRopes.Fmt(ms?.Corners[0]));
            Check(Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "12,4,1"), ("overwrite", "1")).ExitCode == 0
                  && Near(Sail(Rig("ship", NoWind, 0), "main/s")!.Corners[0], 13.5, 5.5, 19.5), "搬關聯 ⇒ 帆角跟著動");
            Check(Sub("ship", "link", ("name", "fore"), ("source_work", "mast"), ("at", "20,30,1"), ("mirror", "x")).ExitCode == 0, "第二根桅 fore mirror=x");
            // 固定點 a (1,1,18) 翻 x（W=10）⇒ 格 (8,1,18) ⇒ +at ⇒ (28,31,19) ⇒ 格心 (28.5,31.5,19.5)
            var fs = Sail(Rig("ship", NoWind, 0), "fore/s");
            Check(fs != null && Near(fs.Corners[0], 28.5, 31.5, 19.5), "mirror=x ⇒ 帆角翻到另一舷（＝固定點換過去的格心 (28.5,31.5,19.5)）：" + SCP_SculptRopes.Fmt(fs?.Corners[0]));
            var rNone = Rig("ship", NoWind, 1, "none");
            Check(Sail(rNone, "main/s")!.State == SCP_SculptSailReading.Hidden && !Sail(rNone, "main/s")!.Simulated && Sail(rNone, "main/s")!.Geom == null, "links=none ⇒ 桅上的帆 hidden、不模擬");

            // 🔴 刪掉角綁的固定點 ⇒ 全庫報出、觀測列 broken；加回 ⇒ 接上
            var un = Sub("mast", "unanchor", ("name", "c"));
            Check(un.ExitCode == 0 && V(un, "sails_now_broken") == "3", "🔴 刪桅的 c ⇒ 全庫掃到 3 面帆壞了（桅自己的 s ＋ 船上的 main/s、fore/s）：" + V(un, "sails_now_broken") + " " + SculptRoom.All(un));
            var vBroken = Run("ship", "view", ("out", Out("v9.png")));
            Check(vBroken.ExitCode == 0 && V(vBroken, "sails_broken") == "2" && SculptRoom.All(vBroken).Contains("🔴"), "觀測照樣出圖、但大聲列出 2 面 broken（⛔ 不靜默消失）：" + V(vBroken, "sails_broken"));
            Check(Sub("mast", "anchor", ("name", "c"), ("at", "8,1,8")).ExitCode == 0 && Sail(Rig("ship", NoWind, 0), "main/s")!.State == SCP_SculptSailReading.Ok, "加回 c ⇒ 接上");

            // obj 匯出
            string obj = Out("ship.obj");
            var ex = Run("ship", "export", ("format", "obj"), ("out", obj));
            Check(ex.ExitCode == 0 && V(ex, "sails_exported") == "5", "export obj：5 面帆（course、jib、topsail、main/s、fore/s）：" + SculptRoom.All(ex));
            if (File.Exists(obj))
            {
                string[] lines = File.ReadAllLines(obj);
                var vs = new List<double[]>(); var vns = new List<double[]>();
                int badIdx = 0, badWind = 0, sailFaces = 0; bool inSail = false;
                foreach (string l in lines)
                {
                    string[] t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0) continue;
                    double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
                    if (t[0] == "v") vs.Add(new[] { D(t[1]), D(t[2]), D(t[3]) });
                    else if (t[0] == "vn") vns.Add(new[] { D(t[1]), D(t[2]), D(t[3]) });
                    else if (t[0] == "o") inSail = t[1].StartsWith("sail_");
                    else if (t[0] == "f")
                    {
                        var vi = new List<int>(); var ni = new List<int>();
                        foreach (string fv in t.Skip(1)) { var parts = fv.Split('/'); vi.Add(int.Parse(parts[0])); ni.Add(parts.Length > 2 ? int.Parse(parts[2]) : 0); }
                        if (vi.Any(i => i < 1 || i > vs.Count) || ni.Any(i => i < 0 || i > vns.Count)) { badIdx++; continue; }
                        if (!inSail) continue;
                        sailFaces++;
                        double[] p0 = vs[vi[0] - 1], p1 = vs[vi[1] - 1], p2 = vs[vi[2] - 1];
                        double[] n = { vns[ni[0] - 1][0] + vns[ni[1] - 1][0] + vns[ni[2] - 1][0], vns[ni[0] - 1][1] + vns[ni[1] - 1][1] + vns[ni[2] - 1][1], vns[ni[0] - 1][2] + vns[ni[1] - 1][2] + vns[ni[2] - 1][2] };
                        double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2], wx = p2[0] - p0[0], wy = p2[1] - p0[1], wz = p2[2] - p0[2];
                        double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                        if (cx * n[0] + cy * n[1] + cz * n[2] <= 0) badWind++;
                    }
                }
                Check(lines.Count(l => l.StartsWith("o sail_")) == 5 && lines.Count(l => l.StartsWith("usemtl sail_")) == 5, "obj：5 個 o sail_n／usemtl sail_n");
                Check(badIdx == 0, "obj 每個面的 v／vn 索引都在範圍內：越界 " + badIdx);
                Check(sailFaces > 0 && badWind == 0, "obj 帆的每個面繞序跟 vn 同向（雙面各一份；OBJ 換軸是鏡像，要反過來寫）：反了 " + badWind + "／" + sailFaces);
                string mtl = File.ReadAllText(Path.ChangeExtension(obj, ".mtl"));
                Check(mtl.Contains("newmtl sail_0") && mtl.Contains("Kd 1.0000 0.0000 0.0000"), "mtl：sail_0 的 Kd 是紅色（course #ff0000）");
                readings.Add("obj 帆面 " + sailFaces);
            }
            else failures.Add("obj 沒寫出來");
            Check(V(Run("ship", "export", ("format", "list"), ("out", Out("ship.txt"))), "sails_exported") == "0", "list 只有格子：sails_exported=0");

            // ⛔ 一格 voxel 都不動
            Check(Own("ship") == ownShip && SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count == evShip, "⛔ 全部帆的操作之後，船自己的格與 events 數都沒變");
            Check(Sub("ship", "unsail", ("name", "topsail")).ExitCode == 0 && SCP_SculptRopes.ReadRopes(store.SpacePaths("ship")).ropes.Count == 1
                  && Rig("ship", NoWind, 0).Ropes.Find(r => r.Path == "sheet")!.State == SCP_SculptRopeReading.Broken, "unsail topsail ⇒ 綁它角的 sheet 變 broken（繩沒被刪）");

            // ⑦ 讀數：二十四面帆（每邊 24 格）一起模擬 3 秒
            var big = new SCP_SculptSailFile { wind = new List<double> { 0, 8, 0 } };
            for (int k = 0; k < 24; k++)
                big.sails.Add(new SCP_SculptSail
                {
                    name = "s" + k, res = 24, bind = new List<int> { 0 },
                    corners = new List<SCP_SculptSailCorner> { new() { at = (k % 6 * 8) + ",10," + (40 - k / 6 * 9) }, new() { at = (k % 6 * 8 + 7) + ",10," + (40 - k / 6 * 9) },
                                                               new() { at = (k % 6 * 8 + 7) + ",10," + (33 - k / 6 * 9) }, new() { at = (k % 6 * 8) + ",10," + (33 - k / 6 * 9) } },
                });
            Check(Create("fleet", "48,48,48") && Sub("fleet", "update", ("meters_per_voxel", "0.1")).ExitCode == 0, "fleet");
            SCP_SculptSails.SaveSails(store.SpacePaths("fleet"), big);
            var sw = Stopwatch.StartNew();
            var rBig = Rig("fleet", WindY, 3);
            double ms3 = sw.Elapsed.TotalMilliseconds;
            Check(rBig.Sails.TrueForAll(s => s.State == SCP_SculptSailReading.Ok) && rBig.Steps == 180, "二十四面帆都 ok、180 步");
            readings.Add("24 面帆×625 布點：3 秒 180 步共 " + ms3.ToString("0") + " ms（每步 " + rBig.LastStepMs.ToString("0.###") + " ms）");

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    static CheckRow SculptSailGpuCleanRoom()
    {
        const string name = "雕刻帆 GPU：有帆的圖紅色像素明顯、沒帆幾乎沒有；只換帆形狀 ⇒ voxel 網格沿用不重建；只有帆也框得到（淨室，TASK-0503）";
        var failures = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++) vox.Add(new SCP_SculptVoxel(x, y, 0, 146));
            SCP_SculptSailGeom Sheet(double iBulge, byte iR, byte iB)
            {
                // 4×4 格的直立方帆（x 2..18、z 4..16、y＝10 往 +y 鼓）—— 直接給幾何，不經模擬
                int n = 4; var pts = new List<double>(); var tri = new List<int>();
                for (int j = 0; j <= n; j++) for (int i = 0; i <= n; i++)
                { double u = (double)i / n, v = (double)j / n; pts.Add(2 + 16 * u); pts.Add(10 + iBulge * Math.Sin(Math.PI * u) * Math.Sin(Math.PI * v)); pts.Add(16 - 12 * v); }
                for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
                { int a = j * (n + 1) + i; tri.AddRange(new[] { a, a + 1, a + n + 2, a, a + n + 2, a + n + 1 }); }
                var g = new SCP_SculptSailGeom { Path = "t", Points = pts.ToArray(), Triangles = tri.ToArray(), R = iR, G = 0, B = iB };
                g.Normals = SCP_SculptSails.VertexNormals(g.Points, g.Triangles);
                return g;
            }
            var p = new SCP_SculptRenderParams { Width = 384, Height = 384, Skybox = SCP_SculptRenderParams.SkyboxNone };
            if (!r.TryRender(vox, p, out byte[] aNone, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "沒帆那張就失敗了：" + err, CheckResult.Fail);
            // 「紅」用色相判（背向光的那一面只吃環境光、整體偏暗，但仍是紅的）
            int Red(byte[] img) { int n = 0; for (int k = 0; k < img.Length; k += 4) if (img[k] > 40 && img[k] > 3 * img[k + 1] && img[k] > 3 * img[k + 2]) n++; return n; }
            var ps = p.Clone(); ps.Sails = new List<SCP_SculptSailGeom> { Sheet(3, 255, 0) };
            Check(r.TryRender(vox, ps, out byte[] aSail, out err), "有帆那張失敗：" + err);
            var rd = r.LastReading;
            int redNone = Red(aNone), redSail = Red(aSail);
            Check(redSail > 1000 && redNone < 5, "紅色像素：有帆 " + redSail + "、沒帆 " + redNone);
            Check(rd.Sails == 1 && rd.SailTriangles == 64 && !rd.MeshRebuilt, "帆是另一趟：讀數 1 面 64 三角形（雙面 32×2），voxel 網格沿用沒重建：" + rd);
            // 背面也看得到：從 −y 那一側看（yaw 180）照樣是紅的
            var pb = ps.Clone(); pb.YawDeg = ps.YawDeg + 180;
            Check(r.TryRender(vox, pb, out byte[] aBack, out err) && Red(aBack) > 1000, "雙面：轉到背面看照樣是紅的（" + Red(aBack) + "）：" + err);
            var ps2 = p.Clone(); ps2.Sails = new List<SCP_SculptSailGeom> { Sheet(6, 0, 255) };
            Check(r.TryRender(vox, ps2, out byte[] aBlue, out err) && !r.LastReading.MeshRebuilt && Red(aBlue) < 5, "只換帆的形狀與顏色 ⇒ voxel 網格照樣沿用、紅色消失（帆那一趟有重傳）");
            Check(r.TryRender(new List<SCP_SculptVoxel>(), ps, out byte[] aOnly, out err) && Red(aOnly) > 1000, "只有帆沒有 voxel ⇒ 照樣框得到（紅色 " + Red(aOnly) + "）：" + err);
            return new CheckRow(name, failures.Count == 0 ? "紅色像素 有帆 " + redSail + "／沒帆 " + redNone + "／背面 " + Red(aBack) + "；" + rd : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
    }
}
