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
//   ② **`bank_root` 是必填參數，本層不推導。** 它是**跨專案共用**的那個根（Tim 2026-09-14）——
//      推導就等於跟著專案漂，而那正是舊 Treasury 一個區一本帳的成因。
//      CLI 那側沒給時會從「路徑管理」頁那一格補上**並印出來**（⛔ 不靜默注入）。
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
                    iChoices: new[] { "accounts", "open", "balance", "credit", "debit" }),
                new SCP_CmdArgSpec("bank_root",
                    "銀行帳本根（絕對路徑）—— **跨專案共用的那一個**。"
                    + "CLI 沒給時會用「路徑管理」頁的 `bankRoot` 那一格補上並印出來", iRequired: true),
                new SCP_CmdArgSpec("account", "帳號 id（大小寫不拘 —— 寫入端一律正規化成小寫）", iDefault: ""),
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
            default: return SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（accounts|open|balance|credit|debit）");
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
}
