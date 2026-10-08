// 區塊職責：**每日結算的觸發** —— 住在 Senate Server（main 那一顆）的常駐迴圈裡（TASK-0315）。
// 物理意義：UTC 跨日之後跑一次：結帳（`SCP_BankClosing`）→ 保管費扣繳＋轉券＋匯率每日版本（`cmd demurrage op=run`）
//           → 把 Cmd 組好的公告貼上酒館（`cmd tavern-write`，寄件人＝persona `tavern-keeper`）。
// 數值影響：一天一次。狀態檔 `<bank_root>/overnight_job_state.json`（`last_run_date`）；
//           扣繳本身冪等（idem_key／轉券簿），所以「跑兩次」的代價只剩**多貼一則公告**。
// 🩸 守衛：
//   ① **跑在背景執行緒**：扣繳＋抓匯率要十幾秒，擋在服務迴圈上的話心跳會停 ⇒ 別人判成「Server 掛了」。
//   ② **扣繳失敗不推進狀態**（下一輪再試，冪等）；**公告失敗照樣推進**（錢已經照報告動了，重跑扣繳不會補出公告）。
//   ③ **失敗要看得見**：每一步印進 Server log，並寫 `overnight_job_last.md`（最後一趟的完整報告）。
#nullable enable
using System.Globalization;
using SCP.Core.Bank;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace Senate.Core;

public static class SenateOvernightJob
{
    public const string StateFileName = "overnight_job_state.json";
    public const string LastReportFileName = "overnight_job_last.md";

    /// <summary>兩次「到期了沒」檢查的最短間隔。服務迴圈每 1 秒一圈，而這件事一天只會成立一次。</summary>
    static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>扣繳失敗之後隔多久再試（冪等，所以重試安全；隔開是為了不在同一個壞狀態上每 30 秒打一次）。</summary>
    static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    static DateTime s_NextCheckUtc = DateTime.MinValue;
    static Task? s_Running;

    /// <summary>正在跑嗎（Server 收工時要知道 —— 被切斷的那一趟下次啟動會冪等重跑，但要說出來）。</summary>
    public static bool IsRunning => s_Running is { IsCompleted: false };

    /// <summary>服務迴圈每一圈呼叫一次；只做一件很便宜的事（看時間），到期才起背景工作。</summary>
    public static void Tick(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        DateTime aNow = DateTime.UtcNow;
        if (aNow < s_NextCheckUtc || IsRunning) return;
        s_NextCheckUtc = aNow + CheckInterval;

        string aToday = aNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        string? aLast;
        try { aLast = ReadLastRunDate(iDataRoot); }
        catch (Exception e)
        {
            // 狀態讀不了 ⇒ 判不出今天跑過沒 ⇒ **不跑**（判不出來 ≠ 沒跑過；多跑一次會多貼一則公告）。
            iErr($"⚠ 每日結算：狀態檔讀不了（{e.GetType().Name}: {e.Message}）⇒ 這一圈不跑，{CheckInterval.TotalSeconds:0} 秒後再看");
            return;
        }
        if (aLast == aToday) return;

        s_Running = Task.Run(() =>
        {
            try { RunForDay(iDataRoot, aToday, iOut, iErr); }
            catch (Exception e)
            {
                iErr($"🔴 每日結算 {aToday} 整段丟出例外（{e.GetType().Name}: {e.Message}）—— 狀態沒推進，{RetryAfterFailure.TotalMinutes:0} 分後再試");
                s_NextCheckUtc = DateTime.UtcNow + RetryAfterFailure;
            }
        });
    }

    static string BankRoot(string iDataRoot)
    {
        // ⛔ 不留第二個答案（TASK-0390）：原本描述表解不出來時退回自己拼的 `<資料根>/Bank`。版面唯一一處是 SCP_DataPaths。
        return SCP.Core.Bank.SCP_BankRegion.BankRootOfDataRoot(iDataRoot);
    }

