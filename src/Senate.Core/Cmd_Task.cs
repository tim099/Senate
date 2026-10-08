// 區塊職責：`senate cmd task` —— 任務單**寫入**的入口（TASK-0349）。讀取（list／show／kanban）走 `senate cmd tasks`。
// 物理意義：Tim 2026-09-30「349 全包 GO」：任務單寫入整格搬到 Senate —— 形狀照酒館那條走過的路
//           （`tavern-post` 組訊息 → `tavern-write` 寫入）：
//             ① 本入口：驗參數（每個 op 的必填＋白名單）、claim 帶 scope 時先開 Coding 場
//             ② `task-write`（Senate Server，唯一寫入端）：配號、讀改寫、閘與狀態機（`SCP_TaskOps`）
//             ③ 本入口：落回傳檔、發酒館通知（`SCP_ITavernPostGateway`）、wrapup 的 why 寫進工作記憶（`SCP_WorkMemory`）
//           ⇒ `senate cmd commit` 推單、晚安寫 skip、Unity Editor（`Cmd_Task` 寫入 op／後台頁）全部走這一支，
//             **Editor 關著也能寫單**。
// 數值影響：一次呼叫 ＝ 一次 Server round-trip ＋ 每則通知一次酒館寫入 ＋（有 why 時）一筆工作記憶。
//           回傳檔 `letters/<P>/cmd/task_<op>.md`（與 Unity 版同一個落點 —— 讀的人不必知道是誰寫的）。
//
// ⚠ 結果四態（照 `tavern-post`）：exit 0 已寫（或 dry-run／冪等零寫入，看 `🔢 wrote`）／
//   exit 1 被閘擋下（**零寫入**，原因在回傳檔）／exit 6 **確定沒寫**（寫入端沒收到，重跑安全）／
//   exit 7 **不知道**（送出了但等不到回執）⇒ ⛔ 先 `senate cmd tasks --arg index=<n>` 回讀，別直接重打。
// ⚠ 寫完之後的附帶效果（通知、記憶）失敗**只是警告**：單子已經寫好了，⛔ 不能讓 exit code 說「失敗」
//   （照直覺重打會再寫一次 —— 留言多一則、勾選多一格）。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SCP.Core.Cmd;
using SCP.Core.WorkMemory;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Session;
using SCP.Core.Tasks;

namespace Senate.Core;

public sealed class Cmd_Task : SCP_Cmd
{
    public override string Name => "task";
    public override string Category => SCP_CmdCategory.Task;

    public override string Summary =>
        "任務單寫入（create／claim／assign／unassign／update／comment／check／link／resolve／commit／sweep／wrapup）"
        + "—— 寫入端是 Senate Server；讀取走 `tasks`";

    public override string Details =>
        "打錯參數名 ⇒ exit 2 並列出該 op 認得的鍵（⛔ 不靜默吃掉）。\n"
        + "⚠ 結果四態：0 已寫（dry-run／冪等看 🔢 wrote）／1 閘擋下（零寫入）／6 確定沒寫（重跑安全）／7 **不知道**（⛔ 先回讀）。\n"
        + "⚠ 長內文（criteria／description／evidence／body／progress／why）一律 `--arg-file`。\n"
        + "⚠ claim 帶 `scope` ＝「我現在要動工」：先開 Coding 場（或綁到現有那一場），開不了就**不認領**。\n"
        + "⚠ 寫完才發酒館通知與寫工作記憶 —— 那兩件失敗只是警告，單子已經寫好了（⛔ 別因此重打）。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("task --arg op=comment --arg persona=Template --arg index=349 --arg-file body=D:/tmp/c.md");

