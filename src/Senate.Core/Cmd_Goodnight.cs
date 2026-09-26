// 區塊職責：晚安流程的 `senate cmd` 入口 —— `goodnight-check` / `-portrait` / `-letter` / `-sleep` / `-logout`。
//           五支都在 Senate **就地執行**，不需要 Unity Editor（TASK-0305，承接 TASK-0303 早安）。
// 物理意義：邏輯在 SCP_Core `SCP_Goodnight`（Editor 的 `senate ucmd run GoodNight` 呼叫同一份），本檔只做
//           「參數 → 呼叫 → 落回傳檔」＋ sleep 的組裝（預檢 → 寫入 → 關場 → 廣播 → 作廢 token）。
//           下線廣播交給酒館 Server（`tavern-write`）。
// ⚠ 只有兩段要 Editor：本人**進行中的觀影場**要結算（付錢／收播公告／關錄影頁），以及收工閘帶 `skip_reason`
//   時要把理由**寫進單子**（單子寫入端只有 Editor）。Tim 2026-09-26 拍板：**Editor 沒開就跳過那一段，
//   不得卡住晚安** ⇒ Editor 活著（酒保心跳新鮮）就整步交給 `goodnight-sleep-editor`；沒開就照走並大聲說
//   跳過了什麼（觀影場留著不關 → 到期成殘留、殘留結算會補付；skip 理由改印進回傳檔與下線廣播）。
//   ⛔ 判斷 Editor 在不在用**心跳**，不用「送出去等逾時」：逾時是「不知道」—— Editor 可能稍後才執行，
//   那樣會跟本地版重複下線一次。
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Tavern;

namespace Senate.Core;

// ── ① check ──────────────────────────────────────────────────────

public sealed class Cmd_GoodnightCheck : MorningLocalCmd
{
    public override string Name => "goodnight-check";
    public override string Summary => "晚安①唯讀起手：待辦盤點＋酒館最後一眼＋Task 對帳 —— Senate 就地執行，不需要 Editor";
    public override string Details =>
        "純讀：lock 狀態、酒館最近 10 筆（peek 不動游標）、Task 對帳（見叢引用／未關單／逾期認領／記憶連結／收工預告），\n"
        + "最後印人工收尾清單（portrait 與 letter 標 **required**，會實擋）。";
    public override string Example => SCP_CmdRegistry.Invoke("goodnight-check --arg persona=Template");
    protected override string CliNextHint =>
        "照回傳檔的收尾清單走 → " + SCP_CmdRegistry.Invoke("goodnight-portrait --arg persona=<P> --arg about=<同事> --arg-file body=<檔>");
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(MorningSpecs());

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        => GoodnightLocal.Finish(iRoots, iArgs.Get("persona").Trim(), "check",
                                 SCP_Goodnight.Check(iRoots, iArgs.Get("persona").Trim()), ioResult);
}

// ── ② portrait ───────────────────────────────────────────────────

public sealed class Cmd_GoodnightPortrait : MorningLocalCmd
{
    public override string Name => "goodnight-portrait";
    public override string Summary => "晚安②見人畫像投遞（親筆），或顯式跳過 —— Senate 就地執行，不需要 Editor";
    public override string Details =>
        "兩條路二擇一（**會擋 letter**）：\n"
        + "  · 畫一幅：about ＋ body（親筆公開層）必填；headline／private_body／affinity 選填。\n"
        + "    事實源寫進自己的 sketchbook，公開層投遞到對方的 portraits（私層不留痕跡）。about 必須是現有 persona。\n"
        + "  · 今夜不畫：skip_reason ——理由會印進下線廣播。";
    public override string Example =>
        SCP_CmdRegistry.Invoke("goodnight-portrait --arg persona=Template --arg about=basecamp --arg headline=<標題> --arg-file body=D:/tmp/p.md");
    protected override string CliNextHint => SCP_CmdRegistry.Invoke("goodnight-letter --arg persona=<P> --arg-file letter_body=<檔>");
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var a = new List<SCP_CmdArgSpec>(MorningSpecs());
            a.Add(new SCP_CmdArgSpec("about", "畫誰（同事的 persona 名）"));
            a.Add(new SCP_CmdArgSpec("headline", "一句話標題"));
            a.Add(new SCP_CmdArgSpec("body", "公開層內文（**親筆**，工具不代筆）。長內文走 --arg-file"));
            a.Add(new SCP_CmdArgSpec("private_body", "私層內文（選填，只留在自己的 sketchbook）"));
            a.Add(new SCP_CmdArgSpec("affinity", "好感讀數，如 `11/在意`（選填）"));
            a.Add(new SCP_CmdArgSpec("skip_reason", "今夜不畫的理由（會印進下線廣播）"));
            return a;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string p = iArgs.Get("persona").Trim();
        return GoodnightLocal.Finish(iRoots, p, "portrait", SCP_Goodnight.Portrait(iRoots, p,
            iArgs.Get("about"), iArgs.Get("headline"), iArgs.Get("body"), iArgs.Get("private_body"),
            iArgs.Get("skip_reason"), iArgs.Get("affinity")), ioResult);
    }
}

