// 區塊職責：作品的草稿層 CLI（TASK-0493，預雕刻）—— draft=1 讓刀落在草稿／讓觀測看「作品＋草稿」；
//          work sub=draft（開／看狀態）、sub=apply（一筆套進作品）、sub=discard（整個丟掉）。
// 物理意義：草稿是作品資料夾底下的 draft/，自己是一個「底不是空的」空間（SCP_SculptDraft）⇒ 寫入類 op、undo／redo、觀測
//          換一顆指向草稿的引擎就原封不動可用。草稿的讀寫拿作品同一顆 _engine 鎖。草稿刀免費、不分享。
//          ⭐ 草稿開著時，寫入作品的刀要**明寫** draft=1（落草稿）或 draft=0（直接落作品）—— 漏帶一個參數，不該讓一刀靜默落進作品。
//          觀測 draft=1 時，被關聯的作品有開著的草稿也一起用草稿（甲板在草稿裡試刻，回船殼裡看合不合 —— TASK-0493×0494）。
// 失敗處置：draft 帶 mark／hide／only、共用展區帶 draft、不收 draft 的 op 帶 draft、draft 帶 exhibit_id ⇒ exit 2；
//          apply 時底過期（內容雜湊對不上）⇒ exit 2、零寫入；套完逐格跟草稿比，對不上 ⇒ exit 1、草稿留著。
using System.Globalization;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    /// <summary>draft=1 會落在草稿的寫入類 op。</summary>
    static readonly string[] DraftWriteOps = { "box", "carve", "carvevox", "stamp2d", "stampimg", "stampvox", "spray" };
    /// <summary>draft=1 看「作品＋草稿」的觀測類 op。</summary>
    static readonly string[] DraftViewOps = { "view", "slice", "section", "stats", "export", "diff" };
    /// <summary>op=work 底下吃 draft=1 的 sub（只動草稿）。</summary>
    static readonly string[] DraftWorkSubs = { "undo", "redo", "move", "assemble" };

    /// <summary>
    /// draft 參數：沒給／0 ⇒ 一般（但作品開著草稿時，寫入類要明寫 draft=0）；1 ⇒ 檢查這個 op 收不收、跟 mark／hide／only 有沒有撞，收 ⇒ <see cref="Ctx.Draft"/>。
    /// ⚠ 要在 ConfigureWork／ConfigureView／ConfigureLinks 之後跑（要知道作品、時間條件、關聯樹）。
    /// </summary>
    static bool ConfigureDraft(Ctx c, out string oError)
    {
        oError = "";
        string aDraft = c.Args.Get("draft").Trim();
        string aSub = c.Args.Get("sub").Trim();
        bool aWrite = Array.IndexOf(DraftWriteOps, c.Op) >= 0 || (c.Op == "work" && Array.IndexOf(DraftWorkSubs, aSub) >= 0);
        if (aDraft.Length == 0 || aDraft == "0")
        {
            // 只有字面上的 draft=0 算「明寫直接落作品」—— draft=（空值，例：樣板變數沒設）跟沒給一樣要擋
            if (!aWrite || aDraft == "0") return true;
            string aId = c.Work?.id ?? "";
            if (aId.Length == 0 && c.Op == "work")
            {
                // 跟 OpWork 同一個解法：work=，沒有就 id=（⛔ 只看 work= 的話，用 id= 點名就繞過這道閘）
                string aRaw = c.Args.Get("work").Trim();
                if (aRaw.Length == 0) aRaw = c.Args.Get("id").Trim();
                try { aId = SCP_SculptWorks.NormalizeId(aRaw); } catch (ArgumentException) { return true; }   // 名字不合法 ⇒ OpWork 自己會擋
            }
            if (aId.Length > 0 && c.Works.Exists(aId) && SCP_SculptDraft.IsOpen(c.Works.SpacePaths(aId)))
            {
                oError = "作品 " + aId + " 開著草稿 —— 這一刀要落在草稿帶 draft=1，要直接落進作品帶 draft=0（⛔ 不猜：漏帶一個參數不該讓一刀靜默落進作品）";
                return false;
            }
            return true;
        }
        if (aDraft != "1") { oError = "draft 要是 1 或 0（got '" + aDraft + "'）"; return false; }
        if (c.Op == "work")
        {
            if (!aWrite) { oError = "work 只有 sub=undo|redo|move|assemble 吃 draft=1（開／看草稿 sub=draft，套進作品 sub=apply，丟掉 sub=discard）"; return false; }
            c.Draft = true;
            return true;
        }
        if (!aWrite && Array.IndexOf(DraftViewOps, c.Op) < 0)
        { oError = c.Op + " 不吃 draft（草稿只給落子／刻與觀測：" + string.Join("／", DraftWriteOps) + "／" + string.Join("／", DraftViewOps) + "）"; return false; }
        if (c.Work == null) { oError = "草稿是作品的 —— 要帶 work=（共用展區沒有草稿）"; return false; }
        if (c.View != null) { oError = "draft 不能跟 mark／hide／only 一起給 —— 草稿是「作品現在＋草稿」，不是過去的某一刻"; return false; }
        if (c.Args.IsExplicit("exhibit_id")) { oError = "draft=1 不能帶 exhibit_id —— 展品是作品的，草稿還沒定案（apply 之後再登錄）"; return false; }
        if (!aWrite)
        {
            bool aOwn = SCP_SculptDraft.IsOpen(c.Works.SpacePaths(c.Work.id));
            // 只算「這次真的會組進來」的關聯上開著的草稿（停用的、被 hide 掉的、沒選到的不算 —— 不然 draft=1 會放行一張看不到任何草稿的圖）
            bool aChild = false;
            void Walk(List<SCP_SculptLinkNode> iNodes)
            {
                foreach (var n in iNodes)
                {
                    if (c.Links == null || !c.Links.Selection.Includes(n.Path, n.Link.enabled)) continue;
                    aChild |= SCP_SculptDraft.IsOpen(c.Works.SpacePaths(n.Card.id));
                    Walk(n.Children);
                }
            }
            if (c.LinkTree != null) Walk(c.LinkTree);
            if (!aOwn && !aChild)
            { oError = "作品 " + c.Work.id + (c.LinkTree != null ? " 與這次組進來的關聯" : "") + "都沒有開著的草稿 —— 先 `work sub=draft` 開一份，或直接帶 draft=1 下第一刀"; return false; }
            if (c.Op == "diff" && !aOwn) { oError = "diff draft=1 比的是「作品現在 → 它自己的草稿」—— 作品 " + c.Work.id + " 沒有開著的草稿"; return false; }
            if (c.Links != null) c.Links.UseDrafts = true;
        }
        c.Draft = true;
        return true;
    }

    /// <summary>指向草稿的引擎（草稿要已經開著）：Root ＝ draft/、底 ＝ 開草稿那一刻、輸出檔仍落在作品資料夾。</summary>
    static SCP_SculptEngine DraftEngine(Ctx c, SCP_SculptWork iCard)
    {
        var aWork = c.Works.SpacePaths(iCard.id);
        SCP_SculptDraftInfo aInfo = SCP_SculptDraft.Read(aWork) ?? throw new InvalidOperationException("作品 " + iCard.id + " 沒有開著的草稿");
        return new SCP_SculptEngine(SCP_SculptDraft.Paths(aWork, aInfo, iCard.Dimensions), c.Roots.DataRoot, iCard.SizeX, iCard.SizeY, iCard.SizeZ);
    }

    /// <summary>草稿的刀：沒開就開一份（回報「開了草稿」）。呼叫端已握作品鎖。殘留的 draft/ ⇒ InvalidOperationException。</summary>
    static SCP_SculptDraftInfo OpenDraft(Ctx c, SCP_SculptWork iCard)
    {
        var aInfo = SCP_SculptDraft.Open(c.Works.SpacePaths(iCard.id), iCard.Dimensions, c.Persona, DateTime.Now, out bool aCreated);
        if (aCreated) c.Report.Append("- 📝 開了草稿（底 ＝ ").Append(aInfo.base_event.Length == 0 ? "空的作品" : aInfo.base_event).Append("）\n");
        c.Result.AddValue("draft", "1");
        c.Result.AddValue("draft_base_event", aInfo.base_event.Length == 0 ? "start" : aInfo.base_event);
        return aInfo;
    }

    /// <summary>work 編輯（move／assemble／undo／redo）用的引擎：draft=1 ⇒ 草稿（move／assemble 沒開就開；undo／redo 要已經開著）。</summary>
    static SCP_SculptEngine WorkEngine(Ctx c, SCP_SculptWork iCard, bool iAutoOpen)
    {
        if (!c.Draft) return c.Works.Engine(iCard, c.Roots.DataRoot);
        if (iAutoOpen) OpenDraft(c, iCard);
        else if (!SCP_SculptDraft.IsOpen(c.Works.SpacePaths(iCard.id))) throw new ArgumentException("作品 " + iCard.id + " 沒有開著的草稿 —— 草稿裡沒有東西可 undo／redo");
        c.Result.AddValue("draft", "1");
        return DraftEngine(c, iCard);
    }

    /// <summary>work sub=draft：沒開 ⇒ 開一份（作者）；開著 ⇒ 看狀態（底、過期與否、幾刀、可 undo／redo、跟作品現在差幾格）。</summary>
    static string? WorkDraft(Ctx c, SCP_SculptWork iCard)
    {
        var aWork = c.Works.SpacePaths(iCard.id);
        SCP_SculptDraftInfo? aInfo = SCP_SculptDraft.Read(aWork);
        if (aInfo == null)
        {
            if (c.Persona.Length == 0 || c.Persona != iCard.owner) return Blocked(c, 2, "作品 " + iCard.id + " 沒有開著的草稿；只有作者 " + iCard.owner + " 能開");
            aInfo = OpenDraft(c, iCard);
        }
        var aDraftPaths = SCP_SculptDraft.Paths(aWork, aInfo, iCard.Dimensions);
        string? aStale = SCP_SculptDraft.Stale(aWork, aInfo, iCard.Dimensions);
        var aHistory = SCP_SculptHistory.Read(aDraftPaths);
        int aDraftEvents = SCP_SculptStore.ListEvents(aDraftPaths).Count;
        var aNow = SCP_SculptDraft.WorkNow(aWork);
        SCP_SculptSpace aDraftSpace;
        try { aDraftSpace = new SCP_SculptEngine(aDraftPaths, c.Roots.DataRoot, iCard.SizeX, iCard.SizeY, iCard.SizeZ).LoadSpace(); }
        catch (SCP_SculptReplayException e) { return Blocked(c, 2, e.Message); }
        var aDiff = SCP_SculptEngine.DiffSpaces(aNow, aDraftSpace, "作品現在", "草稿", new SCP_SculptDiffArgs { Region = c.Args.Get("region").Trim(), Out = c.Args.Get("out").Trim() });
        if (aDiff.ExitCode != 0) return Blocked(c, 2, string.Join(" ", aDiff.Lines));

        c.Result.AddValue("draft", "1");
        c.Result.AddValue("draft_base_event", aInfo.base_event.Length == 0 ? "start" : aInfo.base_event);
        c.Result.AddValue("draft_stale", aStale == null ? "0" : "1");
        c.Result.AddValue("draft_events", aDraftEvents.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("undo_count", aHistory.Active.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("redo_count", aHistory.Redo.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("placed", aDiff.Placed.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("carved", aDiff.Carved.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("recolored", aDiff.Recolored.ToString(CultureInfo.InvariantCulture));
        if (aDiff.OutputPath.Length > 0) { c.Result.AddValue("path", aDiff.OutputPath.Replace('\\', '/')); c.Result.AddValue("sha256", aDiff.Sha256); }
        c.Report.Append("## 草稿（").Append(iCard.id).Append("）\n")
                .Append("- 底：").Append(aInfo.base_event.Length == 0 ? "空的作品" : aInfo.base_event).Append("（開於 ").Append(aInfo.created_at).Append(" by ").Append(aInfo.persona).Append("）\n")
                .Append("- ").Append(aStale == null ? "底沒過期（作品內容跟開草稿時一樣）⇒ 可以 apply" : "⚠ " + aStale + " ⇒ 不能 apply（`work sub=discard` 丟掉、照現在的作品重開）").Append('\n')
                .Append("- 草稿裡 ").Append(aDraftEvents).Append(" 個事件；可 undo ").Append(aHistory.Active.Count).Append("、可 redo ").Append(aHistory.Redo.Count).Append("（`work sub=undo|redo draft=1`）\n")
                .Append("```\n").Append(aDiff.Render()).Append("\n```\n")
                .Append("- 看：觀測類 op 帶 `draft=1`；套進作品：`work sub=apply`；不要了：`work sub=discard`\n")
                .Append("- ⚠ 草稿開著時，直接落進作品的刀要帶 `draft=0`（漏帶會擋下）\n");
        return Finish(c, 0, "✓ 草稿｜" + aDraftEvents + " 個事件｜跟作品現在差：放 " + aDiff.Placed + "／刻 " + aDiff.Carved + "／換色 " + aDiff.Recolored + (aStale == null ? "" : "｜⚠ 底過期"));
    }

    /// <summary>work sub=apply：底沒過期 ⇒ 一筆 workedit（action=draft）套進作品、逐格驗、刪草稿。</summary>
    static string? WorkApplyDraft(Ctx c, SCP_SculptWork iCard)
    {
        var aWork = c.Works.SpacePaths(iCard.id);
        SCP_SculptDraftInfo? aInfo = SCP_SculptDraft.Read(aWork);
        if (aInfo == null) return Blocked(c, 2, "作品 " + iCard.id + " 沒有開著的草稿 —— 沒東西可套");
        string? aStale = SCP_SculptDraft.Stale(aWork, aInfo, iCard.Dimensions);
        if (aStale != null) return Blocked(c, 2, aStale + "，直接套會蓋掉那些改動；未套用。出口：`work sub=discard` 丟掉、照現在的作品重開");
        var aDraftPaths = SCP_SculptDraft.Paths(aWork, aInfo, iCard.Dimensions);
        SCP_SculptSpace aDraftSpace;
        try { aDraftSpace = new SCP_SculptEngine(aDraftPaths, c.Roots.DataRoot, iCard.SizeX, iCard.SizeY, iCard.SizeZ).LoadSpace(); }
        catch (SCP_SculptReplayException e) { return Blocked(c, 2, e.Message); }
        var aEngine = c.Works.Engine(iCard, c.Roots.DataRoot);
        SCP_SculptEdit aEdit = aEngine.DraftEdit(aDraftSpace);
        if (aEdit.after.Count == 0) return Blocked(c, 2, "草稿跟作品現在一模一樣 —— 沒東西可套（草稿留著；不要了用 `work sub=discard`）");
        aEdit.credits = SCP_SculptHistory.Read(aDraftPaths).Credits();
        string aFile = aEngine.CommitEdit(aEdit, c.Persona);

        // 結果帳：套完的作品要跟草稿逐格相同 —— 對不上就不刪草稿，留給人看
        var aAfter = aEngine.LoadSpace();
        int aMismatch = 0;
        foreach (var v in aAfter.Voxels.Entries()) if (aDraftSpace.Voxels.Get(v.X, v.Y, v.Z) != v.Color) aMismatch++;
        foreach (var v in aDraftSpace.Voxels.Entries()) if (aAfter.Voxels.Get(v.X, v.Y, v.Z) == 0) aMismatch++;
        c.Result.AddValue("changed", aEdit.after.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("event_file", aFile);
        c.Result.AddValue("apply_mismatch", aMismatch.ToString(CultureInfo.InvariantCulture));
        if (aMismatch != 0) return Finish(c, 1, "✗ 套完的作品跟草稿差 " + aMismatch + " 格 —— 草稿留著沒刪（event " + aFile + "），要人對");
        // ⚠ 事件已經落了 ⇒ 之後任何收尾失敗都不能讓這一筆看起來像沒套（⛔ 重跑 apply 會撞「底過期」、重跑別的會多一刀）
        try
        {
            int aRemoved = SCP_SculptDraft.Discard(aWork);
            c.Result.AddValue("draft_removed_files", aRemoved.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        {
            c.Result.AddValue("draft_cleanup_failed", "1");
            c.Report.Append("- ⚠ 已套進作品，但草稿資料夾收不乾淨：").Append(e.Message).Append('\n');
            c.Result.Lines.Add("⚠ 已套進作品（event " + aFile + "）；草稿資料夾收不乾淨 —— 再 `work sub=discard` 一次（作品不受影響）");
        }
        c.Result.AddValue("revision", SCP_SculptWorks.Revision(aAfter, iCard.Dimensions));
        c.Result.AddValue("charged", "0");
        AppendCredits(c, iCard.id);
        return Finish(c, 0, "✓ 草稿套進作品｜" + aEdit.after.Count + " 格變動｜一筆事件（`work sub=undo` 一次就回到草稿前）｜套完逐格 ＝ 草稿");
    }

    /// <summary>work sub=discard：把 draft/ 整個搬走再刪（作品的 events／快取／標記／歷史一格不動）。沒有 ⇒ 沒事可做（exit 0）。</summary>
    static string? WorkDiscardDraft(Ctx c, SCP_SculptWork iCard)
    {
        var aWork = c.Works.SpacePaths(iCard.id);
        int aRemoved;
        try { aRemoved = SCP_SculptDraft.Discard(aWork); }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException) { return Blocked(c, 1, e.Message); }
        c.Result.AddValue("draft_removed_files", aRemoved.ToString(CultureInfo.InvariantCulture));
        return Finish(c, 0, aRemoved == 0 && !Directory.Exists(SCP_SculptDraft.Dir(aWork))
            ? "✓ 作品 " + iCard.id + " 沒有草稿（沒東西可丟）"
            : "✓ 草稿丟掉了（" + aRemoved + " 個檔）—— 作品本身一格沒動");
    }
}
