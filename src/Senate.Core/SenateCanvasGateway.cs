// 區塊職責：畫布閘的 **CLI／Server 實作** —— 付款、自由時間資格、分享全部派給 Unity Editor。
// 物理意義：這三件事的權威實作只有 Editor 那側有（券／token 的 canonical ledger、
//           UCL_SessionService、酒館 seq 分配）。Tim 2026-09-03 拍板：**內部串 ucmd，不移植**
//           ⇒ 這裡不重寫帳本，只把問題送過去、把答案讀回來。
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

    public string HostQualifier
        => $"⤷ 錢與資格由 Unity Editor 執行 @ {m_ProjectLabel}（{m_DataRoot}）";

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
        var aArgs = new Dictionary<string, string> { ["op"] = "balance", ["persona"] = iPersona };
        if (!TryRun("CanvasVoucher", iPersona, aArgs, m_QueryTimeoutSec,
                    out List<KeyValuePair<string, string>> aValues, out string aWhy))
        {
            oDetail = "問不到（" + aWhy + "）⇒ -1 是「不知道」不是「沒有券」";
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
        oDetail = "來源：Cmd CanvasVoucher 的 values 欄 " + iField + "=" + aN;
        return aN;
    }

    public long QueryTokenBalance(string iAccountId, out string oDetail)
    {
        var aArgs = new Dictionary<string, string> { ["op"] = "balance", ["account"] = iAccountId };
        if (!TryRun("Treasury", null, aArgs, m_QueryTimeoutSec,
                    out List<KeyValuePair<string, string>> aValues, out string aWhy))
        {
            oDetail = "問不到（" + aWhy + "）⇒ -1 是「不知道」，**不是 0**（0 是查到了沒錢）";
            return -1;
        }
        string aRaw = Value(aValues, "balance");
        if (!long.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long aBalance))
        {
            oDetail = "Cmd 成功但讀不到 balance 欄 ⇒ 仍然是「不知道」";
            return -1;
        }
        oDetail = "來源：Cmd Treasury 的 values 欄 balance=" + aBalance;
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
        if (iAmount <= 0) return SCP_CanvasGateResult.Good("amount<=0，無需扣款（不必打擾 Editor）");
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "debit",
            ["account"] = iAccountId,
            ["amount"] = iAmount.ToString(CultureInfo.InvariantCulture),
            ["currency"] = "tavern_token",
            // ⚠ debit 讀的是 use_kind / use_ref（**credit 才是 source_kind / source_ref**）——
            //   兩支同一個檔、名字差一個字，而帶錯的那次不會報錯：審計欄留白，錢照扣。
            ["use_kind"] = iSourceKind,
            ["use_ref"] = iSourceRef,
            ["description"] = iDescription,
            // 🩸 caller 必須是**帳戶本人**（或 "system"）：UCL_TreasuryLedger 有帳戶隔離鐵律，
            //   caller 非 system 且 != account 就拋例外「不可動用對方帳戶」，
            //   而那個錯誤訊息長得像帳本壞了。語意上這裡就是「該帳戶花自己的錢」。
            ["caller"] = iAccountId,
        };
        if (!TryRun("Treasury", null, aArgs, AgentCmdClient.DefaultWaitTimeoutSec,
                    out _, out string aWhy))
            return SCP_CanvasGateResult.Bad("扣 token 沒有成功的收據（" + aWhy + "）");
        return SCP_CanvasGateResult.Good("扣 " + iAmount + " token（Editor 端 Treasury debit）");
    }

    public SCP_CanvasGateResult Share(string iPersona, string iRoom, string iBody)
    {
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "post",
            ["room"] = iRoom,
            ["body"] = iBody,
            ["persona"] = iPersona,
        };
        if (!TryRun("Tavern", iPersona, aArgs, AgentCmdClient.DefaultWaitTimeoutSec,
                    out List<KeyValuePair<string, string>> aValues, out string aWhy))
            // 分享失敗**不該讓放點失敗** —— 像素已經落盤、錢已經扣了，廣播是 best-effort。
            return SCP_CanvasGateResult.Bad("分享沒發出去（" + aWhy + "）—— 像素與帳不受影響");
        string aSeq = Value(aValues, "post_seq");
        return SCP_CanvasGateResult.Good("已發" + (aSeq.Length > 0 ? "（seq " + aSeq + "）" : ""));
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
                oWhy = aVerdict == AgentCmdWaitResult.Timeout
                    ? "逾時 " + iTimeoutSec.ToString("0", CultureInfo.InvariantCulture) + "s —— Editor 沒開？"
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
