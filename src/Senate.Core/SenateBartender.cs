// 區塊職責：**酒保**（persona `tavern-keeper`）的設定、游標狀態與「這則要不要回、回什麼」—— TASK-0365（重做，不是移植）。
// 物理意義：酒保住在酒館 Server（tavern 那顆）裡：SenateBartenderJob 每幾秒讀新訊息，交給本檔的 ProcessBatch 判定與回覆。
//           本檔**不碰 Server 與檔案以外的東西**：讀訊息、問 LLM、寫回覆都由呼叫端注入 ⇒ selftest 可以整段在記憶體裡跑。
// 數值影響：只回兩種 —— 被 @（本名 `tavern-keeper` 或別名表裡指向它的名字）、`[help]`；程式碼區段裡的都不算。
//           回覆由本機 ollama 生成（Cmd_Llm.Chat），失敗或沒設模型 ⇒ 罐頭句。
// ⚠ **只回酒保上線之後收到的訊息**（Tim 2026-10-05）：上線＝酒館 Server 跑起來、或開關從關打開的那一刻 ⇒ 游標設在當時最新一則；
//   停機期間的訊息不補、⇒ 游標**不存檔**（只活在 Server 的記憶體裡）。
//   上線期間回覆寫不進去 ⇒ 游標停在那一則前面、稍後重試（⛔ 不漏）。
// ⚠ 酒保的發文身分 `sender_id=tavern-keeper` 被一排讀取端當判準（薪資排除、catchup、早安隱藏…）⇒ ⛔ 不改名。
#nullable enable
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed record BartenderSettings(
    bool Enabled, string DisplayName, string ModelId, bool Think, int NumPredict, int KeepAliveSeconds, int TimeoutSeconds,
    string PersonaPrompt, IReadOnlyList<string> CannedReplies, int CooldownSeconds, int DailyCap)
{
    /// <summary>
    /// 初始值。⚠ **預設關**；開關在後台「酒保」頁。
    /// 生成上限與逾時放寬 —— 120 token 會讓 thinking 模型幾乎每次都被截斷、退成罐頭句（seq 21396 就是 canned）。
    /// </summary>
    public static readonly BartenderSettings Defaults = new(
        false, "酒保", "qwen3:0.6b", true, 4096, 120, 120, "あなたはツンデレな猫耳メイドです",
        new[]
        {
            "哼，叫本酒保有什麼事？先點杯的比較有誠意。",
            "在的在的，吧檯永遠有人。要喝什麼？",
            "來了來了 —— 擦杯子擦到一半，說吧。",
            "酒保在此。今天的推薦是「還沒倒的那一杯」。",
            "叫我？那就當你請客囉。",
        }, 30, 999);

    public bool SameAs(BartenderSettings o) => this with { CannedReplies = Array.Empty<string>() } == o with { CannedReplies = Array.Empty<string>() }
                                                && CannedReplies.SequenceEqual(o.CannedReplies);
}

/// <summary>執行狀態（Server 在寫、後台頁在讀）。⚠ 不含游標 —— 游標不存檔（只回上線後的訊息，Tim 2026-10-05）。</summary>
public sealed class BartenderState
{
    public string Day = "";                 // 本地日期 yyyy-MM-dd（每日上限用）
    public int RepliedToday;
    public long LastReplyUnix;
    public int LastReplySeq;                // 酒保最後一則回覆的 seq
    public int LastTriggerSeq;              // 最後一則被回應的訊息 seq
    public string LastReplyAt = "";
    public string LastReplySource = "";     // model id／canned／canned-after-error
    public string LastError = "";
    public string LastErrorAt = "";

    public BartenderState Clone() => new()
    {
        Day = Day, RepliedToday = RepliedToday, LastReplyUnix = LastReplyUnix,
        LastReplySeq = LastReplySeq, LastTriggerSeq = LastTriggerSeq, LastReplyAt = LastReplyAt, LastReplySource = LastReplySource,
        LastError = LastError, LastErrorAt = LastErrorAt,
    };
}

/// <summary>一則訊息的判定。</summary>
public enum BartenderAction { None, Mention, Help }

