// 區塊職責：**CLI ↔ 常駐視窗**的檔案協議（請求／回應／心跳）—— 兩邊共用的那一份。
// 物理意義：TASK-0214。文字模式從此不是「CLI 自己再畫一次」，而是**去問那顆真窗身上有哪些可以點**。
//           ⇒ 同一棵樹只有一個產生者（那顆窗），於是「文字說有、窗上沒有」這種兩個宇宙的失效不存在。
//           🩸 它要防的病有名字：`Create_EditorPage_Workflow.md` §10 那條「per-frame 成本在文字宿主
//           結構性測不到」。**修法不是叫人記得開窗，是讓文字模式本來就在描述一顆正在重畫的窗。**
// 數值影響：⭐ **常駐端每幀的成本＝一次 bool 讀取**（Tim 2026-09-15 拍板：文字樹要走額外指令）。
//           目錄變動由 FileSystemWatcher 推過來，⛔ 不做每幀 `Directory.GetFiles`，
//           也⛔不在窗裡常態保存一份樹 —— 那是把一次性成本改成常駐成本，而且那份快取會過期。
//           ⇒ 這一格有驗收讀數（TASK-0214 ⑧）：閒置時 `--soak` 的 fps 不准有可量的退步。
using System.Globalization;
using SCP.Core.Json;

namespace Senate.Core;

/// <summary>常駐視窗的心跳 —— 形狀照 <see cref="ServerHeartbeat"/>（同一件事換一個宿主）。</summary>
public sealed class GuiHeartbeat
{
    public int Pid;
    public string BuildId = "";
    public string StartedAtUtc = "";
    public string BeatAtUtc = "";

    /// <summary>窗上現在停在哪一頁（給 CLI 印定語用：你問的是**這一頁**）。</summary>
    public string PageKey = "";

    /// <summary>從開窗到現在的 fps 讀數（累積）。</summary>
    public string FpsTotal = "";

    /// <summary>最近一段時間的 fps 讀數 —— 累積值會被開頭那幾秒稀釋，看不出「剛剛卡住」。</summary>
    public string FpsRecent = "";

    public SCP_JsonData ToJson()
    {
        var aData = SCP_JsonData.NewObject();
        aData.Set("pid", SCP_JsonData.NewNumber(Pid));
        aData.Set("build_id", SCP_JsonData.NewString(BuildId));
        aData.Set("started_at_utc", SCP_JsonData.NewString(StartedAtUtc));
        aData.Set("beat_at_utc", SCP_JsonData.NewString(BeatAtUtc));
        aData.Set("page_key", SCP_JsonData.NewString(PageKey));
        aData.Set("fps_total", SCP_JsonData.NewString(FpsTotal));
        aData.Set("fps_recent", SCP_JsonData.NewString(FpsRecent));
        aData.Set("schema_version", SCP_JsonData.NewNumber(1));
        return aData;
    }

    public static GuiHeartbeat? FromJson(SCP_JsonData? iData)
    {
        if (iData == null || !iData.Exists) return null;
        return new GuiHeartbeat
        {
            Pid = iData.GetInt("pid", 0),
            BuildId = iData.GetString("build_id", ""),
            StartedAtUtc = iData.GetString("started_at_utc", ""),
            BeatAtUtc = iData.GetString("beat_at_utc", ""),
            PageKey = iData.GetString("page_key", ""),
            FpsTotal = iData.GetString("fps_total", ""),
            FpsRecent = iData.GetString("fps_recent", ""),
        };
    }

    /// <summary>心跳距今幾秒；解析不了回 null（⚠ 不回 0 —— 0 是「剛跳過」，跟「讀不到」不同形）。</summary>
    public double? AgeSeconds()
    {
        if (DateTime.TryParse(BeatAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime aBeat))
            return (DateTime.UtcNow - aBeat.ToUniversalTime()).TotalSeconds;
        return null;
    }
}

/// <summary>窗在不在、新不新鮮 —— 兩個來源分開放，呼叫端自己決定怎麼印。</summary>
public sealed class GuiBridgeStatus
{
    public GuiHeartbeat? Heartbeat;
    public string? Error;

    /// <summary>有心跳檔，而且還在跳。⚠ 「檔在」不等於「窗活著」——死掉的窗留下的檔長得一模一樣。</summary>
    public bool Alive => Heartbeat?.AgeSeconds() is { } aAge && aAge <= GuiBridge.HeartbeatStaleSeconds;
}

