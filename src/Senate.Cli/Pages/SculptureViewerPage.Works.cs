// 區塊職責：雕刻觀測頁的個人作品區；建立／筆記／匯入都派送同一支 sculpture CLI。
// 物理意義：模式與 ID 明確決定觀測空間；作品選取失效不會退回共用展區。
using System.Globalization;
using System.Text;
using SCP.Core.Gui;
using SCP.Core.Paths;
using SCP.Core.Sculpture;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed partial class SculptureViewerPage
{
    public const string SpaceSel = P + "sel/space", WorkSel = P + "sel/work";
    const string WorkCreateId = P + "work/create-id", WorkCreateTitle = P + "work/create-title";
    const string WorkAt = P + "work/at", WorkExhibit = P + "work/exhibit";
    const string WorkSeen = P + "state/work-seen", ImportSig = P + "state/import-sig", ImportRevision = P + "state/import-revision", ImportCount = P + "state/import-count";
    const string SubjectWork = "work:";
    List<SCP_SculptWork> m_Works = new();
    readonly Dictionary<string, (string Notes, string Todo)> m_WorkTexts = new();
    Dictionary<string, List<SCP_SculptCredit>> m_WorkCredits = new();
    Dictionary<string, string> m_WorkCreditErrors = new();
    Task<(Dictionary<string, List<SCP_SculptCredit>> Credits, Dictionary<string, string> Errors)>? m_CreditJob;
    string m_WorksRoot = "\0";
    string m_WorksError = "";
    // 作品每軸上限（TASK-0479）：跟作品清單一起讀（⛔ 不每幀讀設定檔）；存檔後立刻重讀。
    int m_WorkMaxAxis = SCP_SculptWorks.DefaultMaxAxis;
    string m_WorkMaxAxisSource = "";
    string m_WorkMaxAxisMsg = "";

    void ReloadWorks(SCP_DataRoot data)
    {
        m_WorksRoot = data.Value;
        m_WorkMaxAxis = SculptureWorkPrefs.ResolveMaxAxis(m_Model.RepoRoot, out m_WorkMaxAxisSource);
        m_Works = new(); m_WorkTexts.Clear(); m_WorkCredits = new(); m_WorkCreditErrors = new(); m_WorksError = ""; m_CreditJob = null;
        var paths = new List<(string Id, SCP_SculptPaths Paths)>();
        try
        {
            var store = new SCP_SculptWorks(data);
            m_Works = store.List();
            foreach (var card in m_Works)
            {
                m_WorkTexts[card.id] = (store.ReadText(card.id, false), store.ReadText(card.id, true));
                paths.Add((card.id, store.SpacePaths(card.id)));
            }
        }
        catch (Exception e) { m_WorksError = "作品讀取失敗：" + e.Message; return; }
        // Credit 走歷史索引（TASK-0473）：命中只 stat；第一次讀大作品要解析事件檔 ⇒ 會重畫的宿主丟背景，畫面先寫「計算中」。
        var job = () =>
        {
            var credits = new Dictionary<string, List<SCP_SculptCredit>>(); var errors = new Dictionary<string, string>();
            foreach (var (id, p) in paths)
            {
                try { credits[id] = SCP_SculptHistory.Read(p).Credits(); }
                catch (Exception e) { errors[id] = e.Message; }   // 一件壞掉不拖垮整份清單
            }
            return (credits, errors);
        };
        if (SCP_GuiHost.RedrawsContinuously) m_CreditJob = Task.Run(job);
        else (m_WorkCredits, m_WorkCreditErrors) = job();   // 不會重畫的宿主（CLI 單次 render）：背景算等於把答案丟掉
    }
    void PumpCredits()
    {
        if (m_CreditJob == null || !m_CreditJob.IsCompleted) return;
        try { (m_WorkCredits, m_WorkCreditErrors) = m_CreditJob.Result; }
        catch (Exception e) { m_WorksError = "Credit 計算失敗：" + (e.InnerException ?? e).Message; }
        m_CreditJob = null;
    }
    static bool PersonalSpace(SCP_Ui g) => g.FieldValue(SpaceSel + "/value", "shared") == "work";
    string SelectedWork(SCP_Ui g)
    {
        string id = g.FieldValue(WorkSel + "/value", "");
        return m_Works.Exists(w => w.id == id && w.status == "ready") ? id : "";
    }
    // 草稿模式（TASK-0493）與關聯的一次性開關（TASK-0494）—— 送出去的參數跟 CLI 同名，規則由 Cmd_Sculpture 判（⛔ 頁面不另寫一份）
    const string TWorkDraft = P + "t/work-draft", FLinks = P + "f/links", FHideLinks = P + "f/hide_links";
    static readonly string[] s_DraftOps = { "box", "carve", "carvevox", "stamp2d", "stampimg", "stampvox", "view", "slice", "section", "stats", "export", "diff" };
    static readonly string[] s_LinkViewOps = { "view", "slice", "section", "stats", "export" };

    // 「有沒有草稿可看」每幀都會問（GPU 預覽每幀組一次請求）⇒ 每件作品記一秒
    readonly Dictionary<string, (bool Has, long Ticks)> m_DraftSeen = new();
    bool HasDraftToShow(string id)
    {
        if (id.Length == 0 || m_WorksRoot == "\0") return false;
        long now = Environment.TickCount64;
        if (m_DraftSeen.TryGetValue(id, out var hit) && now - hit.Ticks < 1000) return hit.Has;
        bool has = false;
        try
        {
            var store = new SCP_SculptWorks(new SCP_DataRoot(m_WorksRoot));
            has = SCP_SculptDraft.IsOpen(store.SpacePaths(id));
            if (!has)
            {
                var ids = new SortedSet<string>(StringComparer.Ordinal);
                SCP_SculptLinks.Closure(SCP_SculptLinks.Tree(store, id), ids);
                foreach (string k in ids) has |= SCP_SculptDraft.IsOpen(store.SpacePaths(k));
            }
        }
        catch (Exception) { has = false; }   // 樹壞了 ⇒ 不送 draft=1（觀測那邊自己會說樹壞在哪）
        m_DraftSeen[id] = (has, now);
        return has;
    }

    void AddWorkTarget(SCP_Ui g, Dictionary<string, string> args)
    {
        if (!PersonalSpace(g)) return;
        string id = SelectedWork(g);
        args["work"] = id.Length > 0 ? id : "__missing-work__"; // 不准失效選取靜默變成展區操作。
        args.Remove("exhibit");
        // 沒有 op ＝ view（手動渲染／GPU 預覽）。草稿：開關開 ⇒ draft=1；關 ⇒ 明寫 draft=0（草稿開著時寫入類要明寫，頁面替使用者寫清楚）
        string op = args.TryGetValue("op", out string? o) ? o : "view";
        string sub = args.TryGetValue("sub", out string? s) ? s : "";
        bool draftOn = g.ToggleValue(TWorkDraft, false);
        bool view = Array.IndexOf(s_LinkViewOps, op) >= 0 || op == "diff";
        if (Array.IndexOf(s_DraftOps, op) >= 0 || (op == "work" && (sub == "undo" || sub == "redo" || sub == "move" || sub == "assemble")))
            // 觀測：這件作品（或它組進來的關聯）真的有開著的草稿才送 1 —— 否則開關開著也只會換來「沒有草稿」的錯
            args["draft"] = draftOn && (!view || HasDraftToShow(id)) ? "1" : "0";
        if (Array.IndexOf(s_LinkViewOps, op) >= 0)
        {
            string links = g.FieldValue(FLinks, "").Trim(), hide = g.FieldValue(FHideLinks, "").Trim();
            if (links.Length > 0) args["links"] = links;
            if (hide.Length > 0) args["hide_links"] = hide;
            // 文字欄有填 ⇒ 以文字欄為準（頁面上有寫）；都空 ⇒ 照一條一個的開關（TASK-0499）
            if (links.Length == 0 && hide.Length == 0 && LinkRows(id).Rows is { Count: > 0 } rows)
            {
                var (aOn, aHide) = LinkToggleArgs(rows, r => g.ToggleValue(LinkToggleId(id, r.Path), r.Enabled));
                if (aOn.Length > 0) args["links"] = aOn;
                if (aHide.Length > 0) args["hide_links"] = aHide;
            }
        }
        // 繩索（TASK-0502）：只有 view／export 畫繩 —— 總開關關 ⇒ ropes=0；個別關掉的 ⇒ hide_ropes=（跟 CLI 同名、由 Cmd_Sculpture 驗）
        if ((op == "view" || op == "export") && RopeRows(id).Rows is { Count: > 0 } ropeRows)
        {
            var (aRopes, aHideRopes) = RopeToggleArgs(g.ToggleValue(TRopesAll, true), ropeRows.ConvertAll(r => new RopeRow(r.Path, r.Rope.enabled)),
                                                      path => g.ToggleValue(RopeToggleId(id, path), true));
            if (aRopes.Length > 0) args["ropes"] = aRopes;
            if (aHideRopes.Length > 0) args["hide_ropes"] = aHideRopes;
        }
        // 帆（TASK-0503）：同一種開關（sails=／hide_sails=）＋風與秒數欄位（空 ＝ 作品預設）
        if ((op == "view" || op == "export") && SailRows(id).Rows is { Count: > 0 } sailRows)
        {
            var (aSails, aHideSails) = RopeToggleArgs(g.ToggleValue(TSailsAll, true), sailRows.ConvertAll(r => new RopeRow(r.Path, r.Sail.enabled)),
                                                      path => g.ToggleValue(SailToggleId(id, path), true));
            if (aSails.Length > 0) args["sails"] = aSails;
            if (aHideSails.Length > 0) args["hide_sails"] = aHideSails;
            string wind = g.FieldValue(FSailWind, "").Trim(), secs = g.FieldValue(FSailSeconds, "").Trim();
            if (wind.Length > 0) args["wind"] = wind;
            if (secs.Length > 0) args["sail_seconds"] = secs;
        }
    }

    // ===========================================================
    // 區塊職責：帆的開關與風（TASK-0503）—— 跟繩索開關同一種用法（同一支純函數 RopeToggleArgs ⇒ sails=／hide_sails=）。
    // 物理意義：開關只決定**這一次觀測**畫不畫（⛔ 不寫回 sails.json；被拿掉的帆照樣模擬，綁在它角上的繩照樣拉）。
    //          風／秒數欄位空 ⇒ 用作品預設（work sub=wind）；填了 ⇒ 送 wind=／sail_seconds=（由 Cmd_Sculpture 驗）。
    //          「持續模擬」開著 ⇒ GPU 預覽每幀把帆往前推（帆會動）；關掉 ⇒ 停在 sail_seconds 那一刻（跟 CLI view 同一張）。
    // ===========================================================
    const string TSailsAll = P + "t/sails-all";
    const string TSailPrefix = P + "t/sail/";
    const string TSailLive = P + "t/sail-live";
    const string FSailWind = P + "f/sail_wind";
    const string FSailSeconds = P + "f/sail_seconds";
    static string SailToggleId(string iWork, string iPath) => TSailPrefix + iWork + "/" + iPath;

    void DrawSailToggles(SCP_Ui g)
    {
        string id = SelectedWork(g);
        var (rows, error) = SailRows(id);
        if (error.Length > 0) { g.Note("⚠ 帆讀不了（觀測那邊會說哪裡壞）：" + error); return; }
        if (rows == null || rows.Count == 0) return;   // 沒有帆的作品不顯示這一塊
        using var fold = g.Fold("帆（" + rows.Count + " 面，開關只影響這次觀測）", P + "fold/sail-toggles", iDefaultOpen: true);
        if (!fold.Open) return;
        g.Note("風 wind=x,y,z（m/s，世界座標；空 ＝ 作品預設，work sub=wind 改預設）；秒數 ＝ CLI／關掉持續模擬時取「模擬幾秒後」那一刻。拿掉的帆照樣模擬。⛔ 不寫回作品。");
        Field(g, "風 wind=（m/s，例 0,-8,0）", FSailWind);
        Field(g, "模擬秒數 sail_seconds=（空 ＝ 作品預設）", FSailSeconds);
        g.Toggle("持續模擬（GPU 預覽裡帆會動）", true, TSailLive);
        g.Toggle("畫帆", true, TSailsAll);
        bool allOn = g.ToggleValue(TSailsAll, true);
        foreach (var r in rows)
        {
            var free = new List<string>();
            for (int k = 0; k < r.Sail.corners.Count; k++) if (!SCP_SculptSails.CornerPinned(r.Sail, k)) free.Add(r.Sail.corners[k].name.Length > 0 ? r.Sail.corners[k].name : k.ToString(CultureInfo.InvariantCulture));
            string info = (r.Sail.corners.Count == 3 ? "三角帆" : "四邊形") + (r.Sail.bind.Count > 0 ? "｜釘住的邊 " + string.Join(",", r.Sail.bind) : "")
                          + (free.Count > 0 ? "｜free " + string.Join(",", free) : "") + "｜色 " + r.Sail.color;
            if (r.State == SCP_SculptSailReading.Broken) { g.Note("🔴 `" + r.Path + "` 綁定壞了（不畫）：" + r.Reason); continue; }
            if (!r.Sail.enabled) { g.Label("◌ " + r.Path + "　" + info + "｜作品裡設為關"); continue; }
            using (g.Row())
            {
                g.Toggle(r.Path + "　" + info + (allOn ? "" : "　◌ 總開關關著 ⇒ 不畫"), true, SailToggleId(id, r.Path));
            }
        }
    }

    // ===========================================================
    // 區塊職責：繩索開關（TASK-0502，「觀測頁可以開關繩索 —— 跟關聯 toggle 同一種用法」）。
    // 物理意義：總開關 ＋ 一條一個勾，只決定**這一次觀測**送出去的 ropes=／hide_ropes=；⛔ 不寫回作品的 ropes.json（長期開關用 work sub=rope enabled=0|1）。
    //          勾的 id 帶作品 id（同關聯開關的理由）；全部都在預設 ⇒ 什麼都不送（場景快取的簽章不變）。
    //          綁在關聯上的繩：那條關聯關掉 ⇒ 繩跟著不畫（由 Cmd 判，頁面不另算）；作品裡設為關的繩只列出來、沒有勾（這頁只能拿掉，不能多畫）。
    // ===========================================================
    const string TRopesAll = P + "t/ropes-all";
    const string TRopePrefix = P + "t/rope/";
    static string RopeToggleId(string iWork, string iPath) => TRopePrefix + iWork + "/" + iPath;

    /// <summary>繩索開關的一列：繩路徑＋作品裡的 enabled。</summary>
    public readonly record struct RopeRow(string Path, bool Enabled);

    /// <summary>
    /// 開關 → ropes=／hide_ropes=（純函數，淨室直接測）：總開關關 ⇒ ("0","")；否則作品裡開著、這次被取消勾的 ⇒ hide_ropes；全在預設 ⇒ ("","")。
    /// 作品裡設為關的繩不送（hide 一條本來就不畫的繩沒有意義）。
    /// </summary>
    public static (string Ropes, string Hide) RopeToggleArgs(bool iAllOn, IReadOnlyList<RopeRow> iRows, Func<string, bool> iIsOn)
    {
        if (!iAllOn) return ("0", "");
        var hide = new List<string>();
        foreach (var r in iRows) if (r.Enabled && !iIsOn(r.Path)) hide.Add(r.Path);
        return ("", string.Join(",", hide));
    }

    readonly Dictionary<string, (SCP_SculptSailRig? Rig, string Error, long Ticks)> m_RigRows = new();

    /// <summary>
    /// 那件作品的繩與帆（預設的關聯開關下的讀數，⛔ 不模擬 —— 只要清單）；預覽每幀都會問 ⇒ 每件記一秒。讀不了 ⇒ Rig=null、Error 說哪裡壞。
    /// 繩也走這一支（TASK-0503）：綁帆角的繩要靠帆才解得到端點。
    /// </summary>
    (SCP_SculptSailRig? Rig, string Error) RigRows(string iId)
    {
        if (iId.Length == 0 || m_WorksRoot == "\0") return (null, "");
        long now = Environment.TickCount64;
        if (m_RigRows.TryGetValue(iId, out var hit) && now - hit.Ticks < 1000) return (hit.Rig, hit.Error);
        SCP_SculptSailRig? rig = null; string error = "";
        try
        {
            var store = new SCP_SculptWorks(new SCP_DataRoot(m_WorksRoot));
            List<SCP_SculptLinkNode>? tree = null;
            try { tree = SCP_SculptLinks.Tree(store, iId); } catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { tree = null; }
            rig = SCP_SculptSails.Rig(store, iId, tree, new SCP_SculptLinkSelection(), new SCP_SculptRopeSelection(), new SCP_SculptSailSelection(), new SCP_SculptSailEnv());
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException) { rig = null; error = e.Message; }
        m_RigRows[iId] = (rig, error, now);
        return (rig, error);
    }

    (List<SCP_SculptRopeReading>? Rows, string Error) RopeRows(string iId) { var (rig, error) = RigRows(iId); return (rig?.Ropes, error); }
    (List<SCP_SculptSailReading>? Rows, string Error) SailRows(string iId) { var (rig, error) = RigRows(iId); return (rig?.Sails, error); }

    void DrawRopeToggles(SCP_Ui g)
    {
        string id = SelectedWork(g);
        var (rows, error) = RopeRows(id);
        if (error.Length > 0) { g.Note("⚠ 繩索讀不了（觀測那邊會說哪裡壞）：" + error); return; }
        if (rows == null || rows.Count == 0) return;   // 沒有繩的作品不顯示這一塊
        using var fold = g.Fold("繩索開關（" + rows.Count + " 條，只影響這次觀測）", P + "fold/rope-toggles", iDefaultOpen: true);
        if (!fold.Open) return;
        g.Note("總開關關掉 ⇒ 這次不畫任何繩（ropes=0）；一條一個勾 ⇒ 取消勾的那條這次不畫（hide_ropes=）。綁在關聯上的繩：那條關聯關掉，繩就跟著不畫。⛔ 不寫回作品（長期開關用 work sub=rope enabled=0|1）。");
        g.Toggle("畫繩索", true, TRopesAll);
        bool allOn = g.ToggleValue(TRopesAll, true);
        foreach (var r in rows)
        {
            string info = r.Rope.from + " → " + r.Rope.to + (r.From != null && r.To != null ? "｜長 " + r.Length.ToString("0.##", CultureInfo.InvariantCulture) + "、下垂 " + r.MaxSag.ToString("0.##", CultureInfo.InvariantCulture) : "");
            if (r.State == SCP_SculptRopeReading.Broken) { g.Note("🔴 `" + r.Path + "` 綁定壞了（不畫）：" + r.Reason); continue; }
            if (!r.Rope.enabled) { g.Label("◌ " + r.Path + "　" + info + "｜作品裡設為關"); continue; }
            using (g.Row())
            {
                g.Toggle(r.Path + "　" + info + (allOn ? "" : "　◌ 總開關關著 ⇒ 不畫"), true, RopeToggleId(id, r.Path));
            }
        }
    }

    // ===========================================================
    // 區塊職責：關聯一條一個開關（TASK-0499，Tim「可以用 toggle 開關子模組顯示」）—— 看下層甲板不用打關聯名。
    // 物理意義：開關只決定**這一次觀測**送出去的 links=／hide_links=（跟 CLI 同名、由 Cmd_Sculpture 驗）；⛔ 不寫回作品的 links.json。
    //          開關的 id 帶作品 id ⇒ 換作品清單就換，不會帶著上一件的名字去報「關聯名不存在」。
    //          預設 ＝ 那一條自己的 enabled；全部都在預設 ⇒ 什麼都不送（場景快取的簽章不變，不白重組）。
    // ===========================================================
    const string TLinkPrefix = P + "t/link/";
    static string LinkToggleId(string iWork, string iPath) => TLinkPrefix + iWork + "/" + iPath;
    /// <summary>折疊狀態（"1" ＝ 展開；沒動過 ＝ 折起來，Tim 2026-10-11「預設折疊」）—— 也帶作品 id。</summary>
    static string LinkFoldId(string iWork, string iPath) => P + "f/linkfold/" + iWork + "/" + iPath;

    /// <summary>關聯樹攤平成的一列（照樹的順序：父在子前）。</summary>
    public readonly record struct LinkRow(string Path, int Depth, bool Enabled, string Label);

    readonly Dictionary<string, (List<LinkRow>? Rows, string Error, long Ticks)> m_LinkRows = new();

    /// <summary>那件作品的關聯樹（只讀 links.json 與書卡）；預覽每幀都會問 ⇒ 每件記一秒。樹壞了 ⇒ Rows=null、Error 說哪裡壞。</summary>
    (List<LinkRow>? Rows, string Error) LinkRows(string iId)
    {
        if (iId.Length == 0 || m_WorksRoot == "\0") return (null, "");
        long now = Environment.TickCount64;
        if (m_LinkRows.TryGetValue(iId, out var hit) && now - hit.Ticks < 1000) return (hit.Rows, hit.Error);
        List<LinkRow>? rows = null; string error = "";
        try
        {
            rows = FlattenLinkRows(SCP_SculptLinks.Tree(new SCP_SculptWorks(new SCP_DataRoot(m_WorksRoot)), iId));
        }
        catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException) { rows = null; error = e.Message; }
        m_LinkRows[iId] = (rows, error, now);
        return (rows, error);
    }

    /// <summary>關聯樹攤平成列（父在子前、帶深度與標籤）—— 頁面與淨室共用這一支。</summary>
    public static List<LinkRow> FlattenLinkRows(List<SCP_SculptLinkNode> iTree)
    {
        var rows = new List<LinkRow>();
        void Walk(List<SCP_SculptLinkNode> iNodes, int iDepth)
        {
            foreach (var n in iNodes)
            {
                string label = n.Path + " → " + n.Card.title + "（" + n.Card.id + "）"
                               + (n.Link.turn != 0 ? " turn " + n.Link.turn : "") + (n.Link.mirror.Length > 0 ? " mirror " + n.Link.mirror : "")
                               + (n.Link.enabled ? "" : "｜作品裡設為關");
                rows.Add(new LinkRow(n.Path, iDepth, n.Link.enabled, label));
                Walk(n.Children, iDepth + 1);
            }
        }
        Walk(iTree, 0);
        return rows;
    }

    /// <summary>
    /// 開關 → links=／hide_links=（純函數，淨室直接測）。
    /// 畫不畫：自己開著 ⇒ 且上一層也畫（上一層關了，底下整串都不畫）。
    /// 送法：畫的那幾條全部點名進 links=（含作品裡設為關、這次被打開的）；關掉而上一層有畫的 ⇒ hide_links=（連同底下）。
    /// ⚠ 不能只送 links=：點名那條底下沒點名的會照它自己的 enabled（見 SCP_SculptLinkSelection.Includes）⇒ 開著的子關聯會被畫回來。
    /// 全部都在預設 ⇒ ("","")；一條都不畫 ⇒ links=none。
    /// </summary>
    public static (string Links, string Hide) LinkToggleArgs(IReadOnlyList<LinkRow> iRows, Func<LinkRow, bool> iIsOn)
    {
        bool anyChanged = false;
        foreach (var r in iRows) anyChanged |= iIsOn(r) != r.Enabled;
        if (!anyChanged) return ("", "");
        bool[] drawn = DrawnFlags(iRows, iIsOn);
        var on = new List<string>(); var hide = new List<string>();
        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < iRows.Count; i++)
        {
            byPath[iRows[i].Path] = i;
            if (drawn[i]) { on.Add(iRows[i].Path); continue; }
            int cut = iRows[i].Path.LastIndexOf('/');
            bool parentDrawn = cut < 0 || (byPath.TryGetValue(iRows[i].Path.Substring(0, cut), out int p) && drawn[p]);
            if (parentDrawn) hide.Add(iRows[i].Path);   // 自己關、上一層有畫 ⇒ hide（連同底下）；上一層已經不畫 ⇒ 不必再提
        }
        return on.Count == 0 ? ("none", "") : (string.Join(",", on), string.Join(",", hide));
    }

    /// <summary>
    /// 每一列實際畫不畫（Unity 的 activeInHierarchy）：自己勾著（activeSelf）**且**上一層畫 —— 上一層關了，底下每一層都不畫，
    /// 但它們自己的勾不動（parent 打開回來就照自己的勾回來）。⭐ 送參數與列表顯示都問這一支（⛔ 別各寫一份）。
    /// </summary>
    public static bool[] DrawnFlags(IReadOnlyList<LinkRow> iRows, Func<LinkRow, bool> iIsOn)
    {
        var drawn = new bool[iRows.Count];
        var byPath = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < iRows.Count; i++)   // 父在子前 ⇒ 看到子的時候父已經判好了
        {
            int cut = iRows[i].Path.LastIndexOf('/');
            bool parentDrawn = cut < 0 || (byPath.TryGetValue(iRows[i].Path.Substring(0, cut), out int p) && drawn[p]);
            drawn[i] = parentDrawn && iIsOn(iRows[i]);
            byPath[iRows[i].Path] = i;
        }
        return drawn;
    }

    /// <summary>列表上的一列（Unity Hierarchy 的一行）。</summary>
    public readonly record struct LinkView(LinkRow Row, bool HasChildren, bool Open, bool SelfOn, bool Drawn, int Descendants);

    /// <summary>
    /// 要列出來的那幾列（純函數）：祖先有任何一層折起來 ⇒ 不列；折起來的那一列記它藏了幾條（Descendants）。
    /// ⚠ 折疊只管**列不列**，不管**畫不畫**（畫不畫只看 <see cref="DrawnFlags"/>）—— 跟 Unity 一樣，折起來的 child 照樣在場景裡。
    /// </summary>
    public static List<LinkView> HierarchyView(IReadOnlyList<LinkRow> iRows, Func<LinkRow, bool> iIsOn, Func<LinkRow, bool> iIsOpen)
    {
        bool[] drawn = DrawnFlags(iRows, iIsOn);
        var outRows = new List<LinkView>();
        int hideBelowDepth = int.MaxValue;   // 正在藏的那一段：深度 > 它的都不列
        for (int i = 0; i < iRows.Count; i++)
        {
            var r = iRows[i];
            if (r.Depth > hideBelowDepth) continue;
            hideBelowDepth = int.MaxValue;
            int desc = 0;
            for (int j = i + 1; j < iRows.Count && iRows[j].Depth > r.Depth; j++) desc++;
            bool open = desc == 0 || iIsOpen(r);
            outRows.Add(new LinkView(r, desc > 0, open, iIsOn(r), drawn[i], desc));
            if (!open) hideBelowDepth = r.Depth;
        }
        return outRows;
    }

    void DrawLinkToggles(SCP_Ui g)
    {
        string id = SelectedWork(g);
        var (rows, error) = LinkRows(id);
        if (error.Length > 0) { g.Note("⚠ 關聯樹讀不了（開關先不畫；觀測那邊會說哪裡壞）：" + error); return; }
        if (rows == null || rows.Count == 0) return;   // 沒有關聯的作品不顯示這一塊
        using var fold = g.Fold("關聯開關（" + rows.Count + " 條，只影響這次觀測）", P + "fold/link-toggles", iDefaultOpen: true);
        if (!fold.Open) return;
        bool textGiven = g.FieldValue(FLinks, "").Trim().Length > 0 || g.FieldValue(FHideLinks, "").Trim().Length > 0;
        g.Note(textGiven
            ? "⚠ 上面的 links=／hide_links= 文字欄有填 ⇒ **以文字欄為準**，下面這些開關這一次不生效（清空文字欄就換回開關）。"
            : "一條一個開關（像 Unity 的 Hierarchy）：預設照那一條在作品裡的開關；關掉上一層 ⇒ 底下每一層都不畫，但它們自己的勾照留（標「上層關著」），上一層打開就照自己的勾回來。▶／▼ 只折疊列表、不影響畫不畫（預設折起來）。⛔ 開關與折疊都不寫回作品（長期開關用 work sub=link enabled=0|1）。");
        bool anyParent = false;
        foreach (var r in rows) anyParent |= r.Depth > 0;
        if (anyParent)
            using (g.Row())
            {
                // 全部展開／全部折疊：只動列表（同 Unity 的 Alt＋點三角形），不動畫不畫
                if (g.Button("全部展開", P + "btn/linkfold-all-open")) foreach (var r in rows) g.SetField(LinkFoldId(id, r.Path), "1");
                if (g.Button("全部折疊", P + "btn/linkfold-all-close")) foreach (var r in rows) g.SetField(LinkFoldId(id, r.Path), "0");
            }
        var view = HierarchyView(rows, r => g.ToggleValue(LinkToggleId(id, r.Path), r.Enabled), r => g.FieldValue(LinkFoldId(id, r.Path), "0") == "1");   // 預設折起來（Tim）：只有按過展開的才是 "1"
        foreach (var v in view)
        {
            using (g.Row())
            {
                if (v.Row.Depth > 0) g.Label(new string('　', v.Row.Depth * 2));
                if (v.HasChildren)
                {
                    if (g.FoldArrow(v.Open, P + "btn/linkfold/" + id + "/" + v.Row.Path))
                        g.SetField(LinkFoldId(id, v.Row.Path), v.Open ? "0" : "1");
                }
                else g.FoldArrowSpacer();
                // 勾 ＝ 自己的勾（activeSelf）；上層關著 ⇒ 勾照留、標「不畫」（activeInHierarchy）
                string tail = (v.SelfOn && !v.Drawn ? "　◌ 上層關著 ⇒ 不畫" : "") + (!v.Open ? "　（折疊：底下 " + v.Descendants + " 條）" : "");
                g.Toggle(v.Row.Label + tail, v.Row.Enabled, LinkToggleId(id, v.Row.Path));
            }
        }
    }
    void DrawWorkSelector(SCP_Ui g, string persona)
    {
        g.Dropdown("雕刻空間", new List<SCP_GuiOption> { new("shared", "共用展區（256³）"), new("work", "個人作品（可調尺寸）") }, "shared", SpaceSel);
        if (PersonalSpace(g))
        {
            var options = m_Works.Select(w => new SCP_GuiOption(w.id, w.title + "｜" + w.owner + "｜" + w.status)).ToList();
            string first = m_Works.Find(w => w.owner == persona && w.status == "ready")?.id ?? m_Works.Find(w => w.status == "ready")?.id ?? "";
            if (g.FieldValue(WorkSel + "/value", "").Length == 0 && first.Length > 0)
                g.SetField(WorkSel + "/value", first);
            g.Dropdown("作品", options, first, WorkSel);
            g.Toggle("草稿模式（draft=1：刀落在草稿、看「作品＋草稿」；套進作品／丟掉用 work sub=apply／discard）", false, TWorkDraft);
            Field(g, "只組這些關聯 links=（none｜all｜路徑,…；巢狀用 /；空 ＝ 照每條的開關）", FLinks);
            Field(g, "拿掉這些關聯 hide_links=（路徑,…｜self ＝ 不畫本作品自己的格）", FHideLinks);
            DrawLinkToggles(g);
            DrawRopeToggles(g);
            DrawSailToggles(g);
            g.Note("⚠ 被關聯的作品改了之後，預覽要換一下參數（或按渲染）才會重組 —— 場景快取只認這一頁送出的參數。");
        }
        string selection = PersonalSpace(g) ? SubjectWork + SelectedWork(g) : SubjectFull;
        string seen = g.FieldValue(WorkSeen, "");
        if (seen != selection)
        {
            g.SetField(WorkSeen, selection);
            if (seen.Length > 0 || PersonalSpace(g))
            {
                g.SetField(SSubject, selection); g.SetField(FRegion, ""); g.SetField(FExclude, "");
                g.SetField(FLinks, ""); g.SetField(FHideLinks, "");   // 關聯名是那件作品的 ⇒ 換作品就清掉（⛔ 帶著舊名字去看下一件只會一直報錯）
                g.SetField(SViewPath, ""); g.SetField(SSlicePath, ""); g.SetField(SExportPath, "");
                RequestRender("切換雕刻空間", iForce: true);
            }
        }
    }
    void DrawWorks(SCP_Ui g, string persona)
    {
        if (m_WorksError.Length > 0) g.Note(m_WorksError);
        int defaultSize = Math.Min(SCP_SculptWorks.Size, m_WorkMaxAxis);   // 與 CLI 沒帶 size 時同一條：min(64, 上限)
        g.Note($"作品預設{defaultSize}³，各軸可設1–{m_WorkMaxAxis}（{m_WorkMaxAxisSource}）；建立收10單位，作品內雕刻與調尺寸免費。匯入展區另按實際落地收費，原稿保留。");
        using (var limit = g.Fold("作品尺寸上限", P + "fold/work-max-axis", iDefaultOpen: false))
        {
            if (limit.Open)
            {
                g.Note($"只管「建立」與「調整尺寸」；既有作品不受影響（調小之後照樣能雕、能看）。範圍 1–{SCP_SculptWorks.MaxAxisHard}，預設 {SCP_SculptWorks.DefaultMaxAxis}。");
                string maxText = g.TextField("每軸最多幾格", m_WorkMaxAxis.ToString(CultureInfo.InvariantCulture), P + "work/max-axis");
                if (g.Button("保存上限", P + "btn/work-max-axis"))
                {
                    if (!int.TryParse(maxText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                        m_WorkMaxAxisMsg = "✗ 不是整數：" + maxText;
                    else
                    {
                        var (ok, msg) = SculptureWorkPrefs.SaveMaxAxis(m_Model.RepoRoot, value);
                        m_WorkMaxAxis = SculptureWorkPrefs.ResolveMaxAxis(m_Model.RepoRoot, out m_WorkMaxAxisSource);   // 回讀：印的是存進去之後讀到的值
                        m_WorkMaxAxisMsg = ok ? "✓ 已保存，回讀 " + m_WorkMaxAxis : "✗ " + msg;
                    }
                }
                if (m_WorkMaxAxisMsg.Length > 0) g.Note(m_WorkMaxAxisMsg);
            }
        }
        using (var create = g.Fold("建立作品", P + "fold/work-create", iDefaultOpen: false))
        {
            if (create.Open)
            {
                string id = g.TextField("全庫唯一 ID（英數 _ -）", "", WorkCreateId);
                string title = g.TextField("作品名稱", "", WorkCreateTitle);
                string dimensions = g.TextField($"尺寸（邊長或X,Y,Z，各軸1–{m_WorkMaxAxis}；留空＝{defaultSize}）", defaultSize.ToString(CultureInfo.InvariantCulture), P + "work/create-size").Trim();
                string parent = g.TextField("任務父作品ID（子作品才填）", "", P + "work/create-parent").Trim();
                string pending = g.FieldValue(Pending, "");
                Armed(g, pending, "work-create", parent.Length > 0 ? "建立任務子作品（免費）" : "建立作品（10單位）", () =>
                {
                    var args = new Dictionary<string, string> { ["op"] = "work", ["sub"] = "create", ["persona"] = persona, ["id"] = id, ["title"] = title };
                    if (dimensions.Length > 0) args["size"] = dimensions;   // 留空 ⇒ 不帶 size，交給 CLI 的 min(64, 上限)
                    if (parent.Length > 0) args["parent_work"] = parent;
                    Start(g, "建立作品", () =>
                    {
                        var result = RunCli(args);
                        var outcome = new Outcome { Log = result.Log("建立作品"), Reload = true };
                        if (result.Exit == 0) outcome.Fields[WorkSel + "/value"] = result.Value("work", "");
                        return outcome;
                    });
                });
            }
        }
        string selected = SelectedWork(g);
        if (selected.Length == 0) { g.Note("請建立或選擇付款完成的作品；待付款作品可用同一 ID 再按建立對帳。"); return; }
        var card = m_Works.Find(w => w.id == selected)!;
        g.Label(card.title + "｜" + selected + "｜作者 " + card.owner + "｜" + card.Dimensions);
        if (card.commission.Length > 0) g.Note("委託：" + card.commission + "｜來源 " + card.commission_ref + "｜建立免費、報酬10 token（" + card.status + "）");
        if (card.parent_work.Length > 0) g.Note("任務子作品｜父作品 " + card.parent_work + "｜建立免費，不另領薪");
        PumpCredits();
        if (m_CreditJob != null) g.Note("Credit 計算中…（第一次讀大作品要解析事件檔，之後走本機快取）");
        else if (m_WorkCreditErrors.TryGetValue(selected, out string? creditError)) g.Note("Credit 讀取失敗：" + creditError);
        else if (m_WorkCredits.TryGetValue(selected, out var credits) && credits.Count > 0)
        {
            g.Label("Credit（自動）");
            foreach (var credit in credits) g.Note(credit.title + "（" + credit.work + "）｜by " + credit.author + "｜版本 " + credit.revision);
        }
        if (g.Button("渲染作品", P + "btn/work-render")) RunView(g, "作品 " + selected, new() { ["work"] = selected }, persona, SubjectWork + selected);
        var text = m_WorkTexts.TryGetValue(selected, out var saved) ? saved : (Notes: "", Todo: "");
        string titleKey = P + "work/" + selected + "/title", notesKey = P + "work/" + selected + "/notes", todoKey = P + "work/" + selected + "/todo";
        using (var notesFold = g.Fold("心得與續作 TODO", P + "fold/work-notes", iDefaultOpen: true))
        {
            if (notesFold.Open)
            {
                string title = g.TextField("名稱", card.title, titleKey);
                string dimensions = g.TextField("空間尺寸（邊長或X,Y,Z）", card.Dimensions, P + "work/" + selected + "/size");
                g.Note("調整空間不移動或縮放作品；縮小若會切掉內容，整次保存會拒絕。");
                string notes = g.TextArea("心得／進度／下次從哪開始", text.Notes, notesKey, 6);
                string todo = g.TextArea("TODO", text.Todo, todoKey, 4);
                if (persona == card.owner)
                {
                    if (g.Button("保存尺寸與筆記（免費）", P + "btn/work-save"))
                        Start(g, "保存尺寸與筆記", () => new Outcome { Log = RunCli(new() { ["op"] = "work", ["sub"] = "update", ["work"] = selected, ["persona"] = persona, ["title"] = title, ["size"] = dimensions, ["notes"] = notes, ["todo"] = todo }).Log("保存尺寸與筆記"), Reload = true });
                }
                else g.Note("目前 persona 不是作者；可觀測，不能修改。");
            }
        }
        if (persona == card.owner) DrawWorkEditing(g, persona, selected);
        using var importFold = g.Fold("匯入共用展區", P + "fold/work-import", iDefaultOpen: false);
        if (!importFold.Open) return;
        string at = g.TextField("展區座標（作品原點0,0,0平移到這裡）", "0,0,0", WorkAt);
        string exhibit = g.TextField("新的展品 ID", selected + "-exhibit", WorkExhibit);
        string signature = selected + "|" + persona + "|" + at + "|" + exhibit;
        var importArgs = new Dictionary<string, string> { ["op"] = "work", ["sub"] = "import", ["work"] = selected, ["persona"] = persona, ["at"] = at, ["exhibit_id"] = exhibit };
        if (g.Button("預覽匯入數量與費用", P + "btn/work-import-preview"))
        {
            Start(g, "匯入預覽", () =>
            {
                var result = RunCli(importArgs);
                var outcome = new Outcome { Log = result.Log("匯入預覽") };
                outcome.Fields[ImportSig] = result.Exit == 0 ? signature : "";
                outcome.Fields[ImportRevision] = result.Value("revision", "");
                outcome.Fields[ImportCount] = result.Value("would_place", "");
                return outcome;
            });
        }
        if (g.FieldValue(ImportSig, "") != signature) { g.Note("先預覽，再確認匯入。來源版本或展區落地數改變會拒絕提交。"); return; }
        g.Note("預覽版本 " + g.FieldValue(ImportRevision, "") + "；落地 " + g.FieldValue(ImportCount, "") + " voxels。既有格子會跳過；越界拒絕。");
        Armed(g, g.FieldValue(Pending, ""), "work-import", "匯入並支付落地費用", () =>
        {
            importArgs["confirm"] = "1";
            importArgs["expect_revision"] = g.FieldValue(ImportRevision, "");
            importArgs["expect_placed"] = g.FieldValue(ImportCount, "");
            Start(g, "匯入作品", () =>
            {
                var outcome = new Outcome { Log = RunCli(importArgs).Log("匯入作品"), Reload = true };
                outcome.Fields[ImportSig] = "";
                return outcome;
            });
        });
    }

    /// <summary>長筆記走 UTF-8 arg-file；每次呼叫獨占隨機檔，子行程結束才清掉。</summary>
    sealed class WorkArgumentFiles : IDisposable
    {
        readonly List<string> m_Files = new();
        public string Add(string text)
        {
            string path = Path.Combine(Path.GetTempPath(), "senate-sculpt-work-" + Guid.NewGuid().ToString("N") + ".md");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
            m_Files.Add(path);
            return path;
        }
        public void Dispose() { foreach (string path in m_Files) File.Delete(path); }
    }
}
