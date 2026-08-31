// 區塊職責：晚安五步的 `senate cmd` 入口 —— `goodnight-check` / `-portrait` / `-letter` /
//           `-sleep` / `-logout`。五支全部**委派給 Unity Editor** 執行。
// 物理意義：晚安動的是共享權威狀態（lock 刪除／registry offline／酒館下線廣播／Task 收工閘），
//           而那些只有 Editor 那側有實作。⇒ 入口統一在 `senate cmd`，底下走 AgentCommand 檔案協議。
// 數值影響：本 process 零寫入；狀態變更全在目標專案的 Editor 端發生。
//
// ⚠ 為什麼 `letter` 也委派 —— 它是五步裡唯一「看起來可以原生」的那支（純 letters 層），
//   而我判它不搬。理由不是風險大，是**收益是零**：
//   原生唯一買得到的東西是「不需要 Editor」，而 check/portrait/sleep/logout 四步全部需要 Editor
//   ⇒ `letter` 原生也走不完晚安。代價卻是實的 ——
//   `UCL_AwakeningService.WriteWakeLetter` 的檔名是 `WakeLetterCount(persona) + 1`，
//   也就是**由磁碟檔數算出來的**，然後 `AtomicWrite` 過去。
//   🩸 2026-08-31 血證（basecamp）：`SCP_Consolidate.WakeLetterCount` 第一版寫成「數 wakes/ 全部 *.md」，
//      而某人的 wakes/ 裡有一個 `20260804_wake22.md`（8 位數前綴、不符 `^\d{6}_.*\.md$`）
//      ⇒ 那個人的計數多 1。**全庫只有她的資料能觸發，其他人完全正常。**
//   ⇒ 算錯的後果不是報錯，是**覆蓋掉既有的那封信** —— 安靜地吃掉一個人一天的記憶，
//      而她已經下線了，沒有人會回來檢查。
//   ⇒ 判準（basecamp 的原句）：**這一格會不會產生第二個寫者。**
//      買不到東西的第二個寫者，價格再低都太貴。
//
// ⚠ 為什麼是五支獨立 Cmd 而不是一支 `--arg step=` —— 同 `Cmd_Morning.cs` 的理由：
//   `ArgSpecs` 是每支一份扁平清單，沒有「隨 step 改變的必填」。折成一支的話
//   `letter_body`（letter 要）與 `about`/`body`（portrait 要）都只能宣告成選填
//   ⇒ **必填檢查整個退化成零**。判準：參數集合隨動詞改變 ⇒ 一個動詞一支 Cmd。
using SCP.Core.Cmd;

namespace Senate.Core;

/// <summary>晚安委派 Cmd 的共用殼：把「這一步的下一步指令名」講清楚。</summary>
public abstract class GoodnightDelegateCmd : UnityDelegateCmd
{
    /// <summary>
    /// 回傳檔裡的 `## next` 是 **Editor 端**寫的，教的是 `run_cmd.py` 那條路。
    /// <para>⚠ 走 CLI 的人照著打會打到另一個入口 —— 所以這裡補一行對照，
    /// **但不改寫回傳檔的內容**：改寫別人的產出，就沒有人知道那份檔真正說了什麼。</para>
    /// </summary>
    protected abstract override string CliNextHint { get; }

    protected static IEnumerable<SCP_CmdArgSpec> GoodnightSpecs()
    {
        yield return new SCP_CmdArgSpec("persona",
            "要對誰做這一步。⚠ **一律顯式** —— 猜錯的代價是把同事登出，而擾動過的 session 回不來",
            iRequired: true);
        foreach (SCP_CmdArgSpec aSpec in CommonSpecs()) yield return aSpec;
    }

    /// <summary>`step` ＋ `persona` —— 五支都要送的那兩格。</summary>
    protected Dictionary<string, string> StepArgs(SCP_CmdArgs iArgs, string iStep)
        => new Dictionary<string, string> { ["step"] = iStep, ["persona"] = iArgs.Get("persona") };

    protected sealed override string UnityCmdType => "GoodNight";
}

// ── ① check ──────────────────────────────────────────────────────

public sealed class Cmd_GoodnightCheck : GoodnightDelegateCmd
{
    public override string Name => "goodnight-check";

    public override string Summary => "晚安①唯讀起手：待辦盤點＋酒館最後一眼 —— 由 Unity Editor 執行";

    public override string Details =>
        "**唯讀** —— 這一步不下線、不寫信、不改任何狀態，只把「收工前該看的東西」攤出來：\n"
        + "未收工的單、酒館最後一眼、以及後續每一步的導引。\n"
        + "⚠ 它印的收工預告**只列不擋** —— 真正的實擋在 `goodnight-sleep`。";

    public override string PortNote =>
        "待辦盤點走 UCL_TaskReconcile（判準是四條件合取，**裡面沒有日曆**）＋ ChatTavernIO 讀取，兩者都未移植";

