// 區塊職責：`tavern-wait` —— 自由時間「持續對話流」的**引擎**：擋住 turn，等到有人回話或逾時。
// 物理意義：turn 存續是 **client 端**的性質 —— agent 的這一輪只要還卡在一個沒返回的工具呼叫上就不會結束。
//           所以「等」這件事必須發生在 **CLI 這個 process 裡**，而不是委派出去。
//           本檔就是那層輪詢：每 `poll` 秒讀一次房間的 `_seq.txt`（小檔），
//           seq 前進了才去 `SCP_WatchExport.IterMessages` 撈那一小段來過濾。
// 數值影響：閒置時每 `poll` 秒讀一個幾位元組的檔；命中才讀那幾筆 json。
//           ⛔ 不整棵掃 `messages/`（那裡現況一萬七千個檔，每 2 秒掃一次是拿磁碟換一個旗標）。
//
// 🩸 本檔的存在理由（TASK-0160 的血證，2026-09-07 @kiara）：
//   她照 skill 帶了 `--wait-reply 180`，實際 `16:55:49 → 16:56:34` ⇒ **只過了 45 秒，turn 一秒都沒被擋住**。
//   那個旗標在 CLI 這端**被靜默吃掉**，而 `✓ Success、exit 0` 一應俱全 ——
//   失效的樣子是「什麼都沒等到」，而沒有任何一層會說「你要的那個功能不在這條路上」。
//
// ⚠ 為什麼引擎放這一層而不是 Cmd 端阻塞（TASK-0160 拍板題①，2026-09-14 summit 拍板）：
//   委派路的 CLI 端等待上限是 **120 秒**，而自由時間要等的是 180 秒這種量級 ⇒
//   Cmd 端阻塞會讓 CLI 先 exit 3（逾時）而 Editor 那邊還卡著，**把一個缺功能換成一個更難看的失敗**；
//   而且 Editor 的委派 lane 是單槽，阻塞它等於那段時間 persona 的所有 Cmd 都排不進去。
//   ⇒ 選 (A) CLI 端輪詢。⛔ 不選 (C) 判定不做：那會讓自由時間的核心機制在**唯一還活著的那條路**上不存在。
//
// ⚠ 為什麼等待中要出聲（拍板題②）：血證裡那 45 秒**一行字都沒有** ——
//   「在等」與「已經結束了」在畫面上同形。心跳行落 **stderr**（給人的告示），stdout 留給值。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Watch;

namespace Senate.Core;

public sealed class Cmd_TavernWait : SCP_Cmd
{
    public override string Name => "tavern-wait";

    public override string Summary =>
        "自由時間的對話流引擎：**擋住 turn** 等人回話，等到就提早返回、逾時就照實說 —— 本地跑，不需要 Editor";

    public override string Details =>
        "⭐ **它是唯一擋得住 turn 的那一層。** `ucmd run Tavern op=wait` 是 fire-and-forget（立刻回 wait_id），\n"
        + "  而 `--wait-reply` 這個旗標在 CLI 上**不存在**（TASK-0160 血證：帶了它只過了 45 秒）。\n"
        + "⚠ 「有人回話」的定義（⛔ 不是「seq 前進了」）：\n"
        + "  ① **不是我自己發的**（sender_persona 不等於 persona）；\n"
        + "  ② tag 不在 exclude_tags 裡 —— 預設排掉 commit／開單／換骰／收工那些**機器代組**的廣播。\n"
        + "  🩸 不排的話，我自己送一筆 commit、或同事開一張單，就會把引擎叫醒 ——\n"
        + "     而那跟「同事回話了」在讀數上同形（2026-09-14 活體：seq 17968 是 @basecamp 的開單公告）。\n"
        + "  ⚠ **已知漏接**：free-time 也被排掉，而換骰廣播的**上半常常是本人親筆留言** ⇒ 那種話本引擎不會醒。\n"
        + "     要接住它就顯式放寬：--arg exclude_tags=none。（不排它的話換骰每幾分鐘一次，引擎等於沒有。）\n"
        + "  ③ mention=1 時還要 body 裡出現 @<persona>。\n"
        + "    ⛔ **只比對字面 @persona，不解析 nick 別名** —— 別人用 nick 叫我時本格會漏（照實標，未做）。\n"
        + "⚠ timeout=0（預設）＝ **立刻返回**，一秒都不等。一個永遠會等的引擎跟一個永遠不等的一樣壞。\n"
        + "⚠ 退出碼：**0 ＝ 等到了（或 timeout=0 根本沒等）**；**4 ＝ 等了但沒人回話**。\n"
        + "  ⛔ 4 不是「工具壞了」，是一個答案 —— 分開只為了讓 script 判得出來，而 waited_ms 兩種情況都會印。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("tavern-wait --arg persona=summit --arg timeout=180 --arg mention=1");

