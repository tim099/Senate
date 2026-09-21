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
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace Senate.Core;

public sealed class Cmd_Bank : ServerDelegateCmd
{
    public override string Name => "bank";

    // ⚠ 這兩段是**靜態字串**：它們在指令清單裡印出來，那時候還不知道要對哪一棵資料樹說話
    //   ⇒ ⛔ 不可以在這裡宣布誰是權威（同一顆 exe 服務多棵樹，有的切了有的沒切）。
    //   ⇒ 只講「權威這件事去哪裡讀」，真正的答案由每次執行的定語（`Stamp`）現場推導。
    //   🩸 舊版在這裡寫死「遷移前＝測試用」，於是 2026-09-18 切換那天它整句變成假的。
    public override string Summary =>
        "新版銀行：開戶／查餘額／入帳／扣款 —— 由 Senate Server 執行（**單一寫入端**）";

    public override string PortNote =>
        "⚠ **本帳就是那本帳** —— 兩個區都已於 2026-09-18 切換完成（TASK-0216／0241），"
        + "舊 `Treasury/` 凍結為唯讀歷史（Tim 拍板：不刪、轉唯讀）。"
        + "⛔ 沒有「切回去」的旗標了（TASK-0242 ④ 整段移除）—— 退路留在資料上，不在程式碼上";

