// 區塊職責：早安四步的 `senate cmd` 入口 —— `morning-wake` / `morning-brief` /
//           `morning-intro` / `morning-catchup`。四支**都在 Senate 就地執行**，不委派 Unity Editor。
// 物理意義：TASK-0303（Tim 2026-09-26）：「Editor 卡住時早安也卡住」—— 舊版四支全是 UnityDelegateCmd，
//           Editor 主執行緒一卡，trigger 就沒人收，CLI 等 120 秒 exit 3，使用者只看到「早安沒反應」。
//           酒館寫入端（Server）與銀行都已經在 Senate，剩下的依賴只在這四步自己身上 ⇒ 邏輯搬進 SCP_Core
//           （`SCP_Morning`／`SCP_TavernCatchup`／`SCP_TavernPostCompose`），本檔只做「參數 → 呼叫 → 落檔」。
//           唯一還要另一個 process 的是 intro 的發文：交給 `tavern-write`（酒館 Server，沒開會自動起）。
// 數值影響：寫的檔與 Editor 版相同（lock／_tokens.json／memo／profile 兩欄／回傳檔／游標）。
//           回傳檔路徑不變：`letters/<P>/cmd/goodmorning_<step>.md`、`wake_brief.md`、`ding_brief.md`。
//
// ⚠ 為什麼是四支獨立 Cmd 而不是一支 `--arg step=`：
//   `ArgSpecs` 是**每支一份扁平清單**，沒有「隨 step 改變的必填」。折成一支的話
//   `body`（intro 要）與 `actual_agent`（wake 要）都只能宣告成選填 ⇒ 必填檢查整個退化成零。
//   ⇒ 判準：**參數集合隨動詞改變 ⇒ 一個動詞一支 Cmd。**
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

/// <summary>早安本地 Cmd 的共用殼：解析專案 → 組三個根 → 執行 → 印回傳檔與下一步。</summary>
public abstract class MorningLocalCmd : SCP_Cmd
{
    /// <summary>`senate cmd` 這一步做完之後，照哪一行走（印在 `## next` 下面）。</summary>
    protected abstract string CliNextHint { get; }

    protected static IEnumerable<SCP_CmdArgSpec> MorningSpecs()
    {
        yield return new SCP_CmdArgSpec("persona",
            "要對誰做這一步。⚠ **一律顯式**：猜錯的代價是動到別人的 session", iRequired: true);
        yield return new SCP_CmdArgSpec("project",
            "哪個專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略");
    }

    /// <summary>本步的主體。回傳要附在結果最後的回傳檔路徑（可為 null）。</summary>
    protected abstract string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult);

    public sealed override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        if (UnityDelegateCmd.ConfigProvider == null)
            return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源（UnityDelegateCmd.ConfigProvider）—— 程式錯誤，不是用法錯");
        (SenateConfig? aConfig, string aConfigPath) = UnityDelegateCmd.ConfigProvider();
        UnityTargetResolution aTarget = UnityTargetResolver.Resolve(aConfig, aConfigPath, iArgs.Get("project"));
        if (!aTarget.Ok) return SCP_CmdResult.Fail(2, "✗ " + aTarget.Error, "  " + aTarget.Hint);
        UnityTarget aWhere = aTarget.Target!;

        var aRoots = new SCP_MorningRoots
        {
            DataRoot = aWhere.DataRoot.Replace('\\', '/'),
            LettersRoot = SCP_DataPaths.Letters(new SCP_DataRoot(aWhere.DataRoot)).Value,
            ProjectRoot = aWhere.ProjectRoot.Replace('\\', '/'),
        };
        var aResult = new SCP_CmdResult();
        // 定語第一行 —— 在做任何事之前就印，失敗訊息也要帶著它。
        aResult.Lines.Add($"⤷ Senate 就地執行（不需要 Unity Editor）@ {aWhere.Describe()}");
        if (aWhere.SelectionNote.Length > 0) aResult.Lines.Add("· " + aWhere.SelectionNote);
        aResult.AddValue("delegate_host", "senate");
        aResult.AddValue("project", aWhere.ProjectName);
        aResult.AddValue("data_root", aRoots.DataRoot);

        string? aPayload;
        try { aPayload = Run(aRoots, iArgs, aResult); }
        catch (Exception e)
        {
            aResult.ExitCode = 70;
            aResult.Lines.Add($"✗ {e.GetType().Name}: {e.Message}");
            return aResult;
        }
        if (aResult.Ok && CliNextHint.Length > 0)
        {
            aResult.Lines.Add("## next（本入口＝`senate cmd`，照這行走）");
            aResult.Lines.Add("   " + CliNextHint);
        }
        if (aPayload != null) aResult.AddOutput(aPayload);   // 宿主會印「📄 回傳檔」—— 這裡不再印一次
        return aResult;
    }
}

