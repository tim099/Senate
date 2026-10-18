// 淨室驗收（TASK-0716 繩端綁定的檢查／猜／修）——
// ① 分類：固定點端 ⇒ ok；座標端貼地板（≤ 15 cm）⇒ ok；在模型（轉過 30°、放大 4 倍的方塊）表面上 ⇒ bind；懸空 20 cm ⇒ snap；懸空 1 m ⇒ manual；
//    索具（meta kind=shroud）端點在 kind=gun 的模型上 ⇒ odd；端點在另一條直繩上 ⇒ onrope。
// ② 只讀：不給 fix ⇒ ropes.json 逐位元組不變、報表寫出來（每個要看的端點一行）。
// ③ fix=1（不是作者也行）：bind ⇒ 改寫成 model:box/<模型座標>、反算回來跟原本同一點（差 < 0.001 格）；snap ⇒ 挪到那一格表面（手算 10.5,30.5,1）；
//    其他端點一個字都不動；每改一端 meta_log.jsonl 記一筆（op ropecheck、before／after）；再跑一次 ⇒ 沒有要改的。
// ④ ⛔ 一格 voxel 都不動。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureRopeCheckCleanRoom()
    {
        const string name = "雕刻繩端檢查 ropecheck：固定點／貼格 ⇒ ok、模型表面 ⇒ bind、懸空 20 cm ⇒ snap、1 m ⇒ manual、索具在砲上 ⇒ odd、在別的繩上 ⇒ onrope／"
                            + "只讀零寫入、fix=1 不限作者：bind 反算同一點、snap 手算、其他不動、meta_log 每端一筆、再跑沒得改／一格都不動（淨室，TASK-0716）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            foreach (string p in new[] { "p", "q" }) Directory.CreateDirectory(Path.Combine(room.Letters, p, "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(new SculptProbeRenderer());
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            SCP_CmdResult Sub(string sub, string who, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", "ship"), ("persona", who) };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            List<SCP_SculptRopeReading> Ropes()
            {
                var tree = SCP_SculptLinks.Tree(store, "ship");
                var ms = SCP_SculptModels.Resolve(store, "ship", tree, new SCP_SculptLinkSelection(), new SCP_SculptModelSelection(), false);
                return SCP_SculptRopes.Resolve(store, "ship", tree, new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection(), null, null, ms);
            }
            double Dist(double[]? a, double[] b) => a == null ? double.MaxValue : Math.Sqrt((a[0] - b[0]) * (a[0] - b[0]) + (a[1] - b[1]) * (a[1] - b[1]) + (a[2] - b[2]) * (a[2] - b[2]));
            string RopesFile() => store.SpacePaths("ship").RopesFile;
            string Fmt(double[] p) => string.Join(",", p.Select(v => v.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)));

            // 作品 ship 40×40×40（0.05 m）：地板 z＝0 一整層；方塊模型（單位方塊 ×4、繞 z 轉 30°）擺在 (20,20,10)
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "ship"), ("title", "船"), ("size", "40,40,40"),
                           ("commission", "繩 ship"), ("commission_ref", "user:rope:ship"), ("account", "acct")).ExitCode == 0, "建作品 ship");
            Check(Sub("update", "p", ("meters_per_voxel", "0.05")).ExitCode == 0, "0.05 m／格");
            Check(room.Run(("op", "box"), ("work", "ship"), ("persona", "p"), ("x1", "0"), ("x2", "39"), ("y1", "0"), ("y2", "39"), ("z1", "0"), ("z2", "0"), ("color", "50")).ExitCode == 0, "地板");
            string cube = Path.Combine(room.Root, "cube.obj").Replace('\\', '/');
            File.WriteAllText(cube, CubeObj(true));
            Check(Sub("model", "p", ("name", "box"), ("file", cube), ("at", "20,20,10"), ("scale", "4"), ("rotate", "0,0,30")).ExitCode == 0, "方塊模型 box");
            int ev = SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count;
            foreach (var (n, at) in new[] { ("f1", "2,2,1"), ("f2", "36,2,1"), ("f3", "30,30,1"), ("f4", "30,30,30") })
                Check(Sub("anchor", "p", ("name", n), ("at", at)).ExitCode == 0, "固定點 " + n);

            // 方塊表面上的一點（檔案座標 (0.5,0.5,1) 那一面的中心）⇒ 組裝件座標
            var ms0 = SCP_SculptModels.Resolve(store, "ship", SCP_SculptLinks.Tree(store, "ship"), new SCP_SculptLinkSelection(), new SCP_SculptModelSelection(), false);
            Check(ms0.ResolvePoint("", "box/0.5,0.5,1", out double[]? onBox, out _, out _) == null && onBox != null, "方塊表面的點解得到");
            onBox ??= new double[] { 0, 0, 0 };
            readings.Add("方塊表面點 " + Fmt(onBox));

            Check(Sub("rope", "p", ("name", "solid"), ("from", "f1"), ("to", "5,5,1")).ExitCode == 0, "繩 solid（座標端貼地板 5 cm）");
            Check(Sub("rope", "p", ("name", "bind"), ("from", "f1"), ("to", Fmt(onBox))).ExitCode == 0, "繩 bind（端點在方塊表面）");
            Check(Sub("rope", "p", ("name", "snap"), ("from", "f2"), ("to", "10.5,30.5,4.5")).ExitCode == 0, "繩 snap（懸空 20 cm）");
            Check(Sub("rope", "p", ("name", "far"), ("from", "f2"), ("to", "36.5,10.5,21.5")).ExitCode == 0, "繩 far（懸空 1 m）");
            Check(Sub("rope", "p", ("name", "line"), ("from", "f3"), ("to", "f4"), ("slack", "1")).ExitCode == 0, "直繩 line");
            Check(Sub("rope", "p", ("name", "hang"), ("from", "f2"), ("to", "30.5,30.5,15.5")).ExitCode == 0, "繩 hang（端點在 line 上）");
            Check(Sub("meta", "p", ("part", "model"), ("name", "box"), ("meta", "{\"kind\":\"gun\"}")).ExitCode == 0, "方塊 meta kind=gun");
            Check(Sub("rope", "p", ("name", "odd"), ("from", "f3"), ("to", Fmt(onBox))).ExitCode == 0
                  && Sub("meta", "p", ("part", "rope"), ("name", "odd"), ("meta", "{\"kind\":\"shroud\"}")).ExitCode == 0, "繩 odd（索具，端點在砲上）");
            var before = Ropes();
            byte[] bytes0 = File.ReadAllBytes(RopesFile());

            // ── ② 只讀 ──────────────────────────────────────────────────────
            var r1 = Sub("ropecheck", "p");
            string V(string k) => SculptRoom.V(r1, k);
            readings.Add("只讀：ok " + V("ends_ok") + "、bind " + V("ends_bind") + "、snap " + V("ends_snap") + "、manual " + V("ends_manual") + "、odd " + V("ends_odd") + "、onrope " + V("ends_onrope"));
            Check(r1.ExitCode == 0 && V("ends_bind") == "1" && V("ends_snap") == "1" && V("ends_manual") == "1" && V("ends_odd") == "1" && V("ends_onrope") == "1" && V("ends_ok") == "9",
                  "分類：bind 1、snap 1、manual 1、odd 1、onrope 1、ok 9（" + readings[^1] + "）");
            Check(File.ReadAllBytes(RopesFile()).SequenceEqual(bytes0), "不給 fix ⇒ ropes.json 逐位元組不變");
            string rep = File.Exists(V("path")) ? File.ReadAllText(V("path")) : "";
            Check(rep.Contains("`bind` | to") || (rep.Contains("`bind`") && rep.Contains("| bind |")), "報表列出 bind 那一端");
            Check(rep.Contains("| snap |") && rep.Contains("10.5000,30.5000,1.0000") && rep.Contains("| manual |") && rep.Contains("| odd |") && rep.Contains("| onrope |"), "報表列出 snap（改成 10.5,30.5,1）、manual、odd、onrope");

            // ── ③ fix=1（q 不是作者）────────────────────────────────────────
            var r2 = Sub("ropecheck", "q", ("fix", "1"));
            Check(r2.ExitCode == 0 && SculptRoom.V(r2, "fixed") == "2", "fix=1（不是作者）⇒ 改了 2 端：" + SculptRoom.V(r2, "fixed") + "｜" + SculptRoom.All(r2));
            var file = SCP_SculptRopes.ReadRopes(store.SpacePaths("ship"));
            var bindRope = file.ropes.Find(r => r.name == "bind")!; var snapRope = file.ropes.Find(r => r.name == "snap")!;
            readings.Add("bind.to ⇒ " + bindRope.to + "｜snap.to ⇒ " + snapRope.to);
            Check(bindRope.to.StartsWith("model:box/", StringComparison.Ordinal) && snapRope.to == "10.5000,30.5000,1.0000", "改寫：bind ⇒ model:box/…、snap ⇒ 10.5000,30.5000,1.0000");
            var after = Ropes();
            var bAfter = after.Find(r => r.Path == "bind")!; var sAfter = after.Find(r => r.Path == "snap")!;
            Check(bAfter.State == SCP_SculptRopeReading.Ok && Dist(bAfter.To, onBox) < 1e-3, "bind 反算回來同一點（差 " + Dist(bAfter.To, onBox).ToString("0.######") + " 格）");
            Check(sAfter.State == SCP_SculptRopeReading.Ok && Dist(sAfter.To, new[] { 10.5, 30.5, 1.0 }) < 1e-9, "snap 挪到 (10.5,30.5,1)");
            foreach (string n in new[] { "solid", "far", "line", "hang", "odd" })
            {
                var a = before.Find(r => r.Path == n)!; var b = after.Find(r => r.Path == n)!;
                Check(a.Rope.from == b.Rope.from && a.Rope.to == b.Rope.to, n + " 一個字都沒動");
            }
            string log = File.Exists(store.SpacePaths("ship").MetaLogFile) ? File.ReadAllText(store.SpacePaths("ship").MetaLogFile) : "";
            int logN = log.Split('\n').Count(l => l.Contains("\"op\":\"ropecheck\""));
            Check(logN == 2 && log.Contains("\"persona\":\"q\"") && log.Contains("\"before\":\"10.5,30.5,4.5\""), "meta_log 每改一端一筆（2 筆、persona q、記 before）：" + logN);
            var r3 = Sub("ropecheck", "q", ("fix", "1"));
            Check(r3.ExitCode == 0 && SculptRoom.V(r3, "fixed") == "0" && SculptRoom.V(r3, "ends_bind") == "" && SculptRoom.V(r3, "ends_snap") == "", "再跑一次 ⇒ 沒有要改的");

            // ── ④ 格子 ──────────────────────────────────────────────────────
            Check(SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count == ev, "⛔ 一格 voxel 都不動（事件數不變）");
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
