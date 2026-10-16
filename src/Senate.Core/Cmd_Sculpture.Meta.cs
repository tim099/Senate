// 區塊職責：`sculpture op=work sub=meta`（TASK-0539）—— 給本作品的帆、繩、外部模型、關聯貼 meta（給下游認「這是什麼」，例 帆 `kind=sail, mast=fore, sail=topsail`、繩 `kind=sheet, sail=fore-topsail`）。
// 物理意義：meta 是給遊戲、匯出用的標籤，引擎自己不讀它的意思（組裝、觀測、繩、帆一格都不變）。
//          Tim 2026-10-13 拍板：**只改 meta 的這一刀不限作者**（2026-10-16 Tim 放寬成「不影響模型外觀的修改 ok」：sub=model／link／anchor 與拆件也不限作者，TASK-0646；帆、繩照舊只有作者）；
//          每一筆變更寫進作品資料夾的 `meta_log.jsonl`（一行一筆：時間、誰、作者、哪一種、名字、改之前、改之後）⇒ 事後查得到誰改的。
// 用法：
//   單筆：part=sail|rope|model|link name=<名> meta=<JSON 物件>（合併：給的鍵覆寫、值 null ⇒ 刪那個鍵；meta=none ⇒ 全清）
//   批次：file=<檔>：每行 `part<TAB>名<TAB>meta-JSON`（`#` 開頭、空行略過）；先全部驗過，**一筆不合 ⇒ 整份不寫**（零寫入）
//   讀數：不給 meta／file ⇒ 列出本作品帆、繩、模型、關聯的 meta（`part=` 只列那一種）
// 只認本作品自己的部件 —— 關聯進來的作品裡的帆、繩要到那件作品下（`work=<那件>`）。
// 數值影響：只改那幾個檔的 meta 欄；沒變的檔不寫（位元組不變）。用到 meta 的檔照各自規則升 schema（帆、繩 3、模型 2、關聯 3）。
// 失敗處置：參數不合、名字找不到、meta 不合 ⇒ exit 2、零寫入；讀檔失敗 ⇒ exit 2（⛔ 不當成沒有）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static readonly string[] s_MetaParts = { "sail", "rope", "model", "link" };
    static readonly Dictionary<string, string> s_MetaPartName = new(StringComparer.Ordinal) { ["sail"] = "帆", ["rope"] = "繩", ["model"] = "模型", ["link"] = "關聯" };
    /// <summary>批次檔最多幾行（三艘船的帆與繩加起來兩千出頭；超過多半是給錯檔）。</summary>
    const int MetaBatchMax = 20000;

    static string? WorkMeta(Ctx c, SCP_SculptWork iCard)
    {
        var aPaths = c.Works.SpacePaths(iCard.id);
        string aOnly = c.Args.Get("part").Trim();
        if (aOnly.Length > 0 && Array.IndexOf(s_MetaParts, aOnly) < 0) return Blocked(c, 2, "part 只認 sail｜rope｜model｜link（got '" + aOnly + "'）");
        SCP_SculptSailFile aSails; SCP_SculptRopeFile aRopes; SCP_SculptModelFile aModels; SCP_SculptLinkFile aLinks;
        try
        {
            aSails = SCP_SculptSails.ReadSails(aPaths);
            aRopes = SCP_SculptRopes.ReadRopes(aPaths);
            aModels = SCP_SculptModels.ReadModels(aPaths);
            aLinks = SCP_SculptLinks.Read(aPaths);
        }
        catch (InvalidOperationException e) { return Blocked(c, 2, e.Message); }

        // 每一種部件：名字 → 那一筆 live 的 meta（改的時候直接改它，再存那個檔）
        var aLive = new Dictionary<string, Dictionary<string, SortedDictionary<string, SCP_JsonData>>>(StringComparer.Ordinal)
        {
            ["sail"] = new(StringComparer.Ordinal), ["rope"] = new(StringComparer.Ordinal), ["model"] = new(StringComparer.Ordinal), ["link"] = new(StringComparer.Ordinal),
        };
        foreach (var s in aSails.sails) aLive["sail"][s.name] = s.meta;
        foreach (var r in aRopes.ropes) aLive["rope"][r.name] = r.meta;
        foreach (var m in aModels.models) aLive["model"][m.name] = m.meta;
        foreach (var l in aLinks.links) aLive["link"][l.name] = l.meta;

        bool aHasMeta = c.Args.IsExplicit("meta"), aHasFile = c.Args.IsExplicit("file");
        if (!aHasMeta && !aHasFile) return MetaList(c, iCard, aLive, aOnly);
        if (aHasMeta && aHasFile) return Blocked(c, 2, "meta= 與 file= 擇一（單筆／批次）");
        if (c.Persona.Length == 0) return Blocked(c, 2, "改 meta 要給 persona（記在 meta_log.jsonl 上，查得到是誰改的）");

        // ① 收集要改的（行號給錯誤訊息用）
        var aItems = new List<(string Part, string Name, string Meta, int Line)>();
        if (aHasMeta)
        {
            if (aOnly.Length == 0) return Blocked(c, 2, "單筆要 part=sail|rope|model|link name=<名> meta=<JSON>");
            string aName = c.Args.Get("name").Trim();
            if (aName.Length == 0) return Blocked(c, 2, "單筆要 name=<" + s_MetaPartName[aOnly] + "名>");
            aItems.Add((aOnly, aName, c.Args.Get("meta"), 0));
        }
        else
        {
            string aFile = Path.GetFullPath(c.Args.Get("file").Trim());
            if (!File.Exists(aFile)) return Blocked(c, 2, "file 不在：" + aFile.Replace('\\', '/'));
            string[] aLines;
            try { aLines = File.ReadAllLines(aFile, new UTF8Encoding(false)); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return Blocked(c, 2, "file 讀不了：" + e.Message); }
            for (int i = 0; i < aLines.Length; i++)
            {
                string t = aLines[i].TrimEnd('\r');
                if (t.Trim().Length == 0 || t.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                string[] f = t.Split('\t');
                if (f.Length != 3) return Blocked(c, 2, "file 第 " + (i + 1) + " 行要三欄（part<TAB>名<TAB>meta-JSON），got " + f.Length + " 欄；未寫入");
                string p = f[0].Trim();
                if (Array.IndexOf(s_MetaParts, p) < 0) return Blocked(c, 2, "file 第 " + (i + 1) + " 行的 part 只認 sail｜rope｜model｜link（got '" + p + "'）；未寫入");
                aItems.Add((p, f[1].Trim(), f[2], i + 1));
                if (aItems.Count > MetaBatchMax) return Blocked(c, 2, "file 超過 " + MetaBatchMax + " 筆；未寫入");
            }
            if (aItems.Count == 0) return Blocked(c, 2, "file 裡沒有任何一筆；未寫入");
        }

        // ② 全部先在副本上合併、驗過（同一筆出現好幾行 ⇒ 依序疊上去）
        var aWork = new Dictionary<string, SortedDictionary<string, SCP_JsonData>>(StringComparer.Ordinal);
        var aOrder = new List<(string Part, string Name)>();
        foreach (var it in aItems)
        {
            string aWhere = it.Line > 0 ? "file 第 " + it.Line + " 行：" : "";
            if (!aLive[it.Part].TryGetValue(it.Name, out var aCur))
                return Blocked(c, 2, aWhere + "本作品沒有" + s_MetaPartName[it.Part] + " `" + it.Name + "`（關聯進來的作品裡的要到那件作品下改）；未寫入");
            string aKey = it.Part + "\n" + it.Name;
            if (!aWork.TryGetValue(aKey, out var aCopy))
            {
                aCopy = SCP_SculptMeta.New();
                foreach (var kv in aCur) aCopy[kv.Key] = kv.Value;
                aWork[aKey] = aCopy;
                aOrder.Add((it.Part, it.Name));
            }
            if (SCP_SculptMeta.Merge(aCopy, it.Meta) is string aBad) return Blocked(c, 2, aWhere + s_MetaPartName[it.Part] + " `" + it.Name + "` 的 " + aBad + "；未寫入");
        }

        // ③ 有變的才套、才記；檔有變才存
        var aChanged = new HashSet<string>(StringComparer.Ordinal);
        var aLog = new StringBuilder();
        string aNow = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
        int aSame = 0;
        foreach (var (p, n) in aOrder)
        {
            var aCur = aLive[p][n];
            var aNext = aWork[p + "\n" + n];
            string aBefore = SCP_SculptMeta.ToNode(aCur).ToJson(false), aAfter = SCP_SculptMeta.ToNode(aNext).ToJson(false);
            if (aBefore == aAfter) { aSame++; continue; }
            aCur.Clear();
            foreach (var kv in aNext) aCur[kv.Key] = kv.Value;
            aChanged.Add(p);
            var o = SCP_JsonData.NewObject();
            o.Set("at", aNow); o.Set("persona", c.Persona); o.Set("owner", iCard.owner); o.Set("part", p); o.Set("name", n);
            o.Set("before", SCP_JsonParser.Parse(aBefore, false)); o.Set("after", SCP_JsonParser.Parse(aAfter, false));
            aLog.Append(o.ToJson(false)).Append('\n');
        }
        int aCount = aOrder.Count - aSame;
        if (aCount > 0)
        {
            if (aChanged.Contains("sail")) SCP_SculptSails.SaveSails(aPaths, aSails);
            if (aChanged.Contains("rope")) SCP_SculptRopes.SaveRopes(aPaths, aRopes);
            if (aChanged.Contains("model")) SCP_SculptModels.SaveModels(aPaths, aModels);
            if (aChanged.Contains("link")) SCP_SculptLinks.Save(aPaths, aLinks);
            File.AppendAllText(aPaths.MetaLogFile, aLog.ToString(), new UTF8Encoding(false));
        }
        c.Result.AddValue("meta_changed", aCount.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("meta_unchanged", aSame.ToString(CultureInfo.InvariantCulture));
        foreach (string p in s_MetaParts)
        {
            int k = 0; foreach (var (pp, n) in aOrder) if (pp == p && aLive[pp][n].Count > 0) k++;
            if (k > 0) c.Result.AddValue("meta_" + p, k.ToString(CultureInfo.InvariantCulture));
        }
        if (aCount > 0) c.Result.AddValue("meta_log", aPaths.MetaLogFile.Replace('\\', '/'));
        c.Report.Append("\n## meta 變更（").Append(aCount).Append(" 筆改了、").Append(aSame).Append(" 筆本來就一樣）\n");
        foreach (var (p, n) in aOrder) c.Report.Append("- ").Append(s_MetaPartName[p]).Append(" `").Append(n).Append("`：").Append(aLive[p][n].Count == 0 ? "（空）" : SCP_SculptMeta.Describe(aLive[p][n])).Append('\n');
        bool aNotOwner = c.Persona != iCard.owner;
        return Finish(c, 0, "✓ meta：" + aCount + " 筆改了、" + aSame + " 筆本來就一樣" + (aCount > 0 ? "｜記在 meta_log.jsonl" : "")
                            + (aNotOwner ? "｜作者是 " + iCard.owner + "（meta 不限作者，TASK-0539）" : "") + "｜不是 voxel，一格都沒動");
    }

    /// <summary>讀數：本作品每一種部件、每一筆的 meta（只列有的，最後報沒標的幾筆）。</summary>
    static string? MetaList(Ctx c, SCP_SculptWork iCard, Dictionary<string, Dictionary<string, SortedDictionary<string, SCP_JsonData>>> iLive, string iOnly)
    {
        int aTotal = 0;
        foreach (string p in s_MetaParts)
        {
            if (iOnly.Length > 0 && p != iOnly) continue;
            var aAll = iLive[p];
            int aWith = 0;
            c.Report.Append("\n## ").Append(s_MetaPartName[p]).Append("（").Append(aAll.Count).Append(" 筆）\n");
            var aNames = new List<string>(aAll.Keys); aNames.Sort(StringComparer.Ordinal);
            var aWithout = new List<string>();
            foreach (string n in aNames)
            {
                if (aAll[n].Count == 0) { aWithout.Add(n); continue; }
                aWith++;
                c.Report.Append("- `").Append(n).Append("`：").Append(SCP_SculptMeta.Describe(aAll[n])).Append('\n');
            }
            if (aWithout.Count > 0) c.Report.Append("- 沒有 meta：").Append(aWithout.Count).Append(" 筆（").Append(string.Join("、", aWithout.GetRange(0, Math.Min(12, aWithout.Count)))).Append(aWithout.Count > 12 ? "…" : "").Append("）\n");
            c.Result.AddValue("meta_" + p, aWith.ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("meta_" + p + "_without", aWithout.Count.ToString(CultureInfo.InvariantCulture));
            aTotal += aWith;
        }
        return Finish(c, 0, "✓ meta：" + iCard.id + " 帶 meta 的 " + aTotal + " 筆（只列本作品自己的部件）");
    }
}
