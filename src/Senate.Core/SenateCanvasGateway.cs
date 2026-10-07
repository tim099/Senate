// 區塊職責：畫布閘的 **CLI／Server 實作** —— 四格宿主能力**全部不經 Unity Editor**：
//           token 與券串 Server（`bank` / `voucher`）、在場資格就地讀 session 檔、分享走 `tavern-post`（TASK-0366）。
// 物理意義：原本券／session／酒館發文的權威實作只有 Editor 那側有（Tim 2026-09-03「內部串 ucmd，不移植」）；
//           一格一格搬走之後：token 2026-09-18（TASK-0216 ⑨）、券同日（TASK-0243）、在場資格 2026-10-01（TASK-0360）、
//           分享 2026-10-01（TASK-0366）。⇒ 本檔已經沒有 AgentCommand 檔案協議 round-trip。
//           🩸 Tim 那句「Senate 端的金流直接串到 Server，不用走 ucmd 再繞一圈」的理由對每一格都成立：
//           繞 Editor 多出來的那一段**不增加任何保證，只多一個會逾時的地方**。
// 數值影響：取值一律讀 Cmd 結果的 **values 欄**，⛔ 不 regex stdout（字串會因人讀輸出改版而靜默失配）。
// 設計取捨：② 查詢類逾時回「不知道」（Unknown／-1），寫入類逾時回**失敗** ——
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

    // ⚠ 2026-09-18 **同一天改了兩次**，而中間那一版的定語當天就過期了：
    //   ① 早上：token 切到 Server ⇒ 寫成「token 走 Server／**券**與資格走 Editor」
    //   ② 下午：券也切到 Server（`voucher`）⇒ 上面那句的「券」當場變成假的
    //   ⇒ 那時只剩**在場資格**（自由時間／session）還在 Editor。
    //   ③ 2026-10-01（TASK-0360）：自由時間搬進 Senate ⇒ 在場資格改讀 session 檔（就地），這裡跟著改第三次。
    // 🩸 記著這個形狀：**定語是跟著實作走的，而它不會自己跟** ——
    //   一句半對的定語比沒有定語貴，因為讀它的人會去錯的地方查為什麼沒扣到。
    public string HostQualifier
        => $"⤷ token 與券由 Senate Server 執行（`bank` / `voucher`）／"
           + $"在場資格就地讀 session 檔 @ {(m_ProjectLabel == m_DataRoot ? m_DataRoot : m_ProjectLabel + "（" + m_DataRoot + "）")}";

    /// <summary>
    /// 資料根 → 定語標籤：走 `SCP_DataPaths.ProjectNameOf`（唯一一份，＝資料根完整路徑，TASK-0390）。
    /// 🩸 原本自己取「上一層目錄名」—— 資料根搬到 `D:/Unity/Valhalla` 之後印成 `@ Unity`。
    /// </summary>
    static string DeriveProjectLabel(string iDataRoot) => SCP.Core.Paths.SCP_DataPaths.ProjectNameOf(iDataRoot);

    // ───────────────────────────── 查詢（逾時 ⇒ 不知道）─────────────────────────────

    // TASK-0360：自由時間搬進 Senate 之後，在場資格**就地讀 session 檔**（判準 `IsRunningAt` —— 與 `free-time` 同一支），
    //   ⛔ 不再派 Unity 的 `SessionStatus`（那條路要 Editor 開著，關著時畫布的 freetime 付款就判不出來）。
    public SCP_CanvasTriState QueryInFreeTime(string iPersona, out string oDetail)
    {
        try
        {
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(m_DataRoot);
            string? aPath = SCP.Core.Session.SCP_ActivitySessionStore.PathOf(aRoot, iPersona);
            if (aPath == null || !System.IO.File.Exists(aPath))
            {
                oDetail = "來源：session 檔不存在（`" + (aPath ?? "?") + "`）⇒ 不在";
                return SCP_CanvasTriState.No;
            }
            // ⚠ `Load` 把**壞檔**與**換檔那一瞬間**都回 null（store 檔頭寫明）⇒ 直接拿它判會把「讀不出來」說成「不在」。
            //   所以先自己讀一次、解析一次：這兩步失敗 ＝ 不知道；過得了才交給 Load 判 kind 與到期。
            if (!SCP.Core.Io.SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out _))
            {
                oDetail = "session 檔讀不出來（`" + aPath + "`，可能正在換檔）⇒ 這是「不知道」不是「不在」";
                return SCP_CanvasTriState.Unknown;
            }
            SCP.Core.Json.SCP_JsonParser.Parse(aText);   // 壞檔 ⇒ 丟例外 ⇒ 下面的 catch 回 Unknown
            var aSession = SCP.Core.Session.SCP_ActivitySessionStore.Load(aRoot, iPersona, SCP.Core.Session.SCP_ActivitySessionKind.FreeTime);
            bool aIn = aSession != null && aSession.IsRunningAt(DateTime.Now, out _);
            oDetail = "來源：session 檔 `" + aPath + "`（kind=FreeTime，active 且未過 end_ts）⇒ " + (aIn ? "在" : "不在");
            return aIn ? SCP_CanvasTriState.Yes : SCP_CanvasTriState.No;
        }
        catch (Exception e)
        {
            // 🩸 這一格是本檔最重要的一行：讀不到就回 Unknown。
            //    回 No 的話呼叫端會去開一場他其實已經在的自由時間，而沒有任何一層會喊。
            oDetail = "session 檔讀不到（" + e.GetType().Name + ": " + e.Message + "）⇒ 這是「不知道」不是「不在」";
            return SCP_CanvasTriState.Unknown;
        }
    }

    // ===========================================================
    // 區塊職責：券的查與扣 —— **直接串 Server 的 `voucher`**（TASK-0243），⛔ 不再派 ucmd 繞 Editor。
    // 物理意義：券已於 2026-09-18 遷進 `letters/<persona>/vouchers/<券名>.json`，
    //          而**寫入端只有 Server**（券不記歷史 ⇒ 那是它成立的唯一前提）。
    // 🩸 為什麼一定要跟著切：遷移那一刻起，舊系統每扣一張券，兩本帳就差一張 ——
    //   而遷移的冪等鍵是**區名**，已經寫進去了 ⇒ **不能靠「再遷一次」把差額補回來**。
    //   ⇒ 消費端不切，差額只會單調變大，而兩邊各自都是合法數字。
    // ⚠ 券名是 `canvas`（＝檔名）—— 與 2026-09-18 那次遷移落的檔同名，⛔ 不另取。
    // ===========================================================
    const string k_CanvasVoucher = "canvas";

    /// <summary>酒館券（個人錢包）的券 id —— ⚠ 與繪圖券是**兩本帳**，名字取自 <see cref="SCP.Core.Bank.SCP_SpendPolicy"/>，⛔ 不在這裡再打一次字面。</summary>
    const string k_TavernVoucher = SCP.Core.Bank.SCP_SpendPolicy.TavernVoucherId;

    string LettersRoot()
        => SCP.Core.Paths.SCP_DataPaths.Letters(new SCP.Core.Paths.SCP_DataRoot(m_DataRoot)).Value;

    // ⚠ `iVoucher` 是**必要的參數不是裝飾** —— 這個 gateway 現在同時經手兩種券，
    //   而「扣錯一本」的失效樣子是兩邊都合法（一本莫名變少、一本莫名沒少）。
    SCP_CmdResult DispatchVoucher(string iOp, Dictionary<string, string> iArgs,
                                  string iVoucher = k_CanvasVoucher)
    {
        iArgs["op"] = iOp;
        iArgs["letters_root"] = LettersRoot();
        iArgs["voucher"] = iVoucher;
        // ⚠ `region` 是寫入時的必填欄（券沒有歷史，它是唯一的「誰動過它」線索）。
        iArgs["region"] = SCP.Core.Bank.SCP_BankRegion.Read(m_DataRoot, out string? _);
        return SCP_CmdRegistry.Dispatch("voucher", iArgs);
    }

    public int QueryExpiringVouchers(string iPersona, out string oDetail)
        => QueryVoucherField(iPersona, "expiring", out oDetail);

    public int QueryPermanentVouchers(string iPersona, out string oDetail)
        => QueryVoucherField(iPersona, "permanent", out oDetail);

    /// <summary>酒館券餘量 —— 讀的是**另一個檔**（`vouchers/tavern.json`），⛔ 不是繪圖券那本。</summary>
    public int QueryTavernVouchers(string iPersona, out string oDetail)
        => QueryVoucherField(iPersona, "spendable", out oDetail, k_TavernVoucher);

    int QueryVoucherField(string iPersona, string iField, out string oDetail,
                          string iVoucher = k_CanvasVoucher)
    {
        // ⛔ 舊版在這裡試三種欄名（Editor 那側欄名沒被驗過）。新的 `voucher` 有**宣告過的**
        //   `permanent` / `expiring` 兩欄 ⇒ 只讀那一個名字；讀不到就是「不知道」，
        //   ⛔ 不再猜第二、第三個名字 —— 猜中了也不知道自己讀的是哪一欄。
        SCP_CmdResult aRes = DispatchVoucher("balance",
            new Dictionary<string, string> { ["persona"] = iPersona }, iVoucher);
        string aEcho = "";
        if (aRes.ExitCode != 0)
        {
            oDetail = "問不到（voucher exit " + aRes.ExitCode + "：" + FirstLineOf(aRes)
                      + "）⇒ -1 是「不知道」不是「沒有券」";
            return -1;
        }
        string aRaw = ValueOf(aRes, iField);
        if (!int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aN))
        {
            oDetail = "voucher 成功而讀不到 `" + iField + "` 那一欄 ⇒ 仍然是「不知道」";
            return -1;
        }
        oDetail = "來源：Cmd voucher（券 `" + iVoucher + "`）的 values 欄 " + iField + "=" + aN + aEcho;
        return aN;
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

    /// <summary>扣**酒館券**（個人錢包）—— 與繪圖券同一支 Cmd、**不同券 id**。</summary>
    public SCP_CanvasGateResult ConsumeTavernVouchers(string iPersona, int iCount, string iSourceRef,
                                                      string iDescription)
        => ConsumeVoucherOf(k_TavernVoucher, "酒館券", iPersona, iCount, iSourceRef, iDescription);

    public SCP_CanvasGateResult ConsumeVouchers(string iPersona, int iCount, string iSourceRef,
                                                string iDescription)
        => ConsumeVoucherOf(k_CanvasVoucher, "繪圖券", iPersona, iCount, iSourceRef, iDescription);

    SCP_CanvasGateResult ConsumeVoucherOf(string iVoucher, string iLabel, string iPersona, int iCount,
                                          string iSourceRef, string iDescription)
    {
        if (iCount <= 0) return SCP_CanvasGateResult.Good("amount<=0，無需消券（不必驚動 Server）");
        // ⚠ 新系統的欄名是 `source` / `ref`，而**帶錯名字的失效樣子換了**：
        //   舊路（Editor 的 CanvasVoucher）會靜默取預設值、券照扣、審計欄留白；
        //   `voucher` 有 ArgSpec 預檢 ⇒ 帶錯會被擋下並說出理由。
        var aArgs = new Dictionary<string, string>
        {
            ["persona"] = iPersona,
            ["amount"] = iCount.ToString(CultureInfo.InvariantCulture),
            ["ref"] = iSourceRef,
            ["source"] = iDescription,
        };
        SCP_CmdResult aRes = DispatchVoucher("consume", aArgs, iVoucher);
        // ⚠ 非零一律當**沒扣成功**：不確定有沒有扣到就當沒扣
        //   （當成扣到了就是白拿像素）。⛔ 而「券不足」也走這一條 —— 它是合法結果，
        //   訊息會說出可花多少、要花多少，呼叫端分得出來。
        if (aRes.ExitCode != 0)
            return SCP_CanvasGateResult.Bad("扣" + iLabel + "沒有成功的收據（voucher exit "
                                            + aRes.ExitCode + "：" + FirstLineOf(aRes) + "）");
        return SCP_CanvasGateResult.Good("扣" + iLabel + " " + iCount + " 張（Server 端 voucher consume）"
                                         + "　可花剩 " + ValueOf(aRes, "spendable"));
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

    // 區塊職責：放點分享（含預覽附件）—— `tavern-post`（Senate 組訊息＋酒館 Server 寫入），⛔ 不再派給 Editor（TASK-0366）。
    // 物理意義：附件**原封不動送絕對路徑**，相對化由 `tavern-post` 對**那個專案的根**做 ——
    //   🩸 2026-09-07 第一版在這裡相對化，整條路掛不上附件：Senate 的 `Program.RepoRoot()` 永遠是 `D:/Unity/Senate`，
    //   而預覽圖住在消費端專案 ⇒ `StartsWith` 永遠不成立。當時的結論是「知道那個根的是 Editor」。
    //   ⭐ TASK-0366 起 `tavern-post` 以 `target_data_root`（＝本閘的資料根）選專案 ⇒ （TASK-0390 起 tavern-post 不再選專案：資料根只有一組，顯示基準是 Senate 專案根）
    //   📌 一般形照舊成立：路徑相對化要在**知道那個根的那一層**做 —— 只是那一層現在在 Senate。
    // 數值影響：`iAttachAbsolutePath` 給 null ⇒ 不帶 refs；`iTag` 給值時掛 `tag`（09-06 之前那批是 `canvas-share`）。
    //          分享失敗**不讓放點失敗** —— 像素已經落盤、錢已經扣了，廣播是 best-effort。
    public SCP_CanvasGateResult Share(string iPersona, string iRoom, string iBody,
                                      string? iAttachAbsolutePath = null, string? iTag = null)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["persona"] = iPersona,
            ["room"] = iRoom,
            ["body"] = iBody,
            ["target_data_root"] = m_DataRoot,
        };
        if (!string.IsNullOrEmpty(iTag)) aArgs["tag"] = iTag!;
        string aAttach = iAttachAbsolutePath ?? "";
        if (aAttach.Length > 0) aArgs["refs"] = aAttach.Replace('\\', '/');

        SCP_CmdResult aPost = SCP_CmdRegistry.Dispatch("tavern-post", aArgs);
        if (aPost.ExitCode != 0)
            return SCP_CanvasGateResult.Bad("分享沒發出去（tavern-post exit " + aPost.ExitCode
                + (aPost.ExitCode == 7 ? "：**不知道**有沒有發，⛔ 別補發" : "") + "：" + FirstLineOf(aPost) + "）—— 像素與帳不受影響");
        string aSeq = ValueOf(aPost, "post_seq");
        return SCP_CanvasGateResult.Good("已發" + (aSeq.Length > 0 ? "（seq " + aSeq + "）" : "")
                                         + (aAttach.Length > 0 ? "，附預覽" : "，無附件"));
    }
}
