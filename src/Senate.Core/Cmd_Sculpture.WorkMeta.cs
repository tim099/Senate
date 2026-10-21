// 區塊職責：作品自己的預設 meta（TASK-0795；Tim 2026-10-21「雕刻引擎加預設 meta 功能(可以加)」「meta應該只要標共用的那個子模型」）——
//   被引用很多次的元件（火藥桶、砲、舵輪）標一次就好：作品資料夾的 `work_meta.json` 存一份 meta，
//   引用它的外部模型（models.json 的 work 來源）與關聯（links.json）匯出時自動帶上；擺放上自己寫的鍵優先（同一個鍵 ⇒ 擺放的贏）。
// 物理意義：跟擺放上的 meta 一樣只是給下游的標籤（引擎自己不讀它的意思）；設定走 `work sub=meta part=work`（不限作者、記 meta_log），見 Cmd_Sculpture.Meta.cs。
// 數值影響：匯出（export format=parts、format=vxk include=meta）的 links[]／models[] 的 meta 多出預設的鍵；作品裡的 links.json／models.json 一位元組都不動。
//          沒有 work_meta.json 的作品 ⇒ 匯出跟以前逐欄相同。
//          ⚠ 關聯的 material／render 會變成格子的材質層、render 層（引擎另一條路照擺放上的 meta 算，TASK-0616）⇒ 那兩個鍵寫在預設 meta 裡只進 meta、不改格子的層。
// 失敗處置：work_meta.json 壞了（不是物件、schema 不認得、meta 不合）⇒ 大聲擋下（⛔ 不當成沒有：那樣匯出會默默少掉鍵）。
#nullable enable
using System.Text;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    const int WorkMetaSchema = 1;

    /// <summary>作品 <paramref name="iId"/> 的預設 meta 檔（作品資料夾，跟 links.json 同一層）。</summary>
    static string WorkMetaFile(SCP_SculptWorks iWorks, string iId) => Path.Combine(Path.GetDirectoryName(iWorks.SpacePaths(iId).LinksFile)!, "work_meta.json");

    /// <summary>讀預設 meta（沒有檔 ⇒ 空）。壞了 ⇒ InvalidOperationException（訊息說哪件、哪裡壞）。</summary>
    static SortedDictionary<string, SCP_JsonData> ReadWorkMeta(SCP_SculptWorks iWorks, string iId)
    {
        var aMeta = SCP_SculptMeta.New();
        string f = WorkMetaFile(iWorks, iId);
        if (!File.Exists(f)) return aMeta;
        SCP_JsonData j;
        try { j = SCP_JsonParser.Parse(File.ReadAllText(f, Encoding.UTF8), false); }
        catch (Exception e) { throw new InvalidOperationException("作品 " + iId + " 的 work_meta.json 讀不了：" + e.Message); }
        if (!j.IsObject || j["schema"].AsLong() != WorkMetaSchema) throw new InvalidOperationException("作品 " + iId + " 的 work_meta.json 不是 schema " + WorkMetaSchema + " 的物件");
        if (SCP_SculptMeta.ReadFrom(j["meta"], aMeta) is string aBad) throw new InvalidOperationException("作品 " + iId + " 的 work_meta.json：" + aBad);
        return aMeta;
    }

    /// <summary>存預設 meta；空的 ⇒ 刪檔（清光 ⇒ 作品資料夾逐檔回到沒標之前）。</summary>
    static void SaveWorkMeta(SCP_SculptWorks iWorks, string iId, SortedDictionary<string, SCP_JsonData> iMeta)
    {
        string f = WorkMetaFile(iWorks, iId);
        if (iMeta.Count == 0) { if (File.Exists(f)) File.Delete(f); return; }
        var o = SCP_JsonData.NewObject();
        o.Set("schema", WorkMetaSchema);
        SCP_SculptMeta.WriteTo(o, iMeta);
        File.WriteAllText(f, SCP_JsonWriter.Write(o, true) + "\n", new UTF8Encoding(false));
    }

    /// <summary>
    /// 部件清單（<see cref="SCP_SculptPartsExport.Build"/> 的結果）的 links[]、models[]：照各自的來源作品（`work`）補上預設 meta，擺放上自己的鍵優先。
    /// 回補了幾筆。壞掉的 work_meta.json ⇒ InvalidOperationException（呼叫端擋下）。
    /// </summary>
    static int ApplyWorkMeta(SCP_SculptWorks iWorks, SCP_JsonData ioParts)
    {
        var aCache = new Dictionary<string, SortedDictionary<string, SCP_JsonData>>(StringComparer.Ordinal);
        int n = 0;
        foreach (string aKind in new[] { "links", "models" })
            foreach (SCP_JsonData o in ioParts[aKind])
            {
                if (!o["work"].IsString) continue;                              // 外部 obj（file 來源）沒有作品
                string w = o["work"].AsString();
                if (!aCache.TryGetValue(w, out var aDef)) aCache[w] = aDef = iWorks.Exists(w) ? ReadWorkMeta(iWorks, w) : SCP_SculptMeta.New();
                if (aDef.Count == 0) continue;
                var aOwn = SCP_SculptMeta.New();
                if (SCP_SculptMeta.ReadFrom(o["meta"], aOwn) is string aBad) throw new InvalidOperationException("`" + o["path"].AsString() + "` 的 meta：" + aBad);
                var aOut = SCP_SculptMeta.New();
                foreach (var kv in aDef) aOut[kv.Key] = kv.Value;
                foreach (var kv in aOwn) aOut[kv.Key] = kv.Value;              // 擺放上寫的優先
                o.Set("meta", SCP_SculptMeta.ToNode(aOut));
                n++;
            }
        return n;
    }
}
