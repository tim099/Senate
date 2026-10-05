// 區塊職責：**AI 模型（ollama）後台頁**（TASK-0383）—— ollama 本體與服務狀態、模型目錄×已安裝、顯存裡的模型與卸載、下載／移除、試跑與紀錄；
//           參考 Unity `UCL_LLMModelAdminPage`（之後廢棄，Tim 2026-10-02）。
// 物理意義：本頁**不自己算任何東西**：全部走 `senate cmd llm`（Cmd_Llm），跟 agent 在終端機打的是同一套實作。
//           ⭐ ollama 與它的模型**不進安裝系統**（Tim 2026-10-05）：狀態住在 ollama 服務裡，本頁只是讀它、叫它做事。
// 數值影響：開頁量一次狀態（`ollama list`／`ps`／nvidia-smi，各一次）；下載／移除會動磁碟 ⇒ 先上膛攤計畫、按確認才動手；
//           全部跑背景 job，同一時間只有一個（試跑中另外允許「從顯存卸載」—— 那才是卡住時真正該按的）。
// ⚠ 三個「不得同形」：
//   · 找不到 ollama／服務打不到 ⇒ 已安裝欄畫「不知道」，⛔ 不畫成每顆都「未安裝」。
//   · 顯存門檻是保底值 ⇒ 明講不是量到的。
//   · 試跑被截斷（撞到生成上限）⇒ 畫成失敗並說原因，⛔ 不把半句話當回答。
// ⚠ ollama 本體不代裝（官方安裝是遠端 PowerShell 腳本）：只給下載頁與指令的複製鈕。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元，U+FFFF 以上畫成方框 —— TASK-0356）。
#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public sealed class LlmModelPage : SCP_GuiToolPage
{
    public const string PageKey = "llm";
    const string FitOnlyId = "llm/toggle/fit-only";
    const string BasisId = "llm/sel/basis";
    const string ManualId = "llm/vram-manual";
    const string TestModelId = "llm/sel/test-model";
    const string PromptId = "llm/test/prompt";
    const string SystemId = "llm/test/system";
    const string ThinkId = "llm/test/think";
    const string NumPredictId = "llm/test/num-predict";
    const string KeepAliveId = "llm/test/keep-alive";
    const string TimeoutId = "llm/test/timeout";
    const int HistoryCount = 5;

    /// <summary>
    /// 頁面設定值（顯存門檻與試跑參數）。**按頂欄「存檔設定」才寫檔，⛔ 不自動存**（Tim 2026-10-05）。
    /// 存在 `SenateData/config/llm_page.json`（本機設定，不入版控 —— 同 Unity 頁存在 EditorPrefs 的性質）。
    /// </summary>
    internal sealed record Settings(string Basis, string ManualGb, bool FitOnly, string TestModel, string Prompt, string System,
                                    bool Think, string NumPredict, string KeepAlive, string Timeout);

    /// <summary>
    /// 初始值（沒存過設定檔時用；Tim 2026-10-05 指定）：酒保情境的一句招呼＋人設，開思考段、上限給足 ——
    /// 小模型在 thinking 段就吃掉上百 token，120 常常還沒想完就被截斷。⚠ 只是頁面的初值；CLI `op=test` 的預設照舊。
    /// </summary>
    internal static readonly Settings Defaults = new("free", "", true, "qwen3:0.6b", "跟剛進門的客人打個招呼", "你是傲嬌的貓娘", true, "4096", "120", "60");

    public const string SettingsFileName = "llm_page.json";

    internal sealed record Model(string Id, string Size, string Processor);
    internal sealed record CatalogRow(string Id, string Params, double SizeGb, double VramGb, int Zh, bool Recommend, string Note, bool Installed, bool Exact, bool Fits);
    internal sealed record Vram(bool GpuOk, string GpuName, double TotalGb, double FreeGb, double UsedGb, string Error, double BudgetGb, string Source);
    internal sealed record StatusView(bool Found, string Path, bool OnPath, string Version, bool Serving, List<Model>? Installed, List<Model>? Loaded,
                                      List<CatalogRow> Catalog, List<Model> Extra, Vram Vram, string Error, string Hint, string DownloadUrl, string InstallCommand);
    internal sealed record TestView(bool Ok, bool Truncated, string Model, double Seconds, int EvalCount, double Tps, string Output, string Thinking, string Note, string Error);
    sealed record Done(string Kind, string Label, SCP_CmdResult R);

    readonly SenateModel m_Model;
    Settings m_Saved = Defaults;    // 設定檔裡的值（＝欄位的初值；比對它判斷「有沒有未存的修改」）
    string m_SettingsSource = "";   // 這份設定從哪來：設定檔／初始值（沒存過）／初始值（設定檔讀不了）
    StatusView? m_Status;
    string? m_StatusError;
    bool m_StatusStale = true;
    Task<Done>? m_Job;
    string m_JobLabel = "";
    Task<Done>? m_SideJob;          // 試跑中的「從顯存卸載」—— 不能排在試跑後面（那就失去意義了）
    string? m_Message;
    (string Kind, string Model, List<string> Plan)? m_Armed;
    TestView? m_Test;
    string? m_TestError;
    string m_TestingModel = "";     // 試跑開始時的那一顆 —— 中斷／卸載要對它，⛔ 不是下拉選單「現在」顯示的那一顆
    (long Len, DateTime Mtime, List<string>? Lines)? m_HistoryCache;   // 紀錄檔沒變就不重讀（摺疊打開時每一幀都會畫）

    public LlmModelPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "AI 模型";
    public override string? MenuGroup => "工具";

    public override void OnPush()
    {
        base.OnPush();
        m_StatusStale = true;
        m_Armed = null;
        m_Saved = LoadSettings(SettingsPath, out m_SettingsSource);
    }

    string SettingsPath => Path.Combine(SenatePaths.ConfigDir(m_Model.RepoRoot), SettingsFileName);

    /// <summary>
    /// 讀設定檔。沒有檔 ⇒ 初始值；讀不了 ⇒ 初始值，但 oSource 明講是「讀不了」（⛔ 不跟「沒存過」同形 ——
    /// 前者按存檔會蓋掉一份壞檔，使用者要知道）。缺的欄位逐格補初始值（舊檔少一格不必整份作廢）。
    /// </summary>
    internal static Settings LoadSettings(string iPath, out string oSource)
    {
        if (!File.Exists(iPath)) { oSource = "初始值（還沒存過設定）"; return Defaults; }
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(iPath));
            JsonElement r = d.RootElement;
            string Str(string k, string iDef) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? iDef : iDef;
            bool Bool(string k, bool iDef) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : iDef;
            Settings z = Defaults;
            oSource = "設定檔 " + iPath;
            return new Settings(Str("vram_basis", z.Basis), Str("vram_manual_gb", z.ManualGb), Bool("fit_only", z.FitOnly), Str("test_model", z.TestModel),
                                Str("prompt", z.Prompt), Str("system", z.System), Bool("think", z.Think), Str("num_predict", z.NumPredict),
                                Str("keep_alive", z.KeepAlive), Str("timeout", z.Timeout));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            oSource = $"初始值（設定檔讀不了：{e.Message} —— 按存檔會覆蓋它）";
            return Defaults;
        }
    }

    internal static string SettingsJson(Settings s) => JsonSerializer.Serialize(new
    {
        vram_basis = s.Basis, vram_manual_gb = s.ManualGb, fit_only = s.FitOnly, test_model = s.TestModel,
        prompt = s.Prompt, system = s.System, think = s.Think, num_predict = s.NumPredict, keep_alive = s.KeepAlive, timeout = s.Timeout,
    }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>畫面上現在的值（沒碰過的欄位＝設定檔的值）。收合的區塊不建節點，所以一律用 FieldValue／ToggleValue 讀，不靠元件回傳。</summary>
    internal static Settings Current(SCP_Ui g, Settings iSaved) => new(
        g.FieldValue(BasisId + "/value", iSaved.Basis), g.FieldValue(ManualId, iSaved.ManualGb), g.ToggleValue(FitOnlyId, iSaved.FitOnly),
        g.FieldValue(TestModelId + "/value", iSaved.TestModel), g.FieldValue(PromptId, iSaved.Prompt), g.FieldValue(SystemId, iSaved.System),
        g.ToggleValue(ThinkId, iSaved.Think), g.FieldValue(NumPredictId, iSaved.NumPredict), g.FieldValue(KeepAliveId, iSaved.KeepAlive),
        g.FieldValue(TimeoutId, iSaved.Timeout));

    void SaveSettings(Settings iNow)
    {
        try
        {
            SCP.Core.Letters.SCP_CmdPayload.WriteAtomic(SettingsPath, SettingsJson(iNow) + "\n");
            m_Saved = LoadSettings(SettingsPath, out m_SettingsSource);   // 讀回來才算數：畫面比對的是磁碟上的那份
            m_Message = m_Saved == iNow ? "設定已存檔：" + SettingsPath : "[注意] 存檔後讀回來的值跟畫面不一樣 —— " + m_SettingsSource;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            m_Message = "[注意] 設定沒存成：" + e.Message;
        }
    }

    // ── job ─────────────────────────────────────────────────────

    string DataRoot => m_Model.AgentCommandsRoot.Value;

    void Start(string iKind, string iLabel, Dictionary<string, string> iArgs, bool iSide = false)
    {
        if (!iSide && m_Job != null) { m_Message = $"前一筆（{m_JobLabel}）還沒完 ⇒ 這次沒有動作"; return; }
        if (iSide && m_SideJob != null) { m_Message = "卸載還在跑 ⇒ 這次沒有動作"; return; }
        if (!iSide) m_Message = null;
        if (iKind == "test" && DataRoot.Length > 0) iArgs["data_root"] = DataRoot;
        Func<Done> aWork = () => new Done(iKind, iLabel, SCP_CmdRegistry.Dispatch("llm", iArgs));
        if (SCP_GuiHost.RedrawsContinuously)
        {
            if (iSide) m_SideJob = Task.Run(aWork);
            else { m_JobLabel = iLabel; m_Job = Task.Run(aWork); }
            return;
        }
        // 不會重畫的宿主（CLI 單次 render）：背景跑等於把答案丟掉 —— 這裡同步。
        try { Harvest(aWork()); }
        catch (Exception e) { m_Message = "那一步炸了：" + e.GetType().Name + ": " + e.Message; }
    }

    void PumpJobs()
    {
        foreach (bool aSide in new[] { true, false })
        {
            Task<Done>? j = aSide ? m_SideJob : m_Job;
            if (j == null || !j.IsCompleted) continue;
            if (aSide) m_SideJob = null; else m_Job = null;
            try { Harvest(j.Result); }
            catch (Exception e) { m_Message = "那一步炸了：" + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
        }
    }

    static string Joined(SCP_CmdResult r) => string.Join("\n", r.Lines);

    void Harvest(Done d)
    {
        SCP_CmdResult r = d.R;
        switch (d.Kind)
        {
            case "status":
                m_StatusStale = false;
                if (r.ExitCode != 0) { m_Status = null; m_StatusError = $"狀態量不到（exit {r.ExitCode}）：" + Joined(r); return; }
                m_Status = ParseStatus(Joined(r), out m_StatusError);
                return;
            case "test":
                m_Test = ParseTest(Joined(r), out m_TestError);
                if (m_Test == null && m_TestError == null) m_TestError = $"試跑沒跑成（exit {r.ExitCode}）：" + Joined(r);
                m_StatusStale = true;   // 試跑會把模型載進顯存 ⇒ 「載入顯存中」要重量
                return;
        }
        string aHead = r.Lines.LastOrDefault(l => l.StartsWith("✓", StringComparison.Ordinal) || l.StartsWith("✗", StringComparison.Ordinal)) ?? "";
        m_Message = $"{d.Label}：{(r.ExitCode == 0 ? (aHead.Length > 0 ? aHead : "完成") : $"exit {r.ExitCode} —— " + Joined(r))}";
        m_StatusStale = true;       // 動完一律重新量 —— 畫面要是「動完之後量到的」
    }

    // ── 解析（selftest 也呼叫）────────────────────────────────────

    static string? JsonOf(string iText)
    {
        int s = iText.IndexOf('{'), e = iText.LastIndexOf('}');
        return s >= 0 && e > s ? iText.Substring(s, e - s + 1) : null;
    }

    static string S(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    static bool B(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.True;
    static double D(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement x) && x.TryGetDouble(out double v) ? v : 0;

    /// <summary>null 陣列 ＝ 量不到（回 null）；空陣列 ＝ 量了、0 個（回空清單）—— 兩者畫法不同。</summary>
    static List<Model>? Models(JsonElement root, string k)
    {
        if (!root.TryGetProperty(k, out JsonElement a) || a.ValueKind != JsonValueKind.Array) return null;
        return a.EnumerateArray().Select(m => new Model(S(m, "id"), S(m, "size"), S(m, "processor"))).ToList();
    }

    internal static StatusView? ParseStatus(string iText, out string? oError)
    {
        oError = null;
        string? aJson = JsonOf(iText);
        if (aJson == null) { oError = "狀態輸出不是 JSON：" + iText; return null; }
        try
        {
            using JsonDocument doc = JsonDocument.Parse(aJson);
            JsonElement root = doc.RootElement, v = root.GetProperty("vram");
            var aCat = root.GetProperty("catalog").EnumerateArray().Select(c => new CatalogRow(S(c, "id"), S(c, "params"), D(c, "size_gb"), D(c, "vram_gb"),
                (int)D(c, "zh"), B(c, "recommend"), S(c, "note"), B(c, "installed"), B(c, "exact"), B(c, "fits_budget"))).ToList();
            return new StatusView(B(root, "ollama_found"), S(root, "ollama_path"), B(root, "on_path"), S(root, "version"), B(root, "service_reachable"),
                B(root, "installed_known") ? Models(root, "installed") : null, B(root, "loaded_known") ? Models(root, "loaded") : null,
                aCat, Models(root, "not_in_catalog") ?? new List<Model>(),
                new Vram(B(v, "gpu_ok"), S(v, "gpu_name"), D(v, "total_gb"), D(v, "free_gb"), D(v, "used_gb"), S(v, "error"), D(v, "budget_gb"), S(v, "source")),
                S(root, "error"), S(root, "hint"), S(root, "download_url"), S(root, "install_command"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            oError = "狀態 JSON 解析失敗：" + e.Message;
            return null;
        }
    }

    internal static TestView? ParseTest(string iText, out string? oError)
    {
        oError = null;
        string? aJson = JsonOf(iText);
        if (aJson == null) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(aJson);
            JsonElement t = doc.RootElement;
            return new TestView(B(t, "ok"), B(t, "truncated"), S(t, "model"), D(t, "seconds"), (int)D(t, "eval_count"), D(t, "tokens_per_sec"),
                S(t, "output"), S(t, "thinking"), S(t, "note"), (S(t, "error") + (S(t, "log_error").Length > 0 ? "　（試跑紀錄沒寫進去：" + S(t, "log_error") + "）" : "")).Trim());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { oError = "試跑 JSON 解析失敗：" + e.Message; return null; }
    }

    // ── 畫面 ────────────────────────────────────────────────────

    bool Busy => m_Job != null;

    /// <summary>
    /// 量狀態要帶的參數。⚠ 下拉選單的值存在 `&lt;key&gt;/value`，不是 key 本身 ——
    /// 讀錯那一格的話選「總量」會被安靜吃掉（永遠送 free）。selftest 有一格對拍。
    /// </summary>
    internal static Dictionary<string, string> StatusArgs(SCP_Ui g, Settings iSaved)
    {
        Settings c = Current(g, iSaved);
        var a = new Dictionary<string, string> { ["op"] = "status", ["format"] = "json", ["vram_basis"] = c.Basis };
        string aManual = c.ManualGb.Trim();
        if (aManual.Length > 0) a["vram_budget"] = aManual;
        return a;
    }

    protected override void TopBarButtons(SCP_Ui g)
    {
        if (!Busy && g.Button("重新量狀態", "llm/btn/reload")) m_StatusStale = true;
        // 設定只在按這顆時寫檔（Tim 2026-10-05：要存檔鈕，⛔ 不自動存）
        Settings aNow = Current(g, m_Saved);
        bool aDirty = aNow != m_Saved;
        if (g.Button("存檔設定", "llm/btn/save-settings")) SaveSettings(aNow);
        g.Label(aDirty ? "（設定有未存的修改）" : "（設定與存檔相同）");
        // 酒保用的就是這裡的模型（TASK-0365）—— 人設、罐頭句、開關在酒保頁
        if (g.Button("酒保設定", "llm/btn/to-bartender")) Controller?.Push(new BartenderPage(m_Model));
    }

    protected override void DrawContent(SCP_Ui g)
    {
        PumpJobs();
        if (m_StatusStale && !Busy) Start("status", "量狀態", StatusArgs(g, m_Saved));

        g.Title("AI 模型（ollama）");
        g.Note("本地大語言模型（目前給酒保用）。本頁走 `senate cmd llm`（同一套實作）；模型由 ollama 持有，不走安裝管理頁。");
        g.Note("頁面設定（顯存門檻、試跑參數）來源：" + m_SettingsSource + "　—— 改了要按頂欄「存檔設定」才會留著，不會自動存。");
        if (Busy)
        {
            g.Note($"執行中：{m_JobLabel}（下載可能要好幾分鐘；完成後自動更新）");
            // 下載與試跑可以中斷（殺掉 ollama pull／關掉試跑的連線）；量狀態、移除很快，不給。
            if ((m_JobLabel.StartsWith("下載", StringComparison.Ordinal) || m_JobLabel.StartsWith("試跑", StringComparison.Ordinal))
                && g.Button("中斷", "llm/btn/cancel"))
            {
                LlmOllama.CancelRunning();
                m_Message = $"已要求中斷：{m_JobLabel}";
            }
        }
        if (m_Message != null) g.Note(m_Message);
        if (m_Armed != null && !Busy) DrawConfirm(g);     // 忙的時候不畫：按了確認也只會被拒，而上膛就丟了

        DrawRuntime(g);
        if (m_Status == null) return;
        DrawVram(g);
        DrawLoaded(g);
        DrawCatalog(g);
        DrawTest(g);
        DrawHistory(g);
    }

    void DrawRuntime(SCP_Ui g)
    {
        using (g.Box("ollama 本體與服務", "llm/runtime"))
        {
            if (m_StatusError != null) { g.Note("[注意] " + m_StatusError); return; }
            StatusView? s = m_Status;
            if (s == null) { g.Note(Busy ? "量狀態中…" : "（還沒量）"); return; }
            g.Label("ollama：" + (s.Found ? $"{s.Version}　{s.Path}{(s.OnPath ? "" : "（不在 PATH 上）")}" : "找不到"));
            g.Label("服務：" + (!s.Found ? "—" : s.Serving ? "可連線" : "打不到"));
            g.Label("已安裝：" + (s.Installed == null ? "不知道（量不到，不是 0 個）" : $"{s.Installed.Count} 個"));
            if (s.Hint.Length > 0) g.Note("[注意] " + s.Hint);
            if (s.Error.Length > 0) g.Note(s.Error);
            if (!s.Found)
            {
                g.Note("本頁不代裝 ollama（官方安裝是下載並執行遠端腳本）。到下載頁安裝，或在 PowerShell 跑下面這行；裝完重開 Senate 讓 PATH 生效。");
                g.Label(s.InstallCommand);
                if (SCP_GuiHost.CopyToClipboard != null)
                    using (g.Row())
                    {
                        if (g.Button("複製下載頁網址", "llm/btn/copy-url")) m_Message = SCP_GuiHost.CopyToClipboard(s.DownloadUrl);
                        if (g.Button("複製安裝指令", "llm/btn/copy-cmd")) m_Message = SCP_GuiHost.CopyToClipboard(s.InstallCommand);
                    }
                else g.Label(s.DownloadUrl);
            }
        }
    }

    void DrawVram(SCP_Ui g)
    {
        Vram v = m_Status!.Vram;
        using (var aFold = g.Fold($"顯存門檻：{v.BudgetGb.ToString("0.##", CultureInfo.InvariantCulture)} GB（{LlmOllama.VramSourceText(v.Source)}）", "llm/vram", false))
        {
            if (!aFold.Open) return;
            if (v.GpuOk) g.Label($"{v.GpuName}　總量 {v.TotalGb} GB／已用 {v.UsedGb} GB／可用 {v.FreeGb} GB");
            if (v.Error.Length > 0) g.Note("[注意] 顯存偵測：" + v.Error);
            if (v.Source == "fallback") g.Note("[注意] 這個門檻是保底值，不是量到的 —— 請在下面手動填寫。");
            g.Dropdown("自動偵測時拿哪個數字當門檻", new List<SCP_GuiOption> { new("free", "可用量（扣掉其他程式已佔的）"), new("total", "總量（這張卡買得起哪顆）") }, m_Saved.Basis, BasisId);
            g.TextField("手動門檻（GB；空白＝自動偵測）", m_Saved.ManualGb, ManualId);
            g.Note("門檻只決定目錄預設列不列這顆，不影響能不能下載、也不影響實際載入。顯存不夠時 ollama 不報錯，只會把層數丟給 CPU（慢一個數量級）。");
            if (!Busy && g.Button("套用並重新量", "llm/btn/vram-apply")) m_StatusStale = true;
        }
    }

    void DrawLoaded(SCP_Ui g)
    {
        StatusView s = m_Status!;
        using (g.Box("載入顯存中（ollama ps）", "llm/loaded"))
        {
            if (s.Loaded == null) { g.Note(s.Serving ? "量不到顯存裡的模型。" : "服務打不到 ⇒ 不知道。"); return; }
            if (s.Loaded.Count == 0) { g.Label("沒有模型佔著顯存。"); return; }
            using (g.Table("動作", "模型", "大小", "跑在哪"))
                foreach (Model m in s.Loaded)
                    using (g.TableRowScope())
                    {
                        if (m_SideJob != null) g.TableCell("卸載中…");
                        else if (g.Button("從顯存卸載", $"llm/stop/{m.Id}")) Start("stop", "卸載 " + m.Id, new Dictionary<string, string> { ["op"] = "stop", ["model"] = m.Id }, iSide: true);
                        g.TableCell(m.Id);
                        g.TableCell(m.Size);
                        g.TableCell(m.Processor);
                    }
            g.Note("「跑在哪」不是 100% GPU ⇒ 顯存不夠、有層數在 CPU 上（會很慢）。卡住時按卸載：停掉發問的那一方不會讓模型離開顯存。");
        }
    }

    void DrawCatalog(SCP_Ui g)
    {
        StatusView s = m_Status!;
        using (g.Box("模型目錄", "llm/catalog"))
        {
            bool aFitOnly = g.Toggle($"只列放得下的（顯存約 ≤ {s.Vram.BudgetGb.ToString("0.##", CultureInfo.InvariantCulture)} GB）", m_Saved.FitOnly, FitOnlyId);
            bool aKnown = s.Installed != null;
            var aRows = s.Catalog.Where(c => !aFitOnly || c.Fits || c.Installed).ToList();
            using (g.Table("動作", "模型", "參數", "下載", "顯存約", "中文", "狀態", "說明"))
                foreach (CatalogRow c in aRows)
                    using (g.TableRowScope())
                    {
                        if (Busy || m_Armed != null || !aKnown) g.TableCell("—");
                        else if (c.Exact) { if (g.Button("移除", $"llm/rm/{c.Id}")) ArmUninstall(c.Id); }
                        else if (g.Button(c.Installed ? "下載原版" : "下載", $"llm/pull/{c.Id}")) ArmInstall(c);
                        g.TableCell((c.Recommend ? "* " : "") + c.Id);
                        g.TableCell(c.Params);
                        g.TableCell($"{c.SizeGb} GB");
                        g.TableCell($"{c.VramGb} GB" + (c.Fits ? "" : "（超過門檻）"));
                        g.TableCell($"{c.Zh}/5");
                        g.TableCell(!aKnown ? "不知道" : c.Exact ? "已安裝" : c.Installed ? "裝的是變體" : "未安裝");
                        g.TableCell(c.Note);
                    }
            int aHidden = s.Catalog.Count - aRows.Count;
            g.Note("* ＝ 純聊天推薦。「下載」量是磁碟佔用，「顯存約」才是跑起來要的（含 KV cache）—— 兩個數字不要混用。"
                   + (aHidden > 0 ? $"　另有 {aHidden} 顆超過門檻沒列（取消勾選就看得到）。" : ""));
            if (!aKnown) g.Note("[注意] 已安裝清單量不到 ⇒ 狀態欄一律是「不知道」，下載／移除鈕先收起來。");
            if (s.Extra.Count > 0)
            {
                g.Label("目錄外（自己 pull 的）：");
                foreach (Model m in s.Extra)
                    using (g.Row())
                    {
                        g.Label($"{m.Id}　{m.Size}");
                        if (!Busy && m_Armed == null && g.Button("移除", $"llm/rm-extra/{m.Id}")) ArmUninstall(m.Id);
                    }
            }
        }
    }

    void ArmInstall(CatalogRow c)
        => m_Armed = ("install", c.Id, new List<string>
        {
            $"用 ollama pull 下載 {c.Id}：下載量約 {c.SizeGb} GB，跑起來顯存約 {c.VramGb} GB" + (c.Fits ? "" : "（超過目前門檻，會有層數跑在 CPU 上）"),
            "放在 ollama 自己的模型目錄（不是安裝管理頁的 ModelsRoot）。下載中可以換頁，回來會看到結果。",
        });

    void ArmUninstall(string iId)
        => m_Armed = ("uninstall", iId, new List<string>
        {
            $"用 ollama rm 移除 {iId}：權重會從磁碟刪掉，要用時得重新下載。",
            "酒保若設定成用這顆，移除後它會退回罐頭句。",
        });

    void DrawConfirm(SCP_Ui g)
    {
        var (aKind, aId, aPlan) = m_Armed!.Value;
        using (g.Box(aKind == "install" ? $"確認下載 {aId}" : $"確認移除 {aId}", "llm/confirm"))
        {
            foreach (string l in aPlan) g.Note(l);
            bool aGo = false, aCancel = false;
            using (g.Row())
            {
                if (g.Button(aKind == "install" ? "確認下載" : "確認移除", "llm/confirm/yes")) aGo = true;
                if (g.Button("取消", "llm/confirm/no")) aCancel = true;
            }
            if (aCancel) { m_Armed = null; m_Message = "已取消（沒有動任何東西）"; return; }
            if (!aGo) return;
        }
        m_Armed = null;
        Start(aKind, (aKind == "install" ? "下載 " : "移除 ") + aId,
              new Dictionary<string, string> { ["op"] = aKind, ["model"] = aId, ["confirm"] = "1" });
    }

    void DrawTest(SCP_Ui g)
    {
        StatusView s = m_Status!;
        using (g.Box("試跑", "llm/test"))
        {
            if (s.Installed == null || s.Installed.Count == 0) { g.Note(s.Installed == null ? "已安裝清單量不到 ⇒ 沒辦法挑模型。" : "還沒有安裝任何模型 ⇒ 先從上面的目錄下載一顆。"); return; }
            var aOptions = s.Installed.Select(m => new SCP_GuiOption(m.Id)).ToList();
            // 選過的那顆被移除了 ⇒ 換回清單第一顆（不然它會一直送一個不存在的模型，試跑回 404）
            string aPicked = g.FieldValue(TestModelId + "/value", "");
            // 預設那顆沒裝 ⇒ 退回清單第一顆（⛔ 不送一個不存在的模型）
            string aDefault = s.Installed.Any(m => m.Id == m_Saved.TestModel) ? m_Saved.TestModel : s.Installed[0].Id;
            if (aPicked.Length > 0 && !s.Installed.Any(m => m.Id == aPicked)) g.SetField(TestModelId + "/value", aDefault);
            string aModel = g.Dropdown("模型", aOptions, aDefault, TestModelId);
            string aPrompt = g.TextField("問一句", m_Saved.Prompt, PromptId);
            string aSystem = g.TextArea("system prompt（例如酒保人設；空白＝不帶）", m_Saved.System, SystemId, 4);
            bool aThink = g.Toggle("把思考段一起要回來（診斷 thinking 模型）", m_Saved.Think, ThinkId);
            string aNum = g.TextField("生成上限（token）", m_Saved.NumPredict, NumPredictId);
            string aKeep = g.TextField("用完幾秒後卸載（-1＝ollama 預設 5 分鐘）", m_Saved.KeepAlive, KeepAliveId);
            string aTimeout = g.TextField("等待上限（秒）", m_Saved.Timeout, TimeoutId);
            g.Note("第一次會把模型載進顯存（冷啟動可能幾十秒）。逾時不代表它死了 —— thinking 模型可能還在想；看上面「載入顯存中」，必要時卸載。");
            if (!Busy && g.Button("試跑", "llm/btn/test"))
            {
                m_Test = null; m_TestError = null; m_TestingModel = aModel;
                Start("test", "試跑 " + aModel, new Dictionary<string, string>
                {
                    ["op"] = "test", ["format"] = "json", ["model"] = aModel, ["prompt"] = aPrompt, ["system"] = aSystem,
                    ["think"] = aThink ? "1" : "0", ["num_predict"] = aNum.Trim(), ["keep_alive"] = aKeep.Trim(), ["timeout"] = aTimeout.Trim(),
                });
            }
            if (Busy && m_JobLabel.StartsWith("試跑", StringComparison.Ordinal) && m_SideJob == null && g.Button($"從顯存卸載 {m_TestingModel}", "llm/btn/test-stop"))
            {
                LlmOllama.CancelRunning();      // 先關連線（ollama 會等進行中的請求跑完才卸載）
                Start("stop", "卸載 " + m_TestingModel, new Dictionary<string, string> { ["op"] = "stop", ["model"] = m_TestingModel }, iSide: true);
            }
            DrawTestResult(g);
        }
    }

    void DrawTestResult(SCP_Ui g)
    {
        if (m_TestError != null) { g.Note("[注意] " + m_TestError); return; }
        if (m_Test == null) return;
        TestView t = m_Test;
        g.Label($"{(t.Ok ? "成功" : t.Truncated ? "被截斷" : "失敗")}　{t.Model}　{t.Seconds} 秒　{t.Tps} tok/s　{t.EvalCount} token");
        if (t.Thinking.Length > 0)
            using (var f = g.Fold("思考過程", "llm/test/thinking", false))
                if (f.Open) g.Paragraph(t.Thinking);
        if (t.Output.Length > 0) g.Paragraph(t.Output);
        else if (t.Error.Length == 0) g.Note("（回答是空的）");
        if (t.Note.Length > 0) g.Note("[注意] " + t.Note);
        if (t.Error.Length > 0) g.Note("[注意] " + t.Error);
    }

    List<string>? CachedHistory()
    {
        string aPath = Path.Combine(DataRoot, Cmd_Llm.TestLogRelative.Replace('/', Path.DirectorySeparatorChar));
        var fi = new FileInfo(aPath);
        long aLen = fi.Exists ? fi.Length : -1;
        DateTime aMtime = fi.Exists ? fi.LastWriteTimeUtc : DateTime.MinValue;
        if (m_HistoryCache is { } c && c.Len == aLen && c.Mtime == aMtime) return c.Lines;
        List<string>? aLines = Cmd_Llm.TailTestLog(DataRoot, HistoryCount);
        m_HistoryCache = (aLen, aMtime, aLines);
        return aLines;
    }

    void DrawHistory(SCP_Ui g)
    {
        using (var aFold = g.Fold($"試跑紀錄（最近 {HistoryCount} 筆；{Cmd_Llm.TestLogRelative}）", "llm/history", false))
        {
            if (!aFold.Open) return;
            if (DataRoot.Length == 0) { g.Note("資料根沒設定 ⇒ 不知道紀錄在哪（到「路徑管理」頁設定）。"); return; }
            List<string>? aLines = CachedHistory();
            if (aLines == null) { g.Note("[注意] 紀錄檔讀不了。"); return; }
            if (aLines.Count == 0) { g.Note("還沒有試跑紀錄。"); return; }
            for (int i = 0; i < aLines.Count; i++)
            {
                TestView? t = ParseTest(aLines[i], out _);
                string aTs = "";
                try { using JsonDocument d = JsonDocument.Parse(aLines[i]); aTs = S(d.RootElement, "ts"); } catch (JsonException) { }
                if (t == null) { g.Note($"#{i + 1}　（這一行讀不了）"); continue; }
                using (g.Box($"#{i + 1}　{aTs}　{t.Model}　{(t.Ok ? "成功" : "失敗")}　{t.Seconds} 秒", $"llm/history/{i}"))
                {
                    if (t.Output.Length > 0) g.Paragraph(t.Output);
                    if (t.Note.Length > 0) g.Note(t.Note);
                    if (t.Error.Length > 0) g.Note(t.Error);
                }
            }
            string aPath = Path.Combine(DataRoot, Cmd_Llm.TestLogRelative.Replace('/', Path.DirectorySeparatorChar));
            if (SCP_GuiHost.RevealInFileManager != null && File.Exists(aPath) && g.Button("開啟紀錄檔位置", "llm/btn/reveal-log"))
                m_Message = SCP_GuiHost.RevealInFileManager(aPath);
        }
    }
}
