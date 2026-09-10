// 區塊職責：Senate 側的**推單閘** —— 整步委派回 Unity Editor 的 `Task op=commit`。
// 物理意義：狀態機（有 blocker 不推進／有 QA 推 in_review／沒 QA 才 done）在 Editor 那一支。
//           本層只把訊號送過去，**不判、不猜、不本地重算** —— 複製一份判斷過來就是兩份產線，
//           兩邊都不報錯，而它們遲早各說各話。
// 數值影響：一張單一次 Cmd round-trip。⛔ 不重試 —— 送出之後的失敗可能其實已經推進了。
//
// ⚠ 樣板照抄 `SenateTavernPostGateway`（同一個 Tim 2026-09-03 拍過的形狀：內部串 ucmd）。
//   照抄而不是「參考」是刻意的：兩支閘的三態語意必須一模一樣，否則呼叫端要記兩套。
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Tasks;

namespace Senate.Core;

public sealed class SenateTaskCommitGateway : SCP_ITaskCommitGateway
{
    readonly string m_DataRoot;
    // ⚠ 收 nullable、存 non-null（`?? (_ => { })`）—— 與 SenateTavernPostGateway 同一手勢。
    //   讓「不想印」變成一個什麼都不做的 log，而不是讓每個呼叫點各自判 null。
    readonly Action<string> m_Log;
    readonly double m_TimeoutSec;

    // ⚠ 預設 180s 是**顯式保留舊行為**，不是新設定：`git_commit.py` 那條路帶的就是 `--timeout 180`
    //   （而 senate 預設 120）。不帶就是把等待砍短 ⇒ 多人搶 lane 時更容易誤判「沒送出」。
    //   半套修法的症狀不是紅燈，是降級。
    public SenateTaskCommitGateway(string iDataRoot, Action<string>? iLog = null, double iTimeoutSec = 180)
    {
        m_DataRoot = iDataRoot;
        m_Log = iLog ?? (_ => { });
        m_TimeoutSec = iTimeoutSec;
    }

    public string HostQualifier => "⤷ 單號推進由 Unity Editor 執行（Cmd `Task op=commit`，資料根 " + m_DataRoot + "）";

    public SCP_TaskAdvanceVerdict Advance(string iPersona, string iIndex, string iSha, string iMode,
                                          List<string> oLines)
    {
        if (string.IsNullOrWhiteSpace(iPersona))
            return SCP_TaskAdvanceVerdict.Bad("沒有 persona ⇒ 不知道要用誰的身分推（⛔ 不猜）");
        if (string.IsNullOrWhiteSpace(iIndex))
            return SCP_TaskAdvanceVerdict.Bad("沒有單號");
        if (string.IsNullOrWhiteSpace(iSha))
            // ⚠ 沒有 SHA 的推進是一句沒有憑據的宣告 —— 擋在這裡，不讓它變成單上一筆查不到來源的狀態。
            return SCP_TaskAdvanceVerdict.Bad("沒有 SHA ⇒ 推進會變成一筆查不到來源的狀態變更");

        var aArgs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "commit",
            ["index"] = iIndex,
            ["sha"] = iSha,
            // ⚠ `mode` 預設是 `fixes`（會推狀態）——「只想掛 SHA」一定要顯式給 `refs`。
            //   🩸 @basecamp 2026-09-10 就是漏了這一格：commit 訊息裡刻意沒寫 `Fixes`，
            //   卻跑 `op=commit` 掛 SHA，而它的預設 mode 把單子推成 done 並發了公告。
            //   ⇒ 本層**一律顯式帶**，不吃預設值。
            ["mode"] = iMode,
        };

        oLines.Add(HostQualifier);
        try
        {
            if (!AgentCmdClient.EnsureIdle(m_DataRoot, iPersona, 10, m_Log, out string aIdleWhy))
                return SCP_TaskAdvanceVerdict.Bad("前一筆 Cmd 還卡在同一條 lane：" + aIdleWhy);

            string aCmdId = AgentCmdClient.Submit(m_DataRoot, iPersona, "Task", aArgs, m_Log);
            AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(m_DataRoot, iPersona, aCmdId,
                m_TimeoutSec, AgentCmdClient.DefaultPollSec, m_Log, m_Log, iPrintOutputs: false);
            // ⛔ 順序寫死：**先判定，才准碰 result 檔**（逾時讀到的是上一輪，而它看起來完全正常）。
            if (aVerdict == AgentCmdWaitResult.Timeout)
                // ⚠ 這一格**不是失敗，是不知道**。措辭順序刻意是「等待上限 → 才提 Editor 沒開」：
                //   「Editor 沒開？」擺第一句時，讀的人會把它讀成診斷結果而不是猜測。
                return SCP_TaskAdvanceVerdict.Unknown(
                    "**沒等到回執**（這是 CLI 端的等待上限 "
                    + m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture)
                    + "s，不是宿主的成敗）—— 它可能已經推進了，也可能 Editor 沒開",
                    "cat \"" + AgentCmdClient.ResultPath(m_DataRoot, aCmdId).Replace('\\', '/')
                    + "\"   # result=Success ⇒ **推了，別再補**；並回讀 Tasks/tasks/ 底下那張單");
            if (aVerdict != AgentCmdWaitResult.Success)
                // 宿主自己回報失敗 ⇒ 這一格**確定沒推**，手動補是安全的。
                return SCP_TaskAdvanceVerdict.Bad("Editor 端回報失敗（詳見它的 _cmd_errors 報告）");

            (bool aFound, IReadOnlyList<string> aOutputs, List<KeyValuePair<string, string>> aValues) =
                AgentCmdClient.ResultReport(m_DataRoot, aCmdId);
            if (!aFound)
                return SCP_TaskAdvanceVerdict.Bad("沒有 result 檔（跟「有檔但沒有 values」不同形）");
            for (int i = 0; i < aOutputs.Count; ++i) oLines.Add("  📄 Editor 回傳檔：" + aOutputs[i]);

            // ⛔ 不在這裡宣告「已完成」：落到 `in_review` 還是 `done` 由 Editor 那支判，
            //   本層只知道「訊號送到了」。把 values 原樣帶回去讓呼叫端印，不加工。
            var aDetail = new System.Text.StringBuilder();
            foreach (var kv in aValues)
                if (kv.Key == "status" || kv.Key == "index")
                {
                    if (aDetail.Length > 0) aDetail.Append(' ');
                    aDetail.Append(kv.Key).Append('=').Append(kv.Value);
                }
            return SCP_TaskAdvanceVerdict.Good(aDetail.ToString());
        }
        catch (Exception e)
        {
            // ⚠ 這一格的失敗才是真的**沒送出去**（例外發生在委派握手之前或之中，而 Submit 會自己丟）。
            //   ⛔ 但 `except` 只知道「這裡炸了」，它不知道「炸之前做完了什麼」——
            //   所以措辭是「送出前後不明」而不是斷言沒推。
            return SCP_TaskAdvanceVerdict.Unknown(
                e.GetType().Name + ": " + e.Message + "（例外發生在委派過程中 ⇒ 送出與否不明）",
                "回讀 " + m_DataRoot + "/Tasks/tasks/ 底下那張單，確認狀態有沒有動再決定補不補");
        }
    }
}
