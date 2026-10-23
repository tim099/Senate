// 淨室驗收：梯子的格子不限作者（Tim 2026-10-23「梯子格子只有作者能改 這個規定改掉 因為梯子跟遊戲有關(算是遊戲meta)」）——
//   ① 不是作者（q）在梯子作品（被關聯照 meta kind=ladders 引用的）上落子、move、undo ⇒ 放行，每一筆記進那件作品的 meta_log.jsonl（part ladders、persona q、owner p）
//   ② 作者自己改 ⇒ 不記 meta_log（跟以前一樣）
//   ③ 反向對照：同一艘船上別的關聯（kind=rooms）、船本身、沒有 meta 的關聯 ⇒ q 照舊擋（exit 2、事件沒多）；
//      作者把 kind=ladders 那條關聯拿掉 ⇒ 同一件作品 q 也擋（判準是關聯，不是名字）
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptLadderNotAuthorCleanRoom()
    {
        const string name = "雕刻：梯子作品（kind=ladders 關聯引用的）的格子不限作者 —— 落子、move、undo 放行並記 meta_log；🔴 別的關聯、船本身、拿掉關聯之後照舊擋（淨室，Tim 2026-10-23）";
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
                ("commission", "梯子 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string who, string id, string sub, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", who) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Box(string who, string id, int x, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", who), ("x1", x.ToString()), ("x2", x.ToString()), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", color.ToString()));
            int Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id)).Count;
            string Log(string id) { string f = store.SpacePaths(id).MetaLogFile; return File.Exists(f) ? File.ReadAllText(f) : ""; }
            int Lines(string id) => Log(id).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            // 甲板 deck（16）：關聯「梯子」→ lad（kind=ladders）、「艙房」→ rm（kind=rooms）、「雜項」→ bare（沒有 meta）
            Check(Create("deck", "16") && Create("lad", "8,4,4") && Create("rm", "8,4,4") && Create("bare", "8,4,4"), "四件作品建立");
            Check(Box("p", "lad", 0, 10).ExitCode == 0 && Box("p", "rm", 0, 10).ExitCode == 0 && Box("p", "bare", 0, 10).ExitCode == 0, "作者在三件子作品各放一格");
            Check(Sub("p", "deck", "link", ("name", "梯子"), ("source_work", "lad"), ("at", "0,0,0"), ("meta", "{\"kind\":\"ladders\"}")).ExitCode == 0
                  && Sub("p", "deck", "link", ("name", "艙房"), ("source_work", "rm"), ("at", "0,4,0"), ("meta", "{\"kind\":\"rooms\"}")).ExitCode == 0
                  && Sub("p", "deck", "link", ("name", "雜項"), ("source_work", "bare"), ("at", "0,8,0")).ExitCode == 0, "作者掛三條關聯");

            // ① q 在梯子作品上：落子、move、undo
            int ev = Events("lad");
            var qb = Box("q", "lad", 1, 11);
            Check(qb.ExitCode == 0 && Events("lad") == ev + 1, "🔴 q 在梯子作品落子應放行（多一筆事件）：" + SculptRoom.All(qb));
            Check(Log("lad").Contains("\"persona\":\"q\"") && Log("lad").Contains("\"owner\":\"p\"") && Log("lad").Contains("\"part\":\"ladders\"") && Log("lad").Contains("\"op\":\"box\""),
                "q 落子應記進 lad 的 meta_log（persona q、owner p、part ladders、op box）：" + Log("lad"));
            var qm = Sub("q", "lad", "move", ("region", "0..1,0..0,0..0"), ("delta", "2,0,0"));
            Check(qm.ExitCode == 0 && Log("lad").Contains("\"op\":\"move\"") && Log("lad").Contains("\"delta\":\"2,0,0\""), "q 在梯子作品 move 應放行並記 delta：" + SculptRoom.All(qm));
            var qu = Sub("q", "lad", "undo");
            Check(qu.ExitCode == 0 && Log("lad").Contains("\"op\":\"undo\""), "q 在梯子作品 undo 應放行並留痕：" + SculptRoom.All(qu));

            // ② 作者自己改不記
            int aLines = Lines("lad");
            Check(Box("p", "lad", 5, 12).ExitCode == 0 && Lines("lad") == aLines, "作者自己落子不記 meta_log");

            // ③ 反向對照：別的關聯、船本身、沒有 meta 的關聯 ⇒ 照舊擋
            foreach (string id in new[] { "rm", "deck", "bare" })
            {
                int e0 = Events(id);
                var r = Box("q", id, 3, 13);
                Check(r.ExitCode == 2 && Events(id) == e0 && Log(id).Length == 0, "🔴 q 在 " + id + " 落子應擋（exit 2、事件沒多、沒寫 meta_log）：exit " + r.ExitCode);
            }
            Check(Sub("q", "rm", "move", ("region", "0..0,0..0,0..0"), ("delta", "1,0,0")).ExitCode == 2, "🔴 q 在艙房作品 move 應擋");
            Check(Sub("p", "deck", "unlink", ("name", "梯子")).ExitCode == 0, "作者拿掉梯子那條關聯");
            int e1 = Events("lad");
            var after = Box("q", "lad", 6, 14);
            Check(after.ExitCode == 2 && Events("lad") == e1, "🔴 拿掉 kind=ladders 關聯之後 q 落子應擋：exit " + after.ExitCode);

            return new CheckRow(name, failures.Count == 0 ? "q 在梯子作品落子、move、undo 放行並記 meta_log；作者自己改不記；艙房、船本身、沒 meta 的關聯、拿掉關聯之後都擋" : string.Join("；", failures),
                                failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, "例外：" + e, CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
