// 區塊職責：**匯率與券互換市場後台頁**（TASK-0272）。
// 物理意義：Senate 後台檢視各券種相對於基準幣（USD）與任意 A 券之折算匯率，
//           支援手填維護 Bid/Ask 報價，二段確認（Pending 閘門）原子落盤 `Market/rates_cache.json`。
// 數值影響：讀取無 IO（已快取）或單次讀檔；寫入走原子儲存（tmp → replace）。
// 🩸 守衛：
//   ① **無緩存資訊時，視為無法兌換**（Tim 2026-09-22 拍板：並非所有券都能互相兌換）。
//   ② **二段確認**：動設定與手填匯率需確認兩次，避免誤點覆蓋報價。
//   ③ **核心操作鈕住在 `TopBarButtons`** ⇒ 不跟內容一起捲。
//      🩸 這一頁的匯率表列數隨券種長，鈕留在內容裡的話，
//        「系統現在是開還是關」這個讀數會在你捲下去按鈕的那一刻離開畫面 ——
//        而那顆鈕做的正是把它反過來。所以狀態讀數跟著鈕一起釘在工具列。
//   ④ **`PendingId` 只有一格** ⇒ 兩種二段確認（切系統／寫報價）天生互斥。
//      ⚠ 移進工具列之後這兩顆鈕變成鄰居，覆寫比以前好按 ⇒ **覆寫一律出聲**，不靜默換掉。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Gui;
using SCP.Core.Market;
using Senate.Core;

namespace Senate.Cli.Pages;

public sealed class RateAdminPage : SCP_GuiToolPage
{
    public const string PageKey = "rates";
    public const string PendingId = "rates/pending";
    public const string BaseVoucherId = "rates/base_voucher";

    public const string EditSymbolId = "rates/edit/symbol";
    public const string EditBidId = "rates/edit/bid";
    public const string EditAskId = "rates/edit/ask";
    public const string EditEnabledId = "rates/edit/enabled";

    /// <summary>
    /// 該券手續費覆寫欄。⛔ **留空 ＝ 不覆寫（吃全域）**，不是 0 ——
    /// 兩者在算式裡差一整筆手續費，而在畫面上只差「有沒有打字」。
    /// </summary>
    public const string EditFeeId = "rates/edit/fee_pct";

    /// <summary>
    /// 基準券下拉的 key。
    /// ⚠ 下拉**自己也存一格**影子值（<c>BaseVoucherPickId + "/value"</c>），而權威值是
    /// <see cref="BaseVoucherId"/> ⇒ 程式端改動基準券時**兩格都要寫**（走
    /// <see cref="SetBaseVoucher"/>）。只寫權威值的話，下一輪下拉會把影子裡的舊值
    /// 當成使用者的選擇寫回權威值 —— **而那不會報錯，畫面上只是「我明明選了另一個」**。
    /// </summary>
    public const string BaseVoucherPickId = "rates/sel/base";

    readonly SenateModel m_Model;

    string m_DataRoot = "";
    SCP_MarketRateConfig? m_Config;
    string? m_LoadError;
    string? m_Message;

    public RateAdminPage(SenateModel iModel) : base()
    {
        m_Model = iModel;
    }

    public override string Key => PageKey;
    public override string Title => "匯率與券市場";
    public override string? MenuGroup => "銀行";

    public override void OnPush()
    {
        base.OnPush();
        Load();
    }

    void Load()
    {
        m_LoadError = null;
        m_Message = null;

        // 推導資料根
        m_DataRoot = m_Model.AgentCommandsRoot.Value;
        if (string.IsNullOrEmpty(m_DataRoot) || !Directory.Exists(m_DataRoot))
        {
            string aCandidate = Path.Combine(m_Model.RepoRoot, "AgentCommands");
            if (Directory.Exists(aCandidate)) m_DataRoot = aCandidate;
            else m_DataRoot = "D:/Unity/Bar/AgentCommands";
        }

        if (!Directory.Exists(m_DataRoot))
        {
            m_LoadError = $"找不到 AgentCommands 資料根（{m_DataRoot}）";
            return;
        }

        m_Config = SCP_MarketRateCache.Load(m_DataRoot, out string? aErr);
        if (aErr != null) m_LoadError = aErr;
    }

