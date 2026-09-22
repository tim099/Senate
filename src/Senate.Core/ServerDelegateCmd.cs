// 區塊職責：**委派給 Senate Server 的 SCP_Cmd 基底** —— 同一個類別兩條路：
//           在 Server 裡被派到就跑本體（ExecuteOnServer），在 CLI 裡被打到就派出去等結果。
// 物理意義：TASK-0103。Server 存在的理由是「只有一顆 process 在寫」（D20），所以會寫共用狀態的 Cmd
//           **不准**在 CLI process 裡直接跑 —— 那會長出第二個寫入者，而兩個寫入者的輸出長得一模一樣。
//           ⇒ 路由由 <see cref="ServerContext.InServer"/> 決定，不是由呼叫端記得。
//           跟 UnityDelegateCmd 是同族（另一個宿主的委派），刻意**不共用基底**：那邊的目標是「某個專案的
//           資料根」、要解析 project；這邊的目標是 Senate 自己的 Server 根，沒有 project 這一格。
//           兩者共用的是**協議**（AgentCmdClient）與**回報**（AppendReport／DescribeStamp），不是類別階層。
// 數值影響：CLI 路徑寫 Server 根的 queue/trigger、等 result 檔；exit 0 成功／1 Server 端回報失敗／
//           3 沒有結果（not_running／build_mismatch／queue_busy／timeout，細分走 🔢 delegate_failure）。
//           ⛔ Server 沒在跑**不降級成本地跑**（Tim 2026-09-02 ⑦）—— 印怎麼啟動，exit 3，到此為止。
using SCP.Core.Cmd;

namespace Senate.Core;

/// <summary>
/// 「我現在是不是 Server」的 process 全域旗標 —— 由 <see cref="ServerHost.RunForeground"/> 在啟動時設。
/// <para>⚠ 它是全域的，而且只該被設一次：Server 是一顆 process 一個身分，不是一個 thread 一個身分。</para>
/// </summary>
public static class ServerContext
{
    public static bool InServer;
    public static int Pid;
    public static string BuildId = "";

    /// <summary>執行位置的定語（每一則 Server 回報的第一行）。</summary>
    public static string Describe() => $"pid={Pid} build={BuildId}";
}

public abstract class ServerDelegateCmd : SCP_Cmd
{
    /// <summary>
    /// repo 根的來源。**由宿主在啟動時裝上**（跟 <see cref="UnityDelegateCmd.ConfigProvider"/> 同形）——
    /// Cmd 不知道 Server 根在哪，本層不推導。沒裝上一律 fail loud。
    /// </summary>
    public static Func<string>? RepoRootProvider;

    /// <summary>Server 端的 lane 上限 —— 同 lane 串行、跨 lane 並行（照 Editor Runner 的形狀）。</summary>
    public const string DefaultLane = "server";

    public sealed override SCP_CmdPortStatus PortStatus => SCP_CmdPortStatus.DelegatedToServer;

    /// <summary>
    /// 這支 Cmd 由**哪一顆** Server 服務（TASK-0244）。
    /// <para>預設 `main`（銀行與通用委派都在它身上）。
    /// 酒館那支之後 override 成 <see cref="ServerIds.Tavern"/> ——
    /// ⭐ 這一格就是「分開動工」那條拍板的落點：
    /// 改這一行就招呼到另一顆，⛔ 不用動任何呼叫端。</para>
    /// </summary>
    protected virtual string ServerId => ServerIds.Default;

