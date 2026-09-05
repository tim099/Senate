// 區塊職責：Senate 側的**酒館發文閘** —— 整步委派回 Unity Editor 的 `Tavern op=post`。
// 物理意義：seq 是全域遞增的，而**同時只能有一個寫入端** ⇒ 這一格沒有「本地版」，
//           也不會有（同 `SenateSessionCloseGateway` 對金流的處置：不搬，委派）。
//           ⚠ python 那支 `awakening.py tavern_post` 本來也是 spawn `run_cmd.py Tavern op=post`
//           —— 它從頭到尾就是委派。所以「搬到 CLI 就不用 Editor」對廣播那半**不成立**，
//           別把那句話寫進任何說明裡。
// 數值影響：一次 Cmd round-trip（檔案協議＋Watcher 輪詢，1〜3 秒）。逾時 ⇒ **當作沒發成**。
//
// ⚠ 樣板照抄 `SenateSessionCloseGateway`（Tim 2026-09-03 在 TASK-0114 拍過的形狀：內部串 ucmd）。
#nullable enable
using System.Globalization;
using SCP.Core.Letters;

namespace Senate.Core;

public sealed class SenateTavernPostGateway : SCP_ITavernPostGateway
{
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

    public string HostQualifier => "⤷ 酒館發文由 Unity Editor 執行（Cmd `Tavern op=post`，資料根 " + m_DataRoot + "）";

    public SCP_TavernPostVerdict Post(string iSenderPersona, string iBody,
                                      IReadOnlyDictionary<string, string> iMeta,
                                      string iSessionToken, List<string> oLines)
    {
        if (string.IsNullOrWhiteSpace(iSenderPersona))
            return SCP_TavernPostVerdict.Bad("沒有 persona ⇒ 不知道要署誰的名（⛔ 不猜）");
        if (string.IsNullOrWhiteSpace(iBody))
            return SCP_TavernPostVerdict.Bad("內文是空的 —— 空訊息會佔一則卻不說話");

        // ⚠ 參數名逐格照 `_lib/tavern_client.post_message`（同一支 Cmd 的另一個 client）：
        //   `meta` 是**一整串** `k:v;k:v`（不是每欄一個參數）、`wait-reply` 是**連字號**。
        //   🩸 senate 的 `ucmd` 對未知參數**沒有預檢**（TASK-0125）⇒ 名字打錯會靜默取預設值，
        //   而輸出跟成功時一模一樣。所以這裡不憑印象命名，是照那支抄的。
        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "post",
            ["room"] = "tavern",
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
        if (!string.IsNullOrWhiteSpace(iSessionToken)) aArgs["session_token"] = iSessionToken;

        oLines.Add(HostQualifier);
        try
        {
            if (!AgentCmdClient.EnsureIdle(m_DataRoot, iSenderPersona, 10, m_Log, out string aIdleWhy))
                return SCP_TavernPostVerdict.Bad("前一筆 Cmd 還卡在同一條 lane：" + aIdleWhy);

            string aCmdId = AgentCmdClient.Submit(m_DataRoot, iSenderPersona, "Tavern", aArgs, m_Log);
            AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(m_DataRoot, iSenderPersona, aCmdId,
                m_TimeoutSec, AgentCmdClient.DefaultPollSec, m_Log, m_Log, iPrintOutputs: false);
            // ⛔ 順序寫死：**先判定，才准碰 result 檔**（逾時讀到的是上一輪，而它看起來完全正常）。
            if (aVerdict != AgentCmdWaitResult.Success)
                return SCP_TavernPostVerdict.Bad(aVerdict == AgentCmdWaitResult.Timeout
                    ? "逾時 " + m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture)
                      + "s 沒等到 Editor 的 result —— Editor 沒開？（⚠ 那不代表它沒發，回讀酒館才知道）"
                    : "Editor 端回報失敗（詳見它的 _cmd_errors 報告）");

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
