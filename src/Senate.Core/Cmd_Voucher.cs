// 區塊職責：`senate cmd voucher` —— 券的**唯一入口**（TASK-0243）。
// 物理意義：`ServerDelegateCmd` ⇒ **一律在常駐 Server 裡跑**。
//           券**不記歷史**（Tim 2026-09-18 拍板）⇒ 存的是狀態不是事件
//           ⇒ 「只有一個寫入端」不是偏好，是這個設計成立的**唯一前提**：
//           兩個寫入端互相覆蓋之後，留下的是一個完全合法的數字，而**沒有歷史可以回推**。
// 數值影響：寫的是 `letters/<persona>/vouchers/<券名>.json`。⛔ 完全不碰錢（那是 `bank`）。
//
// 🩸 判準：
//   ① **`letters_root` 必填，本層不推導** —— 同 `bank` 的 `bank_root`。
//      本層不知道自己跑在誰的宿主裡，推導這一格等於在 Cmd 裡多一份「路徑住哪」的答案。
//   ② **`region` 在寫入時必填** —— 券沒有歷史，`updated_region` / `updated_at_utc`
//      是唯一的「誰動過它」線索。⚠ 讓它可空的話，那條線索會在最需要它的那天是空的。
//   ③ **券名先過合法性**（`SCP_LettersPaths.IsValidVoucherName`）：檔名＝券名
//      ⇒ 含 `/` 或 `..` 會憑空長出一個資料夾**而不報錯**。
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace Senate.Core;

public sealed class Cmd_Voucher : ServerDelegateCmd
{
    public override string Name => "voucher";

    public override string Summary =>
        "券：查／發／花／分區遷移 —— 由 Senate Server 執行（**單一寫入端**）"
        + "　⚠ 券**不記歷史**，所以那個前提是它成立的必要條件";

    public override string PortNote =>
        "券綁 persona（`letters/<persona>/vouchers/<券名>.json`）、**跨區共用** ——"
        + "⛔ 沒有區的維度。擋住跨區對撞的不是路徑形狀，是「同時只有一個區在跑」這個**營運前提**"
        + "（Tim 2026-09-18：兩個專案是接力模式）";

    public override string Example => SCP_CmdRegistry.Invoke("voucher --arg op=balance --arg persona=basecamp --arg voucher=canvas");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("op", "做什麼", iDefault: "balance",
                    iChoices: new[] { "balance", "list", "usage", "grant", "consume", "migrate" }),
                new SCP_CmdArgSpec("letters_root",
                    "persona 信件夾根（絕對路徑）。⛔ 本層**不推導**它 —— 跨專案共用的根，推導就會跟著專案漂",
                    iRequired: true),
                new SCP_CmdArgSpec("persona", "誰的券", iDefault: ""),
                new SCP_CmdArgSpec("voucher", "券名（＝檔名）—— `list` 以外都必填", iDefault: ""),
                new SCP_CmdArgSpec("amount", "張數（正整數）", iDefault: "0"),
                new SCP_CmdArgSpec("expires_at",
                    "限時券的到期時刻（ISO-8601 UTC）。**空 ＝ 永久券**", iDefault: ""),
                new SCP_CmdArgSpec("region",
                    "現在是哪一區在動它 —— **寫入時必填**（券沒有歷史，這是唯一的「誰動過」線索）", iDefault: ""),
                new SCP_CmdArgSpec("source", "為什麼發／花這批券", iDefault: ""),
                new SCP_CmdArgSpec("ref", "指回現場（場次 id／seq／單號）", iDefault: ""),
                new SCP_CmdArgSpec("from_path",
                    "`migrate` 用：舊券檔的絕對路徑（舊 schema：`batches[]`）。⛔ 本層不推導它", iDefault: ""),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aLettersRaw = iArgs.Get("letters_root");
        if (string.IsNullOrWhiteSpace(aLettersRaw))
            return SCP_CmdResult.Fail(2, "✗ 缺 `letters_root` —— 本層**不推導**它");
        var aLetters = new SCP_LettersRoot(aLettersRaw.Replace('\\', '/').TrimEnd('/'));

