// 淨室驗收（TASK-0497）：關聯與組裝的鏡像 —— 左右對稱的零件只做一份、另一邊 mirror=x 關聯進來 ⇒ 整件逐格鏡像相同；
// 鏡像＋turn 疊用的落點（手算，不借引擎的公式）；mirror=z；巢狀每一層的鏡像／轉向照順序疊、攤平（assemble／import）＝觀測現組；
// 零件改了兩邊一起變；🔴 鏡像軸寫錯（x 換成 y）⇒ 對稱檢查報得出哪一格不對；舊 links.json（schema 1、沒有 mirror 鍵）照舊、
// 沒用鏡像的檔維持 schema 1、用了才寫 schema 2；turn 只收 Z 的 0／90／180／270；壞的 mirror 擋下且零寫入。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureMirrorCleanRoom()
    {
        const string name = "雕刻鏡像（關聯／組裝）：對稱零件只做一份兩邊逐格鏡像／鏡像＋轉向落點（手算）／mirror=z／巢狀照順序疊＋攤平＝現組／零件改了兩邊一起變／🔴 軸寫錯報得出位置／舊 links.json 照舊＋schema 只在用到時升號／壞參數零寫入（淨室，TASK-0497）";
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
                ("commission", "鏡像 " + id), ("commission_ref", "user:mirror:" + id), ("account", "acct")).ExitCode == 0;
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
            SCP_CmdResult Dot(string id, int x, int y, int z, int color)
                => Run(id, "box", ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            SCP_CmdResult Link(string parent, string nm, string child, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("name", nm), ("source_work", child), ("at", at) };
                a.AddRange(more);
                return Sub(parent, "link", a.ToArray());
            }
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            // 觀測現組的每一格（export format=list ＝ 觀測類 op，照預設選擇組關聯）
            Dictionary<(int, int, int), int> Seen(string id)
            {
                var cells = new Dictionary<(int, int, int), int>();
                string f = Path.Combine(room.Root, "seen_" + Guid.NewGuid().ToString("N")[..6] + ".txt").Replace('\\', '/');
                if (Run(id, "export", ("format", "list"), ("out", f)).ExitCode != 0 || !File.Exists(f)) return cells;
                foreach (string line in File.ReadAllLines(f))
                {
                    if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                    var p = line.Split(',');
                    cells[(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]))] = int.Parse(p[3]);
                }
                return cells;
            }
            Dictionary<(int, int, int), int> OwnCells(string id)
            {
                var cells = new Dictionary<(int, int, int), int>();
                foreach (var v in Own(id).Voxels.Entries()) cells[(v.X, v.Y, v.Z)] = v.Color;
                return cells;
            }
            // 對稱檢查（獨立於引擎的公式）：沿 x 以 W 為寬，每一格的鏡像格要在、顏色要一樣；第一個不對的回一句帶位置的話，全對回 null
            string? SymmetryX(Dictionary<(int, int, int), int> cells, int width)
            {
                foreach (var kv in cells.OrderBy(k => k.Key))
                {
                    var (x, y, z) = kv.Key;
                    var twin = (width - 1 - x, y, z);
                    if (!cells.TryGetValue(twin, out int c)) return "(" + x + "," + y + "," + z + ")=" + kv.Value + " 的鏡像格 (" + twin.Item1 + "," + y + "," + z + ") 是空的";
                    if (c != kv.Value) return "(" + x + "," + y + "," + z + ")=" + kv.Value + " 的鏡像格 (" + twin.Item1 + "," + y + "," + z + ") 是 " + c;
                }
                return null;
            }
            string Diff(Dictionary<(int, int, int), int> a, Dictionary<(int, int, int), int> b)
            {
                foreach (var kv in a) if (!b.TryGetValue(kv.Key, out int c) || c != kv.Value) return "第一個不同 " + kv.Key + "=" + kv.Value + " vs " + (b.TryGetValue(kv.Key, out int d) ? d.ToString() : "空");
                foreach (var kv in b) if (!a.ContainsKey(kv.Key)) return "多出 " + kv.Key + "=" + kv.Value;
                return a.Count == b.Count ? "" : "格數不同";
            }

            // 零件 P：3×2×2，四格四色，三個軸都不對稱（翻哪一軸都看得出來）
            Check(Create("part", "3,2,2") && Dot("part", 0, 0, 0, 50).ExitCode == 0 && Dot("part", 1, 0, 0, 51).ExitCode == 0
                  && Dot("part", 2, 1, 0, 52).ExitCode == 0 && Dot("part", 0, 1, 1, 53).ExitCode == 0, "零件 P 四格");

            // ① 對稱：船 16×4×2，左舷 at x=1 照原樣、右舷 at x=12 mirror=x ⇒ 整件沿 x 逐格鏡像相同（只做一份）
            Check(Create("ship", "16,4,2"), "船");
            int evShip = Events("ship");
            var left = Link("ship", "L", "part", "1,1,0");
            var right = Link("ship", "R", "part", "12,1,0", ("mirror", "x"));
            Check(left.ExitCode == 0 && right.ExitCode == 0 && V(right, "link_mirror") == "x", "左舷照原樣、右舷 mirror=x：" + SculptRoom.All(right));
            var ship = Seen("ship");
            Check(ship.Count == 8 && SymmetryX(ship, 16) == null, "兩舷逐格鏡像相同（8 格）：" + (SymmetryX(ship, 16) ?? "ok") + " count=" + ship.Count);
            Check(Events("ship") == evShip && Own("ship").Voxels.Count == 0, "只記關聯：船自己 0 格、events 沒多");

            // ② 🔴 反向對照：右舷的軸寫錯（x 換成 y）⇒ 對稱檢查報得出哪一格不對
            Check(Link("ship", "R", "part", "12,1,0", ("mirror", "y"), ("overwrite", "1")).ExitCode == 0, "右舷改成 mirror=y（故意寫錯）");
            string? wrong = SymmetryX(Seen("ship"), 16);
            Check(wrong != null && wrong.Contains("("), "🔴 軸寫錯 ⇒ 對稱檢查報出位置：" + (wrong ?? "（沒抓到！）"));
            Check(Link("ship", "R", "part", "12,1,0", ("mirror", "x"), ("overwrite", "1")).ExitCode == 0 && SymmetryX(Seen("ship"), 16) == null, "改回 mirror=x ⇒ 又對稱");

            // ③ 零件改了 ⇒ 兩舷一起變（多 1 格 ⇒ 船多 2 格、仍對稱）
            Check(Dot("part", 2, 0, 1, 54).ExitCode == 0, "零件加一格");
            var ship2 = Seen("ship");
            Check(ship2.Count == 10 && SymmetryX(ship2, 16) == null, "零件改了 ⇒ 兩舷一起變（10 格、仍對稱）：" + ship2.Count + " " + (SymmetryX(ship2, 16) ?? "ok"));

            // ④ 鏡像＋轉向的落點（手算，不借引擎的公式）：mirror=x turn=90 at=4,8,0
            //   先翻（W=3：x ⇒ 2−x）再轉 90（H=2：x' ＝ 1−y、y' ＝ x）：(0,0,0)50 ⇒ (2,0,0) ⇒ (1,2,0)；(1,0,0)51 ⇒ (1,1,0)；(2,1,0)52 ⇒ (0,0,0)；
            //   (0,1,1)53 ⇒ (2,1,1) ⇒ (0,2,1)；(2,0,1)54 ⇒ (0,0,1) ⇒ (1,0,1)；再加 at
            Check(Create("rot", "12,12,4") && Link("rot", "a", "part", "4,8,0", ("mirror", "x"), ("turn", "90")).ExitCode == 0, "mirror=x turn=90 關聯");
            var rot = Seen("rot");
            var expectRot = new Dictionary<(int, int, int), int> { [(5, 10, 0)] = 50, [(5, 9, 0)] = 51, [(4, 8, 0)] = 52, [(4, 10, 1)] = 53, [(5, 8, 1)] = 54 };
            Check(Diff(expectRot, rot) == "", "mirror=x＋turn=90 落點 ＝ 手算：" + Diff(expectRot, rot));
            // mirror=z（D=2：z ⇒ 1−z）at=0,0,2
            Check(Link("rot", "b", "part", "0,0,2", ("mirror", "z")).ExitCode == 0, "mirror=z 關聯");
            var rz = Seen("rot");
            Check(rz.TryGetValue((0, 0, 3), out int z50) && z50 == 50 && rz.TryGetValue((0, 1, 2), out int z53) && z53 == 53 && rz.TryGetValue((2, 0, 2), out int z54) && z54 == 54,
                  "mirror=z：(0,0,0)50 ⇒ (0,0,3)、(0,1,1)53 ⇒ (0,1,2)、(2,0,1)54 ⇒ (2,0,2)");

            // ⑤ assemble 也吃 mirror（副本）：落點跟關聯一模一樣
            Check(Create("asm", "12,12,4") && Sub("asm", "assemble", ("source_work", "part"), ("at", "4,8,0"), ("mirror", "x"), ("turn", "90")).ExitCode == 0, "assemble mirror=x turn=90");
            Check(Diff(expectRot, OwnCells("asm")) == "", "assemble 的副本 ＝ 同參數的關聯：" + Diff(expectRot, OwnCells("asm")));

            // ⑥ 巢狀：船艙 cab（6×4×2）← 零件 mirror=y；甲板 dk（10×10×2）← 船艙 mirror=x turn=270；船殼 hull（16×16×2）← 甲板 mirror=xy turn=90
            //    ⇒ 攤平（assemble 到空作品）逐格 ＝ 觀測現組；import 預覽的格數 ＝ 現組格數
            Check(Create("cab", "6,4,2") && Create("dk", "10,10,2") && Create("hull", "16,16,2"), "三層作品");
            Check(Link("cab", "item", "part", "1,1,0", ("mirror", "y")).ExitCode == 0 && Dot("cab", 5, 3, 0, 60).ExitCode == 0
                  && Link("dk", "room", "cab", "2,1,0", ("mirror", "x"), ("turn", "270")).ExitCode == 0 && Dot("dk", 9, 9, 1, 61).ExitCode == 0
                  && Link("hull", "deck", "dk", "3,2,0", ("mirror", "xy"), ("turn", "90")).ExitCode == 0, "三層關聯，每層各自翻／轉");
            var hullSeen = Seen("hull");
            Check(Create("flat", "16,16,2") && Sub("flat", "assemble", ("source_work", "hull"), ("at", "0,0,0")).ExitCode == 0, "攤平 hull 到空作品");
            Check(hullSeen.Count == 7 && Diff(hullSeen, OwnCells("flat")) == "", "🔴 巢狀攤平 ＝ 觀測現組（7 格）：" + hullSeen.Count + " " + Diff(hullSeen, OwnCells("flat")));
            var pre = Sub("hull", "import", ("at", "0,0,0"));
            Check(pre.ExitCode == 0 && V(pre, "would_place") == hullSeen.Count.ToString(), "import 預覽格數 ＝ 現組：" + V(pre, "would_place"));
            // 疊的順序（手算最深那一格）：part (0,0,0)50 —mirror=y(H=2)⇒ (0,1,0) +at(1,1,0) ⇒ cab (1,2,0)
            //   cab → dk：mirror=x(W=6) ⇒ (4,2,0)；turn 270(W=6) ⇒ (y, W−1−x) ＝ (2,1,0)；+at(2,1,0) ⇒ dk (4,2,0)
            //   dk → hull：mirror=xy(10,10) ⇒ (5,7,0)；turn 90(H=10) ⇒ (H−1−y, x) ＝ (2,5,0)；+at(3,2,0) ⇒ hull (5,7,0)
            Check(hullSeen.TryGetValue((5, 7, 0), out int deep) && deep == 50, "最深那一格照順序疊（手算 hull (5,7,0)=50）：" + (hullSeen.TryGetValue((5, 7, 0), out int d0) ? d0.ToString() : "空"));

            // ⑦ 舊 links.json（schema 1、沒有 mirror 鍵）照舊；沒用鏡像的檔維持 schema 1、用了寫 schema 2
            Check(Create("old", "8") && Create("one", "1"), "舊檔用的作品");
            Check(Dot("one", 0, 0, 0, 70).ExitCode == 0, "一格");
            File.WriteAllText(store.SpacePaths("old").LinksFile, "{\n  \"schema\": 1,\n  \"links\": [\n    {\"name\": \"k\", \"work\": \"one\", \"at\": [2, 3, 1], \"turn\": 0, \"pivot\": [1, 1, 1], \"enabled\": true, \"created_at\": \"\", \"persona\": \"p\", \"note\": \"\"}\n  ]\n}\n");
            var oldSeen = Seen("old");
            Check(oldSeen.Count == 1 && oldSeen.TryGetValue((2, 3, 1), out int o70) && o70 == 70, "舊 links.json（沒有 mirror 鍵）照舊組：" + oldSeen.Count);
            Check(SCP_SculptLinks.Read(store.SpacePaths("ship")).schema == SCP_SculptLinks.SchemaWithMirror
                  && Link("old", "k2", "one", "4,4,4").ExitCode == 0 && SCP_SculptLinks.Read(store.SpacePaths("old")).schema == 1,
                  "用了鏡像 ⇒ schema 2；沒用的檔改過之後仍是 schema 1（舊版照樣讀）");
            Check(Sub("ship", "unlink", ("name", "R")).ExitCode == 0 && SCP_SculptLinks.Read(store.SpacePaths("ship")).schema == 1, "拿掉唯一一條鏡像 ⇒ 降回 schema 1");
            Check(Link("ship", "R", "part", "12,1,0", ("mirror", "x")).ExitCode == 0, "加回右舷");

            // ⑧ 開關不動鏡像；改鏡像要 overwrite；turn 只收 Z 的四個角度；壞的 mirror 擋下且零寫入
            Check(Sub("ship", "link", ("name", "R"), ("enabled", "0")).ExitCode == 0 && SCP_SculptLinks.Read(store.SpacePaths("ship")).links.Find(l => l.name == "R")!.mirror == "x"
                  && Sub("ship", "link", ("name", "R"), ("enabled", "1")).ExitCode == 0, "只改開關 ⇒ mirror 沒被預設值蓋掉");
            Check(Link("ship", "R", "part", "12,1,0", ("mirror", "y")).ExitCode == 2, "🔴 同名改鏡像沒給 overwrite ⇒ 擋下");
            string linksBefore = File.ReadAllText(store.SpacePaths("ship").LinksFile);
            int evAsm = Events("asm");
            Check(Link("ship", "bad", "part", "0,0,0", ("mirror", "q")).ExitCode == 2 && Link("ship", "bad", "part", "0,0,0", ("mirror", "xx")).ExitCode == 2
                  && Link("ship", "bad", "part", "0,0,0", ("turn", "45")).ExitCode == 2, "🔴 mirror=q／xx、turn=45（關聯）⇒ 擋下");
            Check(Sub("asm", "assemble", ("source_work", "part"), ("at", "0,0,2"), ("mirror", "w")).ExitCode == 2
                  && Sub("asm", "assemble", ("source_work", "part"), ("at", "0,0,2"), ("turn", "45")).ExitCode == 2, "🔴 mirror=w、turn=45（assemble）⇒ 擋下");
            Check(File.ReadAllText(store.SpacePaths("ship").LinksFile) == linksBefore && Events("asm") == evAsm, "擋下的都零寫入（links.json 逐字不變、assemble 沒新事件）");
            Check(Sub("ship", "link", ("name", "R2"), ("source_work", "part"), ("at", "12,1,0"), ("mirror", "X,none")).ExitCode == 2, "🔴 mirror 混了別的字 ⇒ 擋下（不猜）");

            return new CheckRow(name, failures.Count == 0
                ? "兩舷逐格鏡像相同（8 格、船自己 0 格）；🔴 x 換成 y ⇒ 報出「" + wrong + "」；零件加 1 格 ⇒ 兩舷 +2 仍對稱；mirror=x＋turn=90 與 mirror=z 落點 ＝ 手算；assemble 副本 ＝ 同參數關聯；三層各自翻轉 ⇒ 攤平 ＝ 現組（7 格）、import 預覽同數、最深格手算對；舊 links.json 照舊；schema 只在用鏡像時升 2、拿掉降回 1；開關不動鏡像；壞 mirror／turn 擋下且零寫入"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
