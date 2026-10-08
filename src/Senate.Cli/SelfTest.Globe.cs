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

                // 頁面的拖曳／滾輪換算（純函式）
                Senate.Cli.Pages.GlobeViewerPage.GlobeDrag(0, 0, 1, 0.5, 0, out double la1, out double lo1);
                Senate.Cli.Pages.GlobeViewerPage.GlobeDrag(0, 0, 4, 0.5, 0, out _, out double lo4);
                Senate.Cli.Pages.GlobeViewerPage.GlobeDrag(0, 170, 1, -0.5, 0, out _, out double loWrap);
                Senate.Cli.Pages.GlobeViewerPage.GlobeDrag(80, 0, 1, 0, 1, out double laClamp, out _);
                Senate.Cli.Pages.GlobeViewerPage.GlobeDrag(0, 0, 1, 0, 0.1, out double laDown, out _);
                readings.Add($"拖半張圖：zoom1 經度 {lo1:0.#}°、zoom4 {lo4:0.#}°；跨換日線 {loWrap:0.#}°");
                Check(la1 == 0 && lo1 < -50 && Math.Abs(lo4 - lo1 / 4) < 1e-9, "往右拖 ⇒ 中心往西、zoom 越大轉越少");
                Check(loWrap > -180 && loWrap < -100, "經度跨換日線折回 -180..180");
                Check(laClamp == 89 && laDown > 0, "往下拖 ⇒ 中心往北、緯度夾在 ±89");
                Check(Math.Abs(Senate.Cli.Pages.GlobeViewerPage.GlobeWheelZoom(2, 1) - 2.5) < 1e-9
                      && Senate.Cli.Pages.GlobeViewerPage.GlobeWheelZoom(1, -100) == 0.5
                      && Senate.Cli.Pages.GlobeViewerPage.GlobeWheelZoom(300, 10) == 400, "滾輪一格 ×1.25、夾在 0.5..400");
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

            // cell：查得到剛畫的黑、沒畫過的是 empty
            var cBlack = Run(("op", "cell"), ("lat", "0"), ("lon", "0"));
            var cSea = Run(("op", "cell"), ("lat", "-45"), ("lon", "-120"));
            Check(Val(cBlack, "color") == "#000001" && Val(cSea, "color") == "empty", "op=cell 讀回格子顏色");

            // 橡皮擦：擦回大海；跟 undo 一樣只是一筆事件
            int eBefore = Events();
            var er = Run(("op", "erase"), ("persona", "t"), ("lat", "0"), ("lon", "0"), ("radius", "3"));
            Check(er.ExitCode == 0 && store.Load().Cells.PaintedCount() == 0 && Events() == eBefore + 1
                  && store.ReadEvent(eBefore + 1).Op == "erase-point", "op=erase 擦回底色（記成 erase-point 事件）");
            Check(Run(("op", "erase"), ("persona", "t"), ("shape", "blob"), ("lat", "0"), ("lon", "0")).ExitCode == 2 && Events() == eBefore + 1, "erase 壞 shape 零寫入");
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 0 && store.Load().Cells.ContentEquals(aWithCache), "erase 也能 undo");

            // 渲染：決定性；給 persona ⇒ 寫進自己的 cmd 夾，不給 letters_root 就喊
            string o1 = Path.Combine(aData, "a.png"), o2 = Path.Combine(aData, "b.png");
            var r1 = Run(("op", "render"), ("center", "0,0"), ("size", "128"), ("out", o1));
            var r2 = Run(("op", "render"), ("center", "0,0"), ("size", "128"), ("out", o2));
            Check(r1.ExitCode == 0 && r2.ExitCode == 0 && File.ReadAllBytes(o1).SequenceEqual(File.ReadAllBytes(o2)), "渲染決定性");
            string aLetters = Path.Combine(aData, "letters");
            Directory.CreateDirectory(Path.Combine(aLetters, "t"));
            Check(Run(("op", "render"), ("persona", "t"), ("size", "64")).ExitCode == 2, "render 給 persona 沒 letters_root ⇒ exit 2");
            var rp = Run(("op", "render"), ("persona", "t"), ("letters_root", aLetters), ("size", "64"));
            Check(rp.ExitCode == 0 && File.Exists(Path.Combine(aLetters, "t", "cmd", "globe_view.png")), "render 寫進 <persona>/cmd/globe_view.png");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aData, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow GlobeExportCleanRoom()
    {
        const string name = "球面輸出：世界地圖方位（西北紅在左上、東南藍在右下）／空格顯示底色／2:1／決定性／export=1 落 exports/ 不互蓋／壞參數 exit 2（淨室）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aData = Path.Combine(Path.GetTempPath(), "senate_globe_export_" + Guid.NewGuid().ToString("N"));
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

            Check(Run(("op", "init"), ("n", "64")).ExitCode == 0, "init");
            Check(Run(("op", "point"), ("persona", "t"), ("lat", "60"), ("lon", "-150"), ("radius", "2"), ("color", "#FF0000")).ExitCode == 0, "西北畫紅");
            Check(Run(("op", "point"), ("persona", "t"), ("lat", "-60"), ("lon", "150"), ("radius", "2"), ("color", "#0000FF")).ExitCode == 0, "東南畫藍");
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aData, SCP_GlobePaths.DirName)));
            SCP_GlobeState st = store.Load();

            // 方位：用像素中心反推經緯度的同一條公式去取像素，再拿格子本身的顏色對（格子走 LatLonToCell，不經渲染器）
            var v = new SCP_GlobeView { Projection = SCP_GlobeView.ProjEquirect, Size = 360, Graticule = 0 };
            byte[] rgba = SCP_GlobeRender.RenderRgba(st, v);
            Check(v.Width == 360 && v.Height == 180 && rgba.Length == 360 * 180 * 4, "世界地圖 2:1");
            int Px(double lat, double lon, int ch) { int x = (int)((lon + 180) / 360 * v.Width), y = (int)((90 - lat) / 180 * v.Height); return rgba[(y * v.Width + x) * 4 + ch]; }
            bool Is(double lat, double lon, int rgb) => Px(lat, lon, 0) == ((rgb >> 16) & 255) && Px(lat, lon, 1) == ((rgb >> 8) & 255) && Px(lat, lon, 2) == (rgb & 255);
            Check(st.Cells.Get(st.Grid.LatLonToCell(60, -150)) == 0xFF0000 && Is(60, -150, 0xFF0000), "西北紅落在地圖左上（左右、上下都沒翻）");
            Check(st.Cells.Get(st.Grid.LatLonToCell(-60, 150)) == 0x0000FF && Is(-60, 150, 0x0000FF), "東南藍落在地圖右下");
            Check(!Is(-60, -150, 0xFF0000) && !Is(60, 150, 0x0000FF), "反向對照：鏡像的位置不是那個顏色");
            Check(Is(0, 0, st.BaseRgb) && Is(-30, 100, st.BaseRgb), "沒畫過的格子顯示底色（不打光）");
            Check(SCP_GlobeRender.RenderRgba(st, v).SequenceEqual(rgba), "世界地圖決定性");
            int aRedPx = 0;
            for (int k = 0; k < rgba.Length; k += 4) if (rgba[k] == 255 && rgba[k + 1] == 0 && rgba[k + 2] == 0) aRedPx++;
            readings.Add($"360×180 地圖上紅色像素 {aRedPx}");
            Check(aRedPx > 0, "紅色點在地圖上看得到");

            // export=1：寫進 exports/、檔名帶時間戳、連按兩次不互蓋；值帶回寬高
            var e1 = Run(("op", "render"), ("projection", "equirect"), ("size", "64"), ("export", "1"));
            System.Threading.Thread.Sleep(5);
            var e2 = Run(("op", "render"), ("projection", "equirect"), ("size", "64"), ("export", "1"));
            var e3 = Run(("op", "render"), ("center", "0,0"), ("size", "32"), ("export", "1"));
            string p1 = Val(e1, "path"), p2 = Val(e2, "path"), p3 = Val(e3, "path");
            string aDir = store.Paths.ExportsDir.Replace('\\', '/');
            Check(e1.ExitCode == 0 && e2.ExitCode == 0 && e3.ExitCode == 0, "export 三次都成功");
            Check(p1.StartsWith(aDir + "/globe_map_", StringComparison.Ordinal) && p3.StartsWith(aDir + "/globe_view_", StringComparison.Ordinal), "export 落在 <球面根>/exports/，map／view 分開命名");
            Check(p1 != p2 && File.Exists(p1) && File.Exists(p2), "連按兩次不互蓋");
            Check(Val(e1, "width") == "64" && Val(e1, "height") == "32" && Val(e3, "width") == "32" && Val(e3, "height") == "32", "回傳寬高（地圖 2:1、視角正方形）");
            readings.Add($"exports/ 有 {Directory.GetFiles(store.Paths.ExportsDir).Length} 張");

            // 壞參數：零寫入（exports/ 張數不變）
            int aBefore = Directory.GetFiles(store.Paths.ExportsDir).Length;
            foreach (var bad in new[]
            {
                Run(("op", "render"), ("projection", "globe"), ("export", "1")),
                Run(("op", "render"), ("projection", "equirect"), ("size", "9000"), ("export", "1")),
                Run(("op", "render"), ("size", "5000"), ("export", "1")),
                Run(("op", "render"), ("export", "1"), ("out", Path.Combine(aData, "x.png"))),
            })
                Check(bad.ExitCode == 2, "壞參數 exit 2：" + string.Join(" ", bad.Lines));
            Check(Directory.GetFiles(store.Paths.ExportsDir).Length == aBefore && !File.Exists(Path.Combine(aData, "x.png")), "壞參數零寫入");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aData, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow GlobeZoneCleanRoom()
    {
        const string name = "球面施工區：開區／同 id 擋／重疊可／跨 180° 判內外／非成員不能改、join 後可以／cell 列出所在區／框線疊圖（淨室）";
        var failures = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aData = Path.Combine(Path.GetTempPath(), "senate_globe_zone_" + Guid.NewGuid().ToString("N"));
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
            Check(Run(("op", "init"), ("n", "64")).ExitCode == 0, "init");
            Check(Run(("op", "zone"), ("sub", "add"), ("persona", "a"), ("id", "japan"), ("title", "創造日本"), ("bbox", "24,122,46,146")).ExitCode == 0, "開區");
            Check(Run(("op", "zone"), ("sub", "add"), ("persona", "b"), ("id", "japan"), ("title", "搶名"), ("bbox", "0,0,1,1")).ExitCode == 2, "同 id 擋");
            Check(Run(("op", "zone"), ("sub", "add"), ("persona", "b"), ("id", "east-asia"), ("title", "東亞"), ("bbox", "10,100,50,150")).ExitCode == 0, "重疊可以");
            Check(Run(("op", "zone"), ("sub", "add"), ("persona", "b"), ("id", "fiji"), ("title", "斐濟"), ("bbox", "-21,176,-15,-178")).ExitCode == 0, "跨 180° 開區");
            Check(Run(("op", "zone"), ("sub", "add"), ("persona", "b"), ("id", "Bad Id"), ("title", "x"), ("bbox", "0,0,1,1")).ExitCode == 2
                  && Run(("op", "zone"), ("sub", "add"), ("persona", "b"), ("id", "x"), ("title", "x"), ("bbox", "10,0,5,1")).ExitCode == 2, "壞 id／壞 bbox 擋");
            Check(Val(Run(("op", "zone"), ("sub", "list")), "zones") == "3", "列出 3 區");

            var tokyo = Run(("op", "cell"), ("lat", "35.7"), ("lon", "139.7"));
            Check(Val(tokyo, "zones") == "east-asia,japan", "東京同時在兩區：" + Val(tokyo, "zones"));
            Check(Val(Run(("op", "cell"), ("lat", "-18"), ("lon", "179")), "zones") == "fiji"
                  && Val(Run(("op", "cell"), ("lat", "-18"), ("lon", "-179")), "zones") == "fiji"
                  && Val(Run(("op", "cell"), ("lat", "-18"), ("lon", "170")), "zones") == "", "跨 180° 判內外");

            Check(Run(("op", "zone"), ("sub", "update"), ("persona", "c"), ("id", "japan"), ("status", "done")).ExitCode == 2, "非成員不能改");
            Check(Run(("op", "zone"), ("sub", "join"), ("persona", "c"), ("id", "japan")).ExitCode == 0
                  && Run(("op", "zone"), ("sub", "update"), ("persona", "c"), ("id", "japan"), ("status", "paused")).ExitCode == 0, "join 後可以改");
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aData, SCP_GlobePaths.DirName)));
            var jp = new SCP_GlobeZones(store.Paths).Find("japan");
            Check(jp != null && jp.Status == "paused" && jp.Members.Contains("c") && jp.Owner == "a", "改動讀回");
            Check(Run(("op", "zone"), ("sub", "update"), ("persona", "a"), ("id", "japan"), ("status", "finished")).ExitCode == 2, "壞 status 擋");
            Check(store.Load().LastSeq == 0, "施工區不寫格子事件");

            // 規劃：計畫清單、勾項（署名＋expect_text 防錯格）、日誌、繪製掛區統計
            Check(Run(("op", "zone"), ("sub", "plan"), ("persona", "z"), ("id", "japan"), ("item", "本州")).ExitCode == 2, "非成員不能加計畫");
            Check(Run(("op", "zone"), ("sub", "plan"), ("persona", "a"), ("id", "japan"), ("item", "本州陸地")).ExitCode == 0
                  && Run(("op", "zone"), ("sub", "plan"), ("persona", "c"), ("id", "japan"), ("item", "富士山")).ExitCode == 0, "加計畫");
            Check(Run(("op", "zone"), ("sub", "check"), ("persona", "a"), ("id", "japan"), ("index", "1"), ("expect_text", "富士")).ExitCode == 2
                  && new SCP_GlobeZones(store.Paths).Find("japan")!.Items.TrueForAll(x => !x.Done), "expect_text 對不上 ⇒ 一格都不勾");
            Check(Run(("op", "zone"), ("sub", "check"), ("persona", "a"), ("id", "japan"), ("index", "9")).ExitCode == 2, "序號越界擋");
            var ck = Run(("op", "zone"), ("sub", "check"), ("persona", "a"), ("id", "japan"), ("index", "1"), ("expect_text", "本州"));
            Check(ck.ExitCode == 0 && Val(ck, "left") == "1", "勾項＋剩幾項");
            Check(Run(("op", "zone"), ("sub", "log"), ("persona", "c"), ("id", "japan"), ("note", "畫了輪廓；下一步富士山")).ExitCode == 0, "寫日誌");
            Check(Run(("op", "point"), ("persona", "a"), ("lat", "36"), ("lon", "138"), ("radius", "1"), ("color", "#00AA00"), ("zone", "nope")).ExitCode == 2
                  && store.Load().LastSeq == 0, "掛不存在的施工區 ⇒ 擋、零寫入");
            Run(("op", "point"), ("persona", "a"), ("lat", "36"), ("lon", "138"), ("radius", "1"), ("color", "#00AA00"), ("zone", "japan"));
            Run(("op", "point"), ("persona", "c"), ("lat", "35"), ("lon", "136"), ("color", "#00AA00"), ("zone", "japan"));
            Run(("op", "point"), ("persona", "c"), ("lat", "34"), ("lon", "135"), ("color", "#00AA00"), ("zone", "japan"));
            Run(("op", "undo"), ("persona", "c"));
            Run(("op", "point"), ("persona", "a"), ("lat", "0"), ("lon", "0"), ("color", "#00AA00"));
            var sh = Run(("op", "zone"), ("sub", "show"), ("id", "japan"));
            Check(Val(sh, "items") == "2" && Val(sh, "items_done") == "1" && Val(sh, "events") == "2"
                  && sh.Lines.Exists(l => l.Contains("下一項：富士山")) && sh.Lines.Exists(l => l.Contains("下一步富士山")),
                  $"show 統計（被 undo 的、沒掛區的不算）：events={Val(sh, "events")} cells={Val(sh, "cells")}");

            string o1 = Path.Combine(aData, "z0.png"), o2 = Path.Combine(aData, "z1.png");
            Run(("op", "render"), ("center", "35,135"), ("zoom", "3"), ("size", "128"), ("graticule", "0"), ("out", o1));
            Run(("op", "render"), ("center", "35,135"), ("zoom", "3"), ("size", "128"), ("graticule", "0"), ("zones", "1"), ("out", o2));
            Check(!File.ReadAllBytes(o1).SequenceEqual(File.ReadAllBytes(o2)), "zones=1 疊得出框線");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aData, true); } catch { } }
        return failures.Count == 0 ? new CheckRow(name, "全部格子通過", CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures), CheckResult.Fail);
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
