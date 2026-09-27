// 區塊職責：Senate 側的**酒館發文閘**（`senate cmd commit` 的公告、小歇廣播走這裡）。
// 物理意義（TASK-0311，epic 0295 ③ 第二刀）：**先走 Senate 那條管線**——
//           `SCP_TavernPostCompose` 組訊息 → 酒館 Server `tavern-write`（配號建檔＋發薪＋@mention）——
//           跟 `senate cmd tavern-post`（TASK-0308）同一條路；前處理全部在 SCP_Core（TASK-0311／0312）。
//           只有「找不到資料根對應的專案根」才整步委派回 Unity Editor 的 `Tavern op=post`（下面 `PostViaEditor`，原樣保留）。
//           ⚠ 本閘不做 alter 配對延遲（呼叫端是公告不是對話，見 `PostViaSenate`）。
//           ⚠ 寫入端一直只有一個：兩條路最後都落到酒館 Server 的 `tavern-write`（Editor 在 `tavern.writer=server`
//           時也是委派它）⇒ 這裡換的是「誰組訊息」，⛔ 不是多開一個寫入端。
// 數值影響：Senate 路 ＝ 一次 Server round-trip；Editor 路 ＝ 一次 Cmd round-trip（檔案協議＋Watcher 輪詢，1〜3 秒）。
//           🩸 兩條路的逾時都是 **`Unresolved`（不知道），不是「沒發」**。TASK-0134 QA（summit 2026-09-05）
//           用一次真的小歇量到：CLI 逾時回報「沒發」，而廣播**其實成功了**（`post_seq 19082`）。
//           ⛔ 兩者處置相反（真沒發要補發／沒等到去補發＝多出第二則，seq 全域遞增），
//           所以它們在回傳型別上就必須不同形，不能靠讀的人自己分辨。
//
// ⚠ Editor 路的樣板照抄 `SenateSessionCloseGateway`（Tim 2026-09-03 在 TASK-0114 拍過的形狀：內部串 ucmd）。
#nullable enable
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed class SenateTavernPostGateway : SCP_ITavernPostGateway
{
    const string Room = "tavern";

    readonly string m_DataRoot;
    readonly Action<string> m_Log;
    readonly double m_TimeoutSec;

    public SenateTavernPostGateway(string iDataRoot, Action<string>? iLog = null, double iTimeoutSec = 30)
    {
        m_DataRoot = iDataRoot;
        m_Log = iLog ?? (_ => { });
        // 30s：ritual 的廣播是 best-effort，⛔ 不該把呼叫者卡到外層 timeout
        //（python 端 rest 的既有值就是 30s，這裡沿用而不是重挑一個）。
        m_TimeoutSec = iTimeoutSec;
    }

    /// <summary>整體定語（兩條路各自在 <c>oLines</c> 再印一行實際走了哪一條）。</summary>
    public string HostQualifier => "⤷ 酒館發文：Senate 組訊息＋酒館 Server 寫入；找不到專案根才交回 Unity Editor（資料根 " + m_DataRoot + "）";

    string SenateQualifier => "⤷ 酒館發文由 Senate 組訊息、酒館 Server 寫入（不經 Unity Editor，資料根 " + m_DataRoot + "）";
    string EditorQualifier => "⤷ 酒館發文由 Unity Editor 執行（Cmd `Tavern op=post`，資料根 " + m_DataRoot + "）";

    public SCP_TavernPostVerdict Post(string iSenderPersona, string iBody,
                                      IReadOnlyDictionary<string, string> iMeta, List<string> oLines)
    {
        if (string.IsNullOrWhiteSpace(iSenderPersona))
            return SCP_TavernPostVerdict.Bad("沒有 persona ⇒ 不知道要署誰的名（⛔ 不猜）");
        if (string.IsNullOrWhiteSpace(iBody))
            return SCP_TavernPostVerdict.Bad("內文是空的 —— 空訊息會佔一則卻不說話");

        // T06.3 schema：不合就是**確定沒發**，兩條路都一樣（Editor 用同一支判出同一個結果）⇒ ⛔ 不交回 Editor 再被擋一次。
        string? aSchema = SCP_TavernMetaSchema.Validate(iMeta);
        if (aSchema != null) return SCP_TavernPostVerdict.Bad("meta 不合 T06.3 schema：" + aSchema);

        // 前處理全部在 Senate 做得完（TASK-0312）⇒ 唯一交回 Editor 的理由是「找不到專案根」：
        //   專案根是詞典附註（`Docs/Glossary`）的根，拿不到就組不出跟 Editor 同形的訊息。⛔ 不猜一個。
        if (TryResolveProjectRoot(out string aProjectRoot, out string aWhy))
            return PostViaSenate(iSenderPersona, iBody, iMeta, aProjectRoot, oLines);
        oLines.Add("· 交回 Editor：找不到這個資料根對應的專案根（" + aWhy + "）");
        return PostViaEditor(iSenderPersona, iBody, iMeta, oLines);
    }

    // ===========================================================
    // 區塊職責：Senate 管線 —— 組訊息 → `tavern-write`。
    // ⚠ 刻意**不做 alter 配對延遲**：本閘的呼叫端是 commit 公告與小歇廣播 —— 那是公告不是對話，
    //   延後它只會讓「commit 已提交」與「公告」脫鉤（呼叫端要的是 seq）。Editor 的 `op=share` 同一個判斷（帶 alter-pacing-bypass）。
    // ===========================================================
    SCP_TavernPostVerdict PostViaSenate(string iPersona, string iBody, IReadOnlyDictionary<string, string> iMeta,
                                        string iProjectRoot, List<string> oLines)
    {
        var aRoots = new SCP_MorningRoots
        {
            DataRoot = m_DataRoot.Replace('\\', '/'),
            LettersRoot = SCP_DataPaths.Letters(new SCP_DataRoot(m_DataRoot)).Value,
            ProjectRoot = iProjectRoot.Replace('\\', '/'),
        };
        SCP_TavernPostDraft aDraft = SCP_TavernPostCompose.Build(aRoots.DataRoot, aRoots.LettersRoot, aRoots.ProjectRoot,
            aRoots.Region, Room, iPersona, iBody, iMeta);
        foreach (string n in aDraft.Notes) oLines.Add("⚠ " + n);
        if (aDraft.Message == null) return SCP_TavernPostVerdict.Bad("發文被拒：" + aDraft.Error);

        oLines.Add(SenateQualifier);
        SCP_CmdResult aWrite = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = aRoots.DataRoot,
            ["room"] = Room,
            ["msg_json"] = SCP_TavernWriter.Serialize(aDraft.Message),
            ["timeout"] = m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture),
        });
        string aSeq = Value(aWrite, "seq");
        if (aWrite.Ok && aSeq.Length > 0)
        {
            // 發薪／@mention 是寫入端做的 ⇒ 讀數原樣帶回（commit +5 的憑據就在這幾格）。
            foreach (var kv in aWrite.Values)
                if (kv.Key.StartsWith("pay_", StringComparison.Ordinal) || kv.Key.StartsWith("mention_", StringComparison.Ordinal))
                    oLines.Add($"  · {kv.Key} = {kv.Value}");
            return SCP_TavernPostVerdict.Good("seq=" + aSeq, aSeq);
        }

        // 三態：逾時／未知 ＝ 不知道（先回讀，別補發）；其餘 ＝ 確定沒發（`senate cmd tavern-post` 同一判準）。
        string aFailure = Value(aWrite, "delegate_failure");
        foreach (string l in aWrite.Lines) oLines.Add("  │ " + l);
        if (aFailure == "timeout" || aFailure == "unknown")
            return SCP_TavernPostVerdict.Unknown(
                $"酒館寫入**結果不明**（delegate_failure={aFailure}）—— 它可能已經發出去了",
                $"senate cmd tavern-query --arg data_root={aRoots.DataRoot} --arg kind=tail --arg room={Room}"
                + "   # 看得到這一則 ⇒ **發了，別補發**；看不到 ⇒ 才補發");
        return SCP_TavernPostVerdict.Bad(
            $"酒館寫入確定沒發（delegate_failure={(aFailure.Length > 0 ? aFailure : "exit " + aWrite.ExitCode)}）");
    }

    /// <summary>
    /// 這個資料根是哪個專案的：在 Senate 設定檔的啟用專案裡找「解析出來的 AgentCommands 根 ＝ 本閘的資料根」那一個。
    /// <para>⛔ 找不到、或有兩個都對得上 ⇒ 回 false（不猜）—— 呼叫端交回 Editor。</para>
    /// </summary>
    bool TryResolveProjectRoot(out string oProjectRoot, out string oWhy)
    {
        oProjectRoot = ""; oWhy = "";
        if (UnityDelegateCmd.ConfigProvider == null) { oWhy = "宿主沒有裝上設定來源"; return false; }
        (SenateConfig? aConfig, string aConfigPath) = UnityDelegateCmd.ConfigProvider();
        if (aConfig == null) { oWhy = "還沒有設定檔（" + aConfigPath + "）"; return false; }
        string aWant = Norm(m_DataRoot);
        var aHits = new List<string>();
        foreach (SenateProject p in aConfig.Projects)
        {
            if (!p.Enabled || string.IsNullOrWhiteSpace(p.Root)) continue;
            string? aDr = ProjectProbe.ResolveAgentCommandsRoot(p.Root, p.AgentCommandsRoot);
            if (aDr != null && Norm(aDr) == aWant) aHits.Add(p.Root);
        }
        if (aHits.Count == 1) { oProjectRoot = aHits[0]; return true; }
        oWhy = aHits.Count == 0 ? "設定檔的啟用專案裡沒有一個的資料根是它" : "有 " + aHits.Count + " 個專案都對得上，不猜";
        return false;
    }

    static string Norm(string iPath)
    {
        string aFull;
        try { aFull = Path.GetFullPath(iPath); } catch (Exception) { aFull = iPath; }
        return aFull.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
    }

    static string Value(SCP_CmdResult iR, string iKey)
    {
        foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
        return "";
    }

    // ===========================================================
    // 區塊職責：Editor 路 —— 整步委派 Unity Editor 的 `Tavern op=post`（TASK-0311 之前的唯一路，原樣保留）。
    // ===========================================================
    SCP_TavernPostVerdict PostViaEditor(string iSenderPersona, string iBody,
                                        IReadOnlyDictionary<string, string> iMeta, List<string> oLines)
    {
        // ⚠ 參數名逐格照 `_lib/tavern_client.post_message`（同一支 Cmd 的另一個 client）：
        //   `meta` 是**一整串** `k:v;k:v`（不是每欄一個參數）、`wait-reply` 是**連字號**。
        //   🩸 senate 的 `ucmd` 對未知參數**沒有預檢**（TASK-0125）⇒ 名字打錯會靜默取預設值，
        //   而輸出跟成功時一模一樣。所以這裡不憑印象命名，是照那支抄的。
        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "post",
            ["room"] = Room,
            ["persona"] = iSenderPersona,
            ["body"] = iBody,
            // ⛔ 不傳顯示身分（`sender`）—— 由 Cmd_Tavern 從 persona 推導。
            //   繞過推導不會報錯，只會**署錯名字**（UCL 端 BUG-23／24 的形狀）。
            ["wait-reply"] = "0",              // ritual 廣播從不等回覆
        };
        var aMetaStr = new System.Text.StringBuilder();
        foreach (var kv in iMeta)
        {
            if (string.IsNullOrWhiteSpace(kv.Value)) continue;
            if (aMetaStr.Length > 0) aMetaStr.Append(';');
            aMetaStr.Append(kv.Key).Append(':').Append(kv.Value);
        }
        if (aMetaStr.Length > 0) aArgs["meta"] = aMetaStr.ToString();

        oLines.Add(EditorQualifier);
        try
        {
            if (!AgentCmdClient.EnsureIdle(m_DataRoot, iSenderPersona, 10, m_Log, out string aIdleWhy))
                return SCP_TavernPostVerdict.Bad("前一筆 Cmd 還卡在同一條 lane：" + aIdleWhy);

            string aCmdId = AgentCmdClient.Submit(m_DataRoot, iSenderPersona, "Tavern", aArgs, m_Log);
            AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(m_DataRoot, iSenderPersona, aCmdId,
                m_TimeoutSec, AgentCmdClient.DefaultPollSec, m_Log, m_Log, iPrintOutputs: false);
            // ⛔ 順序寫死：**先判定，才准碰 result 檔**（逾時讀到的是上一輪，而它看起來完全正常）。
            if (aVerdict.IsIndeterminate())
                // ⚠ 這一格**不是失敗，是不知道**。措辭順序刻意是「等待上限 → 才提 Editor 沒開」：
                //   「Editor 沒開？」擺第一句時，讀的人會把它讀成診斷結果而不是猜測
                //   （summit 拿到 exit 6 時 Editor 是開著的）。同 TASK-0104 對 AgentCmdClient 做過的事。
                // ⚠ 兩種「不知道」的成因不同，措辭要跟著分（TASK-0263）——
                //   共用一句話的話，「被寫回蓋掉」會被讀成「等太久」，而後者聽起來只要再等就好。
                return SCP_TavernPostVerdict.Unknown(
                    aVerdict == AgentCmdWaitResult.Unknown
                    ? "**這一筆從 queue 消失了，而判定檔不存在** —— 它可能已經發了（不寫判定檔的舊版執行端），"
                      + "也可能**根本沒被執行**（append 被別人的整檔寫回蓋掉）"
                    : "**沒等到回執**（這是 CLI 端的等待上限 "
                    + m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture)
                    + "s，不是宿主的成敗）—— 它可能已經發出去了，也可能 Editor 沒開",
                    "cat \"" + AgentCmdClient.ResultPath(m_DataRoot, aCmdId).Replace('\\', '/')
                    + "\"   # result=Success ＋ post_seq ⇒ **發了，別補發**；檔不存在／非 Success ⇒ 才補發");
            if (aVerdict != AgentCmdWaitResult.Success)
                // 宿主自己回報失敗 ⇒ 這一格**確定沒發**，補發是安全的。
                return SCP_TavernPostVerdict.Bad("Editor 端回報失敗（詳見它的 _cmd_errors 報告）");

            (bool aFound, IReadOnlyList<string> aOutputs, List<KeyValuePair<string, string>> aValues) =
                AgentCmdClient.ResultReport(m_DataRoot, aCmdId);
            if (!aFound)
                return SCP_TavernPostVerdict.Bad("沒有 result 檔（跟「有檔但沒有 values」不同形）");
            for (int i = 0; i < aOutputs.Count; ++i) oLines.Add("  📄 Editor 回傳檔：" + aOutputs[i]);

            string aSeq = "";
            foreach (var kv in aValues) if (kv.Key == "post_seq") { aSeq = kv.Value; break; }
            // ⚠ 「Editor 說成功」與「訊息真的在」不是同一件事，而這一層拿得到的**只有 seq**：
            //   有 seq ⇒ 它配過號了（那是寫入真的發生過的直接證據）；
            //   沒 seq ⇒ **明說沒有這個讀數**，⛔ 不要印一個看起來成功的 ✓。
            return aSeq.Length > 0
                ? SCP_TavernPostVerdict.Good("seq=" + aSeq, aSeq)
                : SCP_TavernPostVerdict.Bad("Editor 回成功但**沒有 post_seq** —— 這一格沒有讀數，當作沒發成");
        }
        catch (Exception e)
        {
            return SCP_TavernPostVerdict.Bad(e.GetType().Name + ": " + e.Message);
        }
    }
}
