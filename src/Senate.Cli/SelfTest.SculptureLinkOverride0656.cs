// 淨室驗收（TASK-0656 改裝船第一期）：關聯的覆寫 —— link_hide 拿掉被關聯作品裡的關聯（連同底下）、link_carve 照遮罩挖掉整棵組好的格（巢狀進來的也挖得到）；
// 組裝（list／vxk）與 GPU 預覽的分開畫（每個節點一份來源）結果一樣；同一件作品被連兩次各自照自己那條；
// 🔴 路徑打錯／遮罩尺寸不對／遮罩是自己／不是作者 ⇒ 擋下、links.json 一個位元組都不動；
// schema：用到覆寫 ⇒ 5、清掉 ⇒ 回 1；🔴 schema 4 的檔卻有 hide（手改／舊版寫回）⇒ 大聲讀不了；sub=links 印出覆寫。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureLinkOverrideCleanRoom()
    {
        const string name = "雕刻關聯覆寫（TASK-0656）：hide 剪關聯／carve 照遮罩挖（含巢狀）／組裝＝分開畫／同件兩條各自照自己／🔴 打錯路徑・尺寸不對・遮罩是自己・不是作者擋下零寫入／schema 5 ⇄ 1／🔴 舊 schema 帶 hide 讀不了";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            Directory.CreateDirectory(Path.Combine(room.Letters, "q", "profile"));
            var pay = new SculptProbeGateway { Vouchers = 10 };
            SCP_CanvasGatewayHost.Factory = _ => pay;
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "覆寫 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult SubAs(string persona, string id, string sub, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", persona) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more) => SubAs("p", id, sub, more);
            bool Dot(string id, int x, int y, int color) => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", "0"), ("z2", "0"), ("color", color.ToString())).ExitCode == 0;
            SCP_CmdResult Link(string parent, string nm, string child, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("name", nm), ("source_work", child), ("at", at) };
                a.AddRange(more);
                return Sub(parent, "link", a.ToArray());
            }
            // 那一列（y）組好之後每一格：「x:色」依 x
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
                    if (p[1] == y.ToString() && p[2] == "0") cells[int.Parse(p[0])] = p[0] + ":" + p[3];
                }
                return string.Join(" ", cells.Values);
            }
            string LinksText(string id) => File.ReadAllText(store.SpacePaths(id).LinksFile);
            int Schema(string id) => SCP_SculptLinks.Read(store.SpacePaths(id)).schema;

            // 原船 b（4×3×1）：自己一列 y=0 是 10 20 30 40；關聯 dk → d（一列 50，放在 y=1）、fx → f（一格 60，放在 (0,2)）
            // 遮罩 mk（跟 b 一樣大）：(1,0) 挖 b 自己的格、(2,1) 挖巢狀進來的 dk 的格
            // 改裝船 h（8×3×1）：v1 → b @0（carve mk）、v2 → b @4（hide fx）
            Check(Create("h", "8,3,1") && Create("b", "4,3,1") && Create("d", "4,1,1") && Create("f", "1,1,1") && Create("mk", "4,3,1"), "五件作品建立");
            Check(Dot("b", 0, 0, 10) && Dot("b", 1, 0, 20) && Dot("b", 2, 0, 30) && Dot("b", 3, 0, 40) && Dot("d", 0, 0, 50) && Dot("d", 1, 0, 50) && Dot("d", 2, 0, 50) && Dot("d", 3, 0, 50)
                  && Dot("f", 0, 0, 60) && Dot("mk", 1, 0, 1) && Dot("mk", 2, 1, 1), "各自的格落地");
            Check(Link("b", "dk", "d", "0,1,0").ExitCode == 0 && Link("b", "fx", "f", "0,2,0").ExitCode == 0
                  && Link("h", "v1", "b", "0,0,0").ExitCode == 0 && Link("h", "v2", "b", "4,0,0").ExitCode == 0, "四條關聯");
            Check(Row("h", 0) == "0:10 1:20 2:30 3:40 4:10 5:20 6:30 7:40" && Row("h", 2) == "0:60 4:60" && Schema("h") == 1, "覆寫前：兩份原船一模一樣、schema 1：" + Row("h", 0) + "｜" + Row("h", 2));

            // ① 覆寫：v1 照遮罩挖、v2 拿掉 fx；不用 overwrite
            var r1 = Sub("h", "link", ("name", "v1"), ("link_carve", "mk"));
            var r2 = Sub("h", "link", ("name", "v2"), ("link_hide", "fx"));
            Check(r1.ExitCode == 0 && r2.ExitCode == 0, "不給 overwrite 也能設覆寫：exit " + r1.ExitCode + "／" + r2.ExitCode);
            Check(Row("h", 0) == "0:10 2:30 3:40 4:10 5:20 6:30 7:40", "🔴 v1 挖掉自己的 (1,0)，v2 照舊：" + Row("h", 0));
            Check(Row("h", 1) == "0:50 1:50 3:50 4:50 5:50 6:50 7:50", "🔴 v1 挖掉巢狀進來的 dk 的 (2,1)（遮罩照原船的座標），v2 照舊：" + Row("h", 1));
            Check(Row("h", 2) == "0:60", "🔴 v2 拿掉 fx、v1 的 fx 還在（同一件作品兩條各自照自己）：" + Row("h", 2));
            Check(Schema("h") == SCP_SculptLinks.SchemaWithOverride && LinksText("h").Contains("\"carve\": \"mk\"") && LinksText("h").Contains("\"hide\""), "schema 5、檔裡有 carve 與 hide");

            // ② GPU 預覽的分開畫（每個節點一份來源，不經過組裝器）⇒ 每個實例的格數要跟合併版一樣；被拿掉的 v2/fx 沒有實例
            var parts = new SCP_SculptParts();
            parts.AddLinks(store, SCP_SculptLinks.Tree(store, "h"), false);
            int Count(string iPath) { var inst = parts.Instances.Find(i => i.Path == iPath); return inst == null ? -1 : parts.Sources[inst.Source].Voxels!.Count; }
            Check(Count("v1") == 3 && Count("v1/dk") == 3 && Count("v1/fx") == 1 && Count("v2") == 4 && Count("v2/dk") == 4 && Count("v2/fx") == -1,
                  "🔴 分開畫：v1 3、v1/dk 3、v1/fx 1、v2 4、v2/dk 4、v2/fx 沒有：" + Count("v1") + " " + Count("v1/dk") + " " + Count("v1/fx") + " " + Count("v2") + " " + Count("v2/dk") + " " + Count("v2/fx"));

            // ③ vxk（給遊戲的那一份）：逐格是覆寫過的；source 那一行帶遮罩
            string f = Path.Combine(room.Root, "h.vxk").Replace('\\', '/');
            Check(Run("h", "export", ("format", "vxk"), ("out", f)).ExitCode == 0 && File.Exists(f), "匯出 vxk");
            var vx = SCP_SculptVxk.Unpack(File.ReadAllBytes(f));
            Check(vx.Voxels.Count == 15, "vxk 15 格（v1 3＋3＋1、v2 4＋4）：" + vx.Voxels.Count);

            // ④ 🔴 擋下的那幾種：links.json 一個位元組都不動
            string before = LinksText("h");
            var bads = new (string Who, (string, string) Arg)[] { ("p", ("link_hide", "zz")), ("p", ("link_hide", "dk/zz")), ("p", ("link_carve", "d")), ("p", ("link_carve", "h")), ("q", ("link_hide", "dk")) };
            foreach (var (who, arg) in bads)
            {
                var rb = SubAs(who, "h", "link", ("name", "v2"), arg);
                Check(rb.ExitCode == 2 && LinksText("h") == before, "🔴 " + who + " " + arg.Item1 + "=" + arg.Item2 + " ⇒ exit 2 且 links.json 不動：exit " + rb.ExitCode);
            }

            // ⑤ sub=links 印出覆寫
            var ls = Sub("h", "links");
            string lsReport = File.Exists(room.Letters + "/p/cmd/sculpture_work.md") ? File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md") : "";
            Check(ls.ExitCode == 0 && lsReport.Contains("挖格子照遮罩 mk") && lsReport.Contains("拿掉 fx（關聯）"), "sub=links 印出覆寫");

            // ⑥ 🔴 schema 4 的檔卻有 hide（手改／舊版寫回）⇒ 大聲讀不了
            string hf = store.SpacePaths("h").LinksFile, hText = File.ReadAllText(hf);
            File.WriteAllText(hf, hText.Replace("\"schema\": " + SCP_SculptLinks.SchemaWithOverride, "\"schema\": 4"));
            bool threw = false;
            try { SCP_SculptLinks.Read(store.SpacePaths("h")); } catch (InvalidOperationException) { threw = true; }
            Check(threw && Run("h", "stats").ExitCode == 2, "🔴 schema 4 帶 hide ⇒ Read 丟例外、觀測 exit 2");
            File.WriteAllText(hf, hText);

            // ⑦ 清掉 ⇒ 兩份原船又一樣、schema 回 1
            Check(Sub("h", "link", ("name", "v1"), ("link_carve", "none")).ExitCode == 0 && Sub("h", "link", ("name", "v2"), ("link_hide", "none")).ExitCode == 0
                  && Row("h", 0) == "0:10 1:20 2:30 3:40 4:10 5:20 6:30 7:40" && Row("h", 2) == "0:60 4:60" && Schema("h") == 1
                  && !LinksText("h").Contains("\"hide\"") && !LinksText("h").Contains("\"carve\""), "none ⇒ 清掉、回原樣、schema 1、檔裡沒有 hide／carve：" + Row("h", 0) + " schema=" + Schema("h"));

            return new CheckRow(name, failures.Count == 0
                ? "carve 挖到自己的與巢狀進來的格（照原船座標）、hide 剪掉 fx；同件兩條各自照自己；GPU 分開畫格數跟組裝一樣、被剪的沒有實例；vxk 15 格；🔴 5 種壞值 exit 2 零寫入；sub=links 印覆寫；🔴 schema 4 帶 hide 讀不了；清掉回原樣 schema 1"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
