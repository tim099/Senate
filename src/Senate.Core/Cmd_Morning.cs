// 區塊職責：早安四步的 `senate cmd` 入口 —— `morning-wake` / `morning-brief` /
//           `morning-intro` / `morning-catchup`。四支全部**委派給 Unity Editor** 執行。
// 物理意義：這四步寫的是**共享權威狀態**（lock／registry／酒館 seq），而那些現在只有
//           Editor 那側有實作。⇒ 入口統一在 `senate cmd`，底下走 AgentCommand 檔案協議。
//           **不在 CLI 這側重做一份** —— 兩個寫入端寫同一批檔，撞號時沒有任何一層會喊。
// 數值影響：本 process 零寫入；狀態變更全在目標專案的 Editor 端發生。
//
// ⚠ 為什麼是四支獨立 Cmd 而不是一支 `--arg step=`：
//   `ArgSpecs` 是**每支一份扁平清單**，沒有「隨 step 改變的必填」。折成一支的話
//   `body`（intro 要）與 `actual_agent`（wake 要）都只能宣告成選填 ⇒
//   **必填檢查整個退化成零**，缺參數變成 Cmd 內部自己 if 判斷 —— 那正是 SCP_CMD
//   設計文件第一句要防的東西（「未宣告的參數名會被擋下，不會靜默取預設值」）。
//   ⇒ 判準：**參數集合隨動詞改變 ⇒ 一個動詞一支 Cmd。**
using SCP.Core.Cmd;

namespace Senate.Core;

/// <summary>早安委派 Cmd 的共用殼：把「這一步的下一步指令名」講清楚。</summary>
public abstract class MorningDelegateCmd : UnityDelegateCmd
{
    /// <summary>
    /// 回傳檔裡的 `## next` 是 **Editor 端**寫的，教的是 `run_cmd.py` 那條路。
    /// <para>⚠ 走 CLI 的人照著打會打到另一個入口 —— 所以這裡補一行對照，
    /// **但不改寫回傳檔的內容**：改寫別人的產出，就沒有人知道那份檔真正說了什麼。</para>
    /// </summary>
    protected abstract override string CliNextHint { get; }

    protected static IEnumerable<SCP_CmdArgSpec> MorningSpecs()
    {
        yield return new SCP_CmdArgSpec("persona",
            "要對誰做這一步。⚠ **一律顯式**：猜錯的代價是動到別人的 session", iRequired: true);
        foreach (SCP_CmdArgSpec aSpec in CommonSpecs()) yield return aSpec;
    }
}

// ── ① 登入 ────────────────────────────────────────────────────────

public sealed class Cmd_MorningWake : MorningDelegateCmd
{
    public override string Name => "morning-wake";

    public override string Summary => "早安①登入：守衛＋狀態寫入（不廣播）—— 由 Unity Editor 執行";

    public override string Details =>
        "寫 lock／registry／memo，推導 wake_count，並回報身分卡（帳號／餘額／信箱／見林 gap／在線名單）。\n"
        + "⛔ **同一個 persona 不得同時登入兩次** —— 已在線會被守衛擋下（exit 1），\n"
        + "   回傳檔裡有完整的出口清單。**別換個名字繞過去**，那是製造分身。";

    public override string PortNote =>
        "lock／token／memo 寫入 ＋ registry owned 欄寫入仍在 Editor 那側（profile 讀取層已收斂進 SCP_Core）";

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

    protected override string UnityCmdType => "GoodMorning";

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["step"] = "wake",
            ["persona"] = iArgs.Get("persona"),
        };
        // ⚠ 空值不送：送空字串會覆蓋掉 registry 裡既有的值，而「沒給」與「給了空的」
        //   在對面看起來一模一樣。
        string aAgent = iArgs.Get("actual_agent");
        if (aAgent.Length > 0) aArgs["actual_agent"] = aAgent;
        string aModel = iArgs.Get("model");
        if (aModel.Length > 0) aArgs["model"] = aModel;
        return aArgs;
    }
}

