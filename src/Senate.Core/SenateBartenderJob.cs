// 區塊職責：酒保的常駐回應 —— 掛在酒館 Server（tavern 那顆）的服務迴圈上（TASK-0365）。
// 物理意義：每 PollIntervalSec 秒看一次 tavern 房有沒有比游標新的訊息；有 ⇒ 丟背景執行緒交給 SenateBartender.ProcessBatch。
//           LLM 生成可能要幾十秒 ⇒ ⛔ 不在服務迴圈上跑（否則卡住心跳與整個酒館的寫入）；同一時間只有一批在跑。
// 數值影響：關著時游標照樣跟上最新一則（⇒ 打開的那一刻不會回頭回一整串舊訊息）；
//           Server 停掉期間寫進來的訊息不受影響 —— 重啟後照游標補處理（驗收⑤）。
//           第一次跑（還沒有游標）⇒ 從現在開始，歷史不回放。
// ⚠ 這幾種情況**這一輪不動**並出聲（游標不推 ⇒ 之後補得回來）：
//   狀態檔讀不了（⛔ 不把游標當成 0，那會把整個房間的歷史再回一遍）／別名表讀不了（⛔ 不然 `@酒保` 會被當成沒點名而跳過）／
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
    static DateTime s_NextUtc = DateTime.MinValue;
    static bool? s_WasEnabled;
    static string s_LastWarn = "";

    public static bool IsRunning => s_Running is { IsCompleted: false };

    public static void Tick(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        DateTime aNow = DateTime.UtcNow;
        if (aNow < s_NextUtc || IsRunning) return;
        s_NextUtc = aNow.AddSeconds(PollIntervalSec);

        BartenderSettings s = SenateBartender.LoadSettings(iDataRoot, out string? aSetErr);
        if (aSetErr != null) { WarnOnce(iErr, "酒保：設定檔讀不了 ⇒ 這一輪不動 —— " + aSetErr); return; }
        if (s_WasEnabled != s.Enabled)
        {
            iOut(s.Enabled ? $"· 酒保：開著（@{SenateBartender.PersonaId}／別名／[help]，每 {PollIntervalSec} 秒看一次 {SenateBartender.Room} 房）"
                           : "· 酒保：關著（游標照樣跟上最新一則，打開時不回頭回舊訊息）");
            s_WasEnabled = s.Enabled;
        }

        BartenderState st = SenateBartender.LoadState(iDataRoot, out string? aStErr);
        if (aStErr != null) { WarnOnce(iErr, "酒保：狀態檔讀不了 ⇒ 這一輪不動（⛔ 不把游標當成 0）—— " + aStErr); return; }

        List<SCP_TavernMessage> aTail = SCP_TavernRead.Tail(iDataRoot, SenateBartender.Room, 1);
        if (aTail.Count == 0) return;
        int aTop = aTail[0].Seq;
        bool aHasCursor = st.Cursor.TryGetValue(SenateBartender.Room, out int aCur);
        if (!aHasCursor || !s.Enabled)
        {
            if (aHasCursor && aCur >= aTop) return;
            st.Cursor[SenateBartender.Room] = aTop;
            SenateBartender.SaveState(iDataRoot, st);
            if (!aHasCursor) iOut($"· 酒保：建立起點 {SenateBartender.Room} seq {aTop}（歷史不回放）");
            return;
        }
        if (aCur >= aTop) return;
        s_Running = Task.Run(() =>
        {
            DateTime? aRetryAt = RunBatch(iDataRoot, s, st, aCur, aTop, iOut, iErr);
            if (aRetryAt.HasValue && aRetryAt.Value > s_NextUtc) s_NextUtc = aRetryAt.Value;
        });
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
                st.Cursor[SenateBartender.Room] = aMissing;
                SenateBartender.SaveState(iDataRoot, st);
                iErr($"⚠ 酒保：{SenateBartender.Room} seq {aMissing} 連續 {GapGiveUpRounds} 輪都讀不到 ⇒ **跳過這一則**（如果它有 @ 酒保，這則不會被回）");
                s_GapSeq = -1;
                return null;
            }
            s_GapSeq = -1;
            // 已經回過哪些：回覆一定在游標之後 ⇒ 掃游標到最新一則（精確），再加最後幾則保底
            var aRecent = SCP_TavernRead.Range(iDataRoot, SenateBartender.Room, iCur + 1, iTop);
            aRecent.AddRange(SCP_TavernRead.Tail(iDataRoot, SenateBartender.Room, SenateBartender.DedupeLookback));
            HashSet<int> aReplied = SenateBartender.AlreadyReplied(aRecent);

            SenateBartender.BatchOutcome o = SenateBartender.ProcessBatch(s, st, SenateBartender.Room, aBatch, aAliases, aReplied,
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
