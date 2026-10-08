// 區塊職責：obj 匯出 merge=greedy／none 正反向驗收（淨室）。
// 物理意義：面數只是第一格；合併後每個顏色、每個朝向的總面積要跟逐面版一樣，
//   每個面的頂點序要跟它的 vn 同向 —— 不然「面變少了」可能只是「面掉了」或「翻面了」。
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptExportMergeCleanRoom()
    {
        const string name = "obj 匯出合併：同色立方體 6 面 8 頂點／異色不跨色合併／面積與朝向和逐面版一致／none 照舊／壞值零寫入（淨室）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool condition, string what) { if (!condition) failures.Add(what); }
        try
        {
            using var room = new SculptRoom();
            Directory.CreateDirectory(Path.Combine(room.Letters, "p", "profile"));
            SCP_CanvasGatewayHost.Factory = _ => new SculptProbeGateway { Vouchers = 40 };
            SCP_CmdResult Run(params (string, string)[] a) => room.Run(a);
            Check(Run(("op", "work"), ("sub", "create"), ("id", "cube"), ("title", "方塊"), ("persona", "p"), ("account", "acct")).ExitCode == 0, "建立作品");
            SCP_CmdResult Box(int x1, int x2, int color) => Run(("op", "box"), ("persona", "p"), ("work", "cube"),
                ("x1", x1.ToString()), ("x2", x2.ToString()), ("y1", "0"), ("y2", "3"), ("z1", "0"), ("z2", "3"), ("color", color.ToString()));
            Check(Box(0, 3, 19).ExitCode == 0, "放 4³ 紅色方塊");

            (int Exit, ObjStats S) Export(string merge, string tag)
            {
                string aOut = Path.Combine(room.Root, tag + ".obj");
                var r = merge.Length > 0
                    ? Run(("op", "export"), ("work", "cube"), ("format", "obj"), ("out", aOut), ("merge", merge))
                    : Run(("op", "export"), ("work", "cube"), ("format", "obj"), ("out", aOut));
                return (r.ExitCode, File.Exists(aOut) ? ReadObj(aOut) : new ObjStats());
            }

            var g1 = Export("", "cube_default");
            var n1 = Export("none", "cube_none");
            readings.Add($"單色 4³：預設 {g1.S.Faces} 面／{g1.S.Verts} 頂點，none {n1.S.Faces} 面／{n1.S.Verts} 頂點");
            Check(g1.Exit == 0 && g1.S.Faces == 6 && g1.S.Verts == 8, "預設＝greedy：整顆同色立方體 6 面 8 頂點");
            Check(n1.Exit == 0 && n1.S.Faces == 96 && n1.S.Verts == 384, "none：逐 voxel 面 96 面、384 頂點（不共用）");
            Check(g1.S.SameSurface(n1.S), "greedy 與 none 每色每朝向總面積相同");
            Check(g1.S.Flipped == 0 && n1.S.Flipped == 0, "每個面的頂點序與 vn 同向");

            Check(Box(4, 7, 200).ExitCode == 0, "旁邊接一塊異色 4³");
            var g2 = Export("greedy", "two_greedy");
            var n2 = Export("none", "two_none");
            readings.Add($"雙色：greedy {g2.S.Faces} 面，none {n2.S.Faces} 面");
            // 每塊各 5 面露出（接觸面被遮）＋ 兩塊的上下前後不跨色合併 ⇒ 各 5 面
            Check(g2.Exit == 0 && g2.S.Faces == 10, "異色相鄰：接觸面不出、不跨色合併 ⇒ 10 面");
            Check(g2.S.SameSurface(n2.S) && g2.S.Flipped == 0, "雙色：面積與朝向一致");

            string aBad = Path.Combine(room.Root, "bad.obj");
            var bad = Run(("op", "export"), ("work", "cube"), ("format", "obj"), ("out", aBad), ("merge", "fancy"));
            Check(bad.ExitCode == 2 && !File.Exists(aBad), "merge 壞值 exit 2 且不寫檔");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0
            ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + (aRead.Length > 0 ? "　讀數：" + aRead : ""), CheckResult.Fail);
    }

    sealed class ObjStats
    {
        public int Faces, Verts, Flipped;
        /// <summary>(材質, 法線) → 總面積（以兩倍面積的整數累加）。</summary>
        public readonly SortedDictionary<string, long> Area = new(StringComparer.Ordinal);
        public bool SameSurface(ObjStats o) =>
            Area.Count > 0 && Area.Count == o.Area.Count && Area.All(kv => o.Area.TryGetValue(kv.Key, out long v) && v == kv.Value);
    }

    static ObjStats ReadObj(string iPath)
    {
        var s = new ObjStats();
        var v = new List<long[]>();
        var n = new List<long[]>();
        string mat = "";
        foreach (string aLine in File.ReadAllLines(iPath))
        {
            string[] t = aLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;
            if (t[0] == "v") v.Add(new[] { long.Parse(t[1]), long.Parse(t[2]), long.Parse(t[3]) });
            else if (t[0] == "vn") n.Add(new[] { long.Parse(t[1]), long.Parse(t[2]), long.Parse(t[3]) });
            else if (t[0] == "usemtl") mat = t[1];
            else if (t[0] == "f")
            {
                var p = new long[4][];
                long[] nn = n[int.Parse(t[1].Split("//")[1]) - 1];
                for (int k = 0; k < 4; k++) p[k] = v[int.Parse(t[k + 1].Split("//")[0]) - 1];
                // 四邊形 (p0,p1,p2) 與 (p0,p2,p3) 的叉積和 ＝ 兩倍面積向量
                long[] c = new long[3];
                foreach (var (a, b) in new[] { (1, 2), (2, 3) })
                {
                    long ux = p[a][0] - p[0][0], uy = p[a][1] - p[0][1], uz = p[a][2] - p[0][2];
                    long vx = p[b][0] - p[0][0], vy = p[b][1] - p[0][1], vz = p[b][2] - p[0][2];
                    c[0] += uy * vz - uz * vy; c[1] += uz * vx - ux * vz; c[2] += ux * vy - uy * vx;
                }
                long dot = c[0] * nn[0] + c[1] * nn[1] + c[2] * nn[2];
                if (dot <= 0) s.Flipped++;
                string key = mat + "|" + nn[0] + "," + nn[1] + "," + nn[2];
                s.Area[key] = (s.Area.TryGetValue(key, out long a0) ? a0 : 0) + Math.Abs(dot);
                s.Faces++;
            }
        }
        s.Verts = v.Count;
        return s;
    }
}
