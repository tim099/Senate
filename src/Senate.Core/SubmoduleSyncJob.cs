// 區塊職責：把一輪 submodule 批次（checkout / pull / push）跑在**背景執行緒**上，
//           並提供一份 UI 執行緒讀得安全的進度快照。
// 物理意義：⭐ 這是本 repo **第一個**背景工作，所以判準寫在這裡而不是散在呼叫端。
//           理由是宿主形狀：ImGui 視窗是連續 render loop，而一輪批次跨十幾顆 submodule
//           是**分鐘級**的事 —— 同步跑會讓視窗凍結成「沒有回應」，
//           而那個症狀跟「程式當掉了」長得一模一樣。
// 數值影響：**會改變狀態** —— checkout / pull 移動 HEAD、push 寫遠端。
//           安全線全部由 SCP_GitSync 在動手當下現場重問（本層只決定範圍與順序，
//           而那一半在 SubmoduleScan.RunBatch）。
// ⚠ 執行緒契約，三條，違反任何一條的症狀都不是編譯錯誤：
//   ① **背景執行緒只碰這個物件**（不碰頁面欄位、不碰 m_Scan、不碰 renderer 狀態）。
//   ② **UI 執行緒只透過 Snapshot() 讀**（每幀拷一份，不持有內部集合的引用）。
//   ③ **結果由 UI 執行緒搬進頁面**（背景不直接寫頁面）—— 誰擁有那份狀態要只有一個答案。
// ⚠ 刻意**沒有取消**：git 跑到一半被 kill 可能留下 `index.lock`／半完成的 fetch，
//   而那個殘局比「多等三十秒」貴得多。要停就等它跑完（每一顆 git 自己有逾時上限：
//   本機 120s、走網路 300s，見 SCP_Git）。
using SCP.Core.Git;

namespace Senate.Core;

/// <summary>一輪批次的進度快照 —— **UI 執行緒拿到的是複本**，不是內部集合的引用。</summary>
public sealed record SubmoduleSyncProgress(
    bool Finished,
    int Done,
    int Total,
    string Current,
    List<string> Log,
    List<SubmoduleSyncRow>? Rows,
    string? Error)
{
    /// <summary>做完幾成（Total 為 0 時回 0 —— 不要回 1，「沒有事做」不是「做完了」）。</summary>
    public float Ratio => Total > 0 ? (float)Done / Total : 0f;

    public int OkCount => Rows?.Count(r => r.Outcome == SCP_GitSyncOutcome.Ok) ?? 0;
    public int SkipCount => Rows?.Count(r => r.Outcome == SCP_GitSyncOutcome.Skipped) ?? 0;
    public int FailCount => Rows?.Count(r => r.Outcome == SCP_GitSyncOutcome.Failed) ?? 0;
}

/// <summary>背景跑一輪 submodule 批次。一個實例只跑一次（跑完就丟，不重用）。</summary>
public sealed class SubmoduleSyncJob
{
    readonly object m_Lock = new();

    readonly SubmoduleScanResult m_Scan;
    readonly SCP_GitSyncOptions m_Options;
    readonly bool m_IncludeRoot;
    readonly List<string>? m_Only;

    readonly List<string> m_Log = new();
    string m_Current = "";
    int m_Done;
    readonly int m_Total;
    bool m_Finished;
    List<SubmoduleSyncRow>? m_Rows;
    string? m_Error;

    Thread? m_Thread;

    /// <summary>給人看的一句：這一輪在做什麼（"pull" / "checkout+pull" / "push" / "sync"）。</summary>
    public string Label { get; }

    public SubmoduleSyncJob(string iLabel, SubmoduleScanResult iScan, SCP_GitSyncOptions iOptions,
        bool iIncludeRoot, IReadOnlyCollection<string>? iOnly)
    {
        Label = iLabel;
        m_Scan = iScan;
        m_Options = iOptions;
        m_IncludeRoot = iIncludeRoot;
        m_Only = iOnly == null ? null : new List<string>(iOnly);

        // 分母先算好：進度要能在第一幀就顯示「0/24」而不是「0/0」
        //（0/0 看起來像「沒事要做」，而使用者剛剛才按了那顆鈕）。
        int aCount = 0;
        foreach (var aItem in iScan.Items)
        {
            if (m_Only != null && !m_Only.Contains(aItem.Entry.Path)) continue;
            aCount++;
        }
        if (iIncludeRoot && (iOptions.Pull || iOptions.Push)) aCount++;
        m_Total = aCount;
    }

