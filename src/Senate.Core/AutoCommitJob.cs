// 區塊職責：把自動 commit 的**掃描**或**提交**跑在背景執行緒上，並提供 UI 執行緒讀得安全的進度快照。
// 物理意義：一輪掃描要對 AgentCommands＋二十幾個信件庫＋幾顆設定檔 repo 各跑四五條 git ⇒ 秒級；
//           提交更久。在 ImGui 視窗的 DrawContent 裡同步跑，視窗就凍成「沒有回應」——
//           而**凍住的視窗截起來是正常的**（SubmoduleScanJob 檔頭 TASK-0113 那筆血證）。
//           ⇒ 會重畫的宿主丟這裡；不重畫的宿主（純文字／指令驅動）起完就 WaitForExit（見 AutoCommitPage）。
// 數值影響：掃描唯讀；提交會寫各 repo 的 history（不 push、不動父層 pointer）—— 邏輯全在 SCP_AutoCommit，本層只排程。
//           ⚠ 提交前**逐個 repo 重掃一次**：確認頁看到的是上一張照片，而 index 在那之後可能被別人動過
//             （「呼叫前 index 已有 staged 檔」那道守衛要量的是**現在**，不是確認當下）。
// ⚠ 執行緒契約與 SubmoduleSyncJob 逐條相同：
//   ① 背景執行緒只碰這個物件　② UI 執行緒只透過 Snapshot() 讀　③ 結果由 UI 執行緒搬進頁面。
// ⚠ 刻意**沒有取消**：git 跑到一半被 kill 可能留下 `index.lock`，那個殘局比多等一會兒貴。
using SCP.Core.Git;
using SCP.Core.Paths;

namespace Senate.Core;

/// <summary>要提交的一群（repo 顯示名 ＋ 群 key）。</summary>
public sealed record AutoCommitPick(string RepoName, string Key);

/// <summary>進度快照 —— **UI 執行緒拿到的是複本**。</summary>
public sealed record AutoCommitProgress(
    bool Finished,
    int Done,
    int Total,
    string Current,
    List<string> Log,
    List<SCP_AutoCommitRepo>? Repos,
    List<string> Notes,
    List<SCP_AutoCommitGroupResult>? Results,
    string? Error)
{
    public int OkCount => Results?.Count(r => r.Ok) ?? 0;
    public int SkipCount => Results?.Count(r => r.Skipped) ?? 0;
    public int FailCount => Results?.Count(r => !r.Ok && !r.Skipped) ?? 0;
    public int MismatchCount => Results?.Count(r => r.Ok && r.Extra.Count > 0) ?? 0;
}

/// <summary>背景跑一輪掃描或提交。一個實例只跑一次（跑完就丟，不重用）。</summary>
public sealed class AutoCommitJob
{
    readonly object m_Lock = new();
    readonly SCP_DataRoot m_DataRoot;
    readonly SCP_LettersRoot m_LettersRoot;
    readonly List<AutoCommitPick>? m_Picks;     // null ＝ 掃描

    readonly List<string> m_Log = new();
    readonly List<string> m_Notes = new();
    string m_Current = "";
    int m_Done;
    int m_Total;
    bool m_Finished;
    List<SCP_AutoCommitRepo>? m_Repos;
    List<SCP_AutoCommitGroupResult>? m_Results;
    string? m_Error;
    Thread? m_Thread;

    /// <summary>給人看的一句：這一輪在做什麼。</summary>
    public string Label { get; }

    public bool IsCommit => m_Picks != null;

    AutoCommitJob(string iLabel, SCP_DataRoot iDataRoot, SCP_LettersRoot iLettersRoot, List<AutoCommitPick>? iPicks)
    {
        Label = iLabel;
        m_DataRoot = iDataRoot;
        m_LettersRoot = iLettersRoot;
        m_Picks = iPicks;
        m_Total = iPicks?.Count ?? 0;
    }

    public static AutoCommitJob Scan(SCP_DataRoot iDataRoot, SCP_LettersRoot iLettersRoot)
        => new("掃描", iDataRoot, iLettersRoot, null);

    public static AutoCommitJob Commit(SCP_DataRoot iDataRoot, SCP_LettersRoot iLettersRoot, IEnumerable<AutoCommitPick> iPicks)
        => new("commit", iDataRoot, iLettersRoot, new List<AutoCommitPick>(iPicks));