    public override string Example => SCP_CmdRegistry.Invoke("bank --arg op=balance --arg account=cc");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("op", "做什麼", iDefault: "accounts",
                    iChoices: new[] { "accounts", "open", "balance", "credit", "debit", "pay", "transfer", "close", "reopen",
                                      "requests", "approve", "reject" }),
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
                // ⚠ `open` 也吃這一格（TASK-0250）——「一個宣告過的參數」不等於「每個 op 都看它」，
                //   所以射程寫在說明裡：哪個 op 怎麼解讀它，讀 help 的人一眼看得到。
                new SCP_CmdArgSpec("amount", "金額（正整數；方向由 op 決定）"
                    + "。`open` 時它是**初始金額（種子）** ⇒ >0 要 `confirm=1` ＋ `caller`", iDefault: "0"),
                // 🩸 TASK-0250：種子是憑空增發（`system_init`），⛔ 不是從央行撥 ——
                //   兩者對貨幣總量的影響相反。後台頁那一格用「再按一次」擋，CLI 這一側用這格擋。
                new SCP_CmdArgSpec("confirm",
                    "`1` ＝ 我知道這一次會動錢。目前只有 `open` 帶 `amount>0`（憑空增發種子）要它", iDefault: ""),
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
                // ⚠ 錢包的主人**另開一格**，⛔ 不重用 `persona`（那一格是分道路由，一格裝兩個角色
                //   的話「我填的是誰」要靠 op 才讀得出來，而錯填的代價是花掉別人的券）。
                new SCP_CmdArgSpec("wallet_persona",
                    "錢包（酒館券）的主人 —— `pay` 必填。⛔ 與分道用的 `persona` 是兩回事", iDefault: ""),
                new SCP_CmdArgSpec("letters_root",
                    "券住哪（`pay` 必填；券在 `letters/<persona>/vouchers/`）。⛔ 本層不推導它", iDefault: ""),
                // ── TASK-0261：請款／轉帳審批（把常駐視窗那兩格接到 CLI 上）──────────
                // ⚠ 待審單住 `<data_root>/Treasury/requests` 與 `…/transfer_requests`，
                //   而 `bank_root` 是 `<data_root>/Bank` ⇒ 兩個根**不是同一格**。
                //   ⛔ 不從 `bank_root` 往上推 `data_root`：那是在本層多養一份「路徑住哪」的答案，
                //   而它會跟宿主那份漂（同檔頭判準②）。
                new SCP_CmdArgSpec("data_root",
                    "資料根（絕對路徑）—— `requests`／`approve`／`reject` 必填；待審單在 `<data_root>/Treasury/`。"
                    + "⛔ 本層不從 `bank_root` 推導它", iDefault: ""),
                new SCP_CmdArgSpec("request_id",
                    "要裁決的單號（`approve`／`reject` 必填）—— 請款與轉帳共用這一格；"
                    + "兩種都找不到時**明說兩種都找過**，⛔ 不回一個看起來像「沒有待審」的成功", iDefault: ""),
                new SCP_CmdArgSpec("note", "裁決備註（落進單子的裁決欄）", iDefault: ""),
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
            case "accounts": return Stamp(OpAccounts(aRoot), aRoot);
            case "open": return Stamp(OpOpen(aRoot, iArgs), aRoot);
            case "balance": return Stamp(OpBalance(aRoot, iArgs), aRoot);
            case "credit":
            case "debit": return Stamp(OpPost(aRoot, iArgs, aOp == "debit"), aRoot);
            case "pay": return Stamp(OpPay(aRoot, iArgs), aRoot);
            case "transfer": return Stamp(OpTransfer(aRoot, iArgs), aRoot);
            case "close": return Stamp(OpClose(aRoot, iArgs), aRoot);
            case "reopen": return Stamp(OpReopen(aRoot, iArgs), aRoot);
            // ── TASK-0261：審批三支。⚠ 它們動的是 `<data_root>/Treasury/`（單子）＋ 銀行（錢），
            //    所以 `Stamp` 一樣要蓋 —— 看到數字的人要知道那是哪一本帳。
            case "requests": return Stamp(OpRequests(iArgs), aRoot);
            case "approve": return Stamp(OpDecide(aRoot, iArgs, iApprove: true), aRoot);
            case "reject": return Stamp(OpDecide(aRoot, iArgs, iApprove: false), aRoot);
            default: return SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（accounts|open|balance|credit|debit|pay|transfer|close|reopen|requests|approve|reject）");
        }
    }

    // 區塊職責：D27 的定語 —— 每一次輸出都說一次「這不是那本帳」。
    // 物理意義：新銀行與舊 `Treasury/` **長期並存**，⇒「我有多少錢」有兩個答案，
    //          而**兩邊都不會報錯**（它們各自都對，只是在回答不同的問題）。
    // 🩸 為什麼掛在這裡而不是寫進文件：文件要有人去讀，而這一行長在**每一個看到數字的人**的必經路上。
    //    ⛔ 也不靠「大家記得」—— 記得是這個系統最不能依賴的東西。
    // ⭐ TASK-0242 ④（2026-09-18 晚）：`legacy` 那一支**整段退場**。
    //   它當初留著的理由逐字是「另一棵樹可能還沒切，而它們共用這同一顆 exe」——
    //   而今晚 BTC 那一區也切完了（TASK-0241）⇒ **那個理由失效了，所以它跟著走**。
    // 🩸 為什麼不留著「以防萬一有人切回去」：一個編得過、讀得到、呼叫得到的舊分支，
    //   會讓下一個人寫出「切回 legacy」這種指令，而錢會被寫進一本凍結的帳 —— 沒有任何一層會喊。
    //   ⇒ 退路留在**資料**上（舊帳本原封不動、在 git 裡），⛔ 不留在程式碼上。
    static SCP_CmdResult Stamp(SCP_CmdResult ioResult, string iBankRoot)
    {
        ioResult.Lines.Add("🔁 **本帳＝金流權威**（2026-09-18 切換完成，兩區皆是）"
                           + " —— 舊 `Treasury/` 已凍結為唯讀歷史，⛔ 不再長新分錄");
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

    // ===========================================================
    // 區塊職責：`op=open` —— 開戶，並（帶了種子時）發初始金額。
    // 🩸 TASK-0250：這一支原本**逐字只讀 `account`／`display_name`／`caller`** ——
    //    `amount` 是本 Cmd 宣告過的合法參數（credit／debit／transfer 都吃），
    //    ⇒ ArgSpec 預檢看到的是「一個宣告過的參數」，擋不住「**這個 op 根本不看它**」。
    //    失效樣子：`✓ 已開戶` ＋ exit 0 ＋ 餘額 0 —— 帳開了、錢沒發，沒有任何一層出聲。
    //    ⚠ 那一格的真正教訓是分層的：**參數的合法性是 cmd 層的，它的意義是 op 層的**，
    //      而兩層之間沒有人把關 ⇒ 把關只能寫在這裡（op 自己讀、自己喊）。
    // 📐 修法等級（「讓失敗不可能」＞「當場喊」＞「記得注意」）：
    //    參數名跨 op 共用 ⇒ 做不到「不可能」，所以取第二級 —— **當場喊，且喊在動作發生之前**。
    // ⚠ 種子是**憑空增發**（`system_init`），⛔ 不是從央行撥 ——
    //    兩者對貨幣總量的影響相反，而畫面上都只是「帳戶多了錢」⇒ 所以它要 `confirm=1`。
    //    （後台頁那一格的對應物是「再按一次」：`BankAdminPage.DrawOpenPanel`。）
    // ===========================================================
    static SCP_CmdResult OpOpen(string iRoot, SCP_CmdArgs iArgs)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return SCP_CmdResult.Fail(2, "✗ open 需要 `account`");

        // ⚠ 種子先解析、先擋 —— **全部在 TryOpen 之前**。
        //   擋在開戶之後的話，被擋下的那一次會留下一個「開了但沒錢」的帳戶，
        //   而那正是本單在抱怨的那個半套狀態，只是換成由守衛自己製造。
        string aAmountRaw = iArgs.Get("amount") ?? "";
        int aSeed = 0;
        if (aAmountRaw.Trim().Length > 0)
        {
            if (!int.TryParse(aAmountRaw.Trim(), out aSeed))
                return SCP_CmdResult.Fail(2, $"✗ amount 讀不出來：'{aAmountRaw}'"
                                             + " —— ⛔ 這不是「沒帶」，是**帶了但解析不出**，不猜（⛔ 也不當成 0）");
            if (aSeed < 0)
                return SCP_CmdResult.Fail(2, $"✗ amount 是 {aSeed} —— 開戶的種子**不能是負的**。"
                                             + " 要讓一戶一開始就欠錢，走 `op=debit`，那樣帳本上看得見是誰讓它欠的");
        }

        if (aSeed > 0)
        {
            // 🩸 兩格一起擋、一次把缺的都列出來 —— 分兩次擋的話，補完第一格的人會再撞一次牆。
            var aNeed = new List<string>();
            if (iArgs.Get("confirm") != "1")
                aNeed.Add("confirm=1（種子是**憑空增發** `system_init`，⛔ 不是從央行撥；"
                          + "兩者對貨幣總量的影響相反，而帳戶上都只是「多了錢」）");
            if (string.IsNullOrWhiteSpace(iArgs.Get("caller")))
                aNeed.Add("caller（誰增發的 —— ⛔ 沒簽名的錢日後查不出是誰）");
            if (aNeed.Count > 0)
                return SCP_CmdResult.Fail(2, $"✗ 帶了 `amount={aSeed}` ⇒ 這一次**會動錢**，而還缺 {aNeed.Count} 格：",
                                          "  · " + string.Join("\n  · ", aNeed),
                                          "  ⛔ **帳戶也沒有開** —— 擋下的那一次不留「開了但沒錢」的半套狀態。",
                                          $"  ⇒ 只想開戶不給錢：把 `amount` 拿掉（或給 0）。");
        }

        bool aOk = SCP_BankAccounts.TryOpen(iRoot, aAcct, iArgs.Get("display_name"),
                                            iArgs.Get("caller"), out SCP_BankAccount? aAccount, out string aWhy);
        if (!aOk) return SCP_CmdResult.Fail(1, "✗ 開戶沒有發生：" + aWhy);

        var aResult = SCP_CmdResult.Success($"✓ 已開戶 `{aAccount!.Id}`"
                                            + (aAccount.DisplayName.Length > 0 ? $"（{aAccount.DisplayName}）" : ""));
        aResult.Lines.Add($"  檔案：{SCP_BankAccounts.AccountPath(iRoot, aAccount.Id)}");
        aResult.AddValue("account", aAccount.Id);
        if (aSeed <= 0)
        {
            // ⚠ 沒帶種子時**不加 `seeded` 欄** —— 印 `seeded=0` 的話，
            //   「沒要種子」與「要了但沒發成」在呼叫端同形，而那正是本單的病。
            aResult.AddValue("balance", SCP_BankLedger.GetBalance(iRoot, aAccount.Id).ToString());
            return aResult;
        }

        // 冪等鍵綁帳號 ⇒ 同一戶重送不會發第二次種子。
        SCP_BankPostResult aPost = SCP_BankLedger.Credit(
            iRoot, aAccount.Id, aSeed, "system_init", "cmd_bank_open",
            "開戶種子額度", iArgs.Get("caller"), iArgs.Get("cmd_id"), "seed/" + aAccount.Id);

        if (!aPost.Ok)
        {
            // 🩸 本單的核心：**「戶開了、種子沒發」⛔ 不得回 exit 0。**
            //   回 0 的話它跟「開戶＋發錢都成功」在呼叫端逐字同形 —— 那就是原本那隻病換一張臉。
            aResult.ExitCode = 6;
            aResult.Lines[0] = $"⚠ **戶開了（`{aAccount.Id}`）、種子沒發成功** —— 這兩件事只成了一半";
            aResult.Lines.Add($"  種子失敗原因：{aPost.Why}");
            aResult.Lines.Add($"  ⇒ 戶頭留著（餘額 {SCP_BankLedger.GetBalance(iRoot, aAccount.Id)}）。"
                              + $" 補發走 `op=credit --arg account={aAccount.Id} --arg amount={aSeed}"
                              + $" --arg kind=system_init --arg ref=cmd_bank_open --arg idem_key=seed/{aAccount.Id}`"
                              + "（同一個冪等鍵 ⇒ 補發不會變成發兩次）");
            aResult.AddValue("seeded", "0");
            aResult.AddValue("seed_requested", aSeed.ToString());
            aResult.AddValue("balance", SCP_BankLedger.GetBalance(iRoot, aAccount.Id).ToString());
            return aResult;
        }

        int aBal = SCP_BankLedger.GetBalance(iRoot, aAccount.Id);
        aResult.Lines[0] += $"　＋種子 **{aSeed}**（`system_init` 憑空增發）";
        aResult.Lines.Add($"  種子 entry：{aPost.Entry!.Id}"
                          + (aPost.Duplicate ? "　↻ **冪等判重：這次沒有動錢**（這一戶先前已發過種子）" : ""));
        aResult.AddValue("seeded", aPost.Duplicate ? "0" : aSeed.ToString());
        aResult.AddValue("seed_requested", aSeed.ToString());
        aResult.AddValue("seed_duplicate", aPost.Duplicate ? "1" : "0");
        aResult.AddValue("balance", aBal.ToString());
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

    // ===========================================================
    // 區塊職責：**一筆消費** —— 自動先扣酒館券（個人錢包），不足的部分才扣 token。
    // 物理意義：Tim 2026-09-18 拍板 —— 酒館券是另一本帳，面額與 token 1:1，
    //           主動消費時自動先吃券（10 token 的消費可以是 3 券 ＋ 7 token）。
    //           哪些 `kind` 算主動消費由 `SCP_SpendPolicy` 的**白名單**決定，
    //           ⇒ 名單外（保管費／罰款／系統費用）走純 token。
    // 數值影響：最多動兩本帳 —— 券帳（`letters/<p>/vouchers/tavern.json`）與 token 帳。
    //
    // 🩸 判準：
    //   ① **規則只有這一份。** 放在銀行的付款這一步，而不是每個呼叫端各自判斷 ——
    //      呼叫端各判一次的話，「這裡算消費、那裡不算」會長出第二套政策而沒有人比對過。
    //   ② **先檢查兩邊夠不夠，不夠整筆不做**（Tim 拍板）—— ⛔ 不部分扣款。
    //   ③ ⚠ **兩次寫入之間沒有補償**（Tim 明確不要補償邏輯）：
    //      先扣券、再扣 token。token 那步失敗時**券已經扣掉了** ——
    //      那是已知殘留，⇒ 本層把確切數字印在錯誤訊息裡，讓它能被人工還原，
    //      ⛔ 不假裝整筆沒發生。
    // ===========================================================
    static SCP_CmdResult OpPay(string iRoot, SCP_CmdArgs iArgs)
    {
        string aAcct = iArgs.Get("account");
        if (string.IsNullOrWhiteSpace(aAcct)) return SCP_CmdResult.Fail(2, "✗ 需要 `account`");
        string aWallet = iArgs.Get("wallet_persona").Trim();
        if (aWallet.Length == 0)
            return SCP_CmdResult.Fail(2, "✗ `pay` 需要 `wallet_persona`（錢包的主人）"
                                       + " —— ⛔ 不從 account 反查（反查錯就是花掉別人的券）");
        string aLetters = iArgs.Get("letters_root").Trim();
        if (aLetters.Length == 0)
            return SCP_CmdResult.Fail(2, "✗ `pay` 需要 `letters_root`（券住哪）—— ⛔ 本層不推導它");
        if (!int.TryParse(iArgs.Get("amount"), out int aAmount))
            return SCP_CmdResult.Fail(2, $"✗ amount 讀不出來：'{iArgs.Get("amount")}'");
        string aKind = iArgs.Get("kind");
        if (string.IsNullOrWhiteSpace(aKind))
            return SCP_CmdResult.Fail(2, "✗ `pay` 需要 `kind` —— 它同時決定署名**與這筆算不算主動消費**");

        bool aActive = SCP_SpendPolicy.IsActiveSpend(aKind);

        // 券餘額：⛔ 讀不了**不是**「零張」（後者是讀數，前者是「我不知道」）。
        var aRoot = new SCP_LettersRoot(aLetters);
        SCP_VoucherBook aBook = SCP_VoucherStore.Load(aRoot, aWallet, SCP_SpendPolicy.TavernVoucherId,
                                                      out string? aVoucherProblem);
        if (aVoucherProblem != null)
            return SCP_CmdResult.Fail(1, "✗ 錢包讀不了（" + aVoucherProblem + "）⇒ **這筆沒有付**"
                                       + "　⛔ 讀不到券不等於沒有券");
        DateTime aNow = DateTime.UtcNow;
        int aWalletBalance = aBook.Spendable(aNow);
        int aTokenBalance = SCP_BankLedger.GetBalance(iRoot, aAcct);

        if (!SCP_SpendPolicy.TryPlan(aAmount, aWalletBalance, aTokenBalance, aActive,
                                     out SCP_SpendPolicy.Plan aPlan, out string aWhy))
            return SCP_CmdResult.Fail(1, "✗ " + aWhy);

        // ── ① 先扣券 ──
        int aVoucherBefore = aWalletBalance;
        if (aPlan.Voucher > 0)
        {
            if (!SCP_VoucherStore.TryConsume(aBook, aPlan.Voucher, aNow, out string? aConsumeWhy))
                return SCP_CmdResult.Fail(1, "✗ 扣券失敗（" + aConsumeWhy + "）⇒ **這筆沒有付**");
            string aRegion = SCP_BankRegion.Read(SCP_BankRegion.DataRootOfBankRoot(iRoot), out string? _);
            if (!SCP_VoucherStore.Save(aRoot, aBook, aNow, aRegion, out int _, out string? aSaveErr))
                return SCP_CmdResult.Fail(1, "✗ 券扣不下去（" + aSaveErr + "）⇒ **這筆沒有付**");
        }

        // ── ② 再扣 token ──
        if (aPlan.Token > 0)
        {
            SCP_BankPostResult aPost = SCP_BankLedger.Debit(iRoot, aAcct, aPlan.Token, aKind,
                iArgs.Get("ref"), iArgs.Get("description"), iArgs.Get("caller"), iArgs.Get("cmd_id"),
                iArgs.Get("idem_key"));
            if (!aPost.Ok)
                // 🩸 判準③ 的那一格：券已經扣了而 token 沒扣成 ⇒ 把數字講清楚，讓它能被還原。
                return SCP_CmdResult.Fail(1,
                    "✗ token 扣款失敗（" + aPost.Why + "）",
                    aPlan.Voucher > 0
                        ? $"  ⚠ **而酒館券已經扣掉 {aPlan.Voucher} 張了**（`{aWallet}` {aVoucherBefore} → {aBook.Spendable(aNow)}）"
                          + "　⇒ 這是已知的半付殘留，補回去要手動：`voucher op=grant`"
                        : "  · 券沒有動（這筆不吃券）");
        }

        int aBalAfter = SCP_BankLedger.GetBalance(iRoot, aAcct);
        var aResult = SCP_CmdResult.Success(
            $"✓ 已付 {aAmount}　`{aAcct}`（{aKind}）"
            + (aPlan.Voucher > 0 ? $"　＝ 酒館券 {aPlan.Voucher} ＋ token {aPlan.Token}" : "　（純 token）"));
        if (!aActive)
            aResult.Lines.Add($"  · `{aKind}` **不在主動消費名單上** ⇒ 走純 token（保管費／罰款那一族）");
        aResult.Lines.Add($"  · 錢包 {aVoucherBefore} → {aBook.Spendable(aNow)}　餘額 = {aBalAfter}");
        aResult.AddValue("account", aAcct);
        aResult.AddValue("paid_voucher", aPlan.Voucher.ToString());
        aResult.AddValue("paid_token", aPlan.Token.ToString());
        aResult.AddValue("active_spend", aActive ? "1" : "0");
        aResult.AddValue("wallet_after", aBook.Spendable(aNow).ToString());
        aResult.AddValue("balance", aBalAfter.ToString());
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

    // ===========================================================
    // 區塊職責：📨 **請款／轉帳審批**的 CLI 出口（TASK-0261）。
    // 物理意義：把常駐視窗 `BankAdminPage` 那兩格接到 `senate cmd bank` 上 ——
    //          ⛔ **不另寫一份撥款邏輯**：核准走的是本檔既有的 `OpTransfer`，
    //          跟視窗那條路逐字同一個出口（視窗是 `Dispatch("transfer", …)`）。
    //          🩸 為什麼這一格不能複製：第二個寫入端正是整條銀行線在根治的病 ——
    //          兩份撥款邏輯會在其中一份改過之後開始分岔，**而它們各自都不會報錯**。
    // 數值影響：`approve` 會**真的動錢**（央行→目標戶／A→B）並寫單子的裁決欄；
    //          `reject` 只寫裁決欄，⛔ 一毛錢不動；`requests` 唯讀。
    //
    // ⭐ 順序判準（沿用視窗那條，⛔ 不反過來）：**先動錢，成功了才寫裁決欄**。
    //   反過來的話，中途失敗留下的是「單子寫著 approved、而錢沒撥」——
    //   而那份單子之後**不會再出現在待審清單裡** ⇒ 沒有人會發現錢沒到。
    //   ⇒ 失敗時單子保持 `pending`，它會再被列出來，那正是我們要的。
    // ===========================================================

    /// <summary>待審清單（唯讀）。⚠ 印單號與金額，⛔ 不只印「有幾張」—— 數字答不出「是哪一張」。</summary>
    static SCP_CmdResult OpRequests(SCP_CmdArgs iArgs)
    {
        string aData = iArgs.Get("data_root");
        if (string.IsNullOrWhiteSpace(aData))
            return SCP_CmdResult.Fail(2, "✗ 缺 `data_root` —— 待審單在 `<data_root>/Treasury/`，本層不推導它");

        var aProblems = new List<string>();
        List<SCP_PayoutRequest> aPayouts = SCP_TreasuryRequests.LoadPendingPayouts(aData, aProblems);
        List<SCP_TransferRequest> aTransfers = SCP_TreasuryRequests.LoadPendingTransfers(aData, aProblems);
        string aCentral = SCP_TreasuryRequests.ReadCentralBank(aData);

        var aR = SCP_CmdResult.Success(
            $"# 待審：請款 **{aPayouts.Count}** 張／轉帳 **{aTransfers.Count}** 張");
        aR.Lines.Add($"・請款＝**央行撥款**（從 `{aCentral}` 出，公庫變少）；轉帳＝**A→B**（總量守恆）");

        aR.Lines.Add("");
        aR.Lines.Add("## 📨 請款");
        if (aPayouts.Count == 0) aR.Lines.Add("・（沒有待審請款單）");
        foreach (SCP_PayoutRequest r in aPayouts)
            aR.Lines.Add($"・`{r.RequestId}`　**{r.Amount}** {r.Currency} → **{r.TargetBank}**"
                         + $"　請款人 {r.RequesterPersona}　{r.RequestedAt}"
                         + (r.Reason.Length > 0 ? "　理由：" + r.Reason : ""));

        aR.Lines.Add("");
        aR.Lines.Add("## 💸 轉帳");
        if (aTransfers.Count == 0) aR.Lines.Add("・（沒有待審轉帳單）");
        foreach (SCP_TransferRequest r in aTransfers)
            aR.Lines.Add($"・`{r.RequestId}`　**{r.Amount}** {r.Currency}　**{r.FromBank}** → **{r.ToBank}**"
                         + $"　請求人 {r.RequesterPersona}　{r.RequestedAt}"
                         + (r.Reason.Length > 0 ? "　理由：" + r.Reason : ""));

        // ⚠ 讀不了的單**要出聲**：一張壞掉的單被靜默跳過，跟「它已經被處理掉了」在清單上同形。
        if (aProblems.Count > 0)
        {
            aR.Lines.Add("");
            aR.Lines.Add($"⚠ **有 {aProblems.Count} 張單讀不了**（⛔ 不當成「沒有這張單」）：");
            foreach (string aWhy in aProblems) aR.Lines.Add("  · " + aWhy);
        }
        aR.AddValue("payouts", aPayouts.Count.ToString());
        aR.AddValue("transfers", aTransfers.Count.ToString());
        aR.AddValue("problems", aProblems.Count.ToString());
        aR.AddValue("central_bank", aCentral);
        return aR;
    }

    /// <summary>核准／駁回一張單。<paramref name="iApprove"/>＝true 時**會真的動錢**。</summary>
    SCP_CmdResult OpDecide(string iRoot, SCP_CmdArgs iArgs, bool iApprove)
    {
        string aData = iArgs.Get("data_root");
        string aId = iArgs.Get("request_id").Trim();
        var aMissing = new List<string>();
        if (string.IsNullOrWhiteSpace(aData)) aMissing.Add("data_root（待審單在 `<data_root>/Treasury/`）");
        if (aId.Length == 0) aMissing.Add("request_id（要裁決哪一張）");
        if (aMissing.Count > 0)
            return SCP_CmdResult.Fail(2, "✗ 缺 " + aMissing.Count + " 欄：", "  · " + string.Join("\n  · ", aMissing));

        var aProblems = new List<string>();
        List<SCP_PayoutRequest> aPayouts = SCP_TreasuryRequests.LoadPendingPayouts(aData, aProblems);
        List<SCP_TransferRequest> aTransfers = SCP_TreasuryRequests.LoadPendingTransfers(aData, aProblems);

        SCP_PayoutRequest? aPay = null;
        foreach (SCP_PayoutRequest r in aPayouts)
            if (string.Equals(r.RequestId, aId, StringComparison.OrdinalIgnoreCase)) { aPay = r; break; }
        SCP_TransferRequest? aTr = null;
        if (aPay == null)
            foreach (SCP_TransferRequest r in aTransfers)
                if (string.Equals(r.RequestId, aId, StringComparison.OrdinalIgnoreCase)) { aTr = r; break; }

        // ⚠ 找不到要**說清楚兩種都找過了** —— 「請款裡沒有」與「這張單不存在」是兩件事，
        //   而把前者印成後者的話，下一個人會去建一張已經存在的單。
        if (aPay == null && aTr == null)
        {
            var aFail = SCP_CmdResult.Fail(1,
                $"✗ 待審清單裡找不到 `{aId}` —— **請款（{aPayouts.Count} 張）與轉帳（{aTransfers.Count} 張）兩邊都找過了**");
            aFail.Lines.Add("  ⇒ 它可能已經被裁決過（裁決過的單不在待審清單裡），或是單號打錯");
            if (aProblems.Count > 0)
                aFail.Lines.Add($"  ⚠ 另有 **{aProblems.Count}** 張單讀不了 ⇒ ⛔ 「找不到」這個結論的射程不含它們");
            return aFail;
        }

        bool aIsPayout = aPay != null;
        string aCentral = SCP_TreasuryRequests.ReadCentralBank(aData);
        string aFrom = aIsPayout ? aCentral : aTr!.FromBank;
        string aTo = aIsPayout ? aPay!.TargetBank : aTr!.ToBank;
        int aAmount = aIsPayout ? aPay!.Amount : aTr!.Amount;
        string aPath = aIsPayout ? aPay!.Path : aTr!.Path;
        string aWhat = aIsPayout ? "請款（央行撥款）" : "轉帳（A→B）";

        // ⭐ 反向對照那一格：不帶 `confirm=1` ⇒ **一毛錢沒動、單子狀態不變**，只印會發生什麼。
        //   ⚠ 駁回也要 confirm —— 它不動錢，但它**改變單子的狀態且不可逆**
        //     （裁決欄寫下去，那張單就不在待審清單裡了）。
        if (iArgs.Get("confirm") != "1")
        {
            var aDry = SCP_CmdResult.Success("・**乾跑**（沒帶 `confirm=1`）⇒ 一毛錢沒動、單子狀態沒變");
            aDry.Lines.Add($"  · 這一張：`{aId}`　{aWhat}　**{aAmount}**　**{aFrom}** → **{aTo}**");
            aDry.Lines.Add(iApprove
                ? "  · 帶 `confirm=1` 會：**先動錢**（走本檔 `transfer` 那條路，冪等鍵綁單號），成功了才寫裁決欄 `approved`"
                : "  · 帶 `confirm=1` 會：只寫裁決欄 `rejected`，⛔ 一毛錢不動");
            aDry.AddValue("dry_run", "1");
            aDry.AddValue("request_kind", aIsPayout ? "payout" : "transfer");
            aDry.AddValue("amount", aAmount.ToString());
            return aDry;
        }

        string aActor = "cmd_bank/" + (iArgs.Get("caller").Length > 0 ? iArgs.Get("caller") : "cli");
        string aNote = iArgs.Get("note");

        // ── 駁回：不動錢 ──────────────────────────────────────
        if (!iApprove)
        {
            if (!SCP_TreasuryRequests.Decide(aPath, "rejected", aActor, aNote, null, out string aRejErr))
                return SCP_CmdResult.Fail(1, "✗ 裁決欄寫不進去：" + aRejErr, "  ⇒ 單子**保持 pending**");
            var aRej = SCP_CmdResult.Success($"✅ 已駁回 `{aId}`（{aWhat} **{aAmount}**）—— ⛔ 一毛錢沒動");
            aRej.AddValue("decided", "rejected");
            aRej.AddValue("moved_money", "0");
            return aRej;
        }

        // ── 核准：先動錢，成功了才寫裁決欄 ──────────────────────
        // ⭐ 冪等鍵綁單號 ⇒ 同一張單重送不會撥第二次（與視窗那條路同一把鍵）。
        var aRaw = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["op"] = "transfer",
            ["bank_root"] = iRoot,
            ["account"] = aFrom,
            ["to_account"] = aTo,
            ["amount"] = aAmount.ToString(),
            ["kind"] = aIsPayout ? "payout_request"
                                 : (aTr!.Kind.Length > 0 ? aTr!.Kind : "transfer_request"),
            ["ref"] = aId,
            ["description"] = aIsPayout ? aPay!.Reason : aTr!.Reason,
            ["caller"] = aActor,
            ["cmd_id"] = iArgs.Get("cmd_id"),
            ["idem_key"] = (aIsPayout ? "payout/" : "transfer/") + aId,
        };
        (SCP_CmdArgs? aXferArgs, List<string> aBindErrors) = SCP_CmdArgs.Bind(ArgSpecs, aRaw);
        if (aXferArgs == null)
            return SCP_CmdResult.Fail(1, "✗ 內部組參數失敗（本 Cmd 的規格與實作不同步）：",
                                      "  · " + string.Join("\n  · ", aBindErrors));

        SCP_CmdResult aXfer = OpTransfer(iRoot, aXferArgs);
        if (!aXfer.Ok)
        {
            var aFail = SCP_CmdResult.Fail(aXfer.ExitCode,
                $"❌ {aWhat}失敗 ⇒ **整筆沒有發生**，單子**保持 pending**（裁決欄沒寫）");
            aFail.Lines.AddRange(aXfer.Lines);
            aFail.AddValue("decided", "");
            aFail.AddValue("moved_money", "0");
            return aFail;
        }

        bool aOk = SCP_TreasuryRequests.Decide(aPath, "approved", aActor, aNote, null, out string aErr);
        var aRes = SCP_CmdResult.Success(aOk
            ? $"✅ 已{aWhat}並結單 `{aId}`：**{aAmount}**　**{aFrom}** → **{aTo}**"
            // 🩸 這一行是三本帳分開結算：錢動了（處置成立）而單子沒結（結果沒成立）——
            //    ⛔ 不可以印成一個乾淨的 ✅，否則下一個人會以為兩件事都完成了。
            : $"⚠ **錢已經動了**（{aAmount}　{aFrom} → {aTo}），而裁決欄沒寫成功：{aErr}"
              + "　⇒ 單子仍是 pending，**重送會被冪等鍵擋住、不會撥第二次**，但要有人把裁決欄補上");
        aRes.Lines.AddRange(aXfer.Lines);
        foreach (KeyValuePair<string, string> kv in aXfer.Values) aRes.AddValue(kv.Key, kv.Value);
        aRes.AddValue("decided", aOk ? "approved" : "");
        aRes.AddValue("moved_money", "1");
        aRes.AddValue("request_kind", aIsPayout ? "payout" : "transfer");
        return aRes;
    }
}
