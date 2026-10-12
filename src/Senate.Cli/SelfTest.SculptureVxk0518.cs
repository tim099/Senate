// 淨室驗收（TASK-0518）：`export format=vxk` —— VoxelKit 封裝由 senate 直接產 ——
// ① 格子：封裝解回來 ＝ export list 的格子（逐格逐色）；跨區塊邊界（15／16）的格落在對的區塊。
// ② checksum：另一種寫法手算（ulong、逐項 & 0xFFFFFFFF）＝ header ＝ 解回來的格子算的。
// ③ 區塊：nchunks ＝ 不同 (x/16,y/16,z/16) 的數量、區塊座標照 (cx,cy,cz) 字典序；raw 是 'VXK1'＋uint32 LE 長度＋header。
// ④ header：欄位與順序照 voxel_export.py（format…exported_at，再 parts／rig／rig_skipped／points，最後 nchunks）；meters_per_voxel 沒記 ⇒ 0.1；source 有關聯版本。
// ⑤ include=rig,models,parts,meta：繩中線、兩個一樣的模型只存一份網格、關聯外框 ＝ 手算、meta 與固定點接上。
// ⑥ 🔴 不帶 include ⇒ 不多任何一欄；include 打錯／models 沒配 rig／給 region ⇒ exit 2、沒寫檔。
// ⑦ 🔴 反向對照：改一格色 ⇒ SameVoxels 判不同、checksum 也不同（對拍不會永遠綠）。
using System.IO.Compression;
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureVxkCleanRoom()
    {
        const string name = "雕刻 .vxk 封裝：格子＝list 匯出、跨區塊邊界／checksum 另一種寫法手算／區塊數與字典序、raw 版面／header 欄位與順序照 voxel_export.py／繩、模型去重、關聯外框＝手算、meta 與固定點／🔴 不帶 include 不多欄、壞參數零寫入／🔴 改一格就判不同（淨室，TASK-0518）";
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
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "vxk " + id), ("commission_ref", "user:vxk:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Sub(string id, string sub, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "work"), ("sub", sub), ("work", id), ("persona", "p") };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }
            bool Dot(string id, int x, int y, int z, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x.ToString()), ("x2", x.ToString()), ("y1", y.ToString()), ("y2", y.ToString()), ("z1", z.ToString()), ("z2", z.ToString()), ("color", color.ToString())).ExitCode == 0;
            SCP_CmdResult Vxk(string outFile, params (string, string)[] more)
            {
                var l = new List<(string, string)> { ("op", "export"), ("format", "vxk"), ("work", "host"), ("out", outFile) };
                l.AddRange(more);
                return room.Run(l.ToArray());
            }

            // host 40³（meters_per_voxel 沒記 ⇒ header 0.1）：格子跨區塊邊界 15／16、遠角 39；固定點 r
            Check(Create("host", "40,40,40") && Dot("host", 0, 0, 0, 60) && Dot("host", 15, 0, 0, 61) && Dot("host", 16, 0, 0, 62) && Dot("host", 17, 33, 20, 63) && Dot("host", 39, 39, 39, 64)
                  && Sub("host", "anchor", ("name", "r"), ("at", "1,1,1")).ExitCode == 0, "host");
            // child 8×6×4：一格＋固定點 a；關聯 kid（mirror=y）
            Check(Create("child", "8,6,4") && Dot("child", 0, 0, 0, 100) && Sub("child", "anchor", ("name", "a"), ("at", "1,2,3")).ExitCode == 0
                  && Sub("host", "link", ("name", "kid"), ("source_work", "child"), ("at", "20,5,2"), ("mirror", "y"), ("meta", "{\"kind\":\"magazine\",\"powder_lb\":200}")).ExitCode == 0, "child＋關聯 kid");
            // part（0.02 m）一格＋固定點 tip；兩個一樣的模型（同轉向同縮放 ⇒ 去重後一份網格）
            Check(Create("part", "10,10,10") && Dot("part", 0, 0, 0, 224) && Sub("part", "update", ("meters_per_voxel", "0.02")).ExitCode == 0
                  && Sub("part", "anchor", ("name", "tip"), ("at", "9,0,0")).ExitCode == 0, "part");
            Check(Sub("host", "model", ("name", "g1"), ("source_work", "part"), ("at", "5,30,5"), ("scale", "1"), ("meta", "{\"kind\":\"gun\"}")).ExitCode == 0
                  && Sub("host", "model", ("name", "g2"), ("source_work", "part"), ("at", "25,30,5"), ("scale", "1")).ExitCode == 0, "兩個模型");
            Check(Sub("host", "rope", ("name", "line"), ("from", "r"), ("to", "kid/a"), ("slack", "1.1")).ExitCode == 0, "繩 r → kid/a");

            // 對照組：export list（組好的格子）
            string list = Out("host.txt");
            Check(room.Run(("op", "export"), ("format", "list"), ("work", "host"), ("out", list)).ExitCode == 0, "export list");
            var want = new List<(int X, int Y, int Z, int Color)>();
            foreach (string line in File.ReadAllLines(list))
            {
                if (line.StartsWith("#") || line.Length == 0) continue;
                string[] f = line.Split(',');
                want.Add((int.Parse(f[0]), int.Parse(f[1]), int.Parse(f[2]), int.Parse(f[3])));
            }

            // ── 全部帶 ────────────────────────────────────────────────────
            string full = Out("full.vxk");
            var ex = Vxk(full, ("include", "rig,models,parts,meta"));
            Check(ex.ExitCode == 0 && File.Exists(full), "export vxk include=rig,models,parts,meta：" + SculptRoom.All(ex));
            byte[] blob = File.ReadAllBytes(full);
            var (h, got) = SCP_SculptVxk.Unpack(blob);
            // ① 格子
            Check(SCP_SculptVxk.SameVoxels(want, got) && got.Count == 6, "① 封裝解回來 ＝ list 匯出（6 格：host 5＋kid 1）：" + got.Count);
            Check(got.Contains((15, 0, 0, 61)) && got.Contains((16, 0, 0, 62)) && got.Contains((39, 39, 39, 64)), "① 跨區塊邊界 15／16、遠角 39 的格都在、色對");
            // ② checksum（另一種寫法：ulong、逐項 & 0xFFFFFFFF、最後才取模）
            ulong sum = 0;
            foreach (var v in want) sum += (((ulong)v.X * 73856093UL) & 0xFFFFFFFFUL) ^ (((ulong)v.Y * 19349663UL) & 0xFFFFFFFFUL) ^ (((ulong)v.Z * 83492791UL) & 0xFFFFFFFFUL) ^ (((ulong)v.Color * 2654435761UL) & 0xFFFFFFFFUL);
            uint hand = (uint)(sum & 0xFFFFFFFFUL);
            Check(h["checksum"].AsLong() == hand && SCP_SculptVxk.Checksum(got) == hand, "② checksum 手算 " + hand + " ＝ header " + h["checksum"].AsLong() + " ＝ 解回來的");
            // ③ 區塊：數量、字典序、raw 版面
            var chunks = new SortedSet<(int, int, int)>(want.Select(v => (v.X / 16, v.Y / 16, v.Z / 16)));
            byte[] raw;
            using (var zin = new DeflateStream(new MemoryStream(blob), CompressionMode.Decompress)) using (var ms = new MemoryStream()) { zin.CopyTo(ms); raw = ms.ToArray(); }
            int hl = raw[4] | (raw[5] << 8) | (raw[6] << 16) | (raw[7] << 24);
            var keys = new List<(int, int, int)>();
            for (int i = 0, p = 8 + hl; i < chunks.Count; i++, p += 6) keys.Add((raw[p] | (raw[p + 1] << 8), raw[p + 2] | (raw[p + 3] << 8), raw[p + 4] | (raw[p + 5] << 8)));
            Check(raw[0] == 'V' && raw[1] == 'X' && raw[2] == 'K' && raw[3] == '1' && raw.Length == 8 + hl + chunks.Count * (6 + 4096), "③ raw ＝ 'VXK1'＋uint32 LE 長度＋header＋區塊座標＋區塊內容（長度對得上）");
            Check(h["nchunks"].AsLong() == chunks.Count && keys.SequenceEqual(chunks), "③ nchunks " + h["nchunks"].AsLong() + " ＝ " + chunks.Count + "、區塊照 (cx,cy,cz) 字典序：" + string.Join(" ", keys));
            // ④ header 欄位與順序
            string order = string.Join(",", h.Keys);
            Check(order == "format,version,work,title,owner,meters_per_voxel,size,bbox,chunk,voxels,checksum,palette,colors,source,exported_at,parts,rig,rig_skipped,points,nchunks", "④ header 欄位順序照 voxel_export.py：" + order);
            Check(h["meters_per_voxel"].AsDouble() == 0.1 && h["format"].AsString() == "vxk" && h["version"].AsLong() == 1 && h["bbox"].ToJson(false) == "[0,0,0,39,39,39]" && h["colors"]["60"].AsLong() == 1,
                  "④ meters_per_voxel 沒記 ⇒ 0.1、bbox、colors：" + h["bbox"].ToJson(false));
            Check(h["source"].Count == 1 && h["source"][0].AsString().StartsWith("link kid child@"), "④ source 記關聯的版本：" + h["source"].ToJson(false));
            // ⑤ rig／parts／meta
            SCP_JsonData rig = h["rig"];
            Check(rig["ropes"].Count == 1 && rig["ropes"][0]["pts"].Count % 3 == 0 && rig["ropes"][0]["pts"].Count >= 6, "⑤ 繩 1 條、中線是 x,y,z 攤平");
            Check(rig["models"].Count == 2 && rig["meshes"].Count == 1 && rig["models"][0]["mesh"].AsLong() == 0 && rig["models"][1]["mesh"].AsLong() == 0, "⑤ 兩個一樣的模型 ⇒ 網格只存一份：models " + rig["models"].Count + "、meshes " + rig["meshes"].Count);
            Check(h["parts"].Count == 1 && h["parts"][0]["box"].ToJson(false) == "[20,5,2,27,10,5]" && h["parts"][0]["mirror"].AsString() == "y", "⑤ 關聯外框 ＝ 手算 [20,5,2,27,10,5]：" + h["parts"][0]["box"].ToJson(false));
            // a 格心 (1.5,2.5,3.5)，mirror y 在 6 格的框裡 ⇒ y 3.5，＋at (20,5,2) ⇒ (21.5,8.5,5.5)
            SCP_JsonData pa = h["parts"][0]["points"]["a"];
            Check(h["parts"][0]["meta"]["powder_lb"].AsLong() == 200 && Math.Abs(pa[0].AsDouble() - 21.5) < 1e-9 && Math.Abs(pa[1].AsDouble() - 8.5) < 1e-9 && Math.Abs(pa[2].AsDouble() - 5.5) < 1e-9,
                  "⑤ 關聯 meta 與固定點 a ＝ (21.5,8.5,5.5)：" + pa.ToJson(false));
            Check(rig["models"][0]["meta"]["kind"].AsString() == "gun" && rig["models"][0]["points"]["tip"].Count == 3 && h["points"]["r"].ToJson(false) == "[1.5,1.5,1.5]", "⑤ 模型 meta／固定點、根作品固定點 r");
            readings.Add(got.Count + " 格、" + chunks.Count + " 區塊、checksum " + hand.ToString("x8") + "、繩中線 " + rig["ropes"][0]["pts"].Count / 3 + " 點、網格 " + rig["meshes"].Count + " 份");

            // ── ⑥ 不帶 include：不多任何一欄 ────────────────────────────────
            string bare = Out("bare.vxk");
            Check(Vxk(bare).ExitCode == 0, "不帶 include 也匯得出來");
            var (hb, gb) = SCP_SculptVxk.Unpack(File.ReadAllBytes(bare));
            Check(string.Join(",", hb.Keys) == "format,version,work,title,owner,meters_per_voxel,size,bbox,chunk,voxels,checksum,palette,colors,source,exported_at,nchunks" && SCP_SculptVxk.SameVoxels(gb, got),
                  "⑥ 🔴 不帶 include ⇒ 沒有 parts／rig／points：" + string.Join(",", hb.Keys));
            foreach (var (bad, args) in new[] { ("include 打錯", new[] { ("include", "rig,foo") }), ("models 沒配 rig", new[] { ("include", "models") }), ("給 region", new[] { ("region", "0..5,0..5,0..5") }) })
            {
                string f = Out("bad_" + bad.Length + ".vxk");
                var r = Vxk(f, args);
                Check(r.ExitCode == 2 && !File.Exists(f), "⑥ 🔴 " + bad + " ⇒ exit 2、沒寫檔（exit " + r.ExitCode + "）");
            }

            // ── ⑦ 🔴 反向對照：改一格色 ⇒ 判不同 ──────────────────────────────
            var tampered = new List<(int X, int Y, int Z, int Color)>(got);
            tampered[0] = (tampered[0].X, tampered[0].Y, tampered[0].Z, tampered[0].Color == 1 ? 2 : 1);
            Check(!SCP_SculptVxk.SameVoxels(want, tampered) && SCP_SculptVxk.Checksum(tampered) != hand, "⑦ 🔴 改一格色 ⇒ SameVoxels 判不同、checksum 也不同");
            string? thrown = null;
            try { SCP_SculptVxk.Pack(new List<(int, int, int, int)> { (0, 0, 0, 0) }, SCP_JsonData.NewObject()); } catch (InvalidDataException e) { thrown = e.Message; }
            Check(thrown != null, "⑦ 🔴 色值 0 ⇒ 封裝擋下（.vxk 用 0 當空）");

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }
}
