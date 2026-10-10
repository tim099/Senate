// 區塊職責：雕刻即時預覽（TASK-0472）的淨室驗收 —— 合併同色面的畫面對拍（含反向對照）、網格快取的重建判準、檢視倍率。
// 物理意義：合併只該改「三角形怎麼切」，不該改「畫面長什麼樣」⇒ 拿合併與不合併兩張圖逐像素比；
//          反向對照：合併時不看四角 AO（DebugMergeIgnoreAo）⇒ 角落陰影被抹平 ⇒ 同一把尺必須量得出差異。
//          場景刻意做成 AO 漸層很多的形狀（地板＋台階＋L 形牆），不然「AO 有沒有被抹平」這一格量不到。
//          這台機器建不出 GL 3.3 ⇒ Skipped（⛔ 不是通過）。
using SCP.Core.Sculpture;
using Senate.Desktop;

namespace Senate.Cli;

public static partial class SelfTest
{
    /// <summary>合併 vs 不合併：某個通道差超過這麼多才算「不一樣的像素」（MSAA 邊緣的 ±1～2 不算）。</summary>
    const int SculptMergePixelTol = 8;
    /// <summary>合併後不一樣的像素比例上限（T 形接點在 MSAA 下偶爾差一兩點）。</summary>
    const double SculptMergeDiffLimit = 0.005;

    static List<SCP_SculptVoxel> SculptAoScene()
    {
        var a = new List<SCP_SculptVoxel>();
        for (int x = 0; x < 40; x++) for (int y = 0; y < 40; y++) a.Add(new SCP_SculptVoxel(x, y, 0, 146));          // 地板
        for (int k = 0; k < 6; k++)                                                                                  // 台階
            for (int x = 6 + k * 3; x < 30; x++) for (int y = 8; y < 14; y++) a.Add(new SCP_SculptVoxel(x, y, 1 + k, 228));
        for (int z = 1; z < 12; z++)                                                                                 // L 形牆
        {
            for (int x = 4; x < 36; x++) a.Add(new SCP_SculptVoxel(x, 30, z, 37));
            for (int y = 18; y < 30; y++) a.Add(new SCP_SculptVoxel(4, y, z, 37));
        }
        for (int x = 18; x < 22; x++) for (int y = 20; y < 24; y++) for (int z = 1; z < 5; z++) a.Add(new SCP_SculptVoxel(x, y, z, 200));   // 小方塊
        return a;
    }

    static double PixelDiffFraction(byte[] a, byte[] b, out int oDiff)
    {
        oDiff = 0;
        for (int k = 0; k < a.Length; k += 4)
            if (Math.Abs(a[k] - b[k]) > SculptMergePixelTol || Math.Abs(a[k + 1] - b[k + 1]) > SculptMergePixelTol || Math.Abs(a[k + 2] - b[k + 2]) > SculptMergePixelTol) oDiff++;
        return (double)oDiff / (a.Length / 4);
    }

