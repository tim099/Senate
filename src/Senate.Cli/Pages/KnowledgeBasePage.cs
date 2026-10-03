// 區塊職責：**知識庫後台頁**（TASK-0381）—— 每個 target 的檔數／塊數／新鮮度、常駐嵌入程序在不在、重建、檢索與評估；
//           參考 Unity `UCL_KnowledgeBaseAdminPage`（之後廢棄，Tim 2026-10-02）。
// 物理意義：本頁**不自己算任何東西**：狀態、重建、檢索、評估全部走 `senate cmd kb`（Cmd_Kb），
//           跟 agent 在終端機打的是同一套實作 ⇒ 兩邊不可能對同一份索引給出不同的讀數。
//           狀態與檢索用 `format=json` 讀結構化結果；重建／評估／常駐程序的結果照印 Cmd 的行。
// 數值影響：開頁自動量一次狀態（純 stat、不載模型）；重建／檢索／評估會拉起常駐嵌入程序（冷啟動約 1 分多鐘）⇒ 全部跑背景 job，
//           畫面不卡；同一時間只有一個 job（重建與檢索共用常駐程序，排隊沒有意義）。
// ⚠ 三個「不得同形」：
//   · 常駐程序「沒在跑」≠「0 句」：沒在跑只畫一句話，不畫已嵌句數。
//   · 「量不到狀態」≠「沒有 target」：狀態讀不到時畫錯誤框，不畫空表。
//   · 缺相依（exit 3）≠ 一般錯誤：畫成專屬的框，指去「安裝管理」頁；⛔ 本頁不自己裝。
// ⚠ 排序方式（dense／hybrid，之後可能加 reranker）依 TASK-0382 拍板：本頁只提供「選模式＋跑評估」，
//   ⛔ 不在頁面裡寫死哪一種是預設 —— 預設住在 Cmd_Kb。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元，U+FFFF 以上畫成方框 —— TASK-0356）。
#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class KnowledgeBasePage : SCP_GuiToolPage
{
    public const string PageKey = "kb";
    public const string TargetId = "kb/sel/target";
    public const string ModeId = "kb/sel/mode";
    public const string QueryId = "kb/query";
    public const string SparseId = "kb/sparse";
    public const string DecayId = "kb/toggle/decay";
    public const string EvalModeId = "kb/sel/evalmode";

    const string DefaultTargetValue = "(預設)";
    const string AllTargetValue = "all";
    const int TopK = 8;

    internal sealed record Row(string Name, string State, int Files, int Chunks, string BuiltAt, string Detail);
    internal sealed record StatusView(bool Running, int Pid, string Device, double LoadedSec, long Served, string IndexDir, List<Row> Rows);
    sealed record Hit(double Score, string Target, string Id, string File, string Rel, int Line, string Heading, string Preview);
    sealed record Done(string Kind, string Label, SCP_CmdResult R);

    readonly SenateModel m_Model;

    StatusView? m_Status;
    string? m_StatusError;
    bool m_StatusStale = true;      // true ＝ 要（重新）量；開頁、每個動作做完都會設
    Task<Done>? m_Job;
    string m_JobLabel = "";
    string? m_Message;
    List<string>? m_MissingLines;   // 缺相依時要攤的說明（null ＝ 沒有缺）
    string? m_SearchError;
    string m_SearchHeader = "";
    List<Hit>? m_Hits;              // null ＝ 還沒查；Count == 0 ＝ 查了但沒命中（兩者畫法不同）
    List<string>? m_EvalLines;

    public KnowledgeBasePage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "知識庫";
    public override string? MenuGroup => "工具";

    public override void OnPush()
    {
        base.OnPush();
        m_StatusStale = true;
    }

    // ── 參數與 job ───────────────────────────────────────────────

    Dictionary<string, string>? BaseArgs(out string? oError)
    {
        string aData = m_Model.AgentCommandsRoot.Value, aProj = m_Model.ProjectRoot.Value;
        oError = null;
        if (string.IsNullOrEmpty(aData) || !Directory.Exists(aData)) { oError = $"找不到 AgentCommands 資料根（{aData}）—— 到「路徑管理」頁設定"; return null; }
        if (string.IsNullOrEmpty(aProj) || !Directory.Exists(aProj)) { oError = $"找不到專案根（{aProj}）—— 到「路徑管理」頁設定"; return null; }
        return new Dictionary<string, string> { ["data_root"] = aData, ["project_root"] = aProj };
    }

    void Start(string iKind, string iLabel, Dictionary<string, string> iArgs)
    {
        if (m_Job != null) { m_Message = $"前一筆（{m_JobLabel}）還沒完 ⇒ 這次沒有動作"; return; }
        m_Message = null;
        m_MissingLines = null;
        Func<Done> aWork = () => new Done(iKind, iLabel, SCP_CmdRegistry.Dispatch("kb", iArgs));
        if (SCP_GuiHost.RedrawsContinuously)
        {
            m_JobLabel = iLabel;
            m_Job = Task.Run(aWork);
            return;
        }
        // 不會重畫的宿主（CLI 單次 render）：背景跑等於把答案丟掉 —— 這裡同步。
        try { Harvest(aWork()); }
        catch (Exception e) { m_Message = "那一步炸了：" + e.GetType().Name + ": " + e.Message; }
    }

    void PumpJob()
    {
        if (m_Job == null || !m_Job.IsCompleted) return;
        Task<Done> aJob = m_Job;
        m_Job = null;
        try { Harvest(aJob.Result); }
        catch (Exception e) { m_Message = "那一步炸了：" + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
    }

    static string Joined(SCP_CmdResult r) => string.Join("\n", r.Lines);

    /// <summary>UI 執行緒把背景結果搬進頁面。</summary>
    void Harvest(Done d)
    {
        SCP_CmdResult r = d.R;
        if (d.Kind == "status")
        {
            m_StatusStale = false;
            m_StatusError = null;
            if (r.ExitCode != 0) { m_Status = null; m_StatusError = $"狀態量不到（exit {r.ExitCode}）：" + Joined(r); return; }
            m_Status = ParseStatus(Joined(r), out m_StatusError);
            return;
        }

        // 缺相依（3）與量不到（4）要分開：前者是「確定缺」，後者是「不知道」—— ⛔ 後者不能叫人去裝。
        if (r.ExitCode == 3 || r.ExitCode == 4)
        {
            m_MissingLines = new List<string> { r.ExitCode == 3 ? "缺相依 —— 知識庫的嵌入程序需要下面這些套件／模型：" : "量不到相依（不知道缺不缺）—— 先別裝，到「安裝管理」頁重新量：" };
            m_MissingLines.AddRange(r.Lines.Where(l => l.Trim().Length > 0 && !l.StartsWith("## ", StringComparison.Ordinal)));
            m_Message = $"{d.Label}：{(r.ExitCode == 3 ? "缺相依（零變動）" : "量不到相依（零變動）")}";
            return;
        }

        switch (d.Kind)
        {
            case "search":
                ParseSearch(r);
                m_Message = null;
                m_StatusStale = true;   // 自動重建過的 target 要重量
                return;
            case "eval":
                m_EvalLines = r.ExitCode == 0 ? r.Lines.ToList() : new List<string> { $"評估沒跑成（exit {r.ExitCode}）：" + Joined(r) };
                m_StatusStale = true;
                return;
        }

        string aHead = r.Lines.LastOrDefault(l => l.StartsWith("✓", StringComparison.Ordinal) || l.StartsWith("✗", StringComparison.Ordinal)
                                                 || l.StartsWith("⛔", StringComparison.Ordinal) || l.StartsWith("·", StringComparison.Ordinal)) ?? "";
        string aAll = d.Kind == "reindex" ? string.Join("　／　", r.Lines.Where(l => l.StartsWith("✓", StringComparison.Ordinal))) : aHead;
        m_Message = $"{d.Label}：{(r.ExitCode == 0 ? (aAll.Length > 0 ? aAll : "完成") : $"exit {r.ExitCode} —— " + Joined(r))}";
        m_StatusStale = true;   // 動完一律重新量 —— 畫面要是「動完之後量到的」
    }

    // ── 解析 ────────────────────────────────────────────────────

    static string? JsonOf(string iText)
    {
        int s = iText.IndexOf('{'), e = iText.LastIndexOf('}');
        return s >= 0 && e > s ? iText.Substring(s, e - s + 1) : null;
    }

    internal static StatusView? ParseStatus(string iText, out string? oError)
    {
        oError = null;
        string? aJson = JsonOf(iText);
        if (aJson == null) { oError = "狀態輸出不是 JSON：" + iText; return null; }
        try
        {
            using JsonDocument d = JsonDocument.Parse(aJson);
            JsonElement root = d.RootElement, car = root.GetProperty("sidecar");
            var aRows = new List<Row>();
            foreach (JsonElement t in root.GetProperty("targets").EnumerateArray())
                aRows.Add(new Row(t.GetProperty("name").GetString() ?? "?", t.GetProperty("state").GetString() ?? "?",
                    t.GetProperty("files").GetInt32(), t.GetProperty("chunks").GetInt32(),
                    t.GetProperty("built_at").GetString() ?? "", t.GetProperty("detail").GetString() ?? ""));
            return new StatusView(car.GetProperty("running").GetBoolean(), car.GetProperty("pid").GetInt32(),
                car.GetProperty("device").GetString() ?? "", car.GetProperty("loaded_ms").GetInt64() / 1000.0,
                car.GetProperty("served").GetInt64(), root.GetProperty("index_dir").GetString() ?? "", aRows);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            oError = "狀態 JSON 解析失敗：" + e.Message;
            return null;
        }
    }

    void ParseSearch(SCP_CmdResult iR)
    {
        m_Hits = null; m_SearchHeader = ""; m_SearchError = null;
        if (iR.ExitCode != 0) { m_SearchError = $"檢索沒跑成（exit {iR.ExitCode}）：" + Joined(iR); return; }
        string? aJson = JsonOf(Joined(iR));
        if (aJson == null) { m_SearchError = "檢索輸出不是 JSON：" + Joined(iR); return; }
        try
        {
            using JsonDocument d = JsonDocument.Parse(aJson);
            JsonElement root = d.RootElement;
            var aHits = new List<Hit>();
            foreach (JsonElement h in root.GetProperty("hits").EnumerateArray())
                aHits.Add(new Hit(h.GetProperty("score").GetDouble(), h.GetProperty("target").GetString() ?? "?", h.GetProperty("id").GetString() ?? "",
                    h.GetProperty("file").GetString() ?? "", h.GetProperty("rel").GetString() ?? "", h.GetProperty("line").GetInt32(),
                    h.TryGetProperty("heading", out JsonElement hd) ? hd.GetString() ?? "" : "", h.GetProperty("preview").GetString() ?? ""));
            var aNotes = root.GetProperty("notes").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            var aRe = root.GetProperty("auto_reindexed").EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList();
            m_SearchHeader = $"{aHits.Count} 命中　掃 {root.GetProperty("searched_chunks").GetInt32()} 塊　{root.GetProperty("mode").GetString()}　"
                           + $"嵌入 {root.GetProperty("query_ms").GetInt64()} ms／全程 {root.GetProperty("latency_ms").GetInt64()} ms"
                           + (aRe.Count > 0 ? "　先重建了過期的：" + string.Join("、", aRe) : "")
                           + (aNotes.Count > 0 ? "　[注意] " + string.Join("；", aNotes) : "");
            m_Hits = aHits;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            m_SearchError = "檢索 JSON 解析失敗：" + e.Message;
        }
    }

    // ── 畫面 ────────────────────────────────────────────────────

    bool Busy => m_Job != null;

    protected override void TopBarButtons(SCP_Ui g)
    {
        if (!Busy && g.Button("重新量狀態", "kb/btn/reload")) m_StatusStale = true;
        // 嵌入模型／重排模型／套件都在安裝管理頁裝；知識庫頁不自己裝（同一套實作，只有一個地方動手）。
        // 工作中也給按：換頁不會中斷背景 job（job 在本頁物件上，返回後照樣收尾）。
        if (g.Button("安裝管理", "kb/btn/top-install")) Controller?.Push(new InstallPage(m_Model));
    }

    protected override void DrawContent(SCP_Ui g)
    {
        PumpJob();
        Dictionary<string, string>? aBase = BaseArgs(out string? aBaseErr);
        if (m_StatusStale && !Busy && aBase != null)
            Start("status", "量狀態", new Dictionary<string, string>(aBase) { ["op"] = "status", ["format"] = "json" });

        g.Title("知識庫");
        g.Note("語意檢索的索引管理。本頁走 `senate cmd kb`（同一套實作）；索引在 <資料根>/_kb/<target>/，target 清單讀 UCL_Core 的 kb_targets.json。");
        if (aBaseErr != null) { g.Note("[注意] " + aBaseErr); return; }
        if (Busy) g.Note($"執行中：{m_JobLabel}（第一次要拉起常駐嵌入程序，冷啟動約 1 分多鐘；完成後自動更新）");
        if (m_Message != null) g.Note(m_Message);
        if (m_MissingLines != null) DrawMissing(g);

        DrawStatus(g);
        if (m_Status != null) DrawTargets(g, aBase!);
        DrawSearch(g, aBase!);
        DrawEval(g, aBase!);
    }

    void DrawMissing(SCP_Ui g)
    {
        using (g.Box("缺相依", "kb/missing"))
        {
            foreach (string l in m_MissingLines!) g.Note(l);
            g.Note("本頁不自己裝：到「安裝管理」頁看狀態、安裝（它會先把計畫攤開、你確認才動手）。");
            if (g.Button("到安裝管理頁", "kb/btn/to-install")) Controller?.Push(new InstallPage(m_Model));
        }
    }

    void DrawStatus(SCP_Ui g)
    {
        using (g.Box("環境與常駐嵌入程序", "kb/status"))
        {
            if (m_StatusError != null) { g.Note("[注意] " + m_StatusError); return; }
            if (m_Status == null) { g.Note(Busy ? "量狀態中…" : "（還沒量）"); return; }
            if (m_Status.Running)
                g.Label($"常駐嵌入程序：在跑（pid {m_Status.Pid}，{m_Status.Device}，載入花了 {m_Status.LoadedSec:0.0} 秒，已嵌 {m_Status.Served} 句）");
            else
                g.Label("常駐嵌入程序：沒在跑（第一次檢索或重建時會自己拉起；閒置 30 分鐘它也會自己退）");
            g.Label("索引位置：" + m_Status.IndexDir);
            if (!Busy)
                using (g.Row())
                {
                    if (!m_Status.Running && g.Button("啟動", "kb/btn/sidecar-start")) StartSidecar("start");
                    if (m_Status.Running && g.Button("關閉", "kb/btn/sidecar-stop")) StartSidecar("stop");
                }
        }
    }

    void StartSidecar(string iAction)
    {
        Dictionary<string, string>? a = BaseArgs(out _);
        if (a == null) return;
        a["op"] = "sidecar"; a["action"] = iAction;
        Start("sidecar", iAction == "start" ? "啟動常駐嵌入程序" : "關閉常駐嵌入程序", a);
    }

    void DrawTargets(SCP_Ui g, Dictionary<string, string> iBase)
    {
        StatusView s = m_Status!;
        using (g.Box("各 target 的索引", "kb/targets"))
        {
            int aStale = s.Rows.Count(r => r.State == "stale"), aUnbuilt = s.Rows.Count(r => r.State == "unbuilt");
            g.Label($"{s.Rows.Count} 個 target｜落後 {aStale}｜還沒建 {aUnbuilt}");
            using (g.Table("動作", "target", "狀態", "檔數", "塊數", "建於", "說明"))
            {
                foreach (Row r in s.Rows)
                {
                    using (g.TableRowScope())
                    {
                        // ⚠ 一格只能放一個節點：忙的時候畫成字（不給按）。
                        if (Busy || r.State == "unknown") g.TableCell("—");
                        else if (g.Button(r.State == "unbuilt" ? "建立" : "重建", $"kb/reindex/{r.Name}"))
                            StartReindex(iBase, r.Name, r.State == "unbuilt" ? "建立 " : "重建 ");
                        g.TableCell(r.Name);
                        g.TableCell(StateText(r.State));
                        g.TableCell(r.Files.ToString(CultureInfo.InvariantCulture));
                        g.TableCell(r.State == "unbuilt" ? "—" : r.Chunks.ToString(CultureInfo.InvariantCulture));
                        g.TableCell(r.BuiltAt.Length > 0 ? r.BuiltAt : "—");
                        g.TableCell(r.Detail);
                    }
                }
            }
            if (!Busy && g.Button("重建全部（all）", "kb/btn/reindex-all")) StartReindex(iBase, "all", "重建 ");
            g.Note("重建只重嵌有變的檔（沿用沒變的塊）；檢索本身也會先把過期的 target 重建，所以這裡是給想先看清楚再決定的人。");
        }
    }

    static string StateText(string iState) => iState switch
    {
        "fresh" => "最新",
        "stale" => "落後磁碟",
        "unbuilt" => "還沒建",
        _ => "不認得",
    };

    void StartReindex(Dictionary<string, string> iBase, string iTarget, string iVerb)
        => Start("reindex", iVerb + iTarget, new Dictionary<string, string>(iBase) { ["op"] = "reindex", ["target"] = iTarget });

    void DrawSearch(SCP_Ui g, Dictionary<string, string> iBase)
    {
        using (g.Box("檢索", "kb/search"))
        {
            var aTargets = new List<SCP_GuiOption> { new(DefaultTargetValue, $"預設（{Cmd_Kb.DefaultSearchTargets}）") };
            if (m_Status != null) foreach (Row r in m_Status.Rows) aTargets.Add(new SCP_GuiOption(r.Name));
            aTargets.Add(new SCP_GuiOption(AllTargetValue, "all（跨全部 target）"));
            string aTarget = g.Dropdown("範圍", aTargets, DefaultTargetValue, TargetId);
            List<string> aModes = ModeChoices(false);
            string aMode = g.Dropdown("排序方式", aModes, aModes[0], ModeId);
            if (aMode == "hybrid" || aMode == "rerank") g.TextField("sparse 權重（hybrid／rerank 的候選池才用）", "0.3", SparseId);
            bool aDecay = g.Toggle("時間衰減（只對碎片、工作記憶；文件類不衰減）", false, DecayId);
            g.Note("輸入是一句話不是關鍵字（語意檢索）。rerank 的分數是 0..1 的重排分，跟 dense／hybrid 的內積不同尺度，不能跨排序比大小。"
                 + "第一次用 rerank 要載入重排模型；缺重排模型會走「缺相依」。預設排序依 TASK-0382 的讀數拍板。");
            string aQuery = g.TextField("要找的事（一句話）", "", QueryId);
            if (!Busy && aQuery.Trim().Length > 0 && g.Button("檢索", "kb/btn/search"))
            {
                var a = new Dictionary<string, string>(iBase)
                {
                    ["op"] = "search", ["query"] = aQuery.Trim(), ["topk"] = TopK.ToString(CultureInfo.InvariantCulture),
                    ["mode"] = aMode, ["format"] = "json", ["decay"] = aDecay ? "1" : "0",
                };
                if (aTarget != DefaultTargetValue) a["target"] = aTarget;
                if (aMode == "hybrid" || aMode == "rerank") a["sparse_weight"] = g.FieldValue(SparseId, "0.3");
                Start("search", "檢索", a);
            }
            DrawHits(g);
        }
    }

    /// <summary>排序方式的選項**讀 Cmd 宣告的清單**（TASK-0382 加了新的排序，頁面不必跟著改）；第一個是 Cmd 的預設。</summary>
    static List<string> ModeChoices(bool iForEval)
    {
        SCP_CmdArgSpec? aSpec = SCP_CmdRegistry.Find("kb")?.ArgSpecs.FirstOrDefault(a => a.Name == "mode");
        var aList = aSpec == null ? new List<string>() : aSpec.Choices.ToList();
        if (!iForEval) aList.Remove("compare");   // compare 只給評估：一次比三種排序
        if (aList.Count == 0) aList.Add("hybrid");
        if (aSpec != null && aSpec.Default.Length > 0 && aList.Remove(aSpec.Default)) aList.Insert(0, aSpec.Default);
        return aList;
    }

    void DrawHits(SCP_Ui g)
    {
        if (m_SearchError != null) { g.Note("[注意] " + m_SearchError); return; }
        if (m_Hits == null) return;
        g.Label(m_SearchHeader);
        if (m_Hits.Count == 0)
        {
            // 「查了，0 命中」與「沒查」要能分辨 —— 空結果是一個答案，不是沒有答案
            g.Note("查了，0 命中（有索引但沒有語意相近的片段；若上面有 [注意]，是那個 target 沒查到）。");
            return;
        }
        // 一筆一張卡，不用表格：路徑與預覽都是長字串，表格的欄寬在窄視窗只會把每一欄裁成幾個字（Tim 2026-10-03 截圖）。
        for (int i = 0; i < m_Hits.Count; i++)
        {
            Hit h = m_Hits[i];
            string aFile = h.File.Replace('/', Path.DirectorySeparatorChar);
            bool aExists = File.Exists(aFile);
            using (g.Box($"#{i + 1}　{h.Score.ToString("0.0000", CultureInfo.InvariantCulture)}　{h.Target}", $"kb/hit/{i}"))
            {
                using (g.Row())
                {
                    if (aExists && SCP_GuiHost.RevealInFileManager != null)
                    {
                        if (g.Button("定位", $"kb/hit/{i}/reveal")) m_Message = SCP_GuiHost.RevealInFileManager(aFile);
                    }
                    g.Label(h.Rel + (h.Line > 0 ? ":" + h.Line.ToString(CultureInfo.InvariantCulture) : "") + (aExists ? "" : "　（檔案不存在）"));
                }
                if (h.Heading.Length > 0 && !h.Preview.StartsWith(h.Heading, StringComparison.Ordinal)) g.Note(h.Heading);   // 預覽本來就以標題路徑開頭，不重複印
                g.Paragraph(h.Preview);
            }
        }
        // 檔案不存在 ＝ 索引比磁碟舊（檔被改名／刪了）：要說出來，否則會被當成「定位鈕壞了」。
        if (m_Hits.Any(h => !File.Exists(h.File.Replace('/', Path.DirectorySeparatorChar))))
            g.Note("[注意] 有命中的檔案已經不在磁碟上 ⇒ 索引比磁碟舊，重建該 target。");
    }

    void DrawEval(SCP_Ui g, Dictionary<string, string> iBase)
    {
        using (var aFold = g.Fold("評估題庫（recall@5／MRR）", "kb/eval", false))
        {
            if (!aFold.Open) return;
            g.Note("拿 SenateData/config/kb_eval.json 的題目逐題檢索，算 recall@5 與 MRR@10。本專案答不出來的題會跳過、另外列出。"
                 + "評估前會先把用到的 target 重建到最新，所以可能要等好幾分鐘。這是 TASK-0382 比較排序的量尺。"
                 + "選 compare ＝ dense／hybrid／rerank 各跑一遍（各自帶與不帶衰減），一張表比完，另列已知難題 core-05 逐排序的名次。");
            List<string> aEvalModes = ModeChoices(true);
            string aMode = g.Dropdown("評估的排序方式", aEvalModes, aEvalModes[0], EvalModeId);
            if (!Busy && g.Button("跑評估", "kb/btn/eval"))
            {
                var a = new Dictionary<string, string>(iBase) { ["op"] = "eval", ["mode"] = aMode };
                if (aMode != "dense" && aMode != "compare") a["sparse_weight"] = g.FieldValue(SparseId, "0.3");
                if (g.ToggleValue(DecayId)) a["decay"] = "1";
                Start("eval", $"評估（{aMode}）", a);
            }
            if (m_EvalLines != null) foreach (string l in m_EvalLines) g.Label(l);
        }
    }
}
