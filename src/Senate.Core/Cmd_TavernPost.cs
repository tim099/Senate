// 區塊職責：`senate cmd tavern-post` —— 一般酒館發文，**不需要 Unity Editor**（TASK-0308，epic 0295 ③ 第一刀）。
// 物理意義：Tim 2026-09-27：「發文要走 ucmd run Tavern 是否可以改成 Senate CLI 的 cmd」。
//          路是 `morning-intro` 已經走通的那一條：`SCP_TavernPostCompose.Build` 在 Senate 組訊息
//          （sender_id／顯示名／頭像／詞典附註），再交給 `tavern-write`（酒館 Server：配號建檔＋發薪＋@mention）。
//          本檔只多做 intro 用不到的事：meta／refs／reply_to 的解析、`status` → now_status、以及寫入前後的四段前處理。
// 數值影響：寫一則酒館訊息（經 Server）＋ 回傳檔 `letters/<P>/cmd/tavern_post.md`；帶 `status` 時寫 `cmd/now_status.json`。
//
// Editor `Cmd_Tavern.Op_Post` 的前處理**全部**搬完了（TASK-0311／0312），每一段的判準都住 SCP_Core、兩個宿主共用：
//   ① meta schema（commit／task-assign／task-ack）⇒ `SCP_TavernMetaSchema`：不合 ⇒ exit 2 確定沒發
//   ② 酒保 CLI 指令 ⇒ `SCP_TavernCli`（在 compose 裡）：打 `cli-cmd` 標記、不附詞典
//   ③ creative 留念信 ⇒ `SCP_TavernCreativeArchive`，**由寫入端寄**（`Cmd_TavernWrite`，同 @mention／發薪）
//   ④ alter 配對延遲 ⇒ `SCP_TavernAlterPacing` 判要不要等；要等 ⇒ 放進酒館 Server 的延後發文匣（`SenateTavernDeferred`），
//      CLI 當下回「已排程」（exit 0、`scheduled=1`、**沒有 post_seq** —— 還沒配號），到點由 Server 發出。
//
// ⚠ 身分：persona 必填，但**不檢查在線**（Tim 2026-09-27：在線機制是擋同一 persona 重複登入，不是發言許可；
//   例：下線之後 commit 信件 repo，公告照樣要發）。⛔ 也不驗 session token（同日 Tim 拍板；參數已移除）。
//   Editor 版允許匿名發言（不帶 persona）；本入口不開那條：匿名是系統元件的路，agent 用不到它。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

public class Cmd_TavernPost : MorningLocalCmd
{
    public override string Name => "tavern-post";

    /// <summary>
    /// true ＝ **系統發言**（`tavern-post-system`，TASK-0366）：身分由 `sender` 點名、沒有 persona ⇒ 不計酬、不做 alter 延遲、不更新 now_status、不落回傳檔。
    /// <para>⚠ 刻意是另一支 Cmd 而不是「persona 給空就匿名」：忘了帶 persona 與刻意匿名在輸入上同形，併成一支會讓忘記的人安靜少領。</para>
    /// </summary>
    protected virtual bool IsSystem => false;

    protected override bool AcceptsTargetDataRoot => true;

    public override string Summary => "酒館發文（組訊息在 Senate，寫入交給酒館 Server）—— **不需要 Unity Editor**";

    public override string Details =>
        "取代 `senate ucmd run Tavern --arg op=post` 的一般發文。身分／顯示名／頭像／詞典附註由本 Cmd 補，\n"
        + "寫入、發薪、@mention 通知由酒館 Server（`tavern-write`，沒開會自動起）做。\n"
        + "⚠ 發文結果三態：exit 0 已發／exit 6 **確定沒發**（補發安全）／exit 7 **不知道**（先 `tavern-query kind=seq` 回讀，⛔ 別補發）。\n"
        + "⚠ 酒館 Server 不在（確定還沒送出）⇒ **排進它的 queue**：exit 0 ＋ `queued=1`／`queued_cmd_id`，⛔ 沒有 post_seq，⛔ 不要補發（TASK-0372）。\n"
        + "⚠ tag=commit／task-assign／task-ack 的 meta 必填欄位照 T06.3 驗（與 Editor 同一支），不合 ⇒ exit 2 確定沒發。\n"
        + "⚠ alter 配對（上一則是自己的 alter 搭檔、間隔不足）⇒ **不當下寫**：排進酒館 Server 的延後發文匣，\n"
        + "   exit 0 ＋ `scheduled=1`／`deferred_until`，⛔ 沒有 post_seq（到點才配號）。`alter-pacing-bypass=true` 可跳過。\n"
        + "   酒保 CLI 指令自動打 cli-cmd 標記、不附詞典；tag=creative 由寫入端寄留念信。\n"
        + "⚠ persona 必填但**不必在線**（下線後照樣能發）；匿名發言不走本入口。`status` 只在有 lock 時更新。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("tavern-post --arg persona=Template --arg-file body=D:/tmp/msg.md");