    // ===========================================================
    // 區塊職責：**釘在最上面的工具列** —— 這一頁所有會寫檔的動作都在這裡。
    // 物理意義：工具列住在 `TopBar()` 裡 ⇒ **不跟內容一起捲**（同 BankAdminPage 的理由）。
    // ⚠ 本方法在 `DrawContent` **之前**跑：欄位值一律走 `FieldValue` 讀**欄位倉**，
    //   不是讀下面那幾個 TextField 的回傳值 —— 那時候它們還沒畫。
    //   倉裡拿到的是使用者上一次送出的值，跟畫面上看到的是同一個。
    // 🩸 載入失敗／尚未載入時**照樣把「重新讀取」畫出來** ——
    //   一個沒有出口的錯誤畫面，跟當掉沒有差別。
    // ===========================================================
    protected override void TopBarButtons(SCP_Ui iUi)
    {
        if (iUi.Button("重新讀取", "rates/btn/refresh"))
        {
            Load();
            m_Message = "・已重新讀取快取";
        }

        // ⛔ 沒有設定就沒有可動的東西：明說原因，不畫出按不動的鈕
        //   （按得下去卻什麼都不做，跟「我按錯了」同形）。
        if (m_Config == null)
        {
            iUi.Label(m_LoadError != null ? "｜**載入失敗**" : "｜（尚未載入設定）");
            return;
        }

        DrawFxToggleButtons(iUi);
        DrawSaveQuoteButtons(iUi);

        // 基準券選取也釘在這裡（Tim 2026-09-23）：它是**整張表的分母** ——
        // 捲到表中段時「我現在是以哪個券為基準在看」這個讀數不該離開畫面。
        // ⚠ 下拉展開時會把工具列撐高（選項是 inline 畫的）—— 那是刻意的：
        //   展開＝正在挑券，這時本來就該讓它佔版面；收合之後只剩一顆頭。
        DrawBaseVoucherPicker(iUi);

        // 狀態讀數跟著鈕一起釘住 —— 旁邊那顆鈕做的就是把這個值反過來。
        iUi.Label($"｜系統狀態：{(m_Config.FxSystemEnabled ? "**【互換已啟用】**" : "**【互換已停用】**")}"
                  + $"｜全域手續費 **{m_Config.TakerFeePct * 100m:0.####}%**／筆");
    }

    /// <summary>
    /// 券種清單（含永遠存在的 USD）。⚠ 下拉與表格**必須共用這一支** ——
    /// 各自收集一份的失效樣子是「選單裡有、表上沒有」，而兩邊都不會喊。
    /// </summary>
    List<string> CollectSymbols()
    {
        var aAll = new List<string>(m_Config!.Quotes.Keys);
        if (!aAll.Contains("USD")) aAll.Insert(0, "USD");
        aAll.Sort(StringComparer.OrdinalIgnoreCase);
        return aAll;
    }

    /// <summary>
    /// 讀出目前基準券；⚠ 原選的券已不在清單裡時就地校正（**程式端**改動 ⇒ 影子值一起蓋）。
    /// 回傳值保證落在 <paramref name="iAllSymbols"/> 裡。
    /// </summary>
    string ResolveBaseVoucher(SCP_Ui g, List<string> iAllSymbols)
    {
        string aBase = g.FieldValue(BaseVoucherId, "").ToUpperInvariant();
        if (string.IsNullOrEmpty(aBase) || !iAllSymbols.Contains(aBase))
        {
            aBase = iAllSymbols.Contains("BTC") ? "BTC" : iAllSymbols[0];
            SetBaseVoucher(g, aBase);
        }
        return aBase;
    }

    /// <summary>基準券下拉（住在工具列）。選項標籤標出無報價的券 —— 選得到但換不了要先講。</summary>
    void DrawBaseVoucherPicker(SCP_Ui iUi)
    {
        List<string> aAll = CollectSymbols();
        string aCurrentBase = ResolveBaseVoucher(iUi, aAll);

        var aOptions = new List<SCP_GuiOption>(aAll.Count);
        foreach (string s in aAll)
        {
            bool aTradable = SCP_MarketRateCache.TryGetPairRate(m_Config!, s, "USD", out _, out _, out _);
            aOptions.Add(new SCP_GuiOption(s, aTradable ? s : s + "（無報價）"));
        }

        string aPick = iUi.Dropdown("基準券 (A 券)", aOptions, aCurrentBase, BaseVoucherPickId);
        if (aPick.Length > 0 && !string.Equals(aPick, aCurrentBase, StringComparison.OrdinalIgnoreCase))
        {
            // ⛔ 換基準券**不動** PendingId：這個選擇只換折算表的分母，
            //   兩種待確認（切系統／寫報價）跟它無關 —— 清掉反而會讓人以為自己按過的確認消失了。
            //   （BankAdminPage 換 persona 要清，是因為那裡的待確認動的就是「選中的那一戶」。）
            SetBaseVoucher(iUi, aPick.ToUpperInvariant());
        }
    }

