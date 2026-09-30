// 區塊職責：`tavern-write` —— 酒館訊息的**寫入臨界區**那一格，由 Senate Server 執行（TASK-0106）。
// 物理意義：D20 那句「只有一顆 process 在寫」落到酒館身上就是這一支 —— **酒館訊息唯一的寫入端**
//           （TASK-0341，2026-09-30：Editor 本地寫入與 `tavern.writer` 開關都已刪除）。
//           配號 → 建檔 → 寫 `_seq.txt` → 刷索引在臨界區裡；寫完再做 @ 通知、發薪、詞典附註、創作留念信。
//
// 閘：Server 沒跑 ⇒ 基底類別回 exit 3 並印啟動指令（委派端會 autostart），本檔不必處理；
//     msg_json 讀不出 ⇒ 拒絕。
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
        "酒館訊息寫入臨界區（配號＋建檔＋_seq.txt）—— 由 Senate Server 執行；酒館訊息唯一的寫入端";

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
                // TASK-0313：詞典附註改由寫入端補（Unity 端不碰詞典）。兩格由 CLI 宿主照 senate.local.json 自動填，
                //   ⛔ 呼叫端不必給 —— 沒有它們（例如 in-process 呼叫、沒帶）⇒ 不附，照寫。
                new SCP_CmdArgSpec("glossary_root", "詞典根（宿主自動填；不帶 ⇒ 不補附註）"),
                new SCP_CmdArgSpec("project_root", "專案根（附註路徑顯示用；宿主自動填）"),
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

        string aGlossaryNote = AttachGlossary(aMsg, iArgs.Get("glossary_root").Trim(), iArgs.Get("project_root").Trim());

        // ── 閘③：封存的頻道不寫（TASK-0318）─────────────────────────
        // 🩸 不擋的話：寫入端會在 `rooms/` 自己建一個同名新房（CreateDirectory），而記憶體裡的訊息數還是舊房的
        //   ⇒ 新房從舊號接著編，取消封存時兩份撞名。⛔ 不代為取消封存：那是頻道管理的決定，不是發文的副作用。
        if (SCP.Core.Tavern.SCP_TavernChannels.IsArchived(aDataRoot, aRoom))
            return SCP_CmdResult.Fail(2,
                "✗ 房 `" + aRoom + "` 已封存（在 " + SCP.Core.Tavern.SCP_TavernChannels.ArchiveRoot(aDataRoot) + "）⇒ **沒有寫入**。",
                "要在這裡發文：先取消封存（`senate cmd channel --arg op=unarchive --arg room=" + aRoom + "` 或後台「頻道管理」頁）。")
                .AddValue("room", aRoom);

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
        aResult.AddValue("glossary", aGlossaryNote);
        AppendMentions(aDataRoot, aRoom, aW.Seq, aW.FullPath, aMsg, aResult);
        AppendPayroll(aDataRoot, aRoom, aW.Seq, aMsg, aResult);
        AppendCreativeArchive(aDataRoot, aRoom, aW.Seq, aMsg, aResult);
        return aResult;
    }

    // ===========================================================
    // 區塊職責：**寫入前補詞典附註**（TASK-0313）—— Editor 發的文不再自己附（Tim 2026-09-28：Unity 端不碰詞典）。
    // 物理意義：**只有帶了請求鍵的才附**（`SCP_Glossary.AttachRequestMetaKey`，Editor `Op_Post` 會帶）——
    //          Editor 另有 20 處直接寫訊息，以前都不附；預設全附會讓它們突然長出附註（Discord 進站的人話也會被附）。
    //          判準與 Senate 組訊息端同一支（`ShouldAutoAttach`：系統元件／CLI 指令／顯式 opt-out 不附）；
    //          已含 marker 的原樣放行 ⇒ 每一則最多附一次。
    // 數值影響：改 Body、拿掉請求鍵（⛔ 它不落進訊息檔）。回傳 `🔢 glossary`：
    //          not_requested／already／skip／no_root／none（命中 0）／attached。⛔ 附註失敗不擋寫入。
    // ===========================================================
    public static string AttachGlossary(SCP_TavernMessage ioMsg, string iGlossaryRoot, string iProjectRoot)
    {
        if (!ioMsg.Meta.Remove(SCP.Core.Glossary.SCP_Glossary.AttachRequestMetaKey)) return "not_requested";
        if (ioMsg.Body.Contains(SCP.Core.Glossary.SCP_Glossary.AutoAttachMarker)) return "already";
        if (!SCP.Core.Glossary.SCP_Glossary.ShouldAutoAttach(ioMsg.SenderId, ioMsg.Meta)) return "skip";
        if (iGlossaryRoot.Length == 0) return "no_root";
        string aPrefix = SCP.Core.Glossary.SCP_Glossary.DisplayPrefix(iProjectRoot, iGlossaryRoot);
        string aAttached = SCP.Core.Glossary.SCP_Glossary.AppendRefs(ioMsg.Body, iGlossaryRoot, aPrefix);
        if (aAttached == ioMsg.Body) return "none";
        ioMsg.Body = aAttached;
        return "attached";
    }

    // ===========================================================
    // 區塊職責：**寫完就寄 creative 留念信**（TASK-0312）—— 判準與信文在 SCP_TavernCreativeArchive（與 Editor 本地寫那條同一支）。
    // 物理意義：掛在寫入端（同 @mention／發薪）而不是發文前處理：留念信要 seq，而 alter 延後發文排程時還沒有 seq；
    //          放這裡 ⇒ Senate 路、Editor 路（writer=server 時也委派這裡）、延後發文都恰好寄一封。
    // 數值影響：寫兩份信件檔。失敗只寫進回傳值 —— 訊息已經落檔、seq 已經給出去了，⛔ 不讓寫入回報失敗。
    // ===========================================================
    static void AppendCreativeArchive(string iDataRoot, string iRoom, int iSeq, SCP_TavernMessage iMsg, SCP_CmdResult ioResult)
    {
        try
        {
            string aLetters = SCP.Core.Paths.SCP_DataPaths.Letters(new SCP.Core.Paths.SCP_DataRoot(iDataRoot)).Value;
            bool? aSent = SCP_TavernCreativeArchive.TrySend(aLetters, iMsg.SenderPersona, iRoom, iSeq, iMsg.Body, iMsg.Meta,
                                                            out string aInbox, out string aError);
            if (aSent == null) return;   // 不是 creative／匿名／空內文 ⇒ 不需要寄（跟「寄失敗」不同形）
            ioResult.AddValue("creative_mail", aSent.Value ? "sent" : "failed");
            ioResult.Lines.Add(aSent.Value
                ? "📜 創作留念信 → @" + iMsg.SenderPersona + "（" + aInbox + "）"
                : "⚠ 創作已貼出（seq " + iSeq + "）但留念掛號信沒寄成：" + aError);
        }
        catch (Exception e)
        {
            ioResult.AddValue("creative_mail", "failed");
            ioResult.Lines.Add("⚠ 創作留念信丟例外（貼文本身不受影響）：" + e.Message);
        }
    }

    // ===========================================================
    // 區塊職責：**寫完就通知 @ 到的人**（TASK-0299）—— 規則在 SCP_TavernMentions（與 Editor 本地寫那條同一支）。
    // 物理意義：通知是寫入不變量（任何進到房間的訊息都該觸發），⇒ 掛在寫入端；直打本支的訊息以前沒人通知。
    //          這裡拿得到**真的訊息檔路徑**（Editor 在 server 模式委派之後拿不到）⇒ 截斷的條目會指出全文在哪。
    // 數值影響：通知失敗不讓寫入失敗；逐人回報。repo 根＝資料根的上一層（只用來把路徑印成 repo 相對，
    //          資料根不在 repo 底下時退回印絕對路徑，⛔ 不影響通知本身）。
    // ===========================================================
    static void AppendMentions(string iDataRoot, string iRoom, int iSeq, string iMsgPath, SCP_TavernMessage iMsg, SCP_CmdResult ioResult)
    {
        try
        {
            string aRepoRoot = System.IO.Directory.GetParent(iDataRoot.TrimEnd('/', '\\'))?.FullName ?? "";
            SCP_MentionResult r = SCP_TavernMentions.Notify(iDataRoot, SCP_MentionInput.From(iMsg, iRoom, iSeq, iMsgPath), aRepoRoot,
                                                            iLine => ioResult.Lines.Add("· " + iLine));
            if (r.Notified.Count > 0) ioResult.Lines.Add("📥 已通知：" + string.Join("、", r.Notified));
            if (r.Duplicates.Count > 0) ioResult.Lines.Add("📥 已經通知過（冪等，未重寫）：" + string.Join("、", r.Duplicates));
            foreach (string f in r.Failures) ioResult.Lines.Add("⚠ 通知失敗 " + f);
            ioResult.AddValue("mention_notified", r.Notified.Count.ToString());
            ioResult.AddValue("mention_dup", r.Duplicates.Count.ToString());
            ioResult.AddValue("mention_failed", r.Failures.Count.ToString());
        }
        catch (Exception e)
        {
            ioResult.Lines.Add("⚠ 通知例外（訊息已落檔，⛔ 這一則沒通知）：" + e.GetType().Name + ": " + e.Message);
            ioResult.AddValue("mention_failed", "exception");
        }
    }

    // ===========================================================
    // 區塊職責：**寫完就發薪**（TASK-0296）—— 規則在 SCP_TavernPayroll（與 Editor 本地寫那條同一支），入帳交銀行那顆。
    // 物理意義：發薪掛在寫入端而不是 `op=post` ⇒ 任何入口寫進來的訊息都照同一套規則付
    //          （TASK-0106 #24：直打本支的 4 則以前不付，照構造就不付）。
    //          `bank` 的 ServerId 是 main、本 process 是 tavern ⇒ ServerDelegateCmd 會**委派**過去，
    //          ⛔ 不在這裡寫帳本（那是銀行的第二個寫入端）。
    // 數值影響：發薪失敗**不讓寫入失敗**（訊息已經落檔、seq 已經給出去了），但每一筆都印、並回報計數，
    //          讓 `payroll-audit` 與呼叫端都看得到。冪等命中（同一則被第二條路規劃）另計，⛔ 不算成「付了」。
    // ===========================================================
    static void AppendPayroll(string iDataRoot, string iRoom, int iSeq, SCP_TavernMessage iMsg, SCP_CmdResult ioResult)
    {
        SCP_TavernPayPlan aPlan;
        try { aPlan = SCP_TavernPayroll.Plan(iDataRoot, SCP_TavernPayInput.From(iMsg, iRoom, iSeq)); }
        catch (Exception e)
        {
            ioResult.Lines.Add("⚠ 發薪規劃例外（訊息已落檔，⛔ 這一則沒發）：" + e.GetType().Name + ": " + e.Message);
            ioResult.AddValue("pay_failed", "plan");
            return;
        }
        foreach (string aWarn in aPlan.Warnings) ioResult.Lines.Add("⚠ 發薪：" + aWarn);
        ioResult.AddValue("pay_items", aPlan.Items.Count.ToString());
        if (aPlan.Items.Count == 0) return;

        var aBankRoot = SCP_TavernPayroll.BankRootOf(iDataRoot);
        if (aBankRoot.Error != null || aBankRoot.Value.Length == 0)
        {
            ioResult.Lines.Add($"⚠ 發薪：銀行根解不出來（{aBankRoot.Error}）⇒ {aPlan.Items.Count} 筆**都沒發**");
            ioResult.AddValue("pay_failed", aPlan.Items.Count.ToString());
            return;
        }
        int aOk = 0, aDup = 0, aBad = 0, aQueued = 0;
        foreach (SCP_TavernPayItem aItem in aPlan.Items)
        {
            Dictionary<string, string> aArgs = SCP_TavernPayroll.ToBankArgs(aItem, aBankRoot.Value);
            SCP_CmdResult aR;
            try { aR = SCP_CmdRegistry.Dispatch("bank", aArgs); }
            catch (Exception e) { aR = SCP_CmdResult.Fail(1, "例外：" + e.GetType().Name + ": " + e.Message); }
            if (aR.Ok)
            {
                bool aIsDup = aR.Values.Any(v => v.Key == "duplicate" && v.Value == "1");
                if (aIsDup) aDup++; else aOk++;
                ioResult.Lines.Add("💰 " + SCP_TavernPayroll.Describe(aItem) + (aIsDup ? "　（冪等命中，錢沒動）" : ""));
                continue;
            }

            // ── TASK-0297：銀行那顆**還在啟動／等不到**（這一筆確定還沒送進去）⇒ 照正常協議排進它的 queue，
            //    不等結果；它起來之後的下一個心跳會自己接手，跟正常 CLI 觸發是同一條路。
            //    🩸 此前這裡只印一行就結束 ⇒ 2026-09-20 兩則漏薪就是這樣沒的（exe 重建、Server 換手的空窗）。
            string aWhy = aR.Values.Where(v => v.Key == "delegate_failure").Select(v => v.Value).LastOrDefault() ?? "";
            if (ServerDelegateCmd.ShouldQueueForLater(aWhy)
                && ServerDelegateCmd.TryQueueWithoutWaiting("bank", aArgs, out string aCmdId, out string aDetail))
            {
                aQueued++;
                ioResult.Lines.Add("📥 發薪已排進銀行 queue（delegate_failure=" + aWhy + "）"
                                   + SCP_TavernPayroll.Describe(aItem) + "　cmd_id=" + aCmdId
                                   + " ⇒ 銀行那顆起來後自己跑");
                continue;
            }

            aBad++;
            ioResult.Lines.Add("✗ 發薪失敗 " + SCP_TavernPayroll.Describe(aItem) + "　exit=" + aR.ExitCode
                               + (aWhy.Length > 0 ? "　delegate_failure=" + aWhy : ""));
            foreach (string aLine in aR.Lines.Take(4)) ioResult.Lines.Add("    " + aLine);
        }
        ioResult.AddValue("pay_ok", aOk.ToString());
        ioResult.AddValue("pay_dup", aDup.ToString());
        ioResult.AddValue("pay_queued", aQueued.ToString());
        ioResult.AddValue("pay_failed", aBad.ToString());
    }
}
