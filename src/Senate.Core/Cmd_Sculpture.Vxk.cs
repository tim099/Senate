// 區塊職責：`sculpture op=export format=vxk`（TASK-0518）—— VoxelKit 的 `.vxk` 由 senate 直接產（原本是 ArtGallery/VoxelKit/voxel_export.py）。
// 物理意義：`include=rig,models,parts,meta` 對應 Python 的 --rig／--models／--parts／--meta；格式與欄位照 VoxelKit/README.md 不動。
//          檔頭前段（作品書卡）與後段（關聯外框、繩帆模型、meta＋固定點）在這裡組；格子那一段與封裝、回讀對拍在引擎（SCP_SculptEngine.ExportVxk）。
//          ⚠ 跟 Python 那支唯一刻意不同：繩／帆／模型也照 links=／hide_links= 的選擇（Python 的 obj 那一趟沒帶 links，只有格子帶）。
// 數值影響：不改任何作品檔；模型網格與繩、帆跟觀測頁同一支（SCP_SculptModels／SCP_SculptRopes.Tube／SCP_SculptSails.TwoSided）。
#nullable enable
using System.Globalization;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static readonly string[] s_VxkInclude = { "rig", "models", "parts", "meta", "ends" };

    static string? ExportVxk(Ctx c)
    {
        if (c.Work == null) return Blocked(c, 2, "export format=vxk 要 work=");
        string aOut = c.Args.Get("out").Trim();
        if (aOut.Length == 0 || !Path.IsPathRooted(aOut)) return Blocked(c, 2, "export format=vxk 要 out=<.vxk 的絕對路徑>");
        var aInc = new HashSet<string>(StringComparer.Ordinal);
        foreach (string s in c.Args.Get("include").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string k = s.Trim().ToLowerInvariant();
            if (Array.IndexOf(s_VxkInclude, k) < 0) return Blocked(c, 2, "include 只認 rig,models,parts,meta,ends（got '" + s.Trim() + "'）");
            aInc.Add(k);
        }
        if (aInc.Contains("models") && !aInc.Contains("rig")) return Blocked(c, 2, "include=models 要配 rig（模型放在 header 的 rig.models／rig.meshes）");
        if (aInc.Contains("ends") && !aInc.Contains("rig")) return Blocked(c, 2, "include=ends 要配 rig（繩的兩端放在 header 的 rig.ropes）");
        if (c.Args.Get("region").Trim().Length > 0 || c.Args.Get("exclude_color").Trim().Length > 0) return Blocked(c, 2, "vxk 是整件作品的封裝 —— 不吃 region／exclude_color");
        SCP_SculptWork aCard = c.Work;

        List<SCP_SculptLinkNode> aTree;
        try { aTree = c.LinkTree ?? SCP_SculptLinks.Tree(c.Works, aCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, "關聯樹讀不了：" + e.Message); }
        SCP_SculptLinkSelection aSel = c.Links?.Selection ?? SCP_SculptLinkSelection.Parse(c.Args.Get("links"), c.Args.Get("hide_links"));

        // ① 前段：書卡（同 Python：meters_per_voxel 沒記 ⇒ 0.1）
        var aHead = SCP_JsonData.NewObject();
        aHead.Set("format", SCP_SculptVxk.Format);
        aHead.Set("version", SCP_SculptVxk.Version);
        aHead.Set("work", aCard.id);
        aHead.Set("title", aCard.title);
        aHead.Set("owner", aCard.owner);
        // ⚠ 讀原始欄位（同 Python 的 work.get('meters_per_voxel') or 0.1）—— MetersPerVoxel 屬性沒記時是 1/32，不是 0.1（淨室抓到的）
        aHead.Set("meters_per_voxel", aCard.meters_per_voxel > 0 ? aCard.meters_per_voxel : 0.1);
        var aSize = SCP_JsonData.NewArray(); aSize.Add(aCard.SizeX); aSize.Add(aCard.SizeY); aSize.Add(aCard.SizeZ);
        aHead.Set("size", aSize);

        // ② 後段
        var aTail = SCP_JsonData.NewObject();
        SCP_JsonData? aParts = aInc.Contains("parts") ? SCP_SculptVxk.Parts(aTree) : null;
        if (aParts != null) aTail.Set("parts", aParts);
        SCP_SculptSailRig? aRig = null;
        SCP_JsonData? aRigJ = null;
        if (aInc.Contains("rig") || aInc.Contains("meta"))
        {
            if (ResolveViewRig(c, out aRig) is string aRigBad) return Blocked(c, 2, aRigBad);
            if (aRig == null) return Blocked(c, 2, "繩與帆讀不了（沒有作品？）");
        }
        if (aInc.Contains("rig"))
        {
            AppendRopeReport(c, aRig!.Ropes);
            AppendSailReport(c, aRig);
            List<SCP_SculptModelGeom> aGeoms = aRig.Models?.Geoms() ?? new List<SCP_SculptModelGeom>();
            if (aRig.Models != null) AppendModelReport(c, aRig.Models);
            aRigJ = SCP_JsonData.NewObject();
            try
            {
                aRigJ.Set("ropes", SCP_SculptVxk.Ropes(SCP_SculptRopes.Drawable(aRig.Ropes)));
                aRigJ.Set("sails", SCP_SculptVxk.Sails(aRig.SailGeoms()));
            }
            catch (InvalidDataException e) { return Blocked(c, 1, "vxk：" + e.Message); }
            var aSkipped = SCP_JsonData.NewObject();
            int aModelObjs = aGeoms.Count(g => g.Triangles.Length > 0);
            if (aInc.Contains("models"))
            {
                var (aModelsJ, aMeshesJ) = SCP_SculptVxk.Models(aGeoms);
                aRigJ.Set("models", aModelsJ);
                aRigJ.Set("meshes", aMeshesJ);
            }
            else if (aModelObjs > 0) aSkipped.Set("model", aModelObjs);
            aTail.Set("rig", aRigJ);
            aTail.Set("rig_skipped", aSkipped);
        }
        int aWithMeta = 0, aPointCount = 0;
        if (aInc.Contains("meta"))
        {
            // meta＋固定點：同 export format=parts（TASK-0517）那一份；對不上路徑、固定點讀不了 ⇒ 擋（同 voxel_export.py --meta）
            SCP_JsonData aPj = SCP_SculptPartsExport.Build(c.Works, aCard, aTree, aSel, aRig?.Models ?? new SCP_SculptModelSet());
            var aByLink = new Dictionary<string, SCP_JsonData>(StringComparer.Ordinal);
            var aByModel = new Dictionary<string, SCP_JsonData>(StringComparer.Ordinal);
            foreach (SCP_JsonData o in aPj["links"]) aByLink[o["path"].AsString()] = o;
            foreach (SCP_JsonData o in aPj["models"]) aByModel[o["path"].AsString()] = o;
            var aBad = new List<string>();
            foreach (var kv in aByLink) if (kv.Value["points_error"].Exists) aBad.Add(kv.Key);
            foreach (var kv in aByModel) if (kv.Value["points_error"].Exists) aBad.Add(kv.Key);
            if (aBad.Count > 0) return Blocked(c, 2, "這些部件的固定點讀不了（⛔ 不給空的 points）：" + string.Join("、", aBad));
            aTail.Set("points", aPj["points"]);
            aPointCount += aPj["points"].Count;
            void Attach(SCP_JsonData iTarget, SCP_JsonData iFrom)
            {
                iTarget.Set("meta", iFrom["meta"]);
                iTarget.Set("points", iFrom["points"]);
                if (iFrom["meta"].Count > 0) aWithMeta++;
                aPointCount += iFrom["points"].Count;
            }
            if (aParts != null)
                foreach (SCP_JsonData p in aParts)
                {
                    if (!aByLink.TryGetValue(p["path"].AsString(), out SCP_JsonData? q)) return Blocked(c, 2, "部件清單裡沒有關聯 `" + p["path"].AsString() + "`");
                    Attach(p, q);
                }
            if (aRigJ != null && aRigJ["models"].Exists)
                foreach (SCP_JsonData m in aRigJ["models"])
                {
                    if (!aByModel.TryGetValue(m["name"].AsString(), out SCP_JsonData? q)) return Blocked(c, 2, "部件清單裡沒有模型 `" + m["name"].AsString() + "`");
                    Attach(m, q);
                }
        }
        // 帆與繩（TASK-0539）：meta 有才寫（⇒ 沒標 meta 的作品匯出跟以前逐欄相同）；include=ends ⇒ 繩多帶 frame（繩所在那一層的關聯路徑，組裝件自己 ＝ ""）與 ends（兩端原樣的參照，相對於 frame）
        int aRopeMeta = 0, aSailMeta = 0;
        if (aRigJ != null && (aInc.Contains("meta") || aInc.Contains("ends")))
        {
            var aRopeBy = new Dictionary<string, SCP_SculptRopeReading>(StringComparer.Ordinal);
            foreach (var r in aRig!.Ropes) aRopeBy[r.Path] = r;
            foreach (SCP_JsonData o in aRigJ["ropes"])
            {
                if (!aRopeBy.TryGetValue(o["name"].AsString(), out SCP_SculptRopeReading? r)) return Blocked(c, 2, "繩的讀數裡沒有 `" + o["name"].AsString() + "`（不該發生）");
                if (aInc.Contains("meta") && r.Rope.meta.Count > 0) { o.Set("meta", SCP_SculptMeta.ToNode(r.Rope.meta)); aRopeMeta++; }
                if (aInc.Contains("ends"))
                {
                    string aFrame = r.Path.EndsWith("/" + r.Rope.name, StringComparison.Ordinal) ? r.Path.Substring(0, r.Path.Length - r.Rope.name.Length - 1) : "";
                    var aEnds = SCP_JsonData.NewArray(); aEnds.Add(r.Rope.from); aEnds.Add(r.Rope.to);
                    o.Set("frame", aFrame);
                    o.Set("ends", aEnds);
                }
            }
            if (aInc.Contains("meta"))
            {
                var aSailBy = new Dictionary<string, SCP_SculptSailReading>(StringComparer.Ordinal);
                foreach (var s in aRig.Sails) aSailBy[s.Path] = s;
                foreach (SCP_JsonData o in aRigJ["sails"])
                {
                    if (!aSailBy.TryGetValue(o["name"].AsString(), out SCP_SculptSailReading? s)) return Blocked(c, 2, "帆的讀數裡沒有 `" + o["name"].AsString() + "`（不該發生）");
                    if (s.Sail.meta.Count > 0) { o.Set("meta", SCP_SculptMeta.ToNode(s.Sail.meta)); aSailMeta++; }
                }
            }
        }

        // ③ 格子、封裝、回讀對拍：引擎（拿作品鎖）
        var aArgs = new SCP_SculptExportArgs { Format = SCP_SculptVxk.Format, Out = aOut, OutDir = c.Args.Get("out_dir").Trim(), VxkHead = aHead, VxkTail = aTail };
        if (!TryLocked(c, e => e.Export(aArgs), out SCP_SculptResult aRes, out string? aBlocked)) return aBlocked;
        c.Report.Append("```\n").Append(aRes.Render()).Append("\n```\n");
        if (!aRes.Ok) return EngineFail(c, aRes);
        var aEx = (SCP_SculptExportResult)aRes;
        string aPath = aEx.OutputPath.Replace('\\', '/');
        c.Result.Lines.AddRange(aRes.Lines);
        c.Result.AddValue("path", aPath);
        c.Result.AddValue("sha256", aEx.Sha256);
        c.Result.AddValue("voxels", aEx.VoxelCount.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("include", string.Join(",", Array.FindAll(s_VxkInclude, aInc.Contains)));
        if (aRigJ != null)
        {
            c.Result.AddValue("vxk_ropes", aRigJ["ropes"].Count.ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("vxk_sails", aRigJ["sails"].Count.ToString(CultureInfo.InvariantCulture));
            if (aRigJ["models"].Exists)
            {
                c.Result.AddValue("vxk_models", aRigJ["models"].Count.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("vxk_meshes", aRigJ["meshes"].Count.ToString(CultureInfo.InvariantCulture));
            }
        }
        if (aParts != null) c.Result.AddValue("vxk_parts", aParts.Count.ToString(CultureInfo.InvariantCulture));
        if (aInc.Contains("meta"))
        {
            if (aRigJ != null) { c.Result.AddValue("vxk_rope_meta", aRopeMeta.ToString(CultureInfo.InvariantCulture)); c.Result.AddValue("vxk_sail_meta", aSailMeta.ToString(CultureInfo.InvariantCulture)); }
            c.Result.AddValue("vxk_with_meta", aWithMeta.ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("vxk_points", aPointCount.ToString(CultureInfo.InvariantCulture));
        }
        c.Result.AddOutput(aPath);
        return Finish(c, 0, "✓ export vxk " + aEx.VoxelCount + " voxels" + (aRigJ != null ? "｜繩 " + aRigJ["ropes"].Count + "、帆 " + aRigJ["sails"].Count + (aRigJ["models"].Exists ? "、模型 " + aRigJ["models"].Count + "（網格 " + aRigJ["meshes"].Count + " 份）" : "") : "")
                            + (aParts != null ? "｜關聯外框 " + aParts.Count : "") + (aInc.Contains("meta") ? "｜帶 meta " + aWithMeta + "、固定點 " + aPointCount : "") + " → " + aPath);
    }
}
