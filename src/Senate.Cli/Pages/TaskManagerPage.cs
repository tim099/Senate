// 區塊職責：**任務與專案管理頁**（TASK-0349）—— 篩選、看單、留言、推狀態。
// 物理意義：**讀**走 `SCP_TaskIO`（純讀，磁碟即事實）；**寫**一律走 `cmd task`（入口 → Server `task-write`）——
//           ⛔ 本頁不直接呼叫 `SCP_TaskStore`：那等於**安靜地**多一個寫入端（同 BankAdminPage 的界線）。
//           ⇒ 本頁按下去的每一個動作，跟 agent 打 `senate cmd task` 走的是同一條路（閘、時間線、酒館通知都一樣）。
// 數值影響：讀 ＝ 每次重讀全掃一次 `tasks/*.md`（358 張 2026-09-30）；每 2 秒看一次計數檔與最大檔名，有變才重讀。
//           寫 ＝ 一次 `cmd task`（背景跑，畫面不卡）；寫完回讀磁碟再畫（判準是回讀，不是回傳字串）。
//
// ⚠ 刻意的設計：
//   · 結單（done）**走 `op=resolve` 的閘**：有未解 blocker 時按鈕不出現、寫入端也會擋；單上有 QA 而署名的人不是他 ⇒
//     帶 `qa_note=後台頁代簽（<署名>）`（⛔ 不直接寫檔繞過 QA 閘）。
//   · 推 in_progress／in_review 走 `op=update`（會進時間線；不發酒館通知 —— 與 agent 走 update 同一個規則）。
//   · 二段確認：done／cancelled 要按兩次（誤點的後果是別人的單被關）。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元，U+FFFF 以上畫成方框 —— TASK-0356）；單子內文裡的 emoji 是原文，照印。
#nullable enable
using System.Globalization;
using System.Threading.Tasks;
using SCP.Core.Cmd;
using SCP.Core.Gui;
using SCP.Core.Paths;
using SCP.Core.Tasks;

namespace Senate.Cli.Pages;

public sealed class TaskManagerPage : SCP_GuiToolPage
{
    public const string PageKey = "tasks";
    public const int PageSize = 25;
    static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>session 欄位：篩選、署名、待確認。</summary>
    public const string StatusId = "tasks/sel/status";
    public const string TypeId = "tasks/sel/type";
    public const string PersonaId = "tasks/sel/persona";
    public const string ClosedId = "tasks/toggle/closed";
    public const string AuthorId = "tasks/author";
    public const string PendingId = "tasks/pending";

    readonly SenateModel m_Model;
    string m_DataRoot = "";
    string? m_Error;
    bool m_Loaded;
    List<SCP_TaskEntry> m_All = new();
    Dictionary<int, List<string>> m_Blockers = new();
    int m_Open, m_Stale, m_Broken, m_Blocked;
    int m_Page;
    (int counter, int disk) m_Stamp;
    DateTime m_NextPollUtc = DateTime.MinValue;

    Task<string>? m_Job;
    string m_JobLabel = "";
    string? m_Message;

    public TaskManagerPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "任務與專案管理";
    public override string? MenuGroup => "任務";

    public override void OnPush()
    {
        base.OnPush();
        m_Loaded = false;
    }

