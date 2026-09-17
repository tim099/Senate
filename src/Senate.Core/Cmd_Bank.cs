// 區塊職責：`senate cmd bank` —— 新版銀行的**唯一入口**（TASK-0209 B 段）。
// 物理意義：開戶／查餘額／入帳／扣款全部走這一支，而這一支是 `ServerDelegateCmd`
//           ⇒ **一律在常駐 Server 裡跑**。那正是 `SCP_BankLedger` 那把 debit 鎖成立的前提：
//           鎖是 in-process 的，只有「寫入端只有一個 process」時它才真的擋得住。
// 數值影響：寫的是 `<bankRoot>/accounts/` 與 `<bankRoot>/ledger/`。⛔ 完全不碰舊的 `Treasury/`。
//
// 🩸 設計判準：
//   ① **讀寫都走 Server，不分兩條路。** 讀（balance／accounts）其實可以原生跑，
//      而分兩條的代價是**下一個人往「讀」那條加一個寫**，然後鎖就失效了 ——
//      那種錯不會有任何一層喊。小團隊維護的判準是「入口少」而不是「每條路最佳化」。
//   ② **`bank_root` 是必填參數，本層不推導。** 本層不知道自己跑在誰的宿主裡 ——
//      推導這一格等於在 Cmd 裡多一份「路徑住哪」的答案，而那份會跟宿主那份漂。
//      ⚠ 2026-09-17 起 CLI 那側補的值是 **`<資料根>/Bank`**（推導，不再是可填的 `bankRoot`；
//      Tim 拍板「不額外設定」）——⛔ 仍然**印出來**，不靜默注入。
//   ③ **錢的動作一律要 `kind`**：沒有 kind 的錢，日後沒有人答得出它為什麼動。
using SCP.Core.Bank;
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_Bank : ServerDelegateCmd
{
    public override string Name => "bank";

    public override string Summary =>
        "新版銀行：開戶／查餘額／入帳／扣款 —— 由 Senate Server 執行（**單一寫入端**）"
        + "　⚠ 遷移前＝**測試用**，實際餘額以舊 Treasury 為準（D27）";

    public override string PortNote =>
        "⚠ **不是終局形**：舊的 `Treasury/`（python + Editor）2026-09-15（D27）拍板"
        + "**不是唯讀歷史，是仍在服役的權威** —— 兩套長期並存，遷移前新銀行只是測試用，錢以舊的為準";

    public override string Example => SCP_CmdRegistry.Invoke("bank --arg op=balance --arg account=cc");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("op", "做什麼", iDefault: "accounts",
                    iChoices: new[] { "accounts", "open", "balance", "credit", "debit", "transfer", "close", "reopen" }),
                new SCP_CmdArgSpec("bank_root",
                    "銀行帳本根（絕對路徑）。"
                    + "CLI 沒給時會用 `<AgentCommands 資料根>/Bank` 補上並印出來（推導值，不可設定）",
                    iRequired: true),
                new SCP_CmdArgSpec("account", "帳號 id（大小寫不拘 —— 寫入端一律正規化成小寫）"
                    + "；`transfer` 時它是**轉出方**", iDefault: ""),
                // ⚠ 轉帳的收款方**另開一格**而不是重用 `account` —— 一格裝兩個角色的話，
                //   「我填的是誰」要靠 op 才讀得出來，而錯填的代價是錢進了別人的帳。
                new SCP_CmdArgSpec("to_account", "轉帳的**收款方**帳號 id（`transfer` 必填）", iDefault: ""),
                new SCP_CmdArgSpec("display_name", "顯示名（open 用；可以有大小寫與空白，⛔ 不當 id）", iDefault: ""),
                new SCP_CmdArgSpec("amount", "金額（正整數；方向由 op 決定）", iDefault: "0"),
                new SCP_CmdArgSpec("kind", "為什麼動這筆錢（credit／debit **必填**）", iDefault: ""),
                new SCP_CmdArgSpec("ref", "指回現場（commit sha／seq／單號）—— credit／debit **必填**", iDefault: ""),
                new SCP_CmdArgSpec("description", "人讀的一句話", iDefault: ""),
                new SCP_CmdArgSpec("idem_key",
                    "冪等鍵：同一個鍵重送會回既有那一筆，⛔ 不會扣第二次", iDefault: ""),
                new SCP_CmdArgSpec("persona", "走哪條分道；空 ＝ 公用分道 `server`", iDefault: ""),
                // 🩸 2026-09-14：這兩格我原本**讀了卻沒宣告**，而框架有守衛當場擋下
                //  （「Cmd 取了一個自己沒宣告的參數 —— 規格與實作不同步」）。
                //  ⇒ 那道守衛值得記：它擋的正是「ArgSpec 與實作各說各話」那一族，
                //    而沒有它的話，`caller` 會靜默是空字串，錢就變成**沒有人簽名的**。
                new SCP_CmdArgSpec("caller", "誰動的這筆錢（落進 entry，⛔ 沒簽名的錢日後查不出是誰）—— credit／debit **必填**", iDefault: ""),
                new SCP_CmdArgSpec("cmd_id", "指回派這一筆的那個 cmd（追溯用）", iDefault: ""),
                new SCP_CmdArgSpec("reason", "為什麼銷戶（close **必填** —— 沒有理由的銷戶事後查不出來）", iDefault: ""),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aRoot = iArgs.Get("bank_root");
        if (string.IsNullOrWhiteSpace(aRoot))
            return SCP_CmdResult.Fail(2, "✗ 缺 `bank_root` —— 本層**不推導**它（跨專案共用的根，推導就會跟著專案漂）");

        string aOp = iArgs.Get("op");
        switch (aOp)
        {
            case "accounts": return Stamp(OpAccounts(aRoot));
            case "open": return Stamp(OpOpen(aRoot, iArgs));
            case "balance": return Stamp(OpBalance(aRoot, iArgs));
            case "credit":
            case "debit": return Stamp(OpPost(aRoot, iArgs, aOp == "debit"));
            case "transfer": return Stamp(OpTransfer(aRoot, iArgs));
            case "close": return Stamp(OpClose(aRoot, iArgs));
            case "reopen": return Stamp(OpReopen(aRoot, iArgs));
            default: return SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（accounts|open|balance|credit|debit|transfer|close|reopen）");
        }
    }

    // 區塊職責：D27 的定語 —— 每一次輸出都說一次「這不是那本帳」。
    // 物理意義：新銀行與舊 `Treasury/` **長期並存**，⇒「我有多少錢」有兩個答案，
    //          而**兩邊都不會報錯**（它們各自都對，只是在回答不同的問題）。
    // 🩸 為什麼掛在這裡而不是寫進文件：文件要有人去讀，而這一行長在**每一個看到數字的人**的必經路上。
    //    ⛔ 也不靠「大家記得」—— 記得是這個系統最不能依賴的東西。
    // ⚠ 遷移那天要回來把這一行拿掉（它屆時會變成一句過期的真話，而過期不會叫）。
    static SCP_CmdResult Stamp(SCP_CmdResult ioResult)
    {
        ioResult.Lines.Add("⚠ **遷移前新銀行＝測試用** —— 實際餘額以舊系統（`Treasury/`，酒館領薪那本）為準（D27）");
        return ioResult;
    }

    static SCP_CmdResult OpAccounts(string iRoot)
    {
        var aProblems = new List<string>();
        List<SCP_BankAccount> aAll = SCP_BankAccounts.LoadAll(iRoot, aProblems);
        Dictionary<string, int> aBal = SCP_BankLedger.GetAllBalances(iRoot);

        var aResult = SCP_CmdResult.Success($"# 帳戶 {aAll.Count} 個　根={iRoot}");
        int aSum = 0;
        foreach (SCP_BankAccount a in aAll)
        {
            aBal.TryGetValue(a.Id, out int b);
            aSum += b;
            aResult.Lines.Add($"  {(a.Status == SCP_BankAccountStatus.Closed ? "⛔" : "·")} "
                              + $"{a.Id,-36} {b,8}"
                              + (a.DisplayName.Length > 0 ? $"　{a.DisplayName}" : "")
                              + (a.Status == SCP_BankAccountStatus.Closed ? $"　（已銷戶：{a.ClosedReason}）" : ""));
        }
        aResult.Lines.Add($"  合計 {aSum}");

        // ⚠ 帳本裡有、而**沒有帳戶檔**的帳號要被看見：那是資料不一致（新系統不該長得出來），
        //   藏起來的話「錢在哪」就少一塊，而總計看起來仍然正常。
        var aOrphans = new List<string>();
        foreach (KeyValuePair<string, int> kv in aBal)
        {
            bool aFound = false;
            foreach (SCP_BankAccount a in aAll) if (a.Id == kv.Key) { aFound = true; break; }
            if (!aFound) aOrphans.Add($"{kv.Key}={kv.Value}");
        }
        if (aOrphans.Count > 0)
        {
            aResult.Lines.Add($"🔴 帳本裡有 {aOrphans.Count} 個帳號**沒有帳戶檔**（新系統不該出現）：{string.Join(", ", aOrphans)}");
            aResult.ExitCode = 5;
        }
        foreach (string p in aProblems) aResult.Lines.Add($"⚠ {p}");

        aResult.AddValue("account_count", aAll.Count.ToString());
        aResult.AddValue("total", aSum.ToString());
        aResult.AddValue("orphan_count", aOrphans.Count.ToString());

        // ⭐ 逐戶的機器可讀出口（TASK-0223）。上面那些 `Lines` 是給人看的、欄寬對齊過 ——
        //    呼叫端（銀行後台頁）若去 parse 它，那把尺會在**顯示格式改動的那天**壞掉，
        //    而症狀是「帳戶列表變空」或「餘額全變 0」，跟「真的沒有帳戶」同形。
        //    ⇒ 給程式讀的就給欄位，⛔ 不要讓它從人讀的那一份反解。
        // ⚠ 銷戶的帳戶**照樣列**（前綴 `closed:`）：藏起來的話「這個帳戶不存在」與
        //    「它被銷了」在呼叫端同形，而那兩者的處置相反。
        foreach (SCP_BankAccount a in aAll)
        {
            aBal.TryGetValue(a.Id, out int b);
            string aFlag = a.Status == SCP_BankAccountStatus.Closed ? "closed:" : "open:";
            aResult.AddValue("acc/" + a.Id, aFlag + b.ToString());
        }
        return aResult;
    }

    static SCP_CmdResult OpOpen(string iRoot, SCP_CmdArgs iArgs)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return SCP_CmdResult.Fail(2, "✗ open 需要 `account`");

        bool aOk = SCP_BankAccounts.TryOpen(iRoot, aAcct, iArgs.Get("display_name"),
                                            iArgs.Get("caller"), out SCP_BankAccount? aAccount, out string aWhy);
        if (!aOk) return SCP_CmdResult.Fail(1, "✗ 開戶沒有發生：" + aWhy);

        var aResult = SCP_CmdResult.Success($"✓ 已開戶 `{aAccount!.Id}`"
                                            + (aAccount.DisplayName.Length > 0 ? $"（{aAccount.DisplayName}）" : ""));
        aResult.Lines.Add($"  檔案：{SCP_BankAccounts.AccountPath(iRoot, aAccount.Id)}");
        aResult.AddValue("account", aAccount.Id);
        return aResult;
    }

    // ===========================================================
    // 區塊職責：`op=close` —— 銷戶。
    // 物理意義：帳戶檔**留著**、狀態改 `closed`（判準②：「查不到帳戶」與「它被銷了」不可同形）。
    //          ⛔ 不刪檔、⛔ 不動任何分錄 —— 帳本 append-only，歷史不因為戶頭關了就消失。
    // 數值影響：不動錢。⚠ 而**餘額不是 0 就不准關** —— 關掉一個還有錢的戶頭，
    //          那筆錢會變成「還在總額裡、卻沒有人能動它」，而沒有任何一層會喊。
    //          要關就先把錢處置掉（轉走或補反向分錄），⇒ 處置留在帳本上看得見。
    // 🩸 為什麼現在才有這一支（2026-09-17）：Tim 要把 `luna`／`codex`（別區綁定）移出本區，
    //    而這家銀行**沒有關戶頭的入口** ⇒「餘額 0 的開著」只能假裝成「移除了」。
    // ===========================================================
    static SCP_CmdResult OpClose(string iRoot, SCP_CmdArgs iArgs)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return SCP_CmdResult.Fail(2, "✗ close 需要 `account`");

        string aReason = iArgs.Get("reason");
        if (string.IsNullOrWhiteSpace(aReason))
            return SCP_CmdResult.Fail(2, "✗ close 需要 `reason` —— 沒有理由的銷戶，事後沒有人答得出它為什麼被關");

        SCP_BankAccount? aAcc = SCP_BankAccounts.TryLoad(iRoot, aAcct, out string aWhy);
        if (aAcc == null) return SCP_CmdResult.Fail(1, "✗ 沒有這一戶：" + aWhy);
        if (aAcc.Status == SCP_BankAccountStatus.Closed)
            return SCP_CmdResult.Success($"・`{aAcc.Id}` 本來就已經銷戶（{aAcc.ClosedAtUtc}）—— 這次沒有動作")
                                .AddValue("already_closed", "1");

        int aBal = SCP_BankLedger.GetBalance(iRoot, aAcc.Id);
        if (aBal != 0)
            return SCP_CmdResult.Fail(1, $"✗ `{aAcc.Id}` 餘額是 {aBal}，**不是 0 ⇒ 不准銷戶**。"
                                         + " 關掉一個還有錢的戶頭，那筆錢會留在總額裡而沒有人能動它。"
                                         + " 先把錢處置掉（轉走或補反向分錄），處置才會留在帳本上看得見。");

        aAcc.Status = SCP_BankAccountStatus.Closed;
        aAcc.ClosedAtUtc = System.DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ");
        aAcc.ClosedReason = aReason;
        if (!SCP_BankAccounts.TrySave(iRoot, aAcc, out string aSaveWhy))
            return SCP_CmdResult.Fail(1, "✗ 銷戶沒有落盤：" + aSaveWhy);

        var aResult = SCP_CmdResult.Success($"✓ 已銷戶 `{aAcc.Id}`（餘額 0）　理由：{aReason}");
        aResult.Lines.Add($"  檔案：{SCP_BankAccounts.AccountPath(iRoot, aAcc.Id)}（⛔ 檔留著，狀態改 closed）");
        aResult.AddValue("account", aAcc.Id);
        aResult.AddValue("closed", "1");
        return aResult;
    }

    // ===========================================================
    // 區塊職責：`op=reopen` —— 把銷戶**開回來**。
    // 🩸 為什麼一定要有（2026-09-17 血證）：我先做了 `close` 才發現那一戶**有主人**
    //    （`Luna` 是 @kaguya 的綁定，只是綁在 BTC 區），而當時**沒有回頭路** ——
    //    唯一的選項是手改 `accounts/<id>.json`，那是繞過工具去動錢的資料。
    //    ⇒ 一個不可逆的狀態變更，等於逼下一個人去手改檔案。**可逆是入口的責任，不是使用者的運氣。**
    // 數值影響：只翻狀態欄，⛔ 不動任何分錄、不動餘額。
    // ===========================================================
    static SCP_CmdResult OpReopen(string iRoot, SCP_CmdArgs iArgs)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return SCP_CmdResult.Fail(2, "✗ reopen 需要 `account`");

        string aReason = iArgs.Get("reason");
        if (string.IsNullOrWhiteSpace(aReason))
            return SCP_CmdResult.Fail(2, "✗ reopen 需要 `reason` —— 開回來跟關掉一樣要說得出為什麼");

        SCP_BankAccount? aAcc = SCP_BankAccounts.TryLoad(iRoot, aAcct, out string aWhy);
        if (aAcc == null) return SCP_CmdResult.Fail(1, "✗ 沒有這一戶：" + aWhy);
        if (aAcc.Status != SCP_BankAccountStatus.Closed)
            return SCP_CmdResult.Success($"・`{aAcc.Id}` 本來就是開著的 —— 這次沒有動作")
                                .AddValue("already_open", "1");

        string aWasClosedAt = aAcc.ClosedAtUtc, aWasReason = aAcc.ClosedReason;
        aAcc.Status = SCP_BankAccountStatus.Open;
        aAcc.ClosedAtUtc = "";
        aAcc.ClosedReason = "";
        if (!SCP_BankAccounts.TrySave(iRoot, aAcc, out string aSaveWhy))
            return SCP_CmdResult.Fail(1, "✗ 開回來沒有落盤：" + aSaveWhy);

        var aResult = SCP_CmdResult.Success($"✓ 已開回 `{aAcc.Id}`　理由：{aReason}");
        aResult.Lines.Add($"  （原本銷於 {aWasClosedAt}，理由：{aWasReason}）");
        aResult.AddValue("account", aAcc.Id);
        aResult.AddValue("reopened", "1");
        return aResult;
    }

    static SCP_CmdResult OpBalance(string iRoot, SCP_CmdArgs iArgs)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return OpAccounts(iRoot);

        // ⚠ 先問帳戶能不能用：沒開戶的帳號查餘額會回 0，而 **0 與「沒有這個帳號」同形**。
        SCP_BankAccountCheck aCheck = SCP_BankAccounts.CheckUsable(iRoot, aAcct);
        if (aCheck.Result == SCP_BankAccountCheck.Kind.Invalid || aCheck.Result == SCP_BankAccountCheck.Kind.NotOpened)
            return SCP_CmdResult.Fail(1, "✗ " + aCheck.Why);

        int aBal = SCP_BankLedger.GetBalance(iRoot, aAcct);
        string aId = SCP_BankId.Normalize(aAcct).Id;
        var aResult = SCP_CmdResult.Success($"{aId} 餘額 = {aBal} {SCP_BankEntry.DefaultCurrency}");
        if (aCheck.Result == SCP_BankAccountCheck.Kind.Closed)
            aResult.Lines.Add("⚠ 這個帳號**已銷戶**（餘額仍然算得出來，但收付會被擋）");
        aResult.AddValue("account", aId);
        aResult.AddValue("balance", aBal.ToString());
        return aResult;
    }

    static SCP_CmdResult OpPost(string iRoot, SCP_CmdArgs iArgs, bool iDebit)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return SCP_CmdResult.Fail(2, "✗ 需要 `account`");
        if (!int.TryParse(iArgs.Get("amount"), out int aAmount))
            return SCP_CmdResult.Fail(2, $"✗ amount 讀不出來：'{iArgs.Get("amount")}'"
                                         + " —— ⛔ 這不是「沒帶」，是**帶了但解析不出**，不猜");

        // ⭐ 署名三欄（TASK-0223）：`kind`（為什麼）／`ref`（指回現場）／`caller`（誰動的）。
        // 物理意義：`SCP_BankLedger` 那層**只擋 `kind`** —— 那是刻意的，它要能被 SelfTest 與內部流程
        //          用最小參數打。⇒ 擋在這裡，因為本 Cmd 是人與 agent 的**唯一入口**（檔頭那句）。
        // 🩸 為什麼不是「建議填」：空字串會安靜落進 entry，而一筆 `caller=""` 的錢
        //   與一筆真的由系統動的錢**在帳本上逐位元組同形** —— 日後查「這是誰動的」時，
        //   查不到的原因有兩個（沒填／真的是系統），而它們共用同一個出口。
        //   ⇒ 寫入端擋一次，勝過讀取端每次都要猜。
        var aMissing = new List<string>();
        if (string.IsNullOrWhiteSpace(iArgs.Get("kind"))) aMissing.Add("kind（為什麼動這筆錢）");
        if (string.IsNullOrWhiteSpace(iArgs.Get("ref"))) aMissing.Add("ref（指回現場：commit sha／seq／單號）");
        if (string.IsNullOrWhiteSpace(iArgs.Get("caller"))) aMissing.Add("caller（誰動的）");
        if (aMissing.Count > 0)
            return SCP_CmdResult.Fail(2, "✗ 動錢要署名，缺 " + aMissing.Count + " 欄：",
                                      "  · " + string.Join("\n  · ", aMissing),
                                      "  ⇒ 沒有署名的錢，日後查不出是誰、為什麼、指回哪裡。");

        SCP_BankPostResult aPost = iDebit
            ? SCP_BankLedger.Debit(iRoot, aAcct, aAmount, iArgs.Get("kind"), iArgs.Get("ref"),
                                   iArgs.Get("description"), iArgs.Get("caller"), iArgs.Get("cmd_id"),
                                   iArgs.Get("idem_key"))
            : SCP_BankLedger.Credit(iRoot, aAcct, aAmount, iArgs.Get("kind"), iArgs.Get("ref"),
                                    iArgs.Get("description"), iArgs.Get("caller"), iArgs.Get("cmd_id"),
                                    iArgs.Get("idem_key"));

        if (!aPost.Ok) return SCP_CmdResult.Fail(1, "✗ " + aPost.Why);

        SCP_BankEntry e = aPost.Entry!;
        int aBal = SCP_BankLedger.GetBalance(iRoot, e.AccountId);
        var aResult = SCP_CmdResult.Success(
            (aPost.Duplicate ? "↻ 冪等判重：回既有那一筆，**這次沒有動錢**" : (iDebit ? "✓ 已扣款" : "✓ 已入帳"))
            + $"　{e.AccountId} {(iDebit ? "-" : "+")}{e.Amount} {e.Currency}（{e.Kind}）");
        aResult.Lines.Add($"  entry：{e.Id}　餘額 = {aBal}");
        aResult.AddValue("account", e.AccountId);
        aResult.AddValue("entry_id", e.Id);
        aResult.AddValue("balance", aBal.ToString());
        // ⚠ 冪等要是**機器讀得到的值**，不只是一句話：呼叫端要分得出「扣了」與「本來就扣過了」。
        aResult.AddValue("duplicate", aPost.Duplicate ? "1" : "0");
        return aResult;
    }

    // ===========================================================
    // 區塊職責：轉帳 —— A 扣 N、B 增 N，**總量守恆**。
    // 物理意義：兩腳共用一個 `tx_id`，收款腳失敗時**回捲轉出腳**。
    // ⚠ 為什麼原子性做在這裡而不是頁面上：帳本沒有交易，⇒「兩腳都成功」不是天生的，
    //   是**有人負責**的。做在頁面上的話，每一個呼叫端都得自己寫一次回捲，
    //   而漏寫的那一個**不會報錯** —— 它只會讓錢停在半路，而兩邊的餘額各自看起來都正常。
    // 🩸 這正是為什麼本 Cmd 是「單一寫入端」：守恆是寫入端的性質，不是使用者的紀律。
    // ===========================================================
    static SCP_CmdResult OpTransfer(string iRoot, SCP_CmdArgs iArgs)
    {
        string aFrom = iArgs.Get("account");
        string aTo = iArgs.Get("to_account");
        if (string.IsNullOrWhiteSpace(aFrom)) return SCP_CmdResult.Fail(2, "✗ 需要 `account`（轉出方）");
        if (string.IsNullOrWhiteSpace(aTo)) return SCP_CmdResult.Fail(2, "✗ 需要 `to_account`（收款方）");
        // ⚠ 自己轉給自己**擋下來**：它在帳本上會留兩筆相消的分錄、餘額不變 ——
        //   ⇒ 「我轉錯了對象」與「我轉給自己」事後長得一樣，而前者要追、後者不用。
        if (string.Equals(aFrom.Trim(), aTo.Trim(), StringComparison.OrdinalIgnoreCase))
            return SCP_CmdResult.Fail(2, $"✗ 轉出方與收款方是同一戶（`{aFrom}`）—— 這不會改變任何餘額，"
                                         + "⛔ 不寫兩筆相消的分錄把帳本弄髒");

        if (!int.TryParse(iArgs.Get("amount"), out int aAmount))
            return SCP_CmdResult.Fail(2, $"✗ amount 讀不出來：'{iArgs.Get("amount")}'"
                                         + " —— ⛔ 這不是「沒帶」，是**帶了但解析不出**，不猜");
        if (aAmount <= 0)
            return SCP_CmdResult.Fail(2, $"✗ amount={aAmount} —— 轉帳金額要是正整數"
                                         + "（負數轉帳＝反向轉帳，⛔ 那要顯式換成把兩個帳號對調）");

        var aMissing = new List<string>();
        if (string.IsNullOrWhiteSpace(iArgs.Get("kind"))) aMissing.Add("kind（為什麼動這筆錢）");
        if (string.IsNullOrWhiteSpace(iArgs.Get("ref"))) aMissing.Add("ref（指回現場：commit sha／seq／單號）");
        if (string.IsNullOrWhiteSpace(iArgs.Get("caller"))) aMissing.Add("caller（誰動的）");
        if (aMissing.Count > 0)
            return SCP_CmdResult.Fail(2, "✗ 動錢要署名，缺 " + aMissing.Count + " 欄：",
                                      "  · " + string.Join("\n  · ", aMissing),
                                      "  ⇒ 沒有署名的錢，日後查不出是誰、為什麼、指回哪裡。");

        // ⭐ 兩腳共用的交易識別。呼叫端給 `idem_key` 就用它（⇒ 整筆轉帳可安全重送）；
        //   沒給就現生一個 —— ⚠ 而那時**重送會轉第二次**，所以回傳值把它印出來，
        //   讓「我有沒有給冪等鍵」看得見，不是一個要人記得的細節。
        string aTx = iArgs.Get("idem_key");
        bool aHasIdem = !string.IsNullOrWhiteSpace(aTx);
        if (!aHasIdem) aTx = "tx-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);

        string aDesc = iArgs.Get("description");
        string aDescTx = (aDesc.Length > 0 ? aDesc + "　" : "") + "[tx=" + aTx + "]";

        // ── 第一腳：轉出 ──
        SCP_BankPostResult aOut = SCP_BankLedger.Debit(
            iRoot, aFrom, aAmount, iArgs.Get("kind"), iArgs.Get("ref"),
            aDescTx + " 轉出→" + aTo, iArgs.Get("caller"), iArgs.Get("cmd_id"), aTx + "/out");
        if (!aOut.Ok)
            return SCP_CmdResult.Fail(1, "✗ 轉出腳失敗，**整筆沒有發生**：" + aOut.Why,
                                      $"  ・`{aFrom}` 餘額 = {SCP_BankLedger.GetBalance(iRoot, aFrom)}（未動）");

        // ── 第二腳：收款 ──
        SCP_BankPostResult aIn = SCP_BankLedger.Credit(
            iRoot, aTo, aAmount, iArgs.Get("kind"), iArgs.Get("ref"),
            aDescTx + " 轉入←" + aFrom, iArgs.Get("caller"), iArgs.Get("cmd_id"), aTx + "/in");

        if (!aIn.Ok)
        {
            // ⚠ 回捲**不是靜默的**：它自己是一筆分錄，帳本上看得見「這裡發生過一次失敗的轉帳」。
            //   ⛔ 不去刪掉轉出那一筆 —— 帳本是 append-only，而「沒發生過」與「發生了又撤銷」
            //   是兩件事，抹掉前者會讓事後查帳的人看不到這裡出過事。
            SCP_BankPostResult aBack = SCP_BankLedger.Credit(
                iRoot, aFrom, aAmount, "transfer_rollback", iArgs.Get("ref"),
                aDescTx + " 回捲（收款腳失敗：" + aIn.Why + "）", iArgs.Get("caller"), iArgs.Get("cmd_id"), aTx + "/rollback");

            if (aBack.Ok)
            {
                var aRolled = SCP_CmdResult.Fail(1,
                    "✗ 收款腳失敗，**已回捲**（總量守恆）：" + aIn.Why,
                    $"  ・`{aFrom}` 餘額 = {SCP_BankLedger.GetBalance(iRoot, aFrom)}（扣了又補回）",
                    $"  ・`{aTo}` 餘額 = {SCP_BankLedger.GetBalance(iRoot, aTo)}（沒有收到）",
                    "  ・帳本留下三筆（轉出／回捲），⛔ 刻意不抹掉 —— 「沒發生過」與「發生了又撤銷」是兩件事");
                aRolled.AddValue("tx_id", aTx);
                aRolled.AddValue("rolled_back", "1");
                return aRolled;
            }

            // 🔴 最壞的一格：錢停在半路。**大聲講出來並給處置**，⛔ 不吞掉。
            var aStuck = SCP_CmdResult.Fail(5,
                "🔴 **錢停在半路** —— 轉出成功、收款失敗、而回捲也失敗。",
                "  ・收款失敗：" + aIn.Why,
                "  ・回捲失敗：" + aBack.Why,
                $"  ・`{aFrom}` 餘額 = {SCP_BankLedger.GetBalance(iRoot, aFrom)}（**已經被扣了**）",
                $"  ・`{aTo}` 餘額 = {SCP_BankLedger.GetBalance(iRoot, aTo)}（**沒有收到**）",
                $"  ⇒ 人工處置：`senate cmd bank --arg op=credit --arg account={aFrom} --arg amount={aAmount}"
                + $" --arg kind=transfer_rollback --arg ref=<指回這裡> --arg caller=<你> --arg idem_key={aTx}/rollback`");
            aStuck.AddValue("tx_id", aTx);
            aStuck.AddValue("rolled_back", "0");
            aStuck.AddValue("stuck", "1");
            return aStuck;
        }

        // ⭐ 判準是**回讀兩邊的餘額**，不是上面兩個 Ok。
        int aBalFrom = SCP_BankLedger.GetBalance(iRoot, aFrom);
        int aBalTo = SCP_BankLedger.GetBalance(iRoot, aTo);
        bool aDup = aOut.Duplicate && aIn.Duplicate;

        var aResult = SCP_CmdResult.Success(
            (aDup ? "↻ 冪等判重：兩腳都是既有那一筆，**這次沒有動錢**" : "✓ 已轉帳")
            + $"　{aFrom} → {aTo}　{aAmount}（{iArgs.Get("kind")}）");
        aResult.Lines.Add($"  tx：{aTx}" + (aHasIdem ? "（呼叫端給的冪等鍵 ⇒ 重送安全）"
                                                    : "（**現生的** ⇒ ⚠ 同一道指令重送會再轉一次）"));
        aResult.Lines.Add($"  ・`{aFrom}` 餘額 = {aBalFrom}");
        aResult.Lines.Add($"  ・`{aTo}` 餘額 = {aBalTo}");
        // ⚠ 兩腳的冪等狀態**分開印**：只有一腳判重代表上一次轉到一半，那跟「整筆重送」不同。
        if (aOut.Duplicate != aIn.Duplicate)
            aResult.Lines.Add($"  ⚠ 只有一腳判重（轉出 {(aOut.Duplicate ? "舊" : "新")}／收款 {(aIn.Duplicate ? "舊" : "新")}）"
                              + " ⇒ 上一次這筆轉到一半，這次把它補完了");
        aResult.AddValue("tx_id", aTx);
        aResult.AddValue("from_account", aOut.Entry!.AccountId);
        aResult.AddValue("to_account", aIn.Entry!.AccountId);
        aResult.AddValue("from_balance", aBalFrom.ToString());
        aResult.AddValue("to_balance", aBalTo.ToString());
        aResult.AddValue("duplicate", aDup ? "1" : "0");
        return aResult;
    }
}