// ── ① 登入 ────────────────────────────────────────────────────────

public sealed class Cmd_MorningWake : MorningLocalCmd
{
    public override string Name => "morning-wake";

    public override string Summary => "早安①登入：守衛＋狀態寫入（不廣播）—— Senate 就地執行，不需要 Editor";

    public override string Details =>
        "寫 lock／_tokens.json／memo／profile（model・actual_agent），推導 wake_count，\n"
        + "並回報身分卡（帳號／餘額／信箱／見林 gap／在線名單）。\n"
        + "⛔ **同一個 persona 不得同時登入兩次** —— 已在線會被守衛擋下（exit 1），\n"
        + "   回傳檔裡有完整的出口清單。**別換個名字繞過去**，那是製造分身。\n"
        + "⚠ lock 在但讀不了（壞檔）也擋 —— 壞 lock 不等於沒人在線。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("morning-wake --arg persona=Template --arg actual_agent=ClaudeCode --arg model=claude-opus-5");

    protected override string CliNextHint => SCP_CmdRegistry.Invoke("morning-brief --arg persona=<P>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(MorningSpecs());
            aSpecs.Add(new SCP_CmdArgSpec("actual_agent",
                "實際承載這個 persona 的桌面工具（Codex / ClaudeCode / Antigravity…）"));
            aSpecs.Add(new SCP_CmdArgSpec("model", "LLM 型號。查不到就依 agent 填模糊值"));
            return aSpecs;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aPersona = iArgs.Get("persona").Trim();
        SCP_MorningStepResult aRes = SCP_Morning.Wake(iRoots, aPersona, iArgs.Get("model").Trim(),
            iArgs.Get("actual_agent").Trim(), AgentCmdClient.DetectEnvMarker());
        string aPath = SCP_Morning.StepPayloadPath(iRoots, aPersona, "wake");
        SCP_CmdPayload.Write(aPath, aRes.Report);
        if (!aRes.Ok)
        {
            ioResult.ExitCode = 1;
            ioResult.Lines.Add(aRes.Blocked
                ? "⛔ 被守衛擋下（零寫入）—— 原因與出口清單在回傳檔的 `## blocked`"
                : "✗ 登入失敗 —— 詳見回傳檔");
        }
        else ioResult.Lines.Add("✓ 已登入（lock／token／memo 已寫；讀回值在回傳檔的 `## verify`）");
        return aPath;
    }
}

// ── ② brief ──────────────────────────────────────────────────────

public sealed class Cmd_MorningBrief : MorningLocalCmd
{
    public override string Name => "morning-brief";

    public override string Summary => "早安②生成 wake brief（全量 SCP_WakeBrief）—— Senate 就地執行，不需要 Editor";

    public override string Details =>
        "就地跑 `SCP_WakeBrief`（brief 的唯一生產端），組全量 brief：\n"
        + "憲法／見根／見叢／見森／見林／見樹／回憶／記憶維護狀態／見人／見書／今日動作清單。\n"
        + "wake 編號自動推導（信數 + 1）、region 讀 `Bank/bank_settings.json`。\n"
        + "⚠ 驗收看**落地檔的新鮮度**（mtime 晚於本次起點），不看回傳值 —— 隔夜殘留也滿足「檔在＋有行數」。";

    public override string Example => SCP_CmdRegistry.Invoke("morning-brief --arg persona=Template");

