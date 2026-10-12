// 淨室驗收（TASK-0507）：雕刻的外部模型 ——
// ① obj 解析：方塊（四邊形扇形切成三角形、負索引）讀得到；越界索引／沒有面／壞數字 ⇒ InvalidDataException 且說第幾行。
// ② 座標：ToWork 對拍手算（pivot → scale → Rx → Ry → Rz → at；順序錯了算出來會不一樣）；axes yup／zup／sculpt 三種換軸。
// ③ 幾何：方塊（有 vn、沒 vn 各一份）換到世界後，每個三角形的繞序與法線都朝外（拿「三角形重心 − 方塊中心」判，不借實作的判法）——
//    含行列式 < 0 的兩種（mirror=x、axes=sculpt）。
// ④ CLI：file 匯入（檔收進作品、讀數）、只改變換不用 overwrite、換來源沒給 overwrite ⇒ 擋；🔴 壞 obj ⇒ exit 2、models.json 與 models/ 一個都沒寫。
// ⑤ 作品當模型：比例自動（0.02 ÷ 0.1 ＝ 0.2）、快取 produced → reused → 來源改了 ⇒ produced（鍵換了）；外框 ＝ 手算；🔴 成環（直接、經過關聯）⇒ 擋、零寫入。
//    反方向也擋（TASK-0508）：加**關聯**讓「關聯＋模型」繞成環（直接、隔層）⇒ exit 2、印出環的路徑、links.json 零寫入；不成環的關聯照常加。
// ⑥ 繩綁模型：固定點名／作品格座標／obj 檔案座標三種端點 ＝ 手算；轉模型 ⇒ 端點跟著到手算的位置；拿掉模型 ⇒ 全庫掃到、觀測列 broken。
// ⑦ 觀測：渲染器收到的模型數 ＝ ok 數；models=0／hide_models／links=none（關聯裡的模型跟著不畫）；hide_models 打錯 ⇒ 擋。
// ⑧ 匯出 obj：o model_n、索引在範圍內、繞序跟 vn 同向；list ⇒ models_exported=0。
// ⑨ 🔴 反向對照：沒有模型的作品 —— 加一個模型再拿掉，export obj 逐位元組相同；全部操作前後作品自己的格與 events 數不變。
// ⑩ GPU（另一列，沒有 GPU ⇒ Skipped）：紅色模型畫得出來、只有模型沒有 voxel 也框得到；同一份清單第二幀不重傳。
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
    /// <summary>單位方塊 0..1（y-up 的檔案座標；每面從外面看逆時針）。<paramref name="iNormals"/> ⇒ 帶 vn；<paramref name="iMtl"/> ⇒ usemtl red。</summary>
    static string CubeObj(bool iNormals, string iMtl = "")
    {
        var sb = new System.Text.StringBuilder("# cube\n");
        if (iMtl.Length > 0) sb.Append("mtllib ").Append(iMtl).Append('\n');
        foreach (var v in new[] { "0 0 0", "1 0 0", "1 1 0", "0 1 0", "0 0 1", "1 0 1", "1 1 1", "0 1 1" }) sb.Append("v ").Append(v).Append('\n');
        string[] faces = { "1 4 3 2", "5 6 7 8", "1 2 6 5", "4 8 7 3", "1 5 8 4", "2 3 7 6" };
        string[] normals = { "0 0 -1", "0 0 1", "0 -1 0", "0 1 0", "-1 0 0", "1 0 0" };
        if (iNormals) foreach (var n in normals) sb.Append("vn ").Append(n).Append('\n');
        if (iMtl.Length > 0) sb.Append("usemtl red\n");
        for (int f = 0; f < 6; f++)
        {
            sb.Append('f');
            foreach (string i in faces[f].Split(' ')) sb.Append(' ').Append(iNormals ? i + "//" + (f + 1) : i);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>每個三角形繞序與法線都朝外的個數（外 ＝ 重心 − 中心）：回 (反了的繞序, 反了的法線, 三角形數)。</summary>
    static (int BadWind, int BadNormal, int Tris) Outward(SCP_SculptModelGeom g, double[] iCenter)
    {
        int bw = 0, bn = 0, n = g.Triangles.Length / 3;
        for (int t = 0; t < n; t++)
        {
            int a = g.Triangles[t * 3], b = g.Triangles[t * 3 + 1], c = g.Triangles[t * 3 + 2];
            double[] P(int i) => new[] { g.Points[i * 3], g.Points[i * 3 + 1], g.Points[i * 3 + 2] };
            double[] pa = P(a), pb = P(b), pc = P(c);
            double cx = (pa[0] + pb[0] + pc[0]) / 3 - iCenter[0], cy = (pa[1] + pb[1] + pc[1]) / 3 - iCenter[1], cz = (pa[2] + pb[2] + pc[2]) / 3 - iCenter[2];
            double ux = pb[0] - pa[0], uy = pb[1] - pa[1], uz = pb[2] - pa[2], wx = pc[0] - pa[0], wy = pc[1] - pa[1], wz = pc[2] - pa[2];
            double nx = uy * wz - uz * wy, ny = uz * wx - ux * wz, nz = ux * wy - uy * wx;
            if (nx * cx + ny * cy + nz * cz <= 0) bw++;
            if (g.Normals[a * 3] * cx + g.Normals[a * 3 + 1] * cy + g.Normals[a * 3 + 2] * cz <= 0) bn++;
        }
        return (bw, bn, n);
    }

    static CheckRow SculptureModelsCleanRoom()
    {
        const string name = "雕刻外部模型：obj 解析與壞檔行號／座標對拍手算（旋轉順序、三種換軸）／方塊繞序與法線朝外（含鏡像）／file 匯入、壞檔零寫入／作品當模型：比例自動、快取 produced→reused→來源改了重出、成環擋下／繩綁模型三種端點＝手算、轉模型跟著動、拿掉列 broken／觀測開關／obj 匯出索引與繞序／🔴 沒有模型的作品 export 逐位元不變（淨室，TASK-0507）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            bool Near(double[]? p, double x, double y, double z, double eps = 1e-9) => p != null && Math.Abs(p[0] - x) < eps && Math.Abs(p[1] - y) < eps && Math.Abs(p[2] - z) < eps;
            string Fmt(double[]? p) => SCP_SculptRopes.Fmt(p);

            using var room = new SculptRoom();
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');

            // ── ① obj 解析 ─────────────────────────────────────────────────
            string cubeN = Out("cube_n.obj"), cube0 = Out("cube_0.obj");
            File.WriteAllText(cubeN, CubeObj(true, "cube.mtl"));
            File.WriteAllText(Out("cube.mtl"), "newmtl red\nKd 1 0 0\n");
            File.WriteAllText(cube0, CubeObj(false));
            var mN = SCP_SculptModels.ParseObj(cubeN);
            Check(mN.VertexCount == 8 && mN.TriangleCount == 12 && mN.Normals.Length == 18 && mN.MaterialColors.Count == 1 && mN.MaterialColors[0]![0] == 255, "方塊：8 頂點、6 個四邊形 ⇒ 12 三角形、6 vn、材質 red 的 Kd：" + mN.VertexCount + "/" + mN.TriangleCount);
            File.WriteAllText(Out("neg.obj"), "v 0 0 0\nv 1 0 0\nv 0 1 0\nf -3 -2 -1\n");
            Check(SCP_SculptModels.ParseObj(Out("neg.obj")).CornerPos.SequenceEqual(new[] { 0, 1, 2 }), "負索引（相對）讀得到");
            string? BadMsg(string body)
            {
                string f = Out("bad.obj");
                File.WriteAllText(f, body);
                try { SCP_SculptModels.ParseObj(f); return null; } catch (InvalidDataException e) { return e.Message; }
            }
            string? bIdx = BadMsg("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 4\n"), bNoFace = BadMsg("v 0 0 0\n"), bNum = BadMsg("v 0 x 0\n");
            Check(bIdx != null && bIdx.Contains("第 4 行") && bIdx.Contains("越界"), "🔴 越界索引 ⇒ 擋、說第 4 行：" + bIdx);
            Check(bNoFace != null && bNum != null && bNum.Contains("第 1 行"), "🔴 沒有面／壞數字 ⇒ 擋：" + bNoFace + "｜" + bNum);

            // ── ② 座標（手算）───────────────────────────────────────────────
            var mt = new SCP_SculptModel { pivot = new List<double> { 1, 0, 0 }, scale = new List<double> { 2, 2, 2 }, rotate = new List<double> { 0, 0, 90 }, at = new List<double> { 10, 10, 10 } };
            // (2,0,0) − pivot ＝ (1,0,0) ⇒ ×2 ＝ (2,0,0) ⇒ 繞 Z 90 ＝ (0,2,0) ⇒ ＋at ＝ (10,12,10)
            Check(Near(SCP_SculptModels.ToWork(mt, new[] { 2.0, 0, 0 }), 10, 12, 10, 1e-12), "ToWork 手算 (10,12,10)：" + Fmt(SCP_SculptModels.ToWork(mt, new[] { 2.0, 0, 0 })));
            // 先 X 再 Z：(0,1,0) 繞 X 90 ⇒ (0,0,1)，再繞 Z 90 不動 ⇒ (0,0,1)。（順序反了會是 (−1,0,0)）
            var mr = new SCP_SculptModel { rotate = new List<double> { 90, 0, 90 } };
            Check(Near(SCP_SculptModels.ToWork(mr, new[] { 0.0, 1, 0 }), 0, 0, 1, 1e-12), "旋轉順序：先 X、再 Y、再 Z（繞作品的軸）⇒ (0,0,1)：" + Fmt(SCP_SculptModels.ToWork(mr, new[] { 0.0, 1, 0 })));
            double[] y = SCP_SculptModels.AxesMatrix("yup"), s = SCP_SculptModels.AxesMatrix("sculpt");
            Check(Near(SCP_SculptModels.Mul3(y, new[] { 1.0, 2, 3 }), 1, -3, 2) && Near(SCP_SculptModels.Mul3(s, new[] { 1.0, 2, 3 }), 1, 3, 2)
                  && Near(SCP_SculptModels.Mul3(SCP_SculptModels.AxesMatrix("zup"), new[] { 1.0, 2, 3 }), 1, 2, 3), "換軸：yup (x,−z,y)、sculpt (x,z,y)、zup 原樣");

            // ── ③ 方塊繞序與法線朝外（含行列式 < 0）────────────────────────────
            foreach (var (label, mesh, axes, mirror) in new[] { ("vn yup", mN, "yup", ""), ("無 vn yup", SCP_SculptModels.ParseObj(cube0), "yup", ""),
                                                                  ("vn mirror=x", mN, "yup", "x"), ("無 vn sculpt", SCP_SculptModels.ParseObj(cube0), "sculpt", ""), ("vn sculpt＋mirror=z", mN, "sculpt", "z") })
            {
                var mm = new SCP_SculptModel { axes = axes, mirror = mirror, rotate = new List<double> { 10, 20, 30 }, scale = new List<double> { 3, 2, 5 }, at = new List<double> { 7, 8, 9 } };
                double[] toRoot = SCP_SculptModels.AffineOf(p => SCP_SculptModels.ToWork(mm, p));
                double[] raw2root = SCP_SculptModels.Compose(toRoot, SCP_SculptModels.AxesMatrix(axes));
                var g = SCP_SculptModels.BuildGeom("cube", mesh, raw2root, "");
                double[] center = SCP_SculptModels.Apply(raw2root, new[] { 0.5, 0.5, 0.5 });
                var (bw, bn, tris) = Outward(g, center);
                Check(tris == 12 && bw == 0 && bn == 0, "方塊（" + label + "）換到世界後繞序與法線都朝外：繞序反 " + bw + "、法線反 " + bn + "／" + tris);
            }

            // ── 淨室作品 ─────────────────────────────────────────────────────
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            var probe = new SculptProbeRenderer();
            SCP_SculptRenderers.Register(probe);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "模型 " + id), ("commission_ref", "user:model:" + id), ("account", "acct")).ExitCode == 0;
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
            SCP_CmdResult Dot(string id, int x, int yy, int z, int color)
                => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", yy.ToString()), ("y2", yy.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            SCP_SculptModelSet Models(string id, string links = "", string hide = "") => SCP_SculptModels.Resolve(store, id, SCP_SculptLinks.Tree(store, id), SCP_SculptLinkSelection.Parse(links, hide), new SCP_SculptModelSelection(), true);
            SCP_SculptRopeReading? Rope(string id, string path)
            {
                var tree = SCP_SculptLinks.Tree(store, id);
                var ms = SCP_SculptModels.Resolve(store, id, tree, new SCP_SculptLinkSelection(), new SCP_SculptModelSelection(), false);
                return SCP_SculptRopes.Resolve(store, id, tree, new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection(), null, null, ms).Find(r => r.Path == path);
            }

            // host 40×40×40（0.1 m）；part 10×10×10（0.02 m）一格在 (0,0,0)、固定點 tip (9,0,0)
            Check(Create("host", "40,40,40") && Dot("host", 0, 0, 0, 60).ExitCode == 0 && Sub("host", "update", ("meters_per_voxel", "0.1")).ExitCode == 0, "host");
            Check(Create("part", "10,10,10") && Dot("part", 0, 0, 0, 224).ExitCode == 0 && Sub("part", "update", ("meters_per_voxel", "0.02")).ExitCode == 0
                  && Sub("part", "anchor", ("name", "tip"), ("at", "9,0,0")).ExitCode == 0, "part（0.02 m／格）");
            int evHost = SCP_SculptStore.ListEvents(store.SpacePaths("host")).Count, evPart = SCP_SculptStore.ListEvents(store.SpacePaths("part")).Count;
            string ownHost = string.Join(";", store.Engine(store.Load("host"), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));

            // ⑨ 反向對照的「之前」：沒有模型的作品 export obj
            string obj0 = Out("host_before.obj");
            Check(Run("host", "export", ("format", "obj"), ("out", obj0)).ExitCode == 0, "沒有模型時 export obj");

            // ── ④ file 匯入 ─────────────────────────────────────────────────
            var add = Sub("host", "model", ("name", "box"), ("file", cubeN), ("at", "20,20,10"), ("scale", "4"));
            Check(add.ExitCode == 0 && V(add, "model_triangles") == "12", "匯入方塊 obj：12 三角形：" + SculptRoom.All(add));
            string boxDir = Path.Combine(store.Folder("host"), "models", "box");
            Check(File.Exists(Path.Combine(boxDir, "cube_n.obj")) && File.Exists(Path.Combine(boxDir, "cube.mtl")), "obj 與它的 mtl 都收進作品 models/box/");
            var mBox = Models("host").Models.Find(m => m.Path == "box");
            // yup：(x,y,z) ⇒ (x,−z,y)；方塊 0..1 ⇒ 模型座標 x 0..1、y −1..0、z 0..1 ⇒ ×4＋(20,20,10) ⇒ x 20..24、y 16..20、z 10..14
            Check(mBox != null && Near(mBox.Min, 20, 16, 10, 1e-9) && Near(mBox.Max, 24, 20, 14, 1e-9) && mBox.Geom != null && mBox.Geom.Colors[0] == 255 && mBox.Geom.Colors[1] == 0,
                  "外框 ＝ 手算 (20,16,10)–(24,20,14)、顏色取 mtl 的紅：" + Fmt(mBox?.Min) + "–" + Fmt(mBox?.Max));
            var rot = Sub("host", "model", ("name", "box"), ("rotate", "0,0,90"));
            Check(rot.ExitCode == 0 && V(rot, "model_triangles") == "12", "只改變換（rotate）不用 overwrite、三角形數照讀：" + V(rot, "model_triangles"));
            Check(Sub("host", "model", ("name", "box"), ("file", cube0)).ExitCode == 2, "🔴 換來源沒給 overwrite ⇒ 擋");
            string modelsBefore = File.ReadAllText(store.SpacePaths("host").ModelsFile);
            File.WriteAllText(Out("broken.obj"), "v 0 0 0\nf 1 2 3\n");
            var bad = Sub("host", "model", ("name", "junk"), ("file", Out("broken.obj")));
            Check(bad.ExitCode == 2 && File.ReadAllText(store.SpacePaths("host").ModelsFile) == modelsBefore && !Directory.Exists(Path.Combine(store.Folder("host"), "models", "junk")),
                  "🔴 壞 obj ⇒ exit 2、models.json 逐字不變、models/junk 沒建：" + SculptRoom.All(bad).Split('\n')[0]);
            Check(Sub("host", "model", ("name", "rel"), ("file", "cube_n.obj")).ExitCode == 2 && Sub("host", "model", ("name", "box"), ("scale", "0")).ExitCode == 2
                  && Sub("host", "model", ("name", "box"), ("axes", "xup")).ExitCode == 2, "🔴 相對路徑／scale 0／axes 不認得 ⇒ 擋");

            // ── ⑤ 作品當模型 ───────────────────────────────────────────────
            var aw = Sub("host", "model", ("name", "p1"), ("source_work", "part"), ("at", "5,5,5"));
            Check(aw.ExitCode == 0 && V(aw, "model_cache") == "produced", "作品當模型：第一次 ⇒ 快取 produced：" + SculptRoom.All(aw));
            var mp = Models("host").Models.Find(m => m.Path == "p1");
            Check(mp != null && mp.Model.scale.All(k => Math.Abs(k - 0.2) < 1e-12), "比例自動 0.02 ÷ 0.1 ＝ 0.2：" + string.Join(",", mp?.Model.scale ?? new List<double>()));
            // part 那一格 (0..1)³ ⇒ ×0.2＋(5,5,5) ⇒ (5,5,5)–(5.2,5.2,5.2)
            Check(mp != null && Near(mp.Min, 5, 5, 5, 1e-6) && Near(mp.Max, 5.2, 5.2, 5.2, 1e-6) && !mp.CacheProduced, "外框 ＝ 手算 (5,5,5)–(5.2,5.2,5.2)、第二次讀 ⇒ reused：" + Fmt(mp?.Min) + "–" + Fmt(mp?.Max));
            if (mp?.Geom != null)
            {
                var (bw, bn, tris) = Outward(mp.Geom, new[] { 5.1, 5.1, 5.1 });
                Check(tris == 12 && bw == 0 && bn == 0, "作品匯出 → 快取 → 讀回（sculpt 換軸、行列式 < 0）的方塊繞序與法線朝外：反 " + bw + "／" + bn + "／" + tris);
            }
            string key1 = mp?.CacheKey ?? "";
            Check(Dot("part", 1, 0, 0, 224).ExitCode == 0, "來源 part 加一格");
            // 共用層自己不會產生快取（沒有產生器 ⇒ 明說 broken，⛔ 不假裝）；CLI 的 sub=models 帶產生器 ⇒ 重出
            var stale = Models("host").Models.Find(m => m.Path == "p1");
            Check(stale != null && stale.State == SCP_SculptModelReading.Broken && stale.Reason.Contains("不會產生"), "沒有產生器 ⇒ 快取缺著就列 broken 並說原因：" + stale?.Reason);
            var lm = Sub("host", "models");
            var mp2 = Models("host").Models.Find(m => m.Path == "p1");
            Check(lm.ExitCode == 0 && V(lm, "model_caches_produced") == "1" && mp2 != null && mp2.CacheKey != key1 && Near(mp2.Max, 5.4, 5.2, 5.2, 1e-6),
                  "來源改了 ⇒ sub=models 重出 1 份、鍵換了、外框跟著長（5.4）：" + V(lm, "model_caches_produced") + " " + Fmt(mp2?.Max));
            Check(Directory.GetFiles(Path.Combine(store.ModelCacheRoot, "part"), "*.obj").Length == 1, "同一件作品的舊快取清掉（只剩一份）");
            // 🔴 成環：part 拿 host 當模型（host 已經用 part）⇒ 擋；經過關聯：mid 關聯 part，host 關聯 mid…再讓 part 用 host
            string partModels = File.Exists(store.SpacePaths("part").ModelsFile) ? File.ReadAllText(store.SpacePaths("part").ModelsFile) : "";
            Check(Sub("part", "model", ("name", "loop"), ("source_work", "host")).ExitCode == 2 && Sub("host", "model", ("name", "self"), ("source_work", "host")).ExitCode == 2,
                  "🔴 成環（part ↔ host、拿自己）⇒ 擋");
            Check(Create("mid", "10,10,10") && Dot("mid", 0, 0, 0, 30).ExitCode == 0 && Create("top", "10,10,10") && Dot("top", 0, 0, 0, 31).ExitCode == 0
                  && Sub("top", "link", ("name", "m"), ("source_work", "mid"), ("at", "0,0,0")).ExitCode == 0 && Sub("mid", "model", ("name", "t"), ("source_work", "part")).ExitCode == 0, "top 關聯 mid、mid 用 part 當模型");
            var loop2 = Sub("part", "model", ("name", "loop"), ("source_work", "top"));
            Check(loop2.ExitCode == 2 && SculptRoom.All(loop2).Contains("成環") && (File.Exists(store.SpacePaths("part").ModelsFile) ? File.ReadAllText(store.SpacePaths("part").ModelsFile) : "") == partModels,
                  "🔴 經過關聯與模型的環（part → top → m(mid) → part）⇒ 擋、零寫入：" + SculptRoom.All(loop2).Split('\n')[0]);
            // 🔴 反方向（TASK-0508）：同一個環改由加關聯來閉合 —— 都選放得進的（10³ 進 10³），⛔ 不讓「放不下」先擋掉而測不到環
            string LinksText(string id) => File.Exists(store.SpacePaths(id).LinksFile) ? File.ReadAllText(store.SpacePaths(id).LinksFile) : "";
            string partLinks = LinksText("part");
            string Said(SCP_CmdResult r) => SculptRoom.All(r).Split('\n').FirstOrDefault(l => l.Contains("成環") || l.Contains("關聯 `")) ?? "";
            var lk1 = Sub("part", "link", ("name", "back"), ("source_work", "mid"), ("at", "0,0,0"));
            Check(lk1.ExitCode == 2 && Said(lk1).Contains("part → mid → part") && LinksText("part") == partLinks,
                  "🔴 加關聯閉合「模型」的環（part → link mid → model part）⇒ 擋、印路徑、links.json 零寫入：exit " + lk1.ExitCode + " " + Said(lk1));
            var lk2 = Sub("part", "link", ("name", "back2"), ("source_work", "top"), ("at", "0,0,0"));
            Check(lk2.ExitCode == 2 && Said(lk2).Contains("part → top → mid → part") && LinksText("part") == partLinks,
                  "🔴 隔層（part → link top → link mid → model part）⇒ 擋、印路徑、零寫入：exit " + lk2.ExitCode + " " + Said(lk2));
            readings.Add("加關聯成環 exit " + lk1.ExitCode + "／" + lk2.ExitCode);
            Check(Create("leaf", "10,10,10") && Sub("leaf", "link", ("name", "m"), ("source_work", "mid"), ("at", "0,0,0")).ExitCode == 0 && LinksText("leaf").Contains("\"mid\""),
                  "正向對照：不成環的關聯（leaf → mid → model part）照常加得進去");

            // ── ⑥ 繩綁模型 ─────────────────────────────────────────────────
            // tip 格心 (9.5,0.5,0.5) ⇒ ×0.2＋(5,5,5) ⇒ (6.9,5.1,5.1)
            Check(Sub("host", "rope", ("name", "r1"), ("from", "model:p1/tip"), ("to", "30.5,30.5,0.5")).ExitCode == 0, "繩 r1 綁 model:p1/tip");
            Check(Near(Rope("host", "r1")?.From, 6.9, 5.1, 5.1, 1e-9), "固定點名端點 ＝ 手算 (6.9,5.1,5.1)：" + Fmt(Rope("host", "r1")?.From));
            Check(Sub("host", "rope", ("name", "r2"), ("from", "model:p1/4,4,4"), ("to", "30,30,0")).ExitCode == 0
                  && Near(Rope("host", "r2")?.From, 5.9, 5.9, 5.9, 1e-9), "作品格座標端點（整數 ⇒ 格心 4.5）＝ (5.9,5.9,5.9)：" + Fmt(Rope("host", "r2")?.From));
            // box 現在 rotate 0,0,90、scale 4、at (20,20,10)；檔案座標 (1,0,0) ⇒ yup (1,0,0) ⇒ ×4 (4,0,0) ⇒ 繞 Z 90 (0,4,0) ⇒ (20,24,10)
            Check(Sub("host", "rope", ("name", "r3"), ("from", "model:box/1,0,0"), ("to", "30,30,0")).ExitCode == 0
                  && Near(Rope("host", "r3")?.From, 20, 24, 10, 1e-9), "obj 檔案座標端點 ＝ 手算 (20,24,10)：" + Fmt(Rope("host", "r3")?.From));
            Check(Sub("host", "rope", ("name", "bad"), ("from", "model:box/tip"), ("to", "1,1,1")).ExitCode == 2 && Sub("host", "rope", ("name", "bad"), ("from", "model:nope/1,1,1"), ("to", "1,1,1")).ExitCode == 2,
                  "🔴 obj 模型用固定點名／模型名打錯 ⇒ 擋");
            // 轉＋搬模型 ⇒ 端點跟著：rotate 0,0,90、at (10,10,10)：tip 偏移 (1.9,0.1,0.1) ⇒ 繞 Z 90 (−0.1,1.9,0.1) ⇒ (9.9,11.9,10.1)
            Check(Sub("host", "model", ("name", "p1"), ("rotate", "0,0,90"), ("at", "10,10,10")).ExitCode == 0 && Near(Rope("host", "r1")?.From, 9.9, 11.9, 10.1, 1e-9),
                  "轉 90°＋搬 ⇒ 繩端跟著到手算的 (9.9,11.9,10.1)：" + Fmt(Rope("host", "r1")?.From));
            readings.Add("繩端 " + Fmt(Rope("host", "r1")?.From));

            // ── ⑦ 觀測 ─────────────────────────────────────────────────────
            var v1 = Run("host", "view", ("out", Out("v1.png")));
            Check(v1.ExitCode == 0 && V(v1, "models_drawn") == "2" && probe.Last != null && probe.Last.Models.Count == 2 && V(v1, "visible_voxels") == "1",
                  "view：畫 2 個模型、渲染器收到 2 個、可見 voxel 仍是 1：" + V(v1, "models_drawn") + "/" + probe.Last?.Models.Count + "/" + V(v1, "visible_voxels"));
            var v2 = Run("host", "view", ("out", Out("v2.png")), ("models", "0"));
            var v3 = Run("host", "view", ("out", Out("v3.png")), ("hide_models", "box"));
            Check(V(v2, "models_drawn") == "0" && V(v2, "ropes_drawn") == "3" && V(v3, "models_drawn") == "1" && probe.Last!.Models.Count == 1 && probe.Last.Models[0].Path == "p1",
                  "models=0 ⇒ 0 個（綁在上面的繩照畫 3 條）；hide_models=box ⇒ 只畫 p1");
            Check(Run("host", "view", ("out", Out("v4.png")), ("hide_models", "nope")).ExitCode == 2 && Run("host", "view", ("out", Out("v5.png")), ("models", "2")).ExitCode == 2, "🔴 hide_models 打錯／models=2 ⇒ 擋");
            // 關聯裡的模型：top 關聯 mid、mid 有模型 t ⇒ top 看得到 m/t；links=none ⇒ hidden
            var vt = Run("top", "view", ("out", Out("v6.png")));
            var vtn = Run("top", "view", ("out", Out("v7.png")), ("links", "none"));
            Check(V(vt, "models_drawn") == "1" && V(vtn, "models_drawn") == "0" && V(vtn, "models_hidden") == "1", "關聯裡的模型 m/t 畫；links=none ⇒ 跟著不畫（hidden 1）");

            // ── ⑧ 匯出 obj ─────────────────────────────────────────────────
            string obj = Out("host.obj");
            var ex = Run("host", "export", ("format", "obj"), ("out", obj));
            Check(ex.ExitCode == 0 && V(ex, "models_exported") == "2", "export obj：2 個模型");
            if (File.Exists(obj))
            {
                string[] lines = File.ReadAllLines(obj);
                var vs = new List<double[]>(); var vns = new List<double[]>();
                int badIdx = 0, badWind = 0, modelFaces = 0; bool inModel = false;
                foreach (string l in lines)
                {
                    string[] t = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (t.Length == 0) continue;
                    double D(string x) => double.Parse(x, CultureInfo.InvariantCulture);
                    if (t[0] == "v") vs.Add(new[] { D(t[1]), D(t[2]), D(t[3]) });
                    else if (t[0] == "vn") vns.Add(new[] { D(t[1]), D(t[2]), D(t[3]) });
                    else if (t[0] == "o") inModel = t[1].StartsWith("model_");
                    else if (t[0] == "f")
                    {
                        var vi = new List<int>(); var ni = new List<int>();
                        foreach (string fv in t.Skip(1)) { var parts = fv.Split('/'); vi.Add(int.Parse(parts[0])); ni.Add(parts.Length > 2 ? int.Parse(parts[2]) : 0); }
                        if (vi.Any(i => i < 1 || i > vs.Count) || ni.Any(i => i < 0 || i > vns.Count)) { badIdx++; continue; }
                        if (!inModel) continue;
                        modelFaces++;
                        double[] p0 = vs[vi[0] - 1], p1 = vs[vi[1] - 1], p2 = vs[vi[2] - 1], n0 = vns[ni[0] - 1];
                        double ux = p1[0] - p0[0], uy = p1[1] - p0[1], uz = p1[2] - p0[2], wx = p2[0] - p0[0], wy = p2[1] - p0[1], wz = p2[2] - p0[2];
                        double cx = uy * wz - uz * wy, cy = uz * wx - ux * wz, cz = ux * wy - uy * wx;
                        if (cx * n0[0] + cy * n0[1] + cz * n0[2] <= 0) badWind++;
                    }
                }
                Check(lines.Count(l => l.StartsWith("o model_")) == 2 && badIdx == 0 && modelFaces == 24 && badWind == 0,
                      "obj：兩個 o model_n、索引都在範圍內、模型面 24 個、繞序跟 vn 同向：o " + lines.Count(l => l.StartsWith("o model_")) + "、越界 " + badIdx + "、面 " + modelFaces + "、反 " + badWind);
                readings.Add("obj 模型面 " + modelFaces);
            }
            else failures.Add("obj 沒寫出來");
            Check(Run("host", "export", ("format", "list"), ("out", Out("host.txt"))).ExitCode == 0, "list 照常");

            // ── ⑥ 拿掉模型 ⇒ 綁在上面的繩列 broken ───────────────────────────
            var un = Sub("host", "unmodel", ("name", "box"));
            Check(un.ExitCode == 0 && V(un, "ropes_now_broken") == "1" && !Directory.Exists(boxDir), "🔴 拿掉 box ⇒ 全庫掃到 1 條繩壞了、models/box 拿掉：" + V(un, "ropes_now_broken"));
            var vb = Run("host", "view", ("out", Out("v8.png")));
            Check(vb.ExitCode == 0 && V(vb, "ropes_broken") == "1" && SculptRoom.All(vb).Contains("🔴"), "觀測照樣出圖、大聲列出 broken 的繩");
            var unAnchor = Sub("part", "unanchor", ("name", "tip"));
            Check(unAnchor.ExitCode == 0 && V(unAnchor, "ropes_now_broken") == "2", "🔴 刪來源作品 part 的固定點 tip ⇒ 全庫掃到 host 上綁 model:p1/tip 的那條（＋box 那條）：" + V(unAnchor, "ropes_now_broken"));

            // ── ⑨ 反向對照：模型全拿掉、繩全拿掉 ⇒ export obj 逐位元同「之前」；作品的格與 events 數不變 ──
            foreach (string r in new[] { "r1", "r2", "r3" }) Sub("host", "unrope", ("name", r));
            Check(Sub("host", "unmodel", ("name", "p1")).ExitCode == 0, "拿掉 p1");
            string obj1 = Out("host_after.obj");
            Check(Run("host", "export", ("format", "obj"), ("out", obj1)).ExitCode == 0, "拿掉之後 export obj");
            // 檔名不同 ⇒ mtllib 那一行不同；比「拿掉 mtllib 行」之後的內容
            string Body(string f) => string.Join("\n", File.ReadAllLines(f).Where(l => !l.StartsWith("mtllib ")));
            Check(Body(obj0) == Body(obj1) && File.ReadAllText(Path.ChangeExtension(obj0, ".mtl")) == File.ReadAllText(Path.ChangeExtension(obj1, ".mtl")),
                  "🔴 加過模型再拿掉 ⇒ export obj（除了 mtllib 的檔名）與 mtl 逐位元組同「沒有模型時」");
            string ownHostAfter = string.Join(";", store.Engine(store.Load("host"), room.Data).LoadSpace().Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "," + v.Color));
            Check(ownHostAfter == ownHost && SCP_SculptStore.ListEvents(store.SpacePaths("host")).Count == evHost, "⛔ 全部模型操作之後，host 自己的格、events 數都沒變");
            Check(SCP_SculptStore.ListEvents(store.SpacePaths("part")).Count == evPart + 1, "part 的 events 只多了那一刀（加一格），快取產生沒寫任何事件");

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    static CheckRow SculptModelGpuCleanRoom()
    {
        const string name = "雕刻外部模型 GPU：紅色模型畫得出來、沒模型那張幾乎沒有紅；只有模型沒有 voxel 也框得到；同一份清單第二幀不重傳（淨室，TASK-0507）";
        var failures = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 20; x++) for (int y = 0; y < 20; y++) vox.Add(new SCP_SculptVoxel(x, y, 0, 146));
            string dir = Path.Combine(Path.GetTempPath(), "senate_model_gpu_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            string f = Path.Combine(dir, "cube.obj");
            File.WriteAllText(f, CubeObj(true));
            var mesh = SCP_SculptModels.ParseObj(f);
            Directory.Delete(dir, true);
            var mm = new SCP_SculptModel { scale = new List<double> { 8, 8, 8 }, at = new List<double> { 6, 14, 1 }, rotate = new List<double> { 0, 0, 30 } };
            double[] raw2root = SCP_SculptModels.Compose(SCP_SculptModels.AffineOf(p => SCP_SculptModels.ToWork(mm, p)), SCP_SculptModels.AxesMatrix("yup"));
            var geom = SCP_SculptModels.BuildGeom("cube", mesh, raw2root, "#ff0000");
            var p = new SCP_SculptRenderParams { Width = 384, Height = 384, Skybox = SCP_SculptRenderParams.SkyboxNone };
            if (!r.TryRender(vox, p, out byte[] aNone, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "沒模型那張就失敗了：" + err, CheckResult.Fail);
            int Red(byte[] img) { int n = 0; for (int k = 0; k < img.Length; k += 4) if (img[k] > 120 && img[k + 1] < 60 && img[k + 2] < 60) n++; return n; }
            var pm = p.Clone(); pm.Models = new List<SCP_SculptModelGeom> { geom };
            Check(r.TryRender(vox, pm, out byte[] aModel, out err), "有模型那張失敗：" + err);
            var rd = r.LastReading;
            int redNone = Red(aNone), redModel = Red(aModel);
            Check(redModel > 500 && redNone < 5, "紅色像素：有模型 " + redModel + "、沒模型 " + redNone);
            Check(rd.Models == 1 && rd.ModelTriangles == 12 && rd.ModelsUploaded && !rd.MeshRebuilt, "模型另一趟：1 個、12 三角形、這一幀上傳、voxel 網格沒重建：" + rd);
            Check(r.TryRender(vox, pm, out _, out err) && !r.LastReading.ModelsUploaded && r.LastReading.Models == 1, "同一份清單第二幀 ⇒ 不重傳：" + r.LastReading);
            Check(r.TryRender(new List<SCP_SculptVoxel>(), pm, out byte[] aOnly, out err) && Red(aOnly) > 500, "只有模型沒有 voxel ⇒ 照樣框得到（紅色 " + Red(aOnly) + "）：" + err);
            return new CheckRow(name, failures.Count == 0 ? "紅色像素 有模型 " + redModel + "／沒模型 " + redNone + "；" + rd : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
    }
}
