// 區塊職責：把**常駐視窗**變成第三個委派宿主 —— 撿 CLI 的請求、在真窗上執行、把畫完的樹寫回去。
// 物理意義：TASK-0214。前兩個宿主是 Unity Editor 的 Watcher 與 `ServerHost`；這是同一個形狀的第三次，
//           所以協議沿用 `GuiBridge`（檔案進、檔案出），⛔ 不發明第四套。
//           ⭐ 它存在的理由不是「多一個功能」，是讓 `senate ui --list` 描述的**就是那顆窗**：
//           同一棵樹只有一個產生者 ⇒「文字說有、窗上沒有」這種兩個宇宙的失效不再存在。
// 數值影響：⭐ **閒置時每幀的成本 ＝ 一次 `volatile bool` 讀取**（Tim 2026-09-15 拍板：文字樹走額外指令）。
//           目錄變動由 `FileSystemWatcher` 推過來 ⇒ ⛔ 不做每幀 `Directory.GetFiles`、
//           ⛔ 不在窗裡常態保存一份樹（那是把一次性成本改成常駐成本，而且那份快取會過期）。
//           心跳跑在**自己的 thread** 上 ⇒ 每 0.5 秒一次 IO 不落在 render loop 上。
//           ⇒ 驗收讀數在 TASK-0214 ⑧：閒置時 `--soak` 的 fps 不准有可量的退步。
using SCP.Core.Gui;
using Senate.Core;
using Senate.Desktop;

namespace Senate.Cli;

/// <summary>常駐視窗這一側的橋 —— 由 <c>RunWindow</c> 在互動模式下掛上。</summary>
sealed class GuiBridgeHost : IDisposable
{
    readonly string m_RepoRoot;
    readonly SenateWindow m_Window;
    readonly SCP_GuiStyle m_Style;
    readonly Func<string> m_PageKey;

    /// <summary>
    /// 要不要對外宣告「我就是那顆常駐窗」（＝寫心跳）。
    /// <para>⚠ 截圖／soak 的窗活幾百毫秒就沒了 —— 讓 CLI 指到它，等於指到一個正在消失的宿主。
    /// ⇒ 那兩種模式**掛橋但不宣告**：橋要掛，因為驗收 ⑧ 問的是「掛上橋之後閒置有沒有變慢」，
    /// 量一顆不含待測物的窗，那個綠燈跟沒量過一模一樣。</para>
    /// </summary>
    readonly bool m_Advertise;

    FileSystemWatcher? m_Watcher;
    Thread? m_HeartbeatThread;
    volatile bool m_Stop;

    /// <summary>⭐ render loop 每幀只讀這一格。Watcher 那條 thread 寫它。</summary>
    volatile bool m_Signal;

    // ── 一次請求的進行式 ──────────────────────────────────────────────
    // 🩸 為什麼要等幀：注入走的是 renderer 的跨幀狀態，**下一幀**才畫得出來。
    //   不等就回應的話，回去的是「按之前」那棵樹 —— 而它跟「按了沒反應」長得一模一樣，
    //   那正是這整張單要殺掉的同形。⇒ 等兩幀（一幀消化輸入、一幀把結果畫出來）。
    string? m_ReqPath;
    GuiRequest? m_Req;
    int m_WaitFrames;

    const int FramesAfterInject = 2;

    public GuiBridgeHost(string iRepoRoot, SenateWindow iWindow, SCP_GuiStyle iStyle,
                         Func<string> iPageKey, bool iAdvertise)
    {
        m_RepoRoot = iRepoRoot;
        m_Window = iWindow;
        m_Style = iStyle;
        m_PageKey = iPageKey;
        m_Advertise = iAdvertise;
    }

