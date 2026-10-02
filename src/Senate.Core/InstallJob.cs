// 區塊職責：把安裝系統的長工作（量狀態 ~40 秒、pip／下載可能幾十分鐘）跑在**背景執行緒**上，
//           並給 UI 執行緒一份讀得安全的進度快照（形狀同 SubmoduleSyncJob）。
// 物理意義：ImGui 視窗是連續 render loop —— 同步跑會讓視窗凍成「沒有回應」，而那跟「程式當掉了」同形。
// 數值影響：本類別不碰任何狀態；動作由建構子傳進來的 work 決定。
// ⚠ 執行緒契約（同 SubmoduleSyncJob）：背景只碰這個物件；UI 只透過 Snapshot() 讀；結果由 UI 執行緒搬進頁面。
// ⚠ 刻意**沒有取消**：pip 裝到一半被砍會留下半個套件（Windows 上檔案還被鎖著），比多等幾分鐘貴。
//   要停只能等它跑完或逾時（逾時上限在 InstallRunner）。
namespace Senate.Core;

public sealed record InstallJobProgress<T>(string Label, bool Finished, List<string> Log, T? Result, string? Error);

public sealed class InstallJob<T>
{
    readonly object m_Lock = new();
    readonly Func<Action<string>, T> m_Work;
    readonly List<string> m_Log = new();
    bool m_Finished;
    T? m_Result;
    string? m_Error;
    Thread? m_Thread;

    /// <summary>保留最後幾行就好（pip 的輸出可以上萬行；畫面只看尾巴，完整的在 CLI）。</summary>
    const int MaxLog = 400;

    public string Label { get; }

    public InstallJob(string iLabel, Func<Action<string>, T> iWork) { Label = iLabel; m_Work = iWork; }

    public void Start()
    {
        if (m_Thread != null) throw new InvalidOperationException("這個 job 已經起過了（一個實例只跑一次）");
        m_Thread = new Thread(Run) { IsBackground = true, Name = "install-job" };
        m_Thread.Start();
    }

    /// <summary>同步等它跑完（**只給不會重畫的宿主用** —— 見 <c>SCP_GuiHost.RedrawsContinuously</c>）。</summary>
    public void WaitForExit() => m_Thread?.Join();

    public InstallJobProgress<T> Snapshot()
    {
        lock (m_Lock) return new InstallJobProgress<T>(Label, m_Finished, new List<string>(m_Log), m_Result, m_Error);
    }

    void OnLog(string iLine)
    {
        lock (m_Lock)
        {
            m_Log.Add(iLine);
            if (m_Log.Count > MaxLog) m_Log.RemoveRange(0, m_Log.Count - MaxLog);
        }
    }

    void Run()
    {
        try
        {
            T aResult = m_Work(OnLog);
            lock (m_Lock) m_Result = aResult;
        }
        catch (Exception e)
        {
            // 背景執行緒的例外不會自己出現在任何地方 —— 沒有這個 catch，畫面會永遠停在「執行中」。
            lock (m_Lock) m_Error = $"{e.GetType().Name}: {e.Message}";
        }
        finally
        {
            lock (m_Lock) m_Finished = true;
        }
    }
}
