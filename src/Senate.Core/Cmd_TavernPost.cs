// 區塊職責：`senate cmd tavern-post` —— 一般酒館發文，**不需要 Unity Editor**（TASK-0308，epic 0295 ③ 第一刀）。
// 物理意義：Tim 2026-09-27：「發文要走 ucmd run Tavern 是否可以改成 Senate CLI 的 cmd」。
//          路是 `morning-intro` 已經走通的那一條：`SCP_TavernPostCompose.Build` 在 Senate 組訊息
//          （sender_id／顯示名／頭像／詞典附註），再交給 `tavern-write`（酒館 Server：配號建檔＋發薪＋@mention）。
//          本檔只多做三件 intro 用不到的事：meta／refs／reply_to 的解析、`status` → now_status、以及下面那張擋下清單。
// 數值影響：寫一則酒館訊息（經 Server）＋ 回傳檔 `letters/<P>/cmd/tavern_post.md`；帶 `status` 時寫 `cmd/now_status.json`。
//
// ⛔ **Editor `Cmd_Tavern.Op_Post` 還有三段前處理沒搬** —— 遇到就 exit 2 擋下並指回 `ucmd run Tavern`，
//   ⛔ 不靜默略過（略過的樣子是「訊息發出去了、只是少做一件事」，而那件事沒有任何一層會叫）：
//   ①（已搬，TASK-0311）meta schema：tag=commit／task-assign／task-ack ⇒ `SCP_TavernMetaSchema.Validate`（與 Editor 同一支）
//   ② tag=creative 的歸檔信（`TryArchiveCreativePost`）
//   ③ CLI 指令（body 第一個字是酒保 CLI 前綴）—— Editor 會打 `cli-cmd` tag 並跳過詞典附註
//   ④ alter 配對延遲（上一則是自己的 alter 搭檔，或帶 `alter-*` meta）
//   要搬的那天，把判準搬進 `SCP_TavernPostCompose`（兩個宿主共用），再從這張清單刪掉一格。
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

public sealed class Cmd_TavernPost : MorningLocalCmd
{
    public override string Name => "tavern-post";

    public override string Summary => "酒館發文（組訊息在 Senate，寫入交給酒館 Server）—— **不需要 Unity Editor**";

    public override string Details =>
        "取代 `senate ucmd run Tavern --arg op=post` 的一般發文。身分／顯示名／頭像／詞典附註由本 Cmd 補，\n"
        + "寫入、發薪、@mention 通知由酒館 Server（`tavern-write`，沒開會自動起）做。\n"
        + "⚠ 發文結果三態：exit 0 已發／exit 6 **確定沒發**（補發安全）／exit 7 **不知道**（先 `tavern-query kind=seq` 回讀，⛔ 別補發）。\n"
        + "⚠ tag=commit／task-assign／task-ack 的 meta 必填欄位照 T06.3 驗（與 Editor 同一支），不合 ⇒ exit 2 確定沒發。\n"
        + "⛔ 還沒搬的前處理會 **exit 2 擋下**並指回 `ucmd run Tavern`：tag=creative、\n"
        + "   CLI 指令（body 以酒保 CLI 前綴開頭）、alter 配對（上一則是自己的 alter 搭檔）、`alter-*` meta。\n"
        + "⚠ persona 必填但**不必在線**（下線後照樣能發）；匿名發言不走本入口。`status` 只在有 lock 時更新。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("tavern-post --arg persona=Template --arg-file body=D:/tmp/msg.md");

