// 淨室驗收（TASK-0502）：雕刻的固定點與繩索 ——
// ① 形狀：懸鏈線對拍「另外解一次」的解析解（對稱兩端的下垂量、折線長 ≈ slack×直線距離）；拉直 ⇒ 直線、下垂 0；同輸入逐位元同輸出；
//    管子每個四邊形從外面看是逆時針（拿幾何外法線判，不借實作的判法）。
// ② 跨關聯：端點寫 `main/top` ⇒ 座標照 at（手算）；搬關聯 ⇒ 端點跟著動；mirror=x＋turn=90 ⇒ 落點 ＝ 手算（跟 voxel 那一格的格心一致）。
// ③ 觀測：links=none／hide_links／關聯 enabled=0 ⇒ 綁在上面的繩跟著不畫（hidden）；ropes=0／hide_ropes ⇒ 不畫；渲染器收到的繩數 ＝ ok 的條數。
// ④ 🔴 反向對照：刪固定點（或改名）⇒ 綁著它的繩被報出來（unanchor 當下全庫掃到、之後每次觀測都是 broken），⛔ 不靜默消失；
//    打錯端點／hide_ropes 打錯 ⇒ 擋下且零寫入；壞的顏色／粗細／鬆緊 ⇒ 擋下。
// ⑤ ⛔ 一格 voxel 都不動：全部操作前後作品自己的格與 events 數不變。
// ⑥ 匯出：obj 多出管狀網格（一條一個 o／usemtl rope_n、mtl 有 Kd）、索引都在範圍內、每個面的繞序跟 vn 同向；list 只有格子（ropes_exported=0）。
// ⑦ GPU（另一列，沒有 GPU ⇒ Skipped，不是通過）：有繩那張圖紅色像素明顯、沒繩那張幾乎沒有；只有繩沒有 voxel 也框得到。
using System.Globalization;
using Senate.Cli.Pages;
using Senate.Core;
using Senate.Desktop;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureRopesCleanRoom()
    {
        const string name = "雕刻繩索：懸鏈線對拍獨立解析解＋拉直是直線＋決定性＋管子繞序／跨關聯端點照 at 與 mirror＋turn（手算）、搬關聯跟著動／links=none・hide_links・關聯關掉 ⇒ 跟著不畫、ropes=0・hide_ropes／🔴 刪固定點 ⇒ 全庫報出、觀測列 broken／打錯零寫入／一格 voxel 都不動／obj 管狀網格索引與繞序（淨室，TASK-0502）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            // ── ① 形狀（純函式）──────────────────────────────────────────────
            // 對稱兩端（同高、水平距 20、slack 1.2 ⇒ L 24）：獨立解 2a·sinh(10/a) ＝ 24（牛頓法，不借實作的二分法），下垂 ＝ a(cosh(10/a)−1)
            double a = 10;
            for (int i = 0; i < 100; i++) { double f = 2 * a * Math.Sinh(10 / a) - 24, df = 2 * Math.Sinh(10 / a) - 20 / a * Math.Cosh(10 / a); a -= f / df; }
            double aSagExpect = a * (Math.Cosh(10 / a) - 1);
            double[] pts = SCP_SculptRopes.Solve(new[] { 0.0, 0, 10 }, new[] { 20.0, 0, 10 }, 1.2, out double len, out double sag, out bool vert, out bool clamp);
            Check(Math.Abs(sag - aSagExpect) / aSagExpect < 0.01, "對稱懸鏈線下垂量 ≈ 獨立解析解：實作 " + sag.ToString("0.###") + " vs " + aSagExpect.ToString("0.###"));
            Check(Math.Abs(len - 24) / 24 < 0.005, "折線長 ≈ slack × 直線距離（24）：" + len.ToString("0.###"));
            Check(pts[0] == 0 && pts[2] == 10 && pts[pts.Length - 3] == 20 && pts[pts.Length - 1] == 10, "兩端釘死在端點（逐位元）");
            Check(!vert && !clamp, "一般情況不標 vertical／clamped");
            double[] pts2 = SCP_SculptRopes.Solve(new[] { 0.0, 0, 10 }, new[] { 20.0, 0, 10 }, 1.2, out _, out _, out _, out _);
            Check(pts.SequenceEqual(pts2), "同輸入 ⇒ 逐位元同輸出（決定性）");
            readings.Add("懸鏈線下垂 " + sag.ToString("0.###") + "（獨立解 " + aSagExpect.ToString("0.###") + "）、長 " + len.ToString("0.###") + "／24");
            SCP_SculptRopes.Solve(new[] { 0.0, 0, 0 }, new[] { 10.0, 0, 5 }, 1.0, out double tautLen, out double tautSag, out _, out _);
            Check(Math.Abs(tautLen - Math.Sqrt(125)) < 1e-9 && tautSag == 0, "拉直（slack 1）⇒ 直線：長 ＝ 直線距離、下垂 0");
            SCP_SculptRopes.Solve(new[] { 0.0, 0, 0 }, new[] { 0.0, 0, 9 }, 1.5, out _, out _, out bool aVert, out _);
            Check(aVert, "兩端在同一條鉛直線上 ⇒ 標 vertical（畫直線）");
            // 不對稱兩端：中段要比兩端連線低（有下垂）、長 ≈ L
            SCP_SculptRopes.Solve(new[] { 0.0, 0, 0 }, new[] { 10.0, 4, 6 }, 1.3, out double uLen, out double uSag, out _, out _);
            double uChord = Math.Sqrt(100 + 16 + 36);
            Check(uSag > 0 && Math.Abs(uLen - 1.3 * uChord) / (1.3 * uChord) < 0.005, "不對稱兩端：有下垂、長 ≈ 1.3 × 直線距離：" + uLen.ToString("0.###") + "／" + (1.3 * uChord).ToString("0.###"));
            // 管子繞序：每個四邊形 (q1−q0)×(q3−q0) 跟「頂點 − 中心線上那一點」同向（外法線用幾何算，不用實作給的 normal）
            var geom = new SCP_SculptRopeGeom { Points = pts, Radius = 0.5 };
            SCP_SculptRopes.Tube(geom, 8, out var tPos, out _, out var tQuads);
            int badWinding = 0;
            foreach (int[] q in tQuads)
            {
                double[] p0 = tPos[q[0]], p1 = tPos[q[1]], p3 = tPos[q[3]];
                int ring = q[0] / 8;
                double[] c = { pts[ring * 3], pts[ring * 3 + 1], pts[ring * 3 + 2] };
                double e1x = p1[0] - p0[0], e1y = p1[1] - p0[1], e1z = p1[2] - p0[2], e2x = p3[0] - p0[0], e2y = p3[1] - p0[1], e2z = p3[2] - p0[2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                if (nx * (p0[0] - c[0]) + ny * (p0[1] - c[1]) + nz * (p0[2] - c[2]) <= 0) badWinding++;
            }
            Check(tQuads.Count > 0 && badWinding == 0, "管子每個四邊形從外面看逆時針（幾何判）：反了 " + badWinding + "／" + tQuads.Count);

            // ── 淨室作品 ─────────────────────────────────────────────────────
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
            List<SCP_SculptRopeReading> Ropes(string id, string links = "", string hide = "")
                => SCP_SculptRopes.Resolve(store, id, SCP_SculptLinks.Tree(store, id), SCP_SculptLinkSelection.Parse(links, hide), new SCP_SculptRopeSelection());
            SCP_SculptRopeReading? Rope(string id, string path, string links = "", string hide = "") => Ropes(id, links, hide).Find(r => r.Path == path);
            bool Near(double[]? p, double x, double y, double z) => p != null && Math.Abs(p[0] - x) < 1e-9 && Math.Abs(p[1] - y) < 1e-9 && Math.Abs(p[2] - z) < 1e-9;
            string Fmt(double[]? p) => SCP_SculptRopes.Fmt(p);
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');

            // 桅 mast 3×3×10（一格當桅身，固定點 top 在 (0,2,9)）；船 ship 20×10×12（固定點 bow 在 (0,5,2)），桅放 at 10,4,1
            Check(Create("mast", "3,3,10") && Dot("mast", 1, 1, 0, 50).ExitCode == 0 && Create("ship", "20,10,12") && Dot("ship", 19, 9, 0, 60).ExitCode == 0, "桅、船");
            Check(Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "10,4,1")).ExitCode == 0, "船關聯桅 main @10,4,1");
            int evShip = SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count, evMast = SCP_SculptStore.ListEvents(store.SpacePaths("mast")).Count;
            string ownShip = string.Join(";", store.Engine(store.Load("ship"), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));

            // 固定點
            var anTop = Sub("mast", "anchor", ("name", "top"), ("at", "0,2,9"));
            Check(anTop.ExitCode == 0 && V(anTop, "anchor_at") == "0,2,9", "桅加固定點 top：" + SculptRoom.All(anTop));
            Check(Sub("ship", "anchor", ("name", "bow"), ("at", "0,5,2")).ExitCode == 0, "船加固定點 bow");
            Check(Sub("ship", "anchor", ("name", "oob"), ("at", "20,0,0")).ExitCode == 2, "🔴 固定點在作品外 ⇒ 擋");
            Check(Sub("ship", "anchor", ("name", "bow"), ("at", "1,5,2")).ExitCode == 2, "🔴 同名搬位置沒給 overwrite ⇒ 擋");
            var anchors = Sub("ship", "anchors");
            Check(anchors.ExitCode == 0 && V(anchors, "anchor_count") == "1" && V(anchors, "anchor_count_all") == "2", "sub=anchors：本作品 1、含關聯 2（main/top 看得到）：" + V(anchors, "anchor_count") + "/" + V(anchors, "anchor_count_all"));

            // ② 跨關聯：stay 從 bow 拉到 main/top
            var stay = Sub("ship", "rope", ("name", "stay"), ("from", "bow"), ("to", "main/top"), ("color", "#FF0000"), ("thickness", "0.8"));
            Check(stay.ExitCode == 0, "繩 stay bow → main/top：" + SculptRoom.All(stay));
            var rs = Rope("ship", "stay");
            // 手算：bow 格心 (0.5,5.5,2.5)；top 格心 (0.5,2.5,9.5) + at(10,4,1) ⇒ (10.5,6.5,10.5)
            Check(rs != null && rs.State == SCP_SculptRopeReading.Ok && Near(rs.From, 0.5, 5.5, 2.5) && Near(rs.To, 10.5, 6.5, 10.5), "跨關聯端點 ＝ 手算 (0.5,5.5,2.5)→(10.5,6.5,10.5)：" + Fmt(rs?.From) + "→" + Fmt(rs?.To));
            double chord = Math.Sqrt(100 + 1 + 64);
            Check(rs != null && Math.Abs(rs.Length - chord) < 1e-9 && rs.MaxSag == 0 && V(stay, "rope_max_sag") == "0", "拉直（預設 slack 1）⇒ 長 ＝ 直線距離、下垂 0");
            Check(SCP_SculptRopes.ReadRopes(store.SpacePaths("ship")).ropes[0].color == "#ff0000", "顏色存正規化（小寫 hex）");
            var slack = Sub("ship", "rope", ("name", "stay"), ("slack", "1.1"));
            var rs2 = Rope("ship", "stay");
            Check(slack.ExitCode == 0 && rs2 != null && rs2.MaxSag > 0 && Math.Abs(rs2.Length - 1.1 * chord) / (1.1 * chord) < 0.005, "改 slack 1.1（不用 overwrite）⇒ 下垂 > 0、長 ≈ 1.1×直線：" + V(slack, "rope_length") + "／下垂 " + V(slack, "rope_max_sag"));
            readings.Add("stay 長 " + V(slack, "rope_length") + "、最大下垂 " + V(slack, "rope_max_sag"));
            // 搬關聯 ⇒ 端點跟著動
            Check(Sub("ship", "link", ("name", "main"), ("source_work", "mast"), ("at", "12,4,1"), ("overwrite", "1")).ExitCode == 0, "搬桅到 12,4,1");
            Check(Near(Rope("ship", "stay")?.To, 12.5, 6.5, 10.5), "搬關聯 ⇒ 綁在桅上那一端跟著動（12.5,6.5,10.5）：" + Fmt(Rope("ship", "stay")?.To));
            // mirror=x＋turn=90：top (0,2,9) ⇒ 翻 x（W=3）(2,2,9) ⇒ 轉 90（H=3）(H−1−y, x) ＝ (0,2,9) ⇒ +at(2,0,1) ＝ 格 (2,2,10) ⇒ 格心 (2.5,2.5,10.5)
            Check(Sub("ship", "link", ("name", "fore"), ("source_work", "mast"), ("at", "2,0,1"), ("mirror", "x"), ("turn", "90")).ExitCode == 0, "第二根桅 fore mirror=x turn=90");
            Check(Sub("ship", "rope", ("name", "port"), ("from", "fore/top"), ("to", "6,5,3")).ExitCode == 0, "繩 port fore/top → 座標 6,5,3");
            var rp = Rope("ship", "port");
            Check(rp != null && Near(rp.From, 2.5, 2.5, 10.5) && Near(rp.To, 6.5, 5.5, 3.5), "mirror＋turn 的端點 ＝ 手算格心 (2.5,2.5,10.5)、整數座標 ⇒ 格心 (6.5,5.5,3.5)：" + Fmt(rp?.From) + "→" + Fmt(rp?.To));

            // ③ 觀測：預設兩條都畫；渲染器收到的繩數 ＝ ok 數
            var view = Run("ship", "view", ("out", Out("v1.png")));
            Check(view.ExitCode == 0 && V(view, "ropes_drawn") == "2" && probe.Last != null && probe.Last.Ropes.Count == 2, "view：畫 2 條、渲染器收到 2 條：" + V(view, "ropes_drawn") + "/" + probe.Last?.Ropes.Count);
            Check(V(view, "visible_voxels") == "3", "繩不是 voxel：可見 voxel 仍是 3（船 1＋兩根桅各 1）：" + V(view, "visible_voxels"));
            var vNone = Run("ship", "view", ("out", Out("v2.png")), ("links", "none"));
            Check(vNone.ExitCode == 0 && V(vNone, "ropes_drawn") == "0" && V(vNone, "ropes_hidden") == "2" && probe.Last!.Ropes.Count == 0, "links=none ⇒ 綁在桅上的兩條跟著不畫（hidden 2）：" + V(vNone, "ropes_hidden"));
            var vHide = Run("ship", "view", ("out", Out("v3.png")), ("hide_links", "main"));
            Check(vHide.ExitCode == 0 && V(vHide, "ropes_drawn") == "1" && V(vHide, "ropes_hidden") == "1", "hide_links=main ⇒ 只剩 port 畫：" + V(vHide, "ropes_drawn"));
            Check(Sub("ship", "link", ("name", "fore"), ("enabled", "0")).ExitCode == 0, "關掉 fore");
            var vOff = Run("ship", "view", ("out", Out("v4.png")));
            Check(V(vOff, "ropes_drawn") == "1" && Rope("ship", "port")!.State == SCP_SculptRopeReading.Hidden, "關聯關掉 ⇒ 綁在它上面的 port 跟著不畫");
            Check(Sub("ship", "link", ("name", "fore"), ("enabled", "1")).ExitCode == 0, "開回 fore");
            var vR0 = Run("ship", "view", ("out", Out("v5.png")), ("ropes", "0"));
            var vHr = Run("ship", "view", ("out", Out("v6.png")), ("hide_ropes", "stay"));
            Check(V(vR0, "ropes_drawn") == "0" && V(vHr, "ropes_drawn") == "1" && probe.Last!.Ropes.Count == 1 && probe.Last.Ropes[0].Path == "port", "ropes=0 ⇒ 0 條；hide_ropes=stay ⇒ 只畫 port");
            Check(Run("ship", "view", ("out", Out("v7.png")), ("hide_ropes", "nope")).ExitCode == 2 && Run("ship", "view", ("out", Out("v8.png")), ("ropes", "2")).ExitCode == 2,
                  "🔴 hide_ropes 打錯／ropes=2 ⇒ 擋（⛔ 不變成什麼都沒關）");
            Check(Sub("ship", "rope", ("name", "stay"), ("enabled", "0")).ExitCode == 0 && Rope("ship", "stay")!.State == SCP_SculptRopeReading.Disabled
                  && Sub("ship", "rope", ("name", "stay"), ("enabled", "1")).ExitCode == 0, "繩自己 enabled=0 ⇒ disabled；開回");

            // ⑥ 匯出 obj：管狀網格
            string obj = Out("ship.obj");
            var ex = Run("ship", "export", ("format", "obj"), ("out", obj));
            Check(ex.ExitCode == 0 && V(ex, "ropes_exported") == "2", "export obj：2 條繩：" + SculptRoom.All(ex));
            if (File.Exists(obj))
            {
                string[] lines = File.ReadAllLines(obj);
                var vs = new List<double[]>(); var vns = new List<double[]>();
                int badIdx = 0, badWind = 0, ropeFaces = 0; bool inRope = false;
                foreach (string l in lines)
                {
                    string[] t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0) continue;
                    double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);
                    if (t[0] == "v") vs.Add(new[] { D(t[1]), D(t[2]), D(t[3]) });
                    else if (t[0] == "vn") vns.Add(new[] { D(t[1]), D(t[2]), D(t[3]) });
                    else if (t[0] == "o") inRope = t[1].StartsWith("rope_");
                    else if (t[0] == "f")
                    {
                        var vi = new List<int>(); var ni = new List<int>();
                        foreach (string fv in t.Skip(1)) { var parts = fv.Split('/'); vi.Add(int.Parse(parts[0])); ni.Add(parts.Length > 2 ? int.Parse(parts[2]) : 0); }
                        if (vi.Any(i => i < 1 || i > vs.Count) || ni.Any(i => i < 0 || i > vns.Count)) { badIdx++; continue; }
                        if (!inRope) continue;
                        ropeFaces++;
                        double[] p0 = vs[vi[0] - 1], p1 = vs[vi[1] - 1], p2 = vs[vi[2] - 1], n0 = vns[ni[0] - 1];
                        double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2], wx = p2[0] - p0[0], wy = p2[1] - p0[1], wz = p2[2] - p0[2];
                        double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                        if (cx * n0[0] + cy * n0[1] + cz * n0[2] <= 0) badWind++;
                    }
                }
                Check(lines.Count(l => l.StartsWith("o rope_")) == 2 && lines.Count(l => l.StartsWith("usemtl rope_")) == 2, "obj：兩個 o rope_n／usemtl rope_n");
                Check(badIdx == 0, "obj 每個面的 v／vn 索引都在範圍內：越界 " + badIdx);
                Check(ropeFaces > 0 && badWind == 0, "obj 繩的每個面繞序跟 vn 同向（OBJ y-up 換軸是鏡像，要反過來寫）：反了 " + badWind + "／" + ropeFaces);
                string mtl = File.ReadAllText(Path.ChangeExtension(obj, ".mtl"));
                Check(mtl.Contains("newmtl rope_0") && mtl.Contains("Kd 1.0000 0.0000 0.0000"), "mtl：rope_0 的 Kd 是紅色（stay #ff0000）");
                readings.Add("obj 繩面 " + ropeFaces + "、v " + vs.Count + "、vn " + vns.Count);
            }
            else failures.Add("obj 沒寫出來");
            var exList = Run("ship", "export", ("format", "list"), ("out", Out("ship.txt")));
            Check(exList.ExitCode == 0 && V(exList, "ropes_exported") == "0", "list 只有格子：ropes_exported=0（⛔ 不假裝匯出了繩）");

            // ④ 🔴 反向對照：打錯端點 ⇒ 擋、零寫入；壞參數 ⇒ 擋
            string ropesBefore = File.ReadAllText(store.SpacePaths("ship").RopesFile);
            Check(Sub("ship", "rope", ("name", "typo"), ("from", "bow"), ("to", "main/tpo")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "typo"), ("from", "bow"), ("to", "mian/top")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "typo"), ("from", "sail:a/b"), ("to", "bow")).ExitCode == 2, "🔴 固定點／關聯打錯、`種類:` 端點 ⇒ 擋");
            Check(Sub("ship", "rope", ("name", "typo"), ("from", "bow"), ("to", "1,2,3"), ("color", "300")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "typo"), ("from", "bow"), ("to", "1,2,3"), ("thickness", "0")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "typo"), ("from", "bow"), ("to", "1,2,3"), ("slack", "0.5")).ExitCode == 2
                  && Sub("ship", "rope", ("name", "stay"), ("to", "1,2,3")).ExitCode == 2, "🔴 color 300／thickness 0／slack 0.5／換端點沒給 overwrite ⇒ 擋");
            Check(File.ReadAllText(store.SpacePaths("ship").RopesFile) == ropesBefore, "擋下的全部零寫入（ropes.json 逐字不變）");

            // 刪固定點 ⇒ 綁著它的繩被報出來（全庫掃）＋ 之後每次觀測都列 broken
            var un = Sub("mast", "unanchor", ("name", "top"));
            Check(un.ExitCode == 0 && V(un, "ropes_now_broken") == "2", "🔴 刪桅的 top ⇒ 全庫掃到 2 條綁定壞了（stay、port 在船上）：" + V(un, "ropes_now_broken") + " " + SculptRoom.All(un));
            var vBroken = Run("ship", "view", ("out", Out("v9.png")));
            Check(vBroken.ExitCode == 0 && V(vBroken, "ropes_broken") == "2" && V(vBroken, "ropes_drawn") == "0" && SculptRoom.All(vBroken).Contains("🔴"),
                  "觀測照樣出圖、但大聲列出 2 條 broken（⛔ 不靜默消失）：" + V(vBroken, "ropes_broken"));
            Check(Sub("ship", "ropes").ExitCode == 0 && Rope("ship", "stay")!.State == SCP_SculptRopeReading.Broken && Rope("ship", "stay")!.Reason.Contains("top"), "sub=ropes 的讀數也是 broken、原因寫出固定點名");
            Check(Sub("mast", "anchor", ("name", "masthead"), ("at", "0,2,9")).ExitCode == 0 && Rope("ship", "stay")!.State == SCP_SculptRopeReading.Broken, "改名（新名字加回）⇒ 綁舊名字的繩仍是 broken");
            Check(Sub("mast", "anchor", ("name", "top"), ("at", "0,2,9")).ExitCode == 0 && Rope("ship", "stay")!.State == SCP_SculptRopeReading.Ok, "舊名字加回 ⇒ 又接上");

            // ⑤ 一格 voxel 都不動
            string ownShipAfter = string.Join(";", store.Engine(store.Load("ship"), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));
            Check(ownShipAfter == ownShip && SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count == evShip && SCP_SculptStore.ListEvents(store.SpacePaths("mast")).Count == evMast,
                  "⛔ 全部固定點／繩操作之後，船與桅自己的格、events 數都沒變");
            // 被關聯的作品自己的繩：照那條關聯換座標、跟著那條關聯開關；那一層沒畫 ⇒ 照列成 hidden（hide_ropes 點得到它）
            Check(Sub("mast", "rope", ("name", "shroud"), ("from", "top"), ("to", "2,2,0")).ExitCode == 0, "桅自己的繩 shroud top → 2,2,0");
            var sh = Rope("ship", "main/shroud");
            Check(sh != null && sh.State == SCP_SculptRopeReading.Ok && Near(sh.From, 12.5, 6.5, 10.5) && Near(sh.To, 14.5, 6.5, 1.5) && Rope("ship", "fore/shroud") != null,
                  "桅的繩在船上：main/shroud 照 at 換（12.5,6.5,10.5）→（14.5,6.5,1.5）、fore 那根也有一條：" + Fmt(sh?.From) + "→" + Fmt(sh?.To));
            var vChild = Run("ship", "view", ("out", Out("v10.png")), ("links", "none"), ("hide_ropes", "main/shroud"));
            Check(vChild.ExitCode == 0 && V(vChild, "ropes_hidden") == "4", "links=none ⇒ 4 條都 hidden 且照列；hide_ropes 點那一層沒畫的繩也認得（不報「沒有這條」）：" + V(vChild, "ropes_hidden") + " " + SculptRoom.All(vChild));
            // 觀測頁的開關（純函數）：全在預設 ⇒ 不送；取消勾 ⇒ hide_ropes；作品裡設為關的不送；總開關關 ⇒ ropes=0
            var pageRows = new List<SculptureViewerPage.RopeRow> { new("a", true), new("b", true), new("c", false) };
            Check(SculptureViewerPage.RopeToggleArgs(true, pageRows, _ => true) == ("", "")
                  && SculptureViewerPage.RopeToggleArgs(true, pageRows, x => x != "b") == ("", "b")
                  && SculptureViewerPage.RopeToggleArgs(true, pageRows, x => x != "c") == ("", "")
                  && SculptureViewerPage.RopeToggleArgs(false, pageRows, _ => true) == ("0", ""), "觀測頁繩索開關 → ropes=／hide_ropes=");
            Check(Sub("ship", "unrope", ("name", "port")).ExitCode == 0 && Ropes("ship").Count == 3, "unrope port ⇒ 剩 stay＋兩條 shroud：" + Ropes("ship").Count);

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    static CheckRow SculptRopeGpuCleanRoom()
    {
        const string name = "雕刻繩索 GPU：有繩的圖紅色像素明顯、沒繩幾乎沒有；繩改了網格重建；只有繩沒有 voxel 也框得到（淨室，TASK-0502）";
        var failures = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++) vox.Add(new SCP_SculptVoxel(x, y, 0, 146));
            double[] pts = SCP_SculptRopes.Solve(new[] { 2.5, 10.5, 8.5 }, new[] { 17.5, 10.5, 8.5 }, 1.1, out _, out _, out _, out _);
            var rope = new SCP_SculptRopeGeom { Path = "t", Points = pts, Radius = 0.8, R = 255, G = 0, B = 0 };
            var p = new SCP_SculptRenderParams { Width = 384, Height = 384, Skybox = SCP_SculptRenderParams.SkyboxNone };
            if (!r.TryRender(vox, p, out byte[] aNone, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "沒繩那張就失敗了：" + err, CheckResult.Fail);
            int Red(byte[] img) { int n = 0; for (int k = 0; k < img.Length; k += 4) if (img[k] > 120 && img[k + 1] < 60 && img[k + 2] < 60) n++; return n; }
            var pr = p.Clone(); pr.Ropes = new List<SCP_SculptRopeGeom> { rope };
            Check(r.TryRender(vox, pr, out byte[] aRope, out err), "有繩那張失敗：" + err);
            var rd = r.LastReading;
            int redNone = Red(aNone), redRope = Red(aRope);
            Check(redRope > 200 && redNone < 5, "紅色像素：有繩 " + redRope + "、沒繩 " + redNone);
            Check(rd.MeshRebuilt && rd.Ropes == 1 && rd.RopeTriangles > 0, "繩加進來 ⇒ 網格重建、讀數有 1 條繩：" + rd);
            var pr2 = pr.Clone(); pr2.Ropes = new List<SCP_SculptRopeGeom> { new SCP_SculptRopeGeom { Path = "t", Points = pts, Radius = 0.8, R = 0, G = 0, B = 255 } };
            Check(r.TryRender(vox, pr2, out byte[] aBlue, out err) && r.LastReading.MeshRebuilt && Red(aBlue) < 5, "同一份 voxel、只換繩色 ⇒ 網格重建、紅色消失（⛔ 不拿舊網格）");
            Check(r.TryRender(new List<SCP_SculptVoxel>(), pr, out byte[] aOnly, out err) && Red(aOnly) > 200, "只有繩沒有 voxel ⇒ 照樣框得到（紅色 " + Red(aOnly) + "）：" + err);
            return new CheckRow(name, failures.Count == 0 ? "紅色像素 有繩 " + redRope + "／沒繩 " + redNone + "；" + rd : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
    }
}
