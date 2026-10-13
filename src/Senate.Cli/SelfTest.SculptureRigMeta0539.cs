// 淨室驗收（TASK-0539）：帆與繩的 meta、`work sub=meta`（不限作者、記 meta_log）、匯出 .vxk 帶 meta 與繩的兩端 ——
// ① 非作者 q 改帆的 meta ⇒ 寫得進去、sails.json schema 3、meta_log.jsonl 一行（persona q、owner p、改之前 {}、改之後）。
// ② 批次檔（# 註解、空行略過；同一面帆兩行依序疊上去）⇒ 繩 meta 進 ropes.json（schema 3）、帆 meta 合併。
// ③ 🔴 批次裡一筆不合（名字找不到／壞 meta／欄數不對）⇒ exit 2、ropes.json／sails.json／meta_log 逐位元組不變（整份不寫）。
// ④ 作者改繩、帆的屬性（color）⇒ meta 還在（⛔ 以前「新建物件再抄欄位」會默默清掉）。⑤ 🔴 非作者改形狀照舊被擋。
// ⑥ 讀數：sub=ropes／sails 印 meta；sub=meta 列出、報沒標的幾筆；同樣的 meta 再下一次 ⇒ 0 筆改了、log 不多一行。
// ⑦ export vxk：include=rig,meta ⇒ 繩、帆帶 meta（數字原樣）；🔴 只 include=rig ⇒ 沒有 meta 鍵；include=rig,ends ⇒ 繩帶 frame 與兩端原樣；🔴 ends 不配 rig ⇒ 擋。
// ⑧ 🔴 反向對照：meta=none ⇒ ropes.json／sails.json 逐位元組回到加 meta 之前（schema 回 1、沒有 meta 鍵）。
// ⑨ 🔴 手改：schema 1 卻有 meta ⇒ 大聲讀不了（⛔ 不默默丟掉）。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureRigMetaCleanRoom()
    {
        const string name = "雕刻帆與繩的 meta：非作者寫 meta（schema 3、meta_log 一行）／批次合併／🔴 一筆不合整份不寫／作者改屬性不清 meta／🔴 非作者改形狀照擋／讀數與重下 0 筆／vxk 帶 meta、🔴 不帶 include 沒有鍵、ends＝原樣／🔴 清掉逐位元組回原樣／🔴 手改 schema 1 帶 meta 讀不了（淨室，TASK-0539）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            Directory.CreateDirectory(Path.Combine(room.Letters, "q", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(new SculptProbeRenderer());
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            SCP_CmdResult Sub(string who, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", "host"), ("persona", who) };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            byte[] Bytes(string f) => File.Exists(f) ? File.ReadAllBytes(f) : Array.Empty<byte>();
            SCP_JsonData Json(string f) => SCP_JsonParser.Parse(File.ReadAllText(f), false);
            var paths = store.SpacePaths("host");
            string RopesF = paths.RopesFile, SailsF = paths.SailsFile, LogF = paths.MetaLogFile;
            int LogLines() => File.Exists(LogF) ? File.ReadAllLines(LogF).Count(l => l.Trim().Length > 0) : 0;

            // host 40³（作者 p）：固定點 a b c d、繩 r1 a → b、帆 s1 a|b|c|d（上緣 0 釘住、下兩角 free）
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "host"), ("title", "作品host"), ("size", "40,40,40"),
                           ("commission", "meta host"), ("commission_ref", "user:meta:host"), ("account", "acct")).ExitCode == 0, "建 host");
            Check(room.Run(("op", "box"), ("work", "host"), ("persona", "p"), ("x1", "0"), ("x2", "0"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "60")).ExitCode == 0, "一格");
            foreach (var (n, at) in new[] { ("a", "5,5,20"), ("b", "15,5,20"), ("c", "15,5,10"), ("d", "5,5,10") })
                Check(Sub("p", "anchor", ("name", n), ("at", at)).ExitCode == 0, "固定點 " + n);
            Check(Sub("p", "rope", ("name", "r1"), ("from", "a"), ("to", "b"), ("slack", "1.05")).ExitCode == 0, "繩 r1");
            Check(Sub("p", "sail", ("name", "s1"), ("corners", "a|b|c|d"), ("bind", "0"), ("free", "2,3")).ExitCode == 0, "帆 s1");
            byte[] ropes0 = Bytes(RopesF), sails0 = Bytes(SailsF);
            Check(Json(RopesF)["schema"].AsLong() == 1 && Json(SailsF)["schema"].AsLong() == 1, "還沒標：ropes.json／sails.json schema 1");

            // 對照：還沒標 meta 時匯出（include=rig,meta）⇒ 繩、帆沒有 meta 鍵
            var (h0, _) = ExportVxk(room, Out("before.vxk"), "rig,meta");
            Check(h0 != null && !h0["rig"]["ropes"][0]["meta"].Exists && !h0["rig"]["sails"][0]["meta"].Exists, "🔴 沒標 meta 的作品：vxk 的繩、帆沒有 meta 鍵（跟以前逐欄相同）");

            // ── ① 非作者改帆的 meta ───────────────────────────────────────────
            var w1 = Sub("q", "meta", ("part", "sail"), ("name", "s1"), ("meta", "{\"kind\":\"sail\",\"mast\":\"fore\",\"sail\":\"topsail\"}"));
            Check(w1.ExitCode == 0 && SculptRoom.V(w1, "meta_changed") == "1", "① 非作者 q 改帆的 meta：" + SculptRoom.All(w1));
            Check(Json(SailsF)["schema"].AsLong() == 3 && Json(SailsF)["sails"][0]["meta"]["mast"].AsString() == "fore", "① sails.json schema 3、meta 寫進去");
            Check(LogLines() == 1, "① meta_log.jsonl 一行（got " + LogLines() + "）");
            if (LogLines() >= 1)
            {
                SCP_JsonData lg = SCP_JsonParser.Parse(File.ReadAllLines(LogF)[0], false);
                Check(lg["persona"].AsString() == "q" && lg["owner"].AsString() == "p" && lg["part"].AsString() == "sail" && lg["name"].AsString() == "s1"
                      && lg["before"].Count == 0 && lg["after"]["sail"].AsString() == "topsail", "① log：誰 q、作者 p、改之前 {}、改之後：" + lg.ToJson(false));
            }

            // ── ② 批次 ───────────────────────────────────────────────────────
            string batch = Out("batch.tsv");
            File.WriteAllText(batch, "# 註解\n\nrope\tr1\t{\"kind\":\"sheet\",\"sail\":\"s1\"}\nsail\ts1\t{\"set\":2}\nsail\ts1\t{\"mast\":\"main\"}\n");
            var w2 = Sub("q", "meta", ("file", batch));
            SCP_JsonData sm = Json(SailsF)["sails"][0]["meta"], rm = Json(RopesF)["ropes"][0]["meta"];
            Check(w2.ExitCode == 0 && SculptRoom.V(w2, "meta_changed") == "2", "② 批次：兩筆（帆兩行疊成一筆）：" + SculptRoom.All(w2));
            Check(Json(RopesF)["schema"].AsLong() == 3 && rm["kind"].AsString() == "sheet" && rm["sail"].AsString() == "s1", "② 繩 meta 進 ropes.json（schema 3）：" + rm.ToJson(false));
            Check(sm["set"].Type == SCP_JsonType.Number && sm["set"].AsLong() == 2 && sm["mast"].AsString() == "main" && sm["kind"].AsString() == "sail", "② 帆 meta 合併、後一行蓋前一行、數字原樣：" + sm.ToJson(false));
            Check(LogLines() == 3, "② log 多兩行（got " + LogLines() + "）");

            // ── ③ 🔴 一筆不合 ⇒ 整份不寫 ──────────────────────────────────────
            byte[] r3 = Bytes(RopesF), s3 = Bytes(SailsF), l3 = Bytes(LogF);
            foreach (var (why, body) in new[] {
                ("名字找不到", "rope\tr1\t{\"kind\":\"tack\"}\nrope\tnope\t{\"kind\":\"sheet\"}\n"),
                ("壞 meta", "rope\tr1\t{\"kind\":\"tack\"}\nsail\ts1\t{\"a\":{\"b\":1}}\n"),
                ("欄數不對", "rope\tr1\t{\"kind\":\"tack\"}\nsail\ts1\n"),
                ("part 不認得", "rope\tr1\t{\"kind\":\"tack\"}\nyard\ts1\t{}\n") })
            {
                File.WriteAllText(batch, body);
                var r = Sub("q", "meta", ("file", batch));
                Check(r.ExitCode == 2 && Bytes(RopesF).SequenceEqual(r3) && Bytes(SailsF).SequenceEqual(s3) && Bytes(LogF).SequenceEqual(l3),
                      "③ 🔴 " + why + " ⇒ exit 2、三個檔都不變（exit " + r.ExitCode + "）");
            }

            // ── ④ 作者改屬性不清 meta ─────────────────────────────────────────
            Check(Sub("p", "rope", ("name", "r1"), ("color", "#ff0000")).ExitCode == 0 && Sub("p", "sail", ("name", "s1"), ("color", "#eeeeee")).ExitCode == 0, "④ 作者改顏色");
            Check(Json(RopesF)["ropes"][0]["meta"]["kind"].AsString() == "sheet" && Json(SailsF)["sails"][0]["meta"]["set"].AsLong() == 2, "④ 作者改屬性之後 meta 還在");
            Check(Sub("p", "rope", ("name", "r1"), ("color", "#8b6a3e")).ExitCode == 0 && Sub("p", "sail", ("name", "s1"), ("color", SCP_SculptSails.DefaultColor)).ExitCode == 0, "④ 顏色改回來");

            // ── ⑤ 🔴 非作者改形狀照舊擋 ───────────────────────────────────────
            Check(Sub("q", "rope", ("name", "r1"), ("color", "#00ff00")).ExitCode == 2 && Sub("q", "sail", ("name", "s1"), ("color", "#00ff00")).ExitCode == 2, "⑤ 🔴 非作者 q 改繩、帆的屬性 ⇒ exit 2");

            // ── ⑥ 讀數、重下 0 筆 ─────────────────────────────────────────────
            Check(Sub("p", "ropes").ExitCode == 0 && File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md").Contains("meta kind=sheet, sail=s1"), "⑥ sub=ropes 印 meta");
            Check(Sub("p", "sails").ExitCode == 0 && File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md").Contains("meta kind=sail, mast=main, sail=topsail, set=2"), "⑥ sub=sails 印 meta（依鍵排序）");
            var ls = Sub("q", "meta");
            Check(ls.ExitCode == 0 && SculptRoom.V(ls, "meta_rope") == "1" && SculptRoom.V(ls, "meta_sail") == "1" && SculptRoom.V(ls, "meta_rope_without") == "0", "⑥ sub=meta 讀數：" + SculptRoom.All(ls));
            int before6 = LogLines();
            var again = Sub("q", "meta", ("part", "rope"), ("name", "r1"), ("meta", "{\"kind\":\"sheet\"}"));
            Check(again.ExitCode == 0 && SculptRoom.V(again, "meta_changed") == "0" && LogLines() == before6, "⑥ 同樣的 meta 再下 ⇒ 0 筆改了、log 不多一行");

            // ── ⑦ export vxk ─────────────────────────────────────────────────
            var (h1, e1) = ExportVxk(room, Out("meta.vxk"), "rig,meta");
            Check(h1 != null && h1["rig"]["ropes"][0]["meta"]["sail"].AsString() == "s1" && h1["rig"]["sails"][0]["meta"]["set"].Type == SCP_JsonType.Number,
                  "⑦ include=rig,meta ⇒ 繩、帆帶 meta（數字原樣）：" + (h1 == null ? e1 : h1["rig"]["ropes"][0].ToJson(false).Length.ToString()));
            var (h2, _) = ExportVxk(room, Out("rig.vxk"), "rig");
            Check(h2 != null && !h2["rig"]["ropes"][0]["meta"].Exists && !h2["rig"]["sails"][0]["meta"].Exists && !h2["rig"]["ropes"][0]["ends"].Exists, "⑦ 🔴 只 include=rig ⇒ 沒有 meta、ends 鍵");
            var (h3, _) = ExportVxk(room, Out("ends.vxk"), "rig,ends");
            Check(h3 != null && h3["rig"]["ropes"][0]["ends"].ToJson(false) == "[\"a\",\"b\"]" && h3["rig"]["ropes"][0]["frame"].AsString() == "" && !h3["rig"]["ropes"][0]["meta"].Exists,
                  "⑦ include=rig,ends ⇒ 繩帶 frame \"\" 與兩端原樣 [a,b]：" + (h3 == null ? "" : h3["rig"]["ropes"][0]["ends"].ToJson(false)));
            var bad = room.Run(("op", "export"), ("format", "vxk"), ("work", "host"), ("out", Out("x.vxk")), ("include", "ends"));
            Check(bad.ExitCode == 2 && !File.Exists(Out("x.vxk")), "⑦ 🔴 ends 不配 rig ⇒ exit 2、沒寫檔");
            if (h1 != null) readings.Add("vxk 繩 meta " + h1["rig"]["ropes"][0]["meta"].ToJson(false) + "、帆 meta " + h1["rig"]["sails"][0]["meta"].ToJson(false));

            // ── ⑧ 🔴 清掉 ⇒ 逐位元組回原樣 ─────────────────────────────────────
            Check(Sub("q", "meta", ("part", "rope"), ("name", "r1"), ("meta", "none")).ExitCode == 0 && Sub("q", "meta", ("part", "sail"), ("name", "s1"), ("meta", "none")).ExitCode == 0, "⑧ meta=none");
            Check(Bytes(RopesF).SequenceEqual(ropes0) && Bytes(SailsF).SequenceEqual(sails0), "⑧ 🔴 清掉之後 ropes.json／sails.json 逐位元組 ＝ 加 meta 之前（schema 回 1、沒有 meta 鍵）");

            // ── ⑨ 🔴 手改：schema 1 帶 meta ⇒ 讀不了 ────────────────────────────
            File.WriteAllText(RopesF, File.ReadAllText(RopesF).Replace("\"name\": \"r1\",", "\"name\": \"r1\", \"meta\": {\"kind\": \"sheet\"},"));
            string? thrown = null;
            try { SCP_SculptRopes.ReadRopes(paths); } catch (InvalidOperationException e) { thrown = e.Message; }
            Check(thrown != null && thrown.Contains("schema 1"), "⑨ 🔴 ropes.json schema 1 帶 meta ⇒ 讀不了：" + thrown);
            File.WriteAllBytes(RopesF, ropes0);
            File.WriteAllText(SailsF, File.ReadAllText(SailsF).Replace("\"name\": \"s1\",", "\"name\": \"s1\", \"meta\": {\"kind\": \"sail\"},"));
            thrown = null;
            try { SCP_SculptSails.ReadSails(paths); } catch (InvalidOperationException e) { thrown = e.Message; }
            Check(thrown != null && thrown.Contains("schema 1"), "⑨ 🔴 sails.json schema 1 帶 meta ⇒ 讀不了：" + thrown);
            File.WriteAllBytes(SailsF, sails0);
            readings.Add("meta_log " + LogLines() + " 行");
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    /// <summary>匯出 host 的 .vxk、解回 header；失敗 ⇒ (null, 讀數)。</summary>
    static (SCP_JsonData? Header, string Why) ExportVxk(SculptRoom iRoom, string iOut, string iInclude)
    {
        var r = iRoom.Run(("op", "export"), ("format", "vxk"), ("work", "host"), ("out", iOut), ("include", iInclude));
        if (r.ExitCode != 0 || !File.Exists(iOut)) return (null, SculptRoom.All(r));
        var (h, _) = SCP_SculptVxk.Unpack(File.ReadAllBytes(iOut));
        return (h, "");
    }
}
