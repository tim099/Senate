// 區塊職責：書店閘的 **CLI／Server 實作** —— 錢與券**直接串 Server**（`bank` / `voucher`）。
// 物理意義：本體（`SCP_BooksOps`）搬出 Unity 之後，兩個宿主各給一份閘：
//           Editor 那份走 `UCL_TreasuryLedger.Pay`，這一份走 `SCP_CmdRegistry.Dispatch`。
//           ⇒ 這就是 TASK-0234 ② 說的「效果注入」的另一半。
// 數值影響：`bank op=pay`（主動消費自動先扣酒館券）與 `voucher op=grant`。
//           ⛔ 本層**不判斷**哪些 kind 算主動消費 —— 那條規則只住在 `SCP_SpendPolicy`。
//
// 🩸 判準：
//   ① **失敗一律 throw**（介面判準②）。吞掉的話留下的是「書登記了、錢沒扣」，
//      而那張登記之後沒有人會回來看。
//   ② **`DeliverDossier` 誠實回「這個宿主沒有這一步」**，⛔ 不回一句假裝成功的話 ——
//      續寫包寫進 `letters/` 的版面只有 Editor 那側有，
//      而「沒有這一步」與「做了但失敗」在回報上要分得開（本體會把理由原樣印出來）。
//   ③ 取值一律讀 result 的 **values 欄**，⛔ 不 regex 人讀輸出
//      （那種失配的樣子跟「查不到」一模一樣）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Bank;
using SCP.Core.Books;
using SCP.Core.Cmd;
using SCP.Core.Json;

namespace Senate.Core;

public sealed class SenateBooksGateway : SCP_IBooksGateway
{
    readonly string m_DataRoot;

    public SenateBooksGateway(string iDataRoot) { m_DataRoot = iDataRoot; }

    string BankRoot() => System.IO.Path.Combine(m_DataRoot, "Bank");

    string LettersRoot()
        => SCP.Core.Paths.SCP_DataPaths.Letters(new SCP.Core.Paths.SCP_DataRoot(m_DataRoot)).Value;

    public (int Voucher, int Token) Pay(string iBank, string iWalletPersona, int iAmount, string iKind,
                                        string iRef, string iDescription, string iIdemKey)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "pay",
            ["bank_root"] = BankRoot(),
            ["letters_root"] = LettersRoot(),
            ["account"] = iBank,
            ["wallet_persona"] = iWalletPersona ?? "",
            ["amount"] = iAmount.ToString(CultureInfo.InvariantCulture),
            ["kind"] = iKind,
            ["ref"] = string.IsNullOrEmpty(iRef) ? "-" : iRef,
            ["description"] = iDescription ?? "",
            ["caller"] = string.IsNullOrEmpty(iBank) ? "system" : iBank,
            ["idem_key"] = iIdemKey ?? "",
        };
        // ⚠ 錢包定位不到（persona 空）時 `pay` 會擋下來 —— 那時退回純 `debit`。
        //   ⛔ 不在這裡自己判「算不算主動消費」，只判「有沒有錢包可問」。
        if (string.IsNullOrWhiteSpace(iWalletPersona))
        {
            aArgs["op"] = "debit";
            aArgs.Remove("wallet_persona");
            aArgs.Remove("letters_root");
            SCP_CmdResult aDebit = SCP_CmdRegistry.Dispatch("bank", aArgs);
            if (aDebit.ExitCode != 0)
                throw new InvalidOperationException(
                    $"[Books] 扣款失敗（bank exit {aDebit.ExitCode}）：{Reason(aDebit)} —— 這筆錢**沒有動**。");
            return (0, iAmount);
        }

        SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("bank", aArgs);
        if (aRes.ExitCode != 0)
            throw new InvalidOperationException(
                $"[Books] 付款失敗（bank exit {aRes.ExitCode}）：{Reason(aRes)} —— 這筆錢**沒有動**。");
        return (ValueInt(aRes, "paid_voucher"), ValueInt(aRes, "paid_token"));
    }

    public void GrantVoucher(string iPersona, string iVoucherId, int iAmount, string iSource, string iRef)
    {
        if (iAmount <= 0) return;
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "grant",
            ["letters_root"] = LettersRoot(),
            ["persona"] = iPersona,
            ["voucher"] = iVoucherId,
            ["amount"] = iAmount.ToString(CultureInfo.InvariantCulture),
            // ⚠ 券沒有歷史 ⇒ `region` 是唯一的「誰動過它」線索，寫入時必填。
            ["region"] = SCP_BankRegion.Read(m_DataRoot, out string? _),
            ["source"] = iSource ?? "",
            ["ref"] = iRef ?? "",
        };
        SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("voucher", aArgs);
        if (aRes.ExitCode != 0)
            throw new InvalidOperationException(
                $"[Books] 發券失敗（voucher exit {aRes.ExitCode}）：{Reason(aRes)}");
    }

    public void Warn(string iMessage) => Console.Error.WriteLine(iMessage);

    // 判準②：誠實說「這個宿主沒有這一步」。
    public string? DeliverDossier(string iBook, string iAuthorPersona, SCP_JsonData iEntry, out string oError)
    {
        oError = "Senate 這一側沒有續寫包投遞（它寫進 `letters/` 的版面只有 Editor 那側有）"
                 + " —— ⛔ 這不是失敗，是這個宿主沒有這一步";
        return null;
    }

    // ⛔ 缺欄**不回 0**：「Server 沒印這個數字」與「真的是 0」不是同一件事，
    //   而後者會讓呼叫端把一次沒發生的扣款當成發生過。
    static int ValueInt(SCP_CmdResult iResult, string iKey)
    {
        foreach (KeyValuePair<string, string> aPair in iResult.Values)
            if (string.Equals(aPair.Key, iKey, StringComparison.Ordinal)
                && int.TryParse(aPair.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aValue))
                return aValue;
        throw new InvalidOperationException(
            $"[Books] 付款成功而讀不到 `{iKey}` ⇒ **我不知道這筆是怎麼付的**，⛔ 不當作 0");
    }

    /// <summary>挑出真正的理由那一行（帶 ✗ 的），⛔ 不要第一行 —— 那常常是路由讀數。</summary>
    static string Reason(SCP_CmdResult iResult)
    {
        foreach (string aLine in iResult.Lines)
            if (aLine.IndexOf('✗') >= 0) return aLine.Trim();
        foreach (string aLine in iResult.Lines)
            if (!string.IsNullOrWhiteSpace(aLine)) return aLine.Trim();
        return "(沒有理由那一行)";
    }
}
