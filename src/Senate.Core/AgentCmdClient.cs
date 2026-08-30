// 區塊職責：AgentCommands 檔案協議的 **client 半邊**（＝ UCL_Core `run_cmd.py` 的 C# 對應）。
// 物理意義：Cmd 派遣從頭到尾是檔案協議 —— client 寫 queues/<persona>/queue.json ＋ pending.trigger，
//           Unity Editor 端 Watcher 輪詢接手執行、結果落 _cmd_results/<id>.json。
//           協議雙方誰都不知道對面是誰 ⇒ 用 C# 重做 client 半邊，Editor 端零改動。
//           這支存在的理由：**沒有 python 的環境（Codex）也要能派 Cmd**。
// 數值影響：只動目標專案 AgentCommands 底下的 queue/trigger 檔（append ＋ atomic replace）；
//           不碰 Editor 端任何狀態。exit code 語意與 run_cmd.py 對齊：0 成功／2 失敗／3 逾時。
// ⚠ 協議樣板（queue 路徑、queue entry 欄位、trigger 內容、result 檔判定）與 run_cmd.py／
//   UCL_AgentCommandQueue.cs 是**同一份協議的三個端**——任一端改樣板，三端要一起改，
//   落後的那端症狀是 trigger 寫在對方沒在看的地方，**靜默 pending 到 timeout**。
using System.Text.Json;
using System.Text.Json.Nodes;

using SCP.Core.Paths;

namespace Senate.Core;

/// <summary>一次 Cmd 派遣的等待結果。</summary>
public enum AgentCmdWaitResult
{
    Success = 0,
    Failed = 2,
    Timeout = 3,
}

public static class AgentCmdClient
{
    /// <summary>ensure_idle 的預設等待秒數（與 run_cmd.py DEFAULT_ACK_TIMEOUT 對齊）。</summary>
    public const double DefaultAckTimeoutSec = 180;
    /// <summary>wait 的預設逾時秒數（與 run_cmd.py 對齊）。</summary>
    public const double DefaultWaitTimeoutSec = 120;
    public const double DefaultPollSec = 1.0;

    /// <summary>
    /// 沒帶身分時的 queue 資料夾名 —— run_cmd.py 的保留字，不可當 persona 用。
    /// <para>⚠ 值的定義在 <see cref="SCP_DataPaths.AnonymousQueueId"/>（跨端契約的唯一拼字處）；
    /// 這裡只是既有呼叫端的別名。</para>
    /// </summary>
    public const string AnonymousQueueId = SCP_DataPaths.AnonymousQueueId;

    // ── 路徑樣板（persona 資料夾制，Tim 2026-08-01 拍板；與 run_cmd.py queue_path() 同形）──

    public static string QueueFolder(string iDataRoot, string? iPersona)
        => SCP_DataPaths.QueueFolder(new SCP_DataRoot(iDataRoot), iPersona);

    public static string QueuePath(string iDataRoot, string? iPersona)
        => SCP_DataPaths.QueueFile(new SCP_DataRoot(iDataRoot), iPersona);

    public static string TriggerPath(string iDataRoot, string? iPersona)
        => SCP_DataPaths.TriggerFile(new SCP_DataRoot(iDataRoot), iPersona);

    public static string RunningPath(string iDataRoot, string? iPersona)
        => TriggerPath(iDataRoot, iPersona) + ".running";

    /// <summary>'running' / 'pending' / 'idle'。</summary>
    public static string TriggerState(string iDataRoot, string? iPersona)
    {
        if (File.Exists(RunningPath(iDataRoot, iPersona))) return "running";
        if (File.Exists(TriggerPath(iDataRoot, iPersona))) return "pending";
        return "idle";
    }

    /// <summary>
    /// 寫新 trigger 前等前一批收乾淨。逾時回 false 並由 <paramref name="oWhy"/> 說明殘留檔在哪 ——
    /// Editor 沒開／crash 留下 .running 時，永遠等不到，**必須人工介入**，不替人刪。
    /// </summary>
    public static bool EnsureIdle(string iDataRoot, string? iPersona, double iTimeoutSec,
        Action<string> iLog, out string oWhy)
    {
        oWhy = "";
        var aDeadline = DateTime.UtcNow.AddSeconds(iTimeoutSec);
        string? aLastState = null;
        while (DateTime.UtcNow < aDeadline)
        {
            string aState = TriggerState(iDataRoot, iPersona);
            if (aState == "idle")
            {
                if (aLastState != null) iLog("  ✓ idle, proceeding.");
                return true;
            }
            if (aState != aLastState)
            {
                iLog($"  ... previous batch is '{aState}', waiting (timeout {iTimeoutSec:0}s)...");
                aLastState = aState;
            }
            Thread.Sleep(1000);
        }
        oWhy = $"前一批 {iTimeoutSec:0}s 後仍是 '{TriggerState(iDataRoot, iPersona)}'。\n"
             + "  - 確認 Unity Editor 開著且 UCL_AgentCommandWatcher 啟用。\n"
             + "  - Editor crash 或 watcher 關閉時，手動刪掉：\n"
             + $"      {TriggerPath(iDataRoot, iPersona)}\n"
             + $"      {RunningPath(iDataRoot, iPersona)}";
        return false;
    }

