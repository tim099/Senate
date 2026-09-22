// 區塊職責：`tavern-write` —— 酒館訊息的**寫入臨界區**那一格，由 Senate Server 執行（TASK-0106）。
// 物理意義：D20 那句「只有一顆 process 在寫」落到酒館身上就是這一支。它**不是發文流程**：
//           mention 通知、Discord 鏡像、category 路由仍掛在 Editor 的 `AppendMessage` 上 ——
//           搬過來的只有「配號 → 建檔 → 寫 `_seq.txt`」這段真的需要單一寫入端的臨界區。
//
// 🔴 三道閘，每一道都是**拒絕**而不是降級（D10 丙，Tim 2026-09-20）：
//   ① `tavern.writer` 不是 `server` ⇒ 拒絕。⛔ 不「順便幫你寫」——
//      那會讓開關沒切過去的人以為切過去了，而同一時間 Editor 那側也還在寫。
//   ② 開關讀不了／認不得 ⇒ 拒絕（⛔ 不當成 editor 也不當成 server）。
//   ③ Server 沒跑 ⇒ 基底類別回 exit 3 並印啟動指令，本檔不必處理。
//
// 數值影響：一次寫入 ＝ 一次目錄列舉（冷快取時）＋ 一次建檔 ＋ 一次 `_seq.txt` 覆寫。
//
// ⚠ 訊息**整包以 JSON 傳**（`msg_json`），⛔ 不是一個欄位一個 `--arg`：
//   欄位逐個穿協議的話，加一個欄位就要改三處（宣告／打包／解包），而漏掉的那個
//   不會報錯，只會在落盤檔裡**安靜地少一格**。整包傳 ⇒ 欄位對應只有 `SCP_TavernRead.FromJson` 一處。
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Tavern;
using SCP.Core.Proc;

namespace Senate.Core;

public class Cmd_TavernWrite : ServerDelegateCmd
{
    public override string Name => "tavern-write";

    public override string Summary =>
        "酒館訊息寫入臨界區（配號＋建檔＋_seq.txt）—— 由 Senate Server 執行；"
        + "⛔ 只在 `tavern.writer=server` 時可用";

    public override string PortNote =>
        "終局就是這裡：這一格**必須**在單一 process 內，所以它不會被原生化回 CLI";

    public override string Example =>
        SCP_CmdRegistry.Invoke("tavern-write --arg data_root=<資料根> --arg room=tavern --arg-file msg_json=<檔>");

    /// <summary>
    /// 酒館走**自己那一顆** Server（TASK-0244 的落點）。
    /// <para>⚠ 代價要講清楚：這代表要 `senate server start --id tavern` 另外掛一顆。
    /// 好處是酒館塞住時不會連帶卡住銀行那條 —— 兩者的停機代價差很多。</para>
    /// </summary>
    protected override string ServerId => SCP_ServerIds.Tavern;

    /// <summary>
    /// **固定一條 lane <c>tavern</c>**（留言 #3 的候選 A，PM @basecamp 2026-09-21 拍板，驗收條文 ③）。
    /// <para>⚠ 判準是讀數不是「A 比較安全」：她量了流量分布 ——
    /// 52 房 20,426 則裡 `tavern` 這一個房占 **95.9%**，而**近 7 日是 100%**。
    /// 在這個分布下 A 與 B（per-room lane）序列化的結果**逐筆相同**，
    /// ⇒ B 買到的跨房並行今天收益是 **0**，而它的成本是多依賴一個前提（`_seq.txt` 必須 per-room）。
    /// **同樣的效果，選前提少的那個。**</para>
    /// <para>⚠ 射程照她寫的原樣抄：那是**今天的分布**，⛔ 不是永久性質；
    /// 而「A 吞吐夠用」是**推論不是讀數**（她沒量吞吐上限）。
    /// ⇒ 哪天真有第二個房吃到可觀流量，換 B 的成本是改這裡一行。</para>
    /// <para>🩸 而我 2026-09-21 在她拍板之後**做成了 B**，理由是我自己的技術判斷（跨房並行是安全的）——
    /// 安全與否不是這格的問題，**收益才是**，而那個讀數在我手上沒有。
    /// ⛔ 技術上成立不構成推翻 PM 決策的理由。</para>
    /// <para>⛔ 不走預設的 persona lane（<see cref="DefaultLane"/> ＝ <c>server</c>）：
    /// 那會讓兩個 persona 對同一個房並行寫入，而配號要的正是「同房序列化」。
    /// ⚠ 舊註解寫「要改回 A 只要回 <c>DefaultLane</c>」—— **那句是錯的**，
    /// 它會落在 <c>server</c> 那條 lane 上。改回 A 要回的是本常數。</para>
    /// <para>⚠ 而這道 lane **不是**配號正確性的依靠（那由寫入端自己的鎖與原子建檔保證）——
    /// 它降的是自我校正的重試率。兩者混為一談的話，哪天 lane 設錯了會以為「反正有鎖」。</para>
    /// <para>🩸 lane 名**必須是一層資料夾名**，而我試錯了兩次才量到為什麼：
    /// <br/>· <c>tavern:&lt;room&gt;</c> —— 冒號在 Windows 是 ADS 分隔字元。
    /// <br/>· <c>tavern/&lt;room&gt;</c> —— 那是協議的**子分道**寫法（`SCP_DataPaths.SplitQueueId`
    ///   ⇒ <c>queues/tavern/queue-&lt;room&gt;.json</c>），合法、檔案也真的寫出去了，
    ///   **而 Server 的執行器讀不到它**：`ServerExecutor.Tick` 掃的是 `queues/*` 這一層**目錄**，
    ///   lane 名 ＝ 目錄名，它只看 <c>pending.trigger</c>，⛔ 不看 <c>pending-&lt;lane&gt;.trigger</c>。
    /// <br/>⇒ 2026-09-21 端到端實測：queue 落在對的地方、JSON 合法、trigger 也寫了，
    ///   而 15 秒之後等不到判定 —— **沒有任何一層說「我不認得這個形狀」**。
    /// <br/>📌 ⇒ 子分道是 **Editor Runner 有、Server 執行器沒有**的能力。要它就得改執行器，
    ///   而那不在本單射程（見 TASK-0106 留言）。
    /// <br/>⚠ 這一格在 A 之下**沒有消失，只是碰不到** —— 常數是寫死的，所以現在沒有人能把 `/` 餵進來。
    ///   哪天改回 B（房名接在後面）那個坑原地回來，所以讀數留著。</para>
    /// </summary>
    protected override string Lane(SCP_CmdArgs iArgs) => TavernLane;