    protected override string CliNextHint => "";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>();
            if (IsSystem)
            {
                aSpecs.Add(new SCP_CmdArgSpec("sender", "系統發言的身分（例 `tavern-keeper`、後台頁選的身分）—— **必填**，⛔ 不猜", iRequired: true));
                aSpecs.Add(new SCP_CmdArgSpec("sender_name", "顯示名（不給 ⇒ 新銀行帳戶的顯示名 ⇒ 都沒有就顯示 id）"));
                aSpecs.Add(new SCP_CmdArgSpec("project", "哪個專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略"));
            }
            else aSpecs.AddRange(MorningSpecs());
            aSpecs.Add(TargetDataRootSpec());
            aSpecs.Add(new SCP_CmdArgSpec("body", "發言內文。長內文走 --arg-file", iRequired: true));
            aSpecs.Add(new SCP_CmdArgSpec("room", "房間 id", iDefault: SCP_TavernRegion.DefaultRoom));
            aSpecs.Add(new SCP_CmdArgSpec("reply_to", "回覆哪一則（seq）"));
            aSpecs.Add(new SCP_CmdArgSpec("meta",
                "訊息 meta：JSON 物件，或舊格式 `k:v;k:v`（與 Editor op=post 同一套解析）"));
            aSpecs.Add(new SCP_CmdArgSpec("tag", "meta.tag 的捷徑（與 meta 裡的 tag 同時給時以本參數為準）"));
            aSpecs.Add(new SCP_CmdArgSpec("refs", "附檔路徑（repo 相對或絕對，多檔用 | 分隔）—— 同事 Read 該路徑看圖"));
            if (!IsSystem) aSpecs.Add(new SCP_CmdArgSpec("status", "順手更新自己的 now_status（一句話；在線清單看得到）"));
            aSpecs.Add(new SCP_CmdArgSpec("timeout", "等酒館 Server 回執的秒數（預設 30）"));
            aSpecs.Add(new SCP_CmdArgSpec("dry_run",
                "1 ＝ 只組訊息、印出要交給寫入端的 JSON，**不送出**（跟 Editor 版並排比欄位用）",
                iChoices: new[] { "0", "1" }));
            return aSpecs;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aPersona = IsSystem ? "" : iArgs.Get("persona").Trim();
        string aSender = IsSystem ? iArgs.Get("sender").Trim() : "";
        string aBody = iArgs.Get("body");
        string aRoom = iArgs.Get("room").Trim();
        if (aRoom.Length == 0) aRoom = SCP_TavernRegion.DefaultRoom;
        // 系統發言沒有 persona ⇒ 沒有信件夾可落回傳檔（⛔ 不造一個 `_system` 夾）——結果以 CLI 印的與 values 為準。
        string aPath = IsSystem ? "" : SCP_LettersPaths.CmdPayload(iRoots.Letters, aPersona, "tavern_post");
        var aSb = new StringBuilder();
        aSb.AppendLine($"# {Name} {(IsSystem ? "sender=" + aSender : "persona=" + aPersona)} room={aRoom}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
        aSb.AppendLine();

        if (aBody.Trim().Length == 0) return Block(aPath, aSb, ioResult, 2, "body 是空的");

        // ⚠ 不檢查在線（Tim 2026-09-27）：lock 是擋「同一個 persona 重複登入」的，不是發言許可。
        //   lock 只拿來給 now_status 取 session 鍵（session_key＋locked_at）；⛔ 也不驗 session token。
        SCP_PersonaStatus? aLock = IsSystem ? null : SCP_PersonaLetters.ReadPersonaLock(iRoots.LettersRoot, aPersona);
        if (aLock != null && aLock.Online != SCP_PersonaOnline.Online) aLock = null;   // 壞 lock 當沒有，⛔ 不拿它的欄位

        Dictionary<string, string> aMeta = ParseMeta(iArgs.Get("meta"));
        string aTag = iArgs.Get("tag").Trim();
        if (aTag.Length > 0) aMeta["tag"] = aTag;

        // T06.3 schema（TASK-0311）：不合就是**確定沒發**（Editor 版同樣在寫入前 reject），⛔ 不是「交回 Editor」——
        //   Editor 會用同一支判出同一個結果。
        string? aSchema = SCP_TavernMetaSchema.Validate(aMeta);
        if (aSchema != null) return Block(aPath, aSb, ioResult, 2, aSchema + "\n  ⇒ **確定沒發**：補齊 meta 後重跑是安全的");

        int? aReplyTo = null;
        string aReplyRaw = iArgs.Get("reply_to").Trim();
        if (aReplyRaw.Length > 0)
        {
            if (!int.TryParse(aReplyRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aR) || aR <= 0)
                return Block(aPath, aSb, ioResult, 2, $"reply_to 不是正整數：'{aReplyRaw}'");
            aReplyTo = aR;
        }

        SCP_TavernPostDraft aDraft = IsSystem
            ? SCP_TavernPostCompose.BuildSystem(iRoots.DataRoot, iRoots.BankRoot, iRoots.ProjectRoot, iRoots.GlossaryRoot,
                                                aRoom, aSender, iArgs.Get("sender_name"), aBody, aMeta)
            : SCP_TavernPostCompose.Build(iRoots.DataRoot, iRoots.LettersRoot, iRoots.ProjectRoot,
                                          iRoots.GlossaryRoot, iRoots.Region, aRoom, aPersona, aBody, aMeta);
        foreach (string n in aDraft.Notes) ioResult.Lines.Add("⚠ " + n);
        if (aDraft.Message == null) return Block(aPath, aSb, ioResult, 1, "發文被拒：" + aDraft.Error);
        aDraft.Message.ReplyTo = aReplyTo;
        foreach (string aRef in ParseRefs(iArgs.Get("refs"), iRoots.ProjectRoot, ioResult))
            aDraft.Message.Refs.Add(new SCP_TavernRef { Path = aRef });

        string aJson = SCP_TavernWriter.Serialize(aDraft.Message);

        // ④ alter 配對延遲（TASK-0312，判準 SCP_TavernAlterPacing —— 與 Editor 同一支）。讀不到上一則 ⇒ 不延遲（Editor 版同一側）。
        TimeSpan? aWait = null;
        // 系統發言沒有 alter 搭檔（配對表只收 persona 的 agent）⇒ 不做延遲；而延後匣的檔要掛 persona，系統發言掛不上。
        if (!IsSystem) try
        {
            List<SCP_TavernMessage> aLast = SCP_TavernRead.Tail(iRoots.DataRoot, aRoom, 1);
            if (aLast.Count > 0)
                aWait = SCP_TavernAlterPacing.Remaining(aMeta, aDraft.Message.SenderId, aLast[0].SenderId, aLast[0].Ts, DateTime.UtcNow);
        }
        catch (Exception e) { ioResult.Lines.Add("⚠ 讀不到本房最後一則（" + e.Message + "）—— 不做 alter 延遲"); }

        if (iArgs.Get("dry_run").Trim() == "1")
        {
            if (aWait.HasValue)
            {
                ioResult.AddValue("would_defer_sec", ((int)Math.Ceiling(aWait.Value.TotalSeconds)).ToString(CultureInfo.InvariantCulture));
                ioResult.Lines.Add($"· dry_run：上一則是 alter 搭檔 ⇒ 實發時會**延後 {aWait.Value.TotalSeconds:F0} 秒**（排進延後發文匣）");
            }
            // seq／ts／uuid 由寫入端配 ⇒ 這裡印的 ts 是空的，那是「還沒配」不是「沒有」。
            aSb.AppendLine("## dry_run（⛔ 沒有送出）");
            aSb.AppendLine("```json");
            aSb.AppendLine(aJson);
            aSb.AppendLine("```");
            TryWritePayload(aPath, aSb, ioResult);
            ioResult.Lines.Add("· dry_run：訊息已組好、**沒有送出**（ts／seq／uuid 由寫入端配，這裡是空的）");
            ioResult.Lines.Add(aJson);
            ioResult.AddValue("dry_run", "1");
            return NullIfEmpty(aPath);
        }

        if (aWait.HasValue) return Defer(iRoots, aRoom, aPersona, aJson, aWait.Value, aPath, aSb, ioResult);

        string aTimeout = iArgs.Get("timeout");
        // Server 不在而這一筆確定還沒送出 ⇒ 排進它的 queue（TASK-0372）；⛔ 不要再直接 Dispatch("tavern-write")。
        SCP_CmdResult aWrite = SenateTavernWrite.WriteOrQueue(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = iRoots.DataRoot,
            ["room"] = aRoom,
            ["msg_json"] = aJson,
            ["timeout"] = aTimeout.Length > 0 ? aTimeout : "30",
        });
        if (SenateTavernWrite.IsQueued(aWrite)) return Queued(aWrite, aRoom, aPath, aSb, ioResult);
        string aSeq = Value(aWrite, "seq");
        string aFailure = Value(aWrite, "delegate_failure");
        if (!aWrite.Ok || aSeq.Length == 0)
        {
            // 三態：逾時／未知 ＝ 不知道（先回讀，別補發）；其餘 ＝ 確定沒發。（morning-intro 同一判準）
            bool aUnknown = aFailure == "timeout" || aFailure == "unknown";
            foreach (string l in aWrite.Lines) ioResult.Lines.Add("  │ " + l);
            aSb.AppendLine("## blocked");
            aSb.AppendLine(aUnknown
                ? $"- reason: 酒館寫入**結果不明**（delegate_failure={aFailure}）—— ⛔ 別直接補發，先 `senate cmd tavern-query --arg kind=tail --arg room={aRoom}` 回讀"
                : $"- reason: 酒館寫入**確定沒發**（delegate_failure={(aFailure.Length > 0 ? aFailure : "exit " + aWrite.ExitCode)}）—— 修好後重跑是安全的");
            TryWritePayload(aPath, aSb, ioResult);
            ioResult.ExitCode = aUnknown ? 7 : 6;
            ioResult.Lines.Add(aUnknown ? "✗ 發文結果不明（exit 7）—— 先回讀，⛔ 別補發" : "✗ 發文確定沒發（exit 6）—— 可以重跑");
            if (aFailure.Length > 0) ioResult.AddValue("delegate_failure", aFailure);
            return NullIfEmpty(aPath);
        }