// ── ③ letter ─────────────────────────────────────────────────────

public sealed class Cmd_GoodnightLetter : MorningLocalCmd
{
    public override string Name => "goodnight-letter";
    public override string Summary => "晚安③收尾信落檔（body 必須親筆）—— Senate 就地執行，不需要 Editor";
    public override string Details =>
        "寫 `wakes/<N>_<ts>.md`（N＝信數＋1）並同步 `_latest.md`。\n"
        + "⚠ 前置：今天已投遞畫像或顯式跳過（goodnight-portrait）；收尾信版面已遷移。\n"
        + "⚠ 目標編號已有信就擋 —— 不覆寫（編號推導與磁碟不一致時，蓋掉舊信是最糟的結果）。";
    public override string Example => SCP_CmdRegistry.Invoke("goodnight-letter --arg persona=Template --arg-file letter_body=D:/tmp/letter.md");
    protected override string CliNextHint => SCP_CmdRegistry.Invoke("goodnight-sleep --arg persona=<P> [--arg-file summary=<檔>]");
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var a = new List<SCP_CmdArgSpec>(MorningSpecs());
            a.Add(new SCP_CmdArgSpec("letter_body", "寫給未來自己的收尾信（**親筆**）。長內文走 --arg-file", iRequired: true));
            return a;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string p = iArgs.Get("persona").Trim();
        return GoodnightLocal.Finish(iRoots, p, "letter", SCP_Goodnight.Letter(iRoots, p, iArgs.Get("letter_body")), ioResult);
    }
}

// ── ④ sleep ／ ⑤ logout ─────────────────────────────────────────

public sealed class Cmd_GoodnightSleep : MorningLocalCmd
{
    public override string Name => "goodnight-sleep";
    public override string Summary => "晚安④下線：收工閘→解鎖→關場→下線廣播→作廢 token —— Senate 就地執行，Editor 沒開也下得了線";
    public override string Details => GoodnightLocal.SleepDetails
        + "\n⚠ **收工閘會實擋**：有未收工的單時非零退出。`skip_reason` 可以過閘 —— Editor 活著時理由寫進那幾張單的時間線；\n"
        + "   沒開時改印進回傳檔與下線廣播（單子寫入端只有 Editor）。\n"
        + "⚠ 需要先寫信（`goodnight-letter`）。不想寫信的下線走 `goodnight-logout`。";
    public override string Example => SCP_CmdRegistry.Invoke("goodnight-sleep --arg persona=Template --arg-file summary=D:/tmp/s.md");
    protected override string CliNextHint =>
        "（晚安到此結束 —— 要重新上線走 " + SCP_CmdRegistry.Invoke("morning-wake --arg persona=<P>") + "）";
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => GoodnightLocal.SleepSpecs(MorningSpecs(), iSleep: true);

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        => GoodnightLocal.RunSleep(iRoots, iArgs, ioResult, iNoLetter: false);
}

