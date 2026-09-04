// 區塊職責：**Server 端執行器** —— 沿用 AgentCommand 檔案協議（Tim ③），Server 當 Watcher：
//           掃 `<Server 根>/queues/<lane>/pending.trigger` → 原子接手成 `.running` → 該 lane 一條 thread
//           跑完 queue.json 裡的每一筆 → 寫 `_cmd_results/<id>.json` → 出隊 → 刪 `.running`。
// 物理意義：TASK-0103。形狀照 Editor Runner（UCL_AgentCommandRunner）：**同 lane 串行、跨 lane 並行**、
//           OneShot 成功與失敗都出隊、verdict 一律在 result 檔（「從 queue 消失」只代表結束）。
//           協議三端（run_cmd.py／AgentCmdClient／UCL_AgentCommandQueue）從此變四端 —— 本檔的路徵常數
//           **全部走 SCP_DataPaths／AgentCmdClient**，不重拼一次。
// 數值影響：只動 Server 根（SenateData/runtime/server/）底下的檔；不碰任何 Unity 專案的資料根。
//           result 檔 schema 與 Editor 端 WriteCmdResult 同形（id/type/mode/result/finished_at/client/
//           outputs/values/error/error_report），多三欄 host/server_pid/server_build 與 `lines`。
//
// ⚠ 只接 ServerDelegateCmd：別的型別送進來 ⇒ Failed 並說「這支不走 Server」。
//   Native 的 Cmd 在 Server 裡跑會少掉 CLI 注入的那些便利（letters_root 等），而且它們本來就不需要單一寫入者。
// ⚠ 孤兒 `.running`：Server 上次沒收乾淨（crash／被 kill）留下的。啟動時把它翻回 pending 續跑 ——
//   照 Editor Watcher 的自救形狀；不翻的話那條 lane 永遠 busy，而 CLI 只會看到 queue_busy。
using System.Text.Json;
using System.Text.Json.Nodes;
using SCP.Core.Cmd;
using SCP.Core.Paths;

namespace Senate.Core;

public sealed class ServerExecutor
{
    readonly string m_Root;
    readonly Action<string> m_Out;
    readonly Action<string> m_Err;
    readonly object m_Lock = new();
    readonly Dictionary<string, Thread> m_Running = new(StringComparer.Ordinal);

    /// <summary>已跑完幾筆（含失敗）—— status 那側的讀數。</summary>
    public int Completed { get; private set; }

    public ServerExecutor(string iServerRoot, Action<string> iOut, Action<string> iErr)
    {
        m_Root = iServerRoot;
        m_Out = iOut;
        m_Err = iErr;
    }

    public int RunningLaneCount { get { lock (m_Lock) return m_Running.Count; } }

    string QueuesDir => SCP_DataPaths.Queues(new SCP_DataRoot(m_Root));

    /// <summary>啟動時呼叫一次：把孤兒 `.running` 翻回 pending。回傳翻了幾條。</summary>
    public int RecoverOrphans()
    {
        int aCount = 0;
        if (!Directory.Exists(QueuesDir)) return 0;
        foreach (string aDir in Directory.GetDirectories(QueuesDir))
        {
            string aLane = Path.GetFileName(aDir);
            string aRunning = AgentCmdClient.RunningPath(m_Root, aLane);
            if (!File.Exists(aRunning)) continue;
            string aTrigger = AgentCmdClient.TriggerPath(m_Root, aLane);
            try
            {
                if (File.Exists(aTrigger)) File.Delete(aRunning);   // 兩個都在：pending 那份是新的，running 是屍體
                else File.Move(aRunning, aTrigger);
                aCount++;
                m_Out($"⚠ lane '{aLane}' 有上一顆 Server 留下的 .running ⇒ 翻回 pending 續跑（孤兒鎖自救）");
            }
            catch (Exception e) { m_Err($"⚠ lane '{aLane}' 的孤兒 .running 收不掉：{e.GetType().Name}: {e.Message}"); }
        }
        return aCount;
    }

    /// <summary>每個心跳呼叫一次：有 pending 且該 lane 沒在跑 ⇒ 接手、開 thread。</summary>
    public void Tick()
    {
        if (!Directory.Exists(QueuesDir)) return;
        string[] aDirs;
        try { aDirs = Directory.GetDirectories(QueuesDir); }
        catch (Exception e) { m_Err($"⚠ 掃 queues 失敗：{e.Message}"); return; }

        foreach (string aDir in aDirs)
        {
            string aLane = Path.GetFileName(aDir);
            string aTrigger = AgentCmdClient.TriggerPath(m_Root, aLane);
            if (!File.Exists(aTrigger)) continue;
            lock (m_Lock) { if (m_Running.ContainsKey(aLane)) continue; }

            string aRunning = AgentCmdClient.RunningPath(m_Root, aLane);
            try { File.Move(aTrigger, aRunning); }        // 原子接手 —— 搬不動就是別人（或上一輪）拿走了
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            var aThread = new Thread(() => RunLane(aLane)) { IsBackground = true, Name = "lane:" + aLane };
            lock (m_Lock) m_Running[aLane] = aThread;
            aThread.Start();
        }
    }

