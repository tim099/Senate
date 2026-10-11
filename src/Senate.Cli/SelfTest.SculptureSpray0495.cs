// 淨室驗收（TASK-0495）：噴漆 —— 射線／線段／折線／平滑曲線打出去，碰到的第一格（往裡 depth 層）換色；只換色、不放不刻。
// 物理意義：每一格驗收都配一格反向對照 —— 方向反過來打到另一面；轉彎之後才打得到（直線延長會落空）；
//   接縫三條規則各自有「換一個起點就打到別格」的對照；限制的原色之外一格不動；預覽清單與事件逐格相同；
//   噴完空格的集合一格不差。事件只用舊版認得的形狀（workedit／edit 只有舊欄位）⇒ 沒更新的 exe 也重播得出來。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureSprayCleanRoom()
    {
        const string name = "噴漆：射線／線段／折線／曲線打中第一格換色、預覽→expect_plan 才落刀、一束與沿線平行複製（船殼一整條）、only_color 擋下、depth、接縫三規則、草稿、undo、重播（淨室，TASK-0495）";
        var failures = new List<string>();
        var readings = new List<string>();
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
            bool Create(string id) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", id), ("size", "16"),
                ("commission", "噴漆 " + id), ("commission_ref", "user:spray-" + id), ("account", "acct")).ExitCode == 0;
            SCP_SculptSpace Space(string id) => store.Engine(store.Load(id), room.Data).LoadSpace();
            int Get(string id, int x, int y, int z) => Space(id).Voxels.Get(x, y, z);
            int Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id)).Count;
            string Shape(string id) => string.Join(";", Space(id).Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z).OrderBy(s => s, StringComparer.Ordinal));
            string Paint(string id) => string.Join(";", Space(id).Voxels.Entries().Select(v => v.X + "," + v.Y + "," + v.Z + "=" + v.Color).OrderBy(s => s, StringComparer.Ordinal));
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            bool Box(string id, int x1, int x2, int y1, int y2, int z1, int z2, int color) => Run(id, "box",
                ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()), ("z1", z1.ToString()), ("z2", z2.ToString()), ("color", color.ToString())).ExitCode == 0;
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            SCP_CmdResult Spray(string id, params (string, string)[] more) => Run(id, "spray", more);
            // 預覽 → 拿 plan_sha256 → 同樣的參數落刀
            SCP_CmdResult Commit(string id, params (string, string)[] more)
            {
                var pv = Spray(id, more);
                return Spray(id, more.Append(("confirm", "1")).Append(("expect_plan", V(pv, "plan_sha256"))).ToArray());
            }
            string Rays(SCP_CmdResult r) => r.ExitCode + ":" + V(r, "rays") + "/" + V(r, "rays_hit") + "/" + V(r, "rays_missed") + "/" + V(r, "rays_blocked") + "→" + V(r, "changed");
            List<string> PlanRows(string path) => File.ReadAllLines(path).Where(l => l.Length > 0 && l[0] != '#').ToList();

            // ═══ ① 單一道射線：打中第一格；預覽零事件；confirm 要 expect_plan；預覽清單 ＝ 事件逐格 ═══
            Check(Create("w"), "作品 w 建立");
            Check(Box("w", 5, 7, 5, 7, 5, 7, 10), "放 3³ 方塊（c10）");
            string shape0 = Shape("w");
            int ev0 = Events("w");
            string planPath = Path.Combine(room.Root, "plan_a.txt").Replace('\\', '/');
            var pv = Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("out", planPath));
            readings.Add("單道：" + Rays(pv));
            Check(pv.ExitCode == 0 && V(pv, "applied") == "0" && Rays(pv) == "0:1/1/0/0→1" && Events("w") == ev0, "預覽：1 道打中、換 1 格、零事件：" + Rays(pv));
            Check(SculptRoom.All(pv).Contains("原色 c10×1"), "預覽分原色列：" + SculptRoom.All(pv));
            Check(PlanRows(planPath).SequenceEqual(new[] { "6,5,6,10,30" }), "預覽清單 ＝ 打到 (6,5,6)：" + string.Join("|", PlanRows(planPath)));
            var noPlan = Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("confirm", "1"));
            var badPlan = Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("confirm", "1"), ("expect_plan", "deadbeef"));
            Check(noPlan.ExitCode == 2 && badPlan.ExitCode == 4 && Events("w") == ev0, "🔴 confirm 沒帶 expect_plan ⇒ 2、帶錯 ⇒ 4，都零事件：" + noPlan.ExitCode + "/" + badPlan.ExitCode);
            var a1 = Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("confirm", "1"), ("expect_plan", V(pv, "plan_sha256")), ("expect_placed", "1"));
            Check(a1.ExitCode == 0 && V(a1, "applied") == "1" && Events("w") == ev0 + 1, "帶對 expect_plan ⇒ 落刀、多 1 個事件：" + SculptRoom.All(a1));
            Check(Get("w", 6, 5, 6) == 30 && Get("w", 6, 6, 6) == 10, "第一格換色、第二格不動");
            var ev = SCP_SculptStore.ReadEvent(SCP_SculptStore.ListEvents(store.SpacePaths("w"))[^1].Full)!;
            var editKeys = ev["edit"].Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
            Check(ev.GetString("op", "") == "workedit" && ev["edit"].GetString("action", "") == "spray" && ev["source"].GetString("kind", "") == "spray",
                  "事件是 workedit／action=spray，最上層帶 source");
            Check(editKeys.All(k => new[] { "action", "after", "before", "credits", "persona", "target_event" }.Contains(k)),
                  "edit 只用舊版就有的欄位（沒更新的 exe 讀得進去）：" + string.Join(",", editKeys));
            var evRows = new List<string>();
            for (int i = 0; i < ev["edit"]["after"].Count; i++)
            {
                var b = ev["edit"]["before"][i]; var f = ev["edit"]["after"][i];
                evRows.Add(f.GetInt("x", -1) + "," + f.GetInt("y", -1) + "," + f.GetInt("z", -1) + "," + b.GetInt("color", -1) + "," + f.GetInt("color", -1));
            }
            Check(evRows.SequenceEqual(PlanRows(planPath)), "🔴 預覽清單與事件逐格相同：" + string.Join("|", evRows));

            // ═══ ② 落空 ⇒ 0、零事件；線段只到終點；方向反過來打到另一面；depth；本來就是這個色 ═══
            var miss = Spray("w", ("color", "30"), ("from", "0,0,0"), ("dir", "0,1,0"));
            var missOk = Spray("w", ("color", "30"), ("from", "0,0,0"), ("dir", "0,1,0"), ("confirm", "1"), ("expect_plan", V(miss, "plan_sha256")));
            Check(Rays(miss) == "0:1/0/1/0→0" && missOk.ExitCode == 3 && Events("w") == ev0 + 1, "什麼都沒碰到 ⇒ 預覽 0、落刀 exit 3 零事件：" + Rays(miss) + " / " + missOk.ExitCode);
            Check(V(Spray("w", ("color", "30"), ("from", "6,0,7"), ("to", "6,4,7")), "changed") == "0", "線段停在 (6,4,7) ⇒ 碰不到方塊");
            Check(V(Spray("w", ("color", "30"), ("from", "6,0,7"), ("to", "6,5,7")), "changed") == "1", "線段終點 (6,5,7) ⇒ 那一格算");
            string revPath = Path.Combine(room.Root, "plan_rev.txt").Replace('\\', '/');
            Spray("w", ("color", "30"), ("from", "6,15,6"), ("dir", "0,-1,0"), ("out", revPath));
            Check(PlanRows(revPath).SequenceEqual(new[] { "6,7,6,10,30" }), "🔴 方向反過來 ⇒ 打到另一面 (6,7,6)：" + string.Join("|", PlanRows(revPath)));
            string depthPath = Path.Combine(room.Root, "plan_depth.txt").Replace('\\', '/');
            Spray("w", ("color", "30"), ("from", "5,0,5"), ("dir", "0,1,0"), ("depth", "2"), ("out", depthPath));
            Check(PlanRows(depthPath).SequenceEqual(new[] { "5,5,5,10,30", "5,6,5,10,30" }), "depth=2 ⇒ 往裡兩層：" + string.Join("|", PlanRows(depthPath)));
            var again = Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"));
            Check(V(again, "changed") == "0" && V(again, "already") == "1", "打到本來就是噴漆色的格 ⇒ 不算換色、報 already");

            // ═══ ③ 一束：沿線、沿面平行掃過；only_color 擋下、限制外一格不動 ═══
            var line = Spray("w", ("color", "40"), ("from", "3,0,5"), ("dir", "0,1,0"), ("sweep", "6,0,0"));
            Check(Rays(line) == "0:7/3/4/0→3", "沿線 7 道：打中 3、落空 4：" + Rays(line));
            var plane = Spray("w", ("color", "40"), ("from", "3,0,5"), ("dir", "0,1,0"), ("sweep", "6,0,0"), ("sweep2", "0,0,2"));
            Check(Rays(plane) == "0:21/9/12/0→9" && SculptRoom.All(plane).Contains("c10×8 c30×1"), "沿面 21 道：打中 9、落空 12、分原色 c10×8 c30×1：" + Rays(plane) + " " + SculptRoom.All(plane));
            var only = Commit("w", ("color", "40"), ("from", "3,0,5"), ("dir", "0,1,0"), ("sweep", "6,0,0"), ("sweep2", "0,0,2"), ("only_color", "10"));
            readings.Add("面＋only_color：" + Rays(only));
            Check(Rays(only) == "0:21/8/12/1→8" && V(only, "applied") == "1", "only_color=10 ⇒ (6,5,6) 那一道被擋下、換 8 格：" + Rays(only));
            Check(Get("w", 6, 5, 6) == 30 && Get("w", 5, 5, 5) == 40 && Get("w", 7, 5, 7) == 40 && Get("w", 6, 6, 6) == 10, "🔴 限制的原色之外一格不動（擋下那一道後面也沒漆到）");

            // ═══ ④ 折線與平滑曲線：轉彎之後才打得到 ═══
            var straight = Spray("w", ("color", "50"), ("from", "6,1,1"), ("dir", "0,0,1"));
            string polyPath = Path.Combine(room.Root, "plan_poly.txt").Replace('\\', '/');
            var poly = Spray("w", ("color", "50"), ("path", "6,1,1;6,1,6;6,9,6"), ("out", polyPath));
            Check(V(straight, "changed") == "0" && PlanRows(polyPath).SequenceEqual(new[] { "6,5,6,30,50" }), "🔴 第一段直線延長會落空；折線轉彎後打到 (6,5,6)：" + string.Join("|", PlanRows(polyPath)));
            string curvePath = Path.Combine(room.Root, "plan_curve.txt").Replace('\\', '/');
            var curve = Spray("w", ("color", "50"), ("path", "6,1,1;6,1,6;6,9,6"), ("curve", "smooth"), ("out", curvePath));
            Check(Rays(curve) == "0:1/1/0/0→1" && PlanRows(curvePath).Count == 1 && PlanRows(curvePath)[0].Split(',')[1] == "5", "平滑曲線打中一格、在前面那一面（y=5）：" + string.Join("|", PlanRows(curvePath)));

            // ═══ ⑤ 噴完空格一格不多不少；diff 標得出是噴漆；undo／redo；刪快取重播 ═══
            Check(Shape("w") == shape0, "🔴 只換色：實心格的集合跟噴之前完全相同");
            var df = Run("w", "diff", ("from", "start"));
            Check(SculptRoom.All(df).Contains("workedit:spray×2"), "diff 分 op 列得出 workedit:spray：" + SculptRoom.All(df));
            string painted = Paint("w");
            Check(Run("w", "work", ("sub", "undo")).ExitCode == 0 && Get("w", 5, 5, 5) == 10 && Get("w", 6, 5, 6) == 30, "undo ⇒ 最後一刀噴漆退回");
            Check(Run("w", "work", ("sub", "redo")).ExitCode == 0 && Paint("w") == painted, "redo ⇒ 回來，逐格相同");
            File.Delete(store.SpacePaths("w").CacheFile);
            Check(Paint("w") == painted, "刪掉快取、從事件重播 ⇒ 逐格相同");

            // ═══ ⑥ 接縫三規則（各配一格換起點就打到別格的對照）═══
            Check(Create("s"), "作品 s 建立");
            Check(Box("s", 5, 7, 5, 7, 5, 7, 10) && Box("s", 5, 5, 4, 4, 6, 6, 40) && Box("s", 4, 4, 5, 5, 6, 6, 40) && Box("s", 6, 6, 9, 9, 6, 6, 50), "方塊＋三顆對照格");
            string Hit(params (string, string)[] more)
            {
                string p = Path.Combine(room.Root, "seam_" + Guid.NewGuid().ToString("N")[..6] + ".txt").Replace('\\', '/');
                Spray("s", more.Append(("color", "90")).Append(("out", p)).ToArray());
                return string.Join("|", PlanRows(p));
            }
            Check(Hit(("from", "4.5,0,7"), ("dir", "0,1,0")) == "5,5,7,10,90" && Hit(("from", "7.5,0,7"), ("dir", "0,1,0")) == "",
                  "② 貼著接縫走 ⇒ 算座標較大那一側：x=4.5 打到 x=5、x=7.5 走在 x=8 落空");
            Check(Hit(("from", "4,4,6"), ("dir", "1,1,0")) == "5,5,6,10,90", "③ 剛好穿過角 ⇒ 一步跨過，擦到角的 (5,4,6)／(4,5,6) 不算");
            Check(Hit(("from", "4,4.2,6"), ("dir", "1,1,0")) == "4,5,6,40,90", "🔴 ③ 對照：偏一點不穿角 ⇒ 先進 (4,5,6)");
            Check(Hit(("from", "6,8.5,6"), ("dir", "0,-1,0")) == "6,7,6,10,90", "① 起點在接縫上往 -y ⇒ 算前進方向那一格，(6,9,6) 不算");
            Check(Hit(("from", "6,8.5,6"), ("dir", "0,1,0")) == "6,9,6,50,90", "🔴 ① 對照：同一個起點往 +y ⇒ 第一格就是 (6,9,6)");

            // ═══ ⑦ 船殼一整條：沿一條線（像舷弧）平行複製＋帶寬 ═══
            Check(Create("h"), "作品 h 建立");
            Check(Box("h", 0, 9, 5, 5, 0, 9, 10), "一面 10×10 的牆（y=5）");
            var band = Commit("h", ("color", "60"), ("from", "0,0,2"), ("dir", "0,1,0"), ("sweep_path", "0,0,2;4,0,4;9,0,4"), ("sweep2", "0,0,1"));
            readings.Add("沿線一整條：" + Rays(band));
            int band60 = Space("h").Voxels.Entries().Count(v => v.Color == 60);
            Check(Rays(band) == "0:20/20/0/0→20" && band60 == 20, "沿折線 10 個點 × 帶寬 2 ⇒ 20 道全打中、換 20 格：" + Rays(band) + " 實數 " + band60);
            Check(Get("h", 0, 5, 2) == 60 && Get("h", 0, 5, 3) == 60 && Get("h", 4, 5, 4) == 60 && Get("h", 4, 5, 5) == 60 && Get("h", 9, 5, 4) == 60 && Get("h", 9, 5, 5) == 60,
                  "帶子跟著線走：x=0 在 z2..3、x=4 與 x=9 在 z4..5");
            Check(Get("h", 0, 5, 1) == 10 && Get("h", 0, 5, 4) == 10 && Get("h", 9, 5, 3) == 10 && Get("h", 9, 5, 6) == 10, "🔴 帶子外一格不動");
            Check(Spray("h", ("color", "60"), ("from", "0,0,2"), ("dir", "0,1,0"), ("sweep", "1,0,0"), ("sweep_path", "0,0,2;4,0,4")).ExitCode == 2, "sweep 與 sweep_path 同給 ⇒ 2");

            // ═══ ⑧ 草稿：落在草稿、作品不動；草稿開著時沒寫 draft ⇒ 擋 ═══
            var dr = Commit("h", ("color", "70"), ("from", "9,0,8"), ("dir", "0,1,0"), ("draft", "1"));
            Check(dr.ExitCode == 0 && V(dr, "applied") == "1" && Get("h", 9, 5, 8) == 10, "draft=1 ⇒ 作品本身不動：" + SculptRoom.All(dr));
            string dlist = Path.Combine(room.Root, "draft_list.txt").Replace('\\', '/');
            Check(Run("h", "export", ("format", "list"), ("draft", "1"), ("out", dlist)).ExitCode == 0 && File.ReadAllLines(dlist).Contains("9,5,8,70"), "草稿裡那一格是 c70");
            Check(Spray("h", ("color", "70"), ("from", "9,0,8"), ("dir", "0,1,0")).ExitCode == 2, "草稿開著時噴漆沒寫 draft=0／1 ⇒ 2");

            // ═══ ⑨ 參數擋下（零事件）═══
            int evW = Events("w");
            Check(Spray("w", ("from", "6,0,6"), ("dir", "0,1,0")).ExitCode == 2, "沒明寫 color ⇒ 2");
            Check(Spray("w", ("color", "0"), ("from", "6,0,6"), ("dir", "0,1,0")).ExitCode == 2, "color=0 ⇒ 2");
            Check(Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("to", "6,9,6")).ExitCode == 2, "dir 與 to 同給 ⇒ 2");
            Check(Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,0,0")).ExitCode == 2, "dir 零向量 ⇒ 2");
            Check(Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("depth", "0")).ExitCode == 2, "depth=0 ⇒ 2");
            Check(Spray("w", ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0"), ("out", "rel.txt")).ExitCode == 2, "相對 out ⇒ 2");
            Check(room.Run(("op", "spray"), ("persona", "p"), ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0")).ExitCode == 2, "共用展區（沒給 work）⇒ 2");
            Check(room.Run(("op", "spray"), ("work", "w"), ("persona", "q"), ("color", "30"), ("from", "6,0,6"), ("dir", "0,1,0")).ExitCode == 2, "不是作者 ⇒ 2");
            Check(Events("w") == evW, "擋下的全部零事件");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { SenateSculptureRewards.Factory = original; }
        string aRead = string.Join("；", readings);
        return failures.Count == 0
            ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + (aRead.Length > 0 ? "　讀數：" + aRead : ""), CheckResult.Fail);
    }
}
