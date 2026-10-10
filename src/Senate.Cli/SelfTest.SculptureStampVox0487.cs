// 淨室驗收（TASK-0487）：stampvox 一刀落一塊任意形狀的 3D 格子 —— 作品內免費、展區按實際落地收費；
// 閘門（expect_pixels／空／越界／佔用）、清單錯誤指出第幾行、重播與 Undo/Redo 走貼片分支。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureStampVoxCleanRoom()
    {
        const string name = "雕刻 stampvox：3D 格子清單一刀落地／作品免費、展區按落地收費／閘門與清單錯誤零事件／重播＋Undo/Redo（淨室，TASK-0487）";
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
            Check(room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", "beam"), ("title", "斜梁"), ("size", "16"),
                           ("commission", "雕一根斜放的梁"), ("commission_ref", "user:beam"), ("account", "acct")).ExitCode == 0, "任務作品建立");
            SCP_SculptSpace Space() => store.Engine(store.Load("beam"), room.Data).LoadSpace();
            int Events() => SCP_SculptStore.ListEvents(store.SpacePaths("beam")).Count;
            string List(string file, params string[] lines)
            {
                string path = Path.Combine(room.Root, file);
                File.WriteAllText(path, string.Join("\n", lines) + "\n");
                return path;
            }
            SCP_CmdResult Vox(string list, string at, string expect, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "stampvox"), ("work", "beam"), ("persona", "p"), ("voxels", list), ("at", at) };
                if (expect.Length > 0) a.Add(("expect_pixels", expect));
                a.AddRange(more);
                return room.Run(a.ToArray());
            }

            // ① 一根 45° 的斜梁（對角線、兩種顏色、CRLF＋BOM＋註解）：一刀、格子與顏色逐顆對上、免費
            string diag = Path.Combine(room.Root, "diag.vox.txt");
            File.WriteAllText(diag, "﻿# 斜梁：x=y 對角\r\n0,0,0,141\r\n1,1,0,141\r\n2,2,0,141\r\n\r\n3,3,0,173\r\n3,3,1,173\r\n");
            var one = Vox(diag, "2,2,0", "5");
            var s1 = Space();
            Check(one.ExitCode == 0 && SculptRoom.V(one, "placed") == "5" && SculptRoom.V(one, "charged") == "0" && pay.Consumed == 0, "斜梁一刀落地且免費：" + SculptRoom.All(one));
            Check(s1.Voxels.Count == 5 && s1.Voxels.Get(2, 2, 0) == 141 && s1.Voxels.Get(4, 4, 0) == 141 && s1.Voxels.Get(5, 5, 1) == 173 && s1.Voxels.Get(3, 2, 0) == 0, "格子與顏色逐顆對上（含 at 位移）");
            var ev = SCP_SculptStore.ReadEvent(SculptRoom.V(one, "event_file"));
            Check(ev != null && ev.GetString("op", "") == "stampvox" && ev["source"].GetString("kind", "") == "voxel_file" && ev["source"].GetString("sha256", "").Length == 64
                  && ev["placed_colored"].Count == 5, "事件記 op、清單 sha256 與逐顆 placed_colored");

            // ② 閘門：expect_pixels 對不上（反向對照：清單多一行、沿用舊數）⇒ exit 5 零事件；空清單、越界 ⇒ 零事件；facing／thickness ⇒ exit 2
            int before = Events();
            string grown = List("grown.vox.txt", "0,0,0,141", "1,1,0,141", "2,2,0,141", "3,3,0,173", "3,3,1,173", "4,4,0,173");
            Check(Vox(grown, "2,2,4", "5").ExitCode == 5 && Events() == before, "🔴 清單多一行而 expect_pixels 沿用舊數 ⇒ 擋下、零事件");
            Check(Vox(List("empty.vox.txt", "# 只有註解", ""), "0,0,0", "").ExitCode == 2 && Events() == before, "空清單（作品內最壞體積 0）⇒ exit 2 零事件");
            Check(Vox(diag, "13,0,0", "5").ExitCode == 5 && Events() == before, "越界不帶 allow_clip ⇒ 零事件");
            Check(Vox(diag, "0,0,8", "5", ("facing", "x+")).ExitCode == 2 && Vox(diag, "0,0,8", "5", ("thickness", "3")).ExitCode == 2 && Events() == before, "facing／thickness 給了就擋");

            // ③ 清單本身有錯 ⇒ exit 2、回報第幾行、零事件
            var bad = Vox(List("bad.vox.txt", "0,0,0,141", "1,1,x,141"), "0,0,8", "");
            var dup = Vox(List("dup.vox.txt", "0,0,0,141", "1,0,0,141", "0,0,0,173"), "0,0,8", "");
            var zero = Vox(List("zero.vox.txt", "0,0,0,0"), "0,0,8", "");
            var big = Vox(List("big.vox.txt", "0,0,0,256"), "0,0,8", "");
            Check(bad.ExitCode == 2 && SculptRoom.All(bad).Contains("第 2 行"), "壞行指出第 2 行：" + SculptRoom.All(bad));
            Check(dup.ExitCode == 2 && SculptRoom.All(dup).Contains("第 3 行與第 1 行"), "重複座標指出兩行：" + SculptRoom.All(dup));
            Check(zero.ExitCode == 2 && big.ExitCode == 2 && Events() == before, "顏色 0／256 ⇒ exit 2 零事件");

            // ④ 已佔用的格跳過；allow_clip 只落界內的
            var overlap = Vox(List("overlap.vox.txt", "0,0,0,77", "1,0,0,77", "2,0,0,77"), "1,2,0", "3");
            Check(overlap.ExitCode == 0 && SculptRoom.V(overlap, "placed") == "2" && Space().Voxels.Get(2, 2, 0) == 141 && Space().Voxels.Get(3, 2, 0) == 77, "佔用的格跳過、不改色：" + SculptRoom.All(overlap));
            var clip = Vox(diag, "13,13,10", "5", ("allow_clip", "1"));
            Check(clip.ExitCode == 0 && SculptRoom.V(clip, "placed") == "3" && Space().Voxels.Count == 10, "allow_clip 只落界內 3 顆：" + SculptRoom.All(clip));

            // ⑤ 重播：刪快取重建 ＝ 落子當下；Undo／Redo 對 stampvox 照常
            int count = Space().Voxels.Count;
            File.Delete(store.SpacePaths("beam").CacheFile);
            var rebuilt = Space();
            Check(rebuilt.Voxels.Count == count && rebuilt.Voxels.Get(5, 5, 1) == 173 && rebuilt.Voxels.Get(3, 2, 0) == 77, "刪快取重播一致");
            SCP_CmdResult Edit(string sub) => room.Run(("op", "work"), ("sub", sub), ("work", "beam"), ("persona", "p"));
            Check(Edit("undo").ExitCode == 0 && Space().Voxels.Count == count - 3 && Edit("redo").ExitCode == 0 && Space().Voxels.Count == count, "Undo/Redo 照常");

            // ⑥ 共用展區：按實際落地收費（250 顆 ⇒ ⌈250/100⌉ = 3 張）
            var lines = new List<string>();
            for (int i = 0; i < 250; i++) lines.Add((i % 25) + "," + (i / 25) + ",0,141");
            string sheet = List("sheet.vox.txt", lines.ToArray());
            var paid = room.Run(("op", "stampvox"), ("persona", "p"), ("account", "acct"), ("voxels", sheet), ("at", "10,10,10"), ("expect_pixels", "250"), ("share", "0"));
            Check(paid.ExitCode == 0 && SculptRoom.V(paid, "placed") == "250" && SculptRoom.V(paid, "charged") == "3" && pay.Consumed == 3
                  && room.Engine().LoadSpace().Voxels.Get(34, 19, 10) == 141, "展區按落地收費：" + SculptRoom.All(paid));

            return new CheckRow(name, failures.Count == 0
                ? "斜梁 5 顆一刀免費、逐顆對上；🔴 多一行沿用舊數擋下；空／越界／facing／壞行／重複／顏色 0、256 全零事件；佔用跳過、allow_clip 只落界內；刪快取重播與 Undo/Redo 一致；展區 250 顆收 3 張"
                : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