/// <summary>一次請求的內容（CLI → 窗）。</summary>
public sealed class GuiRequest
{
    /// <summary>list / json / click / set / toggle / fold。</summary>
    public string Op = "";
    public string TargetId = "";
    public string Value = "";

    public SCP_JsonData ToJson()
    {
        var aData = SCP_JsonData.NewObject();
        aData.Set("op", SCP_JsonData.NewString(Op));
        aData.Set("target_id", SCP_JsonData.NewString(TargetId));
        aData.Set("value", SCP_JsonData.NewString(Value));
        aData.Set("schema_version", SCP_JsonData.NewNumber(1));
        return aData;
    }

    public static GuiRequest FromJson(SCP_JsonData iData) => new()
    {
        Op = iData.GetString("op", ""),
        TargetId = iData.GetString("target_id", ""),
        Value = iData.GetString("value", ""),
    };
}

/// <summary>一次請求的結果（窗 → CLI）。</summary>
public sealed class GuiResponse
{
    public bool Ok;
    public string Text = "";
    public string Error = "";
    public string PageKey = "";
    public string FpsTotal = "";
    public string FpsRecent = "";

    public SCP_JsonData ToJson()
    {
        var aData = SCP_JsonData.NewObject();
        aData.Set("ok", SCP_JsonData.NewBool(Ok));
        aData.Set("text", SCP_JsonData.NewString(Text));
        aData.Set("error", SCP_JsonData.NewString(Error));
        aData.Set("page_key", SCP_JsonData.NewString(PageKey));
        aData.Set("fps_total", SCP_JsonData.NewString(FpsTotal));
        aData.Set("fps_recent", SCP_JsonData.NewString(FpsRecent));
        aData.Set("schema_version", SCP_JsonData.NewNumber(1));
        return aData;
    }

    public static GuiResponse FromJson(SCP_JsonData iData) => new()
    {
        Ok = iData.GetBool("ok", false),
        Text = iData.GetString("text", ""),
        Error = iData.GetString("error", ""),
        PageKey = iData.GetString("page_key", ""),
        FpsTotal = iData.GetString("fps_total", ""),
        FpsRecent = iData.GetString("fps_recent", ""),
    };
}

public static class GuiBridge
{
    /// <summary>registry 裡的 tag（ProcessAdminPage 那張表上看得到）。</summary>
    public const string Tag = "senate_gui";

    public const int HeartbeatIntervalMs = 500;

    /// <summary>心跳超過這個秒數視為停了 —— 對齊 <see cref="ServerHost.HeartbeatStaleSeconds"/>。</summary>
    public const double HeartbeatStaleSeconds = 4.0;

    /// <summary>CLI 等回應的上限。⚠ 比心跳判死久一點：窗剛好在畫一幀重的東西不該被當成沒在跑。</summary>
    public const int RequestTimeoutMs = 10_000;

    public const string RequestExt = ".req";
    public const string ResponseExt = ".res";

    // ── 探測 ──────────────────────────────────────────────────────────