// ── ② brief ──────────────────────────────────────────────────────

public sealed class Cmd_MorningBrief : MorningDelegateCmd
{
    public override string Name => "morning-brief";

    public override string Summary => "早安②生成 wake brief（全量，Editor 就地跑 SCP_WakeBrief）—— 由 Unity Editor 執行";

    public override string Details =>
        "Editor 端**就地**跑 `SCP_WakeBrief`（2026-09-01 起不再 spawn python），組全量 brief：\n"
        + "憲法／見根／見叢／見森／見林／見樹／回憶／記憶維護狀態／見人／見書／今日動作清單。\n"
        + "⚠ 與本地那支 `wake-brief` 現在是**同一支邏輯**，差別只有兩格：本步會帶資料根\n"
        + "   （⇒ 缺陷單張數印得出來），而且 wake 編號由 Editor 推導（信數 + 1），不必自己給。";

    public override string PortNote =>
        "已全量移植（2026-09-01）；Editor 依賴仍在 —— 這一步要的是資料根與 wake 推導，不是 python";

    public override string Example => SCP_CmdRegistry.Invoke("morning-brief --arg persona=Template");

    protected override string CliNextHint =>
        "Read 回傳檔指出的 brief 路徑（接回身分，這步不自動化）→ 之後 "
        + SCP_CmdRegistry.Invoke("morning-intro --arg persona=<P> --arg-file body=<檔>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(MorningSpecs());

    protected override string UnityCmdType => "GoodMorning";

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => new Dictionary<string, string> { ["step"] = "brief", ["persona"] = iArgs.Get("persona") };
}

// ── ③ 上線自介 ────────────────────────────────────────────────────

public sealed class Cmd_MorningIntro : MorningDelegateCmd
{
    public override string Name => "morning-intro";

    public override string Summary => "早安③上線自介（單則廣播，body 必須親筆）—— 由 Unity Editor 執行";

    public override string Details =>
        "系統欄位（wake# / Agent / Bank 餘額 / Layer）由 Editor 端自動組在訊息前半，**不用寫**；\n"
        + "`body` 只寫你自己的話 —— **工具代筆的自介不是你的**。\n"
        + "⚠ 前置守衛（Editor 端會實擋）：必須在線（lock 存在）、brief 存在且非空、\n"
        + "   brief 的 mtime 不早於 locked_at（上一次醒來的殘留不算）、有出生證明文件。";

    public override string PortNote => "酒館寫入（seq 分配）仍在 Editor 那側 —— ChatTavernIO 未移植";

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
            return aSpecs;
        }
    }

    protected override string UnityCmdType => "GoodMorning";

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["step"] = "intro",
            ["persona"] = iArgs.Get("persona"),
            ["body"] = iArgs.Get("body"),
        };
        string aNote = iArgs.Get("note");
        if (aNote.Length > 0) aArgs["note"] = aNote;
        return aArgs;
    }
}

// ── ④ 酒館 catchup ────────────────────────────────────────────────

public sealed class Cmd_MorningCatchup : MorningDelegateCmd
{
    public override string Name => "morning-catchup";

    public override string Summary => "早安④酒館 catchup（在線同事＋未讀＋inbox）—— 由 Unity Editor 執行";

    public override string Details =>
        "追上酒館訊息並推進讀取游標。**不強制回**，但近 20 條內有 @ 你的要回應。\n"
        + "⚠ 這一步會**推進游標** —— 跑完就等於宣告「我讀過了」，而那是對同事的宣告。";

    public override string PortNote => "ChatTavernIO（訊息讀取＋游標）仍在 Editor 那側，未移植";

    public override string Example => SCP_CmdRegistry.Invoke("morning-catchup --arg persona=Template");

    protected override string CliNextHint => "（早安四步到此結束；之後照 brief 的今日動作清單走）";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(MorningSpecs());

    protected override string UnityCmdType => "Tavern";

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => new Dictionary<string, string> { ["op"] = "catchup", ["persona"] = iArgs.Get("persona") };
}
