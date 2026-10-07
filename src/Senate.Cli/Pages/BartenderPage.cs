// 區塊職責：**酒保後台頁**（TASK-0365）—— 開關、回應設定（模型／思考段／上限／冷卻）、人設、罐頭句、辨認的名稱（別名）、試回一句。
//           取代 Unity `UCL_BartenderAdminPage` 的「常駐酒保」與「回應來源」兩塊（遠端喚醒、酒館 CLI、派工單不搬；定時提醒在 TASK-0393）。
// 物理意義：真正回應的是酒館 Server 裡的 SenateBartenderJob（每一輪重讀設定檔）⇒ 本頁存檔後**不必重啟 Server**。
//           狀態與試回走 `senate cmd bartender`；存檔直接走 SenateBartender／SCP_TavernMentionAliases 的 TrySave（檢查 → 寫 → 讀回）。
// 數值影響：**按頂欄「存檔設定」才寫檔，⛔ 不自動存**（Tim 2026-10-05，跟 AI 模型頁同一個規矩）。
//           別名表只改指向 tavern-keeper 的那幾條；別人的別名原樣保留。
// ⚠ 別名存檔前擋：跟本名或別人的別名撞名、空白、含 @ —— 這幾種錯都不會當場叫，只會讓 @ 安靜地送錯人（驗收⑦）。
// ⚠ 視窗文字不放 emoji（ImWchar 16 位元，U+FFFF 以上畫成方框 —— TASK-0356）。
#nullable enable
using System.Globalization;
using System.Text.Json;
using System.Threading.Tasks;
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Gui;
using SCP.Core.Tavern;

namespace Senate.Cli.Pages;

public sealed class BartenderPage : SCP_GuiToolPage
{
    public const string PageKey = "bartender";
    const string NoModel = "(不用模型，只回罐頭句)";

    sealed record Done(string Kind, SCP_CmdResult R);
    internal sealed record StatusView(bool Ok, string SettingsError, string StateError, string AliasError, bool SettingsSaved, bool Enabled,
                                      int RepliedToday, int LastReplySeq, int LastTriggerSeq, string LastReplyAt, string LastReplySource, string LastError, string LastErrorAt);

    readonly SenateModel m_Model;
    int m_Gen;                                  // 欄位 id 的世代：重新讀取時換一代，舊的未存修改一併丟掉
    BartenderSettings m_Saved = BartenderSettings.Defaults;
    string? m_SettingsError;
    List<string> m_SavedAliases = new();
    string? m_AliasError;
    StatusView? m_Status;
    string? m_StatusError;
    List<string>? m_Models;                     // null ＝ 還沒量／量不到（下拉只放目前那一顆）
    List<LlmModelPage.Model>? m_LoadedModels;    // 最近一次 Ollama 狀態；不把未載入當成全域預設容量
    string? m_ModelStatusError;
    bool m_ModelsTried;
    (string Key, List<string> Problems)? m_AliasCheck;   // 別名檢查的快取：畫面上的文字沒變就不重讀檔、不重掃白名單
    Task<Done>? m_Job;
    string m_JobLabel = "";
    string? m_Message;
    string? m_Preview;
    bool m_Stale = true;

    public BartenderPage(SenateModel iModel) : base() { m_Model = iModel; }

    public override string Key => PageKey;
    public override string Title => "酒保";
    public override string? MenuGroup => "酒館";

    public override void OnPush()
    {
        base.OnPush();
        Reload();
    }

    string DataRoot => m_Model.AgentCommandsRoot.Value;