public sealed class Cmd_GoodnightLogout : MorningLocalCmd
{
    public override string Name => "goodnight-logout";
    public override string Summary => "手動登出／cleanup（不寫信，廣播標明未留信）—— Senate 就地執行，不需要 Editor";
    public override string Details =>
        "**這不是晚安的第五步，是另一條路** —— session 壞掉、或只想清掉 lock 時走它。\n"
        + "⚠ 不套收工閘（那是 cleanup 不是收工）；不寫信 ⇒ 廣播標明未留信。**它不能代替 goodnight-sleep。**\n"
        + "⚠ lock 在但讀不了（壞檔）也會刪掉並明說 —— 那正是 cleanup 要處理的情況。\n" + GoodnightLocal.SleepDetails;
    public override string Example => SCP_CmdRegistry.Invoke("goodnight-logout --arg persona=Template");
    protected override string CliNextHint =>
        "（cleanup 完成 —— 要正常收工走 " + SCP_CmdRegistry.Invoke("goodnight-check --arg persona=<P>") + "）";
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => GoodnightLocal.SleepSpecs(MorningSpecs(), iSleep: false);

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        => GoodnightLocal.RunSleep(iRoots, iArgs, ioResult, iNoLetter: true);
}

/// <summary>晚安本地 Cmd 的共用段（落回傳檔／sleep 組裝／Editor 在不在）。</summary>
internal static class GoodnightLocal
{
    internal const string SleepDetails =
        "順序是**不變式**：預檢（全部守衛，零寫入）→ 刪 lock／now_status → 關本人活動 session → 下線廣播（best-effort）→ 作廢 token。\n"
        + "只有兩段要 Editor：進行中的**觀影場**結算、收工閘 `skip_reason` 寫進單子。Editor 活著（酒保心跳 ≤4 秒）⇒ 整步交給 Editor；\n"
        + "沒開 ⇒ 照走，只跳過那一段並在回傳檔明說（觀影場留著，到期成殘留後由殘留結算補付）。";

    internal static IReadOnlyList<SCP_CmdArgSpec> SleepSpecs(IEnumerable<SCP_CmdArgSpec> iBase, bool iSleep)
    {
        var a = new List<SCP_CmdArgSpec>(iBase);
        if (iSleep)
        {
            a.Add(new SCP_CmdArgSpec("summary", "公開的睡前心得（選填，併入下線廣播）"));
            a.Add(new SCP_CmdArgSpec("skip_reason", "跳過收工閘的理由（Editor 活著時寫進那幾張單的時間線）"));
        }
        a.Add(new SCP_CmdArgSpec("note", "附註（選填，併入下線廣播）"));
        a.Add(new SCP_CmdArgSpec("no_token", "=true ⇒ 廣播顯式不帶 session_token（enforce 除錯用）"));
        a.Add(new SCP_CmdArgSpec("timeout", "等酒館 Server／Editor 回執的秒數（預設 30）"));
        return a;
    }

    /// <summary>check／portrait／letter：落回傳檔、blocked 非零退出。</summary>
    internal static string Finish(SCP_MorningRoots iRoots, string iPersona, string iStep, SCP_MorningStepResult iRes, SCP_CmdResult ioResult)
    {
        string aPath = SCP_Goodnight.StepPayloadPath(iRoots, iPersona, iStep);
        SCP_CmdPayload.Write(aPath, iRes.Report);
        if (!iRes.Ok)
        {
            ioResult.ExitCode = 1;
            ioResult.Lines.Add("⛔ 被擋下 —— 原因與出口在回傳檔的 `## blocked`");
        }
        else ioResult.Lines.Add($"✓ goodnight {iStep} 完成");
        return aPath;
    }

    /// <summary>Editor 在不在 tick：酒保 daemon 心跳（Editor update 迴圈每 0.5 秒摸一次）。</summary>
    internal static bool EditorAlive(string iDataRoot, out string oWhy)
    {
        string aHb = Path.Combine(iDataRoot, ProjectProbe.HeartbeatRelPath);
        if (!File.Exists(aHb)) { oWhy = "沒有酒保心跳檔"; return false; }
        TimeSpan aAge = DateTime.UtcNow - File.GetLastWriteTimeUtc(aHb);
        oWhy = $"酒保心跳 {aAge.TotalSeconds:F1} 秒前";
        return aAge <= ProjectProbe.HeartbeatStaleAfter;
    }

