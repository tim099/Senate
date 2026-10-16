// 淨室驗收（TASK-0510）：export format=glb —— 主作品、每個關聯節點、每個外部模型各自一個 node，同一份來源共用一份 mesh；
// 實例變換（鏡像、90° 轉、巢狀、模型縮放＋旋轉＋鏡像）正確：glb 的每個 voxel 面經 node 矩陣換回「哪一格、什麼色」，
// 聯集要跟合併版的 export list 逐格相同；模型三角形經矩陣換到組裝件，外框跟 obj 匯出的模型頂點相同。
// 反向對照：藏一條關聯 ⇒ 少一個 node、共用次數少一；models=0 ⇒ 沒有模型 node；hide_links=self ⇒ 沒有主作品 node；
// 點名不存在的路徑、給 region ⇒ exit 2；同輸入匯兩次逐位元組相同。
using Senate.Core;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptGlbCleanRoom()
    {
        const string name = "雕刻匯出 glb（TASK-0510）：每個子模型一個 node＋同件共用 mesh／voxel 面經矩陣換回逐格＝合併版 list／模型外框＝obj／根＝y-up 公尺／🔴 藏一條少一個 node、models=0、hide_links=self、打錯路徑、region、重匯逐位元組";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string why) { if (!c) failures.Add(why); }
        var original = SenateSculptureRewards.Factory;
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            var pay = new SculptProbeGateway { Vouchers = 10 };
            SCP_CanvasGatewayHost.Factory = _ => pay;
            SenateSculptureRewards.Factory = _ => new SculptCommissionProbe();
            SCP_SculptRenderers.Register(null!);
            bool Create(string id, string size) => room.Run(("op", "work"), ("sub", "create"), ("persona", "p"), ("id", id), ("title", "作品" + id), ("size", size),
                ("commission", "glb " + id), ("commission_ref", "user:" + id), ("account", "acct")).ExitCode == 0;
            SCP_CmdResult Box(string id, int x1, int x2, int y1, int y2, int z1, int z2, int color)
                => room.Run(("op", "box"), ("work", id), ("persona", "p"), ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", y1.ToString()), ("y2", y2.ToString()),
                            ("z1", z1.ToString()), ("z2", z2.ToString()), ("color", color.ToString()));
            SCP_CmdResult Link(string iWork, string nm, string src, string at, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", "link"), ("work", iWork), ("persona", "p"), ("name", nm), ("source_work", src), ("at", at) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            SCP_CmdResult Export(string fmt, string out_, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "export"), ("work", "h"), ("persona", "p"), ("format", fmt), ("out", out_) };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            string F(string n) => Path.Combine(room.Root, n).Replace('\\', '/');

            // 場景：主作品 h（一格）；砲 gun（L 形兩色、不對稱）關聯三次（原樣、轉 90°＋鏡像 x、轉 180°）；架子 rk 裡再關聯一門（巢狀）；
            //       同一個不對稱四面體 obj 用兩個名字各放一份（縮放＋旋轉＋鏡像、旋轉＋縮放）。彼此不貼 ⇒ 每一格都有露出面
            Check(Create("h", "32") && Create("gun", "3,2,2") && Create("rk", "6,4,3"), "三件作品建立");
            Check(Box("h", 1, 1, 1, 1, 0, 0, 200).ExitCode == 0, "主作品一格");
            Check(Box("gun", 0, 2, 0, 0, 0, 0, 28).ExitCode == 0 && Box("gun", 0, 0, 1, 1, 0, 1, 224).ExitCode == 0, "砲的格");
            Check(Box("rk", 5, 5, 3, 3, 2, 2, 73).ExitCode == 0 && Link("rk", "g", "gun", "1,1,0", ("turn", "270"), ("mirror", "z")).ExitCode == 0, "架子一格＋裡面一門砲（轉 270°＋鏡像 z）");
            Check(Link("h", "g1", "gun", "12,3,0").ExitCode == 0 && Link("h", "g2", "gun", "12,12,2", ("turn", "90"), ("mirror", "x")).ExitCode == 0
                  && Link("h", "g3", "gun", "3,14,0", ("turn", "180")).ExitCode == 0 && Link("h", "rk", "rk", "20,20,4").ExitCode == 0, "四條關聯（同件三次＋巢狀）");
            string aTet = F("tet.obj");
            File.WriteAllText(aTet, "v 0 0 0\nv 2 0 0\nv 0 1 0\nv 0 0 3\nf 1 3 2\nf 1 2 4\nf 1 4 3\nf 2 3 4\n");
            SCP_CmdResult Model(string nm, params (string, string)[] more)
            {
                var a = new List<(string, string)> { ("op", "work"), ("sub", "model"), ("work", "h"), ("persona", "p"), ("name", nm), ("file", aTet), ("axes", "zup") };
                a.AddRange(more);
                return room.Run(a.ToArray());
            }
            Check(Model("t1", ("at", "4,24,1"), ("scale", "2,1,1.5"), ("rotate", "0,0,30"), ("mirror", "x")).ExitCode == 0
                  && Model("t2", ("at", "26,6,0"), ("rotate", "20,0,-60"), ("scale", "1.5")).ExitCode == 0, "兩個模型（同一個 obj）");

            // ① 匯出、讀回
            var r1 = Export("glb", F("a.glb"));
            Check(r1.ExitCode == 0 && File.Exists(F("a.glb")), "glb 匯出：" + SculptRoom.All(r1));
            if (!File.Exists(F("a.glb"))) throw new InvalidOperationException("沒有 glb 可讀：" + SculptRoom.All(r1));
            var g = Glb.Read(F("a.glb"));
            int aRoot = (int)g.J["scenes"][0]["nodes"][0].AsLong();
            var aKids = g.J["nodes"][aRoot]["children"];
            var aByPath = new Dictionary<string, int>(StringComparer.Ordinal);
            var aUses = new Dictionary<long, int>();
            for (int i = 0; i < aKids.Count; i++)
            {
                var n = g.J["nodes"][(int)aKids[i].AsLong()];
                string p = n["extras"]["sculpt"]["kind"].AsString() == "own" ? "self" : n["extras"]["sculpt"]["path"].AsString().TrimStart('/');
                aByPath[p] = (int)aKids[i].AsLong();
                long m = n["mesh"].AsLong(); aUses[m] = aUses.TryGetValue(m, out int u) ? u + 1 : 1;
            }
            readings.Add($"node {g.J["nodes"].Count}（根底下 {aKids.Count}：{string.Join("、", aByPath.Keys)}）、mesh {g.J["meshes"].Count}、共用次數 {string.Join("/", aUses.Values.OrderByDescending(v => v))}");
            Check(aKids.Count == 8 && g.J["nodes"].Count == 9 && aByPath.ContainsKey("self") && aByPath.ContainsKey("rk/g") && aByPath.ContainsKey("t1") && aByPath.ContainsKey("t2"),
                  "根底下 8 個：主作品＋g1／g2／g3／rk／rk/g＋t1／t2");
            Check(g.J["meshes"].Count == 4 && aUses.Values.OrderByDescending(v => v).SequenceEqual(new[] { 4, 2, 1, 1 }),
                  "mesh 4 份：砲被 4 個 node 共用（g1／g2／g3／rk/g）、四面體 2 個、主作品與架子各 1");
            double s = 1.0 / 32;   // h 沒記 meters_per_voxel ⇒ 預設 1/32 m
            double[] aR = g.Matrix(aRoot);
            Check(Near(aR, new[] { s, 0, 0, 0, 0, 0, s, 0, 0, s, 0, 0, 0, 0, 0, 1 }), "根 ＝ (x, y, z) → (x, z, y) × 1/32 m：" + string.Join(",", aR));

            // ② voxel 面經 node 矩陣換回格子 ＝ 合併版 export list 逐格
            var aGot = new SortedSet<(int, int, int, int)>();
            foreach (var kv in aByPath)
            {
                var n = g.J["nodes"][kv.Value];
                if (kv.Key == "t1" || kv.Key == "t2") continue;
                double[] M = n["matrix"].Exists ? g.Matrix(kv.Value) : new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };
                foreach (var cell in g.VoxelCells((int)n["mesh"].AsLong()))
                {
                    double[] c = Apply4(M, cell.X + 0.5, cell.Y + 0.5, cell.Z + 0.5);
                    aGot.Add(((int)Math.Floor(c[0]), (int)Math.Floor(c[1]), (int)Math.Floor(c[2]), cell.Color));
                }
            }
            var rl = Export("list", F("a.txt"));
            var aWant = new SortedSet<(int, int, int, int)>();
            foreach (string line in File.ReadAllLines(F("a.txt")))
            {
                if (line.StartsWith("#") || line.Trim().Length == 0) continue;
                var q = line.Split(','); aWant.Add((int.Parse(q[0]), int.Parse(q[1]), int.Parse(q[2]), int.Parse(q[3])));
            }
            int aMiss = aWant.Except(aGot).Count(), aExtra = aGot.Except(aWant).Count();
            readings.Add($"voxel 面換回 {aGot.Count} 格 vs 合併 list {aWant.Count} 格：缺 {aMiss}、多 {aExtra}");
            Check(rl.ExitCode == 0 && aWant.Count == 1 + 4 * 5 + 1 && aMiss == 0 && aExtra == 0, "🔴 逐格（位置＋色）跟合併版相同（含鏡像、轉向、巢狀）");

            // ③ 模型：三角形經矩陣到組裝件的外框 ＝ obj 匯出的模型頂點外框（obj 是 (x, z, y)）
            var ro = Export("obj", F("a.obj"));
            var aObj = ObjModelBoxes(File.ReadAllLines(F("a.obj")));
            foreach (string t in new[] { "t1", "t2" })
            {
                var n = g.J["nodes"][aByPath[t]];
                var (lo, hi) = g.MeshBox((int)n["mesh"].AsLong(), g.Matrix(aByPath[t]));
                bool ok = aObj.TryGetValue("/" + t, out var ob) || aObj.TryGetValue(t, out ob);
                Check(ro.ExitCode == 0 && ok && Near(lo, ob.Lo, 1e-3) && Near(hi, ob.Hi, 1e-3),
                      "模型 " + t + " 外框 glb " + string.Join(",", lo.Select(v => v.ToString("0.###"))) + "～" + string.Join(",", hi.Select(v => v.ToString("0.###")))
                      + " vs obj " + (ok ? string.Join(",", ob.Lo.Select(v => v.ToString("0.###"))) + "～" + string.Join(",", ob.Hi.Select(v => v.ToString("0.###"))) : "（obj 裡沒有）"));
            }
            Check(SCP_SculptParts.Det3(new[] { aR[0], aR[4], aR[8], 0, aR[1], aR[5], aR[9], 0, aR[2], aR[6], aR[10], 0 }) < 0, "根的行列式 < 0（繞序交給 glTF 翻）");

            // ④ 反向對照
            var r2 = Export("glb", F("b.glb"), ("hide_links", "g2"));
            var g2 = Glb.Read(F("b.glb"));
            Check(r2.ExitCode == 0 && g2.J["nodes"].Count == 8 && SculptRoom.V(r2, "glb_instances") == "6", "🔴 hide_links=g2 ⇒ 少一個 node（8）、實例 6：" + SculptRoom.V(r2, "glb_instances"));
            var r3 = Export("glb", F("c.glb"), ("models", "0"));
            Check(r3.ExitCode == 0 && SculptRoom.V(r3, "glb_instances") == "5" && SculptRoom.V(r3, "glb_meshes") == "3", "🔴 models=0 ⇒ 沒有模型 node、mesh 3：" + SculptRoom.V(r3, "glb_instances") + "／" + SculptRoom.V(r3, "glb_meshes"));
            var r4 = Export("glb", F("d.glb"), ("hide_links", "self"));
            Check(r4.ExitCode == 0 && SculptRoom.V(r4, "glb_nodes") == "8", "🔴 hide_links=self ⇒ 沒有主作品 node：" + SculptRoom.V(r4, "glb_nodes"));
            Check(Export("glb", F("e.glb"), ("hide_links", "nope")).ExitCode == 2 && !File.Exists(F("e.glb")), "🔴 點名不存在的關聯 ⇒ exit 2、不寫檔");
            Check(Export("glb", F("f.glb"), ("region", "0..5,0..5,0..5")).ExitCode == 2 && !File.Exists(F("f.glb")), "🔴 給 region ⇒ exit 2、不寫檔");
            var r5 = Export("glb", F("a2.glb"));
            Check(r5.ExitCode == 0 && File.ReadAllBytes(F("a.glb")).SequenceEqual(File.ReadAllBytes(F("a2.glb"))), "同輸入匯兩次逐位元組相同");

            return new CheckRow(name, failures.Count == 0 ? string.Join("；", readings) : string.Join("；", failures) + "｜" + string.Join("；", readings),
                failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally { SenateSculptureRewards.Factory = original; }
    }

    static bool Near(double[] a, double[] b, double eps = 1e-9) => a.Length == b.Length && a.Zip(b).All(p => Math.Abs(p.First - p.Second) <= eps);

    /// <summary>glTF 的 4×4（行優先）套一個點。</summary>
    static double[] Apply4(double[] m, double x, double y, double z)
        => new[] { m[0] * x + m[4] * y + m[8] * z + m[12], m[1] * x + m[5] * y + m[9] * z + m[13], m[2] * x + m[6] * y + m[10] * z + m[14] };

    /// <summary>obj 匯出裡每個 `o model_n # 路徑` 的頂點外框（換回雕刻座標：obj 寫的是 (x, z, y)）。</summary>
    static Dictionary<string, (double[] Lo, double[] Hi)> ObjModelBoxes(string[] iLines)
    {
        var aOut = new Dictionary<string, (double[], double[])>(StringComparer.Ordinal);
        string? aCur = null; double[] lo = null!, hi = null!;
        foreach (string l in iLines)
        {
            if (l.StartsWith("o "))
            {
                if (aCur != null) aOut[aCur] = (lo, hi);
                aCur = l.StartsWith("o model_") && l.Contains(" # ") ? l.Substring(l.IndexOf(" # ", StringComparison.Ordinal) + 3).Trim() : null;
                lo = new[] { double.MaxValue, double.MaxValue, double.MaxValue }; hi = new[] { double.MinValue, double.MinValue, double.MinValue };
            }
            else if (aCur != null && l.StartsWith("v "))
            {
                var q = l.Substring(2).Split(' ').Select(t => double.Parse(t, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                double[] p = { q[0], q[2], q[1] };
                for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], p[k]); hi[k] = Math.Max(hi[k], p[k]); }
            }
        }
        if (aCur != null) aOut[aCur] = (lo, hi);
        return aOut;
    }

    /// <summary>最小的 glb 讀回（只給淨室用；不信寫入端的任何程式碼）。</summary>
    sealed class Glb
    {
        public SCP_JsonData J = SCP_JsonData.NewObject();
        public byte[] Bin = Array.Empty<byte>();

        public static Glb Read(string iPath)
        {
            byte[] b = File.ReadAllBytes(iPath);
            if (BitConverter.ToUInt32(b, 0) != 0x46546C67 || BitConverter.ToUInt32(b, 4) != 2 || BitConverter.ToUInt32(b, 8) != b.Length) throw new InvalidDataException("glb 檔頭不對");
            int jl = (int)BitConverter.ToUInt32(b, 12);
            if (BitConverter.ToUInt32(b, 16) != 0x4E4F534A) throw new InvalidDataException("第一塊不是 JSON");
            var g = new Glb { J = SCP_JsonParser.Parse(System.Text.Encoding.UTF8.GetString(b, 20, jl)) };
            int o = 20 + jl, bl = (int)BitConverter.ToUInt32(b, o);
            if (BitConverter.ToUInt32(b, o + 4) != 0x004E4942) throw new InvalidDataException("第二塊不是 BIN");
            g.Bin = b.Skip(o + 8).Take(bl).ToArray();
            return g;
        }

        public double[] Matrix(int iNode) { var m = J["nodes"][iNode]["matrix"]; var a = new double[16]; for (int i = 0; i < 16; i++) a[i] = m[i].AsDouble(); return a; }

        float[] Floats(int iAcc)
        {
            var a = J["accessors"][iAcc]; var v = J["bufferViews"][(int)a["bufferView"].AsLong()];
            int n = (int)a["count"].AsLong() * (a["type"].AsString() == "VEC4" ? 4 : 3), off = (int)v["byteOffset"].AsLong();
            var f = new float[n]; for (int i = 0; i < n; i++) f[i] = BitConverter.ToSingle(Bin, off + i * 4); return f;
        }

        uint[] Ints(int iAcc)
        {
            var a = J["accessors"][iAcc]; var v = J["bufferViews"][(int)a["bufferView"].AsLong()];
            int n = (int)a["count"].AsLong(), off = (int)v["byteOffset"].AsLong();
            var u = new uint[n]; for (int i = 0; i < n; i++) u[i] = BitConverter.ToUInt32(Bin, off + i * 4); return u;
        }

        /// <summary>voxel mesh 的每個露出面（矩形）⇒ 它蓋住的每一格（面心往法線反方向半格）＋材質名裡的色。</summary>
        public List<(int X, int Y, int Z, int Color)> VoxelCells(int iMesh)
        {
            var aOut = new List<(int, int, int, int)>();
            var prims = J["meshes"][iMesh]["primitives"];
            for (int p = 0; p < prims.Count; p++)
            {
                string mat = J["materials"][(int)prims[p]["material"].AsLong()]["name"].AsString();
                int color = int.Parse(mat.Substring(1));
                float[] pos = Floats((int)prims[p]["attributes"]["POSITION"].AsLong()), nrm = Floats((int)prims[p]["attributes"]["NORMAL"].AsLong());
                for (int q = 0; q < pos.Length / 12; q++)
                {
                    int b = q * 12;
                    double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
                    for (int k = 0; k < 4; k++) for (int a = 0; a < 3; a++) { lo[a] = Math.Min(lo[a], pos[b + k * 3 + a]); hi[a] = Math.Max(hi[a], pos[b + k * 3 + a]); }
                    int ax = Math.Abs(nrm[b]) > 0.5 ? 0 : Math.Abs(nrm[b + 1]) > 0.5 ? 1 : 2;
                    double sign = nrm[b + ax];
                    int u = ax == 0 ? 1 : 0, v = ax == 2 ? 1 : 2;
                    for (int i = (int)lo[u]; i < (int)hi[u]; i++)
                        for (int j = (int)lo[v]; j < (int)hi[v]; j++)
                        {
                            var c = new double[3]; c[ax] = lo[ax] - (sign > 0 ? 1 : 0); c[u] = i; c[v] = j;
                            aOut.Add(((int)c[0], (int)c[1], (int)c[2], color));
                        }
                }
            }
            return aOut;
        }

        public (double[] Lo, double[] Hi) MeshBox(int iMesh, double[] iM)
        {
            double[] lo = { double.MaxValue, double.MaxValue, double.MaxValue }, hi = { double.MinValue, double.MinValue, double.MinValue };
            var prims = J["meshes"][iMesh]["primitives"];
            for (int p = 0; p < prims.Count; p++)
            {
                float[] pos = Floats((int)prims[p]["attributes"]["POSITION"].AsLong());
                for (int i = 0; i < pos.Length; i += 3)
                {
                    double[] w = Apply4(iM, pos[i], pos[i + 1], pos[i + 2]);
                    for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], w[k]); hi[k] = Math.Max(hi[k], w[k]); }
                }
            }
            return (lo, hi);
        }
    }
}