    /// <summary>停機前等正在跑的 lane 收尾；回傳等完之後還在跑的數量（0 ＝ 乾淨）。</summary>
    public int Drain(TimeSpan iGrace)
    {
        var aDeadline = DateTime.UtcNow + iGrace;
        while (DateTime.UtcNow < aDeadline)
        {
            if (RunningLaneCount == 0) return 0;
            Thread.Sleep(100);
        }
        int aLeft = RunningLaneCount;
        if (aLeft > 0)
            m_Err($"⚠ {aLeft} 條 lane 在 {iGrace.TotalSeconds:0} 秒內沒跑完 —— 它們的 .running 會留著，下一顆 Server 啟動時翻回 pending 續跑");
        return aLeft;
    }

    // ── lane ──────────────────────────────────────────────────────────

    void RunLane(string iLane)
    {
        string aQueuePath = AgentCmdClient.QueuePath(m_Root, iLane);
        string aRunning = AgentCmdClient.RunningPath(m_Root, iLane);
        try
        {
            JsonObject aQueue = LoadQueue(aQueuePath);
            var aCommands = aQueue["Commands"] as JsonArray ?? new JsonArray();
            int aTotal = aCommands.Count;
            m_Out($"▶ lane '{iLane}'：{aTotal} 筆");

            for (int i = aCommands.Count - 1; i >= 0; --i)
            {
                if (aCommands[i] is not JsonObject aCmd) { aCommands.RemoveAt(i); continue; }
                string aId = (string?)aCmd["Id"] ?? "";
                string aType = (string?)aCmd["Type"] ?? "";
                string aMode = (string?)aCmd["Mode"] ?? "OneShot";
                var aArgs = new Dictionary<string, string>(StringComparer.Ordinal);
                if (aCmd["Args"] is JsonObject aArgsNode)
                    foreach (var kv in aArgsNode) aArgs[kv.Key] = (string?)kv.Value ?? "";

                SCP_CmdResult aResult = RunOne(aType, aArgs);
                // 錯誤報告（TASK-0104）先寫再寫 result —— result 檔的 error_report 欄指的路徵要在它被讀到之前就存在。
                if (CmdErrorReport.ShouldReport(aResult.ExitCode))
                    CmdErrorReport.Write(m_Root, aId, aType, aArgs, aResult, "server", m_Err);
                WriteResult(m_Root, aId, aType, aMode, aArgs, aResult);
                Completed++;
                m_Out(aResult.Ok
                    ? $"  ✓ {aType} ({aId})"
                    : $"  ✗ {aType} ({aId}) exit={aResult.ExitCode}：{FirstLine(aResult)}");

                // OneShot 成功與失敗都出隊（Tim 2026-08-07 拍板的 Editor 半邊，這裡照用）；verdict 在 result 檔。
                if (aMode == "OneShot") aCommands.RemoveAt(i);
                else
                {
                    aCmd["LastRunAt"] = DateTime.UtcNow.ToString("o");
                    aCmd["LastRunResult"] = aResult.Ok ? "Success" : "Failed";
                    aCmd["LastRunError"] = aResult.Ok ? null : FirstLine(aResult);
                    aCmd["RunCount"] = ((int?)aCmd["RunCount"] ?? 0) + 1;
                }
            }
            aQueue["Commands"] = aCommands;
            SaveQueue(aQueuePath, aQueue);
        }
        catch (Exception e)
        {
            m_Err($"✗ lane '{iLane}' 整批失敗：{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try { if (File.Exists(aRunning)) File.Delete(aRunning); }
            catch (Exception e) { m_Err($"⚠ lane '{iLane}' 的 .running 刪不掉：{e.Message}（下一顆 Server 會當孤兒翻回）"); }
            lock (m_Lock) m_Running.Remove(iLane);
        }
    }

    /// <summary>跑一筆：只接 ServerDelegateCmd；框架欄（底線前綴）與未宣告的 persona 先剝掉再交給 Registry 驗參數。</summary>
    static SCP_CmdResult RunOne(string iType, Dictionary<string, string> iArgs)
    {
        SCP_Cmd? aCmd = SCP_CmdRegistry.Find(iType);
        if (aCmd == null)
            return SCP_CmdResult.Fail(2, $"✗ Server 認不得的指令 '{iType}'");
        if (aCmd is not ServerDelegateCmd)
            return SCP_CmdResult.Fail(2, $"✗ '{iType}' 不走 Server（PortStatus={aCmd.PortStatus}）—— 直接 `senate cmd {iType}` 跑它");

        var aClean = new Dictionary<string, string>(StringComparer.Ordinal);
        bool aDeclaresPersona = false;
        foreach (SCP_CmdArgSpec aSpec in aCmd.ArgSpecs) if (aSpec.Name == "persona") aDeclaresPersona = true;
        foreach (var kv in iArgs)
        {
            if (kv.Key.StartsWith("_", StringComparison.Ordinal)) continue;          // _caller_client / _caller_env_marker / _cmd_id
            if (kv.Key == "persona" && !aDeclaresPersona) continue;                   // Submit 順手戳進來的分道宣告
            aClean[kv.Key] = kv.Value;
        }
        return SCP_CmdRegistry.Dispatch(iType, aClean);
    }

    static string FirstLine(SCP_CmdResult iResult)
        => iResult.Lines.Count > 0 ? iResult.Lines[iResult.Lines.Count == 1 ? 0 : Math.Min(1, iResult.Lines.Count - 1)] : "(no message)";

    // ── result 檔（schema 與 Editor 端 WriteCmdResult 同形）────────────

    /// <summary>
    /// 寫 `_cmd_results/&lt;id&gt;.json`。成功與失敗都寫 —— 只寫失敗的話「沒有檔」又變回要推論的空白。
    /// <para>public static 是為了 selftest 能對它做 round-trip（寫 → AgentCmdClient 讀回）。</para>
    /// </summary>
    public static string WriteResult(string iServerRoot, string iCmdId, string iType, string iMode,
        IReadOnlyDictionary<string, string> iArgs, SCP_CmdResult iResult)
    {
        string aDir = Path.Combine(iServerRoot, "_cmd_results");
        Directory.CreateDirectory(aDir);
        var aJson = new JsonObject
        {
            ["id"] = iCmdId,
            ["type"] = iType,
            ["mode"] = iMode,
            ["result"] = iResult.Ok ? "Success" : "Failed",
            ["finished_at"] = DateTime.UtcNow.ToString("o"),
            ["client"] = iArgs.TryGetValue("_caller_client", out string? aClient) && aClient.Length > 0 ? aClient : "unstated",
            ["host"] = "senate-server",
            ["server_pid"] = ServerContext.Pid,
            ["server_build"] = ServerContext.BuildId,
            ["exit_code"] = iResult.ExitCode,
        };
        var aLines = new JsonArray();
        foreach (string l in iResult.Lines) aLines.Add(l);
        aJson["lines"] = aLines;
        if (iResult.Outputs.Count > 0)
        {
            var aOuts = new JsonArray();
            foreach (string o in iResult.Outputs) aOuts.Add(o);
            aJson["outputs"] = aOuts;
        }
        if (iResult.Values.Count > 0)
        {
            var aVals = new JsonArray();
            foreach (var kv in iResult.Values) aVals.Add(new JsonObject { ["key"] = kv.Key, ["value"] = kv.Value });
            aJson["values"] = aVals;
        }
        if (!iResult.Ok)
        {
            aJson["error"] = FirstLine(iResult);
            aJson["error_report"] = Path.Combine(iServerRoot, CmdErrorReport.DirName, iCmdId + ".md");
        }
        string aPath = Path.Combine(aDir, iCmdId + ".json");
        string aTmp = aPath + ".tmp";
        File.WriteAllText(aTmp, aJson.ToJsonString(s_JsonOpt) + "\n", new System.Text.UTF8Encoding(false));
        File.Move(aTmp, aPath, overwrite: true);
        return aPath;
    }

    static readonly JsonSerializerOptions s_JsonOpt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static JsonObject LoadQueue(string iPath)
    {
        if (!File.Exists(iPath)) return new JsonObject { ["Commands"] = new JsonArray() };
        var aNode = JsonNode.Parse(File.ReadAllText(iPath, System.Text.Encoding.UTF8)) as JsonObject;
        if (aNode == null || aNode["Commands"] is not JsonArray) return new JsonObject { ["Commands"] = new JsonArray() };
        return aNode;
    }

    static void SaveQueue(string iPath, JsonObject iRoot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(iPath)!);
        string aTmp = iPath + $".tmp{Environment.ProcessId}";
        File.WriteAllText(aTmp, iRoot.ToJsonString(s_JsonOpt) + "\n", new System.Text.UTF8Encoding(false));
        File.Move(aTmp, iPath, overwrite: true);
    }
}
