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

    /// <summary>
    /// 此刻還在跑的 lane 名字（快照，已排序）。
    /// <para>⚠ 存在的理由不是好看：收尾時只印「還有 N 條」的話，**看的人沒有下一步可做** ——
    /// 他不知道在等誰、也判不出該不該硬停。名字才是拿得去查 queue 的那個東西。</para>
    /// </summary>
    public IReadOnlyList<string> RunningLanes
    {
        get
        {
            lock (m_Lock)
            {
                var aList = new List<string>(m_Running.Count);
                foreach (string aLane in m_Running.Keys) aList.Add(aLane);
                aList.Sort(StringComparer.Ordinal);
                return aList;
            }
        }
    }

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
    // ⚠ 排乾期間**不呼叫 Tick()** —— 這是 quiesce 的語意：不收新工作，只等手上的做完。
    //   呼叫端要每一圈跳一次心跳（<paramref name="iOnPoll"/>），否則 `server stop` 那側會判心跳過期
    //   而去 kill 它 —— **那就把「排乾」變成了「硬切」，比不排還糟**。
    /// <param name="iOnPoll">每一圈呼叫一次（跳心跳／印進度）。參數是「還在跑的 lane」。</param>
    /// <returns>還沒跑完的 lane 數（0 ＝ 排乾了）。</returns>
    public int Drain(TimeSpan iGrace, Action<IReadOnlyList<string>>? iOnPoll = null)
    {
        var aDeadline = DateTime.UtcNow + iGrace;
        while (DateTime.UtcNow < aDeadline)
        {
            IReadOnlyList<string> aLanes = RunningLanes;
            if (aLanes.Count == 0) return 0;
            iOnPoll?.Invoke(aLanes);
            Thread.Sleep(100);
        }
        IReadOnlyList<string> aLeft = RunningLanes;
        if (aLeft.Count > 0)
            m_Err($"⚠ {aLeft.Count} 條 lane 在 {iGrace.TotalSeconds:0} 秒內沒跑完（{string.Join(", ", aLeft)}）"
                  + " —— 它們的 .running 會留著，下一顆 Server 啟動時翻回 pending 續跑。"
                  + " ⚠ **續跑 ＝ 那筆 cmd 會被再執行一次** ⇒ 沒有冪等鍵的寫入端會做第二次。");
        return aLeft.Count;
    }

    // ── lane ──────────────────────────────────────────────────────────

    void RunLane(string iLane)
    {
        string aQueuePath = AgentCmdClient.QueuePath(m_Root, iLane);
        string aRunning = AgentCmdClient.RunningPath(m_Root, iLane);
        // 🔴 這一批處理過的 id（TASK-0263）—— 收尾時**重讀磁碟**只拿掉這些，
        //   ⛔ 不把手上這份跑了幾秒的舊副本整個寫回去。
        var aDoneOneShot = new HashSet<string>(StringComparer.Ordinal);
        var aRepeatState = new Dictionary<string, (string? At, string? Result, string? Error)>(StringComparer.Ordinal);
        try
        {
            JsonObject aQueue;
            using (SCP.Core.Io.SCP_FileLock.Acquire(aQueuePath)) aQueue = LoadQueue(aQueuePath);
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
                if (aMode == "OneShot") { aCommands.RemoveAt(i); aDoneOneShot.Add(aId); }
                else
                {
                    aCmd["LastRunAt"] = DateTime.UtcNow.ToString("o");
                    aCmd["LastRunResult"] = aResult.Ok ? "Success" : "Failed";
                    aCmd["LastRunError"] = aResult.Ok ? null : FirstLine(aResult);
                    aCmd["RunCount"] = ((int?)aCmd["RunCount"] ?? 0) + 1;
                    aRepeatState[aId] = ((string?)aCmd["LastRunAt"], (string?)aCmd["LastRunResult"],
                                         (string?)aCmd["LastRunError"]);
                }
            }
            CommitLane(aQueuePath, iLane, aDoneOneShot, aRepeatState);
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
    /// <para>⚠ 目錄名走 <see cref="SCP_DataPaths.CmdResultsDirName"/>，**與 <c>AgentCmdClient.ResultPath</c>
    /// 同一份**（TASK-0103 ①，@summit 指認：條文說「同一份」而兩端各自拼一次）。
    /// 🩸 而根**不共用**：這裡是 Server 根、那端是資料根 —— 所以共用的只到名字那一層，
    /// ⛔ 不能把它換成 <c>SCP_PathRegistry</c> 的衍生條目（那條從 <c>AgentCommandsRoot</c> 長出來，
    /// 接上來會**靜默**把這個目錄搬到另一個父目錄底下，而 round-trip 會變成「沒有回傳檔」）。</para>
    /// </summary>
    public static string WriteResult(string iServerRoot, string iCmdId, string iType, string iMode,
        IReadOnlyDictionary<string, string> iArgs, SCP_CmdResult iResult)
    {
        string aDir = Path.Combine(iServerRoot, SCP_DataPaths.CmdResultsDirName);
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
            // ⚠ `error_report` 只在**真的有寫那份檔**時才宣告（TASK-0262，第二個入口）。
            //   上面 RunOne 之後寫檔那一段的條件就是 `ShouldReport`；這一欄以前無條件寫
            //   ⇒ exit 2／3 的 result 檔裡躺著一條指向不存在檔案的路徑。
            //   🩸 它今天還沒有消費端，所以它不會叫 —— 而「沒有人讀」不是「它是對的」。
            //   ⇒ 判準與寫檔那端共用同一支 `ShouldReport`，⛔ 不在這裡抄第二份條件。
            if (CmdErrorReport.ShouldReport(iResult.ExitCode))
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

    /// <summary>
    /// 一批跑完之後把結果寫回 queue —— 🔴 **重讀磁碟、只動這一批碰過的 id**（TASK-0263）。
    /// <para>🩸 舊版是 `SaveQueue(整份手上那個副本)`，而那份副本是**批次開始時**載入的。
    /// 一批可能跑好幾秒，這段時間內任何 client `append` 的那一筆會被這次寫回**整個蓋掉** ——
    /// ⚠ 那不是窄窗口的競態，是**整批時長那麼寬**的窗口。
    /// 而下游看不見：那筆 cmd 不在 queue、也不會有 result 檔 ⇒ client 端的舊 fallback
    /// 把它讀成「Cmd disappeared → 推論 Success」。實測 60 筆併發：落盤 55，client 全 exit 0。</para>
    /// <para>⚠ 這裡**不需要**在整批期間握著鎖 —— 那會把 client 擋到逾時。
    /// 要的只是「寫回的那一瞬間，依據的是磁碟現況」。</para>
    /// <para>⛔ 本函式不處理「批次中途丟例外」：那種情況下已跑完的 OneShot 仍留在 queue，
    /// 下一輪會**再跑一次**。那是既有行為（見 `Drain` 的警語），⛔ 不在 TASK-0263 射程。</para>
    /// </summary>
    void CommitLane(string iQueuePath, string iLane, HashSet<string> iDoneOneShot,
                    Dictionary<string, (string? At, string? Result, string? Error)> iRepeat)
    {
        int aKept = CommitLaneQueue(iQueuePath, iDoneOneShot, iRepeat);
        if (aKept > 0)
            m_Out($"  · lane '{iLane}'：保留了這一批期間新進的 {aKept} 筆（下一輪跑）");
    }

    /// <summary>
    /// <see cref="CommitLane"/> 的本體（無日誌）。⚠ <c>public</c> 的理由是**它要被對拍直接驅動** ——
    /// 「批次期間進來的那一筆有沒有活下來」只有在這一層量得到，
    /// 從整顆 Server 外面量要先造出一個時序，而那個時序本身會變成第二個可能壞掉的東西。
    /// </summary>
    /// <returns>這一批期間新進、因而被保留下來的筆數。</returns>
    public static int CommitLaneQueue(string iQueuePath, ICollection<string> iDoneOneShot,
                                      IDictionary<string, (string? At, string? Result, string? Error)> iRepeat)
    {
        using (SCP.Core.Io.SCP_FileLock.Acquire(iQueuePath))
        {
            JsonObject aFresh = LoadQueue(iQueuePath);                 // ⚠ 重讀，⛔ 不用手上那份
            var aCommands = aFresh["Commands"] as JsonArray ?? new JsonArray();
            int aKeptNew = 0;
            for (int i = aCommands.Count - 1; i >= 0; --i)
            {
                if (aCommands[i] is not JsonObject aObj) { aCommands.RemoveAt(i); continue; }
                string aId = (string?)aObj["Id"] ?? "";
                if (iDoneOneShot.Contains(aId)) { aCommands.RemoveAt(i); continue; }
                if (iRepeat.TryGetValue(aId, out var aState))
                {
                    aObj["LastRunAt"] = aState.At;
                    aObj["LastRunResult"] = aState.Result;
                    aObj["LastRunError"] = aState.Error;
                    aObj["RunCount"] = ((int?)aObj["RunCount"] ?? 0) + 1;
                    continue;
                }
                ++aKeptNew;   // 這一批期間才進來的 —— 舊版會把它寫沒
            }
            aFresh["Commands"] = aCommands;
            SaveQueue(iQueuePath, aFresh);
            return aKeptNew;
        }
    }
}
