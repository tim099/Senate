// 區塊職責：雕刻預覽的平滑渲染＋材質反光（TASK-0645）兩組驗收 ——
//   ① 淨室（不用 GPU）：
//      · 整塊平滑 ⇒ 網格封閉（每條邊恰好兩個三角形用）；反向對照：拿掉一個三角形 ⇒ 尺要量到開口。
//      · 半平滑、半方塊 ⇒ 平滑面的邊界那一圈跟方塊面的邊界那一圈逐條相同（交界不露縫）；反向對照：邊界點挪 0.1 格 ⇒ 尺要量到對不上。
//      · Voxels(…, null) 跟不帶參數的 digest 逐位元相同；帶 Surface ⇒ 方塊面變少（平滑格的面不出）。
//      · 材質表：合法的讀得到；壞 JSON、schema 不對、值越界、不認得的鍵、ID 大寫 ⇒ InvalidDataException；
//        組裝讀數：表裡沒有的 ID 進 Unknown、render=voxel 不算平滑、沒有平滑格 ⇒ null。
//   ② GPU：金屬度 1 跟 0 的圖要量得出差異（反向對照：同一份畫兩次 ⇒ 0 差異，尺不是永遠喊不同）；
//      平滑 vs 不平滑的輪廓不同；讀數的平滑三角形數。這台機器建不出 GL 3.3 ⇒ Skipped（⛔ 不是通過）。
//   ⚠ 「沒有 meta 的作品跟改之前逐位元相同」這一格要兩顆 exe（改之前的那顆）—— 淨室做不到，讀數記在單上（新舊 exe 的 sha256）。
using System.Text;
using SCP.Core.Json;
using SCP.Core.Sculpture;
using Senate.Desktop;

namespace Senate.Cli;

public static partial class SelfTest
{
    static List<SCP_SculptVoxel> SmoothBlock(int iNx, int iNy, int iNz, byte iColor)
    {
        var a = new List<SCP_SculptVoxel>();
        for (int x = 0; x < iNx; x++) for (int y = 0; y < iNy; y++) for (int z = 0; z < iNz; z++) a.Add(new SCP_SculptVoxel(x, y, z, iColor));
        return a;
    }

