// 淨室驗收（TASK-0795）：作品本身的預設 meta（`work sub=meta part=work`、work_meta.json）——
// ① 非作者 q 設 part 的預設 meta ⇒ work_meta.json（schema 1）、meta_log 一行（part=work、name ＝ 作品 id）；part 自己的 links.json／models.json 不動。
// ② export parts：host 裡引用 part 的模型（沒寫 meta）⇒ 帶上預設；寫了 kind 的模型 ⇒ 自己的 kind 贏、其他鍵補上；引用 part 的關聯 ⇒ 也帶上；不是引用 part 的不動。
// ③ export vxk include=rig,models,meta ⇒ 模型的 meta 一樣帶上。
// ④ 讀數：sub=meta 列出作品本身那一行；同樣的值再下 ⇒ 0 筆、log 不多一行；批次 `work<TAB><TAB>{…}` 也收。
// ⑤ 🔴 反向對照：meta=none ⇒ work_meta.json 刪掉、host 的部件清單逐位元組回到設之前（沒標預設的作品 ⇒ 匯出跟以前一樣）。
// ⑥ 🔴 work_meta.json 壞了（schema 不對）⇒ export parts 大聲擋下（exit 2、⛔ 不當成沒有）。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureWorkMetaCleanRoom()
    {
        const string name = "雕刻作品本身的預設 meta：非作者設（work_meta.json、meta_log 一行）／部件清單與 vxk 的模型、關聯帶上、擺放寫的優先／讀數、重下 0 筆、批次／🔴 清掉 ⇒ 部件清單逐位元組回原樣／🔴 檔壞了大聲擋（淨室，TASK-0795）";
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
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "wmeta " + id), ("commission_ref", "user:wmeta:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string who, string id, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", who) };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Dot(string id, int x, int y, int z, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            byte[] Bytes(string f) => File.Exists(f) ? File.ReadAllBytes(f) : Array.Empty<byte>();
            SCP_JsonData Json(string f) => SCP_JsonParser.Parse(File.ReadAllText(f), false);
            string WorkMetaF = Path.Combine(Path.GetDirectoryName(store.SpacePaths("part").LinksFile)!, "work_meta.json");
            string LogF = store.SpacePaths("part").MetaLogFile;
            int LogLines() => File.Exists(LogF) ? File.ReadAllLines(LogF).Count(l => l.Trim().Length > 0) : 0;
            SCP_JsonData? Parts(string f)
            {
                var r = room.Run(("op", "export"), ("format", "parts"), ("work", "host"), ("out", Out(f)));
                return r.ExitCode == 0 && File.Exists(Out(f)) ? Json(Out(f)) : null;
            }
            SCP_JsonData Find(SCP_JsonData j, string kind, string path) { foreach (SCP_JsonData o in j[kind]) if (o["path"].AsString() == path) return o; return SCP_JsonData.NewObject(); }

            // host 40³：模型 a（引用 part、沒有 meta）、模型 b（引用 part、meta kind=gun）、模型 c（引用 other）、關聯 kid（引用 part）
            Check(Create("host", "40,40,40") && Dot("host", 0, 0, 0, 60).ExitCode == 0, "host");
            Check(Create("part", "10,10,10") && Dot("part", 0, 0, 0, 224).ExitCode == 0, "part");
            Check(Create("other", "10,10,10") && Dot("other", 0, 0, 0, 100).ExitCode == 0, "other");
            Check(Sub("p", "host", "model", ("name", "a"), ("source_work", "part"), ("at", "10,10,10")).ExitCode == 0, "模型 a");
            Check(Sub("p", "host", "model", ("name", "b"), ("source_work", "part"), ("at", "20,10,10"), ("meta", "{\"kind\":\"gun\"}")).ExitCode == 0, "模型 b");
            Check(Sub("p", "host", "model", ("name", "c"), ("source_work", "other"), ("at", "30,10,10")).ExitCode == 0, "模型 c");
            Check(Sub("p", "host", "link", ("name", "kid"), ("source_work", "part"), ("at", "5,5,5")).ExitCode == 0, "關聯 kid");
            var pj0 = Parts("p0.json");
            byte[] p0 = Bytes(Out("p0.json"));
            Check(pj0 != null && Find(pj0, "models", "a")["meta"].Count == 0 && Find(pj0, "links", "kid")["meta"].Count == 0, "設之前：a、kid 沒有 meta");
            byte[] partModels0 = Bytes(store.SpacePaths("part").ModelsFile), partLinks0 = Bytes(store.SpacePaths("part").LinksFile), hostModels0 = Bytes(store.SpacePaths("host").ModelsFile);

            // ── ① 非作者 q 設 part 的預設 meta ────────────────────────────────
            var w1 = Sub("q", "part", "meta", ("part", "work"), ("meta", "{\"kind\":\"powder\",\"render\":\"lathe\",\"n\":3}"));
            Check(w1.ExitCode == 0 && SculptRoom.V(w1, "meta_changed") == "1", "① 非作者 q 設預設 meta：" + SculptRoom.All(w1));
            Check(File.Exists(WorkMetaF) && Json(WorkMetaF)["schema"].AsLong() == 1 && Json(WorkMetaF)["meta"]["render"].AsString() == "lathe" && Json(WorkMetaF)["meta"]["n"].Type == SCP_JsonType.Number,
                  "① work_meta.json schema 1、值原樣（數字是數字）");
            Check(LogLines() == 1, "① meta_log 一行（got " + LogLines() + "）");
            if (LogLines() >= 1)
            {
                SCP_JsonData lg = SCP_JsonParser.Parse(File.ReadAllLines(LogF)[0], false);
                Check(lg["part"].AsString() == "work" && lg["name"].AsString() == "part" && lg["persona"].AsString() == "q" && lg["owner"].AsString() == "p" && lg["before"].Count == 0,
                      "① log：part=work、name ＝ 作品 id、誰 q、作者 p、改之前 {}：" + lg.ToJson(false));
            }
            Check(Bytes(store.SpacePaths("part").ModelsFile).SequenceEqual(partModels0) && Bytes(store.SpacePaths("part").LinksFile).SequenceEqual(partLinks0)
                  && Bytes(store.SpacePaths("host").ModelsFile).SequenceEqual(hostModels0), "① part、host 的 models.json／links.json 逐位元組不動");

            // ── ② export parts ───────────────────────────────────────────────
            var pj1 = Parts("p1.json");
            Check(pj1 != null, "② export parts 成功");
            if (pj1 != null)
            {
                SCP_JsonData ma = Find(pj1, "models", "a")["meta"], mb = Find(pj1, "models", "b")["meta"], mc = Find(pj1, "models", "c")["meta"], lk = Find(pj1, "links", "kid")["meta"];
                Check(ma["kind"].AsString() == "powder" && ma["render"].AsString() == "lathe" && ma["n"].AsLong() == 3, "② 模型 a（沒寫 meta）⇒ 帶上預設：" + ma.ToJson(false));
                Check(mb["kind"].AsString() == "gun" && mb["render"].AsString() == "lathe", "② 模型 b（寫了 kind=gun）⇒ 自己的 kind 贏、render 補上：" + mb.ToJson(false));
                Check(mc.Count == 0, "② 模型 c（引用 other）⇒ 不動：" + mc.ToJson(false));
                Check(lk["render"].AsString() == "lathe", "② 關聯 kid（引用 part）⇒ 帶上：" + lk.ToJson(false));
                readings.Add("a " + ma.ToJson(false) + "、b " + mb.ToJson(false));
            }

            // ── ③ export vxk ─────────────────────────────────────────────────
            var rv = room.Run(("op", "export"), ("format", "vxk"), ("work", "host"), ("out", Out("h.vxk")), ("include", "rig,models,parts,meta"));
            Check(rv.ExitCode == 0 && File.Exists(Out("h.vxk")), "③ export vxk：" + SculptRoom.All(rv));
            if (File.Exists(Out("h.vxk")))
            {
                var (h, _) = SCP_SculptVxk.Unpack(File.ReadAllBytes(Out("h.vxk")));
                SCP_JsonData? va = null, vb = null;
                foreach (SCP_JsonData m in h["rig"]["models"]) { if (m["name"].AsString() == "a") va = m; if (m["name"].AsString() == "b") vb = m; }
                Check(va != null && va["meta"]["render"].AsString() == "lathe" && vb != null && vb["meta"]["kind"].AsString() == "gun" && vb["meta"]["render"].AsString() == "lathe",
                      "③ vxk 的模型 meta 帶上預設、擺放的 kind 贏：" + (va == null ? "沒有 a" : va["meta"].ToJson(false)));
            }

            // ── ④ 讀數、重下、批次 ───────────────────────────────────────────
            var ls = Sub("q", "part", "meta");
            Check(ls.ExitCode == 0 && SculptRoom.V(ls, "meta_work") == "1" && File.ReadAllText(room.Letters + "/q/cmd/sculpture_work.md").Contains("`part`：kind=powder, n=3, render=lathe"),
                  "④ sub=meta 讀數列出作品本身：" + SculptRoom.All(ls));
            int l4 = LogLines();
            var again = Sub("q", "part", "meta", ("part", "work"), ("meta", "{\"render\":\"lathe\"}"));
            Check(again.ExitCode == 0 && SculptRoom.V(again, "meta_changed") == "0" && LogLines() == l4, "④ 同樣的值再下 ⇒ 0 筆、log 不多一行");
            string batch = Out("b.tsv");
            File.WriteAllText(batch, "work\t\t{\"n\":null}\n");
            var wb = Sub("q", "part", "meta", ("file", batch));
            Check(wb.ExitCode == 0 && SculptRoom.V(wb, "meta_changed") == "1" && !Json(WorkMetaF)["meta"]["n"].Exists, "④ 批次 work<TAB><TAB>{n:null} ⇒ 刪掉 n：" + SculptRoom.All(wb));

            // ── ⑤ 🔴 清掉 ⇒ 逐位元組回原樣 ─────────────────────────────────────
            Check(Sub("q", "part", "meta", ("part", "work"), ("meta", "none")).ExitCode == 0 && !File.Exists(WorkMetaF), "⑤ meta=none ⇒ work_meta.json 刪掉");
            var pj2 = Parts("p2.json");
            Check(pj2 != null && Bytes(Out("p2.json")).SequenceEqual(p0), "⑤ 🔴 清掉之後 host 的部件清單逐位元組 ＝ 設之前");

            // ── ⑥ 🔴 檔壞了 ⇒ 大聲擋 ──────────────────────────────────────────
            File.WriteAllText(WorkMetaF, "{\"schema\": 9, \"meta\": {\"render\": \"lathe\"}}\n");
            var bad = room.Run(("op", "export"), ("format", "parts"), ("work", "host"), ("out", Out("p3.json")));
            Check(bad.ExitCode == 2 && SculptRoom.All(bad).Contains("work_meta.json"), "⑥ 🔴 schema 9 ⇒ export parts exit 2、講哪個檔：" + SculptRoom.All(bad));
            File.Delete(WorkMetaF);
            readings.Add("meta_log " + LogLines() + " 行");
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
