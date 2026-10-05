// 區塊職責：酒保的常駐回應 —— 掛在酒館 Server（tavern 那顆）的服務迴圈上（TASK-0365）。
// 物理意義：每 PollIntervalSec 秒看一次 tavern 房有沒有比游標新的訊息；有 ⇒ 丟背景執行緒交給 SenateBartender.ProcessBatch。
//           ⭐ **只回上線之後收到的**（Tim 2026-10-05）：Server 起來的第一輪、或開關從關打開的那一輪，游標設在當時最新一則；
//             游標只活在記憶體裡（⛔ 不存檔），停機期間的訊息不補。
//           ⭐ **整輪都在背景執行緒**（Tim 2026-10-05）：服務迴圈上只判「到時間了沒、上一輪跑完沒」；同一時間只有一輪在跑。
// 數值影響：狀態檔（今天回了幾則／最後一次回覆／錯誤）只在回覆或出錯時寫 —— 不是每則訊息都寫。
// ⚠ 這幾種情況**這一輪不動**並出聲（游標不推 ⇒ 之後補得回來）：
//   狀態檔讀不了（⛔ 不然每日上限會從 0 重算）／別名表讀不了（⛔ 不然 `@酒保` 會被當成沒點名而跳過）／
//   游標後面那一則讀不到（檔被鎖住或壞掉 ⇒ 等它；連續 GapGiveUpRounds 輪都讀不到才跳過那一則，並大聲說）。
// ⚠ 寫不進去 ⇒ 退避（3 秒起跳、每次加倍、最多 5 分鐘），⛔ 不每 3 秒重問一次 LLM。
#nullable enable
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenateBartenderJob
{
    public const int PollIntervalSec = 3;

    public const int GapGiveUpRounds = 20;
    const int MaxBackoffSec = 300;

    static Task? s_Running;
    static int s_BackoffSec;
    static int s_GapSeq = -1, s_GapRounds;
    static int? s_Cursor;           // 處理到哪一則；null ＝ 還沒上線（下一個開著的輪次設成當時最新一則）
    static long s_NextTicks;        // 下一輪最早什麼時候（UTC ticks）；背景執行緒會把它往後推（退避／冷卻）⇒ Volatile 讀寫
    static bool? s_WasEnabled;
    static string s_LastWarn = "";

    public static bool IsRunning => s_Running is { IsCompleted: false };

    /// <summary>
    /// 服務迴圈每一圈呼叫一次。⭐ **這裡只做兩個判斷**（到時間了沒、上一輪跑完沒），其餘全部在背景執行緒 ——
    /// 讀設定／狀態／訊息、問 LLM、寫回覆都不在 Server 迴圈上（Tim 2026-10-05：酒保不可以卡住酒館 Server）。
    /// 用專用的長時間執行緒（LongRunning）：LLM 一次可能等上百秒，⛔ 不佔執行緒池（那會拖慢 Server 其他背景工作）。
    /// </summary>
    public static void Tick(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        long aNow = DateTime.UtcNow.Ticks;
        if (aNow < Volatile.Read(ref s_NextTicks) || IsRunning) return;
        Volatile.Write(ref s_NextTicks, aNow + TimeSpan.FromSeconds(PollIntervalSec).Ticks);
        s_Running = Task.Factory.StartNew(() =>
        {
            DateTime? aRetryAt = Round(iDataRoot, iOut, iErr);
            if (aRetryAt.HasValue && aRetryAt.Value.Ticks > Volatile.Read(ref s_NextTicks)) Volatile.Write(ref s_NextTicks, aRetryAt.Value.Ticks);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>一輪（背景執行緒）：讀設定與狀態、決定游標、有新訊息就處理一批。回「最早什麼時候再試」。</summary>
    static DateTime? Round(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        try
        {
            BartenderSettings s = SenateBartender.LoadSettings(iDataRoot, out string? aSetErr);
            if (aSetErr != null) { WarnOnce(iErr, "酒保：設定檔讀不了 ⇒ 這一輪不動 —— " + aSetErr); return null; }
            if (s_WasEnabled != s.Enabled)
            {
                iOut(s.Enabled ? $"· 酒保：開著（@{SenateBartender.PersonaId}／別名／[help]，每 {PollIntervalSec} 秒看一次 {SenateBartender.Room} 房）"
                               : "· 酒保：關著（打開的那一刻算上線，只回之後的訊息）");
                s_WasEnabled = s.Enabled;
            }
            if (!s.Enabled) { s_Cursor = null; return null; }     // 關著 ⇒ 下次打開算重新上線

            BartenderState st = SenateBartender.LoadState(iDataRoot, out string? aStErr);
            if (aStErr != null) { WarnOnce(iErr, "酒保：狀態檔讀不了 ⇒ 這一輪不動（⛔ 不然每日上限會從 0 重算）—— " + aStErr); return null; }

            List<SCP_TavernMessage> aTail = SCP_TavernRead.Tail(iDataRoot, SenateBartender.Room, 1);
            if (aTail.Count == 0) return null;
            int aTop = aTail[0].Seq;
            if (s_Cursor == null)
            {
                s_Cursor = aTop;
                iOut($"· 酒保：上線，從 {SenateBartender.Room} seq {aTop} 之後開始回（之前的訊息不回）");
                return null;
            }
            int aCur = s_Cursor.Value;
            return aCur >= aTop ? null : RunBatch(iDataRoot, s, st, aCur, aTop, iOut, iErr);
        }
        catch (Exception e)
        {
            WarnOnce(iErr, $"酒保：這一輪炸了（{e.GetType().Name}：{e.Message}）—— 下一輪再試");
            return null;
        }
    }

    /// <summary>處理一批；回「最早什麼時候再試」（null ＝ 照平常的節奏）。</summary>
    static DateTime? RunBatch(string iDataRoot, BartenderSettings s, BartenderState st, int iCur, int iTop, Action<string> iOut, Action<string> iErr)
    {
        try
        {
            Dictionary<string, string> aAliases = SCP_TavernMentionAliases.Load(iDataRoot, out string? aAliasErr);
            if (aAliasErr != null) { WarnOnce(iErr, "酒保：別名表讀不了 ⇒ 這一輪不動（⛔ 不然 @別名 會被當成沒點名而跳過）—— " + aAliasErr); return null; }

            int aTo = Math.Min(iTop, iCur + SenateBartender.BatchSize);
            List<SCP_TavernMessage> aRead = SCP_TavernRead.Range(iDataRoot, SenateBartender.Room, iCur + 1, aTo).OrderBy(m => m.Seq).ToList();
            // 只收從游標起連續的那一段：中間有一則讀不到（被鎖住／壞掉）⇒ 停在它前面，⛔ 不讓後面的把它跳過去
            var aBatch = new List<SCP_TavernMessage>();
            foreach (SCP_TavernMessage m in aRead) { if (m.Seq != iCur + 1 + aBatch.Count) break; aBatch.Add(m); }
            if (aBatch.Count == 0)
            {
                int aMissing = iCur + 1;
                if (s_GapSeq != aMissing) { s_GapSeq = aMissing; s_GapRounds = 0; }
                if (++s_GapRounds < GapGiveUpRounds) { WarnOnce(iErr, $"酒保：{SenateBartender.Room} seq {aMissing} 讀不到 ⇒ 等它（第 {s_GapRounds} 輪）"); return null; }
                s_Cursor = aMissing;
                iErr($"⚠ 酒保：{SenateBartender.Room} seq {aMissing} 連續 {GapGiveUpRounds} 輪都讀不到 ⇒ **跳過這一則**（如果它有 @ 酒保，這則不會被回）");
                s_GapSeq = -1;
                return null;
            }
            s_GapSeq = -1;

            SenateBartender.BatchOutcome o = SenateBartender.ProcessBatch(s, st, SenateBartender.Room, aBatch, aAliases, iCur,
                (sys, prompt) =>
                {
                    Cmd_Llm.TestResult t = Cmd_Llm.Chat(s.ModelId, prompt, sys, s.Think, s.NumPredict, s.KeepAliveSeconds, s.TimeoutSeconds, CancellationToken.None);
                    return (t.ok, t.output, s.ModelId, t.error.Length > 0 ? t.error : t.note);
                },
                msg =>
                {
                    SCP_CmdResult w = SCP_CmdRegistry.Dispatch("tavern-write", new Dictionary<string, string>
                    {
                        ["data_root"] = iDataRoot, ["room"] = SenateBartender.Room, ["msg_json"] = SCP_JsonWriter.Write(msg),
                    });
                    string aSeq = w.Values.FirstOrDefault(kv => kv.Key == "seq").Value ?? "";
                    return (w.Ok, int.TryParse(aSeq, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : 0,
                            w.Ok ? "" : $"exit {w.ExitCode}：{string.Join(" ", w.Lines.Take(2))}");
                },
                () => DateTime.UtcNow);
            if (s_Cursor == iCur) s_Cursor = o.Cursor;    // 期間被關掉又打開（s_Cursor 被重設）⇒ 不覆蓋
            if (o.Changed) SenateBartender.SaveState(iDataRoot, o.State);
            if (o.StoppedOnWriteFailure)
            {
                s_BackoffSec = Math.Min(MaxBackoffSec, s_BackoffSec == 0 ? PollIntervalSec : s_BackoffSec * 2);
                foreach (string l in o.Log.Take(o.Log.Count - 1)) iOut("· 酒保：" + l);
                WarnOnce(iErr, "酒保：" + o.Log.LastOrDefault() + $"（{s_BackoffSec} 秒後再試）");
                return DateTime.UtcNow.AddSeconds(s_BackoffSec);
            }
            s_BackoffSec = 0;
            foreach (string l in o.Log) iOut("· 酒保：" + l);
            return o.DeferredUntilUtc;
        }
        catch (Exception e)
        {
            s_BackoffSec = Math.Min(MaxBackoffSec, s_BackoffSec == 0 ? PollIntervalSec : s_BackoffSec * 2);
            WarnOnce(iErr, $"酒保：這一批炸了（{e.GetType().Name}：{e.Message}）—— 游標沒動，{s_BackoffSec} 秒後再試");
            return DateTime.UtcNow.AddSeconds(s_BackoffSec);
        }
    }

    static void WarnOnce(Action<string> iErr, string iMsg)
    {
        if (iMsg == s_LastWarn) return;
        s_LastWarn = iMsg;
        iErr("⚠ " + iMsg);
    }
}
