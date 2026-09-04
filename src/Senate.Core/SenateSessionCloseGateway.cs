// 區塊職責：Senate 側的**關場閘** —— 把「關掉一場活動 session（含結算）」委派回 Unity Editor。
// 物理意義：結算就是金流（Editor 的 `SettleAsync` 內含 `UCL_TreasuryLedger.Credit`），
//           而金流搬家是 TASK-0106 —— Tim 拍 B：記單不動。⇒ Senate 這側**不算錢、也不寫 session 檔**，
//           整步交給 Editor 的 `SessionClose`（它自己做三段：權威狀態 → 結算 → 廣播略過）。
// 數值影響：一次 Cmd round-trip（檔案協議 ＋ Watcher 輪詢，1〜3 秒）。逾時 ⇒ **當作沒關成**。
//
// ⏳ **過渡（退場條件：TASK-0106 —— treasury 進 Senate 之後）**：
//    那一天把這個 class 換掉（改成就地結算），`SCP_IActivitySessionCloseGateway`、
//    `SCP_Cmd_Sessions`、管理頁**一行都不用動**。過渡件收斂在這一個檔，是這個設計的重點。
//
// ⚠ 樣板照抄 `SenateCanvasGateway`（Tim 2026-09-03 在 TASK-0114 拍過的形狀：內部串 ucmd，不移植 ledger）。
#nullable enable
using System.Globalization;
using SCP.Core.Session;

namespace Senate.Core;

public sealed class SenateSessionCloseGateway : SCP_IActivitySessionCloseGateway
{
    readonly string m_DataRoot;
    readonly Action<string> m_Log;
    readonly double m_TimeoutSec;

    public SenateSessionCloseGateway(string iDataRoot, string iKind,
                                     Action<string>? iLog = null, double iTimeoutSec = 60)
    {
        m_DataRoot = iDataRoot;
        Kind = iKind;
        m_Log = iLog ?? (_ => { });
        m_TimeoutSec = iTimeoutSec;
    }

    public string Kind { get; }

    /// <summary>
    /// 委派 Editor 的 <c>SessionClose</c> 關掉這一場。
    /// </summary>
    /// <remarks>
    /// ⚠ 這裡回 true 只代表「**Editor 說它關了**」——
    /// 「磁碟上關了沒」由呼叫端（<see cref="SCP_ActivitySessionStore.CloseWithSettlement"/>）回讀確認。
    /// 兩件事分開，因為它們在不同的 process 上，而回報字串會替自己說謊。
    /// </remarks>
    public bool TryClose(SCP_ActivitySession iSession, string iReason, List<string> oLines, out string oError)
    {
        oError = "";
        string aTarget = iSession.persona;
        if (string.IsNullOrWhiteSpace(aTarget))
        {
            oError = "session 沒有 persona 欄 ⇒ 不知道要叫 Editor 關誰的場（⛔ 不猜）";
            return false;
        }
        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["target_persona"] = aTarget,
            ["confirm"] = "1",
            ["reason"] = string.IsNullOrWhiteSpace(iReason) ? "closed-by-senate" : iReason,
        };
        // ⚠ lane 用**目標**的 persona，不是呼叫者的 —— 那不是筆誤，是刻意的：
        //   同一場 session 的兩次關場請求會因此排在同一條 lane 上串行（併發關場自然不可能）。
        //   代價是 Editor 的回傳檔會落在**目標的** letters 夾（那份報告講的正是他的場）。
        oLines.Add("⤷ 關場與結算由 Unity Editor 執行（Cmd `SessionClose`，lane=" + aTarget
                   + "，資料根 " + m_DataRoot + "）");
        try
        {
            if (!AgentCmdClient.EnsureIdle(m_DataRoot, aTarget, 10, m_Log, out string aIdleWhy))
            {
                oError = "前一筆 Cmd 還卡在同一條 lane：" + aIdleWhy;
                return false;
            }
            string aCmdId = AgentCmdClient.Submit(m_DataRoot, aTarget, "SessionClose", aArgs, m_Log);
            AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(m_DataRoot, aTarget, aCmdId,
                m_TimeoutSec, AgentCmdClient.DefaultPollSec, m_Log, m_Log, iPrintOutputs: false);
            // ⛔ 順序寫死：**先判定，才准碰 result 檔**（逾時讀到的是上一輪，而它看起來完全正常）。
            if (aVerdict != AgentCmdWaitResult.Success)
            {
                oError = aVerdict == AgentCmdWaitResult.Timeout
                    ? "逾時 " + m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture)
                      + "s 沒等到 Editor 的 result —— Editor 沒開？（⚠ 那不代表它沒做，回讀磁碟才知道）"
                    : "Editor 端回報失敗（詳見它的 _cmd_errors 報告）";
                return false;
            }
            (bool aFound, IReadOnlyList<string> aOutputs, List<KeyValuePair<string, string>> aValues) =
                AgentCmdClient.ResultReport(m_DataRoot, aCmdId);
            if (!aFound)
            {
                oError = "沒有 result 檔（跟「有檔但沒有 values」不同形）";
                return false;
            }
            for (int i = 0; i < aOutputs.Count; ++i) oLines.Add("  📄 Editor 回傳檔：" + aOutputs[i]);
            string aClosed = "", aSettled = "";
            foreach (var kv in aValues)
            {
                if (kv.Key == "closed") aClosed = kv.Value;
                else if (kv.Key == "settled") aSettled = kv.Value;
            }
            oLines.Add("  Editor 說：closed=" + (aClosed.Length == 0 ? "(沒這欄)" : aClosed)
                       + "／settled=" + (aSettled.Length == 0 ? "(沒這欄)" : aSettled));
            if (aClosed != "1")
            {
                oError = "Editor 回 closed=" + (aClosed.Length == 0 ? "(沒這欄)" : aClosed);
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            oError = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }
}
