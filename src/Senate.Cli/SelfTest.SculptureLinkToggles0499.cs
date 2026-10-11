// 淨室驗收（TASK-0499）：觀測頁的關聯開關 —— 樹攤平（含巢狀）＋預設照 enabled、關一條（連同底下）不畫、只關底下那條上一層照畫、
// 作品裡設為關的可以這次打開、全部關 ⇒ links=none、全部預設 ⇒ 什麼都不送；開關一個字都不寫回 links.json；
// 換作品只出新作品的路徑；沒有關聯 ⇒ 沒有列。走頁面同一支 FlattenLinkRows／LinkToggleArgs，組裝走 Cmd_Sculpture.TryPrepareView（同頁面預覽）。
using Senate.Cli.Pages;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureLinkTogglesCleanRoom()
    {
        const string name = "觀測頁關聯開關：巢狀攤平＋預設照 enabled／關一條連同底下不畫／只關底下那條上一層照畫／設為關的可打開／全關＝none／全預設不送／🔴 只送 links= 會把關掉的子關聯畫回來／不寫回 links.json／換作品不帶舊名字／沒關聯沒有列（淨室，TASK-0499）";
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
                ("commission", "開關 " + id), ("commission_ref", "user:toggle:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            bool Fill(string id, int n, int color) => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", "0"), ("x2", (n - 1).ToString()), ("y1", "0"), ("y2", "0"),
                ("z1", "0"), ("z2", "0"), ("color", color.ToString())).ExitCode == 0;
            bool Link(string parent, string nm, string child, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("name", nm), ("source_work", child), ("at", at) };
                a.AddRange(more);
                return Sub(parent, "link", a.ToArray()).ExitCode == 0;
            }

            // 船殼 h（4 格）← 甲板 deck（8）← 船艙 deck/cabin（2）；h ← 砲 gun（2）；h ← 備用 spare（1，作品裡設為關）
            Check(Create("h", "16") && Create("dk", "8,8,4") && Create("cb", "2,2,2") && Create("gun", "2,1,1") && Create("off", "1") && Create("solo", "8") && Create("x", "1") && Create("bare", "4"), "作品");
            Check(Fill("h", 4, 10) && Fill("dk", 8, 20) && Fill("cb", 2, 30) && Fill("gun", 2, 40) && Fill("off", 1, 50) && Fill("solo", 1, 60) && Fill("x", 1, 70) && Fill("bare", 1, 80), "各自的格");
            Check(Link("h", "deck", "dk", "4,4,4") && Link("dk", "cabin", "cb", "1,1,1") && Link("h", "gun", "gun", "10,10,0") && Link("h", "spare", "off", "0,5,0", ("enabled", "0"))
                  && Link("solo", "x", "x", "3,3,3"), "關聯（巢狀、一條設為關）");
            string linksFile = store.SpacePaths("h").LinksFile;
            string before = File.ReadAllText(linksFile);

            var rows = SculptureViewerPage.FlattenLinkRows(SCP_SculptLinks.Tree(store, "h"));
            string Shape(List<SculptureViewerPage.LinkRow> r) => string.Join(" ", r.Select(x => x.Path + ":" + x.Depth + ":" + (x.Enabled ? "on" : "off")));
            Check(Shape(rows) == "deck:0:on deck/cabin:1:on gun:0:on spare:0:off", "① 樹攤平（父在子前、巢狀深度、預設照 enabled）：" + Shape(rows));

            // 一個「頁面 session」：沒動過的開關 ＝ 那一條的 enabled
            var session = new Dictionary<string, bool>(StringComparer.Ordinal);
            (string L, string H) Args(string work, List<SculptureViewerPage.LinkRow> r)
                => SculptureViewerPage.LinkToggleArgs(r, x => session.TryGetValue(work + "|" + x.Path, out bool v) ? v : x.Enabled);
            int Total(string work, (string L, string H) a, out string err)
            {
                var raw = new Dictionary<string, string> { ["data_root"] = room.Data, ["letters_root"] = room.Letters, ["work"] = work, ["out"] = Path.Combine(room.Root, "t.png") };
                if (a.L.Length > 0) raw["links"] = a.L;
                if (a.H.Length > 0) raw["hide_links"] = a.H;
                return Cmd_Sculpture.TryPrepareView(raw, out SCP_SculptViewPlan p, out _, out err) ? p.TotalVoxels : -1;
            }
            string Show((string L, string H) a, int t) => "links=" + a.L + " hide=" + a.H + " ⇒ " + t;

            var a0 = Args("h", rows);
            Check(a0 == ("", "") && Total("h", a0, out _) == 16, "全部預設 ⇒ 什麼都不送、照作品的開關 16 格（4＋8＋2＋2，spare 關著）：" + Show(a0, Total("h", a0, out _)));

            session["h|deck"] = false;
            var a1 = Args("h", rows);
            Check(Total("h", a1, out string e1) == 6, "② 關掉 deck ⇒ 連船艙一起不畫（4＋砲 2）：" + Show(a1, Total("h", a1, out _)) + e1);
            session["h|deck/cabin"] = true;
            Check(Total("h", Args("h", rows), out _) == 6, "🔴 上一層關著，只開底下那條 ⇒ 底下照樣不畫");
            session.Remove("h|deck"); session.Remove("h|deck/cabin");
            Check(Args("h", rows) == ("", "") && Total("h", Args("h", rows), out _) == 16, "② 打開回來 ⇒ 又是 16（回到預設、不送參數）");

            session["h|deck/cabin"] = false;
            var a3 = Args("h", rows);
            Check(Total("h", a3, out _) == 14, "③ 只關 deck/cabin ⇒ 甲板照畫、只少船艙 2：" + Show(a3, Total("h", a3, out _)));
            // 🔴 為什麼不能只送 links=：點名的那條底下沒點名的，會照它自己的 enabled 畫回來
            Check(Total("h", (a3.L, ""), out _) == 16, "🔴 反向對照：同一組開關只送 links=（不送 hide）⇒ 關掉的船艙被畫回來（16），所以 hide 那半是必要的");
            session.Remove("h|deck/cabin");

            session["h|spare"] = true;
            var a4 = Args("h", rows);
            Check(Total("h", a4, out _) == 17, "作品裡設為關的 spare，這次打開 ⇒ 多 1 格：" + Show(a4, Total("h", a4, out _)));
            session.Remove("h|spare");

            foreach (var r in rows) session["h|" + r.Path] = false;
            var a5 = Args("h", rows);
            Check(a5.L == "none" && Total("h", a5, out _) == 4, "全部關 ⇒ links=none、只剩本作品 4 格：" + Show(a5, Total("h", a5, out _)));

            // ④ 不寫回：開關翻了一整輪，links.json 逐字不變、sub=links 那幾條照樣是（開）
            var lst = Sub("h", "links");
            string lstReport = File.Exists(room.Letters + "/p/cmd/sculpture_work.md") ? File.ReadAllText(room.Letters + "/p/cmd/sculpture_work.md") : "";
            Check(File.ReadAllText(linksFile) == before && lst.ExitCode == 0 && lstReport.Contains("`deck` → dk") && lstReport.Contains("@4,4,4 turn 0（開）"),
                  "④ 🔴 開關全關過一輪 ⇒ links.json 逐字不變、work sub=links 的 deck 仍是（開）");

            // ⑤ 換作品：清單換成新作品的路徑；h 那邊全關的開關帶不過來（id 帶作品 id），不會報關聯名不存在
            var soloRows = SculptureViewerPage.FlattenLinkRows(SCP_SculptLinks.Tree(store, "solo"));
            var a6 = Args("solo", soloRows);
            int t6 = Total("solo", a6, out string e6);
            Check(Shape(soloRows) == "x:0:on" && a6 == ("", "") && t6 == 2, "⑤ 換到 solo ⇒ 只有 x、h 的開關沒帶過來、照常組 2 格：" + Shape(soloRows) + e6);

            // ⑥ 沒有關聯的作品 ⇒ 沒有列（頁面不畫那一塊）
            Check(SculptureViewerPage.FlattenLinkRows(SCP_SculptLinks.Tree(store, "bare")).Count == 0, "⑥ 沒有關聯 ⇒ 0 列");

            return new CheckRow(name, failures.Count == 0
                ? "攤平 deck/cabin 深度 1、spare 預設關；全預設不送 16；關 deck ⇒ 6（🔴 上層關、只開船艙仍 6）；開回 16；只關船艙 ⇒ 14（🔴 只送 links= 會畫回 16）；打開 spare ⇒ 17；全關 ⇒ none 4；🔴 links.json 逐字不變、sub=links 仍（開）；換 solo 只有 x、照組 2；沒關聯 0 列"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