public static class SenateBartender
{
    public const string PersonaId = "tavern-keeper";
    public const string Room = "tavern";
    public const string SettingsFileName = "senate_settings.json";
    public const string StateFileName = "senate_state.json";
    public const string ReplyTag = "bartender-relay";     // 讀取端（tavern-wait／早安／catchup）拿它當過濾條件 ⇒ 沿用
    public const int BatchSize = 50;

    static readonly JsonSerializerOptions s_Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Dir(string iDataRoot) => SCP.Core.Paths.SCP_DataPaths.Bartender(new SCP.Core.Paths.SCP_DataRoot(iDataRoot));   // 版面唯一一處（TASK-0390）
    public static string SettingsPath(string iDataRoot) => Path.Combine(Dir(iDataRoot), SettingsFileName);
    public static string StatePath(string iDataRoot) => Path.Combine(Dir(iDataRoot), StateFileName);

    // ── 設定 ─────────────────────────────────────────────────────

    /// <summary>沒有檔 ⇒ 初始值（oSource 說「還沒存過」）；讀不了 ⇒ 初始值＋oError（⛔ 不跟「沒存過」同形）。缺的欄位逐格補初始值。</summary>
    public static BartenderSettings LoadSettings(string iDataRoot, out string? oError)
    {
        oError = null;
        string aPath = SettingsPath(iDataRoot);
        if (!File.Exists(aPath)) return BartenderSettings.Defaults;
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(aPath));
            JsonElement r = d.RootElement;
            BartenderSettings z = BartenderSettings.Defaults;
            string Str(string k, string iDef) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? iDef : iDef;
            bool Bool(string k, bool iDef) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind is JsonValueKind.True or JsonValueKind.False ? x.GetBoolean() : iDef;
            int Int(string k, int iDef) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int v) ? v : iDef;
            IReadOnlyList<string> aCanned = z.CannedReplies;
            if (r.TryGetProperty("canned_replies", out JsonElement c) && c.ValueKind == JsonValueKind.Array)
            {
                var aList = c.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!.Trim()).Where(x => x.Length > 0).ToList();
                if (aList.Count > 0) aCanned = aList;
            }
            return new BartenderSettings(Bool("enabled", z.Enabled), Str("display_name", z.DisplayName), Str("model_id", z.ModelId), Bool("think", z.Think),
                Int("num_predict", z.NumPredict), Int("keep_alive_seconds", z.KeepAliveSeconds), Int("timeout_seconds", z.TimeoutSeconds),
                Str("persona_prompt", z.PersonaPrompt), aCanned, Int("mention_cooldown_seconds", z.CooldownSeconds), Int("mention_daily_cap", z.DailyCap));
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            oError = $"{aPath} 讀不了：{e.Message}";
            return BartenderSettings.Defaults;
        }
    }

    /// <summary>存檔前的檢查；回每一條問題（空 ＝ 可以存）。</summary>
    public static List<string> Validate(BartenderSettings s)
    {
        var e = new List<string>();
        if (s.DisplayName.Trim().Length == 0) e.Add("顯示名不能是空白");
        if (s.NumPredict < 16) e.Add("生成上限至少 16 token");
        if (s.TimeoutSeconds < 5 || s.TimeoutSeconds > 600) e.Add("等待上限要在 5～600 秒之間");
        if (s.KeepAliveSeconds < -1) e.Add("卸載秒數要 ≥ -1（-1＝ollama 預設）");
        if (s.CooldownSeconds < 0) e.Add("冷卻秒數不能是負的");
        if (s.DailyCap < 0) e.Add("每日上限不能是負的（0＝今天不回）");
        if (s.CannedReplies.Count == 0) e.Add("罐頭句至少要一句（模型失敗時沒有東西可以回）");
        return e;
    }

    public static string SettingsJson(BartenderSettings s) => JsonSerializer.Serialize(new
    {
        enabled = s.Enabled, display_name = s.DisplayName, model_id = s.ModelId, think = s.Think, num_predict = s.NumPredict,
        keep_alive_seconds = s.KeepAliveSeconds, timeout_seconds = s.TimeoutSeconds, persona_prompt = s.PersonaPrompt,
        canned_replies = s.CannedReplies, mention_cooldown_seconds = s.CooldownSeconds, mention_daily_cap = s.DailyCap,
    }, s_Json);

    /// <summary>檢查 → 原子寫 → 讀回比對。現有檔讀不了也擋（⛔ 不安靜蓋掉壞檔）。</summary>
    public static bool TrySaveSettings(string iDataRoot, BartenderSettings s, out List<string> oErrors)
    {
        oErrors = Validate(s);
        if (oErrors.Count > 0) return false;
        LoadSettings(iDataRoot, out string? aErr);
        if (aErr != null) { oErrors.Add("現有的設定檔讀不了，先修好或刪掉再存（⛔ 不覆蓋）：" + aErr); return false; }
        try { SCP.Core.Letters.SCP_CmdPayload.WriteAtomic(SettingsPath(iDataRoot), SettingsJson(s) + "\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { oErrors.Add("寫不進去：" + e.Message); return false; }
        BartenderSettings aBack = LoadSettings(iDataRoot, out string? aBackErr);
        if (aBackErr != null || !aBack.SameAs(s)) { oErrors.Add("寫完讀回來對不上：" + (aBackErr ?? "欄位值不同")); return false; }
        return true;
    }

    // ── 狀態 ─────────────────────────────────────────────────────

    /// <summary>沒有檔 ⇒ 空狀態（oError=null）；讀不了 ⇒ 空狀態＋oError —— ⚠ 呼叫端拿到 oError 時**不可以**把游標當成「從頭開始」。</summary>
    public static BartenderState LoadState(string iDataRoot, out string? oError)
    {
        oError = null;
        var s = new BartenderState();
        string aPath = StatePath(iDataRoot);
        if (!File.Exists(aPath)) return s;
        try
        {
            using JsonDocument d = JsonDocument.Parse(File.ReadAllText(aPath));
            JsonElement r = d.RootElement;
            string Str(string k) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
            long Num(string k) => r.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out long v) ? v : 0;
            s.Day = Str("day"); s.RepliedToday = (int)Num("replied_today"); s.LastReplyUnix = Num("last_reply_unix");
            s.LastReplySeq = (int)Num("last_reply_seq"); s.LastTriggerSeq = (int)Num("last_trigger_seq"); s.LastReplyAt = Str("last_reply_at");
            s.LastReplySource = Str("last_reply_source"); s.LastError = Str("last_error"); s.LastErrorAt = Str("last_error_at");
            return s;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            oError = $"{aPath} 讀不了：{e.Message}";
            return new BartenderState();
        }
    }

    public static string StateJson(BartenderState s) => JsonSerializer.Serialize(new
    {
        day = s.Day, replied_today = s.RepliedToday, last_reply_unix = s.LastReplyUnix, last_reply_seq = s.LastReplySeq,
        last_trigger_seq = s.LastTriggerSeq, last_reply_at = s.LastReplyAt, last_reply_source = s.LastReplySource,
        last_error = s.LastError, last_error_at = s.LastErrorAt,
    }, s_Json);

    public static void SaveState(string iDataRoot, BartenderState s)
        => SCP.Core.Letters.SCP_CmdPayload.WriteAtomic(StatePath(iDataRoot), StateJson(s) + "\n");

    // ── 判定與回覆內容 ────────────────────────────────────────────

    /// <summary>這則要不要回、回哪一種。酒保自己的訊息不回；程式碼區段裡的 `[help]` 與 @ 不算。</summary>
    public static BartenderAction Decide(SCP_TavernMessage m, IReadOnlyDictionary<string, string>? iAliases)
    {
        if (m.SenderId == PersonaId || m.SenderPersona == PersonaId) return BartenderAction.None;
        string aBody = m.Body ?? "";
        if (SCP_TavernMentions.StripCode(aBody).IndexOf("[help]", StringComparison.OrdinalIgnoreCase) >= 0) return BartenderAction.Help;
        return SCP_TavernMentions.Extract(aBody, iAliases).Contains(PersonaId) ? BartenderAction.Mention : BartenderAction.None;
    }

    public const string HelpText =
        "**酒保能做的事**\n"
        + "- `@tavern-keeper`（或後台設定的別名，例如 `@酒保`）：跟酒保說話，酒保會回一句。\n"
        + "- `[help]`：顯示這份說明。\n"
        + "\n"
        + "程式碼區段（反引號）裡的 @ 與 `[help]` 不算數 —— 引用不會把酒保叫出來。\n"
        + "查餘額不歸酒保：`senate cmd bank --arg op=balance --arg account=<帳號>`。";

    /// <summary>把 @ 酒保的部分拿掉，剩下的是要問的話（空的話換成一句「有人叫了你一聲」）。</summary>
    public static string AskText(string iBody, IReadOnlyDictionary<string, string>? iAliases)
    {
        var aNames = new List<string> { PersonaId };
        if (iAliases != null) aNames.AddRange(iAliases.Where(kv => kv.Value == PersonaId).Select(kv => kv.Key));
        string s = iBody ?? "";
        foreach (string n in aNames.OrderByDescending(x => x.Length))
            s = System.Text.RegularExpressions.Regex.Replace(s, "[@＠]" + System.Text.RegularExpressions.Regex.Escape(n), "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        s = s.Trim();
        return s.Length == 0 ? "有人叫了你一聲，回應一下。" : s;
    }

    public static string Who(SCP_TavernMessage m)
        => m.SenderPersona.Length > 0 ? m.SenderPersona : m.SenderName.Length > 0 ? m.SenderName : m.SenderId;

    /// <summary>罐頭句：照 seq 輪（同一則永遠同一句，可複驗）。</summary>
    public static string Canned(BartenderSettings s, int iSeq)
    {
        IReadOnlyList<string> p = s.CannedReplies.Count > 0 ? s.CannedReplies : BartenderSettings.Defaults.CannedReplies;
        return p[(int)((uint)iSeq % (uint)p.Count)];
    }

    /// <summary>回覆一則的 msg_json（`sender_id=tavern-keeper`、meta.tag=bartender-relay、reply_to＝觸發的那則）。</summary>
    public static SCP_JsonData ReplyJson(BartenderSettings s, SCP_TavernMessage iTrigger, BartenderAction iKind, string iBody, string iSource)
    {
        var aMeta = SCP_JsonData.NewObject();
        aMeta.Set("tag", ReplyTag);
        aMeta.Set("reply_kind", iKind == BartenderAction.Help ? "help" : "mention");
        aMeta.Set("reply_source", iSource);
        aMeta.Set("triggered_by_seq", iTrigger.Seq.ToString(CultureInfo.InvariantCulture));
        aMeta.Set("triggered_by_sender", iTrigger.SenderId);
        aMeta.Set("relay", "senate");
        var j = SCP_JsonData.NewObject();
        j.Set("sender_id", PersonaId);
        j.Set("sender_name", s.DisplayName);
        j.Set("kind", "chat");
        j.Set("body", iBody);
        j.Set("meta", aMeta);
        j.Set("reply_to", iTrigger.Seq);
        return j;
    }

    /// <summary>一批的處理結果。</summary>
    public sealed class BatchOutcome
    {
        public BartenderState State = new();
        public List<string> Log = new();
        public int Cursor;                      // 處理到哪一則（呼叫端放回記憶體；⛔ 不存檔）
        public bool StoppedOnWriteFailure;
        /// <summary>冷卻中而停在某一則前面（⛔ 不丟掉 —— 冷卻結束後回它）；值是可以再試的時刻。</summary>
        public DateTime? DeferredUntilUtc;
        public bool Changed;                    // 狀態（回覆數／最後回覆／錯誤）有沒有變 —— 有才寫檔
    }

    /// <summary>
    /// 照 seq 順序處理一批訊息。每處理完一則就推進游標；兩種情況**停在那一則前面**（游標不動）：
    /// 回覆寫不進去（下一輪重試）、冷卻中（冷卻結束再回 —— 排隊，⛔ 不丟）。
    /// <paramref name="iGenerate"/>：(system, prompt) → (ok, 文字, 來源標籤, 錯誤)；<paramref name="iWrite"/>：msg_json → (ok, seq, 錯誤)；
    /// <paramref name="iNow"/>：每一則各取一次（LLM 可能跑了幾十秒，⛔ 不用整批開頭那一刻）。
    /// </summary>
    public static BatchOutcome ProcessBatch(BartenderSettings s, BartenderState iState, string iRoom, IReadOnlyList<SCP_TavernMessage> iBatch,
        IReadOnlyDictionary<string, string>? iAliases, int iCursor,
        Func<string, string, (bool Ok, string Text, string Source, string Error)> iGenerate,
        Func<SCP_JsonData, (bool Ok, int Seq, string Error)> iWrite, Func<DateTime> iNow)
    {
        var o = new BatchOutcome { State = iState.Clone(), Cursor = iCursor };
        BartenderState st = o.State;
        foreach (SCP_TavernMessage m in iBatch.OrderBy(x => x.Seq))
        {
            if (m.Seq <= o.Cursor) continue;
            DateTime aNowUtc = iNow();
            string aToday = aNowUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (st.Day != aToday) { st.Day = aToday; st.RepliedToday = 0; o.Changed = true; }
            BartenderAction a = Decide(m, iAliases);
            if (a == BartenderAction.Mention && st.RepliedToday >= s.DailyCap)
            {
                o.Log.Add($"seq {m.Seq}：今天已回 {st.RepliedToday} 則，到上限 {s.DailyCap} ⇒ 不回");
                a = BartenderAction.None;
            }
            if (a != BartenderAction.None && s.CooldownSeconds > 0)
            {
                long aNowUnix = new DateTimeOffset(aNowUtc).ToUnixTimeSeconds();
                long aWait = st.LastReplyUnix + s.CooldownSeconds - aNowUnix;
                if (aWait > 0)
                {
                    // 冷卻中 ⇒ 停在這一則前面、冷卻結束再回它（⛔ 不丟：重啟補回或兩個人同時叫時，後面的人也要等得到）
                    o.DeferredUntilUtc = aNowUtc.AddSeconds(aWait);
                    o.Log.Add($"seq {m.Seq}：冷卻中，{aWait} 秒後回");
                    return o;
                }
            }
            if (a != BartenderAction.None)
            {
                string aText, aSource;
                if (a == BartenderAction.Help) { aText = HelpText; aSource = "help"; }
                else
                {
                    (bool ok, string text, string src, string err) = s.ModelId.Length == 0
                        ? (false, "", "canned", "")
                        : iGenerate(s.PersonaPrompt, $"{Who(m)} 對你說：{AskText(m.Body, iAliases)}");
                    if (ok && text.Trim().Length > 0) { aText = text.Trim(); aSource = src; }
                    else
                    {
                        aText = Canned(s, m.Seq);
                        aSource = s.ModelId.Length == 0 ? "canned" : "canned-after-error";
                        if (err.Length > 0) { o.Changed = true; st.LastError = $"seq {m.Seq} 生成失敗、退回罐頭句：{err}"; st.LastErrorAt = iNow().ToString("o", CultureInfo.InvariantCulture); }
                    }
                }
                (bool wOk, int wSeq, string wErr) = iWrite(ReplyJson(s, m, a, aText, aSource));
                DateTime aDoneUtc = iNow();
                if (!wOk)
                {
                    st.LastError = $"seq {m.Seq} 的回覆寫不進去：{wErr}（游標停在它前面，稍後重試）";
                    st.LastErrorAt = aDoneUtc.ToString("o", CultureInfo.InvariantCulture);
                    o.Log.Add(st.LastError);
                    o.StoppedOnWriteFailure = true;
                    o.Changed = true;
                    return o;
                }
                o.Changed = true;
                if (a == BartenderAction.Mention) st.RepliedToday++;
                st.LastReplyUnix = new DateTimeOffset(aDoneUtc).ToUnixTimeSeconds();
                st.LastReplySeq = wSeq; st.LastTriggerSeq = m.Seq; st.LastReplySource = aSource;
                st.LastReplyAt = aDoneUtc.ToString("o", CultureInfo.InvariantCulture);
                o.Log.Add($"seq {m.Seq}（{Who(m)}）→ 酒保回 seq {wSeq}（{aSource}）");
            }
            o.Cursor = m.Seq;
        }
        return o;
    }
}
