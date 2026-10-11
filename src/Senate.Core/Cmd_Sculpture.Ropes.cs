// 區塊職責：`sculpture` 的固定點與繩索（TASK-0502）—— work sub=anchor|unanchor|anchors|rope|unrope|ropes，與觀測（view／export）時把繩算好、畫進去。
// 物理意義：資料與形狀全在 SCP_Core `SCP_SculptRopes`（anchors.json／ropes.json、懸鏈線、關聯路徑換座標）；本檔只做參數 → 驗 → 寫 → 讀數。
//          ⛔ 一格 voxel 都不動（不進 events、不收費）。改作品的資料 ⇒ 只有作品作者（同 link）。
//          觀測：繩跟著關聯走 —— links=／hide_links= 選掉的那一層，綁在它上面的繩跟著不畫（state=hidden）；
//                ropes=0 ⇒ 這次不畫繩；hide_ropes=路徑,… ⇒ 這幾條不畫（打錯路徑 ⇒ 擋，⛔ 不變成「什麼都沒關」）。
//                綁定壞掉（固定點刪了／改名、關聯路徑不在了）⇒ state=broken，**每次觀測都列出來**（⛔ 不靜默消失）。
// 數值影響：view 的網格多了管子（voxel 數讀數不變）；export obj 多了管狀網格（list／vox 只有格子 ⇒ 明說繩沒匯出）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    /// <summary>會把繩畫進去的觀測 op（其餘觀測 op 只看格子：slice／section／stats 是 voxel 的讀數）。</summary>
    static readonly string[] RopeViewOps = { "view", "export" };

    // 觀測時的繩跟帆一起算（綁帆角的繩要等帆模擬完才知道形狀）⇒ Cmd_Sculpture.Sails.cs 的 ResolveViewRig（TASK-0503）。

    /// <summary>每條繩一行：狀態、兩端、長度、下垂、顏色粗細、原因 ＋ 回傳值。</summary>
    static void AppendRopeReport(Ctx c, List<SCP_SculptRopeReading> iReadings, bool iAlways = false)
    {
        if (iReadings.Count == 0 && !iAlways) return;
        int aOk = 0, aHidden = 0, aBroken = 0, aDisabled = 0;
        c.Report.Append("\n## 繩索（不是 voxel；長度與下垂量是讀數 —— 單位：格）\n");
        if (iReadings.Count == 0) c.Report.Append("（沒有 —— `--arg op=work --arg sub=rope --arg name=<名> --arg from=<固定點|關聯/…/固定點|x,y,z> --arg to=<…> [--arg slack=1.05] [--arg color=] [--arg thickness=]`）\n");
        foreach (var r in iReadings)
        {
            switch (r.State)
            {
                case SCP_SculptRopeReading.Ok: aOk++; break;
                case SCP_SculptRopeReading.Hidden: aHidden++; break;
                case SCP_SculptRopeReading.Broken: aBroken++; break;
                default: aDisabled++; break;
            }
            string aIcon = r.State == SCP_SculptRopeReading.Ok ? "✓" : r.State == SCP_SculptRopeReading.Broken ? "🔴" : "◌";
            c.Report.Append("- ").Append(aIcon).Append(" `").Append(r.Path).Append("`［").Append(r.State).Append("］ ")
                    .Append(r.Rope.from).Append(" (").Append(SCP_SculptRopes.Fmt(r.From)).Append(") → ")
                    .Append(r.Rope.to).Append(" (").Append(SCP_SculptRopes.Fmt(r.To)).Append(")");
            if (r.From != null && r.To != null)
                c.Report.Append("｜長 ").Append(F2(r.Length)).Append("（直線 ").Append(F2(r.Chord)).Append("、slack ").Append(F2(r.Rope.slack)).Append("）｜最大下垂 ").Append(F2(r.MaxSag));
            c.Report.Append("｜色 ").Append(r.Rope.color).Append("、粗 ").Append(F2(r.Rope.thickness));
            if (r.Reason.Length > 0) c.Report.Append("｜").Append(r.Reason);
            c.Report.Append('\n');
        }
        c.Result.AddValue("ropes_drawn", aOk.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("ropes_hidden", aHidden.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("ropes_disabled", aDisabled.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("ropes_broken", aBroken.ToString(CultureInfo.InvariantCulture));
        if (aBroken > 0)
            c.Result.Lines.Add("🔴 繩索：" + aBroken + " 條綁定壞了（" + string.Join("、", iReadings.FindAll(r => r.State == SCP_SculptRopeReading.Broken).ConvertAll(r => r.Path)) + "）—— 原因見回傳檔；沒畫");
    }

    static string F2(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    // ───────────────────────── work sub=anchor／unanchor／anchors ─────────────────────────

    /// <summary>work sub=anchor name= at=x,y,z [note=]：加一個固定點；同名已在 ⇒ 只給 note 只改 note；要搬位置 overwrite=1。</summary>
    static string? WorkAnchor(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改固定點");
        string aName = c.Args.Get("name");
        if (SCP_SculptRopes.ValidName(aName, "固定點") is string aBadName) return Blocked(c, 2, aBadName);
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptAnchorFile aFile = SCP_SculptRopes.ReadAnchors(aPaths);
        int aIdx = aFile.anchors.FindIndex(a => a.name == aName);
        SCP_SculptAnchor aAnchor;
        if (aIdx >= 0 && !c.Args.IsExplicit("at"))
        {
            if (!c.Args.IsExplicit("note")) return Blocked(c, 2, "固定點 `" + aName + "` 已經在了（@" + string.Join(",", aFile.anchors[aIdx].at) + "）—— 沒有要改什麼（搬位置要 at= 加 overwrite=1）");
            aAnchor = aFile.anchors[aIdx];
            aAnchor.note = c.Args.Get("note").Trim();
        }
        else
        {
            if (!c.Args.IsExplicit("at")) return Blocked(c, 2, "加固定點要 name=<名> 與 at=x,y,z（格座標；點取格心）");
            if (aIdx >= 0 && !WorkOverwrite(c)) return Blocked(c, 2, "已經有固定點 `" + aName + "`（@" + string.Join(",", aFile.anchors[aIdx].at) + "）—— ⛔ 不默默搬走（綁在上面的繩會跟著動）；要搬請加 overwrite=1");
            int[] aAt = WorkVector(c.Args.Get("at"), "at");
            if (aAt[0] < 0 || aAt[1] < 0 || aAt[2] < 0 || aAt[0] >= iCard.SizeX || aAt[1] >= iCard.SizeY || aAt[2] >= iCard.SizeZ)
                return Blocked(c, 2, "at=" + string.Join(",", aAt) + " 不在作品 " + iCard.Dimensions + " 裡（0..尺寸−1）；未寫入");
            SCP_SculptAnchor? aOld = aIdx >= 0 ? aFile.anchors[aIdx] : null;
            aAnchor = new SCP_SculptAnchor
            {
                name = aName, at = new List<int>(aAt),
                note = c.Args.IsExplicit("note") ? c.Args.Get("note").Trim() : aOld?.note ?? "",
                persona = aOld?.persona ?? c.Persona,
                created_at = aOld?.created_at ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            };
            if (aIdx >= 0) aFile.anchors[aIdx] = aAnchor; else aFile.anchors.Add(aAnchor);
        }
        SCP_SculptRopes.SaveAnchors(aPaths, aFile);
        c.Result.AddValue("anchor", aAnchor.name);
        c.Result.AddValue("anchor_at", string.Join(",", aAnchor.at));
        c.Result.AddValue("anchor_count", aFile.anchors.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("anchors_file", aPaths.AnchorsFile.Replace('\\', '/'));
        return Finish(c, 0, "✓ 固定點 `" + aAnchor.name + "` @" + string.Join(",", aAnchor.at) + "（點 ＝ 格心）｜不是 voxel，一格都沒動");
    }

    /// <summary>work sub=unanchor name=：拿掉固定點；綁著它的繩（全庫掃，含上層作品用關聯路徑綁的）⇒ 列出來（它們之後每次觀測都會是 broken）。</summary>
    static string? WorkUnanchor(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改固定點");
        string aName = c.Args.Get("name").Trim();
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptAnchorFile aFile = SCP_SculptRopes.ReadAnchors(aPaths);
        int aIdx = aFile.anchors.FindIndex(a => a.name == aName);
        if (aIdx < 0) return Blocked(c, 2, "沒有固定點 `" + aName + "`（現有：" + (aFile.anchors.Count == 0 ? "（沒有）" : string.Join("、", aFile.anchors.ConvertAll(a => a.name))) + "）");
        aFile.anchors.RemoveAt(aIdx);
        SCP_SculptRopes.SaveAnchors(aPaths, aFile);
        c.Result.AddValue("anchor_count", aFile.anchors.Count.ToString(CultureInfo.InvariantCulture));
        // 全庫掃（TASK-0503 起連帆一起掃：帆角也可能綁在這個固定點上）—— 本體在 Cmd_Sculpture.Sails.cs
        List<string> aBroken = BrokenRiggingAcrossWorks(c, iCard.id, out int aRopesBroken, out int aSailsBroken);
        c.Result.AddValue("ropes_now_broken", aRopesBroken.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("sails_now_broken", aSailsBroken.ToString(CultureInfo.InvariantCulture));
        if (aBroken.Count > 0)
        {
            c.Report.Append("\n## 🔴 現在綁定壞掉的繩與帆（全庫掃：所在作品 → 路徑｜原因）\n");
            foreach (string s in aBroken) c.Report.Append("- ").Append(s).Append('\n');
            c.Result.Lines.Add("🔴 拿掉之後有 " + aRopesBroken + " 條繩、" + aSailsBroken + " 面帆綁定壞了 —— 它們沒被刪，每次觀測都會列成 broken（清單見回傳檔）");
        }
        return Finish(c, 0, "✓ 拿掉固定點 `" + aName + "`" + (aBroken.Count > 0 ? "｜🔴 " + aRopesBroken + " 條繩、" + aSailsBroken + " 面帆的綁定壞了" : "｜沒有繩或帆綁在上面"));
    }

    /// <summary>work sub=anchors：本作品的固定點＋關聯樹上每一層的（路徑指名、換到本作品座標）。</summary>
    static string? WorkAnchors(Ctx c, SCP_SculptWork iCard)
    {
        List<SCP_SculptLinkNode>? aTree = null;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { c.Report.Append("- ⚠ 關聯樹讀不了（只列本作品）：").Append(e.Message).Append('\n'); }
        var aIndex = SCP_SculptRopes.IndexTree(aTree);
        var aFrames = new List<string> { "" };
        SCP_SculptLinks.Paths(aTree ?? new List<SCP_SculptLinkNode>(), aFrames);
        int aOwn = 0, aAll = 0;
        c.Report.Append("## 固定點（繩的端點寫「名字」或「關聯路徑/名字」；座標 ＝ 換到本作品的格心）\n");
        foreach (string f in aFrames)
        {
            string aWork = f.Length == 0 ? iCard.id : aIndex[f].Card.id;
            SCP_SculptAnchorFile aFile;
            try { aFile = SCP_SculptRopes.ReadAnchors(c.Works.SpacePaths(aWork)); }
            catch (InvalidOperationException e) { c.Report.Append("- ⚠ `").Append(f).Append("`（").Append(aWork).Append("）：").Append(e.Message).Append('\n'); continue; }
            foreach (SCP_SculptAnchor a in aFile.anchors)
            {
                string aRef = f.Length == 0 ? a.name : f + "/" + a.name;
                double[] p = SCP_SculptRopes.ToRoot(f, new[] { a.at[0] + 0.5, a.at[1] + 0.5, a.at[2] + 0.5 }, aIndex);
                c.Report.Append("- `").Append(aRef).Append("` ← ").Append(aWork).Append(" @").Append(string.Join(",", a.at))
                        .Append(f.Length > 0 ? " ⇒ 本作品 (" + SCP_SculptRopes.Fmt(p) + ")" : " (" + SCP_SculptRopes.Fmt(p) + ")")
                        .Append(a.note.Length > 0 ? "｜" + a.note : "").Append('\n');
                aAll++; if (f.Length == 0) aOwn++;
            }
        }
        if (aAll == 0) c.Report.Append("（沒有 —— `--arg op=work --arg sub=anchor --arg name=<名> --arg at=x,y,z`）\n");
        c.Result.AddValue("anchor_count", aOwn.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("anchor_count_all", aAll.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ 固定點：本作品 " + aOwn + " 個、含關聯共 " + aAll + " 個");
    }

    // ───────────────────────── work sub=rope／unrope／ropes ─────────────────────────

    /// <summary>
    /// work sub=rope name= from= to= [color=] [thickness=] [slack=] [enabled=] [note=]：加（或改）一條繩。
    /// 同名已在：改 color／thickness／slack／enabled／note 直接改；改兩端要 overwrite=1（⛔ 不默默把繩換到別的地方）。
    /// 寫之前解一次：端點解不到（固定點或關聯路徑不存在）⇒ 擋、未寫入（⛔ 打錯字不會變成一條壞繩）。
    /// </summary>
    static string? WorkRope(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改繩索");
        string aName = c.Args.Get("name");
        if (SCP_SculptRopes.ValidName(aName, "繩") is string aBadName) return Blocked(c, 2, aBadName);
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptRopeFile aFile = SCP_SculptRopes.ReadRopes(aPaths);
        int aIdx = aFile.ropes.FindIndex(r => r.name == aName);
        bool aEnds = c.Args.IsExplicit("from") || c.Args.IsExplicit("to");
        bool aProps = c.Args.IsExplicit("color") || c.Args.IsExplicit("thickness") || c.Args.IsExplicit("slack") || c.Args.IsExplicit("enabled") || c.Args.IsExplicit("note");
        SCP_SculptRope? aOld = aIdx >= 0 ? aFile.ropes[aIdx] : null;
        if (aOld != null && !aEnds && !aProps) return Blocked(c, 2, "繩 `" + aName + "` 已經在了 —— 沒有要改什麼（color／thickness／slack／enabled／note 直接改；換兩端要 from=／to= 加 overwrite=1）");
        if (aOld != null && aEnds && !WorkOverwrite(c)) return Blocked(c, 2, "已經有繩 `" + aName + "`（" + aOld.from + " → " + aOld.to + "）—— ⛔ 不默默換端點；要換請加 overwrite=1");
        if (aOld == null && (!c.Args.IsExplicit("from") || !c.Args.IsExplicit("to"))) return Blocked(c, 2, "加繩要 name=<名> from=<端點> to=<端點>（端點：固定點名｜關聯/…/固定點名｜x,y,z）");

        var aRope = new SCP_SculptRope
        {
            name = aName,
            from = c.Args.IsExplicit("from") ? c.Args.Get("from").Trim() : aOld!.from,
            to = c.Args.IsExplicit("to") ? c.Args.Get("to").Trim() : aOld!.to,
            color = aOld?.color ?? SCP_SculptRopes.DefaultColor,
            thickness = aOld?.thickness ?? SCP_SculptRopes.DefaultThickness,
            slack = aOld?.slack ?? 1.0,
            enabled = aOld?.enabled ?? true,
            note = c.Args.IsExplicit("note") ? c.Args.Get("note").Trim() : aOld?.note ?? "",
            persona = aOld?.persona ?? c.Persona,
            created_at = aOld?.created_at ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        };
        foreach (string e in new[] { aRope.from, aRope.to })
            if (!SCP_SculptRopes.TryParseEndpoint(e, out _, out _, out string aBadEnd)) return Blocked(c, 2, aBadEnd + "；未寫入");
        if (c.Args.IsExplicit("color"))
        {
            if (SCP_SculptRopes.TryParseColor(c.Args.Get("color"), out _, out _, out _, out string aNorm) is string aBadColor) return Blocked(c, 2, aBadColor + "；未寫入");
            aRope.color = aNorm;
        }
        if (c.Args.IsExplicit("thickness"))
        {
            if (!TryFinite(c.Args.Get("thickness").Trim(), out double t)) return Blocked(c, 2, "thickness 不是數字；未寫入");
            aRope.thickness = t;
        }
        if (c.Args.IsExplicit("slack"))
        {
            if (!TryFinite(c.Args.Get("slack").Trim(), out double s)) return Blocked(c, 2, "slack 不是數字；未寫入");
            aRope.slack = s;
        }
        if (SCP_SculptRopes.CheckThickness(aRope.thickness) is string aBadT) return Blocked(c, 2, aBadT + "；未寫入");
        if (SCP_SculptRopes.CheckSlack(aRope.slack, SCP_SculptRopes.OnSail(aRope)) is string aBadS) return Blocked(c, 2, aBadS + "；未寫入");
        if (c.Args.IsExplicit("enabled"))
        {
            string v = c.Args.Get("enabled").Trim();
            if (v != "0" && v != "1") return Blocked(c, 2, "enabled 要是 1 或 0（got '" + v + "'）；未寫入");
            aRope.enabled = v == "1";
        }
        if (aIdx >= 0) aFile.ropes[aIdx] = aRope; else aFile.ropes.Add(aRope);

        // 寫之前用「寫了之後的樣子」解一次（同一支 Resolve，本作品那份換成還沒存的 aFile；關聯全開 ⇒ 只判綁不綁得到，不判這次畫不畫）：端點解不到 ⇒ 擋
        List<SCP_SculptLinkNode>? aTree = null;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, "關聯樹讀不了（端點驗不了）：" + e.Message + "；未寫入"); }
        // 帆角端點（sail:）要靠帆的觀測解 ⇒ 走 SCP_SculptSails.Rig（裡面是同一支 Resolve，帆角由它注入；沒模擬 ⇒ 長度是設計位置的讀數）
        SCP_SculptRopeReading? aReading = SCP_SculptSails.Rig(c.Works, iCard.id, aTree, SCP_SculptLinkSelection.Parse("all", ""), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                                              new SCP_SculptSailEnv { MetersPerVoxel = iCard.MetersPerVoxel }, null, aFile)
                                          .Ropes.Find(r => r.Path == aRope.name);
        if (aReading == null) return Blocked(c, 1, "寫之前的回讀沒找到這條繩（不該發生）；未寫入");
        if (aReading.State == SCP_SculptRopeReading.Broken) return Blocked(c, 2, "端點解不到：" + aReading.Reason + "；未寫入（先 sub=anchor 加固定點，或用 sub=anchors 看有哪些）");
        SCP_SculptRopes.SaveRopes(aPaths, aFile);
        c.Result.AddValue("rope", aRope.name);
        c.Result.AddValue("rope_length", F2(aReading.Length));
        c.Result.AddValue("rope_chord", F2(aReading.Chord));
        c.Result.AddValue("rope_max_sag", F2(aReading.MaxSag));
        c.Result.AddValue("rope_count", aFile.ropes.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("ropes_file", aPaths.RopesFile.Replace('\\', '/'));
        AppendRopeReport(c, new List<SCP_SculptRopeReading> { aReading });
        return Finish(c, 0, "✓ 繩 `" + aRope.name + "` " + aRope.from + " → " + aRope.to + "｜長 " + F2(aReading.Length) + "、最大下垂 " + F2(aReading.MaxSag) + (aRope.enabled ? "" : "（關）") + "｜不是 voxel，一格都沒動");
    }

    /// <summary>work sub=unrope name=：拿掉一條繩。</summary>
    static string? WorkUnrope(Ctx c, SCP_SculptWork iCard)
    {
        if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "只有作品作者 " + iCard.owner + " 可以改繩索");
        string aName = c.Args.Get("name").Trim();
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptRopeFile aFile = SCP_SculptRopes.ReadRopes(aPaths);
        int aIdx = aFile.ropes.FindIndex(r => r.name == aName);
        if (aIdx < 0) return Blocked(c, 2, "沒有繩 `" + aName + "`（現有：" + (aFile.ropes.Count == 0 ? "（沒有）" : string.Join("、", aFile.ropes.ConvertAll(r => r.name))) + "）");
        aFile.ropes.RemoveAt(aIdx);
        SCP_SculptRopes.SaveRopes(aPaths, aFile);
        c.Result.AddValue("rope_count", aFile.ropes.Count.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ 拿掉繩 `" + aName + "`");
    }

    /// <summary>work sub=ropes：每條繩的讀數（本作品＋關聯樹上的；關聯照每條 enabled）—— 長度、最大下垂、狀態。</summary>
    static string? WorkRopes(Ctx c, SCP_SculptWork iCard)
    {
        List<SCP_SculptLinkNode>? aTree = null;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { c.Report.Append("- ⚠ 關聯樹讀不了（綁在關聯上的繩會列成 broken）：").Append(e.Message).Append('\n'); }
        // 綁帆角的繩：位置是帆角的設計位置（沒模擬；模擬完的長度與下垂 ⇒ sub=sails）
        List<SCP_SculptRopeReading> aReadings = SCP_SculptSails.Rig(c.Works, iCard.id, aTree, new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(),
                                                                    new SCP_SculptSailEnv { MetersPerVoxel = iCard.MetersPerVoxel }).Ropes;
        AppendRopeReport(c, aReadings, true);
        c.Result.AddValue("rope_count", aReadings.Count.ToString(CultureInfo.InvariantCulture));
        int aBroken = aReadings.FindAll(r => r.State == SCP_SculptRopeReading.Broken).Count;
        return Finish(c, 0, "✓ 繩 " + aReadings.Count + " 條（含關聯）" + (aBroken > 0 ? "｜🔴 " + aBroken + " 條綁定壞了" : ""));
    }
}
