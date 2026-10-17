// 淨室驗收（TASK-0678 橫繩組與繩梯）——
// ① 展開：兩條母繩之間 z0 ＝ 較高的下端＋start、z1 ＝ 較低的上端−stop、每 step 格一道 ⇒ 道數與每道兩端（手算：直立的 a、斜的 b 線性內插）；
//    名字 組名.01…（由下往上）、子繩（兩端拉直、3 邊管）、組、顏色粗細照組。
// ② 跟著動：母繩換端點 ⇒ 橫繩不重下就換位置；min_span ⇒ 往上收攏到太短就停。
// ③ 繩梯：climb=1 ⇒ 每道 meta kind=ratline、ratlines、rung、rungs、climb、foot、head（組裝件座標）；沒 climb ⇒ 沒有 climb／foot／head。
// ④ 🔴 擋且零寫入：母繩不存在／step 越界／climb 沒給落腳點／名字跟繩撞／換母繩沒 overwrite；母繩上有橫繩 ⇒ unrope 擋。
// ⑤ schema：有橫繩組 ⇒ 4；拿掉 ⇒ 檔逐位元組回原樣；手改成 schema 3 卻有橫繩組 ⇒ 讀不了（⛔ 不默默丟掉）。
// ⑥ ⛔ 一格 voxel 都不動。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureRatlinesCleanRoom()
    {
        const string name = "雕刻橫繩組：兩條母繩＋step 等高展開（道數、兩端手算）／母繩換端點跟著走、min_span 收攏／climb ⇒ meta 帶繩梯落腳點／"
                            + "🔴 母繩不存在・step 越界・climb 沒落腳點・撞名・換母繩沒 overwrite ⇒ 擋且零寫入、母繩有橫繩 ⇒ unrope 擋／schema 4、拿掉逐位元組回原樣（淨室，TASK-0678）";
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
            SCP_SculptRenderers.Register(new SculptProbeRenderer());
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            SCP_CmdResult Sub(string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", "ship"), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            List<SCP_SculptRopeReading> Ropes() => SCP_SculptRopes.Resolve(store, "ship", SCP_SculptLinks.Tree(store, "ship"), SCP_SculptLinkSelection.Parse("", ""), SCP_SculptRopeSelection.Parse("", ""));
            bool Near(double[]? p, double x, double y, double z) => p != null && Math.Abs(p[0] - x) < 1e-9 && Math.Abs(p[1] - y) < 1e-9 && Math.Abs(p[2] - z) < 1e-9;
            string RopesFile() => store.SpacePaths("ship").RopesFile;

            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "ship"), ("title", "船"), ("size", "40,20,40"),
                           ("commission", "繩 ship"), ("commission_ref", "user:rope:ship"), ("account", "acct")).ExitCode == 0, "建作品 ship");
            Check(room.Run(("op", "box"), ("work", "ship"), ("persona", "p"), ("x1", "0"), ("x2", "0"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "50")).ExitCode == 0, "一格");
            int ev = SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count;
            foreach (var (n, at) in new[] { ("a0", "5,2,2"), ("a1", "5,2,30"), ("b0", "25,2,2"), ("b1", "20,2,30"), ("deck", "15,6,1"), ("top", "8,3,31") })
                Check(Sub("anchor", ("name", n), ("at", at)).ExitCode == 0, "固定點 " + n);
            Check(Sub("rope", ("name", "A"), ("from", "a0"), ("to", "a1")).ExitCode == 0 && Sub("rope", ("name", "B"), ("from", "b0"), ("to", "b1")).ExitCode == 0, "母繩 A（直立）、B（斜）");
            byte[] before = File.ReadAllBytes(RopesFile());

            // ── ④ 擋（零寫入）────────────────────────────────────────────
            foreach (var (why, args) in new[]
            {
                ("母繩不存在", new[] { ("name", "R"), ("rope_a", "A"), ("rope_b", "nope") }),
                ("step 越界", new[] { ("name", "R"), ("rope_a", "A"), ("rope_b", "B"), ("step", "0.1") }),
                ("climb 沒給落腳點", new[] { ("name", "R"), ("rope_a", "A"), ("rope_b", "B"), ("climb", "1") }),
                ("名字跟繩撞", new[] { ("name", "A"), ("rope_a", "A"), ("rope_b", "B") }),
                ("a b 同一條", new[] { ("name", "R"), ("rope_a", "A"), ("rope_b", "A") }),
                ("落腳點是繩上的點", new[] { ("name", "R"), ("rope_a", "A"), ("rope_b", "B"), ("climb", "1"), ("foot", "rope:A/0.5"), ("head", "top") }),
            })
            {
                var r = Sub("ratlines", args);
                Check(r.ExitCode == 2 && File.ReadAllBytes(RopesFile()).SequenceEqual(before), "🔴 " + why + " ⇒ 擋、零寫入（exit " + r.ExitCode + "）");
            }

            // ── ① 展開 ──────────────────────────────────────────────────
            // z0 ＝ max(2.5, 2.5) ＋ 2 ＝ 4.5；z1 ＝ min(30.5, 30.5) − 3 ＝ 27.5 ⇒ 4.5, 8.5 … 24.5 共 6 道
            var w = Sub("ratlines", ("name", "R"), ("rope_a", "A"), ("rope_b", "B"), ("step", "4"), ("start", "2"), ("stop", "3"), ("thickness", "0.3"), ("color", "#2b2620"), ("group", "ratlines/lower"));
            Check(w.ExitCode == 0 && SculptRoom.V(w, "ratlines_rungs") == "6", "加橫繩組 R ⇒ 6 道：" + SculptRoom.V(w, "ratlines_rungs"));
            var rs = Ropes().FindAll(r => r.Ratlines == "R");
            Check(rs.Count == 6 && string.Join(",", rs.ConvertAll(r => r.Path)) == "R.01,R.02,R.03,R.04,R.05,R.06", "名字 R.01…R.06：" + string.Join(",", rs.ConvertAll(r => r.Path)));
            for (int k = 0; k < rs.Count; k++)
            {
                double z = 4.5 + 4 * k, xb = 25.5 - 5 * (z - 2.5) / 28;
                var r = rs[k];
                Check(r.State == SCP_SculptRopeReading.Ok && r.Child && r.Geom != null && r.Geom.Sides == SCP_SculptRopes.ChildSides, "R." + (k + 1) + " ok、子繩、3 邊管");
                Check(Near(r.From, 5.5, 2.5, z) && Math.Abs(r.To![0] - xb) < 1e-9 && Math.Abs(r.To[2] - z) < 1e-9, "R." + (k + 1) + " 兩端：" + SCP_SculptRopes.Fmt(r.From) + " → " + SCP_SculptRopes.Fmt(r.To) + "（手算 z " + z + "、b 的 x " + xb + "）");
                Check(r.Group == "ratlines/lower" && r.Rope.color == "#2b2620" && r.Rope.thickness == 0.3, "R." + (k + 1) + " 組、色、粗照組");
                Check((string)r.Rope.meta["kind"].AsString() == "ratline" && r.Rope.meta["rung"].AsDouble() == k + 1 && r.Rope.meta["rungs"].AsDouble() == 6 && !r.Rope.meta.ContainsKey("climb"), "R." + (k + 1) + " meta（沒 climb ⇒ 沒有繩梯欄位）");
            }
            Check(SCP_SculptRopes.ReadRopes(store.SpacePaths("ship")).schema == SCP_SculptRopes.SchemaWithRatlines, "有橫繩組 ⇒ schema 4");

            // ── ② 跟著動 ────────────────────────────────────────────────
            Check(Sub("anchor", ("name", "b1"), ("at", "15,2,30"), ("overwrite", "1")).ExitCode == 0, "搬 b1");
            var moved = Ropes().FindAll(r => r.Ratlines == "R");
            double xb2 = 25.5 - 10 * (4.5 - 2.5) / 28;
            Check(moved.Count == 6 && Math.Abs(moved[0].To![0] - xb2) < 1e-9, "母繩換端點 ⇒ 第一道跟著走：b 端 x " + (moved.Count > 0 ? moved[0].To![0].ToString("0.###") : "—") + "（手算 " + xb2.ToString("0.###") + "）");
            Check(Sub("ratlines", ("name", "R"), ("min_span", "12")).ExitCode == 0, "min_span 12");
            var cut = Ropes().FindAll(r => r.Ratlines == "R");
            // 第 k 道長 ＝ 25.5 − 10(z−2.5)/28 − 5.5（同一個 y、同一個 z）：z 4.5 ⇒ 19.29 … 第一道短於 12 的是 z 24.5（12.14）之後 ⇒ 6 道照舊；改 16 ⇒ 4.5、8.5、12.5（16.43）⇒ 3 道
            Check(cut.Count == 6, "min_span 12 ⇒ 6 道照舊：" + cut.Count);
            Check(Sub("ratlines", ("name", "R"), ("min_span", "16")).ExitCode == 0 && Ropes().FindAll(r => r.Ratlines == "R").Count == 3, "min_span 16 ⇒ 收攏到 3 道：" + Ropes().FindAll(r => r.Ratlines == "R").Count);

            // ── ③ 繩梯 ──────────────────────────────────────────────────
            var cl = Sub("ratlines", ("name", "R"), ("climb", "1"), ("foot", "deck"), ("head", "top"));
            var c1 = Ropes().Find(r => r.Path == "R.01");
            Check(cl.ExitCode == 0 && c1 != null && c1.Rope.meta["climb"].AsDouble() == 1 && c1.Rope.meta["foot"].AsString() == "15.5,6.5,1.5" && c1.Rope.meta["head"].AsString() == "8.5,3.5,31.5"
                  && c1.Rope.meta["ratlines"].AsString() == "R", "climb=1 ⇒ meta climb／foot／head／ratlines：" + (c1 == null ? "—" : SCP_SculptMeta.Describe(c1.Rope.meta)));
            byte[] withSet = File.ReadAllBytes(RopesFile());
            Check(Sub("ratlines", ("name", "R"), ("rope_b", "A")).ExitCode == 2 && File.ReadAllBytes(RopesFile()).SequenceEqual(withSet), "🔴 換母繩沒 overwrite ⇒ 擋、零寫入");
            var un = Sub("unrope", ("name", "B"));
            Check(un.ExitCode == 2 && File.ReadAllBytes(RopesFile()).SequenceEqual(withSet), "🔴 母繩 B 上有橫繩 ⇒ unrope 擋、零寫入（exit " + un.ExitCode + "）");

            // ── ⑤ schema ───────────────────────────────────────────────
            string text = File.ReadAllText(RopesFile());
            File.WriteAllText(RopesFile(), text.Replace("\"schema\": 4", "\"schema\": 3"));
            bool loud = false;
            try { SCP_SculptRopes.ReadRopes(store.SpacePaths("ship")); } catch (InvalidOperationException) { loud = true; }
            Check(loud, "手改成 schema 3 卻有橫繩組 ⇒ 讀不了（⛔ 不默默丟掉）");
            File.WriteAllBytes(RopesFile(), withSet);
            Check(Sub("unratlines", ("name", "R")).ExitCode == 0 && File.ReadAllBytes(RopesFile()).SequenceEqual(before), "unratlines ⇒ 檔逐位元組回到沒加之前");
            Check(Ropes().TrueForAll(r => r.Ratlines.Length == 0), "拿掉之後沒有展開的橫繩");

            // ── ⑥ voxel 不動 ────────────────────────────────────────────
            Check(SCP_SculptStore.ListEvents(store.SpacePaths("ship")).Count == ev, "⛔ 橫繩組不進 events");
            readings.Add("6 道（4.5..24.5）、min_span 16 ⇒ 3 道、繩梯 meta foot 15.5,6.5,1.5");
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
