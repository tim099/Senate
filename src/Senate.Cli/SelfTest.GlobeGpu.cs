// 區塊職責：球面 GPU 預覽（TASK-0470）的淨室驗收 —— GPU／CPU 逐像素格子索引對拍（含反向對照）、分塊增量上傳、顏色抽樣。
// 物理意義：GPU 跑的是視窗預覽同一份 SenateGlobeGl（離屏 context 只是換一個宿主）；CPU 參考解是 SCP_GlobeCamera.CellIndices（double）。
//          對拍比的是**格子索引**不是顏色 —— 顏色一樣不代表取到同一格（相鄰兩格可以同色），索引一樣才是。
//          這台機器建不出 GL 3.3 ⇒ Skipped（⛔ 不是通過）。
using SCP.Core.Cmd;
using SCP.Core.Globe;
using Senate.Desktop;

namespace Senate.Cli;

public static partial class SelfTest
{
    /// <summary>不一致像素比例的上限（float 在格子邊界上差一格；超過它就是公式／基底對不上，不是精度）。</summary>
    const double GlobeGpuMismatchLimit = 0.005;

    static CheckRow GlobeGpuParityCleanRoom()
    {
        const string name = "球面 GPU 預覽對拍：同一組透視視角（面接縫／極區／跨 180°／深度放大／拉遠）每個像素的格子索引 GPU＝CPU（不一致 ≤ 0.5%）＋反向對照：竄改一面基底必須對不上（淨室，TASK-0470）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_globe_gpu_" + Guid.NewGuid().ToString("N"));
        using var gpu = new SenateGlobeOffscreen();
        try
        {
            var store = new SCP_GlobeStore(new SCP_GlobePaths(aRoot));
            store.WriteMeta(new SCP_GlobeMeta { N = 4096 });   // 線上同一個 N；對拍只比索引，不必畫格子
            SCP_GlobeState st = store.Load();
            var cams = new (string Label, double Lat, double Lon, double Zoom)[]
            {
                ("台灣 z1", 23.7, 121, 1), ("+X/+Y 接縫 z3", 0, 45, 3), ("北極 z2", 89.5, 0, 2),
                ("跨 180° z5", 0, 180, 5), ("深度放大 z400", 35, -135, 400), ("拉遠 z0.5", -60, 10, 0.5),
            };
            double aWorst = 0;
            foreach (var c in cams)
            {
                var cam = new SCP_GlobeCamera { CenterLat = c.Lat, CenterLon = c.Lon, Zoom = c.Zoom, Width = 256, Height = 256 };
                var scene = new SCP_GlobeGpuScene(st, 1, cam, 0, false, new List<SCP_GlobeZone>());
                if (!gpu.TryRender(scene, SenateGlobeGl.Mode.Index, out byte[] rgba, out string err))
                {
                    if (gpu.InitError != null)
                        return new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped);
                    failures.Add(c.Label + " GPU 失敗：" + err); continue;
                }
                int[] cpu = cam.CellIndices(st.Grid);
                int aDiff = 0, aHit = 0, aBadRange = 0;
                for (int k = 0; k < cpu.Length; k++)
                {
                    uint u = (uint)(rgba[k * 4] | rgba[k * 4 + 1] << 8 | rgba[k * 4 + 2] << 16 | rgba[k * 4 + 3] << 24);
                    int g = u == 0xFFFFFFFF ? -1 : (int)u;
                    if (g >= st.Grid.CellCount) aBadRange++;
                    if (cpu[k] >= 0) aHit++;
                    if (g != cpu[k]) aDiff++;
                }
                double aFrac = (double)aDiff / cpu.Length;
                aWorst = Math.Max(aWorst, aFrac);
                readings.Add($"{c.Label}：打到球 {aHit} 像素、不一致 {aDiff}（{aFrac:P3}）");
                Check(aHit > 1000, c.Label + "：打到球的像素太少（視角沒對準？）");
                Check(aBadRange == 0, c.Label + "：GPU 給了越界索引");
                Check(aFrac <= GlobeGpuMismatchLimit, $"{c.Label}：不一致 {aFrac:P3} > {GlobeGpuMismatchLimit:P1}");
            }
            readings.Add($"最差 {aWorst:P3}｜{gpu.GlInfo}");