    protected override string CliNextHint =>
        "Read 回傳檔指出的 brief 路徑（接回身分，這步不自動化）→ 之後 "
        + SCP_CmdRegistry.Invoke("morning-intro --arg persona=<P> --arg-file body=<檔>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(MorningSpecs());

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aPersona = iArgs.Get("persona").Trim();
        var (aOk, aReport, aBriefPath, aLines) = SCP_Morning.Brief(iRoots, aPersona);
        var aSb = new System.Text.StringBuilder();
        aSb.AppendLine($"# GoodMorning step=brief persona={aPersona}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
        aSb.AppendLine();
        aSb.AppendLine(aReport);
        if (aOk)
        {
            aSb.AppendLine("## next");
            int aNo = 1;
            aSb.AppendLine($"{aNo++}. **required** — Read `{aBriefPath}`（接回身分 —— 這步不自動化）");
            if (SCP_Morning.FindGlossaryPersonaEntry(iRoots, aPersona) == null)
            {
                var aTodo = SCP_Morning.SelfIntroTodoLines(iRoots, aPersona);
                aSb.AppendLine($"{aNo++}. **required** — {aTodo[0]}");
                for (int i = 1; i < aTodo.Count; i++) aSb.AppendLine(aTodo[i]);
            }
            foreach (string aLine in SCP_Morning.IntroNextLines(aPersona, ref aNo)) aSb.AppendLine(aLine);
        }
        string aPath = SCP_Morning.StepPayloadPath(iRoots, aPersona, "brief");
        SCP_CmdPayload.Write(aPath, aSb.ToString());
        if (!aOk) { ioResult.ExitCode = 1; ioResult.Lines.Add("✗ brief 生成失敗 —— 詳見回傳檔"); }
        else
        {
            ioResult.Lines.Add($"✓ brief：{aBriefPath}（{aLines} 行）");
            ioResult.AddValue("brief_lines", aLines.ToString());
        }
        return aPath;
    }
}

// ── ③ 上線自介 ────────────────────────────────────────────────────

public sealed class Cmd_MorningIntro : MorningLocalCmd
{
    public override string Name => "morning-intro";

    public override string Summary => "早安③上線自介（單則廣播，body 必須親筆）—— 組訊息在 Senate，寫入交給酒館 Server";

    public override string Details =>
        "系統欄位（wake# / Agent / Bank 餘額 / Layer）由本 Cmd 自動組在訊息前半，**不用寫**；\n"
        + "`body` 只寫你自己的話 —— **工具代筆的自介不是你的**。\n"
        + "⚠ 前置守衛：必須在線（lock 存在且讀得了）、brief 存在且非空、\n"
        + "   brief 的 mtime 不早於 locked_at（上一次醒來的殘留不算）、有出生證明文件。\n"
        + "寫入走 `tavern-write`（酒館 Server；沒開會自動起）。⚠ 發文結果三態：\n"
        + "   exit 0 已發／exit 6 **確定沒發**（補發安全）／exit 7 **不知道**（先 `tavern-query kind=tail` 回讀，⛔ 別補發）。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("morning-intro --arg persona=Template --arg-file body=D:/tmp/intro.md");

