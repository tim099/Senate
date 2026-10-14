// 區塊職責：雕刻預覽的海面（TASK-0585）兩組驗收 ——
//   ① 淨室（不用 GPU）：設定疊層（只換高度 ⇒ 開關與顏色沿用下層、unset 一格、不合法的值整層不寫回、不認得的鍵擋）、
//      作品的 sea.json 讀寫一圈不變、壞掉的檔丟例外（⛔ 不默默當成沒有海）。
//   ② GPU：沒開海（Sea＝null）與 Sea.Enabled＝false 的圖逐位元相同；海面放在柱子中間、不透明 ⇒ 柱子的紅色像素變少；
//      反向對照：海面放到柱子底下 ⇒ 紅色像素一顆不少（同一把尺量得出「被蓋住」與「沒被蓋住」）。
//      這台機器建不出 GL 3.3 ⇒ Skipped（⛔ 不是通過）。
using SCP.Core.Json;
using SCP.Core.Sculpture;
using Senate.Desktop;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptSeaProfileCleanRoom()
    {
        const string name = "雕刻海面：設定疊層（只換高度 ⇒ 開關、顏色沿用下層；unset；不合法整層不寫回；不認得的鍵擋）＋作品 sea.json 讀寫一圈、壞檔丟例外（淨室，TASK-0585）";
        var failures = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        string tmp = Path.Combine(Path.GetTempPath(), "senate_sea_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var p = new SCP_SculptRenderParams();
            Check(p.Sea == null, "內建預設不該有海");
            // 下層：開、z 60、自訂色
            var lo = SCP_JsonData.NewObject();
            Check(SCP_SculptRenderProfiles.TryEdit(lo, new Dictionary<string, string> { ["sea"] = "on", ["sea_z"] = "60", ["sea_color"] = "#102030" }, new List<string>(), out string e1), "下層 TryEdit：" + e1);
            Check(SCP_SculptRenderProfiles.TryApply(lo, p, tmp, out e1), "下層 TryApply：" + e1);
            Check(p.Sea is { Enabled: true, Z: 60, R: 0x10, G: 0x20, B: 0x30 }, "下層疊完應是 開、z 60、#102030：" + Describe(p.Sea));
            // 上層只換高度 ⇒ 開關、顏色沿用
            var hi = SCP_JsonData.NewObject();
            SCP_SculptRenderProfiles.TryEdit(hi, new Dictionary<string, string> { ["sea_z"] = "75.5" }, new List<string>(), out _);
            Check(SCP_SculptRenderProfiles.TryApply(hi, p, tmp, out e1) && p.Sea is { Enabled: true, Z: 75.5, R: 0x10 }, "只換高度 ⇒ 開關、顏色要沿用：" + Describe(p.Sea) + " " + e1);
            // 不合法 ⇒ 擋、整層不寫回
            var bad = SCP_JsonData.NewObject();
            SCP_SculptRenderProfiles.TryEdit(bad, new Dictionary<string, string> { ["sea_z"] = "90", ["sea_opacity"] = "1.5" }, new List<string>(), out _);
            Check(!SCP_SculptRenderProfiles.TryApply(bad, p, tmp, out string e2) && e2.Contains("opacity"), "不透明度 1.5 應擋：" + e2);
            Check(p.Sea is { Z: 75.5 }, "🔴 擋下的那一層不該寫回任何一格（z 應還是 75.5）：" + Describe(p.Sea));
            var typo = SCP_JsonData.Parse("{\"sea\":{\"hieght\":3}}");
            Check(!SCP_SculptRenderProfiles.TryApply(typo, p, tmp, out string e3), "sea 物件裡打錯的鍵應擋：" + e3);
            // sea=off ⇒ 關（物件還在、值記得）
            var off = SCP_JsonData.NewObject();
            SCP_SculptRenderProfiles.TryEdit(off, new Dictionary<string, string> { ["sea"] = "off" }, new List<string>(), out _);
            Check(SCP_SculptRenderProfiles.TryApply(off, p, tmp, out _) && p.Sea is { Enabled: false, Z: 75.5 }, "sea=off 應只關開關、值記得：" + Describe(p.Sea));
            // unset 一格
            var un = SCP_JsonData.Parse("{\"sea\":{\"z\":1,\"opacity\":0.5}}");
            Check(SCP_SculptRenderProfiles.TryEdit(un, new Dictionary<string, string> { ["unset"] = "sea_z" }, new List<string>(), out string e4)
                  && !un["sea"]["z"].Exists && un["sea"]["opacity"].Exists, "unset sea_z 應只拿掉 z：" + un.ToJson(false) + " " + e4);

            // 作品的 sea.json：讀寫一圈；沒有檔 ⇒ null；壞檔 ⇒ 例外
            var paths = new SCP_SculptPaths(tmp);
            Check(SCP_SculptRenderProfiles.ReadWorkSea(paths) == null, "沒有 sea.json 應回 null");
            var seaObj = SCP_JsonData.Parse("{\"enabled\":true,\"z\":60,\"opacity\":0.8}");
            SCP_SculptRenderProfiles.SaveWorkSea(paths, seaObj);
            SCP_JsonData? back = SCP_SculptRenderProfiles.ReadWorkSea(paths);
            var q = new SCP_SculptRenderParams();
            Check(back != null && SCP_SculptRenderProfiles.TryApplySeaObject(back, q, out _) && q.Sea is { Enabled: true, Z: 60, Opacity: 0.8 }, "sea.json 讀回不一致：" + back?.ToJson(false));
            File.WriteAllText(paths.SeaFile, "{\"schema\":1}");
            bool threw = false;
            try { SCP_SculptRenderProfiles.ReadWorkSea(paths); } catch (InvalidDataException) { threw = true; }
            Check(threw, "🔴 沒有 sea 物件的檔應丟例外（不默默當成沒有海）");
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        return new(name, failures.Count == 0 ? "疊層 7 格＋sea.json 3 格 全過" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);

        static string Describe(SCP_SculptSea? s) => s == null ? "null" : $"{(s.Enabled ? "開" : "關")} z {s.Z} #{s.R:x2}{s.G:x2}{s.B:x2} 不透明 {s.Opacity}";
    }

    static CheckRow SculptSeaGpuCleanRoom()
    {
        const string name = "雕刻海面（GPU）：沒開海 ＝ Sea.Enabled 關 ⇒ 圖逐位元相同；海面切過柱子、不透明 ⇒ 紅色像素變少；🔴 反向對照：海面放到柱子底下 ⇒ 紅色像素一顆不少（淨室，TASK-0585）";
        var failures = new List<string>();
        var readings = new List<string>();
        void Check(bool v, string why) { if (!v) failures.Add(why); }
        using var r = new SenateSculptRenderer();
        try
        {
            var vox = new List<SCP_SculptVoxel>();
            for (int x = 10; x < 20; x++) for (int y = 10; y < 20; y++) for (int z = 0; z < 20; z++) vox.Add(new SCP_SculptVoxel(x, y, z, 224));   // 紅柱 10×10×20
            var p = new SCP_SculptRenderParams { Width = 256, Height = 256, Skybox = SCP_SculptRenderParams.SkyboxNone, Shadow = false };
            if (!r.TryRender(vox, p, out byte[] aNone, out string err))
                return err.Contains("建不出") ? new CheckRow(name, "這台機器沒有可用的 GPU ⇒ **這是跳過，不是通過**：" + err, CheckResult.Skipped)
                                              : new CheckRow(name, "沒開海就畫不出來：" + err, CheckResult.Fail);
            int Red(byte[] a) { int n = 0; for (int k = 0; k < a.Length; k += 4) if (a[k] > a[k + 1] + 40 && a[k] > a[k + 2] + 40) n++; return n; }

            var pOff = p.Clone(); pOff.Sea = new SCP_SculptSea { Enabled = false, Z = 10, Opacity = 1 };
            Check(r.TryRender(vox, pOff, out byte[] aOff, out err), "Sea 關著畫不出來：" + err);
            Check(aNone.AsSpan().SequenceEqual(aOff), "🔴 Sea.Enabled＝false 的圖應跟沒有海逐位元相同");

            var pCut = p.Clone(); pCut.Sea = new SCP_SculptSea { Enabled = true, Z = 10, Opacity = 1, R = 20, G = 60, B = 200 };
            Check(r.TryRender(vox, pCut, out byte[] aCut, out err), "海面切過柱子畫不出來：" + err);
            var pLow = p.Clone(); pLow.Sea = new SCP_SculptSea { Enabled = true, Z = -5, Opacity = 1, R = 20, G = 60, B = 200 };
            Check(r.TryRender(vox, pLow, out byte[] aLow, out err), "海面在柱子底下畫不出來：" + err);
            // 🩸 第一版數「紅色像素」：海面在柱子底下時少了 48 顆 —— 是輪廓邊緣的 MSAA 混色（背景從深色換成海藍，混出來的色跨過門檻），
            //    不是海蓋住了柱子 ⇒ 只比**內部**像素（沒海時自己＋3×3 鄰格都是紅的）：那些像素在「海在底下」時要逐位元不變
            bool IsRed(byte[] a, int k) => a[k] > a[k + 1] + 40 && a[k] > a[k + 2] + 40;
            var inner = new List<int>();
            for (int y = 1; y < p.Height - 1; y++)
                for (int x = 1; x < p.Width - 1; x++)
                {
                    bool all = true;
                    for (int dy = -1; dy <= 1 && all; dy++) for (int dx = -1; dx <= 1 && all; dx++) all = IsRed(aNone, ((y + dy) * p.Width + x + dx) * 4);
                    if (all) inner.Add((y * p.Width + x) * 4);
                }
            int SameAsNone(byte[] a) { int n = 0; foreach (int k in inner) if (a[k] == aNone[k] && a[k + 1] == aNone[k + 1] && a[k + 2] == aNone[k + 2]) n++; return n; }
            int n0 = Red(aNone), nCut = Red(aCut), nLow = Red(aLow), sCut = SameAsNone(aCut), sLow = SameAsNone(aLow);
            readings.Add($"紅色像素：沒海 {n0}／海面 z 10（切過）{nCut}／海面 z −5（底下）{nLow}；內部像素 {inner.Count} 顆跟沒海逐位元相同：切過 {sCut}、底下 {sLow}");
            Check(inner.Count > 1000, "場景裡的內部紅色像素太少，這把尺量不到東西：" + inner.Count);
            Check(nCut < n0 * 0.8 && sCut < inner.Count * 0.8, "海面切過柱子、不透明 ⇒ 紅色像素與不變的內部像素都應明顯變少（< 80%）：" + nCut + "／" + n0 + "、" + sCut + "／" + inner.Count);
            Check(sLow == inner.Count, "🔴 反向對照：海面在柱子底下 ⇒ 內部像素應一顆不變：" + sLow + "／" + inner.Count);
        }
        catch (Exception e) { failures.Add(e.GetType().Name + "：" + e.Message); }
        string aRead = string.Join("；", readings);
        return new(name, failures.Count == 0 ? aRead + " 全過" : string.Join("；", failures) + (aRead.Length > 0 ? "｜" + aRead : ""),
                   failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