    void Reload()
    {
        m_Loaded = true;
        m_Error = null;
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            m_Error = $"找不到 AgentCommands 資料根（{m_DataRoot}）—— 到「路徑管理」頁設定";
            m_All = new(); m_Blockers = new();
            return;
        }
        var aRoot = new SCP_DataRoot(m_DataRoot);
        var aWarn = new List<string>();
        m_All = SCP_TaskIO.LoadAll(aRoot, aWarn.Add);
        m_Blockers = new();
        foreach (SCP_TaskEntry e in m_All)
            if (!e.IsClosed() && e.blocked_by.Count > 0) m_Blockers[e.index] = SCP_TaskStore.OpenBlockers(aRoot, e);
        SCP_TaskStore.CountStats(aRoot, m_All, out m_Open, out m_Stale, out m_Broken, out m_Blocked);
        m_Stamp = ReadStamp();
        if (aWarn.Count > 0) m_Error = $"有 {aWarn.Count} 張單讀取時出聲（壞欄位落回預設）：{aWarn[0]}";
    }

    (int, int) ReadStamp()
    {
        if (m_DataRoot.Length == 0) return (0, 0);
        var aRoot = new SCP_DataRoot(m_DataRoot);
        return (SCP_TaskIO.ReadCurrentIndex(aRoot), SCP_TaskStore.MaxIndexByFileName(aRoot));
    }

    /// <summary>別人（agent）寫了單 ⇒ 自動重讀。⚠ 只看配號那兩個數：留言／改狀態不會動它們 ⇒ 那些要按「重新讀取」。</summary>
    void PollForNew()
    {
        if (m_Job != null || DateTime.UtcNow < m_NextPollUtc) return;
        m_NextPollUtc = DateTime.UtcNow + PollInterval;
        if (ReadStamp() != m_Stamp) m_Loaded = false;
    }

    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", "tasks/btn/reload")) m_Loaded = false;
        // Open Folder。⚠ 路徑直接取 model —— TopBar 先於 DrawContent 畫，第一次 Reload 前 m_DataRoot 還是空的
        string aRoot = m_Model.AgentCommandsRoot.Value;
        OpenFolderButton(iUi, aRoot.Length > 0 ? SCP_TaskIO.TasksDir(new SCP_DataRoot(aRoot)) : null, "tasks/btn/open-dir");
        var aStatuses = new List<string> { "open", "all" };
        foreach (string s in Enum.GetNames(typeof(SCP_TaskStatus))) if (s != "all" && s != "open") aStatuses.Add(s);
        string aStatus = iUi.Dropdown("狀態", aStatuses, "open", StatusId);
        var aTypes = new List<string>(Enum.GetNames(typeof(SCP_TaskType)));
        iUi.Dropdown("類型", aTypes, "all", TypeId);
        var aPersonas = new List<string> { "全部" };
        foreach (SCP_TaskEntry e in m_All)
        {
            foreach (SCP_TaskParticipant p in e.participants)
                if (!aPersonas.Contains(p.persona, StringComparer.OrdinalIgnoreCase)) aPersonas.Add(p.persona);
            if (e.reporter.Length > 0 && !aPersonas.Contains(e.reporter, StringComparer.OrdinalIgnoreCase)) aPersonas.Add(e.reporter);
        }
        iUi.Dropdown("參與者", aPersonas, "全部", PersonaId);
        if (aStatus == "all" || aStatus == "open")
            iUi.Toggle("含已關（done／cancelled）", false, ClosedId);
        if (iUi.Button("較新", "tasks/btn/prev") && m_Page > 0) m_Page--;
        if (iUi.Button("較舊", "tasks/btn/next")) m_Page++;
    }

    protected override void DrawContent(SCP_Ui g)
    {
        PumpJob();
        if (!m_Loaded) Reload();
        PollForNew();
        g.Title("任務與專案管理");
        if (m_Error != null) g.Note("[注意] " + m_Error);
        if (m_Job != null) g.Note($"執行中：{m_JobLabel}（寫入端是 Senate Server；完成後自動重讀）");
        if (m_Message != null) g.Note(m_Message);

        string aAuthor = g.TextField("署名（留言與推狀態記在誰名下）", "Tim", AuthorId).Trim();
        g.Label($"總 {m_All.Count} 張｜未關 {m_Open}｜被阻塞 {m_Blocked}｜stale（in_progress ≥{SCP_TaskStore.STALE_DAYS} 天）{m_Stale}"
            + (m_Broken > 0 ? $"｜時戳壞掉 {m_Broken}（不算進 stale）" : ""));

        List<SCP_TaskEntry> aList = Filtered(g);
        int aPages = Math.Max(1, (aList.Count + PageSize - 1) / PageSize);
        if (m_Page >= aPages) m_Page = aPages - 1;
        g.Label($"篩選結果 {aList.Count} 張｜第 {m_Page + 1} / {aPages} 頁（每頁 {PageSize} 張；stale 排最前，其餘依單號）");
        if (aList.Count == 0)
        {
            g.Note($"（沒有符合的單。這是「篩不到」不是「系統沒東西」—— 全部有 {m_All.Count} 張）");
            return;
        }
        g.Separator();
        foreach (SCP_TaskEntry e in aList.Skip(m_Page * PageSize).Take(PageSize)) DrawTask(g, e, aAuthor);
    }

    List<SCP_TaskEntry> Filtered(SCP_Ui g)
    {
        string aStatus = g.FieldValue(StatusId + "/value", "open");
        string aType = g.FieldValue(TypeId + "/value", "all");
        string aPersona = g.FieldValue(PersonaId + "/value", "全部");
        bool aClosed = g.ToggleValue(ClosedId, false);
        DateTime aNow = DateTime.UtcNow;
        IEnumerable<SCP_TaskEntry> q = m_All;
        if (aStatus == "open") q = q.Where(e => aClosed || !e.IsClosed());
        else if (aStatus == "all") q = q.Where(e => aClosed || !e.IsClosed());
        else if (SCP_TaskWire.TryParse(aStatus, out SCP_TaskStatus s)) q = q.Where(e => e.status == s);
        if (aType != "all" && SCP_TaskWire.TryParse(aType, out SCP_TaskType t)) q = q.Where(e => e.type == t);
        if (aPersona != "全部")
            q = q.Where(e => e.RolesOfAnyCase(aPersona).Count > 0 || string.Equals(e.reporter, aPersona, StringComparison.OrdinalIgnoreCase));
        return q.OrderByDescending(e => IsStale(e, aNow)).ThenBy(e => e.index).ToList();
    }

    static bool IsStale(SCP_TaskEntry e, DateTime iNow)
        => e.status == SCP_TaskStatus.in_progress && e.DaysSinceUpdate(iNow) >= SCP_TaskStore.STALE_DAYS;

    void DrawTask(SCP_Ui g, SCP_TaskEntry e, string iAuthor)
    {
        m_Blockers.TryGetValue(e.index, out List<string>? aBlk);
        int aBlkN = aBlk?.Count ?? 0;
        bool aStale = IsStale(e, DateTime.UtcNow);
        string aWho = e.participants.Count == 0 ? "（無參與者）" : string.Join("、", e.participants.Select(p => $"{p.persona}({p.role})"));
        string aTitle = $"{e.Id}　[{e.type}/{e.priority}" + (e.severity == SCP_TaskSeverity.none ? "" : "/" + e.severity) + $"]　{e.status}"
            + (aBlkN > 0 ? $"　[被阻塞 {aBlkN}]" : "") + (aStale ? "　[stale]" : "") + $"　{aWho}　{e.title}";
        using (var aFold = g.Fold(aTitle, "tasks/item/" + e.index.ToString(CultureInfo.InvariantCulture), iDefaultOpen: false))
        {
            if (!aFold.Open) return;
            using (g.IdScope("t" + e.index))
            {
                g.Label($"開單：{e.reporter}　建立 {Local(e.created_at)}　更新 {Local(e.updated_at)}" + (e.closed_at.Length > 0 ? $"　結單 {Local(e.closed_at)}" : ""));
                g.Label("參與：" + aWho);
                if (e.QaPersonas().Count == 0) g.Note("這張單沒有指名 QA ⇒ 結單由開單人或 PM 做，沒有 QA 閘會擋");
                g.Label($"blocked_by: {Ids(e.blocked_by)}　blocks: {Ids(e.blocks)}　related_to: {Ids(e.related_to)}"
                    + (e.epic_id.Length > 0 ? $"　屬於 {e.epic_id}" : "") + (e.subtask_indices.Count > 0 ? $"　子任務 {Ids(e.subtask_indices)}" : ""));
                if (aBlk != null) foreach (string b in aBlk) g.Note("未解 blocker：" + b);
                if (e.commit_shas.Count > 0) g.Label("commit：" + string.Join(" ", e.commit_shas));
                if (e.memory_topic.Length > 0) g.Label("工作記憶：" + e.memory_topic);
                string aPath = SCP_TaskIO.TaskPath(new SCP_DataRoot(m_DataRoot), e.index);
                using (g.Row())
                {
                    // ⚠ 在檢視頁裡**編輯**單檔 ＝ 繞過 `cmd task` 的寫入端（不進時間線、不發通知）—— 那一頁的存檔有衝突閘，但閘不了這件事本身
                    if (g.Button("開啟單檔", "tasks/btn/open-file/" + e.index)) MarkdownViewerPage.Open(g, Controller, m_Model, aPath);
                    g.Note("單檔：" + aPath);
                }

                string aCriteria = SCP_TaskStore.ReadSection(aPath, "## 驗收標準");
                int aDone = SCP_TaskStore.ListCheckedCriteria(aCriteria).Count, aOpenBox = SCP_TaskStore.ListUncheckedCriteria(aCriteria).Count;
                using (var aC = g.Fold($"驗收標準（已勾 {aDone}／未勾 {aOpenBox}）", "tasks/crit/" + e.index, iDefaultOpen: false))
                    if (aC.Open) g.Paragraph(aCriteria.Length == 0 ? "（未填）" : aCriteria);
                using (var aD = g.Fold("任務描述", "tasks/desc/" + e.index, iDefaultOpen: false))
                    if (aD.Open)
                    {
                        string aDesc = SCP_TaskStore.ReadSection(aPath, "## 任務描述");
                        g.Paragraph(aDesc.Length == 0 ? "（未填）" : aDesc);
                    }

                if (!e.IsClosed()) DrawStatusButtons(g, e, aBlkN, iAuthor);

                g.Separator();
                g.Label($"留言（{e.comments.Count} 則）");
                foreach (SCP_TaskComment c in e.comments)
                {
                    g.Label($"#{c.id}　{c.persona}　{Local(c.at)}");
                    g.Paragraph(c.body);
                }
                string aDraftId = "tasks/draft/" + e.index;
                string aDraft = g.TextField("新留言", "", aDraftId);
                if (aDraft.Trim().Length > 0 && g.Button("送出留言", "tasks/btn/comment/" + e.index))
                {
                    if (!Sign(iAuthor)) return;
                    string aBody = aDraft.Trim();
                    g.SetField(aDraftId, "");
                    Run($"{e.Id} 留言", new Dictionary<string, string> { ["op"] = "comment", ["index"] = e.index.ToString(CultureInfo.InvariantCulture), ["body"] = aBody }, iAuthor);
                }
            }
        }
    }

    void DrawStatusButtons(SCP_Ui g, SCP_TaskEntry e, int iBlockers, string iAuthor)
    {
        string aIdx = e.index.ToString(CultureInfo.InvariantCulture);
        using (g.Row())
        {
            foreach (SCP_TaskStatus s in new[] { SCP_TaskStatus.todo, SCP_TaskStatus.in_progress, SCP_TaskStatus.in_review })
            {
                if (e.status == s) continue;
                if (g.Button("改為 " + s, $"tasks/btn/{s}/{aIdx}") && Sign(iAuthor))
                    Run($"{e.Id} 改為 {s}", new Dictionary<string, string> { ["op"] = "update", ["index"] = aIdx, ["status"] = s.ToString() }, iAuthor);
            }
            // 結單：二段確認。有未解 blocker 時 done 不給按（寫入端也會擋 —— 這裡是讓人先看到為什麼）。
            string aPending = g.FieldValue(PendingId, "");
            foreach (SCP_TaskStatus s in new[] { SCP_TaskStatus.done, SCP_TaskStatus.cancelled })
            {
                if (s == SCP_TaskStatus.done && iBlockers > 0) continue;
                string aKey = $"{s}/{aIdx}";
                bool aArmed = aPending == aKey;
                if (!g.Button(aArmed ? $"確定 {s}？再按一次" : $"結單 {s}", $"tasks/btn/{aKey}")) continue;
                if (!aArmed) { g.SetField(PendingId, aKey); continue; }
                g.SetField(PendingId, "");
                if (!Sign(iAuthor)) continue;
                var aArgs = new Dictionary<string, string>
                {
                    ["op"] = "resolve", ["index"] = aIdx, ["status"] = s.ToString(), ["confirm"] = "1",
                    ["note"] = $"後台頁結單（{iAuthor}）",
                };
                List<string> aQa = e.QaPersonas();
                if (aQa.Count > 0 && !aQa.Any(q => string.Equals(q, iAuthor, StringComparison.OrdinalIgnoreCase)))
                    aArgs["qa_note"] = $"後台頁代簽（{iAuthor}；本單 QA：{string.Join(" / ", aQa)}）";
                Run($"{e.Id} 結單 {s}", aArgs, iAuthor);
            }
        }
        if (iBlockers > 0) g.Note($"有 {iBlockers} 個未解 blocker ⇒ 不能結 done（cancelled 可以 —— 取消一張被卡的單是合理的）");
    }

    bool Sign(string iAuthor)
    {
        if (iAuthor.Length > 0) return true;
        m_Message = "署名是空的 ⇒ 這次沒有動作（時間線要記在誰名下，⛔ 不猜）";
        return false;
    }

    // ── 寫入：一律 `cmd task`（背景跑）──────────────────────────

    void Run(string iLabel, Dictionary<string, string> iArgs, string iAuthor)
    {
        iArgs["persona"] = iAuthor;
        iArgs["data_root"] = m_DataRoot;
        Start(iLabel, () =>
        {
            SCP_CmdResult r = SCP_CmdRegistry.Dispatch("task", iArgs);
            string aHead = r.Lines.LastOrDefault(l => l.StartsWith("✓", StringComparison.Ordinal) || l.StartsWith("·", StringComparison.Ordinal)
                                                      || l.StartsWith("✗", StringComparison.Ordinal) || l.StartsWith("⛔", StringComparison.Ordinal)) ?? "";
            string aTail = r.ExitCode switch
            {
                0 => "",
                1 => "　⇒ 被閘擋下（零寫入）—— 原因見回傳檔" + (r.Outputs.Count > 0 ? "：" + r.Outputs[0] : ""),
                7 => "　⇒ **結果不明**（等不到寫入端回執）—— 先重新讀取看單子，⛔ 別直接再按一次",
                _ => $"　⇒ exit {r.ExitCode}（確定沒寫）",
            };
            return $"{iLabel}：{(aHead.Length > 0 ? aHead : "exit " + r.ExitCode)}{aTail}";
        });
    }

    void Start(string iLabel, Func<string> iJob)
    {
        if (m_Job != null) { m_Message = "前一筆（" + m_JobLabel + "）還沒完 ⇒ 這次沒有動作"; return; }
        if (SCP_GuiHost.RedrawsContinuously)
        {
            m_JobLabel = iLabel;
            m_Job = Task.Run(iJob);
            m_Message = null;
            return;
        }
        // 不會重畫的宿主（CLI 單次 render）：背景跑等於把答案丟掉 —— 這裡同步。
        try { m_Message = iJob(); }
        catch (Exception e) { m_Message = "那一步炸了：" + e.GetType().Name + ": " + e.Message; }
        m_Loaded = false;
    }

    void PumpJob()
    {
        if (m_Job == null || !m_Job.IsCompleted) return;
        Task<string> aJob = m_Job;
        m_Job = null;
        try { m_Message = aJob.Result; }
        catch (Exception e) { m_Message = "那一步炸了：" + e.GetType().Name + ": " + e.Message; }
        m_Loaded = false;   // 回讀磁碟才是判準
    }

    static string Ids(List<int> iList)
        => iList.Count == 0 ? "—" : string.Join(" ", iList.Select(i => "TASK-" + i.ToString("0000", CultureInfo.InvariantCulture)));

    /// <summary>UTC ISO → 本地 `MM-dd HH:mm`（顯示層才轉當地）。解析不了原樣印。</summary>
    static string Local(string iIso)
    {
        if (!DateTime.TryParse(iIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aUtc)) return iIso;
        return DateTime.SpecifyKind(aUtc, DateTimeKind.Utc).ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}