            // 反向對照：把 +X 面的 U 翻號傳進 shader（模擬 GLSL 寫死了一份錯的基底）⇒ 看著 +X 面的視角必須大量對不上
            var camX = new SCP_GlobeCamera { CenterLat = 0, CenterLon = 0, Zoom = 2, Width = 256, Height = 256 };
            var sceneX = new SCP_GlobeGpuScene(st, 1, camX, 0, false, new List<SCP_GlobeZone>());
            if (gpu.TryRender(sceneX, SenateGlobeGl.Mode.Index, out byte[] bad, out string err2, iTamperFace0: true))
            {
                int[] cpu = camX.CellIndices(st.Grid);
                int aDiff = 0;
                for (int k = 0; k < cpu.Length; k++)
                {
                    uint u = (uint)(bad[k * 4] | bad[k * 4 + 1] << 8 | bad[k * 4 + 2] << 16 | bad[k * 4 + 3] << 24);
                    if ((u == 0xFFFFFFFF ? -1 : (int)u) != cpu[k]) aDiff++;
                }
                double aFrac = (double)aDiff / cpu.Length;
                readings.Add($"反向對照（+X 面 U 翻號）：不一致 {aFrac:P1}");
                Check(aFrac > 0.2, $"反向對照：竄改基底卻只有 {aFrac:P1} 對不上 ⇒ 這把尺量不出基底錯");
            }
            else failures.Add("反向對照 GPU 失敗：" + err2);
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aRoot, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow GlobeGpuPageFallbackCleanRoom()
    {
        const string name = "球面頁 GPU／CPU 三態：沒有畫家 ⇒ CPU 且說原因／有畫家 ⇒ 放 gpu: 場景（帶狀態與透視鏡頭）／畫家回報這一版失敗 ⇒ 退回 CPU 並印原因（淨室，文字模式，TASK-0470）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_globe_gpupage_" + Guid.NewGuid().ToString("N")[..8]);
        Type aSceneType = typeof(SCP_GlobeGpuScene);
        bool aWasRegistered = SCP.Core.Gui.SCP_GuiGpuViews.CanPaint(aSceneType);
        try
        {
            string aCfgDir = Path.Combine(aTmp, "SenateData", "config");
            Directory.CreateDirectory(aCfgDir);
            string aProj = aTmp.Replace(Path.DirectorySeparatorChar, '/');
            File.WriteAllText(Path.Combine(aCfgDir, "senate.local.json"),
                "{\n  \"schemaVersion\": 1,\n  \"paths\": {\n    \"agentCommandsRoot\": \"" + aProj + "/AgentCommands\"\n  }\n}\n");
            string aData = aProj + "/AgentCommands";
            Directory.CreateDirectory(aData);
            Check(SCP_CmdRegistry.Dispatch("globe", new Dictionary<string, string> { ["data_root"] = aData, ["op"] = "init", ["n"] = "64" }).ExitCode == 0, "init");

            var aPage = new Senate.Cli.Pages.GlobeViewerPage(new Senate.Cli.Pages.SenateModel(aTmp));
            aPage.OnPush();
            string DrawText() { var ui = new SCP.Core.Gui.SCP_Ui(); aPage.Draw(ui); return SCP.Core.Gui.SCP_GuiTextRenderer.Render(ui.Root, 200); }

            SCP.Core.Gui.SCP_GuiGpuViews.UnregisterPainter(aSceneType);
            SCP.Core.Gui.SCP_GuiGpuViews.Remove(Senate.Cli.Pages.GlobeViewerPage.GpuKey);
            DrawText();   // 第一幀：CPU 這條路是「先看有沒有圖、再開始渲染」⇒ 圖在第二幀才有（頁面原本的節奏）
            string t1 = DrawText();
            bool aOk1 = t1.Contains("這個宿主沒有 GPU 畫面") && t1.Contains("[圖：球面預覽]") && !t1.Contains("球面預覽（GPU）");
            if (!aOk1) readings.Add("沒有畫家那一幀：" + string.Join(" / ", t1.Split('\n').Where(l => l.Contains("預覽") || l.Contains("GPU") || l.Contains("圖")).Take(6)));
            Check(aOk1, "沒有畫家 ⇒ CPU 圖＋原因");
            Check(!SCP.Core.Gui.SCP_GuiGpuViews.Has(Senate.Cli.Pages.GlobeViewerPage.GpuKey), "沒有畫家 ⇒ 不放 gpu: 場景");

            SCP.Core.Gui.SCP_GuiGpuViews.RegisterPainter(aSceneType);
            string t2 = DrawText();
            bool aPut = SCP.Core.Gui.SCP_GuiGpuViews.TryGet(Senate.Cli.Pages.GlobeViewerPage.GpuKey, out SCP.Core.Gui.SCP_GuiGpuFrame f);
            var sc = aPut ? f.Scene as SCP_GlobeGpuScene : null;
            Check(t2.Contains("球面預覽（GPU）") && t2.Contains("GPU 透視（即時）"), "有畫家 ⇒ 畫面是 gpu: 圖、有開關");
            Check(sc != null && sc.State.Grid.N == 64 && sc.Camera.Width == 720 && Math.Abs(sc.Camera.CenterLat - 23.7) < 1e-9, "放進去的場景帶著這顆球的狀態與頁面的視角");
            readings.Add($"有畫家：場景版本 {(aPut ? f.Version : -1)}、N={sc?.State.Grid.N}、鏡頭 {sc?.Camera.CenterLat},{sc?.Camera.CenterLon} z{sc?.Camera.Zoom}");

            // 同樣的視角再畫一幀 ⇒ 不重放場景（宿主就不會重畫）
            DrawText();
            Check(SCP.Core.Gui.SCP_GuiGpuViews.TryGet(Senate.Cli.Pages.GlobeViewerPage.GpuKey, out var f2) && f2.Version == f.Version, "視角沒變 ⇒ 場景版本不變");

            SCP.Core.Gui.SCP_GuiGpuViews.Report(Senate.Cli.Pages.GlobeViewerPage.GpuKey, f.Version, "模擬的 GPU 失敗", "");
            string t3 = DrawText();
            Check(t3.Contains("GPU 預覽失敗 ⇒ 退回 CPU：模擬的 GPU 失敗") && t3.Contains("[圖：球面預覽]") && !t3.Contains("球面預覽（GPU）"), "畫家回報失敗 ⇒ CPU 圖＋原因");
            // 反向對照：回報的是**舊版**的失敗 ⇒ 不算這一版失敗
            SCP.Core.Gui.SCP_GuiGpuViews.Report(Senate.Cli.Pages.GlobeViewerPage.GpuKey, f.Version - 1, "舊版的失敗", "");
            string t4 = DrawText();
            Check(t4.Contains("球面預覽（GPU）") && !t4.Contains("舊版的失敗"), "舊版的失敗不讓這一版退回 CPU");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally
        {
            if (aWasRegistered) SCP.Core.Gui.SCP_GuiGpuViews.RegisterPainter(aSceneType); else SCP.Core.Gui.SCP_GuiGpuViews.UnregisterPainter(aSceneType);
            SCP.Core.Gui.SCP_GuiGpuViews.Remove(Senate.Cli.Pages.GlobeViewerPage.GpuKey);
            try { Directory.Delete(aTmp, true); } catch { }
        }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow GlobeGpuTileSyncCleanRoom()
    {
        const string name = "球面 GPU 格子快取：只上傳畫過的分塊／再畫一筆只上傳內容變了的那幾塊／同內容重讀零上傳／取色＝畫的顏色、沒畫＝底色、沒打到球＝背景（淨室，TASK-0470）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_globe_gpusync_" + Guid.NewGuid().ToString("N"));
        using var gpu = new SenateGlobeOffscreen();
        try
        {
            Directory.CreateDirectory(aRoot);
            var d = new Dictionary<string, string> { ["data_root"] = aRoot };
            SCP_CmdResult Run(params (string k, string v)[] a)
            {
                var m = new Dictionary<string, string>(d);
                foreach (var (k, v) in a) m[k] = v;
                return SCP_CmdRegistry.Dispatch("globe", m);
            }
            Check(Run(("op", "init"), ("n", "1024")).ExitCode == 0, "init N=1024");   // 分塊 256 ⇒ 每面 4×4、共 96 塊
            Check(Run(("op", "point"), ("persona", "t"), ("lat", "10"), ("lon", "10"), ("radius", "4"), ("color", "#FF0000"), ("unit", "cell")).ExitCode == 0, "畫紅點");
            var store = new SCP_GlobeStore(new SCP_GlobePaths(Path.Combine(aRoot, SCP_GlobePaths.DirName)));
            SCP_GlobeState s1 = store.Load();
            int aTiles1 = s1.Cells.TileKeys.Count();
            var cam = new SCP_GlobeCamera { CenterLat = 10, CenterLon = 10, Zoom = 60, Width = 128, Height = 128 };
            var sc1 = new SCP_GlobeGpuScene(s1, 1, cam, 0, false, new List<SCP_GlobeZone>());
            if (!gpu.TryRender(sc1, SenateGlobeGl.Mode.Color, out byte[] img1, out string err))
            {
                if (gpu.InitError != null) return new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped);
                throw new InvalidOperationException("GPU 渲染失敗（不是沒有 GPU）：" + err);
            }
            var r1 = gpu.LastSync;
            readings.Add("第一次 " + r1);
            Check(r1.Uploaded == aTiles1 && r1.TilesOnGpu == aTiles1 && aTiles1 < 96, $"第一次只上傳畫過的 {aTiles1} 塊（共 96 塊）");

            // 取色：畫面中心＝紅點（有打光 ⇒ 認紅色優勢）；四角離中心很遠但仍在球上 ⇒ 底色（藍色優勢）
            int Px(byte[] a, int x, int y) => (y * 128 + x) * 4;
            int o = Px(img1, 64, 64);
            Check(img1[o] > 120 && img1[o + 1] < 60 && img1[o + 2] < 60, $"中心是紅色（{img1[o]},{img1[o + 1]},{img1[o + 2]}）");
            o = Px(img1, 2, 2);
            Check(img1[o + 2] > img1[o] && img1[o + 2] > 60, $"角落是底色（{img1[o]},{img1[o + 1]},{img1[o + 2]}）");
            var far = new SCP_GlobeGpuScene(s1, 1, new SCP_GlobeCamera { CenterLat = 10, CenterLon = 10, Zoom = 0.5, Width = 128, Height = 128 }, 0, false, new List<SCP_GlobeZone>());
            if (gpu.TryRender(far, SenateGlobeGl.Mode.Color, out byte[] img0, out _))
            {
                o = Px(img0, 0, 0);
                Check(img0[o] == 12 && img0[o + 1] == 14 && img0[o + 2] == 22, $"沒打到球的角落＝背景色（{img0[o]},{img0[o + 1]},{img0[o + 2]}）");
            }

            // 同一個 State、同一版 ⇒ 什麼都不做
            gpu.TryRender(sc1, SenateGlobeGl.Mode.Color, out _, out _);
            Check(gpu.LastSync.Uploaded == r1.Uploaded && ReferenceEquals(gpu.LastSync, r1), "同一個 State 同一版 ⇒ 不重新同步");

            // 重讀一份內容相同的 State（新物件、新版本）⇒ 逐塊比對後零上傳 —— 這是「不整份重傳」的反向對照：
            // 若同步是「換物件就全傳」，這裡會上傳 aTiles1 塊
            SCP_GlobeState s1b = store.Load();
            gpu.TryRender(new SCP_GlobeGpuScene(s1b, 2, cam, 0, false, new List<SCP_GlobeZone>()), SenateGlobeGl.Mode.Color, out _, out _);
            readings.Add("同內容重讀 " + gpu.LastSync);
            Check(gpu.LastSync.Uploaded == 0 && !gpu.LastSync.Rebuilt, "同內容重讀 ⇒ 零上傳");

            // 另一個面再畫一筆 ⇒ 只上傳內容變了的那幾塊（期望值由 CPU 逐塊比對算出，不是猜）
            Check(Run(("op", "point"), ("persona", "t"), ("lat", "-5"), ("lon", "100"), ("radius", "2"), ("color", "#00FF00"), ("unit", "cell")).ExitCode == 0, "畫綠點");
            SCP_GlobeState s2 = store.Load();
            int aExpect = 0;
            foreach (int k in s2.Cells.TileKeys)
                if (!s1.Cells.TileKeys.Contains(k) || !s1.Cells.TileBytes(k).AsSpan().SequenceEqual(s2.Cells.TileBytes(k))) aExpect++;
            gpu.TryRender(new SCP_GlobeGpuScene(s2, 3, cam, 0, false, new List<SCP_GlobeZone>()), SenateGlobeGl.Mode.Color, out _, out _);
            var r2 = gpu.LastSync;
            readings.Add($"再畫一筆（CPU 比對期望 {aExpect} 塊）" + r2);
            Check(aExpect > 0 && r2.Uploaded == aExpect && r2.TilesOnGpu == s2.Cells.TileKeys.Count(), $"再畫一筆只上傳變了的 {aExpect} 塊");

            // Undo 回到只有紅點 ⇒ 綠點那幾塊內容變回全空（分塊仍在 State 裡）⇒ 照樣只上傳那幾塊
            Check(Run(("op", "undo"), ("persona", "t")).ExitCode == 0, "undo");
            SCP_GlobeState s3 = store.Load();
            gpu.TryRender(new SCP_GlobeGpuScene(s3, 4, new SCP_GlobeCamera { CenterLat = -5, CenterLon = 100, Zoom = 60, Width = 128, Height = 128 }, 0, false, new List<SCP_GlobeZone>()),
                          SenateGlobeGl.Mode.Color, out byte[] img3, out _);
            readings.Add("Undo 後 " + gpu.LastSync);
            o = Px(img3, 64, 64);
            Check(img3[o + 1] < img3[o + 2], $"Undo 之後綠點那裡回到底色（{img3[o]},{img3[o + 1]},{img3[o + 2]}）");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(aRoot, true); } catch { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