    public void Start()
    {
        // ⭐ 不宣告的窗（截圖／soak）**只掛 hook，不碰交換所**。
        // 🩸 這一格是實作到一半才想通的，而它的失效很貴：不擋的話，
        //   `senate ui --soak 10` 會在旁邊開第二個 Watcher 去搶常駐窗的請求
        //   —— 那筆請求會被一顆**十秒後就消失**的窗接走並回答，
        //   而回答看起來完全正常（同一份 code、同一棵樹的形狀）。
        //   ⇒ 同一個交換所兩個 Watcher 互搶，正是 SenatePaths.ServerRoot 註解裡那句話換一個宿主。
        // ⚠ 而 hook 照掛 ＝ 每幀成本原封不動 ⇒ 驗收 ⑧ 量到的是**掛了橋的窗**，不是一顆乾淨的窗。
        m_Window.OnFrameServed = Serve;
        if (!m_Advertise) return;

        string aDir = SenatePaths.GuiBridgeDir(m_RepoRoot);
        Directory.CreateDirectory(aDir);

        // ⚠ 開窗時先清乾淨 —— 上一顆窗死掉時留下的 .req／.res 會讓第一道指令收到上一輪的答案，
        //   而「舊答案」跟「新答案」在檔案上長得一模一樣。
        GuiBridge.CleanupStale(m_RepoRoot);

        m_Watcher = new FileSystemWatcher(aDir, "*" + GuiBridge.RequestExt)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        m_Watcher.Created += (_, _) => m_Signal = true;
        m_Watcher.Renamed += (_, _) => m_Signal = true;   // Send() 是 .tmp → Move ⇒ 到達事件是 Renamed
        m_Watcher.Changed += (_, _) => m_Signal = true;

        // ⚠ Watcher 會漏（緩衝區滿、網路磁碟、事件在我們掛上之前就發生了）。
        //   ⇒ 開場先掃一次，並且**逾時那一側有 CLI 自己的 10 秒上限** —— 不做每幀輪詢來補它。
        m_Signal = true;

        m_HeartbeatThread = new Thread(HeartbeatLoop) { IsBackground = true, Name = "senate-gui-heartbeat" };
        m_HeartbeatThread.Start();
    }

    // ── 心跳（自己的 thread，⛔ 不在 render loop 上）──────────────────

