// 區塊職責：把一輪 submodule **掃描**（唯讀）跑在背景執行緒上，並提供 UI 執行緒讀得安全的進度快照。
// 物理意義：掃描跟批次不同 —— 它不改任何狀態，所以「跑到一半沒人接」不會留殘局。
//           但它一樣是**秒級到分鐘級**的事：一顆 submodule 要問 branch／dirty／ahead-behind／remotes，
//           而 LY 有 24 顆 ⇒ 實測第一幀 13.1 秒。
//           🩸 這正是它存在的理由（TASK-0113）：那 13 秒發生在 **DrawContent 的第一幀**，
//             於是視窗開起來就是凍的，而**凍住的視窗截起來是正常的** ——
//             既有的截圖驗收全程綠燈，因為截圖是在那 13 秒之後才拍的。
//           ⇒ 會重畫的宿主把掃描丟這裡、第一幀先畫「掃描中」；
//             不重畫的那一側（純文字／指令驅動）**不走這條** —— 見 SubmoduleSyncPage.Rescan。
// 數值影響：**唯讀** —— 只轉給 SubmoduleScan.Scan。`iFetch` 開著時會動 remote-tracking ref
//           （那是 Scan 自己的既有語意，本層不加也不減）。
// ⚠ 執行緒契約與 SubmoduleSyncJob 逐條相同（那三條寫在它的檔頭，是本 repo 背景工作的判準）：
//   ① 背景執行緒只碰這個物件　② UI 執行緒只透過 Snapshot() 讀　③ 結果由 UI 執行緒搬進頁面。
// ⚠ 一樣**沒有取消**：掃描唯讀，跑完就丟；中途 kill 執行緒反而可能留下半跑的 git process。
//   要換設定就等它跑完 —— 指紋會在下一幀發現不符，自己再掃一輪。
namespace Senate.Core;

/// <summary>一輪掃描的進度快照 —— **UI 執行緒拿到的是複本**，不是內部狀態的引用。</summary>
public sealed record SubmoduleScanProgress(
    bool Finished,
    int Done,
    string Current,
    SubmoduleScanResult? Result,
    string? Error);

/// <summary>背景跑一輪 submodule 掃描。一個實例只跑一次（跑完就丟，不重用）。</summary>
public sealed class SubmoduleScanJob
{
    readonly object m_Lock = new();

    readonly string m_Root;
    readonly bool m_Fetch;
    readonly string? m_DefaultBranch;
    readonly IReadOnlyDictionary<string, string>? m_Overrides;

    int m_Done;
    string m_Current = "";
    bool m_Finished;
    SubmoduleScanResult? m_Result;
    string? m_Error;

    Thread? m_Thread;

    /// <summary>這一輪掃的是誰 —— 進度那一行要說得出來（「掃描中」不說掃誰等於沒說）。</summary>
    public string Root => m_Root;

    /// <summary>
    /// 這份照片是用什麼設定拍的 —— 呼叫端算好之後**寄放**在這裡。
    /// <para>⚠ 為什麼由呼叫端算而不是這裡算：指紋的定義（哪些設定算數）住在頁面，
    /// 那是它的判準。本層只負責「把它跟結果綁在一起」，讓收割時不會把
    /// A 設定的照片配到 B 設定上 —— 而那種錯的症狀是「綠燈全亮、量到的是別的 repo」。</para>
    /// </summary>
    public string Fingerprint { get; }

    public SubmoduleScanJob(string iRoot, bool iFetch, string? iDefaultBranch,
        IReadOnlyDictionary<string, string>? iOverrides, string iFingerprint)
    {
        m_Root = iRoot;
        m_Fetch = iFetch;
        m_DefaultBranch = iDefaultBranch;
        // 複製一份 —— 呼叫端的字典下一幀就會被重建，背景執行緒不能持有它的引用（契約①）。
        m_Overrides = iOverrides == null ? null : new Dictionary<string, string>(iOverrides);
        Fingerprint = iFingerprint;
    }

    /// <summary>
    /// 起背景執行緒。
    /// <para>⚠ 用 <see cref="Thread"/> 不用 <c>Task.Run</c>，理由同 <see cref="SubmoduleSyncJob"/>：
    /// 這是秒級到分鐘級的工作，佔住 thread pool 的執行緒那麼久會逼 pool 去長新的。</para>
    /// </summary>
    public void Start()
    {
        if (m_Thread != null) throw new InvalidOperationException("這個 job 已經起過了（一個實例只跑一次）");
        m_Thread = new Thread(Run) { IsBackground = true, Name = "submodule-scan" };
        m_Thread.Start();
    }

    /// <summary>
    /// 同步等它跑完。
    /// <para>⚠ 只有**一個**呼叫端該用它：使用者按下會動手的鈕、而批次需要一張照片才知道對誰動手
    /// （<c>EnsureScannedForJob</c>）。那一刻等是對的 —— 接下來那一輪批次是分鐘級的。
    /// 平常的自動掃描不准用它，用了就等於沒有這個 job。</para>
    /// </summary>
    public void WaitForExit() => m_Thread?.Join();

    /// <summary>拿一份進度複本（UI 執行緒每幀呼叫一次就好）。</summary>
    public SubmoduleScanProgress Snapshot()
    {
        lock (m_Lock)
            return new SubmoduleScanProgress(m_Finished, m_Done, m_Current, m_Result, m_Error);
    }

    void Run()
    {
        try
        {
            var aResult = SubmoduleScan.Scan(m_Root, m_Fetch, m_DefaultBranch, m_Overrides, iProgress: OnProgress);
            lock (m_Lock) { m_Result = aResult; m_Current = ""; }
        }
        catch (Exception e)
        {
            // 背景執行緒的例外**不會**自動出現在任何地方 —— 沒有這個 catch，
            // 一次炸掉的掃描會表現成「掃描中」永遠停在那裡，而畫面上沒有任何錯誤。
            lock (m_Lock) m_Error = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            // ⚠ 一定要在 finally：Finished 沒被設起來的話頁面會永遠顯示「掃描中」，
            //   而那一頁在掃描中不畫操作鈕 ⇒ 整頁鎖死。
            lock (m_Lock) m_Finished = true;
        }
    }

    void OnProgress(string iPath)
    {
        lock (m_Lock)
        {
            m_Done++;
            m_Current = iPath;
        }
    }
}
