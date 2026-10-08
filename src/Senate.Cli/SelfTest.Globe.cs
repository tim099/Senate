// 區塊職責：可繪製球面（TASK-0467）的淨室驗收 —— 幾何換算、meta 基底、均勻度、跨面連續、繪製／Undo／重播、CLI 反向。
// 物理意義：每一格都拿「跟工具無關的量」對：格子中心換回自己、相鄰取樣點的角距離、逐格快照比對 ——
//   不以「回 success」當憑據。
using SCP.Core.Cmd;
using SCP.Core.Globe;
using SCP.Core.Json;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow GlobeGridCleanRoom()
    {
        const string name = "球面幾何：格心↔格子往返／meta 基底反向驗證／已知經緯度落面／面積比 ≤1.45／跨面鄰格連續／大圓線不斷（淨室，TASK-0467）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        try
        {
            string aRoot = Path.Combine(Path.GetTempPath(), "senate_globe_grid_" + Guid.NewGuid().ToString("N"));
            try
            {
                var store = new SCP_GlobeStore(new SCP_GlobePaths(aRoot));
                store.WriteMeta(new SCP_GlobeMeta { N = 64 });
                SCP_GlobeMeta meta = store.LoadMeta();
                var g = new SCP_GlobeGrid(meta.N, meta.Faces);

                int aBad = 0;
                for (int k = 0; k < g.CellCount; k++)
                {
                    g.CellCenter(k, out double x, out double y, out double z);
                    if (g.DirToCell(x, y, z) != k) aBad++;
                }
                readings.Add($"N=64 全部 {g.CellCount} 格往返失敗 {aBad}");
                Check(aBad == 0, "格心換回自己");

                var big = new SCP_GlobeGrid(2048, meta.Faces);
                var rnd = new Random(467);
                int aBadBig = 0;
                for (int t = 0; t < 20000; t++)
                {
                    int k = rnd.Next(big.CellCount);
                    big.CellCenter(k, out double x, out double y, out double z);
                    if (big.DirToCell(x, y, z) != k) aBadBig++;
                }
                Check(aBadBig == 0, "N=2048 隨機 2 萬格往返");

                // 已知經緯度落在哪一面 —— 面名從 meta 讀
                string FaceAt(double la, double lo) => meta.Faces[g.LatLonToCell(la, lo) / (g.N * g.N)].Name;
                string aFaces = $"{FaceAt(0, 0)} {FaceAt(0, 90)} {FaceAt(89, 0)} {FaceAt(0, 180)} {FaceAt(0, -90)} {FaceAt(-89, 0)}";
                readings.Add("(0,0)(0,90)(89,0)(0,180)(0,-90)(-89,0) → " + aFaces);
                Check(aFaces == "+X +Y +Z -X -Y -Z", "已知經緯度落面");

                // meta 基底反向：把 +X 的 U 翻號寫回 ⇒ 讀回來建 grid 要喊（證明基底真的從 meta 來）
                string aGood = File.ReadAllText(store.Paths.Meta);
                var tamper = store.LoadMeta();
                tamper.Faces[0].U = new List<int> { 0, -1, 0 };
                File.WriteAllText(store.Paths.Meta, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(tamper), true));   // 繞過 WriteMeta 的驗證，模擬手改
                SCP_GlobeMeta bad = store.LoadMeta();
                bool aThrew = false;
                try { _ = new SCP_GlobeGrid(bad.N, bad.Faces); } catch (ArgumentException) { aThrew = true; }
                Check(bad.Faces[0].U.SequenceEqual(new[] { 0, -1, 0 }) && aThrew, "竄改 meta 基底 ⇒ 建格就喊");
                File.WriteAllText(store.Paths.Meta, aGood);

                // 面積比（四角方向圍出的球面四邊形，拆兩個球面三角形）
                double mn = double.MaxValue, mx = 0;
                for (int k = 0; k < g.N * g.N; k++)
                {
                    g.Unpack(k, out int f, out int i, out int j);
                    double[][] c = new double[4][];
                    int[,] off = { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } };
                    for (int q = 0; q < 4; q++)
                    {
                        g.FaceParamToDir(f, -1 + 2.0 * (i + off[q, 0]) / g.N, -1 + 2.0 * (j + off[q, 1]) / g.N, out double x, out double y, out double z);
                        c[q] = new[] { x, y, z };
                    }
                    double a = Tri(c[0], c[1], c[2]) + Tri(c[0], c[2], c[3]);
                    mn = Math.Min(mn, a); mx = Math.Max(mx, a);
                }
                readings.Add($"面積比 {mx / mn:0.000}");
                Check(mx / mn <= 1.45, "每格面積最大／最小 ≤ 1.45");

                // 跨面鄰格：每個邊上的格子往外一格，角距離 ≤ 1.6 格
                double aWorst = 0;
                for (int k = 0; k < g.CellCount; k++)
                {
                    g.Unpack(k, out _, out int i, out int j);
                    if (i != 0 && j != 0 && i != g.N - 1 && j != g.N - 1) continue;
                    foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                        aWorst = Math.Max(aWorst, Ang(g, k, g.Neighbor(k, di, dj)) / g.CellAngle);
                }
                readings.Add($"跨面鄰格最遠 {aWorst:0.00} 格");
                Check(aWorst <= 1.6, "跨面鄰格連續");

                // 大圓線：橫跨多面的長線，相鄰兩格角距離 ≤ 1.6 格
                var line = SCP_GlobeDraw.Line(g, new[] { new SCP_GlobeLatLon(25, 121), new SCP_GlobeLatLon(60, -100), new SCP_GlobeLatLon(-70, 10) }, 0);
                double aGap = 0;
                for (int k = 1; k < line.Count; k++) aGap = Math.Max(aGap, Ang(g, line[k - 1], line[k]) / g.CellAngle);
                var aLineFaces = new HashSet<int>(line.Select(c => c / (g.N * g.N)));
                readings.Add($"長線 {line.Count} 格跨 {aLineFaces.Count} 面、最大間隔 {aGap:0.00} 格");
                Check(aGap <= 1.6 && aLineFaces.Count >= 3, "大圓線不斷");
            }
            finally { try { Directory.Delete(aRoot, true); } catch { } }
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow GlobePaintCleanRoom()
    {
        const string name = "球面繪製：底色不動格子／點線多邊形油漆桶／Undo 逐格回到前一刻／快取刪掉重播一致／CLI 壞參數零寫入（淨室，TASK-0467）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aData = Path.Combine(Path.GetTempPath(), "senate_globe_paint_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aData);
            SCP_CmdResult Run(params (string K, string V)[] a)
            {
                var d = new Dictionary<string, string> { ["data_root"] = aData };
                foreach (var (k, v) in a) d[k] = v;
                return SCP_CmdRegistry.Dispatch("globe", d);
            }
            string Val(SCP_CmdResult r, string k) => r.Values.FirstOrDefault(kv => kv.Key == k).Value ?? "";

            Check(Run(("op", "status")).ExitCode == 1, "沒 init 時 status 說還沒建立");
            Check(Run(("op", "init"), ("n", "64")).ExitCode == 0, "init");
            Check(Run(("op", "init"), ("n", "64")).ExitCode == 1, "init 不覆寫");
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aData, SCP_GlobePaths.DirName)));
            SCP_GlobeCells Snap() => store.Load().Cells.Clone();
            int Events() => store.Load().LastSeq;

            SCP_GlobeCells s0 = Snap();
            Check(s0.PaintedCount() == 0 && !s0.TileKeys.Any(), "初始全部沒畫過、一個分塊都沒配置（海水是底色，不是塗出來的）");
            Check(Run(("op", "base"), ("color", "#008000")).ExitCode == 0 && Snap().ContentEquals(s0) && Events() == 0 && store.LoadMeta().BaseColor == "#008000", "改底色不動格子、不寫事件");

            var p = Run(("op", "point"), ("persona", "t"), ("lat", "23.7"), ("lon", "121"), ("radius", "2"), ("color", "#FF0000"));
            SCP_GlobeCells s1 = Snap();
            Check(p.ExitCode == 0 && int.Parse(Val(p, "changed")) >= 9, "畫點（半徑 2）");
            var poly = Run(("op", "polygon"), ("persona", "t"), ("color", "#00FF00"), ("points", "10,10;10,30;30,30;30,10"));
            SCP_GlobeCells s2 = Snap();
            Check(poly.ExitCode == 0 && int.Parse(Val(poly, "changed")) > 100, "多邊形填色");
            var ln = Run(("op", "line"), ("persona", "t"), ("color", "#FFFFFF"), ("points", "-40,-60;40,60"));
            SCP_GlobeCells s3 = Snap();
            Check(ln.ExitCode == 0, "畫線");
            readings.Add($"點 {Val(p, "changed")} 格、多邊形 {Val(poly, "changed")} 格、線 {Val(ln, "changed")} 格");
            {
                var st = store.Load();
                int c1 = st.Grid.LatLonToCell(23.7, 121), c2 = st.Grid.LatLonToCell(20, 20);
                Check(st.Cells.Get(c1) == 0xFF0000 && st.Cells.Get(c2) == 0x00FF00, "全彩原值存回（沒有量化）");
                readings.Add($"配置分塊 {st.Cells.TileKeys.Count()}／{6 * st.Cells.TilesPerSide * st.Cells.TilesPerSide}");
            }

            // 油漆桶：在多邊形裡面換色 ⇒ 改到的格數＝多邊形那筆（同一塊連通區）
            var fill = Run(("op", "fill"), ("persona", "t"), ("lat", "20"), ("lon", "20"), ("color", "#0000FF"));
            SCP_GlobeCells s4 = Snap();
            readings.Add($"油漆桶 {Val(fill, "changed")} 格");
            Check(fill.ExitCode == 0 && int.Parse(Val(fill, "changed")) > 100, "油漆桶在封閉區域內");
            int e4 = Events();
            var flood = Run(("op", "fill"), ("persona", "t"), ("lat", "-60"), ("lon", "-150"), ("color", "#0000FF"), ("max_cells", "1000"));
            Check(flood.ExitCode == 2 && Events() == e4 && Snap().ContentEquals(s4), "油漆桶超上限 ⇒ exit 2、零寫入");
            var same = Run(("op", "fill"), ("persona", "t"), ("lat", "20"), ("lon", "20"), ("color", "#0000FF"));
            Check(same.ExitCode == 0 && Val(same, "changed") == "0" && Events() == e4, "全部同色 ⇒ 不寫事件");

            // 反向：壞參數零寫入
            foreach (var bad in new[]
            {
                Run(("op", "line"), ("persona", "t"), ("color", "#FFFFFF"), ("points", "")),
                Run(("op", "point"), ("persona", "t"), ("lat", "95"), ("lon", "0"), ("color", "#010203")),
                Run(("op", "point"), ("persona", "t"), ("lat", "1"), ("lon", "0"), ("color", "#GG0000")),
                Run(("op", "point"), ("persona", "t"), ("lat", "1"), ("lon", "0"), ("color", "256,0,0")),
                Run(("op", "point"), ("lat", "1"), ("lon", "0"), ("color", "#010203")),
                Run(("op", "polygon"), ("persona", "t"), ("color", "#010203"), ("points", "80,0;80,120;80,240")),
            })
                Check(bad.ExitCode == 2, "壞參數 exit 2：" + string.Join(" ", bad.Lines));
            Check(Events() == e4 && Snap().ContentEquals(s4), "壞參數全部零寫入");

            // Undo：照順序一格不差退回
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().ContentEquals(s3), "undo 1 ⇒ 回到油漆桶之前");
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().ContentEquals(s2), "undo 2 ⇒ 回到畫線之前");
            var again = Run(("op", "line"), ("persona", "t"), ("color", "#FFFF00"), ("points", "0,0;0,40"));
            Check(again.ExitCode == 0 && Run(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().ContentEquals(s2), "undo 後再畫再退");
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().ContentEquals(s1), "undo ⇒ 回到多邊形之前");
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().ContentEquals(s0), "undo ⇒ 回到全空");
            int eEnd = Events();
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 1 && Events() == eEnd, "沒得退 ⇒ exit 1、零寫入");

            // 快取刪掉從頭重播 ＝ 快取讀出來的
            var blk = Run(("op", "point"), ("persona", "t"), ("lat", "0"), ("lon", "0"), ("radius", "3"), ("color", "#000000"));
            {
                var st = store.Load();
                int cb = st.Grid.LatLonToCell(0, 0);
                Check(blk.ExitCode == 0 && st.Cells.Get(cb) == 1 && st.Cells.PaintedCount() > 0, "純黑存成 0x000001，不會被當成沒畫過");
            }
            SCP_GlobeCells aWithCache = Snap();
            Directory.Delete(store.Paths.CacheDir, true);
            SCP_GlobeState re = store.Load();
            Check(!re.FromCache && re.Cells.ContentEquals(aWithCache), "刪快取重播一致");
            readings.Add($"事件共 {re.LastSeq} 筆（含 undo），最後有效 {re.Stack.Count} 筆");

            // 渲染：決定性
            string o1 = Path.Combine(aData, "a.png"), o2 = Path.Combine(aData, "b.png");
            var r1 = Run(("op", "render"), ("center", "0,0"), ("size", "128"), ("out", o1));
            var r2 = Run(("op", "render"), ("center", "0,0"), ("size", "128"), ("out", o2));
            Check(r1.ExitCode == 0 && r2.ExitCode == 0 && File.ReadAllBytes(o1).SequenceEqual(File.ReadAllBytes(o2)), "渲染決定性");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aData, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static double Ang(SCP_GlobeGrid g, int a, int b)
    {
        g.CellCenter(a, out double ax, out double ay, out double az);
        g.CellCenter(b, out double bx, out double by, out double bz);
        return Math.Acos(Math.Max(-1, Math.Min(1, ax * bx + ay * by + az * bz)));
    }

    /// <summary>球面三角形面積（Van Oosterom–Strackee）。</summary>
    static double Tri(double[] a, double[] b, double[] c)
    {
        double num = a[0] * (b[1] * c[2] - b[2] * c[1]) - a[1] * (b[0] * c[2] - b[2] * c[0]) + a[2] * (b[0] * c[1] - b[1] * c[0]);
        double den = 1 + (a[0] * b[0] + a[1] * b[1] + a[2] * b[2]) + (b[0] * c[0] + b[1] * c[1] + b[2] * c[2]) + (c[0] * a[0] + c[1] * a[1] + c[2] * a[2]);
        return 2 * Math.Abs(Math.Atan2(num, den));
    }
}