    /// <summary>互換系統開關（二段確認）。</summary>
    void DrawFxToggleButtons(SCP_Ui iUi)
    {
        string aPending = iUi.FieldValue(PendingId, "");

        if (aPending == "toggle_fx")
        {
            if (iUi.Button(m_Config!.FxSystemEnabled ? "確認停用系統？" : "確認啟用系統？", "rates/btn/confirm_toggle"))
            {
                m_Config.FxSystemEnabled = !m_Config.FxSystemEnabled;
                if (SCP_MarketRateCache.Save(m_DataRoot, m_Config, out string? aSaveErr))
                {
                    m_Message = $"・匯率互換系統已設為：{(m_Config.FxSystemEnabled ? "啟用" : "停用")}";
                }
                else
                {
                    // 🩸 存檔失敗時記憶體裡那一格已經被翻掉了 ⇒ 翻回來。
                    //   不翻的話畫面顯示「已啟用」而磁碟上是停用，兩邊不同而沒有人會喊。
                    m_Config.FxSystemEnabled = !m_Config.FxSystemEnabled;
                    m_Message = $"[錯誤] 儲存失敗（設定未變更）：{aSaveErr}";
                }
                iUi.SetField(PendingId, "");
            }
            if (iUi.Button("取消", "rates/btn/cancel_toggle"))
            {
                iUi.SetField(PendingId, "");
            }
            return;
        }

        if (iUi.Button(m_Config!.FxSystemEnabled ? "停用互換系統" : "啟用互換系統", "rates/btn/req_toggle"))
        {
            ArmPending(iUi, "toggle_fx", aPending);
        }
    }

    /// <summary>手填報價寫入（二段確認）。欄位本體畫在內容區，這裡只放動作。</summary>
    void DrawSaveQuoteButtons(SCP_Ui iUi)
    {
        string aPending = iUi.FieldValue(PendingId, "");

        string aSymbol = iUi.FieldValue(EditSymbolId, "").Trim().ToUpperInvariant();
        string aBidStr = iUi.FieldValue(EditBidId, "").Trim();
        string aAskStr = iUi.FieldValue(EditAskId, "").Trim();
        string aFeeStr = iUi.FieldValue(EditFeeId, "").Trim();
        bool aEnabled = iUi.ToggleValue(EditEnabledId, true);

        if (aPending == "save_rate")
        {
            string aFeeText = aFeeStr.Length == 0 ? "費率:吃全域" : $"費率:{aFeeStr}";
            if (iUi.Button($"確認將 `{aSymbol}` (Bid:{aBidStr}, Ask:{aAskStr}, {aFeeText}) 寫入快取？", "rates/btn/confirm_save"))
            {
                ExecuteSaveQuote(aSymbol, aBidStr, aAskStr, aFeeStr, aEnabled);
                iUi.SetField(PendingId, "");
            }
            if (iUi.Button("取消", "rates/btn/cancel_save"))
            {
                iUi.SetField(PendingId, "");
            }
            return;
        }

        if (iUi.Button("儲存 / 更新報價 (二段確認)", "rates/btn/req_save"))
        {
            if (!ValidateQuoteInput(aSymbol, aBidStr, aAskStr, aFeeStr, out string? aReason))
            {
                m_Message = "・" + aReason;
                return;
            }
            ArmPending(iUi, "save_rate", aPending);
        }
    }