    public static GuiBridgeStatus Probe(string iRepoRoot)
    {
        var aStatus = new GuiBridgeStatus();
        string aHb = SenatePaths.GuiHeartbeat(iRepoRoot);
        if (!File.Exists(aHb)) { aStatus.Error = "心跳檔不存在"; return aStatus; }
        try { aStatus.Heartbeat = GuiHeartbeat.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aHb))); }
        catch (Exception e) { aStatus.Error = $"心跳檔讀不了：{e.GetType().Name}: {e.Message}"; }
        return aStatus;
    }

    /// <summary>窗沒在跑時該印的那幾行 —— ⛔ 一句話都不要改成「已改用文字模式」。</summary>
    public static void PrintNotRunning(GuiBridgeStatus iStatus, Action<string> iErr)
    {
        iErr("✗ 常駐視窗沒在跑 —— 這道指令問的是「那顆窗身上有哪些可以點」，沒有窗就沒有答案。");
        if (iStatus.Error != null) iErr($"  心跳：{iStatus.Error}");
        else if (iStatus.Heartbeat?.AgeSeconds() is { } aAge)
            iErr($"  心跳停了 {aAge:0.0} 秒（判死門檻 {HeartbeatStaleSeconds:0.#} 秒）⇒ 窗可能卡住或已被關掉");
        iErr("  開窗：senate ui --window");
        iErr("  ⛔ **不會**自動退回 CLI 自己畫一次 —— 那棵樹描述的是另一個宇宙的畫面。");
        iErr("     真的要那一份（headless／CI）就顯式帶 --local，它會在輸出開頭說明自己不是窗上的畫面。");
    }

    // ── CLI 端：送一次請求，等回應 ────────────────────────────────────

    /// <summary>
    /// 送出請求並等回應；逾時回 null。
    /// <para>⚠ 呼叫端要先 <see cref="Probe"/> 確認窗活著 —— 本函式**不替你判**，
    /// 因為「窗沒在跑」與「窗在跑但這一筆逾時」該印的話不一樣，而它們的差別只有呼叫端知道要怎麼說。</para>
    /// </summary>
    public static GuiResponse? Send(string iRepoRoot, GuiRequest iRequest, int iTimeoutMs = RequestTimeoutMs)
    {
        string aDir = SenatePaths.GuiBridgeDir(iRepoRoot);
        Directory.CreateDirectory(aDir);

        string aId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}";
        string aReq = Path.Combine(aDir, aId + RequestExt);
        string aRes = Path.Combine(aDir, aId + ResponseExt);

        // ⚠ 原子落檔：先寫 .tmp 再 Move。直接寫 .req 的話 Watcher 可能在只寫了一半時就讀走它，
        //   而「JSON 被截斷」與「請求內容真的長這樣」在對面讀起來都只是一次解析失敗。
        string aTmp = aReq + ".tmp";
        File.WriteAllText(aTmp, iRequest.ToJson().ToJson());
        File.Move(aTmp, aReq, overwrite: true);

        var aClock = System.Diagnostics.Stopwatch.StartNew();
        while (aClock.ElapsedMilliseconds < iTimeoutMs)
        {
            if (File.Exists(aRes))
            {
                try
                {
                    string aText = File.ReadAllText(aRes);
                    TryDelete(aRes);
                    return GuiResponse.FromJson(SCP_JsonParser.Parse(aText));
                }
                catch (IOException) { /* 還在寫 —— 下一圈再讀 */ }
            }
            Thread.Sleep(15);
        }

        // 逾時：把自己的請求收掉，不要留一顆孤兒讓窗晚點才回、而回給沒有人
        TryDelete(aReq);
        return null;
    }

    static void TryDelete(string iPath)
    {
        try { if (File.Exists(iPath)) File.Delete(iPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ── 窗端：撿請求、寫回應 ──────────────────────────────────────────

    /// <summary>撿一筆待處理的請求檔路徑；沒有就回 null。⚠ 這支**不由每幀呼叫**，見檔頭數值影響。</summary>
    public static string? TakePendingRequest(string iRepoRoot)
    {
        string aDir = SenatePaths.GuiBridgeDir(iRepoRoot);
        if (!Directory.Exists(aDir)) return null;
        string[] aFiles;
        try { aFiles = Directory.GetFiles(aDir, "*" + RequestExt); }
        catch (DirectoryNotFoundException) { return null; }
        if (aFiles.Length == 0) return null;
        Array.Sort(aFiles, StringComparer.Ordinal);   // 時間戳在檔名前綴 ⇒ 字典序即到達序
        return aFiles[0];
    }

    public static void WriteResponse(string iRequestPath, GuiResponse iResponse)
    {
        string aRes = Path.ChangeExtension(iRequestPath, ResponseExt);
        string aTmp = aRes + ".tmp";
        File.WriteAllText(aTmp, iResponse.ToJson().ToJson());
        File.Move(aTmp, aRes, overwrite: true);
        TryDelete(iRequestPath);
    }

    /// <summary>開窗時清掉上一顆窗留下的殘骸 —— 不清的話第一道指令可能收到上一輪那筆的回應。</summary>
    public static void CleanupStale(string iRepoRoot)
    {
        string aDir = SenatePaths.GuiBridgeDir(iRepoRoot);
        if (!Directory.Exists(aDir)) return;
        try { foreach (string aFile in Directory.GetFiles(aDir)) TryDelete(aFile); }
        catch (DirectoryNotFoundException) { }
    }
}
