// 區塊職責：**新銀行後台頁** —— 查帳戶／查餘額／開戶／入帳／扣款，以及「把舊 Treasury 餘額搬進來」那一步。
// 物理意義：TASK-0223。形狀參考 Unity 那側的 `UCL_BankAdminPage`，但**只做基礎功能**
//           （⛔ 孤兒歸戶／跨 bank 轉帳／繪圖券／酒館券都不在這裡，Tim 2026-09-16 拍板）。
//           帳號來源是**跟舊系統共用的那一份綁定** `letters/<persona>/bank/<region>.md`，
//           ⛔ 本頁不另建綁定表 —— 第二份綁定表可以跟第一份說不一樣的話，而兩邊都不報錯。
// 數值影響：**讀**走 `cmd bank op=accounts`；**寫**走 `cmd bank op=open/credit/debit`。
//           ⛔ 本頁不直接呼叫 `SCP_BankLedger` —— 那支的 debit 鎖是 in-process 的，
//           只有「寫入端只有一顆 process」時才擋得住；後台自己寫就等於**安靜地**多一個寫入端。
//           遷移那一步的餘額走 Unity 端 `Cmd_Treasury op=balances`（＝舊帳本的 canonical replayer），
//           ⛔ 本頁不重放舊 ledger、也不 parse `accounts/_balances.snapshot.txt`（Tim 2026-08-20 硬規則）。
//
// 🩸 三條從 `SCP_GuiSessionAdminPage` 原樣搬過來的界線（不是新發明的）：
//   ① **委派不得同步阻塞畫面迴圈**：會重畫的宿主走背景 task ＋「⏳ 執行中」態；
//      不會重畫的（CLI 單次 render）才同步跑 —— 那裡沒有第二幀可以印結果。
//   ② **二段確認**：動錢與遷移都要按兩次。誤點的後果是別人帳上多／少一筆。
//   ③ **判準是回讀，不是回傳字串**：每一個寫動作之後都重抓一次帳戶表再畫。
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SCP.Core.Bank;
using SCP.Core.Cmd;
using SCP.Core.Gui;
using SCP.Core.Letters;
using SCP.Core.Paths;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class BankAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "bank";

    /// <summary>待確認的動作（session 欄位；值是動作描述，空 ＝ 沒有待確認）。</summary>
    public const string PendingId = "bank/pending";

    /// <summary>目前選的區（session 欄位 —— 換頁回來要記得剛才在看哪一區）。</summary>
    public const string RegionId = "bank/region";

    /// <summary>上方選單：選到的 persona／帳戶（session 欄位）。</summary>
    public const string PersonaId = "bank/sel/persona";
    public const string AccountId = "bank/sel/account";

    readonly SenateModel m_Model;

    SCP_PathResolution m_BankRoot = new SCP_PathResolution("", "?", "還沒讀");
    SCP_PathResolution m_Letters = new SCP_PathResolution("", "?", "還沒讀");
    SCP_PathResolution m_DataRoot = new SCP_PathResolution("", "?", "還沒讀");

    /// <summary>這台機器上看得到的區（從綁定檔的檔名長出來，⛔ 不寫死 Florin／BTC）。</summary>
    List<string> m_Regions = new List<string>();

    /// <summary>目前這一區的逐人讀數。</summary>
    List<Row> m_Rows = new List<Row>();

    /// <summary>新銀行帳戶表：id → （有沒有銷戶, 餘額）。來自 `cmd bank op=accounts` 的 `acc/<id>` 欄位。</summary>
    Dictionary<string, Acct> m_Accounts = new Dictionary<string, Acct>(StringComparer.Ordinal);

    List<string> m_Problems = new List<string>();
    string? m_Message;
    bool m_Loaded;

    /// <summary>遷移對照表（null ＝ 還沒取過讀數）。</summary>
    List<MigRow>? m_Mig;
    string m_MigStamp = "";

    Task<string>? m_Job;
    string m_JobLabel = "";

    public BankAdminPage(SenateModel iModel) : base() { m_Model = iModel; }

    // ===========================================================
    // 區塊職責：**釘在最上面的那兩格選單**（Tim 2026-09-17：「Persona 選單也是 override TopBarButtons
    //          後放在最上方」）—— 對應 Unity `UCL_BankAdminPage.TopBarButtons` 的 persona 下拉。
    // 物理意義：工具列住在 `TopBar()` 裡 ⇒ **不跟內容一起捲**。
    //          🩸 這一頁很長（帳戶表 22 列＋遷移對照表 11 列）⇒ 沒釘住的話，
    //            「我現在在對誰動錢」這個讀數會在你捲下去按鈕的那一刻離開畫面。
    //            而動錢的那兩顆鈕就在下面。
    // ⚠ 下拉展開時會把工具列撐高（選項是 inline 畫的）—— 那是刻意的：
    //   展開＝正在挑人，這時本來就該讓它佔版面；收合之後只剩兩顆鈕。
    // ===========================================================
    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (!m_Loaded) Reload(iUi);

        string aSelP = SelectedPersona(iUi);
        var aPersonaOpts = new List<SCP_GuiOption>(m_Rows.Count);
        foreach (Row r in m_Rows)
            aPersonaOpts.Add(new SCP_GuiOption(r.Persona,
                r.Persona + " → " + r.AccountId + (r.Borrowed ? "（借用 " + r.BindingRegion + "）" : "")));
        string aPickP = iUi.Dropdown("Persona", aPersonaOpts, aSelP, "bank/sel/p");
        if (aPickP != aSelP && aPickP.Length > 0)
        {
            iUi.SetField(PersonaId, aPickP);
            // 選 persona → 帳戶跟著同步（同 Unity 那頁的手勢）。⚠ 二段確認要清掉：
            //   換了人還留著上一個人的「待確認」，下一次按下去動的是**新選到的那一戶**。
            iUi.SetField(AccountId, AccountOfPersona(aPickP));
            iUi.SetField(PendingId, "");
            m_Message = $"・已選 `{aPickP}` ⇒ 帳戶同步到 `{AccountOfPersona(aPickP)}`";
        }

        DrawAgentPicker(iUi, SelectedAccount(iUi));

        string aNow = SelectedAccount(iUi);
        iUi.Label(aNow.Length == 0 ? "｜（未選帳戶）" : $"｜**{aNow}**　{BalanceText(aNow)}");
    }

    public override string Key { get { return PageKey; } }
    public override string Title { get { return "銀行後台（新銀行）"; } }
    public override string? MenuGroup { get { return "管理"; } }

    // ── 讀 ────────────────────────────────────────────────────

    public override void OnPush()
    {
        base.OnPush();
        m_BankRoot = m_Model.BankRoot;
        m_Letters = m_Model.LettersRoot;
        m_DataRoot = m_Model.AgentCommandsRoot;
        m_Regions = ScanRegions(m_Letters.Value);
        // ⚠ 區域名讀**舊系統那一格**（`Treasury/bank_settings.json` 的 `currency_id`）——
        //   新銀行不另立設定：兩份設定會對「這裡是哪一區」給出不同答案，而兩邊都是合法字串。
        m_Region = m_DataRoot.Value.Length > 0
            ? SCP_BankRegion.Read(m_DataRoot.Value, out m_RegionWhy)
            : SCP_BankRegion.DefaultRegion;
        m_Loaded = false;
    }

    /// <summary>
    /// 這台機器上有哪些區 —— 掃 `letters/&lt;persona&gt;/bank/*.md` 的**檔名**。
    /// <para>⚠ 刻意不寫死區名：寫死的那一版在別的專案上會漏掉整個區，
    /// 而症狀是「那一區的人一個都沒有」，跟「那一區真的沒人」同形。</para>
    /// </summary>
    static List<string> ScanRegions(string iLettersRoot)
    {
        var aSet = new SortedSet<string>(StringComparer.Ordinal);
        if (iLettersRoot.Length == 0 || !Directory.Exists(iLettersRoot)) return new List<string>(aSet);
        foreach (string aPersonaDir in Directory.GetDirectories(iLettersRoot))
        {
            string aBankDir = Path.Combine(aPersonaDir, "bank");
            if (!Directory.Exists(aBankDir)) continue;
            foreach (string f in Directory.GetFiles(aBankDir, "*.md"))
            {
                string aName = Path.GetFileNameWithoutExtension(f);
                if (aName.Length > 0) aSet.Add(aName);
            }
        }
        return new List<string>(aSet);
    }

    // ⛔ `CurrentRegion(SCP_Ui)` 已退場（Tim 2026-09-17「新銀行不用多區」）：
    //   區域現在是**設定檔那一格**，不是「畫面上按了哪顆鈕」。
    //   🩸 舊版的症狀：漏按一個區，跟「那一區沒有人」在畫面上完全同形。

    // ===========================================================
    // 區塊職責：上方那兩格選單的**選取值**（形狀取自 Unity 的 `UCL_BankAdminPage`：persona 下拉 ＋ 帳戶下拉）。
    // 物理意義：選 persona ⇒ 帳戶跟著同步到他在本區 resolve 到的那一個；⇒ 底下每個動作都吃這兩格。
    // ⚠ 兩格都有**明確的空值**：沒有選到時回空字串，而動作那側會擋下來並說「先選一個」——
    //   ⛔ 不要在沒選的時候偷偷用第一列：那會讓「我沒選」與「我選了第一個」同形，而它們動的是不同人的錢。
    // ===========================================================
    string SelectedPersona(SCP_Ui g)
    {
        string aPick = g.FieldValue(PersonaId, "");
        foreach (Row r in m_Rows) if (r.Persona == aPick) return aPick;
        return "";
    }

    /// <summary>選到的帳戶。⚠ persona 沒選時仍可能有值（可以只選帳戶，例如央行）。</summary>
    string SelectedAccount(SCP_Ui g)
    {
        string aPick = g.FieldValue(AccountId, "").Trim();
        return aPick;
    }

    /// <summary>某個 persona 在本區 resolve 到的帳號（⛔ 讀綁定，不是猜）。</summary>
    string AccountOfPersona(string iPersona)
    {
        foreach (Row r in m_Rows) if (r.Persona == iPersona) return r.AccountId;
        return "";
    }

    /// <summary>一個人在這一區的讀數。</summary>
    sealed class Row
    {
        public string Persona = "";
        public string AccountId = "";
        /// <summary>綁定檔實際來自哪一區 —— 與所選區不同 ＝ 借用別區。</summary>
        public string BindingRegion = "";
        public bool Borrowed;
    }

    sealed class Acct
    {
        public bool Closed;
        public int Balance;
    }

    sealed class MigRow
    {
        public string AccountId = "";
        public int OldBalance;
        public int NewBalance;
        public bool HasAccount;
        /// <summary>不搬的理由（空 ＝ 要搬）。</summary>
        public string Skip = "";
    }

    void Reload(SCP_Ui g)
    {
        m_Problems = new List<string>();
        m_Rows = new List<Row>();
        m_Accounts = new Dictionary<string, Acct>(StringComparer.Ordinal);
        m_Loaded = true;

        string aRegion = m_Region;
        if (m_Letters.Value.Length > 0 && aRegion.Length > 0)
        {
            foreach (string aName in SCP_PersonaProfile.PoolNames(m_Letters.Value, m => m_Problems.Add(m)))
            {
                string aAcc = SCP_PersonaProfile.GetBankAccount(m_Letters.Value, aName, aRegion,
                                                                out string aSrc, out string _);
                if (aAcc.Length == 0) continue;   // 這一區沒綁 ⇒ 不是「他沒有帳號」，只是不在這一區
                m_Rows.Add(new Row
                {
                    Persona = aName,
                    AccountId = aAcc,
                    BindingRegion = aSrc,
                    Borrowed = !string.Equals(aSrc, aRegion, StringComparison.Ordinal),
                });
            }
        }
        LoadAccounts();
    }

    /// <summary>抓新銀行的帳戶表（走 `cmd bank op=accounts` 的機器可讀欄位）。</summary>
    void LoadAccounts()
    {
        if (m_BankRoot.Value.Length == 0) { m_Problems.Add("沒有 bankRoot ⇒ 帳戶表量不到"); return; }
        SCP_CmdResult aRes = Dispatch("accounts", new Dictionary<string, string>());
        // ⚠ exit 5 ＝ 有孤兒帳號（帳本有、帳戶檔沒有）。那**不是**讀取失敗，資料照樣是好的
        //   ⇒ 照收，另外印一行警告。把它當失敗的話，畫面會在資料可用時說「量不到」。
        if (aRes.ExitCode != 0 && aRes.ExitCode != 5)
        {
            m_Problems.Add("`cmd bank op=accounts` 回 exit " + aRes.ExitCode + "："
                           + (aRes.Lines.Count > 0 ? aRes.Lines[0] : "（沒有訊息）"));
            return;
        }
        foreach (KeyValuePair<string, string> kv in aRes.Values)
        {
            if (!kv.Key.StartsWith("acc/", StringComparison.Ordinal)) continue;
            string aId = kv.Key.Substring(4);
            string aVal = kv.Value;
            bool aClosed = aVal.StartsWith("closed:", StringComparison.Ordinal);
            int aCut = aVal.IndexOf(':');
            int.TryParse(aCut >= 0 ? aVal.Substring(aCut + 1) : aVal, out int aBal);
            m_Accounts[aId] = new Acct { Closed = aClosed, Balance = aBal };
        }
        foreach (KeyValuePair<string, string> kv in aRes.Values)
            if (kv.Key == "orphan_count" && kv.Value != "0")
                m_Problems.Add("帳本裡有 " + kv.Value + " 個帳號沒有帳戶檔（新系統不該出現）"
                               + " —— 本頁只把它指出來，⛔ 歸戶不在這一頁做");
    }

    SCP_CmdResult Dispatch(string iOp, Dictionary<string, string> iArgs)
    {
        iArgs["op"] = iOp;
        iArgs["bank_root"] = m_BankRoot.Value;
        return SCP_CmdRegistry.Dispatch("bank", iArgs);
    }

    // ── 畫 ────────────────────────────────────────────────────

    protected override void DrawContent(SCP_Ui g)
    {
        g.Note("新銀行（`SCP_Bank*`）的後台。**帳號來源＝跟舊系統共用的綁定** "
               + "`letters/<persona>/bank/<區>.md`：同一個人**每區是不同帳號 id**。");
        g.Note("⚠ **遷移前新銀行＝測試用**，「我有多少錢」的答案仍然是舊 `Treasury/`（D27）。"
               + "⛔ 本頁不切換權威。");

        DrawPathRow(g);
        if (m_BankRoot.Value.Length == 0)
        {
            g.Note("⚠ 本頁**沒有資料來源** ⇒ 這不是「銀行是空的」，是「量不到」。原因："
                   + (string.IsNullOrEmpty(m_BankRoot.Error) ? "解出來是空字串" : m_BankRoot.Error));
            // ⚠ 2026-09-17 起銀行根是**推導**的（`<資料根>/Bank`）⇒ 這裡解不出來，壞的是**資料根**。
            //   ⛔ 別再叫人去設 bankRoot —— 那一格已經不存在，照著做會找不到東西可按。
            g.Note("· 銀行根 ＝ `<AgentCommands 資料根>/Bank`（推導，不可設定）"
                   + " ⇒ 解不出來就是**資料根**那一格壞了，去「路徑管理」頁（`senate ui --page paths`）看它。");
            return;
        }

        PumpJob();                      // 先收委派結果再畫，不然畫面比磁碟晚一幀
        if (!m_Loaded) Reload(g);

        DrawRegionRow(g);
        g.Separator();
        DrawAccountTable(g);
        g.Separator();
        DrawPostPanel(g);
        g.Separator();
        DrawMigrationPanel(g);

        for (int i = 0; i < m_Problems.Count; ++i) g.Note("⚠ " + m_Problems[i]);
        if (m_Job != null) g.Note("⏳ 執行中：" + m_JobLabel + "（完成前不受理第二筆）");
        if (m_Message != null) g.Note(m_Message);
    }

    void DrawPathRow(SCP_Ui g)
    {
        using (g.Row())
        {
            g.Label("銀行根：" + (m_BankRoot.Value.Length > 0 ? m_BankRoot.Value : "（沒有）"));
            g.Label("來源=" + m_BankRoot.Origin);
        }
        using (g.Row())
        {
            g.Label("信件庫：" + (m_Letters.Value.Length > 0 ? m_Letters.Value : "（沒有）"));
            if (g.Button("重讀", "bank/reload")) { m_Loaded = false; m_Message = null; }
        }
    }

    // ===========================================================
    // 區塊職責：區域那一列 —— **本棵資料樹只有一個區**（Tim 2026-09-17：「新銀行不用多區，參考原本的」）。
    // 物理意義：值住在**舊系統那一格**（`Treasury/bank_settings.json` 的 `currency_id`），⛔ 新銀行不另立設定。
    // 🩸 為什麼拿掉選擇器：它讓「我在看哪一區」變成一個**畫面狀態**，而錢的歸屬不該由當下按了哪顆鈕決定 ——
    //   遷移那天就是靠人記得逐區各按一次，而漏按一區跟「那一區沒有人」在畫面上同形。
    // ⚠ 改這個名字＝把全體 persona 的綁定檔重新定鍵（`letters/<persona>/bank/<區>.md`）
    //   ⇒ 二段確認，而且**本頁不代為改名任何綁定檔**（那是另一件事，不該藏在一個輸入框後面）。
    // ===========================================================
    void DrawRegionRow(SCP_Ui g)
    {
        using (g.Row())
        {
            g.Label("區域（＝幣別軸）：**" + m_Region + "**");
            g.Label("來源=" + (m_RegionWhy == null ? "`Treasury/bank_settings.json` 的 `currency_id`"
                                                   : "⚠ 預設（" + m_RegionWhy + "）"));
        }
        if (m_Regions.Count > 1)
        {
            var aOther = new List<string>();
            foreach (string r in m_Regions) if (r != m_Region) aOther.Add(r);
            g.Note("・信件庫裡另外還有綁定檔：" + string.Join("、", aOther)
                   + "　—— 那是**別棵樹的區**，本頁不處理它們（它們由那一區自己的資料根處理）。");
        }

        string aDraft = g.TextField("改區域名（⚠ 會把全體 persona 的綁定檔重新定鍵）", m_Region, "bank/f/region");
        bool aArmed = g.FieldValue(PendingId, "") == "region:" + aDraft;
        if (aDraft != m_Region && aDraft.Trim().Length > 0)
        {
            g.Note("⚠ 送出之後，`letters/<persona>/bank/" + m_Region + ".md` 這批檔**不會**自動改名 ⇒"
                   + " 在它們改名之前，全員的帳號會解析不到。⛔ 本頁不代為改名。");
            using (g.Row())
            {
                if (g.Button(aArmed ? "⚠ 再按一次確認改區域名" : "改區域名", "bank/region/set"))
                {
                    if (!aArmed) { g.SetField(PendingId, "region:" + aDraft); m_Message = "⚠ 待確認：再按一次才會寫回設定檔"; }
                    else
                    {
                        g.SetField(PendingId, "");
                        if (SCP_BankRegion.Write(m_DataRoot.Value, aDraft, out string? aErr))
                        {
                            m_Message = "✅ 區域名已寫回 " + SCP_BankRegion.SettingsPath(m_DataRoot.Value);
                            m_Loaded = false;
                            m_Mig = null;      // 換區 ⇒ 舊的對照表是上一個區的讀數，⛔ 不留著誤用
                            OnPush();          // 回讀，⛔ 不採信剛才那個 draft
                        }
                        else m_Message = "✗ " + aErr;
                    }
                }
                if (aArmed && g.Button("取消", "bank/region/cancel"))
                { g.SetField(PendingId, ""); m_Message = "・已取消（設定檔一個字都沒動）"; }
            }
        }
    }

    // ===========================================================
    // 區塊職責：上方的 **Persona 選單 ＋ 帳戶（Agent）選單**，形狀取自 Unity 的 `UCL_BankAdminPage`
    //          （Tim 2026-09-17：「其他操作都是根據選取的 Persona & Bank 操作」）。
    // 物理意義：選 persona ⇒ 帳戶**自動同步**到他在本區 resolve 到的那一個（同 Unity 那頁的手勢）。
    // ⚠ 下拉走 `SCP_GuiWidgets.Dropdown`（可搜尋＋分頁；Tim 指路 `SCP_GuiHomePage` 那頁的用法）。
    //   🩸 我第一版寫成「一排選鈕」，理由是我斷定「`SCP_Ui` 沒有下拉」——
    //     而那個結論來自**只 grep 了 `SCP_Ui.cs` 一個檔**，下拉其實是 `SCP_GuiWidgets.cs` 的擴充方法。
    //   ⇒ 又一次「我量的那一格 ≠ 我宣稱的射程」：一個檔案的搜尋結果，被我講成整個 GUI 層的性質。
    //   ⭐ 表格照樣留著（`●`／`○` 看得出誰被選到）—— 下拉收合時只有一顆鈕，
    //     那時「有哪些候選」與「誰被選到」都要有地方看。
    // ⚠ 借用別區綁定的人照樣列且可選 —— 借用＝**在本區真的開戶並綁定**，那是本區可用的帳戶
    //   （Tim 2026-09-17 更正；我曾把它讀成「不屬於本區」而關掉了 @kaguya 的帳戶）。
    // ===========================================================
    void DrawAccountTable(SCP_Ui g)
    {
        string aSelP = SelectedPersona(g);
        string aSelA = SelectedAccount(g);

        using (g.Box("目前選取"))
        {
            g.Label("Persona：" + (aSelP.Length > 0 ? aSelP : "（未選）")
                    + "　｜　帳戶：" + (aSelA.Length > 0 ? aSelA : "（未選）")
                    + "　｜　餘額：" + BalanceText(aSelA));
            if (aSelP.Length > 0)
            {
                Row? r = FindRow(aSelP);
                if (r != null && r.Borrowed)
                    g.Note($"・`{aSelP}` 在本區沒有自己的綁定，用的是 **{r.BindingRegion} 區的帳號 id**"
                           + "（借用＝依那個 id 在本區開戶＋綁定 ⇒ 是本區可以正常運作的帳戶）");
            }
            if (aSelA.Length == 0)
                g.Note("⚠ **還沒選帳戶** ⇒ 底下的動作會被擋下來。"
                       + "⛔ 本頁不替你挑第一個 —— 「我沒選」與「我選了第一個」動的是不同人的錢。");
            if (g.Button("清掉選取", "bank/sel/clear"))
            { g.SetField(PersonaId, ""); g.SetField(AccountId, ""); g.SetField(PendingId, ""); }
        }

        g.Title("Persona 選單（這一區綁定得到的人）");
        if (m_Rows.Count == 0)
        {
            g.Note("（這一區沒有任何 persona 綁定 —— 不是「沒有人」，是這一區沒人綁）");
        }
        else
        {
            using (g.Table("", "persona", "帳號 id", "新銀行", "餘額", "備註"))
            {
                for (int i = 0; i < m_Rows.Count; ++i)
                {
                    Row r = m_Rows[i];
                    Acct? a = FindAcct(r.AccountId);
                    string aState = a == null ? "未開戶" : a.Closed ? "⛔ 已銷戶" : "· 已開戶";
                    string aBal = a == null ? "—" : a.Balance.ToString();
                    string aNote = r.Borrowed ? "借用 " + r.BindingRegion + " 區的綁定" : "";
                    g.TableRow(r.Persona == aSelP ? "●" : "○", r.Persona, r.AccountId, aState, aBal, aNote);
                }
            }
            // ⚠ 摺起來的是**控制項不是資料**：上面那張表永遠展開（誰被選到看得見），
            //   摺的只是那一排按鈕 —— CLI 上 22 顆擠成一行會把後面的內容推到看不見。
        }
        g.Note("· 「未開戶」＝新銀行還沒有這一戶的帳戶檔。⛔ 那**不是**「他沒有錢」——"
               + "遷移前錢都還在舊 `Treasury/`。");
        g.Note("· 選人／選帳戶的兩格下拉**釘在最上面那一條**（跟著捲的話，"
               + "「我在對誰動錢」會在你捲到動錢鈕的那一刻離開畫面）。");
    }

    /// <summary>帳戶（Agent）選單 —— 直接選帳號，⚠ 央行那種沒有人綁的只能從這裡選。</summary>
    void DrawAgentPicker(SCP_Ui g, string iSelected)
    {
        // ⛔ 這裡不畫標題：它現在住在工具列（`Title` 會畫成一條分隔線，把那一條頂欄切成兩段）。
        //   下拉自己的 label 就是標題。
        var aIds = new List<string>();
        foreach (Row r in m_Rows) if (!aIds.Contains(r.AccountId)) aIds.Add(r.AccountId);
        // 新銀行有、而本區沒有人綁的（央行／已銷戶的舊戶）也列出來 —— 藏起來的話
        // 「那一戶不存在」與「沒有人綁它」同形，而前者要開戶、後者不必。
        foreach (KeyValuePair<string, Acct> kv in m_Accounts)
        {
            bool aDup = false;
            foreach (string id in aIds) if (string.Equals(id, kv.Key, StringComparison.OrdinalIgnoreCase)) { aDup = true; break; }
            if (!aDup) aIds.Add(kv.Key);
        }
        if (aIds.Count == 0) { g.Note("（沒有任何帳戶可選）"); return; }

        aIds.Sort(StringComparer.OrdinalIgnoreCase);
        var aAcctOpts = new List<SCP_GuiOption>(aIds.Count);
        foreach (string id in aIds)
        {
            Acct? a = FindAcct(id);
            string aTag = a == null ? "（未開戶）" : a.Closed ? "（已銷戶）" : "（" + a.Balance + "）";
            aAcctOpts.Add(new SCP_GuiOption(id, id + aTag + WhoUses(id)));
        }
        string aPickA = g.Dropdown("帳戶（Agent）", aAcctOpts, iSelected, "bank/sel/a");
        if (!string.Equals(aPickA, iSelected, StringComparison.Ordinal) && aPickA.Length > 0)
        {
            g.SetField(AccountId, aPickA);
            g.SetField(PendingId, "");
            // ⚠ 直接選帳戶時**不去反推 persona**：一個帳戶可能掛好幾個人（`cc` 有七個），
            //   替他挑一個等於替使用者決定了「這筆是誰動的」，而那一欄要進帳本。
            g.SetField(PersonaId, "");
            m_Message = $"・已選帳戶 `{aPickA}`（persona 選取已清掉 —— 一戶可能掛好幾個人）";
        }
    }

    /// <summary>誰在用這個帳號（⚠ 一戶可能掛好幾個人 —— 下拉的標籤要看得出來）。</summary>
    string WhoUses(string iAccountId)
    {
        var aWho = new List<string>();
        foreach (Row r in m_Rows)
            if (string.Equals(r.AccountId, iAccountId, StringComparison.OrdinalIgnoreCase)) aWho.Add(r.Persona);
        if (aWho.Count == 0) return "　（本區沒有人用它）";
        if (aWho.Count <= 3) return "　" + string.Join("、", aWho);
        return $"　{aWho[0]} 等 {aWho.Count} 人";
    }

    Row? FindRow(string iPersona)
    {
        foreach (Row r in m_Rows) if (r.Persona == iPersona) return r;
        return null;
    }

    Acct? FindAcct(string iAccountId)
    {
        if (m_Accounts.TryGetValue(iAccountId.ToLowerInvariant(), out Acct? a)) return a;
        return m_Accounts.TryGetValue(iAccountId, out a) ? a : null;
    }

    string BalanceText(string iAccountId)
    {
        if (iAccountId.Length == 0) return "—";
        Acct? a = FindAcct(iAccountId);
        return a == null ? "（新銀行還沒有這一戶）" : (a.Closed ? "⛔ 已銷戶／" : "") + a.Balance.ToString();
    }

    // ── 開戶 / 入帳 / 扣款 ────────────────────────────────────

    void DrawPostPanel(SCP_Ui g)
    {
        g.Title("動作（對**選取的**那一戶）");

        // ⚠ 帳號**不再是一個自由輸入框**（Tim 2026-09-17：「其他操作都是根據選取的 Persona & Bank 操作」）。
        // 🩸 自由輸入的症狀是打錯一個字就動到別人的錢，而那一層**不會叫** ——
        //   `cmd bank` 只驗這個 id 合不合法、開沒開戶，它無從知道你想動的是不是這一戶。
        string aAcct = SelectedAccount(g);
        string aSelP = SelectedPersona(g);
        if (aAcct.Length == 0)
        {
            g.Note("⚠ **還沒選帳戶** —— 上面的兩格選單挑一個再回來。⛔ 本頁不提供手打帳號："
                   + "打錯一個字會動到別人的錢，而沒有任何一層會喊。");
            return;
        }
        g.Label($"目標帳戶：**{aAcct}**（餘額 {BalanceText(aAcct)}）"
                + (aSelP.Length > 0 ? $"　persona：**{aSelP}**" : "　persona：（未選）"));

        using (g.Row())
        {
            string aName = g.TextField("顯示名（開戶用）", g.FieldValue("bank/f/name", ""), "bank/f/name");
            if (g.Button("開戶", "bank/do/open"))
                Start("開戶 " + aAcct, () =>
                {
                    var aArgs = new Dictionary<string, string> { ["account"] = aAcct, ["display_name"] = aName };
                    return Describe(Dispatch("open", aArgs), aAcct);
                });
        }

        string aAmount = g.TextField("金額", g.FieldValue("bank/f/amount", ""), "bank/f/amount");
        string aKind = g.TextField("kind（為什麼）", g.FieldValue("bank/f/kind", ""), "bank/f/kind");
        string aRef = g.TextField("ref（指回現場）", g.FieldValue("bank/f/ref", ""), "bank/f/ref");
        // ⭐ `caller` 預設帶**選取的 persona** —— 那一欄會進帳本，是「誰動的這筆錢」的簽名。
        //   ⚠ 仍然可以改：一戶掛好幾個人時（`cc` 有七個），簽名是人的決定，⛔ 不是選單替他決定的。
        string aCaller = g.TextField("caller（誰動的；預設＝選取的 persona）",
                                     g.FieldValue("bank/f/caller", aSelP), "bank/f/caller");

        // ⚠ 三欄的擋是 `Cmd_Bank` 做的，本頁**不自己再驗一次** —— 兩份驗證遲早會不一樣，
        //   而不一樣的那天，畫面會說可以、帳本會說不行（或反過來）。這裡只把它會擋的事先講出來。
        g.Note("· `kind` / `ref` / `caller` 三欄**缺一就會被擋下**（擋在 `cmd bank`，不是這一頁）。");

        string aPending = g.FieldValue(PendingId, "");
        using (g.Row())
        {
            bool aArmedC = aPending == "credit";
            if (g.Button(aArmedC ? "⚠ 再按一次確認入帳" : "入帳", "bank/do/credit"))
            {
                if (aArmedC)
                {
                    g.SetField(PendingId, "");
                    Start("入帳 " + aAcct + " +" + aAmount, () => Describe(Post("credit", aAcct, aAmount, aKind, aRef, aCaller), aAcct));
                }
                else { g.SetField(PendingId, "credit"); m_Message = "⚠ 待確認：再按一次才會真的入帳"; }
            }

            bool aArmedD = aPending == "debit";
            if (g.Button(aArmedD ? "⚠ 再按一次確認扣款" : "扣款", "bank/do/debit"))
            {
                if (aArmedD)
                {
                    g.SetField(PendingId, "");
                    Start("扣款 " + aAcct + " -" + aAmount, () => Describe(Post("debit", aAcct, aAmount, aKind, aRef, aCaller), aAcct));
                }
                else { g.SetField(PendingId, "debit"); m_Message = "⚠ 待確認：再按一次才會真的扣款"; }
            }

            if (aPending.Length > 0 && g.Button("取消", "bank/do/cancel"))
            { g.SetField(PendingId, ""); m_Message = "・已取消（沒有動任何錢）"; }
        }
    }

    SCP_CmdResult Post(string iOp, string iAcct, string iAmount, string iKind, string iRef, string iCaller)
        => Dispatch(iOp, new Dictionary<string, string>
        {
            ["account"] = iAcct,
            ["amount"] = iAmount,
            ["kind"] = iKind,
            ["ref"] = iRef,
            ["caller"] = iCaller,
        });

    /// <summary>把一次 Cmd 的結果講成人話，⭐ 並**回讀**那一戶的現況當收據。</summary>
    string Describe(SCP_CmdResult iRes, string iAcct)
    {
        var aSb = new System.Text.StringBuilder();
        aSb.Append(iRes.Ok ? "✅ " : "❌ exit " + iRes.ExitCode + " ");
        for (int i = 0; i < iRes.Lines.Count && i < 6; ++i) aSb.Append(i == 0 ? "" : "\n  ").Append(iRes.Lines[i]);
        // ⭐ 判準是回讀，不是上面那句話。失敗時也回讀 —— 「它說失敗」與「它真的沒寫」是兩件事。
        if (iAcct.Length > 0)
        {
            SCP_CmdResult aBack = Dispatch("balance", new Dictionary<string, string> { ["account"] = iAcct });
            string aBal = "（讀不回來）";
            foreach (KeyValuePair<string, string> kv in aBack.Values) if (kv.Key == "balance") aBal = kv.Value;
            aSb.Append("\n  ・回讀 `").Append(iAcct).Append("` 餘額＝").Append(aBal);
        }
        return aSb.ToString();
    }

    // ── 遷移（舊 Treasury → 新銀行的開帳分錄）──────────────────

    void DrawMigrationPanel(SCP_Ui g)
    {
        g.Title("遷移：把舊 Treasury 的餘額搬成開帳分錄");
        g.Note("① 先取讀數（走 Unity 端 `Cmd_Treasury op=balances` ＝ 舊帳本的 canonical replayer）"
               + " → ② 看對照表 → ③ 兩段確認才落帳。");
        g.Note("⛔ 只搬**開帳金額**，不搬歷史分錄；只搬這一區綁定得到的帳號。"
               + "⚠ 本步驟**不**切換權威（TASK-0216 的 ⑥⑦ 不在這裡）。");

        if (m_DataRoot.Value.Length == 0)
        {
            g.Note("⚠ 沒有 AgentCommands 資料根 ⇒ 取不到舊餘額（去「路徑管理」頁設）。");
            return;
        }

        using (g.Row())
        {
            if (g.Button("① 取舊餘額讀數", "bank/mig/fetch"))
            {
                // ⚠ 在**按下去的這一刻**抓住是哪一區：取讀數跑在背景 thread 上，
                //   那邊碰不到 `SCP_Ui`（也不該碰 —— 頁面狀態不是 thread-safe 的）。
                Start("取舊餘額", FetchMigration);
            }
            if (m_Mig != null) g.Label("讀數時間：" + m_MigStamp + "（一取出來就開始過期）");
        }

        if (m_Mig == null) TryLoadMigCache(m_Region);
        if (m_Mig == null) { g.Note("（還沒有讀數 —— 這不是「舊帳本是空的」）"); return; }

        // ⚠ 讀數的**年齡**要印出來，⛔ 而且不自動過期。
        //   理由跟本 repo 的 Plurk 快取同一條：自動判過期會讓「新鮮」變成一個推論；
        //   印年齡、讓按下去的人自己判。
        //   🩸 這一格是實測出來的：CLI 每次 `--click` 都是新 process ⇒ 頁面欄位活不過一次呼叫，
        //     於是「② 執行遷移」那顆鈕在 agent 那側**根本畫不出來**（畫面說「沒有這個 id」）。
        //     那不是安全設計，是功能只有視窗模式能用 —— 而兩者在畫面上同形。
        g.Note("・讀數年齡：" + MigAgeText() + "　⛔ 不自動過期 —— 覺得舊就按 ① 重取。");

        int aWillMove = 0, aSkip = 0;
        for (int i = 0; i < m_Mig.Count; ++i) { if (m_Mig[i].Skip.Length == 0) ++aWillMove; else ++aSkip; }

        using (g.Table("帳號 id", "舊餘額", "新銀行現值", "處置"))
            for (int i = 0; i < m_Mig.Count; ++i)
            {
                MigRow r = m_Mig[i];
                g.TableRow(r.AccountId, r.OldBalance.ToString(),
                           r.HasAccount ? r.NewBalance.ToString() : "未開戶",
                           r.Skip.Length == 0 ? "→ 開帳 " + r.OldBalance : "⛔ " + r.Skip);
            }

        // ⭐ 具名的放棄：沒搬的逐列列出來（TASK-0216 ④）。⛔ 不是「差額對不起來」。
        g.Note("・要搬 **" + aWillMove + "** 戶／具名放棄 **" + aSkip + "** 戶（理由逐列寫在上表）");

        string aPending = g.FieldValue(PendingId, "");
        bool aArmed = aPending == "migrate";
        using (g.Row())
        {
            if (g.Button(aArmed ? "⚠ 再按一次確認遷移（會真的落帳）" : "② 執行遷移", "bank/mig/run"))
            {
                if (aArmed) { g.SetField(PendingId, ""); List<MigRow> aRows = m_Mig; Start("遷移", () => RunMigration(aRows)); }
                else { g.SetField(PendingId, "migrate"); m_Message = "⚠ 待確認：再按一次才會真的落開帳分錄"; }
            }
            if (aArmed && g.Button("取消", "bank/mig/cancel"))
            { g.SetField(PendingId, ""); m_Message = "・已取消（一戶都沒開、一筆分錄都沒落）"; }
        }
    }

    /// <summary>取舊餘額 —— 委派 Unity 端跑 `Cmd_Treasury op=balances`，讀它落的 TSV 報表。</summary>
    string FetchMigration()
    {
        string aDataRoot = m_DataRoot.Value;
        string aOut = Path.Combine(Path.GetTempPath(),
                                   "senate_bank_migration_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tsv");
        var aArgs = new Dictionary<string, string> { ["op"] = "balances", ["out_path"] = aOut };
        var aLog = new List<string>();
        string aCmdId = AgentCmdClient.Submit(aDataRoot, null, "Treasury", aArgs, s => aLog.Add(s));
        AgentCmdWaitResult aWait = AgentCmdClient.Wait(aDataRoot, null, aCmdId,
                                                       AgentCmdClient.DefaultWaitTimeoutSec,
                                                       AgentCmdClient.DefaultPollSec,
                                                       s => aLog.Add(s), s => aLog.Add(s), iPrintOutputs: false);
        if (aWait != AgentCmdWaitResult.Success)
            return "❌ 舊餘額取不到（" + aWait + "）—— ⛔ 這**不是**「舊帳本是空的」。"
                   + "\n  Editor 沒開的話它跑不完；開了再按一次。";
        if (!File.Exists(aOut))
            return "❌ Cmd 回成功，而報表檔不在：" + aOut
                   + "\n  ⇒ 「它說寫了」與「檔案真的在」是兩件事，這裡採信後者。";

        var aOld = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int aReportRows = 0;
        string aStamp = "";
        foreach (string aLine in File.ReadAllLines(aOut))
        {
            if (aLine.Length == 0) continue;
            string[] aCols = aLine.Split('\t');
            if (aLine[0] == '#')
            { if (aCols.Length >= 2 && aCols[0] == "# generated_at") aStamp = aCols[1]; continue; }
            if (aCols.Length < 3) continue;
            if (!int.TryParse(aCols[2], out int aBal)) continue;
            ++aReportRows;
            aOld[aCols[0]] = aBal;
        }

        // ⚠ **一個帳號可能綁著好幾個 persona**（實測 BTC 區：calli／gura／kiara 三個人都綁 `Myth`）。
        //   遷移的單位是**帳戶**不是人 ⇒ 逐人跑會把同一戶算三次，而「要搬 N 戶」那個數字會膨脹。
        //   🩸 落帳那側其實擋得住（`idem_key` 綁帳號 ⇒ 第二筆被判重），
        //   但**對照表上的數字會先騙到人** —— 而人是照那張表按下去的。
        var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aRows = new List<MigRow>();
        for (int i = 0; i < m_Rows.Count; ++i)
        {
            Row r = m_Rows[i];
            if (!aSeen.Add(r.AccountId)) continue;
            var aRow = new MigRow { AccountId = r.AccountId };
            aOld.TryGetValue(r.AccountId, out int aOldBal);
            aRow.OldBalance = aOldBal;

            m_Accounts.TryGetValue(r.AccountId.ToLowerInvariant(), out Acct? a);
            if (a == null) m_Accounts.TryGetValue(r.AccountId, out a);
            aRow.HasAccount = a != null;
            aRow.NewBalance = a?.Balance ?? 0;

            // 具名的放棄 —— 每一條都說得出理由，⛔ 不用一個「差額」把它們蓋掉。
            if (r.Borrowed) aRow.Skip = "借用 " + r.BindingRegion + " 區的綁定（那一區自己搬）";
            else if (a != null && a.Closed) aRow.Skip = "新銀行這一戶已銷戶";
            else if (aOldBal == 0) aRow.Skip = "舊餘額 0（開帳分錄只搬有錢的）";
            else if (a != null && a.Balance != 0) aRow.Skip = "新銀行這一戶已經有錢（" + a.Balance + "）⇒ 停下來問人";
            aRows.Add(aRow);
        }
        m_Mig = aRows;
        m_MigStamp = aStamp.Length > 0 ? aStamp : "（報表沒寫時間）";
        string aCacheWhy = SaveMigCache(m_Region);
        // ⚠ 兩個數字分開報：`aOld` 是**大小寫不分**的字典（`Zeta`／`zeta` 併成一格），
        //   而報表上是兩列。把字典的筆數說成「報表 N 戶」，就是拿一個數字去回答另一個問題。
        return "✅ 取到舊餘額：報表 " + aReportRows + " 列（大小寫不分後 " + aOld.Count + " 戶）"
               + "，其中這一區綁定得到的 " + aRows.Count + " 戶"
               + "\n  報表：" + aOut
               + "\n  " + aCacheWhy;
    }

    /// <summary>這棵樹的區域名 —— 來自 `Treasury/bank_settings.json` 的 `currency_id`（⛔ 不是畫面狀態）。</summary>
    string m_Region = "";

    /// <summary>區域名**回退到預設**的理由（null ＝ 真的是設定檔說的）。⚠ 「沒設定」與「值壞了」不可同形。</summary>
    string? m_RegionWhy;

    string MigCachePath(string iRegion)
        => Path.Combine(SenatePaths.RuntimeDir(m_Model.RepoRoot), "bank_migration_" + iRegion + ".tsv");

    /// <summary>
    /// 把對照表落一份，讓它活過 process 邊界（CLI 每次 `--click` 都是新 process）。
    /// <para>⚠ 落的是**快照**：它一寫下去就開始過期 ⇒ 第一行一定帶 `generated_at`，
    /// 讀回來時把年齡印在畫面上。⛔ 不做自動過期判斷。</para>
    /// </summary>
    string SaveMigCache(string iRegion)
    {
        if (m_Mig == null || iRegion.Length == 0) return "（沒有東西可以存）";
        try
        {
            const char TAB = '\t';
            const char LF = '\n';
            var aSb = new System.Text.StringBuilder();
            aSb.Append("# generated_at").Append(TAB).Append(m_MigStamp).Append(LF);
            aSb.Append("# region").Append(TAB).Append(iRegion).Append(LF);
            foreach (MigRow r in m_Mig)
                aSb.Append(r.AccountId).Append(TAB).Append(r.OldBalance).Append(TAB)
                   .Append(r.NewBalance).Append(TAB).Append(r.HasAccount ? "1" : "0")
                   .Append(TAB).Append(r.Skip).Append(LF);
            string aPath = MigCachePath(iRegion);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            File.WriteAllText(aPath, aSb.ToString(), new System.Text.UTF8Encoding(false));
            return "對照表已存：" + aPath;
        }
        catch (Exception e)
        {
            // ⚠ 存不起來要說：不說的話，下一次 process 進來會看到「還沒有讀數」，
            //   而那跟「我根本沒按過 ①」同形。
            return "⚠ 對照表存不起來（" + e.GetType().Name + "）⇒ 換一個 process 就得重按 ①";
        }
    }

    void TryLoadMigCache(string iRegion)
    {
        if (iRegion.Length == 0) return;
        try
        {
            string aPath = MigCachePath(iRegion);
            if (!File.Exists(aPath)) return;
            var aRows = new List<MigRow>();
            string aStamp = "";
            foreach (string aLine in File.ReadAllLines(aPath))
            {
                if (aLine.Length == 0) continue;
                string[] c = aLine.Split('\t');
                if (aLine[0] == '#')
                { if (c.Length >= 2 && c[0] == "# generated_at") aStamp = c[1]; continue; }
                if (c.Length < 5) continue;
                aRows.Add(new MigRow
                {
                    AccountId = c[0],
                    OldBalance = int.TryParse(c[1], out int o) ? o : 0,
                    NewBalance = int.TryParse(c[2], out int n) ? n : 0,
                    HasAccount = c[3] == "1",
                    Skip = c[4],
                });
            }
            if (aRows.Count == 0) return;
            m_Mig = aRows;
            m_MigStamp = aStamp;
            m_Region = iRegion;
        }
        catch { /* 讀不回來就當沒有 —— 畫面會照樣說「還沒有讀數」 */ }
    }

    /// <summary>讀數有多舊（算不出來就照實說算不出來，⛔ 不猜一個「剛剛」）。</summary>
    string MigAgeText()
    {
        if (!DateTime.TryParse(m_MigStamp, null,
                               System.Globalization.DateTimeStyles.AdjustToUniversal
                               | System.Globalization.DateTimeStyles.AssumeUniversal, out DateTime aT))
            return m_MigStamp + "（算不出年齡）";
        TimeSpan aAge = DateTime.UtcNow - aT;
        string aHow = aAge.TotalMinutes < 1 ? "不到 1 分鐘"
                    : aAge.TotalHours < 1 ? ((int)aAge.TotalMinutes) + " 分鐘"
                    : ((int)aAge.TotalHours) + " 小時 " + (aAge.Minutes) + " 分";
        return m_MigStamp + "（" + aHow + "前）";
    }

    /// <summary>真的落開帳分錄。每一戶：沒帳戶先開戶 → credit 一筆 `opening_balance`。</summary>
    string RunMigration(List<MigRow> iRows)
    {
        int aOpened = 0, aPosted = 0, aFailed = 0, aDuplicate = 0;
        var aSb = new System.Text.StringBuilder();
        for (int i = 0; i < iRows.Count; ++i)
        {
            MigRow r = iRows[i];
            if (r.Skip.Length > 0) continue;

            if (!r.HasAccount)
            {
                SCP_CmdResult aOpen = Dispatch("open", new Dictionary<string, string>
                { ["account"] = r.AccountId, ["display_name"] = r.AccountId });
                if (!aOpen.Ok)
                {
                    ++aFailed;
                    aSb.Append("\n  ❌ ").Append(r.AccountId).Append(" 開戶失敗：")
                       .Append(aOpen.Lines.Count > 0 ? aOpen.Lines[0] : "?");
                    continue;
                }
                ++aOpened;
            }

            // ⭐ `ref` 指回那一份讀數（TASK-0216 ③：新帳本自己解釋得了第一塊錢從哪來）。
            // ⭐ `idem_key` 綁帳號 ⇒ 同一戶重跑不會落第二筆開帳（開帳只能有一次）。
            SCP_CmdResult aPost = Dispatch("credit", new Dictionary<string, string>
            {
                ["account"] = r.AccountId,
                ["amount"] = r.OldBalance.ToString(),
                ["kind"] = "opening_balance",
                ["ref"] = "treasury-balances@" + m_MigStamp,
                ["caller"] = "bank-admin-page",
                ["description"] = "舊 Treasury 餘額遷入（TASK-0216／0223）",
                // ⚠ key 用**正規化後**的 id（小寫），⛔ 不是綁定檔上那個寫法。
                // 🩸 2026-09-17 遷移日量到：summit 的綁定是 BTC=`Zeta`／Florin=`zeta`，
                //   而新銀行的帳號 id **一律小寫**（`SCP_BankId`）⇒ 兩區指的是同一戶；
                //   判重卻是 `StringComparison.Ordinal` 比 key ⇒ `opening/Zeta` ≠ `opening/zeta`
                //   ⇒ 逐區各跑一次就會把**同一筆錢入帳兩次**（3337 → 6674），
                //   而兩次都回 Ok、兩次都不判重、帳戶檔只有一個 —— 沒有任何一層會喊。
                ["idem_key"] = "opening/" + r.AccountId.ToLowerInvariant(),
            });
            if (!aPost.Ok)
            {
                ++aFailed;
                aSb.Append("\n  ❌ ").Append(r.AccountId).Append(" 開帳失敗：")
                   .Append(aPost.Lines.Count > 0 ? aPost.Lines[0] : "?");
                continue;
            }

            // ⭐ **判重不算搬成功** —— 這是本頁最危險的一格。
            // 🩸 形狀：`idem_key` 綁帳號（開帳只能有一次，那是對的）⇒ 如果這一戶**先前試跑過**，
            //   遷移日這一筆會被判重、回 Ok、而**錢一毛都沒搬**。
            //   ⇒ 把它算進「開帳 N 筆」的話，畫面會印一個漂亮的 ✅，
            //     而「真的搬了」與「它以為它搬過了」在那個數字上同形。
            //   ⇒ 所以判重**單獨一欄**，並逐戶點名。
            bool aDup = false;
            foreach (KeyValuePair<string, string> kv in aPost.Values)
                if (kv.Key == "duplicate" && kv.Value == "1") aDup = true;
            if (aDup)
            {
                ++aDuplicate;
                aSb.Append("\n  ↻ ").Append(r.AccountId)
                   .Append(" **判重 ⇒ 這次沒有動錢**（這一戶先前已經有開帳分錄，多半是試跑留下的）");
                continue;
            }
            ++aPosted;
        }
        string aHead = "開戶 " + aOpened + " 戶／開帳分錄 " + aPosted + " 筆／判重 " + aDuplicate
                       + " 筆／失敗 " + aFailed + " 筆";
        if (aDuplicate > 0)
            aSb.Append("\n  ⚠ 有判重 ⇒ **那幾戶的錢沒有搬**。開帳只能有一次，")
               .Append("所以要嘛它本來就搬過了，要嘛是試跑的殘留要先清掉。");
        return (aFailed > 0 || aDuplicate > 0 ? "⚠ " : "✅ ") + aHead + aSb.ToString();
    }

    // ── 背景委派（形狀照 SCP_GuiSessionAdminPage）──────────────

    void Start(string iLabel, Func<string> iJob)
    {
        if (m_Job != null) { m_Message = "⏳ 前一筆（" + m_JobLabel + "）還沒完 ⇒ 這次沒有動作"; return; }
        if (SCP_GuiHost.RedrawsContinuously)
        {
            m_JobLabel = iLabel;
            m_Job = Task.Run(iJob);
            m_Message = null;
            return;
        }
        // 不會重畫的宿主（CLI 單次 render）：背景跑等於把答案丟掉 —— 這裡同步。
        try { m_Message = iJob(); }
        catch (Exception e) { m_Message = "⚠ 那一步炸了：" + e.GetType().Name + ": " + e.Message; }
        m_Loaded = false;
    }

    void PumpJob()
    {
        if (m_Job == null || !m_Job.IsCompleted) return;
        Task<string> aJob = m_Job;
        m_Job = null;
        try { m_Message = aJob.Result; }
        catch (Exception e) { m_Message = "⚠ 那一步炸了：" + e.GetType().Name + ": " + e.Message; }
        m_Loaded = false;   // 回讀磁碟才是判準
    }
}