    void HeartbeatLoop()
    {
        string aPath = SenatePaths.GuiHeartbeat(m_RepoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
        var aBeat = new GuiHeartbeat
        {
            Pid = Environment.ProcessId,
            BuildId = ServerHost.BuildId,
            StartedAtUtc = DateTime.UtcNow.ToString("o"),
        };
        while (!m_Stop)
        {
            aBeat.BeatAtUtc = DateTime.UtcNow.ToString("o");
            aBeat.PageKey = SafePageKey();
            aBeat.FpsTotal = m_Window.FpsTotal;
            aBeat.FpsRecent = m_Window.FpsRecent;
            try { File.WriteAllText(aPath, aBeat.ToJson().ToJson()); }
            catch (IOException) { /* 下一拍再寫 —— 心跳掉一拍不該把窗弄掛 */ }
            Thread.Sleep(GuiBridge.HeartbeatIntervalMs);
        }
    }

    string SafePageKey()
    {
        try { return m_PageKey(); }
        catch (Exception) { return ""; }   // 心跳不該因為讀不到頁名而停
    }

    // ── 每幀：⭐ 閒置成本就是下面第一行那個 if ──────────────────────

    void Serve(SCP_GuiNode iTree)
    {
        if (m_ReqPath == null)
        {
            if (!m_Signal) return;          // ⭐ 閒置時整個函式就到這裡為止
            m_Signal = false;
            BeginRequest(iTree);
            return;
        }

        if (--m_WaitFrames > 0) return;
        Finish(iTree);
    }

    void BeginRequest(SCP_GuiNode iTree)
    {
        string? aPath = GuiBridge.TakePendingRequest(m_RepoRoot);
        if (aPath == null) return;

        GuiRequest aReq;
        try { aReq = GuiRequest.FromJson(SCP.Core.Json.SCP_JsonParser.Parse(File.ReadAllText(aPath))); }
        catch (Exception e)
        {
            GuiBridge.WriteResponse(aPath, Fail($"請求檔讀不了：{e.GetType().Name}: {e.Message}"));
            m_Signal = true;                // 後面可能還排著別的，別讓它們等到逾時
            return;
        }

        m_ReqPath = aPath;
        m_Req = aReq;

        // 讀類的操作不必等幀：手上這棵就是窗上現在畫的那一棵。
        if (aReq.Op is "list" or "json") { m_WaitFrames = 1; return; }

        // 截圖：拍的是**這顆常駐窗**（Tim 2026-09-15「需要時截圖看一下視窗」）。
        // ⚠ 跟 `ui --screenshot` 那條路不是同一件事：那一條會另外開一顆窗拍完就關，
        //   而它拍到的畫面**不含你剛剛在常駐窗上做的任何操作**——兩張圖看起來一樣合理。
        if (aReq.Op == "screenshot")
        {
            if (aReq.Value.Length == 0) { Respond(Fail("screenshot 要一個落檔路徑")); return; }
            try { m_Window.CaptureTo(aReq.Value); }
            catch (Exception e) { Respond(Fail($"截圖失敗：{e.GetType().Name}: {e.Message}")); return; }
            var aShot = new GuiResponse { Ok = true };
            bool aExists = File.Exists(aReq.Value);
            long aSize = aExists ? new FileInfo(aReq.Value).Length : 0;
            // ⚠ 回讀落檔 —— 「Capture 沒丟例外」不等於「檔案在」。
            aShot.Text = aExists
                ? $"✓ 截圖已落檔：{aReq.Value}（{aSize} bytes）"
                : $"✗ 截圖沒有落檔：{aReq.Value}";
            aShot.Ok = aExists;
            Respond(aShot);
            return;
        }

        // 寫類的：先驗 id 存不存在（⚠ 對不存在的 id 靜默成功會讓「按了沒反應」與「按錯了」同形）
        string aId = aReq.Op == "set" ? SplitSetId(aReq.Value) : aReq.TargetId;
        var aElem = SCP_GuiQuery.Find(iTree, aId);
        if (aElem == null)
        {
            Respond(Fail($"畫面上沒有這個 id：{aId}　（senate ui --list 看目前有哪些）"));
            return;
        }

        switch (aReq.Op)
        {
            case "click":
                m_Window.InjectClick(aId);
                break;
            case "set":
                m_Window.InjectField(aId, SplitSetValue(aReq.Value));
                break;
            case "toggle":
                m_Window.InjectToggle(aId, !(m_Window.TryGetToggle(aId, out bool aOn) ? aOn : aElem.On));
                break;
            case "fold":
                m_Window.InjectFold(aId, !(m_Window.TryGetFold(aId, out bool aOpen) ? aOpen : aElem.On));
                break;
            default:
                Respond(Fail($"認不得的 op：{aReq.Op}"));
                return;
        }
        m_WaitFrames = FramesAfterInject;
    }

    void Finish(SCP_GuiNode iTree)
    {
        var aRes = new GuiResponse { Ok = true };
        try
        {
            aRes.Text = m_Req!.Op switch
            {
                "json" => SCP_GuiQuery.ToJson(iTree).ToJson(),
                _ => UiDriver.ListElements(iTree, m_Style),
            };
        }
        catch (Exception e) { aRes = Fail($"序列化失敗：{e.GetType().Name}: {e.Message}"); }
        Respond(aRes);
    }

    void Respond(GuiResponse iRes)
    {
        iRes.PageKey = SafePageKey();
        iRes.FpsTotal = m_Window.FpsTotal;
        iRes.FpsRecent = m_Window.FpsRecent;
        if (m_ReqPath != null) GuiBridge.WriteResponse(m_ReqPath, iRes);
        m_ReqPath = null;
        m_Req = null;
        m_WaitFrames = 0;
        m_Signal = true;   // 可能還有下一筆排著
    }

    static GuiResponse Fail(string iError) => new() { Ok = false, Error = iError };

    static string SplitSetId(string iSet)
    {
        int aEq = iSet.IndexOf('=');
        return aEq <= 0 ? iSet : iSet.Substring(0, aEq);
    }

    static string SplitSetValue(string iSet)
    {
        int aEq = iSet.IndexOf('=');
        return aEq < 0 ? "" : iSet.Substring(aEq + 1);
    }

    public void Dispose()
    {
        m_Stop = true;
        m_Window.OnFrameServed = null;
        try { m_Watcher?.Dispose(); } catch (Exception) { }
        // ⚠ 心跳檔**收工時要刪** —— 留著的話下一道 CLI 指令會看到一個不會再跳的心跳，
        //   而「窗剛關掉」與「窗卡住了」該印的話不一樣。
        if (!m_Advertise) return;   // 沒宣告過就沒有心跳檔可刪，也不該去動別人的
        try { File.Delete(SenatePaths.GuiHeartbeat(m_RepoRoot)); } catch (Exception) { }
        GuiBridge.CleanupStale(m_RepoRoot);
    }
}
