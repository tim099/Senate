// 區塊職責：`SCP_IChessGateway` 的 **Senate 端實作** —— 棋局本體要的兩格宿主能力（廣播、發券）。
// 物理意義：TASK-0268 ⑥ —— 在本 process 內直接派：
//           · 廣播 ＝ `tavern-post`／`tavern-post-system`（TASK-0366；同 `SenateCanvasGateway` 的分享那一格）
//           · 發券 ＝ `SCP_CmdRegistry.Dispatch("voucher")`（同 `SenateBooksGateway.GrantVoucher`）
// 數值影響：廣播寫一則酒館訊息（經酒館 Server）；券寫 `letters/<persona>/vouchers/canvas.json`。
//
// 🩸 判準：
//   ① **身分＝下棋的 persona，通道＝棋局編號**（Tim 2026-08-01 persona 資料夾制，照 python 那段註解）。
//      `<persona>/chess-<n>` 是 lane 不是身分 —— ⛔ 不可以改成 `chess-N` 當 persona（那會長出
//      `queues/chess-1/`，而**棋局不是人**）。沒有 persona（系統代發）⇒ 以 `tavern-keeper` 系統發言（不計酬）。
//   ② **刻意不帶 sender_id** —— 顯示身分由 `SCP_TavernPostCompose` 從 persona 推導
//      （2026-08-20 summit 血證：顯式帶 sender_id 會繞過 BUG-22 的修法，同一分鐘兩個署名）。
//   ③ 失敗**回 false ＋ 理由**，⛔ 不丟例外：棋步已落盤，廣播／發券是 best-effort，由本體印出來。
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Bank;
using SCP.Core.Chess;
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class SenateChessGateway : SCP_IChessGateway
{
    readonly string m_DataRoot;

    // 廣播要等寫入端把那一則寫完才算數 —— 上限 80 秒。
    const double k_BroadcastTimeoutSec = 80;

    public SenateChessGateway(string iDataRoot) { m_DataRoot = iDataRoot; }

    public string HostQualifier
        => "⤷ 棋局由 senate 本地跑／廣播走 tavern-post（Senate 組訊息＋酒館 Server 寫入）／券由 Senate Server 發（`voucher`）";

    string LettersRoot()
        => SCP.Core.Paths.SCP_DataPaths.Letters(new SCP.Core.Paths.SCP_DataRoot(m_DataRoot)).Value;

    // 區塊職責：棋局廣播 —— `tavern-post`（有 persona）／`tavern-post-system`（系統代發）（TASK-0366）。
    // 物理意義：`iLane`（`chess-<n>`）是檔案協議的子分道（同一人兩盤棋的廣播不互相排隊）；
    //          就地呼叫不經 queue ⇒ 沒有分道可排，收下不用（⛔ 不假造一條）。
    // 數值影響：exit 7（不知道有沒有發）照實回 false ＋「別重發」—— 棋步已落盤，廣播是 best-effort。
    public bool Broadcast(string? iSenderPersona, string iLane, string iBody, string iMetaJson, out string oDetail)
    {
        bool aSystem = string.IsNullOrEmpty(iSenderPersona);
        var aArgs = new Dictionary<string, string>
        {
            ["room"] = "tavern",
            ["body"] = iBody,
            ["meta"] = iMetaJson,
            ["target_data_root"] = m_DataRoot,
            ["timeout"] = k_BroadcastTimeoutSec.ToString(CultureInfo.InvariantCulture),
        };
        if (aSystem) aArgs["sender"] = "tavern-keeper"; else aArgs["persona"] = iSenderPersona!;
        try
        {
            SCP_CmdResult aPost = SCP_CmdRegistry.Dispatch(aSystem ? "tavern-post-system" : "tavern-post", aArgs);
            string aSeq = "";
            foreach (KeyValuePair<string, string> kv in aPost.Values) if (kv.Key == "post_seq") aSeq = kv.Value;
            if (aPost.ExitCode == 0)
            {
                oDetail = aSeq.Length > 0 ? "seq " + aSeq : "已排程（alter 延後，到點由酒館 Server 發）";
                return true;
            }
            oDetail = "tavern-post exit " + aPost.ExitCode + "：" + Reason(aPost)
                      + (aPost.ExitCode == 7 ? "　⚠ 這是「不知道」不是「沒送出」—— 先 tavern-query 回讀，⛔ 別重發" : "");
            return false;
        }
        catch (Exception e)
        {
            oDetail = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    public int? GrantCanvasVoucher(string iPersona, int iAmount, string iSource, string iRef, out string oDetail)
    {
        oDetail = "";
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "grant",
            ["letters_root"] = LettersRoot(),
            ["persona"] = iPersona,
            ["voucher"] = "canvas",
            ["amount"] = iAmount.ToString(CultureInfo.InvariantCulture),
            // ⚠ 券沒有歷史 ⇒ `region` 是唯一的「誰動過它」線索，寫入時必填。
            ["region"] = SCP_BankRegion.Read(m_DataRoot, out string? _),
            ["source"] = iSource ?? "",
            ["ref"] = iRef ?? "",
        };
        SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("voucher", aArgs);
        if (aRes.ExitCode != 0)
        {
            oDetail = $"voucher exit {aRes.ExitCode}：{Reason(aRes)}";
            return null;
        }
        // 讀回落地後的餘額 —— 印 ✓ 不算數，讀回來才算。⚠ 讀不到那一欄 ≠ 餘額 0 ⇒ 回 -1。
        foreach (KeyValuePair<string, string> aKv in aRes.Values)
            if (aKv.Key == "spendable" && int.TryParse(aKv.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aBal))
                return aBal;
        oDetail = "發券成功但回傳沒有 spendable 那一欄";
        return -1;
    }

    static string Reason(SCP_CmdResult iResult)
    {
        foreach (string aLine in iResult.Lines)
            if (aLine.IndexOf('✗') >= 0) return aLine.Trim();
        foreach (string aLine in iResult.Lines)
            if (!string.IsNullOrWhiteSpace(aLine)) return aLine.Trim();
        return "(沒有理由那一行)";
    }
}
