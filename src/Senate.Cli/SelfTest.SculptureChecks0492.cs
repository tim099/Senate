// 淨室驗收（TASK-0492）：通用的檢查搬進引擎 —— diff（兩個時間點之間放／刻／換色、差異清單）、stats 的結構讀數
// （元件、封閉空腔、鏡像差）、carvevox（照清單刻：清單外一格不動）、export format=list（純格子清單，可原樣貼回）。
// 每一項都帶一格反向對照：少一刀要報得出、斷一格要多一塊、開一個洞空腔要消失、清單外被刻到要量得到。
using System.Text;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureChecksCleanRoom()
    {
        const string name = "雕刻通用檢查：diff 分色差異＋清單／stats 元件・空腔・鏡像差／carvevox 照清單刻／export list 原樣貼回（淨室，TASK-0492）";
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
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", id), ("size", size),
                ("commission", "檢查 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_SculptSpace Space(string id) => store.Engine(store.Load(id), room.Data).LoadSpace();
            int Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id)).Count;
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Box(string id, int x1, int x2, int y, int z, int color)
                => Run(id, "box", ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            SCP_CmdResult Carve(string id, int x, int y, int z)
                => Run(id, "carve", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()));
            string List(string file, IEnumerable<string> lines)
            {
                string path = Path.Combine(room.Root, file).Replace('\\', '/');
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                return path;
            }
            SCP_CmdResult Stamp(string id, string list, int count, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("voxels", list), ("at", "0,0,0"), ("expect_pixels", count.ToString()) };
                a.AddRange(more);
                return Run(id, "stampvox", a.ToArray());
            }
            SCP_CmdResult Mark(string id, string nm, string phase = "") => phase.Length == 0
                ? room.Run(("op", "work"), ("sub", "mark"), ("work", id), ("persona", "p"), ("name", nm))
                : room.Run(("op", "work"), ("sub", "mark"), ("work", id), ("persona", "p"), ("name", nm), ("phase", phase));
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            string Pdc(SCP_CmdResult r) => r.ExitCode + ":" + V(r, "placed") + "/" + V(r, "carved") + "/" + V(r, "recolored");

            // ═══ ① diff ═══ A（2 格）→ 標 a、cut:begin → B 2 格、A 第一格換色、刻掉 A 第二格 → cut:end → C → D 放了又刻（中間改回原樣）
            Check(Create("t", "16"), "作品 t 建立");
            Check(Box("t", 0, 1, 0, 0, 10).ExitCode == 0, "A 落地");
            Check(Mark("t", "a").ExitCode == 0 && Mark("t", "cut", "begin").ExitCode == 0, "標 a、cut:begin");
            Check(Box("t", 4, 5, 0, 0, 20).ExitCode == 0, "B 落地");
            Check(Stamp("t", List("recolor.vox.txt", new[] { "0,0,0,30" }), 1, ("overwrite", "1")).ExitCode == 0, "A 第一格換色");
            string stampEvent = Path.GetFileName(SCP_SculptStore.ListEvents(store.SpacePaths("t"))[^1].Rel);
            Check(Carve("t", 1, 0, 0).ExitCode == 0, "刻掉 A 第二格");
            Check(Mark("t", "cut", "end").ExitCode == 0, "標 cut:end");
            Check(Box("t", 8, 8, 0, 0, 40).ExitCode == 0 && Box("t", 10, 10, 0, 0, 50).ExitCode == 0 && Carve("t", 10, 0, 0).ExitCode == 0, "C 落地；D 放了又刻");

            byte[] cacheBefore = File.ReadAllBytes(store.SpacePaths("t").CacheFile);
            int evBefore = Events("t");
            string diffList = Path.Combine(room.Root, "cut.diff.txt").Replace('\\', '/');
            var cut = Run("t", "diff", ("from", "cut:begin"), ("to", "cut:end"), ("out", diffList));
            Check(Pdc(cut) == "0:2/1/1" && V(cut, "events") == "3", "cut 區間 ⇒ 放 2／刻 1／換色 1、3 個事件：" + Pdc(cut) + " ev=" + V(cut, "events") + " " + SculptRoom.All(cut));
            string cutText = string.Join("|", SculptRoom.All(cut).Split('\n'));
            Check(cutText.Contains("c20×2") && cutText.Contains("c10×1") && cutText.Contains("10→30×1") && cutText.Contains("box×1") && cutText.Contains("carve×1") && cutText.Contains("stampvox×1"),
                  "分顏色、分 op 列出：" + cutText);
            string[] rows = File.Exists(diffList) ? File.ReadAllLines(diffList).Where(l => l.Length > 0 && l[0] != '#').ToArray() : Array.Empty<string>();
            Check(string.Join(";", rows) == "0,0,0,10,30;1,0,0,10,0;4,0,0,0,20;5,0,0,0,20", "差異清單逐行（x→y→z 排）：" + string.Join(";", rows));
            Check(File.Exists(diffList) && V(cut, "sha256") == SCP_SculptPy.Sha256Hex(File.ReadAllBytes(diffList)), "清單 sha256 ＝ 檔案位元組");
            // 🔴 反向對照：少一刀（終點停在刻之前）⇒ 刻掉數要少 1
            var missing = Run("t", "diff", ("from", "cut:begin"), ("to", stampEvent));
            Check(Pdc(missing) == "0:2/0/1", "🔴 終點停在刻之前（少一刀）⇒ 刻 0：" + Pdc(missing));
            // 放了又刻掉的 D 不算差異；只看兩端
            var tail = Run("t", "diff", ("from", "cut:end"));
            Check(Pdc(tail) == "0:1/0/0" && V(tail, "events") == "3", "cut:end → now：D 放了又刻不算，只有 C：" + Pdc(tail) + " ev=" + V(tail, "events"));
            var whole = Run("t", "diff", ("from", "start"), ("to", "now"));
            Check(whole.ExitCode == 0 && int.Parse(V(whole, "placed")) - int.Parse(V(whole, "carved")) == Space("t").Voxels.Count && V(whole, "recolored") == "0",
                  "start → now：放 − 刻 ＝ 現在總數：" + Pdc(whole) + "／" + Space("t").Voxels.Count);
            Check(Pdc(Run("t", "diff", ("from", "cut:begin"), ("to", "cut:end"), ("region", "4..9,0..0,0..0"))) == "0:2/0/0", "region 只算框裡的（B 兩格）");
            Check(Pdc(Run("t", "diff", ("from", "a"), ("to", "a"))) == "0:0/0/0", "同一刻 ⇒ 全 0");
            string firstFile = Path.GetFileName(SCP_SculptStore.ListEvents(store.SpacePaths("t"))[0].Rel);
            Check(Pdc(Run("t", "diff", ("from", firstFile), ("to", "a"))) == "0:0/0/0" && Pdc(Run("t", "diff", ("from", "start"), ("to", "a"))) == "0:2/0/0", "事件檔名、start 都當得了端點");
            Check(Run("t", "diff", ("from", "cut:end"), ("to", "cut:begin")).ExitCode == 2, "🔴 from 晚於 to ⇒ exit 2");
            Check(Run("t", "diff").ExitCode == 2 && Run("t", "diff", ("from", "nope")).ExitCode == 2 && Run("t", "diff", ("from", "cut")).ExitCode == 2, "🔴 沒給 from／不存在的標記／區間名沒說開始結束 ⇒ exit 2");
            Check(Run("t", "diff", ("from", "a"), ("mark", "a")).ExitCode == 2 && Run("t", "diff", ("from", "a"), ("out", "rel.txt")).ExitCode == 2, "🔴 diff 帶 mark、out 相對路徑 ⇒ exit 2");
            Check(File.ReadAllBytes(store.SpacePaths("t").CacheFile).AsSpan().SequenceEqual(cacheBefore) && Events("t") == evBefore, "diff 之後快取逐位元組不變、事件數不變");

            // ═══ ② stats 結構讀數 ═══ 3×3×3 空心殼（中心 1 格空腔）＋ 一條 5 格的梁 ＋ 一顆孤立的格
            Check(Create("s", "16"), "作品 s 建立");
            var shell = new List<string>();
            for (int x = 0; x < 3; x++) for (int y = 0; y < 3; y++) for (int z = 0; z < 3; z++)
                if (x != 1 || y != 1 || z != 1) shell.Add(x + "," + y + "," + z + ",141");
            for (int x = 5; x <= 9; x++) shell.Add(x + ",5,5,104");
            shell.Add("12,12,12,177");
            Check(Stamp("s", List("shell.vox.txt", shell), shell.Count).ExitCode == 0, "殼＋梁＋孤立格落地");
            SCP_CmdResult Stats(string id, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "stats"), ("work", id) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            string Struct(SCP_CmdResult r) => r.ExitCode + ":" + V(r, "components") + "/" + V(r, "largest_component") + "/" + V(r, "cavities") + "/" + V(r, "cavity_cells");
            var st = Stats("s", ("structure", "1"));
            Check(Struct(st) == "0:3/26/1/1", "殼＋梁＋孤立格 ⇒ 3 塊、最大 26、空腔 1 個 1 格：" + Struct(st) + "\n" + SculptRoom.All(st));
            Check(SculptRoom.All(st).Contains("小塊 1 格 @(12,12,12)") && SculptRoom.All(st).Contains("空腔 1 格 @(1,1,1)"), "最小那塊與空腔都附位置：" + SculptRoom.All(st));
            Check(Mark("s", "whole").ExitCode == 0, "標 whole");
            // 🔴 反向對照：梁中間斷一格 ⇒ 多一塊；殼底開一個洞 ⇒ 空腔消失
            Check(Carve("s", 7, 5, 5).ExitCode == 0 && Struct(Stats("s", ("structure", "1"))) == "0:4/26/1/1", "🔴 梁斷一格 ⇒ 4 塊：" + Struct(Stats("s", ("structure", "1"))));
            Check(Carve("s", 1, 1, 0).ExitCode == 0 && Struct(Stats("s", ("structure", "1"))) == "0:4/25/0/0", "🔴 殼底開洞 ⇒ 空腔 0：" + Struct(Stats("s", ("structure", "1"))));
            Check(Struct(Stats("s", ("structure", "1"), ("mark", "whole"))) == "0:3/26/1/1", "吃 mark：whole 那一刻仍是 3 塊、1 空腔");
            Check(Struct(Stats("s", ("structure", "1"), ("region", "0..2,0..2,0..2"))) == "0:1/25/0/0" && V(Stats("s", ("structure", "1"), ("region", "0..2,0..2,0..2")), "scoped_voxels") == "25", "region 只算殼");
            Check(Struct(Stats("s", ("structure", "1"), ("exclude_color", "141"))) == "0:3/2/0/0", "exclude_color=141 ⇒ 殼不算：梁兩段＋孤立格");
            var plainStats = store.Engine(store.Load("s"), room.Data).Stats();
            Check(plainStats.Lines.Count == 3 && V(Stats("s"), "components").Length == 0, "不給參數 ⇒ 與 python 時代同樣 3 行、不算結構");
            // 外框太大 ⇒ 空腔明說沒算（⛔ 不印 0）
            Check(Create("big", "800"), "作品 big（800³）建立");
            Check(Stamp("big", List("far.vox.txt", new[] { "0,0,0,141", "700,700,700,141" }), 2).ExitCode == 0, "兩格隔很遠");
            var far = Stats("big", ("structure", "1"));
            Check(far.ExitCode == 0 && V(far, "cavities") == "skipped" && V(far, "components") == "2" && SculptRoom.All(far).Contains("沒算"), "外框超過上限 ⇒ cavities=skipped、元件照算：" + Struct(far));

            // 鏡像差：y:8（格 c ↔ 16−c）—— 對稱一對、少一邊、顏色不同；y:7.5（c ↔ 15−c）
            Check(Create("m", "16"), "作品 m 建立");
            Check(Stamp("m", List("mir.vox.txt", new[] { "3,6,0,141", "3,10,0,141", "4,8,0,141", "5,7,0,141", "6,5,0,104", "6,11,0,173" }), 6).ExitCode == 0, "鏡像用的 6 格");
            string Mir(SCP_CmdResult r) => r.ExitCode + ":" + V(r, "mirror_missing_low") + "/" + V(r, "mirror_missing_high") + "/" + V(r, "mirror_color_diff");
            var mir = Stats("m", ("mirror", "y:8"));
            Check(Mir(mir) == "0:1/0/1" && SculptRoom.All(mir).Contains("(5,7,0) c141 的鏡像是空的") && SculptRoom.All(mir).Contains("(6,5,0) c104 ↔ 鏡像 c173"),
                  "y:8 ⇒ 低側缺 1（5,7,0）、顏色不同 1 對（6,5↔6,11）、鏡面上的 (4,8,0) 不算：" + Mir(mir) + "\n" + SculptRoom.All(mir));
            Check(Mir(Stats("m", ("mirror", "y:7.5"))) == "0:3/3/0", "y:7.5 ⇒ 中心換了，6 格全不對稱（低 3／高 3）：" + Mir(Stats("m", ("mirror", "y:7.5"))));
            var cropped = Stats("m", ("mirror", "y:8"), ("region", "0..15,0..9,0..0"));
            Check(Mir(cropped) == "0:1/0/0" && V(cropped, "mirror_skipped") == "2",
                  "region 切掉高側 ⇒ 鏡像落在框外的 2 格不比（不算缺）：" + Mir(cropped) + " skipped=" + V(cropped, "mirror_skipped"));
            Check(Stats("m", ("mirror", "q:1")).ExitCode == 2 && Stats("m", ("mirror", "y:7.3")).ExitCode == 2 && Stats("m", ("structure", "2")).ExitCode == 2
                  && Stats("m", ("structure", "1"), ("region", "0..1,0..1")).ExitCode == 2 && Stats("m", ("structure", "1"), ("exclude_color", "x")).ExitCode == 2,
                  "🔴 壞的 mirror／structure／region／exclude_color ⇒ exit 2");

            // ═══ ③ carvevox ═══ 一排 6 格（兩種色），照清單刻 3 格（其中 1 格本來就空）
            Check(Create("c", "16"), "作品 c 建立");
            Check(Stamp("c", List("row.vox.txt", new[] { "0,0,0,141", "1,0,0,141", "2,0,0,173", "3,0,0,173", "4,0,0,141", "0,1,0,141" }), 6).ExitCode == 0, "一排 6 格");
            Check(Carve("c", 4, 0, 0).ExitCode == 0, "先刻掉 (4,0,0)（讓清單裡有一格本來就空）");
            var keep = Space("c").Voxels.ToList();
            int evC = Events("c");
            string cutList = List("cut.vox.txt", new[] { "# 要刻的格", "1,0,0", "2,0,0,173", "4,0,0" });
            var cv = Run("c", "carvevox", ("voxels", cutList), ("at", "0,0,0"), ("expect_pixels", "3"));
            Check(cv.ExitCode == 0 && V(cv, "placed") == "2" && V(cv, "list_count") == "3" && V(cv, "already_empty") == "1" && V(cv, "charged") == "0",
                  "刻掉 2 ＝ 清單裡現在有東西的格數、本來就空 1、免費：" + SculptRoom.All(cv) + " " + V(cv, "placed"));
            var after = Space("c");
            int outside = keep.Count(v => !(v.X == 1 && v.Y == 0 && v.Z == 0) && !(v.X == 2 && v.Y == 0 && v.Z == 0) && after.Voxels.Get(v.X, v.Y, v.Z) != v.Color);
            Check(outside == 0 && after.Voxels.Count == keep.Count - 2, "清單外一格都沒動（逐格比）：動到 " + outside);
            var cutDiff = Run("c", "diff", ("from", Path.GetFileName(SCP_SculptStore.ListEvents(store.SpacePaths("c"))[evC - 1].Rel)));
            Check(Pdc(cutDiff) == "0:0/2/0", "用 diff 量同一刀：只刻 2、沒放沒換色：" + Pdc(cutDiff));
            var ev = SCP_SculptStore.ReadEvent(V(cv, "event_file"));
            Check(ev != null && ev.GetString("op", "") == "carve" && ev.GetString("mode", "") == "list" && ev["carved_voxels"].Count == 2 && ev["source"].GetString("sha256", "").Length == 64,
                  "事件記成 op=carve＋mode=list＋逐格 carved_voxels＋清單 sha256（舊重播器讀得懂）");
            // 🔴 反向對照：同一個框改用 AABB carve ⇒ 清單外的格會被刻到，逐格比要量得到
            var probe = store.Engine(store.Load("c"), room.Data);
            var before2 = Space("c").Voxels.ToList();
            probe.Carve(new SCP_SculptCarveArgs { X1 = 0, X2 = 3, Y1 = 0, Y2 = 1, Z1 = 0, Z2 = 0, Persona = "p" });
            var after2 = Space("c");
            int outside2 = before2.Count(v => !(v.X == 3 && v.Y == 0) && after2.Voxels.Get(v.X, v.Y, v.Z) != v.Color);
            Check(outside2 == 2, "🔴 AABB 刻同一個框 ⇒ 清單（只列 3,0,0）外被刻到 2 格要量得到：" + outside2);
            var undoAabb = room.Run(("op", "work"), ("sub", "undo"), ("work", "c"), ("persona", "p"));
            Check(undoAabb.ExitCode == 0 && Space("c").Voxels.Count == before2.Count, "undo 那一刀 AABB：" + SculptRoom.All(undoAabb));
            int evC2 = Events("c");
            Check(Run("c", "carvevox", ("voxels", List("wrong.vox.txt", new[] { "0,0,0,173" })), ("at", "0,0,0")).ExitCode == 5 && Space("c").Voxels.Get(0, 0, 0) == 141,
                  "🔴 清單顏色對不上現況 ⇒ 整刀拒絕、那格還在");
            Check(Run("c", "carvevox", ("voxels", cutList), ("at", "0,0,0"), ("expect_pixels", "4")).ExitCode == 5, "🔴 expect_pixels 對不上 ⇒ 拒絕");
            Check(Run("c", "carvevox", ("voxels", List("gone.vox.txt", new[] { "1,0,0", "4,0,0" })), ("at", "0,0,0")).ExitCode == 5, "🔴 清單上全是空的 ⇒ 拒絕（沒東西可刻）");
            Check(Run("c", "carvevox", ("voxels", List("oob.vox.txt", new[] { "0,0,0", "20,0,0" })), ("at", "0,0,0")).ExitCode == 5, "🔴 越界不帶 allow_clip ⇒ 拒絕");
            Check(Run("c", "carvevox", ("voxels", List("dup.vox.txt", new[] { "0,0,0", "0,0,0,141" })), ("at", "0,0,0")).ExitCode == 2
                  && Run("c", "carvevox", ("voxels", List("bad.vox.txt", new[] { "0,0" })), ("at", "0,0,0")).ExitCode == 2
                  && Run("c", "carvevox", ("voxels", cutList), ("at", "0,0,0"), ("facing", "x+")).ExitCode == 2
                  && Run("c", "carvevox", ("voxels", cutList)).ExitCode == 2, "🔴 重複格／壞行／給 facing／沒給 at ⇒ exit 2");
            Check(Events("c") == evC2, "被拒絕的那幾刀零事件");
            var clip = Run("c", "carvevox", ("voxels", List("oob2.vox.txt", new[] { "0,0,0", "20,0,0" })), ("at", "0,0,0"), ("allow_clip", "1"));
            Check(clip.ExitCode == 0 && V(clip, "placed") == "1" && Space("c").Voxels.Get(0, 0, 0) == 0, "allow_clip 只刻界內那格：" + SculptRoom.All(clip));
            int nowC = Space("c").Voxels.Count;
            File.Delete(store.SpacePaths("c").CacheFile);
            Check(Space("c").Voxels.Count == nowC, "刪快取重播一致");
            Check(room.Run(("op", "work"), ("sub", "undo"), ("work", "c"), ("persona", "p")).ExitCode == 0 && Space("c").Voxels.Get(0, 0, 0) == 141, "undo carvevox ⇒ 那格連顏色回來");
            // 共用展區：按實際刻掉數收費（120 格 ⇒ ⌈120/100⌉ = 2 張）
            var sheet = new List<string>();
            for (int i = 0; i < 150; i++) sheet.Add((i % 25) + "," + (i / 25) + ",0,141");
            var paidStamp = room.Run(("op", "stampvox"), ("persona", "p"), ("account", "acct"), ("voxels", List("sheet.vox.txt", sheet)), ("at", "10,10,10"), ("expect_pixels", "150"), ("share", "0"));
            int consumed = pay.Consumed;
            var paidCut = room.Run(("op", "carvevox"), ("persona", "p"), ("account", "acct"), ("voxels", List("sheet_cut.vox.txt", sheet.Take(120).Select(l => l.Substring(0, l.LastIndexOf(','))))),
                                   ("at", "10,10,10"), ("expect_pixels", "120"), ("share", "0"));
            Check(paidStamp.ExitCode == 0 && paidCut.ExitCode == 0 && V(paidCut, "placed") == "120" && V(paidCut, "charged") == "2" && pay.Consumed == consumed + 2
                  && room.Engine().LoadSpace().Voxels.Count == 30, "展區照清單刻 120 收 2 張、剩 30 格：" + SculptRoom.All(paidCut));

            // ═══ ④ export format=list ═══ 讀出來 ＝ 重播的結果；原樣貼回另一件作品逐格相同
            // 先放一格 x 比較小的（表的插入序 ≠ x→y→z 序 ⇒ 「照 x→y→z 排」才量得到）
            Check(Box("t", 3, 3, 1, 0, 70).ExitCode == 0, "最後才放 (3,1,0)");
            string listOut = Path.Combine(room.Root, "t.list.txt").Replace('\\', '/');
            var ex = Run("t", "export", ("format", "list"), ("out", listOut));
            Check(ex.ExitCode == 0 && File.Exists(listOut) && V(ex, "sha256") == SCP_SculptPy.Sha256Hex(File.ReadAllBytes(listOut)), "export list 寫檔、sha256 對得上：" + SculptRoom.All(ex));
            Check(SCP_SculptEngine.TryParseVoxelList(File.Exists(listOut) ? File.ReadAllBytes(listOut) : Array.Empty<byte>(), out List<int[]> exported, out string why), "清單本身就是 stampvox 吃得下的格式：" + why);
            var tNow = Space("t");
            Check(exported.Count == tNow.Voxels.Count && exported.All(v => tNow.Voxels.Get(v[0], v[1], v[2]) == v[3]), "清單逐格 ＝ 重播的作品（" + exported.Count + "／" + tNow.Voxels.Count + "）");
            var sorted = exported.Select(v => v[0] * 1000000 + v[1] * 1000 + v[2]).ToList();
            Check(sorted.SequenceEqual(sorted.OrderBy(k => k)), "照 x→y→z 排");
            Check(Create("t2", "16") && Stamp("t2", listOut, exported.Count).ExitCode == 0, "貼回另一件作品");
            var t2 = Space("t2");
            Check(t2.Voxels.Count == tNow.Voxels.Count && tNow.Voxels.Entries().All(v => t2.Voxels.Get(v.X, v.Y, v.Z) == v.Color), "原樣貼回逐格相同");
            var exA = Run("t", "export", ("format", "list"), ("mark", "a"), ("out", Path.Combine(room.Root, "a.list.txt").Replace('\\', '/')));
            Check(exA.ExitCode == 0 && V(exA, "voxels") == "2", "export list 吃 mark（a 那一刻 2 格）");
            // 🔴 反向對照：作品之後再改一格 ⇒ 舊清單跟作品對不上，逐格比要量得到
            Check(Box("t", 12, 12, 0, 0, 60).ExitCode == 0, "匯出之後再放一格");
            var tLater = Space("t");
            int stale = tLater.Voxels.Entries().Count(v => !exported.Any(e => e[0] == v.X && e[1] == v.Y && e[2] == v.Z && e[3] == v.Color));
            Check(stale == 1, "🔴 匯出後再改一格 ⇒ 舊清單少 1 格量得到：" + stale);

            return new CheckRow(name, failures.Count == 0
                ? "diff：cut 區間放 2／刻 1／換色 1、分色分 op、清單逐行＋sha256；🔴 少一刀刻 0；放了又刻不算；放−刻＝總數；region／同一刻／檔名端點；🔴 反序、沒 from、壞標記、帶 mark、相對 out 擋下；快取不動｜"
                  + "stats：3 塊／最大 26／空腔 1；🔴 斷一格 4 塊、開洞空腔 0；mark／region／exclude 生效；不給參數 3 行；外框太大 skipped；鏡像 y:8 缺 1／色差 1、y:7.5 不對稱、region 外不比；🔴 壞參數擋下｜"
                  + "carvevox：刻 2＝現在有東西的格數、本來就空 1、清單外 0 格動；diff 量同一刀；事件 op=carve＋mode=list；🔴 AABB 刻同框量到清單外 2 格；🔴 顏色對不上／expect／全空／越界／重複／壞行／facing／沒 at 全零事件；allow_clip；重播＋undo；展區 120 收 2｜"
                  + "export list：sha256、stampvox 吃得下、逐格＝重播、排序、貼回逐格相同、吃 mark；🔴 匯出後改一格量得到"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