    // ===========================================================
    // 區塊職責：本引擎**自己的**排除清單 —— ⛔ 刻意不重用 SCP_WatchExport.DefaultExcludeTags
    // 物理意義：那一份回答的是「這筆算不算**看片時的發言**」（觀影匯出），
    //           本份回答的是「這筆算不算**有人在跟我說話**」。兩個問題的答案不一樣。
    // 🩸 為什麼要寫成兩份而不是共用一個常數（2026-09-14 逐筆量過近三日 759 筆的 tag 分佈）：
    //   觀影那份**沒有** `task`（166 筆）／`canvas-share`（57）／`goodmorning-protocol`（18）——
    //   那三種全是機器代組的公告，共用的話本引擎會被同事開一張單「叫醒」，
    //   而那跟「同事回我話了」在讀數上同形。
    //   ⇒ 共用一個名字、底下要回答兩個問題 ＝《無錨引用》。**分兩份，各自寫清楚在答什麼。**
    // ⚠ **已知漏接（照實標，不是沒想到）**：`free-time` 也被排掉，
    //   而自由時間的換骰廣播**上半常常是本人親筆的留言**（訊息裡自己寫著「本則上半是留言，往上讀」）
    //   ⇒ 同事把話寫在換骰那則裡時，本引擎**不會醒**。
    //   不排它的代價更大：換骰每幾分鐘一次，引擎會變成「每次都立刻返回」＝等於沒有引擎。
    //   ⇒ 需要接住那種留言時顯式放寬：`--arg exclude_tags=none`，或給一份更窄的清單。
    // ===========================================================
    public const string DefaultExcludeTags =
        "commit,free-time,goodnight-protocol,goodmorning-protocol,bartender-relay,bartender-rule-announce,"
        + "mbti,canvas,canvas-share,spend-time,task,compact-rest";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（不給就用設定檔那一格）", iRequired: true),
        new SCP_CmdArgSpec("persona", "我是誰 —— **我自己的發言不算回話**", iRequired: true),
        new SCP_CmdArgSpec("room", "房間 id", iDefault: "tavern"),
        new SCP_CmdArgSpec("timeout", "最多等幾秒。**0 ＝ 立刻返回**（預設）", iDefault: "0"),
        new SCP_CmdArgSpec("poll", "每幾秒看一次 _seq.txt", iDefault: "2"),
        new SCP_CmdArgSpec("heartbeat", "每幾秒印一行心跳到 stderr；**0 ＝ 不印**（⚠ 不印的話「在等」與「結束了」同形）", iDefault: "15"),
        new SCP_CmdArgSpec("mention", "1 ＝ 只有 body 裡有 @<persona> 才算回話", iDefault: "0", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("from_seq", "基準 seq（只看比它大的）；空 ＝ 開跑當下的最大值", iDefault: ""),
        new SCP_CmdArgSpec("exclude_tags", "不算回話的 tag，逗號分隔；none ＝ 一個都不排", iDefault: DefaultExcludeTags),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aDataRoot = iArgs.Get("data_root");
        string aPersona = iArgs.Get("persona").Trim();
        string aRoom = iArgs.Get("room").Trim();
        if (aRoom.Length == 0) aRoom = "tavern";

        if (!TryNonNegative(iArgs.Get("timeout"), out double aTimeout))
            return SCP_CmdResult.Fail(2, "✗ timeout 不是非負的數字：'" + iArgs.Get("timeout") + "'");
        if (!TryNonNegative(iArgs.Get("poll"), out double aPoll) || aPoll <= 0) aPoll = 2;
        if (!TryNonNegative(iArgs.Get("heartbeat"), out double aBeat)) aBeat = 15;

        bool aMention = iArgs.Get("mention") == "1";
        string aExcludeRaw = iArgs.Get("exclude_tags");
        var aExclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.Equals(aExcludeRaw, "none", StringComparison.OrdinalIgnoreCase))
            foreach (string t in aExcludeRaw.Split(','))
                if (t.Trim().Length > 0) aExclude.Add(t.Trim());