    /// <summary>本體 —— **只在 Server process 裡被呼叫**。這裡可以放心當作「我是唯一寫入者」。</summary>
    protected abstract SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs);

    /// <summary>
    /// 走哪條 queue 分道。預設：有宣告 <c>persona</c> 就用它，沒有就走 <see cref="DefaultLane"/>。
    /// <para>⚠ 不走 anonymous：那個名字在這套系統裡是症狀（全員互相阻塞的那一道）。
    /// Server 是 Senate 自己的根，沒有 persona 的 Cmd 用一條具名公用分道，出事至少看得出是哪一條。</para>
    /// </summary>
    protected virtual string Lane(SCP_CmdArgs iArgs)
    {
        if (DeclaresArg("persona"))
        {
            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length > 0) return aPersona;
        }
        return DefaultLane;
    }

    /// <summary>每一支委派 Cmd 都有的參數。子類別把自己的接在後面。</summary>
    protected static IEnumerable<SCP_CmdArgSpec> CommonSpecs()
    {
        yield return new SCP_CmdArgSpec("timeout",
            "等 Server 回應的秒數", iDefault: ((int)AgentCmdClient.DefaultWaitTimeoutSec).ToString());
    }

    protected bool DeclaresArg(string iName)
    {
        foreach (SCP_CmdArgSpec aSpec in ArgSpecs) if (aSpec.Name == iName) return true;
        return false;
    }

    public sealed override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        if (ServerContext.InServer)
        {
            SCP_CmdResult aServerResult = ExecuteOnServer(iArgs);
            // 定語第一行：這一則是 Server 跑的。⚠ 插在最前面 —— 失敗訊息也要帶著它。
            aServerResult.Lines.Insert(0, $"⤷ 於 senate server 執行 @ {ServerContext.Describe()}");
            aServerResult.AddValue("delegate_host", "server");
            aServerResult.AddValue("server_pid", ServerContext.Pid.ToString());
            aServerResult.AddValue("server_build", ServerContext.BuildId);
            return aServerResult;
        }

        var aResult = new SCP_CmdResult();
        if (RepoRootProvider == null)
            return SCP_CmdResult.Fail(70,
                "✗ 宿主沒有裝上 repo 根來源（ServerDelegateCmd.RepoRootProvider）——",
                "  這是程式錯誤不是用法錯：委派需要知道 Server 根在哪，而本層不推導路徑。");
        string aRepoRoot = RepoRootProvider();
        string aServerId = ServerIds.Normalize(ServerId);
        string aServerRoot = SenatePaths.ServerRoot(aRepoRoot, aServerId);
        // ⚠ 報告路徑那邊要用它去找對樹（Program.cs）——少了這一值，錯誤報告會指到另一顆的根。
        aResult.AddValue("server_id", aServerId);

        // ① Server 在不在、是不是同一顆 exe。
        //    沒在跑 ⇒ **自動拉一顆起來**（TASK-0209 A4，Tim 2026-09-14 翻掉 D20 ⑦ 的「手動」那半）。
        //    ⛔ 而「不降級」那半**沒有翻**：拉不起來仍然 exit 3，絕不改成本地跑
        //      —— 本地跑就是第二個寫入者，而它的輸出跟 Server 跑的一模一樣。
        ServerStatus aStatus = ServerHost.Probe(aRepoRoot, aServerId);
        if (!aStatus.IsRunning)
        {
            ServerAutoStartReport aAuto = ServerAutoStart.Ensure(aRepoRoot, aServerId, iLine => aResult.Lines.Add(iLine));
            if (!aAuto.Ok)
            {
                aResult.ExitCode = 3;
                aResult.AddValue("delegate_host", "server");
                // ⚠ 兩個值刻意不同：「還沒好」與「起不來」處置相反（再等 ／ 去看 log），
                //   共用一個值就是把兩種相反的處置塞進同一個出口。
                aResult.AddValue("delegate_failure",
                                 aAuto.Outcome == ServerAutoStartOutcome.TimedOut
                                     ? "autostart_timeout" : "autostart_failed");
                if (aAuto.LogPath != null) aResult.AddValue("server_start_log", aAuto.LogPath);
                ServerAutoStart.Explain(aAuto, aResult.Lines);
                aResult.Lines.Add("  ⛔ 不會改成本地跑：本地跑就是第二個寫入者，而它的輸出跟 Server 跑的一模一樣。");
                return aResult;
            }
            if (aAuto.Outcome == ServerAutoStartOutcome.Started)
                aResult.Lines.Add($"⤷ Server 自動啟動完成（{aAuto.Detail}）");
            // 起來了 ⇒ 重取一次讀數。⛔ 不沿用上面那份：那是「還沒起來」時量的，
            //   拿它去填 pid／build 會印出一份**格式完整而內容過期**的定語。
            aStatus = ServerHost.Probe(aRepoRoot, aServerId);
            if (!aStatus.IsRunning)
            {
                aResult.ExitCode = 3;
                aResult.AddValue("delegate_host", "server");
                aResult.AddValue("delegate_failure", "not_running");
                aResult.Lines.Add("✗ 自動啟動回報成功，但重取讀數時 Server 又不在了 —— 這一筆**沒有送出**。");
                aResult.Lines.Add("  ⚠ 它起來又馬上退了（看啟動 log），或有人同時 stop 了它。");
                return aResult;
            }
        }
        aResult.Lines.Add($"⤷ 由 senate server 執行 @ pid={aStatus.Alive!.Pid} build={aStatus.Heartbeat?.BuildId ?? "?"}");
        aResult.AddValue("delegate_host", "server");
        aResult.AddValue("server_pid", aStatus.Alive.Pid.ToString());
        if (!aStatus.BuildMatches)
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "build_mismatch");
            aResult.Lines.Add($"✗ 版本不符：Server build={aStatus.Heartbeat?.BuildId ?? "?"}，本 CLI build={aStatus.MyBuildId} —— 這一筆**沒有送出**。");
            aResult.Lines.Add("  出口：`senate server stop` 再 `senate server start`（讓新 exe 的那顆來跑）。");
            return aResult;
        }

        string aLane = Lane(iArgs);
        aResult.AddValue("queue_lane", aLane);
        double aTimeout = ParseTimeout(iArgs, aResult);

        // ② 前一筆還卡著就別送 —— 送了會排在後面，然後兩筆一起逾時。
        if (!AgentCmdClient.EnsureIdle(aServerRoot, aLane, Math.Min(aTimeout, 30), aResult.Lines.Add, out string aWhy))
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "queue_busy");
            aResult.Lines.Add($"✗ Server 分道 '{aLane}' 前一筆還沒收 —— 這一筆**沒有送出**。");
            aResult.Lines.Add("  出口：senate server status（看分道）；Server 活著卻不收 ⇒ 看它的終端機。");
            return aResult;
        }

        // ③ 送出：只帶這支宣告過的參數（timeout 是 CLI 端的，不送過去）。
        var aSend = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SCP_CmdArgSpec aSpec in ArgSpecs)
            if (aSpec.Name != "timeout") aSend[aSpec.Name] = iArgs.Get(aSpec.Name);
        // 🩸 第一輪驗收（2026-09-02）：這裡原本傳 persona 而不是 lane ⇒ 沒 persona 的 Cmd 被 client 寫進
        //   `anonymous` 分道，而上面 EnsureIdle／下面 Wait 盯的是 `server` 分道 —— 兩邊各自誠實，合起來是
        //   「queue 空了 ⇒ 推論成功、無 result 檔」。Submit 的第二個參數是**分道**，一律傳 aLane；
        //   只有分道真的是 persona 時才讓它戳進 args。
        bool aLaneIsPersona = aLane != DefaultLane;
        string aCmdId;
        try
        {
            aCmdId = AgentCmdClient.Submit(aServerRoot, aLane, Name, aSend, aResult.Lines.Add,
                iInjectPersona: aLaneIsPersona);
        }
        catch (Exception e)
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "submit_failed");
            aResult.Lines.Add($"✗ 送不出去：{e.GetType().Name}: {e.Message}");
            return aResult;
        }
        aResult.AddValue("cmd_id", aCmdId);

        // ④ 等判定。回傳檔由 AppendReport 統一經手（帶 mtime），這裡不重印。
        AgentCmdWaitResult aVerdict = AgentCmdClient.Wait(aServerRoot, aLane, aCmdId, aTimeout,
            AgentCmdClient.DefaultPollSec, aResult.Lines.Add, aResult.Lines.Add,
            iPrintOutputs: false, iHostLabel: "Server");

        if (aVerdict.IsIndeterminate())
        {
            // ⚠ 兩種成因共用這個出口（處置相同：先回讀、⛔ 不要重送），
            //   而 **它們要分得出來** —— 把 Unknown 印成「逾時」就是一個比事實大的名字（TASK-0263）。
            aResult.ExitCode = 3;
            bool aLost = aVerdict == AgentCmdWaitResult.Unknown;
            aResult.AddValue("delegate_failure", aLost ? "unknown" : "timeout");
            aResult.Lines.Add(aLost
                ? "⛔ **不知道**：這筆已不在 queue 而判定檔不存在 ⇒ 它可能根本沒被執行"
                  + "（append 被別人的整檔寫回蓋掉）。⛔ 本 Cmd **不去讀回傳檔**。"
                : "⛔ 逾時 ⇒ 本 Cmd **不去讀回傳檔**（那份是上一輪的，而它看起來正常）。");
            return aResult;
        }
        // Server 端的 Lines 落在 result 檔的 `lines`，這裡原樣接回來 —— 使用者要看到的是 Server 說了什麼。
        foreach (string aLine in AgentCmdClient.ResultLines(aServerRoot, aCmdId)) aResult.Lines.Add("  " + aLine);
        if (aVerdict == AgentCmdWaitResult.Failed)
        {
            // 區塊職責：把**執行端 Cmd 自己回的退出碼**帶回呼叫端，⛔ 不再寫死 1（TASK-0262）。
            // 物理意義：exit 1（回報失敗）與 exit 2（用法錯）在下游是兩種處置 ——
            //          1 有一份錯誤報告可讀，2 刻意沒有（`CmdErrorReport.ShouldReport`：
            //          「打錯字配一份 stack 只會訓練人忽略這個目錄」）。
            //          壓成 1 之後，宣告報告路徑的那一段照 1 的規矩跑、寫檔那端照 2 的規矩（正確地）沒寫
            //          ⇒ CLI 自己承認找不到，再猜「Server 端沒寫成？」。
            //          ⇒ 治的是接縫，不是任何一端：兩端各自都對。
            // 數值影響：走 Server 委派且 Cmd 回非 1 退出碼的那些呼叫，`exit_code` 從 1 變成真值。
            //
            // ⚠ 三種讀不到真值的情況**不共用一個結局**，因為它們不是同一件事：
            //   · 沒有 result 檔／沒有 `exit_code` 欄（舊版執行端）⇒ 退回 1，那是本次改動前的行為。
            //   · 讀到 0 而判定是 Failed ⇒ **矛盾**：退回 1 並把矛盾印出來，
            //     ⛔ 不靜默採信 0（那會把一筆失敗變成 exit 0，比本單在治的病更貴）。
            int? aCmdExit = AgentCmdClient.ResultExitCode(aServerRoot, aCmdId);
            if (aCmdExit is int aExit && aExit != 0)
            {
                aResult.ExitCode = aExit;
            }
            else
            {
                aResult.ExitCode = 1;
                if (aCmdExit == 0)
                {
                    aResult.Lines.Add("⚠ result 檔說判定 Failed 而 `exit_code` 是 0（互相矛盾）"
                                      + " ⇒ 本 Cmd 取 1，⛔ 不採信那個 0。");
                }
            }
            aResult.AddValue("delegate_failure", "cmd_failed");
            UnityDelegateCmd.AppendReport(aResult, aServerRoot, aCmdId);
            return aResult;
        }
        UnityDelegateCmd.AppendReport(aResult, aServerRoot, aCmdId);
        return aResult;
    }

    static double ParseTimeout(SCP_CmdArgs iArgs, SCP_CmdResult oResult)
    {
        string aRaw = iArgs.Get("timeout");
        if (double.TryParse(aRaw, out double aSec) && aSec > 0) return aSec;
        oResult.Lines.Add($"⚠ timeout='{aRaw}' 不是正數 ⇒ 用預設 {AgentCmdClient.DefaultWaitTimeoutSec:0}s");
        return AgentCmdClient.DefaultWaitTimeoutSec;
    }
}