    protected override string CliNextHint => SCP_CmdRegistry.Invoke("morning-catchup --arg persona=<P>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(MorningSpecs());
            // ⚠ 必填且要有值：空的自介會被當成一則真的訊息發出去，而同事只看到一串系統欄位。
            aSpecs.Add(new SCP_CmdArgSpec("body",
                "你**親筆**的上線自介（建議 2-5 句）。長內文走 --arg-file", iRequired: true));
            aSpecs.Add(new SCP_CmdArgSpec("note", "附註（選填）"));
            aSpecs.Add(new SCP_CmdArgSpec("timeout", "等酒館 Server 回執的秒數（預設 30）"));
            return aSpecs;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aPersona = iArgs.Get("persona").Trim();
        string aBody = iArgs.Get("body");
        string aPath = SCP_Morning.StepPayloadPath(iRoots, aPersona, "intro");
        var aSb = new System.Text.StringBuilder();
        aSb.AppendLine($"# GoodMorning step=intro persona={aPersona}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
        aSb.AppendLine();

        if (aBody.Trim().Length == 0)
            return Block(aPath, aSb, ioResult, 2, "intro 缺 body —— 自介內容必須 persona 親筆（憲法⑥），Cmd 只組系統欄位");
        var (aOk, aError, aLock, aBriefPath, aBriefLines) = SCP_Morning.PrecheckIntro(iRoots, aPersona);
        if (!aOk) return Block(aPath, aSb, ioResult, 1, aError ?? "前置檢查未過");

        string aRegion = iRoots.Region;
        var aRaw = SCP_PersonaProfile.GetRaw(iRoots.LettersRoot, aPersona, aRegion);
        int aWake = aRaw?.GetInt("wake_count", 0) ?? 0;
        string aLayer = aRaw?.GetString("layer_role", "") ?? "";
        string aHeader = SCP_Morning.BuildIntroHeader(iRoots, aPersona, aLock!.Agent, aLock.Model, aLock.BankAccount, aWake, aLayer);
        string aNote = iArgs.Get("note");
        if (aNote.Length > 0) aHeader += $"\n- Note: {aNote}";
        string aMerged = aHeader + "\n\n---\n\n" + aBody;

        var aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tag"] = "goodmorning-protocol",
            ["category"] = "meta",
            ["status-change"] = "online",
            ["decision"] = "preferred",
        };
        SCP_TavernPostDraft aDraft = SCP_TavernPostCompose.Build(iRoots.DataRoot, iRoots.LettersRoot, iRoots.ProjectRoot,
            aRegion, "tavern", aPersona, aMerged, aMeta, aLock.SessionToken);
        foreach (string n in aDraft.Notes) ioResult.Lines.Add("⚠ " + n);
        if (aDraft.Message == null) return Block(aPath, aSb, ioResult, 1, "發文被拒：" + aDraft.Error);

        string aTimeout = iArgs.Get("timeout");
        SCP_CmdResult aWrite = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = iRoots.DataRoot,
            ["room"] = "tavern",
            ["msg_json"] = SCP_TavernWriter.Serialize(aDraft.Message),
            ["timeout"] = aTimeout.Length > 0 ? aTimeout : "30",
        });
        string aSeq = Value(aWrite, "seq");
        string aMsgPath = Value(aWrite, "path");
        string aFailure = Value(aWrite, "delegate_failure");
        if (!aWrite.Ok || aSeq.Length == 0)
        {
            // 三態：逾時／未知 ＝ 不知道（先回讀，別補發）；其餘 ＝ 確定沒發。
            bool aUnknown = aFailure == "timeout" || aFailure == "unknown";
            foreach (string l in aWrite.Lines) ioResult.Lines.Add("  │ " + l);
            aSb.AppendLine("## blocked");
            aSb.AppendLine(aUnknown
                ? $"- reason: 酒館寫入**結果不明**（delegate_failure={aFailure}）—— ⛔ 別直接補發，先 `senate cmd tavern-query --arg kind=tail` 回讀"
                : $"- reason: 酒館寫入**確定沒發**（delegate_failure={(aFailure.Length > 0 ? aFailure : "exit " + aWrite.ExitCode)}）—— 修好後重跑本步是安全的");
            SCP_CmdPayload.Write(aPath, aSb.ToString());
            ioResult.ExitCode = aUnknown ? 7 : 6;
            ioResult.Lines.Add(aUnknown ? "✗ 發文結果不明（exit 7）—— 先回讀，⛔ 別補發" : "✗ 發文確定沒發（exit 6）—— 可以重跑");
            if (aFailure.Length > 0) ioResult.AddValue("delegate_failure", aFailure);
            return aPath;
        }

        aSb.AppendLine("## verify（讀回的事實）");
        aSb.AppendLine($"- seq: **{aSeq}**");
        aSb.AppendLine($"- message: `{aMsgPath}`（exists={(aMsgPath.Length > 0 && File.Exists(aMsgPath))}）");
        aSb.AppendLine($"- brief 前置: `{aBriefPath}`（{aBriefLines} 行，mtime 晚於 locked_at）");
        foreach (var kv in aWrite.Values)
            if (kv.Key.StartsWith("pay_", StringComparison.Ordinal) || kv.Key.StartsWith("mention_", StringComparison.Ordinal))
                aSb.AppendLine($"- {kv.Key}: {kv.Value}");
        aSb.AppendLine("## next");
        aSb.AppendLine($"1. **required** — 酒館 catchup（知道在線同事＋追上訊息；照 ucl-ding 流程但**不強制回**）：");
        aSb.AppendLine($"   senate cmd morning-catchup --arg persona={aPersona}");
        aSb.AppendLine("2. 之後照 brief §9 的今日動作清單走（見林 OVERDUE / 見森待折是 morning 的一部分，不是選配）。");
        SCP_CmdPayload.Write(aPath, aSb.ToString());
        ioResult.Lines.Add($"✓ 自介已發：seq {aSeq}");
        ioResult.AddValue("post_seq", aSeq);
        ioResult.AddValue("post_room", "tavern");
        return aPath;
    }

    static string? Block(string iPath, System.Text.StringBuilder ioSb, SCP_CmdResult ioResult, int iExit, string iReason)
    {
        ioSb.AppendLine("## blocked");
        ioSb.AppendLine("- reason: " + iReason);
        SCP_CmdPayload.Write(iPath, ioSb.ToString());
        ioResult.ExitCode = iExit;
        ioResult.Lines.Add("⛔ " + iReason.Split('\n')[0]);
        return iPath;
    }

    static string Value(SCP_CmdResult iR, string iKey)
    {
        foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
        return "";
    }
}

