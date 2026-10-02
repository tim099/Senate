// 區塊職責：酒館寫入的**唯一共用入口**（CLI 這一側）—— `tavern-write` 送不進 Server 時改成排進它的 queue（TASK-0372）。
// 物理意義：Tim 2026-10-02「排隊」：酒館 Server 停掉那段時間，發文跟發薪一樣先排著，Server 起來後送出。
//           ⇒ 沿用發薪那條（TASK-0297）：`ServerDelegateCmd.TryQueueWithoutWaiting` 把**同一筆** `tavern-write`
//             照正常協議排進酒館 Server 的 queue，它起來後的下一個心跳接手 —— 配號、發薪、@mention、詞典附註
//             全部跟一般發文同一條路（寫入端沒有多一個，⛔ 不另造待補目錄／補送機制）。
//           哪幾種失敗可以排也沿用 `ShouldQueueForLater`（每一種「確定還沒送進 Server」的理由寫在那支的註解）：
//           ⛔ `timeout`／`unknown`（已經在 Server 手上）、`build_mismatch`、`cmd_failed` 照原樣回，不排。
// 數值影響：排隊時只寫酒館 Server 根的 queue／trigger；⛔ 不等 result、**沒有 seq**。
//
// ⚠ 呼叫端一律走這裡，⛔ 不要再直接 `Dispatch("tavern-write")` —— 五個呼叫端各自判「要不要排」，
//   就會有一個忘了排、一個把「已排隊」讀成「確定沒發」然後叫人補發（＝排隊那則送出時多一則、付兩次錢）。
// ⚠ 排隊的那一則，訊息時間戳是**組訊息當下**的（msg_json 已經組好），配號是送出當下的 ⇒ 晚到的那幾則
//   在酒館裡會排在 Server 起來之後的位置、而時間戳比它前面幾則早。那是「晚到不丟」的代價，⛔ 不是亂序 bug。
#nullable enable
using SCP.Core.Cmd;

namespace Senate.Core;

public static class SenateTavernWrite
{
    /// <summary>
    /// 寫一則；送不進 Server 而且「確定還沒送出」⇒ 排進酒館 Server 的 queue。
    /// <para>回傳三種形狀：① 寫入端的原始結果（有 seq ＝ 已發）；② `queued=1` 的結果（exit 0、**沒有 seq**、帶 `queued_cmd_id`）；
    /// ③ 寫入端的原始失敗（不可排、或排也排不進去 —— 後者多一個 `queue_failure`）。</para>
    /// </summary>
    public static SCP_CmdResult WriteOrQueue(Dictionary<string, string> iArgs)
    {
        SCP_CmdResult aWrite = SCP_CmdRegistry.Dispatch("tavern-write", iArgs);
        if (aWrite.Ok && Value(aWrite, "seq").Length > 0) return aWrite;

        string aWhy = Value(aWrite, "delegate_failure");
        if (!ServerDelegateCmd.ShouldQueueForLater(aWhy)) return aWrite;

        if (!ServerDelegateCmd.TryQueueWithoutWaiting("tavern-write", iArgs, out string aCmdId, out string aDetail))
        {
            // 排不進去 ⇒ 仍是「確定沒發」（這一筆從頭到尾沒有進過 Server），照原樣回並說為什麼沒排成。
            aWrite.Lines.Add("⚠ 酒館 Server 不在，想排進它的 queue 也排不進去：" + aDetail);
            aWrite.AddValue("queue_failure", aDetail);
            return aWrite;
        }

        var aQueued = new SCP_CmdResult();
        foreach (string l in aWrite.Lines) aQueued.Lines.Add(l);
        aQueued.Lines.Add($"📥 已排隊（delegate_failure={aWhy}）：{aDetail}　cmd_id={aCmdId}");
        aQueued.Lines.Add("   酒館 Server 起來後的下一個心跳送出（配號＋發薪＋@ 照常）。⛔ **這一則還沒有 seq，不要補發** —— 補了就是兩則。");
        aQueued.AddValue("queued", "1");
        aQueued.AddValue("queued_cmd_id", aCmdId);
        aQueued.AddValue("queued_because", aWhy);
        aQueued.AddValue("delegate_host", "queued");
        return aQueued;
    }

    /// <summary>這個結果是不是「已排隊」（⛔ 不是已發、也不是確定沒發）。</summary>
    public static bool IsQueued(SCP_CmdResult iR) => Value(iR, "queued") == "1";

    public static string Value(SCP_CmdResult iR, string iKey)
        => iR.Values.LastOrDefault(kv => kv.Key == iKey).Value ?? "";
}
