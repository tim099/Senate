// 區塊職責：Senate 側的**推單閘** —— `senate cmd commit` 的 `Fixes/Refs TASK-n` 交給 `senate cmd task op=commit`。
// 物理意義：狀態機（有 blocker 不推進／有 QA 推 in_review／沒 QA 才 done）只有一份，住在 SCP_Core `SCP_TaskOps`，
//           由任務單唯一的寫入端（Senate Server `task-write`）執行。本層只把訊號送過去，**不判、不猜、不本地重算**。
//           🩸 TASK-0349 之前這一格委派 Unity Editor 的 `Task op=commit` ⇒ **Editor 沒開，commit 就推不了單**。
// 數值影響：一張單一次 Server round-trip（＋狀態有變時一則酒館通知，由入口發）。⛔ 不重試 —— 送出之後的失敗可能其實已經推進了。
//
// ⚠ 三態照舊（呼叫端只認這一套）：入口 exit 0 ⇒ Advanced；exit 7（結果不明）⇒ Unresolved；其餘 ⇒ NotSent（確定沒寫）。
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Cmd;
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

    public string HostQualifier => "⤷ 單號推進由 Senate 任務寫入端執行（`senate cmd task op=commit` → Server `task-write`，資料根 " + m_DataRoot + "）";

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

        oLines.Add(HostQualifier);
        SCP_CmdResult aR;
        try
        {
            aR = SCP_CmdRegistry.Dispatch("task", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["op"] = "commit",
                ["persona"] = iPersona,
                ["data_root"] = m_DataRoot,
                ["index"] = iIndex,
                ["sha"] = iSha,
                // ⚠ `mode` 預設是 `fixes`（會推狀態）——「只想掛 SHA」一定要顯式給 `refs`。
                //   🩸 @basecamp 2026-09-10：commit 訊息刻意沒寫 `Fixes`，卻跑 `op=commit` 掛 SHA，
                //   預設 mode 把單子推成 done 並發了公告 ⇒ 本層**一律顯式帶**，不吃預設值。
                ["mode"] = iMode,
                ["timeout"] = m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture),
            });
        }
        catch (Exception e)
        {
            return SCP_TaskAdvanceVerdict.Unknown(
                e.GetType().Name + ": " + e.Message + "（例外發生在委派過程中 ⇒ 送出與否不明）",
                SCP_CmdRegistry.Invoke("tasks --arg index=" + iIndex) + "   # 狀態有沒有動再決定補不補");
        }
        foreach (string l in aR.Lines) if (l.Length > 0) oLines.Add("  " + l);
        foreach (string o in aR.Outputs) oLines.AddRange(SCP_ReadHint.Lines("  📄 回傳檔：", o, m_DataRoot));
        if (aR.ExitCode == 7)
            return SCP_TaskAdvanceVerdict.Unknown(
                "**沒等到任務寫入端的回執** —— 它可能已經推進了",
                SCP_CmdRegistry.Invoke("tasks --arg index=" + iIndex)
                + "   # 看得到這顆 sha ⇒ **推了，別再補**");
        if (!aR.Ok)
            return SCP_TaskAdvanceVerdict.Bad($"任務寫入端回報沒有寫（exit {aR.ExitCode}）—— 原因見上面的輸出／回傳檔");
        // ⛔ 不在這裡宣告「已完成」：落到 `in_review` 還是 `done` 由寫入端判，本層只把讀數原樣帶回去。
        var aDetail = new System.Text.StringBuilder();
        foreach (var kv in aR.Values)
            if (kv.Key == "status" || kv.Key == "index")
            {
                if (aDetail.Length > 0) aDetail.Append(' ');
                aDetail.Append(kv.Key).Append('=').Append(kv.Value);
            }
        return SCP_TaskAdvanceVerdict.Good(aDetail.ToString());
    }
}