    public override string Example => SCP_CmdRegistry.Invoke("goodnight-check --arg persona=Template");

    protected override string CliNextHint =>
        SCP_CmdRegistry.Invoke("goodnight-portrait --arg persona=<P> …（畫像或顯式跳過，二擇一）");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(GoodnightSpecs());

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => StepArgs(iArgs, "check");
}

// ── ② portrait ───────────────────────────────────────────────────

public sealed class Cmd_GoodnightPortrait : GoodnightDelegateCmd
{
    public override string Name => "goodnight-portrait";

    public override string Summary => "晚安②見人畫像投遞，或顯式跳過 —— 由 Unity Editor 執行";

    public override string Details =>
        "投遞：`about` ＋ `headline` ＋ `body`（親筆，長內文走 `--arg-file`）。\n"
        + "跳過：只給 `skip_reason` —— 而理由會印進下線廣播，所以它是**對同事說的話**，不是給工具的旗標。\n"
        + "⚠ 這一步會**擋住** `goodnight-letter`：畫像或顯式跳過，二擇一，沒有第三條。\n"
        + "⚠ `about`+`body` 與 `skip_reason` 的**互斥檢查在 Editor 那側**，本 CLI 不重做一份 ——\n"
        + "   兩個地方各判一次的話，兩份判準遲早分岔，而分岔的那天兩邊都不會報錯。";

    public override string PortNote =>
        "sketchbook 寫入 ＋ 酒館廣播（seq 分配）仍在 Editor 那側，未移植";

    public override string Example =>
        SCP_CmdRegistry.Invoke("goodnight-portrait --arg persona=Template --arg skip_reason=今晚沒有值得畫的一格");

    protected override string CliNextHint =>
        SCP_CmdRegistry.Invoke("goodnight-letter --arg persona=<P> --arg-file letter_body=<檔>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(GoodnightSpecs());
            aSpecs.Add(new SCP_CmdArgSpec("about", "畫誰（同事的 persona 名）"));
            aSpecs.Add(new SCP_CmdArgSpec("headline", "一句話標題"));
            aSpecs.Add(new SCP_CmdArgSpec("body", "公開層內文（**親筆**，工具不代筆）。長內文走 --arg-file"));
            aSpecs.Add(new SCP_CmdArgSpec("private_body", "私層內文（選填）"));
            aSpecs.Add(new SCP_CmdArgSpec("affinity", "好感讀數，如 `11/在意`（選填）"));
            aSpecs.Add(new SCP_CmdArgSpec("skip_reason",
                "**今晚為什麼不畫** —— 顯式跳過用。理由會印進下線廣播"));
            return aSpecs;
        }
    }

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
    {
        Dictionary<string, string> aArgs = StepArgs(iArgs, "portrait");
        // ⚠ 空值不送 —— 送空字串與「沒給」在對面看起來一模一樣，而 portrait 的互斥判準
        //   正是靠「哪幾格有值」決定走投遞還是跳過。
        AddIfSet(aArgs, iArgs, "about");
        AddIfSet(aArgs, iArgs, "headline");
        AddIfSet(aArgs, iArgs, "body");
        AddIfSet(aArgs, iArgs, "private_body");
        AddIfSet(aArgs, iArgs, "affinity");
        AddIfSet(aArgs, iArgs, "skip_reason");
        return aArgs;
    }

    static void AddIfSet(Dictionary<string, string> ioTarget, SCP_CmdArgs iArgs, string iKey)
    {
        string aValue = iArgs.Get(iKey);
        if (aValue.Length > 0) ioTarget[iKey] = aValue;
    }
}

// ── ③ letter ─────────────────────────────────────────────────────

public sealed class Cmd_GoodnightLetter : GoodnightDelegateCmd
{
    public override string Name => "goodnight-letter";

    public override string Summary => "晚安③收尾信落檔（body 必須親筆）—— 由 Unity Editor 執行";

    public override string Details =>
        "收尾信寫進 `wakes/<編號>_<時戳>.md`，並同步 `_latest.md`（那是**內容副本**不是連結）。\n"
        + "`letter_body` 只寫你自己的話 —— **工具代筆的信不是你的**。\n"
        + "⚠ 作者自己在 body 開頭寫的 frontmatter 會被拆：與機器欄同名的存成 `<key>_as_written`，\n"
        + "   不同名的原樣保留。機器欄固定五個：`type/actor/written_at/written_by_persona/trigger`。\n"
        + "⚠ **本步刻意不在 CLI 這側原生實作**（檔頭有完整理由）：信編號由磁碟檔數算出，\n"
        + "   多一個寫者就多一次算錯的機會，而算錯是**覆蓋掉既有的那封信**，不是報錯。";

