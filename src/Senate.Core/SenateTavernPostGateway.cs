// 區塊職責：Senate 側的**酒館發文閘**（`senate cmd commit` 的公告、小歇廣播走這裡）。
// 物理意義（TASK-0311，epic 0295 ③ 第二刀）：**先走 Senate 那條管線**——
//           `SCP_TavernPostCompose` 組訊息 → 酒館 Server `tavern-write`（配號建檔＋發薪＋@mention）——
//           跟 `senate cmd tavern-post`（TASK-0308）同一條路；前處理全部在 SCP_Core（TASK-0311／0312）。
//           顯示基準是 Senate 專案根（TASK-0390）。
//           ⚠ 本閘不做 alter 配對延遲（呼叫端是公告不是對話，見 `PostViaSenate`）。
//           ⚠ 寫入端只有一個：酒館 Server 的 `tavern-write`（TASK-0341）⇒ 這裡管的是「誰組訊息」，⛔ 不是多開一個寫入端。
// 數值影響：一次 Server round-trip。
//           🩸 逾時是 **`Unresolved`（不知道），不是「沒發」**。TASK-0134 QA（summit 2026-09-05）
//           用一次真的小歇量到：CLI 逾時回報「沒發」，而廣播**其實成功了**（`post_seq 19082`）。
//           ⛔ 兩者處置相反（真沒發要補發／沒等到去補發＝多出第二則，seq 全域遞增），
//           所以它們在回傳型別上就必須不同形，不能靠讀的人自己分辨。
#nullable enable
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace Senate.Core;

public sealed class SenateTavernPostGateway : SCP_ITavernPostGateway
{
    const string Room = "tavern";

    readonly string m_DataRoot;
    readonly Action<string> m_Log;
    readonly double m_TimeoutSec;

    public SenateTavernPostGateway(string iDataRoot, Action<string>? iLog = null, double iTimeoutSec = 30)
    {
        m_DataRoot = iDataRoot;
        m_Log = iLog ?? (_ => { });
        // 30s：ritual 的廣播是 best-effort，⛔ 不該把呼叫者卡到外層 timeout
        //（python 端 rest 的既有值就是 30s，這裡沿用而不是重挑一個）。
        m_TimeoutSec = iTimeoutSec;
    }

    /// <summary>整體定語（兩條路各自在 <c>oLines</c> 再印一行實際走了哪一條）。</summary>
    public string HostQualifier => "⤷ 酒館發文：Senate 組訊息＋酒館 Server 寫入（資料根 " + m_DataRoot + "）";

    string SenateQualifier => "⤷ 酒館發文由 Senate 組訊息、酒館 Server 寫入（資料根 " + m_DataRoot + "）";

    public SCP_TavernPostVerdict Post(string iSenderPersona, string iBody,
                                      IReadOnlyDictionary<string, string> iMeta, List<string> oLines)
    {
        if (string.IsNullOrWhiteSpace(iSenderPersona))
            return SCP_TavernPostVerdict.Bad("沒有 persona ⇒ 不知道要署誰的名（⛔ 不猜）");
        if (string.IsNullOrWhiteSpace(iBody))
            return SCP_TavernPostVerdict.Bad("內文是空的 —— 空訊息會佔一則卻不說話");

        // T06.3 schema：不合就是**確定沒發**，（判準 SCP_TavernMetaSchema）。
        string? aSchema = SCP_TavernMetaSchema.Validate(iMeta);
        if (aSchema != null) return SCP_TavernPostVerdict.Bad("meta 不合 T06.3 schema：" + aSchema);

        // 前處理全部在 Senate 做得完（TASK-0312）。詞典附註的顯示基準是 **Senate 專案根**（詞典是 Senate 的 submodule）。
        if (SenatePathBinding.HostRepoRoot.Length == 0)
            return SCP_TavernPostVerdict.Bad("宿主沒有宣告 Senate 專案根（SenateHostPaths.Install 沒跑）—— 程式錯誤，**確定沒發**");
        return PostViaSenate(iSenderPersona, iBody, iMeta, SenatePathBinding.HostRepoRoot, oLines);
    }

