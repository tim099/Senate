// 區塊職責：`sculpture op=export format=glb`（TASK-0510）—— 子模型各自成物件、同一份來源共用一份 mesh 的 glTF 2.0 二進位。
// 物理意義：場景跟觀測頁 GPU 的分開畫（view parts=1，TASK-0509）同一份準備：主作品只帶自己的格（引擎 Links=null）、
//          關聯樹每個節點＋每個外部模型是實例（SCP_SculptParts）；這次出哪幾個照 links=／hide_links=／models=／hide_models= 算（Visibility，跟頁面同一支）。
//          繩與帆照 view 的算法（ResolveViewRig）⇒ 世界座標網格各一個 node。寫檔在 SCP_SculptGlb。
// 數值影響：不改任何作品檔；新格式 ⇒ obj／vox／list／vxk 的輸出一位元組都不動。
#nullable enable
using System.Globalization;
using SCP.Core.Io;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static string? ExportGlb(Ctx c)
    {
        if (c.Work == null) return Blocked(c, 2, "export format=glb 要 work=");
        if (c.Args.Get("region").Trim().Length > 0 || c.Args.Get("exclude_color").Trim().Length > 0)
            return Blocked(c, 2, "glb 是整件作品（子模型各自成物件）—— 不吃 region／exclude_color");
        string aModelsArg = c.Args.Get("models").Trim();
        if (aModelsArg.Length > 0 && aModelsArg != "0" && aModelsArg != "1") return Blocked(c, 2, "models 要是 1 或 0（got '" + aModelsArg + "'）");
        SCP_SculptWork aCard = c.Work;
        string aOut = c.Args.Get("out").Trim();
        if (aOut.Length == 0)
        {
            string aDir = c.Args.Get("out_dir").Trim();
            if (aDir.Length == 0) aDir = c.Works.SpacePaths(aCard.id).Exports;
            aOut = Path.Combine(aDir, aCard.id + "_" + DateTime.Now.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture) + ".glb");
        }

        // 繩與帆（同 view／obj：照關聯選擇、模擬到 sail_seconds 那一刻）
        if (ResolveViewRig(c, out SCP_SculptSailRig? aRig) is string aRigBad) return Blocked(c, 2, aRigBad);
        var aIn = new SCP_SculptGlbInput { WorkId = aCard.id, Title = aCard.title, MetersPerVoxel = aCard.MetersPerVoxel, Parts = new SCP_SculptParts() };
        if (aRig != null)
        {
            AppendRopeReport(c, aRig.Ropes);
            AppendSailReport(c, aRig);
            aIn.Ropes = SCP_SculptRopes.Drawable(aRig.Ropes);
            aIn.Sails = aRig.SailDrawGeoms();
        }

        try
        {
            using (LockWorks(c))   // 作品＋關聯樹上所有作品，依 ID 排序（TASK-0494）
            {
                SCP_SculptEngine aEngine = NewEngine(c);
                aEngine.Links = null;   // 主作品只帶自己的格；關聯改成實例
                foreach (var v in aEngine.LoadView().Voxels.Entries()) aIn.Own.Add((v.X, v.Y, v.Z, v.Color));
                if (c.LinkTree != null) aIn.Parts.AddLinks(c.Works, c.LinkTree, c.Links?.UseDrafts ?? c.Draft);
            }
        }
        catch (SCP_FileLockTimeoutException e) { return Blocked(c, 4, "拿不到雕刻鎖：" + e.Message); }
        catch (ArgumentException e) { return Blocked(c, 2, "子模型：" + e.Message); }
        catch (SCP_SculptReplayException e) { return Blocked(c, 1, "重播失敗：" + e.Message); }
        // 外部模型：每一層（含設為關的關聯裡的）都讀網格、⛔ 不烘（同 view parts=1）；出不出由 Visibility 決定
        try
        {
            SCP_SculptModelSet aSet = SCP_SculptModels.Resolve(c.Works, aCard.id, c.LinkTree, new SCP_SculptLinkSelection { All = true },
                                                               new SCP_SculptModelSelection(), true, ModelCacheProducer(c), null, false);
            aIn.Parts.AddModels(aSet);
        }
        catch (InvalidOperationException e) { return Blocked(c, 2, "模型：" + e.Message); }

        SCP_SculptLinkSelection aSel = c.Links?.Selection ?? SCP_SculptLinkSelection.Parse(c.Args.Get("links"), c.Args.Get("hide_links"));
        SCP_SculptParts.Shown aShown = SCP_SculptParts.Visibility(aIn.Parts, aSel, SCP_SculptModelSelection.Parse(aModelsArg, c.Args.Get("hide_models").Trim()));
        if (aShown.Unknown.Count > 0) return Blocked(c, 2, "選擇裡點名了這件沒有的：" + string.Join("、", aShown.Unknown));
        aIn.DrawOwn = aShown.Own;
        aIn.Visible = aShown.Visible;

        byte[]? aGlb = SCP_SculptGlb.Build(aIn, out SCP_SculptGlbStats aStats, out string aErr);
        if (aGlb == null) return Blocked(c, 1, "glb：" + aErr);
        string? aParent = Path.GetDirectoryName(Path.GetFullPath(aOut));
        if (!string.IsNullOrEmpty(aParent)) Directory.CreateDirectory(aParent);
        File.WriteAllBytes(aOut, aGlb);
        string aSha = SCP_SculptPy.Sha256Hex(aGlb);

        string aPath = Path.GetFullPath(aOut).Replace('\\', '/');
        int aShared = 0; foreach (var kv in aStats.Uses) if (kv.Value > 1) aShared++;
        c.Report.Append("# 📦 GLB 匯出完成（glTF 2.0；子模型各自成物件，TASK-0510）\n")
                .Append("- 檔案：`").Append(aPath).Append("`（").Append(aGlb.Length).Append(" bytes）\n")
                .Append("- node ").Append(aStats.Nodes).Append("（根 1 ＋ 主作品 ").Append(aIn.DrawOwn && aIn.Own.Count > 0 ? 1 : 0)
                .Append(" ＋ 子模型實例 ").Append(aStats.Instances).Append(" ＋ 繩 ").Append(aStats.Ropes).Append(" ＋ 帆 ").Append(aStats.Sails).Append("）")
                .Append("／mesh ").Append(aStats.Meshes).Append("（被兩個以上 node 共用的 ").Append(aShared).Append(" 份）／材質 ").Append(aStats.Materials)
                .Append("／三角形 ").Append(aStats.Triangles).Append('\n')
                .Append("- 座標：根 node ＝ 雕刻 (x, y, z) → glTF (x, z, y) × ").Append(aCard.MetersPerVoxel.ToString("0.######", CultureInfo.InvariantCulture)).Append(" m／格（y-up、公尺）\n")
                .Append("- ⚠ 子模型畫自己的全部格子（貼合處不互相剔除、重疊處兩份都在；同觀測頁分開畫）；封閉空腔的面不省略\n");
        foreach (var kv in aStats.Uses) if (kv.Value > 1) c.Report.Append("  - `").Append(kv.Key).Append("` × ").Append(kv.Value).Append('\n');
        c.Result.AddValue("path", aPath);
        c.Result.AddValue("sha256", aSha);
        c.Result.AddValue("glb_nodes", aStats.Nodes.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("glb_meshes", aStats.Meshes.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("glb_instances", aStats.Instances.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("glb_shared_meshes", aShared.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("glb_triangles", aStats.Triangles.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("glb_ropes", aStats.Ropes.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("glb_sails", aStats.Sails.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ glb：node " + aStats.Nodes + "、mesh " + aStats.Meshes + "（共用 " + aShared + "）、實例 " + aStats.Instances + " → " + aPath);
    }
}