    static string StatePath(string iDataRoot) => Path.Combine(BankRoot(iDataRoot), StateFileName);

    static string BartenderDir(string iDataRoot) => SCP.Core.Paths.SCP_DataPaths.Bartender(new SCP.Core.Paths.SCP_DataRoot(iDataRoot));

    /// <summary>讀上次跑的日子。沒有狀態檔 ⇒ null（＝今天要跑）。</summary>
    static string? ReadLastRunDate(string iDataRoot)
    {
        string aPath = StatePath(iDataRoot);
        return File.Exists(aPath) ? SCP_JsonParser.Parse(File.ReadAllText(aPath)).GetString("last_run_date", "") : null;
    }

    static void WriteState(string iDataRoot, string iDate, string iWhy)
    {
        var aJd = SCP_JsonData.NewObject();
        aJd.Set("last_run_date", SCP_JsonData.NewString(iDate));
        aJd.Set("updated_at_utc", SCP_JsonData.NewString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        aJd.Set("why", SCP_JsonData.NewString(iWhy));
        string aPath = StatePath(iDataRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
        string aTmp = aPath + ".tmp";
        File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJd) + "\n");
        File.Move(aTmp, aPath, overwrite: true);
    }

    static void RunForDay(string iDataRoot, string iDay, Action<string> iOut, Action<string> iErr)
    {
        var aReport = new List<string> { $"# 每日結算　`{iDay}`（UTC）　Senate Server pid={Environment.ProcessId}", "" };
        void Say(string iLine) { iOut("· 每日結算：" + iLine); aReport.Add("- " + iLine); }
        void Warn(string iLine) { iErr("每日結算：" + iLine); aReport.Add("- ⚠ " + iLine); }

        string aBank = BankRoot(iDataRoot);

        // ── ① 結帳（失敗不擋扣繳：結帳是加速不是前提）──
        try
        {
            var aProblems = new List<string>();
            int aWritten = SCP_BankClosing.GenerateMissing(aBank, out string aSummary, aProblems);
            Say(aWritten > 0 ? $"結帳：{aSummary}" : "結帳：沒有要補的日子");
            foreach (string p in aProblems) Warn("結帳讀不動：" + p);
        }
        catch (Exception e) { Warn($"結帳失敗（不擋扣繳）：{e.GetType().Name}: {e.Message}"); }

        // ── ② 扣繳＋轉券＋匯率（同一支 Cmd，本 process 內直接執行）───────────
        string aBodyFile = Path.Combine(BartenderDir(iDataRoot), $"demurrage_{iDay}.md");
        SCP_CmdResult aDem = SCP_CmdRegistry.Dispatch("demurrage", new Dictionary<string, string>
        {
            ["op"] = "run",
            ["confirm"] = "1",
            ["date"] = iDay,
            ["data_root"] = iDataRoot,
            ["bank_root"] = aBank,
            ["body_out"] = aBodyFile,
        });
        string V(string iKey) { foreach (var kv in aDem.Values) if (kv.Key == iKey) return kv.Value; return ""; }
        if (!aDem.Ok)
        {
            // 守衛②：⛔ 不推進 —— 這一輪沒有發生，稍後再來（重跑冪等）。
            Warn($"🔴 扣繳失敗（exit {aDem.ExitCode}）⇒ **狀態沒推進**，{RetryAfterFailure.TotalMinutes:0} 分後再試");
            foreach (string l in aDem.Lines) aReport.Add("    " + l);
            s_NextCheckUtc = DateTime.UtcNow + RetryAfterFailure;
            WriteReport(iDataRoot, aReport);
            return;
        }
        Say($"扣繳：{V("accounts_charged")} 戶、合計 {V("total_fee")}；轉券問題 {V("voucher_problems")} 格；匯率 `{V("fx_sync")}` {V("fx_version")}");

        // 錢已經照報告動了 ⇒ 從這一行起**一律推進**（守衛②）。
        WriteState(iDataRoot, iDay, "ran");

        // ── ③ 公告（寄件人＝酒保）─────────
        string aBody;
        try { aBody = File.ReadAllText(aBodyFile); }
        catch (Exception e)
        {
            // ⛔ 不自己編一段：「這一輪發生了什麼」只有 Cmd 知道。
            Warn($"公告本文讀不到（{aBodyFile}：{e.Message}）—— ⚠ 錢**已經扣了**，只是這一則公告沒有發出去");
            WriteReport(iDataRoot, aReport);
            return;
        }
        var aMeta = SCP_JsonData.NewObject();
        aMeta.Set("tag", SCP_JsonData.NewString("bartender-relay"));
        aMeta.Set("subtag", SCP_JsonData.NewString(V("subtag").Length > 0 ? V("subtag") : "overnight-deposit-fee"));
        aMeta.Set("check_date", SCP_JsonData.NewString(iDay));
        foreach (string k in new[] { "total_fee", "central_bank", "central_bank_income", "accounts_charged", "accounts_safe" })
            aMeta.Set(k, SCP_JsonData.NewString(V("meta_" + k)));
        aMeta.Set("fx_sync", SCP_JsonData.NewString(V("fx_sync")));
        aMeta.Set("fx_version", SCP_JsonData.NewString(V("fx_version")));
        aMeta.Set("triggered_by", SCP_JsonData.NewString("senate-server"));
        var aMsg = SCP_JsonData.NewObject();
        aMsg.Set("sender_id", SCP_JsonData.NewString("tavern-keeper"));
        // 顯示名一律是 persona id；酒保就是 persona `tavern-keeper`（Tim 2026-09-28，TASK-0317）。
        aMsg.Set("sender_name", SCP_JsonData.NewString(SCP.Core.Letters.SCP_PersonaDisplay.TavernKeeperPersona));
        aMsg.Set("sender_persona", SCP_JsonData.NewString(SCP.Core.Letters.SCP_PersonaDisplay.TavernKeeperPersona));
        aMsg.Set("kind", SCP_JsonData.NewString("chat"));
        aMsg.Set("body", SCP_JsonData.NewString(aBody));
        aMsg.Set("meta", aMeta);

        // 本工作跑在 **main** 那顆 Server、酒館是**另一顆** ⇒ 酒館不在時跟 CLI 一樣排進它的 queue（TASK-0372）。
        SCP_CmdResult aPost = SenateTavernWrite.WriteOrQueue(new Dictionary<string, string>
        {
            ["data_root"] = iDataRoot,
            ["room"] = "tavern",
            ["msg_json"] = SCP_JsonWriter.Write(aMsg),
        });
        if (SenateTavernWrite.IsQueued(aPost))
            Say($"公告已排隊：酒館 Server 不在（cmd_id {SenateTavernWrite.Value(aPost, "queued_cmd_id")}），起來後送出 —— ⛔ 不要補貼");
        else if (aPost.Ok)
        {
            string aSeq = ""; foreach (var kv in aPost.Values) if (kv.Key == "seq") aSeq = kv.Value;
            Say($"公告已貼：tavern seq {aSeq}");
        }
        else
        {
            Warn($"公告沒貼成（exit {aPost.ExitCode}）—— ⚠ 錢**已經照上面扣了**；⛔ 不重跑扣繳（冪等，但公告不會因此補出來），補貼走 `tavern-write`，本文在 `{aBodyFile}`");
            foreach (string l in aPost.Lines) aReport.Add("    " + l);
        }
        WriteReport(iDataRoot, aReport);
    }

    static void WriteReport(string iDataRoot, List<string> iLines)
    {
        try
        {
            string aPath = Path.Combine(BankRoot(iDataRoot), LastReportFileName);
            File.WriteAllText(aPath, string.Join("\n", iLines) + "\n", new System.Text.UTF8Encoding(false));
        }
        catch (Exception) { /* 報告寫不出去不改變結算結果；Server log 裡已有同一份 */ }
    }
}
