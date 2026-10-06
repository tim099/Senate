// 區塊職責：`senate cmd bartender` —— 酒保（tavern-keeper）的狀態與試回（TASK-0365）。
// 物理意義：真正回應的是酒館 Server 裡的 SenateBartenderJob；本支只**讀**它的設定與狀態，以及在不發文的情況下試回一句。
//           「酒保」後台頁讀同一份（format=json）。
// 數值影響：status 純讀；preview 會呼叫本機 ollama（跟真的回覆同一份 Chat），⛔ 不寫進酒館。
// ⚠ exit code：0 成功；2 用法錯；4 量不到（設定檔／狀態檔讀不了）；5 試回生成失敗（附上會退回的罐頭句）。
#nullable enable
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using SCP.Core.Cmd;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed class Cmd_Bartender : SCP_Cmd
{
    public override string Name => "bartender";
    public override string Category => SCP_CmdCategory.Tavern;

    public override string Summary => "酒保（tavern-keeper）：status 看開關／今天回了幾則／最後一次回覆／別名；preview 不發文試回一句 —— 回應本身由酒館 Server 執行";

    public override string Details =>
        "酒保只回兩種：被 @（`@tavern-keeper` 或別名表裡指向它的名字，例如 `@酒保`）與 `[help]`；程式碼區段裡的不算。\n"
        + "設定在 `ChatTavern/bartender/" + SenateBartender.SettingsFileName + "`、別名在 `ChatTavern/" + SCP_TavernMentionAliases.FileName + "`，改它們走後台「酒保」頁。\n"
        + "⚠ 開關預設關；只回上線之後收到的訊息（Server 起來或開關打開的那一刻起），停機期間的不補。\n"
        + "⚠ 查餘額不歸酒保：`senate cmd bank --arg op=balance --arg account=<帳號>`。";

    public override string Example => SCP_CmdRegistry.Invoke("bartender --arg op=preview --arg text=\"今天推薦什麼？\"");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "status（預設）｜preview", iDefault: "status", iChoices: new[] { "status", "preview" }),
        new SCP_CmdArgSpec("text", "preview：要對酒保說的話（不用寫 @）"),
        new SCP_CmdArgSpec("who", "preview：說話的人（預設 Tim）", iDefault: "Tim"),
        new SCP_CmdArgSpec("format", "text（預設）｜json（後台頁讀這個）", iDefault: "text", iChoices: new[] { "text", "json" }),
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（沒給 ⇒ 用設定檔那一格）"),
    };

    static readonly JsonSerializerOptions s_Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aData = iArgs.Get("data_root");
        if (aData.Length == 0 || !Directory.Exists(aData)) return SCP_CmdResult.Fail(2, $"✗ 解不出資料根（{aData}）—— 用 `senate cmd paths` 看");
        bool aJson = iArgs.Get("format") == "json";
        BartenderSettings s = SenateBartender.LoadSettings(aData, out string? aSetErr);
        return iArgs.Get("op") == "preview" ? Preview(s, aSetErr, iArgs, aJson) : Status(aData, s, aSetErr, aJson);
    }

    static SCP_CmdResult Status(string iData, BartenderSettings s, string? iSetErr, bool iJson)
    {
        BartenderState st = SenateBartender.LoadState(iData, out string? aStErr);
        Dictionary<string, string> aAliases = SCP_TavernMentionAliases.Load(iData, out string? aAliasErr);
        List<string> aMine = aAliases.Where(kv => kv.Value == SenateBartender.PersonaId).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        bool aHasSettings = File.Exists(SenateBartender.SettingsPath(iData));
        var r = new SCP_CmdResult { ExitCode = iSetErr != null || aStErr != null ? 4 : 0 };
        r.AddValue("enabled", s.Enabled ? "1" : "0");
        if (iJson)
        {
            r.Lines.Add(JsonSerializer.Serialize(new
            {
                ok = r.ExitCode == 0, settings_error = iSetErr ?? "", state_error = aStErr ?? "", alias_error = aAliasErr ?? "",
                settings_saved = aHasSettings, settings_path = SenateBartender.SettingsPath(iData), aliases_path = SCP_TavernMentionAliases.PathOf(iData),
                enabled = s.Enabled,
                // 狀態檔讀不了 ⇒ null（⛔ 不給 0：那是讀數，這裡沒有讀數）
                replied_today = aStErr != null ? (int?)null : st.Day == DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ? st.RepliedToday : 0,
                last_reply_seq = st.LastReplySeq, last_trigger_seq = st.LastTriggerSeq, last_reply_at = st.LastReplyAt, last_reply_source = st.LastReplySource,
                last_error = st.LastError, last_error_at = st.LastErrorAt, aliases = aMine,
            }, s_Json));
            return r;
        }
        r.Lines.Add("# 酒保（tavern-keeper）");
        if (iSetErr != null) r.Lines.Add("⚠ 設定檔讀不了（Server 這邊這一輪不動）：" + iSetErr);
        r.Lines.Add($"· 開關：{(s.Enabled ? "開" : "關")}　設定：{(aHasSettings ? SenateBartender.SettingsPath(iData) : "還沒存過（用初始值）")}");
        r.Lines.Add($"· 模型：{(s.ModelId.Length == 0 ? "（不用模型，只回罐頭句）" : s.ModelId)}　上限 {s.NumPredict} token　等 {s.TimeoutSeconds} 秒　冷卻 {s.CooldownSeconds} 秒　每日上限 {s.DailyCap}");
        r.Lines.Add("· 認得的名字：@" + SenateBartender.PersonaId + (aMine.Count > 0 ? "、" + string.Join("、", aMine.Select(a => "@" + a)) : "") + "（全形 ＠ 也算）");
        if (aAliasErr != null) r.Lines.Add("  ⚠ 別名表讀不了（寫入端這時只認本名）：" + aAliasErr);
        if (aStErr != null) r.Lines.Add("⚠ 狀態檔讀不了：" + aStErr);
        r.Lines.Add("· 只回酒保上線（酒館 Server 起來、或開關打開）之後收到的訊息；之前的不回，也不記讀到哪一則");
        if (st.LastReplySeq > 0) r.Lines.Add($"· 最後一次回覆：seq {st.LastTriggerSeq} → 回 seq {st.LastReplySeq}（{st.LastReplySource}，{st.LastReplyAt}）");
        if (st.LastError.Length > 0) r.Lines.Add($"· 最後一個錯誤：{st.LastError}（{st.LastErrorAt}）");
        return r;
    }

    static SCP_CmdResult Preview(BartenderSettings s, string? iSetErr, SCP_CmdArgs iArgs, bool iJson)
    {
        string aText = iArgs.Get("text").Trim();
        if (aText.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=preview 要給 --arg text=<要對酒保說的話>");
        string aWho = iArgs.Get("who").Trim(); if (aWho.Length == 0) aWho = "Tim";
        string aPrompt = $"{aWho} 對你說：{aText}";
        string aCanned = SenateBartender.Canned(s, aText.GetHashCode());
        Cmd_Llm.TestResult? t = s.ModelId.Length == 0 ? null
            : Cmd_Llm.Chat(s.ModelId, aPrompt, s.PersonaPrompt, s.Think, s.NumPredict, s.KeepAliveSeconds, s.TimeoutSeconds);
        bool aOk = t is { ok: true } && t.output.Trim().Length > 0;
        string aReply = aOk ? t!.output.Trim() : aCanned;
        string aSource = aOk ? s.ModelId : s.ModelId.Length == 0 ? "canned" : "canned-after-error";
        string aWhy = t == null ? "" : t.error.Length > 0 ? t.error : t.note;
        var r = new SCP_CmdResult { ExitCode = t == null || aOk ? 0 : 5 };
        r.AddValue("reply_source", aSource);
        if (iJson)
        {
            r.Lines.Add(JsonSerializer.Serialize(new
            {
                ok = r.ExitCode == 0, reply = aReply, source = aSource, prompt = aPrompt, system = s.PersonaPrompt,
                seconds = t?.seconds ?? 0, thinking = t?.thinking ?? "", error = aOk ? "" : aWhy, settings_error = iSetErr ?? "",
            }, s_Json));
            return r;
        }
        r.Lines.Add($"# 酒保試回（不發文）");
        if (iSetErr != null) r.Lines.Add("⚠ 設定檔讀不了，用的是初始值：" + iSetErr);
        r.Lines.Add($"· 問：{aPrompt}");
        r.Lines.Add($"· 回（{aSource}{(t != null ? $"，{t.seconds} 秒" : "")}）：{aReply}");
        if (!aOk && aWhy.Length > 0) r.Lines.Add("  ⚠ 模型沒回成 ⇒ 真的發文時會退回這句罐頭：" + aWhy);
        return r;
    }
}
