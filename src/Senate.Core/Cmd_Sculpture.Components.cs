// 區塊職責：`sculpture op=work sub=components`（Tim 2026-10-14：造船「儘量用已有的元件」改用指令查，不寫死清單）——
//          全部作品裡的外部模型照「來源作品」彙整：哪件作品被誰當模型用、幾次、meta（kind 與 gun 等）、用過哪些縮放。造船規格 §2.1。
// 物理意義：唯讀；只讀每件作品的 models.json（不產生網格快取、不拿作品鎖）。讀不了的作品列出來（⛔ 不默默略過 —— 少一件看起來跟「沒有人用」一樣）。
//          obj 檔來源的模型不是雕刻作品、別的船引用不到 ⇒ 不列。
#nullable enable
using System.Globalization;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    sealed class ComponentUse
    {
        public string Source = "";
        public int Count;
        public readonly SortedDictionary<string, int> Hosts = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Kinds = new(StringComparer.Ordinal);
        public readonly SortedSet<string> Scales = new(StringComparer.Ordinal);
    }

    /// <summary>work sub=components [kind=gun]：每件被當模型用的作品一行（被引用最多的在前）。</summary>
    static string? WorkComponents(Ctx c)
    {
        string aKind = c.Args.Get("kind").Trim().ToLowerInvariant();
        var aCards = c.Works.List();
        var aById = new Dictionary<string, SCP_SculptWork>(StringComparer.Ordinal);
        foreach (var w in aCards) aById[w.id] = w;
        var aUse = new Dictionary<string, ComponentUse>(StringComparer.Ordinal);
        var aErrors = new List<string>();
        foreach (var w in aCards)
        {
            SCP_SculptModelFile aFile;
            try { aFile = SCP_SculptModels.ReadModels(c.Works.SpacePaths(w.id)); }
            catch (Exception e) { aErrors.Add(w.id + "：" + e.Message); continue; }
            foreach (var m in aFile.models)
            {
                if (m.source != SCP_SculptModels.SourceWork || m.work.Length == 0) continue;
                string aDesc = MetaDesc(m.meta);
                if (aKind.Length > 0 && !aDesc.Split(',').Any(k => k == aKind || k.StartsWith(aKind + ":", StringComparison.Ordinal))) continue;
                if (!aUse.TryGetValue(m.work, out var u)) aUse[m.work] = u = new ComponentUse { Source = m.work };
                u.Count++;
                u.Hosts[w.id] = u.Hosts.TryGetValue(w.id, out int n) ? n + 1 : 1;
                if (aDesc.Length > 0) u.Kinds.Add(aDesc);
                u.Scales.Add(string.Join(",", m.scale.Select(s => Math.Round(s, 4).ToString(CultureInfo.InvariantCulture)).Distinct()));
            }
        }
        var aRows = aUse.Values.OrderByDescending(u => u.Hosts.Count).ThenByDescending(u => u.Count).ThenBy(u => u.Source, StringComparer.Ordinal).ToList();
        foreach (var u in aRows)
        {
            aById.TryGetValue(u.Source, out SCP_SculptWork? aSrc);
            c.Report.Append("- `").Append(u.Source).Append('`')
                .Append(aSrc != null ? "｜" + Title(aSrc.title) + "｜by " + aSrc.owner + "｜" + aSrc.MetersPerVoxel.ToString(CultureInfo.InvariantCulture) + " m／格" : "｜⚠ 來源作品不存在")
                .Append("｜meta ").Append(u.Kinds.Count > 0 ? string.Join("／", u.Kinds) : "（沒有）")
                .Append("｜引用 ").Append(u.Count).Append(" 次、").Append(u.Hosts.Count).Append(" 件：")
                .Append(string.Join("、", u.Hosts.Select(h => HostName(aById, h.Key) + "×" + h.Value)))
                .Append("｜scale ").Append(string.Join("／", u.Scales)).Append('\n');
        }
        foreach (string e in aErrors) c.Report.Append("- ⚠ 讀不了 models.json：").Append(e).Append('\n');
        c.Result.AddValue("components", aRows.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("works_scanned", aCards.Count.ToString(CultureInfo.InvariantCulture));
        if (aErrors.Count > 0) c.Result.AddValue("unreadable", aErrors.Count.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ 被當模型用的作品 " + aRows.Count + " 件（掃了 " + aCards.Count + " 件作品" + (aKind.Length > 0 ? "、只看 kind=" + aKind : "") + "）"
            + (aErrors.Count > 0 ? "｜⚠ " + aErrors.Count + " 件讀不了（清單見回傳檔）" : ""));
    }

    /// <summary>meta 的簡述：kind（＋gun／boat 這類子種類），例「gun:24l」「boat:cutter」「wheel」。</summary>
    static string MetaDesc(SortedDictionary<string, SCP_JsonData> iMeta)
    {
        if (!iMeta.TryGetValue("kind", out SCP_JsonData? k) || k == null || !k.IsString || k.AsString().Length == 0) return "";
        string aKind = k.AsString();
        return iMeta.TryGetValue(aKind, out SCP_JsonData? s) && s != null && s.IsString && s.AsString().Length > 0 ? aKind + ":" + s.AsString() : aKind;
    }

    /// <summary>引用它的作品：名字（子作品的名字本來就帶船名，例「風暴角號｜上甲板」）；名字裡的「｜」換成「・」，⛔ 不然跟讀數的欄位分隔混在一起。</summary>
    static string HostName(Dictionary<string, SCP_SculptWork> iById, string iId) => iById.TryGetValue(iId, out SCP_SculptWork? w) && w.title.Length > 0 ? Title(w.title) : iId;

    static string Title(string iTitle) => iTitle.Replace("｜", "・");
}