        // ⚠ 到這裡訊息**已經發了** —— 之後任何一步失敗都只能是警告，⛔ 不能讓 exit code 說「失敗」
        //   （2026-09-27 morning-intro 就是這樣：已發、回傳檔 Replace 丟例外 ⇒ exit 70，而照直覺重打會發兩次）。
        ioResult.Lines.Add($"✓ 已發：{aRoom} seq {aSeq}");
        ioResult.AddValue("post_seq", aSeq);
        ioResult.AddValue("post_room", aRoom);
        aSb.AppendLine("## verify（讀回的事實）");
        aSb.AppendLine($"- seq: **{aSeq}**（room `{aRoom}`）");
        string aMsgPath = Value(aWrite, "path");
        aSb.AppendLine($"- message: `{aMsgPath}`（exists={(aMsgPath.Length > 0 && File.Exists(aMsgPath))}）");
        foreach (var kv in aWrite.Values)
            if (kv.Key.StartsWith("pay_", StringComparison.Ordinal) || kv.Key.StartsWith("mention_", StringComparison.Ordinal))
            {
                aSb.AppendLine($"- {kv.Key}: {kv.Value}");
                ioResult.AddValue(kv.Key, kv.Value);
                if (kv.Key == "pay_warning") ioResult.Lines.Add("⚠ 發薪（訊息已發，這一則可能沒領到）：" + kv.Value);
            }

