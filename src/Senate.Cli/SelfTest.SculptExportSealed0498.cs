// 淨室驗收（TASK-0498）：obj 匯出省略封閉面 —— 朝向封閉空腔（外面的空氣灌不到的空格）的面不出，作品資料一格都不動。
// 物理意義：面數只是第一格。挖一個封住的洞之後，省略版的 obj 要跟沒挖洞的實心立方體**逐字相同**（mtllib 那行除外）——
//   不然「還是 6 面」可能只是剛好；反向對照把洞開一個口通到外面，內壁就得照常輸出、跟 keep 版逐字相同。
// 省略數有算術上的答案（20³ 的洞 ＝ 6×20×20 ＝ 2400 個 voxel 面），不是拿同一支程式回讀自己。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptExportSealedCleanRoom()
    {
        const string name = "obj 匯出省略封閉面：100³ 實心 6 面／挖封住的洞仍 6 面且逐字相同／🔴 開口後內壁照常輸出／省略數＝6×20²／keep 照舊／region 判的是匯出那批格／作品不動（淨室，TASK-0498）";
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
            SCP_SculptRenderers.Register(null!);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", id), ("size", size),
                ("commission", "封閉面 " + id), ("commission_ref", "user:sealed-" + id), ("account", "acct")).ExitCode == 0;
            int Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id)).Count;
            int Voxels(string id) => store.Engine(store.Load(id), room.Data).LoadSpace().Voxels.Count;
            SCP_CmdResult Run(string id, string op, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", op), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            (string, string)[] Range(int x1, int x2, int y1, int y2, int z1, int z2) => new[]
            {
                ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()), ("z1", z1.ToString()), ("z2", z2.ToString()),
            };
            bool Box(string id, int a, int b, int color) => Run(id, "box", Range(a, b, a, b, a, b).Append(("color", color.ToString())).ToArray()).ExitCode == 0;
            bool Carve(string id, int x1, int x2, int y1, int y2, int z1, int z2) => Run(id, "carve", Range(x1, x2, y1, y2, z1, z2)).ExitCode == 0;
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            (SCP_CmdResult R, ObjStats S, string Body) Export(string id, string tag, params (string, string)[] more)
            {
                string aOut = Path.Combine(room.Root, tag + ".obj").Replace('\\', '/');
                var a = new List<(string, string)> { ("format", "obj"), ("out", aOut) };
                a.AddRange(more);
                var r = Run(id, "export", a.ToArray());
                if (!File.Exists(aOut)) return (r, new ObjStats(), "");
                // mtllib 那行帶著輸出檔名 ⇒ 比內容時拿掉
                string body = string.Join("\n", File.ReadAllLines(aOut).Where(l => !l.StartsWith("mtllib ", StringComparison.Ordinal)));
                return (r, ReadObj(aOut), body);
            }
            string Sealed(SCP_CmdResult r) => r.ExitCode + ":" + V(r, "faces") + "/" + V(r, "sealed") + "/" + V(r, "sealed_faces") + "/" + V(r, "sealed_cavities") + "/" + V(r, "sealed_cells");

            // ═══ ① 100³ 實心單色立方體 ⇒ 6 面（12 個三角形）═══
            Check(Create("big", "128"), "作品 big 建立");
            Check(Box("big", 0, 99, 19), "放 100³ 實心方塊");
            var solid = Export("big", "solid");
            readings.Add("100³ 實心：" + Sealed(solid.R));
            Check(Sealed(solid.R) == "0:6/omit/0/0/0", "實心 ⇒ 6 面、省略 0、空腔 0：" + Sealed(solid.R));
            Check(solid.S.Faces == 6 && solid.S.Flipped == 0, "obj 檔裡 6 個四邊形（12 個三角形）、朝外：" + solid.S.Faces);

            // ═══ ② 正中間挖一個完全封住的 20³ 空洞 ⇒ 仍 6 面，省略數 ＝ 6×20×20 ═══
            Check(Carve("big", 40, 59, 40, 59, 40, 59), "挖 20³ 封住的洞");
            int evHole = Events("big"), voxHole = Voxels("big");
            var hole = Export("big", "hole");
            var holeKeep = Export("big", "hole_keep", ("sealed", "keep"));
            readings.Add("挖洞：" + Sealed(hole.R) + "，keep " + Sealed(holeKeep.R));
            Check(Sealed(hole.R) == "0:6/omit/2400/1/8000", "封住的洞 ⇒ 6 面、省略 2400 個 voxel 面、1 個空腔 8000 格：" + Sealed(hole.R));
            Check(hole.Body.Length > 0 && hole.Body == solid.Body, "省略版 obj 跟沒挖洞的實心立方體逐字相同（mtllib 行除外）");
            Check(holeKeep.R.ExitCode == 0 && V(holeKeep.R, "faces") == "12" && V(holeKeep.R, "sealed") == "keep" && V(holeKeep.R, "sealed_faces") == "",
                  "sealed=keep ⇒ 洞的 6 面內壁照常輸出（12 面）、不報省略數：" + Sealed(holeKeep.R));
            Check(holeKeep.S.Flipped == 0, "keep 版每面朝向與 vn 同向");
            Check(Events("big") == evHole && Voxels("big") == voxHole && voxHole == 1000000 - 8000, "匯出不動作品：事件數、格數不變（" + voxHole + "）");

            // ═══ ③ 🔴 反向對照：洞頂開一條 1 格的通道到外面 ⇒ 內壁照常輸出，跟 keep 版逐字相同 ═══
            Check(Carve("big", 50, 50, 50, 50, 60, 99), "從洞頂往上開一條通道到外面");
            var open = Export("big", "open");
            var openKeep = Export("big", "open_keep", ("sealed", "keep"));
            readings.Add("開口：" + Sealed(open.R));
            Check(open.R.ExitCode == 0 && V(open.R, "sealed_faces") == "0" && V(open.R, "sealed_cavities") == "0",
                  "🔴 開口之後沒有封閉空腔 ⇒ 省略 0：" + Sealed(open.R));
            Check(int.TryParse(V(open.R, "faces"), out int openFaces) && openFaces > 12, "🔴 內壁與通道照常輸出（面數 > 12）：" + V(open.R, "faces"));
            Check(open.Body.Length > 0 && open.Body == openKeep.Body, "🔴 沒有空腔時省略版與 keep 版逐字相同");

            // ═══ ④ 小方塊：merge=none 的算術、與 stats structure 同一個空腔定義、region 判的是匯出那批格 ═══
            Check(Create("s", "16"), "作品 s 建立");
            Check(Box("s", 0, 9, 19) && Carve("s", 3, 6, 3, 6, 3, 6), "10³ 方塊挖 4³ 封住的洞");
            var none = Export("s", "s_none", ("merge", "none"));
            var noneKeep = Export("s", "s_none_keep", ("merge", "none"), ("sealed", "keep"));
            var greedy = Export("s", "s_greedy");
            readings.Add("10³ 挖 4³：none " + Sealed(none.R) + "，none keep " + V(noneKeep.R, "faces"));
            Check(Sealed(none.R) == "0:600/omit/96/1/64", "none ⇒ 外殼 6×10² ＝ 600 面、省略 6×4² ＝ 96：" + Sealed(none.R));
            Check(V(noneKeep.R, "faces") == "696", "none keep ⇒ 600＋96 ＝ 696 面：" + V(noneKeep.R, "faces"));
            Check(greedy.S.SameSurface(none.S) && greedy.S.Flipped == 0 && none.S.Flipped == 0, "greedy 與 none 省略後每色每朝向總面積相同、朝向對");
            var st = Run("s", "stats", ("structure", "1"));
            Check(SculptRoom.All(st).Contains("封閉空腔（6 連通）: 1 個，共 64 格"), "stats structure 量到同一個空腔（1 個 64 格）：" + SculptRoom.All(st));
            // region 只匯出 x 0..4 ⇒ 洞在 x=5 那側沒有牆 ⇒ 對這批格來說它通到外面
            var cut = Export("s", "s_cut", ("region", "0..4,0..9,0..9"));
            var whole = Export("s", "s_whole", ("region", "0..7,0..9,0..9"));
            Check(V(cut.R, "sealed_cavities") == "0" && V(cut.R, "sealed_faces") == "0", "region 切過洞 ⇒ 對匯出那批格不是封閉空腔：" + Sealed(cut.R));
            Check(V(whole.R, "sealed_cavities") == "1" && V(whole.R, "sealed_faces") == "96", "region 把洞整個包住 ⇒ 仍是 1 個空腔、省略 96：" + Sealed(whole.R));

            // ═══ ⑤ 外框太大算不動 ⇒ 照常輸出並明說（⛔ 不靜默當成沒有空腔）；壞值擋下 ═══
            Check(Create("far", "1024"), "作品 far 建立");
            Check(Run("far", "box", Range(0, 0, 0, 0, 0, 0).Append(("color", "19")).ToArray()).ExitCode == 0
                  && Run("far", "box", Range(700, 700, 700, 700, 700, 700).Append(("color", "19")).ToArray()).ExitCode == 0, "兩個角落各一格（外框 703³ 超過上限）");
            var far = Export("far", "far");
            Check(far.R.ExitCode == 0 && V(far.R, "sealed") == "skipped" && V(far.R, "faces") == "12" && SculptRoom.All(far.R).Contains("沒算"),
                  "外框太大 ⇒ sealed=skipped、照常輸出 12 面、明說沒算：" + Sealed(far.R));
            string aBad = Path.Combine(room.Root, "bad.obj");
            var bad = Run("s", "export", ("format", "obj"), ("out", aBad), ("sealed", "fancy"));
            Check(bad.ExitCode == 2 && !File.Exists(aBad), "sealed 壞值 exit 2 且不寫檔：" + bad.ExitCode);
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { SenateSculptureRewards.Factory = original; }
        string aRead = string.Join("；", readings);
        return failures.Count == 0
            ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + (aRead.Length > 0 ? "　讀數：" + aRead : ""), CheckResult.Fail);
    }
}