    /// <summary>每條邊（照位置，四捨五入到 1e-4）被幾個三角形用。stride ＝ 每頂點幾個 float。</summary>
    static Dictionary<string, int> EdgeUse(float[] iV, uint[] iI, int iStride, int iSkipTri = -1)
    {
        string P(uint k) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F4},{1:F4},{2:F4}", iV[k * iStride], iV[k * iStride + 1], iV[k * iStride + 2]);
        var d = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int t = 0; t + 2 < iI.Length; t += 3)
        {
            if (t / 3 == iSkipTri) continue;
            for (int e = 0; e < 3; e++)
            {
                string a = P(iI[t + e]), b = P(iI[t + (e + 1) % 3]);
                string k = string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;
                d[k] = d.TryGetValue(k, out int n) ? n + 1 : 1;
            }
        }
        return d;
    }

    static CheckRow SculptSmoothMeshCleanRoom()
    {
        const string name = "雕刻平滑渲染：surface nets 網格封閉、平滑面跟方塊面的交界逐條相同（不露縫）、Surface＝null 的 digest 不變、材質表壞檔大聲擋（淨室，TASK-0645）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        string tmp = Path.Combine(Path.GetTempPath(), "senate_smooth_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // ── 整塊平滑：封閉 ──
            var cube = SmoothBlock(6, 6, 6, 146);
            SCP_SculptMesh m = SCP_SculptSmoothMesh.Build(cube, SCP_SculptSurface.AllSmooth(0, 0.5f));
            var use = EdgeUse(m.Vertices, m.Indices, SCP_SculptSmoothMesh.FloatsPerVertex);
            int open = use.Values.Count(n => n != 2);
            int moved = 0;
            for (int i = 0; i < m.VertexCount; i++)
                for (int k = 0; k < 3; k++) { float f = m.Vertices[i * 8 + k]; if (Math.Abs(f - MathF.Round(f)) > 1e-4) { moved++; break; } }
            readings.Add($"6³ 整塊平滑：{m.VertexCount} 頂點、{m.IndexCount / 3} 三角形、外露面 {m.FaceCount}、邊 {use.Count} 條（不是剛好兩個三角形用的 {open}）、離開格角的頂點 {moved}");
            Check(m.FaceCount == 6 * 36, "6³ 外露面應是 216：" + m.FaceCount);
            Check(open == 0, "整塊平滑的網格應封閉：有 " + open + " 條邊不是剛好兩個三角形用");
            Check(moved > 0, "平滑面的頂點應有離開格角的（不然等於沒平滑）");
            var useBad = EdgeUse(m.Vertices, m.Indices, SCP_SculptSmoothMesh.FloatsPerVertex, iSkipTri: 0);
            Check(useBad.Values.Count(n => n != 2) == 3, "🔴 反向對照：拿掉一個三角形 ⇒ 尺應量到 3 條開口的邊：" + useBad.Values.Count(n => n != 2));

            // ── 半平滑、半方塊：交界不露縫 ──
            var half = SmoothBlock(8, 4, 4, 146);
            var surf = new SCP_SculptSurface();
            foreach (var v in half) if (v.X < 4) surf.Smooth.Add(SCP_SculptSurface.Key(v.X, v.Y, v.Z));
            SCP_SculptMesh ms = SCP_SculptSmoothMesh.Build(half, surf);
            SCP_SculptMesh mc = SCP_SculptMeshBuilder.Voxels(half, false, false, surf);
            var bs = EdgeUse(ms.Vertices, ms.Indices, SCP_SculptSmoothMesh.FloatsPerVertex).Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            var bc = EdgeUse(mc.Vertices, mc.Indices, SCP_SculptMesh.FloatsPerVertex).Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            readings.Add($"8×4×4 半平滑：平滑面 {ms.IndexCount / 3} 三角形（邊界 {bs.Count} 條）、方塊面 {mc.FaceCount} 面（邊界 {bc.Count} 條）");
            Check(bs.Count > 0 && bs.SetEquals(bc), $"平滑面與方塊面的邊界應逐條相同：平滑 {bs.Count}、方塊 {bc.Count}、共同 {bs.Intersect(bc).Count()}");
            Check(mc.FaceCount == 4 * 4 + 2 * (4 * 4) + 2 * (4 * 4), "方塊那半只出自己的外露面（4×4 端面＋四個側面各 4×4）：" + mc.FaceCount);
            // 反向對照：平滑面的邊界點挪 0.1 格 ⇒ 邊界對不上
            float[] shifted = (float[])ms.Vertices.Clone();
            for (int i = 0; i < ms.VertexCount; i++) if (Math.Abs(shifted[i * 8] - 4f) < 1e-4) { shifted[i * 8] += 0.1f; }
            var bsBad = EdgeUse(shifted, ms.Indices, SCP_SculptSmoothMesh.FloatsPerVertex).Where(kv => kv.Value == 1).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);
            Check(!bsBad.SetEquals(bc), "🔴 反向對照：交界那一圈挪 0.1 格 ⇒ 尺應量到對不上");

            // ── Surface ＝ null ⇒ 逐位元不變；帶 Surface ⇒ 面變少 ──
            ulong d0 = SCP_SculptMeshBuilder.Voxels(half, true, true).Digest, d1 = SCP_SculptMeshBuilder.Voxels(half, true, true, null).Digest;
            int f0 = SCP_SculptMeshBuilder.Voxels(half, false, false).FaceCount;
            readings.Add($"digest 不帶參數 {d0:x16}／null {d1:x16}；方塊外露面 {f0} → 帶 Surface {mc.FaceCount}");
            Check(d0 == d1, "Surface＝null 的網格 digest 應跟不帶參數相同");
            Check(mc.FaceCount < f0, "帶 Surface ⇒ 平滑格的方塊面不出：" + f0 + " → " + mc.FaceCount);

            // ── 材質表 ──
            var good = SCP_SculptMaterials.Parse(SCP_JsonData.Parse("{\"schema\":1,\"list\":{\"iron\":{\"name\":\"鐵\",\"metal\":1,\"rough\":0.35},\"oak\":{\"metal\":0}}}"), "淨室");
            Check(good.TryGet("iron", out float gm, out float gr) && gm == 1 && Math.Abs(gr - 0.35f) < 1e-6, "iron 應讀到 1／0.35");
            Check(good.TryGet("oak", out _, out float orr) && orr == 0.5f, "沒寫 rough ⇒ 0.5");
            Check(!good.TryGet("gold", out _, out _), "表外的 ID 應回 false");
            string[] bads =
            {
                "{\"schema\":2,\"list\":{}}",
                "{\"schema\":1,\"list\":{\"iron\":{\"metal\":1.5}}}",
                "{\"schema\":1,\"list\":{\"iron\":{\"metl\":1}}}",
                "{\"schema\":1,\"list\":{\"Iron\":{\"metal\":1}}}",
                "{\"schema\":1,\"lst\":{}}",
                "{\"schema\":1,\"list\":{\"iron\":{\"metal\":\"1\"}}}",
            };
            int aBlocked = 0;
            foreach (string b in bads)
            {
                try { SCP_SculptMaterials.Parse(SCP_JsonData.Parse(b), "淨室"); failures.Add("壞表應擋：" + b); }
                catch (InvalidDataException) { aBlocked++; }
            }
            Directory.CreateDirectory(tmp);
            Check(SCP_SculptMaterials.Load(tmp).List.Count == 0, "沒有 materials.json ⇒ 空表");
            File.WriteAllText(Path.Combine(tmp, SCP_SculptMaterials.FileName), "{ not json", Encoding.UTF8);
            bool threw = false;
            try { SCP_SculptMaterials.Load(tmp); } catch (InvalidDataException) { threw = true; }
            Check(threw, "壞 JSON 應丟 InvalidDataException（⛔ 不默默當成沒有表）");
            readings.Add($"材質表壞檔 {aBlocked + (threw ? 1 : 0)}／{bads.Length + 1} 擋下");

            // ── 組裝讀數 ⇒ Surface ──
            var vis = new List<SCP_SculptVoxel> { new(0, 0, 0, 1), new(1, 0, 0, 1), new(2, 0, 0, 1), new(3, 0, 0, 1) };
            var ren = new Dictionary<long, string> { [SCP_SculptSurface.Key(0, 0, 0)] = "smooth", [SCP_SculptSurface.Key(1, 0, 0)] = "smooth", [SCP_SculptSurface.Key(2, 0, 0)] = "voxel" };
            var mat = new Dictionary<long, string> { [SCP_SculptSurface.Key(0, 0, 0)] = "iron", [SCP_SculptSurface.Key(1, 0, 0)] = "gold", [SCP_SculptSurface.Key(3, 0, 0)] = "iron" };
            SCP_SculptSurface? s = SCP_SculptSurface.FromCompose(vis, ren, mat, good);
            Check(s != null && s.Smooth.Count == 2, "render=smooth 兩格、voxel 那格不算：" + s?.Smooth.Count);
            Check(s != null && s.Mat.ContainsKey(SCP_SculptSurface.Key(0, 0, 0)) && !s.Mat.ContainsKey(SCP_SculptSurface.Key(1, 0, 0)), "iron 有材質、gold 沒有");
            Check(s != null && s.Unknown.Contains("gold") && s.Unknown.Count == 1, "表裡沒有的 gold 應進 Unknown：" + string.Join(",", s?.Unknown ?? new SortedSet<string>()));
            Check(s != null && s.MaterialOnVoxel == 1, "有材質但不平滑的應數到 1 格：" + s?.MaterialOnVoxel);
            Check(SCP_SculptSurface.FromCompose(vis, new Dictionary<long, string> { [SCP_SculptSurface.Key(2, 0, 0)] = "voxel" }, mat, good) == null, "沒有平滑格 ⇒ null");
            Check(SCP_SculptSurface.FromCompose(vis, null, mat, good) == null, "沒有 render 讀數 ⇒ null");
            readings.Add("組裝讀數：" + (s?.Describe() ?? "null"));
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch (IOException) { } }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }

    static CheckRow SculptSmoothGpuCleanRoom()
    {
        const string name = "雕刻平滑渲染 GPU：平滑 vs 方塊的輪廓不同、金屬度 1 vs 0 量得出差異（反向對照：同一份畫兩次差 0）、讀數有平滑三角形（淨室，TASK-0645）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool c, string what) { if (!c) failures.Add(what); }
        using var r = new SenateSculptRenderer();
        try
        {
            // 地板＋一顆半徑 9 的球（球那幾格平滑）
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 0; x < 40; x++) for (int y = 0; y < 40; y++) vox.Add(new SCP_SculptVoxel(x, y, 0, 146));
            var ball = new List<long>();
            for (int x = 0; x < 40; x++) for (int y = 0; y < 40; y++) for (int z = 1; z < 20; z++)
                    {
                        double dx = x + 0.5 - 20, dy = y + 0.5 - 20, dz = z + 0.5 - 10;
                        if (dx * dx + dy * dy + dz * dz <= 81) { vox.Add(new SCP_SculptVoxel(x, y, z, 182)); ball.Add(SCP_SculptSurface.Key(x, y, z)); }
                    }
            SCP_SculptSurface Surf(float iMetal)
            {
                var s = new SCP_SculptSurface();
                foreach (long k in ball) { s.Smooth.Add(k); if (iMetal > 0) s.Mat[k] = (iMetal, 0.35f); }
                return s;
            }
            var p = new SCP_SculptRenderParams { Width = 384, Height = 384, AmbientOcclusion = true, Shadow = true, Skybox = SCP_SculptRenderParams.SkyboxNone };
            if (!r.TryRender(vox, p, out byte[] aCube, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "方塊畫法就失敗了：" + err, CheckResult.Fail);
            Check(r.LastReading.SmoothTriangles == 0, "沒有 Surface ⇒ 平滑三角形 0：" + r.LastReading.SmoothTriangles);
            var p0 = p.Clone(); p0.Surface = Surf(0);
            Check(r.TryRender(vox, p0, out byte[] aSm0, out err), "平滑（金屬度 0）失敗：" + err);
            var rd0 = r.LastReading;
            var p1 = p.Clone(); p1.Surface = Surf(1);
            Check(r.TryRender(vox, p1, out byte[] aSm1, out err), "平滑（金屬度 1）失敗：" + err);
            var p1b = p.Clone(); p1b.Surface = Surf(1);
            Check(r.TryRender(vox, p1b, out byte[] aSm1b, out err), "平滑（金屬度 1，第二次）失敗：" + err);
            double dShape = PixelDiffFraction(aCube, aSm0, out int nShape);
            double dMetal = PixelDiffFraction(aSm0, aSm1, out int nMetal);
            double dSame = PixelDiffFraction(aSm1, aSm1b, out int nSame);
            readings.Add($"平滑 {rd0.SmoothVoxels:N0} 格 {rd0.SmoothTriangles:N0} 三角形；方塊 vs 平滑 差 {nShape} 像素（{dShape:P2}）；金屬度 0 vs 1 差 {nMetal}（{dMetal:P2}）；同一份畫兩次差 {nSame}");
            Check(rd0.SmoothVoxels == ball.Count && rd0.SmoothTriangles > 0, $"讀數應是 {ball.Count} 格平滑、三角形 > 0：{rd0.SmoothVoxels}／{rd0.SmoothTriangles}");
            Check(nShape > 0, "平滑與方塊的圖應不同（不然平滑面沒畫上去）");
            Check(nMetal > 0, "金屬度 1 與 0 的圖應不同（不然材質沒進著色器）");
            Check(nSame == 0, "🔴 反向對照：同一份畫兩次應逐像素相同（尺不是永遠喊不同）：" + nSame);
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return failures.Count == 0 ? new CheckRow(name, aRead, CheckResult.Pass)
            : new CheckRow(name, "失敗：" + string.Join("、", failures) + "　讀數：" + aRead, CheckResult.Fail);
    }
}