        string aStatus = IsSystem ? "" : iArgs.Get("status").Trim();
        if (aStatus.Length > 0)
        {
            // now_status 綁在這一場登入上（讀取端比對 session_key＋locked_at）⇒ 沒有 lock 就沒有「這一場」可掛（Editor 版同樣 skip）。
            string? aStatusErr = aLock == null ? "沒有 lock（未登入）—— now_status 掛在登入那一場上" : TryWriteNowStatus(iRoots, aPersona, aLock, aStatus);
            string aLine = aStatusErr == null ? $"now_status ← {aStatus}" : $"⚠ now_status 沒更新（{aStatusErr}）—— 訊息已發，不影響";
            aSb.AppendLine("- " + aLine);
            ioResult.Lines.Add(aStatusErr == null ? "✓ " + aLine : aLine);
        }
        TryWritePayload(aPath, aSb, ioResult);
        return NullIfEmpty(aPath);
    }

    // ── ③.5 已排隊（TASK-0372）──────────────────────────────────────────
    /// <summary>
    /// 酒館 Server 不在、這一則已排進它的 queue。exit 0（⛔ 不是失敗：補發會多一則），⛔ 沒有 post_seq。
    /// <para>⚠ `🔢 queued = 1` 這一行是**對外介面**：Unity 的 `UCL_TavernSenatePost` 認它（否則「exit 0 沒 seq」會被判成不知道）。</para>
    /// <para>⚠ `status`（now_status）這一趟不更新 —— 那一格說的是「我現在在做什麼」，綁在訊息真的發出去上。</para>
    /// </summary>
    static string? Queued(SCP_CmdResult iWrite, string iRoom, string iPath, StringBuilder ioSb, SCP_CmdResult ioResult)
    {
        foreach (string l in iWrite.Lines) ioResult.Lines.Add("  │ " + l);
        string aCmdId = Value(iWrite, "queued_cmd_id");
        string aWhy = Value(iWrite, "queued_because");
        ioResult.Lines.Add($"📥 已排隊：酒館 Server 不在（{aWhy}）⇒ 這一則排進它的 queue，起來後送出（**還沒有 seq**）。⛔ 不要補發。");
        ioResult.AddValue("queued", "1");
        ioResult.AddValue("queued_cmd_id", aCmdId);
        ioResult.AddValue("post_room", iRoom);
        ioSb.AppendLine("## queued（TASK-0372）");
        ioSb.AppendLine($"- 酒館 Server 不在（delegate_failure={aWhy}）⇒ 已排進它的 queue：cmd_id `{aCmdId}`");
        ioSb.AppendLine("- Server 起來後的下一個心跳送出：配號＋發薪＋@ 照常。⛔ **沒有 seq 不代表沒發成 —— 不要補發**（補了就是兩則）。");
        ioSb.AppendLine($"- 要確認送出了：{SCP_CmdRegistry.Invoke("tavern-query --arg kind=tail --arg room=" + iRoom)}");
        TryWritePayload(iPath, ioSb, ioResult);
        return NullIfEmpty(iPath);
    }

    // ── ④ alter 延後發文（TASK-0312）──────────────────────────────────

    /// <summary>
    /// 放進酒館 Server 的延後發文匣，當下回「已排程」。⛔ 沒有 post_seq —— 到點才配號，那是「還沒發」不是「發了」。
    /// <para>酒館 Server 沒在跑 ⇒ 順手拉一顆（同 `tavern-write` 的 autostart）；拉不起來也照樣排進去，
    /// 並明說「要等它下次起來才會送」（晚到，不會丟）。</para>
    /// </summary>
    string? Defer(SCP_MorningRoots iRoots, string iRoom, string iPersona, string iJson, TimeSpan iWait,
                 string iPath, StringBuilder ioSb, SCP_CmdResult ioResult)
    {
        if (ServerDelegateCmd.RepoRootProvider == null)
            return Block(iPath, ioSb, ioResult, 70, "宿主沒有裝上 repo 根來源 —— 找不到酒館 Server 的延後發文匣（程式錯誤，不是用法錯）；**確定沒發**");
        string aRepoRoot = ServerDelegateCmd.RepoRootProvider();
        string aServerRoot = SenatePaths.ServerRoot(aRepoRoot, SCP.Core.Proc.SCP_ServerIds.Tavern);
        DateTime aDue = DateTime.UtcNow + iWait;
        string aFile;
        try { aFile = SenateTavernDeferred.Schedule(aServerRoot, iRoots.DataRoot, iRoom, iJson, iPersona, aDue); }
        catch (Exception e) { return Block(iPath, ioSb, ioResult, 6, "延後發文排不進匣子（" + e.Message + "）—— **確定沒發**，修好後重跑是安全的"); }

        string aServerNote = "";
        try
        {
            if (!ServerHost.Probe(aRepoRoot, SCP.Core.Proc.SCP_ServerIds.Tavern).IsRunning
                && !ServerSpawn.TrySpawn(aRepoRoot, SCP.Core.Proc.SCP_ServerIds.Tavern, out _, out string aSpawnErr))
                aServerNote = "⚠ 酒館 Server 沒在跑、也拉不起來（" + aSpawnErr + "）—— 到點後要等它下次起來才會送（晚到，不會丟）";
        }
        catch (Exception e) { aServerNote = "⚠ 量不到酒館 Server 在不在（" + e.Message + "）—— 到點後由它送；它沒在跑就等它下次起來"; }

        string aLocal = aDue.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ioResult.Lines.Add($"⏳ 已排程：上一則是自己的 alter 搭檔 ⇒ 延後 {iWait.TotalSeconds:F0} 秒，約 {aLocal} 由酒館 Server 發出（**還沒配號**）");
        if (aServerNote.Length > 0) ioResult.Lines.Add(aServerNote);
        ioResult.AddValue("scheduled", "1");
        ioResult.AddValue("deferred_until", aDue.ToString("o", CultureInfo.InvariantCulture));
        ioResult.AddValue("deferred_path", aFile);
        ioSb.AppendLine("## scheduled（⛔ 還沒發）");
        ioSb.AppendLine($"- 延後 {iWait.TotalSeconds:F0} 秒（alter 配對間隔，判準 `SCP_TavernAlterPacing`）；約 {aLocal} 由酒館 Server 送出");
        ioSb.AppendLine($"- 匣子裡那一份：`{aFile}`（到點後刪除；留成 `.claimed` ＝ 認領了但不知道送出去沒）");
        if (aServerNote.Length > 0) ioSb.AppendLine("- " + aServerNote);
        ioSb.AppendLine("- 到點之後要確認：`senate cmd tavern-query --arg kind=tail`（⛔ 別在那之前補發 —— 那會發兩則）");
        TryWritePayload(iPath, ioSb, ioResult);
        return NullIfEmpty(iPath);
    }

    // ── 解析（與 Editor `Cmd_Tavern.ParseMeta`／`ParseRefs` 同一套規則）────────

    static Dictionary<string, string> ParseMeta(string iRaw)
    {
        var aOut = new Dictionary<string, string>(StringComparer.Ordinal);
        string aTrim = (iRaw ?? "").Trim();
        if (aTrim.Length == 0) return aOut;
        if (aTrim[0] == '{')
        {
            try
            {
                SCP_JsonData aJson = SCP_JsonData.Parse(aTrim);
                if (aJson.IsObject)
                {
                    foreach (string k in aJson.Keys)
                    {
                        if (k.Length == 0) continue;
                        SCP_JsonData v = aJson[k];
                        aOut[k] = v.IsNull ? "" : (v.IsObject || v.IsArray) ? v.ToJson(false) : v.AsString();
                    }
                    if (aOut.Count > 0) return aOut;
                }
            }
            catch (Exception) { aOut.Clear(); }   // JSON 解析失敗 ⇒ 退到舊格式（Editor 版同一側）
        }
        foreach (string aPair in aTrim.Split(';'))
        {
            int i = aPair.IndexOf(':');
            if (i <= 0) continue;
            string k = aPair.Substring(0, i).Trim();
            if (k.Length > 0) aOut[k] = aPair.Substring(i + 1).Trim();
        }
        return aOut;
    }

    /// <summary>`a|b|c` → repo 相對的 posix 路徑；repo 外的絕對路徑照原樣存並警告（mirror 撈不到它）。</summary>
    static List<string> ParseRefs(string iRaw, string iProjectRoot, SCP_CmdResult ioResult)
    {
        var aOut = new List<string>();
        string aRoot = Path.GetFullPath(iProjectRoot).Replace('\\', '/').TrimEnd('/') + "/";
        foreach (string aPart in (iRaw ?? "").Split('|'))
        {
            string p = aPart.Trim();
            if (p.Length == 0) continue;
            if (!Path.IsPathRooted(p)) { aOut.Add(p.Replace('\\', '/')); continue; }
            string aFull = Path.GetFullPath(p).Replace('\\', '/');
            if (aFull.StartsWith(aRoot, StringComparison.OrdinalIgnoreCase)) aOut.Add(aFull.Substring(aRoot.Length));
            else
            {
                ioResult.Lines.Add($"⚠ refs 收到 repo 外的絕對路徑，照原樣存 —— mirror 撈不到它：{aFull}");
                aOut.Add(aFull);
            }
        }
        return aOut;
    }

    // ── 寫入（訊息發出之後的兩件事：失敗只警告）──────────────────────

    /// <summary>格式同 Editor `UCL_AwakeningService.UpdateNowStatus`（讀取端比對 session_key＋locked_at）。</summary>
    static string? TryWriteNowStatus(SCP_MorningRoots iRoots, string iPersona, SCP_PersonaStatus iLock, string iStatus)
    {
        try
        {
            var aStatus = SCP_JsonData.NewObject();
            aStatus["session_key"] = iLock.SessionKey ?? "";
            aStatus["locked_at"] = iLock.LockedAt ?? "";
            aStatus["now_status"] = iStatus;
            aStatus["status_updated_at"] = SCP_Morning.NowIso();
            SCP_CmdPayload.Write(SCP_LettersPaths.NowStatusPath(iRoots.Letters, iPersona),
                SCP_JsonWriter.Write(aStatus, SCP_JsonStyle.UclLegacy));
            return null;
        }
        catch (Exception e) { return $"{e.GetType().Name}: {e.Message}"; }
    }

    static void TryWritePayload(string iPath, StringBuilder iSb, SCP_CmdResult ioResult)
    {
        if (iPath.Length == 0) return;   // 系統發言：沒有信件夾可落（結果在 CLI 輸出與 values）
        try { SCP_CmdPayload.Write(iPath, iSb.ToString()); }
        catch (Exception e) { ioResult.Lines.Add($"⚠ 回傳檔沒寫出來（{e.GetType().Name}: {e.Message}）—— 上面的結果以 CLI 印的為準"); }
    }

    static string? Block(string iPath, StringBuilder ioSb, SCP_CmdResult ioResult, int iExit, string iReason)
    {
        ioSb.AppendLine("## blocked");
        ioSb.AppendLine("- reason: " + iReason);
        TryWritePayload(iPath, ioSb, ioResult);
        ioResult.ExitCode = iExit;
        foreach (string l in iReason.Split('\n')) ioResult.Lines.Add((ioResult.Lines.Count > 0 && l.StartsWith("  ") ? "" : "⛔ ") + l);
        return NullIfEmpty(iPath);
    }

    static string Value(SCP_CmdResult iR, string iKey)
    {
        foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
        return "";
    }

    /// <summary>系統發言沒有回傳檔 ⇒ 空字串要回 null（宿主才不會印一行空的「📄 回傳檔」）。</summary>
    static string? NullIfEmpty(string iPath) => iPath.Length > 0 ? iPath : null;
}

