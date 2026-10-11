// 淨室驗收（TASK-0493）：作品的草稿層 —— draft=1 的刀只動草稿（作品 events／快取／marks／歷史索引逐位元組不變）、
// 觀測看得到「作品＋草稿」、草稿裡 undo／redo／微調、apply 一筆套進作品且逐格 ＝ 草稿、apply 後 undo 回到草稿前、
// discard 無痕、底過期擋下零寫入、草稿快取刪掉從底重建、參數與權限擋下。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureDraftCleanRoom()
    {
        const string name = "雕刻草稿層：草稿刀不動作品（逐位元組）／draft=1 觀測／草稿 undo・redo・微調／apply 一筆且逐格＝草稿、undo 回草稿前／discard 無痕／底過期擋下／快取從底重建（淨室，TASK-0493）";
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
            bool Create(string id) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", id), ("size", "16"),
                ("commission", "草稿 " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_SculptSpace Space(string id) => store.Engine(store.Load(id), room.Data).LoadSpace();
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
            (string, string)[] Draft = { ("draft", "1") };
            SCP_CmdResult Box(string id, int x1, int x2, int color, bool draft)
            {
                var a = new List<(string, string)> { ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", color.ToString()) };
                if (draft) a.Add(("draft", "1"));
                return Run(id, "box", a.ToArray());
            }
            SCP_CmdResult Carve(string id, int x, bool draft)
            {
                var a = new List<(string, string)> { ("x1", x.ToString()), ("x2", x.ToString()), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0") };
                if (draft) a.Add(("draft", "1"));
                return Run(id, "carve", a.ToArray());
            }
            string Total(SCP_CmdResult r) => r.ExitCode + ":" + SculptRoom.V(r, "total_voxels");
            string V(SCP_CmdResult r, string k) => SculptRoom.V(r, k);
            // 作品本身的「痕跡」：events 清單＋快取＋marks.json＋歷史索引的位元組
            string Trace(string id)
            {
                var p = store.SpacePaths(id);
                string Hash(string f) => File.Exists(f) ? SCP_SculptPy.Sha256Hex(File.ReadAllBytes(f)) : "-";
                var ev = SCP_SculptStore.ListEvents(p).Select(e => e.Rel + "=" + Hash(e.Full));
                return string.Join("|", ev) + "#" + Hash(p.CacheFile) + "#" + Hash(p.MarksFile) + "#" + Hash(p.HistoryCacheFile);
            }

            Check(Create("d"), "作品 d 建立");
            Check(Box("d", 0, 2, 10, false).ExitCode == 0, "作品 A 三格");
            Check(Sub("d", "mark", ("name", "m1")).ExitCode == 0 && Sub("d", "history").ExitCode == 0, "標 m1、跑一次 history（讓歷史索引落地，才比得了位元組）");
            Space("d");
            string trace0 = Trace("d");
            int ev0 = SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count;

            // ① 草稿刀：作品不動
            var b1 = Box("d", 5, 6, 20, true);
            Check(b1.ExitCode == 0 && V(b1, "draft") == "1" && V(b1, "draft_base_event") == SCP_SculptStore.ListEvents(store.SpacePaths("d"))[^1].Rel.Replace('\\', '/'),
                  "草稿第一刀自動開草稿、底 ＝ 作品最後一個事件：" + SculptRoom.All(b1));
            Space("d");
            Check(Trace("d") == trace0, "草稿刀之後作品 events／快取／marks／歷史索引逐位元組不變");
            Check(Total(Run("d", "stats", Draft)) == "0:5" && Total(Run("d", "stats")) == "0:3", "stats draft=1 ＝ 5 格、不帶 ＝ 作品 3 格");
            var vw = Run("d", "view", ("draft", "1"), ("out", Path.Combine(room.Root, "d.png")));
            Check(vw.ExitCode != 2, "view 吃 draft=1（沒有渲染器也要先過參數這一關）：" + SculptRoom.All(vw));
            var exl = Run("d", "export", ("format", "list"), ("draft", "1"), ("out", Path.Combine(room.Root, "d.list.txt").Replace('\\', '/')));
            Check(exl.ExitCode == 0 && V(exl, "voxels") == "5", "export list draft=1 ＝ 5 格");

            // ② 微調：刻掉 A 一格、換色一格；undo 換色、redo、再 undo —— 作品一直不動
            Check(Carve("d", 0, true).ExitCode == 0, "草稿刻掉 (0,0,0)");
            string recolor = Path.Combine(room.Root, "recolor.vox.txt");
            File.WriteAllText(recolor, "1,0,0,30\n");
            Check(Run("d", "stampvox", ("voxels", recolor), ("at", "0,0,0"), ("expect_pixels", "1"), ("overwrite", "1"), ("draft", "1")).ExitCode == 0, "草稿換色 (1,0,0)→30");
            SCP_SculptPaths DraftPaths(string id) => SCP_SculptDraft.Paths(store.SpacePaths(id), SCP_SculptDraft.Read(store.SpacePaths(id))!, store.Load(id).Dimensions);
            SCP_SculptSpace dspace() => new SCP_SculptEngine(DraftPaths("d"), room.Data, 16, 16, 16).LoadSpace();
            Check(dspace().Voxels.Get(1, 0, 0) == 30, "草稿裡 (1,0,0) 是 30");
            Check(Sub("d", "undo", Draft).ExitCode == 0 && dspace().Voxels.Get(1, 0, 0) == 10, "草稿 undo ⇒ 回 10");
            Check(Sub("d", "redo", Draft).ExitCode == 0 && dspace().Voxels.Get(1, 0, 0) == 30, "草稿 redo ⇒ 又是 30");
            Check(Sub("d", "undo", Draft).ExitCode == 0 && dspace().Voxels.Get(1, 0, 0) == 10, "再 undo ⇒ 10（這一刀之後不該被 apply 帶進去）");
            Check(Sub("d", "move", ("region", "5..6,0..0,0..0"), ("delta", "0,1,0"), ("draft", "1")).ExitCode == 0 && dspace().Voxels.Get(5, 1, 0) == 20 && dspace().Voxels.Get(5, 0, 0) == 0, "草稿 move（B 往 +y 一格）");
            Space("d");
            Check(Trace("d") == trace0, "微調、undo、redo、move 之後作品仍逐位元組不變");

            // ③ 狀態與差異：放 2（c20）、刻 1（c10）、換色 0；可 undo／redo 的數
            var st = Sub("d", "draft");
            Check(st.ExitCode == 0 && V(st, "placed") == "2" && V(st, "carved") == "1" && V(st, "recolored") == "0" && V(st, "draft_stale") == "0",
                  "sub=draft：放 2／刻 1／換色 0、底沒過期：" + SculptRoom.All(st) + " " + V(st, "placed") + "/" + V(st, "carved") + "/" + V(st, "recolored"));
            var df = Run("d", "diff", Draft);
            Check(df.ExitCode == 0 && V(df, "placed") == "2" && V(df, "carved") == "1" && SculptRoom.All(df).Contains("c20×2"), "diff draft=1 ＝ 作品現在 → 草稿：" + SculptRoom.All(df));

            // ④ 草稿快取刪掉 ⇒ 從底重建（🔴 反向對照：底被忽略的話只剩草稿自己放的格）
            var dpaths = DraftPaths("d");
            int before = dspace().Voxels.Count;
            File.Delete(dpaths.CacheFile);
            Check(dspace().Voxels.Count == before && before == 4, "刪草稿快取 ⇒ 從底重建同樣 4 格（A 兩格＋B 兩格）：" + before);
            var noBase = new SCP_SculptPaths(dpaths.Root);
            File.Delete(dpaths.CacheFile);
            Check(SCP_SculptStore.Load(noBase).Voxels.Count != before, "🔴 不給底重建 ⇒ 格數對不上（證明「從底起」真的有在做事）");
            File.Delete(dpaths.CacheFile);

            // ⑤ apply：一筆事件、逐格 ＝ 草稿、undo 掉的換色沒帶進去、草稿資料夾不見；undo 回草稿前、redo 回來
            var draftFinal = dspace();
            var ap = Sub("d", "apply");
            var after = Space("d");
            Check(ap.ExitCode == 0 && V(ap, "apply_mismatch") == "0" && SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count == ev0 + 1, "apply ⇒ 作品只多 1 個事件：" + SculptRoom.All(ap));
            Check(after.Voxels.Count == draftFinal.Voxels.Count && draftFinal.Voxels.Entries().All(v => after.Voxels.Get(v.X, v.Y, v.Z) == v.Color), "apply 後作品逐格 ＝ 草稿");
            Check(after.Voxels.Get(1, 0, 0) == 10 && after.Voxels.Get(0, 0, 0) == 0 && after.Voxels.Get(5, 1, 0) == 20, "草稿裡 undo 掉的換色沒有跟著進來");
            Check(!Directory.Exists(SCP_SculptDraft.Dir(store.SpacePaths("d"))), "apply 後草稿資料夾不見");
            var last = SCP_SculptStore.ReadEvent(SCP_SculptStore.ListEvents(store.SpacePaths("d"))[^1].Full);
            Check(last != null && last.GetString("op", "") == "workedit" && last["edit"].GetString("action", "") == "draft", "那一筆是 workedit（action=draft）—— 舊 exe 重播得出來");
            Check(Sub("d", "undo").ExitCode == 0 && Space("d").Voxels.Count == 3 && Space("d").Voxels.Get(0, 0, 0) == 10, "apply 後 undo 一次 ⇒ 回到草稿前（A 三格）");
            Check(Sub("d", "redo").ExitCode == 0 && Space("d").Voxels.Count == 4, "redo ⇒ 草稿的樣子回來");

            // ⑥ discard 無痕
            Space("d");
            string trace1 = Trace("d");
            Check(Box("d", 9, 9, 40, true).ExitCode == 0 && Carve("d", 1, true).ExitCode == 0 && Sub("d", "undo", Draft).ExitCode == 0, "開新草稿、兩刀、undo 一刀");
            var dc = Sub("d", "discard");
            Space("d");
            Check(dc.ExitCode == 0 && !Directory.Exists(SCP_SculptDraft.Dir(store.SpacePaths("d"))) && Trace("d") == trace1, "discard ⇒ 草稿資料夾不見、作品逐位元組 ＝ 開草稿之前：" + SculptRoom.All(dc));
            Check(Sub("d", "discard").ExitCode == 0 && V(Sub("d", "discard"), "draft_removed_files") == "0", "沒草稿時 discard ⇒ exit 0、刪 0 檔");

            // ⑦ 底過期 ⇒ apply 擋下、零寫入；草稿留著
            Check(Box("d", 11, 11, 50, true).ExitCode == 0, "開草稿");
            int evGuard = SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count;
            Check(Box("d", 13, 13, 60, false).ExitCode == 2 && SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count == evGuard,
                  "🔴 草稿開著、刀沒寫 draft=0|1 ⇒ exit 2、作品零事件（漏帶一個參數不讓刀靜默落進作品）");
            Check(Run("d", "box", ("x1", "13"), ("x2", "13"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "60"), ("draft", "0")).ExitCode == 0,
                  "明寫 draft=0 ⇒ 作品自己又下一刀（底過期）");
            Space("d");
            string trace2 = Trace("d");
            var stale = Sub("d", "apply");
            Space("d");
            Check(stale.ExitCode == 2 && Trace("d") == trace2 && SCP_SculptDraft.IsOpen(store.SpacePaths("d")), "🔴 底過期 ⇒ exit 2、作品零寫入、草稿留著：" + SculptRoom.All(stale));
            Check(V(Sub("d", "draft"), "draft_stale") == "1", "sub=draft 印出底過期");
            Check(Sub("d", "discard").ExitCode == 0, "丟掉過期的草稿");

            // ⑧ 草稿跟作品一模一樣 ⇒ apply 擋下（不寫空事件）、草稿留著
            Check(Sub("d", "draft").ExitCode == 0 && SCP_SculptDraft.IsOpen(store.SpacePaths("d")), "sub=draft 開一份空草稿");
            int ev3 = SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count;
            Check(Sub("d", "apply").ExitCode == 2 && SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count == ev3 && SCP_SculptDraft.IsOpen(store.SpacePaths("d")), "🔴 空草稿 apply ⇒ exit 2、零事件、草稿留著");
            Check(Sub("d", "discard").ExitCode == 0, "丟掉空草稿");

            // ⑨ 空的作品也能開草稿（底 ＝ start）
            Check(Create("e"), "作品 e 建立（沒有事件）");
            var e1 = Box("e", 0, 0, 77, true);
            Check(e1.ExitCode == 0 && V(e1, "draft_base_event") == "start" && Space("e").Voxels.Count == 0, "空作品的草稿：底 start、作品仍空");
            Check(Sub("e", "apply").ExitCode == 0 && Space("e").Voxels.Get(0, 0, 0) == 77, "空作品 apply");

            // ⑨b 作品沒有快取、沒有歷史索引時，草稿刀／move／undo 也不會替它建出來（讀作品一律只讀）
            Check(Create("f") && Box("f", 0, 3, 10, false).ExitCode == 0, "作品 f 四格");
            var fp = store.SpacePaths("f");
            File.Delete(fp.CacheFile);
            if (File.Exists(fp.HistoryCacheFile)) File.Delete(fp.HistoryCacheFile);
            string Listing(string id) => string.Join("|", Directory.GetFiles(store.Folder(id), "*", SearchOption.AllDirectories)
                .Where(f => !f.Replace('\\', '/').Contains("/draft/") && !f.EndsWith(".lock", StringComparison.Ordinal)).OrderBy(f => f, StringComparer.Ordinal)
                .Select(f => f + "=" + SCP_SculptPy.Sha256Hex(File.ReadAllBytes(f))));
            string fBefore = Listing("f");
            Check(Box("f", 5, 5, 20, true).ExitCode == 0 && Sub("f", "move", ("region", "5..5,0..0,0..0"), ("delta", "1,0,0"), ("draft", "1")).ExitCode == 0
                  && Sub("f", "undo", Draft).ExitCode == 0 && Run("f", "stats", Draft).ExitCode == 0 && Sub("f", "draft").ExitCode == 0, "草稿刀、move、undo、觀測、看狀態");
            Check(Listing("f") == fBefore && !File.Exists(fp.CacheFile) && !File.Exists(fp.HistoryCacheFile), "🔴 作品資料夾（draft/ 以外）的檔案清單與位元組都沒變、沒建出快取／歷史索引");
            // 草稿裡 export 沒給 out ⇒ 落在作品的 exports/（⛔ 不落進 draft/ —— apply／discard 會把它刪掉）
            var fex = Run("f", "export", ("format", "list"), ("draft", "1"));
            Check(fex.ExitCode == 0 && V(fex, "path").Replace('\\', '/').Contains("/works/f/exports/") && !V(fex, "path").Contains("/draft/"), "草稿 export 預設落在作品的 exports/：" + V(fex, "path"));
            Check(Run("f", "stampvox", ("voxels", recolor), ("at", "0,0,0"), ("expect_pixels", "1"), ("overwrite", "1"), ("draft", "1"), ("exhibit_id", "x")).ExitCode == 2, "🔴 draft＋exhibit_id ⇒ exit 2");
            Check(Sub("f", "discard").ExitCode == 0, "丟掉 f 的草稿");
            // 殘留的 draft/（沒有 base.json）⇒ 不沿用，要先 discard
            Directory.CreateDirectory(Path.Combine(SCP_SculptDraft.Dir(fp), "events"));
            File.WriteAllText(Path.Combine(SCP_SculptDraft.Dir(fp), "sculpt_cache.json"), "{\"last_event_file\": \"\", \"voxels\": {\"9,9,9\": 99}}");
            var residue = Box("f", 7, 7, 30, true);
            Check(residue.ExitCode == 2 && SculptRoom.All(residue).Contains("殘留"), "🔴 殘留的 draft/ ⇒ exit 2（⛔ 不沿用幽靈內容）：" + SculptRoom.All(residue));
            Check(Sub("f", "discard").ExitCode == 0 && !Directory.Exists(SCP_SculptDraft.Dir(fp)) && Box("f", 7, 7, 30, true).ExitCode == 0
                  && new SCP_SculptEngine(DraftPaths("f"), room.Data, 16, 16, 16).LoadSpace().Voxels.Get(9, 9, 9) == 0, "discard 清掉殘留之後重開，幽靈格不在");
            Check(Sub("f", "discard").ExitCode == 0, "丟掉");

            // ⑩ 參數與權限
            Check(Run("d", "stats", ("draft", "1")).ExitCode == 2, "🔴 沒開草稿就 draft=1 觀測 ⇒ exit 2");
            Check(Sub("d", "undo", Draft).ExitCode == 2, "🔴 沒開草稿就 undo draft=1 ⇒ exit 2");
            Check(Box("d", 2, 2, 5, true).ExitCode == 0, "再開一份");
            Check(Run("d", "stats", ("draft", "1"), ("mark", "m1")).ExitCode == 2, "🔴 draft＋mark ⇒ exit 2");
            Check(room.Run(("op", "stats"), ("draft", "1")).ExitCode == 2, "🔴 共用展區 draft ⇒ exit 2");
            Check(Run("d", "exhibit", ("sub", "list"), ("draft", "1")).ExitCode == 2 && Sub("d", "mark", ("name", "x"), ("draft", "1")).ExitCode == 2, "🔴 不吃 draft 的 op／sub ⇒ exit 2");
            Check(Run("d", "stats", ("draft", "2")).ExitCode == 2, "🔴 draft=2 ⇒ exit 2");
            Check(Run("d", "diff", ("draft", "1"), ("from", "m1")).ExitCode == 2, "🔴 diff draft=1 帶 from ⇒ exit 2");
            Check(room.Run(("op", "box"), ("work", "d"), ("persona", "q"), ("x1", "3"), ("x2", "3"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("draft", "1")).ExitCode == 2
                  && room.Run(("op", "work"), ("sub", "apply"), ("work", "d"), ("persona", "q")).ExitCode == 2
                  && room.Run(("op", "work"), ("sub", "discard"), ("work", "d"), ("persona", "q")).ExitCode == 2, "🔴 非作者的草稿刀／apply／discard ⇒ exit 2");
            Check(SCP_SculptDraft.IsOpen(store.SpacePaths("d")), "非作者的 discard 沒有刪到草稿");
            // 審查抓到的兩個繞道：用 id= 點名作品、draft=（空值）—— 都要跟沒帶 draft 一樣擋下
            int evBypass = SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count;
            Check(room.Run(("op", "work"), ("sub", "undo"), ("id", "d"), ("persona", "p")).ExitCode == 2
                  && Run("d", "box", ("x1", "3"), ("x2", "3"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "5"), ("draft", "")).ExitCode == 2
                  && SCP_SculptStore.ListEvents(store.SpacePaths("d")).Count == evBypass, "🔴 草稿開著：work sub=undo 用 id= 點名、box 帶 draft=（空值）⇒ 都擋下、作品零事件");
            Check(Sub("d", "discard").ExitCode == 0, "丟掉");

            // ⑪ apply 之後收尾失敗（草稿資料夾有檔被佔用）⇒ 照實回報「已套進作品」exit 0＋draft_cleanup_failed；殘留不被當成還開著的草稿
            Check(Create("g") && Box("g", 0, 0, 1, false).ExitCode == 0 && Box("g", 1, 1, 2, true).ExitCode == 0, "作品 g＋草稿一刀");
            var gp = store.SpacePaths("g");
            int evG = SCP_SculptStore.ListEvents(gp).Count;
            string heldFile = SCP_SculptStore.ListEvents(DraftPaths("g"))[0].Full;
            SCP_CmdResult heldApply;
            using (new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                heldApply = Sub("g", "apply");
            Check(heldApply.ExitCode == 0 && V(heldApply, "draft_cleanup_failed") == "1" && SCP_SculptStore.ListEvents(gp).Count == evG + 1 && Space("g").Voxels.Get(1, 0, 0) == 2,
                  "🔴 apply 後草稿資料夾有檔被佔用 ⇒ exit 0、已套進作品、draft_cleanup_failed=1：" + SculptRoom.All(heldApply));
            Check(!SCP_SculptDraft.IsOpen(gp) && Box("g", 2, 2, 3, true).ExitCode == 2, "殘留不算開著的草稿；再開 ⇒ 擋下要先 discard");
            Check(Sub("g", "discard").ExitCode == 0 && !Directory.Exists(SCP_SculptDraft.Dir(gp)) && Box("g", 2, 2, 3, true).ExitCode == 0 && Sub("g", "discard").ExitCode == 0,
                  "放掉之後 discard 清乾淨、可以重開");

            return new CheckRow(name, failures.Count == 0
                ? "草稿刀自動開草稿、作品逐位元組不變；stats／view／export 吃 draft=1；刻、換色、undo／redo、move 只動草稿；diff 與 sub=draft 放 2／刻 1；刪草稿快取從底重建（🔴 不給底就對不上）；apply 只多 1 事件、逐格＝草稿、undo 掉的不帶進去、workedit action=draft、undo 回草稿前；discard 無痕；🔴 底過期／空草稿 apply 擋下零寫入；空作品可開草稿；🔴 參數與非作者全擋"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
