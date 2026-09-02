// 區塊職責：**Senate 常駐 Server 的生命週期** —— start（前景常駐）／stop（請求停、等不到才 kill）／status。
// 物理意義：TASK-0102。Server 是 TASK-0100 那條線的「單一 process」容器：酒館 seq／銀行 ledger
//           之後要搬進來的前提是**只有一顆 process 在寫**。本檔管的是「那一顆」怎麼被認出、被停、被看見；
//           它**不執行任何 Cmd**（執行器是 TASK-0103），所以現在 start 起來就是一顆會跳心跳的空殼。
//           Tim 2026-09-02 拍板：**A 前景**（掛在終端機，Ctrl+C 就停，log 就在眼前）、**永駐**（不 idle 自退）、
//           **手動啟動**（CLI 不自動 spawn；委派 Cmd 撞到沒 Server 只印怎麼啟動）。
// 數值影響：寫 SenateData/runtime/ 兩個檔（心跳 json 每 0.5 秒 atomic replace、停止請求檔）；
//           自我登記進 SCP_ProcessRegistry（tag `senate_server`）；退出時三個都收掉。
//
// 🩸 三格血證決定了形狀：
//   ① **身分不是 pid 檔**：pid 會被 OS 回收再發 ⇒ 認人一律走 SCP_ProcessRegistry 三重身分（pid＋name＋start time），
//      「pid 檔存在」與「Server 活著」是兩件事（UCL 2026-07-27 那套的理由，這裡照用）。
//   ② **兩顆 exe 長得一模一樣**：Server 是舊 exe、CLI 是新 exe 時，兩本帳在畫面上同形（Setup_And_Build §「先 build 再對 exe」）。
//      ⇒ 心跳裡帶 build id，status 對不上就明說「先 stop 再 start」，不照跑。
//   ③ **exe 會被常駐的自己鎖住**（D10：覆寫 publish 出來的 exe 撞鎖）⇒ build 腳本 publish 前先 `server stop`。
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using SCP.Core.Json;
using SCP.Core.Proc;

namespace Senate.Core;

/// <summary>心跳檔的內容（Server 每 <see cref="ServerHost.HeartbeatIntervalMs"/> 毫秒覆寫一次）。</summary>
public sealed class ServerHeartbeat
{
    public int Pid;
    public string BuildId = "";
    public string StartedAtUtc = "";
    public string BeatAtUtc = "";

    public SCP_JsonData ToJson()
    {
        var aData = SCP_JsonData.NewObject();
        aData.Set("pid", SCP_JsonData.NewNumber(Pid));
        aData.Set("build_id", SCP_JsonData.NewString(BuildId));
        aData.Set("started_at_utc", SCP_JsonData.NewString(StartedAtUtc));
        aData.Set("beat_at_utc", SCP_JsonData.NewString(BeatAtUtc));
        aData.Set("schema_version", SCP_JsonData.NewNumber(1));
        return aData;
    }

