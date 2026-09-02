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
        string aServerRoot = SenatePaths.ServerRoot(aRepoRoot);

        // ① Server 在不在、是不是同一顆 exe。⛔ 不在就到此為止，不降級成本地跑。
        ServerStatus aStatus = ServerHost.Probe(aRepoRoot);
        if (!aStatus.IsRunning)
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_host", "server");
            aResult.AddValue("delegate_failure", "not_running");
            aResult.Lines.Add($"✗ 這支 Cmd 由 Senate Server 執行，而 Server 沒在跑 —— 這一筆**沒有送出**。");
            aResult.Lines.Add("  啟動：開一個終端機跑 `senate server start`（前景常駐），再回來重跑這一行。");
            aResult.Lines.Add("  ⛔ 不會改成本地跑：本地跑就是第二個寫入者，而它的輸出跟 Server 跑的一模一樣。");
            return aResult;
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

        if (aVerdict == AgentCmdWaitResult.Timeout)
        {
            aResult.ExitCode = 3;
            aResult.AddValue("delegate_failure", "timeout");
            aResult.Lines.Add("⛔ 逾時 ⇒ 本 Cmd **不去讀回傳檔**（那份是上一輪的，而它看起來正常）。");
            return aResult;
        }
        // Server 端的 Lines 落在 result 檔的 `lines`，這裡原樣接回來 —— 使用者要看到的是 Server 說了什麼。
        foreach (string aLine in AgentCmdClient.ResultLines(aServerRoot, aCmdId)) aResult.Lines.Add("  " + aLine);
        if (aVerdict == AgentCmdWaitResult.Failed)
        {
            aResult.ExitCode = 1;
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
