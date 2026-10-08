// 區塊職責：可繪製球面（TASK-0467）的淨室驗收 —— 幾何換算、meta 基底、均勻度、跨面連續、繪製／Undo／重播、CLI 反向。
// 物理意義：每一格都拿「跟工具無關的量」對：格子中心換回自己、相鄰取樣點的角距離、逐格快照比對 ——
//   不以「回 success」當憑據。
using SCP.Core.Cmd;
using SCP.Core.Gui;
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

    static CheckRow GlobePreviewMemoryCleanRoom()
    {
        const string name = "球面預覽走記憶體影像：Put 驗長度／版本遞增／文字模式有圖無圖分得開／渲染結果逐位元組進得去／檔案被鎖也不影響（淨室）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_globe_mem_" + Guid.NewGuid().ToString("N"));
        string aKey = "selftest/globe-mem-" + Guid.NewGuid().ToString("N");
        try
        {
            // 長度與尺寸對不上 ⇒ 擋（⛔ 不安靜吞掉一張壞圖）
            bool aThrew = false;
            try { SCP_GuiImageStore.Put(aKey, new byte[10], 2, 2); } catch (ArgumentException) { aThrew = true; }
            Check(aThrew && !SCP_GuiImageStore.Has(aKey), "長度對不上 ⇒ 丟例外、登記處沒有東西");

            // 文字模式：沒圖 ⇒ [無圖…]；Put 之後 ⇒ [圖：…]
            string TextOf() { var ui = new SCP_Ui(); ui.ImageInteractive(SCP_GuiImageStore.Ref(aKey), 720f, "球面預覽", "t/img"); return SCP_GuiTextRenderer.Render(ui.Root); }
            Check(TextOf().Contains("[無圖：球面預覽]") && !TextOf().Contains("[圖："), "key 在、圖還沒進 ⇒ 文字模式印無圖");

            // 真的渲染一張球面，拿它放進去
            Directory.CreateDirectory(aRoot);
            var d = new Dictionary<string, string> { ["data_root"] = aRoot };
            SCP_CmdResult Run(params (string k, string v)[] a)
            {
                var m = new Dictionary<string, string>(d);
                foreach (var (k, v) in a) m[k] = v;
                return SCP_CmdRegistry.Dispatch("globe", m);
            }
            Check(Run(("op", "init"), ("n", "64")).ExitCode == 0, "init");
            Check(Run(("op", "point"), ("persona", "t"), ("lat", "0"), ("lon", "0"), ("radius", "3"), ("color", "#FF0000")).ExitCode == 0, "中心畫紅");
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aRoot, SCP_GlobePaths.DirName)));
            SCP_GlobeState st = store.Load();
            var v = new SCP_GlobeView { CenterLat = 0, CenterLon = 0, Zoom = 1, Graticule = 0, Size = 96 };
            byte[] rgba = SCP_GlobeRender.RenderRgba(st, v);

            // 反向對照：舊做法把圖寫到固定檔名；那個檔被別人鎖住時寫不進去 —— 記憶體這條路不碰檔案
            string aLocked = Path.Combine(aRoot, "view.png");
            using (var aHold = new FileStream(aLocked, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                bool aOldWayFails = false;
                try { File.WriteAllBytes(aLocked, new byte[] { 1 }); } catch (IOException) { aOldWayFails = true; }
                Check(aOldWayFails, "反向對照：鎖住的檔案確實寫不進去（舊做法的失敗樣子）");
                long aV1 = SCP_GuiImageStore.Put(aKey, rgba, v.Width, v.Height);
                Check(SCP_GuiImageStore.TryGet(aKey, out SCP_GuiImageFrame f1) && f1.Version == aV1 && f1.Width == 96 && f1.Height == 96, "檔案被鎖住時照樣放得進去、讀得出來");
                readings.Add($"檔案鎖住時：舊路徑寫入失敗={aOldWayFails}、記憶體版本 {aV1}");
            }

            Check(SCP_GuiImageStore.TryGet(aKey, out SCP_GuiImageFrame f2) && f2.Rgba.SequenceEqual(rgba), "進去的就是渲染出來的那份（逐位元組）");
            int aRed = 0;
            for (int k = 0; k < rgba.Length; k += 4) if (rgba[k] > 150 && rgba[k + 1] < 80 && rgba[k + 2] < 80) aRed++;   // ortho 有打光 ⇒ 不是純 255,0,0，認「紅色優勢」
            Check(aRed > 0, "那張圖裡看得到紅點（不是一張空白圖）");
            readings.Add($"96² 預覽紅色像素 {aRed}");
            Check(TextOf().Contains("[圖：球面預覽]"), "放進去之後文字模式印有圖");

            // 版本遞增、同 key 覆蓋
            long aV2 = SCP_GuiImageStore.Put(aKey, new byte[4 * 4 * 4], 4, 4);
            Check(aV2 > f2.Version && SCP_GuiImageStore.TryGet(aKey, out SCP_GuiImageFrame f3) && f3.Width == 4 && f3.Version == aV2, "同 key 再放 ⇒ 版本遞增、尺寸跟著新圖");
            SCP_GuiImageStore.Remove(aKey);
            Check(!SCP_GuiImageStore.Has(aKey) && TextOf().Contains("[無圖："), "Remove 之後回到無圖");
            Check(SCP_GuiImageStore.IsMemory(SCP_GuiImageStore.Ref(aKey)) && SCP_GuiImageStore.KeyOf(SCP_GuiImageStore.Ref(aKey)) == aKey
                  && !SCP_GuiImageStore.IsMemory("D:/a/b.png") && SCP_GuiImageStore.KeyOf("D:/a/b.png") == null, "mem: 前綴只認自己，路徑不被誤判");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { SCP_GuiImageStore.Remove(aKey); try { Directory.Delete(aRoot, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow GlobeRegridCleanRoom()
    {
        const string name = "球面 regrid：dry-run 零寫入／每格變 f×f 逐格對／網格巢狀／舊事件不動／Undo 跨一次與兩次 regrid／舊 N 快取續播／緊湊編碼＝舊格式逐格相同／初始格單位換算／過期 N 的寫入被擋（淨室）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aData = Path.Combine(Path.GetTempPath(), "senate_globe_regrid_" + Guid.NewGuid().ToString("N"));
        string aY = Path.Combine(Path.GetTempPath(), "senate_globe_legacy_" + Guid.NewGuid().ToString("N"));
        string aCacheKeep = Path.Combine(Path.GetTempPath(), "senate_globe_cachekeep_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aData);
            SCP_CmdResult Run(string iRoot, params (string K, string V)[] a)
            {
                var d = new Dictionary<string, string> { ["data_root"] = iRoot };
                foreach (var (k, v) in a) d[k] = v;
                return SCP_CmdRegistry.Dispatch("globe", d);
            }
            SCP_CmdResult R(params (string K, string V)[] a) => Run(aData, a);
            string Val(SCP_CmdResult r, string k) => r.Values.FirstOrDefault(kv => kv.Key == k).Value ?? "";
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aData, SCP_GlobePaths.DirName)));
            int Events() => store.Load().LastSeq;
            SCP_GlobeCells Snap() => store.Load().Cells.Clone();
            // 細格每一格都等於它的母格（逐格、不靠 Expand 自己）
            bool Nested(SCP_GlobeCells iFine, int iFineN, SCP_GlobeCells iCoarse, int iCoarseN)
            {
                int f = iFineN / iCoarseN;
                for (int face = 0; face < 6; face++)
                    for (int j = 0; j < iFineN; j++)
                        for (int i = 0; i < iFineN; i++)
                            if (iFine.Get(face * iFineN * iFineN + j * iFineN + i) != iCoarse.Get(face * iCoarseN * iCoarseN + (j / f) * iCoarseN + (i / f))) return false;
                return true;
            }

            Check(R(("op", "init"), ("n", "64")).ExitCode == 0, "init");
            Check(R(("op", "point"), ("persona", "t"), ("lat", "23.7"), ("lon", "121"), ("radius", "3"), ("color", "#FF0000")).ExitCode == 0, "A：畫紅點");
            SCP_GlobeCells sA = Snap();
            var poly = R(("op", "polygon"), ("persona", "t"), ("color", "#00FF00"), ("points", "10,10;10,30;30,30;30,10"));
            SCP_GlobeCells sB = Snap();
            Check(poly.ExitCode == 0, "B：多邊形");
            var ln = R(("op", "line"), ("persona", "t"), ("color", "#FFFFFF"), ("points", "-40,-60;40,60"), ("width", "1"));
            SCP_GlobeCells sL = Snap();
            Check(ln.ExitCode == 0, "L：跨面長線");
            int aPaintedOld = sL.PaintedCount();

            // ── 緊湊編碼 ──
            SCP_GlobeEvent ePoly = store.ReadEvent(2);
            int aRunSum = 0;
            for (int k = 0; k + 3 < ePoly.Runs.Count; k += 4) aRunSum += ePoly.Runs[k + 1];
            Check(ePoly.Cells.Count == 0 && ePoly.Runs.Count > 0 && ePoly.Runs.Count % 4 == 0 && aRunSum == ePoly.CountCells() && ePoly.CountCells() == int.Parse(Val(poly, "changed")),
                  "新事件只存 Runs（不存 Cells 三元組），區段長度總和＝實際改的格數");
            readings.Add($"多邊形 {ePoly.CountCells()} 格存成 {ePoly.Runs.Count / 4} 段（約 {ePoly.CountCells() / Math.Max(1, ePoly.Runs.Count / 4)} 格／段）");
            SCP_GlobeMeta m1 = store.LoadMeta();
            Check(m1.Mapping == SCP_GlobeMeta.MappingNameV2 && m1.Mapping != SCP_GlobeMeta.MappingName && m1.Version == 2,
                  "出現緊湊事件後 meta 改成 v2 mapping（舊版程式不認得 ⇒ 大聲拒絕，不會靜默算錯）");

            // 重疊覆蓋：舊值一半是沒畫過、一半是綠 ⇒ 區段要在舊值變的地方切開，undo 逐格回到前一刻
            SCP_GlobeCells sBeforeOver = Snap();
            var over = R(("op", "point"), ("persona", "t"), ("lat", "30"), ("lon", "30"), ("radius", "4"), ("color", "#FFFF00"));
            SCP_GlobeEvent eOver = store.ReadEvent(store.Load().LastSeq);
            var aOlds = new HashSet<int>();
            for (int k = 0; k + 3 < eOver.Runs.Count; k += 4) aOlds.Add(eOver.Runs[k + 3]);
            Check(over.ExitCode == 0 && aOlds.Contains(0) && aOlds.Contains(0x00FF00) && eOver.Runs.Count / 4 >= 2, "壓在別人的色上：舊值不同的格子分成不同段");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().ContentEquals(sBeforeOver), "混舊值的事件 undo 逐格回到前一刻");

            // 舊事件檔的位元組，regrid 前後要一模一樣
            var aBytes = new Dictionary<int, byte[]>();
            for (int q = 1; q <= Events(); q++) aBytes[q] = File.ReadAllBytes(store.Paths.EventFile(q));
            // 舊 N 的快取（regrid 之後拿回來，驗「舊快取＋補重播 regrid」）
            CopyDir(store.Paths.CacheDir, aCacheKeep);
            int eBefore = Events();

            // ── dry-run 與壞參數：零寫入 ──
            var dry = R(("op", "regrid"), ("persona", "t"));
            Check(dry.ExitCode == 0 && Val(dry, "regrid") == "dry_run" && Val(dry, "to_n") == "128" && Events() == eBefore && store.Load().Grid.N == 64, "dry-run：只印、零寫入");
            foreach (string aBad in new[] { "1", "2.5", "abc", "1000", "0", "-2" })
                Check(R(("op", "regrid"), ("persona", "t"), ("factor", aBad), ("confirm", "1")).ExitCode == 2 && Events() == eBefore, "regrid 壞倍率 " + aBad + " ⇒ exit 2、零寫入");
            Check(R(("op", "regrid"), ("confirm", "1")).ExitCode == 2 && Events() == eBefore, "regrid 沒給 persona ⇒ exit 2、零寫入");

            // ── regrid ×2 ──
            var rg = R(("op", "regrid"), ("persona", "t"), ("confirm", "1"), ("note", "測試"));
            Check(rg.ExitCode == 0 && Val(rg, "regrid") == "done" && Val(rg, "n") == "128", "regrid ×2 成功");
            SCP_GlobeState st1 = store.Load();
            Check(st1.Grid.N == 128 && st1.InitialN == 64 && st1.Regrids.Count == 2 && st1.Regrids[1] == 128 && st1.LastSeq == eBefore + 1, "N=128、初始 64、regrid 事件 #" + (eBefore + 1));
            Check(st1.Cells.PaintedCount() == aPaintedOld * 4, $"有畫的格子 {aPaintedOld} → {st1.Cells.PaintedCount()}（×4）");
            Check(Nested(st1.Cells, 128, sL, 64), "每個細格都等於它的母格（逐格，98304 格）");
            {
                // 反向對照：偷改一個細格 ⇒ Nested 一定要抓到（不然這把尺是不會紅的尺）
                SCP_GlobeCells aTamper = st1.Cells.Clone();
                int aAt = -1;
                for (int q = 0; q < st1.Grid.CellCount && aAt < 0; q++) if (aTamper.Get(q) != 0) aAt = q;
                aTamper.Set(aAt, aTamper.Get(aAt) ^ 0x010101);
                Check(aAt >= 0 && !Nested(aTamper, 128, sL, 64), "反向對照：偷改一個細格，巢狀檢查會紅");
            }
            bool aSame = true;
            for (int q = 1; q <= eBefore; q++) if (!File.ReadAllBytes(store.Paths.EventFile(q)).SequenceEqual(aBytes[q])) aSame = false;
            Check(aSame, "舊事件檔一個位元組都沒動");
            SCP_GlobeMeta m2 = store.LoadMeta();
            Check(m2.N == 128 && m2.InitialN == 64 && m2.Mapping == SCP_GlobeMeta.MappingNameV2, "meta：N=128、InitialN=64、v2");
            var stat = R(("op", "status"));
            Check(Val(stat, "n") == "128" && Val(stat, "initial_n") == "64" && Val(stat, "regrids") == "1", "status 讀得出 N／初始 N／regrid 次數");
            Check(R(("op", "history"), ("last", "3")).Lines.Any(l => l.Contains("regrid → 每面 128 格")), "history 看得到 regrid 事件");

            // 網格巢狀：同一個經緯度，細格一定落在母格裡面
            {
                var g64 = new SCP_GlobeGrid(64, m2.Faces);
                var g128 = st1.Grid;
                var rnd = new Random(7);
                int aBadNest = 0;
                for (int q = 0; q < 2000; q++)
                {
                    double la = Math.Asin(rnd.NextDouble() * 2 - 1) * 180 / Math.PI, lo = rnd.NextDouble() * 360 - 180;
                    g64.Unpack(g64.LatLonToCell(la, lo), out int cf, out int ci, out int cj);
                    g128.Unpack(g128.LatLonToCell(la, lo), out int ff, out int fi, out int fj);
                    if (ff != cf || fi / 2 != ci || fj / 2 != cj) aBadNest++;
                }
                readings.Add($"隨機 2000 個經緯度：細格不在母格內 {aBadNest} 個");
                Check(aBadNest == 0, "網格是巢狀的（2000 個隨機經緯度，細格都在母格內）");
            }

            // 舊 N 的快取＋補重播（含 regrid 事件）＝ 從頭重播
            Directory.Delete(store.Paths.CacheDir, true);
            CopyDir(aCacheKeep, store.Paths.CacheDir);
            SCP_GlobeState stOldCache = store.Load();
            Check(stOldCache.FromCache && stOldCache.Grid.N == 128 && stOldCache.Cells.ContentEquals(st1.Cells), "用 regrid 之前的舊快取開球：從快取起算、補重播 regrid、結果同一份");
            Directory.Delete(store.Paths.CacheDir, true);
            SCP_GlobeState stScratch = store.Load();
            Check(!stScratch.FromCache && stScratch.Cells.ContentEquals(st1.Cells), "刪快取從頭重播：同一份");
            R(("op", "point"), ("persona", "t"), ("lat", "-60"), ("lon", "-120"), ("radius", "1"), ("color", "#123456"));   // 存一份 N=128 的快取
            R(("op", "undo"), ("persona", "t"));
            SCP_GlobeState stNewCache = store.Load();
            Check(stNewCache.FromCache && stNewCache.Cells.ContentEquals(st1.Cells), "regrid 之後存的新快取（tiles_128）讀得回來、內容一致");
            Check(Directory.Exists(store.Paths.CacheTilesDir("tiles_128")), "N=128 的分塊放在自己的夾（不跟 N=64 的分塊混用）");

            // ── 初始格單位換算 ──
            var pIni = R(("op", "point"), ("persona", "t"), ("lat", "0"), ("lon", "0"), ("radius", "2"), ("color", "#0000FF"));
            var pCell = R(("op", "point"), ("persona", "t"), ("lat", "0"), ("lon", "90"), ("radius", "2"), ("color", "#0000FF"), ("unit", "cell"));
            int cIni = int.Parse(Val(pIni, "changed")), cCell = int.Parse(Val(pCell, "changed"));
            readings.Add($"radius=2：初始格單位 {cIni} 格、unit=cell {cCell} 格（regrid ×2 之後）");
            Check(pIni.ExitCode == 0 && pCell.ExitCode == 0 && cCell >= 9 && cCell <= 25 && cIni > cCell * 25 / 10, "radius=2 預設＝初始格（面積約 ×4），unit=cell 照實際格數");
            Check(R(("op", "point"), ("persona", "t"), ("lat", "0"), ("lon", "0"), ("radius", "2"), ("unit", "bogus")).ExitCode == 2, "unit 壞值 ⇒ exit 2");
            var kp = R(("op", "polygon"), ("persona", "t"), ("color", "#00AA00"), ("points", "40,10;40,30;55,30;55,10"));
            int aKp = int.Parse(Val(kp, "changed"));
            int eNow = Events();
            int aLimit = aKp / 4 + 10;
            var fCell = R(("op", "fill"), ("persona", "t"), ("lat", "47"), ("lon", "20"), ("color", "#AA00AA"), ("max_cells", aLimit.ToString()), ("unit", "cell"));
            Check(fCell.ExitCode == 2 && Events() == eNow, $"fill max_cells={aLimit}（unit=cell）擋 {aKp} 格 ⇒ exit 2、零寫入");
            var fIni = R(("op", "fill"), ("persona", "t"), ("lat", "47"), ("lon", "20"), ("color", "#AA00AA"), ("max_cells", aLimit.ToString()));
            Check(fIni.ExitCode == 0 && int.Parse(Val(fIni, "changed")) == aKp, $"同一個 max_cells={aLimit}（初始格）換算成 {aLimit * 4} 格 ⇒ 塗得下 {aKp} 格");

            // ── 過期 N 的寫入被擋 ──
            int eGuard = Events();
            bool aThrew = false;
            try { store.Paint("point", "t", "", new[] { 0, 1, 2 }, 0xFF0000, "", 64); } catch (SCP_GlobeException) { aThrew = true; }
            Check(aThrew && Events() == eGuard, "拿 N=64 算的格子去寫 N=128 的球面 ⇒ 例外、零寫入");

            // ── Undo：先退掉 regrid 之後畫的，回到 regrid 剛做完的樣子 ──
            SCP_GlobeCells sAfterR1 = st1.Cells;
            int aUndos = 0;
            while (store.Load().Stack.Count > st1.Stack.Count)   // ⚠ undo 自己也是事件（LastSeq 會增加）⇒ 用「有效繪製堆疊的深度」判斷
            {
                var u = R(("op", "undo"), ("persona", "t"));
                if (u.ExitCode != 0) break;
                aUndos++;
                if (aUndos > 20) break;
            }
            Check(Snap().ContentEquals(sAfterR1), $"退掉 regrid 之後畫的 {aUndos} 筆 ⇒ 回到 regrid 剛做完的樣子");

            // ── 第二次 regrid（128→256），undo 跨兩次 ──
            R(("op", "point"), ("persona", "t"), ("lat", "-20"), ("lon", "45"), ("radius", "2"), ("color", "#00FFFF"));   // X：N=128 時代
            SCP_GlobeCells sX = Snap();
            var rg2 = R(("op", "regrid"), ("persona", "t"), ("confirm", "1"));
            Check(rg2.ExitCode == 0 && Val(rg2, "n") == "256", "第二次 regrid（128→256）");
            R(("op", "point"), ("persona", "t"), ("lat", "-20"), ("lon", "80"), ("radius", "2"), ("color", "#FF00FF"));      // Y：N=256 時代
            SCP_GlobeState st2 = store.Load();
            Check(st2.Grid.N == 256 && st2.Regrids.Count == 4 && st2.NAt(2) == 64 && st2.NAt(eBefore + 2) == 128 && st2.NAt(st2.LastSeq) == 256, "regrid 歷史兩筆；每筆事件寫下時的 N 推得出來（64／128／256）");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 0 && Nested(Snap(), 256, sX, 128), "undo Y（256 時代）⇒ 等於畫 Y 之前（X 展成 256）");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 0 && Nested(Snap(), 256, sAfterR1, 128), "undo X（128 時代，跨一次 regrid）⇒ 等於第一次 regrid 剛做完（展成 256）");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 0 && Nested(Snap(), 256, sB, 64), "undo L（64 時代，跨兩次 regrid，一格變 4×4）⇒ 等於畫 L 之前");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 0 && Nested(Snap(), 256, sA, 64), "undo B（跨兩次）⇒ 等於畫 B 之前");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 0 && Snap().PaintedCount() == 0, "undo A ⇒ 全空");
            Check(R(("op", "undo"), ("persona", "t")).ExitCode == 1, "沒得退 ⇒ exit 1");
            Directory.Delete(store.Paths.CacheDir, true);
            Check(store.Load().Cells.PaintedCount() == 0 && store.Load().Grid.N == 256, "刪快取從頭重播（含兩次 regrid 與全部 undo）⇒ 一樣全空、N=256");

            // ── meta 被別人「剛好正在讀」：換檔要重試，不能一擲就丟（正式 regrid 撞過一次） ──
            {
                string aMeta = store.Paths.Meta;
                var aBase = store.LoadMeta();
                File.Copy(aMeta, aMeta + ".probe", true);   // 換檔用的來源（要真的存在，不然丟的是「找不到檔」而不是「被掛住」）
                using (var aHold = new FileStream(aMeta, FileMode.Open, FileAccess.Read, FileShare.Read))   // 不給 Delete 共享 ⇒ File.Replace 會丟 IOException
                {
                    bool aNoRetryFails = false;
                    try { File.Replace(aMeta + ".probe", aMeta, null); } catch (Exception) { aNoRetryFails = true; }
                    Check(aNoRetryFails, "反向對照：目標檔被掛住時，一次性的 File.Replace 確實會丟例外");
                }
                File.Delete(aMeta + ".probe");
                var aReleaser = new System.Threading.Thread(() => { using (var h = new FileStream(aMeta, FileMode.Open, FileAccess.Read, FileShare.Read)) System.Threading.Thread.Sleep(250); });
                aReleaser.Start();
                System.Threading.Thread.Sleep(60);   // 讓那條執行緒先把檔案掛住
                bool aWrote = true;
                try { store.WriteMeta(aBase); } catch (Exception) { aWrote = false; }
                aReleaser.Join();
                Check(aWrote, "meta 被掛住 250 ms ⇒ WriteMeta 重試後寫成功");
                var aLong = new System.Threading.Thread(() => { using (var h = new FileStream(aMeta, FileMode.Open, FileAccess.Read, FileShare.Read)) System.Threading.Thread.Sleep(3000); });
                aLong.Start();
                System.Threading.Thread.Sleep(60);
                bool aThrewLong = false;
                try { store.WriteMeta(aBase); } catch (IOException) { aThrewLong = true; }
                aLong.Join();
                Check(aThrewLong, "掛住太久（3 秒）⇒ 重試用完照丟例外（不吞）");
                Check(store.LoadMeta().N == aBase.N, "兩次之後 meta 仍然讀得回來、內容沒壞");
            }

            // ── 緊湊編碼 ＝ 舊格式（Cells 三元組）逐格相同 ──
            {
                string aX = Path.Combine(Path.GetTempPath(), "senate_globe_x_" + Guid.NewGuid().ToString("N"));
                try
                {
                    Directory.CreateDirectory(aX); Directory.CreateDirectory(aY);
                    Check(Run(aX, ("op", "init"), ("n", "64")).ExitCode == 0, "（對照組）init");
                    Run(aX, ("op", "point"), ("persona", "t"), ("lat", "23.7"), ("lon", "121"), ("radius", "3"), ("color", "#FF0000"));
                    Run(aX, ("op", "polygon"), ("persona", "t"), ("color", "#00FF00"), ("points", "10,10;10,30;30,30;30,10"));
                    Run(aX, ("op", "point"), ("persona", "t"), ("lat", "30"), ("lon", "30"), ("radius", "4"), ("color", "#FFFF00"));
                    Run(aX, ("op", "undo"), ("persona", "t"));
                    var sx = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aX, SCP_GlobePaths.DirName)));
                    // 把每筆事件轉成舊格式（Cells 三元組、沒有 Runs），寫進另一顆「純舊版」的球
                    var sy = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aY, SCP_GlobePaths.DirName)));
                    SCP_GlobeMeta myMeta = sx.LoadMeta();
                    myMeta.Mapping = SCP_GlobeMeta.MappingName; myMeta.Version = 1; myMeta.InitialN = 0;
                    sy.WriteMeta(myMeta);
                    Directory.CreateDirectory(sy.Paths.Events);
                    int aLegacyCells = 0;
                    for (int q = 1; q <= sx.Load().LastSeq; q++)
                    {
                        SCP_GlobeEvent e = sx.ReadEvent(q);
                        var legacy = new SCP_GlobeEvent { Seq = e.Seq, Op = e.Op, Persona = e.Persona, At = e.At, Note = e.Note, Target = e.Target, Zone = e.Zone };
                        e.ForEachCell((idx, nw, old) => { legacy.Cells.Add(idx); legacy.Cells.Add(nw); legacy.Cells.Add(old); aLegacyCells++; });
                        File.WriteAllText(sy.Paths.EventFile(q), SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(legacy), false));
                    }
                    SCP_GlobeState sxs = sx.Load(), sys = sy.Load();
                    Check(sys.Cells.ContentEquals(sxs.Cells) && sys.Stack.SequenceEqual(sxs.Stack), "舊格式（Cells 三元組）重播 ＝ 緊湊編碼重播（格子與有效堆疊都一樣）");
                    readings.Add($"對照：{aLegacyCells} 格轉成舊格式");
                    SCP_GlobeCells sysCells = sys.Cells.Clone();
                    // 舊格式的球：undo 仍可用、再畫新事件會升級成 v2
                    Check(Run(aY, ("op", "point"), ("persona", "t"), ("lat", "-30"), ("lon", "100"), ("radius", "2"), ("color", "#0000FF")).ExitCode == 0
                          && sy.LoadMeta().Mapping == SCP_GlobeMeta.MappingNameV2, "舊格式的球照樣能畫、第一筆緊湊事件寫下時才升級成 v2");
                    Check(Run(aY, ("op", "undo"), ("persona", "t")).ExitCode == 0 && sy.Load().Cells.ContentEquals(sysCells), "舊格式的球：undo 緊湊事件後回到只有舊事件的樣子");
                }
                finally { try { Directory.Delete(aX, true); } catch { } }
            }
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally
        {
            foreach (string d in new[] { aData, aY, aCacheKeep }) { try { Directory.Delete(d, true); } catch { } }
        }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static void CopyDir(string iFrom, string iTo)
    {
        Directory.CreateDirectory(iTo);
        foreach (string f in Directory.GetFiles(iFrom)) File.Copy(f, Path.Combine(iTo, Path.GetFileName(f)), true);
        foreach (string d in Directory.GetDirectories(iFrom)) CopyDir(d, Path.Combine(iTo, Path.GetFileName(d)));
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
