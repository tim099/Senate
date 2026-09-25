// 區塊職責：`SCP_IChessGateway` 的 **Senate 端實作** —— 棋局本體要的兩格宿主能力（廣播、發券）。
// 物理意義：TASK-0268 ⑥ —— `chess.py` 原本 `subprocess` 叫 `senate.exe ucmd run Tavern`；
//           搬進 C# 之後在本 process 內直接派：
//           · 廣播 ＝ `AgentCmdClient` 把 `Tavern op=post` 派給 Editor（同 `SenateCanvasGateway` 的分享那一格）
//           · 發券 ＝ `SCP_CmdRegistry.Dispatch("voucher")`（同 `SenateBooksGateway.GrantVoucher`）
// 數值影響：廣播落 `queues/<persona>/queue-chess-<n>.json`（每局一條子分道）；券寫 `letters/<persona>/vouchers/canvas.json`。
//
// 🩸 判準：
//   ① **身分＝下棋的 persona，通道＝棋局編號**（Tim 2026-08-01 persona 資料夾制，照 python 那段註解）。
//      `<persona>/chess-<n>` 是 lane 不是身分 —— ⛔ 不可以改成 `chess-N` 當 persona（那會長出
//      `queues/chess-1/`，而**棋局不是人**）。沒有 persona（系統代發）⇒ 不帶身分，落 anonymous。
//   ② **刻意不帶 sender_id** —— 顯示身分由 Cmd_Tavern 從 persona 推導
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

    // 廣播要等 Editor 把那一則寫完才算數 —— python 那側給的是 80s（`--timeout 80`），照抄。
    const double k_BroadcastTimeoutSec = 80;

    public SenateChessGateway(string iDataRoot) { m_DataRoot = iDataRoot; }

    public string HostQualifier
        => "⤷ 棋局由 senate 本地跑／廣播派給 Unity Editor（Cmd_Tavern）／券由 Senate Server 發（`voucher`）";

    string LettersRoot()
        => SCP.Core.Paths.SCP_DataPaths.Letters(new SCP.Core.Paths.SCP_DataRoot(m_DataRoot)).Value;

    public bool Broadcast(string? iSenderPersona, string iLane, string iBody, string iMetaJson, out string oDetail)
    {
        oDetail = "";
        var aArgs = new Dictionary<string, string>
        {
            ["op"] = "post",
            ["room"] = "tavern",
            ["body"] = iBody,
            ["meta"] = iMetaJson,
        };
        if (!string.IsNullOrEmpty(iSenderPersona)) aArgs["persona"] = iSenderPersona;
        // 判準①：`<persona>/<lane>` ⇒ `queues/<persona>/queue-<lane>.json`；沒有 persona 就不帶身分。
        string? aRoute = string.IsNullOrEmpty(iSenderPersona) ? null : iSenderPersona + "/" + iLane;
        try
        {
            if (!AgentCmdClient.EnsureIdle(m_DataRoot, aRoute, 10, _ => { }, out string aIdleWhy))
            {
                oDetail = "前一筆廣播還卡在同一條 lane：" + aIdleWhy;
                return false;
            }
            string aCmdId = AgentCmdClient.Submit(m_DataRoot, aRoute, "Tavern", aArgs, _ => { });
            AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(m_DataRoot, aRoute, aCmdId,
                k_BroadcastTimeoutSec, AgentCmdClient.DefaultPollSec, _ => { }, _ => { }, iPrintOutputs: false);
            if (aVerdict == AgentCmdWaitResult.Success)
            {
                oDetail = "cmd_id=" + aCmdId;
                return true;
            }
            // ⛔ 不在這裡猜成因 —— 逾時的成因只有一個地方量（AgentCmdClient.DescribeWaitTimeout）。
            oDetail = aVerdict.IsIndeterminate()
                ? AgentCmdClient.DescribeWaitTimeout(m_DataRoot, aRoute, aCmdId, k_BroadcastTimeoutSec)
                  + "　⚠ 逾時是「不知道」不是「沒送出」—— 先看那條分道的 trigger 再決定要不要重發"
                : "Editor 端回報失敗（cmd_id=" + aCmdId + "）";
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