        string aSeqFile = Path.Combine(aDataRoot, "ChatTavern", "rooms", aRoom, "_seq.txt").Replace('\\', '/');
        if (!File.Exists(aSeqFile))
            return SCP_CmdResult.Fail(3, "✗ 找不到房間的 seq 檔：" + aSeqFile,
                "　（房名打錯，或 data_root 指到另一棵資料樹 —— 兩者的症狀一樣，所以把路徑印出來）");

        long aBaseline;
        string aFromSeq = iArgs.Get("from_seq").Trim();
        if (aFromSeq.Length > 0)
        {
            if (!long.TryParse(aFromSeq, NumberStyles.Integer, CultureInfo.InvariantCulture, out aBaseline))
                return SCP_CmdResult.Fail(2, "✗ from_seq 不是整數：'" + aFromSeq + "'");
        }
        else if (!TryReadSeq(aSeqFile, out aBaseline))
        {
            return SCP_CmdResult.Fail(3, "✗ 讀不到 seq：" + aSeqFile);
        }

        var aWatch = Stopwatch.StartNew();

        // ── timeout=0：**一秒都不等**。反向對照②就是這一格（TASK-0160）。
        if (aTimeout <= 0)
        {
            SCP_CmdResult aNoWait = SCP_CmdResult.Success(
                "⏭ timeout=0 ⇒ **沒有等**（這是設計，不是失效）。要擋住 turn 請給秒數。",
                "　基準 seq＝" + aBaseline + "　房間＝" + aRoom);
            aNoWait.AddValue("replied", "0");
            aNoWait.AddValue("waited_ms", aWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            aNoWait.AddValue("from_seq", aBaseline.ToString(CultureInfo.InvariantCulture));
            return aNoWait;
        }

        Console.Error.WriteLine("⏳ 等 " + aTimeout.ToString("0.#", CultureInfo.InvariantCulture) + " 秒"
            + "　房間=" + aRoom + "　基準 seq=" + aBaseline
            + "　只算別人的發言" + (aMention ? "、且要 @" + aPersona : "")
            + "　（每 " + aPoll.ToString("0.#", CultureInfo.InvariantCulture) + "s 看一次"
            + (aBeat > 0 ? "，每 " + aBeat.ToString("0.#", CultureInfo.InvariantCulture) + "s 報一次）" : "，⚠ 不報心跳）"));

        long aSeen = aBaseline;
        double aNextBeat = aBeat;
        var aWarnings = new List<string>();

        while (aWatch.Elapsed.TotalSeconds < aTimeout)
        {
            double aLeft = aTimeout - aWatch.Elapsed.TotalSeconds;
            Thread.Sleep((int)(Math.Min(aPoll, aLeft) * 1000) + 1);

            if (TryReadSeq(aSeqFile, out long aNow) && aNow > aSeen)
            {
                foreach ((long aSeq, SCP_JsonData? aMsg, string _) in
                         SCP_WatchExport.IterMessages(aDataRoot, aRoom, aSeen + 1, aNow, aWarnings))
                {
                    if (aMsg == null) continue;
                    if (!Qualifies(aMsg, aPersona, aMention, aExclude, out string aWho, out string aTag)) continue;

                    long aMs = aWatch.ElapsedMilliseconds;
                    Console.Error.WriteLine("📨 " + aWho + " 回話了（seq " + aSeq + "）—— 等了 "
                        + (aMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "s，提早返回");
                    SCP_CmdResult aHit = SCP_CmdResult.Success(
                        "📨 **有人回話** —— " + aWho + "　seq " + aSeq + (aTag.Length > 0 ? "　tag=" + aTag : ""),
                        "　等了 " + (aMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                            + "s / 上限 " + aTimeout.ToString("0.#", CultureInfo.InvariantCulture)
                            + "s ⇒ **提早返回**（不是傻等到逾時）",
                        "　內容：" + Excerpt(aMsg.GetString("body", ""), 160));
                    aHit.AddValue("replied", "1");
                    aHit.AddValue("waited_ms", aMs.ToString(CultureInfo.InvariantCulture));
                    aHit.AddValue("from_seq", aBaseline.ToString(CultureInfo.InvariantCulture));
                    aHit.AddValue("hit_seq", aSeq.ToString(CultureInfo.InvariantCulture));
                    aHit.AddValue("hit_persona", aWho);
                    return aHit;
                }
                aSeen = aNow;
            }

            if (aBeat > 0 && aWatch.Elapsed.TotalSeconds >= aNextBeat)
            {
                Console.Error.WriteLine("　⏳ 還在等　已 "
                    + aWatch.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + "s / " + aTimeout.ToString("0.#", CultureInfo.InvariantCulture) + "s"
                    + "　目前 seq=" + aSeen + "（基準 " + aBaseline + "）");
                aNextBeat += aBeat;
            }
        }

        long aTotal = aWatch.ElapsedMilliseconds;
        SCP_CmdResult aOut = SCP_CmdResult.Fail(4,
            "⏱ **逾時：" + (aTotal / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)
                + "s 之內沒有人回話**（上限 " + aTimeout.ToString("0.#", CultureInfo.InvariantCulture) + "s）",
            "　⛔ 這不是失敗，是一個答案 —— exit 4 只是讓 script 分得出「沒人回」與「等到了」。",
            "　基準 seq " + aBaseline + " → 現在 " + aSeen
                + (aSeen > aBaseline
                    ? "（期間有新訊息，但都是我自己的／被 exclude_tags 排掉的廣播）"
                    : "（期間沒有新訊息）"));
        aOut.AddValue("replied", "0");
        aOut.AddValue("waited_ms", aTotal.ToString(CultureInfo.InvariantCulture));
        aOut.AddValue("from_seq", aBaseline.ToString(CultureInfo.InvariantCulture));
        aOut.AddValue("last_seq", aSeen.ToString(CultureInfo.InvariantCulture));
        foreach (string w in aWarnings) aOut.Lines.Add(w);
        return aOut;
    }

    /// <summary>一筆訊息算不算「有人回話」。三格都通過才算 —— 見 <see cref="Details"/>。</summary>
    static bool Qualifies(SCP_JsonData iMsg, string iPersona, bool iMention,
                          HashSet<string> iExclude, out string oWho, out string oTag)
    {
        oWho = iMsg.GetString("sender_persona", "");
        if (oWho.Length == 0) oWho = iMsg.GetString("sender_id", "");
        oTag = "";
        SCP_JsonData aMeta = iMsg["meta"];
        if (aMeta.Exists && !aMeta.IsNull) oTag = aMeta.GetString("tag", "");

        if (string.Equals(oWho, iPersona, StringComparison.OrdinalIgnoreCase)) return false;   // 我自己不算
        if (oTag.Length > 0 && iExclude.Contains(oTag)) return false;                          // 機器代組的廣播不算
        if (iMention && iMsg.GetString("body", "").IndexOf("@" + iPersona, StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return true;
    }

    static bool TryReadSeq(string iFile, out long oSeq)
    {
        oSeq = 0;
        try
        {
            return long.TryParse(File.ReadAllText(iFile, Encoding.UTF8).Trim(),
                                 NumberStyles.Integer, CultureInfo.InvariantCulture, out oSeq);
        }
        // 剛好撞到寫入 —— 下一輪再讀，⛔ 不當成「沒有新訊息」（那會讓一次讀檔競態變成一次靜默的漏接）
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    static bool TryNonNegative(string iRaw, out double oValue)
        => double.TryParse(iRaw == null ? "" : iRaw.Trim(), NumberStyles.Float,
                           CultureInfo.InvariantCulture, out oValue) && oValue >= 0;

    static string Excerpt(string iBody, int iMax)
    {
        string aOne = iBody.Replace("\r", " ").Replace("\n", " ").Trim();
        return aOne.Length <= iMax ? aOne : aOne.Substring(0, iMax) + "…";
    }
}
