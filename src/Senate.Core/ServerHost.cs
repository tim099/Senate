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

    // 區塊職責：收到停止請求之後，**等手上的 lane 跑完**再退（TASK-0209 A6，Tim 2026-09-14 選 (乙)）。
    // 物理意義：被切掉的那條 lane 的 `.running` 會留著，下一顆 Server 啟動時翻回 pending **續跑**
    //          ⇒ **那筆 cmd 會被再執行一次**。銀行搬進來之後，那可能正是一筆扣款。
    //          ⇒ 所以上限到了不是「切掉就走」，是**拒絕退出並指名還在跑的 lane** ——
    //            讓「誰擋著」變成讀數，而不是讓那一筆安靜地被做第二次。
    // 數值影響：重啟變慢（最壞 ＝ 最久那條 lane 的執行時間）。這是刻意換來的。
    // ⚠ 排乾期間**仍然跳心跳** —— 不跳的話 `server stop` 那側會判它掛了而去 kill，
    //   那就把「排乾」變成「硬切」，比 (甲) 還糟。
    /// <summary>收到 stop 請求後等 lane 跑完的上限；到了**不硬切**，改成拒絕退出並報告。</summary>
    public const int DrainMaxSeconds = 30;

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
        // 🩸 **自己的輸出自己落檔**（TASK-0209 A5，2026-09-14 兩次實測換來的形狀）：
        //   ① 先是共用一份 `_server_start.log`，`WriteAllText` 互相**截斷覆蓋**。
        //   ② 改成一顆一份之後仍然是空的：輸出原本接成 **parent 的管線**，
        //      而 parent（CLI）跑完就退 ⇒ **非同步讀取器跟著死**，
        //      輸掉單例鎖那幾顆的「拿不到單例鎖」一個字都沒留下。
        //   ⇒ 兩次的症狀一模一樣：**要查「它為什麼沒起來」的時候，那幾行剛好不在。**
        //   ⇒ 所以落檔的責任歸 child：它活多久 log 就寫多久，跟誰拉起它無關。
        // ⚠ 手動 `senate server start` 也照寫（終端機看得到 ＋ 檔案留得住，兩者不互斥）。
        string aStartLog = StartLogPath(iRepoRoot, Environment.ProcessId);
        var aLogLock = new object();
        void Tee(string iLine)
        {
            try { lock (aLogLock) File.AppendAllText(aStartLog, iLine + Environment.NewLine); }
            catch (Exception) { /* log 寫不進去不該把 Server 拖下水 */ }
        }
        try
        {
            Directory.CreateDirectory(SenatePaths.RuntimeDir(iRepoRoot));
            File.WriteAllText(aStartLog,
                $"# senate server　pid={Environment.ProcessId}　{DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                + Environment.NewLine);
        }
        catch (Exception) { /* 建不起來就只剩終端機那一份；⛔ 不因此拒絕啟動 */ }
        Action<string> aOut0 = iOut, aErr0 = iErr;
        iOut = s => { aOut0(s); Tee(s); };
        iErr = s => { aErr0(s); Tee("⚠ " + s); };

        if (!SCP_ProcessRegistry.Enabled)
        {
            iErr("✗ SCP_ProcessRegistry 沒有 Configure ⇒ Server 沒辦法登記自己，拒絕啟動（沒登記的常駐 ＝ 沒人管得到的孤兒）。");
            return 70;
        }

        // ── 單例閘（TASK-0209 A2）────────────────────────────────────────
        // 🩸 在這道鎖之前，唯一性靠的是下面那個 `Probe()` ⇒ 檢查 ⇒ `Register(iAllowMultiple: true)`，
        //    而**中間沒有任何互斥**：兩顆同時起來會雙雙通過 Probe，然後雙雙登記成功。
        //    手動啟動時幾乎踩不到（人不會在同一毫秒按兩次）——
        //    ⚠ **而自動啟動（A4）會把它變成常態**：N 顆 CLI 同時發現「沒在跑」就 N 顆一起 start。
        //    ⇒ 所以 A2 必須先落地並驗過，A4 才接得上去；反過來是自動製造我們要防的競態。
        // 設計取捨：用 **OS advisory lock（獨佔開檔）**，不是 pid 鎖檔。
        //    差別只有一格，而那一格是關鍵：**process 死掉 OS 自動放**。
        //    pid 鎖檔要靠程式記得刪 ⇒ 當機／強制關掉就留下死鎖，而銀行搬進來之後，
        //    被鎖住的是跨日保管費／領薪／發文計酬那些**沒有人在看**的自動流程。
        //    ⛔ 也不用 Named Mutex：實務上 Windows-only，而這裡沒有非它不可的理由。
        // ⚠ 鎖**握到 process 結束**（不是只包住 Probe+Register）：
        //    「誰是唯一那顆」由鎖回答，「那顆是誰」由 registry 回答 —— 兩個不同的問題，各自一個機制。
        FileStream? aSingleton = TryAcquireSingletonLock(iRepoRoot, out string aLockWhy);
        if (aSingleton == null)
        {
            iErr($"✗ 拿不到單例鎖 ⇒ 拒絕啟動：{aLockWhy}");
            iErr("  多半是另一顆 Server 正在啟動或已在跑：senate server status（看它）／senate server stop（收掉它）。");
            return 1;
        }
        using FileStream aSingletonHold = aSingleton;

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
        // ⚠ `registered_by` 要說**真的是誰起的**：現在有兩個入口（`senate server start` 與
        //   `Senate.Server.exe`，TASK-0209 A1）。寫死一個的話，ProcessAdminPage 與 status 上
        //   那一行會**指向一個沒有發生過的動作** —— 而它讀起來完全正常，
        //   人會照著去 grep 一個不存在的呼叫端。⇒ 由執行檔名推導，不寫死。
        string aEntry = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
        string aBy = aEntry.Equals("Senate.Server", StringComparison.OrdinalIgnoreCase)
                     ? "Senate.Server.exe"
                     : "senate server start";
        SCP_ProcessRecord? aRec = SCP_ProcessRegistry.Register(aSelf, Tag,
            $"Senate 常駐 Server（build {aBuild}）", aBy, iAllowMultiple: true);
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

        // 身分旗標：從這一行起，本 process 裡的 ServerDelegateCmd 走「本體」那條路。
        ServerContext.InServer = true;
        ServerContext.Pid = aSelf.Id;
        ServerContext.BuildId = aBuild;
        string aServerRoot = SenatePaths.ServerRoot(iRepoRoot);
        Directory.CreateDirectory(aServerRoot);
        var aExecutor = new ServerExecutor(aServerRoot, iOut, iErr);
        // 先把 Cmd 目錄掃好再開 lane：Discover 有鎖（正解），這一行是讓第一筆 Cmd 不必付反射那幾百毫秒。
        SCP.Core.Cmd.SCP_CmdRegistry.Discover();
        int aOrphans = aExecutor.RecoverOrphans();

        bool aCancel = false;
        ConsoleCancelEventHandler aOnCancel = (_, e) => { e.Cancel = true; aCancel = true; };
        Console.CancelKeyPress += aOnCancel;

        iOut($"⤷ senate server 啟動 @ pid={aSelf.Id}　build={aBuild}　registry={SCP_ProcessRegistry.RegistryDir}");
        iOut($"· 心跳：{aHbPath}（每 {HeartbeatIntervalMs} ms）　停止：Ctrl+C 或 `senate server stop`");
        if (aBuild == "unversioned")
            iOut("⚠ build=unversioned ⇒ 這是 `dotnet run`（Debug DLL），不是 publish 出來的 exe。CLI 那側會判成版本不符。");
        iOut($"· 執行器：{aServerRoot}（queues/<lane>/ 同 lane 串行、跨 lane 並行）"
             + (aOrphans > 0 ? $"　⚠ 翻回 {aOrphans} 條孤兒 lane" : ""));

        string aExitWhy;
        try
        {
            while (true)
            {
                aHb.BeatAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                WriteAtomic(aHbPath, SCP_JsonWriter.Write(aHb.ToJson()) + "\n");
                if (aCancel) { aExitWhy = "Ctrl+C"; break; }
                if (File.Exists(aStopReq))
                {
                    // 先消費掉請求：不刪的話下一圈又命中，而使用者會看到同一段排乾訊息一直重印。
                    TryDelete(aStopReq);
                    if (DrainBeforeExit(aExecutor, aHb, aHbPath, iOut, iErr))
                    {
                        aExitWhy = "收到 `senate server stop` 的請求";
                        break;
                    }
                    // (乙)：排不乾 ⇒ **拒絕退出**，回到服務迴圈繼續跑。
                    // ⛔ 不硬切：被切的那條會在下一顆 Server 啟動時續跑 ＝ 那筆 cmd 做第二次。
                    continue;
                }
                aExecutor.Tick();
                Thread.Sleep(HeartbeatIntervalMs);
            }
        }
        finally
        {
            Console.CancelKeyPress -= aOnCancel;
            // Ctrl+C 那條走到這裡時還沒排乾過（使用者要求立刻停，不能拒絕他）——
            // 仍然給它同一個上限，**但排不乾就照實說**，不假裝收尾乾淨。
            int aLeft = aExecutor.RunningLaneCount == 0
                        ? 0
                        : aExecutor.Drain(TimeSpan.FromSeconds(DrainMaxSeconds),
                                          aLanes => BeatAndReport(aHb, aHbPath, aLanes, iOut));
            if (aLeft == 0 && aExecutor.Completed > 0) iOut($"· 執行器收尾：本次共跑 {aExecutor.Completed} 筆");
            ServerContext.InServer = false;
            // 三件遺物一起收；任何一件收不掉都要說 —— 留下來的心跳檔會讓下一次 status 讀到一個「剛剛還在跳」的假象。
            TryDelete(aHbPath, iErr);
            TryDelete(aStopReq, iErr);
            SCP_ProcessRegistry.Unregister(aSelf.Id, Tag);
        }
        iOut($"· Server 已停（{aExitWhy}）　pid={aSelf.Id}");
        return 0;
    }

    /// <summary>單例鎖等多久（毫秒）。短 —— 這道鎖只保護「檢查＋登記」那一瞬間，等久了代表對方是**在跑**不是在啟動。</summary>
    public const int SingletonLockWaitMs = 3000;

    /// <summary>某顆 Server（pid）的啟動 log 路徑 —— 自動啟動那側要指得出**哪一份**。</summary>
    public static string StartLogPath(string iRepoRoot, int iPid)
        => Path.Combine(SenatePaths.RuntimeDir(iRepoRoot), $"_server_start_{iPid}.log");

    /// <summary>
    /// 拿單例鎖：獨佔開檔（<see cref="FileShare.None"/>），**握著 handle ＝ 持有鎖**，
    /// process 死掉由 OS 釋放（⛔ 所以沒有 stale lock 要偵測，也不必猜 pid）。
    /// <para>拿不到回 null，<paramref name="oWhy"/> 一定有話說。檔案本身留著沒關係 —— 鎖是 handle 不是檔案存在。</para>
    /// </summary>
    static FileStream? TryAcquireSingletonLock(string iRepoRoot, out string oWhy)
    {
        oWhy = "";
        string aPath = Path.Combine(SenatePaths.RuntimeDir(iRepoRoot), "_server_singleton.lock");
        try { Directory.CreateDirectory(SenatePaths.RuntimeDir(iRepoRoot)); }
        catch (Exception e) { oWhy = $"建不了 runtime 目錄：{e.Message}"; return null; }

        var aSw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                var aFs = new FileStream(aPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                // 寫 pid 純粹是給人看的（誰握著）—— ⛔ 互斥不靠它，靠的是這個 handle。
                try
                {
                    byte[] aBytes = System.Text.Encoding.UTF8.GetBytes(
                        Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n");
                    aFs.SetLength(0);
                    aFs.Write(aBytes, 0, aBytes.Length);
                    aFs.Flush();
                }
                catch (Exception) { /* 寫不進去不影響互斥；不要為了一行註解放掉鎖 */ }
                return aFs;
            }
            catch (IOException e)
            {
                if (aSw.ElapsedMilliseconds >= SingletonLockWaitMs)
                {
                    oWhy = $"{aPath} 被占用超過 {SingletonLockWaitMs} ms（{e.GetType().Name}）";
                    return null;
                }
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException e)
            {
                oWhy = $"沒有權限開 {aPath}：{e.Message}";
                return null;
            }
        }
    }

    /// <summary>
    /// 收到 stop 請求後排乾。排乾了回 true（可以退）；上限到了還沒排乾回 false（**拒絕退出**）。
    /// </summary>
    static bool DrainBeforeExit(ServerExecutor iExecutor, ServerHeartbeat iHb, string iHbPath,
                                Action<string> iOut, Action<string> iErr)
    {
        if (iExecutor.RunningLaneCount == 0) return true;

        iOut($"· 收到停止請求 ⇒ **停止收新工作**，等手上的 {iExecutor.RunningLaneCount} 條 lane 跑完"
             + $"（上限 {DrainMaxSeconds}s）…");
        int aLeft = iExecutor.Drain(TimeSpan.FromSeconds(DrainMaxSeconds),
                                    aLanes => BeatAndReport(iHb, iHbPath, aLanes, iOut));
        if (aLeft == 0)
        {
            iOut("· 已排乾（0 lane 在跑）⇒ 退出");
            return true;
        }

        // ⛔ 這裡**刻意不退**。硬切的話那條 lane 的 .running 會留著、下一顆 Server 翻回 pending 續跑
        //    ⇒ 同一筆 cmd 被執行第二次；若它是扣款而且沒帶冪等鍵，那就是扣兩次。
        iErr($"✗ **拒絕退出**：{aLeft} 條 lane 還在跑 —— {string.Join(", ", iExecutor.RunningLanes)}");
        iErr("  Server 仍在服務（已重新開始收工作）。出口三選一：");
        iErr("   ① 等那幾條跑完，再送一次 `senate server stop`");
        iErr("   ② 去看它們在跑什麼：`<Server 根>/queues/<lane>/*.running`");
        iErr("   ③ 真的要現在停 ⇒ `senate server stop` 等不到會 kill（⚠ 被切的那筆下次會**再跑一次**）");
        return false;
    }

    /// <summary>排乾期間每一圈：跳心跳（不跳會被 stop 那側判掛掉而 kill）＋ 每 2 秒印一次還在跑的是誰。</summary>
    static void BeatAndReport(ServerHeartbeat iHb, string iHbPath, IReadOnlyList<string> iLanes,
                              Action<string> iOut)
    {
        iHb.BeatAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        try { WriteAtomic(iHbPath, SCP_JsonWriter.Write(iHb.ToJson()) + "\n"); }
        catch (Exception) { /* 排乾期間心跳寫不出去不該打斷排乾；stop 那側會自己判過期 */ }

        // 印得太密會把真正的訊息洗掉；2 秒一次夠人看見「它在動，不是卡住」。
        DateTime aNow = DateTime.UtcNow;
        if ((aNow - s_LastDrainReportUtc).TotalSeconds < 2.0) return;
        s_LastDrainReportUtc = aNow;
        iOut($"  … 還在跑 {iLanes.Count} 條：{string.Join(", ", iLanes)}");
    }

    static DateTime s_LastDrainReportUtc = DateTime.MinValue;

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

        // 🩸 (乙) 的代價落在這裡（TASK-0209 A6）：Server 那側收到請求後會**先排乾**（上限
        //    DrainMaxSeconds），而排乾期間它是活的、心跳照跳。若這裡照舊只等 StopGraceMs 就 kill，
        //    **排乾會被自己的 stop 指令硬切掉** —— 那比不排乾更糟（下一顆 Server 會把那筆重跑一次）。
        //    ⇒ 判準改成「**它還在跳心跳就繼續等**」，而不是「等夠 N 秒就砍」：
        //      · 心跳新鮮 ＝ 它活著而且在做事 ⇒ 等（最多 StopGraceMs + 排乾上限，留一點餘裕）
        //      · 心跳過期 ＝ 它真的卡死了 ⇒ 才 kill
        //    ⚠ 「還在排乾」與「卡死了」在 registry 那一層**同形**（兩者都是 Alive）——
        //      分得開它們的只有心跳的新鮮度。
        int aMaxWaitMs = StopGraceMs + (DrainMaxSeconds + 5) * 1000;
        var aSw = Stopwatch.StartNew();
        bool aSaidDraining = false;
        while (aSw.ElapsedMilliseconds < aMaxWaitMs)
        {
            Thread.Sleep(200);
            if (SCP_ProcessRegistry.Validate(aRec) == SCP_ProcessStatus.Dead)
            {
                iOut($"✓ Server 已自行退出（pid={aRec.Pid}，{aSw.ElapsedMilliseconds} ms）");
                TryDelete(aStopReq);
                return 0;
            }
            if (aSw.ElapsedMilliseconds < StopGraceMs) continue;

            // 過了基本寬限還沒退 ⇒ 看它是不是還在跳（在排乾），而不是直接砍。
            ServerStatus aNow = Probe(iRepoRoot);
            double? aAge = aNow.Heartbeat?.AgeSeconds();
            if (aAge != null && aAge.Value <= HeartbeatStaleSeconds)
            {
                if (!aSaidDraining)
                {
                    aSaidDraining = true;
                    iOut($"· 它還在跳心跳（{aAge.Value:0.0}s 前）⇒ 判定**正在排乾**，繼續等"
                         + $"（最多再 {(aMaxWaitMs - StopGraceMs) / 1000} 秒；那一側會印還在跑的 lane）");
                }
                continue;
            }
            iErr($"⚠ 心跳{(aAge == null ? "讀不到" : $"已過期 {aAge.Value:0.0}s")} ⇒ 不是在排乾，是停住了。");
            break;
        }

        // 等不到 ⇒ kill。KillRegistered 會再做一次身分複驗，PID 易主就拒絕。
        // ⚠ 被 kill 的那條 lane 的 `.running` 會留著 ⇒ 下一顆 Server 啟動時翻回 pending **續跑**
        //   ＝ 那筆 cmd 會被執行第二次。沒帶冪等鍵的寫入端要當成「可能做了兩次」處理。
        if (SCP_ProcessRegistry.KillRegistered(aRec, out string aErr))
        {
            iOut($"✓ Server 沒在 {aSw.ElapsedMilliseconds / 1000} 秒內自退，已 kill（pid={aRec.Pid}）"
                 + "　⚠ 手上那筆 cmd 會在下一顆 Server 啟動時**再跑一次**");
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
