// 淨室驗收（TASK-0517）：外部模型與關聯的 meta、`export format=parts` 的固定點 ——
// ① 模型 meta：寫進 models.json（schema 2）、數字原樣是 JSON 數字（⛔ 不是字串）、讀數印得出來。
// ② 合併：給的鍵覆寫、null 刪鍵、沒給 meta 只改位置 ⇒ meta 原封不動。
// ③ 🔴 壞的 meta（巢狀、陣列、布林、讀不了的 JSON、鍵含空白）⇒ exit 2、models.json 逐位元組不變。
// ④ 關聯 meta：只改 meta 不用 overwrite；overwrite 搬位置 ⇒ meta 還在；schema：鏡像 2 → 加 meta 3 → 清掉回 2。
// ⑤ export parts：模型的固定點 ＝ 手算（縮放＋轉 90°＋at）、關聯的固定點 ＝ 手算（鏡像＋at）、關聯外框 ＝ 手算、根作品的固定點；meta 型別原樣。
// ⑥ 🔴 反向對照：加 meta 再 meta=none ⇒ models.json／links.json 逐位元組回到加之前；沒用 meta 的檔 schema 不變、沒有 meta 鍵。
// ⑦ 🔴 手改的檔：有 meta 卻是 schema 1 ⇒ 大聲讀不了（⛔ 不默默丟掉）。
using System.Globalization;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureMetaCleanRoom()
    {
        const string name = "雕刻 meta 與部件清單：模型／關聯 meta 寫入（數字原樣）、合併與刪鍵、改位置不清 meta／🔴 壞 meta 零寫入／schema 升降（鏡像 2、meta 3）／export parts 固定點＝手算（模型縮放轉向、關聯鏡像）、外框＝手算／🔴 加了再清 ⇒ 檔逐位元組回原樣／🔴 手改 schema 1 帶 meta ⇒ 大聲讀不了（淨室，TASK-0517）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            string Out(string f) => Path.Combine(room.Root, f).Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(new SculptProbeRenderer());
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "meta " + id), ("commission_ref", "user:meta:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            SCP_CmdResult Dot(string id, int x, int y, int z, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString()));
            string ModelsFile(string id) => store.SpacePaths(id).ModelsFile;
            string LinksFile(string id) => store.SpacePaths(id).LinksFile;
            byte[] Bytes(string f) => File.Exists(f) ? File.ReadAllBytes(f) : Array.Empty<byte>();
            SCP_JsonData Json(string f) => SCP_JsonParser.Parse(File.ReadAllText(f), false);
            bool Near(SCP_JsonData p, double x, double y, double z) => p.IsArray && p.Count == 3
                && Math.Abs(p[0].AsDouble() - x) < 1e-6 && Math.Abs(p[1].AsDouble() - y) < 1e-6 && Math.Abs(p[2].AsDouble() - z) < 1e-6;
            string P(SCP_JsonData p) => p.ToJson(false);

            // host 40³（0.1 m，固定點 r (0,0,0)）；part 10³（0.02 m，固定點 muzzle (9,0,0)、breech (0,0,0)）；child 8×6×4（固定點 a (1,2,3)）
            Check(Create("host", "40,40,40") && Dot("host", 0, 0, 0, 60).ExitCode == 0 && Sub("host", "update", ("meters_per_voxel", "0.1")).ExitCode == 0
                  && Sub("host", "anchor", ("name", "r"), ("at", "0,0,0")).ExitCode == 0, "host");
            Check(Create("part", "10,10,10") && Dot("part", 0, 0, 0, 224).ExitCode == 0 && Sub("part", "update", ("meters_per_voxel", "0.02")).ExitCode == 0
                  && Sub("part", "anchor", ("name", "muzzle"), ("at", "9,0,0")).ExitCode == 0 && Sub("part", "anchor", ("name", "breech"), ("at", "0,0,0")).ExitCode == 0, "part");
            Check(Create("child", "8,6,4") && Dot("child", 0, 0, 0, 100).ExitCode == 0 && Sub("child", "anchor", ("name", "a"), ("at", "1,2,3")).ExitCode == 0, "child");

            // ── ⑥ 之前：沒有 meta 的模型與關聯 ──────────────────────────────
            Check(Sub("host", "model", ("name", "gun"), ("source_work", "part"), ("at", "20,20,10"), ("rotate", "0,0,90")).ExitCode == 0, "加模型 gun（scale 自動 0.2）");
            Check(Sub("host", "link", ("name", "kid"), ("source_work", "child"), ("at", "10,5,2"), ("mirror", "y")).ExitCode == 0, "加關聯 kid（mirror=y）");
            byte[] models0 = Bytes(ModelsFile("host")), links0 = Bytes(LinksFile("host"));
            var mj0 = Json(ModelsFile("host"));
            Check(mj0["schema"].AsLong() == 1 && !mj0["models"][0]["meta"].Exists, "沒用 meta：models.json schema 1、沒有 meta 鍵");
            Check(Json(LinksFile("host"))["schema"].AsLong() == 2, "只有鏡像：links.json schema 2");

            // ── ① 模型 meta ─────────────────────────────────────────────────
            var w1 = Sub("host", "model", ("name", "gun"), ("meta", "{\"kind\":\"gun\",\"gun\":\"24l\",\"hp\":100}"));
            var mj1 = Json(ModelsFile("host"));
            SCP_JsonData meta1 = mj1["models"][0]["meta"];
            Check(w1.ExitCode == 0 && mj1["schema"].AsLong() == 2, "寫 meta ⇒ schema 2：" + SculptRoom.All(w1));
            Check(meta1["kind"].IsString && meta1["kind"].AsString() == "gun" && meta1["hp"].Type == SCP_JsonType.Number && meta1["hp"].AsLong() == 100,
                  "meta 型別原樣（hp 是數字不是字串）：" + meta1.ToJson(false));
            var listed = Sub("host", "models");
            Check(listed.ExitCode == 0 && File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md").Contains("meta gun=24l, hp=100, kind=gun"), "sub=models 讀數印出 meta（依鍵排序）");

            // ── ② 合併、刪鍵、改位置不清 meta ────────────────────────────────
            Check(Sub("host", "model", ("name", "gun"), ("meta", "{\"hp\":null,\"powder_lb\":9.5}")).ExitCode == 0, "合併 meta");
            Check(Sub("host", "model", ("name", "gun"), ("at", "20,20,11")).ExitCode == 0 && Sub("host", "model", ("name", "gun"), ("at", "20,20,10")).ExitCode == 0, "只改位置");
            SCP_JsonData meta2 = Json(ModelsFile("host"))["models"][0]["meta"];
            Check(!meta2["hp"].Exists && meta2["powder_lb"].AsDouble() == 9.5 && meta2["kind"].AsString() == "gun" && meta2["gun"].AsString() == "24l",
                  "null 刪鍵、新鍵加進來、舊鍵還在、改位置沒清掉：" + meta2.ToJson(false));

            // ── ③ 🔴 壞 meta 零寫入 ─────────────────────────────────────────
            byte[] beforeBad = Bytes(ModelsFile("host"));
            foreach (string bad in new[] { "{\"a\":{\"b\":1}}", "{\"a\":[1,2]}", "{\"a\":true}", "{\"a\":", "[1]", "{\"a b\":1}" })
            {
                var r = Sub("host", "model", ("name", "gun"), ("meta", bad));
                Check(r.ExitCode == 2 && Bytes(ModelsFile("host")).SequenceEqual(beforeBad), "🔴 壞 meta `" + bad + "` ⇒ exit 2、models.json 不變（exit " + r.ExitCode + "）");
            }

            // ── ④ 關聯 meta ──────────────────────────────────────────────────
            Check(Sub("host", "link", ("name", "kid"), ("meta", "{\"kind\":\"magazine\",\"powder_lb\":9000}")).ExitCode == 0, "關聯只改 meta（不用 overwrite）");
            Check(Json(LinksFile("host"))["schema"].AsLong() == 3, "有 meta ⇒ links.json schema 3");
            Check(Sub("host", "link", ("name", "kid"), ("at", "11,5,2"), ("overwrite", "1")).ExitCode == 0 && Sub("host", "link", ("name", "kid"), ("at", "10,5,2"), ("overwrite", "1")).ExitCode == 0, "overwrite 搬位置");
            SCP_JsonData lmeta = Json(LinksFile("host"))["links"][0]["meta"];
            Check(lmeta["kind"].AsString() == "magazine" && lmeta["powder_lb"].AsLong() == 9000, "overwrite 搬位置 ⇒ meta 還在：" + lmeta.ToJson(false));

            // ── ⑤ export parts ──────────────────────────────────────────────
            string parts = Out("host.parts.json");
            var ex = room.Run(("op", "export"), ("format", "parts"), ("work", "host"), ("out", parts));
            Check(ex.ExitCode == 0 && File.Exists(parts), "export parts：" + SculptRoom.All(ex));
            var pj = Json(parts);
            SCP_JsonData gun = pj["models"][0], kid = pj["links"][0];
            // 模型：muzzle 格心 (9.5,0.5,0.5) ×0.2 ⇒ (1.9,0.1,0.1)，繞 Z 90 ⇒ (−0.1,1.9,0.1)，＋at (20,20,10) ⇒ (19.9,21.9,10.1)
            Check(Near(gun["points"]["muzzle"], 19.9, 21.9, 10.1), "模型固定點 muzzle ＝ 手算 (19.9,21.9,10.1)：" + P(gun["points"]["muzzle"]));
            // breech 格心 (0.5,0.5,0.5) ×0.2 ⇒ (0.1,0.1,0.1) ⇒ (−0.1,0.1,0.1) ⇒ (19.9,20.1,10.1)
            Check(Near(gun["points"]["breech"], 19.9, 20.1, 10.1), "模型固定點 breech ＝ 手算 (19.9,20.1,10.1)：" + P(gun["points"]["breech"]));
            // 關聯：a 格心 (1.5,2.5,3.5)，mirror y 在 6 格的框裡 ⇒ y 3.5，＋at (10,5,2) ⇒ (11.5,8.5,5.5)
            Check(Near(kid["points"]["a"], 11.5, 8.5, 5.5), "關聯固定點 a ＝ 手算 (11.5,8.5,5.5)：" + P(kid["points"]["a"]));
            SCP_JsonData kbox = kid["box"];
            Check(kbox.Count == 6 && kbox[0].AsDouble() == 10 && kbox[1].AsDouble() == 5 && kbox[2].AsDouble() == 2 && kbox[3].AsDouble() == 18 && kbox[4].AsDouble() == 11 && kbox[5].AsDouble() == 6,
                  "關聯外框 ＝ 手算 [10,5,2,18,11,6]：" + kbox.ToJson(false));
            Check(Near(pj["points"]["r"], 0.5, 0.5, 0.5), "根作品固定點 r ＝ (0.5,0.5,0.5)：" + P(pj["points"]["r"]));
            Check(gun["meta"]["powder_lb"].Type == SCP_JsonType.Number && kid["meta"]["powder_lb"].Type == SCP_JsonType.Number && gun["box"].Count == 6,
                  "parts 的 meta 數字原樣、模型有外框");
            readings.Add("muzzle " + P(gun["points"]["muzzle"]) + "、breech " + P(gun["points"]["breech"]) + "、a " + P(kid["points"]["a"]) + "、kid box " + kbox.ToJson(false));

            // ── ⑥ 🔴 反向對照：清掉 ⇒ 檔回到加 meta 之前 ─────────────────────
            Check(Sub("host", "model", ("name", "gun"), ("meta", "none")).ExitCode == 0 && Sub("host", "link", ("name", "kid"), ("meta", "none")).ExitCode == 0, "meta=none 全清");
            Check(Bytes(ModelsFile("host")).SequenceEqual(models0), "🔴 models.json 清掉 meta 後逐位元組 ＝ 加之前（schema 回 1、沒有 meta 鍵）");
            Check(Bytes(LinksFile("host")).SequenceEqual(links0), "🔴 links.json 清掉 meta 後逐位元組 ＝ 加之前（schema 回 2）");

            // ── ⑦ 🔴 手改：schema 1 卻有 meta ⇒ 大聲讀不了 ─────────────────────
            string mf = ModelsFile("host");
            File.WriteAllText(mf, File.ReadAllText(mf).Replace("\"enabled\": true,", "\"meta\": {\"kind\": \"gun\"}, \"enabled\": true,"));
            string? thrown = null;
            try { SCP_SculptModels.ReadModels(store.SpacePaths("host")); } catch (InvalidOperationException e) { thrown = e.Message; }
            Check(thrown != null && thrown.Contains("schema 1"), "🔴 schema 1 帶 meta ⇒ 讀不了：" + thrown);
            File.WriteAllBytes(mf, models0);
            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
