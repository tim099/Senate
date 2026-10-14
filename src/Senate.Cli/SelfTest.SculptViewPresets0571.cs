// 淨室驗收（TASK-0571）：預覽 preset —— work sub=preset 存一組（關聯路徑當場驗）、preset=<名字> 跟手打那幾個參數組出來的格數一樣、
// preset=all ＝ 不給、名字打錯 ⇒ 擋並列出有哪幾組、跟手打的同一個參數同時給 ⇒ 擋、繩帆模型的鍵在 stats 略過、all 是保留字、
// 只有作者能存、觀測一輪 view_presets.json 逐字不變、頁面那一支展開（SCP_SculptViewPresets.Expand）跟 CLI 的 preset_args 同一份、unpreset 拿得掉。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptViewPresetsCleanRoom()
    {
        const string name = "預覽 preset：存一組＋路徑當場驗／preset= 跟手打一樣／all ＝ 不給／打錯名字擋／🔴 跟手打同一個參數擋／繩帆模型在 stats 略過／all 保留字／只有作者能存／不寫回／頁面展開＝CLI／unpreset（淨室，TASK-0571）";
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
                ("commission", "preset " + id), ("commission_ref", "user:preset:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string id, string sub, string persona, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", persona) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            bool Fill(string id, int n, int color) => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", "0"), ("x2", (n - 1).ToString()), ("y1", "0"), ("y2", "0"),
                ("z1", "0"), ("z2", "0"), ("color", color.ToString())).ExitCode == 0;
            SCP_CmdResult Stats(params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "stats"), ("work", "ship"), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            string Total(SCP_CmdResult r) => r.ExitCode + ":" + SculptRoom.V(r, "total_voxels");

            // 組裝件 ship（自己 1 格）← 船殼 hull（4）、甲板 deck（8）
            Check(Create("ship", "16") && Create("hull", "8,2,2") && Create("deck", "8,2,2"), "作品");
            Check(Fill("ship", 1, 10) && Fill("hull", 4, 20) && Fill("deck", 8, 30), "各自的格");
            Check(Sub("ship", "link", "p", ("name", "船殼"), ("source_work", "hull"), ("at", "0,4,0")).ExitCode == 0
                  && Sub("ship", "link", "p", ("name", "甲板"), ("source_work", "deck"), ("at", "0,8,0")).ExitCode == 0, "關聯");
            string file = store.SpacePaths("ship").ViewPresetsFile;

            // ① 存：藏船殼、只看船殼（只要關聯，不要組裝件自己）、繩帆模型（stats 略過）；打錯路徑當場擋、沒寫檔
            var bad = Sub("ship", "preset", "p", ("name", "壞"), ("hide_links", "船殼,沒這條"));
            Check(bad.ExitCode == 2 && SculptRoom.All(bad).Contains("沒這條") && !File.Exists(file), "① 🔴 關聯路徑打錯 ⇒ 擋、view_presets.json 沒生出來：" + bad.ExitCode);
            Check(Sub("ship", "preset", "p", ("name", "藏船殼"), ("hide_links", "船殼"), ("note", "看甲板")).ExitCode == 0, "① 存 藏船殼");
            Check(Sub("ship", "preset", "p", ("name", "只看船殼"), ("links", "船殼"), ("hide_links", "self")).ExitCode == 0, "① 存 只看船殼");
            Check(Sub("ship", "preset", "p", ("name", "繩"), ("hide_ropes", "沒有的繩"), ("exclude_color", "30")).ExitCode == 0, "① 存 繩（stats 只吃 exclude_color）");
            var allName = Sub("ship", "preset", "p", ("name", "all"), ("links", "none"));
            Check(allName.ExitCode == 2, "① all 是保留字 ⇒ 擋");
            var notOwner = Sub("ship", "preset", "q", ("name", "別人的"), ("links", "none"));
            Check(notOwner.ExitCode == 2, "① 不是作者 ⇒ 擋");
            string saved = File.ReadAllText(file);

            // ② 觀測：preset ＝ 手打那幾個參數
            string plain = Total(Stats()), pAll = Total(Stats(("preset", "all")));
            Check(plain == "0:13" && pAll == plain, "② all ＝ 不給（1＋4＋8 ＝ 13）：" + plain + " / " + pAll);
            string pHide = Total(Stats(("preset", "藏船殼"))), mHide = Total(Stats(("hide_links", "船殼")));
            Check(pHide == "0:9" && pHide == mHide, "② 藏船殼 ＝ hide_links=船殼（9）：" + pHide + " / " + mHide);
            string pOnly = Total(Stats(("preset", "只看船殼"))), mOnly = Total(Stats(("links", "船殼"), ("hide_links", "self")));
            Check(pOnly == "0:4" && pOnly == mOnly, "② 只看船殼 ＝ links=船殼 hide_links=self（4）：" + pOnly + " / " + mOnly);
            var rRig = Stats(("preset", "繩"));
            string mRig = Total(Stats(("exclude_color", "30")));
            Check(Total(rRig) == mRig && rRig.ExitCode == 0 && SculptRoom.All(rRig).Contains("略過 hide_ropes") && SculptRoom.V(rRig, "preset_args") == "exclude_color=30",
                  "② 繩：stats 略過 hide_ropes、只補 exclude_color（＝ 手打 exclude_color=30）：" + Total(rRig) + " / " + mRig + "｜" + SculptRoom.V(rRig, "preset_args"));

            // ② export（view 那一族）：同一份清單逐字相同
            string Export(string tag, params (string, string)[] more)
            {
                string outp = Path.Combine(room.Root, "exp_" + tag + ".txt");
                var a = new List<(string, string)> { ("op", "export"), ("work", "ship"), ("persona", "p"), ("format", "list"), ("out", outp) };
                a.AddRange(more);
                return room.Run(a.ToArray()).ExitCode == 0 && File.Exists(outp) ? File.ReadAllText(outp) : "fail";
            }
            string eP = Export("p", ("preset", "藏船殼")), eM = Export("m", ("hide_links", "船殼")), eA = Export("a");
            Check(eP != "fail" && eP == eM && eP != eA, "② export list：preset=藏船殼 跟 hide_links=船殼 逐字相同、跟不給不同");

            // ③ 反向對照
            var typo = Stats(("preset", "藏船売"));
            Check(typo.ExitCode == 2 && SculptRoom.All(typo).Contains("藏船殼") && SculptRoom.All(typo).Contains("只看船殼"), "③ 🔴 名字打錯 ⇒ 擋、列出有哪幾組：" + typo.ExitCode);
            var clash = Stats(("preset", "藏船殼"), ("hide_links", "甲板"));
            Check(clash.ExitCode == 2 && SculptRoom.All(clash).Contains("不默默挑一個"), "③ 🔴 preset 跟手打的 hide_links 同時給 ⇒ 擋：" + clash.ExitCode);
            var onWrite = room.Run(("op", "box"), ("work", "ship"), ("persona", "p"), ("preset", "藏船殼"), ("x1", "5"), ("x2", "5"), ("y1", "0"), ("y2", "0"), ("z1", "0"), ("z2", "0"), ("color", "9"));
            Check(onWrite.ExitCode == 2, "③ 落子類帶 preset ⇒ 擋：" + onWrite.ExitCode);
            Check(File.ReadAllText(file) == saved, "③ 觀測一輪 ⇒ view_presets.json 逐字不變");

            // ④ 頁面那一支展開 ＝ CLI 的 preset_args（同一份）
            var pf = SCP_SculptViewPresets.Read(store.SpacePaths("ship"));
            var exp = SCP_SculptViewPresets.Expand(pf, "只看船殼", "stats", new List<string>());
            string page = string.Join(";", exp.Select(kv => kv.Key + "=" + kv.Value));
            string cli = SculptRoom.V(Stats(("preset", "只看船殼")), "preset_args");
            Check(page == cli && page == "links=船殼;hide_links=self", "④ 頁面展開 ＝ CLI preset_args：" + page + " / " + cli);
            Check(SCP_SculptViewPresets.Expand(pf, "繩", "view", new List<string>()).ContainsKey("hide_ropes"), "④ view 才吃繩帆模型的鍵");

            // ⑤ 列、拿掉
            var list = Sub("ship", "presets", "p");
            Check(list.ExitCode == 0 && SculptRoom.V(list, "preset_names") == "藏船殼;只看船殼;繩", "⑤ presets 列出三組：" + SculptRoom.V(list, "preset_names"));
            Check(Sub("ship", "unpreset", "p", ("name", "繩")).ExitCode == 0 && Stats(("preset", "繩")).ExitCode == 2, "⑤ unpreset 之後再叫 ⇒ 擋");

            return new CheckRow(name, failures.Count == 0
                ? "🔴 打錯路徑擋、沒生檔；all 保留字擋；非作者擋｜all ＝ 不給 13；藏船殼 ＝ hide_links 9；只看船殼 ＝ links＋self 4；繩在 stats 略過、只補 exclude_color（＝ 手打）｜🔴 打錯名字擋並列名；🔴 跟手打同參數擋；落子帶 preset 擋；檔逐字不變｜頁面展開 ＝ CLI；presets 列三組、unpreset 後擋"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