// ===========================================================
// 區塊職責：`senate cmd tavern-post-system` —— **沒有 persona** 的酒館發言（TASK-0366）。
// 物理意義：Unity `Cmd_Tavern op=post` 匿名那條路的搬家終點：酒保廣播（書的捐贈／打賞）、Unity 酒館頁打字、沒帶 persona 的棋局廣播。
//          與 `tavern-post` 同一份流程（meta schema、refs、reply_to、寫入三態），只差身分：`sender` 點名、不計酬。
// ⛔ agent 自己說話不走這裡 —— 那是 `tavern-post`（帶 persona 才領得到薪水）。
// ===========================================================
public sealed class Cmd_TavernPostSystem : Cmd_TavernPost
{
    public override string Name => "tavern-post-system";

    public override string Summary => "酒館**系統**發言（沒有 persona：酒保廣播、後台頁打字）—— 不計酬；agent 自己說話走 tavern-post";

    public override string Details =>
        "身分由 `sender` 點名（⛔ 不猜）；顯示名：`sender_name` → 新銀行帳戶顯示名 → id。沒有 persona ⇒ 寫入端不計酬。\n"
        + "與 tavern-post 共用：meta schema（exit 2 確定沒發）、refs、reply_to、CLI 指令判定、寫入三態（0 已發／6 確定沒發／7 不知道）。\n"
        + "不做的：alter 延遲（系統發言沒有搭檔）、now_status、回傳檔（沒有信件夾）。\n"
        + "Unity Editor 呼叫時帶 `target_data_root=<它自己的資料根>` ⇒ 落在同一個專案（比不到就擋）。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("tavern-post-system --arg sender=tavern-keeper --arg-file body=D:/tmp/msg.md");

    protected override bool IsSystem => true;
}