    /// <summary>
    /// 進入二段確認的待確認態。
    /// 🩸 `PendingId` 只有一格 ⇒ 另一顆鈕的待確認會被**覆寫**。以前兩顆鈕隔得遠，
    /// 現在是鄰居 ⇒ 覆寫一律出聲：**「我剛剛明明按了確認」跟「我按到另一顆」在畫面上同形**。
    /// </summary>
    void ArmPending(SCP_Ui iUi, string iNext, string iCurrent)
    {
        if (iCurrent.Length > 0 && iCurrent != iNext)
        {
            m_Message = $"⚠ 原本待確認的「{PendingText(iCurrent)}」已被取消 ⇒ 現在待確認的是「{PendingText(iNext)}」";
        }
        iUi.SetField(PendingId, iNext);
    }

    static string PendingText(string iPending) => iPending switch
    {
        "toggle_fx" => "切換互換系統",
        "save_rate" => "寫入手填報價",
        _ => iPending
    };

    /// <summary>
    /// 手續費率合理性上限（**不含**）。⚠ 必須與 `SCP_Cmd_MarketRate.FeeSanityCap` 同值 ——
    /// 兩個入口寫不同的門檻，等於同一份設定有兩套規則，而那只會在「CLI 擋下、頁面放行」時才現形。
    /// 🩸 血證 2026-09-23：CLI 那格原本寫 `> 0.5`，而 **0.5 正是它要擋的手滑**（0.5% 漏掉百分比那一格）
    ///   ⇒ 50% 手續費被寫進真檔。門檻設在最可能打錯的那個數字上，等於沒有門。
    /// </summary>
    const decimal FeeSanityCap = 0.05m;

    /// <summary>報價輸入檢查 —— 擋下之後**不**進入待確認態（免得去確認一個注定失敗的動作）。</summary>
    static bool ValidateQuoteInput(string iSymbol, string iBidStr, string iAskStr, string iFeeStr, out string? oReason)
    {
        oReason = null;
        if (string.IsNullOrEmpty(iSymbol)) { oReason = "請先填寫券種代碼"; return false; }
        if (!decimal.TryParse(iBidStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aBid) || aBid <= 0)
        { oReason = "買入價 Bid 必須為大於 0 之數字"; return false; }
        if (!decimal.TryParse(iAskStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aAsk) || aAsk <= 0)
        { oReason = "賣出價 Ask 必須為大於 0 之數字"; return false; }
        if (aAsk < aBid) { oReason = "賣出價 Ask 不得低於買入價 Bid（避免倒掛）"; return false; }
        return TryParseFeeOverride(iFeeStr, out _, out oReason);
    }

    /// <summary>
    /// 解析手續費覆寫欄。⛔ **留空 ⇒ <c>null</c>（不覆寫）**，不是 0。
    /// </summary>
    static bool TryParseFeeOverride(string iFeeStr, out decimal? oFee, out string? oReason)
    {
        oFee = null;
        oReason = null;
        string aStr = (iFeeStr ?? "").Trim();
        if (aStr.Length == 0) return true;   // 留空＝不覆寫，合法

        if (!decimal.TryParse(aStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aFee))
        { oReason = $"手續費覆寫必須是數字（收到 '{aStr}'）；不想覆寫就**留空**"; return false; }
        if (aFee < 0m)
        { oReason = $"手續費不得為負（收到 {aFee}）—— 負手續費＝憑空生錢"; return false; }
        if (aFee >= FeeSanityCap)
        { oReason = $"手續費必須 < {FeeSanityCap}（＝{FeeSanityCap * 100m:0.#}%），收到 {aFee}（＝{aFee * 100m:0.####}%）。⚠ 手滑最常見的是漏掉百分比那一格：0.5% ＝ 0.005"; return false; }

        oFee = aFee;
        return true;
    }

    protected override void DrawContent(SCP_Ui g)
    {
        g.Title("【匯率與券互換市場後台】");
        g.Note("查閱與管理各券種相對於基準幣（USD）與任意指定基準券（A 券）之雙向折算比率。");
        g.Note("・規則：無緩存資訊之券種視為無法兌換（並非所有券都能互相兌換）。");
        g.Note("・核心操作（重新讀取／互換開關／寫入報價）在**上方工具列**，不跟本頁一起捲動。");

        if (m_LoadError != null)
        {
            g.Note($"[錯誤] 載入失敗：{m_LoadError}");
            g.Note("・出口：上方工具列的「重新讀取」。");
            return;
        }

        if (m_Config == null)
        {
            g.Note("（尚未載入設定）");
            return;
        }

        if (m_Message != null)
        {
            g.Note(m_Message);
        }

        using (g.Row())
        {
            g.Label($"基準貨幣：**{m_Config.BaseCurrency}**");
            g.Label($"快取更新時間：`{m_Config.UpdatedAtUtc}`");
        }
        g.Separator();

        DrawRateMatrix(g);
        g.Separator();

        DrawManualEditPanel(g);
    }