    void Reload()
    {
        m_Gen++;
        m_Message = null;
        m_Preview = null;
        m_Stale = true;
        m_ModelsTried = false;
        m_LoadedModels = null;
        m_ModelStatusError = null;
        if (DataRoot.Length == 0 || !Directory.Exists(DataRoot)) return;
        m_Saved = SenateBartender.LoadSettings(DataRoot, out m_SettingsError);
        m_SavedAliases = SCP_TavernMentionAliases.Load(DataRoot, out m_AliasError)
            .Where(kv => kv.Value == SenateBartender.PersonaId).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    string Id(string iKey) => $"bartender/{m_Gen}/{iKey}";

    // ── job ─────────────────────────────────────────────────────

    void Start(string iKind, string iLabel, string iCmd, Dictionary<string, string> iArgs)
    {
        if (m_Job != null) { m_Message = $"前一筆（{m_JobLabel}）還沒完 ⇒ 這次沒有動作"; return; }
        iArgs["data_root"] = DataRoot;
        Func<Done> aWork = () => new Done(iKind, SCP_CmdRegistry.Dispatch(iCmd, iArgs));
        if (SCP_GuiHost.RedrawsContinuously) { m_JobLabel = iLabel; m_Job = Task.Run(aWork); return; }
        try { Harvest(aWork()); }
        catch (Exception e) { m_Message = "那一步炸了：" + e.GetType().Name + ": " + e.Message; }
    }

    void PumpJob()
    {
        if (m_Job == null || !m_Job.IsCompleted) return;
        Task<Done> j = m_Job;
        m_Job = null;
        try { Harvest(j.Result); }
        catch (Exception e) { m_Message = "那一步炸了：" + (e.InnerException ?? e).GetType().Name + ": " + (e.InnerException ?? e).Message; }
    }

    static string? JsonOf(string iText)
    {
        int s = iText.IndexOf('{'), e = iText.LastIndexOf('}');
        return s >= 0 && e > s ? iText.Substring(s, e - s + 1) : null;
    }

    static string S(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
    static bool B(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.True;
    static int I(JsonElement e, string k) => e.TryGetProperty(k, out JsonElement x) && x.TryGetInt32(out int v) ? v : 0;

    internal static StatusView? ParseStatus(string iText, out string? oError)
    {
        oError = null;
        string? aJson = JsonOf(iText);
        if (aJson == null) { oError = "狀態輸出不是 JSON：" + iText; return null; }
        try
        {
            using JsonDocument d = JsonDocument.Parse(aJson);
            JsonElement r = d.RootElement;
            return new StatusView(B(r, "ok"), S(r, "settings_error"), S(r, "state_error"), S(r, "alias_error"), B(r, "settings_saved"), B(r, "enabled"),
                r.TryGetProperty("replied_today", out JsonElement rt) && rt.TryGetInt32(out int rv) ? rv : -1, I(r, "last_reply_seq"),
                I(r, "last_trigger_seq"), S(r, "last_reply_at"), S(r, "last_reply_source"), S(r, "last_error"), S(r, "last_error_at"));
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { oError = "狀態 JSON 解析失敗：" + e.Message; return null; }
    }

    void Harvest(Done d)
    {
        SCP_CmdResult r = d.R;
        switch (d.Kind)
        {
            case "status":
                m_Status = ParseStatus(string.Join("\n", r.Lines), out m_StatusError);
                return;
            case "models":
                LlmModelPage.StatusView? v = LlmModelPage.ParseStatus(string.Join("\n", r.Lines), out m_ModelStatusError);
                m_Models = v?.Installed?.Select(m => m.Id).ToList();
                m_LoadedModels = v?.Loaded;
                if (v != null && v.Loaded == null) m_ModelStatusError = v.Error.Length > 0 ? v.Error : "Ollama 載入狀態量不到";
                return;
            case "preview":
                m_ModelsTried = false;   // 試回會載入模型；下一筆重量 context，不能留試回前的讀數
                string? aJson = JsonOf(string.Join("\n", r.Lines));
                if (aJson == null) { m_Preview = $"試回沒跑成（exit {r.ExitCode}）：" + string.Join("\n", r.Lines); return; }
                using (JsonDocument doc = JsonDocument.Parse(aJson))
                {
                    JsonElement p = doc.RootElement;
                    string aErr = S(p, "error");
                    m_Preview = $"[{S(p, "source")}] {S(p, "reply")}" + (aErr.Length > 0 ? $"\n（模型沒回成 ⇒ 真的發文時會退回這句罐頭：{aErr}）" : "");
                }
                return;
        }
    }

    // ── 畫面上的值 ────────────────────────────────────────────────

    static List<string> Lines(string iText) => iText.Replace("\r\n", "\n").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

    static int ParseInt(string s, int iFallback) => int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : iFallback;

    /// <summary>畫面上現在的設定（沒碰過的欄位＝存檔值）。數字欄位讀不懂 ⇒ oBad 記下來，⛔ 不默默換成存檔值去存。</summary>
    BartenderSettings Current(SCP_Ui g, List<string> oBad)
    {
        BartenderSettings z = m_Saved;
        int Num(string iKey, string iLabel, int iSaved)
        {
            string aRaw = g.FieldValue(Id(iKey), iSaved.ToString(CultureInfo.InvariantCulture));
            if (int.TryParse(aRaw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
            oBad.Add($"{iLabel}「{aRaw}」不是整數");
            return iSaved;
        }
        string aModel = g.FieldValue(Id("model") + "/value", z.ModelId.Length == 0 ? NoModel : z.ModelId);
        return new BartenderSettings(
            g.ToggleValue(Id("enabled"), z.Enabled), g.FieldValue(Id("name"), z.DisplayName).Trim(), aModel == NoModel ? "" : aModel,
            g.ToggleValue(Id("think"), z.Think), Num("num", "生成上限", z.NumPredict), Num("keep", "卸載秒數", z.KeepAliveSeconds),
            Num("timeout", "等待上限", z.TimeoutSeconds), g.FieldValue(Id("persona"), z.PersonaPrompt),
            Lines(g.FieldValue(Id("canned"), string.Join("\n", z.CannedReplies))), Num("cooldown", "冷卻秒數", z.CooldownSeconds), Num("cap", "每日上限", z.DailyCap));
    }

    List<string> CurrentAliases(SCP_Ui g) => Lines(g.FieldValue(Id("aliases"), string.Join("\n", m_SavedAliases)));

    /// <summary>別名表整份（別人的照舊＋酒保的換成畫面上的）。</summary>
    Dictionary<string, string> MergedAliases(List<string> iMine)
    {
        Dictionary<string, string> aAll = SCP_TavernMentionAliases.Load(DataRoot, out _);
        var aOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> kv in aAll) if (kv.Value != SenateBartender.PersonaId) aOut[kv.Key] = kv.Value;
        foreach (string a in iMine) aOut[a] = SenateBartender.PersonaId;    // 撞名由 Validate 擋（同名覆蓋前先檢查）
        return aOut;
    }

    /// <summary>存檔前要擋的別名問題（含「同一個名字已經是別人的別名」—— 合併時會被覆蓋，所以要在這裡先看）。</summary>
    List<string> AliasProblems(List<string> iMine)
    {
        var aErrors = new List<string>();
        Dictionary<string, string> aAll = SCP_TavernMentionAliases.Load(DataRoot, out _);
        foreach (string a in iMine)
            if (aAll.TryGetValue(a, out string? aOwner) && aOwner != SenateBartender.PersonaId) aErrors.Add($"別名「{a}」已經是 {aOwner} 的別名 ⇒ `@{a}` 會變成兩個人");
        foreach (IGrouping<string, string> grp in iMine.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Where(x => x.Count() > 1)) aErrors.Add($"別名「{grp.Key}」重複");
        aErrors.AddRange(SCP_TavernMentionAliases.Validate(iMine.Distinct(StringComparer.OrdinalIgnoreCase).Select(a => new KeyValuePair<string, string>(a, SenateBartender.PersonaId)),
                                                           SCP_TavernMentions.Whitelist(DataRoot)));
        return aErrors;
    }

    void Save(BartenderSettings iNow, List<string> iMine)
    {
        List<string> aProblems = AliasProblems(iMine);
        aProblems.AddRange(SenateBartender.Validate(iNow));
        if (aProblems.Count > 0) { m_Message = "[未寫入] " + string.Join("；", aProblems); return; }
        bool aSettingsChanged = !iNow.SameAs(m_Saved) || m_SettingsError != null;
        bool aAliasesChanged = !iMine.SequenceEqual(m_SavedAliases);
        var aDone = new List<string>();
        if (aSettingsChanged)
        {
            if (!SenateBartender.TrySaveSettings(DataRoot, iNow, out List<string> e1)) { m_Message = "[未寫入] 設定：" + string.Join("；", e1); return; }
            aDone.Add("設定");
        }
        if (aAliasesChanged)
        {
            if (!SCP_TavernMentionAliases.TrySave(DataRoot, MergedAliases(iMine), SCP_TavernMentions.Whitelist(DataRoot), out List<string> e2))
            {
                m_Message = (aDone.Count > 0 ? "設定已存，但" : "") + "[未寫入] 辨認的名稱：" + string.Join("；", e2);
                return;
            }
            aDone.Add("辨認的名稱");
        }
        Reload();
        m_Message = aDone.Count == 0 ? "沒有要存的修改" : $"已存：{string.Join("、", aDone)}（酒館 Server 下一輪就會讀到，不用重啟）";
    }

    // ── 畫面 ────────────────────────────────────────────────────

    bool Busy => m_Job != null;

    protected override void TopBarButtons(SCP_Ui g)
    {
        if (DataRoot.Length == 0 || !Directory.Exists(DataRoot)) return;
        var aBad = new List<string>();
        BartenderSettings aNow = Current(g, aBad);
        List<string> aMine = CurrentAliases(g);
        bool aDirty = aBad.Count > 0 || !aNow.SameAs(m_Saved) || !aMine.SequenceEqual(m_SavedAliases);
        if (g.Button("存檔設定", "bartender/btn/save"))
        {
            if (aBad.Count > 0) m_Message = "[未寫入] " + string.Join("；", aBad);
            else Save(aNow, aMine);
        }
        g.Label(aDirty ? "（有未存的修改）" : "（與存檔相同）");
        if (g.Button("重新讀取", "bartender/btn/reload")) Reload();
        if (g.Button("AI 模型頁", "bartender/btn/to-llm")) Controller?.Push(new LlmModelPage(m_Model));
    }

    protected override void DrawContent(SCP_Ui g)
    {
        PumpJob();
        g.Title("酒保（tavern-keeper）");
        g.Note("被 @（本名或下面設定的名字）或有人寫 `[help]` 時，由酒館 Server 回一句。存檔後 Server 下一輪就讀到，不用重啟。定時提醒不在這裡（TASK-0393）。");
        if (DataRoot.Length == 0 || !Directory.Exists(DataRoot)) { g.Note($"[注意] 找不到 AgentCommands 資料根（{DataRoot}）—— 到「路徑管理」頁設定"); return; }
        if (m_Stale && !Busy)
        {
            m_Stale = false;
            Start("status", "量狀態", "bartender", new Dictionary<string, string> { ["op"] = "status", ["format"] = "json" });
        }
        // 模型清單只量一次（要跑 ollama list／nvidia-smi）；排在狀態後面 —— 同一時間只有一筆 job
        if (!m_ModelsTried && !Busy)
        {
            m_ModelsTried = true;
            Start("models", "量模型清單", "llm", new Dictionary<string, string> { ["op"] = "status", ["format"] = "json" });
        }
        if (Busy) g.Note($"執行中：{m_JobLabel}");
        if (m_Message != null) g.Note(m_Message);
        if (m_SettingsError != null) g.Note("[注意] 設定檔讀不了，畫面上是初始值；修好或刪掉之前存檔會被擋（⛔ 不覆蓋壞檔）：" + m_SettingsError);

        DrawStatus(g);
        DrawSwitch(g);
        DrawReply(g);
        DrawPersona(g);
        DrawAliases(g);
        DrawPreview(g);
    }

    void DrawStatus(SCP_Ui g)
    {
        using (g.Box("狀態", "bartender/status"))
        {
            if (m_StatusError != null) { g.Note("[注意] " + m_StatusError); return; }
            StatusView? s = m_Status;
            if (s == null) { g.Note(Busy ? "量狀態中…" : "（還沒量）"); return; }
            g.Label("設定：" + (s.SettingsSaved ? "已存過" : "還沒存過（用初始值；開關預設關）"));
            g.Note("只回酒保上線（酒館 Server 起來、或開關打開）之後收到的訊息；之前的不回。");
            if (s.StateError.Length > 0) g.Note("[注意] 狀態檔讀不了：" + s.StateError);
            g.Label(s.RepliedToday < 0 ? "今天回了幾則：讀不了（狀態檔有問題）" : $"今天回了 {s.RepliedToday} 則（@ 才算，[help] 不算）");
            if (s.LastReplySeq > 0) g.Label($"最後一次：seq {s.LastTriggerSeq} → 回 seq {s.LastReplySeq}（{s.LastReplySource}，{s.LastReplyAt}）");
            if (s.LastError.Length > 0) g.Note($"[注意] 最後一個錯誤：{s.LastError}（{s.LastErrorAt}）");
            if (s.AliasError.Length > 0) g.Note("[注意] 別名表讀不了（寫入端這時只認本名）：" + s.AliasError);
            if (!Busy && g.Button("重新量狀態", "bartender/btn/status")) m_Stale = true;
        }
    }

    void DrawSwitch(SCP_Ui g)
    {
        using (g.Box("開關", "bartender/switch"))
        {
            g.Toggle("酒保開著（被 @／[help] 時回應）", m_Saved.Enabled, Id("enabled"));
            g.Note("打開的那一刻算上線：只回之後收到的訊息，之前的不回。");
            g.TextField("顯示名（發文時的 sender_name；身分 id 固定是 tavern-keeper）", m_Saved.DisplayName, Id("name"));
        }
    }

    void DrawReply(SCP_Ui g)
    {
        using (g.Box("回應", "bartender/reply"))
        {
            var aOpts = new List<SCP_GuiOption> { new(NoModel) };
            string aCur = m_Saved.ModelId.Length == 0 ? NoModel : m_Saved.ModelId;
            foreach (string m in m_Models ?? new List<string>()) aOpts.Add(new SCP_GuiOption(m));
            if (aCur != NoModel && !aOpts.Any(o => o.Value == aCur)) aOpts.Add(new SCP_GuiOption(aCur, aCur + (m_Models == null ? "" : "（沒有安裝）")));
            g.Dropdown("模型（本機 ollama）", aOpts, aCur, Id("model"));
            string aSelected = g.FieldValue(Id("model") + "/value", aCur);
            g.Note("若要修改 context，請開啟 Ollama → Settings → Context length。上下文容量由 Ollama 管理；模型自帶 num_ctx 可能覆蓋全域預設。這裡的生成上限只限制輸出長度。");
            if (aSelected != NoModel)
                g.Label(!m_ModelsTried || (Busy && m_JobLabel == "量模型清單") ? "實際 context：查詢中…" : LlmModelPage.ContextStatus(m_LoadedModels, aSelected));
            if (m_ModelStatusError != null) g.Note("[注意] 模型狀態：" + m_ModelStatusError);
            if (!Busy && g.Button("重新量模型與 context", "bartender/btn/model-context")) m_ModelsTried = false;
            if (m_Models == null && !Busy) g.Note("模型清單量不到（ollama 沒裝或服務沒開？）—— 到 AI 模型頁看。");
            g.Toggle("開思考段（thinking 模型的推理放在思考段，不混進回答）", m_Saved.Think, Id("think"));
            g.TextField("生成上限（token）", m_Saved.NumPredict.ToString(CultureInfo.InvariantCulture), Id("num"));
            g.TextField("用完幾秒後卸載（-1＝ollama 預設 5 分鐘）", m_Saved.KeepAliveSeconds.ToString(CultureInfo.InvariantCulture), Id("keep"));
            g.TextField("等待上限（秒；超過就退回罐頭句）", m_Saved.TimeoutSeconds.ToString(CultureInfo.InvariantCulture), Id("timeout"));
            g.TextField("冷卻（秒；兩次 @ 回覆的最短間隔）", m_Saved.CooldownSeconds.ToString(CultureInfo.InvariantCulture), Id("cooldown"));
            g.TextField("每日上限（則；0＝今天不回 @）", m_Saved.DailyCap.ToString(CultureInfo.InvariantCulture), Id("cap"));
            g.Note("生成上限太小時 thinking 模型會在思考段被截斷、退回罐頭句（Unity 版 120 token 幾乎每次都是這樣）。");
        }
    }

    void DrawPersona(SCP_Ui g)
    {
        using (g.Box("人設與罐頭句", "bartender/persona"))
        {
            g.TextArea("人設（system prompt）", m_Saved.PersonaPrompt, Id("persona"), 4);
            g.TextArea("罐頭句（一行一句；沒設模型、模型失敗或逾時就照 seq 輪一句）", string.Join("\n", m_Saved.CannedReplies), Id("canned"), 6);
        }
    }

    void DrawAliases(SCP_Ui g)
    {
        using (g.Box("辨認的名稱", "bartender/aliases"))
        {
            g.TextArea("別名（一行一個，不用寫 @；本名 tavern-keeper 永遠有效、不用列）", string.Join("\n", m_SavedAliases), Id("aliases"), 4);
            g.Note("`@別名` 跟 `@tavern-keeper` 同義；全形 ＠ 也算；程式碼區段裡的不算。中文別名後面直接接字也算（`@酒保幫我調一杯`）；英文別名後面緊接英數字的不算（`@bartender2` 是另一個名字）。");
            if (m_AliasError != null) g.Note("[注意] 別名表讀不了：" + m_AliasError);
            List<string> aMine = CurrentAliases(g);
            string aKey = m_Gen + "\n" + string.Join("\n", aMine);
            if (m_AliasCheck is not { } chk || chk.Key != aKey)
            {
                List<string> aRes;
                try { aRes = AliasProblems(aMine); }
                catch (Exception e) { aRes = new List<string> { "檢查不了（" + e.GetType().Name + "：" + e.Message + "）⇒ 存檔時會再檢查一次" }; }
                m_AliasCheck = (aKey, aRes);
            }
            List<string> aProblems = m_AliasCheck.Value.Problems;
            if (aProblems.Count > 0) foreach (string p in aProblems) g.Note("[擋] " + p);
        }
    }

    void DrawPreview(SCP_Ui g)
    {
        using (g.Box("試回一句（不發文）", "bartender/preview"))
        {
            string aText = g.TextField("對酒保說", "今天推薦什麼？", "bartender/preview/text");
            g.Note("用的是**已存檔**的設定（跟 Server 真的回覆同一份）；改了還沒存的不會生效。");
            if (!Busy && aText.Trim().Length > 0 && g.Button("試回", "bartender/btn/preview"))
            {
                m_Preview = null;
                Start("preview", "試回", "bartender", new Dictionary<string, string> { ["op"] = "preview", ["format"] = "json", ["text"] = aText.Trim() });
            }
            if (m_Preview != null) g.Paragraph(m_Preview);
        }
    }
}
