// 淨室驗收（TASK-0646）：規則由 Tim 定（2026-10-16）「不影響模型外觀的修改 ok」——
//   ① 不是作者（q）也能改外部模型、固定點、關聯；成功的那一筆記進那件作品的 meta_log.jsonl（誰、作者是誰）
//   ② 反向對照：q 動格子（box）照舊擋下（exit 2、事件一筆都沒多）
//   ③ 拆件 work sub=split：預覽（沒 confirm）一個檔都不寫；confirm=1 ⇒ 新子作品的作者照原作者、格子搬過去、
//      每個引用原作品的關聯與模型都多一個雙生（擺放照抄、插在原本的下一個）⇒ 引用它的作品組起來逐格相同
//   ④ 反向對照：原作品自己的格蓋住它關聯的格 ⇒ 拆掉會露出來 ⇒ 擋下、沒建新作品；選到全部格 ⇒ 擋
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptSplitCleanRoom()
    {
        const string name = "雕刻：不影響外觀的修改不限作者（模型、固定點、關聯；格子照舊擋）＋拆件 sub=split（預覽零寫入、雙生引用、組起來逐格相同、露出關聯格就擋）（淨室，TASK-0646）";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            Directory.CreateDirectory(Path.Combine(room.Letters, "q", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "拆件 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string who, string id, string sub, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", who) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Box(string who, string id, int x1, int x2, int y, int z, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", who), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            int Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id)).Count;
            HashSet<string> Composed(string id)
            {
                var card = store.Load(id);
                var tree = SCP_SculptLinks.Tree(store, id);
                var own = SCP_SculptStore.Load(store.SpacePaths(id), false);
                var sp = tree.Count == 0 ? own : new SCP_SculptLinkComposer(store, room.Data, tree, new SCP_SculptLinkSelection { All = true }).Compose(own, card.SizeX, card.SizeY, card.SizeZ);
                var s = new HashSet<string>();
                foreach (var v in sp.Voxels.Entries()) s.Add(v.X + "," + v.Y + "," + v.Z + ":" + v.Color);
                return s;
            }
            string Log(string id) { string f = store.SpacePaths(id).MetaLogFile; return File.Exists(f) ? File.ReadAllText(f) : ""; }

            // 舵輪 wh（8,4,4）：輪 色 10（z 2 一排）、支柱 色 20（z 0 一排）；船 sa（16）：關聯 wl → wh、模型 w ← wh
            Check(Create("wh", "8,4,4") && Create("sa", "16"), "兩件作品建立");
            Check(Box("p", "wh", 0, 7, 1, 2, 10).ExitCode == 0 && Box("p", "wh", 2, 5, 1, 0, 20).ExitCode == 0, "舵輪的格（輪 8、支柱 4）");
            Check(Sub("p", "sa", "link", ("name", "wl"), ("source_work", "wh"), ("at", "0,0,0")).ExitCode == 0, "作者掛關聯 wl");
            var m0 = Sub("p", "sa", "model", ("name", "w"), ("source_work", "wh"), ("at", "4,4,8"), ("pivot", "0,0,0"), ("scale", "1"), ("meta", "{\"kind\":\"wheel\"}"));
            Check(m0.ExitCode == 0, "作者掛模型 w：" + SculptRoom.All(m0));

            // ① 不是作者：模型、固定點、關聯
            var q1 = Sub("q", "sa", "model", ("name", "w2"), ("source_work", "wh"), ("at", "8,8,8"), ("pivot", "0,0,0"), ("scale", "1"));
            Check(q1.ExitCode == 0, "🔴 q（不是作者）掛模型應放行：" + SculptRoom.All(q1));
            Check(Log("sa").Contains("\"persona\":\"q\"") && Log("sa").Contains("\"owner\":\"p\"") && Log("sa").Contains("\"part\":\"model\""), "q 掛模型應記進 sa 的 meta_log（persona q、owner p）：" + Log("sa"));
            Check(Sub("q", "sa", "unmodel", ("name", "w2")).ExitCode == 0, "q 拿掉模型應放行");
            Check(Sub("q", "wh", "anchor", ("name", "hub"), ("at", "0,1,2")).ExitCode == 0 && Log("wh").Contains("\"part\":\"anchor\""), "q 加固定點應放行並留痕");
            Check(Sub("q", "sa", "link", ("name", "wl9"), ("source_work", "wh"), ("at", "8,0,0")).ExitCode == 0 && Sub("q", "sa", "unlink", ("name", "wl9")).ExitCode == 0, "q 加、拿關聯應放行");
            int aLogP = Log("wh").Split('\n').Length;
            Check(Sub("p", "wh", "anchor", ("name", "hub2"), ("at", "1,1,2")).ExitCode == 0 && Log("wh").Split('\n').Length == aLogP, "作者自己改不記 meta_log（跟以前一樣）");

            // ② 反向對照：q 動格子照舊擋
            int evSa = Events("sa");
            var qb = Box("q", "sa", 0, 0, 0, 0, 50);
            Check(qb.ExitCode == 2 && Events("sa") == evSa, "🔴 q 落格子應擋（exit 2、事件沒多）：exit " + qb.ExitCode);

            // ③ 拆件
            var before = Composed("sa");
            var linksBefore = SCP_SculptLinks.Read(store.SpacePaths("sa")).links.ConvertAll(l => l.name);
            var pv = Sub("q", "wh", "split", ("into", "wh-stand"), ("colors", "20"), ("twin", "{name}座"));
            Check(pv.ExitCode == 0 && !store.Exists("wh-stand") && SculptRoom.V(pv, "split_voxels") == "4" && SculptRoom.V(pv, "split_mismatch") == "0"
                  && SculptRoom.V(pv, "split_twin_links") == "1" && SculptRoom.V(pv, "split_twin_models") == "1", "預覽：搬 4 格、雙生關聯 1、模型 1、一個檔都沒寫：" + SculptRoom.All(pv));
            int evWh = Events("wh");
            var sp = Sub("q", "wh", "split", ("into", "wh-stand"), ("colors", "20"), ("twin", "{name}座"), ("twin_meta", "{\"kind\":\"wheel_stand\"}"), ("confirm", "1"));
            Check(sp.ExitCode == 0, "🔴 q 拆件應成功：" + SculptRoom.All(sp));
            if (sp.ExitCode == 0)
            {
                var b = store.Load("wh-stand");
                Check(b.owner == "p" && b.IsSub, "新子作品的作者照原作者（p）、標 sub：owner " + b.owner);
                Check(SCP_SculptStore.Load(store.SpacePaths("wh"), false).Voxels.Count == 8 && SCP_SculptStore.Load(store.SpacePaths("wh-stand"), false).Voxels.Count == 4, "wh 剩 8 格、wh-stand 4 格");
                Check(Events("wh") == evWh + 1, "wh 多一刀（split）");
                var after = Composed("sa");
                Check(before.SetEquals(after), "🔴 sa 組起來拆前拆後應逐格相同：拆前 " + before.Count + "、拆後 " + after.Count);
                var links = SCP_SculptLinks.Read(store.SpacePaths("sa")).links;
                int i = links.FindIndex(l => l.name == "wl");
                Check(i >= 0 && i + 1 < links.Count && links[i + 1].name == "wl座" && links[i + 1].work == "wh-stand" && string.Join(",", links[i + 1].at) == string.Join(",", links[i].at),
                      "雙生關聯插在原本的下一個、擺放照抄：" + string.Join("、", links.ConvertAll(l => l.name)));
                var models = SCP_SculptModels.ReadModels(store.SpacePaths("sa")).models;
                var mw = models.Find(m => m.name == "w"); var ms = models.Find(m => m.name == "w座");
                Check(mw != null && ms != null && ms.work == "wh-stand" && string.Join(",", ms.at) == string.Join(",", mw!.at) && string.Join(",", ms.pivot) == string.Join(",", mw.pivot)
                      && string.Join(",", ms.scale) == string.Join(",", mw.scale) && ms.meta.ContainsKey("kind") && ms.meta["kind"].AsString() == "wheel_stand" && mw.meta["kind"].AsString() == "wheel",
                      "雙生模型擺放照抄、meta 用 twin_meta（原本的 kind=wheel 不抄）");
                Check(Log("sa").Contains("\"part\":\"split\"") && Log("wh").Contains("\"part\":\"split\""), "拆件記進 sa、wh 的 meta_log");
            }

            // ④ 反向對照：原作品自己的格蓋住它關聯的格 ⇒ 擋
            Check(Create("cv", "4,4,4") && Create("ch", "1,1,1"), "cv、ch 建立");
            Check(Box("p", "ch", 0, 0, 0, 0, 40).ExitCode == 0 && Box("p", "cv", 0, 0, 0, 0, 30).ExitCode == 0 && Box("p", "cv", 1, 1, 0, 0, 31).ExitCode == 0
                  && Sub("p", "cv", "link", ("name", "c1"), ("source_work", "ch"), ("at", "0,0,0")).ExitCode == 0, "cv 的格 (0,0,0) 蓋住關聯 ch 的格");
            var bad = Sub("q", "cv", "split", ("into", "cv-x"), ("colors", "30"), ("twin", "{name}x"), ("confirm", "1"));
            Check(bad.ExitCode == 2 && !store.Exists("cv-x") && SculptRoom.V(bad, "split_mismatch") != "0", "🔴 拆了會露出關聯的格 ⇒ 應擋、沒建 cv-x：" + SculptRoom.All(bad));
            var all = Sub("q", "cv", "split", ("into", "cv-y"), ("colors", "30,31"), ("twin", "{name}y"), ("confirm", "1"));
            Check(all.ExitCode == 2 && !store.Exists("cv-y"), "選到全部格 ⇒ 應擋");
            var nop = Sub("q", "cv", "split", ("into", "cv-z"), ("colors", "99"), ("twin", "{name}z"));
            Check(nop.ExitCode == 2, "沒有符合的格 ⇒ 應擋");

            return new CheckRow(name, failures.Count == 0 ? "q 改模型、固定點、關聯並留痕；q 落格子擋下；拆件 4 格、雙生 1＋1、組起來逐格相同；露出關聯格、全選、沒選到都擋" : string.Join("；", failures),
                                failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, "例外：" + e, CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
