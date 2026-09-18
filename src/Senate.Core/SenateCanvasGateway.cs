// 區塊職責：畫布閘的 **CLI／Server 實作** —— 券、自由時間資格、分享派給 Unity Editor；
//           **token 直接串 Server**（`bank`）。
// 物理意義：券／session／酒館 seq 的權威實作只有 Editor 那側有。Tim 2026-09-03 拍板
//           「內部串 ucmd，不移植」⇒ 那幾格這裡不重寫，只把問題送過去、把答案讀回來。
//           ⭐ 2026-09-18 起 **token 那一格不同**：權威已切到新銀行（TASK-0216 ⑨），
//           而 Tim 說「Senate 端的金流直接串到 Server，不用走 ucmd 再繞一圈」——
//           繞 Editor 的話是 CLI → 檔案協議 → Editor → 再 spawn 一顆 senate → Server，
//           🩸 多出來的那一段**不增加任何保證，只多一個會逾時的地方**。
// 數值影響：每一次呼叫 ＝ 一次 AgentCommand 檔案協議 round-trip（寫 queue＋trigger、等 result 檔）。
//           取值一律讀 result 檔的 **values 欄**（`AgentCmdClient.ResultReport`），
//           ⛔ 不 regex stdout —— python 那側是 parse `🔢 in_free_time = 0|1` 的字串，
//           而字串會因為人讀輸出改版而靜默失配（那種錯的樣子跟「查不到」一模一樣）。
// 設計取捨：① **判定先於讀檔**：逾時的時候 result 檔沒有被更新，讀到的是上一輪的內容，
//              而它格式完整、數字合理（UCL 2026-08-16 血證）⇒ 這裡一律先看 Wait 的判定。
//           ② 查詢類逾時回「不知道」（Unknown／-1），寫入類逾時回**失敗** ——
//              兩者方向相反是刻意的：查不到可以再問，而「不確定有沒有扣到錢」只能當沒扣，
//              因為當成扣到了會讓像素白拿。
//           ③ 查詢的 timeout 可調（預設短）：資格查詢卡 180 秒對使用者是「工具壞了」，
//              而它的答案本來就允許是 Unknown。付款那條用預設長 timeout。
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Canvas;
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class SenateCanvasGateway : SCP_ICanvasGateway
{
    readonly string m_DataRoot;
    readonly string m_ProjectLabel;
    readonly Action<string> m_Log;
    readonly double m_QueryTimeoutSec;

    // 區塊職責：券餘額那一趟 round-trip 的**共用讀數** —— expiring 與 permanent 是
    //           同一支 Cmd、同一個 op、**同一份回應的兩個欄位**，所以只該問一次。
    // 🩸 為什麼（TASK-0226，2026-09-16 量的）：介面上是兩支方法，於是 SCP_CanvasPlace.TryPlan
    //   照著呼叫兩次 ⇒ 每一次 place 都跑**兩趟**完全相同的 round-trip。
    //   實測 `op=gateway`（3 趟）10.33／10.32s ⇒ 每趟約 3.4s，其中一趟是純重複的。
    //   而代價不只是慢：兩趟是兩個時刻的讀數 ⇒ **同一份餘額的兩個欄位可以互相矛盾**，
    //   apex-one 13:41 那次看到的正是 `expiring=-1（問不到）` 與 `permanent=120（查到了）`
    //   並排 —— 讀的人會以為券系統壞了，而它只是被問了兩次、第二次撞上忙碌的 lane。
    // 數值影響：命中時**零 round-trip**；oDetail 會說它來自哪一次、隔了多久（定語不可省）。
    // ⚠ 失敗也進快取，而且是刻意的：第一趟問不到時，第二個欄位再問一次只是再賠一個
    //   逾時（最壞 20s→40s），而答案必然相同。⛔ 但 TTL 必須短到一次 Cmd 之內 ——
    //   快取的用途是「同一個問題不問兩次」，不是「記住餘額」。
    static readonly TimeSpan k_VoucherEchoTtl = TimeSpan.FromSeconds(5);
    string m_VoucherEchoPersona = "";
    DateTime m_VoucherEchoAt = DateTime.MinValue;
    List<KeyValuePair<string, string>>? m_VoucherEchoValues;
    string m_VoucherEchoWhy = "";

    /// <summary>
    /// <paramref name="iLog"/> 給 null ＝ 靜音（本閘的 round-trip 細節不該蓋掉 Cmd 自己的輸出）。
    /// </summary>
    public SenateCanvasGateway(string iDataRoot, string? iProjectLabel = null,
                               Action<string>? iLog = null, double iQueryTimeoutSec = 20)
    {
        m_DataRoot = iDataRoot;
        // 🩸 專案標籤**從資料根自己算**（資料根的上一層目錄名 —— 與地理定語的寫入端同一條規則）。
        //    2026-09-03 實測：原本吃宿主傳進來的 repo 根 basename ⇒ 印出
        //    「⤷ 錢與資格由 Unity Editor 執行 @ Senate（D:/Unity/Bar/AgentCommands）」——
        //    定語與它描述的那棵樹**是兩個來源**，於是定語自己說了謊。
        //    ⇒ 定語必須從被描述的那個東西身上長出來，不能由呼叫端另外宣告。
        //    （呼叫端仍可顯式覆寫，但那是刻意行為，不是預設。）
        m_ProjectLabel = iProjectLabel ?? DeriveProjectLabel(iDataRoot);
        m_Log = iLog ?? (_ => { });
        m_QueryTimeoutSec = iQueryTimeoutSec;
    }

    // ⚠ 2026-09-18 起**錢與資格不再是同一個宿主**：token 走 Server（`bank`），
    //   券／在場資格仍走 Editor。⛔ 兩者壓成一句「由 Editor 執行」就是一個過期的定語，
    //   而讀它的人會去錯的地方查為什麼沒扣到。
    public string HostQualifier
        => $"⤷ token 由 Senate Server 執行（`bank`）／券與資格由 Unity Editor 執行 @ {m_ProjectLabel}（{m_DataRoot}）";

    /// <summary>資料根 → 專案標籤（上一層目錄名）。解不出來就說「未宣告」，⛔ 不猜一個看起來合理的。</summary>
    static string DeriveProjectLabel(string iDataRoot)
    {
        try
        {
            string aTrimmed = iDataRoot.Replace('\\', '/').TrimEnd('/');
            string? aParent = System.IO.Path.GetDirectoryName(aTrimmed);
            string aName = System.IO.Path.GetFileName(aParent?.Replace('\\', '/').TrimEnd('/') ?? "");
            return aName.Length > 0 ? aName : "未宣告";
        }
        catch (Exception)
        {
            return "未宣告";
        }
    }

    // ───────────────────────────── 查詢（逾時 ⇒ 不知道）─────────────────────────────

    public SCP_CanvasTriState QueryInFreeTime(string iPersona, out string oDetail)
    {
        var aArgs = new Dictionary<string, string> { ["scope"] = "persona", ["persona"] = iPersona };
        if (!TryRun("SessionStatus", iPersona, aArgs, m_QueryTimeoutSec,
                    out List<KeyValuePair<string, string>> aValues, out string aWhy))
        {
            // 🩸 這一格是本檔最重要的一行：問不到就回 Unknown。
            //    回 No 的話呼叫端會去開一場他其實已經在的自由時間，而沒有任何一層會喊。
            oDetail = "問不到（" + aWhy + "）⇒ 這是「不知道」不是「不在」";
            return SCP_CanvasTriState.Unknown;
        }
        string aRaw = Value(aValues, "in_free_time");
        if (aRaw.Length == 0)
        {
            oDetail = "Cmd 成功但沒有回 in_free_time 這一欄 ⇒ 仍然是「不知道」";
            return SCP_CanvasTriState.Unknown;
        }
        oDetail = "來源：Cmd SessionStatus 的 values 欄 in_free_time=" + aRaw;
        return aRaw == "1" ? SCP_CanvasTriState.Yes : SCP_CanvasTriState.No;
    }

    public int QueryExpiringVouchers(string iPersona, out string oDetail)
        => QueryVoucherField(iPersona, "expiring", out oDetail);

    public int QueryPermanentVouchers(string iPersona, out string oDetail)
        => QueryVoucherField(iPersona, "permanent", out oDetail);

    int QueryVoucherField(string iPersona, string iField, out string oDetail)
    {
        (List<KeyValuePair<string, string>>? aValues, string aWhy, string aEcho) =
            VoucherBalance(iPersona);
        if (aValues == null)
        {
            oDetail = "問不到（" + aWhy + "）" + aEcho + "⇒ -1 是「不知道」不是「沒有券」";
            return -1;
        }
        // 兩種欄名都試：Editor 那側的欄名還沒被本單驗過（②的已知缺口，不假裝知道）
        string aRaw = Value(aValues, iField);
        if (aRaw.Length == 0) aRaw = Value(aValues, iField + "_vouchers");
        if (aRaw.Length == 0) aRaw = Value(aValues, "voucher_" + iField);
        if (!int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aN))
        {
            oDetail = "Cmd 成功但讀不到 " + iField + " 那一欄（回了 "
                      + aValues.Count + " 欄）⇒ 仍然是「不知道」";
            return -1;
        }
        oDetail = "來源：Cmd CanvasVoucher 的 values 欄 " + iField + "=" + aN + aEcho;
        return aN;
    }

    /// <summary>
    /// 券餘額那一趟 round-trip（<c>CanvasVoucher op=balance</c>）——
    /// 同一個 persona 在 <see cref="k_VoucherEchoTtl"/> 內只問一次，兩個欄位共用那份回應。
    /// </summary>
    /// <returns>
    /// values 給 null ＝ 問不到（why 說為什麼）；echo 是**定語**：
    /// 這個讀數是現問的還是沿用的、沿用的話隔了多久。⛔ 不可省 —— 省掉之後
    /// 「剛剛量到的」與「5 秒前量到的」在畫面上同形。
    /// </returns>
    (List<KeyValuePair<string, string>>? Values, string Why, string Echo) VoucherBalance(string iPersona)
    {
        TimeSpan aAge = DateTime.UtcNow - m_VoucherEchoAt;
        if (m_VoucherEchoAt != DateTime.MinValue
            && string.Equals(m_VoucherEchoPersona, iPersona, StringComparison.Ordinal)
            && aAge >= TimeSpan.Zero && aAge <= k_VoucherEchoTtl)
        {
            string aEcho = "（與上一欄同一次 round-trip，+"
                           + aAge.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s）";
            return (m_VoucherEchoValues, m_VoucherEchoWhy, aEcho);
        }

        var aArgs = new Dictionary<string, string> { ["op"] = "balance", ["persona"] = iPersona };
        bool aOk = TryRun("CanvasVoucher", iPersona, aArgs, m_QueryTimeoutSec,
                          out List<KeyValuePair<string, string>> aValues, out string aWhy);
        m_VoucherEchoPersona = iPersona;
        m_VoucherEchoAt = DateTime.UtcNow;
        m_VoucherEchoValues = aOk ? aValues : null;
        m_VoucherEchoWhy = aWhy;
        return (m_VoucherEchoValues, aWhy, "");
    }

    // ===========================================================
    // 區塊職責：token 的讀與寫 —— **直接串 Server**（`bank`），⛔ 不再派 ucmd 繞 Editor。
    // 物理意義：Tim 2026-09-18：「Senate 端的金流直接串到 Server，不用走 ucmd 再繞一圈。」
    //          權威切到新銀行之後（TASK-0216 ⑨），繞 Editor 那條是
    //          **CLI → 檔案協議 → Editor → 再 spawn 一顆 senate → Server**：
    //          同一筆錢走兩次行程邊界，而中間那一段**不增加任何保證**。
    // 🩸 而它不只是慢：多一段就多一個會逾時的地方 ⇒「不知道有沒有扣到」的機會變兩倍，
    //   而那個狀態正是這支最貴的失效（逾時一律當沒扣，否則就是白拿像素）。
    // ⚠ `bank` 是 `ServerDelegateCmd` ⇒ 在 CLI 裡被打到會自己委派給 Server
    //   （路由由 `ServerContext.InServer` 決定，**不是由呼叫端記得**）。
    // ⚠ 參數名跟舊的 `Treasury` 那支**不一樣**：這裡是 `kind` / `ref`，⛔ 不是 `use_kind` / `use_ref`。
    //   ⭐ 而帶錯的失效樣子也換了：`bank` 有 ArgSpec 預檢**會擋下並說出理由**，
    //     ⛔ 不再是舊路那種「靜默取預設值、錢照扣、審計欄留白」。
    // ===========================================================
    string BankRoot()
        => System.IO.Path.Combine(
            m_DataRoot, SCP.Core.Paths.SCP_PathRegistry.Get(SCP.Core.Paths.SCP_PathId.BankRoot).DeriveSuffix);

    /// <summary>失敗訊息的第一行 —— `SCP_CmdResult` 沒有 Title 欄，人讀的內容在 `Lines`。</summary>
    static string FirstLineOf(SCP_CmdResult iResult)
        => iResult.Lines.Count > 0 ? iResult.Lines[0] : "（沒有訊息）";

    /// <summary>從 `SCP_CmdResult` 的 values 撈一欄（沒有就回空字串）。</summary>
    static string ValueOf(SCP_CmdResult iResult, string iKey)
    {
        foreach (KeyValuePair<string, string> aPair in iResult.Values)
            if (string.Equals(aPair.Key, iKey, StringComparison.Ordinal)) return aPair.Value;
        return "";
    }

    public long QueryTokenBalance(string iAccountId, out string oDetail)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "balance",
            ["bank_root"] = BankRoot(),
            ["account"] = iAccountId,
        };
        SCP_CmdResult aResult = SCP_CmdRegistry.Dispatch("bank", aArgs);
        if (aResult.ExitCode != 0)
        {
            oDetail = "問不到（bank exit " + aResult.ExitCode + "：" + FirstLineOf(aResult)
                      + "）⇒ -1 是「不知道」，**不是 0**（0 是查到了沒錢）";
            return -1;
        }
        string aRaw = ValueOf(aResult, "balance");
        if (!long.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long aBalance))
        {
            oDetail = "bank 成功但讀不到 balance 欄 ⇒ 仍然是「不知道」";
            return -1;
        }
        oDetail = "來源：Cmd bank（Server）的 values 欄 balance=" + aBalance;
        return aBalance;
    }

    // ───────────────────────────── 寫入（逾時 ⇒ 失敗）─────────────────────────────

    public SCP_CanvasGateResult ConsumeVouchers(string iPersona, int iCount, string iSourceRef,
                                                string iDescription)
    {
        if (iCount <= 0) return SCP_CanvasGateResult.Good("amount<=0，無需消券（不必打擾 Editor）");
        // ⚠ 參數名是量出來的不是猜的：Editor 端 Op_Consume 讀的是 `ref`（不是 source_ref）。
        //   帶錯名字不會被說「名字錯」—— 它會拿預設值（空字串）然後照樣扣，審計欄留白。
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "consume",
            ["persona"] = iPersona,
            ["amount"] = iCount.ToString(CultureInfo.InvariantCulture),
            ["ref"] = iSourceRef,
            ["description"] = iDescription,
        };
        if (!TryRun("CanvasVoucher", iPersona, aArgs, AgentCmdClient.DefaultWaitTimeoutSec,
                    out _, out string aWhy))
            // ⚠ 逾時在這裡是**失敗**：不確定有沒有扣到，一律當沒扣（當成扣到了就是白拿像素）。
            return SCP_CanvasGateResult.Bad("扣券沒有成功的收據（" + aWhy + "）");
        return SCP_CanvasGateResult.Good("扣券 " + iCount + " 張（Editor 端 CanvasVoucher consume）");
    }

    public SCP_CanvasGateResult DebitTokens(string iAccountId, int iAmount, string iSourceKind,
                                             string iSourceRef, string iDescription)
    {
        if (iAmount <= 0) return SCP_CanvasGateResult.Good("amount<=0，無需扣款（不必驚動 Server）");
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "debit",
            ["bank_root"] = BankRoot(),
            ["account"] = iAccountId,
            ["amount"] = iAmount.ToString(CultureInfo.InvariantCulture),
            // ⚠ `bank` 這支的欄名是 `kind` / `ref`（⛔ 不是舊 Treasury 的 use_kind / use_ref）。
            //   舊路帶錯名字會**靜默取預設值、錢照扣、審計欄留白**；
            //   這支有 ArgSpec 預檢 ⇒ 帶錯會被擋下並說出理由。
            ["kind"] = iSourceKind,
            ["ref"] = iSourceRef,
            ["description"] = iDescription,
            // 🩸 `caller` 是**簽名欄**（沒簽名的錢日後查不出是誰動的）。
            //   ⚠ 新銀行**沒有**舊系統那條「帳戶隔離鐵律」（caller != account 就拋例外）——
            //     所以這裡填帳戶本人不再是為了通過檢查，是為了**留下正確的簽名**。
            ["caller"] = iAccountId,
        };
        SCP_CmdResult aResult = SCP_CmdRegistry.Dispatch("bank", aArgs);
        // ⛔ 非零一律當「沒扣成功」——「不知道有沒有扣到」在這支要當成沒扣
        //   （當成扣到了就是白拿像素）。
        if (aResult.ExitCode != 0)
            return SCP_CanvasGateResult.Bad("扣 token 沒有成功的收據（bank exit "
                                            + aResult.ExitCode + "：" + FirstLineOf(aResult) + "）");
        return SCP_CanvasGateResult.Good("扣 " + iAmount + " token（Server 端 bank debit）");
    }

    // 區塊職責：把分享（含預覽附件）派給 Editor 的 Cmd_Tavern op=post
    // 物理意義：附件**原封不動送絕對路徑**，相對化交給收件端（`Cmd_Tavern.ParseRefs`）——
    //   🩸 2026-09-07 我第一版在這裡相對化，實測整條路都掛不上附件。真因：Senate 的
    //   `Program.RepoRoot()` 是**從 exe 自己的目錄**往上找 `.git` ⇒ 它永遠是 `D:/Unity/Senate`，
    //   而預覽圖住在消費端專案（`D:/Unity/Bar/AgentCommands/Canvas/previews/`）⇒
    //   `StartsWith` 永遠不成立、refs 永遠是空的。
    //   ⇒ 本宿主**結構上不知道**那棵樹的 repo 根在哪；知道的是 Editor（mirror 就在它那邊）。
    //   📌 一般形：路徑相對化要在**知道那個根的那一層**做，不是在手上剛好有一個根的那一層做。
    // 數值影響：`iAttachAbsolutePath` 給 null ⇒ 不帶 refs（純文字分享，行為與加入前相同）；
    //          `iTag` 給值時掛 `meta=tag:<tag>`（收件端用它分類，09-06 之前那批是 `canvas-share`）。
    public SCP_CanvasGateResult Share(string iPersona, string iRoom, string iBody,
                                      string? iAttachAbsolutePath = null, string? iTag = null)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "post",
            ["room"] = iRoom,
            ["body"] = iBody,
            ["persona"] = iPersona,
        };
        if (!string.IsNullOrEmpty(iTag)) aArgs["meta"] = "tag:" + iTag;

        string aAttach = iAttachAbsolutePath ?? "";
        if (aAttach.Length > 0) aArgs["refs"] = aAttach.Replace('\\', '/');

        if (!TryRun("Tavern", iPersona, aArgs, AgentCmdClient.DefaultWaitTimeoutSec,
                    out List<KeyValuePair<string, string>> aValues, out string aWhy))
            // 分享失敗**不該讓放點失敗** —— 像素已經落盤、錢已經扣了，廣播是 best-effort。
            return SCP_CanvasGateResult.Bad("分享沒發出去（" + aWhy + "）—— 像素與帳不受影響");
        string aSeq = Value(aValues, "post_seq");
        return SCP_CanvasGateResult.Good("已發" + (aSeq.Length > 0 ? "（seq " + aSeq + "）" : "")
                                         + (aAttach.Length > 0 ? "，附預覽（相對化由收件端做）" : "，無附件"));
    }

    // ───────────────────────────── 底層：一次 round-trip ─────────────────────────────

    bool TryRun(string iCmdType, string? iPersona, Dictionary<string, string> iArgs,
                double iTimeoutSec, out List<KeyValuePair<string, string>> oValues, out string oWhy)
    {
        oValues = new List<KeyValuePair<string, string>>();
        oWhy = "";
        try
        {
            if (!AgentCmdClient.EnsureIdle(m_DataRoot, iPersona, 10, m_Log, out string aIdleWhy))
            {
                // 殘留檔在哪由 EnsureIdle 自己說 —— 我不改寫它的措辭（改寫等於把定語弄丟）
                oWhy = "前一筆 Cmd 還卡在同一條 lane：" + aIdleWhy;
                return false;
            }
            string aCmdId = AgentCmdClient.Submit(m_DataRoot, iPersona, iCmdType, iArgs, m_Log);
            AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(m_DataRoot, iPersona, aCmdId,
                iTimeoutSec, AgentCmdClient.DefaultPollSec, m_Log, m_Log, iPrintOutputs: false);
            // ⛔ 順序寫死：**先判定，才准碰 result 檔**（逾時讀到的是上一輪，而它看起來完全正常）
            if (aVerdict != AgentCmdWaitResult.Success)
            {
                // ⛔ 不在這裡猜成因 —— 成因是**量**出來的，而量它的地方只有一個
                //   （AgentCmdClient.DescribeWaitTimeout；理由見那支方法的血證註解）。
                oWhy = aVerdict == AgentCmdWaitResult.Timeout
                    ? AgentCmdClient.DescribeWaitTimeout(m_DataRoot, iPersona, aCmdId, iTimeoutSec)
                    : "Editor 端回報失敗";
                return false;
            }
            (bool aFound, _, List<KeyValuePair<string, string>> aValues) =
                AgentCmdClient.ResultReport(m_DataRoot, aCmdId);
            if (!aFound)
            {
                oWhy = "沒有 result 檔（跟「有檔但沒有 values」不同形）";
                return false;
            }
            oValues = aValues;
            return true;
        }
        catch (Exception e)
        {
            oWhy = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    static string Value(List<KeyValuePair<string, string>> iValues, string iKey)
    {
        foreach (KeyValuePair<string, string> aKv in iValues)
            if (string.Equals(aKv.Key, iKey, StringComparison.Ordinal)) return aKv.Value;
        return "";
    }
}
