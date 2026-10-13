// 淨室驗收（TASK-0530）：帆的貼圖（像素畫；拿帆當旗）——
// ① 設定：texture=<png> ⇒ 複製進作品 textures/<帆名>.png（位元組相同）、sails.json 記相對路徑、schema 2；中文帆名 ⇒ sail_<雜湊>.png。
// ② 渲染端：2×2 圖（紅綠藍＋一格透明）⇒ 渲染器收到 3 份平色網格、順序＝顏色在圖上第一次出現的順序；每份的偶數格點 ＝ 布的格點（逐位元）⇒ 貼在模擬出來的布上；
//    透明那一格沒有任何網格；讀數「畫了幾面帆」仍是 1（⛔ 不把碎片數成帆）。
// ③ 反向對照：圖轉 180° ⇒ 角 0 那格變透明（布的角 0 不再是任何一份的頂點）、紅色搬到右下。
// ④ 三角帆：像素中心落在斜邊外的不畫（白那格）。
// ⑤ obj：一色一個材質（3 個）、sails_exported 數帆（1）、sail_meshes_exported 數網格（3）。
// ⑥ vxk：帆仍是一份（遊戲拿它跑布料物理），布本身（verts／tris）跟沒貼圖時逐位元相同，多 texture（w／h／rgba／uv：每頂點 2 個）。
// ⑦ 🔴 圖壞了 ⇒ 照底色畫、讀數印 ⚠ 與 sail_texture_broken（⛔ 不靜默）；壞參數（檔不在／太大／不是 PNG）⇒ exit 2、sails.json 一個位元組都沒動；
//    手改成 schema 1 卻帶 texture ⇒ 大聲讀不了。
// ⑧ texture=none ⇒ sails.json 回到加貼圖之前（逐位元組）、圖檔刪掉、畫出來跟沒貼圖時逐位元相同；unsail 也刪圖。
using System.Text;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureSailTextureCleanRoom()
    {
        const string name = "雕刻帆貼圖：texture= 複製進作品、schema 2、中文帆名用雜湊檔名／渲染端每色一份、格點＝布的格點、透明不畫、帆數仍是 1／轉 180° 角 0 換色／三角帆斜邊外不畫／obj 一色一材質／vxk 布不變多 uv／🔴 壞圖照底色畫並報、壞參數零寫入、schema 1 帶 texture 大聲擋／none 回到原本位元組（淨室，TASK-0530）";
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
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            string Png(string f, int w, int h, byte[] rgba) { string p = Out(f); File.WriteAllBytes(p, SCP_CanvasPng.EncodeRgbaRows(rgba, w, h)); return p; }
            var paths = store.SpacePaths("ship");
            byte[] SailsBytes() => File.ReadAllBytes(paths.SailsFile);
            SCP_SculptSailGeom Cloth()
            {
                var rig = SCP_SculptSails.Rig(store, "ship", SCP_SculptLinks.Tree(store, "ship"), SCP_SculptLinkSelection.Parse("", ""), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                              new SCP_SculptSailEnv { Wind = new double[3], MetersPerVoxel = 0.1 });
                rig.Simulate(1);
                return rig.Sails.Find(s => s.Path == "flag")!.Geom!;
            }
            // 一份網格的 (q, s) 格點 ＝ 布的 (i0 + s/2, j0 + q/2) 格點（只比偶數格點：nu ＝ nv ＝ 2×res×0.5 ＝ 12 ⇒ 每兩格碰到一次布的格線）
            bool OnCloth(SCP_SculptSailGeom g, SCP_SculptSailGeom cloth, int i0, int j0)
            {
                for (int q = 0; q <= 12; q += 2)
                    for (int s = 0; s <= 12; s += 2)
                    {
                        int a = q * 13 + s, b = (j0 + q / 2) * 13 + (i0 + s / 2);
                        for (int k = 0; k < 3; k++) if (Math.Abs(g.Points[a * 3 + k] - cloth.Points[b * 3 + k]) > 1e-9) return false;
                    }
                return true;
            }
            bool IsVertex(SCP_SculptSailGeom g, double[] p)
            {
                for (int a = 0; a < g.PointCount; a++)
                    if (Math.Abs(g.Points[a * 3] - p[0]) < 1e-9 && Math.Abs(g.Points[a * 3 + 1] - p[1]) < 1e-9 && Math.Abs(g.Points[a * 3 + 2] - p[2]) < 1e-9) return true;
                return false;
            }
            string Col(SCP_SculptSailGeom g) => g.R + "," + g.G + "," + g.B;
            var viewArgs = new[] { ("wind", "0,0,0"), ("sail_seconds", "1") };
            SCP_CmdResult View(string f) { var l = new List<(string, string)> { ("out", Out(f)) }; l.AddRange(viewArgs); return Run("ship", "view", l.ToArray()); }

            // ── 淨室作品：一面直立的旗 30×20（y=20 那一片）；角 0 ＝ 左上（h0）、角 1 ＝ 右上、角 2 ＝ 右下、角 3 ＝ 左下 ──
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "ship"), ("title", "旗"), ("size", "48,48,48"),
                           ("commission", "旗"), ("commission_ref", "user:sailtex"), ("account", "acct")).ExitCode == 0
                  && Sub("ship", "update", ("meters_per_voxel", "0.1")).ExitCode == 0
                  && Run("ship", "box", ("x1", "0"), ("x2", "0"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "60")).ExitCode == 0, "作品 ship");
            foreach (var (n, at) in new[] { ("h0", "5,20,30"), ("h1", "35,20,30"), ("f1", "35,20,10"), ("f0", "5,20,10") })
                Check(Sub("ship", "anchor", ("name", n), ("at", at)).ExitCode == 0, "固定點 " + n);
            Check(Sub("ship", "sail", ("name", "flag"), ("corners", "h0|h1|f1|f0"), ("bind", "0"), ("res", "12"), ("color", "#808080")).ExitCode == 0, "帆 flag（上緣釘住、res 12）");
            byte[] s0 = SailsBytes();
            Check(SCP_SculptSails.ReadSails(paths).schema == 1 && !Encoding.UTF8.GetString(s0).Contains("texture"), "沒貼圖：schema 1、沒有 texture 這個 key");
            var v0 = View("v0.png");
            Check(v0.ExitCode == 0 && probe.Last!.Sails.Count == 1 && Col(probe.Last.Sails[0]) == "128,128,128", "沒貼圖：渲染器收到 1 份、底色");
            double[] p0 = probe.Last!.Sails[0].Points;
            string vxk0 = Out("v0.vxk");
            Check(Run("ship", "export", ("format", "vxk"), ("include", "rig"), ("out", vxk0)).ExitCode == 0, "沒貼圖：vxk");
            SCP_JsonData sail0 = SCP_SculptVxk.Unpack(File.ReadAllBytes(vxk0)).Item1["rig"]["sails"][0];
            Check(!sail0.Contains("texture"), "沒貼圖：vxk 的帆沒有 texture");

            // ① 設定貼圖：2×2（紅 綠／藍 透明）
            byte[] rgba = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 0, 0, 0, 0 };
            string png = Png("flag.png", 2, 2, rgba);
            var set = Sub("ship", "sail", ("name", "flag"), ("texture", png));
            string dst = SCP_SculptSails.TextureFile(paths, "textures/flag.png");
            Check(set.ExitCode == 0 && File.Exists(dst) && File.ReadAllBytes(dst).SequenceEqual(File.ReadAllBytes(png)), "① texture= ⇒ 複製進作品 textures/flag.png（位元組相同）：" + SculptRoom.All(set));
            var f1 = SCP_SculptSails.ReadSails(paths);
            Check(f1.schema == SCP_SculptSails.SchemaWithTexture && f1.sails[0].texture == "textures/flag.png", "① sails.json schema 2、texture ＝ textures/flag.png：" + f1.schema + "／" + f1.sails[0].texture);
            Check(SculptRoom.All(set).Length >= 0 && set.Values.Exists(kv => kv.Key == "texture_file"), "① 回傳值有 texture_file");

            // ② 渲染端：3 份、順序、格點在布上、透明不畫、帆數仍是 1
            var v1 = View("v1.png");
            var cloth = Cloth();
            var got = probe.Last!.Sails;
            Check(v1.ExitCode == 0 && V(v1, "sails_drawn") == "1", "② 讀數：畫了 1 面帆（⛔ 不把碎片數成帆）：" + V(v1, "sails_drawn"));
            Check(got.Count == 3 && got.TrueForAll(g => g.Path == "flag") && got.ConvertAll(Col).SequenceEqual(new[] { "255,0,0", "0,255,0", "0,0,255" }),
                  "② 渲染器收到 3 份（紅、綠、藍 —— 圖上第一次出現的順序）：" + string.Join(" ", got.ConvertAll(Col)));
            if (got.Count == 3)
            {
                Check(got.TrueForAll(g => g.Triangles.Length / 3 == 288 && g.PointCount == 169), "② 每份 12×12 格 ⇒ 288 三角形、169 頂點：" + string.Join("/", got.ConvertAll(g => g.Triangles.Length / 3)));
                Check(OnCloth(got[0], cloth, 0, 0) && OnCloth(got[1], cloth, 6, 0) && OnCloth(got[2], cloth, 0, 6), "② 每份的偶數格點 ＝ 布的格點（紅＝左上、綠＝右上、藍＝左下；逐位元）⇒ 貼在模擬出來的布上");
                double[] br = { cloth.Points[(12 * 13 + 12) * 3], cloth.Points[(12 * 13 + 12) * 3 + 1], cloth.Points[(12 * 13 + 12) * 3 + 2] };
                Check(!got.Exists(g => IsVertex(g, br)), "② 透明那格（右下）沒有任何網格：布的角 2 不是任何一份的頂點");
                var mesh = SCP_SculptMeshBuilder.Dynamic(got, null);
                Check(mesh.SailCount == 1 && mesh.SailTriangles == 3 * 288 * 2, "② GPU 網格讀數：帆 1 面（⛔ 不把 3 份數成 3 面）、三角形 3×288×2（雙面）：" + mesh.SailCount + "／" + mesh.SailTriangles);
                readings.Add("2×2 貼圖 ⇒ 3 份 × 288 三角形，格點逐位元在布上；GPU 讀數帆 " + mesh.SailCount + " 面");
            }

            // ③ 反向對照：轉 180°（透明 藍／綠 紅）⇒ 角 0 那格變透明、紅搬到右下
            byte[] rot = { 0, 0, 0, 0, 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255 };
            Check(Sub("ship", "sail", ("name", "flag"), ("texture", Png("flag180.png", 2, 2, rot))).ExitCode == 0, "③ 換成轉 180° 的圖");
            View("v2.png");
            var got2 = probe.Last!.Sails;
            double[] c0 = { cloth.Points[0], cloth.Points[1], cloth.Points[2] };
            Check(got2.Count == 3 && Col(got2[0]) == "0,0,255" && !got2.Exists(g => IsVertex(g, c0)), "③ 反向對照：第一份變藍、布的角 0 不再是任何一份的頂點：" + string.Join(" ", got2.ConvertAll(Col)));
            var red2 = got2.Find(g => Col(g) == "255,0,0");
            Check(red2 != null && OnCloth(red2, cloth, 6, 6), "③ 紅搬到右下（格點 ＝ 布的 (6..12, 6..12)）");

            // ⑤ obj：一色一個材質、sails_exported 數帆、sail_meshes_exported 數網格
            string obj = Out("ship.obj");
            var ex = Run("ship", "export", ("format", "obj"), ("out", obj));
            string mtl = File.Exists(Path.ChangeExtension(obj, ".mtl")) ? File.ReadAllText(Path.ChangeExtension(obj, ".mtl")) : "";
            int Count(string text, string what) { int n = 0; for (int k = text.IndexOf(what, StringComparison.Ordinal); k >= 0; k = text.IndexOf(what, k + 1, StringComparison.Ordinal)) n++; return n; }
            Check(ex.ExitCode == 0 && V(ex, "sails_exported") == "1" && V(ex, "sail_meshes_exported") == "3" && Count(mtl, "newmtl sail_") == 3,
                  "⑤ obj：帆 1 面、網格 3 份、材質 3 個：" + V(ex, "sails_exported") + "/" + V(ex, "sail_meshes_exported") + "/" + Count(mtl, "newmtl sail_"));

            // ⑥ vxk：帆仍是一份、布不變、多 texture
            string vxk1 = Out("v1.vxk");
            Check(Run("ship", "export", ("format", "vxk"), ("include", "rig"), ("out", vxk1)).ExitCode == 0, "⑥ vxk 匯得出來");
            SCP_JsonData sails1 = SCP_SculptVxk.Unpack(File.ReadAllBytes(vxk1)).Item1["rig"]["sails"];
            SCP_JsonData sail1 = sails1[0];
            Check(sails1.Count == 1 && sail1["verts"].ToJson(false) == sail0["verts"].ToJson(false) && sail1["tris"].ToJson(false) == sail0["tris"].ToJson(false),
                  "⑥ vxk 的帆仍是 1 份、verts／tris 跟沒貼圖時逐位元相同（遊戲的布料物理不受影響）");
            SCP_JsonData tex = sail1["texture"];
            Check(tex["w"].AsLong() == 2 && tex["h"].AsLong() == 2 && tex["alpha_min"].AsLong() == 128 && tex["file"].AsString() == "textures/flag.png"
                  && Convert.FromBase64String(tex["rgba"].AsString()).SequenceEqual(rot) && tex["uv"].Count == sail1["verts"].Count / 3 * 2,
                  "⑥ texture：2×2、alpha_min 128、rgba ＝ 轉 180° 那張、uv 每頂點 2 個（" + tex["uv"].Count + " vs verts " + sail1["verts"].Count + "）");

            // ④ 三角帆：白那格（像素中心 0.75＋0.75 ＞ 1）不畫
            Check(Sub("ship", "anchor", ("name", "j0"), ("at", "5,30,30")).ExitCode == 0 && Sub("ship", "anchor", ("name", "j1"), ("at", "35,30,30")).ExitCode == 0
                  && Sub("ship", "anchor", ("name", "j2"), ("at", "5,30,10")).ExitCode == 0, "三角帆的固定點");
            byte[] four = { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 };
            Check(Sub("ship", "sail", ("name", "jib"), ("corners", "j0|j1|j2"), ("bind", "0"), ("res", "12"), ("texture", Png("four.png", 2, 2, four))).ExitCode == 0, "④ 三角帆 jib 帶 2×2 四色");
            View("v3.png");
            var jib = probe.Last!.Sails.FindAll(g => g.Path == "jib");
            Check(jib.Count == 3 && !jib.Exists(g => Col(g) == "255,255,255"), "④ 三角帆：紅綠藍 3 份、白那格（斜邊外）不畫：" + string.Join(" ", jib.ConvertAll(Col)));

            // ⑦ 🔴 圖壞了 ⇒ 照底色畫、報出來
            byte[] good = File.ReadAllBytes(dst);
            File.WriteAllText(dst, "not a png");
            var vBad = View("v4.png");
            Check(vBad.ExitCode == 0 && V(vBad, "sail_texture_broken") == "1" && probe.Last!.Sails.FindAll(g => g.Path == "flag").Count == 1
                  && Col(probe.Last.Sails.Find(g => g.Path == "flag")!) == "128,128,128" && SculptRoom.All(vBad).Contains("⚠"),
                  "⑦ 🔴 圖壞了 ⇒ 照底色畫、sail_texture_broken＝1、印 ⚠（⛔ 不靜默）：" + V(vBad, "sail_texture_broken"));
            File.WriteAllBytes(dst, good);
            // 壞參數：零寫入
            byte[] before = SailsBytes();
            File.WriteAllText(Out("junk.png"), "junk");
            foreach (var (why, arg) in new[] { ("檔不在", Out("nope.png")), ("太大 129×1", Png("wide.png", 129, 1, Enumerable.Repeat((byte)255, 129 * 4).ToArray())), ("不是 PNG", Out("junk.png")),
                                               ("全透明", Png("clear.png", 1, 1, new byte[] { 0, 0, 0, 0 })) })
            {
                var r = Sub("ship", "sail", ("name", "flag"), ("texture", arg));
                Check(r.ExitCode == 2 && SailsBytes().SequenceEqual(before) && File.ReadAllBytes(dst).SequenceEqual(good), "⑦ 🔴 " + why + " ⇒ exit 2、sails.json 與圖檔都沒動：exit " + r.ExitCode);
            }
            // 手改成 schema 1 卻帶 texture ⇒ 大聲讀不了
            string text = Encoding.UTF8.GetString(before);
            string hand = text.Replace("\"schema\": 2", "\"schema\": 1");
            Check(hand != text, "（手改 schema 的字串真的換到了）");
            File.WriteAllText(paths.SailsFile, hand, new UTF8Encoding(false));
            bool loud = false;
            try { SCP_SculptSails.ReadSails(paths); } catch (InvalidOperationException e) { loud = e.Message.Contains("schema 1"); }
            Check(loud, "⑦ 🔴 schema 1 卻帶 texture ⇒ 大聲讀不了（舊版 exe 也是這樣擋 ⇒ 不會讀了再寫回時丟掉 texture）");
            File.WriteAllBytes(paths.SailsFile, before);

            // 中文帆名 ⇒ 雜湊檔名
            Check(Sub("ship", "sail", ("name", "旗"), ("corners", "h0|h1|f1|f0"), ("res", "6"), ("texture", png)).ExitCode == 0, "中文帆名也能貼圖");
            string zh = SCP_SculptSails.ReadSails(paths).sails.Find(s => s.name == "旗")!.texture;
            Check(zh.StartsWith("textures/sail_", StringComparison.Ordinal) && zh.Length == "textures/sail_".Length + 8 + 4 && SCP_SculptSails.CheckTexturePath(zh) == null, "中文帆名 ⇒ textures/sail_<8 碼>.png：" + zh);
            Check(Sub("ship", "unsail", ("name", "旗")).ExitCode == 0 && !File.Exists(SCP_SculptSails.TextureFile(paths, zh)), "unsail ⇒ 沒人用的圖檔刪掉");

            // ⑧ texture=none ⇒ 回到原本（jib 先拿掉，才比得了位元組）
            Check(Sub("ship", "unsail", ("name", "jib")).ExitCode == 0, "拿掉 jib");
            var none = Sub("ship", "sail", ("name", "flag"), ("texture", "none"));
            Check(none.ExitCode == 0 && SailsBytes().SequenceEqual(s0) && !File.Exists(dst) && V(none, "texture_removed") == "textures/flag.png",
                  "⑧ texture=none ⇒ sails.json 回到加貼圖之前（逐位元組）、圖檔刪掉：" + V(none, "texture_removed"));
            View("v5.png");
            Check(probe.Last!.Sails.Count == 1 && probe.Last.Sails[0].Points.SequenceEqual(p0), "⑧ 畫出來跟沒貼圖時逐位元相同");

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
