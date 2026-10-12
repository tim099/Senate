// 淨室驗收（TASK-0505）：作品 tags —— 建立子作品帶 tags=sub；掛在任務底下（parent_work）但沒標的仍是主作品；
// work update tags= 轉小寫、去重、空字串清空；別人改不了、不合法的整份擋下，兩者書卡一個位元組都不動；work list 印出標籤；
// 觀測頁 FilterWorks 吃真的書卡（同一支 store.List）。
using Senate.Cli.Pages;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureTagsCleanRoom()
    {
        const string name = "雕刻作品 tags：子作品帶 sub／有 parent_work 沒標仍是主作品／update 轉小寫去重、空＝清空／🔴 別人改不了、不合法整份擋下且書卡零位元組變動／list 印標籤／觀測頁只列主作品看標籤（淨室，TASK-0505）";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            foreach (string p in new[] { "p", "q" }) Directory.CreateDirectory(Path.Combine(room.Letters, p, "profile"));
            var pay = new SculptProbeGateway { Vouchers = 10 };
            SCP_CanvasGatewayHost.Factory = _ => pay;
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            var store = new SCP_SculptWorks(new SCP_DataRoot(room.Data));
            SCP_CmdResult Work(string persona, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("persona", persona) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            string Card(string id) => File.ReadAllText(Path.Combine(store.Folder(id), "work.json"));

            Check(Work("p", ("sub", "create"), ("id", "ship"), ("title", "船"), ("size", "8"), ("commission", "造船"), ("commission_ref", "user:tags:ship"), ("account", "acct")).ExitCode == 0, "建立主任務");
            var deck = Work("p", ("sub", "create"), ("id", "deck"), ("title", "甲板"), ("size", "8"), ("parent_work", "ship"), ("tags", "sub"));
            var asm = Work("p", ("sub", "create"), ("id", "asm"), ("title", "組裝"), ("size", "8"), ("parent_work", "ship"));
            Check(deck.ExitCode == 0 && asm.ExitCode == 0, "建立兩件子作品");

            // ① 標記與判斷：deck 標了 sub；asm 掛在任務底下但沒標 ⇒ 主作品
            Check(store.Load("deck").IsSub && string.Join(",", store.Load("deck").tags) == "sub", "建立時帶的 tags=sub 沒進書卡");
            Check(!store.Load("asm").IsSub && store.Load("asm").parent_work == "ship", "有 parent_work 沒標 sub 應是主作品");
            Check(!store.Load("ship").IsSub && store.Load("ship").tags.Count == 0, "沒給 tags 的作品應是空清單");

            // ② update：轉小寫、去重、空字串清空
            var up = Work("p", ("sub", "update"), ("work", "asm"), ("tags", "WIP, wip ,main"));
            Check(up.ExitCode == 0 && string.Join(",", store.Load("asm").tags) == "wip,main", "update tags 應轉小寫去重：" + string.Join(",", store.Load("asm").tags));
            Check(SculptRoom.V(up, "tags") == "wip,main", "回傳值 tags 不對：" + SculptRoom.V(up, "tags"));

            // ③④ 🔴 別人改不了、不合法整份擋下 —— 書卡一個位元組都不動
            string asmBefore = Card("asm");
            var other = Work("q", ("sub", "update"), ("work", "asm"), ("tags", "sub"));
            Check(other.ExitCode != 0 && Card("asm") == asmBefore, "非作者改標籤應被擋、書卡不動");
            var bad = Work("p", ("sub", "update"), ("work", "asm"), ("tags", "ok,has space"));
            Check(bad.ExitCode == 2 && Card("asm") == asmBefore, "不合法標籤應 exit 2、書卡不動（不挑合法的那個寫進去）");
            var badChild = Work("p", ("sub", "create"), ("id", "bad-child"), ("title", "壞"), ("size", "8"), ("parent_work", "ship"), ("tags", "x/y"));
            Check(badChild.ExitCode == 2 && !store.Exists("bad-child"), "建立子作品帶不合法標籤應擋下、不建");
            Check(Work("p", ("sub", "update"), ("work", "asm"), ("tags", "")).ExitCode == 0 && store.Load("asm").tags.Count == 0, "tags= 空字串應清空");

            // ⑤ list 印出標籤 —— 清單寫在 persona 的回傳檔（Lines 只有標題行），讀 agent 會讀的那一份
            Check(Work("p", ("sub", "list")).ExitCode == 0, "work list 失敗");
            string list = File.ReadAllText(SCP_LettersPaths.CmdPayload(new SCP_LettersRoot(room.Letters), "p", "sculpture", "work"));
            Check(list.Contains("deck｜甲板") && list.Contains("tags sub"), "work list 應印出標籤：" + list.Replace("\n", "⏎"));

            // ⑥ 解析規則的邊界
            bool Throws(string t) { try { SCP_SculptWorks.ParseTags(t); return false; } catch (ArgumentException) { return true; } }
            Check(Throws(new string('a', SCP_SculptWorks.MaxTagLength + 1)) && !Throws(new string('a', SCP_SculptWorks.MaxTagLength)), "標籤長度邊界");
            Check(Throws(string.Join(",", Enumerable.Range(0, SCP_SculptWorks.MaxTags + 1).Select(i => "t" + i))) && !Throws(string.Join(",", Enumerable.Range(0, SCP_SculptWorks.MaxTags).Select(i => "t" + i))), "標籤個數邊界");

            // ⑦ 觀測頁吃真的書卡：只列主作品 ⇒ ship、asm（deck 標了 sub）
            string Ids(SculptWorkFilter f) => string.Join(",", SculptureViewerPage.FilterWorks(store.List(), f).Select(w => w.id));
            Check(Ids(new SculptWorkFilter()) == "asm,ship", "觀測頁只列主作品應看標籤：" + Ids(new SculptWorkFilter()));
            Check(Ids(new SculptWorkFilter { MainOnly = false }) == "asm,deck,ship", "關掉只列主作品應三件都在");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + ": " + e.Message); }
        finally { SenateSculptureRewards.Factory = original; }
        return new(name, failures.Count == 0 ? "子作品標記／主作品判斷／update 規則／別人改不了與不合法零寫入／list／觀測頁篩選 全過" : string.Join("；", failures),
                   failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
