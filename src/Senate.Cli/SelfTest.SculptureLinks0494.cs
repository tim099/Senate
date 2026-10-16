// 淨室驗收（TASK-0494）：關聯匯入 —— 只記關聯不複製 voxel、被關聯的作品改了跟著變、巢狀（甲板→船艙）、同一件關聯兩次（一門轉 180°）、
// 開關（長期 enabled／一次性 links=／hide_links=、路徑指到任一層、hide_links=self）、重疊與越界計數、刪子作品快取結果相同、
// 父作品落子不動子作品（linked_hits）、成環（含隔層、含停用的）／自己／不存在／放不下／保留字擋下、攤平（import 預覽、assemble）＋Credit＋版本跟著變、
// export 檔頭記版本、草稿＋關聯（子作品的草稿在父作品裡看得到）、觀測頁的行程內預覽走同一串設定。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureLinksCleanRoom()
    {
        const string name = "雕刻關聯匯入：不複製／子作品改了跟著變／巢狀／同一件兩次＋轉向／開關與路徑／重疊越界計數／刪快取相同／父刀不動子／成環擋下／攤平＋Credit／export 記版本／草稿＋關聯／觀測頁同一串設定（淨室，TASK-0494）";
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
            bool Create(string id, string size, string who = "p") => room.Run(("op", "work"), ("sub", "create"), ("persona", who), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "關聯 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_SculptSpace Own(string id) => store.Engine(store.Load(id), room.Data).LoadSpace();
            int Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id)).Count;
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
            SCP_CmdResult Box(string id, int x1, int x2, int y1, int y2, int z, int color)
                => Run(id, "box", ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            SCP_CmdResult Link(string parent, string nm, string child, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("name", nm), ("source_work", child), ("at", at) };
                a.AddRange(more);
                return Sub(parent, "link", a.ToArray());
            }
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            SCP_CmdResult Stats(string id, params (string, string)[] more) => Run(id, "stats", more);
            string Total(SCP_CmdResult r) => r.ExitCode + ":" + V(r, "total_voxels");

            // 作品：船殼 h（4 格）、甲板 dk（8 格，8×8×4）、船艙 cb（2 格，2×2×2）、砲 gun（2 格兩色，2×1×1）
            Check(Create("h", "16") && Create("dk", "8,8,4") && Create("cb", "2,2,2") && Create("gun", "2,1,1"), "四件作品建立");
            Check(Box("h", 0, 3, 0, 0, 0, 10).ExitCode == 0 && Box("dk", 0, 3, 0, 1, 0, 20).ExitCode == 0 && Box("cb", 0, 1, 0, 0, 0, 30).ExitCode == 0
                  && Box("gun", 0, 0, 0, 0, 0, 40).ExitCode == 0 && Box("gun", 1, 1, 0, 0, 0, 41).ExitCode == 0, "各自的格落地");
            int evH = Events("h");

            // ① 只記關聯：h→dk、dk→cb（巢狀）、h→gun 兩次（gunR 轉 180°）
            var l1 = Link("h", "deck", "dk", "4,4,4");
            Check(l1.ExitCode == 0 && Link("dk", "cabin", "cb", "1,1,1").ExitCode == 0 && Link("h", "gunL", "gun", "10,10,0").ExitCode == 0
                  && Link("h", "gunR", "gun", "10,12,0", ("turn", "180")).ExitCode == 0, "四條關聯（巢狀、同一件兩次）：" + SculptRoom.All(l1));
            Check(Events("h") == evH && Own("h").Voxels.Count == 4, "父作品 events 一筆都沒多、本作品仍 4 格（一格都沒複製）");
            var s1 = Stats("h");
            var comp = store.Engine(store.Load("h"), room.Data);
            Check(Total(s1) == "0:18" && V(s1, "link_placed") == "14", "stats ＝ 本作品 4 ＋ 甲板 8 ＋ 船艙 2 ＋ 砲 2×2 ＝ 18：" + Total(s1) + " placed=" + V(s1, "link_placed"));
            Check(V(s1, "links_used").Contains("deck=dk@") && V(s1, "links_used").Contains("deck/cabin=cb@") && V(s1, "links_used").Contains("gunR=gun@"), "links_used 列出每條（含巢狀路徑）：" + V(s1, "links_used"));
            string Cell(string id, int x, int y, int z, params (string, string)[] more)
            {
                string f = Path.Combine(room.Root, "cell_" + Guid.NewGuid().ToString("N")[..6] + ".txt").Replace('\\', '/');
                var a = new List<(string, string)> { ("format", "list"), ("out", f) };
                a.AddRange(more);
                var r = Run(id, "export", a.ToArray());
                if (r.ExitCode != 0 || !File.Exists(f)) return "x";
                foreach (string line in File.ReadAllLines(f)) if (!line.StartsWith("#") && line.StartsWith(x + "," + y + "," + z + ",")) return line.Split(',')[3];
                return "0";
            }
            Check(Cell("h", 4, 4, 4) == "20" && Cell("h", 5, 5, 5) == "30" && Cell("h", 10, 10, 0) == "40" && Cell("h", 11, 10, 0) == "41"
                  && Cell("h", 11, 12, 0) == "40" && Cell("h", 10, 12, 0) == "41", "位置與轉向：甲板 (4,4,4)、巢狀船艙 (5,5,5)、gunL 40/41、gunR 轉 180° 顏色對調");

            // ② 子作品改了 ⇒ 父作品下一次觀測就是新樣子（巢狀第二層也是）
            Check(Box("cb", 0, 0, 1, 1, 0, 31).ExitCode == 0 && Total(Stats("h")) == "0:19" && Cell("h", 5, 6, 5) == "31", "🔴 巢狀第二層（船艙）加 1 格 ⇒ 船殼也多 1 格、位置對");
            Check(Events("h") == evH, "父作品仍沒有新事件");

            // ③ 開關：一次性 links=／hide_links=（路徑指到任一層、self）、長期 enabled、links=all
            Check(Total(Stats("h", ("links", "none"))) == "0:4", "links=none ⇒ 只有本作品 4 格");
            Check(Total(Stats("h", ("hide_links", "deck"))) == "0:8", "hide_links=deck ⇒ 甲板連同船艙一起不見（4＋砲 4）");
            Check(Total(Stats("h", ("hide_links", "deck/cabin"))) == "0:16", "🔴 只關掉那一層的那一件（deck/cabin）⇒ 只少船艙 3 格");
            Check(Total(Stats("h", ("links", "deck"))) == "0:15", "links=deck ⇒ 本作品＋甲板＋它底下開著的船艙（4＋8＋3）");
            Check(Total(Stats("h", ("links", "deck"), ("hide_links", "self"))) == "0:11", "links=deck hide_links=self ⇒ 只看甲板（8＋3）");
            Check(Sub("h", "link", ("name", "gunR"), ("enabled", "0")).ExitCode == 0 && Total(Stats("h")) == "0:17", "長期關掉 gunR（只改開關）⇒ 少 2 格");
            Check(Total(Stats("h", ("links", "all"))) == "0:19", "links=all ⇒ 停用的也組進來");
            Check(Sub("h", "link", ("name", "gunR"), ("enabled", "1")).ExitCode == 0 && Total(Stats("h")) == "0:19", "開回來 ⇒ 又出現");
            var gr = SCP_SculptLinks.Read(store.SpacePaths("h")).links.Find(l => l.name == "gunR");
            Check(gr != null && gr.turn == 180, "只改開關時 turn 沒被預設值 0 蓋掉");
            Check(Stats("h", ("hide_links", "nope")).ExitCode == 2 && Stats("h", ("links", "deck/nope")).ExitCode == 2, "🔴 不存在的路徑 ⇒ exit 2（打錯字不會變成「什麼都沒關」）");
            // 點名順序不影響結果：船艙停用時，links=deck,deck/cabin 跟 links=deck/cabin,deck 都要把點名的船艙組進來（審查抓到）
            Check(Sub("dk", "link", ("name", "cabin"), ("enabled", "0")).ExitCode == 0, "船艙長期停用");
            string ab = Total(Stats("h", ("links", "deck,deck/cabin"))), ba = Total(Stats("h", ("links", "deck/cabin,deck")));
            Check(ab == "0:15" && ba == "0:15", "🔴 點名順序不影響：" + ab + "／" + ba);
            Check(Sub("dk", "link", ("name", "cabin"), ("enabled", "1")).ExitCode == 0, "船艙開回來");
            // overwrite 只改有給的格：關掉＋備註之後搬位置，開關／轉向／備註都留著
            Check(Sub("h", "link", ("name", "gunR"), ("enabled", "0"), ("note", "右舷")).ExitCode == 0
                  && Link("h", "gunR", "gun", "10,13,0", ("overwrite", "1")).ExitCode == 0, "gunR 關掉、加備註、再 overwrite 搬到 y=13");
            var moved = SCP_SculptLinks.Read(store.SpacePaths("h")).links.Find(l => l.name == "gunR");
            Check(moved != null && !moved.enabled && moved.turn == 180 && moved.note == "右舷" && moved.at[1] == 13, "🔴 overwrite 搬位置沒有把開關、轉向、備註默默重設");
            Check(Link("h", "gunR", "gun", "10,12,0", ("overwrite", "1"), ("enabled", "1")).ExitCode == 0 && Total(Stats("h")) == "0:19", "搬回來、開回來");
            Check(Run("h", "box", ("x1", "0"), ("x2", "0"), ("y1", "1"), ("y2", "1"), ("z1", "0"), ("z2", "0"), ("links", "none")).ExitCode == 2
                  && Run("h", "diff", ("from", "start"), ("links", "none")).ExitCode == 2 && room.Run(("op", "stats"), ("links", "none")).ExitCode == 2, "🔴 links 給在寫入類／diff／共用展區 ⇒ exit 2");

            // ④ 重疊（本作品的格優先）與越界（被關聯的作品長大了）都計數、附位置
            Check(Box("h", 4, 4, 4, 4, 4, 99).ExitCode == 0, "父作品在甲板的格上放一格");
            var ov = Stats("h");
            Check(Total(ov) == "0:19" && V(ov, "link_overlap") == "1" && Cell("h", 4, 4, 4) == "99", "重疊 1 格、本作品的格贏、總數 ＝ 5＋7＋3＋4：" + Total(ov) + " overlap=" + V(ov, "link_overlap"));
            Check(Sub("dk", "update", ("size", "16,8,4")).ExitCode == 0 && Box("dk", 15, 15, 0, 0, 0, 21).ExitCode == 0, "甲板長大、在 x=15 放一格（在船殼裡 ＝ x 19 越界）");
            var cl = Stats("h");
            Check(V(cl, "link_clipped") == "1" && Total(cl) == "0:19", "越界 1 格丟掉、計數：" + V(cl, "link_clipped"));
            var lst = Sub("h", "links");
            string lstReport = File.Exists(room.Letters + "/p/cmd/sculpture_work.md") ? File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md") : "";
            Check(lst.ExitCode == 0 && V(lst, "link_paths").Contains("deck/cabin") && lstReport.Contains("尺寸變過"), "sub=links 列出巢狀路徑、標出甲板尺寸變過：" + V(lst, "link_paths"));

            // ⑤ 刪被關聯作品的快取 ⇒ 組出來一樣；而且不會替它把快取寫回去（只讀）
            File.Delete(store.SpacePaths("dk").CacheFile);
            Check(Total(Stats("h")) == "0:19" && !File.Exists(store.SpacePaths("dk").CacheFile), "刪甲板快取 ⇒ 船殼照樣 19 格、甲板的快取沒被寫回來");

            // ⑥ 父作品的刀碰不到子作品：在甲板範圍刻 ⇒ 只刻本作品自己的那一格；回報 linked_hits
            int evDk = Events("dk");
            var cut = Run("h", "carve", ("x1", "4"), ("x2", "7"), ("y1", "4"), ("y2", "5"), ("z1", "4"), ("z2", "4"));
            Check(cut.ExitCode == 0 && V(cut, "placed") == "1" && V(cut, "linked_hits") == "8" && Events("dk") == evDk, "刻甲板那一塊 ⇒ 只刻掉本作品 1 格、linked_hits=8、甲板 events 不變：" + V(cut, "linked_hits"));
            Check(Cell("h", 4, 4, 4) == "20", "那一格回到甲板的顏色（甲板一直都在）");

            // ⑦ 成環（隔層、含停用的）、自己、不存在、放不下、保留字、名字含 /
            Check(Link("dk", "up", "h", "0,0,0").ExitCode == 2, "🔴 dk→h（h→dk→h）擋下");
            Check(Link("cb", "up", "h", "0,0,0").ExitCode == 2, "🔴 隔兩層 cb→h（h→dk→cb→h）擋下");
            Check(Sub("h", "link", ("name", "gunL"), ("enabled", "0")).ExitCode == 0 && Sub("h", "link", ("name", "gunR"), ("enabled", "0")).ExitCode == 0
                  && Link("gun", "up", "h", "0,0,0").ExitCode == 2, "🔴 h→gun 兩條都停用，gun→h 照樣擋（links=all 會走到）");
            Check(Sub("h", "link", ("name", "gunL"), ("enabled", "1")).ExitCode == 0 && Sub("h", "link", ("name", "gunR"), ("enabled", "1")).ExitCode == 0, "開回來");
            Check(Link("h", "me", "h", "0,0,0").ExitCode == 2 && Link("h", "ghost", "nope", "0,0,0").ExitCode == 2 && Link("h", "far", "gun", "15,0,0").ExitCode == 2
                  && Link("h", "self", "gun", "0,0,0").ExitCode == 2 && Link("h", "a/b", "gun", "0,0,0").ExitCode == 2 && Link("h", "all", "gun", "0,0,0").ExitCode == 2,
                  "🔴 關聯自己、不存在、放不下、保留字 self／all、名字含 / ⇒ 擋下");
            Check(Link("h", "deck", "gun", "0,0,0").ExitCode == 2, "🔴 同名改作品沒給 overwrite ⇒ 擋下");
            // TASK-0646：規則由 Tim 改成「不影響模型外觀的修改 ok」⇒ 非作者也能加關聯（記進 meta_log.jsonl）；加完當場拿掉，後面的格照舊
            Check(room.Run(("op", "work"), ("sub", "link"), ("work", "h"), ("persona", "q"), ("name", "x"), ("source_work", "gun"), ("at", "0,0,0")).ExitCode == 0
                  && File.ReadAllText(store.SpacePaths("h").MetaLogFile).Contains("\"persona\":\"q\"")
                  && room.Run(("op", "work"), ("sub", "unlink"), ("work", "h"), ("persona", "q"), ("name", "x")).ExitCode == 0, "非作者改關聯 ⇒ 放行並記進 meta_log（TASK-0646），再拿掉");
            Check(Sub("h", "update", ("size", "10")).ExitCode == 2, "🔴 縮小父作品會切掉關聯 ⇒ 擋下");

            // ⑧ 攤平：import 預覽（would_place ＝ 組好的格數、revision 跟著子作品變、Credit）、assemble（目標在來源樹上 ⇒ 擋）
            var pre1 = Sub("h", "import", ("at", "0,0,0"));
            Check(pre1.ExitCode == 0 && V(pre1, "would_place") == V(Stats("h"), "total_voxels"), "import 預覽 ＝ 攤平後的格數：" + V(pre1, "would_place"));
            Check(Run("gun", "stampvox", ("voxels", WriteList(room, "g.vox.txt", "0,0,0,42")), ("at", "0,0,0"), ("expect_pixels", "1"), ("overwrite", "1")).ExitCode == 0, "砲換一格顏色");
            var pre2 = Sub("h", "import", ("at", "0,0,0"));
            Check(V(pre2, "revision") != V(pre1, "revision"), "🔴 子作品一改 ⇒ import 的 revision 跟著變（expect_revision 會對不上）");
            // 上游 Credit：q 的作品 x 先被 assemble 進船艙 cb；攤平船殼時 x／q 也要在（照 assemble 的規矩，審查抓到）
            Check(Create("x", "2", "q") && room.Run(("op", "box"), ("work", "x"), ("persona", "q"), ("x1", "0"), ("x2", "0"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "88")).ExitCode == 0
                  && Sub("cb", "assemble", ("source_work", "x"), ("at", "1,1,1")).ExitCode == 0, "q 的作品 x 裝進船艙");
            Check(Create("copy", "16") && Sub("copy", "assemble", ("source_work", "h"), ("at", "0,0,0")).ExitCode == 0 && Own("copy").Voxels.Count == int.Parse(V(Stats("h"), "total_voxels")),
                  "assemble 來源有關聯 ⇒ 攤平成實際 voxel");
            var cr = Sub("copy", "history");
            string crReport = File.Exists(room.Letters + "/p/cmd/sculpture_work.md") ? File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md") : "";
            Check(int.Parse(V(cr, "credit_count")) >= 5 && crReport.Contains("（x）｜by q"), "Credit 記到 h、dk、cb、gun，連船艙裡 q 的 x 也在：" + V(cr, "credit_count"));
            Check(Sub("dk", "assemble", ("source_work", "h"), ("at", "0,0,0")).ExitCode == 2, "🔴 把 h 裝進 dk（dk 在 h 的關聯樹上）⇒ 擋下");

            // ⑨ export 檔頭記下每條關聯用的是哪個版本
            string exOut = Path.Combine(room.Root, "h.list.txt").Replace('\\', '/');
            var ex = Run("h", "export", ("format", "list"), ("out", exOut));
            string head = File.Exists(exOut) ? string.Join("\n", File.ReadAllLines(exOut).Where(l => l.StartsWith("#"))) : "";
            Check(ex.ExitCode == 0 && head.Contains("# link deck dk@") && head.Contains("# link deck/cabin cb@") && V(ex, "links_used").Length > 0, "list 檔頭與回傳值記版本：" + head);

            // ⑩ 草稿＋關聯：甲板開草稿試刻 ⇒ 船殼帶 draft=1 看得到、不帶看不到；船殼自己沒草稿 ⇒ diff draft=1 擋下
            int before = int.Parse(V(Stats("h"), "total_voxels"));
            Check(Run("dk", "box", ("x1", "0"), ("x2", "0"), ("y1", "3"), ("y2", "3"), ("z1", "0"), ("z2", "0"), ("color", "22"), ("draft", "1")).ExitCode == 0, "甲板在草稿裡加一格");
            var dv = Stats("h", ("draft", "1"));
            Check(V(Stats("h"), "total_voxels") == before.ToString() && V(dv, "total_voxels") == (before + 1).ToString() && V(dv, "links_used").Contains("(草稿)"),
                  "船殼不帶 draft 看不到、帶 draft=1 看得到甲板的草稿（links_used 標草稿）：" + V(dv, "total_voxels") + " " + V(dv, "links_used"));
            Check(Run("h", "diff", ("draft", "1")).ExitCode == 2, "🔴 船殼自己沒草稿 ⇒ diff draft=1 擋下");
            Check(Sub("dk", "discard").ExitCode == 0, "丟掉甲板的草稿");

            // ⑪ 觀測頁的行程內預覽走同一串設定：關聯、hide_links、mark
            Check(Sub("h", "mark", ("name", "now")).ExitCode == 2 && Sub("h", "mark", ("name", "m0"), ("upto", "start")).ExitCode == 0, "標 m0 ＝ start");
            var raw = new Dictionary<string, string> { ["data_root"] = room.Data, ["letters_root"] = room.Letters, ["work"] = "h", ["out"] = Path.Combine(room.Root, "gui.png") };
            bool ok1 = Cmd_Sculpture.TryPrepareView(raw, out SCP_SculptViewPlan p1, out _, out string e1);
            raw["hide_links"] = "deck";
            bool ok2 = Cmd_Sculpture.TryPrepareView(raw, out SCP_SculptViewPlan p2, out _, out string e2);
            raw.Remove("hide_links"); raw["mark"] = "m0";
            bool ok3 = Cmd_Sculpture.TryPrepareView(raw, out SCP_SculptViewPlan p3, out _, out string e3);
            Check(ok1 && ok2 && ok3 && p1.TotalVoxels == before && p2.TotalVoxels < p1.TotalVoxels && p3.TotalVoxels == before - Own("h").Voxels.Count,
                  "觀測頁預覽：組進關聯 " + p1.TotalVoxels + "、hide_links 生效 " + p2.TotalVoxels + "、mark=m0（本作品回到空的、關聯照現在）" + p3.TotalVoxels + "｜" + e1 + e2 + e3);

            // ⑪b 被關聯的作品重播不出來：觀測 exit 2；父作品的刀照落、exit 0、linked_hits=skipped（⛔ 已落的刀不能回 70 讓人重跑成第二刀）
            Check(Create("pp", "8") && Create("kk", "2") && Box("kk", 0, 0, 0, 0, 0, 7).ExitCode == 0 && Link("pp", "kid", "kk", "0,0,0").ExitCode == 0, "pp→kk");
            Directory.CreateDirectory(Path.Combine(store.SpacePaths("kk").Events, "2099-01-01"));
            string bogus = Path.Combine(store.SpacePaths("kk").Events, "2099-01-01", "000000_000_aaaaaa.json");
            File.WriteAllText(bogus, "{\"op\": \"bogus\"}");
            File.Delete(store.SpacePaths("kk").CacheFile);
            int evPp = Events("pp");
            var hurt = Box("pp", 3, 3, 3, 3, 3, 9);
            Check(hurt.ExitCode == 0 && V(hurt, "linked_hits") == "skipped" && Events("pp") == evPp + 1, "🔴 子作品壞了 ⇒ 父作品的刀照落、exit 0、linked_hits=skipped：" + hurt.ExitCode);
            Check(Stats("pp").ExitCode == 2 && Total(Stats("pp", ("links", "none"))) == "0:1", "觀測 exit 2 說哪件壞了；links=none 照樣看得到本作品");
            File.Delete(bogus);

            // ⑪c 轉軸固定：轉了 180° 的砲把自己加寬 ⇒ 船殼裡那兩格不能跟著平移（軸心用加關聯當下的尺寸）
            Check(Sub("gun", "update", ("size", "3,1,1")).ExitCode == 0 && Cell("h", 11, 12, 0) == "42" && Cell("h", 10, 12, 0) == "41",
                  "🔴 砲加寬到 3 格 ⇒ gunR 那兩格原地不動（42／41）：" + Cell("h", 11, 12, 0) + "／" + Cell("h", 10, 12, 0));

            // ⑫ unlink
            Check(Sub("h", "unlink", ("name", "gunR")).ExitCode == 0 && V(Stats("h"), "total_voxels") == (before - 2).ToString(), "unlink gunR ⇒ 少 2 格、砲本身沒動");
            Check(Own("gun").Voxels.Count == 2 && Sub("h", "unlink", ("name", "gunR")).ExitCode == 2, "砲還在；再 unlink 一次 ⇒ 擋下（沒有這條）");

            return new CheckRow(name, failures.Count == 0
                ? "只記關聯（父 events 不變）18 格＝4＋8＋2＋2×2、位置與 180° 轉向對；🔴 巢狀第二層改一格船殼也改；none／hide／路徑／self／enabled／all 全對；🔴 只關 deck/cabin 只少船艙；重疊本作品贏、越界丟掉並計數、尺寸變過標出；刪子快取相同且不寫回；父刀 linked_hits=8 不動子；🔴 成環（隔層、停用）／自己／不存在／放不下／保留字／縮小擋下、非作者改關聯放行並留痕（TASK-0646）；import 預覽＝攤平、🔴 子改 revision 變；assemble 攤平＋Credit、🔴 自己裝進自己擋；export 記版本；甲板草稿在船殼 draft=1 看得到；觀測頁預覽吃關聯／hide_links／mark；unlink"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    static string WriteList(SculptRoom iRoom, string iFile, params string[] iLines)
    {
        string path = Path.Combine(iRoom.Root, iFile).Replace('\\', '/');
        File.WriteAllText(path, string.Join("\n", iLines) + "\n");
        return path;
    }
}