    protected override string CliNextHint => "";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>(MorningSpecs());
            aSpecs.Add(new SCP_CmdArgSpec("body", "發言內文。長內文走 --arg-file", iRequired: true));
            aSpecs.Add(new SCP_CmdArgSpec("room", "房間 id", iDefault: SCP_TavernRegion.DefaultRoom));
            aSpecs.Add(new SCP_CmdArgSpec("reply_to", "回覆哪一則（seq）"));
            aSpecs.Add(new SCP_CmdArgSpec("meta",
                "訊息 meta：JSON 物件，或舊格式 `k:v;k:v`（與 Editor op=post 同一套解析）"));
            aSpecs.Add(new SCP_CmdArgSpec("tag", "meta.tag 的捷徑（與 meta 裡的 tag 同時給時以本參數為準）"));
            aSpecs.Add(new SCP_CmdArgSpec("refs", "附檔路徑（repo 相對或絕對，多檔用 | 分隔）—— 同事 Read 該路徑看圖"));
            aSpecs.Add(new SCP_CmdArgSpec("status", "順手更新自己的 now_status（一句話；在線清單看得到）"));
            aSpecs.Add(new SCP_CmdArgSpec("timeout", "等酒館 Server 回執的秒數（預設 30）"));
            aSpecs.Add(new SCP_CmdArgSpec("dry_run",
                "1 ＝ 只組訊息、印出要交給寫入端的 JSON，**不送出**（跟 Editor 版並排比欄位用）",
                iChoices: new[] { "0", "1" }));
            return aSpecs;
        }
    }

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aPersona = iArgs.Get("persona").Trim();
        string aBody = iArgs.Get("body");
        string aRoom = iArgs.Get("room").Trim();
        if (aRoom.Length == 0) aRoom = SCP_TavernRegion.DefaultRoom;
        string aPath = SCP_LettersPaths.CmdPayload(iRoots.Letters, aPersona, "tavern_post");
        var aSb = new StringBuilder();
        aSb.AppendLine($"# tavern-post persona={aPersona} room={aRoom}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
        aSb.AppendLine();

        if (aBody.Trim().Length == 0) return Block(aPath, aSb, ioResult, 2, "body 是空的");

        // ⚠ 不檢查在線（Tim 2026-09-27）：lock 是擋「同一個 persona 重複登入」的，不是發言許可。
        //   lock 只拿來給 now_status 取 session 鍵（session_key＋locked_at）；⛔ 也不驗 session token。
        SCP_PersonaStatus? aLock = SCP_PersonaLetters.ReadPersonaLock(iRoots.LettersRoot, aPersona);
        if (aLock != null && aLock.Online != SCP_PersonaOnline.Online) aLock = null;   // 壞 lock 當沒有，⛔ 不拿它的欄位

        Dictionary<string, string> aMeta = ParseMeta(iArgs.Get("meta"));
        string aTag = iArgs.Get("tag").Trim();
        if (aTag.Length > 0) aMeta["tag"] = aTag;

        string aHint = "\n  ⇒ 這一則請改走 `senate ucmd run Tavern --persona " + aPersona
            + " --arg op=post …`（要 Editor 開著）—— 本入口**確定沒發**";
        string? aNotPorted = NotPortedReason(iRoots.DataRoot, aBody, aMeta);
        if (aNotPorted != null) return Block(aPath, aSb, ioResult, 2, aNotPorted + aHint);
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

        SCP_TavernPostDraft aDraft = SCP_TavernPostCompose.Build(iRoots.DataRoot, iRoots.LettersRoot, iRoots.ProjectRoot,
            iRoots.Region, aRoom, aPersona, aBody, aMeta);
        foreach (string n in aDraft.Notes) ioResult.Lines.Add("⚠ " + n);
        if (aDraft.Message == null) return Block(aPath, aSb, ioResult, 1, "發文被拒：" + aDraft.Error);
        string? aAlter = AlterPairReason(iRoots.DataRoot, aRoom, aDraft.Message.SenderId);
        if (aAlter != null) return Block(aPath, aSb, ioResult, 2, aAlter + aHint);
        aDraft.Message.ReplyTo = aReplyTo;
        foreach (string aRef in ParseRefs(iArgs.Get("refs"), iRoots.ProjectRoot, ioResult))
            aDraft.Message.Refs.Add(new SCP_TavernRef { Path = aRef });

        string aJson = SCP_TavernWriter.Serialize(aDraft.Message);
        if (iArgs.Get("dry_run").Trim() == "1")
        {
            // seq／ts／uuid 由寫入端配 ⇒ 這裡印的 ts 是空的，那是「還沒配」不是「沒有」。
            aSb.AppendLine("## dry_run（⛔ 沒有送出）");
            aSb.AppendLine("```json");
            aSb.AppendLine(aJson);
            aSb.AppendLine("```");
            TryWritePayload(aPath, aSb, ioResult);
            ioResult.Lines.Add("· dry_run：訊息已組好、**沒有送出**（ts／seq／uuid 由寫入端配，這裡是空的）");
            ioResult.Lines.Add(aJson);
            ioResult.AddValue("dry_run", "1");
            return aPath;
        }

        string aTimeout = iArgs.Get("timeout");
        SCP_CmdResult aWrite = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = iRoots.DataRoot,
            ["room"] = aRoom,
            ["msg_json"] = aJson,
            ["timeout"] = aTimeout.Length > 0 ? aTimeout : "30",
        });
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
            return aPath;
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
            }

        string aStatus = iArgs.Get("status").Trim();
        if (aStatus.Length > 0)
        {
            // now_status 綁在這一場登入上（讀取端比對 session_key＋locked_at）⇒ 沒有 lock 就沒有「這一場」可掛（Editor 版同樣 skip）。
            string? aStatusErr = aLock == null ? "沒有 lock（未登入）—— now_status 掛在登入那一場上" : TryWriteNowStatus(iRoots, aPersona, aLock, aStatus);
            string aLine = aStatusErr == null ? $"now_status ← {aStatus}" : $"⚠ now_status 沒更新（{aStatusErr}）—— 訊息已發，不影響";
            aSb.AppendLine("- " + aLine);
            ioResult.Lines.Add(aStatusErr == null ? "✓ " + aLine : aLine);
        }
        TryWritePayload(aPath, aSb, ioResult);
        return aPath;
    }

    // ── 擋下清單（見檔頭 ①-④）──────────────────────────────────────

    /// <summary>
    /// 還沒搬進 Senate 的前處理（見檔頭 ②-④）。回 null ＝ Senate 做得完這一則；回字串 ＝ 要交回 Editor。
    /// <para>⚠ <see cref="SenateTavernPostGateway"/> 也呼叫這一支（TASK-0311）—— ⛔ 不在那邊另列一張清單。</para>
    /// </summary>
    public static string? NotPortedReason(string iDataRoot, string iBody, IReadOnlyDictionary<string, string> iMeta)
    {
        if (iMeta.TryGetValue("tag", out string? aTag) && string.Equals(aTag, "creative", StringComparison.Ordinal))
            return "tag=creative 的前處理（歸檔信）還沒搬進 Senate";
        foreach (string k in iMeta.Keys)
            if (k.StartsWith("alter-", StringComparison.Ordinal))
                return $"meta `{k}` 是 alter 配對延遲的旗標，而延遲還沒搬進 Senate";

        string? aPrefix = CliPrefix(iDataRoot);
        if (aPrefix != null)
        {
            string aTrim = iBody.TrimStart();
            int aEnd = 0;
            while (aEnd < aTrim.Length && !char.IsWhiteSpace(aTrim[aEnd])) aEnd++;
            if (string.Equals(aTrim.Substring(0, aEnd), aPrefix, StringComparison.OrdinalIgnoreCase))
                return $"body 以酒保 CLI 前綴 `{aPrefix}` 開頭 —— 那是指令不是對話，而 CLI 判定（打 cli-cmd tag、跳過詞典附註）還沒搬進 Senate";
        }
        return null;
    }

    /// <summary>
    /// alter 配對：上一則是自己的 alter 搭檔 ⇒ Editor 會延遲後才寫。讀不到就不擋（Editor 版同一側：沒有前一則＝不延遲）。
    /// ⚠ 用組好的訊息的 sender_id —— 那是 Editor 配對時用的同一個鍵（顯示身分，不是 persona）。
    /// </summary>
    public static string? AlterPairReason(string iDataRoot, string iRoom, string iSender)
    {
        string aSender = iSender;
        const string AlterSuffix = "-alter";
        string aPartner = aSender.EndsWith(AlterSuffix, StringComparison.Ordinal)
            ? aSender.Substring(0, aSender.Length - AlterSuffix.Length) : aSender + AlterSuffix;
        try
        {
            List<SCP_TavernMessage> aLast = SCP_TavernRead.Tail(iDataRoot, iRoom, 1);
            if (aLast.Count > 0 && aLast[0].SenderId == aPartner)
                return $"上一則是 alter 搭檔 `{aPartner}` 發的 —— 配對發言要延遲，而延遲還沒搬進 Senate";
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>酒保 CLI 前綴（`ChatTavern/bartender/cli_settings.json`）；CLI 關著或讀不到回 null。</summary>
    static string? CliPrefix(string iDataRoot)
    {
        try
        {
            string aPath = Path.Combine(iDataRoot, "ChatTavern", "bartender", "cli_settings.json");
            if (!File.Exists(aPath)) return null;
            SCP_JsonData aSettings = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
            if (!aSettings.GetBool("enabled", false)) return null;
            string aPrefix = aSettings.GetString("prefix", "cmd").Trim();
            return aPrefix.Length > 0 ? aPrefix : "cmd";
        }
        // ⚠ 讀壞時往「擋」那側倒：Editor 預設前綴是 cmd，放行一則指令的代價（群發打進別人輸入框）比擋一則對話大。
        catch (Exception) { return "cmd"; }
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
        return iPath;
    }

    static string Value(SCP_CmdResult iR, string iKey)
    {
        foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
        return "";
    }
}