    public static ServerHeartbeat? FromJson(SCP_JsonData? iData)
    {
        if (iData == null || !iData.Exists) return null;
        return new ServerHeartbeat
        {
            Pid = iData.GetInt("pid", 0),
            BuildId = iData.GetString("build_id", ""),
            StartedAtUtc = iData.GetString("started_at_utc", ""),
            BeatAtUtc = iData.GetString("beat_at_utc", ""),
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

/// <summary>`server status` 的一次讀數 —— 三個來源分開放，呼叫端自己決定怎麼印。</summary>
public sealed class ServerStatus
{
    /// <summary>registry 裡 tag 吻合且身分 Alive 的那一筆；null ＝ 沒有活著的 Server。</summary>
    public SCP_ProcessRecord? Alive;

    /// <summary>registry 裡 tag 吻合但身分驗不出來的（Unknown）—— 不能當活著，也不能當死了。</summary>
    public List<SCP_ProcessRecord> Unverifiable = new();

    /// <summary>心跳檔內容；null ＝ 檔不在或讀不了（<see cref="HeartbeatError"/> 有原因）。</summary>
    public ServerHeartbeat? Heartbeat;
    public string? HeartbeatError;

    /// <summary>這顆 CLI 自己的 build id（跟心跳裡的比）。</summary>
    public string MyBuildId = "";

    public bool IsRunning => Alive != null;

    /// <summary>心跳還新鮮嗎（Server 活著但心跳停了 ＝ 卡住，不是正常）。</summary>
    public bool HeartbeatFresh
    {
        get
        {
            double? aAge = Heartbeat?.AgeSeconds();
            return aAge.HasValue && aAge.Value <= ServerHost.HeartbeatStaleSeconds;
        }
    }

    /// <summary>Server 跑的是不是跟我同一顆 exe。⚠ 只有兩邊都有 build id 才有意義；任一邊是 unversioned 也算不符（那正是 Debug vs exe 那兩本帳）。</summary>
    public bool BuildMatches => Heartbeat != null && Heartbeat.BuildId.Length > 0
                                && string.Equals(Heartbeat.BuildId, MyBuildId, StringComparison.Ordinal);
}

public static class ServerHost
{
    /// <summary>registry 裡的 tag（ProcessAdminPage 那張表上看到的名字）。</summary>
    public const string Tag = "senate_server";

    public const int HeartbeatIntervalMs = 500;

    /// <summary>心跳超過這個秒數視為停了（對照 Unity 那側 `_heartbeat.txt` 的 4 秒判準）。</summary>
    public const double HeartbeatStaleSeconds = 4.0;

    /// <summary>stop 請求送出後等 Server 自己退的時間；等不到才 kill。</summary>
    public const int StopGraceMs = 5000;

    /// <summary>
    /// 這顆執行檔的 build id ＝ AssemblyInformationalVersion（由 build.sh／build.ps1 在 publish 時塞入 git SHA＋時間）。
    /// <para>⚠ `dotnet run`（Debug DLL）沒有那個屬性或是 SDK 預設的 `1.0.0` ⇒ 回 <c>unversioned</c> ——
    /// 這不是缺陷，是**定語**：它讓「Debug 在跑」跟「exe 在跑」在心跳裡分得出來。</para>
    /// </summary>
    public static string BuildId
    {
        get
        {
            string? aVer = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(aVer) || aVer == "1.0.0" || aVer.StartsWith("1.0.0+", StringComparison.Ordinal))
                return "unversioned";
            return aVer;
        }
    }

    // ── status ────────────────────────────────────────────────────────

    public static ServerStatus Probe(string iRepoRoot)
    {
        var aStatus = new ServerStatus { MyBuildId = BuildId };
        foreach (var aKv in SCP_ProcessRegistry.LoadAllWithStatus())
        {
            if (!string.Equals(aKv.Key.Tag, Tag, StringComparison.Ordinal)) continue;
            if (aKv.Value == SCP_ProcessStatus.Alive) aStatus.Alive ??= aKv.Key;
            else if (aKv.Value == SCP_ProcessStatus.Unknown) aStatus.Unverifiable.Add(aKv.Key);
            // Dead / PidReused：CleanupStale 會收；這裡不列 —— 列了會讓「有一筆死的」看起來像「有東西」。
        }

        string aHb = SenatePaths.ServerHeartbeat(iRepoRoot);
        if (!File.Exists(aHb)) { aStatus.HeartbeatError = "心跳檔不存在"; return aStatus; }
        try { aStatus.Heartbeat = ServerHeartbeat.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aHb))); }
        catch (Exception e) { aStatus.HeartbeatError = $"心跳檔讀不了：{e.GetType().Name}: {e.Message}"; }
        return aStatus;
    }

    // ── start（前景，永駐）─────────────────────────────────────────────

    /// <summary>
    /// 前景常駐直到 Ctrl+C 或收到停止請求。回傳 exit code。
    /// <para>⚠ 已有 Alive 的 Server ⇒ 拒絕第二顆（exit 1）並印它的 pid：兩顆 Server 就是兩個寫入者，
    /// 那正是本檔存在要防的事。Unknown 的也拒絕 —— 認不出來不等於沒有。</para>
    /// </summary>
    public static int RunForeground(string iRepoRoot, Action<string> iOut, Action<string> iErr)
    {
        if (!SCP_ProcessRegistry.Enabled)
        {
            iErr("✗ SCP_ProcessRegistry 沒有 Configure ⇒ Server 沒辦法登記自己，拒絕啟動（沒登記的常駐 ＝ 沒人管得到的孤兒）。");
            return 70;
        }
        ServerStatus aExisting = Probe(iRepoRoot);
        if (aExisting.Alive != null)
        {
            iErr($"✗ 已有一顆 Server 在跑：pid={aExisting.Alive.Pid}　build={aExisting.Heartbeat?.BuildId ?? "?"}"
                 + $"　start={aExisting.Alive.StartTimeUtcText}");
            iErr("  出口：senate server status（看它）／senate server stop（收掉它）。⛔ 不會自動接管 —— 兩顆 Server 就是兩個寫入者。");
            return 1;
        }
        if (aExisting.Unverifiable.Count > 0)
        {
            iErr($"✗ registry 裡有 {aExisting.Unverifiable.Count} 筆 `{Tag}` 身分驗不出來（pid="
                 + string.Join(",", aExisting.Unverifiable.ConvertAll(r => r.Pid.ToString())) + "）—— 認不出來不等於沒有。");
            iErr("  出口：ProcessAdminPage（`senate ui --click home/open/process`）看那幾筆，人工判斷後移除記錄再 start。");
            return 1;
        }

        string aStopReq = SenatePaths.ServerStopRequest(iRepoRoot);
        string aHbPath = SenatePaths.ServerHeartbeat(iRepoRoot);
        Directory.CreateDirectory(SenatePaths.RuntimeDir(iRepoRoot));
        // 舊的停止請求是上一顆的遺物 —— 不清掉會讓這一顆起來就退，而且退得理直氣壯。
        TryDelete(aStopReq);

        using Process aSelf = Process.GetCurrentProcess();
        string aBuild = BuildId;
        SCP_ProcessRecord? aRec = SCP_ProcessRegistry.Register(aSelf, Tag,
            $"Senate 常駐 Server（build {aBuild}）", "senate server start", iAllowMultiple: true);
        if (aRec == null)
        {
            iErr("✗ 登記失敗（Warn 那條有原因）⇒ 拒絕啟動：沒登記的 Server 沒人停得掉。");
            return 70;
        }

        var aHb = new ServerHeartbeat
        {
            Pid = aSelf.Id,
            BuildId = aBuild,
            StartedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };

        bool aCancel = false;
        ConsoleCancelEventHandler aOnCancel = (_, e) => { e.Cancel = true; aCancel = true; };
        Console.CancelKeyPress += aOnCancel;

        iOut($"⤷ senate server 啟動 @ pid={aSelf.Id}　build={aBuild}　registry={SCP_ProcessRegistry.RegistryDir}");
        iOut($"· 心跳：{aHbPath}（每 {HeartbeatIntervalMs} ms）　停止：Ctrl+C 或 `senate server stop`");
        if (aBuild == "unversioned")
            iOut("⚠ build=unversioned ⇒ 這是 `dotnet run`（Debug DLL），不是 publish 出來的 exe。CLI 那側會判成版本不符。");
        iOut("· 執行器尚未接上（TASK-0103）—— 目前只跳心跳。");

        string aExitWhy;
        try
        {
            while (true)
            {
                aHb.BeatAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteAtomic(aHbPath, SCP_JsonWriter.Write(aHb.ToJson()) + "\n");
                if (aCancel) { aExitWhy = "Ctrl+C"; break; }
                if (File.Exists(aStopReq)) { aExitWhy = "收到 `senate server stop` 的請求"; break; }
                Thread.Sleep(HeartbeatIntervalMs);
            }
        }
        finally
        {
            Console.CancelKeyPress -= aOnCancel;
            // 三件遺物一起收；任何一件收不掉都要說 —— 留下來的心跳檔會讓下一次 status 讀到一個「剛剛還在跳」的假象。
            TryDelete(aHbPath, iErr);
            TryDelete(aStopReq, iErr);
            SCP_ProcessRegistry.Unregister(aSelf.Id, Tag);
        }
        iOut($"· Server 已停（{aExitWhy}）　pid={aSelf.Id}");
        return 0;
    }

    // ── stop ──────────────────────────────────────────────────────────

    /// <summary>
    /// 請 Server 自己退；<see cref="StopGraceMs"/> 內沒退才 kill（身分驗證過的才 kill）。
    /// 沒有在跑 ⇒ exit 0（冪等：build 腳本每次都呼叫它）。
    /// </summary>
    public static int Stop(string iRepoRoot, Action<string> iOut, Action<string> iErr)
    {
        ServerStatus aStatus = Probe(iRepoRoot);
        if (aStatus.Alive == null)
        {
            if (aStatus.Unverifiable.Count > 0)
            {
                iErr($"⚠ 沒有 Alive 的 Server，但有 {aStatus.Unverifiable.Count} 筆身分驗不出來的記錄 —— 沒動它們（不能 kill 認不出來的東西）。");
                return 1;
            }
            iOut("· 沒有 Server 在跑（沒有東西要停）。");
            // 遺物順手清：沒有活著的 Server 而心跳檔還在 ⇒ 那是上一顆沒收乾淨的。
            if (File.Exists(SenatePaths.ServerHeartbeat(iRepoRoot)))
            {
                TryDelete(SenatePaths.ServerHeartbeat(iRepoRoot), iErr);
                iOut("· 清掉一份殘留的心跳檔（沒有活著的 Server 對得上它）。");
            }
            return 0;
        }

        SCP_ProcessRecord aRec = aStatus.Alive;
        string aStopReq = SenatePaths.ServerStopRequest(iRepoRoot);
        WriteAtomic(aStopReq, DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + "\n");
        iOut($"· 已送出停止請求 → pid={aRec.Pid}，等最多 {StopGraceMs / 1000} 秒…");

        var aSw = Stopwatch.StartNew();
        while (aSw.ElapsedMilliseconds < StopGraceMs)
        {
            Thread.Sleep(200);
            if (SCP_ProcessRegistry.Validate(aRec) == SCP_ProcessStatus.Dead)
            {
                iOut($"✓ Server 已自行退出（pid={aRec.Pid}，{aSw.ElapsedMilliseconds} ms）");
                TryDelete(aStopReq);
                return 0;
            }
        }

        // 等不到 ⇒ kill。KillRegistered 會再做一次身分複驗，PID 易主就拒絕。
        if (SCP_ProcessRegistry.KillRegistered(aRec, out string aErr))
        {
            iOut($"✓ Server 沒在 {StopGraceMs / 1000} 秒內自退，已 kill（pid={aRec.Pid}）");
            TryDelete(SenatePaths.ServerHeartbeat(iRepoRoot), iErr);
            TryDelete(aStopReq);
            return 0;
        }
        iErr($"✗ 停不掉：{aErr}（pid={aRec.Pid}）");
        return 1;
    }

    // ── 內部 ──────────────────────────────────────────────────────────

    /// <summary>先寫暫存再換檔 —— 讀的人不會讀到半個 json。</summary>
    static void WriteAtomic(string iPath, string iText)
    {
        string aTmp = iPath + ".tmp";
        File.WriteAllText(aTmp, iText);
        File.Move(aTmp, iPath, overwrite: true);
    }

    static void TryDelete(string iPath, Action<string>? iErr = null)
    {
        try { if (File.Exists(iPath)) File.Delete(iPath); }
        catch (Exception e) { iErr?.Invoke($"⚠ 刪不掉 {iPath}：{e.GetType().Name}: {e.Message}"); }
    }
}