    /// <summary>
    /// 權威值與下拉影子值一起寫。
    /// 🩸 只寫其中一格的失效樣子是「選了 A，下一輪自己跳回 B」—— 沒有例外、沒有紅字。
    /// </summary>
    static void SetBaseVoucher(SCP_Ui g, string iSymbol)
    {
        g.SetField(BaseVoucherId, iSymbol);
        g.SetField(BaseVoucherPickId + "/value", iSymbol);
    }

    void DrawRateMatrix(SCP_Ui g)
    {
        // ⚠ 清單與基準券都走跟工具列下拉**同一支**（CollectSymbols / ResolveBaseVoucher）——
        //   各自算一份的失效樣子是「選單裡有、表上沒有」，而兩邊都不會喊。
        List<string> aAllSymbols = CollectSymbols();
        string aCurrentBase = ResolveBaseVoucher(g, aAllSymbols);

        g.Label($"全券種折算匯率表（基準券 ＝ **{aCurrentBase}**，要換基準券請用**上方工具列**的下拉）");
        // 🩸 淨率與毛率**一起印**：只印毛率的話，它會跟 `voucher-swap` 實際給的張數
        //   差一個手續費，而那個差額沒有任何一層會說它是手續費 —— 看起來像其中一支算錯了。
        g.Note("・**淨**＝扣完手續費實際到手（`voucher-swap` 給的就是這個）；**毛**＝市場價，不含手續費。");
        g.Note($"・手續費**按腿計**：{aCurrentBase} → USD → 該券，兩端都不是 USD ＝ 兩筆成交收兩次；一端是 USD 只收一次。");
        // 🩸 這一句非有不可：「該券手續費」欄只說**該券那一腿**，而淨率含**兩腿**。
        //   少了它，USD 那列會長成「手續費 0%，但淨率比毛率少 0.1%」—— 自相矛盾而沒有人解釋得了。
        g.Note($"・⚠「該券手續費」只是**該券那一腿**；淨率還含**基準券 {aCurrentBase} 那一腿**"
               + $"（{SCP_MarketRateCache.FeePctOf(m_Config!, aCurrentBase) * 100m:0.####}%）。"
               + $"⇒ USD 列費率印 0% 而淨率仍低於毛率，少的就是 {aCurrentBase} 那一腿。");

        using (g.Table("券種", "買入價 (Bid USD)", "賣出價 (Ask USD)", "該券手續費",
                       $"淨折算率 (賣 1 {aCurrentBase} 實得)", $"毛折算率 (不含費)",
                       $"淨買價 (買 1 該券實付 {aCurrentBase})", "狀態", "資料來源"))
        {
            foreach (string s in aAllSymbols)
            {
                if (!m_Config!.Quotes.TryGetValue(s, out var q))
                {
                    if (s == "USD") q = new SCP_RateQuote { Symbol = "USD", Bid = 1, Ask = 1, IsEnabled = true, Source = "builtin" };
                    else continue;
                }

                string aSellNetStr = "—";
                string aSellGrossStr = "—";
                string aBuyNetStr = "—";
                string aStatusStr = "無法兌換";

                if (SCP_MarketRateCache.TryGetPairRate(m_Config, aCurrentBase, s, out decimal aSellGross, out _, out _)
                    && SCP_MarketRateCache.TryGetPairRateNet(m_Config, aCurrentBase, s,
                        out decimal aSellNet, out decimal aBuyNet, out _, out _))
                {
                    aSellNetStr = aSellNet.ToString("0.########", CultureInfo.InvariantCulture);
                    aSellGrossStr = aSellGross.ToString("0.########", CultureInfo.InvariantCulture);
                    aBuyNetStr = aBuyNet.ToString("0.########", CultureInfo.InvariantCulture);
                    aStatusStr = "可兌換";
                }
                else
                {
                    if (!q.IsEnabled) aStatusStr = "已停用";
                    else aStatusStr = "無法兌換";
                }

                // ⚠ 「該券自己設的」與「吃全域」要分得出來 —— 兩者在數字上可能一模一樣，
                //   而改全域費率時只有前者不會跟著動。
                decimal aFee = SCP_MarketRateCache.FeePctOf(m_Config, q.Symbol);
                string aFeeStr = q.FeePct.HasValue
                    ? $"{aFee * 100m:0.####}%（覆寫）"
                    : $"{aFee * 100m:0.####}%";

                g.TableRow(
                    q.Symbol,
                    q.Bid.ToString("0.########", CultureInfo.InvariantCulture),
                    q.Ask.ToString("0.########", CultureInfo.InvariantCulture),
                    aFeeStr,
                    aSellNetStr,
                    aSellGrossStr,
                    aBuyNetStr,
                    aStatusStr,
                    q.Source
                );
            }
        }
    }