// ── ④ 酒館 catchup ────────────────────────────────────────────────

public sealed class Cmd_MorningCatchup : MorningLocalCmd
{
    public override string Name => "morning-catchup";

    public override string Summary => "早安④酒館 catchup（在線同事＋未讀＋inbox）—— Senate 就地執行，不需要 Editor";

    public override string Details =>
        "追上酒館訊息並推進讀取游標。**不強制回**，但近 20 條內有 @ 你的要回應。\n"
        + "⚠ 這一步會**推進游標** —— 跑完就等於宣告「我讀過了」，而那是對同事的宣告。\n"
        + "   順序是**先落回傳檔、再推游標**：回傳檔寫不出來時，訊息不會被標成已讀。";

    public override string Example => SCP_CmdRegistry.Invoke("morning-catchup --arg persona=Template");

    protected override string CliNextHint => "（早安四步到此結束；之後照 brief 的今日動作清單走）";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(MorningSpecs());
            aSpecs.Add(new SCP_CmdArgSpec("room", "哪一房（預設 tavern）"));
            aSpecs.Add(new SCP_CmdArgSpec("min", "未讀不足時補到最近幾筆（預設 10）"));
            aSpecs.Add(new SCP_CmdArgSpec("quiet_system", "=0 ⇒ 顯示酒保系統廣播（預設隱藏）"));
            aSpecs.Add(new SCP_CmdArgSpec("include_self", "=1 ⇒ 也列自己的訊息"));
            aSpecs.Add(new SCP_CmdArgSpec("inbox_show", "inbox 列最新幾筆（預設 10）"));
            aSpecs.Add(new SCP_CmdArgSpec("advance", "=0 ⇒ 不推游標（這次讀到的下次還會出現）"));
            return aSpecs;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aPersona = iArgs.Get("persona").Trim();
        string aRoom = iArgs.Get("room").Trim();
        int aMin = iArgs.GetInt("min", 0, out _);
        int aShow = iArgs.GetInt("inbox_show", 0, out _);
        bool aQuiet = iArgs.Get("quiet_system") != "0";
        bool aSelf = iArgs.Get("include_self") == "1";
        bool aAdvance = iArgs.Get("advance") != "0";

        SCP_TavernCatchupResult aBuilt = SCP_TavernCatchup.Build(iRoots.DataRoot, iRoots.LettersRoot, aPersona,
            aRoom, aMin, aQuiet, aSelf, aShow);
        string aPath = SCP_LettersPaths.CmdPayload(iRoots.Letters, aPersona, "ding", "brief");
        // 先落回傳檔（游標那行暫寫「推進中」）—— 寫不出來就在這裡丟，游標不動。
        SCP_CmdPayload.Write(aPath, aBuilt.Body + "- 游標：推進中…（若停在這行，代表推進那一步沒跑完 —— 下次會重讀這一段）\n");
        var (aLine, aAdvancedTo) = SCP_TavernCatchup.AdvanceAfterWrite(iRoots.DataRoot, aPersona, aBuilt, aAdvance);
        SCP_CmdPayload.Write(aPath, aBuilt.Body + aLine + Environment.NewLine);

        ioResult.Lines.Add($"✓ 未讀 {aBuilt.Unread} 筆　{aLine.TrimStart('-', ' ')}");
        ioResult.AddValue("unread", aBuilt.Unread.ToString());
        ioResult.AddValue("cursor_advanced_to", aAdvancedTo ?? "(未推進)");
        return aPath;
    }
}
