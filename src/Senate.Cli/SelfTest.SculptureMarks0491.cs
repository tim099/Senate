// 淨室驗收（TASK-0491）：作品的時間標記 —— 標某一刻／某一段；觀測（view／stats／export／slice／section）給 mark 看那一刻、
// hide 撤掉一段、only 只看一段；作品、事件、快取一格都不動；寫入類 op 帶標記擋下；undo 之前的標記仍看得到 undo 之前。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureMarksCleanRoom()
    {
        const string name = "雕刻時間標記：mark 看那一刻／hide 撤掉一段／only 只看一段／作品事件快取不動／寫入類帶標記擋下／undo 之前的標記（淨室，TASK-0491）";
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
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "hull"), ("title", "船殼"), ("size", "16"),
                           ("commission", "標記測試"), ("commission_ref", "user:marks"), ("account", "acct")).ExitCode == 0, "任務作品建立");
            var paths = store.SpacePaths("hull");
            SCP_SculptSpace Space() => store.Engine(store.Load("hull"), room.Data).LoadSpace();
            int Events() => SCP_SculptStore.ListEvents(paths).Count;
            SCP_CmdResult Box(int x1, int x2, int color) => room.Run(("op", "box"), ("work", "hull"), ("persona", "p"),
                ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", color.ToString()));
            SCP_CmdResult Mark(params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", "mark"), ("work", "hull"), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Stats(params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "stats"), ("work", "hull") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            string Total(SCP_CmdResult r) => r.ExitCode + ":" + SculptRoom.V(r, "total_voxels");

            // ① 一段時間線：A（2 格）→ 標 a、標 shell:begin → B（2 格）＋ 把 A 的一格改色 → 標 shell:end → C（1 格）→ 刻掉 B 的一格
            Check(Box(0, 1, 10).ExitCode == 0, "A 落地");
            var ma = Mark(("name", "a"), ("note", "只有 A"));
            Check(ma.ExitCode == 0 && SculptRoom.V(ma, "event_index") == "1" && SculptRoom.V(ma, "phase") == "point", "標 a ＝ 第 1 個事件：" + SculptRoom.All(ma));
            Check(Mark(("name", "shell"), ("phase", "begin")).ExitCode == 0, "標 shell:begin");
            Check(Box(4, 5, 20).ExitCode == 0, "B 落地");
            string recolor = Path.Combine(room.Root, "recolor.vox.txt");
            File.WriteAllText(recolor, "0,0,0,30\n");
            Check(room.Run(("op", "stampvox"), ("work", "hull"), ("persona", "p"), ("voxels", recolor), ("at", "0,0,0"), ("expect_pixels", "1"), ("overwrite", "1")).ExitCode == 0
                  && Space().Voxels.Get(0, 0, 0) == 30, "A 的一格改色（overwrite）");
            Check(Mark(("name", "shell"), ("phase", "end")).ExitCode == 0, "標 shell:end");
            Check(Box(8, 8, 40).ExitCode == 0, "C 落地");
            Check(room.Run(("op", "carve"), ("work", "hull"), ("persona", "p"), ("x1", "5"), ("x2", "5"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0")).ExitCode == 0
                  && Space().Voxels.Count == 4, "刻掉 B 的一格（區間之後）");

            // ② mark 看那一刻；作品、事件、快取都不動
            byte[] cacheBefore = File.ReadAllBytes(paths.CacheFile);
            int evBefore = Events();
            Check(Total(Stats(("mark", "a"))) == "0:2", "mark=a ⇒ 2 格：" + Total(Stats(("mark", "a"))));
            Check(Total(Stats(("mark", "shell:end"))) == "0:4", "mark=shell:end ⇒ A 2 ＋ B 2：" + Total(Stats(("mark", "shell:end"))));
            Check(Total(Stats()) == "0:4", "不給 mark ⇒ 現在 4 格（A 2、B 1、C 1）");
            var view = room.Run(("op", "view"), ("work", "hull"), ("mark", "a"), ("out", Path.Combine(room.Root, "a.png")));
            Check(view.ExitCode != 2 && SculptRoom.V(view, "view_mark") == "a", "view 吃 mark（沒有渲染器也要先過參數這一關）：" + SculptRoom.All(view));
            var ex = room.Run(("op", "export"), ("work", "hull"), ("mark", "a"), ("format", "vox"), ("out_dir", room.Root));
            Check(ex.ExitCode == 0 && SculptRoom.V(ex, "voxels") == "2", "export mark=a ⇒ 2 格：" + SculptRoom.All(ex));
            var sec = room.Run(("op", "section"), ("work", "hull"), ("mark", "a"), ("out", Path.Combine(room.Root, "a_sec.png")));
            Check(sec.ExitCode == 0, "section 吃 mark：" + SculptRoom.All(sec));
            Check(File.ReadAllBytes(paths.CacheFile).AsSpan().SequenceEqual(cacheBefore) && Events() == evBefore && Space().Voxels.Count == 4,
                  "看過去之後：快取逐位元組不變、事件數不變、作品仍是 4 格");

            // ③ hide 撤掉一段：B 回到沒有（刻掉的那格是區間之後改的，維持刻掉）、改色的 A 回到 10；only 只看那一段
            var hidden = store.Engine(store.Load("hull"), room.Data);
            hidden.ViewFilter = new SCP_SculptViewFilter();
            var iv = SCP_SculptMarks.ResolveInterval(SCP_SculptMarks.Read(paths), "shell");
            hidden.ViewFilter.Hide.Add((iv.Begin, iv.End, "shell"));
            var hs = hidden.LoadView();
            Check(hs.Voxels.Count == 3 && hs.Voxels.Get(0, 0, 0) == 10 && hs.Voxels.Get(1, 0, 0) == 10 && hs.Voxels.Get(4, 0, 0) == 0 && hs.Voxels.Get(8, 0, 0) == 40,
                  "hide=shell ⇒ A 兩格（改色撤回 10）＋ C，B 不見：" + hs.Voxels.Count);
            Check(Total(Stats(("hide", "shell"))) == "0:3", "CLI hide=shell ⇒ 3 格");
            var only = Stats(("only", "shell"));
            Check(Total(only) == "0:2", "only=shell ⇒ B 剩下那格＋改色的 A 那格：" + Total(only));
            Check(Total(Stats(("mark", "shell:end"), ("hide", "shell"))) == "0:2", "mark=shell:end＋hide=shell ⇒ 回到 A 兩格");

            // ④ 反向對照：解不出來 ⇒ exit 2、零寫入
            Check(Stats(("mark", "nope")).ExitCode == 2, "🔴 不存在的標記 ⇒ exit 2");
            Check(Stats(("mark", "shell")).ExitCode == 2 && SculptRoom.All(Stats(("mark", "shell"))).Contains("shell:begin"), "🔴 同名有開始與結束卻沒說哪一個 ⇒ exit 2 並教寫法");
            Check(Stats(("hide", "a")).ExitCode == 2, "🔴 單點標記拿來當區間 ⇒ exit 2");
            Check(room.Run(("op", "stats"), ("mark", "a")).ExitCode == 2, "🔴 沒帶 work ⇒ exit 2");
            int ev2 = Events();
            Check(room.Run(("op", "box"), ("work", "hull"), ("persona", "p"), ("x1", "9"), ("x2", "9"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "50"), ("mark", "a")).ExitCode == 2
                  && Events() == ev2 && Space().Voxels.Get(9, 0, 0) == 0, "🔴 寫入類 op 帶 mark ⇒ exit 2、零事件");
            Check(Mark(("name", "a")).ExitCode == 2, "🔴 同名同類 ⇒ 擋下（不默默蓋掉）");
            Check(Mark(("name", "a"), ("overwrite", "1"), ("upto", "start")).ExitCode == 0 && Total(Stats(("mark", "a"))) == "0:0", "overwrite=1 ⇒ 改標到 start（空的作品）");
            Check(Mark(("name", "x:y")).ExitCode == 2 && Mark(("name", "z"), ("upto", "2026-01-01/nope.json")).ExitCode == 2, "🔴 名字含 `:`、upto 指到不存在的事件 ⇒ exit 2");
            string firstFile = Path.GetFileName(SCP_SculptStore.ListEvents(paths)[0].Rel);
            var byName = Mark(("name", "first"), ("upto", firstFile));
            Check(byName.ExitCode == 0 && SculptRoom.V(byName, "event_index") == "1", "upto 給唯一檔名 ⇒ 第 1 個事件：" + SculptRoom.All(byName));
            Check(Mark(("name", "late"), ("phase", "end"), ("upto", firstFile)).ExitCode == 0
                  && Mark(("name", "late"), ("phase", "begin")).ExitCode == 2, "🔴 區間開始晚於結束 ⇒ exit 2");
            Check(room.Run(("op", "work"), ("sub", "mark"), ("work", "hull"), ("persona", "q"), ("name", "q")).ExitCode == 2, "🔴 非作者加標記 ⇒ exit 2");
            Check(Events() == ev2, "標記全程沒有新增事件（marks.json 不在 events/）");

            // ⑤ undo 之前的標記仍看得到 undo 之前；刪快取重播不受標記影響；history 列出標記
            Check(Mark(("name", "before-undo")).ExitCode == 0, "標 before-undo");
            Check(room.Run(("op", "work"), ("sub", "undo"), ("work", "hull"), ("persona", "p")).ExitCode == 0, "undo（刻掉 B 那一刀）");
            Check(Total(Stats()) == "0:5" && Total(Stats(("mark", "before-undo"))) == "0:4", "現在 5 格、before-undo 仍是 4 格：" + Total(Stats()) + "／" + Total(Stats(("mark", "before-undo"))));
            File.Delete(paths.CacheFile);
            Check(Space().Voxels.Count == 5, "刪快取重播 ＝ 現在（標記不參與重播）");
            var hist = room.Run(("op", "work"), ("sub", "history"), ("work", "hull"));
            var listed = room.Run(("op", "work"), ("sub", "marks"), ("work", "hull"));
            Check(hist.ExitCode == 0 && SculptRoom.V(hist, "mark_count") == SculptRoom.V(listed, "count") && SculptRoom.V(listed, "count") == "6",
                  "history 與 marks 都列出 6 個標記：" + SculptRoom.V(hist, "mark_count") + "／" + SculptRoom.V(listed, "count"));

            // ⑥ marks.json 壞了 ⇒ 不當成空的（寫回去會清掉所有標記）
            File.WriteAllText(paths.MarksFile, "{ not json");
            Check(Mark(("name", "after-corrupt")).ExitCode == 2 && File.ReadAllText(paths.MarksFile) == "{ not json", "🔴 marks.json 壞了 ⇒ 擋下、檔案原封不動");

            return new CheckRow(name, failures.Count == 0
                ? "mark=a 2 格、shell:end 4 格、現在 4 格；view／export／section 吃 mark；看過去後快取逐位元組不變、事件數不變；hide=shell 3 格（改色撤回）、only=shell 2 格；🔴 不存在／沒說開始結束／單點當區間／沒帶 work／寫入類帶 mark／同名同類／名字含冒號／事件不存在／開始晚於結束／非作者 全擋下且零事件；undo 前的標記仍是 4 格；刪快取重播不受影響；🔴 marks.json 壞了不清空"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