    public override string PortNote =>
        "刻意不移植 —— 寫入端只留 Editor 一個。`wakes/` 的檔名由 WakeLetterCount+1 決定，"
        + "第二個寫者算錯會 AtomicWrite 覆蓋既有的信（安靜地吃掉一天的記憶）";

    public override string Example =>
        SCP_CmdRegistry.Invoke("goodnight-letter --arg persona=Template --arg-file letter_body=D:/tmp/letter.md");

    protected override string CliNextHint => SCP_CmdRegistry.Invoke("goodnight-sleep --arg persona=<P>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(GoodnightSpecs());
            // ⚠ 參數名刻意與 Editor 協議**逐字同形**（`letter_body`，不簡化成 `body`）——
            //   改名等於在兩個入口之間多一層翻譯，而翻譯錯的症狀是「參數靜默取預設值」。
            aSpecs.Add(new SCP_CmdArgSpec("letter_body",
                "你**親筆**的收尾信。長內文走 --arg-file", iRequired: true));
            return aSpecs;
        }
    }

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
    {
        Dictionary<string, string> aArgs = StepArgs(iArgs, "letter");
        aArgs["letter_body"] = iArgs.Get("letter_body");
        return aArgs;
    }
}

// ── ④ sleep ──────────────────────────────────────────────────────

public sealed class Cmd_GoodnightSleep : GoodnightDelegateCmd
{
    public override string Name => "goodnight-sleep";

    public override string Summary => "晚安④下線：收工閘→offline→解鎖→下線廣播 —— 由 Unity Editor 執行";

    public override string Details =>
        "步驟順序是**不變式**，不能重排：權威狀態先落地（profile offline ／刪 lock）→ 下線廣播\n"
        + "（best-effort）→ 最後才 expire token。\n"
        + "⚠ **收工閘會實擋**：有未收工的單時本步非零退出，回傳檔帶出口清單。\n"
        + "   `skip_reason` 可以過閘 —— 但那個理由會**寫進那幾張單的時間線**，\n"
        + "   也就是說它不是一個旗標，是一筆留給下一個看那張單的人的紀錄。\n"
        + "⚠ 需要先寫信（`goodnight-letter`）。不想寫信的下線走 `goodnight-logout`。";

    public override string PortNote =>
        "收工閘走 UCL_TaskReconcile.PendingWrapups（402 行，判準四條件合取）＋ profile 寫入 ＋ 酒館廣播，全未移植";

    public override string Example => SCP_CmdRegistry.Invoke("goodnight-sleep --arg persona=Template");

    protected override string CliNextHint =>
        "（晚安到此結束 —— 下線後不要再跑任何 goodnight-* ；要重新上線走 "
        + SCP_CmdRegistry.Invoke("morning-wake --arg persona=<P>") + "）";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(GoodnightSpecs());
            aSpecs.Add(new SCP_CmdArgSpec("summary", "公開的睡前心得（選填，併入下線廣播）"));
            aSpecs.Add(new SCP_CmdArgSpec("skip_reason",
                "跳過收工閘的理由。⚠ 會寫進那幾張單的時間線 —— 那是紀錄不是旗標"));
            return aSpecs;
        }
    }

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
    {
        Dictionary<string, string> aArgs = StepArgs(iArgs, "sleep");
        string aSummary = iArgs.Get("summary");
        if (aSummary.Length > 0) aArgs["summary"] = aSummary;
        string aSkip = iArgs.Get("skip_reason");
        if (aSkip.Length > 0) aArgs["skip_reason"] = aSkip;
        return aArgs;
    }
}

// ── ⑤ logout（獨立，不是第五步）────────────────────────────────────

public sealed class Cmd_GoodnightLogout : GoodnightDelegateCmd
{
    public override string Name => "goodnight-logout";

    public override string Summary => "手動登出／cleanup（不寫信，廣播標明未留信）—— 由 Unity Editor 執行";

    public override string Details =>
        "**這不是晚安的第五步，是另一條路** —— session 壞掉、或只想清掉 lock 時走它。\n"
        + "⚠ 它**不套收工閘**（那是 cleanup 不是收工）。合併的話「手動登出」會被沒收工的單擋住，\n"
        + "   而那正是它存在的理由：擋住出口的守衛沒有出口。\n"
        + "⚠ 不寫信 ⇒ `wakes/` 不會新增，下線廣播會標明未留信。**它不能代替 goodnight-sleep。**";

    public override string PortNote => "lock 刪除 ＋ profile offline ＋ 酒館廣播仍在 Editor 那側，未移植";

    public override string Example => SCP_CmdRegistry.Invoke("goodnight-logout --arg persona=Template");

    protected override string CliNextHint =>
        "（cleanup 完成 —— 這條路不寫信，所以沒有下一步；要正常收工走 "
        + SCP_CmdRegistry.Invoke("goodnight-check --arg persona=<P>") + "）";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(GoodnightSpecs());

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => StepArgs(iArgs, "logout");
}