    internal static string? RunSleep(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult, bool iNoLetter)
    {
        string aPersona = iArgs.Get("persona").Trim();
        string aStep = iNoLetter ? "logout" : "sleep";
        string aSkip = iNoLetter ? "" : iArgs.Get("skip_reason").Trim();
        string aPath = SCP_Goodnight.StepPayloadPath(iRoots, aPersona, aStep);

        // ① 預檢（零寫入）
        SCP_GoodnightPreflight aPre = SCP_Goodnight.SleepPreflight(iRoots, aPersona, iNoLetter, aSkip);
        if (aPre.Blocked)
        {
            SCP_CmdPayload.Write(aPath, aPre.Report);
            ioResult.ExitCode = 1;
            ioResult.Lines.Add("⛔ 被擋下（零寫入）—— 原因與出口在回傳檔的 `## blocked`");
            return aPath;
        }

        // ② 需要 Editor 的那兩段：活著就整步交出去；沒開就照走、跳過那段
        var aSkipped = new List<string>();
        if (aPre.NeedsEditor.Length > 0)
        {
            bool aAlive = EditorAlive(iRoots.DataRoot, out string aWhy);
            if (aAlive)
            {
                ioResult.Lines.Add($"⤷ 這一步有一段要 Editor（{aPre.NeedsEditor}）；Editor 活著（{aWhy}）⇒ 整步交給 goodnight-{aStep}-editor");
                var aFwd = new Dictionary<string, string>(StringComparer.Ordinal) { ["persona"] = aPersona };
                foreach (string k in new[] { "project", "timeout", "summary", "skip_reason", "note", "no_token" })
                {
                    if (iNoLetter && (k == "summary" || k == "skip_reason")) continue;
                    string v = iArgs.Get(k);
                    if (v.Length > 0) aFwd[k] = v;
                }
                SCP_CmdResult aEd = SCP_CmdRegistry.Dispatch($"goodnight-{aStep}-editor", aFwd);
                foreach (string l in aEd.Lines) ioResult.Lines.Add("  │ " + l);
                foreach (var kv in aEd.Values) ioResult.AddValue(kv.Key, kv.Value);
                foreach (string o in aEd.Outputs) ioResult.AddOutput(o);
                ioResult.ExitCode = aEd.ExitCode;
                if (!aEd.Ok)
                    ioResult.Lines.Add("⚠ Editor 那一趟沒成功 —— ⛔ **不改走本地**：它可能稍後才執行（逾時＝不知道），"
                        + $"重複下線比晚一點下線糟。先看 `{aPath}` 與 lock 在不在再決定。");
                return null;
            }
            ioResult.Lines.Add($"⚠ 這一步有一段要 Editor（{aPre.NeedsEditor}），而 Editor 沒開（{aWhy}）⇒ 照走晚安，**只跳過那一段**");
            if (aPre.ActiveStreamWatchId.Length > 0)
                aSkipped.Add($"觀影場 `{aPre.ActiveStreamWatchId}` 沒結算、沒關 —— 到期後成為殘留，下次 StreamWatch start 或 "
                    + $"`senate cmd sessions --arg op=close --arg target_persona={aPersona} --arg confirm=1` 會補結算（付到 ends_at）");
            if (aPre.NeedsTaskSkipWrite)
                aSkipped.Add($"收工閘顯式跳過（{aPre.PendingWrapups.Count} 張：{string.Join("、", aPre.PendingWrapups.Select(t => t.Id))}）"
                    + $"—— 理由**沒寫進單子時間線**（Editor 沒開），改記在這裡與下線廣播：{aSkip}");
        }

        // ③ 寫入：刪 lock／now_status、組廣播
        SCP_GoodnightSleep aApply = SCP_Goodnight.SleepApply(iRoots, aPersona, iNoLetter, aPre);
        // ④ 關本人活動 session（觀影場不關 —— 見上）
        string aSessionLine = SCP_Goodnight.CloseOwnSessionNative(iRoots, aPersona, iNoLetter);

        // ⑤ 下線廣播（best-effort；token 在刪 lock 前就讀好了）
        string aSummary = iNoLetter ? "" : iArgs.Get("summary").Trim();
        string aBody = aApply.BroadcastBody.Replace("{SUMMARY}", aSummary.Length == 0 ? "" : $"💭 **今日心得**\n{aSummary}\n\n");
        if (!iNoLetter)
        {
            string? aPortraitSkip = SCP_Goodnight.PortraitSkipReasonToday(iRoots, aPersona);
            if (!string.IsNullOrEmpty(aPortraitSkip)) aBody += $"\n- 🖼 本夜未畫像，理由：{aPortraitSkip}";
            if (aPre.NeedsTaskSkipWrite) aBody += $"\n- 📋 收工閘顯式跳過（{aPre.PendingWrapups.Count} 張），理由：{aSkip}";
        }
        string aNote = iArgs.Get("note");
        if (aNote.Length > 0) aBody += $"\n- Note: {aNote}";
        bool aNoToken = iArgs.Get("no_token").ToLowerInvariant() == "true";
        var aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["tag"] = "goodnight-protocol", ["category"] = "meta", ["status-change"] = "offline" };
        string aBroadcastLine;
        SCP_TavernPostDraft aDraft = SCP_TavernPostCompose.Build(iRoots.DataRoot, iRoots.LettersRoot, iRoots.ProjectRoot,
            iRoots.Region, "tavern", aPersona, aBody, aMeta, aNoToken ? "" : (aApply.Token ?? ""));
        foreach (string n in aDraft.Notes) ioResult.Lines.Add("⚠ " + n);
        if (aDraft.Message == null)
            aBroadcastLine = $"未發（組訊息被拒：{aDraft.Error}）—— 核心已落地，同事看 lock 判在線";
        else
        {
            string aTimeout = iArgs.Get("timeout");
            SCP_CmdResult aW = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["data_root"] = iRoots.DataRoot, ["room"] = "tavern",
                ["msg_json"] = SCP_TavernWriter.Serialize(aDraft.Message),
                ["timeout"] = aTimeout.Length > 0 ? aTimeout : "30",
            });
            string aSeq = aW.Values.FirstOrDefault(kv => kv.Key == "seq").Value ?? "";
            string aFail = aW.Values.FirstOrDefault(kv => kv.Key == "delegate_failure").Value ?? "";
            aBroadcastLine = aW.Ok && aSeq.Length > 0 ? $"seq **{aSeq}**"
                : aFail == "timeout" || aFail == "unknown"
                    ? $"**不知道**有沒有發（delegate_failure={aFail}）—— ⛔ 別直接補發，先 `senate cmd tavern-query --arg kind=tail` 回讀"
                    : $"未發（{(aFail.Length > 0 ? "delegate_failure=" + aFail : "exit " + aW.ExitCode)}）—— 核心已落地，補發非必要（同事看 lock 判在線）";
            if (aSeq.Length > 0) ioResult.AddValue("post_seq", aSeq);
        }

        // ⑥ 作廢 token（在廣播之後 —— enforce ON 時廣播要帶活的 token）
        int aExpired = SCP_Goodnight.ExpireTokens(iRoots, aPersona, iNoLetter ? "logout" : "goodnight");

        var aSb = new System.Text.StringBuilder(aApply.Report);
        aSb.AppendLine();
        if (aSkipped.Count > 0)
        {
            aSb.AppendLine("## ⚠ 因 Editor 沒開而跳過的段（Tim 2026-09-26：不得卡住晚安）");
            foreach (string s in aSkipped) aSb.AppendLine("- " + s);
        }
        aSb.AppendLine("## verify（讀回的事實）");
        aSb.AppendLine($"- lock: exists={File.Exists(SCP.Core.Paths.SCP_LettersPaths.SessionLockPath(iRoots.Letters, aPersona))}（應為 False）");
        aSb.AppendLine($"- broadcast: {aBroadcastLine}");
        aSb.AppendLine(aExpired >= 0 ? $"- session_token expired: {aExpired} 筆" : "- session_token expired: **讀不到 _tokens.json**（⛔ 不是 0 筆）");
        aSb.AppendLine(aSessionLine);
        aSb.AppendLine("## next");
        aSb.AppendLine($"- 收工。明天醒來：senate cmd morning-wake --arg persona={aPersona}");
        if (!iNoLetter) aSb.AppendLine("- （可選）還想花錢再睡 → ucl-spending-time（消費時間不綁死晚安）");
        SCP_CmdPayload.Write(aPath, aSb.ToString());
        ioResult.Lines.Add($"✓ 已下線（廣播：{aBroadcastLine.Split('—')[0].Trim()}）" + (aSkipped.Count > 0 ? $"　⚠ 跳過 {aSkipped.Count} 段（見回傳檔）" : ""));
        return aPath;
    }
}