    /// <summary>
    /// 起背景執行緒。
    /// <para>⚠ 用 <see cref="Thread"/> 不用 <c>Task.Run</c>：這是**分鐘級**的工作，
    /// 佔住一條 thread pool 執行緒那麼久會讓 pool 去長新的（而 pool 是給短工作用的）。
    /// <c>IsBackground = true</c> ⇒ process 要結束時不會被它卡住。</para>
    /// </summary>
    public void Start()
    {
        if (m_Thread != null) throw new InvalidOperationException("這個 job 已經起過了（一個實例只跑一次）");
        m_Thread = new Thread(Run) { IsBackground = true, Name = "submodule-sync" };
        m_Thread.Start();
    }

    /// <summary>
    /// 同步等它跑完（**只給不會重畫的宿主用** —— 見 <c>SCP_GuiHost.RedrawsContinuously</c>）。
    /// <para>⚠ 在 ImGui 視窗裡呼叫這個 ＝ 凍結畫面，那正是整個 job 要避免的事。</para>
    /// </summary>
    public void WaitForExit()
    {
        m_Thread?.Join();
    }

    /// <summary>拿一份進度複本（UI 執行緒每幀呼叫一次就好）。</summary>
    public SubmoduleSyncProgress Snapshot()
    {
        lock (m_Lock)
        {
            return new SubmoduleSyncProgress(
                m_Finished, m_Done, m_Total, m_Current,
                new List<string>(m_Log),
                m_Rows == null ? null : new List<SubmoduleSyncRow>(m_Rows),
                m_Error);
        }
    }

    void Run()
    {
        try
        {
            // ⚠ iLog 會被**這條**執行緒呼叫 —— 所以裡面只准動這個物件（契約①）。
            var aRows = SubmoduleScan.RunBatch(m_Scan, m_Options, m_IncludeRoot, m_Only, iLog: OnLog);
            lock (m_Lock)
            {
                m_Rows = aRows;
                m_Current = "";
            }
        }
        catch (Exception e)
        {
            // 背景執行緒的例外**不會**自動出現在任何地方 —— 沒有這個 catch，
            // 一個炸掉的批次會表現成「進度永遠停在 3/24」，而畫面上沒有任何錯誤。
            lock (m_Lock) m_Error = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            // ⚠ 一定要在 finally：Finished 沒被設起來的話 UI 會永遠顯示「執行中」，
            //   而那會把整頁鎖住（操作鈕在執行中不畫）。
            lock (m_Lock) m_Finished = true;
        }
    }

    void OnLog(string iLine)
    {
        lock (m_Lock)
        {
            m_Log.Add(iLine);
            // RunBatch 的 log 是「逐條經過」，而每一顆 submodule 的結論行以路徑開頭。
            // ⚠ 這裡只拿它當**進度顯示**，不拿它當計數的真相源 ——
            //   真相源是 Rows（結束後才有）。靠字串前綴算進度會在有人改一個字的那天靜默偏掉。
            m_Current = iLine.Length > 120 ? iLine.Substring(0, 120) + "…" : iLine;
            m_Done = CountOutcomeLines();
        }
    }

    /// <summary>
    /// 已完成幾顆 —— 數 log 裡的**結論行**（✓ / ⏭ / ✗ 開頭）。
    /// <para>⚠ 這是**估計值**，只給進度條用：`SCP_GitSync` 對一顆 repo 可能印多行
    /// （fetch 警告 ＋ 結論），而結論行的字首是它的既有格式。
    /// 真正的統計一律讀 <c>Rows</c>（那是結構化的），不讀這個。
    /// 📌 `SCP_GitSyncResult` 的 doc 明講「不要比對 Summary 的開頭字元」——
    /// 那條約束的是**判定成敗**，而這裡只是畫一條進度條。兩者不同，所以這裡標明它是估計。</para>
    /// </summary>
    int CountOutcomeLines()
    {
        int aCount = 0;
        foreach (string aLine in m_Log)
        {
            if (aLine.StartsWith("✓", StringComparison.Ordinal)      // ✓
                || aLine.StartsWith("⏭", StringComparison.Ordinal)   // ⏭
                || aLine.StartsWith("✗", StringComparison.Ordinal))  // ✗
                aCount++;
        }
        return aCount > m_Total ? m_Total : aCount;
    }
}