    static CheckRow SculptMergeParityCleanRoom()
    {
        const string name = "雕刻合併同色面：合併後的畫面與逐面畫法逐像素對拍（差異 ≤ 0.5%）＋三角形數大減＋反向對照：合併時不看四角 AO ⇒ 必須量得出差異（淨室，TASK-0472）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = SculptAoScene();
            var p = new SCP_SculptRenderParams { Width = 512, Height = 512, AmbientOcclusion = true, Shadow = true, Skybox = SCP_SculptRenderParams.SkyboxNone };
            if (!r.TryRender(vox, p, out byte[] aFlat, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "逐面畫法就失敗了：" + err, CheckResult.Fail);
            var r0 = r.LastReading;
            var pm = p.Clone(); pm.MergeFaces = true;
            Check(r.TryRender(vox, pm, out byte[] aMerged, out err), "合併畫法失敗：" + err);
            var r1 = r.LastReading;
            double aFrac = PixelDiffFraction(aFlat, aMerged, out int aDiff);
            readings.Add($"逐面 {r0.Triangles:N0} 三角形 → 合併 {r1.Triangles:N0}（外露面 {r1.Faces:N0} 併成 {r1.Quads:N0} 塊）；畫面差異 {aDiff} 像素（{aFrac:P3}）；網格 digest 逐面 {r0.MeshDigest:x16}／合併 {r1.MeshDigest:x16}");
            Check(r0.Faces == r1.Faces && r0.Quads == r0.Faces, "兩種畫法的外露面數相同、逐面時塊數＝面數");
            Check(r1.Triangles * 3 < r0.Triangles, "合併後三角形至少少到三分之一");
            Check(aFrac <= SculptMergeDiffLimit, $"合併後畫面差異 {aFrac:P3} > {SculptMergeDiffLimit:P1}");

            // 反向對照：不看 AO 就併 ⇒ 角落陰影被抹平
            SenateSculptRenderer.DebugMergeIgnoreAo = true;
            try
            {
                var pr = p.Clone(); pr.MergeFaces = true;
                var vox2 = SculptAoScene();   // 新清單物件 ⇒ 一定重建（DebugMergeIgnoreAo 不在快取鍵裡）
                Check(r.TryRender(vox2, pr, out byte[] aBad, out err), "反向對照畫法失敗：" + err);
                double aBadFrac = PixelDiffFraction(aFlat, aBad, out int aBadDiff);
                readings.Add($"反向對照（不看 AO 就併）：差異 {aBadDiff} 像素（{aBadFrac:P2}）、{r.LastReading.Quads:N0} 塊");
                // 判準＝「上一格那一句 check 換成這張圖會失敗」⇒ 超過同一個上限就夠（⛔ 不另訂一個更嚴的數，那是兩把尺）
                Check(aBadFrac > SculptMergeDiffLimit, $"反向對照只差 {aBadFrac:P3}（≤ {SculptMergeDiffLimit:P1}）⇒ 這把尺量不出 AO 被抹平");
            }
            finally { SenateSculptRenderer.DebugMergeIgnoreAo = false; }
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    /// <summary>
    /// 真資料讀數（TASK-0472）：小木屋（meadow-cozy-cabin）照觀測頁的路準備場景（Cmd_Sculpture.TryPrepareView），
    /// 第一張建網格、之後換三次 yaw ⇒ 印每一張的時間與網格有沒有重建。⚠ 是讀數不是淨室：作品不在 ⇒ Skipped。
    /// </summary>
    static CheckRow SculptGpuRealWorkReading()
    {
        const string name = "雕刻即時預覽真資料讀數：小木屋準備場景／建網格（合併）／換 yaw 三次每張的時間（TASK-0472）";
        IReadOnlyList<SelfTestTarget> aTargets = RealTargets();
        string aData = aTargets.Count > 0 ? aTargets[0].AgentCommandsRoot ?? "" : "";
        if (aData.Length == 0 || !Directory.Exists(Path.Combine(aData, "Sculpture", "works", "meadow-cozy-cabin")))
            return new CheckRow(name, "找不到小木屋作品 ⇒ **這是跳過，不是通過**", CheckResult.Skipped);
        using var r = new SenateSculptRenderer();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (!Senate.Core.Cmd_Sculpture.TryPrepareView(new Dictionary<string, string> { ["data_root"] = aData, ["work"] = "meadow-cozy-cabin" },
                                                      out SCP_SculptViewPlan aPlan, out _, out string aErr))
            return new CheckRow(name, "準備場景失敗：" + aErr, CheckResult.Fail);
        double aPrepMs = sw.Elapsed.TotalMilliseconds;
        var readings = new List<string> { $"準備場景 {aPrepMs:0} ms（{aPlan.Voxels.Count:N0} voxel）" };
        var p = aPlan.Params.Clone(); p.MergeFaces = true; p.Width = 720; p.Height = 720;
        bool aOk = true;
        foreach (double aYaw in new[] { p.YawDeg, p.YawDeg + 30, p.YawDeg + 60, p.YawDeg + 90 })
        {
            var q = p.Clone(); q.YawDeg = aYaw;
            var t = System.Diagnostics.Stopwatch.StartNew();
            if (!r.TryRender(aPlan.Voxels, q, out _, out aErr))
                return aErr.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + aErr, CheckResult.Skipped)
                                               : new CheckRow(name, "渲染失敗：" + aErr, CheckResult.Fail);
            readings.Add($"yaw {aYaw:0}：{t.Elapsed.TotalMilliseconds:0} ms（含讀回）—— {r.LastReading}");
            if (aYaw != p.YawDeg && r.LastReading.MeshRebuilt) aOk = false;
        }
        return new CheckRow(name, string.Join("；", readings) + (aOk ? "" : "　✗ 換 yaw 卻重建了網格"), aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    static CheckRow SculptMeshCacheCleanRoom()
    {
        const string name = "雕刻網格快取：同一份 voxel 換視角不重建／換清單物件重建／同一個清單改了內容也重建（防拿到舊網格）／換合併旗標重建／ViewScale 放大（淨室，TASK-0472）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = SculptAoScene();
            var p = new SCP_SculptRenderParams { Width = 256, Height = 256, Skybox = SCP_SculptRenderParams.SkyboxNone };
            if (!r.TryRender(vox, p, out byte[] a1, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "第一張就失敗了：" + err, CheckResult.Fail);
            Check(r.LastReading.MeshRebuilt, "第一張要建網格");
            var p2 = p.Clone(); p2.YawDeg = 120; p2.PitchDeg = 50;
            r.TryRender(vox, p2, out byte[] a2, out _);
            Check(!r.LastReading.MeshRebuilt, "同一份 voxel 換視角 ⇒ 沿用網格");
            Check(!a1.AsSpan().SequenceEqual(a2), "換了視角畫面真的變了（不是沿用了舊圖）");
            readings.Add("換視角：" + r.LastReading);

            r.TryRender(SculptAoScene(), p, out _, out _);
            Check(r.LastReading.MeshRebuilt, "換一個清單物件 ⇒ 重建");
            var vox3 = SculptAoScene();
            r.TryRender(vox3, p, out _, out _);
            vox3[0] = new SCP_SculptVoxel(39, 39, 20, 224);   // 同一個清單物件、內容改了
            r.TryRender(vox3, p, out _, out _);
            Check(r.LastReading.MeshRebuilt, "同一個清單改了內容 ⇒ 重建（只比物件會拿到舊網格而不報錯）");
            var pm = p.Clone(); pm.MergeFaces = true;
            r.TryRender(vox3, pm, out _, out _);
            Check(r.LastReading.MeshRebuilt, "換合併旗標 ⇒ 重建");

            // 「作品佔幾個像素」＝ 跟一張空場景（同參數、沒有 voxel）比。⚠ 沒有天空時背景是垂直漸層，不是單色 ——
            //   第一版拿 (15,23,42) 當背景色，數出來 65536／65536（全部都「不是背景」）
            r.TryRender(new List<SCP_SculptVoxel>(), p, out byte[] aBg, out _);
            int Lit(byte[] a) { int n = 0; for (int k = 0; k < a.Length; k += 4) if (Math.Abs(a[k] - aBg[k]) + Math.Abs(a[k + 1] - aBg[k + 1]) + Math.Abs(a[k + 2] - aBg[k + 2]) > 6) n++; return n; }
            var pz = p.Clone(); pz.ViewScale = 2;
            r.TryRender(vox, pz, out byte[] aZ, out _);
            int n1 = Lit(a1), nz = Lit(aZ);
            readings.Add($"ViewScale 1 → 2：非背景像素 {n1} → {nz}");
            Check(nz > n1 * 1.5, "ViewScale 2 ⇒ 作品佔的像素明顯變多");
            var pbad = p.Clone(); pbad.ViewScale = 0;
            Check(!r.TryRender(vox, pbad, out _, out string aBadErr) && aBadErr.Contains("ViewScale"), "ViewScale 0 ⇒ 擋下並說原因");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