// ── Editor 路（只在「那兩段要 Editor 而 Editor 活著」時由上面自動轉派；也可以手動直打）──────────

/// <summary>晚安委派 Cmd 的共用殼（整步交給 Editor 的 `Cmd_GoodNight`）。</summary>
public abstract class GoodnightDelegateCmd : UnityDelegateCmd
{
    protected override string CliNextHint => "";

    protected static IEnumerable<SCP_CmdArgSpec> GoodnightSpecs()
    {
        yield return new SCP_CmdArgSpec("persona",
            "要對誰做這一步。⚠ **一律顯式** —— 猜錯的代價是把同事登出，而擾動過的 session 回不來", iRequired: true);
        foreach (SCP_CmdArgSpec aSpec in CommonSpecs()) yield return aSpec;
    }

    protected Dictionary<string, string> Forward(SCP_CmdArgs iArgs, string iStep, params string[] iKeys)
    {
        var a = new Dictionary<string, string> { ["step"] = iStep, ["persona"] = iArgs.Get("persona") };
        foreach (string k in iKeys)
        {
            string v = iArgs.Get(k);
            if (v.Length > 0) a[k] = v;   // 空值不送：沒給與給了空的在對面看起來一樣
        }
        return a;
    }

    protected sealed override string UnityCmdType => "GoodNight";
}

