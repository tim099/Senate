// 淨室驗收（TASK-0584 關聯那一半）：關聯的替換色 recolor —— 同時換不連鎖（互換）、表外的色不動、巢狀（外層看到內層換完的色）、
// 同一件關聯兩次只換有寫的那條、不給 overwrite 也能改、搬位置不會重設、清掉 ⇒ 回原色；
// 🔴 新色 0／舊色 0／超出 1..255／同一個舊色寫兩次／寫錯格式 ⇒ 擋下、links.json 一個位元組都不動；
// schema：用到 recolor ⇒ 4、清掉 ⇒ 回 1；🔴 schema 3 的檔卻有 recolor（手改的）⇒ 大聲讀不了；sub=links 印出替換表。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureLinkRecolorCleanRoom()
    {
        const string name = "雕刻關聯替換色（TASK-0584）：互換不連鎖／表外不動／巢狀順序／同件兩條各自換／不用 overwrite／🔴 0 與壞格式擋下零寫入／schema 4 ⇄ 1／🔴 舊 schema 帶 recolor 讀不了";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            var pay = new SculptProbeGateway { Vouchers = 10 };
            SCP_CanvasGatewayHost.Factory = _ => pay;
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "替換色 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
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
            bool Dot(string id, int x, int color) => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", color.ToString())).ExitCode == 0;
            SCP_CmdResult Link(string parent, string nm, string child, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("name", nm), ("source_work", child), ("at", at) };
                a.AddRange(more);
                return Sub(parent, "link", a.ToArray());
            }
            string Row(string id, int y)
            {
                string f = Path.Combine(room.Root, "row_" + Guid.NewGuid().ToString("N")[..6] + ".txt").Replace('\\', '/');
                var r = Run(id, "export", ("format", "list"), ("out", f));
                if (r.ExitCode != 0 || !File.Exists(f)) return "x" + r.ExitCode;
                var cells = new SortedDictionary<int, string>();
                foreach (string line in File.ReadAllLines(f))
                {
                    if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                    string[] p = line.Split(',');
                    if (p[1] == y.ToString() && p[2] == "0") cells[int.Parse(p[0])] = p[3];
                }
                return string.Join(" ", cells.Values);
            }
            string LinksText(string id) => File.ReadAllText(store.SpacePaths(id).LinksFile);
            int Schema(string id) => SCP_SculptLinks.Read(store.SpacePaths(id)).schema;

            // 零件 pt（4×1×1）：自己三格 10／20／30，第四格是關聯進來的 in（一格 40）；船 h 把 pt 放兩次（y=0 換色、y=2 原樣）
            Check(Create("h", "8,4,1") && Create("pt", "4,1,1") && Create("in", "1,1,1"), "三件作品建立");
            Check(Dot("pt", 0, 10) && Dot("pt", 1, 20) && Dot("pt", 2, 30) && Dot("in", 0, 40), "各自的格落地");
            Check(Link("pt", "inner", "in", "3,0,0").ExitCode == 0 && Link("h", "a", "pt", "0,0,0").ExitCode == 0 && Link("h", "b", "pt", "0,2,0").ExitCode == 0, "三條關聯");
            Check(Row("h", 0) == "10 20 30 40" && Schema("h") == 1, "換色前：原色、schema 1：" + Row("h", 0));

            // ① 互換不連鎖、表外的色不動；不給 overwrite 也能改（不動幾何）
            var r1 = Sub("h", "link", ("name", "a"), ("recolor", "20:10, 10:20"));
            Check(r1.ExitCode == 0 && Row("h", 0) == "20 10 30 40", "🔴 recolor=20:10,10:20 ⇒ 10、20 互換（不連鎖成同一色）、30 與 40 不動：" + r1.ExitCode + " " + Row("h", 0) + " " + SculptRoom.All(r1));
            Check(Row("h", 2) == "10 20 30 40", "同一件的另一條關聯 b 沒寫 recolor ⇒ 原色（換色不漏到別條、不汙染快取）：" + Row("h", 2));
            Check(SculptRoom.V(r1, "link_recolor") == "10:20,20:10" && Schema("h") == SCP_SculptLinks.SchemaWithRecolor && LinksText("h").Contains("\"recolor\""),
                  "存成正規化的 10:20,20:10、links.json 升 schema " + SCP_SculptLinks.SchemaWithRecolor + "：" + SculptRoom.V(r1, "link_recolor") + " schema=" + Schema("h"));

            // ② 巢狀：內層 inner 先把 40 換成 50；外層 a 的表寫 50:60、40:70 ⇒ 外層看到的是內層換完的 50 ⇒ 60（40:70 用不到）
            Check(Sub("pt", "link", ("name", "inner"), ("recolor", "40:50")).ExitCode == 0 && Row("pt", 0) == "10 20 30 50", "內層 inner 40→50：" + Row("pt", 0));
            Check(Sub("h", "link", ("name", "a"), ("recolor", "10:20,20:10,50:60,40:70")).ExitCode == 0 && Row("h", 0) == "20 10 30 60",
                  "🔴 巢狀：外層的表套在內層換完的色上（50→60，不是 40→70）：" + Row("h", 0));
            Check(Row("h", 2) == "10 20 30 50", "b 只吃到內層的換色：" + Row("h", 2));

            // ②b GPU 預覽的分開畫（TASK-0509：每個節點一份來源＋矩陣，不經過組裝器）⇒ 每個實例的色要跟合併版一樣；
            //    同一件作品、有效表不同 ⇒ 分開兩份來源；有效表相同才共用
            var parts = new SCP_SculptParts();
            parts.AddLinks(store, SCP_SculptLinks.Tree(store, "h"), false);
            string Colors(string iPath)
            {
                var inst = parts.Instances.Find(i => i.Path == iPath);
                if (inst == null) return "沒有";
                var vs = new List<SCP_SculptVoxel>(parts.Sources[inst.Source].Voxels!);
                vs.Sort((p, q) => p.X.CompareTo(q.X));
                return string.Join(" ", vs.ConvertAll(v => v.Color.ToString()));
            }
            Check(Colors("a") == "20 10 30" && Colors("a/inner") == "60" && Colors("b") == "10 20 30" && Colors("b/inner") == "50" && parts.Sources.Count == 4,
                  "🔴 分開畫：a 20 10 30、a/inner 60（內層 50 再被外層換）、b 原色、b/inner 50；來源 4 份（pt、in 各兩種有效表）：a=" + Colors("a") + " a/inner=" + Colors("a/inner")
                  + " b=" + Colors("b") + " b/inner=" + Colors("b/inner") + " sources=" + parts.Sources.Count);

            // ③ 🔴 擋下的那幾種：links.json 一個位元組都不動
            string before = LinksText("h");
            foreach (string bad in new[] { "10:0", "0:10", "10:256", "10:20,10:30", "10-20", "10:", "a:b", "10:10" })
            {
                var rb = Sub("h", "link", ("name", "a"), ("recolor", bad));
                Check(rb.ExitCode == 2 && LinksText("h") == before, "🔴 recolor=" + bad + " ⇒ exit 2 且 links.json 不動：exit " + rb.ExitCode);
            }

            // ④ 搬位置（overwrite）不會把替換表重設；sub=links 印出替換表
            Check(Link("h", "a", "pt", "4,0,0", ("overwrite", "1")).ExitCode == 0 && Row("h", 0) == "20 10 30 60", "overwrite 搬到 x=4 ⇒ 替換表照留：" + Row("h", 0));
            var ls = Sub("h", "links");
            string lsReport = File.Exists(room.Letters + "/p/cmd/sculpture_work.md") ? File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md") : "";
            Check(ls.ExitCode == 0 && lsReport.Contains("recolor 10:20,20:10,40:70,50:60") && lsReport.Contains("recolor 40:50"), "sub=links 印出替換表（含巢狀那一條）");

            // ④b 匯出 vxk（給遊戲的那一份）與 obj 也是換過的色：vxk 解開逐格讀；obj 換色前後要不一樣
            string Vxk()
            {
                string f = Path.Combine(room.Root, "h_" + Guid.NewGuid().ToString("N")[..6] + ".vxk").Replace('\\', '/');
                if (Run("h", "export", ("format", "vxk"), ("out", f)).ExitCode != 0 || !File.Exists(f)) return "x";
                var cells = new SortedDictionary<int, int>();
                foreach (var v in SCP_SculptVxk.Unpack(File.ReadAllBytes(f)).Voxels) if (v.Y == 0 && v.Z == 0) cells[v.X] = v.Color;
                return string.Join(" ", cells.Values);
            }
            string Obj()
            {
                string f = Path.Combine(room.Root, "h_" + Guid.NewGuid().ToString("N")[..6] + ".obj").Replace('\\', '/');
                return Run("h", "export", ("format", "obj"), ("out", f)).ExitCode == 0 && File.Exists(f) ? File.ReadAllText(f) + File.ReadAllText(Path.ChangeExtension(f, ".mtl")) : "x";
            }
            string vxkRe = Vxk(), objRe = Obj();
            Check(vxkRe == "20 10 30 60", "vxk 解開：(4..7,0,0) 是換過的色：" + vxkRe);

            // ⑤ 清掉 ⇒ 原色、schema 回 1（沒有別的新欄位）
            Check(Sub("h", "link", ("name", "a"), ("recolor", "none")).ExitCode == 0 && Row("h", 0) == "10 20 30 50" && Schema("h") == 1 && !LinksText("h").Contains("\"recolor\""),
                  "recolor=none ⇒ 回原色（內層的換色照舊）、schema 1、檔裡沒有 recolor：" + Row("h", 0) + " schema=" + Schema("h"));
            string vxkNo = Vxk(), objNo = Obj();
            Check(vxkNo == "10 20 30 50" && objRe != "x" && objNo != "x" && objRe != objNo, "清掉之後 vxk 回原色、obj 跟換色時不一樣：" + vxkNo);

            // ⑥ 🔴 schema 3 的檔卻有 recolor（手改／舊版寫回）⇒ 大聲讀不了，⛔ 不當成沒換色
            string pf = store.SpacePaths("pt").LinksFile;
            string ptText = File.ReadAllText(pf);
            Check(ptText.Contains("\"schema\": " + SCP_SculptLinks.SchemaWithRecolor), "pt 的 links.json 是 schema " + SCP_SculptLinks.SchemaWithRecolor);
            File.WriteAllText(pf, ptText.Replace("\"schema\": " + SCP_SculptLinks.SchemaWithRecolor, "\"schema\": 3"));
            bool threw = false;
            try { SCP_SculptLinks.Read(store.SpacePaths("pt")); } catch (InvalidOperationException) { threw = true; }
            Check(threw && Run("h", "stats").ExitCode == 2, "🔴 schema 3 帶 recolor ⇒ Read 丟例外、觀測 exit 2");
            File.WriteAllText(pf, ptText);
            Check(Row("h", 0) == "10 20 30 50", "改回去 ⇒ 照常");

            return new CheckRow(name, failures.Count == 0
                ? "互換 10↔20 不連鎖、30／40 不動；同件另一條原色；巢狀外層看到內層換完的 50→60；GPU 分開畫每個實例同色、有效表不同各一份來源（4 份）；🔴 8 種壞值 exit 2 零寫入；overwrite 搬位置照留；sub=links 印表；vxk 解開逐格是換過的色、obj 換色前後不同；清掉回原色 schema 1；🔴 schema 3 帶 recolor 讀不了"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