    /// <summary>
    /// 酒館唯一那條 lane。⭐ **事實源在 <see cref="SCP_TavernWriter.LaneName"/>** ——
    /// 送出端（Unity 的 `UCL_ChatTavernIO`）與本收下端唯一共同編得到的組件是 SCP_Core，
    /// ⇒ 兩邊指同一顆常數，⛔ 不是各寫一個字面再靠註解說「與對面同字面」。
    /// </summary>
    public const string TavernLane = SCP_TavernWriter.LaneName;

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— ⛔ 本層不推導", iRequired: true),
                new SCP_CmdArgSpec("room", "房間 id", iRequired: true),
                new SCP_CmdArgSpec("msg_json",
                    "整則訊息的 JSON（落盤同形）。長內容走 `--arg-file`", iRequired: true),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aDataRoot = iArgs.Get("data_root").Trim();
        string aRoom = iArgs.Get("room").Trim();
        string aJsonText = iArgs.Get("msg_json");

        // ── 閘①②：開關 ───────────────────────────────────────────
        SCP_TavernWriteModeRead aMode = SCP_TavernWriteMode.Read(aDataRoot);
        if (!aMode.Ok)
            return SCP_CmdResult.Fail(2,
                "✗ " + aMode.Describe(),
                "⛔ 開關讀不出來時**不寫** —— 猜哪一邊都會造出第二個寫入端。",
                "設定檔：" + SCP_TavernWriteMode.SettingsPath(aDataRoot));

        if (aMode.Host != SCP_TavernWriteHost.Server)
            return SCP_CmdResult.Fail(2,
                "✗ 這棵資料樹的 " + aMode.Describe() + " ⇒ **這支不該被呼叫**。",
                "⛔ 不代寫：開關還指著 Editor，而 Editor 此刻也在寫同一個房。",
                "要切過來：`senate cmd tavern-writer --arg data_root=" + aDataRoot + " --arg set=server`");

        SCP_JsonData aJson;
        try { aJson = SCP_JsonParser.Parse(aJsonText); }
        catch (Exception e)
        {
            return SCP_CmdResult.Fail(2, "✗ msg_json 解析不了：" + e.Message,
                "⛔ 這不是「沒帶」，是**帶了但讀不出**，不猜。");
        }

        // seq／path 由寫入端決定 ⇒ 這裡先給 0／空（⛔ 不從 msg_json 收 seq：那是呼叫端猜的號碼）
        SCP_TavernMessage aMsg = SCP_TavernRead.FromJson(aJson, aRoom, 0, "");
        if (aMsg.Body.Length == 0 && aMsg.Kind.Length == 0)
            return SCP_CmdResult.Fail(2, "✗ msg_json 解出來是空的（body 與 kind 都沒有）——"
                                         + " ⛔ 寧可拒絕，也不要落一則沒有內容的訊息。");

        SCP_TavernWriteResult aW = SCP_TavernWriter.WriteMessage(aDataRoot, aRoom, aMsg);
        if (!aW.Wrote)
            return SCP_CmdResult.Fail(1, "✗ " + aW.Detail).AddValue("room", aRoom);

        var aResult = SCP_CmdResult.Success(
            "✓ 已寫入　房=" + aRoom + "　seq=" + aW.Seq,
            "落點：" + aW.FullPath);
        if (aW.HealAttempts > 0)
            aResult.Lines.Add("⚠ 自我校正 " + aW.HealAttempts + " 次才寫成 —— "
                              + "**這在單一寫入端的世界裡不該發生**，代表有東西繞過本支直接寫檔。");
        aResult.AddValue("room", aRoom);
        aResult.AddValue("seq", aW.Seq.ToString());
        aResult.AddValue("path", aW.FullPath);
        aResult.AddValue("heal_attempts", aW.HealAttempts.ToString());
        return aResult;
    }
}
