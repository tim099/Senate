// ===========================================================
// 檔案職責：核准請款**動錢之前**的兩道回讀 —— 單子還是不是 pending、帳上是不是已經付過這張單。
// 共用者：`Cmd_Bank op=approve`、Senate 後台 `BankAdminPage`（單張核准與一鍵批准全部）。
// ⛔ 別在呼叫端各寫一份：兩份判準會在其中一份改過之後開始分岔，而兩邊都不會報錯。
//
// 🩸 為什麼有這一檔（2026-10-01，LY 補薪 6 張單）：
//   cc 的 591929 與 zeta 的 bc2988 **各付了兩次**（帳上 114／112，應 57／56）。三格疊起來才會發生：
//   ① 一鍵批准只真的跑了第一張（`Start` 同時只收一件，其餘五張被「前一筆還沒完」吞掉），
//      畫面卻印「已送出 6 張」⇒ 剩下的單子還掛在待審，人自然會再按。
//   ② 增發寫 `payout/<id>`、央行撥款寫 `payout/<id>/out`＋`/in` —— 註解說兩條路「共用同一把冪等鍵」，
//      而帳本層其實是**三把不同的鍵** ⇒ 同一張單走過一條，另一條照樣付得下去。
//   ③ 順序是「先動錢、成功了才寫裁決欄」，而 `Decide` 的「不是 pending 就不寫」檢查排在動錢**之後**。
//   ⇒ 本檔把判準改成**看帳上有沒有這張單的入帳腳**（kind=payout_request ∧ ref=單號 ∧ credit），
//     不看冪等鍵長什麼樣 —— 兩種資金來源、新舊鍵形狀都被同一格擋到。
// ===========================================================
using System;
using System.IO;
using SCP.Core.Bank;
using SCP.Core.Json;

namespace Senate.Core;

public static class PayoutApprovalGuard
{
    /// <summary>
    /// 現在磁碟上那張單的 `status`。讀不了 ⇒ 回空字串（⛔ 呼叫端要把它當「不是 pending」—— 讀不到不是放行）。
    /// </summary>
    public static string ReadStatus(string iRequestPath)
    {
        try
        {
            SCP_JsonData aJd = SCP_JsonParser.Parse(File.ReadAllText(iRequestPath));
            return aJd.IsObject ? aJd.GetString("status", "") : "";
        }
        catch (Exception) { return ""; }
    }

    /// <summary>
    /// 帳上這張請款單已經有的入帳腳（不論增發或央行撥款、不論冪等鍵形狀）。沒有回 null。
    /// <para>⚠ 回捲那一筆是 `transfer_rollback`、不是 `payout_request` ⇒ 收款腳失敗又回捲的單不算付過（它本來就沒付到）。</para>
    /// </summary>
    public static SCP_BankEntry? FindPaidCredit(string iBankRoot, string iRequestId)
    {
        if (string.IsNullOrWhiteSpace(iRequestId)) return null;
        foreach (SCP_BankEntry e in SCP_BankLedger.EnumerateEntries(iBankRoot))
        {
            if (e.Type != SCP_BankEntryType.Credit) continue;
            if (!string.Equals(e.Kind, "payout_request", StringComparison.Ordinal)) continue;
            if (string.Equals(e.Ref, iRequestId, StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }
}
