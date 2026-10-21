// 區塊職責：`sculpture op=export format=parts`（TASK-0517）—— 部件清單 JSON：每條關聯、每個外部模型的路徑、來源、外框、meta、points（組裝件座標）。
// 物理意義：組裝的邏輯在 SCP_Core `SCP_SculptPartsExport`；本檔只做參數 → 讀樹與模型 → 寫檔 → 讀數。
//          給 VoxelKit（voxel_export.py --meta）把 meta／points 接進 `.vxk` header —— ⛔ 不再從給人看的讀數（sub=links 的行）拆字串。
// 數值影響：不讀 voxel、不拿作品鎖、不改任何作品檔；模型外框要網格 ⇒ work 來源缺快取會現產生（同 view／export obj）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static string? ExportParts(Ctx c)
    {
        string aWork = c.Args.Get("work").Trim();
        if (aWork.Length == 0) return Blocked(c, 2, "export format=parts 要 work=");
        string aOut = c.Args.Get("out").Trim();
        if (aOut.Length == 0 || !Path.IsPathRooted(aOut)) return Blocked(c, 2, "export format=parts 要 out=<.json 的絕對路徑>");
        SCP_SculptWork aCard = c.Works.Load(SCP_SculptWorks.NormalizeId(aWork));
        c.Result.AddValue("work", aCard.id);
        List<SCP_SculptLinkNode> aTree;
        try { aTree = SCP_SculptLinks.Tree(c.Works, aCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, "關聯樹讀不了：" + e.Message); }
        var aSel = SCP_SculptLinkSelection.Parse(c.Args.Get("links").Trim(), c.Args.Get("hide_links").Trim());
        try { SCP_SculptLinks.ValidateSelection(aTree, aSel); }
        catch (ArgumentException e) { return Blocked(c, 2, e.Message); }
        if (ResolveViewModels(c, aCard, aTree, aSel, true, out SCP_SculptModelSet aSet) is string aBad) return Blocked(c, 2, aBad);
        SCP_JsonData aJson = SCP_SculptPartsExport.Build(c.Works, aCard, aTree, aSel, aSet);
        int aDefaulted;                                                    // 作品本身的預設 meta（TASK-0795，Cmd_Sculpture.WorkMeta.cs）：引用它的模型、關聯帶上，擺放上寫的優先
        try { aDefaulted = ApplyWorkMeta(c.Works, aJson); }
        catch (InvalidOperationException e) { return Blocked(c, 2, e.Message); }
        string? aDir = Path.GetDirectoryName(aOut);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllText(aOut, SCP_JsonWriter.Write(aJson, true) + "\n", new UTF8Encoding(false));

        int aLinks = aJson["links"].Count, aModels = aJson["models"].Count, aWithMeta = 0, aPoints = aJson["points"].Count, aErrors = 0;
        foreach (string aKind in new[] { "links", "models" })
            foreach (SCP_JsonData o in aJson[aKind])
            {
                if (o["meta"].Count > 0) aWithMeta++;
                aPoints += o["points"].Count;
                if (o["points_error"].Exists) { aErrors++; c.Report.Append("- 🔴 `").Append(o["path"].AsString()).Append("` 固定點讀不了：").Append(o["points_error"].AsString()).Append('\n'); }
            }
        string aPath = aOut.Replace('\\', '/');
        c.Result.AddValue("path", aPath);
        c.Result.AddValue("parts_links", aLinks.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("parts_models", aModels.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("parts_with_meta", aWithMeta.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("parts_points", aPoints.ToString(CultureInfo.InvariantCulture));
        if (aDefaulted > 0) c.Result.AddValue("parts_work_meta", aDefaulted.ToString(CultureInfo.InvariantCulture));
        if (aErrors > 0)
        {
            c.Result.AddValue("parts_points_errors", aErrors.ToString(CultureInfo.InvariantCulture));
            c.Result.Lines.Add("🔴 " + aErrors + " 個部件的固定點讀不了（points 是空的、points_error 寫了原因）—— 見回傳檔");
        }
        c.Result.AddOutput(aPath);
        return Finish(c, 0, "✓ export parts：關聯 " + aLinks + "、模型 " + aModels + "（帶 meta " + aWithMeta + "）、固定點 " + aPoints + " → " + aPath);
    }
}