        string aPersona = iArgs.Get("persona").Trim();
        if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ 缺 `persona`");

        string aOp = iArgs.Get("op");
        if (aOp == "list") return OpList(aLetters, aPersona);

        string aVoucher = iArgs.Get("voucher").Trim();
        if (aVoucher.Length == 0)
            return SCP_CmdResult.Fail(2, $"✗ op={aOp} 缺 `voucher`（券名＝檔名）");
        if (!SCP_LettersPaths.IsValidVoucherName(aVoucher))
            return SCP_CmdResult.Fail(2,
                $"✗ 券名 `{aVoucher}` 不能當檔名（含 `/`、`\\`、`..` 或檔名非法字元）"
                + " —— ⛔ 擋在這裡，否則它會**憑空長出一個資料夾而不報錯**");

        return aOp switch
        {
            "balance" => OpBalance(aLetters, aPersona, aVoucher),
            "usage" => OpUsage(aLetters, aPersona, aVoucher, iArgs),
            "grant" => OpGrant(aLetters, aPersona, aVoucher, iArgs),
            "consume" => OpConsume(aLetters, aPersona, aVoucher, iArgs),
            "migrate" => OpMigrate(aLetters, aPersona, aVoucher, iArgs),
            _ => SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（balance|list|usage|grant|consume|migrate）"),
        };
    }

    // ── 讀 ────────────────────────────────────────────────────

    static SCP_CmdResult OpList(SCP_LettersRoot iLetters, string iPersona)
    {
        List<string> aAll = SCP_VoucherStore.ListVouchers(iLetters, iPersona);
        var aResult = SCP_CmdResult.Success($"# `{iPersona}` 的券 {aAll.Count} 種");
        DateTime aNow = DateTime.UtcNow;
        foreach (string aName in aAll)
        {
            SCP_VoucherBook aBook = SCP_VoucherStore.Load(iLetters, iPersona, aName, out string? aProblem);
            if (aProblem != null) { aResult.Lines.Add($"  ⚠ {aName}：{aProblem}"); continue; }
            aResult.Lines.Add($"  · {aName,-20} 可花 {aBook.Spendable(aNow),6}"
                              + $"（永久 {aBook.Permanent} ＋ 未過期限時 {aBook.ExpiringAlive(aNow)}）");
            aResult.AddValue("v/" + aName, aBook.Spendable(aNow).ToString());
        }
        aResult.AddValue("voucher_kinds", aAll.Count.ToString());
        return aResult;
    }

    static SCP_CmdResult OpBalance(SCP_LettersRoot iLetters, string iPersona, string iVoucher)
    {
        SCP_VoucherBook aBook = SCP_VoucherStore.Load(iLetters, iPersona, iVoucher, out string? aProblem);
        // ⛔ 讀不了**不是**「零張」：後者是讀數，前者是「我不知道」。
        if (aProblem != null) return SCP_CmdResult.Fail(1, "✗ " + aProblem);

        DateTime aNow = DateTime.UtcNow;
        var aResult = SCP_CmdResult.Success(
            $"# 券 `{iVoucher}`　`{iPersona}`　可花 **{aBook.Spendable(aNow)}**");
        aResult.Lines.Add($"  · 永久 {aBook.Permanent}　未過期限時 {aBook.ExpiringAlive(aNow)}"
                          + $"　（⚠ 可花總額 ＝ 兩者之和，⛔ 不是任何一批的餘額）");
        foreach (SCP_VoucherBatch aBatch in aBook.Expiring)
        {
            bool aAlive = SCP_VoucherBook.IsAlive(aBatch, aNow);
            aResult.Lines.Add($"    {(aAlive ? "·" : "⛔")} {aBatch.Amount,6} 張"
                              + $"　到期 {(aBatch.ExpiresAtUtc.Length > 0 ? aBatch.ExpiresAtUtc : "（永久）")}"
                              + (aAlive ? "" : "　已過期（下次寫入時清掉）")
                              + (aBatch.Source.Length > 0 ? $"　{aBatch.Source}" : ""));
        }
        if (aBook.UpdatedAtUtc.Length > 0)
            aResult.Lines.Add($"  · 最後寫入 {aBook.UpdatedAtUtc}"
                              + $"　區＝{(aBook.UpdatedRegion.Length > 0 ? aBook.UpdatedRegion : "（未宣告）")}");
        if (aBook.MigratedRegions.Count > 0)
            aResult.Lines.Add($"  · 已遷過的區：{string.Join("、", aBook.MigratedRegions)}");

        aResult.AddValue("spendable", aBook.Spendable(aNow).ToString());
        aResult.AddValue("permanent", aBook.Permanent.ToString());
        aResult.AddValue("expiring", aBook.ExpiringAlive(aNow).ToString());
        return aResult;
    }

    // ===========================================================
    // 區塊職責：某一批（按 `ref`）的**用量三值** —— 發了幾張／還剩幾張／用了幾張。
    // 物理意義：自由時間收工要回報「本場那 10 顆免費像素用了幾顆」，
    //          而 `op=balance` 回的是**所有**未過期限時券 ⇒ 同時持有兩場的券時它答的是別的問題。
    // 數值影響：純讀。
    //
    // 🩸 判準：**`found=0` 時三個數字一律 0，⛔ 不回一個「真實的發放量」**。
    //   回了的話呼叫端只要做一次「granted − 0」就會印出「全數用畢」——
    //   TASK-0195 那隻病（**查無被算成用完**）就是這樣長出來的，
    //   而兩者在畫面上一模一樣。⇒ 讓那個減法在物理上拿不到數字。
    //
    // ⚠ **射程**：券不記歷史 ⇒ 批次在過期後的下一次寫入就被清掉
    //   ⇒ 那之後本 op 只能回 `found=0`（＝「我不知道」，⛔ 不是「沒用」）。
    //   這是「不留歷史」這個拍板的**已知代價**，不是漏掉的一格。
    // ===========================================================
    static SCP_CmdResult OpUsage(SCP_LettersRoot iLetters, string iPersona, string iVoucher, SCP_CmdArgs iArgs)
    {
        string aRef = iArgs.Get("ref").Trim();
        if (aRef.Length == 0)
            return SCP_CmdResult.Fail(2, "✗ op=usage 缺 `ref` —— ⛔ 不給就回總量的話，"
                                       + "那是拿「沒指定」冒充「全部」");

        SCP_VoucherBook aBook = SCP_VoucherStore.Load(iLetters, iPersona, iVoucher, out string? aProblem);
        if (aProblem != null) return SCP_CmdResult.Fail(1, "✗ " + aProblem);

        DateTime aNow = DateTime.UtcNow;
        int aGranted = 0, aRemain = 0, aAlive = 0;
        bool aFound = false;
        foreach (SCP_VoucherBatch aBatch in aBook.Expiring)
        {
            if (!string.Equals(aBatch.Ref, aRef, StringComparison.Ordinal)) continue;
            aFound = true;
            aGranted += aBatch.Granted > 0 ? aBatch.Granted : aBatch.Amount;
            aRemain += aBatch.Amount;
            if (SCP_VoucherBook.IsAlive(aBatch, aNow)) aAlive += aBatch.Amount;
        }
        if (!aFound) { aGranted = 0; aRemain = 0; aAlive = 0; }
        int aUsed = aGranted - aRemain;
        if (aUsed < 0) aUsed = 0;

        var aResult = SCP_CmdResult.Success(aFound
            ? $"# `{iVoucher}`　`{iPersona}`　`{aRef}`：發 **{aGranted}**　剩 **{aRemain}**　用 **{aUsed}**"
            : $"# `{iVoucher}`　`{iPersona}`　`{aRef}`：**查無這一批** ——"
              + " ⛔ 那是「我不知道」不是「一張都沒用」（批次可能已過期被清掉）");
        if (aFound)
            aResult.Lines.Add($"  · 其中**還花得掉的** {aAlive}"
                              + (aAlive < aRemain ? "　⚠ 其餘已過期（下次寫入時清掉）" : ""));
        aResult.AddValue("found", aFound ? "1" : "0");
        aResult.AddValue("granted", aGranted.ToString());
        aResult.AddValue("remain", aRemain.ToString());
        aResult.AddValue("alive", aAlive.ToString());
        aResult.AddValue("used", aUsed.ToString());
        return aResult;
    }

    // ── 寫 ────────────────────────────────────────────────────

    /// <summary>寫入前的共用閘：`region` 必填（判準②）。</summary>
    static string? RequireRegion(SCP_CmdArgs iArgs, out string oRegion)
    {
        oRegion = iArgs.Get("region").Trim();
        return oRegion.Length > 0
            ? null
            : "✗ 寫入要帶 `region` —— 券沒有歷史，`updated_region` 是唯一的「誰動過它」線索。"
              + "⛔ 讓它可空的話，那條線索會在最需要它的那天是空的";
    }

    static SCP_CmdResult OpGrant(SCP_LettersRoot iLetters, string iPersona, string iVoucher, SCP_CmdArgs iArgs)
    {
        string? aWhy = RequireRegion(iArgs, out string aRegion);
        if (aWhy != null) return SCP_CmdResult.Fail(2, aWhy);
        if (!int.TryParse(iArgs.Get("amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aAmount)
            || aAmount <= 0)
            return SCP_CmdResult.Fail(2, $"✗ `amount` 必須是正整數（收到 '{iArgs.Get("amount")}'）");

        SCP_VoucherBook aBook = SCP_VoucherStore.Load(iLetters, iPersona, iVoucher, out string? aProblem);
        if (aProblem != null) return SCP_CmdResult.Fail(1, "✗ " + aProblem + "　⛔ 不在讀不了的檔上面加券");

        DateTime aNow = DateTime.UtcNow;
        string aExpires = iArgs.Get("expires_at").Trim();
        if (aExpires.Length == 0) aBook.Permanent += aAmount;
        else
            aBook.Expiring.Add(new SCP_VoucherBatch
            {
                Amount = aAmount,
                Granted = aAmount,   // ⚠ 發放量，⛔ 之後不再變動（`op=usage` 靠它答「用了幾張」）
                ExpiresAtUtc = aExpires,
                GrantedAtUtc = aNow.ToString("o", CultureInfo.InvariantCulture),
                Source = iArgs.Get("source"),
                Ref = iArgs.Get("ref"),
            });

        if (!SCP_VoucherStore.Save(iLetters, aBook, aNow, aRegion, out int aDropped, out string? aError))
            return SCP_CmdResult.Fail(1, "✗ " + aError);

        var aResult = SCP_CmdResult.Success(
            $"✓ 發 {aAmount} 張 `{iVoucher}` 給 `{iPersona}`"
            + (aExpires.Length == 0 ? "（永久券）" : $"（限時券，到期 {aExpires}）"));
        Tail(aResult, aBook, aNow, aDropped);
        return aResult;
    }

    static SCP_CmdResult OpConsume(SCP_LettersRoot iLetters, string iPersona, string iVoucher, SCP_CmdArgs iArgs)
    {
        string? aWhy = RequireRegion(iArgs, out string aRegion);
        if (aWhy != null) return SCP_CmdResult.Fail(2, aWhy);
        if (!int.TryParse(iArgs.Get("amount"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aAmount)
            || aAmount <= 0)
            return SCP_CmdResult.Fail(2, $"✗ `amount` 必須是正整數（收到 '{iArgs.Get("amount")}'）");

        SCP_VoucherBook aBook = SCP_VoucherStore.Load(iLetters, iPersona, iVoucher, out string? aProblem);
        if (aProblem != null) return SCP_CmdResult.Fail(1, "✗ " + aProblem + "　⛔ 不在讀不了的檔上面扣券");

        DateTime aNow = DateTime.UtcNow;
        if (!SCP_VoucherStore.TryConsume(aBook, aAmount, aNow, out string? aNo))
            // ⛔ 券不足是**合法結果**不是程式錯 ⇒ exit 1（呼叫端分得出「不夠」與「壞了」）
            return SCP_CmdResult.Fail(1, $"✗ {aNo}");

        if (!SCP_VoucherStore.Save(iLetters, aBook, aNow, aRegion, out int aDropped, out string? aError))
            // 🩸 這裡失敗代表**扣了但沒落盤** ⇒ 一定要說「這一筆沒有成立」，
            //   ⛔ 不可以只說「寫入失敗」—— 呼叫端會不知道券到底有沒有少。
            return SCP_CmdResult.Fail(1, "✗ " + aError + "　⇒ **這一筆沒有成立**（記憶體扣了，磁碟沒動）");

        var aResult = SCP_CmdResult.Success($"✓ 花 {aAmount} 張 `{iVoucher}`（`{iPersona}`）");
        Tail(aResult, aBook, aNow, aDropped);
        return aResult;
    }

    static SCP_CmdResult OpMigrate(SCP_LettersRoot iLetters, string iPersona, string iVoucher, SCP_CmdArgs iArgs)
    {
        string? aWhy = RequireRegion(iArgs, out string aRegion);
        if (aWhy != null) return SCP_CmdResult.Fail(2, aWhy);
        string aFrom = iArgs.Get("from_path").Trim();
        if (aFrom.Length == 0)
            return SCP_CmdResult.Fail(2, "✗ `migrate` 缺 `from_path`（舊券檔）—— ⛔ 本層不推導它");

        SCP_VoucherBook aBook = SCP_VoucherStore.Load(iLetters, iPersona, iVoucher, out string? aProblem);
        if (aProblem != null) return SCP_CmdResult.Fail(1, "✗ " + aProblem + "　⛔ 不在讀不了的檔上面加總");

        // ⭐ 冪等鍵是**區名**，判斷寫在這裡（⛔ 不靠呼叫端記得）。
        //   ⚠ 已遷過要**大聲說**並 exit 0：靜默成功會讓「跑過了」跟「剛剛加了一次」同形。
        if (SCP_VoucherStore.AlreadyMigrated(aBook, aRegion))
        {
            var aSkip = SCP_CmdResult.Success(
                $"・`{aRegion}` 區**已經遷過** `{iVoucher}` ⇒ 這次一張都沒加（冪等）");
            aSkip.Lines.Add($"  · 已遷過的區：{string.Join("、", aBook.MigratedRegions)}");
            aSkip.AddValue("migrated", "0");
            aSkip.AddValue("already", "1");
            return aSkip;
        }

        (int aPermanent, List<SCP_VoucherBatch> aExpiring, string? aReadErr) = ReadLegacy(aFrom);
        if (aReadErr != null) return SCP_CmdResult.Fail(1, "✗ 舊券檔讀不了：" + aReadErr);

        DateTime aNow = DateTime.UtcNow;
        SCP_VoucherStore.ApplyMigration(aBook, aRegion, aPermanent, aExpiring);
        // ⚠ 標記與加總**同一次寫入**落盤 —— 先寫標記再加總的話，中途失敗留下的是
        //   「標記說遷過了、而券一張都沒加」，**而它之後永遠不會再跑一次**。
        if (!SCP_VoucherStore.Save(iLetters, aBook, aNow, aRegion, out int aDropped, out string? aError))
            return SCP_CmdResult.Fail(1, "✗ " + aError + "　⇒ **這次遷移沒有成立**（標記也沒落盤，可以重跑）");

        int aExpSum = 0;
        foreach (SCP_VoucherBatch aBatch in aExpiring) aExpSum += aBatch.Amount;
        var aResult = SCP_CmdResult.Success(
            $"✓ `{aRegion}` 區的 `{iVoucher}` 已遷入：永久 +{aPermanent}／限時 +{aExpSum}（{aExpiring.Count} 批）");
        aResult.AddValue("migrated", "1");
        aResult.AddValue("migrated_permanent", aPermanent.ToString());
        aResult.AddValue("migrated_expiring", aExpSum.ToString());
        Tail(aResult, aBook, aNow, aDropped);
        return aResult;
    }

    /// <summary>
    /// 讀舊券檔（schema v2：`batches[{amount|remain, expires_at}]`）。
    /// <para>⚠ 取的是 `remain` 不是 `amount` —— `amount` 是**發放時**的張數，
    /// 花掉的那些不在裡面扣。拿 amount 遷移＝把已經花掉的券再發一次，
    /// 而它是一個完全合法的數字。</para>
    /// </summary>
    static (int Permanent, List<SCP_VoucherBatch> Expiring, string? Error) ReadLegacy(string iPath)
    {
        var aExpiring = new List<SCP_VoucherBatch>();
        int aPermanent = 0;
        try
        {
            if (!File.Exists(iPath)) return (0, aExpiring, $"檔不在（{iPath}）");
            SCP.Core.Json.SCP_JsonData aData = SCP.Core.Json.SCP_JsonParser.Parse(File.ReadAllText(iPath));
            SCP.Core.Json.SCP_JsonData aBatches = aData["batches"];
            if (aBatches.Exists && aBatches.IsArray)
            {
                foreach (SCP.Core.Json.SCP_JsonData aItem in aBatches)
                {
                    int aRemain = aItem.GetInt("remain", aItem.GetInt("amount", 0));
                    if (aRemain <= 0) continue;
                    string aExpires = aItem.GetString("expires_at", "");
                    if (aExpires.Length == 0) aPermanent += aRemain;
                    else
                        aExpiring.Add(new SCP_VoucherBatch
                        {
                            Amount = aRemain,
                            // ⚠ 發放量取舊檔的 `amount`（不是 remain）—— 遷過來之後
                            //   「本場發了幾張」才答得出來；remain 只回答「還剩幾張」。
                            Granted = aItem.GetInt("amount", aRemain),
                            ExpiresAtUtc = aExpires,
                            GrantedAtUtc = aItem.GetString("granted_at", ""),
                            Source = aItem.GetString("source", ""),
                            Ref = aItem.GetString("ref", ""),
                        });
                }
            }
            else
            {
                // legacy v1：只有純量 `balance` ⇒ 讀成一批永久券（與舊系統讀取結果逐值相同）
                aPermanent = aData.GetInt("balance", 0);
            }
        }
        catch (Exception e) { return (0, aExpiring, $"{e.GetType().Name}: {e.Message}"); }
        return (aPermanent, aExpiring, null);
    }

    /// <summary>每一次寫入之後的共用尾巴：三個數字 ＋ 過期清掉幾張（⛔ 不靜默消失）。</summary>
    static void Tail(SCP_CmdResult ioResult, SCP_VoucherBook iBook, DateTime iNow, int iDropped)
    {
        ioResult.Lines.Add($"  · 可花 **{iBook.Spendable(iNow)}**"
                           + $"（永久 {iBook.Permanent} ＋ 未過期限時 {iBook.ExpiringAlive(iNow)}）");
        if (iDropped > 0)
            ioResult.Lines.Add($"  · ⏳ 順手清掉 **{iDropped}** 張已過期的券"
                               + "（⛔ 這是清理不是沒收 —— 它們在讀取端本來就已經不算）");
        ioResult.AddValue("spendable", iBook.Spendable(iNow).ToString());
        ioResult.AddValue("permanent", iBook.Permanent.ToString());
        ioResult.AddValue("expiring", iBook.ExpiringAlive(iNow).ToString());
        ioResult.AddValue("dropped_expired", iDropped.ToString());
    }
}
