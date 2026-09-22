// 區塊職責：`cmd demurrage` —— 跨日存款保管費的扣繳（TASK-0278 把它從 Unity 那側整段搬過來）。
// 物理意義：它**會動錢**（debit 繳費者＋credit 央行）⇒ 跟 `cmd bank` 同族，走 `ServerDelegateCmd`
//           ⇒ 一律在常駐 Server 裡跑。那正是 `SCP_BankLedger` 那把 debit 鎖成立的前提：
//           **只有一顆 process 在寫**。⛔ 在 CLI process 裡直接算完扣掉＝安靜地多一個寫入端。
// 數值影響：`op=preview`（預設）與 `op=parity` **零寫入**；`op=run` 要 `confirm=1` 才動錢。
//
// ⚠ **本支不判「今天是不是跨日」，也不推進任何 state** —— 那是觸發端（Unity daemon tick）的事。
//   本支拿到一個日期就照那個日期算，重跑由 `idem_key` 擋（同一天跑兩次 ⇒ 一毛錢都不會再動）。
//   📌 TASK-0278 ⑧：搬的是扣繳，⛔ 不含「誰來觸發」——那是下一張單。
//
// ⚠ **廣播由觸發端貼，不在這裡貼**：酒館寫入端目前是 Editor（`tavern.writer=editor`）
//   ⇒ Server 這一側沒有資格寫酒館。本支把本文寫進 `body_out` 指定的檔，由呼叫端讀去貼。
//   ⛔ 不在這裡偷開第二個酒館寫入端 —— 那是 TASK-0106 正在收斂的那條線。
using SCP.Core.Bank;
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_Demurrage : ServerDelegateCmd
{
    public override string Name => "demurrage";

    public override string Summary =>
        "跨日存款保管費：算帳單／真的扣／跟舊實作對拍 —— 由 Senate Server 執行（**單一寫入端**）";

    public override string PortNote =>
        "⚠ 觸發仍在 Unity（daemon 跨日 tick）—— 本支只負責**扣繳與組廣播**，"
        + "⛔ 不判跨日、不推進 state、不貼酒館（TASK-0278 ⑧）";

    public override string Example =>
        SCP_CmdRegistry.Invoke("demurrage --arg op=preview --arg date=2026-09-20");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("op", "做什麼", iDefault: "preview",
                    iChoices: new[] { "preview", "run", "parity" }),
                new SCP_CmdArgSpec("data_root", "資料根（政策設定從這裡找）", iRequired: true),
                new SCP_CmdArgSpec("letters_root",
                    "persona 信件夾根 —— **帳號歸一要它**（`sirius` → `Spectre`）。"
                    + "⛔ 少了它就不歸一，而錢會落在不同帳戶上（本 Cmd 會出聲，不靜默）", iRequired: true),
                new SCP_CmdArgSpec("bank_root",
                    "銀行帳本根（絕對路徑）。CLI 沒給時會用 `<資料根>/Bank` 補上並印出來（推導值，不可設定）",
                    iRequired: true),
                new SCP_CmdArgSpec("date", "要結算哪一個 **UTC** 日（`YYYY-MM-DD`）；不給＝今天", iDefault: ""),
                // ⭐ 為什麼 preview 是預設、run 還要 confirm：扣款的錯**不會在當下叫** ——
                //   每一欄都合法，錢也真的動了。⇒ 讓「看一眼」比「做下去」更容易打。
                new SCP_CmdArgSpec("confirm", "=1 ⇒ `op=run` 才會真的動錢", iDefault: ""),
                new SCP_CmdArgSpec("body_out",
                    "把廣播本文寫進這個檔（觸發端讀去貼酒館）。不給＝不寫檔，只印在輸出裡", iDefault: ""),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aData = iArgs.Get("data_root").Trim();
        string aBank = iArgs.Get("bank_root").Trim();
        string aOp = iArgs.Get("op").Trim();
        string aLetters = iArgs.Get("letters_root").Trim();
        string aDate = iArgs.Get("date").Trim();
        if (aDate.Length == 0) aDate = SCP_Demurrage.TodayUtc();

        if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);
        // 根給錯時**說它是根給錯**，⛔ 不要退化成「帳號不存在」（TASK-0260 那條血證）。
        if (!Directory.Exists(Path.Combine(aBank, SCP_BankAccounts.AccountsDirName)))
            return SCP_CmdResult.Fail(2,
                $"✗ `bank_root` 底下沒有 `{SCP_BankAccounts.AccountsDirName}/` ⇒ **這個根給錯了**，⛔ 不是「帳號不存在」",
                $"  · 給的是：`{aBank}`");

        if (aOp == "parity") return OpParity(aData, aBank, aLetters, aDate);

        bool aRun = aOp == "run";
        if (aRun && iArgs.Get("confirm").Trim() != "1")
            return SCP_CmdResult.Fail(2, "✗ `op=run` 會真的動錢 ⇒ 要 `--arg confirm=1`",
                                      "  · 只想看帳單：`--arg op=preview`（零寫入）");

        SCP_DemurrageOutcome aOut = SCP_Demurrage.Apply(aData, aBank, aLetters, aDate, iDryRun: !aRun);

        var aR = new SCP_CmdResult();
        aR.Lines.Add($"# 跨日存款保管費　`{aDate}`　{(aRun ? "**op=run（真的扣了）**" : "op=preview（**零寫入**）")}");
        aR.Lines.Add($"- 政策：門檻 {aOut.Plan.Threshold} ／ 費率 {aOut.Plan.FeeRateDisplay}%"
                     + $"（{aOut.Plan.FeePermille}‰）／ 央行 `{aOut.Plan.CentralBank}`"
                     + $"／ 央行豁免 {(aOut.Plan.ExemptCentral ? "開" : "關")}");
        foreach (string p in aOut.Problems) aR.Lines.Add("⚠ " + p);

        aR.Lines.Add("");
        aR.Lines.Add("| 帳戶 | 結算前 | 超額 | 費用 | 這次動了 |");
        aR.Lines.Add("|---|---:|---:|---:|---|");
        foreach (SCP_DemurrageCharge c in aOut.Charges)
            aR.Lines.Add($"| `{c.AccountId}` | {c.BalanceBefore} | {c.Excess} | {c.PlannedFee} | "
                         + (c.Problem.Length > 0 ? "🔴 " + c.Problem
                            : c.Duplicate ? "0（冪等命中）"
                            : aRun ? c.Moved.ToString() : $"{c.Moved}（預計）")
                         + " |");
        if (aOut.Charges.Count == 0) aR.Lines.Add("| —— | | | | 這一輪沒有人要繳 |");

        aR.AddValue("date", aDate);
        aR.AddValue("dry_run", aRun ? "0" : "1");
        aR.AddValue("accounts_charged", aOut.BroadcastMeta["accounts_charged"]);
        aR.AddValue("accounts_safe", aOut.BroadcastMeta["accounts_safe"]);
        aR.AddValue("total_fee", aOut.TotalMoved.ToString());
        aR.AddValue("central_income", aOut.CentralIncome.ToString());
        aR.AddValue("central_balance_after", aOut.CentralBalanceAfter.ToString());
        aR.AddValue("subtag", aOut.Subtag);
        aR.AddValue("problems", aOut.Problems.Count.ToString());

        string aBodyOut = iArgs.Get("body_out").Trim();
        if (aBodyOut.Length > 0)
        {
            try
            {
                string? aDir = Path.GetDirectoryName(aBodyOut);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                File.WriteAllText(aBodyOut, aOut.BroadcastBody, new System.Text.UTF8Encoding(false));
                aR.AddValue("body_file", aBodyOut);
                aR.AddValue("body_len", aOut.BroadcastBody.Length.ToString());
                foreach (KeyValuePair<string, string> kv in aOut.BroadcastMeta)
                    aR.AddValue("meta_" + kv.Key, kv.Value);
            }
            catch (Exception e)
            {
                // ⚠ 錢已經動了而廣播寫不出去 —— 這一格**要大聲**：
                //   靜默的話，帳上有扣款而酒館沒有任何一則說明，看起來像「有人偷扣錢」。
                aR.Lines.Add($"🔴 廣播本文寫不進 `{aBodyOut}`：{e.Message}"
                             + "　—— ⚠ 錢的部分**已經照上表發生了**，⛔ 不要重跑（重跑是冪等的，但廣播仍然不會自己補）");
                aR.AddValue("body_file", "");
            }
        }
        else
        {
            aR.Lines.Add("");
            aR.Lines.Add("## 廣播本文（沒給 `body_out` ⇒ 只印在這裡）");
            aR.Lines.Add(aOut.BroadcastBody);
        }
        return aR;
    }

    // ===========================================================
    // 區塊職責：TASK-0278 ② 那道閘 —— 拿舊實作**已經寫在帳本上**的輸出跟新實作對拍。
    // ⚠ 它比的是逐帳戶的金額，⛔ 不是抽樣、⛔ 不是「我算過了」。
    // ===========================================================
    static SCP_CmdResult OpParity(string iData, string iBank, string iLetters, string iDate)
    {
        SCP_DemurrageParityReport aRep = SCP_DemurrageParity.Run(iData, iBank, iLetters, iDate);
        var aR = new SCP_CmdResult();
        aR.Lines.Add($"# 對拍（舊實作的帳本產物 ↔ 新實作同快照重算）　`{iDate}`");
        foreach (string p in aRep.Problems) aR.Lines.Add("⚠ " + p);
        if (aRep.SnapshotAtUtc.Length == 0)
        {
            aR.AddValue("date", iDate);
            aR.AddValue("rows", "0");
            // ⛔ 「沒有東西可以對拍」不可以長得像「對拍通過」：
            //   mismatches=0 ＋ exit 0 正是「通過」的形狀 ⇒ 這裡兩格都要不一樣。
            aR.AddValue("mismatches", "-1");
            aR.ExitCode = 4;
            return aR;
        }
        aR.Lines.Add($"- 快照時點：`{aRep.SnapshotAtUtc}`（那天第一筆保管費 entry 之前）"
                     + $"／快照裡有 **{aRep.AccountsInSnapshot}** 個帳戶");
        aR.Lines.Add("");
        aR.Lines.Add("| 帳戶（歸一後） | 舊扣繳 | 新扣繳 | 舊入庫 | 新入庫 | |");
        aR.Lines.Add("|---|---:|---:|---:|---:|---|");
        foreach (SCP_DemurrageParityRow r in aRep.Rows)
            aR.Lines.Add($"| `{r.AccountId}` | {r.OldFee} | {r.NewFee} | {r.OldDeposit} | {r.NewDeposit} | "
                         + (r.Match ? "✅" : r.FeeMatch ? "⚠ 入庫歸屬不同（費用相同）" : "🔴 費用不符") + " |");
        aR.Lines.Add("");
        aR.Lines.Add($"- 扣繳合計：舊 **{aRep.OldFeeTotal}** ／ 新 **{aRep.NewFeeTotal}**"
                     + $"　　入庫合計：舊 **{aRep.OldDepositTotal}** ／ 新 **{aRep.NewDepositTotal}**");
        // ⚠ 兩個計數**分開報**：「誰付了多少」與「那筆錢掛在哪個 ref 下入庫」是兩件事，
        //   而它們不符的意義完全不同 —— 前者是算錯了，後者是歸屬換了而錢守恆。
        aR.Lines.Add(aRep.FeeMismatches == 0
            ? $"✅ **費用逐帳戶相同，不符 0 筆**（{aRep.Rows.Count} 個帳戶逐位比對，⛔ 不是抽樣）"
            : $"🔴 **費用有 {aRep.FeeMismatches} 個帳戶對不上** —— ⛔ 別搬，先查那幾格");
        if (aRep.DepositMismatches > 0)
            aR.Lines.Add($"⚠ **入庫歸屬有 {aRep.DepositMismatches} 個帳戶不同**"
                         + $"（總額 {aRep.OldDepositTotal} → {aRep.NewDepositTotal}）—— "
                         + "舊實作用「回讀餘額」算入庫金額，於是同一個帳戶上的兩筆扣款被併成一筆 credit，"
                         + "掛在**後面那個**繳費者的 ref 下；新實作一腳扣一腳存、各自帶自己的鑰匙。"
                         + "⇒ 這一格**要人判**，⛔ 不要自己當成通過。");
        aR.AddValue("date", iDate);
        aR.AddValue("rows", aRep.Rows.Count.ToString());
        aR.AddValue("mismatches", aRep.Mismatches.ToString());
        aR.AddValue("fee_mismatches", aRep.FeeMismatches.ToString());
        aR.AddValue("deposit_mismatches", aRep.DepositMismatches.ToString());
        aR.AddValue("old_fee_total", aRep.OldFeeTotal.ToString());
        aR.AddValue("new_fee_total", aRep.NewFeeTotal.ToString());
        aR.AddValue("old_deposit_total", aRep.OldDepositTotal.ToString());
        aR.AddValue("new_deposit_total", aRep.NewDepositTotal.ToString());
        aR.AddValue("snapshot_at_utc", aRep.SnapshotAtUtc);
        if (aRep.Mismatches > 0) aR.ExitCode = 1;
        return aR;
    }
}