    /// <summary>起背景執行緒（用 Thread 不用 Task.Run：秒到分鐘級的工作不該佔 thread pool）。</summary>
    public void Start()
    {
        if (m_Thread != null) throw new InvalidOperationException("這個 job 已經起過了（一個實例只跑一次）");
        m_Thread = new Thread(Run) { IsBackground = true, Name = "auto-commit" };
        m_Thread.Start();
    }

    /// <summary>同步等它跑完（**只給不會重畫的宿主用**）。</summary>
    public void WaitForExit() => m_Thread?.Join();

    public AutoCommitProgress Snapshot()
    {
        lock (m_Lock)
        {
            return new AutoCommitProgress(m_Finished, m_Done, m_Total, m_Current,
                new List<string>(m_Log),
                m_Repos,                       // 跑完之後才設、之後不再動 ⇒ 交出引用是安全的
                new List<string>(m_Notes),
                m_Results == null ? null : new List<SCP_AutoCommitGroupResult>(m_Results),
                m_Error);
        }
    }

    void Run()
    {
        try
        {
            using (SCP_Git.Scope(SCP_AutoCommit.ProcessTag, nameof(AutoCommitJob)))
            {
                var aNotes = new List<string>();
                var aRepos = SCP_AutoCommit.Discover(m_DataRoot, m_LettersRoot, aNotes);
                lock (m_Lock) m_Notes.AddRange(aNotes);

                if (m_Picks == null)
                {
                    lock (m_Lock) m_Total = aRepos.Count;
                    foreach (var aRepo in aRepos)
                    {
                        SetCurrent("掃描 " + aRepo.Name);
                        SCP_AutoCommit.Scan(aRepo);
                        lock (m_Lock) m_Done++;
                    }
                    lock (m_Lock) m_Repos = aRepos;
                    return;
                }

                // ── 提交：照 Discover 的順序走（子 repo 在前、父層最後），每個 repo 先重掃再提交 ──
                var aResults = new List<SCP_AutoCommitGroupResult>();
                foreach (var aRepo in aRepos)
                {
                    var aKeys = new List<string>();
                    foreach (var aPick in m_Picks) if (aPick.RepoName == aRepo.Name) aKeys.Add(aPick.Key);
                    if (aKeys.Count == 0) continue;
                    SetCurrent("重掃 " + aRepo.Name);
                    SCP_AutoCommit.Scan(aRepo);
                    foreach (string aKey in aKeys)
                    {
                        SetCurrent($"commit {aRepo.Name} [{aKey}]");
                        var aResult = SCP_AutoCommit.CommitGroup(aRepo, aKey);
                        aResults.Add(aResult);
                        lock (m_Lock)
                        {
                            m_Log.AddRange(aResult.Log);
                            if (!aResult.Ok && !aResult.Skipped && aResult.Log.Count == 0)
                                m_Log.Add($"✗ {aRepo.Name} [{aKey}] {aResult.Error}");
                            m_Done++;
                        }
                    }
                }
                // 確認頁上有、這一輪發現裡卻沒有的 repo（改名／被移走）⇒ 說出來，不要讓它安靜地消失。
                foreach (var aPick in m_Picks)
                {
                    if (aRepos.Exists(r => r.Name == aPick.RepoName)) continue;
                    var aMissing = new SCP_AutoCommitGroupResult
                    {
                        RepoName = aPick.RepoName, Key = aPick.Key,
                        Error = "這一輪重新發現時找不到這個 repo（改名或被移走？）—— 沒有提交",
                    };
                    aResults.Add(aMissing);
                    lock (m_Lock) { m_Log.Add($"✗ {aPick.RepoName} [{aPick.Key}] {aMissing.Error}"); m_Done++; }
                }
                lock (m_Lock) { m_Results = aResults; m_Repos = aRepos; }
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
            // ⚠ 一定要在 finally：Finished 沒被設起來的話 UI 會永遠顯示「執行中」並把操作鈕鎖住。
            lock (m_Lock) { m_Finished = true; m_Current = ""; }
        }
    }

    void SetCurrent(string iText)
    {
        lock (m_Lock) m_Current = iText;
    }
}