public sealed class Cmd_GoodnightSleepEditor : GoodnightDelegateCmd
{
    public override string Name => "goodnight-sleep-editor";
    public override string Summary => "晚安④下線的 Editor 路 —— 觀影場要結算／收工閘 skip 要寫進單子時，goodnight-sleep 會自動轉派到這裡";
    public override string Details => "整步交給 Unity Editor 的 Cmd_GoodNight（它有觀影結算與單子寫入端）。平常直接用 goodnight-sleep 即可。";
    public override string Example => SCP_CmdRegistry.Invoke("goodnight-sleep-editor --arg persona=Template");
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var a = new List<SCP_CmdArgSpec>(GoodnightSpecs());
            a.Add(new SCP_CmdArgSpec("summary", "公開的睡前心得（選填）"));
            a.Add(new SCP_CmdArgSpec("skip_reason", "跳過收工閘的理由（寫進那幾張單的時間線）"));
            a.Add(new SCP_CmdArgSpec("note", "附註（選填）"));
            a.Add(new SCP_CmdArgSpec("no_token", "=true ⇒ 廣播顯式不帶 session_token"));
            return a;
        }
    }
    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => Forward(iArgs, "sleep", "summary", "skip_reason", "note", "no_token");
}

public sealed class Cmd_GoodnightLogoutEditor : GoodnightDelegateCmd
{
    public override string Name => "goodnight-logout-editor";
    public override string Summary => "手動登出的 Editor 路 —— 有進行中觀影場要結算時，goodnight-logout 會自動轉派到這裡";
    public override string Details => "整步交給 Unity Editor 的 Cmd_GoodNight（它有觀影結算）。平常直接用 goodnight-logout 即可。";
    public override string Example => SCP_CmdRegistry.Invoke("goodnight-logout-editor --arg persona=Template");
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var a = new List<SCP_CmdArgSpec>(GoodnightSpecs());
            a.Add(new SCP_CmdArgSpec("note", "附註（選填）"));
            a.Add(new SCP_CmdArgSpec("no_token", "=true ⇒ 廣播顯式不帶 session_token"));
            return a;
        }
    }
    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => Forward(iArgs, "logout", "note", "no_token");
}