    /// <summary>手填欄位本體。⛔ 這裡**沒有按鈕** —— 寫入動作在工具列（守衛③）。</summary>
    void DrawManualEditPanel(SCP_Ui g)
    {
        g.Label("手填維護券種報價（相對於 USD）");

        g.TextField("券種代碼 (Symbol，如 BTC/GOLD)", g.FieldValue(EditSymbolId, ""), EditSymbolId);
        g.TextField("買入價 Bid USD (賣券換 USD 單價)", g.FieldValue(EditBidId, ""), EditBidId);
        g.TextField("賣出價 Ask USD (支付 USD 買券單價)", g.FieldValue(EditAskId, ""), EditAskId);
        g.TextField($"手續費覆寫 (0.001＝0.1%；留空＝吃全域 {m_Config!.TakerFeePct * 100m:0.####}%)", g.FieldValue(EditFeeId, ""), EditFeeId);
        g.Toggle("開放兌換 (IsEnabled)", g.ToggleValue(EditEnabledId, true), EditEnabledId);

        string aPending = g.FieldValue(PendingId, "");
        g.Note(aPending == "save_rate"
            ? "・待確認中 ⇒ 請到**上方工具列**按下確認（或取消）。"
            : "・填好之後按**上方工具列**的「儲存 / 更新報價 (二段確認)」。");
    }

    void ExecuteSaveQuote(string iSymbol, string iBidStr, string iAskStr, string iFeeStr, bool iEnabled)
    {
        // ⚠ 這裡是最後一道：待確認期間欄位還能被改，所以**確認的那一刻要重驗一次**
        //   —— 按鈕上印的那串字是按下「請求」那一刻組的，它可能已經不是現在的值了。
        if (!ValidateQuoteInput(iSymbol, iBidStr, iAskStr, iFeeStr, out string? aReason))
        {
            m_Message = $"🔴 未寫入（確認時重驗不過）：{aReason}";
            return;
        }

        decimal aBid = decimal.Parse(iBidStr, NumberStyles.Any, CultureInfo.InvariantCulture);
        decimal aAsk = decimal.Parse(iAskStr, NumberStyles.Any, CultureInfo.InvariantCulture);
        TryParseFeeOverride(iFeeStr, out decimal? aFeeOverride, out _);   // 上一行已驗過，這裡只取值

        var aQuote = new SCP_RateQuote
        {
            Symbol = iSymbol,
            BaseCurrency = "USD",
            Bid = aBid,
            Ask = aAsk,
            IsEnabled = iEnabled,
            Source = "admin_ui",
            FeePct = aFeeOverride,
            UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
        };

        m_Config!.Quotes[iSymbol] = aQuote;

        if (SCP_MarketRateCache.Save(m_DataRoot, m_Config, out string? aSaveErr))
        {
            m_Message = aFeeOverride.HasValue
                ? $"✅ 券種 `{iSymbol}` 報價已落盤（手續費覆寫 {aFeeOverride.Value * 100m:0.####}%）"
                : $"✅ 券種 `{iSymbol}` 報價已落盤（手續費**吃全域** {m_Config.TakerFeePct * 100m:0.####}%，未覆寫）";
        }
        else
        {
            // 🩸 存檔失敗 ⇒ 記憶體裡那筆要拿掉，否則畫面上的表印著一筆磁碟上沒有的報價。
            m_Config.Quotes.Remove(iSymbol);
            m_Message = $"🔴 儲存失敗（未寫入，表上那筆已撤回）：{aSaveErr}";
        }
    }
}
