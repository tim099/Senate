// 淨室驗收（TASK-0584 外部模型那一半）：模型的替換色 recolor —— 換的是材質色（正好是調色盤色的那幾種）、同時換不連鎖（互換）、表外的色不動；
// .vxk：換色的擺放跟原色共用同一份網格、換過的色寫在擺放的 mats（同長度、一一對應）；沒換色的擺放沒有 mats（反向對照）；
// 表裡的舊色一個都沒有 ⇒ 讀數說「換到 0 種」、vxk 也不寫 mats；obj 檔的材質不是調色盤色 ⇒ 不動，是調色盤色的那一個照換；
// 關聯的 recolor 只換格子、不套到那一層的模型（反向對照）；GPU 分開畫：表不同各一份來源；
// 🔴 壞值、跟 color 一起給 ⇒ exit 2、models.json 一個位元組都不動；schema：用到 ⇒ 3、清掉 ⇒ 回 1；🔴 schema 2 的檔卻有 recolor（手改的）⇒ 大聲讀不了。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureModelRecolorCleanRoom()
    {
        const string name = "雕刻外部模型替換色（TASK-0584）：互換不連鎖／表外不動／vxk 共用網格＋擺放 mats／沒換色沒有 mats／關聯換色不碰模型／GPU 分開兩份／🔴 壞值與 color 同給擋下零寫入／schema 3 ⇄ 1／🔴 schema 2 帶 recolor 讀不了";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "模型換色 " + id), ("commission_ref", "user:mrc:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            bool Dot(string id, int x, int y, int color) => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", "0"), ("z2", "0"), ("color", color.ToString())).ExitCode == 0;
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            string ModelsText(string id) => File.Exists(store.SpacePaths(id).ModelsFile) ? File.ReadAllText(store.SpacePaths(id).ModelsFile) : "";
            int Schema(string id) => SCP_SculptModels.ReadModels(store.SpacePaths(id)).schema;
            // 調色盤色的 RGB（Kd 0..1、4 位小數）⇒ 第幾色；不是調色盤色 ⇒ 0
            int Idx(SCP_JsonData rgb) => SCP_SculptModels.PaletteIndexOf((byte)Math.Round(rgb[0].AsDouble() * 255), (byte)Math.Round(rgb[1].AsDouble() * 255), (byte)Math.Round(rgb[2].AsDouble() * 255));
            string Idxs(SCP_JsonData mats) { var l = new List<string>(); for (int i = 0; i < mats.Count; i++) l.Add(Idx(mats[i]).ToString()); return string.Join(" ", l); }
            SCP_JsonData Vxk(string id)
            {
                string f = Out(id + "_" + Guid.NewGuid().ToString("N")[..6] + ".vxk");
                var r = Run(id, "export", ("format", "vxk"), ("include", "rig,models"), ("out", f));
                if (r.ExitCode != 0 || !File.Exists(f)) throw new InvalidOperationException("export vxk exit " + r.ExitCode + "：" + SculptRoom.All(r));
                return SCP_SculptVxk.Unpack(File.ReadAllBytes(f)).Header;
            }
            SCP_JsonData? ModelJ(SCP_JsonData h, string path) { foreach (SCP_JsonData m in h["rig"]["models"]) if (m["name"].AsString() == path) return m; return null; }
            string Own(SCP_JsonData h, string path) { var m = ModelJ(h, path); return m == null ? "沒有" : m["mats"].Exists ? Idxs(m["mats"]) : "-"; }
            string Mesh(SCP_JsonData h, string path) { var m = ModelJ(h, path); return m == null ? "沒有" : Idxs(h["rig"]["meshes"][(int)m["mesh"].AsLong()]["mats"]); }
            long MeshNo(SCP_JsonData h, string path) => ModelJ(h, path)?["mesh"].AsLong() ?? -1;
            string Report() => File.Exists(room.Letters + "/p/cmd/sculpture_work.md") ? File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md") : "";

            // 零件 pt（2×1×1）：x=0 色 10、x=1 色 20；船 h（8×8×8，同比例 ⇒ scale 1）自己一格 30（vxk 要有格子）
            Check(Create("h", "8,8,8") && Create("pt", "2,1,1") && Dot("pt", 0, 0, 10) && Dot("pt", 1, 0, 20) && Dot("h", 7, 7, 30), "作品與格子");
            // 三個擺放：a 原色、b 互換、c 換一個表裡沒有的色（99）；都只差整數平移 ⇒ 網格可以共用
            Check(Sub("h", "model", ("name", "a"), ("source_work", "pt"), ("at", "0,0,0")).ExitCode == 0, "a：原色");
            var rb = Sub("h", "model", ("name", "b"), ("source_work", "pt"), ("at", "0,2,0"), ("recolor", "20:10, 10:20"));
            Check(rb.ExitCode == 0 && SculptRoom.V(rb, "model_recolor") == "10:20,20:10" && SculptRoom.V(rb, "model_recolor_hits") == "2",
                  "b：recolor=20:10,10:20 ⇒ 存成正規化 10:20,20:10、換到 2 種材質色：" + SculptRoom.V(rb, "model_recolor") + " hits=" + SculptRoom.V(rb, "model_recolor_hits"));
            Check(Report().Contains("換色 10:20,20:10（換到 2 種材質色）"), "回傳檔印出替換表與換到幾種");
            var rc = Sub("h", "model", ("name", "c"), ("source_work", "pt"), ("at", "0,4,0"), ("recolor", "99:100"));
            Check(rc.ExitCode == 0 && SculptRoom.V(rc, "model_recolor_hits") == "0" && Report().Contains("一種材質色都沒換到"), "c：表裡的舊色一個都沒有 ⇒ 換到 0 種、回傳檔說 ⚠：" + SculptRoom.V(rc, "model_recolor_hits"));
            Check(Schema("h") == SCP_SculptModels.SchemaWithRecolor && ModelsText("h").Contains("\"recolor\": \"10:20,20:10\""), "models.json 升 schema " + SCP_SculptModels.SchemaWithRecolor + "：" + Schema("h"));

            // ① 觀測（烘好的幾何）：b 的頂點色互換、a 原色（換色不漏到同來源的別個擺放）
            var set = SCP_SculptModels.Resolve(store, "h", SCP_SculptLinks.Tree(store, "h"), new SCP_SculptLinkSelection(), new SCP_SculptModelSelection(), true);
            string Cols(string path)
            {
                var g = set.Models.Find(m => m.Path == path)?.Geom;
                if (g == null) return "沒畫";
                var s = new SortedSet<int>();
                for (int k = 0; k + 2 < g.Colors.Length; k += 3) s.Add(SCP_SculptModels.PaletteIndexOf(g.Colors[k], g.Colors[k + 1], g.Colors[k + 2]));
                return string.Join(" ", s);
            }
            // 每個頂點色跟它換之前的色：b 是 10↔20，a 沒有 BaseColors
            var gb = set.Models.Find(m => m.Path == "b")?.Geom;
            bool aSwapOk = gb?.BaseColors != null;
            for (int k = 0; aSwapOk && k + 2 < gb!.Colors.Length; k += 3)
            {
                int was = SCP_SculptModels.PaletteIndexOf(gb.BaseColors![k], gb.BaseColors[k + 1], gb.BaseColors[k + 2]), now = SCP_SculptModels.PaletteIndexOf(gb.Colors[k], gb.Colors[k + 1], gb.Colors[k + 2]);
                aSwapOk = (was == 10 && now == 20) || (was == 20 && now == 10);
            }
            Check(aSwapOk && set.Models.Find(m => m.Path == "a")?.Geom?.BaseColors == null && set.Models.Find(m => m.Path == "c")?.Geom?.BaseColors == null,
                  "🔴 b 每個頂點 10↔20（互換、不連鎖成同一色）；a、c 沒有換之前的色（沒換到東西）");
            Check(Cols("a") == "10 20" && Cols("b") == "10 20" && Cols("c") == "10 20", "三個擺放用到的色都還是 {10,20}（互換不會多出新色）：" + Cols("a") + "／" + Cols("b") + "／" + Cols("c"));

            // ② .vxk：三個擺放共用一份網格（網格的 mats 是原色 10 20）；b 帶 mats 20 10；a、c 沒有 mats（反向對照）
            var h1 = Vxk("h");
            Check(h1["rig"]["meshes"].Count == 1 && MeshNo(h1, "a") == 0 && MeshNo(h1, "b") == 0 && MeshNo(h1, "c") == 0,
                  "🔴 換色的擺放跟原色共用同一份網格（網格 " + h1["rig"]["meshes"].Count + " 份；a/b/c → " + MeshNo(h1, "a") + "/" + MeshNo(h1, "b") + "/" + MeshNo(h1, "c") + "）");
            Check(Mesh(h1, "a") == "10 20" && Own(h1, "b") == "20 10" && Own(h1, "a") == "-" && Own(h1, "c") == "-",
                  "網格 mats 10 20；b 的 mats 20 10（一一對應、同長度）；a、c 沒有 mats：mesh=" + Mesh(h1, "a") + " b=" + Own(h1, "b") + " a=" + Own(h1, "a") + " c=" + Own(h1, "c"));

            // ③ obj 檔的模型：材質不是調色盤色 ⇒ 不動；正好是調色盤色 10 的那一個 ⇒ 照表換
            SCP_CanvasPalette.IndexToRgb(10, out byte r10, out byte g10, out byte b10);
            string F(byte v) => (v / 255.0).ToString("F4", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(Out("tri.mtl"), "newmtl pal\nKd " + F(r10) + " " + F(g10) + " " + F(b10) + "\nnewmtl odd\nKd 0.1230 0.4560 0.7890\n");
            File.WriteAllText(Out("tri.obj"), "mtllib tri.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nv 0 0 1\nusemtl pal\nf 1 2 3\nusemtl odd\nf 1 3 4\n");
            var ro = Sub("h", "model", ("name", "o"), ("file", Out("tri.obj")), ("axes", "zup"), ("at", "2,0,0"), ("recolor", "10:40"));
            var go = SCP_SculptModels.Resolve(store, "h", SCP_SculptLinks.Tree(store, "h"), new SCP_SculptLinkSelection(), new SCP_SculptModelSelection(), true).Models.Find(m => m.Path == "o")?.Geom;
            var oc = new SortedSet<string>();
            for (int k = 0; go != null && k + 2 < go.Colors.Length; k += 3) oc.Add(go.Colors[k] + "," + go.Colors[k + 1] + "," + go.Colors[k + 2]);
            SCP_CanvasPalette.IndexToRgb(40, out byte r40, out byte g40, out byte b40);
            Check(ro.ExitCode == 0 && SculptRoom.V(ro, "model_recolor_hits") == "1" && oc.Contains(r40 + "," + g40 + "," + b40) && oc.Contains("31,116,201") && !oc.Contains(r10 + "," + g10 + "," + b10),
                  "🔴 obj：調色盤色 10 換成 40、非調色盤色 (31,116,201) 不動（表外不動）：hits=" + SculptRoom.V(ro, "model_recolor_hits") + " " + string.Join(" ", oc));

            // ④ 關聯的 recolor 只換格子、不套到那一層的模型：top 關聯 h（換 10:50、20:60、30:70）⇒ h 的格 30 → 70，模型 a 照舊 10 20、b 照舊自己的 20 10
            Check(Create("top", "8,8,8") && Dot("top", 0, 7, 1) && Sub("top", "link", ("name", "ship"), ("source_work", "h"), ("at", "0,0,0"), ("recolor", "10:50,20:60,30:70")).ExitCode == 0, "top 關聯 h 並換色");
            var ht = Vxk("top");
            int hullAt77 = -1;
            string tf = Out("top_" + Guid.NewGuid().ToString("N")[..6] + ".vxk");
            if (Run("top", "export", ("format", "vxk"), ("out", tf)).ExitCode == 0)
                foreach (var v in SCP_SculptVxk.Unpack(File.ReadAllBytes(tf)).Voxels) if (v.X == 7 && v.Y == 7 && v.Z == 0) hullAt77 = v.Color;
            Check(hullAt77 == 70 && Mesh(ht, "ship/a") == "10 20" && Own(ht, "ship/a") == "-" && Own(ht, "ship/b") == "20 10",
                  "🔴 關聯換色：格子 30→70，模型不跟著換（a 網格 10 20、沒有 mats；b 照舊自己的 20 10）：格=" + hullAt77 + " a=" + Mesh(ht, "ship/a") + "/" + Own(ht, "ship/a") + " b=" + Own(ht, "ship/b"));

            // ⑤ GPU 分開畫（TASK-0509：不烘、每個實例一份來源＋矩陣）：a 與 c 同一份來源（c 沒換到也是不同的表 ⇒ 分開也行，但 a／b 一定分開），b 的來源是換過的色
            var parts = new SCP_SculptParts();
            parts.AddModels(SCP_SculptModels.Resolve(store, "h", SCP_SculptLinks.Tree(store, "h"), new SCP_SculptLinkSelection(), new SCP_SculptModelSelection(), true, null, null, false));
            int Src(string path) => parts.Instances.Find(i => i.Path == path)?.Source ?? -1;
            string SrcCols(string path)
            {
                int s = Src(path);
                var g = s >= 0 ? parts.Sources[s].Model : null;
                if (g == null) return "沒有";
                var l = new List<int>();
                for (int k = 0; k + 2 < g.Colors.Length; k += 3) { int i = SCP_SculptModels.PaletteIndexOf(g.Colors[k], g.Colors[k + 1], g.Colors[k + 2]); if (!l.Contains(i)) l.Add(i); }
                return string.Join(" ", l);
            }
            Check(Src("a") >= 0 && Src("b") >= 0 && Src("a") != Src("b") && SrcCols("b") == string.Join(" ", SrcCols("a").Split(' ').Select(x => x == "10" ? "20" : x == "20" ? "10" : x)),
                  "🔴 分開畫：a、b 各一份來源，b 的色是 a 的 10↔20：a=" + SrcCols("a") + " b=" + SrcCols("b"));

            // ⑥ 🔴 擋下的那幾種：models.json 一個位元組都不動
            string before = ModelsText("h");
            foreach (string bad in new[] { "10:0", "0:10", "10:256", "10:20,10:30", "10-20", "10:", "a:b", "10:10" })
            {
                var r = Sub("h", "model", ("name", "a"), ("recolor", bad));
                Check(r.ExitCode == 2 && ModelsText("h") == before, "🔴 recolor=" + bad + " ⇒ exit 2 且 models.json 不動：exit " + r.ExitCode);
            }
            var both = Sub("h", "model", ("name", "a"), ("color", "30"), ("recolor", "10:20"));
            Check(both.ExitCode == 2 && ModelsText("h") == before, "🔴 color 與 recolor 一起給 ⇒ exit 2、不動：exit " + both.ExitCode);
            Check(Sub("h", "model", ("name", "b"), ("color", "30")).ExitCode == 2 && ModelsText("h") == before, "🔴 b 已有 recolor 再給 color ⇒ exit 2、不動");

            // ⑦ 清掉 ⇒ 回原色、沒有 mats、schema 回 1（沒有 meta）
            foreach (string n in new[] { "b", "c", "o" }) Check(Sub("h", "model", ("name", n), ("recolor", "none")).ExitCode == 0, "清掉 " + n);
            var h2 = Vxk("h");
            Check(Schema("h") == 1 && !ModelsText("h").Contains("\"recolor\"") && Own(h2, "b") == "-" && MeshNo(h2, "b") == MeshNo(h2, "a"),
                  "清掉 ⇒ schema 1、檔裡沒有 recolor、vxk 的 b 沒有 mats：schema=" + Schema("h") + " b=" + Own(h2, "b"));

            // ⑧ 🔴 schema 2 的檔卻有 recolor（手改／舊版寫回）⇒ 大聲讀不了，⛔ 不當成沒換色
            Check(Sub("h", "model", ("name", "b"), ("recolor", "10:20")).ExitCode == 0 && Sub("h", "model", ("name", "b"), ("meta", "{\"kind\":\"lamp\"}")).ExitCode == 0
                  && Schema("h") == SCP_SculptModels.SchemaWithRecolor, "有 meta 也有 recolor ⇒ 還是 schema " + SCP_SculptModels.SchemaWithRecolor);
            string mf = store.SpacePaths("h").ModelsFile, mText = File.ReadAllText(mf);
            File.WriteAllText(mf, mText.Replace("\"schema\": " + SCP_SculptModels.SchemaWithRecolor, "\"schema\": " + SCP_SculptModels.SchemaWithMeta));
            bool threw = false;
            try { SCP_SculptModels.ReadModels(store.SpacePaths("h")); } catch (InvalidOperationException) { threw = true; }
            Check(threw && Sub("h", "models").ExitCode != 0, "🔴 schema 2 帶 recolor ⇒ ReadModels 丟例外、sub=models 不是 exit 0");
            File.WriteAllText(mf, mText);
            Check(Schema("h") == SCP_SculptModels.SchemaWithRecolor, "改回去 ⇒ 照常");

            return new CheckRow(name, failures.Count == 0
                ? "b 每頂點 10↔20 不連鎖、a 原色；vxk 三個擺放 1 份網格、b mats 20 10、a／c 沒有 mats；表裡的舊色沒有 ⇒ 換到 0 種＋⚠；obj 調色盤色換、非調色盤色不動；關聯換色格子 30→70、模型不動；GPU 分開 a／b 各一份；🔴 8 種壞值＋color 同給 exit 2 零寫入；清掉 schema 1；🔴 schema 2 帶 recolor 讀不了"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
