// 區塊職責：預覽 preset 的 CLI（TASK-0571）—— work sub=preset／unpreset／presets；觀測類 op 的 preset=<名字>。
// 物理意義：preset 只是幾個觀測參數的原字串（SCP_SculptViewPresets）；preset= 在進 Ctx 之前展開成那幾個參數、重綁一次參數包 ⇒
//          後面每一支（關聯、繩、帆、模型、範圍、排除色）照原本的規則驗與畫 —— 跟手打那幾個參數走同一條路。
// 數值影響：只多讀一次 view_presets.json；沒給 preset ⇒ 參數包原封不動。
// 失敗處置：名字不存在、preset 跟手打的同一個參數都給了（⛔ 不默默挑一個）、不是觀測類 op、沒帶 work ⇒ exit 2。
//          ⚠ 展開時把原參數包全部讀過一次（重綁要原值）⇒ 這一趟「給了卻沒被讀」的兜底警告（TASK-0289）不會亮。
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    /// <summary>
    /// preset=<名字> ⇒ 回一個重綁過的參數包（那幾個參數變成「顯式給的」）；沒給 preset／preset=all ⇒ 原包。失敗 ⇒ null、oError 說原因。
    /// </summary>
    static SCP_CmdArgs? ExpandPreset(SCP_CmdArgs iArgs, string iOp, string iDataRoot, SCP_CmdResult ioResult, out string oError)
    {
        oError = "";
        if (!iArgs.IsExplicit("preset")) return iArgs;
        string aName = iArgs.Get("preset").Trim();
        if (Array.IndexOf(SCP_SculptViewPresets.Ops, iOp) < 0)
        {
            oError = "preset 只給觀測類 op（" + string.Join("／", SCP_SculptViewPresets.Ops) + "）—— 存一組用 work sub=preset name=… 加參數";
            return null;
        }
        ioResult.AddValue("preset", aName.Length == 0 ? SCP_SculptViewPresets.All : aName);
        if (aName.Length == 0 || string.Equals(aName, SCP_SculptViewPresets.All, StringComparison.OrdinalIgnoreCase)) return iArgs;
        string aWork = iArgs.Get("work").Trim();
        if (aWork.Length == 0) { oError = "preset 是作品的 —— 要帶 work="; return null; }
        Dictionary<string, string> aAdd;
        var aSkipped = new List<string>();
        try
        {
            var aWorks = new SCP_SculptWorks(new SCP.Core.Paths.SCP_DataRoot(iDataRoot));
            string aId = SCP_SculptWorks.NormalizeId(aWork);
            aWorks.Load(aId);
            aAdd = SCP_SculptViewPresets.Expand(SCP_SculptViewPresets.Read(aWorks.SpacePaths(aId)), aName, iOp, aSkipped);
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException) { oError = e.Message; return null; }
        var aClash = aAdd.Keys.Where(iArgs.IsExplicit).ToList();
        if (aClash.Count > 0)
        {
            oError = "preset `" + aName + "` 已經有 " + string.Join("、", aClash.Select(k => k + "=" + aAdd[k])) + " —— 又手打了同一個參數（⛔ 不默默挑一個；要改就拿掉 preset 手打，或改那組 preset）";
            return null;
        }
        // 重綁：值照原包（含預設值）、顯式名單 ＝ 原本顯式的 ＋ preset 的（⛔ 不能把每一個都當顯式：view 用「顯式」判斷要不要蓋過設定檔鏈）
        var aRaw = new Dictionary<string, string>(StringComparer.Ordinal);
        var aExplicit = new List<string>();
        foreach (SCP_CmdArgSpec aSpec in ArgSpecsStatic)
        {
            aRaw[aSpec.Name] = iArgs.Get(aSpec.Name);
            if (iArgs.IsExplicit(aSpec.Name)) aExplicit.Add(aSpec.Name);
        }
        foreach (var kv in aAdd) { aRaw[kv.Key] = kv.Value; aExplicit.Add(kv.Key); }
        aRaw[SCP_CmdArgs.ForwardedExplicitKey] = string.Join(",", aExplicit);
        var (aBound, aErrors) = SCP_CmdArgs.Bind(ArgSpecsStatic, aRaw);
        if (aBound == null) { oError = "preset `" + aName + "` 展開後的參數不合法：" + string.Join("；", aErrors); return null; }
        ioResult.Lines.Add("⤷ preset `" + aName + "`：" + (aAdd.Count == 0 ? "（這個 op 沒有要補的參數）" : string.Join("｜", aAdd.Select(kv => kv.Key + "=" + kv.Value)))
                           + (aSkipped.Count > 0 ? "｜略過 " + string.Join("、", aSkipped) + "（繩、帆、模型只有 view／export 畫）" : ""));
        ioResult.AddValue("preset_args", string.Join(";", aAdd.Select(kv => kv.Key + "=" + kv.Value)));
        return aBound;
    }

    // ───────────────────────── work sub=preset／unpreset／presets ─────────────────────────

    /// <summary>
    /// work sub=preset name= [links=] [hide_links=] [region=] [exclude_color=] [models=] [hide_models=] [ropes=] [hide_ropes=] [rope_groups=] [hide_rope_groups=] [sails=] [hide_sails=] [note=]：
    /// 新增或改一組。有給的參數蓋過去（給空字串 ＝ 清掉那一格），沒給的不動。links／hide_links 的路徑要在關聯樹上；其他的照觀測時的規則驗（用的時候大聲擋）。
    /// </summary>
    static string? WorkPreset(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改 preset");
        string aName = c.Args.Get("name");
        if (SCP_SculptViewPresets.ValidName(aName) is string aBadName) return Blocked(c, 2, aBadName);
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptViewPresetFile aFile = SCP_SculptViewPresets.Read(aPaths);
        int aIdx = aFile.presets.FindIndex(p => p.name == aName);
        var aGiven = SCP_SculptViewPresets.Keys.Where(c.Args.IsExplicit).ToList();
        bool aNote = c.Args.IsExplicit("note");
        if (aGiven.Count == 0 && !aNote)
            return Blocked(c, 2, (aIdx >= 0 ? "preset `" + aName + "` 已經在了 —— 沒有要改什麼" : "新的 preset 至少要給一個參數") + "（收：" + string.Join("、", SCP_SculptViewPresets.Keys) + "、note）");
        SCP_SculptViewPreset aPreset = aIdx >= 0 ? aFile.presets[aIdx] : new SCP_SculptViewPreset
        {
            name = aName, persona = c.Persona,
            created_at = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };
        foreach (string k in aGiven) SCP_SculptViewPresets.Set(aPreset, k, c.Args.Get(k));
        if (aNote) aPreset.note = c.Args.Get("note").Trim();
        // 關聯路徑要在樹上（打錯的名字現在就擋，⛔ 不等 Tim 在下拉選了才報）
        string aLinks = aPreset.links, aHide = aPreset.hide_links;
        if (aLinks.Length > 0 || aHide.Length > 0)
        {
            var aAll = new List<string>();
            try { SCP_SculptLinks.Paths(SCP_SculptLinks.Tree(c.Works, iCard.id), aAll); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, "關聯樹讀不了：" + e.Message + "；未寫入"); }
            var aBad = new List<string>();
            foreach (string s in (aLinks + "," + aHide).Split(','))
            {
                string p = s.Trim();
                if (p.Length == 0 || p == "none" || p == "all" || p == SCP_SculptLinks.Self) continue;
                if (!aAll.Contains(p)) aBad.Add(p);
            }
            if (aBad.Count > 0) return Blocked(c, 2, "關聯路徑不在樹上：" + string.Join("、", aBad) + "（有：" + (aAll.Count == 0 ? "（沒有關聯）" : string.Join("、", aAll)) + "）；未寫入");
        }
        if (aIdx < 0) aFile.presets.Add(aPreset);
        SCP_SculptViewPresets.Save(aPaths, aFile);
        c.Result.AddValue("preset", aPreset.name);
        c.Result.AddValue("preset_args", string.Join(";", SCP_SculptViewPresets.Pairs(aPreset).Select(kv => kv.Key + "=" + kv.Value)));
        c.Result.AddValue("preset_count", aFile.presets.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("presets_file", aPaths.ViewPresetsFile.Replace('\\', '/'));
        AppendPresets(c, aFile);
        return Finish(c, 0, "✓ preset `" + aPreset.name + "`：" + SCP_SculptViewPresets.Describe(aPreset) + "｜觀測帶 preset=" + aPreset.name + "；觀測頁的下拉選得到");
    }

    /// <summary>work sub=unpreset name=：拿掉一組。</summary>
    static string? WorkUnpreset(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改 preset");
        string aName = c.Args.Get("name").Trim();
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptViewPresetFile aFile = SCP_SculptViewPresets.Read(aPaths);
        int aIdx = aFile.presets.FindIndex(p => p.name == aName);
        if (aIdx < 0) return Blocked(c, 2, "沒有 preset `" + aName + "`（有：" + (aFile.presets.Count == 0 ? "（沒有）" : string.Join("、", aFile.presets.ConvertAll(p => p.name))) + "）");
        aFile.presets.RemoveAt(aIdx);
        SCP_SculptViewPresets.Save(aPaths, aFile);
        c.Result.AddValue("preset_count", aFile.presets.Count.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ 拿掉 preset `" + aName + "`");
    }

    /// <summary>work sub=presets：列出這件作品的 preset。</summary>
    static string? WorkPresets(Ctx c, SCP_SculptWork iCard)
    {
        SCP_SculptViewPresetFile aFile = SCP_SculptViewPresets.Read(c.Works.SpacePaths(iCard.id));
        c.Result.AddValue("preset_count", aFile.presets.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("preset_names", string.Join(";", aFile.presets.ConvertAll(p => p.name)));
        AppendPresets(c, aFile);
        return Finish(c, 0, "✓ preset " + aFile.presets.Count + " 組（＋all ＝ 正常顯示）");
    }

    static void AppendPresets(Ctx c, SCP_SculptViewPresetFile iFile)
    {
        c.Report.Append("\n## 預覽 preset（觀測帶 preset=<名字>；觀測頁「預覽 preset」下拉，預設 all ＝ 正常顯示）\n- `all` ＝ 正常顯示（保留字）\n");
        foreach (var p in iFile.presets)
            c.Report.Append("- `").Append(p.name).Append("`：").Append(SCP_SculptViewPresets.Describe(p)).Append(p.note.Length > 0 ? "｜" + p.note : "").Append('\n');
    }
}