    /// <summary>偵測 caller 環境標記（與 run_cmd.py `_detect_caller_env_marker` 同表；審計用自由字串）。
    /// 差異一格：python 端 fallback 是 "unknown"，本端是 "senate-cli" —— 讓帳上分得出走哪條 client。</summary>
    public static string DetectEnvMarker()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLAUDECODE"))) return "claude-code";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_SESSION"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTIGRAVITY_USER_ID"))) return "antigravity";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_SESSION"))) return "gemini";
        return "senate-cli";
    }

    /// <summary>
    /// append 一筆 OneShot 指令到 queue.json 並寫 pending.trigger，回傳 cmd_id。
    /// <para>⚠ queue.json 用 JsonNode 讀改寫 —— 既有指令（含本版不認得的欄位）**原樣保留**，
    /// 反序列化丟掉的東西序列化就再也寫不回來（senate.local.json 的 Extra 同一課）。</para>
    /// </summary>
    public static string Submit(string iDataRoot, string? iPersona, string iCmdType,
        Dictionary<string, string> iArgs, Action<string> iLog)
    {
        // caller-side env marker 注入（Treasury 審計欄；已顯式給的不覆寫 —— 測試 override 用）
        if (!iArgs.ContainsKey("_caller_env_marker"))
            iArgs["_caller_env_marker"] = DetectEnvMarker();
        // 顯式 --persona 戳進 args（與 run_cmd.py 同律：只在缺席時填；兩者不同 → 出聲照 --arg 走）
        if (!string.IsNullOrWhiteSpace(iPersona))
        {
            string aArgPersona = iArgs.TryGetValue("persona", out var p) ? p.Trim() : "";
            if (aArgPersona.Length == 0)
                iArgs["persona"] = iPersona.Trim();
            else if (!string.Equals(aArgPersona, iPersona.Trim(), StringComparison.Ordinal))
                iLog($"  ⚠ 身分宣告衝突：--persona {iPersona} vs --arg persona={aArgPersona} → 依 --arg 值送出。");
        }

        string aCmdId = MakeId(iCmdType);
        JsonObject aRoot = LoadQueue(iDataRoot, iPersona, iLog);
        var aCommands = aRoot["Commands"] as JsonArray ?? new JsonArray();
        aRoot["Commands"] = aCommands;

        var aArgsNode = new JsonObject();
        foreach (var kv in iArgs) aArgsNode[kv.Key] = kv.Value;
        aCommands.Add(new JsonObject
        {
            ["Id"] = aCmdId,
            ["Type"] = iCmdType,
            ["Mode"] = "OneShot",
            ["RunCount"] = 0,
            ["Args"] = aArgsNode,
            ["CreatedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["LastRunAt"] = null,
            ["LastRunResult"] = null,
            ["LastRunError"] = null,
            ["Description"] = null,
        });
        SaveQueue(iDataRoot, iPersona, aRoot);

        // trigger：內容只是 debug 註記，Watcher 認的是檔案存在本身。
        var aTrigger = new JsonObject
        {
            ["createdAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["submittedBy"] = $"senate ucmd run {iCmdType}",
        };
        File.WriteAllText(TriggerPath(iDataRoot, iPersona),
            aTrigger.ToJsonString(s_JsonOpt) + "\n", System.Text.Encoding.UTF8);
        return aCmdId;
    }

    /// <summary>
    /// 等待一筆 cmd 結束並判定結果（＝ run_cmd.py cmd_wait 的移植）。
    /// <para>權威判定來源是 <c>_cmd_results/&lt;id&gt;.json</c>（Editor Runner 出隊前寫）——
    /// 「從 queue 消失」只代表結束，不代表成功。找不到 result 檔才退回舊推論並明講。</para>
    /// </summary>
    public static AgentCmdWaitResult Wait(string iDataRoot, string? iPersona, string iCmdId,
        double iTimeoutSec, double iPollSec, Action<string> iOut, Action<string> iErr)
    {
        iOut($"Waiting for {iCmdId}...");
        iOut($"  Timeout: {iTimeoutSec:0}s   Poll: every {iPollSec:0.0}s");
        var aDeadline = DateTime.UtcNow.AddSeconds(iTimeoutSec);
        bool aSawRunning = false;

        while (DateTime.UtcNow < aDeadline)
        {
            string aState = TriggerState(iDataRoot, iPersona);
            if (aState == "running" && !aSawRunning)
            {
                aSawRunning = true;
                iOut("  ... Editor picked up the trigger (now running)");
            }
            if (aState == "idle")
            {
                JsonObject aQueue = LoadQueue(iDataRoot, iPersona, iOut);
                JsonObject? aCmd = FindCmd(aQueue, iCmdId);
                if (aCmd == null)
                {
                    JsonObject? aVerdict = ReadCmdResult(iDataRoot, iCmdId);
                    if (aVerdict != null)
                    {
                        if ((string?)aVerdict["result"] == "Failed")
                        {
                            string aErrMsg = (string?)aVerdict["error"] ?? "(no error message)";
                            FailVerdict(iOut, iErr, $"  ✗ Cmd failed（Editor 已自動出隊）: {aErrMsg}");
                            PrintOutputs(aVerdict, iOut);   // blocked 也會先落 payload —— 出口清單在那個檔裡
                            PrintErrorReport(iDataRoot, iCmdId, iOut);
                            return AgentCmdWaitResult.Failed;
                        }
                        iOut("  ✓ Cmd completed → Success（result 檔判定，非推論）");
                        PrintOutputs(aVerdict, iOut);
                        return AgentCmdWaitResult.Success;
                    }
                    // fallback：無 result 檔（舊版 Editor / 落檔失敗）—— 明講這是推論。
                    iOut("  ✓ Cmd disappeared from queue → Success (推論：無 result 檔的舊版 fallback)");
                    return AgentCmdWaitResult.Success;
                }
                // 還在 queue → 看 LastRunResult（Repeatable / 失敗殘留兩種可能）
                string? aResult = (string?)aCmd["LastRunResult"];
                if (aResult == "Success")
                {
                    iOut($"  ✓ Repeatable cmd ran successfully (RunCount={(int?)aCmd["RunCount"] ?? 0})");
                    JsonObject? aVerdict2 = ReadCmdResult(iDataRoot, iCmdId);
                    if (aVerdict2 != null) PrintOutputs(aVerdict2, iOut);
                    return AgentCmdWaitResult.Success;
                }
                if (aResult == "Failed")
                {
                    FailVerdict(iOut, iErr, $"  ✗ Cmd failed: {(string?)aCmd["LastRunError"] ?? "(no error message)"}");
                    PrintErrorReport(iDataRoot, iCmdId, iOut);
                    // 失敗的 OneShot 留在 queue 會讓下一批把它整包重跑 —— 清掉（run_cmd.py 同律）。
                    RemoveCmd(iDataRoot, iPersona, iCmdId, iErr);
                    return AgentCmdWaitResult.Failed;
                }
            }
            Thread.Sleep(TimeSpan.FromSeconds(iPollSec));
        }
        FailVerdict(iOut, iErr,
            $"  ✗ Timeout after {iTimeoutSec:0}s — Editor not running, or UCL_AgentCommandWatcher disabled?");
        iErr("  ⚠ 本筆未完成 ⇒ **回傳檔沒有被更新**。若下一步要讀它，先確認檔頭時間戳。");
        return AgentCmdWaitResult.Timeout;
    }

    // ── 內部 ──────────────────────────────────────────────────────────

    static readonly JsonSerializerOptions s_JsonOpt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Id 格式與 run_cmd.py make_id 同形：yyyyMMdd-HHmmss-&lt;6hex&gt;-&lt;type小寫&gt;。</summary>
    static string MakeId(string iCmdType)
        => $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-{iCmdType.ToLowerInvariant()}";

    static JsonObject LoadQueue(string iDataRoot, string? iPersona, Action<string> iLog)
    {
        string aPath = QueuePath(iDataRoot, iPersona);
        if (!File.Exists(aPath)) return new JsonObject { ["Commands"] = new JsonArray() };
        try
        {
            var aNode = JsonNode.Parse(File.ReadAllText(aPath, System.Text.Encoding.UTF8)) as JsonObject;
            if (aNode == null || aNode["Commands"] is not JsonArray)
                return new JsonObject { ["Commands"] = new JsonArray() };
            return aNode;
        }
        catch (Exception e)
        {
            iLog($"  ⚠ queue.json parse error: {e.Message}（以空骨架續行 —— 舊內容不動，寫入走 atomic replace）");
            return new JsonObject { ["Commands"] = new JsonArray() };
        }
    }

    /// <summary>atomic ＋ retry 寫回 queue.json（temp → File.Move overwrite；撞 Editor 檔鎖 backoff 重試 5 次）。</summary>
    static void SaveQueue(string iDataRoot, string? iPersona, JsonObject iRoot)
    {
        string aPath = QueuePath(iDataRoot, iPersona);
        Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
        string aPayload = iRoot.ToJsonString(s_JsonOpt) + "\n";
        string aTmp = aPath + $".tmp{Environment.ProcessId}";
        Exception? aLast = null;
        for (int aAttempt = 0; aAttempt < 5; ++aAttempt)
        {
            try
            {
                File.WriteAllText(aTmp, aPayload, new System.Text.UTF8Encoding(false));
                File.Move(aTmp, aPath, overwrite: true);
                return;
            }
            catch (IOException e) { aLast = e; }
            catch (UnauthorizedAccessException e) { aLast = e; }
            Thread.Sleep(100 * (aAttempt + 1));
        }
        try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { /* 殘檔清不掉不該蓋掉真錯 */ }
        throw aLast!;
    }

    static JsonObject? FindCmd(JsonObject iQueue, string iCmdId)
    {
        foreach (var aNode in iQueue["Commands"] as JsonArray ?? new JsonArray())
            if (aNode is JsonObject aObj && (string?)aObj["Id"] == iCmdId) return aObj;
        return null;
    }

    static void RemoveCmd(string iDataRoot, string? iPersona, string iCmdId, Action<string> iErr)
    {
        JsonObject aQueue = LoadQueue(iDataRoot, iPersona, iErr);
        var aCommands = aQueue["Commands"] as JsonArray;
        if (aCommands == null) return;
        for (int i = aCommands.Count - 1; i >= 0; --i)
        {
            if (aCommands[i] is JsonObject aObj && (string?)aObj["Id"] == iCmdId)
            {
                aCommands.RemoveAt(i);
                SaveQueue(iDataRoot, iPersona, aQueue);
                iErr("  ↳ removed failed cmd from queue");
                return;
            }
        }
    }

    static JsonObject? ReadCmdResult(string iDataRoot, string iCmdId)
    {
        try
        {
            string aPath = Path.Combine(iDataRoot, "_cmd_results", $"{iCmdId}.json");
            if (!File.Exists(aPath)) return null;
            return JsonNode.Parse(File.ReadAllText(aPath, System.Text.Encoding.UTF8)) as JsonObject;
        }
        catch { return null; }   // 壞檔當沒有 —— fallback 舊推論，不擋判定
    }

    /// <summary>印 result 檔的 outputs（回傳檔路徑）與 values（純量回報）—— 兩欄分開印，混了名字比事實大。</summary>
    static void PrintOutputs(JsonObject iVerdict, Action<string> iOut)
    {
        if (iVerdict["outputs"] is JsonArray aOuts)
            foreach (var o in aOuts)
                if (o is JsonValue && (string?)o is { Length: > 0 } aPath)
                    iOut($"  📄 回傳檔：{aPath}");
        if (iVerdict["values"] is JsonArray aVals)
            foreach (var v in aVals)
                if (v is JsonObject aKv && (string?)aKv["key"] is { Length: > 0 } aKey)
                    iOut($"  🔢 {aKey} = {(string?)aKv["value"] ?? ""}");
    }

    /// <summary>失敗判決 stderr＋stdout 各印一份（PS 5.1 `2>&1` 會把 stderr 重編碼吞掉 —— run_cmd.py 同一課）。</summary>
    static void FailVerdict(Action<string> iOut, Action<string> iErr, string iText)
    {
        iErr(iText);
        iOut(iText);
    }

    static void PrintErrorReport(string iDataRoot, string iCmdId, Action<string> iOut, int iMaxLines = 60)
    {
        try
        {
            string aPath = Path.Combine(iDataRoot, "_cmd_errors", $"{iCmdId}.md");
            if (!File.Exists(aPath)) return;
            string[] aLines = File.ReadAllLines(aPath, System.Text.Encoding.UTF8);
            iOut("  ── Editor 端詳細錯誤報告 ──");
            foreach (var aLine in aLines.Take(iMaxLines)) iOut($"  {aLine}");
            if (aLines.Length > iMaxLines) iOut($"  …（省略 {aLines.Length - iMaxLines} 行）");
            iOut($"  📄 完整報告：{aPath}");
        }
        catch { /* 報告是加值，讀不到不該蓋掉原始錯誤 */ }
    }
}
