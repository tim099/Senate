// 淨室驗收：補缺口不限作者（TASK-1293；Tim 2026-11-14「只有作者可編輯作品這點 修復缺口不算(這算是補上瑕疵)」）——
//   ① 不是作者（q）stampvox 帶 repair=TASK-1293 落在空格 ⇒ 放行，格子照清單、記進那件作品的 meta_log.jsonl（part repair、persona q、owner p、repair 單號）
//   ② 清單裡碰到作者原有的格 ⇒ 那幾格照舊（落子類預設跳過）：原有的一格顏色都不變
//   ③ 反向對照：q 沒帶 repair、repair 不是單號、帶 overwrite=1、沒帶 expect_pixels、draft=1、用 box／carvevox 帶 repair ⇒ 全部擋（exit 2、事件沒多、原有的格不變）
//   ④ 作者自己帶 repair 照舊落、不記 meta_log
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptRepairNotAuthorCleanRoom()
    {
        const string name = "雕刻：補缺口不限作者（stampvox 帶 repair=單號、只補空格）—— 放行並記 meta_log、原有的格不動；🔴 沒單號、overwrite=1、沒 expect_pixels、草稿、box／carvevox 都擋";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            Directory.CreateDirectory(Path.Combine(room.Letters, "q", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 10 };
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "house"), ("title", "房子"), ("size", "8"),
                           ("commission", "房子"), ("commission_ref", "user:house"), ("account", "acct")).ExitCode == 0, "作品建立");
            SCP_SculptSpace Space() => store.Engine(store.Load("house"), room.Data).LoadSpace();
            int Events() => SCP_SculptStore.ListEvents(store.SpacePaths("house")).Count;
            string Log() { string f = store.SpacePaths("house").MetaLogFile; return File.Exists(f) ? File.ReadAllText(f) : ""; }
            int Lines() => Log().Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            string List(string file, params string[] lines) { string path = Path.Combine(room.Root, file); File.WriteAllText(path, string.Join("\n", lines) + "\n"); return path; }
            SCP_CmdResult Vox(string who, string list, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "stampvox"), ("work", "house"), ("persona", who), ("voxels", list), ("at", "0,0,0") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }

            // 作者先放兩格牆（0,0,0 與 1,0,0，顏色 141）
            Check(room.Run(("op", "box"), ("work", "house"), ("persona", "p"), ("x1", "0"), ("x2", "1"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "141")).ExitCode == 0, "作者放兩格牆");

            // ① q 補三格空格
            string gap = List("gap.vox.txt", "0,0,1,141", "1,0,1,141", "2,0,1,141");
            int e0 = Events();
            var ok = Vox("q", gap, ("repair", "TASK-1293"), ("expect_pixels", "3"));
            var s1 = Space();
            Check(ok.ExitCode == 0 && Events() == e0 + 1 && s1.Voxels.Get(0, 0, 1) == 141 && s1.Voxels.Get(2, 0, 1) == 141, "🔴 q 帶 repair 補空格應放行、三格照清單：" + SculptRoom.All(ok));
            Check(Log().Contains("\"persona\":\"q\"") && Log().Contains("\"owner\":\"p\"") && Log().Contains("\"part\":\"repair\"") && Log().Contains("\"repair\":\"TASK-1293\"") && Log().Contains("2026-11-14"),
                "q 補缺口應記進 meta_log（persona q、owner p、part repair、單號、規則出處）：" + Log());

            // ② 清單碰到原有的格 ⇒ 那一格不動
            string hit = List("hit.vox.txt", "0,0,0,13", "3,0,0,13");
            var h = Vox("q", hit, ("repair", "TASK-1293"), ("expect_pixels", "2"));
            var s2 = Space();
            Check(h.ExitCode == 0 && s2.Voxels.Get(0, 0, 0) == 141 && s2.Voxels.Get(3, 0, 0) == 13, "🔴 清單碰到作者原有的格 ⇒ 那一格照舊 141、空格補上：" + SculptRoom.All(h));

            // ③ 反向對照
            string more = List("more.vox.txt", "4,0,0,13");
            void Deny(string why, SCP_CmdResult r, int ev)
                => Check(r.ExitCode == 2 && Events() == ev && Space().Voxels.Get(4, 0, 0) == 0 && Space().Voxels.Get(0, 0, 0) == 141, "🔴 " + why + " 應擋（exit 2、事件沒多、原有的格不變）：exit " + r.ExitCode);
            int e1 = Events();
            Deny("q 沒帶 repair", Vox("q", more, ("expect_pixels", "1")), e1);
            Deny("repair 不是單號", Vox("q", more, ("repair", "fix"), ("expect_pixels", "1")), e1);
            Deny("repair 帶 overwrite=1", Vox("q", List("ow.vox.txt", "0,0,0,13"), ("repair", "TASK-1293"), ("expect_pixels", "1"), ("overwrite", "1")), e1);
            Deny("repair 沒帶 expect_pixels", Vox("q", more, ("repair", "TASK-1293")), e1);
            Deny("repair 進草稿", Vox("q", more, ("repair", "TASK-1293"), ("expect_pixels", "1"), ("draft", "1")), e1);
            Deny("box 帶 repair", room.Run(("op", "box"), ("work", "house"), ("persona", "q"), ("x1", "4"), ("x2", "4"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "13"), ("repair", "TASK-1293")), e1);
            Deny("carvevox 帶 repair", room.Run(("op", "carvevox"), ("work", "house"), ("persona", "q"), ("voxels", List("cut.vox.txt", "0,0,0")), ("at", "0,0,0"), ("expect_pixels", "1"), ("repair", "TASK-1293")), e1);

            // ④ 作者自己帶 repair：照舊落、不記
            int l0 = Lines();
            Check(Vox("p", more, ("repair", "TASK-1293"), ("expect_pixels", "1")).ExitCode == 0 && Space().Voxels.Get(4, 0, 0) == 13 && Lines() == l0, "作者自己帶 repair 照舊落、不記 meta_log");

            return new CheckRow(name, failures.Count == 0 ? "q 帶 repair 補空格放行並記 meta_log；碰到原有的格不動；沒單號、不是單號、overwrite=1、沒 expect_pixels、草稿、box、carvevox 都擋；作者自己不記" : string.Join("；", failures),
                                failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, "例外：" + e, CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
