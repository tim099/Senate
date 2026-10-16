// 區塊職責：關聯匯入的 CLI（TASK-0494）—— work sub=link／unlink／links；觀測類 op 的 links=／hide_links=；
//          關聯樹的鎖（樹上所有作品連同本作品依 ID 排序一次拿齊）；觀測與攤平的讀數（放了幾格、被蓋掉、越界、用了哪幾件的哪個版本）。
// 物理意義：關聯只記在 links.json（不進 events）⇒ 觀測時現組；寫入類 op 只動本作品，落子後回報「這一刀的範圍裡有幾格是關聯進來的」
//          （那些格沒被動到 —— 要改去改那件作品）。「子作品就是獨立作品，只是創作時切出來的」⇒ 任何作品都能被關聯。
// 失敗處置：links／hide_links 給在非觀測類 op、名字不存在、關聯成環、被關聯的作品讀不了 ⇒ exit 2；
//          加關聯時放不下、成環（連同還沒存的那一筆一起驗、含停用的關聯；關聯＋模型繞成的也算）⇒ exit 2、零寫入。關聯的增刪先拿 registry 鎖（全庫序列化）。
using System.Globalization;
using SCP.Core.Io;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    /// <summary>會把關聯組進來的觀測類 op（exhibit 的展品照也照看得到的樣子出）。</summary>
    static readonly string[] LinkViewOps = { "view", "slice", "section", "stats", "export", "exhibit" };

    /// <summary>
    /// 讀關聯樹、解析 links=／hide_links=。觀測類 ⇒ <see cref="Ctx.Links"/>（組裝器）；寫入類 ⇒ 只留樹（落子後回報 linked_hits）。
    /// links=none ⇒ 不讀樹、不組（樹壞了也看得到本作品）。
    /// </summary>
    static bool ConfigureLinks(Ctx c, out string oError)
    {
        oError = "";
        string aLinks = c.Args.Get("links").Trim(), aHide = c.Args.Get("hide_links").Trim();
        bool aGiven = aLinks.Length > 0 || aHide.Length > 0;
        bool aView = Array.IndexOf(LinkViewOps, c.Op) >= 0 && c.Op != "exhibit";
        if (c.Op == "work" && c.Args.Get("sub").Trim() == "preset") return true;   // TASK-0571：存 preset ⇒ links／hide_links 是要存的字（WorkPreset 驗路徑）
        if (aGiven && !aView)
        {
            oError = "links／hide_links 只給觀測類 op（view／slice／section／stats／export）—— 匯入展區／assemble 一律照每條關聯的 enabled 攤平；diff 只比本作品自己的事件";
            return false;
        }
        if (c.Work == null)
        {
            if (aGiven) { oError = "關聯是作品的 —— 要帶 work="; return false; }
            return true;
        }
        if (aLinks == "none" && aHide.Length == 0) return true;
        bool aWrite = Array.IndexOf(DraftWriteOps, c.Op) >= 0;
        if (Array.IndexOf(LinkViewOps, c.Op) < 0 && !aWrite) return true;
        try
        {
            c.LinkTree = SCP_SculptLinks.Tree(c.Works, c.Work.id);
            if (c.LinkTree.Count == 0)
            {
                if (aGiven) { oError = "作品 " + c.Work.id + " 沒有任何關聯（links.json 沒有內容）—— links／hide_links 沒東西可選"; return false; }
                c.LinkTree = null;
                return true;
            }
            if (Array.IndexOf(LinkViewOps, c.Op) >= 0)
                c.Links = new SCP_SculptLinkComposer(c.Works, c.Roots.DataRoot, c.LinkTree, SCP_SculptLinkSelection.Parse(aLinks, aHide));
            return true;
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException)
        {
            if (aWrite) { c.LinkTree = null; c.Report.Append("- ⚠ 關聯樹讀不了（落子照常；linked_hits 不算）：").Append(e.Message).Append('\n'); return true; }
            oError = e.Message + (aGiven ? "" : "（只想看本作品：links=none）");
            return false;
        }
    }

    /// <summary>一次拿齊的一組作品鎖（依 ID 排序拿；放的時候反過來放）。</summary>
    sealed class LockSet : IDisposable
    {
        readonly List<SCP_FileLock> m_Locks = new List<SCP_FileLock>();
        public void Add(SCP_FileLock iLock) => m_Locks.Add(iLock);
        public void Dispose()
        {
            for (int i = m_Locks.Count - 1; i >= 0; i--) m_Locks[i].Dispose();
            m_Locks.Clear();
        }
    }

    /// <summary>依 ID 排序拿這幾件作品的 _engine 鎖（全系統只有這一種鎖序 ⇒ 不會互等）。拿不到 ⇒ 已拿的放掉、丟逾時例外。</summary>
    static LockSet LockAll(Ctx c, IEnumerable<string> iIds)
    {
        var aSet = new LockSet();
        try
        {
            foreach (string id in new SortedSet<string>(iIds, StringComparer.Ordinal))
                aSet.Add(SCP_FileLock.Acquire(c.Works.EngineLock(id), EngineLockTimeoutSec));
            return aSet;
        }
        catch { aSet.Dispose(); throw; }
    }

    /// <summary>這一個 op 要的鎖：共用展區 ⇒ 展區那顆；作品 ⇒ 作品＋它關聯樹上所有作品（依 ID 排序）。</summary>
    static IDisposable LockWorks(Ctx c)
    {
        if (c.Work == null) return SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec);
        var aIds = new SortedSet<string>(StringComparer.Ordinal) { c.Work.id };
        if (c.LinkTree != null) SCP_SculptLinks.Closure(c.LinkTree, aIds);
        return LockAll(c, aIds);
    }

    /// <summary>觀測完把組裝的讀數接到回報與回傳值（沒有關聯 ⇒ 不印）。</summary>
    static void AppendLinkReport(Ctx c, SCP_SculptComposeReport? iReport)
    {
        if (iReport == null) return;
        var aUsed = new List<string>();
        c.Report.Append("## 關聯（現組，不在 events）\n");
        if (iReport.HideSelf) c.Report.Append("- 本作品自己的格：不畫（hide_links=self）\n");
        foreach (var r in iReport.Rows)
        {
            aUsed.Add(r.Path + "=" + r.Work + "@" + r.Revision.Substring(0, Math.Min(12, r.Revision.Length)) + (r.Draft ? "(草稿)" : ""));
            c.Report.Append("- `").Append(r.Path).Append("` ← ").Append(r.Work).Append(r.Draft ? "（**草稿**）" : "")
                    .Append("：放 ").Append(r.Placed).Append(r.Overlap > 0 ? "、被蓋掉 " + r.Overlap : "").Append(r.Clipped > 0 ? "、越界丟掉 " + r.Clipped : "")
                    .Append(r.PivotDrift ? "｜⚠ 那件作品的尺寸跟加關聯時不一樣（照加關聯時的軸心轉）" : "").Append('\n');
        }
        if (iReport.Rows.Count == 0) c.Report.Append("（這次沒有選到任何關聯）\n");
        foreach (string s in iReport.Samples) c.Report.Append("  · ").Append(s).Append('\n');
        c.Result.AddValue("links_used", aUsed.Count == 0 ? "none" : string.Join(";", aUsed));
        c.Result.AddValue("link_placed", iReport.Placed.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("link_overlap", iReport.Overlap.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("link_clipped", iReport.Clipped.ToString(CultureInfo.InvariantCulture));
        if (iReport.Overlap > 0 || iReport.Clipped > 0)
            c.Result.Lines.Add("⚠ 關聯：被蓋掉 " + iReport.Overlap + " 格、越界丟掉 " + iReport.Clipped + " 格（本作品的格優先；關聯照清單順序先到的贏）—— 位置見回傳檔");
    }

    /// <summary>
    /// 落子之後：這一刀的範圍裡有幾格是關聯進來的（那些格沒被動到）。box／carve ＝ 框；stampvox／carvevox ＝ 清單上的格；貼圖類不算。
    /// 只讀（不存任何快取）；樹讀不了就不算。
    /// </summary>
    static void AppendLinkedHits(Ctx c)
    {
        if (c.LinkTree == null || c.Work == null) return;
        if (c.Op != "box" && c.Op != "carve" && c.Op != "stampvox" && c.Op != "carvevox") return;   // 貼圖類不算（先判，⛔ 不白組一次）
        SCP_SculptSpace aLinked;
        try
        {
            aLinked = new SCP_SculptLinkComposer(c.Works, c.Roots.DataRoot, c.LinkTree, SCP_SculptLinkSelection.Parse("", SCP_SculptLinks.Self))
                .Compose(new SCP_SculptSpace(), c.Work.SizeX, c.Work.SizeY, c.Work.SizeZ);
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException)
        {
            // ⚠ 這一刀已經落了 ⇒ 落子後的讀數失敗只能是警告（⛔ 不能讓已落的刀看起來像失敗 —— 重跑就是第二刀）
            c.Result.AddValue("linked_hits", "skipped");
            c.Report.Append("- ⚠ linked_hits 沒算（關聯的作品讀不了）：").Append(e.Message).Append('\n');
            return;
        }
        int aHits = 0;
        if (c.Op == "box" || c.Op == "carve")
        {
            if (!TryInt(c, "x1", out int x1) || !TryInt(c, "x2", out int x2) || !TryInt(c, "y1", out int y1) || !TryInt(c, "y2", out int y2) || !TryInt(c, "z1", out int z1) || !TryInt(c, "z2", out int z2)) return;
            foreach (var v in aLinked.Voxels.Entries())
                if (v.X >= Math.Min(x1, x2) && v.X <= Math.Max(x1, x2) && v.Y >= Math.Min(y1, y2) && v.Y <= Math.Max(y1, y2) && v.Z >= Math.Min(z1, z2) && v.Z <= Math.Max(z1, z2)) aHits++;
        }
        else
        {
            string aList = c.Args.Get("voxels").Trim();
            string[] at = c.Args.Get("at").Split(',');
            if (at.Length != 3 || !int.TryParse(at[0], out int ax) || !int.TryParse(at[1], out int ay) || !int.TryParse(at[2], out int az)) return;
            if (!SCP_SculptEngine.TryParseCarveList(File.ReadAllBytes(aList), out List<int[]> aCells, out _)) return;
            foreach (int[] v in aCells) if (aLinked.Voxels.Get(v[0] + ax, v[1] + ay, v[2] + az) != 0) aHits++;
        }
        c.Result.AddValue("linked_hits", aHits.ToString(CultureInfo.InvariantCulture));
        if (aHits > 0)
        {
            c.Report.Append("- ⚠ 這一刀的範圍裡有 ").Append(aHits).Append(" 格是關聯進來的作品的格 —— 它們**沒被動到**（關聯不複製 voxel）；要改請去改那件作品\n");
            c.Result.Lines.Add("⚠ linked_hits=" + aHits + "：這一刀的範圍裡有關聯進來的格，沒被動到（要改去改那件作品）");
        }
    }

    /// <summary>攤平（匯入展區／assemble 的來源）：本作品＋照 enabled 的關聯。回傳組好的空間；讀數接到回報；Credit 逐件加進 <paramref name="ioCredits"/>。呼叫端已握樹上所有作品的鎖。</summary>
    static SCP_SculptSpace Flatten(Ctx c, SCP_SculptWork iCard, List<SCP_SculptLinkNode> iTree, List<SCP_SculptCredit> ioCredits)
    {
        SCP_SculptSpace aOwn = SCP_SculptStore.Load(c.Works.SpacePaths(iCard.id), false);   // 只讀（⛔ 不替來源寫快取）
        if (iTree.Count == 0) return aOwn;
        var aComposer = new SCP_SculptLinkComposer(c.Works, c.Roots.DataRoot, iTree, new SCP_SculptLinkSelection());
        SCP_SculptSpace aOut = aComposer.Compose(aOwn, iCard.SizeX, iCard.SizeY, iCard.SizeZ);
        AppendLinkReport(c, aComposer.Last);
        // Credit 照 assemble 的規矩：每件關聯進來的作品＋它自己歷史裡帶的上游 Credit（別人的零件不能在攤平時掉了作者）
        var aSeen = new HashSet<string>(StringComparer.Ordinal);
        void Add(SCP_SculptCredit iCredit) { if (aSeen.Add(iCredit.work + "|" + iCredit.author + "|" + iCredit.revision)) ioCredits.Add(iCredit); }
        foreach (var kv in aComposer.Last!.Works)
        {
            Add(new SCP_SculptCredit { work = kv.Key, title = kv.Value.Card.title, author = kv.Value.Card.owner, revision = kv.Value.Revision });
            foreach (var up in SCP_SculptHistory.Read(c.Works.SpacePaths(kv.Key)).Credits()) Add(up);
        }
        return aOut;
    }

    /// <summary>樹上的作品 id（不含根）。樹讀不了 ⇒ 例外照丟。</summary>
    static SortedSet<string> TreeIds(List<SCP_SculptLinkNode> iTree)
    {
        var aIds = new SortedSet<string>(StringComparer.Ordinal);
        SCP_SculptLinks.Closure(iTree, aIds);
        return aIds;
    }

    // ───────────────────────── work sub=link／unlink／links ─────────────────────────

    /// <summary>
    /// work sub=link：加（或改）一條關聯。同名已存在：只給 enabled／note ⇒ 只改這兩格；要改作品／位置／轉向 ⇒ overwrite=1。
    /// 驗：被關聯的作品存在且 ready、不是自己、轉向後放得進、整棵樹（連同這一筆、含停用的）不成環、關聯＋模型也不繞回本作品。呼叫端已握 registry 鎖與本作品鎖。
    /// </summary>
    static string? WorkLink(Ctx c, SCP_SculptWork iCard)
    {
        if (GameLayerPersona(c, "關聯") is string aNoPersona) return aNoPersona;   // TASK-0646：不限作者（Tim 定：不影響模型外觀的修改 ok）
        string aName = c.Args.Get("name");
        string? aBadName = SCP_SculptLinks.ValidName(aName);
        if (aBadName != null) return Blocked(c, 2, aBadName);
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptLinkFile aFile = SCP_SculptLinks.Read(aPaths);
        int aIdx = aFile.links.FindIndex(l => l.name == aName);
        bool aPos = c.Args.IsExplicit("source_work") || c.Args.IsExplicit("at") || c.Args.IsExplicit("turn") || c.Args.IsExplicit("mirror");
        bool aFlags = c.Args.IsExplicit("enabled") || c.Args.IsExplicit("note") || c.Args.IsExplicit("meta") || c.Args.IsExplicit("recolor");
        string aEnabled = c.Args.Get("enabled").Trim();
        if (c.Args.IsExplicit("enabled") && aEnabled != "0" && aEnabled != "1") return Blocked(c, 2, "enabled 要是 1 或 0（got '" + aEnabled + "'）");
        // 替換色（TASK-0584）：不動幾何 ⇒ 跟 enabled／note／meta 一樣直接改；先驗，壞了一格都不寫
        string? aRecolor = null;
        if (c.Args.IsExplicit("recolor"))
        {
            if (SCP_SculptLinks.NormalizeRecolor(c.Args.Get("recolor"), out string aNormRe, out _) is string aBadRe) return Blocked(c, 2, aBadRe + "；未寫入");
            aRecolor = aNormRe;
        }
        // meta（TASK-0517）：從既有那一筆出發合併；先在副本上驗，壞了一格都不寫
        SortedDictionary<string, SCP.Core.Json.SCP_JsonData> aMeta = SCP_SculptMeta.New();
        if (aIdx >= 0) foreach (var kv in aFile.links[aIdx].meta) aMeta[kv.Key] = kv.Value;
        if (c.Args.IsExplicit("meta") && SCP_SculptMeta.Merge(aMeta, c.Args.Get("meta")) is string aBadMeta) return Blocked(c, 2, aBadMeta + "；未寫入");

        SCP_SculptLink aLink;
        if (aIdx >= 0 && !aPos)
        {
            if (!aFlags) return Blocked(c, 2, "關聯 `" + aName + "` 已經在了 —— 沒有要改什麼（改開關 enabled=0|1、note、meta、recolor 直接改；改作品／位置／轉向／鏡像要 overwrite=1）");
            aLink = aFile.links[aIdx];
            if (c.Args.IsExplicit("enabled")) aLink.enabled = aEnabled == "1";
            if (c.Args.IsExplicit("note")) aLink.note = c.Args.Get("note").Trim();
            if (aRecolor != null) aLink.recolor = aRecolor;
            aLink.meta = aMeta;
        }
        else
        {
            if (aIdx >= 0 && !WorkOverwrite(c)) return Blocked(c, 2, "已經有關聯 `" + aName + "`（→ " + aFile.links[aIdx].work + "）—— ⛔ 不默默蓋掉；要改作品／位置／轉向／鏡像請加 overwrite=1");
            // overwrite：從既有那一筆出發，只改有給的格（⛔ 搬個位置不該把關掉的開關、轉向、鏡像、備註默默重設）
            SCP_SculptLink? aOld = aIdx >= 0 ? aFile.links[aIdx] : null;
            string aSource = c.Args.IsExplicit("source_work") ? c.Args.Get("source_work").Trim() : aOld?.work ?? "";
            if (aSource.Length == 0 || (aOld == null && !c.Args.IsExplicit("at"))) return Blocked(c, 2, "加關聯要 source_work=<作品> 與 at=x,y,z（turn= 選填 0/90/180/270；mirror= 選填 x／y／z 或組合）");
            SCP_SculptWork aChild;
            try { aChild = c.Works.Load(SCP_SculptWorks.NormalizeId(aSource)); }
            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException) { return Blocked(c, 2, "被關聯的作品讀不了：" + e.Message); }
            if (aChild.id == iCard.id) return Blocked(c, 2, "不能關聯自己");
            int[] aAt = c.Args.IsExplicit("at") ? WorkVector(c.Args.Get("at"), "at") : aOld!.at.ToArray();
            int aTurn = aOld?.turn ?? 0;
            if (c.Args.IsExplicit("turn") && (!TryInt(c, "turn", out aTurn) || (aTurn != 0 && aTurn != 90 && aTurn != 180 && aTurn != 270)))
                return Blocked(c, 2, "turn 要是 0/90/180/270（繞 Z 轉，轉法同 assemble）");
            string aMirror = aOld?.mirror ?? "";
            if (c.Args.IsExplicit("mirror") && SCP_SculptLinks.NormalizeMirror(c.Args.Get("mirror"), out aMirror) is string aBadMirror)
                return Blocked(c, 2, aBadMirror + "；未寫入");
            // 軸心：換了作品（或新關聯）才用那件作品現在的尺寸；同一件只搬位置／轉向 ⇒ 沿用加關聯當下記的
            int[] aPivot = aOld != null && aOld.work == aChild.id ? aOld.pivot.ToArray() : new[] { aChild.SizeX, aChild.SizeY, aChild.SizeZ };
            string? aFit = SCP_SculptLinks.Fits(aChild.id, aPivot, aAt, aTurn, iCard.SizeX, iCard.SizeY, iCard.SizeZ);
            if (aFit != null) return Blocked(c, 2, aFit + "（關聯要整件放得進來 —— 那件作品之後在自己的尺寸裡怎麼改都不會越界）；未寫入");
            aLink = new SCP_SculptLink
            {
                name = aName, work = aChild.id, at = new List<int>(aAt), turn = aTurn, mirror = aMirror, pivot = new List<int>(aPivot),
                enabled = c.Args.IsExplicit("enabled") ? aEnabled == "1" : aOld?.enabled ?? true,
                created_at = aOld?.created_at ?? DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                persona = aOld?.persona ?? c.Persona,
                note = c.Args.IsExplicit("note") ? c.Args.Get("note").Trim() : aOld?.note ?? "",
                recolor = aRecolor ?? aOld?.recolor ?? "",
                meta = aMeta,
            };
            if (aIdx >= 0) aFile.links[aIdx] = aLink; else aFile.links.Add(aLink);
        }
        // 整棵樹連同這一筆一起驗（含停用的關聯 —— links=all 會走到它）：成環、作品讀不了 ⇒ 不寫
        List<SCP_SculptLinkNode> aTree;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id, (iCard.id, aFile)); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, e.Message + "；未寫入"); }
        // 關聯樹只走關聯；「作品當模型」是另一種用到的邊 ⇒ 從被關聯的那件出發、兩種邊都走，走回本作品就是環（同 sub=model 的判法）。
        // 新的這一筆還沒存也不影響：那條路一走到本作品就停，用不到本作品自己的 links.json。
        List<string>? aLoop;
        try { aLoop = SCP_SculptModels.PathTo(c.Works, aLink.work, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException) { return Blocked(c, 2, "成環檢查讀不了作品：" + e.Message + "；未寫入"); }
        if (aLoop != null) return Blocked(c, 2, "關聯＋模型成環：" + iCard.id + " → " + string.Join(" → ", aLoop) + "（關聯與模型都算）；未寫入");
        SCP_SculptLinks.Save(aPaths, aFile);
        c.Result.AddValue("link", aLink.name);
        c.Result.AddValue("link_work", aLink.work);
        c.Result.AddValue("link_at", string.Join(",", aLink.at));
        c.Result.AddValue("link_turn", aLink.turn.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("link_mirror", aLink.mirror.Length == 0 ? "none" : aLink.mirror);
        c.Result.AddValue("link_recolor", aLink.recolor.Length == 0 ? "none" : aLink.recolor);
        c.Result.AddValue("links_schema", aFile.schema.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("link_enabled", aLink.enabled ? "1" : "0");
        c.Result.AddValue("link_count", aFile.links.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("links_file", aPaths.LinksFile.Replace('\\', '/'));
        AppendLinkTree(c, aTree);
        return Finish(c, 0, "✓ 關聯 `" + aLink.name + "` → " + aLink.work + " @" + string.Join(",", aLink.at) + " turn " + aLink.turn + (aLink.mirror.Length > 0 ? " mirror " + aLink.mirror : "") + (aLink.recolor.Length > 0 ? " recolor " + aLink.recolor : "") + (aLink.enabled ? "（開）" : "（關）") + "｜只記關聯，一格 voxel 都沒複製");
    }

    /// <summary>work sub=unlink name=：拿掉一條關聯（那件作品本身不動）。</summary>
    static string? WorkUnlink(Ctx c, SCP_SculptWork iCard)
    {
        if (GameLayerPersona(c, "關聯") is string aNoPersona) return aNoPersona;   // TASK-0646
        string aName = c.Args.Get("name").Trim();
        var aPaths = c.Works.SpacePaths(iCard.id);
        SCP_SculptLinkFile aFile = SCP_SculptLinks.Read(aPaths);
        int aIdx = aFile.links.FindIndex(l => l.name == aName);
        if (aIdx < 0) return Blocked(c, 2, "沒有關聯 `" + aName + "`（現有：" + (aFile.links.Count == 0 ? "（沒有）" : string.Join("、", aFile.links.ConvertAll(l => l.name))) + "）");
        string aWork = aFile.links[aIdx].work;
        aFile.links.RemoveAt(aIdx);
        SCP_SculptLinks.Save(aPaths, aFile);
        c.Result.AddValue("link_count", aFile.links.Count.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, "✓ 拿掉關聯 `" + aName + "`（→ " + aWork + "；那件作品本身沒動）");
    }

    /// <summary>work sub=links：列出整棵關聯樹（巢狀、開關、位置、轉向、尺寸是否變過、有沒有開著的草稿）。</summary>
    static string? WorkLinks(Ctx c, SCP_SculptWork iCard)
    {
        List<SCP_SculptLinkNode> aTree;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iCard.id); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return Blocked(c, 2, e.Message); }
        var aAll = new List<string>();
        SCP_SculptLinks.Paths(aTree, aAll);
        c.Result.AddValue("link_count", aTree.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("link_paths", string.Join(";", aAll));
        AppendLinkTree(c, aTree);
        return Finish(c, 0, "✓ 關聯 " + aTree.Count + " 條（含巢狀共 " + aAll.Count + " 條）");
    }

    static void AppendLinkTree(Ctx c, List<SCP_SculptLinkNode> iTree)
    {
        c.Report.Append("\n## 關聯樹（觀測時 links=none|all|路徑,… ／ hide_links=路徑,…|self；路徑用 / 分層）\n");
        if (iTree.Count == 0) c.Report.Append("（沒有 —— `--arg op=work --arg sub=link --arg name=<名> --arg source_work=<作品> --arg at=x,y,z [--arg turn=] [--arg mirror=x|y|z]`）\n");
        void Walk(List<SCP_SculptLinkNode> iNodes, int iDepth)
        {
            foreach (var n in iNodes)
            {
                c.Report.Append(new string(' ', iDepth * 2)).Append("- `").Append(n.Path).Append("` → ").Append(n.Card.id).Append("《").Append(n.Card.title).Append("》by ").Append(n.Card.owner)
                        .Append(" @").Append(string.Join(",", n.Link.at)).Append(" turn ").Append(n.Link.turn).Append(n.Link.mirror.Length > 0 ? " mirror " + n.Link.mirror : "")
                        .Append(n.Link.recolor.Length > 0 ? " recolor " + n.Link.recolor : "").Append(n.Link.enabled ? "（開）" : "（關）")
                        .Append(n.PivotDrift ? "｜⚠ 尺寸變過（加關聯時 " + string.Join(",", n.Link.pivot) + "、現在 " + n.Card.Dimensions + "）" : "")
                        .Append(SCP_SculptDraft.IsOpen(c.Works.SpacePaths(n.Card.id)) ? "｜📝 開著草稿" : "")
                        .Append(n.Link.note.Length > 0 ? "｜" + n.Link.note : "")
                        .Append(n.Link.meta.Count > 0 ? "｜meta " + SCP_SculptMeta.Describe(n.Link.meta) : "").Append('\n');
                Walk(n.Children, iDepth + 1);
            }
        }
        Walk(iTree, 0);
    }

    /// <summary>Credit 的「關聯」那一段（現組，不在 events）：樹上每件作品的標題與作者。樹讀不了 ⇒ 印原因。</summary>
    static void AppendLinkCredits(Ctx c, string iId)
    {
        List<SCP_SculptLinkNode> aTree;
        try { aTree = SCP_SculptLinks.Tree(c.Works, iId); }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { c.Report.Append("\n⚠ 關聯樹讀不了：").Append(e.Message).Append('\n'); return; }
        if (aTree.Count == 0) return;
        var aSeen = new HashSet<string>(StringComparer.Ordinal);
        c.Report.Append("\n## Credit —— 關聯（現組，不在 events）\n");
        void Walk(List<SCP_SculptLinkNode> iNodes)
        {
            foreach (var n in iNodes)
            {
                if (aSeen.Add(n.Card.id)) c.Report.Append("- ").Append(n.Card.title).Append("（").Append(n.Card.id).Append("）｜by ").Append(n.Card.owner).Append('\n');
                Walk(n.Children);
            }
        }
        Walk(aTree);
        c.Result.AddValue("link_credit_count", aSeen.Count.ToString(CultureInfo.InvariantCulture));
    }
}