    // ===========================================================
    // 區塊職責：Senate 管線 —— 組訊息 → `tavern-write`。
    // ⚠ 刻意**不做 alter 配對延遲**：本閘的呼叫端是 commit 公告與小歇廣播 —— 那是公告不是對話，
    //   延後它只會讓「commit 已提交」與「公告」脫鉤（呼叫端要的是 seq）。
    // ===========================================================
    SCP_TavernPostVerdict PostViaSenate(string iPersona, string iBody, IReadOnlyDictionary<string, string> iMeta,
                                        string iProjectRoot, List<string> oLines)
    {
        var aRoots = new SCP_MorningRoots
        {
            DataRoot = m_DataRoot.Replace('\\', '/'),
            LettersRoot = SCP_DataPaths.Letters(new SCP_DataRoot(m_DataRoot)).Value,
            ProjectRoot = iProjectRoot.Replace('\\', '/'),
        };
        // 詞典根走 PathsPage 那一格（SCP_PathId.GlossaryRoot）；解不出來 ⇒ 說出來、本次不附詞典（TASK-0390：不猜根）。
        string? aGlossary = SenatePathBinding.ResolveGlossaryRoot(SenateConfigSource.Provider?.Invoke().Item1, out string? aGlossaryErr);
        if (aGlossary != null) aRoots.GlossaryRoot = aGlossary;
        else oLines.Add($"⚠ 詞典根解不出來（{aGlossaryErr}）—— 本次不附詞典附註");
        SCP_TavernPostDraft aDraft = SCP_TavernPostCompose.Build(aRoots.DataRoot, aRoots.LettersRoot, aRoots.ProjectRoot,
            aRoots.GlossaryRoot, aRoots.Region, Room, iPersona, iBody, iMeta);
        foreach (string n in aDraft.Notes) oLines.Add("⚠ " + n);
        if (aDraft.Message == null) return SCP_TavernPostVerdict.Bad("發文被拒：" + aDraft.Error);

        oLines.Add(SenateQualifier);
        // Server 不在而這一筆確定還沒送出 ⇒ 排進它的 queue（TASK-0372），回第四態 Queued（⛔ 不是 Bad：補發會多一則）。
        SCP_CmdResult aWrite = SenateTavernWrite.WriteOrQueue(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["data_root"] = aRoots.DataRoot,
            ["room"] = Room,
            ["msg_json"] = SCP_TavernWriter.Serialize(aDraft.Message),
            ["timeout"] = m_TimeoutSec.ToString("0.###", CultureInfo.InvariantCulture),
        });
        if (SenateTavernWrite.IsQueued(aWrite))
        {
            foreach (string l in aWrite.Lines) oLines.Add("  │ " + l);
            string aCmdId = Value(aWrite, "queued_cmd_id");
            return SCP_TavernPostVerdict.Queued(
                $"酒館 Server 不在（{Value(aWrite, "queued_because")}）⇒ 已排進它的 queue（cmd_id {aCmdId}），起來後送出", aCmdId);
        }
        string aSeq = Value(aWrite, "seq");
        if (aWrite.Ok && aSeq.Length > 0)
        {
            // 發薪／@mention 是寫入端做的 ⇒ 讀數原樣帶回（commit +5 的憑據就在這幾格）。
            foreach (var kv in aWrite.Values)
                if (kv.Key.StartsWith("pay_", StringComparison.Ordinal) || kv.Key.StartsWith("mention_", StringComparison.Ordinal))
                    oLines.Add($"  · {kv.Key} = {kv.Value}");
            return SCP_TavernPostVerdict.Good("seq=" + aSeq, aSeq);
        }

        // 三態：逾時／未知 ＝ 不知道（先回讀，別補發）；其餘 ＝ 確定沒發（`senate cmd tavern-post` 同一判準）。
        string aFailure = Value(aWrite, "delegate_failure");
        foreach (string l in aWrite.Lines) oLines.Add("  │ " + l);
        if (aFailure == "timeout" || aFailure == "unknown")
            return SCP_TavernPostVerdict.Unknown(
                $"酒館寫入**結果不明**（delegate_failure={aFailure}）—— 它可能已經發出去了",
                $"senate cmd tavern-query --arg kind=tail --arg room={Room}"
                + "   # 看得到這一則 ⇒ **發了，別補發**；看不到 ⇒ 才補發");
        return SCP_TavernPostVerdict.Bad(
            $"酒館寫入確定沒發（delegate_failure={(aFailure.Length > 0 ? aFailure : "exit " + aWrite.ExitCode)}）");
    }

    static string Value(SCP_CmdResult iR, string iKey)
    {
        foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
        return "";
    }

}
