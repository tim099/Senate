// 淨室驗收（TASK-0521）：大事件拆檔 —— 門檻調小（MaxEventBytes），用一般的刀驗整條路：
// ① 新寫的大刀拆成頭檔＋續檔：每檔 ≤ 上限、名字 X.json／X_p2.json…、頭檔帶 parts、續檔帶 part_of；從頭重播 ＝ 不拆的同一刀（另一件作品逐格對拍）。
// ② 歷史：續檔不另算一筆（undo_count 只數刀）。
// ③ 時間點：標記／upto 指到頭檔 ⇒ 延伸到最後一個續檔（不會停在半刀上）。
// ④ undo／redo 整刀來回（undo 本身也大 ⇒ 也拆），快取 ＝ 從頭重播。
// ⑤ split-events：試算一個位元組都不寫；confirm=1 就地拆、頭檔留原名、重播不變、拆完照樣 undo。
// ⑥ 🔴 反向對照：門檻照預設時同一刀是單一個檔、沒有 parts／part_of 欄；拆完再跑一次 split-events ⇒ 沒有超過上限的。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureEventSplitCleanRoom()
    {
        const string name = "雕刻大事件拆檔：大刀拆成頭檔＋續檔且每檔 ≤ 上限、重播＝不拆的同一刀／歷史只數刀／標記指頭檔延伸到整刀／undo・redo 整刀來回／split-events 試算零寫入、就地拆後重播不變／🔴 預設門檻單檔無新欄、拆完再掃為零（淨室，TASK-0521）";
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
            bool Create(string id) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", id), ("size", "16"),
                ("commission", "拆檔 " + id), ("commission_ref", "user:split:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            bool Slab(string id) => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", "0"), ("x2", "15"), ("y1", "0"), ("y2", "15"),
                ("z1", "0"), ("z2", "3"), ("color", "10")).ExitCode == 0;   // 1,024 格
            SCP_CmdResult Lift(string id) => Sub(id, "move", ("region", "0..15,0..15,0..3"), ("delta", "0,0,4"));   // 2,048 格變動
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            List<SCP_SculptStore.EventFile> Events(string id) => SCP_SculptStore.ListEvents(store.SpacePaths(id));
            // 比的是格子本身（排序後逐格逐色）—— ⛔ 不用 revision：它把水位檔名與列舉順序也雜湊進去，兩件作品、或快取與重播永遠不會相等
            static string Cells(SCP_SculptSpace iSpace)
            {
                var l = new List<(int, int, int, int)>();
                foreach (var v in iSpace.Voxels.Entries()) l.Add((v.X, v.Y, v.Z, v.Color));
                l.Sort();
                return l.Count + ":" + string.Join(";", l);
            }
            // 從頭重播（不讀不寫快取）—— 拆出來的檔一定要被讀到，這個數才算數
            string Replay(string id, string upto = "") => Cells(SCP_SculptStore.LoadView(store.SpacePaths(id), new SCP_SculptViewFilter { UptoEvent = upto }));
            string Cached(string id) => Cells(store.Engine(store.Load(id), room.Data).LoadSpace());
            string Trace(string id)
            {
                var p = store.SpacePaths(id);
                string Hash(string f) => File.Exists(f) ? SCP_SculptPy.Sha256Hex(File.ReadAllBytes(f)) : "-";
                return string.Join("|", SCP_SculptStore.ListEvents(p).Select(e => e.Rel + "=" + Hash(e.Full))) + "#" + Hash(p.CacheFile);
            }
            SCP_JsonData Ev(SCP_SculptStore.EventFile f) => SCP_SculptStore.ReadEvent(f.Full)!;
            bool OverLimit(SCP_SculptStore.EventFile f) => new FileInfo(f.Full).Length > SCP_SculptStore.MaxEventBytes;
            const long Small = 64 * 1024;

            // 對照組 b：預設門檻 ⇒ 同一刀單一個檔
            Check(Create("a") && Create("b") && Create("c"), "三件作品建立");
            Check(Slab("a") && Slab("b") && Slab("c"), "三件都放 1,024 格");
            string revSlab = Replay("a");
            var mb = Lift("b");
            Check(mb.ExitCode == 0 && V(mb, "event_parts") == "1", "🔴 預設門檻：同一刀是一個檔：" + SculptRoom.All(mb));
            var lastB = Ev(Events("b")[^1]);
            Check(!lastB["parts"].Exists && !lastB["part_of"].Exists, "🔴 單檔的刀沒有 parts／part_of 欄");
            string revLift = Replay("b");
            long bytesB = new FileInfo(Events("b")[^1].Full).Length;
            readings.Add("整刀單檔 " + bytesB + " B");

            // ① 門檻調小 ⇒ 拆
            SCP_SculptStore.MaxEventBytes = Small;
            int before = Events("a").Count;
            var ma = Lift("a");
            int parts = int.TryParse(V(ma, "event_parts"), out int pn) ? pn : 0;
            var evA = Events("a");
            Check(ma.ExitCode == 0 && parts > 1 && evA.Count == before + parts, "大刀拆成多檔（event_parts>1、檔數多 parts 個）：" + SculptRoom.All(ma));
            if (parts > 1 && evA.Count == before + parts)
            {
                var head = evA[before];
                for (int k = 1; k < parts; k++) Check(evA[before + k].Rel == SCP_SculptStore.PartRel(head.Rel, k + 1), "續檔名照 X_p" + (k + 1) + ".json：" + evA[before + k].Rel);
                Check(evA.Skip(before).All(f => !OverLimit(f)), "每個檔 ≤ 上限");
                Check(Ev(head)["parts"].AsString() == parts.ToString(), "頭檔帶 parts＝" + parts);
                Check(evA.Skip(before + 1).All(f => Ev(f)["part_of"].AsString() == head.Rel.Replace('\\', '/')), "續檔帶 part_of ＝ 頭檔");
                Check(Replay("a") == revLift && Cached("a") == revLift && revLift != revSlab, "從頭重播 ＝ 快取 ＝ 不拆的同一刀（作品 b），且跟刀前不同");
                readings.Add("拆成 " + parts + " 檔（上限 " + Small + " B）");

                // ② 歷史只數刀
                var h = Sub("a", "history");
                Check(V(h, "undo_count") == "2" && V(h, "redo_count") == "0", "歷史：box＋move ＝ 2 刀（續檔不另算）：" + SculptRoom.All(h));

                // ③ 時間點：指頭檔 ⇒ 整刀
                Check(Replay("a", head.Rel) == revLift, "upto 指頭檔 ⇒ 延伸到最後一個續檔（看到整刀）");
                Check(Replay("a", evA[before + 1].Rel) == revLift, "upto 指中間的續檔 ⇒ 也延伸到整刀");
                Check(Sub("a", "mark", ("name", "lift"), ("upto", Path.GetFileName(head.Rel))).ExitCode == 0, "標記 lift 指頭檔名");
                string markRel = SCP_SculptMarks.ResolvePoint(SCP_SculptMarks.Read(store.SpacePaths("a")), "lift");
                Check(Replay("a", markRel) == revLift, "標記指頭檔 ⇒ 觀測到整刀：" + markRel);

                // ④ undo／redo 整刀來回
                int beforeUndo = Events("a").Count;
                var u = Sub("a", "undo");
                Check(u.ExitCode == 0 && Replay("a") == revSlab && Cached("a") == revSlab, "undo 回到放完 1,024 格那一刻（重播＝快取）：" + SculptRoom.All(u));
                Check(int.TryParse(V(u, "event_parts"), out int up) && up > 1 && Events("a").Count == beforeUndo + up, "undo 本身也大 ⇒ 也拆");
                var h2 = Sub("a", "history");
                Check(V(h2, "undo_count") == "1" && V(h2, "redo_count") == "1", "undo 之後：可 undo 1、可 redo 1：" + SculptRoom.All(h2));
                var r = Sub("a", "redo");
                Check(r.ExitCode == 0 && Replay("a") == revLift && Cached("a") == revLift, "redo 整刀套回（頭檔＋續檔）：" + SculptRoom.All(r));
                var h3 = Sub("a", "history");
                Check(V(h3, "undo_count") == "2" && V(h3, "redo_count") == "0", "redo 之後：可 undo 2、可 redo 0");
            }

            // ⑤ split-events：c 用預設門檻寫一個大檔（舊資料的樣子），再調小門檻就地拆
            SCP_SculptStore.MaxEventBytes = SCP_SculptStore.DefaultMaxEventBytes;
            Check(Lift("c").ExitCode == 0, "c 用預設門檻寫一刀（單檔）");
            var evC = Events("c");
            string bigRel = evC[^1].Rel;
            SCP_SculptStore.MaxEventBytes = Small;
            string trace0 = Trace("c");
            var dry = Sub("c", "split-events");
            Check(dry.ExitCode == 0 && V(dry, "over_limit") == "1" && V(dry, "would_split") == "1", "試算：1 個超過、1 個會拆：" + SculptRoom.All(dry));
            Check(Trace("c") == trace0, "試算一個位元組都沒寫（events 與快取逐位元組不變）");
            var ap = Sub("c", "split-events", ("confirm", "1"));
            var evC2 = Events("c");
            int cParts = evC2.Count - evC.Count + 1;
            Check(ap.ExitCode == 0 && V(ap, "split") == "1" && cParts > 1, "confirm=1 拆了：" + SculptRoom.All(ap));
            Check(evC2[evC.Count - 1].Rel == bigRel && evC2.Skip(evC.Count).All(f => SCP_SculptStore.IsPartOf(f.Rel, bigRel)), "頭檔留原名、續檔緊跟在後");
            Check(evC2.All(f => !OverLimit(f)), "拆完每個檔 ≤ 上限");
            Check(Replay("c") == revLift && V(ap, "voxels") == "1024" && V(ap, "checksum") == V(dry, "checksum") && V(ap, "checksum").Length > 0, "就地拆完重播不變（＝ b；checksum 拆前拆後相同）");
            var hc = Sub("c", "history");
            Check(V(hc, "undo_count") == "2", "拆完歷史仍是 2 刀");
            Check(Sub("c", "undo").ExitCode == 0 && Replay("c") == revSlab, "拆完照樣 undo 整刀");
            var again = Sub("c", "split-events");
            Check(again.ExitCode == 0 && V(again, "over_limit") == "0", "🔴 拆完再掃：沒有超過上限的：" + SculptRoom.All(again));
            readings.Add("就地拆 " + cParts + " 檔");

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; SCP_SculptStore.MaxEventBytes = SCP_SculptStore.DefaultMaxEventBytes; }
    }
}