    // 參數聯集（每個 op 認得哪幾格在 SCP_TaskOps.Known；這裡宣告全部 ⇒ CLI 的未知參數預檢擋得住拼錯的名字）
    static readonly (string name, string desc)[] OpArgs =
    {
        ("index", "單號（收 TASK-0008 / 8 / 0008）"),
        ("title", "標題（create 必填；update 改寫）"),
        ("type", "feature|improvement|refactor|spike|subtask|bug|epic（create；預設 feature）"),
        ("priority", "urgent|high|normal|low"),
        ("severity", "none|blocking|wrong|annoying（bug 預設 wrong）"),
        ("status", "create／update 設定值；resolve 用 done|cancelled"),
        ("criteria", "驗收標準（create 必填、bug 可省；update ＝ **整段覆寫**）。走 --arg-file"),
        ("description", "任務描述（update ＝ 整段覆寫）。走 --arg-file"),
        ("evidence", "bug 單的硬證＋讀數怎麼拿到的（type=bug create 必填）。走 --arg-file"),
        ("milestone", "里程碑"),
        ("epic_id", "父單（create；之後改用 op=link subtask_of）"),
        ("tags", "逗號分隔"),
        ("memory_topic", "工作記憶主題"),
        ("memory_archived_commit", "update：記憶歸檔後的 commit sha"),
        ("unset", "update：顯式清空欄位（memory_topic / memory_archived_commit / milestone，逗號分隔）"),
        ("allow_shrink", "update：criteria／description 整段縮水時的顯式放行（1）"),
        ("role", "dev|design|qa|pm|reviewer|sound|art（claim／assign 預設 dev）"),
        ("scope", "claim：施工範圍（絕對路徑，多段用 |）—— 給了＝認領＋開 Coding 場＋綁單"),
        ("target_persona", "assign／unassign 的對象"),
        ("replace", "assign：1 ＝ 換角色（先拿掉這個人既有的其他角色）"),
        ("body", "comment 內容。走 --arg-file"),
        ("criteria_index", "check：**未勾清單**的 1-based 序號（逗號分隔）；不帶 ＝ dry-run"),
        ("expect_text", "check：序號那一行的前綴（多筆用 | 分隔）—— 序號會位移、文字不會"),
        ("target", "link 的對方單號"),
        ("op_link", "link：blocked_by|blocks|subtask_of|has_subtask|related_to（預設 blocked_by）"),
        ("remove", "link：1 ＝ 解除該關聯"),
        ("note", "resolve 的結單說明"),
        ("qa_note", "resolve：代 QA 結單時的驗收紀錄"),
        ("confirm", "resolve／sweep：1 才真的寫"),
        ("sha", "commit：commit SHA"),
        ("mode", "commit：fixes|refs（預設 fixes —— 會推狀態）"),
        ("assignee", "sweep：只看某人參與的單"),
        ("progress", "wrapup：還剩什麼、下一步從哪接（必填）。走 --arg-file"),
        ("why", "wrapup：為什麼卡住／試過什麼不行 ⇒ 寫進工作記憶（選填）。走 --arg-file"),
        ("memory_type", "wrapup 的 why：pitfall|decision|knowhow（預設 pitfall）"),
        ("reason", "wrapup_skip：跳過收工的理由（晚安用）"),
    };

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("op", "寫入 op", iRequired: true, iChoices: SCP_TaskOps.WriteOps),
                new SCP_CmdArgSpec("persona", "動手的人。⚠ **一律顯式**（時間線與署名是它）", iRequired: true),
                new SCP_CmdArgSpec("data_root", "直接指定 AgentCommands 資料根（給了就不解析 project；commit／晚安這類已經知道資料根的呼叫端用）"),
                new SCP_CmdArgSpec("timeout", "等任務寫入端回執的秒數（預設 60）"),
            };
            foreach (var (n, d) in OpArgs) aSpecs.Add(new SCP_CmdArgSpec(n, d));
            return aSpecs;
        }
    }

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aOp = iArgs.Get("op").Trim();
        string aPersona = iArgs.Get("persona").Trim();
        if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ persona 是空的 —— 不知道要署誰的名（⛔ 不猜）");

        // ── ① 每個 op 的參數閘（寫入端的 Known／Required 是同一份表）──────────
        var aOpArgs = new Dictionary<string, string>(StringComparer.Ordinal);
        var aUnknown = new List<string>();
        string[] aKnown = SCP_TaskOps.Known[aOp];
        foreach (var (n, _) in OpArgs)
        {
            if (!iArgs.IsExplicit(n)) continue;
            if (Array.IndexOf(aKnown, n) < 0) { aUnknown.Add(n); continue; }
            aOpArgs[n] = iArgs.Get(n);
        }
        if (aUnknown.Count > 0)
            return SCP_CmdResult.Fail(2,
                $"✗ op={aOp} 不吃這些參數：{string.Join(", ", aUnknown)}　—— ⛔ 不靜默吃掉（🩸 打錯的參數名被丟掉 ⇒ 走預設 ⇒ 做了另一件事）",
                $"  op={aOp} 認得的：{(aKnown.Length == 0 ? "（無）" : string.Join(", ", aKnown))}");
        var aMissing = SCP_TaskOps.Required[aOp].Where(k => !aOpArgs.TryGetValue(k, out string? v) || v.Trim().Length == 0).ToList();
        if (aMissing.Count > 0)
            return SCP_CmdResult.Fail(2, $"✗ op={aOp} 缺必填：{string.Join(", ", aMissing)}（長內文走 --arg-file）");

        // ── ② 資料根 ───────────────────────────────────────────────
        if (!TryResolveRoots(iArgs, out string aDataRoot, out string aWhere, out SCP_CmdResult? aFail))
            return aFail!;
        var aResult = new SCP_CmdResult();
        aResult.Lines.Add($"⤷ 任務寫入：Senate 入口驗參數 → Senate Server 寫單 @ {aWhere}");
        var aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(aDataRoot));
        string aPayload = SCP_LettersPaths.CmdPayload(aLetters, aPersona, "task_" + aOp);

        // ── ③ claim 帶 scope ⇒ 先開場（開不了就不認領）──────────────
        var aPre = new List<string>();
        bool aStartedSession = false;
        if (aOp == "claim" && aOpArgs.TryGetValue("scope", out string? aScope) && aScope.Trim().Length > 0)
        {
            int aIdx = SCP_TaskStore.ParseTaskRef(aOpArgs["index"]);
            if (aIdx <= 0) return SCP_CmdResult.Fail(2, $"✗ index 認不得：'{aOpArgs["index"]}'（收 TASK-0008 / 8 / 0008）");
            if (!OpenCodingSession(aDataRoot, aPersona, aIdx, aScope.Trim(), aPre, out aStartedSession))
            {
                var aBlock = new StringBuilder();
                aBlock.AppendLine($"# Task op=claim persona={aPersona}");
                aBlock.AppendLine();
                aBlock.AppendLine("## ⛔ 沒有認領 —— 開場被擋");
                foreach (string l in aPre) aBlock.AppendLine(l);
                aBlock.AppendLine();
                aBlock.AppendLine("⚠ **認領一個位元組都沒寫** —— 帶了 `scope` 的意思是「我現在要動工」，而動不了。只想記錄「我在做這件事」就拿掉 `--arg scope=` 再跑一次。");
                SCP_CmdPayload.Write(aPayload, aBlock.ToString());
                aResult.ExitCode = 1;
                aResult.Lines.Add("⛔ 沒有認領 —— 開場被擋（零寫入）");
                aResult.Lines.AddRange(aPre.Select(l => "  " + l));
                aResult.AddValue("wrote", "0");
                return aResult.AddOutput(aPayload);
            }
        }

        // ── ④ 交給寫入端 ─────────────────────────────────────────────
        string aTimeout = iArgs.Get("timeout").Trim();
        SCP_CmdResult aWrite = SCP_CmdRegistry.Dispatch("task-write", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = aDataRoot,
            ["op"] = aOp,
            ["persona"] = aPersona,
            ["args_json"] = JsonSerializer.Serialize(aOpArgs),
            ["timeout"] = aTimeout.Length > 0 ? aTimeout : "60",
        });
        string aWrote = Value(aWrite, "wrote");
        string aFailure = Value(aWrite, "delegate_failure");
        if (aWrote.Length == 0)
        {
            // 寫入端**沒有回一個判定**（它的判定一定帶 wrote）⇒ 三態的後兩態：確定沒寫／不知道
            bool aUnknownOutcome = aFailure == "timeout" || aFailure == "unknown";
            if (aStartedSession && !aUnknownOutcome) aPre.Add(RollbackCodingSession(aDataRoot, aPersona));
            aResult.ExitCode = aUnknownOutcome ? 7 : 6;
            aResult.Lines.Add(aUnknownOutcome
                ? $"✗ 任務寫入**結果不明**（delegate_failure={aFailure}）—— ⛔ 別直接重打，先回讀：{SCP_CmdRegistry.Invoke("tasks --arg index=<單號>")}"
                : $"✗ 任務寫入**確定沒寫**（delegate_failure={(aFailure.Length > 0 ? aFailure : "exit " + aWrite.ExitCode)}）—— 修好後重跑是安全的");
            foreach (string l in aWrite.Lines) aResult.Lines.Add("  │ " + l);
            aResult.Lines.AddRange(aPre.Select(l => "  " + l));
            if (aFailure.Length > 0) aResult.AddValue("delegate_failure", aFailure);
            return aResult;
        }

        // 寫入端的回報（它的第一行是定語，其餘是 markdown 回傳檔本體；委派層會加兩格縮排）
        var aReport = new StringBuilder();
        foreach (string l in aWrite.Lines)
        {
            string t = l.StartsWith("  ", StringComparison.Ordinal) ? l.Substring(2) : l;
            if (t.StartsWith("⤷ 任務單寫入端", StringComparison.Ordinal)) { aResult.Lines.Add(t); continue; }
            if (t.StartsWith("⏱ ", StringComparison.Ordinal)) continue;
            aReport.AppendLine(t);
        }
        foreach (string l in aPre) aReport.AppendLine(l);
        aResult.ExitCode = aWrite.ExitCode;
        if (aWrite.ExitCode != 0 && aStartedSession) { string rb = RollbackCodingSession(aDataRoot, aPersona); aReport.AppendLine(rb); aResult.Lines.Add(rb); }
        aResult.Lines.Add(Value(aWrite, "headline"));
        foreach (var kv in aWrite.Values)
            if (kv.Key is "wrote" or "index" or "comment_id" or "status" or "from_status" or "heal_attempts")
                aResult.AddValue(kv.Key, kv.Value);

        // ── ⑤ 寫完之後：通知、記憶（失敗只是警告）──────────────────
        if (aWrite.ExitCode == 0)
        {
            PostNotices(aDataRoot, aPersona, Value(aWrite, "notices_json"), aReport, aResult);
            RunMemory(aDataRoot, Value(aWrite, "memory_json"), aReport, aResult);
        }
        try { SCP_CmdPayload.Write(aPayload, aReport.ToString()); aResult.AddOutput(aPayload); }
        catch (Exception e) { aResult.Lines.Add($"⚠ 回傳檔沒寫成（{e.Message}）—— 單子的寫入不受影響"); }
        if (aWrite.ExitCode != 0) aResult.Lines.Add("  ⇒ 原因在回傳檔的 `## ❌ 失敗`／`## blocked`（零寫入）");
        return aResult;
    }

    // ===========================================================
    // 區塊職責：資料根 —— 給了 `data_root` 就直接用；沒給 ⇒ **唯一入口** `SenatePathBinding.ResolveDataRoot`（TASK-0390）。
    // ===========================================================
    static bool TryResolveRoots(SCP_CmdArgs iArgs, out string oDataRoot, out string oWhere, out SCP_CmdResult? oFail)
    {
        oDataRoot = ""; oWhere = ""; oFail = null;
        string aExplicit = iArgs.Get("data_root").Trim();
        if (aExplicit.Length > 0)
        {
            oDataRoot = aExplicit.Replace('\\', '/').TrimEnd('/');
            if (!Directory.Exists(oDataRoot)) { oFail = SCP_CmdResult.Fail(2, "✗ data_root 不存在：" + oDataRoot); return false; }
            oWhere = "資料根 " + oDataRoot;
            return true;
        }
        if (SenateConfigSource.Provider == null)
        { oFail = SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源（SenateConfigSource.Provider）—— 程式錯誤，不是用法錯"); return false; }
        (SenateConfig? aConfig, string aConfigPath) = SenateConfigSource.Provider();
        string? aRoot = SenatePathBinding.ResolveDataRoot(aConfig, out string? aErr);
        if (aRoot == null)
        { oFail = SCP_CmdResult.Fail(2, "✗ 資料根解不出來：" + aErr, "  到 `senate ui` 的「路徑管理」頁設定 AgentCommands 資料根（" + aConfigPath + "）"); return false; }
        oDataRoot = aRoot.Replace('\\', '/').TrimEnd('/');
        oWhere = "資料根 " + oDataRoot + "（設定檔）";
        return true;
    }

    // ===========================================================
    // 區塊職責：claim 帶 scope ⇒ 開一場新的，或綁到現有那一場（UCL `TryStartOrBindCodingSession` 的搬家）。
    // 物理意義：每人一場 ⇒ 已經有場的人**補綁**，⛔ 不開第二場；⛔ 也不自動擴大現有場的範圍
    //           （靜默擴大會擋掉別人，而被擋的人看到的是一個我從來沒宣告過的路徑）。
    //           開新場走 `coding op=start`（守衛與被擋時的出口都在那一支，⛔ 不在這裡重造）。
    // ===========================================================
    static bool OpenCodingSession(string iDataRoot, string iPersona, int iIndex, string iScope, List<string> oLines, out bool oStarted)
    {
        oStarted = false;
        var aRoot = new SCP_DataRoot(iDataRoot);
        SCP_CodingSession? aMine = SCP_ActivitySessionStore.Load<SCP_CodingSession>(aRoot, iPersona, SCP_ActivitySessionKind.Coding);
        if (aMine != null && aMine.active && aMine.IsRunningAt(DateTime.Now, out _))
        {
            string? aRead = SCP.Core.Cmd.SCP_Cmd_Coding.BindTasks(aRoot, iPersona, iIndex.ToString(CultureInfo.InvariantCulture));
            if (aRead == null)
            {
                oLines.Add("- 妳已經有一場 Coding，但**補綁沒有落檔**（回讀不到那一場）—— 先查：" + SCP_CmdRegistry.Invoke("coding"));
                return false;
            }
            oLines.Add($"- 🛠 **綁到妳現有的場**（⛔ 沒有開第二場）：`{aMine.session_id}`　回讀 tasks = **{aRead}**");
            string aHave = SCP_ActivitySessionStore.ScopeOf(aMine);
            if (!string.Equals(aHave, iScope, StringComparison.OrdinalIgnoreCase))
                oLines.Add("- ⚠ 現有場的範圍**沒有變**：" + (aHave.Length > 0 ? $"`{aHave}`" : "**（沒宣告 ⇒ 整棵樹）**")
                    + $"；這次給的 `{iScope}` **沒有被套用**（要換範圍得先收掉現在那一場）。");
            return true;
        }
        SCP_CmdResult aStart = SCP_CmdRegistry.Dispatch("coding", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = iDataRoot,
            ["op"] = "start",
            ["persona"] = iPersona,
            ["status"] = $"TASK-{iIndex:0000}（由 op=claim 開場）",
            ["scope"] = iScope,
            ["tasks"] = iIndex.ToString(CultureInfo.InvariantCulture),
        });
        foreach (string l in aStart.Lines) oLines.Add("  " + l);
        if (!aStart.Ok) return false;
        oStarted = true;
        oLines.Insert(0, "- 🛠 **已開 Coding 場**並綁定本單 ⇒ 它離開施工狀態（`in_review`／`done`）時本場自動收");
        return true;
    }

    /// <summary>認領沒寫成而場是剛開的 ⇒ 收掉它（⛔ 不留一個沒有單的孤兒場擋住所有人）。回捲本身失敗要說出來。</summary>
    static string RollbackCodingSession(string iDataRoot, string iPersona)
    {
        try
        {
            var aRoot = new SCP_DataRoot(iDataRoot);
            SCP_CodingSession? aS = SCP_ActivitySessionStore.Load<SCP_CodingSession>(aRoot, iPersona, SCP_ActivitySessionKind.Coding);
            if (aS == null || !aS.active) return "（剛開的場已經不在了）";
            SCP_ActivitySessionStore.Close(aRoot, iPersona, aS, "task-claim-rollback");
            SCP_ActivitySession? aBack = SCP_ActivitySessionStore.Load(aRoot, iPersona);
            return aBack != null && !aBack.active
                ? "（認領沒寫成 ⇒ 剛開的場已回捲收掉，回讀確認=True）"
                : "（⚠ **回捲失敗：那一場還開著** —— 請手動 " + SCP_CmdRegistry.Invoke("coding --arg op=end --arg persona=" + iPersona) + "）";
        }
        catch (Exception e) { return "（⚠ **回捲丟例外，那一場可能還開著**：" + e.GetType().Name + "）"; }
    }

    // ===========================================================
    // 區塊職責：寫完之後發酒館通知（寫入端組好的本體 ＋ meta tag=task）。
    // ⚠ 失敗**只是警告**：單子已經寫好了。但一定要印 ——「我以為他知道了」是最貴的那種靜默失敗。
    // ===========================================================
    static void PostNotices(string iDataRoot, string iPersona, string iJson, StringBuilder ioReport, SCP_CmdResult ioResult)
    {
        List<Dictionary<string, string>>? aNotices = null;
        try { if (iJson.Length > 0) aNotices = JsonSerializer.Deserialize<List<Dictionary<string, string>>>(iJson); }
        catch (Exception e) { ioResult.Lines.Add("⚠ 寫入端交回的通知清單解析不了（" + e.Message + "）—— **沒有發通知**"); return; }
        if (aNotices == null || aNotices.Count == 0) return;
        SCP_ITavernPostGateway? aGate = SCP_TavernPostGatewayHost.Create(iDataRoot);
        if (aGate == null)
        {
            ioResult.Lines.Add("⚠ 本宿主沒有登記發文閘 ⇒ **酒館通知沒發**（單子已寫好）—— 相關的人還不知道這件事");
            ioReport.AppendLine("- ⚠ **酒館通知沒發出去**（本宿主沒有發文閘）—— 相關的人**還不知道這件事**，要自己去講一聲。");
            return;
        }
        int aSent = 0, aFailed = 0, aQueued = 0;
        foreach (Dictionary<string, string> n in aNotices)
        {
            var aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tag"] = "task",
                ["task"] = n.TryGetValue("task", out string? t) ? t : "",
                ["kind"] = n.TryGetValue("kind", out string? k) ? k : "",
            };
            var aLines = new List<string>();
            SCP_TavernPostVerdict aV = aGate.Post(iPersona, n.TryGetValue("body", out string? b) ? b : "", aMeta, aLines);
            if (aV.Outcome == SCP_TavernPostOutcome.Posted)
            {
                aSent++;
                ioResult.Lines.Add($"📣 酒館通知已發（{aMeta["task"]} {aMeta["kind"]}）seq {aV.Seq}");
                ioReport.AppendLine($"- 📣 酒館通知已發：seq **{aV.Seq}**（{aMeta["kind"]}）");
                continue;
            }
            if (aV.Outcome == SCP_TavernPostOutcome.Queued)
            {
                // 已排隊（TASK-0372）：酒館 Server 起來後送出 ⇒ ⛔ 不算沒發、不要補發。
                aQueued++;
                ioResult.Lines.Add($"📥 酒館通知已排隊（{aMeta["task"]} {aMeta["kind"]}）—— Server 起來後送出，還沒有 seq");
                ioReport.AppendLine($"- 📥 酒館通知**已排隊**（{aMeta["kind"]}）：{aV.Detail} —— ⛔ 不要補發");
                continue;
            }
            aFailed++;
            // ⛔ 逐態寫明：認不得的判定當成「不知道」，不准落進「確定沒發」（那會叫人補發）。
            string aWhy = aV.Outcome == SCP_TavernPostOutcome.NotPosted
                ? $"**確定沒發**（{aV.Detail}）"
                : $"**結果不明**（{aV.Detail}）—— ⛔ 別補發，先回讀：{aV.RecheckHint}";
            ioResult.Lines.Add($"⚠ 酒館通知 {aWhy}（單子已寫好）");
            foreach (string l in aLines) ioResult.Lines.Add("  │ " + l);
            ioReport.AppendLine($"- ⚠ **酒館通知沒發成**：{aWhy} —— 單子已經寫好了；相關的人可能**還不知道這件事**。");
        }
        ioResult.AddValue("notify_sent", aSent.ToString(CultureInfo.InvariantCulture));
        if (aFailed > 0) ioResult.AddValue("notify_failed", aFailed.ToString(CultureInfo.InvariantCulture));
        if (aQueued > 0) ioResult.AddValue("notify_queued", aQueued.ToString(CultureInfo.InvariantCulture));
    }

    // ===========================================================
    // 區塊職責：wrapup 的 `why` ⇒ 寫進工作記憶（`SCP_WorkMemory.Add`，與 `senate cmd work-memory --arg op=add` 同一支）。
    // 數值影響：寫一份 fragment ＋重建該主題的 `_index.md`；失敗是警告（進度已落盤），並印手動補的指令。
    // ===========================================================
    static void RunMemory(string iDataRoot, string iJson, StringBuilder ioReport, SCP_CmdResult ioResult)
    {
        if (iJson.Length == 0) return;
        Dictionary<string, string>? m;
        try { m = JsonSerializer.Deserialize<Dictionary<string, string>>(iJson); }
        catch (Exception e) { ioResult.Lines.Add("⚠ 寫入端交回的記憶請求解析不了（" + e.Message + "）—— **記憶那半沒寫**"); return; }
        if (m == null) return;
        string G(string k) => m.TryGetValue(k, out string? v) ? v : "";
        string aManual = SCP_CmdRegistry.InvokeOf<SCP_Cmd_WorkMemory>(
            $"--arg op=add --arg topic={G("topic")} --arg type={G("type")} --arg id={G("id")} --arg title=\"{G("title")}\" --arg-file body=<檔> --arg by={G("by")}");
        try
        {
            var aWm = new SCP_WorkMemory(iDataRoot);   // 具名根由宿主宣告（TASK-0390）
            SCP_WorkMemoryResult aR = aWm.Add(G("topic"), G("type"), G("id"), G("title"), G("body"), "", "", G("by"));
            if (aR.Exit != 0) throw new InvalidOperationException(string.Join(" ", aR.Lines));
            ioResult.Lines.Add($"🧠 已寫進工作記憶：{G("topic")} / {G("type")} / {G("id")}");
            ioReport.AppendLine($"- 🧠 已寫進工作記憶：`{G("topic")}` / `{G("type")}` / `{G("id")}`");
            ioResult.AddValue("memory", "written");
        }
        catch (Exception e)
        {
            ioResult.Lines.Add("⚠ **記憶那半沒寫成**（" + e.Message + "）—— 進度已落盤");
            ioReport.AppendLine($"- ⚠ **記憶那半沒寫成**（{e.Message}）—— 進度已落盤。手動補：`{aManual}`");
            ioResult.AddValue("memory", "failed");
        }
    }

    // ⛔ 2026-10-07（TASK-0390）刪掉 `UclCoreTool`（讀 Unity 專案的 .gitmodules 找 UCL_Core 底下的工具檔）：知識庫定義檔搬進 Senate（描述表 KbTargetsFile）。

    static string Value(SCP_CmdResult r, string k)
    {
        foreach (var kv in r.Values) if (kv.Key == k) return kv.Value;
        return "";
    }
}
