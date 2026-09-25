// 區塊職責：自我對拍 —— 這套東西自己的讀數（不是「應該會動」）。
// 物理意義：SCP_Core 的 JSON 層是共用碼，它的第一個責任是**讀得懂既有資料**：
//           那些 json 是 Unity 端的 UCL JsonData 寫出來的，所以「能不能讀」不是單元測試問題，
//           是拿真檔案去試的問題。⇒ 每一項都印出**讀到什麼**，不是只印 ✓。
// 數值影響：純讀。找不到樣本檔時回報「跳過（沒有樣本）」，**不當成通過** ——
//           「沒測」與「測過而且對」同形是這個 repo 最貴的錯誤形狀。
using System.Reflection;
using System.Text;
using Senate.Core;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Prefs;
using SCP.Core.Session;
using SCP.Core.Skills;
using SCP.Core.Entry;
using SCP.Core.Letters;
using SCP.Core.Reflect;
using System.Text.RegularExpressions;
using SCP.Core.Watch;
using SCP.Core.Books;
using SCP.Core.Library;
using SCP.Core.Cmd;
using SCP.Core.Bank;
using SCP.Core.Io;
using SCP.Core.Proc;
using SCP.Core.Tavern;
using System.Globalization;

using Senate.Cli.Pages;

namespace Senate.Cli;

public enum CheckResult { Pass, Fail, Skipped }

public sealed record CheckRow(string Name, string Reading, CheckResult Result);

public static class SelfTest
{
    // ===========================================================
    // 區塊職責：一筆對拍項目的**登記**（key／群／怎麼跑），供列出與挑選。
    //
    // 物理意義：`Key` 一律用 `nameof(<那支方法>)` —— ⛔ 不另取顯示名。
    //   理由：另取名字就是第二份真相源，而它會在方法改名的那天安靜地過期
    //   （`nameof` 會被編譯器逼著一起改）。
    //
    // ⚠ `Group` 是**成本分類**不是主題分類：挑選的目的是「不要每次都付慢的那一份」，
    //   所以分群的判準是「這一格要不要碰真檔案」，而不是「它在講哪個功能」。
    // ===========================================================
    sealed record Entry(string Key, string Group, Func<IEnumerable<CheckRow>> Run);

    static Entry One(string iKey, string iGroup, Func<CheckRow> iRun)
        => new Entry(iKey, iGroup, () => new[] { iRun() });

    static Entry Many(string iKey, string iGroup, Func<IEnumerable<CheckRow>> iRun)
        => new Entry(iKey, iGroup, iRun);

    // ⚠ 這張表就是「有哪些項目」的唯一來源 —— `--list` 印它、`--only` 篩它、`Run` 跑它。
    //   三個消費端吃同一份，⛔ 不要在別處再抄一份清單。
    static List<Entry> Catalog(IReadOnlyList<ProjectReading> iProjects) => new()
    {
        One(nameof(MissingSemantics), "core", MissingSemantics),
        One(nameof(WriterStability), "core", WriterStability),
        One(nameof(ConfigRoundTripKeepsUnknownKeys), "core", ConfigRoundTripKeepsUnknownKeys),
        One(nameof(PrefsThreeStates), "core", PrefsThreeStates),
        One(nameof(PrefsKeepsOtherSections), "core", PrefsKeepsOtherSections),
        One(nameof(PathsSingleSource), "core", PathsSingleSource),
        One(nameof(PathRegistryShape), "core", PathRegistryShape),
        One(nameof(ErrorReportShape), "core", ErrorReportShape),
        One(nameof(ProcessStatusClassification), "core", ProcessStatusClassification),
        One(nameof(QueueSubLaneShape), "core", QueueSubLaneShape),
        One(nameof(ServerResultRoundTrip), "core", ServerResultRoundTrip),
        One(nameof(DelegateCarriesCmdExitCode), "core", DelegateCarriesCmdExitCode),
        One(nameof(AtomicFileDistinguishesCollisionFromOtherIo), "core", AtomicFileDistinguishesCollisionFromOtherIo),
        One(nameof(ServerEndpointFourStates), "core", ServerEndpointFourStates),
        One(nameof(ServerCmdClientMatchesAgentCmdClient), "core", ServerCmdClientMatchesAgentCmdClient),
        One(nameof(WaitTimeoutDescribesLane), "core", WaitTimeoutDescribesLane),
        One(nameof(QueueAppendSurvivesConcurrency), "core", QueueAppendSurvivesConcurrency),
        One(nameof(QueueCommitKeepsEntriesAppendedDuringBatch), "core", QueueCommitKeepsEntriesAppendedDuringBatch),
        One(nameof(VanishedCmdIsUnknownNotSuccess), "core", VanishedCmdIsUnknownNotSuccess),
        One(nameof(UnityCompileStatusShape), "core", UnityCompileStatusShape),

        One(nameof(LoginPageResolvesLettersRoot), "gui", LoginPageResolvesLettersRoot),
        One(nameof(StyleRoundTrip), "gui", StyleRoundTrip),
        One(nameof(PageStack), "gui", PageStack),
        One(nameof(TypeSchemaShape), "gui", TypeSchemaShape),
        One(nameof(MapperRoundTrip), "gui", MapperRoundTrip),
        One(nameof(InspectorEdits), "gui", InspectorEdits),
        One(nameof(FoldSemantics), "gui", FoldSemantics),
        One(nameof(DropdownWidget), "gui", DropdownWidget),
        One(nameof(PageCatalogShape), "gui", PageCatalogShape),
        One(nameof(PageAutoRegister), "gui", PageAutoRegister),
        One(nameof(RowLayout), "gui", RowLayout),
        One(nameof(SourceHint), "gui", SourceHint),
        One(nameof(SourceCapabilityFallback), "gui", SourceCapabilityFallback),
        One(nameof(SourceMessageLifecycle), "gui", SourceMessageLifecycle),

        One(nameof(EntryDocBlock), "entrydoc", EntryDocBlock),
        One(nameof(EntryDocDefects), "entrydoc", EntryDocDefects),
        One(nameof(EntryDocInstallIo), "entrydoc", EntryDocInstallIo),
        One(nameof(SkillMirror), "entrydoc", SkillMirror),

        One(nameof(ActivitySessionBehaviour), "session", ActivitySessionBehaviour),
        One(nameof(ActivitySessionSubclassRoundTrip), "session", ActivitySessionSubclassRoundTrip),

        One(nameof(RestLetterShape), "letters", RestLetterShape),
        One(nameof(TavernPostVerdictThreeStates), "letters", TavernPostVerdictThreeStates),

        One(nameof(BookAddCleanRoom), "book", BookAddCleanRoom),
        One(nameof(BookWritingFilter), "book", BookWritingFilter),
        One(nameof(BookChapterArcCleanRoom), "book", BookChapterArcCleanRoom),

        One(nameof(LibraryJsonStyleFixture), "library", LibraryJsonStyleFixture),

        One(nameof(ServerAutoStartFourStates), "server", ServerAutoStartFourStates),
        One(nameof(TavernWriteCleanRoom), "tavern", TavernWriteCleanRoom),
        One(nameof(TavernWriteModeFourStates), "tavern", TavernWriteModeFourStates),
        One(nameof(TavernWriteCmdGates), "tavern", TavernWriteCmdGates),
        Many(nameof(RealTavernSerializerMatchesEditor), "tavern",
             () => RealTavernSerializerMatchesEditor(iProjects)),

        One(nameof(BankIdRules), "bank", BankIdRules),
        One(nameof(BankAccountCleanRoom), "bank", BankAccountCleanRoom),
        One(nameof(BankLedgerConcurrentDebit), "bank", BankLedgerConcurrentDebit),
        One(nameof(JsonExtensionDataRoundTrip), "bank", JsonExtensionDataRoundTrip),

        // ── 以下都會去讀**真專案的真檔案** ⇒ 慢的那一份都在這裡 ──
        Many(nameof(RealFileRoundTrip), "real", () => RealFileRoundTrip(iProjects)),
        Many(nameof(RealPersonaScan), "real", () => RealPersonaScan(iProjects)),
        Many(nameof(RealActivitySessionRoundTrip), "real", () => RealActivitySessionRoundTrip(iProjects)),
        Many(nameof(RealWatchLedgerRead), "watch", () => RealWatchLedgerRead(iProjects)),
        Many(nameof(RealWatchResolveFingerprint), "watch", () => RealWatchResolveFingerprint(iProjects)),
        Many(nameof(RealWatchChapterRebuild), "watch", () => RealWatchChapterRebuild(iProjects)),
        One(nameof(WatchIdentityGuard), "watch", WatchIdentityGuard),
        Many(nameof(WatchWriteCleanRoom), "watch", () => WatchWriteCleanRoom(iProjects)),
        Many(nameof(RealLibraryByteRoundTrip), "library", () => RealLibraryByteRoundTrip(iProjects)),
        Many(nameof(RealRecallPortMatchesEditor), "library", () => RealRecallPortMatchesEditor(iProjects)),
        Many(nameof(RealBookshelfPortMatchesEditor), "library", () => RealBookshelfPortMatchesEditor(iProjects)),
        Many(nameof(RealLibraryInitMatchesDisk), "library", () => RealLibraryInitMatchesDisk(iProjects)),
        One(nameof(LibraryBuilderGolden), "library", LibraryBuilderGolden),
        One(nameof(LibraryNoteCleanRoom), "library", LibraryNoteCleanRoom),
        One(nameof(LibraryCharacterCleanRoom), "library", LibraryCharacterCleanRoom),

        One(nameof(InvokeValueThreeStates), "invoke", InvokeValueThreeStates),
        One(nameof(InvokeSeparatesTargetThrowFromMisuse), "invoke", InvokeSeparatesTargetThrowFromMisuse),
        One(nameof(InvokeChainStopsAndMarksNotExecuted), "invoke", InvokeChainStopsAndMarksNotExecuted),
        One(nameof(InvokeRejectsUnknownStepKey), "invoke", InvokeRejectsUnknownStepKey),
    };

    /// <summary>`--list` 用：回 (key, group) 清單。⛔ 不跑任何一格。</summary>
    public static List<(string Key, string Group)> List(IReadOnlyList<ProjectReading> iProjects)
    {
        var aOut = new List<(string, string)>();
        foreach (var e in Catalog(iProjects)) aOut.Add((e.Key, e.Group));
        return aOut;
    }

    /// <summary>
    /// 跑對拍。<paramref name="iOnly"/> 給了就**只跑**名稱或群命中的那幾格。
    /// <para>⚠ 挑選是在**呼叫之前**過濾的（沒被選到的那一格根本不執行）——
    /// 不是跑完再把行藏起來。⇒ 這一格的意義是省時間，藏起來省不到。</para>
    /// <para>⛔ 篩到 0 格時**不回空清單當成功** —— 呼叫端要能分辨
    /// 「全部通過」與「我一格都沒跑」，那兩件事在 `失敗 0` 上同形。</para>
    /// </summary>
    public static List<CheckRow> Run(IReadOnlyList<ProjectReading> iProjects, string iOnly = "")
    {
        var aRows = new List<CheckRow>();
        foreach (var e in Catalog(iProjects))
        {
            if (!Matches(e, iOnly)) continue;
            aRows.AddRange(e.Run());
        }
        return aRows;
    }

    /// <summary>幾筆會被 <paramref name="iOnly"/> 選中（給呼叫端分辨「0 格」與「全過」）。</summary>
    public static int CountSelected(IReadOnlyList<ProjectReading> iProjects, string iOnly)
    {
        int n = 0;
        foreach (var e in Catalog(iProjects)) if (Matches(e, iOnly)) ++n;
        return n;
    }

    /// <summary>
    /// 命中判準：逗號分隔、**大小寫不敏感的子字串**，比對 key 與 group 兩者任一。
    /// <para>⚠ 用子字串而不是完全相符：完全相符要人記得整個方法名，而打錯的下場是
    /// 「0 格、失敗 0」—— 看起來像全過。子字串讓 `watch`／`real`／`book` 這種短詞就能用。</para>
    /// </summary>
    static bool Matches(Entry iEntry, string iOnly)
    {
        if (string.IsNullOrWhiteSpace(iOnly)) return true;
        foreach (string aTok in iOnly.Split(',', StringSplitOptions.RemoveEmptyEntries
                                                 | StringSplitOptions.TrimEntries))
        {
            if (iEntry.Key.Contains(aTok, StringComparison.OrdinalIgnoreCase)) return true;
            if (iEntry.Group.Contains(aTok, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // 區塊職責：queue 子分道（`<persona>/<lane>`）的路徑與**身分不被污染**。
    // 物理意義：子分道要解的是「同一個人同時派兩筆會互相排隊」——
    //          而它最可能的壞法不是路徑錯，是**身分被連著 lane 一起戳進 args**：
    //          下游（Tavern 署名／Treasury 記帳）會拿到一個叫 `summit/chess-5` 的人，
    //          那個人不存在，而且沒有任何一層會報錯。
    // ⚠ 檔名與 python `run_cmd.py` 逐字同形是硬需求：Editor 端 watcher 掃 `queue*.json`，
    //   形狀差一個字＝那筆永遠不被取走（而送出端一切正常）。
    static CheckRow QueueSubLaneShape()
    {
        var aRoot = new SCP_DataRoot("D:/x/AgentCommands");

        // ① 不帶 lane ⇒ 原樣（既有呼叫端行為一個字都不能變）
        bool aPlain = SCP_DataPaths.QueueFile(aRoot, "summit").EndsWith("/queues/summit/queue.json", StringComparison.Ordinal)
                      && SCP_DataPaths.TriggerFile(aRoot, "summit").EndsWith("/queues/summit/pending.trigger", StringComparison.Ordinal);

        // ② 帶 lane ⇒ 同一個資料夾、不同檔名（與 run_cmd.py 同形）
        bool aLaned = SCP_DataPaths.QueueFile(aRoot, "summit/chess-5").EndsWith("/queues/summit/queue-chess-5.json", StringComparison.Ordinal)
                      && SCP_DataPaths.TriggerFile(aRoot, "summit/chess-5").EndsWith("/queues/summit/pending-chess-5.trigger", StringComparison.Ordinal)
                      && SCP_DataPaths.QueueFolder(aRoot, "summit/chess-5").EndsWith("/queues/summit", StringComparison.Ordinal);

        // ③ **互不阻塞**的機械證據：兩條分道的 trigger 是不同的檔
        bool aIsolated = SCP_DataPaths.TriggerFile(aRoot, "summit") != SCP_DataPaths.TriggerFile(aRoot, "summit/chess-5")
                         && SCP_DataPaths.TriggerFile(aRoot, "summit/chess-2") != SCP_DataPaths.TriggerFile(aRoot, "summit/chess-4");

        // ④ 身分只取 folder 那一半 —— 這一格是本測項存在的理由
        bool aIdentity = SCP_DataPaths.SplitQueueId("summit/chess-5").Folder == "summit"
                         && SCP_DataPaths.SplitQueueId("summit/chess-5").Lane == "chess-5"
                         && SCP_DataPaths.SplitQueueId("summit").Lane.Length == 0;

        // ⑤ 反向對照：不合法的組合**整筆**退回 anonymous，⛔ 不可以只消毒一半
        bool aGuard = SCP_DataPaths.SplitQueueId("../etc/x").Folder == SCP_DataPaths.AnonymousQueueId
                      && SCP_DataPaths.SplitQueueId("summit/..").Folder == SCP_DataPaths.AnonymousQueueId
                      && SCP_DataPaths.SplitQueueId("summit/a/b").Folder == SCP_DataPaths.AnonymousQueueId
                      && SCP_DataPaths.SplitQueueId("summit/..").Lane.Length == 0
                      && SCP_DataPaths.SplitQueueId("").Folder == SCP_DataPaths.AnonymousQueueId;

        bool aOk = aPlain && aLaned && aIsolated && aIdentity && aGuard;
        return new CheckRow("queue 子分道（<persona>/<lane>）",
            $"不帶 lane 原樣={aPlain}／帶 lane 同資料夾異檔名={aLaned}／**兩條分道 trigger 不同檔**={aIsolated}／"
            + $"**身分只取 folder**={aIdentity}／不合法整筆退回 anonymous={aGuard}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    // 區塊職責：Unity 編譯狀態讀取層的**反向對照** —— 那三種「看起來像綠燈的沒有讀數」。
    // 物理意義：這一支的價值全在它**不會**說什麼：檔不在時不可以印 0 errors、
    //          找不到第二來源時不可以說「一致」、tracker 說 0 而 ErrorLog 有錯時要以 ErrorLog 為準。
    //          🩸 這三格都不是假想：`check_compile.py --watch` 就是在「沒有讀數」那一格印了綠燈
    //          （TASK-0154，2026-09-07 實測印出三天前的快照）。
    // ⚠ 用暫存根，不碰任何真專案 —— 驗一個「讀狀態」的東西時去動真的狀態，是把受測體污染掉。
    static CheckRow UnityCompileStatusShape()
    {
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_compile_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aRoot);

            // ① 檔不在 ⇒ 沒有讀數，且那句話不可以長得像「沒有錯誤」
            var aMissing = SCP.Core.Compile.SCP_UnityCompile.Read(aRoot);
            bool aMissingOk = !aMissing.Found && aMissing.Status == null
                              && aMissing.Error.Contains("沒有讀數", StringComparison.Ordinal)
                              && aMissing.Path.EndsWith(SCP.Core.Compile.SCP_UnityCompile.StatusFileName, StringComparison.Ordinal);

            // ② 壞 JSON ⇒ 帶原因回來，⛔ 不可以靜默變成「空的狀態」
            string aPath = Path.Combine(aRoot, SCP.Core.Compile.SCP_UnityCompile.StatusFileName);
            File.WriteAllText(aPath, "{ 這不是 json", Encoding.UTF8);
            var aBroken = SCP.Core.Compile.SCP_UnityCompile.Read(aPath.Length > 0 ? aRoot : aRoot);
            bool aBrokenOk = !aBroken.Found && aBroken.Error.Length > 0;

            // ③ 正常讀 ＋ 已知答案：兩顆錯、一顆警告 ⇒ 去重後錯誤剩兩顆
            File.WriteAllText(aPath, """
{
  "tracker": "UCL_CompileErrorTracker",
  "timestamp": "2026-09-07T09:01:13",
  "duration_seconds": 1.8,
  "in_progress": false,
  "total_errors": 2,
  "total_warnings": 1,
  "total_messages": 4,
  "messages": [
    {"assembly":"A","file":"X.cs","line":1,"column":2,"type":"Error","message":"error CS0128: 甲"},
    {"assembly":"A","file":"X.cs","line":1,"column":2,"type":"Error","message":"error CS0128: 甲"},
    {"assembly":"A","file":"Y.cs","line":9,"column":1,"type":"Error","message":"error CS8603: 乙"},
    {"assembly":"A","file":"Z.cs","line":3,"column":1,"type":"Warning","message":"warning CS0168: 丙"}
  ]
}
""", Encoding.UTF8);
            var aRead = SCP.Core.Compile.SCP_UnityCompile.Read(aRoot);
            bool aReadOk = aRead.Found && aRead.Status != null && aRead.Status.total_errors == 2
                           && aRead.Status.messages.Count == 4
                           && SCP.Core.Compile.SCP_UnityCompile.ErrorsOf(aRead.Status).Count == 2;   // 去重把重複那顆吃掉

            // ④ 新鮮度：mtime **等於**基準要算新（tracker 只有秒精度，同一秒觸發會等於而不是大於
            //    —— 判成「還沒跑」就是永遠等下去）
            DateTime aStamp = aRead.WriteTimeUtc;
            bool aFreshOk = SCP.Core.Compile.SCP_UnityCompile.IsFresherThan(aRead, aStamp)
                            && SCP.Core.Compile.SCP_UnityCompile.IsFresherThan(aRead, aStamp.AddSeconds(-1))
                            && !SCP.Core.Compile.SCP_UnityCompile.IsFresherThan(aRead, aStamp.AddSeconds(1));

            // ⑤ 找不到第二來源 ⇒ **NoSecondSource**，⛔ 不可以退化成 AgreeClean
            var aCross = SCP.Core.Compile.SCP_UnityCompile.Crosscheck(aRoot, aRead.Status!);
            bool aCrossOk = aCross.Verdict == SCP.Core.Compile.SCP_UnityCompile.SCP_CrosscheckVerdict.NoSecondSource;

            // ⑥ tracker 說 0 而 ErrorLog 有錯 ⇒ 以 ErrorLog 為準（這格是本層存在的理由）
            string aLogDir = Path.Combine(aRoot, "Assets", "DebugLogs~");
            Directory.CreateDirectory(aLogDir);
            File.WriteAllText(Path.Combine(aLogDir, "Errors_latest.log"),
                "[09:05:00] Foo.cs(1,1): error CS0246: 找不到型別\n", Encoding.UTF8);
            var aZero = new SCP.Core.Compile.SCP_UnityCompileStatus { timestamp = "2026-09-07T09:01:13", total_errors = 0 };
            var aMissed = SCP.Core.Compile.SCP_UnityCompile.Crosscheck(aRoot, aZero);
            bool aMissedOk = aMissed.Verdict == SCP.Core.Compile.SCP_UnityCompile.SCP_CrosscheckVerdict.TrackerMissedErrors
                             && aMissed.LogCount == 1;

            // ⑦ 射程那句話必須真的印在輸出裡（它是結論的一部分，不是說明文件）
            List<string> aRender = SCP.Core.Compile.SCP_UnityCompile.Render(aRead, aRoot, true, 20);
            bool aScopeOk = aRender.Exists(l => l.Contains("不涵蓋 `senate.exe`", StringComparison.Ordinal));

            bool aOk = aMissingOk && aBrokenOk && aReadOk && aFreshOk && aCrossOk && aMissedOk && aScopeOk;
            return new CheckRow("Unity 編譯狀態讀取（反向對照）",
                $"檔不在≠0錯={aMissingOk}／壞 JSON 帶原因={aBrokenOk}／讀回＋去重={aReadOk}／"
                + $"**mtime 等於基準算新**={aFreshOk}／無第二來源≠一致={aCrossOk}／"
                + $"**tracker 說 0 而 ErrorLog 有錯**={aMissedOk}／射程有印={aScopeOk}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally { try { if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true); } catch { } }
    }

    // 區塊職責：`cmd book op=add` 的產物，與 `library.py add-book` 的**真實輸出**逐位元組對拍。
    // 物理意義：下面那段 aWant **是 2026-09-06 從 python 真跑出來的檔抄回來的**（只有日期那格
    //          換成今天，因為 `_today()` 本來就是當日）—— 它是**對照組**，不是「我期望它長這樣」。
    //          兩份實作各自寫同一個 store，漂掉的症狀是「兩邊都成功、內容也對，而位元組不同」，
    //          沒有任何一層會喊 —— TASK-0143 第五刀就是被這個形狀咬的。
    // 數值影響：純暫存目錄，⛔ 不碰任何真 store。四格一起驗：
    //          ① 逐位元組相同（含 **CRLF** 與 2 空格縮排、`characters: []` 不展開）
    //          ② `status` 覆寫成 `writing` 後**留在原位**（python dict 的插入序保證）
    //          ③ 別名去重且保序
    //          ④ 反向對照：同一本再建一次要被擋（exit 1）且**檔案逐位元組沒被動過**
    // 區塊職責：`op=log-chapter` / `op=arc` 的 clean-room 對拍 —— 期望值是 **python 真產物的位元組**。
    // 物理意義：TASK-0143 ②-bis 拍板 (a) 補上的那兩個寫入端。本格把「我 2026-09-07 手動跑過一次的
    //          逐位元組對拍」換成長在必經路上的機械 —— 手動讀數只證明那一天，而**下次改這支的人
    //          不會來問我**（攔截來源只有兩個，「記得再跑一遍」不在名單上）。
    // 🩸 兩個會漂又不會叫的細節各給一格斷言：
    //   ① 空值欄位那行是 `title: `（**尾隨一個空格** —— python `f"{k}: {v}"` 的結果）。
    //   ② 產物是 **CRLF**（python 文字模式在 Windows）⇒ 寫成 LF 會「內容一樣、逐位元組不同」。
    // 數值影響：純暫存目錄，⛔ 不碰真 store。
    static CheckRow BookChapterArcCleanRoom()
    {
        const string aName = "log-chapter／arc clean-room（對照 library.py 真產物）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bookch_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(aTmp, "BookNotes"));
            var aCmd = new SCP_Cmd_Book();

            // ⭐ 走 Bind 不自己塞值 —— 測試裡打錯參數名要**當場炸**，不是靜默取預設值然後綠著過。
            SCP_CmdArgs Args(Dictionary<string, string> iRaw)
            {
                var (a, aErrs) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, iRaw);
                if (a == null) throw new InvalidOperationException(string.Join("；", aErrs));
                return a;
            }

            string aBookDir = Path.Combine(aTmp, "BookNotes", "cr-test-book");
            string aBookJson = Path.Combine(aBookDir, "book.json");

            SCP_CmdResult aAdd = aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "add", ["id"] = "cr-test-book",
                ["title"] = "對拍用書", ["aliases"] = "對拍用書", ["author"] = "誰",
            }));
            if (aAdd.ExitCode != 0 || !File.Exists(aBookJson))
                return new CheckRow(aName,
                    $"前置建檔失敗：exit={aAdd.ExitCode}、book.json 存在={File.Exists(aBookJson)}",
                    CheckResult.Fail);

            // ── ① 滿參數的 ch3，接著**全省略**的 ch1（fallback 路徑才是空格與「（待補）」住的地方）──
            aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "log-chapter", ["book"] = "cr-test-book",
                ["chapter"] = "3", ["title"] = "約克的石頭", ["summary"] = "第三章摘要",
                ["events"] = "事件甲;事件乙", ["views"] = "看法一|看法二",
                ["new_characters"] = "諾瑞爾;史傳傑", ["foreshadow"] = "伏筆X",
            }));
            aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "log-chapter", ["book"] = "cr-test-book",
                ["chapter"] = "1",
            }));

            string aToday = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string aWantCh1 = string.Join("\r\n", new[]
            {
                "---",
                "book: cr-test-book",
                "chapter: 1",
                "title: ",                       // ← ⚠ 尾隨空格是產物的一部分，不是編輯失誤
                "reading_date: " + aToday,
                "new_characters: []",
                "---",
                "",
                "## 內容摘要",
                "（待補）",
                "",
                "## 關鍵事件",
                "- （待補）",
                "",
                "## 本章對人物的新認識",
                "- （待補）",
                "",
                "## 伏筆 / 待解",
                "- （無）",
                "",
            });
            byte[] aGotCh1 = File.ReadAllBytes(Path.Combine(aBookDir, "chapters", "ch01_ch1.md"));
            bool aCh1Same = ByteEqual(aGotCh1, new UTF8Encoding(false).GetBytes(aWantCh1));

            // ── ② 書籤不倒退：跑過 ch3 之後跑 ch1，`current_chapter` 必須仍是 3 ──
            string aJsonAfterCh = File.ReadAllText(aBookJson, Encoding.UTF8);
            bool aNoRegress = aJsonAfterCh.Contains("\"current_chapter\": 3");

            // ── ③ arc：先 7-9，再用同範圍 1-6 覆蓋一次（python 會把它移到陣列尾端）──
            aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "arc", ["book"] = "cr-test-book",
                ["chapters"] = "1-6", ["title"] = "第一階段",
            }));
            aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "arc", ["book"] = "cr-test-book",
                ["chapters"] = "7-9", ["title"] = "第二階段",
            }));
            aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "arc", ["book"] = "cr-test-book",
                ["chapters"] = "1-6", ["title"] = "第一階段（改）",
            }));
            string aWantArc = string.Join("\r\n", new[]
            {
                "---",
                "book: cr-test-book",
                "chapters: 1-6",
                "title: 第一階段（改）",
                "date: " + aToday,
                "---",
                "",
                "## 階段大綱（見林）",
                "（待補）",
                "",
                "## 貫穿線索 / 伏筆狀態",
                "- （待補）",
                "",
            });
            byte[] aGotArc = File.ReadAllBytes(Path.Combine(aBookDir, "arcs", "arc_1-6.md"));
            bool aArcSame = ByteEqual(aGotArc, new UTF8Encoding(false).GetBytes(aWantArc));

            string aJsonAfterArc = File.ReadAllText(aBookJson, Encoding.UTF8);
            int aIdx79 = aJsonAfterArc.IndexOf("\"7-9\"", StringComparison.Ordinal);
            int aIdx16 = aJsonAfterArc.IndexOf("\"1-6\"", StringComparison.Ordinal);
            // 取代那筆要移到尾端 ⇒ 7-9 在前、1-6 在後；且只剩兩筆（沒有重複登記）。
            bool aArcOrder = aIdx79 >= 0 && aIdx16 > aIdx79
                             && CountOccurrences(aJsonAfterArc, "\"1-6\"") == 1;

            // ── ④ `--reader` 分支：首次啟用要自動 init，且 **不影響初始讀者** ──
            aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "log-chapter", ["book"] = "cr-test-book",
                ["chapter"] = "2", ["title"] = "分支章", ["reader"] = "gura",
            }));
            string aWantBranch = string.Join("\r\n", new[]
            {
                "{",
                "  \"id\": \"cr-test-book::gura\",",
                "  \"title\": \"對拍用書\",",
                "  \"title_original\": \"\",",
                "  \"author\": \"誰\",",
                "  \"reader_persona\": \"gura\",",
                "  \"branch_of\": \"cr-test-book\",",
                "  \"branched_from\": \"(獨立起讀)\",",
                "  \"status\": \"reading\",",
                "  \"progress\": {",
                "    \"current_chapter\": 2,",
                "    \"last_read\": \"" + aToday + "\",",
                "    \"bookmark_note\": \"\"",
                "  },",
                "  \"characters\": []",
                "}",
                "",
            });
            byte[] aGotBranch = File.ReadAllBytes(
                Path.Combine(aBookDir, "branches", "gura", "book.json"));
            bool aBranchSame = ByteEqual(aGotBranch, new UTF8Encoding(false).GetBytes(aWantBranch));
            // 初始讀者那份的章號不准被分支動到（分支寫 ch2，main 仍該是 3）。
            bool aMainUntouched = File.ReadAllText(aBookJson, Encoding.UTF8)
                                      .Contains("\"current_chapter\": 3");

            // ── ⑤ 反向對照：書不存在時要擋（exit 1）且**不准生出目錄** ──
            //    ⛔ 只驗「該寫的寫了」的話，一個「什麼書名都先建目錄」的實作也會全綠。
            SCP_CmdResult aMiss = aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["op"] = "log-chapter", ["book"] = "沒有這本書",
                ["chapter"] = "1",
            }));
            bool aGuarded = aMiss.ExitCode == 1
                            && !Directory.Exists(Path.Combine(aTmp, "BookNotes", "沒有這本書"));

            bool aOk = aCh1Same && aNoRegress && aArcSame && aArcOrder
                       && aBranchSame && aMainUntouched && aGuarded;
            string aReading =
                $"ch01（全省略）逐位元組：{(aCh1Same ? "相同" : "**不同**")}"
                + $"（{aGotCh1.Length} bytes、CRLF {CountCrLf(aGotCh1)}／換行 {CountLf(aGotCh1)}）"
                + $"；arc_1-6 逐位元組：{(aArcSame ? "相同" : "**不同**")}（{aGotArc.Length} bytes）"
                + $"；分支 book.json 逐位元組：{(aBranchSame ? "相同" : "**不同**")}（{aGotBranch.Length} bytes）"
                + $"；書籤不倒退：{(aNoRegress ? "current_chapter 仍是 3" : "**被 ch1 蓋掉了**")}"
                + $"；arcs[] 同範圍取代：{(aArcOrder ? "7-9 在前、1-6 移到尾端且只一筆" : "**順序或筆數不對**")}"
                + $"；分支不影響初始讀者：{(aMainUntouched ? "main 仍是 3" : "**main 被動了**")}"
                + $"；書不存在守衛：{(aGuarded ? $"擋下（exit {aMiss.ExitCode}）且沒生出目錄" : "**沒擋住或生了目錄**")}";

            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e)
        {
            return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail);
        }
        finally
        {
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { /* 清不掉不影響判定 */ }
        }
    }

    /// <summary>數一個字面出現幾次（`"1-6"` 有沒有被重複登記那一格要它）。</summary>
    static int CountOccurrences(string iText, string iNeedle)
    {
        int aCount = 0;
        for (int i = iText.IndexOf(iNeedle, StringComparison.Ordinal); i >= 0;
             i = iText.IndexOf(iNeedle, i + iNeedle.Length, StringComparison.Ordinal))
            aCount++;
        return aCount;
    }

    static CheckRow BookAddCleanRoom()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bookadd_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(aTmp, "BookNotes"));
            var aCmd = new SCP_Cmd_Book();

            // ⭐ 走 Bind 不自己塞值 —— 順帶讓這格測試也吃到 ArgSpec 預檢：
            //   我在測試裡打錯參數名會**當場炸**，不會靜默取預設值然後綠著過。
            SCP_CmdArgs Args(Dictionary<string, string> iRaw)
            {
                var (a, aErrs) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, iRaw);
                if (a == null) throw new InvalidOperationException(string.Join("；", aErrs));
                return a;
            }

            SCP_CmdResult aR1 = aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp,
                ["op"] = "add",
                ["title"] = "深海的對拍錄 Vol.2",
                ["title_original"] = "Abyssal Recheck",
                ["author"] = "gura",
                // 刻意讓第一個別名與 title 重複 ⇒ 驗去重；分隔符 `;` 與 `|` 各出現一次。
                ["aliases"] = "深海的對拍錄 Vol.2;鯊魚札記|對拍錄",
                ["origin"] = "authored",
                ["author_persona"] = "gura",
            }));

            string aOut = Path.Combine(aTmp, "BookNotes", "深海的對拍錄-vol-2", "book.json");
            if (aR1.ExitCode != 0 || !File.Exists(aOut))
                return new CheckRow("add-book clean-room（對照 library.py 真產物）",
                    $"建檔失敗：exit={aR1.ExitCode}、檔案存在={File.Exists(aOut)}", CheckResult.Fail);

            string aToday = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string aWant = string.Join("\r\n", new[]
            {
                "{",
                "  \"id\": \"深海的對拍錄-vol-2\",",
                "  \"title\": \"深海的對拍錄 Vol.2\",",
                "  \"title_original\": \"Abyssal Recheck\",",
                "  \"author\": \"gura\",",
                "  \"aliases\": [",
                "    \"深海的對拍錄 Vol.2\",",
                "    \"鯊魚札記\",",
                "    \"對拍錄\",",
                "    \"Abyssal Recheck\"",
                "  ],",
                "  \"reader_persona\": \"gura\",",
                "  \"status\": \"writing\",",
                "  \"progress\": {",
                "    \"current_chapter\": 0,",
                "    \"last_read\": \"" + aToday + "\"",
                "  },",
                "  \"characters\": [],",
                "  \"origin\": \"authored\",",
                "  \"author_persona\": \"gura\",",
                "  \"publish_status\": \"draft\"",
                "}",
                "",
            });

            byte[] aGot = File.ReadAllBytes(aOut);
            bool aSame = ByteEqual(aGot, new UTF8Encoding(false).GetBytes(aWant));

            // ── ④ 反向對照：再建一次要被擋，而且**不准動到既有檔** ──
            SCP_CmdResult aR2 = aCmd.Execute(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp,
                ["op"] = "add",
                ["id"] = "深海的對拍錄-vol-2",
                ["title"] = "覆寫用的假書名",
                ["aliases"] = "覆寫用的假書名",
            }));
            bool aGuarded = aR2.ExitCode == 1 && ByteEqual(File.ReadAllBytes(aOut), aGot);

            string aReading =
                $"逐位元組對 python 真產物：{(aSame ? "相同" : "**不同**")}（{aGot.Length} bytes、"
                + $"CRLF {CountCrLf(aGot)} 個／換行 {CountLf(aGot)} 個）"
                + $"；重建守衛：{(aGuarded ? $"擋下（exit {aR2.ExitCode}）且既有檔未被動" : "**沒擋住或檔被動了**")}";

            return new CheckRow("add-book clean-room（對照 library.py 真產物）", aReading,
                aSame && aGuarded ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e)
        {
            return new CheckRow("add-book clean-room（對照 library.py 真產物）",
                "例外：" + e.Message, CheckResult.Fail);
        }
        finally
        {
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { /* 清不掉不影響判定 */ }
        }
    }

    // 區塊職責：`op=writing`／brief §6.7 的篩選 —— **驗該被排除的有沒有真的不見**。
    // 物理意義：只驗「有列出來」驗不到東西：一個永遠回全部的實作也會通過。
    //          所以這裡放三本**應該被排除**的（已發布／imported／沒有 origin），
    //          它們有沒有消失才是這一格的讀數。
    // 數值影響：純暫存目錄，⛔ 不碰真 store。另驗「0 本」與「未量」**必須是兩個不同的結局** ——
    //          🩸 那兩件事在畫面上同形，而人往那個空格裡填的一定是「沒事」。
    static CheckRow BookWritingFilter()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bookwrite_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aCmd = new SCP_Cmd_Book();
            SCP_CmdArgs Args(Dictionary<string, string> iRaw)
            {
                var (a, aErrs) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, iRaw);
                if (a == null) throw new InvalidOperationException(string.Join("；", aErrs));
                return a;
            }

            Directory.CreateDirectory(Path.Combine(aTmp, "BookNotes"));
            void Add(string iId, string iOrigin, string iPersona)
            {
                var aRaw = new Dictionary<string, string>
                {
                    ["data_root"] = aTmp, ["op"] = "add", ["id"] = iId,
                    ["title"] = iId, ["aliases"] = iId,
                };
                if (iOrigin.Length > 0) aRaw["origin"] = iOrigin;
                if (iPersona.Length > 0) aRaw["author_persona"] = iPersona;
                aCmd.Execute(Args(aRaw));
            }

            Add("a-writing", "authored", "basecamp");    // ← 只有這本該出現
            Add("b-published", "authored", "basecamp");  // 下一步改成 published
            Add("c-imported", "imported", "");
            Add("d-noorigin", "", "");

            // 改成已發布 —— 直接改檔：這裡是暫存根，而「發布」不是 add 的職責。
            string aBJson = Path.Combine(aTmp, "BookNotes", "b-published", "book.json");
            File.WriteAllText(aBJson,
                File.ReadAllText(aBJson, Encoding.UTF8).Replace("\"draft\"", "\"published\""),
                new UTF8Encoding(false));

            if (!SCP_BookStore.TryListWriting(aTmp, null, out List<SCP_AuthoredBook> aAll, out _))
                return new CheckRow("寫到一半的書：篩選 ＋ 未量／零本分家",
                    "四本都建好了卻讀不到書庫", CheckResult.Fail);

            var aIds = new List<string>();
            foreach (SCP_AuthoredBook aBook in aAll) aIds.Add(aBook.Id);
            aIds.Sort(StringComparer.Ordinal);
            bool aOnlyOne = aIds.Count == 1 && aIds[0] == "a-writing";

            // persona 篩選：別人的名字要回 0 本（⛔ 不是回全部）
            SCP_BookStore.TryListWriting(aTmp, "someone-else", out List<SCP_AuthoredBook> aOther, out _);

            // 「0 本」與「未量」必須分得開
            string aEmpty = Path.Combine(aTmp, "empty");
            Directory.CreateDirectory(Path.Combine(aEmpty, "BookNotes"));
            bool aZeroOk = SCP_BookStore.TryListWriting(aEmpty, null, out List<SCP_AuthoredBook> aZero, out _)
                           && aZero.Count == 0;
            bool aUnmeasured = !SCP_BookStore.TryListWriting(Path.Combine(aTmp, "no-such-root"), null,
                                                            out _, out string aWhy)
                               && aWhy.Length > 0;

            string aReading =
                $"四本進 ⇒ 列出 [{string.Join(" , ", aIds)}]"
                + $"（該排除的 b-published／c-imported／d-noorigin {(aOnlyOne ? "**都不見了**" : "**沒排乾淨**")}）"
                + $"；別人的 persona ⇒ {aOther.Count} 本"
                + $"；空書庫 ⇒ {(aZeroOk ? "0 本且 ok=true" : "**不是 0**")}"
                + $"；根不存在 ⇒ {(aUnmeasured ? "ok=false 且說得出原因" : "**沒有分家**")}";

            return new CheckRow("寫到一半的書：篩選 ＋ 未量／零本分家", aReading,
                aOnlyOne && aOther.Count == 0 && aZeroOk && aUnmeasured ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e)
        {
            return new CheckRow("寫到一半的書：篩選 ＋ 未量／零本分家", "例外：" + e.Message, CheckResult.Fail);
        }
        finally
        {
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { /* 清不掉不影響判定 */ }
        }
    }

    static int CountCrLf(byte[] iBytes)
    {
        int n = 0;
        for (int i = 1; i < iBytes.Length; ++i) if (iBytes[i] == (byte)'\n' && iBytes[i - 1] == (byte)'\r') ++n;
        return n;
    }

    static int CountLf(byte[] iBytes)
    {
        int n = 0;
        foreach (byte b in iBytes) if (b == (byte)'\n') ++n;
        return n;
    }

    // 區塊職責：Server 端寫的 result 檔，CLI 端（AgentCmdClient）讀得回同樣的東西 —— 協議第四端的對拍。
    // 物理意義：兩端各自實作 schema，漂掉的症狀是「Server 說成功、CLI 印不出 values」而兩邊都不紅。
    //          這裡不需要真的 Server：WriteResult 是 public static，直接對一個暫存根寫再讀。
    static CheckRow ServerResultRoundTrip()
    {
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_server_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aRes = SCP.Core.Cmd.SCP_CmdResult.Success("第一行", "第二行（中文不轉義）");
            aRes.AddOutput("D:/x/回傳.md").AddValue("seq", "17").AddValue("seq", "18");
            var aArgs = new Dictionary<string, string> { ["_caller_client"] = "selftest", ["echo"] = "hi" };
            ServerExecutor.WriteResult(aRoot, "id-1", "server-ping", "OneShot", aArgs, aRes);
            var (aFound, aOuts, aVals) = AgentCmdClient.ResultReport(aRoot, "id-1");
            List<string> aLines = AgentCmdClient.ResultLines(aRoot, "id-1");
            bool aOutOk = aFound && aOuts.Count == 1 && aOuts[0] == "D:/x/回傳.md";
            bool aValOk = aVals.Count == 2 && aVals[0].Key == "seq" && aVals[1].Value == "18";   // 同 key 兩筆都要活著
            bool aLineOk = aLines.Count == 2 && aLines[1].Contains("中文", StringComparison.Ordinal);
            bool aClientOk = File.ReadAllText(Path.Combine(aRoot, "_cmd_results", "id-1.json")).Contains("\"client\": \"selftest\"", StringComparison.Ordinal);
            var aFail = SCP.Core.Cmd.SCP_CmdResult.Fail(1, "✗ 壞了");
            ServerExecutor.WriteResult(aRoot, "id-2", "server-ping", "OneShot", aArgs, aFail);
            bool aFailOk = File.ReadAllText(Path.Combine(aRoot, "_cmd_results", "id-2.json")).Contains("\"result\": \"Failed\"", StringComparison.Ordinal);
            bool aOk = aOutOk && aValOk && aLineOk && aClientOk && aFailOk;
            return new CheckRow("Server result 檔 round-trip",
                $"outputs 讀回={aOutOk}／同 key 兩筆 values 都在={aValOk}／lines 讀回={aLineOk}／client 欄={aClientOk}／Failed 落檔={aFailOk}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally { try { if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true); } catch { } }
    }

    // 區塊職責：執行端 Cmd 自己回的退出碼，要能原樣被讀回；而錯誤報告的宣告要跟「真的有寫」同一個判準（TASK-0262）。
    // 物理意義：以前委派閘把任何 Failed 都寫死成 exit 1 ⇒ 用法錯（2）在呼叫端跟執行失敗（1）同形，
    //          而 1 那條規矩會去宣告一份報告路徑、寫檔那端照 2 的規矩（正確地）沒寫
    //          ⇒ CLI 自己承認找不到，再猜「Server 端沒寫成？」。
    //          ⇒ 這一格量的是**接縫的兩側**：result 檔寫得對不對（exit_code／error_report），
    //             以及 AgentCmdClient 讀不讀得回來。
    // 🩸 為什麼是 selftest 而不是只靠活體：活體那半要真的起一顆 Server（會打斷所有在線的人），
    //   ⇒ 活體是另外一格、由人挑時機跑；這一格是**每天都會跑到的那條路上的錨**。
    //   ⚠ 它**不涵蓋** ServerDelegateCmd 那一段的接線（那要活體）—— 兩者不可同形，所以這裡寫明。
    // 數值影響：純寫暫存根 + 讀回，零 Cmd 派遣、不碰任何真的資料根。
    static CheckRow DelegateCarriesCmdExitCode()
    {
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_exit_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aArgs = new Dictionary<string, string> { ["_caller_client"] = "selftest" };

            // ① exit 2（用法錯）：exit_code 讀得回 2，而 result 檔**不宣告**報告路徑（那份檔設計上就不該存在）
            ServerExecutor.WriteResult(aRoot, "id-usage", "server-ping", "OneShot", aArgs,
                SCP.Core.Cmd.SCP_CmdResult.Fail(2, "✗ 用法錯"));
            int? aUsageExit = AgentCmdClient.ResultExitCode(aRoot, "id-usage");
            bool aUsageNoReport = !File.ReadAllText(Path.Combine(aRoot, "_cmd_results", "id-usage.json"))
                .Contains("error_report", StringComparison.Ordinal);

            // ② exit 1（回報失敗）：一字不變 —— 仍是 1，仍然宣告報告路徑（反向對照）
            ServerExecutor.WriteResult(aRoot, "id-failed", "server-ping", "OneShot", aArgs,
                SCP.Core.Cmd.SCP_CmdResult.Fail(1, "✗ 真的爆了"));
            int? aFailedExit = AgentCmdClient.ResultExitCode(aRoot, "id-failed");
            bool aFailedHasReport = File.ReadAllText(Path.Combine(aRoot, "_cmd_results", "id-failed.json"))
                .Contains("error_report", StringComparison.Ordinal);

            // ③ 沒有 result 檔 ⇒ null，⛔ 不是 0（「這一欄不存在」與「它回了 0」是兩件事）
            bool aMissingIsNull = AgentCmdClient.ResultExitCode(aRoot, "id-not-there") == null;

            bool aOk = aUsageExit == 2 && aUsageNoReport && aFailedExit == 1 && aFailedHasReport && aMissingIsNull;
            return new CheckRow("委派帶回 Cmd 自己的退出碼",
                $"exit 2 讀回={aUsageExit?.ToString() ?? "null"}／exit 2 不宣告報告={aUsageNoReport}／"
                + $"exit 1 讀回={aFailedExit?.ToString() ?? "null"}／exit 1 仍宣告報告={aFailedHasReport}／缺檔回 null={aMissingIsNull}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally { try { if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true); } catch { } }
    }

    // 區塊職責：逾時的成因描述必須**跟著 lane 的現況變**，三態各自不同形。
    // 物理意義：逾時本身分不出「宿主不在」「宿主在跑」「跑完了我沒接到」，而這三種的下一步互斥
    //          （開 Editor ／ 等它 ／ 去讀 result 檔）。三句話塌成一句的症狀是：讀的人照著
    //          一句已知為假的診斷，去檢查一個沒有問題的宿主（2026-09-04／09-05 兩次血證）。
    // 🩸 為什麼要這一格（TASK-0227）：`running` 與 `pending` 兩支我用活體對照驗過
    //   （指向沒有 Editor 的空樹／trigger 出現後造 .running），⛔ 而 **`idle` 那一支造不出乾淨的活體**
    //   —— 它要求「lane 空了而我沒接到 result」，那是一個競態。
    //   ⇒ 沒有活體就把它做成可重跑的讀數；把空白留著才是讓「沒驗」跟「驗過」同形。
    // 數值影響：純建／刪暫存檔＋字串比對，不派任何 Cmd、不碰真的資料根。
    static CheckRow WaitTimeoutDescribesLane()
    {
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_lane_" + Guid.NewGuid().ToString("N")[..8]);
        const string aPersona = "selftest-persona";
        try
        {
            string aTrigger = AgentCmdClient.TriggerPath(aRoot, aPersona);
            string aRunning = AgentCmdClient.RunningPath(aRoot, aPersona);
            Directory.CreateDirectory(Path.GetDirectoryName(aTrigger)!);

            // ① lane 空 ⇒ 「很可能已經跑完了」，而且要把 result 檔的路徑交出來（不然「去看 mtime」是空話）
            string aIdle = AgentCmdClient.DescribeWaitTimeout(aRoot, aPersona, "cid-1", 20);
            bool aIdleOk = aIdle.Contains("'idle'", StringComparison.Ordinal)
                           && aIdle.Contains("cid-1", StringComparison.Ordinal);

            // ② trigger 還在 ⇒ 沒被取走，這時「宿主沒開？」才是一個合理的**假設**
            File.WriteAllText(aTrigger, "x");
            string aPending = AgentCmdClient.DescribeWaitTimeout(aRoot, aPersona, "cid-1", 20);
            bool aPendingOk = aPending.Contains("'pending'", StringComparison.Ordinal)
                              && aPending.Contains("還沒被取走", StringComparison.Ordinal);

            // ③ .running 在 ⇒ 取走了還在跑。⛔ 這一支**不准**再提「沒開」——它已經被量掉了
            File.WriteAllText(aRunning, "x");
            string aRun = AgentCmdClient.DescribeWaitTimeout(aRoot, aPersona, "cid-1", 20);
            bool aRunOk = aRun.Contains("'running'", StringComparison.Ordinal)
                          && !aRun.Contains("沒開", StringComparison.Ordinal);

            // ④ 三句必須互不相同 —— 「都有印東西」與「印的是對的那句」是兩件事
            bool aDistinct = aIdle != aPending && aPending != aRun && aIdle != aRun;
            // ⑤ 宿主標籤要吃得進去（Server 那條路的呼叫端會傳別的名字）
            bool aHostOk = AgentCmdClient.DescribeWaitTimeout(aRoot, aPersona, "cid-1", 20, "Server")
                           .Contains("Server", StringComparison.Ordinal);

            bool aOk = aIdleOk && aPendingOk && aRunOk && aDistinct && aHostOk;
            return new CheckRow("逾時成因跟著 lane 現況變（三態不同形）",
                $"idle={aIdleOk}／pending={aPendingOk}／running 不再提「沒開」={aRunOk}"
                + $"／三句互異={aDistinct}／宿主標籤={aHostOk}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally { try { if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true); } catch { } }
    }

    // 區塊職責：錯誤報告的判準與內容 —— 該寫的才寫、長值截斷、stack 有留、client 有欄。
    // 物理意義：報告是「失敗之後唯一能回頭看的地方」，它自己漂掉沒有人會發現（失敗時沒人在看它長什麼樣）。
    static CheckRow ErrorReportShape()
    {
        // exit 3（沒有結果）2026-09-04 起一律不寫 —— 逾時那筆對面其實跑完了，報告不該被宣告（TASK-0104 QA）。
        bool aPolicy = CmdErrorReport.ShouldReport(1) && CmdErrorReport.ShouldReport(70)
                       && !CmdErrorReport.ShouldReport(2) && !CmdErrorReport.ShouldReport(0)
                       && !CmdErrorReport.ShouldReport(3);
        var aRes = SCP.Core.Cmd.SCP_CmdResult.Fail(70, "✗ 爆了");
        try { throw new InvalidOperationException("測試用例外"); } catch (Exception e) { aRes.Exception = e; }
        string aLong = string.Join("\n", Enumerable.Range(1, 40).Select(i => "line" + i));
        var aArgs = new Dictionary<string, string> { ["body"] = aLong, ["persona"] = "probe", ["_caller_client"] = "selftest" };
        string aText = CmdErrorReport.Render("id-x", "probe-cmd", aArgs, aRes, "local");
        bool aStack = aText.Contains("InvalidOperationException", StringComparison.Ordinal) && aText.Contains("Stack trace", StringComparison.Ordinal);
        bool aTrunc = aText.Contains("40 行，只印前 20 行", StringComparison.Ordinal) && !aText.Contains("line21", StringComparison.Ordinal) && aText.Contains("line20", StringComparison.Ordinal);
        bool aClient = aText.Contains("**client**: selftest", StringComparison.Ordinal);
        bool aHost = aText.Contains("**執行位置**: local", StringComparison.Ordinal);
        bool aExit = aText.Contains("**exit_code**: 70", StringComparison.Ordinal);
        bool aOk = aPolicy && aStack && aTrunc && aClient && aHost && aExit;
        return new CheckRow("錯誤報告形狀",
            $"判準（1/70 寫、0/2/3 不寫）={aPolicy}／stack 有留={aStack}／40 行值截成 20={aTrunc}／client 欄={aClient}／執行位置={aHost}／exit_code={aExit}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>「不存在」不可以長得像「空值」—— 讀 Missing 必須丟例外。</summary>
    // 區塊職責：process 四態的**分類邏輯**（Alive／Dead／PidReused／Unknown）＋ 四種狀態各有各的字。
    // 物理意義：🩸 TASK-0101 QA（summit 2026-09-03）量到 Dead／PidReused **在任何 QA 能驅動的路徑上都到不了畫面** ——
    //           `Main` 每次先跑 CleanupStale，`--window --screenshot` 也是一次新的 Main。
    //           而她 grep 完當時 28 格 selftest：沒有任何一格碰到這四態。
    //           ⇒ 那兩態當時由「讀 code 覺得對」保證。本格把分類搬到一個**不需要畫面也不需要活體**的地方：
    //           直接餵四筆記錄給 Validate。畫面呈現那半留給 Alive／Unknown（QA 驅動得到的那兩態）。
    //           ⭐ 2026-09-09（TASK-0123）畫面那半也有路了：`senate ui --no-cleanup` 跳過渲染前的清理
    //           ⇒ Dead 與 PidReused **真的畫得出來**（實測：`server start` → `taskkill /F` ⇒ `・DEAD`；
    //           把 `start_time_utc` 回撥一小時 ⇒ `▲ PID 已易主`；兩態都**沒有** Kill 鈕，復原後回 ALIVE 且 Kill 鈕出現）。
    //           ⛔ 本格仍然留著：它量的是**分類**，不需要活體也不需要畫面 —— 那兩件事不該綁在一起。
    // 數值影響：純讀 OS 的 process 表，不寫檔、不殺任何東西。
    static CheckRow ProcessStatusClassification()
    {
        using var aSelf = System.Diagnostics.Process.GetCurrentProcess();
        // Alive：三個身分欄全部吻合本行程（name ＋ start time 都要對，只有 pid 不算數）。
        var aAliveRec = new SCP.Core.Proc.SCP_ProcessRecord
        {
            Pid = aSelf.Id, ProcessName = aSelf.ProcessName,
            StartTimeUtcText = aSelf.StartTime.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        // PidReused：pid 真的活著，但名字不是當初登記的那顆 ⇒ 這個 pid 被 OS 回收再發給別人了。
        var aReusedRec = new SCP.Core.Proc.SCP_ProcessRecord
        {
            Pid = aSelf.Id, ProcessName = "definitely-not-this-process",
            StartTimeUtcText = aAliveRec.StartTimeUtcText,
        };
        // PidReused（第二條路）：名字對而**啟動時間差太多** —— 兩條路要分開驗，不然只證明其中一條。
        var aReusedByTimeRec = new SCP.Core.Proc.SCP_ProcessRecord
        {
            Pid = aSelf.Id, ProcessName = aSelf.ProcessName,
            StartTimeUtcText = aSelf.StartTime.ToUniversalTime().AddHours(-3).ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        };
        var aDeadRec = new SCP.Core.Proc.SCP_ProcessRecord { Pid = 0x3FFFFFFF, ProcessName = "senate" };   // 不存在的 pid
        var aUnknownRec = new SCP.Core.Proc.SCP_ProcessRecord { Pid = 0, ProcessName = "senate" };         // 沒有 pid 可問

        var aStatus = SCP.Core.Proc.SCP_ProcessRegistry.Validate(aAliveRec);
        var aReused = SCP.Core.Proc.SCP_ProcessRegistry.Validate(aReusedRec);
        var aReused2 = SCP.Core.Proc.SCP_ProcessRegistry.Validate(aReusedByTimeRec);
        var aDead = SCP.Core.Proc.SCP_ProcessRegistry.Validate(aDeadRec);
        var aUnknown = SCP.Core.Proc.SCP_ProcessRegistry.Validate(aUnknownRec);
        var aNull = SCP.Core.Proc.SCP_ProcessRegistry.Validate(null);

        bool aClass = aStatus == SCP.Core.Proc.SCP_ProcessStatus.Alive
                      && aReused == SCP.Core.Proc.SCP_ProcessStatus.PidReused
                      && aReused2 == SCP.Core.Proc.SCP_ProcessStatus.PidReused
                      && aDead == SCP.Core.Proc.SCP_ProcessStatus.Dead
                      && aUnknown == SCP.Core.Proc.SCP_ProcessStatus.Unknown
                      && aNull == SCP.Core.Proc.SCP_ProcessStatus.Unknown;
        // 四種狀態各自的說明字必須互不相同 —— 併成同一句就等於畫面上分不出來（四態分開的理由本身）。
        var aTexts = new List<string>
        {
            SCP.Core.Proc.SCP_ProcessRegistry.StatusText(SCP.Core.Proc.SCP_ProcessStatus.Alive),
            SCP.Core.Proc.SCP_ProcessRegistry.StatusText(SCP.Core.Proc.SCP_ProcessStatus.Dead),
            SCP.Core.Proc.SCP_ProcessRegistry.StatusText(SCP.Core.Proc.SCP_ProcessStatus.PidReused),
            SCP.Core.Proc.SCP_ProcessRegistry.StatusText(SCP.Core.Proc.SCP_ProcessStatus.Unknown),
        };
        bool aDistinct = aTexts.TrueForAll(t => !string.IsNullOrWhiteSpace(t)) && aTexts.Distinct(StringComparer.Ordinal).Count() == 4;
        bool aOk = aClass && aDistinct;
        return new CheckRow("process 四態分類",
            $"本行程三欄吻合⇒Alive={aStatus}／換名字⇒{aReused}／啟動時間差 3 小時⇒{aReused2}／不存在的 pid⇒{aDead}／pid=0⇒{aUnknown}／null⇒{aNull}／四種說明字互不相同={aDistinct}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    static CheckRow MissingSemantics()
    {
        var aData = SCP_JsonData.Parse("{\"a\":1}");
        bool aThrew = false;
        string aPath = "";
        try { _ = aData["b"].AsString(); }
        catch (SCP_JsonMissingException e) { aThrew = true; aPath = e.Message; }

        bool aFallbackOk = aData.GetString("b", "預設值") == "預設值";
        bool aExistsOk = !aData["b"].Exists && aData["a"].Exists;

        return new CheckRow(
            "Missing 語意",
            aThrew
                ? $"讀不存在的 key 會丟例外（訊息帶路徑）／fallback={aFallbackOk}／Exists 判定={aExistsOk}"
                : "⚠ 讀不存在的 key **沒有**丟例外",
            aThrew && aFallbackOk && aExistsOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>同樣的資料輸出兩次必須逐字相同，且 key 照插入順序（不然 diff 會滿江紅）。</summary>
    static CheckRow WriterStability()
    {
        var aObj = SCP_JsonData.NewObject();
        aObj.Set("zebra", "斑馬");
        aObj.Set("apple", 42);
        aObj.Set("中文鍵", true);
        string a1 = aObj.ToJson();
        string a2 = SCP_JsonData.Parse(a1).ToJson();
        bool aOrderKept = a1.IndexOf("zebra", StringComparison.Ordinal) < a1.IndexOf("apple", StringComparison.Ordinal);
        bool aCjkRaw = a1.Contains("中文鍵", StringComparison.Ordinal);

        return new CheckRow(
            "輸出穩定性",
            $"round-trip 逐字相同={a1 == a2}／插入順序保留={aOrderKept}／中文不轉義={aCjkRaw}",
            a1 == a2 && aOrderKept && aCjkRaw ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>
    /// 頁面堆疊的性質：只有最上方那頁會被畫、生命週期呼叫順序、同實例 push 兩次要擋、
    /// 導覽路徑存讀一致、認不得的 key 要**停在那裡**而不是悄悄退回根頁。
    /// </summary>
    static CheckRow PageStack()
    {
        var aLog = new List<string>();
        var aCtrl = new SCP_GuiPageController();
        var aA = new ProbePage("a", aLog);
        var aB = new ProbePage("b", aLog);

        aCtrl.Push(aA);
        aCtrl.Push(aB);

        // ① 只畫最上方那頁
        var aUi = new SCP_Ui();
        aCtrl.Draw(aUi);
        string aText = SCP_GuiTextRenderer.Render(aUi.Root);
        bool aOnlyTop = aText.Contains("我是 b", StringComparison.Ordinal)
                        && !aText.Contains("我是 a", StringComparison.Ordinal);

        // ② 返回鈕在 Count>1 時存在且 id 固定（agent 靠它返回）
        bool aBackExists = SCP_GuiQuery.Find(aUi.Root, SCP_GuiPageController.BackButtonId) != null;

        // ③ 同一個實例 push 兩次要丟例外（stack 裡兩個相同引用會讓 Pop/Remove 看運氣）
        bool aDupBlocked = false;
        try { aCtrl.Push(aB); }
        catch (InvalidOperationException) { aDupBlocked = true; }

        // ④ 導覽路徑存讀一致（走 SCP_GuiState 的 nav）
        var aState = new SCP_GuiState();
        aState.Nav = aCtrl.PathKeys;
        var aBack = SCP_GuiState.FromJson(SCP_JsonData.Parse(aState.ToJson().ToJson()));
        bool aNavOk = aBack.Nav.Count == 2 && aBack.Nav[0] == "a" && aBack.Nav[1] == "b";

        // ⑤ pop 的生命週期順序
        aCtrl.Pop();
        bool aLifecycle = string.Join(",", aLog) == "a:push,b:push,a:pause,b:close,a:resume";

        // ⑥ 認不得的 key ⇒ 回報它、停在原地（不可以悄悄退回根頁）
        var aCtrl2 = new SCP_GuiPageController();
        aCtrl2.Push(new ProbePage("a", aLog));
        string? aBadKey = aCtrl2.RestorePath(new List<string> { "a", "沒這頁" },
                                             k => k == "a" ? null : null);
        bool aStopOnUnknown = aBadKey == "沒這頁" && aCtrl2.Count == 1;

        bool aOk = aOnlyTop && aBackExists && aDupBlocked && aNavOk && aLifecycle && aStopOnUnknown;
        return new CheckRow("頁面堆疊",
            $"只畫最上頁={aOnlyTop}／返回鈕={aBackExists}／同實例擋下={aDupBlocked}／nav 存讀={aNavOk}"
            + $"／生命週期順序={aLifecycle}（{string.Join(",", aLog)}）／未知 key 停手={aStopOnUnknown}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }



    /// <summary>
    /// 摺疊的語意：收合時**子節點不存在**（不是畫了再隱藏）、狀態由輸入決定、可存進 session、
    /// 而且可摺疊的框要出現在「可互動元件」清單裡（看不見畫面的人才知道有東西被收起來）。
    /// </summary>
    static CheckRow FoldSemantics()
    {
        // ① 預設展開 ⇒ 內容在
        var aOpenUi = new SCP_Ui();
        DrawFoldProbe(aOpenUi);
        string aOpenText = SCP_GuiTextRenderer.Render(aOpenUi.Root, 120);
        bool aOpenOk = aOpenText.Contains("▼", StringComparison.Ordinal)
                       && aOpenText.Contains("裡面的內容", StringComparison.Ordinal);

        // ② 收合 ⇒ 內容**根本沒被建出來**（樹裡找不到，不是畫面上看不到）
        var aInput = new SCP_GuiInput();
        aInput.Folds["probe/box"] = false;
        var aShutUi = new SCP_Ui(aInput);
        DrawFoldProbe(aShutUi);
        string aShutText = SCP_GuiTextRenderer.Render(aShutUi.Root, 120);
        bool aShutOk = aShutText.Contains("▶", StringComparison.Ordinal)
                       && !aShutText.Contains("裡面的內容", StringComparison.Ordinal);

        // ③ 可摺疊的框要在可互動清單裡，而且 HowTo 是 --fold
        var aElem = SCP_GuiQuery.Find(aOpenUi.Root, "probe/box");
        bool aListed = aElem != null && aElem.HowTo == "--fold probe/box" && aElem.On;

        // ④ session 存讀（摺疊是偏好，跟資料分開存）
        var aState = new SCP_GuiState();
        aState.Folds["probe/box"] = false;
        var aBack = SCP_GuiState.FromJson(SCP_JsonData.Parse(aState.ToJson().ToJson()));
        bool aPersisted = aBack.Folds.TryGetValue("probe/box", out bool aVal) && !aVal
                          && aBack.ToInput(null).Folds["probe/box"] == false;

        bool aOk = aOpenOk && aShutOk && aListed && aPersisted;
        return new CheckRow("摺疊",
            $"展開時內容在={aOpenOk}／收合時子節點不存在={aShutOk}／出現在可互動清單（--fold）={aListed}"
            + $"／session 存讀={aPersisted}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    static void DrawFoldProbe(SCP_Ui iUi)
    {
        using (var aFold = iUi.Fold("探針區塊", "probe/box"))
            if (aFold.Open) iUi.Label("裡面的內容");
    }

    // ── 反射三層（型別快取 / 自動序列化 / 自動繪製）────────────────
    /// <summary>對拍用的探針型別 —— 刻意把「支援」與「不支援」的成員擺在一起。</summary>
    sealed class ProbeConfig
    {
        public bool Flag = true;
        public int Count = 3;
        public float Ratio = 0.5f;
        public string Name = "初值";
        public SCP_GuiSize Size = SCP_GuiSize.Medium;
        public List<string> Tags = new() { "a", "b" };
        public Dictionary<string, int> Scores = new() { { "x", 1 } };
        public ProbeChild Child = new();

        public int[] Legacy = new int[0];                       // 不支援：陣列
        public Dictionary<int, string> BadMap = new();          // 不支援：key 不是 string
        [SCP_Ignore] public string Secret = "不該出現";
        public string ReadOnlyProp => "唯讀";
    }

    sealed class ProbeChild
    {
        public int Depth = 1;
        public string Note = "child";
    }

    /// <summary>schema 的分類要對，而且**不支援的成員要留在清單裡帶原因**（不是消失）。</summary>
    static CheckRow TypeSchemaShape()
    {
        var aSchema = SCP_Reflect.SchemaOf(typeof(ProbeConfig));
        SCP_MemberSchema? aFlag = aSchema.Find("Flag");
        SCP_MemberSchema? aCount = aSchema.Find("Count");
        SCP_MemberSchema? aTags = aSchema.Find("Tags");
        SCP_MemberSchema? aScores = aSchema.Find("Scores");
        SCP_MemberSchema? aLegacy = aSchema.Find("Legacy");
        SCP_MemberSchema? aBadMap = aSchema.Find("BadMap");
        SCP_MemberSchema? aReadOnly = aSchema.Find("ReadOnlyProp");

        bool aKinds = aFlag?.Kind == SCP_ValueKind.Bool
                      && aCount?.Kind == SCP_ValueKind.Integer
                      && aSchema.Find("Ratio")?.Kind == SCP_ValueKind.Decimal
                      && aSchema.Find("Name")?.Kind == SCP_ValueKind.Text
                      && aSchema.Find("Size")?.Kind == SCP_ValueKind.Choice
                      && aTags?.Kind == SCP_ValueKind.ListOf && aTags.ElementType == typeof(string)
                      && aScores?.Kind == SCP_ValueKind.MapOf && aScores.ElementType == typeof(int)
                      && aSchema.Find("Child")?.Kind == SCP_ValueKind.Nested;

        // 不支援的要在、要有原因（消失的欄位會讓人以為資料本來就沒有那一格）
        bool aUnsupportedListed = aLegacy?.Kind == SCP_ValueKind.Unsupported
                                  && aLegacy.UnsupportedReason.Length > 0
                                  && aBadMap?.Kind == SCP_ValueKind.Unsupported
                                  && aBadMap.UnsupportedReason.Length > 0;

        bool aIgnored = aSchema.Find("Secret") == null;
        bool aReadOnlyOk = aReadOnly != null && !aReadOnly.CanWrite;
        bool aCached = ReferenceEquals(aSchema, SCP_Reflect.SchemaOf(typeof(ProbeConfig)));

        bool aOk = aKinds && aUnsupportedListed && aIgnored && aReadOnlyOk && aCached;
        return new CheckRow("型別 schema",
            $"分類={aKinds}／不支援有列且有原因={aUnsupportedListed}／[SCP_Ignore] 跳過={aIgnored}"
            + $"／唯讀屬性 CanWrite=false={aReadOnlyOk}／快取同一份={aCached}（成員 {aSchema.Members.Count} 個）",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>自動序列化的三個性質：round-trip 一致／缺 key 保留原值／型別不合不寫入且留紀錄。</summary>
    static CheckRow MapperRoundTrip()
    {
        var aSrc = new ProbeConfig
        {
            Flag = false, Count = 42, Ratio = 1.25f, Name = "改過的名字",
            Size = SCP_GuiSize.XL,
            Tags = new List<string> { "紅", "綠", "藍" },
            Scores = new Dictionary<string, int> { { "甲", 7 }, { "乙", 8 } },
            Child = new ProbeChild { Depth = 9, Note = "巢狀" },
        };

        var aWriteOpt = new SCP_JsonMapOptions();
        string aJson = SCP_JsonMapper.ToJson(aSrc, aWriteOpt).ToJson();

        // 不支援的成員必須出現在 Diagnostics（靜默略過才是 bug）
        bool aNoted = aWriteOpt.Diagnostics.Exists(d => d.Contains("Legacy", StringComparison.Ordinal))
                      && aWriteOpt.Diagnostics.Exists(d => d.Contains("BadMap", StringComparison.Ordinal));

        var aDst = new ProbeConfig();
        SCP_JsonMapper.Populate(aDst, SCP_JsonData.Parse(aJson));
        bool aSame = aDst.Flag == aSrc.Flag && aDst.Count == aSrc.Count
                     && Math.Abs(aDst.Ratio - aSrc.Ratio) < 0.0001f && aDst.Name == aSrc.Name
                     && aDst.Size == aSrc.Size
                     && string.Join(",", aDst.Tags) == "紅,綠,藍"
                     && aDst.Scores.Count == 2 && aDst.Scores["乙"] == 8
                     && aDst.Child.Depth == 9 && aDst.Child.Note == "巢狀";

        // 缺 key ⇒ 保留原值（那是「沒設過」，不是 0）
        var aKeep = new ProbeConfig { Count = 77 };
        SCP_JsonMapper.Populate(aKeep, SCP_JsonData.Parse("{\"Name\":\"只給名字\"}"));
        bool aKeptOld = aKeep.Count == 77 && aKeep.Name == "只給名字";

        // 型別不合 ⇒ 不寫入、留一筆（"abc" → 0 比整筆失敗難查十倍）
        var aBad = new ProbeConfig { Count = 5 };
        var aReadOpt = new SCP_JsonMapOptions();
        SCP_JsonMapper.Populate(aBad, SCP_JsonData.Parse("{\"Count\":\"abc\"}"), aReadOpt);
        bool aRefused = aBad.Count == 5 && aReadOpt.Diagnostics.Count > 0;

        bool aOk = aNoted && aSame && aKeptOld && aRefused;
        return new CheckRow("自動序列化",
            $"round-trip 一致={aSame}／不支援有記錄={aNoted}／缺 key 保留原值={aKeptOld}"
            + $"／型別不合不寫入={aRefused}（{aJson.Length} 字元）",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>自動繪製要真的改到物件（不是只畫得出來），而解析不了的輸入不可以靜默寫入或清空。</summary>
    static CheckRow InspectorEdits()
    {
        // ① 純畫一次：每個成員都要出現，不支援的也要出現
        var aObj = new ProbeConfig();
        var aUi = new SCP_Ui();
        SCP_GuiInspector.Draw(aUi, aObj, "cfg");
        string aText = SCP_GuiTextRenderer.Render(aUi.Root, 200);
        bool aDrawn = aText.Contains("Flag", StringComparison.Ordinal)
                      && aText.Contains("Size", StringComparison.Ordinal)
                      && aText.Contains("Child", StringComparison.Ordinal)
                      && aText.Contains("Legacy", StringComparison.Ordinal)      // 不支援也要看得到
                      && !aText.Contains("Secret", StringComparison.Ordinal);    // [SCP_Ignore] 不該出現

        // ② 餵輸入 ⇒ 物件要真的變（欄位、勾選、enum 按鈕、巢狀欄位各一格）
        var aEdit = new ProbeConfig();
        var aInput = new SCP_GuiInput { ClickedId = "cfg/Size=Small" };
        aInput.Fields["cfg/Name"] = "被改過";
        aInput.Fields["cfg/Child/Depth"] = "5";
        aInput.Toggles["cfg/Flag"] = false;
        var aUi2 = new SCP_Ui(aInput);
        var aRes = SCP_GuiInspector.Draw(aUi2, aEdit, "cfg");
        bool aWrote = aRes.Changed && aEdit.Name == "被改過" && !aEdit.Flag
                      && aEdit.Child.Depth == 5 && aEdit.Size == SCP_GuiSize.Small;

        // ③ 打錯字 ⇒ 不寫入、留一筆、現值不變
        var aKeep = new ProbeConfig { Count = 3 };
        var aBadInput = new SCP_GuiInput();
        aBadInput.Fields["cfg/Count"] = "abc";
        var aUi3 = new SCP_Ui(aBadInput);
        var aRes3 = SCP_GuiInspector.Draw(aUi3, aKeep, "cfg");
        bool aRefused = aKeep.Count == 3 && aRes3.Notes.Exists(n => n.Contains("cfg/Count", StringComparison.Ordinal));

        bool aOk = aDrawn && aWrote && aRefused;
        return new CheckRow("自動繪製",
            $"成員都畫出來（含不支援、排除 Ignore）={aDrawn}／輸入寫進物件={aWrote}／打錯字不寫入且留紀錄={aRefused}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>
    /// 下拉選單（複合元件）：收合時不建子節點、搜尋是**關鍵字不是 regex**、分頁邊界、
    /// 以及選了之後有沒有把選擇寫回去。
    /// <para>⚠ 第三項（regex）是刻意跟 UCL 那側不同的一格：UCL 用 <c>new Regex(input)</c>，
    /// 編譯失敗就退回「不篩」—— 於是打一個 <c>(</c> 會讓清單看起來全部符合。
    /// 這裡驗的是「打 <c>(</c> 應該是 0 筆」，因為使用者打的是關鍵字。</para>
    /// </summary>
    static CheckRow DropdownWidget()
    {
        var aOptions = new List<SCP_GuiOption>();
        for (int i = 0; i < 30; i++) aOptions.Add(new SCP_GuiOption("k" + i, "項目 " + i));

        // ① 收合 ⇒ 樹裡只有那一顆鈕（不是「畫了再隱藏」）；而且**沒有任何狀態時預設就是收合**
        //    🩸 Tim 看到的「預設展開」不是這裡的預設值，是我在 CLI 點開的 session 漏進了視窗（見 D18）
        var aShut = new SCP_Ui();
        aShut.Dropdown("頁面", aOptions, "k0", "d");
        bool aShutOk = SCP_GuiQuery.Interactive(aShut.Root).Count == 1;
        bool aDefaultShut = aShutOk
                            && SCP_GuiTextRenderer.Render(aShut.Root, 120).Contains("▼", StringComparison.Ordinal);

        // ② 展開 ⇒ 搜尋框 ＋ 一頁 12 筆 ＋ 只有「下一頁」（第一頁沒有上一頁鈕，而不是有一顆按了沒事的）
        var aOpenUi = DrawDropdown(aOptions, null, null, out _);
        var aOpenEls = SCP_GuiQuery.Interactive(aOpenUi.Root);
        int aRows = CountPrefix(aOpenEls, "d/pick/");
        bool aOpenOk = aRows == SCP_GuiWidgets.DefaultRowsPerPage
                       && HasId(aOpenEls, "d/search") && HasId(aOpenEls, "d/next") && !HasId(aOpenEls, "d/prev");

        // ③ 搜尋：空白分隔的關鍵字要**每一個都命中**（AND）——
        //    "項目 1" ＝ 兩個關鍵字，所以 1／10-19／**21** 共 12 筆（不是 11：「項目 21」兩個字串都含）。
        //    🩸 我第一版把答案寫成 11，紅燈的是斷言不是程式 —— AND 語意本來就會多命中 21。
        //    ／regex 字元不是樣式而是字面（"(" ⇒ 0 筆，UCL 那側會退回「不篩」⇒ 30 筆）
        int aHitsKeyword = SCP_GuiWidgets.Filter(aOptions, "項目 1").Count;
        int aHitsParen = SCP_GuiWidgets.Filter(aOptions, "(").Count;
        bool aSearchOk = aHitsKeyword == 12 && aHitsParen == 0;

        // ④ 展開後的結構：頭與選項在**同一個等寬群組**裡（版位靠這個 —— 清單對齊自己的頭）
        SCP_GuiNode? aGroup = FindUniformGroup(aOpenUi.Root);
        bool aGrouped = aGroup != null
                        && aGroup.Children.Count > 0
                        && aGroup.Children[0].Kind == SCP_GuiNodeKind.Button
                        && aGroup.Children[0].Id == "d";     // 第一個就是那顆頭

        // ⑤ 選一項 ⇒ 回傳新值，而且**寫回請求**裡有值與「收起來」
        var aPickUi = DrawDropdown(aOptions, "d/pick/k7", null, out string aPicked);
        var aWrites = aPickUi.FieldWrites;
        bool aPickOk = aPicked == "k7"
                       && WroteEquals(aWrites, "d/value", "k7")
                       && WroteEquals(aWrites, "d/open", "0");

        // ⑤ 頁碼被搜尋縮短時要夾回去（不夾的話畫面是一片空白，跟「沒有符合的項目」同形）
        DrawDropdown(aOptions, null, "99", out _, iUiOut: out SCP_Ui aClampUi);
        bool aClampOk = WroteEquals(aClampUi.FieldWrites, "d/page", "2");   // 30 筆 / 12 ⇒ 3 頁，夾到 index 2

        bool aOk = aDefaultShut && aShutOk && aOpenOk && aSearchOk && aPickOk && aClampOk && aGrouped;
        return new CheckRow("下拉選單",
            $"預設摺疊={aDefaultShut}／收合時子節點不存在={aShutOk}／展開分頁（{aRows} 列＋下一頁）={aOpenOk}"
            + $"／頭與選項同一個等寬群組={aGrouped}"
            + $"／關鍵字比對（\"項目 1\"={aHitsKeyword}、\"(\"={aHitsParen}）={aSearchOk}"
            + $"／選取寫回={aPickOk}／頁碼夾取={aClampOk}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>畫一次下拉（展開狀態），回傳那一輪的 SCP_Ui 與選中的值。</summary>
    static SCP_Ui DrawDropdown(List<SCP_GuiOption> iOptions, string? iClick, string? iPage, out string oPicked)
        => DrawDropdown(iOptions, iClick, iPage, out oPicked, out _);

    static SCP_Ui DrawDropdown(List<SCP_GuiOption> iOptions, string? iClick, string? iPage,
        out string oPicked, out SCP_Ui iUiOut)
    {
        var aInput = new SCP_GuiInput { ClickedId = iClick };
        aInput.Fields["d/open"] = "1";
        if (iPage != null) aInput.Fields["d/page"] = iPage;
        var aUi = new SCP_Ui(aInput);
        oPicked = aUi.Dropdown("頁面", iOptions, "k0", "d");
        iUiOut = aUi;
        return aUi;
    }

    static bool HasId(List<SCP_GuiElement> iEls, string iId)
    {
        foreach (var e in iEls) if (e.Id == iId) return true;
        return false;
    }

    static int CountPrefix(List<SCP_GuiElement> iEls, string iPrefix)
    {
        int n = 0;
        foreach (var e in iEls) if (e.Id.StartsWith(iPrefix, StringComparison.Ordinal)) n++;
        return n;
    }

    /// <summary>找出樹裡第一個宣告等寬的群組（下拉展開後的那一塊）。</summary>
    static SCP_GuiNode? FindUniformGroup(SCP_GuiNode iNode)
    {
        if (iNode.UniformWidth) return iNode;
        foreach (var c in iNode.Children)
        {
            var aHit = FindUniformGroup(c);
            if (aHit != null) return aHit;
        }
        return null;
    }

    static bool WroteEquals(IReadOnlyList<KeyValuePair<string, string>> iWrites, string iId, string iValue)
    {
        foreach (var kv in iWrites) if (kv.Key == iId && kv.Value == iValue) return true;
        return false;
    }

    /// <summary>
    /// 頁面目錄：opt-in（MenuGroup 為 null 的不列）、分組篩選、認不得的 key 回 null、
    /// 以及**一頁建不出來不可以擋住整個清單**（要記一筆診斷，不是靜默消失）。
    /// </summary>
    static CheckRow PageCatalogShape()
    {
        var aCatalog = new SCP_GuiPageCatalog();
        aCatalog.Register("a", () => new ProbeToolPage("a", "甲頁", "組一"));
        aCatalog.Register("b", () => new ProbeToolPage("b", "乙頁", "組二"));
        aCatalog.Register("c", () => new ProbeToolPage("c", "丙頁", "組一"));
        aCatalog.Register("hidden", () => new ProbeToolPage("hidden", "藏起來的頁", null));
        aCatalog.Register("boom", () => throw new InvalidOperationException("我壞掉了"));

        bool aOptIn = aCatalog.Entries.Count == 3;                       // hidden 與 boom 都不在
        bool aGrouped = aCatalog.Groups.Count == 2
                        && aCatalog.InGroup("組一").Count == 2
                        && aCatalog.InGroup("").Count == 3;              // 空 ＝ 不篩
        bool aBroken = aCatalog.Diagnostics.Count == 1
                       && aCatalog.Diagnostics[0].Contains("boom", StringComparison.Ordinal);
        bool aUnknown = aCatalog.Create("nope") == null && aCatalog.Create("a") != null;
        bool aHiddenStillCreatable = aCatalog.Create("hidden") != null;  // 不列 ≠ 不存在

        bool aDupThrew = false;
        try { aCatalog.Register("a", () => new ProbeToolPage("a", "重複", "組一")); }
        catch (InvalidOperationException) { aDupThrew = true; }

        bool aOk = aOptIn && aGrouped && aBroken && aUnknown && aHiddenStillCreatable && aDupThrew;
        return new CheckRow("頁面目錄",
            $"opt-in（{aCatalog.Entries.Count}/5 列出）={aOptIn}／分組篩選={aGrouped}"
            + $"／壞頁記一筆不擋清單={aBroken}／認不得的 key 回 null={aUnknown}"
            + $"／不列但仍造得出來={aHiddenStillCreatable}／重複登記丟例外={aDupThrew}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>
    /// Row 的排版規則：**連續的 inline 併成一行，遇到群組換行**。
    /// <para>🩸 2026-08-23 Tim 的截圖：ImGui renderer 對每個子節點都 SameLine()，
    /// 於是「一顆鈕 ＋ 一個展開的下拉」把整疊選項畫在那顆鈕上面，**疊成一團**。
    /// 文字 renderer 當時是「全部 inline 才併，否則整列逐項換行」—— 不會疊，但也不會併。
    /// 兩邊都改成同一條規則，而「誰算 inline」只有一份（<c>SCP_GuiNode.IsInline</c>）。</para>
    /// <para>⚠ 這一項只驗得到文字 renderer。ImGui 那側的讀數是截圖，不在這裡。</para>
    /// </summary>
    static CheckRow RowLayout()
    {
        var aUi = new SCP_Ui();
        using (aUi.Row())
        {
            aUi.Button("甲", "r/a");
            aUi.Button("乙", "r/b");
            using (aUi.Box("")) aUi.Button("群組裡面", "r/inner");
            aUi.Button("丙", "r/c");
        }
        string[] aLines = SCP_GuiTextRenderer.Render(aUi.Root, 60)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // 期望：① 甲乙同一行 ② 群組自己的框 ③ 丙**不會**被吸回甲乙那行
        bool aJoined = aLines.Length > 0 && aLines[0].Contains("[ 甲 ]") && aLines[0].Contains("[ 乙 ]");
        bool aBoxBroke = aLines.Any(l => l.Contains("群組裡面"))
                         && !aLines.Any(l => l.Contains("[ 甲 ]") && l.Contains("群組裡面"));
        bool aTailOwnLine = aLines.Any(l => l.Contains("[ 丙 ]") && !l.Contains("[ 甲 ]"));

        // 分類只有一份：群組類一律不是 inline
        bool aKinds = SCP_GuiNode.IsInline(SCP_GuiNodeKind.Button)
                      && SCP_GuiNode.IsInline(SCP_GuiNodeKind.TextField)
                      && !SCP_GuiNode.IsInline(SCP_GuiNodeKind.Box)
                      && !SCP_GuiNode.IsInline(SCP_GuiNodeKind.Table)
                      && !SCP_GuiNode.IsInline(SCP_GuiNodeKind.Row);

        bool aOk = aJoined && aBoxBroke && aTailOwnLine && aKinds;
        return new CheckRow("Row 排版",
            $"連續 inline 併一行={aJoined}／群組會換行（不疊在鈕上）={aBoxBroke}"
            + $"／群組後面的鈕自己一行={aTailOwnLine}／inline 分類只有一份={aKinds}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>
    /// 「原始碼」鈕的路徑來源。**這一項存在是因為它只能量不能推**：
    /// <c>[CallerFilePath]</c> 只有在子類**顯式寫 <c>: base()</c>** 時才會被填。
    /// <para>釘住它的理由：忘了寫的症狀是「精確路徑悄悄變成 null」，
    /// 而畫面上看起來完全一樣（會退回用類別名找）。編譯器哪天改行為也要有人喊。</para>
    /// </summary>
    static CheckRow SourceHint()
    {
        var aImplicit = new ProbeToolPage("a", "甲頁", "組一");     // 隱式 base()
        var aExplicit = new ProbeSourcePage();                       // 顯式 : base()

        bool aImplicitNull = aImplicit.SourceFilePath == null;
        bool aExplicitFilled = aExplicit.SourceFilePath != null
                               && aExplicit.SourceFilePath.EndsWith("SelfTest.cs", StringComparison.Ordinal);
        bool aFallback = aImplicit.SourceFileName == "ProbeToolPage.cs";

        // 宿主端：純檔名找得回來、找不到要說出原因（不是靜默失敗）
        string aFound = SenateShell.Reveal("這個檔一定不存在.cs", AppContext.BaseDirectory);
        bool aLoudMiss = aFound.StartsWith("⚠", StringComparison.Ordinal) && aFound.Contains("找不到");

        bool aOk = aImplicitNull && aExplicitFilled && aFallback && aLoudMiss;
        return new CheckRow("原始碼路徑",
            $"隱式 base() ⇒ null={aImplicitNull}／顯式 : base() ⇒ 填入本檔={aExplicitFilled}"
            + $"／退回類別名={aFallback}（{aImplicit.SourceFileName}）／找不到會出聲={aLoudMiss}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>
    /// 「這一頁是哪個 class」這個資訊在**每一種宿主能力組合**下都要到得了使用者手上。
    /// <para>三種狀態 ＋ 一種失敗：① 能開檔案總管 ② 只能複製 ③ 兩種都沒有 ④ 能開但這次開不起來。
    /// ⚠ 用 stub 換掉 <c>SCP_GuiHost</c> 的兩個委派（跑完還原）——
    /// **不碰真的剪貼簿**：selftest 把使用者的剪貼簿蓋掉是一個誰都不會預期的副作用。
    /// ⇒ 代價要說：真的 <c>clip.exe</c> 寫得進去這件事，這一項**沒有**驗到。</para>
    /// </summary>
    /// <summary>
    /// 「原始碼」訊息的**生命週期**：成功不留字、失敗留字且關得掉。
    /// <para>🩸 這一格存在的理由是一隻活過驗收的 bug（2026-09-01）：宿主改成成功回空字串，
    /// 而顯示條件寫的是 <c>!= null</c> ⇒ 空字串照樣過關，畫面多一條**內容為空的 Note**。
    /// 「沒有話要說」與「有一句空話」在型別上不同形，在畫面上卻同形 —— 沒有讀數就抓不到。</para>
    /// <para>⚠ 用**同一個 page 實例**連續繪製：訊息是頁面的狀態，每次 new 一個新頁就永遠測不到
    /// 「按了關閉之後它真的不見了」。</para>
    /// </summary>
    static CheckRow SourceMessageLifecycle()
    {
        var aSavedReveal = SCP_GuiHost.RevealInFileManager;
        var aSavedCopy = SCP_GuiHost.CopyToClipboard;
        try
        {
            // ⓐ 成功（宿主回空字串＝不說話）⇒ 訊息區整塊不該存在
            SCP_GuiHost.RevealInFileManager = _ => string.Empty;
            SCP_GuiHost.CopyToClipboard = _ => "✓ 假裝複製了";
            var aPageA = new ProbeSourcePage();
            Ids(DrawWith(aPageA, SCP_GuiToolPage.SourceButtonId));      // 按下去
            var aAfterA = Ids(DrawWith(aPageA, null));                  // 下一幀
            bool aOkA = !aAfterA.Contains(SCP_GuiToolPage.DismissMessageButtonId);

            // ⓑ 失敗 ⇒ 訊息在，而且關得掉（關閉鈕要畫得出來）
            SCP_GuiHost.RevealInFileManager = _ => "⚠ 假裝開不起來";
            var aPageB = new ProbeSourcePage();
            Ids(DrawWith(aPageB, SCP_GuiToolPage.SourceButtonId));
            var aUiB = DrawWith(aPageB, null);
            bool aOkB = Ids(aUiB).Contains(SCP_GuiToolPage.DismissMessageButtonId)
                        && SCP_GuiTextRenderer.Render(aUiB.Root, 200)
                            .Contains("假裝開不起來", StringComparison.Ordinal);

            // ⓒ 按下關閉 ⇒ 再畫一次就不見了（這是「真的關掉」與「只是這一幀沒畫」的分界）
            DrawWith(aPageB, SCP_GuiToolPage.DismissMessageButtonId);
            var aUiC = DrawWith(aPageB, null);
            bool aOkC = !Ids(aUiC).Contains(SCP_GuiToolPage.DismissMessageButtonId)
                        && !SCP_GuiTextRenderer.Render(aUiC.Root, 200)
                            .Contains("假裝開不起來", StringComparison.Ordinal);

            bool aOk = aOkA && aOkB && aOkC;
            return new CheckRow("原始碼訊息生命週期",
                $"成功⇒完全不留字（含空行）={aOkA}／失敗⇒留字且有關閉鈕={aOkB}"
                + $"／按關閉⇒下一幀真的不見={aOkC}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally
        {
            SCP_GuiHost.RevealInFileManager = aSavedReveal;
            SCP_GuiHost.CopyToClipboard = aSavedCopy;
        }
    }

    /// <summary>拿**既有的**頁面實例畫一幀（狀態要跨幀存活時用它，不要每次 new）。</summary>
    static SCP_Ui DrawWith(SCP_GuiPage iPage, string? iClickId)
    {
        var aUi = new SCP_Ui(new SCP_GuiInput { ClickedId = iClickId });
        iPage.Draw(aUi);
        return aUi;
    }

    static CheckRow SourceCapabilityFallback()
    {
        var aSavedReveal = SCP_GuiHost.RevealInFileManager;
        var aSavedCopy = SCP_GuiHost.CopyToClipboard;
        try
        {
            // ① 能開檔案總管 ⇒ 只有「原始碼」那顆
            SCP_GuiHost.RevealInFileManager = _ => "✓ 假裝開了";
            SCP_GuiHost.CopyToClipboard = _ => "✓ 假裝複製了";
            var aA = Ids(DrawProbe(null));
            bool aOkA = aA.Contains(SCP_GuiToolPage.SourceButtonId)
                        && !aA.Contains(SCP_GuiToolPage.CopyClassButtonId);

            // ② 開不了 ⇒ 換成「複製類別名」
            SCP_GuiHost.RevealInFileManager = null;
            var aB = Ids(DrawProbe(null));
            bool aOkB = aB.Contains(SCP_GuiToolPage.CopyClassButtonId)
                        && !aB.Contains(SCP_GuiToolPage.SourceButtonId);

            // ③ 兩種都沒有 ⇒ 兩顆鈕都不畫，但類別名要印在 page key 那行
            SCP_GuiHost.CopyToClipboard = null;
            var aCUi = DrawProbe(null);
            var aC = Ids(aCUi);
            string aCText = SCP_GuiTextRenderer.Render(aCUi.Root, 120);
            bool aOkC = !aC.Contains(SCP_GuiToolPage.SourceButtonId)
                        && !aC.Contains(SCP_GuiToolPage.CopyClassButtonId)
                        && aCText.Contains("ProbeSourcePage", StringComparison.Ordinal);

            // ④ 裝了但這次失敗 ⇒ 自動退到複製，而且訊息裡看得到類別名
            SCP_GuiHost.RevealInFileManager = _ => "⚠ 假裝開不起來";
            SCP_GuiHost.CopyToClipboard = _ => "✓ 假裝複製了";
            string aDText = SCP_GuiTextRenderer.Render(DrawProbe(SCP_GuiToolPage.SourceButtonId).Root, 200);
            bool aOkD = aDText.Contains("已改為複製類別名", StringComparison.Ordinal)
                        && aDText.Contains("ProbeSourcePage", StringComparison.Ordinal);

            bool aOk = aOkA && aOkB && aOkC && aOkD;
            return new CheckRow("原始碼／類別名退路",
                $"能開檔案總管⇒只有原始碼鈕={aOkA}／開不了⇒換成複製鈕={aOkB}"
                + $"／兩種都沒有⇒類別名印在 page key 那行={aOkC}／開不起來⇒自動退到複製={aOkD}"
                + "（用 stub，沒有碰真的剪貼簿 ⇒ clip.exe 本身未驗）",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally
        {
            SCP_GuiHost.RevealInFileManager = aSavedReveal;
            SCP_GuiHost.CopyToClipboard = aSavedCopy;
        }
    }

    static SCP_Ui DrawProbe(string? iClickId)
    {
        var aUi = new SCP_Ui(new SCP_GuiInput { ClickedId = iClickId });
        new ProbeSourcePage().Draw(aUi);
        return aUi;
    }

    static List<string> Ids(SCP_Ui iUi)
    {
        var aIds = new List<string>();
        foreach (var e in SCP_GuiQuery.Interactive(iUi.Root)) aIds.Add(e.Id);
        return aIds;
    }

    /// <summary>對拍用：**顯式** <c>: base()</c> 的假頁（用來量 CallerFilePath 有沒有被填）。</summary>
    [SCP_PageIgnore("自我對拍用的探針頁（驗 [CallerFilePath] 退路），不是給人開的頁")]
    sealed class ProbeSourcePage : SCP_GuiToolPage
    {
        public ProbeSourcePage() : base() { }
        public override string Key => "probe-source";
        protected override void DrawContent(SCP_Ui iUi) { }
    }

    /// <summary>對拍用的假工具頁。</summary>
    [SCP_PageIgnore("自我對拍用的探針頁，不是給人開的頁")]
    sealed class ProbeToolPage : SCP_GuiToolPage
    {
        readonly string m_Key;
        readonly string m_Title;
        readonly string? m_Group;
        public ProbeToolPage(string iKey, string iTitle, string? iGroup)
        {
            m_Key = iKey; m_Title = iTitle; m_Group = iGroup;
        }
        public override string Key => m_Key;
        public override string Title => m_Title;
        public override string? MenuGroup => m_Group;
        protected override void DrawContent(SCP_Ui iUi) => iUi.Label($"我是 {m_Key}");
    }

    /// <summary>
    /// 對拍用：與 <see cref="ProbeDupB"/> **共用同一個 `PageKey`** —— 「key 撞名」那格的受測體。
    /// <para>🩸 為什麼要合成：撞名**在產品型別上造不出來**（造得出來就代表產品已經壞了）
    /// ⇒ 沒有受測體的話那個不變量只有註解沒有讀數。</para>
    /// </summary>
    [SCP_PageIgnore("自我對拍用的探針頁（key 撞名受測體 A），不是給人開的頁")]
    sealed class ProbeDupA : SCP_GuiToolPage
    {
        public const string PageKey = "probe-dup";
        public ProbeDupA() : base() { }
        public override string Key => PageKey;
        public override string? MenuGroup => "探針";
        protected override void DrawContent(SCP_Ui iUi) { }
    }

    /// <summary>對拍用：與 <see cref="ProbeDupA"/> 撞 key 的另一半。</summary>
    [SCP_PageIgnore("自我對拍用的探針頁（key 撞名受測體 B），不是給人開的頁")]
    sealed class ProbeDupB : SCP_GuiToolPage
    {
        public const string PageKey = "probe-dup";
        public ProbeDupB() : base() { }
        public override string Key => PageKey;
        public override string? MenuGroup => "探針";
        protected override void DrawContent(SCP_Ui iUi) { }
    }

    /// <summary>對拍用：ctor 形狀不符（吃一個**不是** context 的東西）—— 它必須被點名，⛔ 不是靜默跳過。</summary>
    [SCP_PageIgnore("自我對拍用的探針頁（ctor 形狀不符受測體），不是給人開的頁")]
    sealed class ProbeBadCtorPage : SCP_GuiToolPage
    {
        public const string PageKey = "probe-badctor";
        public ProbeBadCtorPage(int iNotAContext) : base() { _ = iNotAContext; }
        public override string Key => PageKey;
        protected override void DrawContent(SCP_Ui iUi) { }
    }

    /// <summary>
    /// 對拍用：`PageKey` **逐字等於型別名** —— 下拉標籤那條規則的第二臂。
    /// <para>⚠ 2026-09-22 實測：產品 13 支頁的 key 與型別名 **13/13 全不相等**
    /// ⇒ 那一臂**在產品上零樣本**，而零樣本與失效在計數上同形。⇒ 只能靠這一支。</para>
    /// </summary>
    [SCP_PageIgnore("自我對拍用的探針頁（Key == TypeName 的標籤受測體），不是給人開的頁")]
    sealed class ProbeSameNamePage : SCP_GuiToolPage
    {
        public const string PageKey = nameof(ProbeSameNamePage);
        public ProbeSameNamePage() : base() { }
        public override string Key => PageKey;
        public override string? MenuGroup => "探針";
        protected override void DrawContent(SCP_Ui iUi) { }
    }

    /// <summary>對拍用的假頁 —— 把生命週期呼叫記成可比對的字串。</summary>
    [SCP_PageIgnore("自我對拍用的探針頁，不是給人開的頁")]
    sealed class ProbePage : SCP_GuiPage
    {
        readonly string m_Key;
        readonly List<string> m_Log;
        public ProbePage(string iKey, List<string> iLog) { m_Key = iKey; m_Log = iLog; }
        public override string Key => m_Key;
        public override void Draw(SCP_Ui iUi) => iUi.Label($"我是 {m_Key}");
        public override void OnPush() => m_Log.Add($"{m_Key}:push");
        public override void OnPause() => m_Log.Add($"{m_Key}:pause");
        public override void OnResume() => m_Log.Add($"{m_Key}:resume");
        public override void OnClose() { m_Log.Add($"{m_Key}:close"); base.OnClose(); }
    }

    /// <summary>
    /// 設定檔 round-trip **不可以吃掉本版不認得的欄位**（含使用者手寫的 <c>"//"</c> 註解鍵）。
    /// <para>🩸 這一項是為一隻真的 bug 立的：2026-08-23 介面尺寸寫回設定檔的第一版，
    /// 把 <c>"//"</c> 那行整條吃掉 —— projects 還在，所以看起來一切正常。</para>
    /// </summary>
    static CheckRow ConfigRoundTripKeepsUnknownKeys()
    {
        string aPath = Path.Combine(Path.GetTempPath(), "senate_selftest_config.json");
        const string aSrc = """
            {
              "//": "手寫註解，不可以被吃掉",
              "schemaVersion": 1,
              "projects": [ { "name": "X", "root": "D:/X", "//p": "專案層註解" } ],
              "未來版本的欄位": 42
            }
            """;
        try
        {
            File.WriteAllText(aPath, aSrc);
            SenateConfig? aCfg = SenateConfig.Load(aPath);
            if (aCfg == null) return new CheckRow("設定檔 round-trip", "讀不到剛寫出的暫存檔", CheckResult.Fail);

            aCfg.Ui.Scale = 1.75f;          // 模擬「使用者改了尺寸」那條寫入路徑
            aCfg.Save(aPath);
            string aBack = File.ReadAllText(aPath);

            bool aRootNote = aBack.Contains("手寫註解", StringComparison.Ordinal);
            bool aProjNote = aBack.Contains("專案層註解", StringComparison.Ordinal);
            bool aFuture = aBack.Contains("未來版本的欄位", StringComparison.Ordinal);
            bool aUi = SenateConfig.Load(aPath)?.Ui.Scale == 1.75f;

            return new CheckRow("設定檔 round-trip",
                $"根層註解保留={aRootNote}／專案層註解保留={aProjNote}／未知欄位保留={aFuture}／ui.scale 回讀={aUi}",
                aRootNote && aProjNote && aFuture && aUi ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("設定檔 round-trip", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { File.Delete(aPath); } catch { /* 暫存檔刪不掉不影響判定 */ } }
    }

    // 區塊職責：prefs 的**三態**要真的分得開 —— 這是它跟 PlayerPrefs 的全部差別。
    // 物理意義：PlayerPrefs 的病是「key 打錯」與「沒設定過」同形，兩者都靜靜回預設值。
    //          本層要求：沒設定 ⇒ Missing、型別不符 ⇒ ReadError、讀到 ⇒ Present，
    //          而 Get() 用預設值是**顯式選擇**（呼叫端寫出來的），不是預設行為。
    // 數值影響：純暫存檔讀寫，跑完刪掉。
    static CheckRow PrefsThreeStates()
    {
        string aPath = Path.Combine(Path.GetTempPath(), "senate_selftest_prefs_states.json");
        var aKey = SCP_PrefKey.String("awakening", "lettersRoot", "(預設)");
        var aNum = SCP_PrefKey.Long("awakening", "lettersRoot", 0);   // 同名不同型 ⇒ 要 ReadError
        try
        {
            try { File.Delete(aPath); } catch { /* 本來就不存在是正常的 */ }
            var aPrefs = new SCP_JsonPrefs(aPath);

            // ① 檔還不存在 ⇒ Missing（不是空字串、不是錯誤）
            var aBefore = aPrefs.Read(aKey);
            bool aMissingOk = aBefore.State == SCP_PrefState.Missing;
            bool aDefaultExplicit = aPrefs.Get(aKey) == "(預設)";

            // ② 寫進去 ⇒ Present，而且值就是寫進去的那個
            var (aWroteOk, aWroteMsg) = aPrefs.Write(aKey, "D:/Unity/Bar/AgentCommands/ChatTavern/baton/letters");
            var aAfter = aPrefs.Read(aKey);
            bool aPresentOk = aAfter.State == SCP_PrefState.Present
                              && aAfter.Value.EndsWith("baton/letters", StringComparison.Ordinal);

            // ③ 用錯型別讀 ⇒ ReadError（**不是** Missing —— 那會讓人以為補上就好，實際會被舊值蓋掉）
            var aWrongType = aPrefs.Read(aNum);
            bool aErrorOk = aWrongType.State == SCP_PrefState.ReadError
                            && aWrongType.Error != null
                            && aWrongType.Error.Contains("awakening.lettersRoot", StringComparison.Ordinal);

            bool aOk = aMissingOk && aDefaultExplicit && aPresentOk && aWroteOk && aErrorOk;
            return new CheckRow("prefs 三態",
                $"未設定=Missing:{aMissingOk}／顯式 Get 用預設:{aDefaultExplicit}／寫後=Present:{aPresentOk}"
                + $"／型別不符=ReadError 且訊息帶 key:{aErrorOk}／寫入回報:{(aWroteOk ? "ok" : aWroteMsg)}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("prefs 三態", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { File.Delete(aPath); } catch { /* 暫存檔刪不掉不影響判定 */ } }
    }

    // 區塊職責：寫一個 section **不可以動到別人的 section**，未知欄位也要活著。
    // 物理意義：🩸 直接 WriteAllText 覆蓋會把別人的區塊一起帶走，而檔案仍然是合法 JSON、
    //          仍然讀得起來 —— 沒有任何一層會報錯。這一格就是那個失敗的探針。
    static CheckRow PrefsKeepsOtherSections()
    {
        string aPath = Path.Combine(Path.GetTempPath(), "senate_selftest_prefs_sections.json");
        const string aSrc = """
            {
              "//": "手寫註解，不可以被吃掉",
              "别页": { "keep": "別頁存的東西", "未來欄位": 42 },
              "awakening": { "lettersRoot": "舊值" }
            }
            """;
        try
        {
            File.WriteAllText(aPath, aSrc);
            var aPrefs = new SCP_JsonPrefs(aPath);
            var (aOkWrite, aMsg) = aPrefs.Write(SCP_PrefKey.String("awakening", "lettersRoot"), "新值");
            string aBack = File.ReadAllText(aPath);

            bool aNote = aBack.Contains("手寫註解", StringComparison.Ordinal);
            bool aOther = aBack.Contains("別頁存的東西", StringComparison.Ordinal);
            bool aFuture = aBack.Contains("未來欄位", StringComparison.Ordinal);
            bool aNew = aPrefs.Read(SCP_PrefKey.String("awakening", "lettersRoot")).Value == "新值";

            bool aOk = aOkWrite && aNote && aOther && aFuture && aNew;
            return new CheckRow("prefs 只動自己那格",
                $"根層註解保留={aNote}／別的 section 保留={aOther}／未知欄位保留={aFuture}／新值回讀={aNew}"
                + (aOkWrite ? "" : $"／寫入失敗：{aMsg}"),
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("prefs 只動自己那格", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { File.Delete(aPath); } catch { /* 暫存檔刪不掉不影響判定 */ } }
    }

    // 區塊職責：路徑解析**只有一個落點** —— 這一格就是「兩處各算一次」的探針。
    // 物理意義：目錄名散在多處拼字時，改一處漏一處**不會報錯**：
    //          `senate cmd status` 會去掃一個空目錄然後印「沒有東西卡住」，
    //          而那跟真的沒卡住一模一樣。⇒ 這裡逐條比對「舊呼叫端」與「新解析器」給的答案。
    // 數值影響：純字串，零 IO（pointer 那格用暫存目錄，跑完刪掉）。
    static CheckRow PathsSingleSource()
    {
        const string aData = "D:/Unity/Bar/AgentCommands";
        var aRoot = new SCP_DataRoot(aData);

        // ① 舊入口（AgentCmdClient）與新解析器必須逐字同意 —— 不同意就是有人還在自己算
        bool aQueueAgrees = AgentCmdClient.QueueFolder(aData, "basecamp") == SCP_DataPaths.QueueFolder(aRoot, "basecamp")
                            && AgentCmdClient.QueuePath(aData, "basecamp") == SCP_DataPaths.QueueFile(aRoot, "basecamp")
                            && AgentCmdClient.TriggerPath(aData, "basecamp") == SCP_DataPaths.TriggerFile(aRoot, "basecamp");

        // ② status 分支掃的目錄，必須是 QueueFolder 的父層（漏改的那格就是在這裡分岔）
        bool aQueuesDirAgrees = SCP_DataPaths.QueueFolder(aRoot, "basecamp")
                                    .StartsWith(SCP_DataPaths.Queues(aRoot) + "/", StringComparison.Ordinal);

        // ③ 路徑穿越：persona 來自 CLI，`..` 一定要被擋回 anonymous
        bool aTraversalBlocked = SCP_DataPaths.SafeQueueId("../../etc") == SCP_DataPaths.AnonymousQueueId
                                 && SCP_DataPaths.SafeQueueId("  ") == SCP_DataPaths.AnonymousQueueId
                                 && SCP_DataPaths.SafeQueueId("basecamp") == "basecamp";

        // ④ 根正規化：反斜線與尾斜線不可以生出第二種寫法
        bool aNormalised = new SCP_DataRoot(@"D:\Unity\Bar\AgentCommands\").Value == aData
                           && new SCP_DataRoot("D:/Unity/Bar/AgentCommands/").Value == aData;

        // ⑤ 舊的 letters 入口與新解析器同意（SCP_WakeLetters 已退化成外殼）
        var aLetters = SCP_DataPaths.Letters(aRoot);
        bool aLettersAgrees = SCP_WakeLetters.ConstitutionPath(aLetters.Value, "basecamp")
                              == SCP_LettersPaths.ConstitutionPath(aLetters, "basecamp");

        // ⑥ 資料根三種來源不得同形（Configured / Pointer / Convention）
        string aTmpProj = Path.Combine(Path.GetTempPath(), "senate_selftest_proj");
        bool aOriginOk;
        try
        {
            Directory.CreateDirectory(aTmpProj);
            var aProj = new SCP_ProjectRoot(aTmpProj);
            var aConv = SCP_ProjectPaths.ResolveDataRoot(aProj, "auto");
            File.WriteAllText(SCP_ProjectPaths.DataRootPointer(aProj),
                "# 註解行（pointer 檔允許註解與空行，解析要跳過）\n\nD:/別的地方/AgentCommands\n");
            var aPtr = SCP_ProjectPaths.ResolveDataRoot(aProj, "auto");
            var aCfg = SCP_ProjectPaths.ResolveDataRoot(aProj, "D:/顯式指定");
            aOriginOk = aConv.Origin == SCP_ProjectPaths.DataRootOrigin.Convention
                        && aPtr.Origin == SCP_ProjectPaths.DataRootOrigin.Pointer
                        && aPtr.Root.Value == "D:/別的地方/AgentCommands"
                        && aCfg.Origin == SCP_ProjectPaths.DataRootOrigin.Configured;
        }
        catch (Exception e) { return new CheckRow("路徑單一落點", $"pointer 那格例外：{e.Message}", CheckResult.Fail); }
        finally { try { Directory.Delete(aTmpProj, true); } catch { /* 暫存目錄刪不掉不影響判定 */ } }

        bool aOk = aQueueAgrees && aQueuesDirAgrees && aTraversalBlocked && aNormalised && aLettersAgrees && aOriginOk;
        return new CheckRow("路徑單一落點",
            $"queue 舊新一致={aQueueAgrees}／status 掃的是父層={aQueuesDirAgrees}／穿越擋回 anonymous={aTraversalBlocked}"
            + $"／根正規化={aNormalised}／letters 舊新一致={aLettersAgrees}／資料根三來源可分={aOriginOk}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    // 區塊職責：自動收頁的五個不變量 —— 而**每一個都要有一次「它會叫」的讀數**。
    // 物理意義: TASK-0276（Tim 2026-09-22 拍板 C）。此前這一格叫 `PageDiscovery`，
    //          驗的是「程式碼裡有、目錄裡沒有」的差集。改成自動收頁之後**那個差集不存在了**
    //          （目錄就是程式碼）⇒ 那支會永遠回 0 筆。
    //          🩸 而 0 筆現在的意思是「對得上」⇒ **一個永遠綠的燈，而它看起來像有人在看**。
    //          ⇒ 整支重新指向新的不變量，⛔ 不是把舊的留著打折。
    // 🔴 三格的受測體是**合成的**（撞名／ctor 形狀／key＝型別名）——
    //   那三件事在產品型別上造不出來，而「造不出來」與「驗過了」在計數上同形。
    // 數值影響: 純反射 ＋ 幾次便宜的 ctor，零 IO。
    static CheckRow PageAutoRegister()
    {
        var aModel = new SenateModel(SenateRepoRoot());

        // ① 產品路徑：真的 BuildCatalog ⇒ 零缺陷，而且三種 ctor 形狀都收得到
        SCP_GuiPageCatalog aReal = SenatePages.BuildCatalog(aModel);
        List<string> aKeys = aReal.AllKeys;
        int aDefectCount = aReal.Diagnostics.Count;
        bool aQuiet = aDefectCount == 0;
        bool aHasThem = aKeys.Contains(SCP_GuiHomePage.PageKey)              // 顯式（吃 catalog）
                        && aKeys.Contains(SCP_GuiProcessAdminPage.PageKey)   // 無參 ctor
                        && aKeys.Contains(BankAdminPage.PageKey)             // 吃 SenateModel
                        && aKeys.Contains(SCP_GuiStylePage.PageKey);         // 吃 ISCP_GuiAppContext

        // ② [SCP_PageIgnore] 擋的是**收錄**（⇒ 連建構都不會發生），⛔ 不只是不列清單
        bool aIgnoreWorks = !aKeys.Contains("probe-source") && !aKeys.Contains(ProbeDupA.PageKey)
                            && !aKeys.Contains(ProbeBadCtorPage.PageKey);

        // ③ 🔴 key 撞名要**點名兩邊**（身分是 TypeFullName ⇒ 說得出是誰跟誰）
        List<string> aDupDefects = new SCP_GuiPageCatalog().AutoRegisterTypes(
            new Type?[] { typeof(ProbeDupA), typeof(ProbeDupB) }, aModel, iIncludeIgnored: true);
        bool aNamesBoth = false;
        foreach (string d in aDupDefects)
            if (d.Contains(nameof(ProbeDupA), StringComparison.Ordinal)
                && d.Contains(nameof(ProbeDupB), StringComparison.Ordinal)) { aNamesBoth = true; break; }
        // 反向對照：只餵一個 ⇒ **零缺陷**。⛔ 少了這一格，「它會叫」可能只是它對什麼都叫
        bool aQuietOnOne = new SCP_GuiPageCatalog().AutoRegisterTypes(
            new Type?[] { typeof(ProbeDupA) }, aModel, iIncludeIgnored: true).Count == 0;

        // ④ ctor 形狀不符要被點名，⛔ 不是靜默跳過（UCL 那版是 LogWarning + continue）
        List<string> aBad = new SCP_GuiPageCatalog().AutoRegisterTypes(
            new Type?[] { typeof(ProbeBadCtorPage) }, aModel, iIncludeIgnored: true);
        bool aNamesBadCtor = false;
        foreach (string d in aBad)
            if (d.Contains(nameof(ProbeBadCtorPage), StringComparison.Ordinal)
                && d.Contains("建構子形狀不符", StringComparison.Ordinal)) { aNamesBadCtor = true; break; }

        // ⑤ 標籤 ＝ `Key(TypeName)`，而 `Key == TypeName` 時只印型別名
        var aLabelCat = new SCP_GuiPageCatalog();
        aLabelCat.AutoRegisterTypes(new Type?[] { typeof(ProbeSameNamePage), typeof(ProbeDupA) },
                                    aModel, iIncludeIgnored: true);
        string aSame = "(沒找到)", aDiff = "(沒找到)";
        foreach (SCP_GuiPageEntry e in aLabelCat.Entries)
        {
            if (e.TypeName == nameof(ProbeSameNamePage)) aSame = e.Label;
            if (e.TypeName == nameof(ProbeDupA)) aDiff = e.Label;
        }
        bool aLabelOk = aSame == nameof(ProbeSameNamePage)
                        && aDiff == ProbeDupA.PageKey + "(" + nameof(ProbeDupA) + ")";

        bool aOk = aQuiet && aHasThem && aIgnoreWorks && aNamesBoth && aQuietOnOne
                   && aNamesBadCtor && aLabelOk;
        return new CheckRow("自動收頁（反射）",
            $"產品目錄零缺陷={aQuiet}（{aDefectCount} 筆／收了 {aKeys.Count} 支，三種 ctor 形狀都在={aHasThem}）"
            + $"／[SCP_PageIgnore] 連收錄都擋={aIgnoreWorks}"
            + $"／🔴 撞 key 點名兩邊={aNamesBoth}（🔴 反向對照：只餵一個 ⇒ 零缺陷={aQuietOnOne}）"
            + $"／ctor 形狀不符會點名={aNamesBadCtor}"
            + $"／標籤={aLabelOk}（相等⇒'{aSame}'／不等⇒'{aDiff}'）",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>Senate repo 根 —— selftest 自己要建 model 時用（走執行檔位置往上找 senate.slnx）。</summary>
    static string SenateRepoRoot()
    {
        var aDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (DirectoryInfo? d = aDir; d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Senate.slnx"))) return d.FullName;
        return AppContext.BaseDirectory;   // 找不到就用執行目錄（Discover 不吃這個值，只有 model 建構要）
    }

    // 區塊職責：入口檔受管區塊的**七種狀態**要真的分得開，而且使用者的字一個都不能掉。
    // 物理意義: 這一層動的是**使用者的檔**（CLAUDE.md），源端沒有副本 —— 寫壞只能靠 git，
    //          而消費端不一定有 git。⇒ 每一條規則都要有自己的讀數。
    static CheckRow EntryDocBlock()
    {
        const string aBody1 = "## SCP_Core 共用規則\n\n請先讀 <SCP_Core>/Docs~/Coding_Standards.md。";
        const string aBody2 = "## SCP_Core 共用規則\n\n（第二版：多了一行）";

        // ① 檔不存在 ⇒ 新建，檔頭就是 BEGIN（使用者區為空）
        string aFresh = SCP_EntryDoc.Apply(null, aBody1, "claude", "ClaudeTemplate/CLAUDE.md");
        bool aHeadIsBegin = aFresh.StartsWith(SCP_EntryDoc.BeginToken, StringComparison.Ordinal);

        // ② 既有使用者內容 ⇒ append 在後面，前面一個字都不動
        const string aUser = "# 我自己的規則\n\nAAAA";
        string aAppended = SCP_EntryDoc.Apply(aUser, aBody1, "claude", "t");
        bool aUserKept = aAppended.StartsWith("# 我自己的規則\n\nAAAA\n\n", StringComparison.Ordinal);
        bool aSynced = SCP_EntryDoc.Parse(aAppended, aBody1).State == SCP_EntryState.Synced;

        // ③ END 之後使用者又補了東西 ⇒ 更新受管區塊，**那段要活著**（Tim 拍板：保留）
        string aWithTail = aAppended.TrimEnd('\n') + "\n\n## 我後來加的\n\nZZZZ\n";
        string aUpdated = SCP_EntryDoc.Apply(aWithTail, aBody2, "claude", "t");
        bool aTailKept = aUpdated.Contains("ZZZZ", StringComparison.Ordinal)
                         && aUpdated.Contains("AAAA", StringComparison.Ordinal)
                         && !aUpdated.Contains("第二版", StringComparison.Ordinal) == false;
        bool aOnlyOneBlock = CountSub(aUpdated, SCP_EntryDoc.BeginToken) == 1;

        // ④ 冪等：同樣的內容套兩次要逐字相同（不然每次同步都產生假 diff）
        string aTwice = SCP_EntryDoc.Apply(aUpdated, aBody2, "claude", "t");
        bool aIdempotent = aTwice == aUpdated;

        // ⑤ 移除：受管區塊切掉，前後的使用者內容都在
        string aRemoved = SCP_EntryDoc.Remove(aUpdated);
        bool aRemoveOk = !aRemoved.Contains(SCP_EntryDoc.BeginToken, StringComparison.Ordinal)
                         && aRemoved.Contains("AAAA", StringComparison.Ordinal)
                         && aRemoved.Contains("ZZZZ", StringComparison.Ordinal);

        // ⑥ CRLF 進來也要判得出 Synced（autocrlf 不可以造成幻影 Stale）
        bool aCrlfOk = SCP_EntryDoc.Parse(aAppended.Replace("\n", "\n"), aBody1).State == SCP_EntryState.Synced;

        bool aOk = aHeadIsBegin && aUserKept && aSynced && aTailKept && aOnlyOneBlock
                   && aIdempotent && aRemoveOk && aCrlfOk;
        return new CheckRow("入口檔區塊",
            $"新檔檔頭是 BEGIN={aHeadIsBegin}／既有內容不動={aUserKept}／判 Synced={aSynced}"
            + $"／END 後的字活著={aTailKept}／只有一個區塊={aOnlyOneBlock}／套兩次逐字相同={aIdempotent}"
            + $"／移除後前後都在={aRemoveOk}／CRLF 不造成幻影 Stale={aCrlfOk}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    // 區塊職責：**壞掉的形狀要停手**，而且七態不得同形。
    // 物理意義: 「挑第一個修」「猜他想放哪」這兩種好心，會留下另一個還在生效的區塊
    //          而畫面顯示綠燈 —— 那是這一層最貴的錯法。
    static CheckRow EntryDocDefects()
    {
        const string aBody = "## 受管內容";
        string aGood = SCP_EntryDoc.Apply(null, aBody, "claude", "t");

        // ① 兩個區塊 ⇒ Duplicated（停手）
        bool aDup = SCP_EntryDoc.Parse(aGood + "\n" + aGood, aBody).State == SCP_EntryState.Duplicated;

        // ② 只有 BEGIN 沒有 END ⇒ MarkerBroken
        string aNoEnd = aGood.Replace(SCP_EntryDoc.EndToken, "");
        bool aBroken = SCP_EntryDoc.Parse(aNoEnd, aBody).State == SCP_EntryState.MarkerBroken;

        // ③ 區塊被手改 ⇒ LocalEdit（**不是** Stale：Stale 覆寫安全，這個會吃掉人寫的字）
        string aEdited = aGood.Replace("## 受管內容", "## 受管內容（我加了一句）");
        SCP_EntryParse aE = SCP_EntryDoc.Parse(aEdited, aBody);
        bool aLocalEdit = aE.State == SCP_EntryState.LocalEdit && aE.Detail.Contains("sha", StringComparison.Ordinal);

        // ④ 來源更新 ⇒ Stale（可安全覆寫）
        bool aStale = SCP_EntryDoc.Parse(aGood, aBody + "\n\n多一行").State == SCP_EntryState.Stale;

        // ⑤ 🩸 遷移：整份就是舊版整檔安裝 ⇒ NeedsMigration，而 Apply **不會**變成兩份
        //    （本 repo 的 CLAUDE.md 實測就是這個形狀 —— 2026-08-30 與 template 逐字相同）
        string aLegacy = "# 專案規則\n\n這是舊版整檔安裝的內容。";
        bool aMigrate = SCP_EntryDoc.Parse(aLegacy, aLegacy).State == SCP_EntryState.NeedsMigration;
        string aMigrated = SCP_EntryDoc.Apply(aLegacy, aLegacy, "claude", "t");
        bool aNoDouble = CountSub(aMigrated, "這是舊版整檔安裝的內容") == 1;

        // ⑥ 有內容但不是舊版安裝 ⇒ NotInstalled（會 append，不會遷移）
        bool aPlain = SCP_EntryDoc.Parse("# 使用者自己寫的\n", aBody).State == SCP_EntryState.NotInstalled;

        bool aOk = aDup && aBroken && aLocalEdit && aStale && aMigrate && aNoDouble && aPlain;
        return new CheckRow("入口檔異常形狀",
            $"兩個區塊=Duplicated:{aDup}／缺 END=MarkerBroken:{aBroken}／手改=LocalEdit:{aLocalEdit}"
            + $"／來源更新=Stale:{aStale}／舊版整檔=NeedsMigration:{aMigrate}（遷移後不重複:{aNoDouble}）"
            + $"／一般檔=NotInstalled:{aPlain}",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    static int CountSub(string iText, string iSub)
    {
        int n = 0, i = 0;
        while ((i = iText.IndexOf(iSub, i, StringComparison.Ordinal)) >= 0) { n++; i += iSub.Length; }
        return n;
    }

    // 區塊職責：真的寫一次檔 —— 純函式對了不代表落地對（三本帳的第三本）。
    // 物理意義: 這是唯一會動使用者手寫檔的地方，所以要驗的是「不敢寫的時候真的沒寫」。
    static CheckRow EntryDocInstallIo()
    {
        string aDir = Path.Combine(Path.GetTempPath(), "senate_selftest_entry");
        string aPath = Path.Combine(aDir, "CLAUDE.md");
        const string aBody = "## SCP_Core 共用規則\n\n指路：<SCP_Core>/Docs~/Coding_Standards.md";
        try
        {
            Directory.CreateDirectory(aDir);
            foreach (string f in Directory.GetFiles(aDir)) File.Delete(f);

            // ① 新檔
            var r1 = SCP_EntryDocInstaller.Install(aPath, aBody, "claude", "t");
            bool aCreated = r1.Ok && r1.Changed && File.Exists(aPath) && r1.BackupPath == null;

            // ② 冪等：再跑一次不動檔（不製造假 diff）
            var r2 = SCP_EntryDocInstaller.Install(aPath, aBody, "claude", "t");
            bool aIdem = r2.Ok && !r2.Changed;

            // ③ 使用者在前後各加東西 ⇒ 更新只動中間那段，而且**第一次改動前有備份**
            string aUserEdited = "# 我的規則\n\nAAAA\n\n" + File.ReadAllText(aPath).TrimEnd() + "\n\n## 尾巴\n\nZZZZ\n";
            File.WriteAllText(aPath, aUserEdited);
            var r3 = SCP_EntryDocInstaller.Install(aPath, aBody + "\n\n（新版）", "claude", "t");
            string aAfter = File.ReadAllText(aPath);
            bool aKeptBoth = r3.Ok && r3.Changed
                             && aAfter.Contains("AAAA", StringComparison.Ordinal)
                             && aAfter.Contains("ZZZZ", StringComparison.Ordinal)
                             && aAfter.Contains("（新版）", StringComparison.Ordinal);
            bool aBackedUp = r3.BackupPath != null && File.Exists(r3.BackupPath!);

            // ④ 手改受管區塊 ⇒ **不敢寫**，而且檔案真的沒被動過
            string aTampered = aAfter.Replace("（新版）", "（我手改的）");
            File.WriteAllText(aPath, aTampered);
            var r4 = SCP_EntryDocInstaller.Install(aPath, aBody, "claude", "t");
            bool aRefused = !r4.Ok && r4.StateBefore == SCP_EntryState.LocalEdit
                            && File.ReadAllText(aPath) == aTampered;

            // ⑤ force ⇒ 才動
            var r5 = SCP_EntryDocInstaller.Install(aPath, aBody, "claude", "t", iForce: true);
            bool aForced = r5.Ok && r5.Changed;

            // ⑥ 移除 ⇒ 使用者的字全在
            var r6 = SCP_EntryDocInstaller.Uninstall(aPath, aBody);
            string aRemoved = File.ReadAllText(aPath);
            bool aClean = r6.Ok && aRemoved.Contains("AAAA", StringComparison.Ordinal)
                          && aRemoved.Contains("ZZZZ", StringComparison.Ordinal)
                          && !aRemoved.Contains(SCP_EntryDoc.BeginToken, StringComparison.Ordinal);

            bool aOk = aCreated && aIdem && aKeptBoth && aBackedUp && aRefused && aForced && aClean;
            return new CheckRow("入口檔落地",
                $"新建={aCreated}／再跑不動檔={aIdem}／前後使用者內容都活著={aKeptBoth}／有備份={aBackedUp}"
                + $"／手改時拒寫且檔案沒動={aRefused}／force 才動={aForced}／移除後使用者的字全在={aClean}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("入口檔落地", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { Directory.Delete(aDir, true); } catch { } }
    }

    // 區塊職責：skill 鏡像的三件事 —— 枚舉判準、鏡像同步、**誰裝的**要分得開。
    // 物理意義: 🩸 最後一項是這一格存在的主要理由：第一版把「帶 .ucl_source 的目錄」
    //          併進 Orphan，於是頁面第一次跑就給 Bar 底下 26 個 UCL skill 各配一顆刪除鈕。
    //          那不是顯示錯誤，是**一顆會刪掉別套系統資產的按鈕**。
    static CheckRow SkillMirror()
    {
        string aBase = Path.Combine(Path.GetTempPath(), "senate_selftest_skills");
        string aSrc = Path.Combine(aBase, "Skills~");
        string aProj = Path.Combine(aBase, "proj");
        var aTarget = SCP_SkillTarget.Claude;
        try
        {
            if (Directory.Exists(aBase)) Directory.Delete(aBase, true);

            // 源端：一個算數的、三個不算數的（_ 前綴／~ 結尾／缺 SKILL.md）
            Directory.CreateDirectory(Path.Combine(aSrc, "good"));
            File.WriteAllText(Path.Combine(aSrc, "good", "SKILL.md"), "# good\n");
            File.WriteAllText(Path.Combine(aSrc, "good", "extra.md"), "before\n");
            Directory.CreateDirectory(Path.Combine(aSrc, "_hidden"));
            File.WriteAllText(Path.Combine(aSrc, "_hidden", "SKILL.md"), "x");
            Directory.CreateDirectory(Path.Combine(aSrc, "tilde~"));
            File.WriteAllText(Path.Combine(aSrc, "tilde~", "SKILL.md"), "x");
            Directory.CreateDirectory(Path.Combine(aSrc, "noskill"));
            File.WriteAllText(Path.Combine(aSrc, "noskill", "readme.md"), "x");

            List<string> aFound = SCP_SkillSource.Discover(aSrc);
            bool aEnum = aFound.Count == 1 && aFound[0] == "good";

            // ① 安裝
            var r1 = SCP_SkillInstall.Sync(aSrc, aTarget, aProj, "good");
            string aDst = aTarget.SkillDir(aProj, "good");
            bool aInstalled = r1.Ok && File.Exists(Path.Combine(aDst, "SKILL.md"))
                              && File.Exists(Path.Combine(aDst, SCP_SkillSource.MarkerFileName));

            // ② 冪等：再同步一次不寫任何檔
            var r2 = SCP_SkillInstall.Sync(aSrc, aTarget, aProj, "good");
            bool aIdem = r2.Ok && r2.Copied == 0 && r2.RemovedOrphanFiles == 0;

            // ③ 源端改一個檔 ⇒ Stale ⇒ 同步後回 Synced；源端刪一個檔 ⇒ 安裝端跟著清
            File.WriteAllText(Path.Combine(aSrc, "good", "SKILL.md"), "# good v2\n");
            bool aStale = FindState(SCP_SkillInstall.Status(aSrc, aTarget, aProj), "good") == SCP_SkillState.Stale;
            File.Delete(Path.Combine(aSrc, "good", "extra.md"));
            var r3 = SCP_SkillInstall.Sync(aSrc, aTarget, aProj, "good");
            bool aResynced = r3.Ok && r3.Copied == 1 && r3.RemovedOrphanFiles == 1
                             && FindState(SCP_SkillInstall.Status(aSrc, aTarget, aProj), "good") == SCP_SkillState.Synced;

            // ④ 誰裝的要分得開：我的殘留／別套裝的／沒人認領
            MakeInstalled(aTarget, aProj, "mine-orphan", SCP_SkillSource.MarkerFileName);
            MakeInstalled(aTarget, aProj, "ucl-thing", SCP_SkillInstall.LegacyMarkerFileName);
            MakeInstalled(aTarget, aProj, "hand-placed", null);
            List<SCP_SkillStatus> aRows2 = SCP_SkillInstall.Status(aSrc, aTarget, aProj);
            bool aProvenance = FindState(aRows2, "mine-orphan") == SCP_SkillState.Orphan
                               && FindState(aRows2, "ucl-thing") == SCP_SkillState.Foreign
                               && FindState(aRows2, "hand-placed") == SCP_SkillState.Unmanaged;

            // ⑤ 別套裝的 **連顯式放行都不刪**；沒標記的預設不刪
            var rF = SCP_SkillInstall.Remove(aTarget, aProj, "ucl-thing", iAllowUnmanaged: true);
            var rU = SCP_SkillInstall.Remove(aTarget, aProj, "hand-placed");
            bool aRefuse = !rF.Ok && Directory.Exists(aTarget.SkillDir(aProj, "ucl-thing"))
                           && !rU.Ok && Directory.Exists(aTarget.SkillDir(aProj, "hand-placed"));

            // ⑥ 自己的殘留刪得掉
            var rO = SCP_SkillInstall.Remove(aTarget, aProj, "mine-orphan");
            bool aRemoveMine = rO.Ok && !Directory.Exists(aTarget.SkillDir(aProj, "mine-orphan"));

            bool aOk = aEnum && aInstalled && aIdem && aStale && aResynced && aProvenance && aRefuse && aRemoveMine;
            return new CheckRow("skill 鏡像",
                $"枚舉判準（4 選 1）={aEnum}／安裝含標記={aInstalled}／再同步不寫檔={aIdem}／改源端=Stale:{aStale}"
                + $"／同步後回 Synced 且清殘檔={aResynced}／誰裝的分得開（Orphan/Foreign/Unmanaged）={aProvenance}"
                + $"／別套的連放行都不刪={aRefuse}／自己的殘留刪得掉={aRemoveMine}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("skill 鏡像", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { Directory.Delete(aBase, true); } catch { } }
    }

    static SCP_SkillState FindState(List<SCP_SkillStatus> iRows, string iName)
    {
        foreach (SCP_SkillStatus r in iRows) if (r.Name == iName) return r.State;
        return SCP_SkillState.NotInstalled;
    }

    static void MakeInstalled(SCP_SkillTarget iTarget, string iProj, string iName, string? iMarker)
    {
        string aDir = iTarget.SkillDir(iProj, iName);
        Directory.CreateDirectory(aDir);
        File.WriteAllText(Path.Combine(aDir, "SKILL.md"), "x");
        if (iMarker != null) File.WriteAllText(Path.Combine(aDir, iMarker), "{}");
    }

    /// <summary>顯示參數的 round-trip：存進 JSON 再讀回來要是同一份，且缺欄位用預設（不是 0）。</summary>
    static CheckRow StyleRoundTrip()
    {
        var aStyle = new SCP_GuiStyle();
        aStyle.SetScale(1.5f);
        aStyle.TextWidth = 120;
        SCP_GuiStyle aBack = SCP_GuiStyle.FromJson(SCP_JsonData.Parse(aStyle.ToJson().ToJson()));
        bool aSame = Math.Abs(aBack.Scale - 1.5f) < 0.001f && aBack.TextWidth == 120;

        // 空物件 ⇒ 預設值（「沒設過」不可以變成 0）
        SCP_GuiStyle aEmpty = SCP_GuiStyle.FromJson(SCP_JsonData.Parse("{}"));
        bool aDefault = Math.Abs(aEmpty.Scale - SCP_GuiStyle.DefaultScale) < 0.001f && aEmpty.TextWidth >= 40;

        // 超出範圍要被夾，不可以照收（NaN／0 會讓每個尺寸都變 0 而版位不報錯）
        var aClamp = new SCP_GuiStyle();
        aClamp.SetScale(99f);
        bool aClamped = Math.Abs(aClamp.Scale - SCP_GuiStyle.MaxScale) < 0.001f;

        return new CheckRow("顯示參數 round-trip",
            $"存讀一致={aSame}／缺欄位用預設={aDefault}（{aEmpty.Scale:0.##}）／超範圍夾住={aClamped}",
            aSame && aDefault && aClamped ? CheckResult.Pass : CheckResult.Fail);
    }


    // ═══════════════════════════════════════════════════════════
    // 區塊職責：閱讀庫 JSON 版面的兩格 —— ① writer 的固定點（真閘）② 與磁碟的相符份數（讀數）。
    //
    // 物理意義：寫入端從 Unity 端搬進 SCP_Core 時，遷移期間**兩個寫入端並存**，
    //           而「搬對了」的唯一可信讀數是同輸入兩邊輸出**逐位元組**相同。
    //           版面不同的失敗樣子是「內容逐鍵相同、整批翻紅」—— 沒有任何一層會喊。
    //
    // ⚠ 為什麼 ② 是讀數不是閘：**磁碟不是規格。**（2026-09-14 calli 逐位元組量的）
    //    `BookNotes/Library` 底下 596 份 JSON 是至少三支 writer、跨數個時代的沉積
    //    （tab＋冒號無空格 426 份／2 空格 一批／tab＋冒號有空格 一批），
    //    而**行尾那一根軸整根不算數** —— repo 的 core.autocrlf=true，checkout 會把整份換成 CRLF
    //    （含結尾那一個 LF）⇒ 工作樹上的行尾是 git 的產物，不是 writer 的。
    //    ⇒ 拿它當通過條件的話，這一格永遠紅，而紅得沒有資訊。它的正確用途是
    //      **間接驗「UclLegacy 有沒有把舊 writer 抄對」**，所以印份數、不判生死。
    //
    // 🩸 這一格的由來：我 2026-09-11 列了「三根軸」就宣布量完了，今天逐位元組跑完發現是**六根**
    //    （縮排／冒號空格／陣列括號位置／空容器渲染／行尾／結尾換行），其中最後兩根還是假的（git 的）。
    //    ⇒ 一把只看得見自己列出那幾根軸的尺，在漏軸時的輸出跟「完全相同」一模一樣。
    //      所以 ① 不用軸表斷言，用**一份把所有軸一次蓋掉的期望字串**。
    // ═══════════════════════════════════════════════════════════
    static CheckRow LibraryJsonStyleFixture()
    {
        // 一份刻意把每一根軸都踩到的樹：純量／巢狀物件／非空陣列／**空陣列**／非 ASCII。
        var aData = SCP_JsonData.NewObject();
        aData.Set("chapter_id", "0001");
        aData.Set("title", "第 1 話");
        var aProgress = SCP_JsonData.NewObject();
        aProgress.Set("current_chapter_id", "0001");
        aData.Set("progress", aProgress);
        var aRounds = SCP_JsonData.NewArray();
        var aRound = SCP_JsonData.NewObject();
        aRound.Set("round", 1);
        aRounds.Add(aRound);
        aData.Set("rounds", aRounds);
        aData.Set("facts", SCP_JsonData.NewArray());
        aData.Set("schema_version", 2);

        // 期望值＝`UCL_JsonData.SerializeValueBeautify` 的形狀，逐字抄自它
        // （⛔ 不是抄磁碟上某一份檔 —— 那份檔的行尾是 git 給的）。
        string aExpect = string.Join("\n", new[]
        {
            "{",
            "\t\"chapter_id\":\"0001\",",
            "\t\"title\":\"第 1 話\",",
            "\t\"progress\":{",
            "\t\t\"current_chapter_id\":\"0001\"",
            "\t},",
            "\t\"rounds\":",
            "\t[",
            "\t\t{",
            "\t\t\t\"round\":1",
            "\t\t}",
            "\t],",
            "\t\"facts\":",
            "\t[",
            "",
            "\t],",
            "\t\"schema_version\":2",
            "}",
        });

        string aGot = SCP_JsonWriter.Write(aData, SCP_JsonStyle.UclLegacy);
        bool aMatch = aGot == aExpect;

        // writer 的固定點：寫出來的東西讀回去再寫，必須逐位元組相同。
        // ⛔ 這一格跟上面那格治的不是同一種病：上面治「抄錯舊 writer」，這格治「writer 自己不穩」。
        string aAgain = SCP_JsonWriter.Write(SCP_JsonData.Parse(aGot), SCP_JsonStyle.UclLegacy);
        bool aStable = aAgain == aGot;

        string aWhere = aMatch ? "—" : FirstDiffAt(aExpect, aGot);
        return new CheckRow("閱讀庫 JSON 版面（UclLegacy 對舊 writer）",
            $"與舊 writer 同形={aMatch}{(aMatch ? "" : "（首差 " + aWhere + "）")}／round-trip 穩定={aStable}",
            aMatch && aStable ? CheckResult.Pass : CheckResult.Fail);
    }

    static string FirstDiffAt(string iExpect, string iGot)
    {
        int n = Math.Min(iExpect.Length, iGot.Length);
        for (int i = 0; i < n; i++)
            if (iExpect[i] != iGot[i])
                return $"@{i}　期望「{Show(iExpect, i)}」／實得「{Show(iGot, i)}」";
        return $"前 {n} 字元相同，長度 {iExpect.Length}／{iGot.Length}";
    }

    static string Show(string iS, int iAt)
    {
        int aFrom = Math.Max(0, iAt - 8), aTo = Math.Min(iS.Length, iAt + 10);
        return iS.Substring(aFrom, aTo - aFrom).Replace("\n", "\n").Replace("\t", "\t");
    }

    /// <summary>
    /// 拿**真的閱讀庫**逐位元組對拍：每一份都要 parse 得動，並印出 UclLegacy 寫回後
    /// 與磁碟相符的份數（行尾無關）。
    /// </summary>
    /// <remarks>⚠ 相符份數是**讀數不是閘** —— 理由見本區塊上方註解（磁碟不是規格）。</remarks>
    // 區塊職責：搬進 SCP_Core 的追回檔渲染層（`SCP_LibraryRecall`），與 **Editor 端真產物**逐位元組對拍。
    // 物理意義：受測體是磁碟上那些 `letters/<p>/cmd/reading_recall_<media>.md` ——
    //          它們是 `UCL_ReadingLibraryIO.RenderRecall`（另一份實作、另一個 process）寫出來的，
    //          ⇒ 對照組**不同源**，這正是 TASK-0166 ③ 要的那種讀數。
    // ⚠ 而它們是**快照**：資料後來被改過的話，重新渲染出來不一樣是**合理的**，不是移植壞了。
    //   🩸 拿它整批當閘的下場是「永遠紅，而紅得沒有資訊」（calli 2026-09-14 在隔壁那格踩過同一隻）。
    //   ⇒ 判準：只有**檔比它全部來源都新**（mtime ≥ reader 目錄／media.json／work.json 的最大值）
    //     那幾份才當閘；其餘只當讀數，並且把兩個數字**分開印**。
    // ⚠ `generated_at:` 那一行逐次不同（本機時間）⇒ 比對時排除它，而**排除這件事要印出來**：
    //   不說的話，「我比了全部」與「我比了除了那一行之外的全部」在畫面上同形。
    static IEnumerable<CheckRow> RealRecallPortMatchesEditor(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aLetters = Path.Combine(p.AgentCommandsRoot, "ChatTavern", "baton", "letters");
            if (!Directory.Exists(aLetters)) continue;
            string[] aFiles = Directory.GetFiles(aLetters, "reading_recall_*.md", SearchOption.AllDirectories);
            if (aFiles.Length == 0) continue;
            aAny = true;

            int aFreshSame = 0, aFreshDiff = 0, aStaleSame = 0, aStaleDiff = 0, aUnrenderable = 0;
            string aFirstDiff = "";
            foreach (string f in aFiles)
            {
                string aEditorText;
                try { aEditorText = File.ReadAllText(f, new UTF8Encoding(false)); }
                catch { aUnrenderable++; continue; }
                string aPersona = FrontmatterValue(aEditorText, "persona");
                string aMediaId = FrontmatterValue(aEditorText, "media_id");
                if (aPersona.Length == 0 || aMediaId.Length == 0) { aUnrenderable++; continue; }

                string? aMine = SCP_LibraryRecall.RenderRecall(p.AgentCommandsRoot, aMediaId, aPersona,
                                                               true, out _);
                if (aMine == null) { aUnrenderable++; continue; }

                bool aSame = Eol(StripGeneratedAt(aEditorText)) == Eol(StripGeneratedAt(aMine));
                bool aFresh = RecallIsFresh(p.AgentCommandsRoot, aMediaId, aPersona, f);
                if (aFresh) { if (aSame) aFreshSame++; else aFreshDiff++; }
                else { if (aSame) aStaleSame++; else aStaleDiff++; }
                if (!aSame && aFresh && aFirstDiff.Length == 0)
                    aFirstDiff = "　▸ 第一筆不符（新鮮）：" + Path.GetFileName(f);
            }

            int aFresh2 = aFreshSame + aFreshDiff;
            yield return new CheckRow(
                $"追回檔渲染移植 vs Editor 真產物（{p.Name}）",
                $"受測 {aFiles.Length} 份／**新鮮 {aFresh2}：相符 {aFreshSame}／不符 {aFreshDiff}**"
                + $"（這 {aFresh2} 份是閘）　過期 {aStaleSame + aStaleDiff}：相符 {aStaleSame}／不符 {aStaleDiff}"
                + "（**只是讀數** —— 資料在那之後改過，不符是合理的）"
                + $"／渲染不出來 {aUnrenderable}"
                + "　⚠ 比對**排除 `generated_at:` 那一行**（本機時間，逐次不同）" + aFirstDiff,
                aFreshDiff == 0
                    ? (aFresh2 > 0 ? CheckResult.Pass : CheckResult.Skipped)
                    : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("追回檔渲染移植 vs Editor 真產物",
                "找不到任何 `reading_recall_*.md` ⇒ **這是跳過，不是通過**", CheckResult.Skipped);
    }

    // 區塊職責：搬進 SCP_Core 的閱讀卡渲染（`SCP_LibraryBookshelf.RenderCard`），
    //          與 **Editor 端真產物** `Library/media/<id>/readers/<p>/bookshelf.md` 逐位元組對拍。
    // ⚠ 新鮮度判準跟追回檔那格**不一樣，而且要更窄**：閱讀卡只由 reader.json／media.json／work.json
    //   三個檔決定（章節與人物**不影響**它）⇒ 拿整個 reader 目錄當來源會把「讀了新的一章」
    //   誤判成「卡片過期」，而那是一個假的不新鮮。
    // ⚠ 本格**不排除任何行**（卡片裡沒有逐次變動的時戳；`updated_at` 來自 reader.json）。
    //   ⛔ 例外：reader.json 缺 `updated_at` 時渲染會落 `Today()` ⇒ 那種卡片跨日必然不符。
    //   它會落在「不符」那一欄，而不是被悄悄排掉。
    static IEnumerable<CheckRow> RealBookshelfPortMatchesEditor(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aLibrary = Path.Combine(p.AgentCommandsRoot, "BookNotes", "Library");
            if (!Directory.Exists(aLibrary)) continue;
            string[] aFiles = Directory.GetFiles(aLibrary, "bookshelf.md", SearchOption.AllDirectories);
            if (aFiles.Length == 0) continue;
            aAny = true;

            int aFreshSame = 0, aFreshDiff = 0, aStaleSame = 0, aStaleDiff = 0, aUnrenderable = 0, aOldWriter = 0;
            string aFirstDiff = "";
            foreach (string f in aFiles)
            {
                string aEditorText;
                try { aEditorText = File.ReadAllText(f, new UTF8Encoding(false)); }
                catch { aUnrenderable++; continue; }
                string aPersona = FrontmatterValue(aEditorText, "reader_persona");
                string aMediaId = FrontmatterValue(aEditorText, "media_id");
                if (aPersona.Length == 0 || aMediaId.Length == 0) { aUnrenderable++; continue; }

                // 🩸 「檔比來源新」**不蘊含**「檔是用現行 writer 產的」（2026-09-16 這一格咬了我一次）：
                //   新鮮度尺回答的是「資料後來有沒有被改」，⛔ 不回答「這份是誰寫的」。
                //   實測 102 份裡有 2 份的 `generated:` 是**裸的**（沒有後面那串註解）⇒ 舊版 writer 的產物，
                //   而其中一份的 mtime 比 reader.json 新 ⇒ 被判成「新鮮」⇒ 閘紅，而移植沒有錯。
                //   ⇒ 分一個桶，並且**把數字印出來** —— ⛔ 悄悄排掉的話，「驗過 42 份」與「驗過 41 份」同形。
                if (!aEditorText.Contains("generated: mechanical   #", StringComparison.Ordinal))
                { aOldWriter++; continue; }

                string? aMine = SCP_LibraryBookshelf.RenderCard(p.AgentCommandsRoot, aMediaId, aPersona, out _);
                if (aMine == null) { aUnrenderable++; continue; }

                bool aSame = Eol(aEditorText) == Eol(aMine);
                bool aFresh = CardIsFresh(p.AgentCommandsRoot, aMediaId, aPersona, f);
                if (aFresh) { if (aSame) aFreshSame++; else aFreshDiff++; }
                else { if (aSame) aStaleSame++; else aStaleDiff++; }
                if (!aSame && aFresh && aFirstDiff.Length == 0)
                    aFirstDiff = "　▸ 第一筆不符（新鮮）：" + aMediaId + "／" + aPersona;
            }

            int aFresh2 = aFreshSame + aFreshDiff;
            yield return new CheckRow(
                $"閱讀卡渲染移植 vs Editor 真產物（{p.Name}）",
                $"受測 {aFiles.Length} 份／**新鮮 {aFresh2}：相符 {aFreshSame}／不符 {aFreshDiff}**（這 {aFresh2} 份是閘）"
                + $"　過期 {aStaleSame + aStaleDiff}：相符 {aStaleSame}／不符 {aStaleDiff}（**只是讀數**）"
                + $"／渲染不出來 {aUnrenderable}／**舊版 writer 產的 {aOldWriter}**（`generated:` 沒有註解 ⇒ 驗不了現行移植，不當閘）"
                + "　⚠ 來源只算 reader/media/work.json 三個檔（章節與人物不影響卡片）" + aFirstDiff,
                aFreshDiff == 0
                    ? (aFresh2 > 0 ? CheckResult.Pass : CheckResult.Skipped)
                    : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("閱讀卡渲染移植 vs Editor 真產物",
                "找不到任何 `bookshelf.md` ⇒ **這是跳過，不是通過**", CheckResult.Skipped);
    }

    // 區塊職責：建檔層（`SCP_LibraryInit` 的三個 Build）產出的形狀，與磁碟上 Editor 建的那些逐位元組對拍。
    // ⚠ **這把尺量不到什麼，先講**（本格最重要的一行）：
    //   輸入是**從輸出讀回來的** —— 我拿 work.json 裡的 title/author/aliases 去重建 work.json。
    //   ⇒ 它驗得到：鍵序、JSON 版面（UclLegacy）、schema_version、陣列渲染、`SaveJson` 的結尾換行。
    //   ⛔ 它**驗不到**：alias 合併語意（「title 有沒有被收進 aliases」那一格，因為輸入裡它已經在了）。
    //     那一格結構上同源，要驗它得有一份「原始輸入」，而磁碟上沒有留。
    // ⚠ 為什麼不開 clean room 對照 Editor：Editor 端的路徑寫死 `UCL_RepoPath.AgentCommandsDir`，
    //   **吃不了 data_root** ⇒ 讓它寫進暫存樹這條路不存在，而讓它寫進真樹是污染。
    // 🩸 受測體的判準落在**來源**不是**結果**（2026-09-16 第一次跑 work 29 相符／10 不符，而移植沒錯）：
    //   那 10 份的**鍵集合**根本不同（6 份只有 4 個鍵、1 份多 `relations`/`_note`、
    //   1 份是寫書線的 `author_persona`/`publish_status`…）⇒ 它們不是這支 builder 寫的。
    //   ⇒ 判準：鍵**集合**（無序）相同才算受測體；⛔ 而**順序仍在受測範圍內** ——
    //     集合是出身、順序是行為，把順序也排掉的話，一個把鍵寫反的移植 bug 會被判成「不是我寫的」。
    //   ⚠ 而受測體數**要印出來**：它掉下去就是射程縮了，那件事必須看得見。
    static IEnumerable<CheckRow> RealLibraryInitMatchesDisk(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aLibrary = Path.Combine(p.AgentCommandsRoot, "BookNotes", "Library");
            if (!Directory.Exists(aLibrary)) continue;
            aAny = true;
            var aUtf8 = new UTF8Encoding(false);

            int aWorkSame = 0, aWorkDiff = 0, aWorkObjAlias = 0, aWorkOther = 0, aWorkByteOnly = 0;
            string aFirst = "", aFirstM = "";
            foreach (string f in Directory.GetFiles(Path.Combine(aLibrary, SCP_LibraryStore.WorksDirName),
                                                    SCP_LibraryStore.WorkJsonName, SearchOption.AllDirectories))
            {
                string aDisk; SCP_JsonData aTree;
                try { aDisk = File.ReadAllText(f, aUtf8); aTree = SCP_JsonData.Parse(aDisk); }
                catch { aWorkDiff++; continue; }
                // 物件形狀的 alias 重建不回去（AliasToString 是單向的）⇒ 分桶，⛔ 不算進閘也不假裝相符
                bool aObjAlias = false;
                var aAliases = new List<string>();
                SCP_JsonData aA = aTree[SCP_LibraryIO.Key_Aliases];
                if (aA.Exists && aA.IsArray)
                    for (int i = 0; i < aA.Count; i++)
                    {
                        if (aA[i].IsObject) { aObjAlias = true; break; }
                        aAliases.Add(SCP_LibraryRecall.AliasToString(aA[i]));
                    }
                if (aObjAlias) { aWorkObjAlias++; continue; }
                var aTags = new List<string>();
                SCP_JsonData aG = aTree[SCP_LibraryIO.Key_GenreTags];
                if (aG.Exists && aG.IsArray)
                    for (int i = 0; i < aG.Count; i++) aTags.Add(SCP_LibraryRecall.AliasToString(aG[i]));

                SCP_JsonData aMine = SCP_LibraryInit.BuildWorkJson(
                    aTree.GetString(SCP_LibraryIO.Key_WorkId, ""),
                    aTree.GetString(SCP_LibraryIO.Key_Title, ""),
                    aTree.GetString(SCP_LibraryIO.Key_TitleOriginal, ""),
                    aTree.GetString(SCP_LibraryIO.Key_Author, ""), aAliases, aTags);
                // ⚠ 鍵**序**不同 ⇒ 這份不是現行 builder 一次寫成的（例：`apocalypse-hotel` 的
                //   `title_original` 排在最末 ＝ 後來補上去的）⇒ 它的**原始輸入不可回復**
                //   （當初那次 ToStringArray 的 alsoInclude 跟今天不同，aliases 陣列順序因此不同）。
                //   ⇒ 分桶排除，⛔ 而鍵序本身不是就此不驗 —— 它由 `LibraryBuilderGolden` 的定值 fixture 釘住。
                if (!SameKeySet(aTree, aMine) || !SameKeyOrder(aTree, aMine)) { aWorkOther++; continue; }
                // 閘＝**語意相等**（逐鍵取值）：值寫錯／鍵漏掉／schema_version 錯，這一層抓得到。
                if (!SameValues(aTree, aMine))
                { aWorkDiff++; if (aFirst.Length == 0) aFirst = "　▸ work 首筆語意不符：" + Path.GetFileName(Path.GetDirectoryName(f)!); }
                // 讀數＝逐位元組：它同時受**鍵序**與**版面**影響，而磁碟是多支 writer 的沉積
                // ⇒ ⛔ 不當閘（那等於要求全庫都由現行 writer 產出，而那個前提為假）。
                else if (Eol(aDisk) == Eol(SCP_JsonWriter.Write(aMine, SCP_JsonStyle.UclLegacy) + "\n")) aWorkSame++;
                else aWorkByteOnly++;
            }

            int aMediaSame = 0, aMediaDiff = 0, aMediaOther = 0, aMediaByteOnly = 0;
            foreach (string f in Directory.GetFiles(Path.Combine(aLibrary, SCP_LibraryStore.MediaDirName),
                                                    SCP_LibraryStore.MediaJsonName, SearchOption.AllDirectories))
            {
                string aDisk; SCP_JsonData aTree;
                try { aDisk = File.ReadAllText(f, aUtf8); aTree = SCP_JsonData.Parse(aDisk); }
                catch { aMediaDiff++; continue; }
                SCP_JsonData aMine = SCP_LibraryInit.BuildMediaJson(
                    aTree.GetString(SCP_LibraryIO.Key_MediaId, ""),
                    aTree.GetString(SCP_LibraryIO.Key_WorkId, ""),
                    aTree.GetString(SCP_LibraryIO.Key_MediaKind, ""));
                if (!SameKeySet(aTree, aMine)) { aMediaOther++; continue; }
                if (!SameValues(aTree, aMine))
                { aMediaDiff++; if (aFirstM.Length == 0) aFirstM = "　▸ media 首筆語意不符：" + Path.GetFileName(Path.GetDirectoryName(f)!); }
                else if (Eol(aDisk) == Eol(SCP_JsonWriter.Write(aMine, SCP_JsonStyle.UclLegacy) + "\n")) aMediaSame++;
                else aMediaByteOnly++;
            }

            // reader.json 只拿**還停在初值**的那些當受測體 —— 讀過幾章之後那份本來就該長得不一樣。
            int aReaderSame = 0, aReaderDiff = 0, aReaderLive = 0;
            foreach (string f in Directory.GetFiles(Path.Combine(aLibrary, SCP_LibraryStore.MediaDirName),
                                                    SCP_LibraryStore.ReaderJsonName, SearchOption.AllDirectories))
            {
                string aDisk; SCP_JsonData aTree;
                try { aDisk = File.ReadAllText(f, aUtf8); aTree = SCP_JsonData.Parse(aDisk); }
                catch { aReaderDiff++; continue; }
                SCP_JsonData aProg = aTree[SCP_LibraryIO.Key_Progress];
                string aStarted = aTree.GetString(SCP_LibraryIO.Key_ReadingStartedAt, "");
                bool aPristine = aProg.Exists
                    && aProg.GetString(SCP_LibraryIO.Key_CurrentChapterId, "x") == ""
                    && aProg.GetString(SCP_LibraryIO.Key_BookmarkNote, "") == "（尚未開始）"
                    && aStarted.Length > 0
                    && aProg.GetString(SCP_LibraryIO.Key_LastRead, "") == aStarted
                    && aTree.GetString(SCP_LibraryIO.Key_UpdatedAt, "") == aStarted;
                if (!aPristine) { aReaderLive++; continue; }
                SCP_JsonData aMine = SCP_LibraryInit.BuildReaderJson(
                    aTree.GetString(SCP_LibraryIO.Key_MediaId, ""),
                    aTree.GetString(SCP_LibraryIO.Key_ReaderPersona, ""),
                    aTree.GetInt(SCP_LibraryIO.Key_Anticipation, 0), aStarted);
                if (Eol(aDisk) == Eol(SCP_JsonWriter.Write(aMine, SCP_JsonStyle.UclLegacy) + "\n")) aReaderSame++;
                else aReaderDiff++;
            }

            // ⚠ 受測體歸零也是失敗 —— 「全部相符」與「一個都沒驗」⛔ 不可以同形。
            bool aOk = aWorkDiff == 0 && aMediaDiff == 0 && aReaderDiff == 0
                       && (aWorkSame + aWorkByteOnly) > 0 && (aMediaSame + aMediaByteOnly) > 0;
            yield return new CheckRow(
                $"建檔層形狀 vs 磁碟既有落檔（{p.Name}）",
                $"work 受測 {aWorkSame + aWorkDiff + aWorkByteOnly} ⇒ **語意不符 {aWorkDiff}（閘）**"
                + $"／逐位元組相符 {aWorkSame}、只差鍵序或版面 {aWorkByteOnly}（讀數）"
                + $"（鍵集合不同 {aWorkOther}／物件形狀 alias {aWorkObjAlias}，兩者不當受測體）"
                + $"　media 受測 {aMediaSame + aMediaDiff + aMediaByteOnly} ⇒ **語意不符 {aMediaDiff}（閘）**"
                + $"／位元組相符 {aMediaSame}、只差鍵序或版面 {aMediaByteOnly}（鍵集合不同 {aMediaOther}）"
                + $"　reader（只取停在初值的）**{aReaderSame}／{aReaderDiff}**（已在讀的 {aReaderLive} 不當受測體）"
                + "　⚠ 輸入是從輸出讀回來的 ⇒ ⛔ **驗不到 alias 合併語意**（那一格結構上同源）；"
                + "鍵序與版面由 `LibraryBuilderGolden` 的定值 fixture 釘住" + aFirst + aFirstM,
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("建檔層形狀 vs 磁碟既有落檔",
                "找不到 BookNotes/Library ⇒ **這是跳過，不是通過**", CheckResult.Skipped);
    }

    /// <summary>
    /// 逐鍵取值比對（頂層 ＋ 陣列逐格 ＋ 巢狀物件遞迴）—— **語意相等**，與鍵序、縮排、版面無關。
    /// <para>🩸 為什麼閘要落在這裡而不是位元組（TASK-0166，2026-09-16）：磁碟是**多支 writer 的沉積**
    /// （實測 40 份 work.json 有 7 種鍵形狀、media.json 有兩種縮排）⇒
    /// 拿位元組相等當閘，等於要求全庫都是現行 writer 產出的，而那個前提為假 ——
    /// 那種閘永遠紅，而紅得沒有資訊。值寫錯／鍵漏掉／schema_version 錯，這一層照樣抓得到。</para>
    /// <para>⚠ 而它**量不到鍵序與版面** —— 那兩格由 <c>LibraryBuilderGolden</c> 的定值 fixture 釘住。</para>
    /// </summary>
    static bool SameValues(SCP_JsonData iA, SCP_JsonData iB)
    {
        if (iA.IsObject || iB.IsObject)
        {
            if (!iA.IsObject || !iB.IsObject) return false;
            if (!SameKeySet(iA, iB)) return false;
            foreach (string k in iA.Keys) if (!SameValues(iA[k], iB[k])) return false;
            return true;
        }
        if (iA.IsArray || iB.IsArray)
        {
            if (!iA.IsArray || !iB.IsArray || iA.Count != iB.Count) return false;
            for (int i = 0; i < iA.Count; i++) if (!SameValues(iA[i], iB[i])) return false;
            return true;
        }
        return SCP_JsonWriter.Write(iA, false) == SCP_JsonWriter.Write(iB, false);
    }

    /// <summary>
    /// 兩棵 JSON 的**鍵集合**（無序、僅頂層）相不相同 —— 用來判「這份是不是這支 builder 寫的」。
    /// ⛔ 刻意不比順序：順序是**行為**，要留在受測範圍內。
    /// </summary>
    // 區塊職責：建檔層三個 builder 的**逐位元組定值** —— 鍵序、版面、結尾換行全部釘死。
    // 物理意義：語料那一格的閘是**語意相等**（它必須如此：磁碟是多支 writer 的沉積），
    //          ⇒ 鍵序與版面在那裡是**不受測的**。這一格補上它們。
    // ⭐ 而這些定值**不是我自己編的**：三段都逐位元組取自磁碟上 Editor 真產物
    //   （`works/kotoko-lamp-and-ledger`／`media/book-kotoko-lamp-and-ledger`／
    //    該 media 底下 `readers/Sirius`，2026-09-16 取樣）⇒ 對照組仍然不同源。
    // ⭐ 它還補上語料閘量不到的那一格：**alias 自動合併** ——
    //   下面 work 的 `aliases` 是給 `null` 之後由 title ＋ title_original 自己長出來的。
    // ⚠ reader 那一段是**初值形狀**（`BuildReaderJson`）：取樣那份已經在讀了，
    //   所以定值只沿用它的**鍵序**，值改成初值。⇒ 這一段的鍵序不同源，值同源。照實標。
    static CheckRow LibraryBuilderGolden()
    {
        const string aWork =
            "{\n\t\"work_id\":\"kotoko-lamp-and-ledger\",\n\t\"title\":\"Lamp and Ledger\",\n"
            + "\t\"title_original\":\"kotoko-lamp-and-ledger\",\n\t\"author\":\"kotoko\",\n"
            + "\t\"aliases\":\n\t[\n\t\t\"Lamp and Ledger\",\n\t\t\"kotoko-lamp-and-ledger\"\n\t],\n"
            + "\t\"genre_tags\":\n\t[\n\t\t\"original\",\n\t\t\"reflection\"\n\t],\n"
            + "\t\"schema_version\":1\n}\n";
        const string aMedia =
            "{\n\t\"media_id\":\"book-kotoko-lamp-and-ledger\",\n\t\"work_id\":\"kotoko-lamp-and-ledger\",\n"
            + "\t\"media_kind\":\"book\",\n\t\"schema_version\":1\n}\n";
        const string aReader =
            "{\n\t\"schema_version\":2,\n\t\"reader_persona\":\"Sirius\",\n"
            + "\t\"media_id\":\"book-kotoko-lamp-and-ledger\",\n\t\"status\":\"reading\",\n"
            + "\t\"anticipation\":4,\n\t\"reading_started_at\":\"2026-08-13\",\n"
            + "\t\"progress\":{\n\t\t\"current_chapter_id\":\"\",\n\t\t\"last_read\":\"2026-08-13\",\n"
            + "\t\t\"bookmark_note\":\"（尚未開始）\"\n\t},\n"
            + "\t\"current_impression\":\"（尚未寫下第一筆心得）\",\n\t\"updated_at\":\"2026-08-13\"\n}\n";

        string aW = SCP_JsonWriter.Write(SCP_LibraryInit.BuildWorkJson(
            "kotoko-lamp-and-ledger", "Lamp and Ledger", "kotoko-lamp-and-ledger", "kotoko",
            null, new List<string> { "original", "reflection" }), SCP_JsonStyle.UclLegacy) + "\n";
        string aM = SCP_JsonWriter.Write(SCP_LibraryInit.BuildMediaJson(
            "book-kotoko-lamp-and-ledger", "kotoko-lamp-and-ledger", "book"),
            SCP_JsonStyle.UclLegacy) + "\n";
        string aR = SCP_JsonWriter.Write(SCP_LibraryInit.BuildReaderJson(
            "book-kotoko-lamp-and-ledger", "Sirius", 4, "2026-08-13"),
            SCP_JsonStyle.UclLegacy) + "\n";

        bool aWOk = aW == aWork, aMOk = aM == aMedia, aROk = aR == aReader;
        // 章節關係標籤也釘一格：它是**寫進回傳檔給人讀**的字串，改掉不會有任何一層叫。
        bool aLabelOk = SCP_LibraryInit.RelationLabel(SCP_ChapterRelation.Next) == "續讀（+1）"
                        && SCP_LibraryInit.RelationLabel(SCP_ChapterRelation.Gap)
                           == "⚠ 跳章（已在 chapter.json 記 gap，未靜默）";
        bool aOk = aWOk && aMOk && aROk && aLabelOk;
        return new CheckRow("建檔層逐位元組定值（鍵序＋版面，取自 Editor 真產物）",
            $"work={aWOk}（含 **alias 自動合併**：輸入給 null，title ＋ title_original 自己長出來）"
            + $"／media={aMOk}／reader 初值={aROk}／RelationLabel={aLabelOk}"
            + "　⚠ reader 那段的鍵序取自真檔、值是初值（取樣那份已經在讀了）",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    // 區塊職責：`SCP_LibraryNote.NoteChapter` 的 **clean room** —— 在暫存樹上真的跑一遍寫入，
    //          把 chapter.json 的逐位元組形狀、續寫（segments）行為、與兩道拒絕寫入的閘全部釘住。
    // ⭐ 為什麼這一支驗得比前幾刀好：`SCP_LibraryNote` **吃 iDataRoot**，所以它跑得進暫存樹；
    //   而 Editor 那側的路徑寫死 `UCL_RepoPath.AgentCommandsDir`，同樣的事它做不到。
    //   ⇒ 移植到 SCP_Core 這件事本身，讓這一層第一次有了「不污染真資料的實跑」。
    // ⭐ chapter.json 的期望值**不是我編的**：形狀逐位元組取自
    //   `media/anim-apocalypse-hotel/readers/basecamp/chapters/0001/chapter.json`（Editor 真產物）。
    // 🩸 而續寫那一段**全庫零活體**（2026-09-16 實測：沒有任何一份 chapter.json 帶 `segments`）
    //   ⇒ TASK-0121 的那條路從落地到今天沒有任何產物驗證過它。這一格是它的第一份讀數。
    // 數值影響：只在暫存目錄建檔，跑完刪掉；⛔ 不碰任何真的資料樹。
    static CheckRow LibraryNoteCleanRoom()
    {
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_note_" + Guid.NewGuid().ToString("N")[..8]);
        var aLetters = new SCP_LettersRoot(Path.Combine(aRoot, "letters"));
        try
        {
            string aToday = SCP_LibraryIO.Today();
            SCP_LibraryInit.MediaInit(aLetters, aRoot, "w1", "book-w1", "book", "tester",
                "標題", "原題", "作者", 4, null, null, out string? aInitErr);

            // ① 第一場：開 r1、寫索引、更新 reader
            string? aLog1 = SCP_LibraryNote.NoteChapter(aLetters, aRoot, "book-w1", "tester", "0001",
                "第 1 話", "照著做的一百年", "12:37-13:41", "正文一", "看法一", "書籤一",
                false, 0, out string? aFile1, out int aRound1, out string? aErr1);
            string aChapterJson = Path.Combine(SCP_LibraryStore.ChapterDir(aRoot, "book-w1", "tester", "0001"),
                                               SCP_LibraryStore.ChapterJsonName);
            string aExpectChapter =
                "{\n\t\"chapter_id\":\"0001\",\n\t\"display_number\":\"第 1 話\",\n"
                + "\t\"title\":\"照著做的一百年\",\n\t\"time_range\":\"12:37-13:41\",\n"
                + "\t\"rounds\":\n\t[\n\t\t{\n\t\t\t\"round\":1,\n"
                + $"\t\t\t\"reading_date\":\"{aToday}\",\n\t\t\t\"file\":\"r1_{aToday}.md\"\n"
                + "\t\t}\n\t],\n\t\"schema_version\":2\n}\n";
            bool aShapeOk = aInitErr == null && aErr1 == null && aRound1 == 1
                            && Eol(File.ReadAllText(aChapterJson, new UTF8Encoding(false))) == Eol(aExpectChapter);
            // round md ＝ 正文 TrimEnd ＋ 一個換行，⛔ 沒有任何頭尾裝飾
            bool aBodyOk = aFile1 != null
                           && Eol(File.ReadAllText(aFile1, new UTF8Encoding(false))) == Eol("正文一\n");
            // 三個投影都要被帶起來（少任何一個都是「每一層都綠」的失效）
            bool aProjOk = File.Exists(Path.Combine(
                               SCP_LibraryStore.ReaderRoot(aRoot, "book-w1", "tester"),
                               SCP_LibraryStore.BookshelfName))
                           && File.Exists(SCP_LettersPaths.CmdPayload(aLetters, "tester", "reading_recall", "book-w1"));

            // ② 續寫：追加進 r1、segments=2、⛔ **不開 r2**，且章層 time_range 接上去
            string? aLog2 = SCP_LibraryNote.NoteChapter(aLetters, aRoot, "book-w1", "tester", "0001",
                null, null, "30:00-52:00", "正文二", null, null,
                true, 0, out string? aFile2, out int aRound2, out string? aErr2);
            SCP_JsonData aCh = SCP_JsonData.Parse(File.ReadAllText(aChapterJson, new UTF8Encoding(false)));
            bool aAppendOk = aErr2 == null && aRound2 == 1 && aFile2 == aFile1
                             && aCh[SCP_LibraryIO.Key_Rounds].Count == 1
                             && aCh[SCP_LibraryIO.Key_Rounds][0].GetInt(SCP_LibraryIO.Key_Segments, 0) == 2
                             && aCh.GetString(SCP_LibraryIO.Key_TimeRange, "") == "12:37-13:41, 30:00-52:00";
            string aRoundText = File.ReadAllText(aFile1!, new UTF8Encoding(false));
            bool aAppendBodyOk = aRoundText.Contains("正文一", StringComparison.Ordinal)      // 舊字沒被動
                                 && aRoundText.Contains("正文二", StringComparison.Ordinal)
                                 && aRoundText.Contains("## 續寫・第 2 場", StringComparison.Ordinal)
                                 && aRoundText.Contains("30:00-52:00", StringComparison.Ordinal);
            // 續寫**不印** RelationLabel（那句話的答案永遠是「同一章」⇒ 不帶資訊）
            bool aLabelOk = aLog1 != null && aLog1.Contains("續讀（+1）", StringComparison.Ordinal)
                            && aLog2 != null && !aLog2.Contains("續讀（+1）", StringComparison.Ordinal)
                            && aLog2.Contains("沒有開新的 round", StringComparison.Ordinal);

            // ③ 反向對照：續寫一個索引裡沒有的 round ⇒ **拒絕**，且磁碟一個位元組都不動
            string aBefore = File.ReadAllText(aChapterJson, new UTF8Encoding(false));
            string? aLog3 = SCP_LibraryNote.NoteChapter(aLetters, aRoot, "book-w1", "tester", "0001",
                null, null, null, "不該落地", null, null, true, 99,
                out _, out _, out string? aErr3);
            bool aRefuseOk = aLog3 == null && aErr3 != null && aErr3.Contains("不在 chapter.json 索引裡", StringComparison.Ordinal)
                             && File.ReadAllText(aChapterJson, new UTF8Encoding(false)) == aBefore;

            // ④ 反向對照：沒有 reader.json 的人 ⇒ 停下來，⛔ 不替他建檔
            string? aLog4 = SCP_LibraryNote.NoteChapter(aLetters, aRoot, "book-w1", "nobody", "0001",
                null, null, null, "不該落地", null, null, false, 0, out _, out _, out string? aErr4);
            bool aLadderOk = aLog4 == null && aErr4 != null && aErr4.Contains("op=media_init", StringComparison.Ordinal)
                             && !Directory.Exists(SCP_LibraryStore.ReaderRoot(aRoot, "book-w1", "nobody"));

            bool aOk = aShapeOk && aBodyOk && aProjOk && aAppendOk && aAppendBodyOk
                       && aLabelOk && aRefuseOk && aLadderOk;
            return new CheckRow("note_chapter clean room（暫存樹實跑）",
                $"chapter.json 逐位元組={aShapeOk}／round 正文無裝飾={aBodyOk}／書架＋追回檔都生出來={aProjOk}"
                + $"／**續寫 segments=2 且不開 r2**={aAppendOk}／舊字未動＋續寫頭={aAppendBodyOk}"
                + $"／續寫不印關係標籤={aLabelOk}"
                + $"／🔴 續寫不存在的 round ⇒ 拒絕且磁碟零變動={aRefuseOk}"
                + $"／🔴 無 reader ⇒ 停下來不代建={aLadderOk}"
                + "　⚠ chapter.json 的期望值取自 Editor 真產物；續寫那段**全庫零活體**，這是它的第一份讀數",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally { try { if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true); } catch { } }
    }

    // 區塊職責：人物（facts／view 版本史）與書籤那一批的 clean room —— 暫存樹實跑。
    // ⭐ view 檔的期望值取自 Editor 真產物的形狀
    //   （`characters/Mujina/v1_2026-08-25.md`，2026-09-16 取樣）⇒ 對照組不同源。
    // ⚠ 本格最該守的不是「寫得出來」，是**寫不進去的那兩格**：
    //   人物已存在時 add_character 必須拒絕（覆寫 v1 抹掉的是「我當時還不知道」，事後補不回來），
    //   人物不存在時 revise_view 必須拒絕（不代建，否則「還沒記」會變成「記過而沒內容」）。
    static CheckRow LibraryCharacterCleanRoom()
    {
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_selftest_char_" + Guid.NewGuid().ToString("N")[..8]);
        var aLetters = new SCP_LettersRoot(Path.Combine(aRoot, "letters"));
        try
        {
            string aToday = SCP_LibraryIO.Today();
            SCP_LibraryInit.MediaInit(aLetters, aRoot, "w1", "book-w1", "book", "tester",
                "標題", "原題", "作者", 3, null, null, out _);
            string aCharDir = SCP_LibraryStore.CharacterDir(aRoot, "book-w1", "tester", "Mujina");

            // ① 建人物：profile.json 的 facts 一律**陣列**，v1 檔逐位元組
            string? aAdd = SCP_LibraryCharacter.AddCharacter(aLetters, aRoot, "book-w1", "tester",
                "Mujina", "狸貓長老", "むじな", "第一條\n第二條", "初版看法", out string? aAddErr);
            string aV1 = Path.Combine(aCharDir, $"v1_{aToday}.md");
            string aExpectV1 =
                "---\ncharacter_id: Mujina\nversion: 1\n" + $"date: {aToday}\n"
                + "reader_persona: tester\n---\n\n## tester 的看法（v1）\n\n初版看法\n";
            SCP_JsonData aProfile = SCP_JsonData.Parse(
                File.ReadAllText(Path.Combine(aCharDir, SCP_LibraryStore.ProfileJsonName), new UTF8Encoding(false)));
            bool aAddOk = aAdd != null && aAddErr == null && File.Exists(aV1)
                          && Eol(File.ReadAllText(aV1, new UTF8Encoding(false))) == Eol(aExpectV1)
                          && aProfile[SCP_LibraryIO.Key_Facts].IsArray
                          && aProfile[SCP_LibraryIO.Key_Facts].Count == 2;

            // ② 🔴 再建一次 ⇒ 拒絕，而且 v1 一個位元組都不動
            string aV1Before = File.ReadAllText(aV1, new UTF8Encoding(false));
            string? aDup = SCP_LibraryCharacter.AddCharacter(aLetters, aRoot, "book-w1", "tester",
                "Mujina", "X", null, "X", "X", out string? aDupErr);
            bool aDupOk = aDup == null && aDupErr != null
                          && aDupErr.Contains("op=revise_view", StringComparison.Ordinal)
                          && File.ReadAllText(aV1, new UTF8Encoding(false)) == aV1Before;

            // ③ 改觀 ⇒ fork v2（含「改觀觸發」段），v1 保留不動，facts 同步
            string? aRev = SCP_LibraryCharacter.ReviseView(aLetters, aRoot, "book-w1", "tester",
                "Mujina", "第二版看法", "讀到第 5 話", "只剩一條", out string? aRevErr);
            string aV2 = Path.Combine(aCharDir, $"v2_{aToday}.md");
            string aV2Text = File.Exists(aV2) ? File.ReadAllText(aV2, new UTF8Encoding(false)) : "";
            SCP_JsonData aProfile2 = SCP_JsonData.Parse(
                File.ReadAllText(Path.Combine(aCharDir, SCP_LibraryStore.ProfileJsonName), new UTF8Encoding(false)));
            bool aRevOk = aRev != null && aRevErr == null && File.Exists(aV2)
                          && aV2Text.Contains("> **改觀觸發**：讀到第 5 話", StringComparison.Ordinal)
                          && aV2Text.Contains("version: 2", StringComparison.Ordinal)
                          && File.ReadAllText(aV1, new UTF8Encoding(false)) == aV1Before   // v1 未動
                          && aProfile2[SCP_LibraryIO.Key_Facts].Count == 1;

            // ④ 🔴 對不存在的人物改觀 ⇒ 拒絕、不代建
            string? aGhost = SCP_LibraryCharacter.ReviseView(aLetters, aRoot, "book-w1", "tester",
                "NoSuchOne", "x", null, null, out string? aGhostErr);
            bool aGhostOk = aGhost == null && aGhostErr != null
                            && aGhostErr.Contains("op=add_character", StringComparison.Ordinal)
                            && !Directory.Exists(SCP_LibraryStore.CharacterDir(aRoot, "book-w1", "tester", "NoSuchOne"));

            // ⑤ 書籤：reader 三欄更新 ＋ 兩個投影重生成
            string aRecall = SCP_LettersPaths.CmdPayload(aLetters, "tester", "reading_recall", "book-w1");
            File.Delete(aRecall);
            string? aBm = SCP_LibraryCharacter.Bookmark(aLetters, aRoot, "book-w1", "tester",
                "讀到 0003", "目前看法 X", "completed", out string? aBmErr);
            SCP_JsonData aReader = SCP_JsonData.Parse(File.ReadAllText(
                SCP_LibraryStore.ReaderJsonPath(aRoot, "book-w1", "tester"), new UTF8Encoding(false)));
            bool aBmOk = aBm != null && aBmErr == null
                         && aReader[SCP_LibraryIO.Key_Progress].GetString(SCP_LibraryIO.Key_BookmarkNote, "") == "讀到 0003"
                         && aReader.GetString(SCP_LibraryIO.Key_CurrentImpression, "") == "目前看法 X"
                         && aReader.GetString(SCP_LibraryIO.Key_Status, "") == "completed"
                         && File.Exists(aRecall);   // 刪掉之後又被生回來 ⇒ 確實有重生成

            bool aOk = aAddOk && aDupOk && aRevOk && aGhostOk && aBmOk;
            return new CheckRow("人物／書籤 clean room（暫存樹實跑）",
                $"建人物＋v1 逐位元組＋facts 是陣列={aAddOk}"
                + $"／🔴 重建 ⇒ 拒絕且 v1 零變動={aDupOk}"
                + $"／改觀 fork v2＋改觀觸發段＋v1 未動＋facts 同步={aRevOk}"
                + $"／🔴 對不存在的人物改觀 ⇒ 拒絕不代建={aGhostOk}"
                + $"／書籤三欄＋追回檔重生成={aBmOk}"
                + "　⚠ v1 期望值取自 Editor 真產物 `characters/Mujina/v1_2026-08-25.md` 的形狀",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally { try { if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true); } catch { } }
    }

    /// <summary>頂層鍵**順序**一不一樣 —— 用來判「這份是不是現行 builder 一次寫成的」。</summary>
    static bool SameKeyOrder(SCP_JsonData iA, SCP_JsonData iB)
    {
        if (iA.Keys.Count != iB.Keys.Count) return false;
        for (int i = 0; i < iA.Keys.Count; i++)
            if (!string.Equals(iA.Keys[i], iB.Keys[i], StringComparison.Ordinal)) return false;
        return true;
    }

    static bool SameKeySet(SCP_JsonData iA, SCP_JsonData iB)
    {
        var aKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in iA.Keys) aKeys.Add(k);
        var bKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string k in iB.Keys) bKeys.Add(k);
        return aKeys.SetEquals(bKeys);
    }

    /// <summary>閱讀卡是不是比它的三個來源都新。判不出來 ⇒ 不當閘。</summary>
    static bool CardIsFresh(string iDataRoot, string iMediaId, string iPersona, string iCardPath)
    {
        try
        {
            DateTime aCardAt = File.GetLastWriteTimeUtc(iCardPath);
            DateTime aNewest = DateTime.MinValue;
            void Bump(string iPath)
            {
                if (!File.Exists(iPath)) return;
                DateTime t = File.GetLastWriteTimeUtc(iPath);
                if (t > aNewest) aNewest = t;
            }
            Bump(SCP_LibraryStore.ReaderJsonPath(iDataRoot, iMediaId, iPersona));
            string aMediaJson = SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId);
            Bump(aMediaJson);
            SCP_JsonData? aMedia = SCP_LibraryIO.LoadJson(aMediaJson, out _);
            string aWorkId = aMedia != null ? aMedia.GetString(SCP_LibraryIO.Key_WorkId, "") : "";
            if (aWorkId.Length > 0) Bump(SCP_LibraryStore.WorkJsonPath(iDataRoot, aWorkId));
            return aNewest != DateTime.MinValue && aCardAt >= aNewest;
        }
        catch { return false; }
    }

    /// <summary>讀 frontmatter 的一欄；缺欄回空字串（⛔ 不猜）。</summary>
    static string FrontmatterValue(string iText, string iKey)
    {
        foreach (string aLine in iText.Replace("\r\n", "\n").Split('\n'))
        {
            if (aLine == "---") continue;
            int aColon = aLine.IndexOf(':');
            if (aColon <= 0) continue;
            if (aLine.Substring(0, aColon).Trim() != iKey) continue;
            return aLine.Substring(aColon + 1).Trim();
        }
        return "";
    }

    /// <summary>把 `generated_at:` 整行拿掉 —— 它是本機時間，兩次渲染必然不同。</summary>
    static string StripGeneratedAt(string iText)
    {
        var aSb = new StringBuilder();
        foreach (string aLine in iText.Replace("\r\n", "\n").Split('\n'))
        {
            if (aLine.StartsWith("generated_at:", StringComparison.Ordinal)) continue;
            aSb.Append(aLine).Append('\n');
        }
        return aSb.ToString();
    }

    /// <summary>
    /// 這份追回檔是不是「比它全部來源都新」。
    /// <para>來源＝該 reader 目錄（遞迴）＋ media.json ＋ work.json。任何一個比它新 ⇒ 不新鮮
    /// ⇒ 重新渲染不一樣是**資料變了**，不是移植錯了。</para>
    /// </summary>
    static bool RecallIsFresh(string iDataRoot, string iMediaId, string iPersona, string iRecallPath)
    {
        try
        {
            DateTime aRecallAt = File.GetLastWriteTimeUtc(iRecallPath);
            DateTime aNewest = DateTime.MinValue;
            void Bump(string iPath)
            {
                if (File.Exists(iPath))
                {
                    DateTime t = File.GetLastWriteTimeUtc(iPath);
                    if (t > aNewest) aNewest = t;
                }
            }
            string aReaderRoot = SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona);
            if (Directory.Exists(aReaderRoot))
                foreach (string s in Directory.GetFiles(aReaderRoot, "*", SearchOption.AllDirectories)) Bump(s);
            string aMediaJson = SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId);
            Bump(aMediaJson);
            SCP_JsonData? aMedia = SCP_LibraryIO.LoadJson(aMediaJson, out _);
            string aWorkId = aMedia != null ? aMedia.GetString(SCP_LibraryIO.Key_WorkId, "") : "";
            if (aWorkId.Length > 0) Bump(SCP_LibraryStore.WorkJsonPath(iDataRoot, aWorkId));
            return aNewest != DateTime.MinValue && aRecallAt >= aNewest;
        }
        catch { return false; }   // 判不出來就**不當閘**（⛔ 不把「不知道」算成新鮮）
    }

    static IEnumerable<CheckRow> RealLibraryByteRoundTrip(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aLibrary = Path.Combine(p.AgentCommandsRoot, "BookNotes", "Library");
            if (!Directory.Exists(aLibrary)) continue;
            aAny = true;

            string[] aFiles = Directory.GetFiles(aLibrary, "*.json", SearchOption.AllDirectories);
            int aSame = 0, aBad = 0;
            var aUtf8 = new UTF8Encoding(false);
            foreach (string f in aFiles)
            {
                string aDisk;
                SCP_JsonData aTree;
                try
                {
                    aDisk = File.ReadAllText(f, aUtf8);
                    aTree = SCP_JsonData.Parse(aDisk);
                }
                catch { aBad++; continue; }
                string aBack = SCP_JsonWriter.Write(aTree, SCP_JsonStyle.UclLegacy) + "\n";
                if (Eol(aDisk) == Eol(aBack)) aSame++;
            }

            yield return new CheckRow(
                $"閱讀庫逐位元組對拍（{p.Name}）",
                $"{aFiles.Length} 份／parse 不動 {aBad}／UclLegacy 寫回相符 {aSame}"
                + $"（行尾無關；⚠ 相符份數是**讀數不是閘** —— 磁碟是三支 writer 的沉積，"
                + "而行尾由 core.autocrlf 決定，不歸 writer 管）",
                aBad == 0 ? CheckResult.Pass : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("閱讀庫逐位元組對拍",
                "找不到樣本（沒有可用專案或該專案沒有 BookNotes/Library）—— **這是跳過，不是通過**",
                CheckResult.Skipped);
    }

    static string Eol(string iS) { return iS.Replace("\r\n", "\n"); }

    /// <summary>
    /// 拿**真的、由 Unity 端 UCL JsonData 寫出來的檔**過一遍：讀 → 寫 → 再讀，
    /// 兩次的樹必須等價（逐 key 比較），而且第一次就要讀得到預期的欄位。
    /// </summary>
    static IEnumerable<CheckRow> RealFileRoundTrip(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aFile = Path.Combine(p.AgentCommandsRoot, "commands_schema.json");
            if (!File.Exists(aFile)) continue;
            aAny = true;

            string aText = File.ReadAllText(aFile);
            SCP_JsonData? aRoot = null;
            string aParseError = "";
            // yield 不能寫在 catch 裡（CS1631）⇒ 先把結果收進變數，離開 try 之後才 yield
            try { aRoot = SCP_JsonData.Parse(aText); }
            catch (SCP_JsonParseException e) { aParseError = e.Message; }
            if (aRoot == null)
            {
                yield return new CheckRow($"讀真檔（{p.Name}）", $"解析失敗：{aParseError}", CheckResult.Fail);
                continue;
            }

            int aCmdCount = aRoot["commands"].Count;
            string aGen = aRoot.GetString("generator", "(沒有這個欄位)");
            string aOut = aRoot.ToJson();
            SCP_JsonData aAgain = SCP_JsonData.Parse(aOut);
            bool aSame = Equivalent(aRoot, aAgain);

            yield return new CheckRow(
                $"讀真檔（{p.Name}）",
                $"{Path.GetFileName(aFile)}：{aText.Length} 字元／commands={aCmdCount}／generator={aGen}／"
                + $"寫回再讀等價={aSame}",
                aSame && aCmdCount > 0 ? CheckResult.Pass : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("讀真檔",
                "找不到樣本（沒有可用專案或該專案沒有 commands_schema.json）—— **這是跳過，不是通過**",
                CheckResult.Skipped);
    }

    // 區塊職責：拿**真的信件庫**掃一次 —— 移植（System.Text.Json → SCP_Json）之後最重要的一格。
    // 物理意義：共用層的第一個責任是「讀得懂既有資料」，而那些 lock json 是 awakening 端寫的。
    //          ⇒ 驗收方式是拿真檔案跑，不是自己造樣本（同 SCP_Json 當初的驗收）。
    // ⚠ 找不到樣本回 Skipped **不是 Pass** —— 「沒測」與「測過而且對」同形是這裡最貴的錯。
    // 📌 三態那條也在這裡驗：掃到 0 個人 ⇒ Fail（信件庫應該有人），
    //    而「量不到」要能從 Problems 看出來，不可以靜靜變成「全體離線」。
    static IEnumerable<CheckRow> RealPersonaScan(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            var aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(p.AgentCommandsRoot));
            if (!Directory.Exists(aLetters.Value)) continue;
            aAny = true;

            SCP_PersonaScan aScan = SCP_PersonaLetters.Scan(aLetters.Value);

            // 「量不到」必須看得出來：Unknown 只可能來自 Problems 有話說
            bool aThreeStateHonest = aScan.UnknownCount == 0 || aScan.Problems.Count > 0;
            bool aFound = aScan.Enumerated && aScan.Personas.Count > 0;
            string aProblems = aScan.Problems.Count == 0 ? "無" : string.Join("；", aScan.Problems);

            yield return new CheckRow(
                $"真信件庫掃描（{p.Name}）",
                $"persona={aScan.Personas.Count}（線上 {aScan.OnlineCount}／離線 {aScan.OfflineCount}／未知 {aScan.UnknownCount}）"
                + "／lock=<p>/profile/" + SCP_LettersPaths.SessionLockFileName
                + $"／Unknown 有交代={aThreeStateHonest}／problems：{aProblems}",
                aFound && aThreeStateHonest ? CheckResult.Pass : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("真信件庫掃描",
                "找不到樣本（沒有可用專案或該專案沒有 letters 目錄）—— **這是跳過，不是通過**",
                CheckResult.Skipped);
    }


    // 區塊職責：活動 session 的四個行為 —— 未知鍵保留／跨 kind 擋覆蓋／同 kind 不擋／關場三本帳分開回報。
    // 物理意義：這四格全部是**沒有畫面的**：出錯時沒有紅字，只有「別人的欄位不見了」「別人的場被覆蓋了」
    //          「結算失敗被講成關場失敗」。⇒ 它們只能長在這裡，不能靠人記得試。
    // 數值影響：寫在暫存根，跑完刪掉；不碰任何真資料。
    static CheckRow ActivitySessionBehaviour()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_selftest_session_" + Guid.NewGuid().ToString("N")[..8]);
        var aRoot = new SCP_DataRoot(aTmp);
        try
        {
            SCP_ActivitySessionGatewayHost.ClearForTest();
            Directory.CreateDirectory(SCP_ActivitySessionStore.Dir(aRoot));

            // ① 未知鍵保留：手寫一份帶 kind 專屬欄位的檔（真檔就長這樣：rounds / activities_done / activity）
            string aPath = SCP_ActivitySessionStore.PathOf(aRoot, "probe") ?? "";
            File.WriteAllText(aPath,
                "{\"rounds\":3,\"activity\":\"canvas-2d\",\"persona\":\"probe\",\"kind\":\"FreeTime\","
                + "\"session_id\":\"ft-x\",\"end_ts\":\"2099-01-01T00:00:00.000Z\",\"active\":true}",
                new UTF8Encoding(false));
            var aLoaded = SCP_ActivitySessionStore.Load(aRoot, "probe");
            bool aReadOk = aLoaded != null && aLoaded.kind == "FreeTime" && aLoaded.active && aLoaded.session_id == "ft-x";
            SCP_ActivitySessionStore.Save(aRoot, "probe", aLoaded!);
            string aBack = File.ReadAllText(aPath, Encoding.UTF8);
            bool aKeepUnknown = aBack.Contains("\"rounds\"", StringComparison.Ordinal)
                                && aBack.Contains("canvas-2d", StringComparison.Ordinal);

            // ② 跨 kind 擋覆蓋（TASK-0056 那個洞的守衛）：probe 正在 FreeTime，開 StreamWatch 應被擋
            var aNew = new SCP_ActivitySession { persona = "probe", session_id = "sw-x", active = true,
                end_ts = "2099-01-01T00:00:00.000Z" };
            bool aBlocked = !SCP_ActivitySessionStore.TryStart(aRoot, "probe", aNew,
                SCP_ActivitySessionKind.StreamWatch, DateTime.Now, out var aBlocker);
            bool aBlockerNamed = aBlocker != null && aBlocker.kind == "FreeTime" && aBlocker.session_id == "ft-x";
            // ⭐ 反向對照：擋下之後**原本那份必須原封不動**（只驗「有沒有擋」的話，一個擋完仍覆蓋的實作也會通過）
            var aStill = SCP_ActivitySessionStore.Load(aRoot, "probe");
            bool aVictimAlive = aStill != null && aStill.kind == "FreeTime" && aStill.session_id == "ft-x";

            // ③ 同 kind 不擋（那由各 kind 自己的守衛管，本層不重造）
            var aSame = new SCP_ActivitySession { persona = "probe", session_id = "ft-y", active = true,
                end_ts = "2099-01-01T00:00:00.000Z" };
            bool aSameOk = SCP_ActivitySessionStore.TryStart(aRoot, "probe", aSame,
                SCP_ActivitySessionKind.FreeTime, DateTime.Now, out _);

            // ④ 關場三本帳：沒有 handler ⇒ 關得掉且**明說沒有 handler**（不是靜默成功）
            var aCur = SCP_ActivitySessionStore.Load(aRoot, "probe")!;
            var aNoGate = SCP_ActivitySessionStore.CloseWithSettlement(aRoot, "probe", aCur, "selftest");
            bool aNoGateOk = aNoGate.Closed && !aNoGate.HasHandler && !aNoGate.Settled
                             && SCP_ActivitySessionStore.Load(aRoot, "probe")!.active == false;

            // ⑤ gateway 炸掉 ⇒ **不留半關的場**，而且判定看的是**回讀**不是 gateway 說什麼。
            //   🩸 語意 2026-09-04 改過：gateway 從「只結算」變成「整步關場」（權威狀態由那一端寫）
            //   ⇒ 它炸掉時本層**不自己補寫** —— 補寫就是第二個寫入端，而那正是 TASK-0100 的主題。
            //   ⇒ 要驗的因此不是「場仍關閉」，是「**磁碟上那份沒有被動過**」（沒有半關的狀態）。
            SCP_ActivitySessionGatewayHost.Register(new ThrowingGate());
            var aBoom = new SCP_ActivitySession { persona = "probe2", kind = SCP_ActivitySessionKind.FreeTime,
                session_id = "ft-z", active = true, end_ts = "2099-01-01T00:00:00.000Z" };
            SCP_ActivitySessionStore.Save(aRoot, "probe2", aBoom, SCP_ActivitySessionKind.FreeTime);
            var aRes = SCP_ActivitySessionStore.CloseWithSettlement(aRoot, "probe2", aBoom, "selftest");
            var aAfterBoom = SCP_ActivitySessionStore.Load(aRoot, "probe2");
            bool aSplitLedger = !aRes.Closed && aRes.HasHandler && !aRes.ClosedByGateway
                                && aRes.SettleError.Length > 0
                                && aAfterBoom != null && aAfterBoom.active && aAfterBoom.ended_at.Length == 0;

            bool aOk = aReadOk && aKeepUnknown && aBlocked && aBlockerNamed && aVictimAlive && aSameOk
                       && aNoGateOk && aSplitLedger;
            return new CheckRow("活動 session 行為",
                $"讀真形狀={aReadOk}／未知鍵保留={aKeepUnknown}／跨 kind 擋下={aBlocked}（點名擋你的那場={aBlockerNamed}）"
                + $"／被擋後原場沒被覆蓋={aVictimAlive}／同 kind 不擋={aSameOk}／無 gateway 走 base close={aNoGateOk}"
                + $"／gateway 炸掉不留半關的場={aSplitLedger}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally
        {
            SCP_ActivitySessionGatewayHost.ClearForTest();
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { }
        }
    }

    /// <summary>只會爆炸的關場 gateway —— 驗「gateway 炸掉時，判定看的是回讀不是它說什麼」。</summary>
    sealed class ThrowingGate : SCP_IActivitySessionCloseGateway
    {
        public string Kind => SCP_ActivitySessionKind.FreeTime;
        public bool TryClose(SCP_ActivitySession iSession, string iReason, List<string> oLines, out string oError)
            => throw new InvalidOperationException("關場故意炸掉（selftest）");
    }

    // ===========================================================
    // 區塊職責：**子類別** round-trip —— 各 kind 的宿主用 typed 子類別讀寫同一份檔（TASK-0127 ⑦ 的機制）。
    // 物理意義：⑦ 把 UCL 那側的 typed model（`UCL_FreeTimeSession` / `UCL_StreamWatchSession`）
    //          的基底換成 `SCP_ActivitySession`，於是那 33 個 typed 欄位由 `SCP_JsonMapper` 進出。
    //          這一格要擋的是**那個換基底如果錯了會怎麼死**：不是編譯錯，是欄位安靜消失。
    // 🩸 為什麼它值一格永久的驗收：2026-09-04 上午的活體就是「讀成比檔案窄的型別 → 改幾欄 → 寫回」
    //    吃掉 `rounds`／`activity`，而工具回 `closed=1`、零紅字。⇒ 那條路現在有機器在看。
    // 數值影響：寫在暫存根，跑完刪掉；不碰任何真資料。
    // ===========================================================
    static CheckRow ActivitySessionSubclassRoundTrip()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_selftest_subsession_" + Guid.NewGuid().ToString("N")[..8]);
        var aRoot = new SCP_DataRoot(aTmp);
        try
        {
            SCP_ActivitySessionGatewayHost.ClearForTest();
            Directory.CreateDirectory(SCP_ActivitySessionStore.Dir(aRoot));

            // ① 子類別寫出去 ⇒ 專屬欄位**真的落在檔案裡**，而且 bool 是**原生 bool** 不是 "True" 字串。
            //    （UCL 那側原本為此各寫一個 SerializeToJson override；換基底之後由函式庫滿足，
            //      而「由誰滿足」如果沒有人在量，下一個人會以為它從來沒有人在意過。）
            var aWrite = new ProbeKindSession
            {
                persona = "probe", session_id = "ft-sub", active = true,
                end_ts = "2099-01-01T00:00:00.000Z",
                rounds = 7, activity = "canvas-2d", note_written = true,
            };
            bool aSaved = SCP_ActivitySessionStore.Save(aRoot, "probe", aWrite, SCP_ActivitySessionKind.FreeTime);
            string aPath = SCP_ActivitySessionStore.PathOf(aRoot, "probe") ?? "";
            string aText = File.ReadAllText(aPath, Encoding.UTF8);
            bool aTypedOut = aText.Contains("\"rounds\"", StringComparison.Ordinal)
                             && aText.Contains("canvas-2d", StringComparison.Ordinal);
            // ⚠ 量的是「沒有引號包住的 true」——`"True"` 與 `true` 在「有沒有這個字」的問法下同形。
            bool aNativeBool = aText.Contains("\"note_written\": true", StringComparison.Ordinal)
                               && !aText.Contains("\"True\"", StringComparison.OrdinalIgnoreCase);
            bool aNoRawLeak = !aText.Contains("\"Raw\"", StringComparison.Ordinal)
                              && !aText.Contains("RawJson", StringComparison.Ordinal);

            // ② 讀回**同一個子類別** ⇒ typed 欄位拿到原值（不是預設值 —— 預設值跟「沒設過」同形）。
            var aTyped = SCP_ActivitySessionStore.Load<ProbeKindSession>(
                aRoot, "probe", SCP_ActivitySessionKind.FreeTime);
            bool aTypedBack = aTyped != null && aTyped.rounds == 7 && aTyped.activity == "canvas-2d"
                              && aTyped.note_written && aTyped.session_id == "ft-sub";

            // ③ ⭐ 反向對照（這一格才是 09-04 那隻）：**讀成基底**（管理頁／關場路徑走的就是這條）
            //    → 動共通欄位 → 寫回 ⇒ 子類別的專屬欄位**必須還在**。
            //    只驗 ①② 的話，一個「基底寫回就吃鍵」的實作也會通過這個 check。
            var aBase = SCP_ActivitySessionStore.Load(aRoot, "probe");
            bool aBaseIsNotSubclass = aBase != null && aBase.GetType() == typeof(SCP_ActivitySession);
            SCP_ActivitySessionStore.Close(aRoot, "probe", aBase!, "selftest");
            var aAfter = SCP_ActivitySessionStore.Load<ProbeKindSession>(aRoot, "probe");
            bool aSurvived = aAfter != null && aAfter.rounds == 7 && aAfter.activity == "canvas-2d"
                             && aAfter.note_written && !aAfter.active && aAfter.ended_at.Length > 0;

            bool aOk = aSaved && aTypedOut && aNativeBool && aNoRawLeak
                       && aTypedBack && aBaseIsNotSubclass && aSurvived;
            return new CheckRow("活動 session 子類別 round-trip",
                $"子類別欄位落檔={aTypedOut}／bool 原生={aNativeBool}／無 Raw 外洩={aNoRawLeak}"
                + $"／讀回 typed 原值={aTypedBack}／基底讀的真是基底={aBaseIsNotSubclass}"
                + $"／**基底寫回不吃專屬欄位**={aSurvived}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally
        {
            SCP_ActivitySessionGatewayHost.ClearForTest();
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { }
        }
    }

    /// <summary>假的 kind 專屬子類別 —— 模仿 UCL 那側的 <c>UCL_FreeTimeSession</c>（rounds/activity）
    /// 與 <c>UCL_StreamWatchSession</c>（bool 欄位）各取一格，欄位形狀與真的那兩個同族。</summary>
    sealed class ProbeKindSession : SCP_ActivitySession
    {
        public int rounds = 0;
        public string activity = "";
        public bool note_written = false;
    }

    // 區塊職責：小歇記憶信的**形狀**（`SCP_LetterWriter`）—— 兩個寫入端並存時唯一擋得住漂移的那一格。
    // 物理意義：python `awakening.py write_letter` 與本層寫的是同一種檔。形狀分岔的症狀
    //          **不是解析失敗**（那會喊），是讀信的人拿到預設值 ⇒ 只能靠對拍。
    // ⚠ 四格裡有兩格是反向對照：空 body 要**一個位元組都不寫**、
    //   不該修的字面 \n 要**原封不動**。只驗正向的話，
    //   一個「永遠寫檔」或「無腦全域替換」的實作也會全綠。
    static CheckRow RestLetterShape()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_selftest_rest_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aLetters = Path.Combine(aTmp, "letters").Replace('\\', '/');
            string aPersona = "Probe";
            Directory.CreateDirectory(Path.Combine(aLetters, aPersona));

            // ① 正向：寫得成，而且 `_latest.md` 與信本體**逐位元組相同**（不是「存在」）
            var aR1 = SCP_LetterWriter.WriteSelfLetter(aLetters, aPersona, "probe-bank", "第一段\n\n第二段");
            string aBody1 = File.ReadAllText(aR1.Path);
            bool aWrote = File.Exists(aR1.Path) && aBody1.Contains("trigger: cmd_rest")
                          && aBody1.Contains("written_by_persona: " + aPersona)
                          && aBody1.Contains("type: letter_to_future_self");
            bool aLatestSame = File.Exists(aR1.LatestPath)
                               && File.ReadAllBytes(aR1.LatestPath).Length == File.ReadAllBytes(aR1.Path).Length
                               && File.ReadAllText(aR1.LatestPath) == aBody1;

            // ② 作者自己也寫了 frontmatter ⇒ 併入，**不疊第二坨**；同名欄留 `_as_written`
            var aR2 = SCP_LetterWriter.WriteSelfLetter(aLetters, aPersona, "probe-bank",
                "---\ntype: hand_written\nintended_reader: 明天的我\n---\n\n內文",
                iNowUtc: DateTime.UtcNow.AddSeconds(1));
            string aBody2 = File.ReadAllText(aR2.Path);
            int aFmCount = CountText(aBody2, "---");
            bool aMerged = aBody2.Contains("intended_reader: 明天的我")
                           && aBody2.Contains("type_as_written: hand_written")   // 作者版留痕
                           && aBody2.Contains("type: letter_to_future_self")     // 機器版勝出
                           && aFmCount == 2;                                     // 只有一份 frontmatter

            // ③ 反向對照：空 body ⇒ 丟例外，而且 rests/ **沒有多出任何檔**
            int aBefore = Directory.GetFiles(Path.Combine(aLetters, aPersona, "rests")).Length;
            bool aRefused = false;
            try { SCP_LetterWriter.WriteSelfLetter(aLetters, aPersona, "probe-bank", "   "); }
            catch (ArgumentException) { aRefused = true; }
            int aAfter = Directory.GetFiles(Path.Combine(aLetters, aPersona, "rests")).Length;
            bool aNothingWritten = aRefused && aAfter == aBefore;

            // ④ 反向對照：字面換行只在**兩個門檻同時成立**時才修
            SCP_LetterWriter.NormalizeEscapedNewlines("一行\\n二行\\n三行", out bool aFixedHit);
            SCP_LetterWriter.NormalizeEscapedNewlines("真的換行很多\n\n\n而這裡只是在講 \\n 這個符號", out bool aFixedMiss);
            bool aNewlineRule = aFixedHit && !aFixedMiss;

            // ⑤ 現地定語兩欄（TASK-0134 QA 抓到的那格）—— **正反都要**：
            //    給了 ⇒ 寫進去的是那個值；沒給 ⇒ 欄位**仍在**而值是 unstated。
            //    🩸 只驗「有 region 欄」的話，一個永遠寫 unstated 的實作會全綠；
            //    只驗「給了有值」的話，沒給時整欄消失也會全綠 —— 而後者正是這隻 bug 的原形
            //    （少欄的信會被讀成「2026-09-02 之前的舊信」，而它是今天寫的）。
            var aR3 = SCP_LetterWriter.WriteSelfLetter(aLetters, aPersona, "probe-bank", "帶定語",
                iNowUtc: DateTime.UtcNow.AddSeconds(2),
                iRegion: "PROBEREGION", iDataRoot: "/tmp/ProbeProject/AgentCommands");
            string aBody3 = File.ReadAllText(aR3.Path);
            bool aQualifiedGiven = aBody3.Contains("region: PROBEREGION")
                                   && aBody3.Contains("project: ProbeProject");
            var aR4 = SCP_LetterWriter.WriteSelfLetter(aLetters, aPersona, "probe-bank", "沒有定語",
                iNowUtc: DateTime.UtcNow.AddSeconds(3));
            string aBody4 = File.ReadAllText(aR4.Path);
            bool aQualifiedMissing = aBody4.Contains("region: unstated")
                                     && aBody4.Contains("project: unstated");
            bool aQualifiers = aQualifiedGiven && aQualifiedMissing;

            bool aOk = aWrote && aLatestSame && aMerged && aNothingWritten && aNewlineRule && aQualifiers;
            return new CheckRow("小歇記憶信形狀（SCP_LetterWriter）",
                $"落檔＋機器欄={aWrote}／`_latest` 與本體逐位元組同={aLatestSame}"
                + $"／作者 frontmatter 併入不疊第二坨={aMerged}"
                + $"／**空 body 一個位元組都不寫**={aNothingWritten}"
                + $"／**字面換行只在門檻內才修**={aNewlineRule}"
                + $"／**現地定語正反兩格**（給了寫值={aQualifiedGiven}／沒給仍留欄位＝unstated={aQualifiedMissing}）",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        finally
        {
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { }
        }
    }

    // 區塊職責：發文判定的**三態不同形**（`SCP_TavernPostVerdict`）。
    // 物理意義：🩸 TASK-0134 QA（summit 2026-09-05）拿到「廣播沒發」而**廣播其實成功了**
    //          （Editor 開著、post_seq 19082）—— 真實語意是「CLI 沒等到回執」。
    //          兩者的處置**相反**：真沒發要補發；沒等到去補發＝在全域遞增的 seq 上多出第二則。
    // ⚠ 本格量的是**型別層**（三態分不分得開），不是活體。活體那格要真的讓廣播逾時，
    //   而那需要關 Editor ⇒ 它是 QA 的事，⛔ 這裡不假裝量到了。
    static CheckRow TavernPostVerdictThreeStates()
    {
        var aGood = SCP_TavernPostVerdict.Good("seq=1", "1");
        var aBad = SCP_TavernPostVerdict.Bad("宿主回報失敗");
        var aUnknown = SCP_TavernPostVerdict.Unknown("沒等到回執", "cat /tmp/probe.json");

        // ① 三個 Outcome 兩兩不同 —— 這是「分得開」的直接讀數
        bool aDistinct = aGood.Outcome == SCP_TavernPostOutcome.Posted
                         && aBad.Outcome == SCP_TavernPostOutcome.NotPosted
                         && aUnknown.Outcome == SCP_TavernPostOutcome.Unresolved;
        // ② 反向對照：`Posted` 這個 bool **分不開**後兩態 —— 把它記進讀數，
        //    是為了讓下一個想「用 Posted 判斷要不要補發」的人在這裡看到為什麼不行。
        bool aBoolCannotTell = !aBad.Posted && !aUnknown.Posted;
        // ③ 未定態**必須**帶得走一行可貼的回讀指令；確定沒發的那態不需要（它要的是補發指令）
        bool aHintOnlyWhenUnknown = aUnknown.RecheckHint.Length > 0
                                    && aBad.RecheckHint.Length == 0
                                    && aGood.RecheckHint.Length == 0;
        // ④ 未定 ⇒ **沒有 seq**（有 seq 就不叫未定了）
        bool aNoSeqWhenUnknown = aUnknown.Seq.Length == 0 && aGood.Seq.Length > 0;

        bool aOk = aDistinct && aBoolCannotTell && aHintOnlyWhenUnknown && aNoSeqWhenUnknown;
        return new CheckRow("發文判定三態不同形（SCP_TavernPostVerdict）",
            $"三態兩兩可分={aDistinct}／**bool `Posted` 分不開「沒發」與「不知道」**={aBoolCannotTell}"
            + $"／回讀指令只掛在未定態={aHintOnlyWhenUnknown}／未定態無 seq={aNoSeqWhenUnknown}"
            + "　⚠ 這是型別層讀數；**逾時活體要關 Editor 才量得到**（未量）",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    static int CountText(string iText, string iNeedle)
    {
        int aCount = 0, aAt = 0;
        while ((aAt = iText.IndexOf(iNeedle, aAt, StringComparison.Ordinal)) >= 0) { ++aCount; aAt += iNeedle.Length; }
        return aCount;
    }

    // 區塊職責：拿**真的** session 檔跑 round-trip —— 讀得回來、而且寫回去不會吃掉別人的欄位。
    // 物理意義：這些檔是 Unity 那側寫的（TASK-0127 之後兩邊共用同一份）。
    //          「能不能讀」不是單元測試問題，是拿真檔案去試的問題 —— 找不到樣本回**跳過**，不是通過。
    // ⚠ 純讀：複製到暫存根再寫，**絕不碰原檔**。
    // 區塊職責：拿**真的**實錄台帳跑一次 C# 版讀取（TASK-0143 ⑤ 的移植第一刀）。
    // 物理意義：這一層是從 `library.py` 移過來的，而移植的判準是「**行為一樣**」不是「編得過」。
    //          ⇒ 本格取的是**異源讀數**：同一份 `sessions_log.jsonl`，C# 自己數一次，
    //            拿去跟 python `_sessions_log_state()` 的數字並排（python 那半在單子留言上）。
    // 🩸 為什麼要驗「export 事件不會憑空造出一場」：那是移植時最容易寫錯的一格 ——
    //    python 是 `if sid in state`，照抄成 `state[sid] = ...` 就會讓孤兒事件長出一個沒有區間的場，
    //    而它在計數上跟真的場**完全同形**。
    // ⚠ 純讀：一個位元組都不寫（AppendExportEvents **不在本格**，它要寫真台帳，不能拿真檔驗）。
    // 區塊職責：拿**台帳裡的每一場**跑一次 C# 版反查，壓成一個指紋（md5）跟 python 版對拍。
    // 物理意義：這一格是移植的**全量**驗收 —— 不是抽樣、不是計數，是「103 場逐場逐欄位」。
    //          規範格式兩邊寫死成同一句：`sid|media|lib|chapter|title|work|R:區間|S:場次`，
    //          任何一欄漂掉、任何一場的區間或場次**順序**不同，md5 就不會一樣。
    // ⭐ 為什麼用指紋而不是逐筆比：指紋讓「哪裡不一樣」變成一個**必須去查**的問題，
    //   而逐筆比很容易被寫成「差異只有 N 筆，看起來還好」。⇒ 它只有兩種答案。
    // ⚠ 純讀。反查一個位元組都不寫（寫入端是 AppendExportEvents，不在本格）。
    static IEnumerable<CheckRow> RealWatchResolveFingerprint(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            if (!File.Exists(SCP_WatchLedger.SessionsLogPath(p.AgentCommandsRoot))) continue;
            aAny = true;

            var aWarn = new List<string>();
            var aState = SCP_WatchLedger.SessionsLogState(p.AgentCommandsRoot, aWarn);
            // 哨兵值由**宿主**供給（本層不自己讀設定，同 SCP_WakeBrief 對 region 的契約）。
            string aMarker = "##None##";
            string aSettings = Path.Combine(p.AgentCommandsRoot, "StreamWatch", "settings.json");
            if (File.Exists(aSettings))
            {
                try
                {
                    string aV = SCP_JsonData.Parse(File.ReadAllText(aSettings, Encoding.UTF8))
                                            .GetString("untitled_marker", "").Trim();
                    if (aV.Length > 0) aMarker = aV;
                }
                catch { /* 讀不動 ⇒ 用地板值；python 那側同一條退路 */ }
            }

            var aSids = new List<string>(aState.Keys);
            aSids.Sort(StringComparer.Ordinal);
            var aSb = new StringBuilder();
            int aOk = 0, aErr = 0;
            for (int i = 0; i < aSids.Count; ++i)
            {
                var aLines = new List<string>();
                var r = SCP_WatchResolve.FromSession(p.AgentCommandsRoot, aSids[i], null, aMarker, aLines);
                if (i > 0) aSb.Append('\n');
                if (r.Error.Length > 0) { ++aErr; aSb.Append(aSids[i]).Append("|ERR"); continue; }
                ++aOk;
                var aRng = new StringBuilder();
                for (int k = 0; k < r.Ranges.Count; ++k)
                { if (k > 0) aRng.Append(','); aRng.Append(r.Ranges[k].ToString()); }
                aSb.Append(aSids[i]).Append('|').Append(r.Media).Append('|').Append(r.LibraryMediaId)
                   .Append('|').Append(r.Chapter).Append('|').Append(r.LedgerTitle)
                   .Append('|').Append(r.LedgerWorkTitle).Append("|R:").Append(aRng)
                   .Append("|S:").Append(string.Join(",", r.Sessions));
            }
            string aMd5;
            using (var aHash = System.Security.Cryptography.MD5.Create())
            {
                byte[] aBytes = aHash.ComputeHash(new UTF8Encoding(false).GetBytes(aSb.ToString()));
                var aHex = new StringBuilder(32);
                foreach (byte b in aBytes) aHex.Append(b.ToString("x2"));
                aMd5 = aHex.ToString();
            }

            // ⚠ 這個常數是**python 那側算出來的**（`_resolve_from_session` 全量，2026-09-06）。
            //   ⛔ 它不是「期望值」是**對照組**：哪天 python 那側改了行為，這一格會紅 ——
            //   而那正是我要的：兩個實作分岔時，我要它當場喊，不是等產物出錯才發現。
            // 對照組更新紀錄（⚠ 每次更新都要寫清楚**這個值是從哪一側算來的**）：
            //   2026-09-06 早　103 場　`5897bf6df16cf9a2b10b2fca29ebdef2`
            //   2026-09-06 晚　108 場　`9719f72abe60f95558194701df487ee3`
            //     ← 當晚觀影把台帳推到 108 場，**在 python 那側對同一份台帳重算**（`_resolve_from_session` 全量）
            //       ⇒ 兩側仍然逐場逐欄位相同。⛔ 不是把 C# 的值抄過來，那樣對照組就變成自己抄自己。
            const string aPythonMd5 = "9719f72abe60f95558194701df487ee3";
            const int aPythonSessions = 108;   // ⭐ 對照組是在**這個場次數**上算出來的

            // 🩸 寫下它的**當天晚上**就踩到（2026-09-06）：一場觀影把台帳推到 108 場，
            //   這一格立刻紅 —— 而它紅的原因不是兩個實作分岔，是**資料集長大了**。
            //   ⇒ 一個「每次有人看片就會紅」的測試，最後會被所有人忽略，
            //     而被忽略的紅燈跟綠燈一樣沒有攔截力。
            //   ⇒ 場次數對不上時回 **Skipped（未量）**，⛔ 不是 Pass 也不是 Fail：
            //     「這次沒得比」與「比過而且一樣」必須長得不一樣。
            if (aSids.Count != aPythonSessions)
            {
                yield return new CheckRow($"觀影反查全量對拍（{p.Name}）",
                    $"場次 **{aSids.Count}**（解得出 {aOk}／錯 {aErr}）／C# md5 `{aMd5}`"
                    + $"　⚠ **對照組是 {aPythonSessions} 場時算的（2026-09-06），場次數已變 ⇒ 這次沒得比**"
                    + "　（要恢復對拍：在 python 那側對**同一份台帳**重算一次指紋再更新這兩個常數；"
                    + "⛔ 不要只把 md5 改成 C# 現在算出來的值 —— 那會讓對照組變成自己抄自己）",
                    CheckResult.Skipped);
                continue;
            }

            bool aMatch = string.Equals(aMd5, aPythonMd5, StringComparison.Ordinal);
            yield return new CheckRow($"觀影反查全量對拍（{p.Name}）",
                $"場次 **{aSids.Count}**（解得出 {aOk}／錯 {aErr}）／C# md5 `{aMd5}`"
                + $"／python md5 `{aPythonMd5}` ⇒ **逐場逐欄位相同={aMatch}**"
                + "　（規範格式 `sid|media|lib|chapter|title|work|R:區間|S:場次`，"
                + "任一欄或任一順序漂掉都不會同號）",
                aMatch && aOk > 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        if (!aAny)
            yield return new CheckRow("觀影反查全量對拍",
                "找不到任何專案的 `StreamWatch/sessions_log.jsonl` ⇒ **跳過**（⛔ 不當成通過）",
                CheckResult.Skipped);
    }

    // 區塊職責：把**磁碟上真的章**用 C# 版重出一次，逐位元組比。
    // 物理意義：章的表頭是機械產物，它自己就寫著當初的參數（媒材／區間／章名／作品／場次／備註）
    //          ⇒ 拿它當輸入重跑，就是一次**不需要任何人記得參數**的重現實驗。
    // ⭐ 判準只認**最新那一章**：舊章可能是更早版本的 python 排出來的，
    //   它們不符不代表移植錯（那是「舊快照」不是「壞掉」）。⇒ 其餘章只報數字不判定。
    // ⚠ 純讀：重出的結果只留在記憶體裡比對，**一個位元組都不寫回 Books/**。
    static IEnumerable<CheckRow> RealWatchChapterRebuild(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aBooks = Path.Combine(p.AgentCommandsRoot, "Books");
            if (!Directory.Exists(aBooks)) continue;

            var aFiles = new List<string>();
            foreach (string aDir in Directory.GetDirectories(aBooks, "watch-*"))
                foreach (string aF in Directory.GetFiles(aDir, "???.txt"))
                {
                    string aStem = Path.GetFileNameWithoutExtension(aF);
                    if (aStem.Length == 3 && int.TryParse(aStem, out _)) aFiles.Add(aF);
                }
            if (aFiles.Count == 0) continue;
            aAny = true;
            aFiles.Sort((x, y) => File.GetLastWriteTimeUtc(y).CompareTo(File.GetLastWriteTimeUtc(x)));

            // ⚠ 判定只認最新那章 ⇒ **先問它在不在本區的 seq 軸上**。
            //   不問的話，一章別區產的實錄會讓整格變紅，而紅的理由跟「移植壞了」同形。
            {
                string aTop = File.ReadAllText(aFiles[0], Encoding.UTF8).Replace("\r\n", "\n");
                if (TryParseChapterHeader(aTop, out _, out List<SCP_SeqRange> aTopRanges,
                                          out _, out _, out _, out _, out _))
                {
                    string? aOut = WhyOutOfThisRegion(p.AgentCommandsRoot, aTopRanges);
                    if (aOut != null)
                    {
                        yield return new CheckRow($"觀影章重出對拍（{p.Name}）",
                            $"最新那章 `{Path.GetFileName(Path.GetDirectoryName(aFiles[0]))}/"
                            + $"{Path.GetFileName(aFiles[0])}`：{aOut}"
                            + $" ⇒ **跳過**（⛔ 不當成通過）。全庫共 {aFiles.Count} 章，本次一章都沒判",
                            CheckResult.Skipped);
                        continue;
                    }
                }
            }

            int aMatch = 0, aDiff = 0, aSkip = 0;
            var aMatched = new List<string>();
            bool aNewestOk = false; string aNewestName = ""; string aNewestWhy = "";
            for (int i = 0; i < aFiles.Count; ++i)
            {
                string aF = aFiles[i];
                string aWant = File.ReadAllText(aF, Encoding.UTF8).Replace("\r\n", "\n");
                if (!TryParseChapterHeader(aWant, out string aMedia, out List<SCP_SeqRange> aRanges,
                                           out string aTitle, out string aSub, out string aWork,
                                           out string aSessions, out string aNote))
                { ++aSkip; if (i == 0) { aNewestName = Path.GetFileName(aF); aNewestWhy = "表頭解析不出來"; } continue; }

                var aWarn = new List<string>();
                var aCh = SCP_WatchExport.BuildChapter(
                    p.AgentCommandsRoot, "tavern", aRanges,
                    Path.GetFileNameWithoutExtension(aF), aMedia, aTitle, aSub, aWork, aSessions, aNote,
                    null, null, iAllowZeroStripped: true, aWarn);
                bool aSame = aCh.Error.Length == 0
                             && string.Equals(aCh.Text, aWant, StringComparison.Ordinal);
                if (aSame)
                {
                    ++aMatch;
                    // ⚠ 印出**是哪幾章**符合 —— 只印數字的話，「哪幾章」永遠是讀的人自己推的，
                    //   而推出來的相關性跟量出來的長得一樣。
                    aMatched.Add(Path.GetFileName(Path.GetDirectoryName(aF)) + "/"
                                 + Path.GetFileName(aF) + "@"
                                 + File.GetLastWriteTime(aF).ToString("MM-dd HH:mm"));
                }
                else ++aDiff;
                if (i == 0)
                {
                    aNewestOk = aSame;
                    aNewestName = Path.GetFileName(Path.GetDirectoryName(aF)) + "/" + Path.GetFileName(aF);
                    if (!aSame)
                        aNewestWhy = aCh.Error.Length > 0 ? aCh.Error
                                     : $"長度 {aCh.Text.Length} vs {aWant.Length}";
                }
            }
            yield return new CheckRow($"觀影章重出對拍（{p.Name}）",
                $"**最新那章 `{aNewestName}` 逐位元組相同={aNewestOk}**"
                + (aNewestOk ? "" : $"（{aNewestWhy}）")
                + $"／全部 {aFiles.Count} 章：符合 {aMatch}／不符 {aDiff}／表頭解不出 {aSkip}"
                + "　符合的是：" + (aMatched.Count > 0 ? string.Join("、", aMatched) : "（無）")
                + "　⚠ 只判定最新那章 —— **舊章可能是更早版本的 python 排的**，"
                + "它們不符是「舊快照」不是「移植壞了」（⛔ 也不代表它們一定沒事，那是未量）",
                aNewestOk ? CheckResult.Pass : CheckResult.Fail);
        }
        if (!aAny)
            yield return new CheckRow("觀影章重出對拍",
                "找不到任何 `Books/watch-*/NNN.txt` ⇒ **跳過**（⛔ 不當成通過）", CheckResult.Skipped);
    }

    /// <summary>從章的表頭把當初的參數讀回來。⛔ 解不出就回 false，不猜。</summary>
    // ⛔ 本檔曾自己維護一份 `TryParseChapterHeader` —— 已搬進 `SCP_WatchExport`（TASK-0143）。
    //   理由：`cmd watch --arg op=audit` 也要用它，而兩份各自維護的解析器＝兩個會各自漂的真相源，
   //   漂掉的症狀是「selftest 說 5 章符合、audit 說 7 章符合」，**兩邊都不報錯**。
    static bool TryParseChapterHeader(string iText, out string oMedia, out List<SCP_SeqRange> oRanges,
                                      out string oTitle, out string oSubtitle, out string oWork,
                                      out string oSessions, out string oNote)
        => SCP_WatchExport.TryParseChapterHeader(iText, out oMedia, out oRanges, out oTitle,
                                                 out oSubtitle, out oWork, out oSessions, out oNote);

    // 區塊職責：章的**落檔那一半**（守衛＋寫檔＋回讀＋台帳回填）在 **clean-room** 跑一次。
    // 物理意義：這一層會寫東西 ⇒ ⛔ 不可以拿真資料根驗。
    //          把需要的輸入複製到暫存根（只複製用得到的那幾個 seq 檔），在那裡寫、在那裡比。
    // ⭐ 四格，其中**兩格是反向對照**：
    //    ① 正向：重出真章 ⇒ 與真產物逐位元組相同
    //    ② 反向：同一章再跑一次而不給 force ⇒ **擋下且檔案 md5 不變**
    //       （只驗「寫得成」的話，一個永遠覆寫的實作也會全綠）
    //    ③ 反向：拿一段與既有章重疊的區間 ⇒ **擋下**（一話不該有兩章）
    //    ④ 台帳：append-only ⇒ 行數只增不減，且既有行逐位元組不變
    // ===========================================================
    // 區塊職責：這一章的 seq 區間，**在這台機器的這個區裡有資料嗎**？
    //
    // 物理意義：酒館 seq 是**分區的**（跨區各自從 1 開始數）。而觀影出書那兩格對拍
    //   取的是「`Books/` 底下最新那一章」—— 而 `Books` 是同一個 repo 被多專案掛著，
    //   ⇒ **最新那章很可能是別區產的**，它的 seq 區間在本區的 tavern 裡根本不存在。
    //
    // 🩸 TASK-0138 QA 現場（basecamp 2026-09-07，這兩格紅燈的成因）：
    //   最新那章 `watch-sluha-narodu/002.txt` 要 seq **18738–18779**，
    //   而本區（LY／Florin）的 tavern 最大只到 **16541**。
    //   ⇒ 撈到 0 則 ⇒ 重出是空的 ⇒ 「不符 31 章」。
    //   **那不是移植壞了，是這個測試沒有帶區域定語。**
    //
    // ⚠ 而它最貴的地方在輸出：「沒撈到資料」與「比對不通過」印出來**是同一句話**
    //   ——「不符 31 章」會被讀成「有 31 章壞了」，真相是「有 31 章我根本沒去比」。
    //   ⇒ 這裡回 **Skipped 並把兩個數字印出來**，⛔ 不是 Pass 也不是 Fail。
    //
    // 數值影響：只掃檔名（`messages/<date>/<8 位 seq>.json`）取最大值，**不 parse 任何 JSON**。
    // ===========================================================
    static long MaxTavernSeq(string iDataRoot)
    {
        try
        {
            string aBase = SCP_WatchExport.MessagesDir(iDataRoot, "tavern");
            if (!Directory.Exists(aBase)) return -1;
            long aMax = -1;
            foreach (string f in Directory.GetFiles(aBase, "*.json", SearchOption.AllDirectories))
                if (long.TryParse(Path.GetFileNameWithoutExtension(f), out long s) && s > aMax) aMax = s;
            return aMax;
        }
        catch { return -1; }
    }

    /// <summary>
    /// 這批區間能不能在本區判定。回 <c>null</c>＝可以；回字串＝**不能判的理由**（拿去當 Skipped 的說明）。
    /// </summary>
    static string? WhyOutOfThisRegion(string iDataRoot, IReadOnlyList<SCP_SeqRange> iRanges)
    {
        long aMax = MaxTavernSeq(iDataRoot);
        if (aMax < 0) return "本區的 `tavern` 一則訊息都沒有 ⇒ 沒有資料可判";
        if (iRanges.Count == 0) return "表頭沒有可用的 seq 區間";
        long aLo = long.MaxValue, aHi = long.MinValue;
        foreach (var r in iRanges) { if (r.Lo < aLo) aLo = r.Lo; if (r.Hi > aHi) aHi = r.Hi; }
        // ⚠ 判準刻意只擋「**整段**都在本區軸之外」這一種。
        //   ⛔ 部分重疊仍然要去比 —— 把「只有一半資料」也跳掉的話，
        //   真的缺資料的那種 bug 會跟著被藏起來，而那正是這個測試要抓的東西。
        if (aLo > aMax)
            return $"這一章要 seq {aLo}–{aHi}，而本區 `tavern` 最大只到 {aMax}"
                   + " ⇒ **這批資料不在這一區**（酒館 seq 分區，跨區各自從 1 數）";
        return null;
    }

    /// <summary>讀一章並把行尾正規化成 LF —— **只給表頭解析用**。
    /// ⛔ 「一樣／不一樣」一律用位元組比，⛔ 不准先正規化再比（那會把唯一的差異抹掉）。</summary>
    static readonly string s_Lf = ((char)10).ToString();

    static string ReadChapterNormalized(string iPath)
        => string.Join(s_Lf, File.ReadAllLines(iPath, Encoding.UTF8));


    // ===========================================================
    // 區塊職責：TASK-0217 的常駐守衛 —— **這一章的 seq 現在還指著同一批訊息嗎**。
    //
    // 🩸 為什麼要它：章檔表頭只記 `seq A – B`，**沒記是哪一區的 seq**，而酒館 seq 隨區域分岔。
    //   2026-09-15 逐章量：有實錄段的 39 章裡**本文對得上 0 章**、對不上 27、seq 根本不存在 12。
    //   ⇒ 在這道閘之前，重出任一章都會產出**格式完整、seq 連續、回讀驗證全過**而內容是別人工作公告的東西。
    //
    // ⚠ 本格**不依賴真專案的資料**：自己造訊息、自己造章檔 ⇒ 它不會因為某一區沒有樣本而跳過。
    //   （那正是 `WatchWriteCleanRoom` 的老毛病：它在 LY 每次都跳過，而跳過跟通過在看板上同形。）
    // ===========================================================
    static CheckRow WatchIdentityGuard()
    {
        const string aName = "章身分核對（seq 還指著同一批訊息嗎）＋ 對不上一個檔都不生";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_watchid_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            const string aRoom = "tavern";
            const string aBook = "watch-identity-probe";
            string aMsgDir = Path.Combine(SCP_WatchExport.MessagesDir(aTmp, aRoom), "2026-01-01");
            Directory.CreateDirectory(aMsgDir);
            string aMsgPath = Path.Combine(aMsgDir, "00000042.json");
            void WriteMsg(string iBody) => File.WriteAllText(aMsgPath,
                "{\"ts\":\"2026-01-01T00:00:00.000Z\",\"sender_persona\":\"summit\",\"body\":"
                + SCP_JsonWriter.Write(SCP_JsonData.NewString(iBody), iIndented: false) + "}",
                new UTF8Encoding(false));

            string aBookDir = Path.Combine(aTmp, "Books", aBook);
            Directory.CreateDirectory(aBookDir);
            string aChapter = Path.Combine(aBookDir, "001.txt");
            void WriteChapterFile(string iEntryBody) => File.WriteAllText(aChapter,
                "# 第 1 章 · 探針\n\n> 機械匯出 —— 內容為聊天酒館 seq 42 – 42 原文。\n\n## 實錄\n\n"
                + "### [seq 42] 00:00 · summit\n\n" + iEntryBody + "\n", new UTF8Encoding(false));

            // ① 身分對得上（訊息與章檔同一則）
            WriteMsg("📺 開播觀影 —— 這是那一則的本文");
            WriteChapterFile("📺 開播觀影 —— 這是那一則的本文");
            bool aMatch = SCP_WatchWriter.VerifyChapterIdentity(aTmp, aRoom, aChapter, out string aWhy1);

            // ② 🔴 反向對照 A：同一個 seq 現在是**別的訊息** ⇒ 對不上，而且說得出兩邊各是什麼
            WriteMsg("📦 WorkMemory f86f0c8 —— 完全不同的一則工作公告");
            bool aDiff = !SCP_WatchWriter.VerifyChapterIdentity(aTmp, aRoom, aChapter, out string aWhy2);
            bool aWhySide = aWhy2.Contains("開播觀影") && aWhy2.Contains("WorkMemory");

            // ③ 🔴 對不上時 `WriteChapter` 要**整個拒絕**，而且**一個檔都不生**（含 `_vN`）
            int aBefore = Directory.GetFiles(aBookDir).Length;
            var aLines = new List<string>();
            SCP_WatchWriteResult aW = SCP_WatchWriter.WriteChapter(
                aTmp, aRoom, new[] { new SCP_SeqRange(42, 42) }, "probe-media",
                aBook, "001", "探針", null, null, null, null, null, null,
                iForce: true, iAllowOverlap: true, iAllowZeroStripped: true, aLines);
            int aAfter = Directory.GetFiles(aBookDir).Length;
            bool aRefused = aW.Error.Contains("拒絕重出") && aAfter == aBefore;

            // ④ 反向對照 B：那個 seq **現在不存在** ⇒ 也要對不上（⛔ 不能回「對得上」）
            File.Delete(aMsgPath);
            bool aGone = !SCP_WatchWriter.VerifyChapterIdentity(aTmp, aRoom, aChapter, out string aWhy3)
                         && aWhy3.Contains("不存在");

            // ⑤ 反向對照 C：章檔沒有任何實錄段 ⇒ **無從核對**，⛔ 不是「對得上」
            File.WriteAllText(aChapter, "# 第 1 章 · 沒有實錄段\n", new UTF8Encoding(false));
            bool aNoEntry = !SCP_WatchWriter.VerifyChapterIdentity(aTmp, aRoom, aChapter, out string aWhy4)
                            && aWhy4.Contains("無從核對");

            bool aOk = aMatch && aDiff && aWhySide && aRefused && aGone && aNoEntry;
            string aReading =
                $"同一則 ⇒ 對得上={aMatch}{(aMatch ? "" : "（" + aWhy1 + "）")}"
                + $"；🔴 換成別的訊息 ⇒ 對不上={aDiff}，訊息並排兩邊都印={aWhySide}"
                + $"；🔴 對不上時 WriteChapter 拒絕={aW.Error.Contains("拒絕重出")}"
                + $" 且目錄檔數 {aBefore}→{aAfter} 不變={aAfter == aBefore}"
                + $"；seq 不存在 ⇒ 對不上={aGone}；沒有實錄段 ⇒ 無從核對={aNoEntry}";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { /* 清不掉不影響判定 */ } }
    }

    static IEnumerable<CheckRow> WatchWriteCleanRoom(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aSrc = p.AgentCommandsRoot;
            string aBooksSrc = Path.Combine(aSrc, "Books");
            if (!Directory.Exists(aBooksSrc)) continue;

            // 取「最新那章」當受測體 —— 它保證來自現行實作。
            // 🩸 2026-09-15：原版只挑**一個**（最新那份），而它的 seq 落在別區 ⇒ 這一格在 LY
            //   **每次都跳過**。「跳過」不是通過，所以那等於這條路上沒有任何守衛在跑。
            //   ⇒ 改成由新到舊逐個試，挑**第一個在本區、表頭又解得開**的；全部不合才跳過，
            //     而跳過時要說**試了幾個**（⛔ 不讓「沒有樣本」跟「我只看了一個」同形）。
            var aCands = new List<(string File, DateTime T)>();
            foreach (string d in Directory.GetDirectories(aBooksSrc, "watch-*"))
                foreach (string f in Directory.GetFiles(d, "???.txt"))
                {
                    string aStem = Path.GetFileNameWithoutExtension(f);
                    if (aStem.Length != 3 || !int.TryParse(aStem, out _)) continue;
                    aCands.Add((f, File.GetLastWriteTimeUtc(f)));
                }
            if (aCands.Count == 0) continue;
            aCands.Sort((x, y) => y.T.CompareTo(x.T));
            aAny = true;

            string? aPick = null;
            string aMedia = "", aTitle = "", aSub = "", aWork = "", aSessions = "", aNote = "";
            List<SCP_SeqRange> aRanges = new List<SCP_SeqRange>();
            string aLastWhy = "（沒有候選）";
            int aTried = 0, aBadHead = 0, aOutRegion = 0, aIdMiss = 0, aSeqGone = 0;

            foreach ((string aF, DateTime _) in aCands)
            {
                ++aTried;
                string aTxt = ReadChapterNormalized(aF);
                if (!TryParseChapterHeader(aTxt, out aMedia, out aRanges,
                                           out aTitle, out aSub, out aWork, out aSessions, out aNote))
                { aLastWhy = "表頭解析不出來"; ++aBadHead; continue; }
                string? aWhy = WhyOutOfThisRegion(aSrc, aRanges);
                if (aWhy != null) { aLastWhy = aWhy; ++aOutRegion; continue; }
                // 🔴 TASK-0217：上面那把尺只比**上界**（號碼在不在本區的範圍內），
                //   它答不了「這些號碼**還指著同一批訊息**嗎」。2026-09-15 逐章量：39 章裡 0 章對得上。
                //   ⇒ 不加這一格的話，一個跨區的樣本會被挑來當「重出逐位元組相同」的受測體，
                //     而那一格必紅 —— 紅的是**挑法**，不是排版器。
                if (!SCP_WatchWriter.VerifyChapterIdentity(aSrc, "tavern", aF, out string aIdWhy))
                {
                    aLastWhy = aIdWhy;
                    if (aIdWhy.Contains("不存在")) ++aSeqGone; else ++aIdMiss;
                    continue;
                }
                aPick = aF; break;
            }
            // ⚠ 原版靠「一定挑最新那章」來保證受測體來自**現行排版器** ——
            //   我把挑法放寬成「第一個在本區的」之後，那個保證就沒了。
            //   ⇒ 逐位元組相同這一格**只有在受測體真的是最新那章時才是閘**；
            //     退而求其次挑到舊章時它降級成**讀數**（照印差在哪，⛔ 但不判失敗）。
            //   🩸 不這樣分的話，只有兩條路：讓它長紅（別人的 build 閘替我扛）
            //     或把檢查拔掉（為綠燈改共用狀態）。兩個都不行。
            bool aIsNewest = aPick != null && string.Equals(aPick, aCands[0].File, StringComparison.Ordinal);
            if (aPick == null)
            {
                yield return new CheckRow($"章落檔 clean-room（{p.Name}）",
                    $"逐個試了 **{aTried}** 章，沒有一章能當受測體 ⇒ **跳過**（⛔ 不當成通過）"
                    // ⚠ 只印「最後一個的理由」會讓 40 章長得像同一種壞法 —— 逐類數出來才是讀數。
                    + $"　逐類：**身分對不上 {aIdMiss}**／**那個 seq 現在不存在 {aSeqGone}**"
                    + $"／超出本區 {aOutRegion}／表頭解不開 {aBadHead}"
                    + $"　（TASK-0217：章檔的 seq 沒有區域定語，而酒館 seq 隨區域分岔）"
                    + $"　最後一個的理由：{aLastWhy}",
                    CheckResult.Skipped);
                continue;
            }

            string aTmp = Path.Combine(Path.GetTempPath(), "senate_watchwrite_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                // ── 只複製用得到的輸入（⛔ 不整棵複製，也絕不寫回來源）──
                string aMsgSrc = SCP_WatchExport.MessagesDir(aSrc, "tavern");
                int aCopied = 0;
                if (Directory.Exists(aMsgSrc))
                    foreach (string f in Directory.GetFiles(aMsgSrc, "*.json", SearchOption.AllDirectories))
                    {
                        if (!long.TryParse(Path.GetFileNameWithoutExtension(f), out long q)) continue;
                        bool aIn = false;
                        foreach (SCP_SeqRange r in aRanges) if (q >= r.Lo && q <= r.Hi) { aIn = true; break; }
                        if (!aIn) continue;
                        string aRel = f.Substring(aMsgSrc.Length).TrimStart('\\', '/');
                        string aDst = Path.Combine(SCP_WatchExport.MessagesDir(aTmp, "tavern"), aRel);
                        Directory.CreateDirectory(Path.GetDirectoryName(aDst)!);
                        File.Copy(f, aDst); ++aCopied;
                    }
                Directory.CreateDirectory(Path.Combine(aTmp, "StreamWatch"));
                foreach (string n in new[] { "sessions_log.jsonl", "segments.jsonl", "settings.json" })
                {
                    string f = Path.Combine(aSrc, "StreamWatch", n);
                    if (File.Exists(f)) File.Copy(f, Path.Combine(aTmp, "StreamWatch", n));
                }
                string aMediaJson = Path.Combine(SCP_WatchWriter.MediaRoot(aSrc, aMedia), "media.json");
                if (File.Exists(aMediaJson))
                {
                    string aMd = SCP_WatchWriter.MediaRoot(aTmp, aMedia);
                    Directory.CreateDirectory(aMd);
                    File.Copy(aMediaJson, Path.Combine(aMd, "media.json"));
                }

                string aLedger = SCP_WatchLedger.SessionsLogPath(aTmp);
                int aLedgerBefore = File.Exists(aLedger) ? File.ReadAllLines(aLedger).Length : 0;
                string aLedgerHeadBefore = File.Exists(aLedger)
                    ? string.Join("\n", File.ReadAllLines(aLedger)) : "";

                // ── ① 正向：重出 ──
                var aL1 = new List<string>();
                var aW1 = SCP_WatchWriter.WriteChapter(
                    aTmp, "tavern", aRanges, aMedia,
                    Path.GetFileName(Path.GetDirectoryName(aPick)),
                    Path.GetFileNameWithoutExtension(aPick),
                    aTitle, aSub, aWork, aSessions, aNote, null, null,
                    iForce: false, iAllowOverlap: false, iAllowZeroStripped: true, aL1);
                bool aWrote = aW1.Error.Length == 0 && File.Exists(aW1.OutPath);
                // 🩸 這裡原本兩邊都先 `Replace("\r\n","\n")` 才比 —— **把唯一的差異正規化掉了**
                //   （python 文字模式寫出 CRLF、C# `WriteAllText` 寫出 LF，而內容全同）
                //   於是它綠著，而實跑 `cmp` 立刻紅在第 1 行第 51 字元。
                //   ⇒ 「一樣／不一樣」的問題只能用**位元組**回答，⛔ 不准在比之前先整理。
                bool aSame = aWrote
                    && ByteEqual(File.ReadAllBytes(aW1.OutPath), File.ReadAllBytes(aPick));
                // ⚠ 一個 False 而不說**差在哪**，就是「沒有讀數」的那種形狀 ——
                //   讀的人會去猜（格式改過？行尾？手改過？），而猜要花的時間比印出來貴得多。
                string aDiffWhere = "";
                if (aWrote && !aSame)
                {
                    byte[] aA = File.ReadAllBytes(aW1.OutPath), aB = File.ReadAllBytes(aPick);
                    int aN = Math.Min(aA.Length, aB.Length), aAt = -1;
                    for (int k = 0; k < aN; k++) if (aA[k] != aB[k]) { aAt = k; break; }
                    if (aAt < 0) aDiffWhere = $"前 {aN} 位元組相同，長度不同（重出 {aA.Length} vs 磁碟 {aB.Length}）";
                    else
                    {
                        int aFrom = Math.Max(0, aAt - 30), aLen = Math.Min(60, aN - aFrom);
                        string aCtxA = Encoding.UTF8.GetString(aA, aFrom, aLen).Replace(s_Lf, "⏎");
                        string aCtxB = Encoding.UTF8.GetString(aB, aFrom, aLen).Replace(s_Lf, "⏎");
                        aDiffWhere = $"第 {aAt} 位元組起不同（重出 {aA.Length}B／磁碟 {aB.Length}B）"
                                     + $"　重出「{aCtxA}」／磁碟「{aCtxB}」";
                    }
                }
                bool aBackOk = aWrote && aW1.BackEntries == aW1.Chapterized!.Kept.Count;

                // ── ② 反向：不給 force 再跑一次 ⇒ 擋下且檔案不變 ──
                string aMd5Before = aWrote ? Md5OfFile(aW1.OutPath) : "";
                var aL2 = new List<string>();
                var aW2 = SCP_WatchWriter.WriteChapter(
                    aTmp, "tavern", aRanges, aMedia,
                    Path.GetFileName(Path.GetDirectoryName(aPick)),
                    Path.GetFileNameWithoutExtension(aPick),
                    aTitle, aSub, aWork, aSessions, aNote, null, null,
                    iForce: false, iAllowOverlap: false, iAllowZeroStripped: true, aL2);
                bool aRefused = aW2.Error.Contains("拒絕重出");
                bool aUnchanged = aWrote && string.Equals(aMd5Before, Md5OfFile(aW1.OutPath), StringComparison.Ordinal);

                // ── ⑤ TASK-0152：給 force ⇒ **不覆蓋**，出成 `NNN_v2.txt`，正本一個位元組都不動 ──
                //   ⚠ 這一格要同時量三件事，少一件就有一種壞法會全綠：
                //     (a) 真的寫出了 v2（否則「擋下來什麼都沒做」也會過）
                //     (b) **正本 md5 不變**（否則「回了 v2 路徑但照樣覆寫」也會過）
                //     (c) v2 **不被算成新的一章**（`???.txt` 數不變）—— 版本不是章
                var aL5 = new List<string>();
                string aBaseMd5 = aWrote ? Md5OfFile(aW1.OutPath) : "";
                string aBookDir = aWrote ? Path.GetDirectoryName(aW1.OutPath)! : aTmp;
                int aChaptersBefore = Directory.Exists(aBookDir)
                    ? Directory.GetFiles(aBookDir, "???.txt").Length : 0;
                var aW5 = SCP_WatchWriter.WriteChapter(
                    aTmp, "tavern", aRanges, aMedia,
                    Path.GetFileName(Path.GetDirectoryName(aPick)),
                    Path.GetFileNameWithoutExtension(aPick),
                    aTitle, aSub, aWork, aSessions, aNote, null, null,
                    iForce: true, iAllowOverlap: false, iAllowZeroStripped: true, aL5);
                bool aV2Wrote = aW5.Error.Length == 0 && aW5.Version == 2
                                && aW5.OutPath.EndsWith("_v2.txt", StringComparison.Ordinal)
                                && File.Exists(aW5.OutPath);
                bool aBaseIntact = aWrote
                                   && string.Equals(aBaseMd5, Md5OfFile(aW1.OutPath), StringComparison.Ordinal);
                int aChaptersAfter = Directory.Exists(aBookDir)
                    ? Directory.GetFiles(aBookDir, "???.txt").Length : 0;
                bool aNotAChapter = aChaptersAfter == aChaptersBefore;

                // ── ③ 反向：另一章號但區間重疊 ⇒ 擋下 ──
                var aL3 = new List<string>();
                var aW3 = SCP_WatchWriter.WriteChapter(
                    aTmp, "tavern", aRanges, aMedia,
                    Path.GetFileName(Path.GetDirectoryName(aPick)), "777",
                    aTitle, aSub, aWork, aSessions, aNote, null, null,
                    iForce: false, iAllowOverlap: false, iAllowZeroStripped: true, aL3);
                bool aOverlapBlocked = aW3.Error.Contains("一話不該有兩章")
                                       && !File.Exists(Path.Combine(Path.GetDirectoryName(aW1.OutPath)!, "777.txt"));

                // ── ④ 台帳 append-only ──
                int aLedgerAfter = File.Exists(aLedger) ? File.ReadAllLines(aLedger).Length : 0;
                string aLedgerHeadAfter = File.Exists(aLedger)
                    ? string.Join("\n", File.ReadAllLines(aLedger)[..aLedgerBefore]) : "";
                bool aAppendOnly = aLedgerAfter >= aLedgerBefore
                                   && string.Equals(aLedgerHeadBefore, aLedgerHeadAfter, StringComparison.Ordinal);

                bool aOk = (aSame || !aIsNewest) && aBackOk && aRefused && aUnchanged && aOverlapBlocked && aAppendOnly
                           && aV2Wrote && aBaseIntact && aNotAChapter;
                yield return new CheckRow($"章落檔 clean-room（{p.Name}）",
                    $"受測體 `{Path.GetFileName(Path.GetDirectoryName(aPick))}/{Path.GetFileName(aPick)}`"
                    + $"（複製 {aCopied} 則訊息進暫存根）"
                    + $"／**重出逐位元組相同={aSame}**（{(aIsNewest ? "受測體＝最新那章 ⇒ **這一格是閘**" : "⚠ 最新那章不在本區 ⇒ 退而挑舊章，**這一格降級成讀數不判失敗**")}）{(aDiffWhere.Length > 0 ? "　⚠ " + aDiffWhere : "")}／回讀段數＝收錄數={aBackOk}"
                    + $"／**沒給 force 擋下={aRefused}** 且檔案 md5 不變={aUnchanged}"
                    + $"／**區間重疊擋下且沒生出 777.txt={aOverlapBlocked}**"
                    + $"／**force ⇒ 出 v2 不覆蓋={aV2Wrote}**（正本 md5 不變={aBaseIntact}、"
                    + $"`???.txt` 章數 {aChaptersBefore}→{aChaptersAfter} 不變={aNotAChapter}）"
                    + $"／台帳 append-only（{aLedgerBefore}→{aLedgerAfter} 行、既有行不變={aAppendOnly}）"
                    + "　⚠ 全程在暫存根，**來源一個位元組都沒動**",
                    aOk ? CheckResult.Pass : CheckResult.Fail);
            }
            finally
            {
                try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { }
            }
        }
        if (!aAny)
            yield return new CheckRow("章落檔 clean-room",
                "找不到任何 `Books/watch-*/NNN.txt` ⇒ **跳過**（⛔ 不當成通過）", CheckResult.Skipped);
    }

    /// <summary>逐位元組比。⛔ 不做任何正規化 —— 被正規化掉的那一格正是最容易漏的那一格。</summary>
    static bool ByteEqual(byte[] iA, byte[] iB)
    {
        if (iA.Length != iB.Length) return false;
        for (int i = 0; i < iA.Length; ++i) if (iA[i] != iB[i]) return false;
        return true;
    }

    static string Md5OfFile(string iPath)
    {
        using var aHash = System.Security.Cryptography.MD5.Create();
        byte[] aB = aHash.ComputeHash(File.ReadAllBytes(iPath));
        var aHex = new StringBuilder(32);
        foreach (byte b in aB) aHex.Append(b.ToString("x2"));
        return aHex.ToString();
    }

    static IEnumerable<CheckRow> RealWatchLedgerRead(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aPath = SCP_WatchLedger.SessionsLogPath(p.AgentCommandsRoot);
            if (!File.Exists(aPath)) continue;
            aAny = true;

            var aWarn = new List<string>();
            var aRaw = SCP_WatchLedger.ReadSessionsLog(p.AgentCommandsRoot, aWarn);
            var aState = SCP_WatchLedger.SessionsLogState(p.AgentCommandsRoot, aWarn);

            int aExportEvents = 0, aOrphanEvents = 0;
            foreach (var kv in aRaw)
            {
                if (!string.Equals(kv.Value.GetString("record_type", ""), SCP_WatchLedger.RecordTypeExport,
                                   StringComparison.Ordinal)) continue;
                ++aExportEvents;
                string aSid = kv.Value.GetString("session_id", "");
                if (aSid.Length > 0 && !aState.ContainsKey(aSid)) ++aOrphanEvents;
            }
            int aWithChapter = 0, aWithTitle = 0;
            foreach (var kv in aState)
            {
                if (kv.Value.ExportedChapter.Length > 0) ++aWithChapter;
                if (kv.Value.ExportedTitle.Length > 0) ++aWithTitle;
            }
            // 反向對照：孤兒 export 事件**不可以**變成一個場次列。
            // ⛔ 只數「有幾場」的話，一個把孤兒也塞進去的實作會得到更大的數字而看起來更「完整」。
            bool aNoGhost = true;
            foreach (var kv in aState) if (!kv.Value.Raw.Contains("session_id")) { aNoGhost = false; break; }

            bool aOk = aState.Count > 0 && aExportEvents > 0 && aNoGhost;
            yield return new CheckRow($"真實錄台帳讀取（{p.Name}）",
                $"場次 **{aState.Count}**／export 事件 {aExportEvents}（其中孤兒 {aOrphanEvents} 筆**未造出場次**）"
                + $"／有章號 **{aWithChapter}**／有章名 **{aWithTitle}**"
                + $"／壞行 {aWarn.Count}／每一列都帶 session_id={aNoGhost}"
                + "　⚠ 這是**異源讀數**（與 python `_sessions_log_state()` 並排用）——"
                + "本格只比三個計數；**全量逐場逐欄位的對拍在下一格**（觀影反查全量對拍）",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        if (!aAny)
            yield return new CheckRow("真實錄台帳讀取",
                "找不到任何專案的 `StreamWatch/sessions_log.jsonl` ⇒ **跳過**（⛔ 不當成通過）",
                CheckResult.Skipped);
    }

    static IEnumerable<CheckRow> RealActivitySessionRoundTrip(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            var aSrcRoot = new SCP_DataRoot(p.AgentCommandsRoot);
            string aDir = SCP_ActivitySessionStore.Dir(aSrcRoot);
            if (!Directory.Exists(aDir)) continue;
            string[] aFiles = Directory.GetFiles(aDir, "*.json");
            if (aFiles.Length == 0) continue;
            aAny = true;

            var aProblems = new List<string>();
            List<SCP_ActivitySession> aAll = SCP_ActivitySessionStore.LoadAll(aSrcRoot, aProblems);
            int aRunning = 0, aRegistered = 0;
            foreach (var s in aAll)
            {
                if (SCP_ActivitySessionKind.IsRegistered(s.kind)) aRegistered++;
                if (s.IsRunningNow()) aRunning++;
            }

            // 寫回對拍：複製一份到暫存根，Save 之後比對「原檔的每一個鍵都還在」
            string aTmp = Path.Combine(Path.GetTempPath(), "senate_selftest_realsession_" + Guid.NewGuid().ToString("N")[..8]);
            bool aKeysKept = true;
            string aWorst = "";
            try
            {
                var aTmpRoot = new SCP_DataRoot(aTmp);
                Directory.CreateDirectory(SCP_ActivitySessionStore.Dir(aTmpRoot));
                foreach (string aFile in aFiles)
                {
                    string aName = Path.GetFileNameWithoutExtension(aFile);
                    string? aDst = SCP_ActivitySessionStore.PathOf(aTmpRoot, aName);
                    if (aDst == null) continue;
                    File.Copy(aFile, aDst, true);
                    SCP_JsonData aBefore = SCP_JsonParser.Parse(File.ReadAllText(aDst, Encoding.UTF8));
                    var aSession = SCP_ActivitySessionStore.Load(aTmpRoot, aName);
                    if (aSession == null) { aKeysKept = false; aWorst = aName + "：讀不回來"; continue; }
                    SCP_ActivitySessionStore.Save(aTmpRoot, aName, aSession);
                    SCP_JsonData aAfter = SCP_JsonParser.Parse(File.ReadAllText(aDst, Encoding.UTF8));
                    for (int i = 0; i < aBefore.Keys.Count; ++i)
                    {
                        if (aAfter.Contains(aBefore.Keys[i])) continue;
                        aKeysKept = false;
                        aWorst = aName + "：寫回後少了鍵 `" + aBefore.Keys[i] + "`";
                        break;
                    }
                }
            }
            finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }

            string aProb = aProblems.Count == 0 ? "無" : string.Join("；", aProblems);
            yield return new CheckRow(
                $"真活動 session round-trip（{p.Name}）",
                $"檔 {aFiles.Length} 份／讀回 {aAll.Count} 份（已登記 kind {aRegistered}／此刻進行中 {aRunning}）"
                + $"／寫回不吃鍵={aKeysKept}{(aWorst.Length > 0 ? "（" + aWorst + "）" : "")}／problems：{aProb}",
                aAll.Count == aFiles.Length && aKeysKept ? CheckResult.Pass : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("真活動 session round-trip",
                "找不到樣本（沒有可用專案或該專案沒有 sessions 目錄）—— **這是跳過，不是通過**",
                CheckResult.Skipped);
    }

    /// <summary>兩棵樹等價比較（型別 → 結構 → 原文）。</summary>
    static bool Equivalent(SCP_JsonData iA, SCP_JsonData iB)
    {
        if (iA.Type != iB.Type) return false;
        switch (iA.Type)
        {
            case SCP_JsonType.Object:
                if (iA.Count != iB.Count) return false;
                for (int i = 0; i < iA.Keys.Count; i++)
                {
                    string k = iA.Keys[i];
                    if (iB.Keys[i] != k) return false;          // 順序也要一樣（diff 穩定性）
                    if (!Equivalent(iA[k], iB[k])) return false;
                }
                return true;
            case SCP_JsonType.Array:
                if (iA.Count != iB.Count) return false;
                for (int i = 0; i < iA.Count; i++)
                    if (!Equivalent(iA[i], iB[i])) return false;
                return true;
            case SCP_JsonType.Null:
                return true;
            default:
                return iA.ToJson(false) == iB.ToJson(false);
        }
    }

    // 區塊職責：**「登入狀態」頁拿到的是解析後的信件庫根，不是存起來的原始值。**
    // 物理意義：`lettersRoot` 是 `[SCP_PathAuto]` 的 ⇒ 它可以合法地存成字面 `auto`。
    //          🩸 2026-09-05 之前那一頁自己走 `Prefs.Read(awakening.lettersRoot)` ——
    //          存 `auto` 時它會拿字面 `"auto"` 當目錄去掃，掃不到 ⇒ 畫面印
    //          「這裡真的還沒有人」，**而同一台的 CLI 解得出真正的路徑**。兩邊都不報錯。
    //          ⇒ 這一格用**最壞的那個值**（`auto`）當輸入：解析對了，畫面才數得出人。
    // 數值影響：全在暫存目錄裡自己造一份 senate.local.json 與一個 persona，跑完刪掉 ——
    //          ⛔ **不碰使用者的設定**（去寫使用者的 prefs 來「驗完」一頁，正是上一版的錯法）。
    static CheckRow LoginPageResolvesLettersRoot()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_selftest_login_" + Guid.NewGuid().ToString("N"));
        try
        {
            // ① 造一份「最壞但合法」的設定：lettersRoot 存的是字面 auto
            string aCfgDir = Path.Combine(aTmp, "SenateData", "config");
            Directory.CreateDirectory(aCfgDir);
            string aProjRoot = aTmp.Replace(Path.DirectorySeparatorChar, '/');
            File.WriteAllText(Path.Combine(aCfgDir, "senate.local.json"),
                "{\n  \"schemaVersion\": 1,\n  \"projects\": [\n    {\n"
                + "      \"name\": \"Probe\",\n      \"root\": \"" + aProjRoot + "\",\n"
                + "      \"agentCommandsRoot\": \"auto\",\n      \"enabled\": true,\n      \"profile\": \"\"\n"
                + "    }\n  ],\n  \"awakening\": {\n    \"lettersRoot\": \"auto\"\n  }\n}\n");

            // ② 造一個看得見的 persona —— 掃得到它，就證明掃的不是字面 "auto"
            string aLetters = aProjRoot + "/AgentCommands/ChatTavern/baton/letters";
            Directory.CreateDirectory(Path.Combine(aLetters, "probe-one", SCP_LettersPaths.ProfileDirName));

            // ③ 宿主解析：值要是推導出來的那條，Origin 要說得出「是 auto 推的」
            var aModel = new SenateModel(aTmp);
            SCP_PathResolution aRes = aModel.LettersRoot;
            bool aResolved = aRes.Error == null
                             && aRes.Value.Replace(Path.DirectorySeparatorChar, '/') == aLetters
                             && aRes.Origin.Contains(SCP_PathRegistry.AutoLiteral, StringComparison.Ordinal);

            // ④ 畫真的那一頁：數得出人 ＝ 它掃的是解析後的目錄（舊版會是 0 人）
            var aPage = new SCP_GuiLoginStatusPage(aModel);
            aPage.OnPush();
            var aUi = new SCP_Ui();
            aPage.Draw(aUi);
            string aText = SCP_GuiTextRenderer.Render(aUi.Root, 200);
            bool aCounts = aText.Contains("persona 1 人", StringComparison.Ordinal);
            bool aShowsOrigin = aText.Contains("來源：", StringComparison.Ordinal)
                                && aText.Contains(aLetters, StringComparison.Ordinal);

            // ⑤ 那一頁**不准再自己存一份路徑**：反射找它身上的 prefs key，應該一個都沒有
            int aOwnKeys = 0;
            foreach (FieldInfo f in typeof(SCP_GuiLoginStatusPage)
                         .GetFields(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance))
                if (f.FieldType == typeof(SCP_PrefKey<string>)) aOwnKeys++;

            bool aOk = aResolved && aCounts && aShowsOrigin && aOwnKeys == 0;
            return new CheckRow("登入頁信件庫根",
                $"auto 解析={aResolved}（值={aRes.Value}／來源={aRes.Origin}）"
                + $"／畫面數得出人={aCounts}／印出值＋來源={aShowsOrigin}／本頁自存的路徑 key={aOwnKeys}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e)
        {
            return new CheckRow("登入頁信件庫根", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail);
        }
        finally { try { Directory.Delete(aTmp, true); } catch { /* 暫存目錄刪不掉不影響判定 */ } }
    }

    // 區塊職責：路徑描述表自身的合法性 —— **「漏掛 attribute」要在出廠驗收擋下**，
    //          不是執行到那一格才炸（那時症狀是頁面打不開／CLI 少一列）。
    static CheckRow PathRegistryShape()
    {
        var aReadings = new List<string>();
        List<string> aProblems;
        try { aProblems = SCP.Core.Paths.SCP_PathRegistry.Validate(); }
        catch (Exception e)
        { return new CheckRow("路徑描述表", "Validate 自己炸了：" + e.Message, CheckResult.Fail); }

        int aCount = SCP.Core.Paths.SCP_PathRegistry.All.Count;
        aReadings.Add($"共 {aCount} 條");
        int aStored = 0, aDerived = 0;
        foreach (var d in SCP.Core.Paths.SCP_PathRegistry.All)
            if (d.Kind == SCP.Core.Paths.SCP_PathKind.Stored) aStored++; else aDerived++;
        aReadings.Add($"Stored {aStored}／Derived {aDerived}");
        aReadings.Add(aProblems.Count == 0 ? "問題 0" : $"問題 {aProblems.Count}：{string.Join("；", aProblems)}");
        return new CheckRow("路徑描述表", string.Join("／", aReadings),
            aProblems.Count == 0 && aCount > 0 ? CheckResult.Pass : CheckResult.Fail);
    }

    // ===========================================================
    // 區塊職責：銀行三層（id 正規化／帳戶／帳本）與 `[SCP_JsonExtensionData]` 的**常駐**守衛。
    //
    // 🩸 為什麼這四格在這裡而不是留在單子上：TASK-0209 A/B 段每一格都驗過，
    //   **而那些驗證全部是丟棄式探針** —— 跑完就沒了。下一個人把互斥拿掉、把正規化改成
    //   「不合法就替你換字元」、把未知鍵的收容所拆掉，**不會有任何一層喊**，
    //   而失效樣子分別是：超扣／兩個帳號靜默併成一個／別人的設定欄位安靜消失。
    //
    // ⚠ 射程（這幾格量不到什麼）：
    //   · 這裡驗的是**行程內**的互斥。跨行程的唯一寫入端由 Server 單例鎖保證，
    //     那一格要起子行程 ⇒ 刻意不收進來（flaky 的成本每個人每天付），重現指令寫在 TASK-0209 上。
    //   · ⛔ 沒有「把鎖拿掉必須超扣」的反向對照 —— 那要改產品碼。
    //     ⭐ 但它不必在這裡：**互斥被拿掉時，下面那一格會自己變紅**（20/20 成功、餘額負值）。
    //     ⇒ 這一格是回歸閘，不是存在性證明。
    // ===========================================================
    static CheckRow BankIdRules()
    {
        const string aName = "銀行 id 正規化（大小寫／拒絕不替換／撞名分組）";
        try
        {
            // ① 大小寫：正規化成小寫，且 Changed 說得出「我動過它」
            SCP_BankIdResult aUpper = SCP_BankId.Normalize("  FRS  ");
            bool aCase = aUpper.Ok && aUpper.Id == "frs" && aUpper.Changed;

            // ② ⛔ 不合法就拒絕，**不替你換掉** —— 換字元＝靜默把兩個帳號併成一個，而那是錢
            string[] aBadOnes = { "discord:123", "Federal Reserve System", "-leading", "", new string('x', 65) };
            int aRejected = 0;
            foreach (string aBad in aBadOnes)
            {
                SCP_BankIdResult r = SCP_BankId.Normalize(aBad);
                if (!r.Ok && r.Id.Length == 0) aRejected++;
            }

            // ③ 合法字元集
            bool aGood = SCP_BankId.Normalize("a.b_c-9").Ok && SCP_BankId.Normalize("9lives").Ok;

            // ④ SameAccount 只回答「這兩個字串指同一個帳號嗎」
            bool aSame = SCP_BankId.SameAccount("Zeta", "zeta") && !SCP_BankId.SameAccount("zeta", "zeta-da-xiaojie");

            // ⑤ 撞名分組 —— 餵的是**舊帳本真的那四組**（TASK-0209 B1 的活體讀數）
            var aCollide = SCP_BankId.GroupCollisions(new[]
            {
                "antigravity", "Antigravity", "gemini-da-xiaojie", "Gemini-da-xiaojie",
                "zeta", "Zeta", "zeta-da-xiaojie", "Zeta-da-xiaojie",
                "cc", "Myth", "Template",          // 沒有變體的那些不該進表
                "discord:123",                     // 不合法的不混進撞名表
            });
            bool aGroups = aCollide.Count == 4
                           && aCollide.ContainsKey("antigravity") && aCollide["antigravity"].Count == 2
                           && aCollide.ContainsKey("zeta") && !aCollide.ContainsKey("cc");

            bool aOk = aCase && aRejected == aBadOnes.Length && aGood && aSame && aGroups;
            string aReading =
                $"大小寫：{(aCase ? "'  FRS  ' ⇒ 'frs'（Changed=true）" : "**沒正規化或沒標 Changed**")}"
                + $"；拒絕不替換：{aRejected}/{aBadOnes.Length} 筆被擋且 Id 為空"
                + $"；合法字元：{(aGood ? "a.b_c-9／9lives 都收" : "**誤擋合法 id**")}"
                + $"；SameAccount：{(aSame ? "Zeta≡zeta 且 zeta≢zeta-da-xiaojie" : "**判錯**")}"
                + $"；撞名分組：{aCollide.Count} 組（真帳本那 4 組{(aGroups ? "，且無變體者與不合法者都不入表" : "**，內容不對**")}）";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }

    static CheckRow BankAccountCleanRoom()
    {
        const string aName = "銀行帳戶（開戶是顯式動作／撞名擋下／壞欄位走安全側）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bankacct_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);

            // ① 開戶：id 正規化落檔，DisplayName 原樣保留（改顯示名不等於換帳號）
            bool aOpened = SCP_BankAccounts.TryOpen(aTmp, "FRS", "Federal Reserve System", "basecamp", out _, out string aWhy1);
            string aPath = Path.Combine(aTmp, "accounts", "frs.json");
            bool aFile = File.Exists(aPath);
            SCP_BankAccount? aLoaded = aFile ? SCP_BankAccounts.TryLoad(aTmp, "frs", out _) : null;
            bool aDisplay = aLoaded != null && aLoaded.DisplayName == "Federal Reserve System";

            // ② 撞名：`Frs` 指的就是 `frs` ⇒ 擋下，且**不覆寫**既有檔
            byte[] aBefore = aFile ? File.ReadAllBytes(aPath) : new byte[0];
            bool aDup = !SCP_BankAccounts.TryOpen(aTmp, "Frs", "冒名", "someone", out _, out _);
            bool aIntact = aFile && ByteEqual(aBefore, File.ReadAllBytes(aPath));
            // 反向對照：擋的是撞名，不是全擋
            bool aOther = SCP_BankAccounts.TryOpen(aTmp, "frs2", "", "basecamp", out _, out _);

            // ③ 沒開戶不能收付（舊系統那 42 個幽靈帳戶就是從這裡長出來的）
            bool aGhost = SCP_BankAccounts.CheckUsable(aTmp, "nobody").Result == SCP_BankAccountCheck.Kind.NotOpened;
            bool aInvalid = SCP_BankAccounts.CheckUsable(aTmp, "discord:123").Result == SCP_BankAccountCheck.Kind.Invalid;

            // ④ 🔴 壞掉的 status 走**安全側**（不可用）——
            //    反過來的話，一個壞欄位會讓帳號變成可以動錢的
            string aBadPath = Path.Combine(aTmp, "accounts", "frs2.json");
            bool aSafeSide = false;
            var aBadKind = SCP_BankAccountCheck.Kind.Usable;
            if (File.Exists(aBadPath))
            {
                File.WriteAllText(aBadPath, File.ReadAllText(aBadPath).Replace("\"Open\"", "\"???\"").Replace("\"open\"", "\"???\""));
                aBadKind = SCP_BankAccounts.CheckUsable(aTmp, "frs2").Result;
                aSafeSide = aBadKind != SCP_BankAccountCheck.Kind.Usable;
            }

            bool aOk = aOpened && aFile && aDisplay && aDup && aIntact && aOther && aGhost && aInvalid && aSafeSide;
            string aReading =
                $"開戶：{(aOpened && aFile ? "'FRS' ⇒ accounts/frs.json" : $"**失敗（{aWhy1}）**")}"
                + $"；顯示名：{(aDisplay ? "保留 'Federal Reserve System'" : "**被正規化吃掉**")}"
                + $"；撞名 'Frs'：{(aDup ? "擋下" : "**放行**")}，既有檔逐位元組{(aIntact ? "不變" : "**被覆寫**")}"
                + $"；反向對照 'frs2'：{(aOther ? "開得成（擋的是撞名不是全擋）" : "**被誤擋**")}"
                + $"；沒開戶：{(aGhost ? "NotOpened" : "**沒擋**")}／不合法 id：{(aInvalid ? "Invalid" : "**沒擋**")}"
                + $"；壞 status：{(aSafeSide ? $"{aBadKind}（安全側）" : "**判成 Usable —— 一個壞欄位就能動錢**")}";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { /* 清不掉不影響判定 */ } }
    }

    static CheckRow BankLedgerConcurrentDebit()
    {
        const string aName = "銀行帳本：20 筆併發 debit 只夠 10 筆 ⇒ 不超扣（＋冪等先於餘額）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bankledger_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            SCP_BankAccounts.TryOpen(aTmp, "probe", "", "selftest", out _, out _);
            SCP_BankLedger.Credit(aTmp, "probe", 100, "selftest_seed");

            // 20 執行緒同時各扣 10，而餘額只夠 10 筆 —— 互斥被拿掉時這一格會變成 20/20、餘額 −100
            var aResults = new SCP_BankPostResult[20];
            var aThreads = new Thread[20];
            using (var aGate = new ManualResetEventSlim(false))
            {
                for (int i = 0; i < aThreads.Length; i++)
                {
                    int aIdx = i;
                    aThreads[i] = new Thread(() =>
                    {
                        aGate.Wait();
                        aResults[aIdx] = SCP_BankLedger.Debit(aTmp, "probe", 10, "selftest_race");
                    });
                    aThreads[i].Start();
                }
                aGate.Set();
                foreach (Thread t in aThreads) t.Join(TimeSpan.FromSeconds(30));
            }

            int aWon = 0;
            foreach (SCP_BankPostResult r in aResults) if (r.Ok) aWon++;
            int aBalance = SCP_BankLedger.GetBalance(aTmp, "probe");

            // ⛔ 讀數用**檔名列舉**，不信上面那個回傳計數（它跟被測物同源）
            int aFiles = Directory.GetFiles(Path.Combine(aTmp, "ledger"), "*.json", SearchOption.AllDirectories).Length;

            // 冪等：同一把 key 送兩次 ⇒ 同一個 entry、只扣一次
            SCP_BankLedger.Credit(aTmp, "probe", 50, "selftest_seed2");
            SCP_BankPostResult aI1 = SCP_BankLedger.Debit(aTmp, "probe", 20, "selftest_idem", iIdempotencyKey: "k-1");
            SCP_BankPostResult aI2 = SCP_BankLedger.Debit(aTmp, "probe", 20, "selftest_idem", iIdempotencyKey: "k-1");
            bool aIdem = aI1.Ok && aI2.Ok && aI2.Duplicate && aI1.Entry!.Id == aI2.Entry!.Id
                         && SCP_BankLedger.GetBalance(aTmp, "probe") == 30;

            // 🩸 冪等**先於**餘額檢查：餘額歸零後重送同一把 key，要回既有 entry，
            //    ⛔ 不是「餘額不足」（那句話是假的 —— 錢早就扣過了）
            SCP_BankLedger.Debit(aTmp, "probe", 30, "selftest_drain");
            SCP_BankPostResult aI3 = SCP_BankLedger.Debit(aTmp, "probe", 20, "selftest_idem", iIdempotencyKey: "k-1");
            bool aIdemFirst = aI3.Ok && aI3.Duplicate && aI1.Entry != null && aI3.Entry!.Id == aI1.Entry!.Id;

            bool aOk = aWon == 10 && aBalance == 0 && aFiles == 11 && aIdem && aIdemFirst;
            string aReading =
                $"併發：{aWon}/20 成功（期望 10）、重放求和餘額 {aBalance}（期望 0）、"
                + $"entry 檔 {aFiles} 個（期望 11 ＝ 1 credit ＋ 10 debit，**檔名列舉**非回傳計數）"
                + $"；冪等：{(aIdem ? "同 key 兩次回同一個 entry 且只扣一次" : "**扣了兩次或 entry 不同**")}"
                + $"；冪等先於餘額：{(aIdemFirst ? "餘額 0 時重送仍回既有 entry" : "**噴了餘額不足 —— 那句話是假的**")}";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { /* 清不掉不影響判定 */ } }
    }

    // ⚠ 這兩個型別**只差一個 attribute** —— 反向對照要真的把 X 拿掉，
    //   不能寫一個「看起來像沒有 X」的替身（TASK-0209 留言 #9 的血證）。
    sealed class ExtBagType
    {
        public string Name = "";
        [SCP_JsonExtensionData] public Dictionary<string, SCP_JsonData> Extra = new Dictionary<string, SCP_JsonData>();
    }

    sealed class NoExtBagType
    {
        public string Name = "";
    }

    static CheckRow JsonExtensionDataRoundTrip()
    {
        const string aName = "[SCP_JsonExtensionData]：本版不認得的 key 原樣寫回（＋沒掛 attribute 必須消失）";
        try
        {
            const string aSrc = "{\"Name\":\"frs\",\"future_field\":42,\"//\":\"註解行\",\"nested\":{\"a\":[1,2]}}";
            // ⚠ key 是 `Name` 不是 `name`：成員比對是**逐字**的（`SCP_TypeSchema.Find`）。
            //   🩸 我第一版寫小寫 ⇒ 它被當成未知鍵收進收容所（4 個而不是 3 個），而 `Name` 留空 ——
            //   失效樣子是「這一格紅了」，⛔ 不是產品壞了。記在這裡免得下一個人去改產品碼。

            var aKept = new ExtBagType();
            SCP_JsonMapper.Populate(aKept, SCP_JsonData.Parse(aSrc));
            string aOut = SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(aKept));
            bool aRound = aKept.Name == "frs"
                          && aKept.Extra.Count == 3
                          && aOut.Contains("future_field") && aOut.Contains("\"//\"") && aOut.Contains("nested");

            // 🔴 反向對照：同一份輸入餵給**沒掛 attribute** 的孿生型別 ⇒ 未知鍵必須不見
            var aLost = new NoExtBagType();
            SCP_JsonMapper.Populate(aLost, SCP_JsonData.Parse(aSrc));
            string aOutLost = SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(aLost));
            bool aDropped = aLost.Name == "frs" && !aOutLost.Contains("future_field") && !aOutLost.Contains("nested");

            bool aOk = aRound && aDropped;
            string aReading =
                $"有收容所：未知鍵 {aKept.Extra.Count} 個（future_field／\"//\"／nested）"
                + $"{(aRound ? "全數原樣寫回" : "**掉了或沒寫回**")}"
                + $"；🔴 反向對照（拿掉 attribute）：{(aDropped ? "未知鍵確實消失 ⇒ 這一格量到的是 attribute 本身" : "**沒掉 —— 那上面那格什麼都沒證明**")}";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }


    // ===========================================================
    // 區塊職責：酒館寫入臨界區（`SCP_TavernWriter`）的兩格 —— TASK-0106
    // ⚠ 這兩格量的是**不同的東西**，⛔ 不要把它們合成一格：
    //   · 淨室那格量「撞檔時會怎樣」—— 那是行為，臨時目錄就夠。
    //   · 真檔那格量「寫出來的位元組跟 Editor 一不一樣」—— 那只有真語料答得出來。
    // ===========================================================

    /// <summary>
    /// 淨室：配號、原子建檔、撞檔自我校正、`_seq.txt` 快取，以及序列化的選填欄位規則。
    /// 🔴 反向對照在第三格：**人工放一個佔位檔**，寫入端必須繞過它而**不是覆蓋它**
    /// （舊版 `File.Exists` → `WriteAllText` 在單 process 下也繞得過，所以這一格真正的閘是
    /// 「那個佔位檔的位元組一個都沒變」＋「HealAttempts 說得出它重算過一次」）。
    /// </summary>
    static CheckRow TavernWriteCleanRoom()
    {
        const string aName = "酒館寫入臨界區（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_tavernwrite_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            const string aRoom = "probe-room";
            SCP_TavernWriter.InvalidateCount(aTmp, aRoom);

            var aM1 = new SCP_TavernMessage
            {
                SenderId = "zeta", SenderName = "zeta", SenderPersona = "summit",
                Kind = "chat", Body = "第一則",
            };
            SCP_TavernWriteResult aR1 = SCP_TavernWriter.WriteMessage(aTmp, aRoom, aM1);

            // 人工佔位：下一個號碼的檔名先被別人佔走（模擬「另一個寫入端剛寫完而我不知道」）
            string aDir = Path.GetDirectoryName(aR1.FullPath)!;
            string aSquat = Path.Combine(aDir, "00000002.json");
            byte[] aSquatBytes = new UTF8Encoding(false).GetBytes("{\"decoy\":\"佔位\"}");
            File.WriteAllBytes(aSquat, aSquatBytes);

            var aM2 = new SCP_TavernMessage
            {
                SenderId = "zeta", SenderName = "zeta", SenderPersona = "summit",
                Kind = "chat", Body = "第二則",
            };
            SCP_TavernWriteResult aR2 = SCP_TavernWriter.WriteMessage(aTmp, aRoom, aM2);

            bool aSquatIntact = File.ReadAllBytes(aSquat).AsSpan()
                                    .SequenceEqual(aSquatBytes.AsSpan());
            string aSeqCache = File.Exists(SCP_TavernRooms.SeqPath(aTmp, aRoom))
                ? File.ReadAllText(SCP_TavernRooms.SeqPath(aTmp, aRoom)).Trim() : "(沒有)";

            // 序列化的選填欄位規則：空的不 emit、`kind` 空要補 "chat"、seq 不進內容
            var aFull = new SCP_TavernMessage
            {
                Ts = "2026-09-21T00:00:00.000Z", Uuid = "abcdef",
                SenderId = "zeta", SenderName = "zeta", SenderPersona = "summit",
                SenderAvatarSprite = "Null", Kind = "", Body = "引號\" 換行\n 反斜線\\",
                ReplyTo = 7, ReplyToUuid = "112233", Seq = 999,
            };
            aFull.Meta["tag"] = "probe";
            string aJson = SCP_TavernWriter.Serialize(aFull);
            const string aExpect =
                "{\"ts\":\"2026-09-21T00:00:00.000Z\",\"uuid\":\"abcdef\",\"sender_id\":\"zeta\","
                + "\"sender_name\":\"zeta\",\"sender_persona\":\"summit\",\"sender_avatar_sprite\":\"Null\","
                + "\"kind\":\"chat\",\"body\":\"引號\\\" 換行\\n 反斜線\\\\\",\"reply_to\":7,"
                + "\"reply_to_uuid\":\"112233\",\"meta\":{\"tag\":\"probe\"}}";
            bool aSerOk = aJson == aExpect;

            var aBare = new SCP_TavernMessage { Ts = "t", SenderId = "a", SenderName = "b", Kind = "chat", Body = "" };
            string aBareJson = SCP_TavernWriter.Serialize(aBare);
            bool aOmit = !aBareJson.Contains("sender_persona", StringComparison.Ordinal)
                         && !aBareJson.Contains("sender_avatar_sprite", StringComparison.Ordinal)
                         && !aBareJson.Contains("reply_to", StringComparison.Ordinal)
                         && !aBareJson.Contains("\"seq\"", StringComparison.Ordinal);

            bool aOk = aR1.Wrote && aR1.Seq == 1 && aR1.HealAttempts == 0
                       && aR2.Wrote && aR2.Seq == 3 && aR2.HealAttempts == 1
                       && aSquatIntact && aSeqCache == "3" && aSerOk && aOmit;

            string aReading =
                $"第一則 seq={aR1.Seq}（校正 {aR1.HealAttempts} 次）／撞檔後 seq={aR2.Seq}（校正 {aR2.HealAttempts} 次）"
                + $"／🔴 佔位檔位元組{(aSquatIntact ? "一個都沒變 ⇒ **沒有覆蓋**" : "**被改掉了 —— 那就是舊行為**")}"
                + $"／`_seq.txt`={aSeqCache}"
                + $"／序列化逐字對齊 Editor={aSerOk}／選填欄位空值不 emit＋seq 不進內容={aOmit}";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }

    /// <summary>
    /// 真語料：把每一則落盤訊息讀回來、用 `SCP_TavernWriter.Serialize` 重寫一次，**逐位元組比**。
    /// <para>⚠ 閘只押在「**現行 Editor writer 寫的那一段**」：2026-05-16 之前那段是遷移工具
    /// 與幾支繞道寫入端（python 排版、BOM、尾端換行）的產物 —— 它們本來就不是這支移植要復刻的對象。
    /// ⛔ 而它們**不靜默排掉**：每一桶的數字都印出來，否則「驗過 19,884 則」與「驗過 20,434 則」同形。</para>
    /// </summary>
    static IEnumerable<CheckRow> RealTavernSerializerMatchesEditor(IReadOnlyList<ProjectReading> iProjects)
    {
        // 現行 writer 的起點：全庫最後一筆「不同」落在 2026-05-15T15:52Z（2026-09-21 實測）。
        const string aCutoff = "2026-05-16";
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            List<string> aRooms = SCP_TavernRead.EnumerateRoomIds(p.AgentCommandsRoot);
            if (aRooms.Count == 0) continue;
            aAny = true;

            int aNewSame = 0, aNewDiff = 0, aOldSame = 0, aOldDiff = 0, aNoFile = 0;
            string aFirstDiff = "";
            foreach (string r in aRooms)
            {
                int n = SCP_TavernRead.CountMessages(p.AgentCommandsRoot, r);
                if (n <= 0) continue;
                foreach (SCP_TavernMessage m in SCP_TavernRead.Range(p.AgentCommandsRoot, r, 1, n, null))
                {
                    if (string.IsNullOrEmpty(m.Path) || !File.Exists(m.Path)) { aNoFile++; continue; }
                    byte[] aDisk;
                    try { aDisk = File.ReadAllBytes(m.Path); } catch { aNoFile++; continue; }
                    byte[] aMine = new UTF8Encoding(false).GetBytes(SCP_TavernWriter.Serialize(m));
                    bool aSame = aDisk.AsSpan().SequenceEqual(aMine.AsSpan());
                    bool aNew = string.CompareOrdinal(m.Ts, aCutoff) >= 0;
                    if (aNew) { if (aSame) aNewSame++; else aNewDiff++; }
                    else { if (aSame) aOldSame++; else aOldDiff++; }
                    if (aNew && !aSame && aFirstDiff.Length == 0)
                        aFirstDiff = "　▸ 第一筆不符：" + r + " #" + m.Seq + "（" + m.Ts + "）";
                }
            }

            int aNewTotal = aNewSame + aNewDiff;
            yield return new CheckRow(
                $"酒館序列化移植 vs Editor 真產物（{p.Name}）",
                $"**{aCutoff} 之後 {aNewTotal} 則：相符 {aNewSame}／不符 {aNewDiff}**（這一桶是閘）"
                + $"　之前 {aOldSame + aOldDiff} 則：相符 {aOldSame}／不符 {aOldDiff}"
                + "（**只是讀數** —— 遷移工具與繞道寫入端的產物：python 排版／BOM／尾端換行）"
                + $"／讀不到檔 {aNoFile}" + aFirstDiff,
                aNewDiff == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        if (!aAny)
            yield return new CheckRow("酒館序列化移植 vs Editor 真產物", "沒有可讀的專案 ⇒ **量不到**，不是通過", CheckResult.Skipped);
    }


    /// <summary>
    /// 開關的四種讀法必須**兩兩可分**（TASK-0106 / D10）：沒設定過／設成 editor／設成 server／看不懂。
    /// 🔴 這一格真正的閘是最後一種：**認不得的值不可以悄悄回 editor** ——
    /// 那會讓打錯字的人看到「一切正常」，然後以為自己切過去了。
    /// </summary>
    /// <summary>
    /// 自動啟動的**四態**（TASK-0267）—— 餵已知答案，⛔ 不起任何真的 Server。
    /// <para>🔴 為什麼這一格必須存在：那四態的處置兩兩相反（再等／去看 log／不必等／什麼都不做），
    /// 而在這之前它們**只有註解沒有讀數**。⚠ 而 `TimedOut` 原本結構上測不到（逾時寫死 20s）——
    /// 那不是「還沒測」，是**一個量不到的態跟一個壞掉的態在計數上同形**。</para>
    /// <para>⛔ 本格**不驗** spawn 怎麼生行程（那是宿主側 `ServerSpawn`，平台相依）——
    /// 它驗的是「拿到什麼答案就走哪一條」。</para>
    /// </summary>
    static CheckRow ServerAutoStartFourStates()
    {
        const string aName = "自動啟動四態（AlreadyRunning／Started／TimedOut／SpawnFailed）";
        try
        {
            Func<int, string> aLogPath = iPid => "log-" + iPid;

            // ① 本來就在跑 ⇒ AlreadyRunning，而且 **spawn 一次都不准被呼叫**
            int aSpawnCalls = 0;
            bool aSpawnOk(out int oPid, out string oErr) { ++aSpawnCalls; oPid = 4242; oErr = ""; return true; }
            var aAlready = SCP_ServerAutoStart.Ensure(() => true, aSpawnOk, aLogPath);
            bool aOk1 = aAlready.Outcome == SCP_ServerAutoStartOutcome.AlreadyRunning
                        && aAlready.Ok && aSpawnCalls == 0 && aAlready.LogPath == null;

            // ② 連 spawn 都失敗 ⇒ SpawnFailed，Detail 一定有話，⛔ 而且不該有 log 路徑（沒生出來哪來的 log）
            bool aSpawnFail(out int oPid, out string oErr) { oPid = 0; oErr = "探針：故意起不來"; return false; }
            var aFailed = SCP_ServerAutoStart.Ensure(() => false, aSpawnFail, aLogPath);
            bool aOk2 = aFailed.Outcome == SCP_ServerAutoStartOutcome.SpawnFailed
                        && !aFailed.Ok && aFailed.Detail.Length > 0 && aFailed.LogPath == null;

            // ③ 起來了 ⇒ Started。⚠ 第一次問是 false（否則走不到 spawn），之後才 true ——
            //    這格順便驗判準①：**等的是「有沒有一顆」**，而不是「我那顆 pid」。
            int aAsk = 0;
            aSpawnCalls = 0;
            var aStarted = SCP_ServerAutoStart.Ensure(() => ++aAsk > 1, aSpawnOk, aLogPath, null, 3000);
            bool aOk3 = aStarted.Outcome == SCP_ServerAutoStartOutcome.Started
                        && aStarted.Ok && aStarted.LogPath == "log-4242" && aSpawnCalls == 1;

            // 🔴 ④ 一直等不到 ⇒ TimedOut（⛔ 不是 SpawnFailed）。窗口撐到 600ms 才測得出來。
            var aTimeout = SCP_ServerAutoStart.Ensure(() => false, aSpawnOk, aLogPath, null, 600);
            bool aOk4 = aTimeout.Outcome == SCP_ServerAutoStartOutcome.TimedOut
                        && !aTimeout.Ok && aTimeout.Detail.Length > 0 && aTimeout.LogPath == "log-4242";

            // 🔴 ⑤ 兩種失敗**說的話必須不同** —— 壓成同一段就是把兩種相反的處置塞進一個出口。
            var aLinesT = new List<string>();
            var aLinesF = new List<string>();
            SCP_ServerAutoStart.Explain(aTimeout, aLinesT);
            SCP_ServerAutoStart.Explain(aFailed, aLinesF);
            bool aOk5 = aLinesT.Count > 0 && aLinesF.Count > 0
                        && !string.Equals(string.Join("\n", aLinesT), string.Join("\n", aLinesF),
                                          StringComparison.Ordinal);

            // ⑥ 成功那兩態**不該說話**（它們沒有出口要指）
            var aLinesA = new List<string>();
            SCP_ServerAutoStart.Explain(aAlready, aLinesA);
            SCP_ServerAutoStart.Explain(aStarted, aLinesA);
            bool aOk6 = aLinesA.Count == 0;

            bool aOk = aOk1 && aOk2 && aOk3 && aOk4 && aOk5 && aOk6;
            string aReading =
                $"在跑 ⇒ AlreadyRunning 且 spawn 零呼叫：{aOk1}／spawn 失敗 ⇒ SpawnFailed 且無 log 路徑：{aOk2}"
                + $"／起來了 ⇒ Started（spawn 恰一次）：{aOk3}"
                + $"／🔴 等不到 ⇒ **TimedOut 不是 SpawnFailed**（窗口 600ms）：{aOk4}"
                + $"／🔴 兩種失敗說的話不同：{aOk5}（{aLinesT.Count} 行 vs {aLinesF.Count} 行）"
                + $"／成功兩態不說話：{aOk6}"
                + "　⛔ 本格不驗 spawn 怎麼生行程（宿主側 ServerSpawn，平台相依）";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }

    static CheckRow TavernWriteModeFourStates()
    {
        const string aName = "酒館寫入開關四態（agent_settings.json）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_tavernmode_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);

            // ① 檔還不存在 ⇒ 沒設定過（不是錯誤），走預設 editor
            SCP_TavernWriteModeRead aNone = SCP_TavernWriteMode.Read(aTmp);
            bool aOkNone = aNone.Ok && aNone.Host == SCP_TavernWriteHost.Editor && !aNone.IsExplicit;

            // ② 顯式 editor ⇒ 同樣走 editor，但 IsExplicit 要分得出來
            SCP_TavernWriteMode.Write(aTmp, SCP_TavernWriteHost.Editor);
            SCP_TavernWriteModeRead aEd = SCP_TavernWriteMode.Read(aTmp);
            bool aOkEd = aEd.Ok && aEd.Host == SCP_TavernWriteHost.Editor && aEd.IsExplicit;

            // ③ 顯式 server
            SCP_TavernWriteMode.Write(aTmp, SCP_TavernWriteHost.Server);
            SCP_TavernWriteModeRead aSv = SCP_TavernWriteMode.Read(aTmp);
            bool aOkSv = aSv.Ok && aSv.Host == SCP_TavernWriteHost.Server && aSv.IsExplicit;

            // 🔴 ④ 手寫一個認不得的值 ⇒ 必須是 Error，⛔ 不是「回 editor」
            string aPath = SCP_TavernWriteMode.SettingsPath(aTmp);
            File.WriteAllText(aPath, "{\"tavern\":{\"writer\":\"serverr\"}}", new UTF8Encoding(false));
            SCP_TavernWriteModeRead aBad = SCP_TavernWriteMode.Read(aTmp);
            bool aOkBad = !aBad.Ok && aBad.Raw == "serverr";

            // ⑤ 值是空字串 ⇒ 也要出聲（「有人清空它」跟「沒設定過」不同形）
            File.WriteAllText(aPath, "{\"tavern\":{\"writer\":\"\"}}", new UTF8Encoding(false));
            SCP_TavernWriteModeRead aEmpty = SCP_TavernWriteMode.Read(aTmp);
            bool aOkEmpty = !aEmpty.Ok;

            // 反向對照：寫開關不可以吃掉別人的 section
            File.WriteAllText(aPath, "{\"tavern\":{\"writer\":\"editor\"},\"skills\":{\"agent\":\"summit\"}}",
                              new UTF8Encoding(false));
            SCP_TavernWriteMode.Write(aTmp, SCP_TavernWriteHost.Server);
            string aAfter = File.ReadAllText(aPath);
            bool aKept = aAfter.Contains("\"skills\"", StringComparison.Ordinal)
                         && aAfter.Contains("summit", StringComparison.Ordinal);

            bool aOk = aOkNone && aOkEd && aOkSv && aOkBad && aOkEmpty && aKept;
            string aReading =
                $"沒設定過 ⇒ editor 且 IsExplicit=false：{aOkNone}／顯式 editor：{aOkEd}／顯式 server：{aOkSv}"
                + $"／🔴 認不得的值 `serverr` ⇒ **報錯不回預設**：{aOkBad}"
                + $"／值空字串 ⇒ 出聲：{aOkEmpty}／寫入保留別人的 section：{aKept}"
                + $"　⚠ 這一格只量開關本身，⛔ **不量「Server 在不在」**（那是另一個讀數）";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }


    /// <summary>
    /// `tavern-write` 的三道閘（TASK-0106 / D10 丙）。
    /// 🔴 最重要的是**拒絕那一格要零寫入** —— 「拒絕了」與「拒絕了但還是寫了半個目錄出去」
    /// 在 exit code 上長得一樣，而後者正是「兩個寫入端」的第一步。
    /// ⚠ 這格用 <c>ServerContext.InServer</c> 直接跑本體，⛔ **不經過檔案協議**
    ///   ⇒ 它量的是閘與寫入，**不量**委派那條路（那條由 `ServerResultRoundTrip` 管）。
    /// </summary>
    static CheckRow TavernWriteCmdGates()
    {
        const string aName = "tavern-write 三道閘（開關未切 ⇒ 零寫入）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_tavernwritecmd_" + Guid.NewGuid().ToString("N")[..8]);
        bool aWas = Senate.Core.ServerContext.InServer;
        string aWasId = Senate.Core.ServerContext.ServerId;
        try
        {
            Directory.CreateDirectory(aTmp);
            var aCmd = new Senate.Core.Cmd_TavernWrite();
            Senate.Core.ServerContext.InServer = true;
            // TASK-0296：本體只在「我就是這支指定的那一顆」時才跑 ⇒ 要扮成酒館那顆，⛔ 不是任意一顆
            Senate.Core.ServerContext.ServerId = SCP.Core.Proc.SCP_ServerIds.Tavern;

            const string aRoom = "probe";
            string aMsgJson = "{\"ts\":\"2026-09-21T02:00:00.000Z\",\"uuid\":\"aa11bb\",\"sender_id\":\"zeta\","
                              + "\"sender_name\":\"zeta\",\"sender_persona\":\"summit\",\"kind\":\"chat\","
                              + "\"body\":\"閘測\"}";

            SCP_CmdArgs Args(Dictionary<string, string> iRaw)
            {
                var (a, aErrs) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, iRaw);
                if (a == null) throw new InvalidOperationException(string.Join("；", aErrs));
                return a;
            }
            Dictionary<string, string> Raw(string iJson) => new()
            {
                ["data_root"] = aTmp, ["room"] = aRoom, ["msg_json"] = iJson,
            };

            // ① 開關沒設過（＝ editor）⇒ 必須拒絕，而且**一個位元組都不寫**
            SCP_CmdResult aR1 = aCmd.Execute(Args(Raw(aMsgJson)));
            string aMsgDir = Path.Combine(aTmp, "ChatTavern", "rooms", aRoom);
            bool aNoWrite = !Directory.Exists(aMsgDir);
            bool aOk1 = aR1.ExitCode != 0 && aNoWrite;

            // ② 認不得的值 ⇒ 也是拒絕（⛔ 不是「當成 editor」也不是「當成 server」）
            File.WriteAllText(SCP_TavernWriteMode.SettingsPath(aTmp),
                              "{\"tavern\":{\"writer\":\"serverr\"}}", new UTF8Encoding(false));
            SCP_CmdResult aR2 = aCmd.Execute(Args(Raw(aMsgJson)));
            bool aOk2 = aR2.ExitCode != 0 && !Directory.Exists(aMsgDir);

            // ③ 切到 server ⇒ 寫得進去，且簽章是本寫入端的
            SCP_TavernWriteMode.Write(aTmp, SCP_TavernWriteHost.Server);
            SCP_CmdResult aR3 = aCmd.Execute(Args(Raw(aMsgJson)));
            string Val(SCP_CmdResult iR, string iKey)
            {
                foreach (KeyValuePair<string, string> kv in iR.Values) if (kv.Key == iKey) return kv.Value;
                return "";
            }
            string aSeq = Val(aR3, "seq");
            string aPath = Val(aR3, "path");
            bool aLanded = aPath.Length > 0 && File.Exists(aPath);
            string aOnDisk = aLanded ? File.ReadAllText(aPath, new UTF8Encoding(false)) : "";
            bool aOk3 = aR3.ExitCode == 0 && aSeq == "1" && aLanded
                        && aOnDisk.Contains("\"body\":\"閘測\"", StringComparison.Ordinal)
                        && aOnDisk.Contains("\"_writer\":\"" + SCP_TavernWriter.WriterSignatureValue + "\"",
                                            StringComparison.Ordinal);

            // ④ msg_json 壞掉 ⇒ 拒絕，且不多出第二個檔
            SCP_CmdResult aR4 = aCmd.Execute(Args(Raw("{not json")));
            int aFiles = Directory.Exists(aMsgDir)
                ? Directory.GetFiles(aMsgDir, "*.json", SearchOption.AllDirectories).Length : 0;
            bool aOk4 = aR4.ExitCode != 0 && aFiles == 1;

            // 🔴 ⑤ lane 是**固定一條 `tavern`**（PM 2026-09-21 拍板 A，驗收條文 ③），
            //   **而且必須是一層目錄名**（⛔ 不含 `/` 也不含 `:`）。兩個性質要同時在場：
            //   [a] **房名不會漏進 lane** —— 餵兩個不同的房要拿到同一條 lane。
            //       🩸 只比「等於 tavern」擋不住 per-room 實作：它在 room=`tavern` 那個房上
            //       **產生一模一樣的字串**，而那正是近 7 日 100% 流量所在的房
            //       ⇒ 真實用法下那種對拍永遠不會紅。
            //   [b] **執行器讀得到它** —— `ServerExecutor.Tick` 掃 `queues/*` 那一層目錄、
            //       只認 `pending.trigger` ⇒ 帶 `/` 的子分道會落到 `queue-<lane>.json`，
            //       **它永遠讀不到，而且不會說不認得**（2026-09-21 端到端實測：等 15 秒逾時，
            //       queue 檔好好躺在磁碟上）。
            var aProbe = new TavernLaneProbe();
            string aLane = aProbe.Peek(Args(Raw(aMsgJson)));
            string aLaneOther = aProbe.Peek(Args(new Dictionary<string, string>
            {
                ["data_root"] = aTmp, ["room"] = "another-room", ["msg_json"] = aMsgJson,
            }));
            bool aOk5 = aLane == Senate.Core.Cmd_TavernWrite.TavernLane
                        && aLaneOther == aLane
                        && !aLane.Contains("/", StringComparison.Ordinal)
                        && !aLane.Contains(":", StringComparison.Ordinal);

            bool aOk = aOk1 && aOk2 && aOk3 && aOk4 && aOk5;
            string aReading =
                $"🔴 開關未切 ⇒ 拒絕（exit {aR1.ExitCode}）且 rooms/ 根本沒建出來：{aOk1}"
                + $"／認不得的值 ⇒ 拒絕（exit {aR2.ExitCode}）：{aOk2}"
                + $"／切到 server ⇒ seq={aSeq}、落盤且簽章 `{SCP_TavernWriter.WriterSignatureValue}`：{aOk3}"
                + $"／msg_json 壞 ⇒ 拒絕且檔數仍是 {aFiles}：{aOk4}"
                + $"／🔴 lane=`{aLane}`（**固定一條**，PM 拍板 A）、"
                + $"換一個房 `another-room` 仍是 `{aLaneOther}`（＝房名沒漏進 lane）、"
                + "且是一層目錄名（帶 `/` 的話執行器讀不到且不出聲）：" + aOk5
                + "　⚠ 本格**不經過檔案協議** ⇒ 委派那條路不在射程內";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally
        {
            Senate.Core.ServerContext.InServer = aWas;
            Senate.Core.ServerContext.ServerId = aWasId;
            try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { }
        }
    }

    /// <summary>
    /// 只為了讀 <c>Lane()</c> 而存在的探針 —— `Lane` 是 protected，而「它到底回哪一條」
    /// 正是 TASK-0106 留言 #3 要拍的那一格 ⇒ 它必須被量到，不能只寫在註解裡。
    /// （<c>Cmd_TavernWrite</c> 因此**不加 sealed**，理由就是這一格。）
    /// </summary>
    sealed class TavernLaneProbe : Senate.Core.Cmd_TavernWrite
    {
        public string Peek(SCP_CmdArgs iArgs) => Lane(iArgs);
    }


    /// <summary>
    /// Server 名片的四態（TASK-0106 第 4 步）。
    /// 🔴 這一格的閘是**「沒有名片」與「名片在但心跳死了」不可以同形** ——
    /// 前者要去啟動，後者要去查它為什麼死；壓成一個 bool 的話兩邊都只會印「Server 沒跑」。
    /// </summary>
    static CheckRow ServerEndpointFourStates()
    {
        const string aName = "Server 名片四態（沒有／遺體／活著／讀不開）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_endpoint_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            const string aId = "probe";
            string aHb = Path.Combine(aTmp, "_hb.json").Replace('\\', '/');

            // ① 沒有名片
            SCP_ServerProbe aNone = SCP_ServerEndpoint.Probe(aTmp, aId);
            bool aOk1 = aNone.State == SCP_ServerLiveness.NoEndpoint && !aNone.Alive;

            var aCard = new SCP_ServerEndpointInfo
            {
                ServerId = aId, Pid = 12345, BuildId = "test",
                StartedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ServerRoot = aTmp + "/server-probe", HeartbeatPath = aHb, StaleSeconds = 4.0,
            };
            SCP_ServerEndpoint.Write(aTmp, aCard);

            // ② 名片在、心跳檔不在 ⇒ 遺體（⛔ 不是「沒有 Server」）
            SCP_ServerProbe aNoHb = SCP_ServerEndpoint.Probe(aTmp, aId);
            bool aOk2 = aNoHb.State == SCP_ServerLiveness.StaleHeartbeat && aNoHb.Info?.Pid == 12345;

            // ③ 心跳在但過期
            void Beat(double iAgoSeconds)
            {
                string aAt = DateTime.UtcNow.AddSeconds(-iAgoSeconds).ToString("o", CultureInfo.InvariantCulture);
                File.WriteAllText(aHb, "{\"beat_at_utc\":\"" + aAt + "\"}", new UTF8Encoding(false));
            }
            Beat(30);
            SCP_ServerProbe aStale = SCP_ServerEndpoint.Probe(aTmp, aId);
            bool aOk3 = aStale.State == SCP_ServerLiveness.StaleHeartbeat
                        && aStale.BeatAgeSeconds.HasValue && aStale.BeatAgeSeconds.Value > 4.0;

            // ④ 心跳新鮮 ⇒ 活著（🔴 反向對照：上一格與這一格只差心跳時間，其餘完全相同
            //    ⇒ 判定真的是由心跳決定的，不是由「名片在不在」決定的）
            Beat(0.2);
            SCP_ServerProbe aAlive = SCP_ServerEndpoint.Probe(aTmp, aId);
            bool aOk4 = aAlive.State == SCP_ServerLiveness.Alive && aAlive.Alive
                        && aAlive.Info?.ServerRoot == aTmp + "/server-probe";

            // ⑤ 名片壞掉 ⇒ 不知道（⛔ 不當成沒有）
            File.WriteAllText(SCP_ServerEndpoint.PathFor(aTmp, aId), "{壞掉", new UTF8Encoding(false));
            SCP_ServerProbe aBad = SCP_ServerEndpoint.Probe(aTmp, aId);
            bool aOk5 = aBad.State == SCP_ServerLiveness.Unreadable;

            // ⑥ 刪掉 ⇒ 回到「沒有」
            SCP_ServerEndpoint.Delete(aTmp, aId);
            bool aOk6 = SCP_ServerEndpoint.Probe(aTmp, aId).State == SCP_ServerLiveness.NoEndpoint;

            bool aOk = aOk1 && aOk2 && aOk3 && aOk4 && aOk5 && aOk6;
            string aReading =
                $"沒有名片 ⇒ NoEndpoint：{aOk1}／名片在但心跳檔不在 ⇒ **遺體**：{aOk2}"
                + $"／心跳 30s 沒跳 ⇒ 遺體（量得出 {aStale.BeatAgeSeconds?.ToString("0.0", CultureInfo.InvariantCulture)}s）：{aOk3}"
                + $"／🔴 只把心跳換成 0.2s 前（其餘一個位元組都沒動）⇒ Alive：{aOk4}"
                + $"／名片壞掉 ⇒ **Unreadable 不是 NoEndpoint**：{aOk5}／刪掉 ⇒ 回到 NoEndpoint：{aOk6}"
                + "　⚠ 本格是淨室；**真的起一顆 Server 的那次**另外量（見 TASK-0106 留言）";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }


    /// <summary>
    /// 🔴 **兩份 client 的 queue 形狀必須一樣**（TASK-0106 第 5 步）。
    /// <para>Unity 那側編不到 <c>AgentCmdClient</c>（吃 System.Text.Json），所以協議有了第二份實作
    /// （<c>SCP_ServerCmdClient</c>）。⛔ 兩份實作會分岔，而分岔的失效樣子是
    /// **「送出去了，對面永遠看不到」** —— 路徑對、JSON 合法、trigger 也寫了，而 Watcher 不認得那個形狀。
    /// ⇒ 這一格拿兩邊各送一筆進暫存樹，逐欄比 `queue.json`。</para>
    /// <para>⚠ 刻意**不比**的三格（它們本來就該不同，比了會逼人改成一樣而失去意義）：
    /// `Id`／`CreatedAt`（每次不同）、`Args` 裡兩邊各自注入的 caller 標記。</para>
    /// </summary>
    static CheckRow ServerCmdClientMatchesAgentCmdClient()
    {
        const string aName = "兩份 Cmd client 的 queue 形狀一致（Unity 版 vs Senate 版）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_twoclients_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aRootA = Path.Combine(aTmp, "a");
            string aRootB = Path.Combine(aTmp, "b");
            Directory.CreateDirectory(aRootA);
            Directory.CreateDirectory(aRootB);
            const string aLane = "probe";
            var aArgs = new Dictionary<string, string> { ["data_root"] = "D:/x", ["room"] = "tavern" };

            string aIdA = Senate.Core.AgentCmdClient.Submit(aRootA, aLane, "TavernWrite",
                                                            new Dictionary<string, string>(aArgs), _ => { });
            string aIdB = SCP_ServerCmdClient.Submit(aRootB, aLane, "TavernWrite", aArgs);

            string aQa = Senate.Core.AgentCmdClient.QueuePath(aRootA, aLane);
            string aQb = SCP_ServerCmdClient.QueuePath(aRootB, aLane);
            bool aBothQueues = File.Exists(aQa) && File.Exists(aQb);
            bool aBothTriggers = File.Exists(Senate.Core.AgentCmdClient.TriggerPath(aRootA, aLane))
                                 && File.Exists(SCP_ServerCmdClient.TriggerPath(aRootB, aLane));

            SCP_JsonData aA = SCP_JsonParser.Parse(File.ReadAllText(aQa));
            SCP_JsonData aB = SCP_JsonParser.Parse(File.ReadAllText(aQb));
            SCP_JsonData aCmdA = aA["Commands"][0];
            SCP_JsonData aCmdB = aB["Commands"][0];

            // ① 指令物件的**鍵集合**逐字相同（順序也比 —— 兩邊都是手寫順序，差一格就代表有人漏加）
            string KeysOf(SCP_JsonData iObj)
            {
                var aKeys = new List<string>();
                foreach (string k in iObj.Keys) aKeys.Add(k);
                return string.Join(",", aKeys);
            }
            string aKeysA = KeysOf(aCmdA), aKeysB = KeysOf(aCmdB);
            bool aOkKeys = aKeysA == aKeysB;

            // ② 不會變的那幾格逐字相同
            bool aOkFixed = aCmdA.GetString("Type", "") == aCmdB.GetString("Type", "")
                            && aCmdA.GetString("Mode", "") == aCmdB.GetString("Mode", "")
                            && aCmdA.GetInt("RunCount", -1) == aCmdB.GetInt("RunCount", -2);

            // ③ 三個 null 欄位真的是 null（⛔ 不是空字串 —— Watcher 那側區分得出來）
            bool IsNull(SCP_JsonData iObj, string iKey) => iObj.Contains(iKey) && iObj[iKey].IsNull;
            bool aOkNulls = IsNull(aCmdB, "LastRunAt") && IsNull(aCmdB, "LastRunResult")
                            && IsNull(aCmdB, "LastRunError") && IsNull(aCmdB, "Description")
                            && IsNull(aCmdA, "LastRunAt");

            // ④ cmd id 的形狀一樣（`<日期>-<時間>-<6碼>-<型別小寫>`）
            bool ShapeOk(string iId)
            {
                string[] aSeg = iId.Split('-');
                return aSeg.Length == 4 && aSeg[0].Length == 8 && aSeg[1].Length == 6
                       && aSeg[2].Length == 6 && aSeg[3] == "tavernwrite";
            }
            bool aOkId = ShapeOk(aIdA) && ShapeOk(aIdB);

            // ⑤ 業務參數兩邊都在（caller 標記各自不同，刻意不比）
            bool aOkArgs = aCmdA["Args"].GetString("room", "") == "tavern"
                           && aCmdB["Args"].GetString("room", "") == "tavern"
                           && aCmdB["Args"].GetString("_caller_client", "") == SCP_ServerCmdClient.ClientId;

            // 🔴 ⑥ **子分道的路徑兩邊要一樣** —— 這一格是本測試真正的閘。
            //   🩸 第一版只比了 queue.json 的內容、兩邊又都餵沒有子分道的 lane
            //   ⇒ 它對「Unity 版自己拼了一套路徑」完全無感（實際發生過，2026-09-21）。
            //   協議的子分道住在**檔名**裡（`queues/<folder>/queue-<lane>.json`），⛔ 不是另一個資料夾。
            const string aSub = "tavern/my-room";
            bool aOkLane = SCP_ServerCmdClient.QueuePath(aRootB, aSub)
                               == Senate.Core.AgentCmdClient.QueuePath(aRootB, aSub)
                           && SCP_ServerCmdClient.TriggerPath(aRootB, aSub)
                               == Senate.Core.AgentCmdClient.TriggerPath(aRootB, aSub)
                           && SCP_ServerCmdClient.QueuePath(aRootB, aSub).Contains("queue-my-room.json",
                                                                                   StringComparison.Ordinal);

            bool aOk = aBothQueues && aBothTriggers && aOkKeys && aOkFixed && aOkNulls && aOkId && aOkArgs && aOkLane;
            string aReading =
                $"兩邊都生出 queue＋trigger：{aBothQueues && aBothTriggers}"
                + $"／🔴 指令物件鍵集合逐字相同：{aOkKeys}（`{aKeysB}`）"
                + $"／Type·Mode·RunCount 相同：{aOkFixed}／四個欄位真的是 null 不是空字串：{aOkNulls}"
                + $"／cmd_id 形狀相同：{aOkId}／業務參數穿得過去：{aOkArgs}"
                + $"／🔴 子分道路徑兩邊逐字相同（`queue-my-room.json`）：{aOkLane}"
                + "　⚠ 刻意不比 Id／CreatedAt／caller 標記（那三格本來就該不同）";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }


    /// <summary>
    /// 🔴 撞檔與「其他 IO 失敗」必須**同時在場、而且分得開**（TASK-0256，@kiara QA ② 擋下的那格）。
    /// <para>🩸 第一版判準是 `catch (IOException) when (File.Exists(path))`，而
    /// `FileStream(CreateNew)` 一建構檔就已經存在（0 bytes）⇒ 那個條件在**任何**建構後的
    /// IOException 上都成立。實測把「磁碟空間不足」丟進去：被吃掉、降級成撞檔、留下 0-byte 孤兒檔
    /// **永久佔住一個 seq**，而最後報出來的成因是「資料層可能損壞」——它會把人送去翻一個沒壞的目錄。</para>
    /// <para>⚠ 驗收寫成**比較**不是狀態（她的判準）：只寫「磁碟滿會往上炸」的話，
    /// 它半套的時候（吃掉、留孤兒、報錯成因）看起來一模一樣。</para>
    /// <para>⚠ 射程：磁碟滿那一腿在這裡是**用 win32 code 合成的例外**驗判準本身；
    /// **真的把磁碟寫滿那次是 @kiara 跑的**（她的 harness，`SetLength(可用空間+64GB)`）。
    /// ⛔ 本格不宣稱重現了那個現場。</para>
    /// </summary>
    static CheckRow AtomicFileDistinguishesCollisionFromOtherIo()
    {
        const string aName = "原子建檔：撞檔 vs 其他 IO 失敗（兩個讀數同時在場）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_atomic_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            string aPath = Path.Combine(aTmp, "target.json");

            // ① 第一次建檔：成功
            bool aFirst = SCP_AtomicFile.TryCreateNew(aPath, "{\"n\":1}");
            // ② 同一個路徑再建一次：**撞檔 ⇒ false**，而且**原檔一個位元組都沒變**
            byte[] aBefore = File.ReadAllBytes(aPath);
            bool aSecond = SCP_AtomicFile.TryCreateNew(aPath, "{\"n\":2}");
            bool aIntact = File.ReadAllBytes(aPath).AsSpan().SequenceEqual(aBefore.AsSpan());

            // 🔴 ③ 判準本身：撞檔碼吃掉、其他碼**不吃**
            bool Judge(int iWin32) => SCP_AtomicFile.IsAlreadyExists(new IOException("x") { HResult = unchecked((int)(0x80070000 | (uint)iWin32)) });
            bool aTake80 = Judge(80);            // ERROR_FILE_EXISTS
            bool aTake183 = Judge(183);          // ERROR_ALREADY_EXISTS
            bool aSkip112 = !Judge(112);         // 🔴 ERROR_DISK_FULL —— 這一格是閘
            bool aSkip32 = !Judge(32);           // ERROR_SHARING_VIOLATION（別人開著同名檔）
            bool aSkip3 = !Judge(3);             // ERROR_PATH_NOT_FOUND

            // 🔴 ④ 反向對照：舊判準（問 File.Exists）對**同一批**例外會怎麼答
            //    —— 它在目標檔存在時一律回 true ⇒ 磁碟滿也會被讀成撞檔。
            bool aOldWouldEat = File.Exists(aPath);   // 舊版的條件；此刻檔在 ⇒ true ＝ 它會吃掉 112
            bool aNewBeatsOld = aOldWouldEat && aSkip112;

            bool aOk = aFirst && !aSecond && aIntact && aTake80 && aTake183
                       && aSkip112 && aSkip32 && aSkip3 && aNewBeatsOld;
            string aReading =
                $"首建 {aFirst}／同名再建 ⇒ false：{!aSecond}、原檔位元組未變：{aIntact}"
                + $"／判準吃 80·183：{aTake80 && aTake183}"
                + $"／🔴 **不吃** 112 磁碟滿：{aSkip112}、32 共用衝突：{aSkip32}、3 路徑不存在：{aSkip3}"
                + $"／🔴 反向對照：同一時刻舊判準 `File.Exists` ＝ {aOldWouldEat}"
                + $"（⇒ 它會把磁碟滿讀成撞檔；新判準不會）：{aNewBeatsOld}"
                + "　⚠ 磁碟滿這一腿是**合成例外驗判準**，真的寫滿那次是 @kiara 的 harness 跑的";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }


    /// <summary>
    /// 🔴 委派佇列的 append 在併發下**不得遺失**（TASK-0263）。
    /// <para>🩸 舊形狀是「讀整個 queue.json → 加一筆 → 寫回整檔」，⛔ 沒有互斥
    /// ⇒ 兩個寫入端各自讀到同一份舊內容、各自寫回，**後寫的把先寫的那一筆整個吃掉**。
    /// 活體（2026-09-21，三顆 CLI 併發 60 筆委派）：落盤 55，而 **60 顆 client 全部 exit 0** ——
    /// 沒有任何一層說「有一筆不見了」。</para>
    /// <para>⚠ 本格是**差分**不是狀態（@kiara 的判準）：舊形狀與新形狀在同一支 harness 下各跑一次，
    /// 兩個讀數同時在場。只寫「新的不會掉」的話，它半套的時候（偶爾掉一兩筆）看起來一模一樣。</para>
    /// <para>⚠ 射程：本格重現的是**那對原語**（無鎖讀改寫 vs <see cref="SCP_FileLock"/>），
    /// ⛔ 不是整顆 Server —— 執行器那一側由
    /// <see cref="QueueCommitKeepsEntriesAppendedDuringBatch"/> 管。</para>
    /// </summary>
    static CheckRow QueueAppendSurvivesConcurrency()
    {
        const string aName = "委派佇列 append 併發不遺失（舊形狀 vs 新形狀，同一支 harness）";
        const int aThreads = 4, aPer = 25;
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_queuerace_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            int aOldLanded = RaceAppend(Path.Combine(aTmp, "old"), aThreads, aPer, iLocked: false);
            int aNewLanded = RaceAppend(Path.Combine(aTmp, "new"), aThreads, aPer, iLocked: true);
            int aWant = aThreads * aPer;

            // 🔴 舊形狀**必須真的掉**，否則這一格什麼都沒證明
            //   （機器太快／排程沒交錯 ⇒ 沒有競爭 ⇒ 新形狀的綠燈是「沒測到」的偽裝）。
            bool aOldLoses = aOldLanded < aWant;
            bool aNewKeeps = aNewLanded == aWant;
            bool aOk = aOldLoses && aNewKeeps;
            string aReading =
                $"{aThreads} 條執行緒 × {aPer} 筆 ＝ 送出 {aWant}"
                + $"／🔴 舊形狀（無鎖讀改寫）落盤 **{aOldLanded}**（掉 {aWant - aOldLanded}）：{aOldLoses}"
                + $"／🔴 新形狀（檔案鎖）落盤 **{aNewLanded}**：{aNewKeeps}"
                + "　⚠ 舊形狀沒掉的話本格判 Fail —— 那代表這一趟沒有發生競爭，"
                + "而**綠燈會變成「沒測到」的偽裝**";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }

    /// <summary>N 條執行緒同時 append 進同一顆 queue.json，回傳最後**真的在檔案裡**的筆數。</summary>
    /// <param name="iLocked"><c>true</c> ＝ 走 <see cref="SCP_FileLock"/>（新形狀）；
    /// <c>false</c> ＝ 原地重現舊形狀（讀 → 改 → 寫回，無互斥）。</param>
    static int RaceAppend(string iRoot, int iThreads, int iPer, bool iLocked)
    {
        string aDir = Path.Combine(iRoot, "queues", "lane");
        Directory.CreateDirectory(aDir);
        string aPath = Path.Combine(aDir, "queue.json");
        File.WriteAllText(aPath, "{\"Commands\":[]}\n", new UTF8Encoding(false));

        void AppendOne(string iId)
        {
            // ⚠ 兩條路**只差一個 using** —— 其餘每一行刻意相同，
            //   否則差分量到的可能是別的東西（受詞先於差值）。
            if (iLocked) { using (SCP_FileLock.Acquire(aPath)) AppendUnlocked(aPath, iId); }
            else AppendUnlocked(aPath, iId);
        }

        var aThreadList = new List<System.Threading.Thread>();
        for (int t = 0; t < iThreads; ++t)
        {
            int aT = t;
            aThreadList.Add(new System.Threading.Thread(() =>
            {
                for (int i = 0; i < iPer; ++i) AppendOne($"t{aT}-{i}");
            }));
        }
        foreach (var aTh in aThreadList) aTh.Start();
        foreach (var aTh in aThreadList) aTh.Join();

        SCP_JsonData aRoot = SCP_JsonParser.Parse(File.ReadAllText(aPath));
        return aRoot.Contains("Commands") ? aRoot["Commands"].Count : 0;
    }

    /// <summary>讀 → 加一筆 → 寫回整檔。⛔ **沒有互斥** —— 這正是 TASK-0263 的那個形狀。</summary>
    static void AppendUnlocked(string iPath, string iId)
    {
        for (int aTry = 0; ; ++aTry)
        {
            try
            {
                SCP_JsonData aRoot = SCP_JsonParser.Parse(File.ReadAllText(iPath));
                SCP_JsonData aCmd = SCP_JsonData.NewObject();
                aCmd.Set("Id", SCP_JsonData.NewString(iId));
                aRoot["Commands"].Add(aCmd);
                string aTmpFile = iPath + ".tmp" + Guid.NewGuid().ToString("N")[..6];
                File.WriteAllText(aTmpFile, SCP_JsonWriter.Write(aRoot) + "\n", new UTF8Encoding(false));
                File.Move(aTmpFile, iPath, overwrite: true);
                return;
            }
            // ⚠ 檔案被別人開著／正在被換掉時重試 —— 這一腿**兩條路都有**（既有的 WriteAtomic 也這樣做）
            //   ⇒ 差分量到的不是「舊的會丟例外」，是**它會安靜地少一筆**。
            catch (Exception) when (aTry < 60) { System.Threading.Thread.Sleep(2); }
        }
    }

    /// <summary>
    /// 🔴 執行器收尾時**不得把批次期間新進的那幾筆寫沒**（TASK-0263 的第二個落點）。
    /// <para>🩸 舊版是 <c>SaveQueue(批次開始時載入的那份副本)</c>，而一批要跑好幾秒 ——
    /// 這段時間內 client append 的那一筆會被整個蓋掉。⚠ 那不是窄窗口的競態，
    /// 是**整批時長那麼寬**的窗口。而下游看不見：它不在 queue、也沒有判定檔
    /// ⇒ client 端把它讀成「Cmd disappeared → 推論 Success」。</para>
    /// </summary>
    static CheckRow QueueCommitKeepsEntriesAppendedDuringBatch()
    {
        const string aName = "執行器收尾：批次期間新進的那筆要活下來（⛔ 不寫回舊副本）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_queuecommit_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aDir = Path.Combine(aTmp, "queues", "lane");
            Directory.CreateDirectory(aDir);
            string aPath = Path.Combine(aDir, "queue.json");

            // ① 批次開始：queue 裡有兩筆（a、b），執行器把它們讀進記憶體開跑
            File.WriteAllText(aPath, "{\"Commands\":[{\"Id\":\"a\"},{\"Id\":\"b\"}]}\n", new UTF8Encoding(false));

            // ② 批次「跑了幾秒」期間，某顆 client append 第三筆（c）—— 直接寫檔，模擬另一個 process
            SCP_JsonData aMid = SCP_JsonParser.Parse(File.ReadAllText(aPath));
            SCP_JsonData aNew = SCP_JsonData.NewObject();
            aNew.Set("Id", SCP_JsonData.NewString("c"));
            aMid["Commands"].Add(aNew);
            File.WriteAllText(aPath, SCP_JsonWriter.Write(aMid) + "\n", new UTF8Encoding(false));

            // ③ 執行器收尾：a、b 跑完出隊
            int aKept = Senate.Core.ServerExecutor.CommitLaneQueue(
                aPath, new List<string> { "a", "b" },
                new Dictionary<string, (string?, string?, string?)>());

            SCP_JsonData aAfter = SCP_JsonParser.Parse(File.ReadAllText(aPath));
            var aIds = new List<string>();
            for (int i = 0; i < aAfter["Commands"].Count; ++i)
                aIds.Add(aAfter["Commands"][i].GetString("Id", ""));

            bool aOnlyC = aIds.Count == 1 && aIds[0] == "c";
            bool aOk = aOnlyC && aKept == 1;
            string aReading =
                "批次開始 [a,b] ⇒ 期間 client 加了 c ⇒ 收尾把 a,b 出隊"
                + $"／🔴 剩下 `[{string.Join(",", aIds)}]`（要是 `[c]`，⛔ 不是空的）：{aOnlyC}"
                + $"／回報保留新進 {aKept} 筆：{aKept == 1}"
                + "　⚠ 舊版在這裡會寫回 `[]` —— 而 c 不在 queue、也沒有判定檔 ⇒ 送它的人拿到 exit 0";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }

    /// <summary>
    /// 🔴 「不在 queue ＋ 沒有判定檔」⇒ 必須是 <c>Unknown</c>，⛔ **不得回 Success**（TASK-0263 ③）。
    /// <para>🩸 舊版在這裡回 Success，理由是相容不寫判定檔的舊版執行端 ——
    /// 而同一個形狀也是「這筆 append 被別人的整檔寫回蓋掉」的樣子，**兩者處置相反**。</para>
    /// <para>⚠ 本格同時量**另一半**：判定檔在的時候仍然要回 Success ——
    /// 只驗「不回 Success」的話，一支永遠回 Unknown 的實作也會綠。</para>
    /// </summary>
    static CheckRow VanishedCmdIsUnknownNotSuccess()
    {
        const string aName = "委派判定：cmd 不見了而無判定檔 ⇒ **不知道**（⛔ 不是成功）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_vanished_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(aTmp, "queues", "lane"));
            File.WriteAllText(AgentCmdClient.QueuePath(aTmp, "lane"), "{\"Commands\":[]}\n",
                              new UTF8Encoding(false));
            const string aMissing = "20260921-000000-aaaaaa-probe";
            const string aDone = "20260921-000000-bbbbbb-probe";
            var aSink = new List<string>();

            // ① 不在 queue、沒有判定檔 ⇒ Unknown
            AgentCmdWaitResult aR1 = AgentCmdClient.Wait(aTmp, "lane", aMissing, 2, 0.05,
                aSink.Add, aSink.Add, iPrintOutputs: false);

            // ② 判定檔在（result=Success）⇒ 仍然要 Success
            string aResults = Path.Combine(aTmp, SCP_DataPaths.CmdResultsDirName);
            Directory.CreateDirectory(aResults);
            File.WriteAllText(Path.Combine(aResults, aDone + ".json"),
                              "{\"result\":\"Success\",\"values\":{},\"outputs\":[]}", new UTF8Encoding(false));
            AgentCmdWaitResult aR2 = AgentCmdClient.Wait(aTmp, "lane", aDone, 2, 0.05,
                aSink.Add, aSink.Add, iPrintOutputs: false);

            bool aSaysDontResend = aSink.Exists(s => s.Contains("不要直接重送", StringComparison.Ordinal));
            bool aOk = aR1 == AgentCmdWaitResult.Unknown && (int)aR1 == 7 && aR1.IsIndeterminate()
                       && aR2 == AgentCmdWaitResult.Success && aSaysDontResend;
            string aReading =
                $"🔴 不見了＋無判定檔 ⇒ `{aR1}`（exit {(int)aR1}）、歸在「不知道」那族：{aR1.IsIndeterminate()}"
                + $"／⚠ 反向：判定檔說成功 ⇒ `{aR2}`（⛔ 不是一律回 Unknown）：{aR2 == AgentCmdWaitResult.Success}"
                + $"／訊息叫人先回讀別重送：{aSaysDontResend}"
                + "　⚠ 本格不量「為什麼不見了」—— 成因兩種而長得一樣，那正是它回 Unknown 的理由";
            return new CheckRow(aName, aReading, aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }


    // 區塊職責：`cmd invoke` 的反射調用層（SCP_Invoker）—— 四格，全部是**兩兩不可同形**的那種。
    // 物理意義：這支的用途就是「取一個讀數」與「按一下一支 API」，所以它的失效方式不是崩潰，
    //           是把兩個意思不同的結果講成同一句話。⇒ 每一格都帶一個反向格。
    // 數值影響：純記憶體反射，零 IO（受測體刻意挑 BCL 與 SCP_Core 自己的成員）。
    // 🩸 為什麼這組存在：2026-09-22 新增這支時，`TryBuildStep` 漏了一行 `oStep = aStep` ——
    //   開頭那句 `oStep = null` 已經讓編譯器滿意 ⇒ **「out 已指派」與「指派了對的值」在編譯期同形**，
    //   0 錯 0 警告，而它從第一次呼叫就壞著。抓到它的是第一次真的跑它，⛔ 不是編譯器也不是警覺。
    //   ⇒ 而「我手動跑過 14 條路徑」那件事**不會在未來自動重跑**，所以它得住在這裡。

    /// <summary>void ／ null ／ value（**可能是空字串**）三態要兩兩可分。</summary>
    static CheckRow InvokeValueThreeStates()
    {
        const string aName = "invoke 回傳三態（void／null／空字串不可同形）";
        try
        {
            var aVars = new Dictionary<string, object?>();

            // value：有值而且**是空字串** —— 這一格最容易被寫成 null
            var aEmpty = new SCP_InvokeStep { TypeName = "System.String", MemberName = "Empty", Kind = "field" };
            SCP_InvokeResult aR1 = SCP_Invoker.InvokeOne(aEmpty, aVars);
            bool aEmptyOk = aR1.Success && aR1.ValueKind == "value" && aR1.ValueAsString.Length == 0;

            // null：有回傳值而它是 null（Type.GetType 找不到時回 null，⛔ 不丟例外）
            var aNull = new SCP_InvokeStep { TypeName = "System.Type", MemberName = "GetType", Kind = "method" };
            aNull.ParamTypes.Add("System.String");
            aNull.Args.Add("No.Such.Type.Exists.At.All");
            SCP_InvokeResult aR2 = SCP_Invoker.InvokeOne(aNull, aVars);
            bool aNullOk = aR2.Success && aR2.ValueKind == "null";

            // void：沒有回傳值（setter）
            var aVoid = new SCP_InvokeStep
            { TypeName = "System.Environment", MemberName = "ExitCode", Kind = "property", IsGetter = false };
            aVoid.Args.Add("0");
            SCP_InvokeResult aR3 = SCP_Invoker.InvokeOne(aVoid, aVars);
            bool aVoidOk = aR3.Success && aR3.ValueKind == "void";

            // 🔴 反向格：三個 ValueKind 必須**互不相同** —— 只驗各自等於預期值的話，
            //    有人把三個都改成同一個字串時，上面三格仍然會全綠。
            bool aDistinct = aR1.ValueKind != aR2.ValueKind
                             && aR2.ValueKind != aR3.ValueKind
                             && aR1.ValueKind != aR3.ValueKind;

            bool aOk = aEmptyOk && aNullOk && aVoidOk && aDistinct;
            return new CheckRow(aName,
                $"空字串 ⇒ `{aR1.ValueKind}` 且 value 長度 0：{aEmptyOk}"
                + $"／null ⇒ `{aR2.ValueKind}`：{aNullOk}"
                + $"／setter ⇒ `{aR3.ValueKind}`：{aVoidOk}"
                + $"／🔴 反向：三態互異={aDistinct}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }

    /// <summary>
    /// 「被呼叫的那支 API 自己丟了例外」與「我找錯成員／參數給錯」處置相反 ⇒ 不可同形。
    /// ⚠ 判準是 <c>TargetThrew</c> 旗標，⛔ 不是比對錯誤訊息的字串（字串會被改，旗標不會）。
    /// </summary>
    static CheckRow InvokeSeparatesTargetThrowFromMisuse()
    {
        const string aName = "invoke 分得出「那支 API 炸了」與「我打錯了」";
        try
        {
            var aVars = new Dictionary<string, object?>();

            // ① 被呼叫的程式自己丟例外（指定多載，確定走到真的呼叫）
            var aThrow = new SCP_InvokeStep { TypeName = "System.IO.File", MemberName = "ReadAllText" };
            aThrow.ParamTypes.Add("System.String");
            aThrow.Args.Add(Path.Combine(Path.GetTempPath(),
                "senate-selftest-absent-" + Guid.NewGuid().ToString("N") + ".txt"));
            SCP_InvokeResult aThrew = SCP_Invoker.InvokeOne(aThrow, aVars);
            bool aThrewOk = !aThrew.Success && aThrew.TargetThrew;

            // ② 成員根本不存在 ⇒ 用法錯，**不是**那支 API 炸了
            var aMissing = new SCP_InvokeStep { TypeName = "System.String", MemberName = "NoSuchMemberHere" };
            SCP_InvokeResult aMiss = SCP_Invoker.InvokeOne(aMissing, aVars);
            bool aMissOk = !aMiss.Success && !aMiss.TargetThrew;

            // ③ 型別找不到時，訊息要**把兩個成因都說出來**（名字不對／組件還沒載入）——
            //    那兩個的處置相反，而它們預設都只是「找不到」
            Type? aNone = SCP_Invoker.ResolveType("Totally.Absent.Type." + Guid.NewGuid().ToString("N"), out string aErr);
            bool aTwoCauses = aNone == null
                              && aErr.Contains("還沒被載入", StringComparison.Ordinal)
                              && aErr.Contains("組件", StringComparison.Ordinal);

            // 🔴 反向格：兩個失敗的 Success 都是 false ⇒ 光看它分不出來。旗標必須相異。
            bool aFlagsDiffer = aThrew.TargetThrew != aMiss.TargetThrew;

            bool aOk = aThrewOk && aMissOk && aTwoCauses && aFlagsDiffer;
            return new CheckRow(aName,
                $"API 炸了 ⇒ TargetThrew={aThrew.TargetThrew}：{aThrewOk}"
                + $"／成員不存在 ⇒ TargetThrew={aMiss.TargetThrew}：{aMissOk}"
                + $"／找不到型別的訊息帶兩個成因：{aTwoCauses}"
                + $"／🔴 反向：兩者旗標相異={aFlagsDiffer}（⛔ 兩邊 Success 都是 false，光看它分不出來）",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }

    /// <summary>
    /// 鏈式呼叫（store_as / $name）＋ 中途失敗後**後面那些沒有執行**。
    /// ⚠ 「沒跑」與「跑了而失敗」不可同形 —— 呼叫端據此決定要不要重跑。
    /// </summary>
    static CheckRow InvokeChainStopsAndMarksNotExecuted()
    {
        const string aName = "invoke 鏈式呼叫 ＋ 中途失敗後「沒有執行」不可同形";
        try
        {
            // ① 三步鏈：static method 帶參數 → $var 當引數 → instance property
            var aS1 = new SCP_InvokeStep { TypeName = "System.Type", MemberName = "GetType", StoreAs = "t" };
            aS1.ParamTypes.Add("System.String");
            aS1.Args.Add("SCP.Core.Reflect.SCP_Reflect");
            var aS2 = new SCP_InvokeStep
            { TypeName = "SCP.Core.Reflect.SCP_Reflect", MemberName = "SchemaOf", StoreAs = "schema" };
            aS2.ParamTypes.Add("System.Type");
            aS2.Args.Add("$t");
            var aS3 = new SCP_InvokeStep { Target = "t", MemberName = "FullName", Kind = "property" };

            IReadOnlyList<SCP_InvokeResult> aChain =
                SCP_Invoker.Run(new[] { aS1, aS2, aS3 }, out IReadOnlyDictionary<string, object?> aVars);
            bool aChainOk = aChain.Count == 3 && aChain[0].Success && aChain[1].Success && aChain[2].Success
                            && aChain[2].ValueAsString == "SCP.Core.Reflect.SCP_Reflect"
                            && aVars.ContainsKey("t") && aVars.ContainsKey("schema");

            // ② 中途失敗 ⇒ 只跑到那一步，後面**一步都沒跑**（結果筆數 < 步數就是那個讀數）
            var aB1 = new SCP_InvokeStep { TypeName = "SCP.Core.Reflect.SCP_Reflect", MemberName = "Describe" };
            var aB2 = new SCP_InvokeStep { TypeName = "SCP.Core.Reflect.SCP_Reflect", MemberName = "NoSuchThing" };
            var aB3 = new SCP_InvokeStep { TypeName = "SCP.Core.Reflect.SCP_Reflect", MemberName = "Describe" };
            IReadOnlyList<SCP_InvokeResult> aBroken = SCP_Invoker.Run(new[] { aB1, aB2, aB3 }, out _);
            bool aStopOk = aBroken.Count == 2 && aBroken[0].Success && !aBroken[1].Success;

            // 🔴 反向格：變數表**跑完就沒** —— 分兩次 Run 不得共用。
            //    （CLI 每次呼叫都是新 process；照 Unity 那側的 static 字典寫法會讓 $var 永遠找不到，
            //     而失敗訊息會長得像「變數名打錯了」。）
            IReadOnlyList<SCP_InvokeResult> aSecond = SCP_Invoker.Run(new[] { aS3 }, out _);
            bool aNoLeak = aSecond.Count == 1 && !aSecond[0].Success
                           && aSecond[0].Error.Contains("變數表", StringComparison.Ordinal);

            bool aOk = aChainOk && aStopOk && aNoLeak;
            return new CheckRow(aName,
                $"三步鏈（static＋參數／$var 傳參／instance property）={aChainOk}"
                + $"／中途失敗 ⇒ 跑了 {aBroken.Count}/3 步（後面沒執行）：{aStopOk}"
                + $"／🔴 反向：變數不跨 Run 洩漏={aNoLeak}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }

    /// <summary>
    /// steps 裡認不得的鍵**不得靜默吞掉**（TASK-0109 那族：未知參數靜默取預設值）。
    /// ⚠ 同時驗 camelCase 別名**真的有效** —— 不然「兩種都收」那句話是假的。
    /// </summary>
    static CheckRow InvokeRejectsUnknownStepKey()
    {
        const string aName = "invoke steps 未知鍵被擋 ＋ camelCase 別名真的有效";
        try
        {
            // ① 認不得的鍵 ⇒ 擋下並**指名那個鍵**
            bool aRejected = !SCP_Invoker.TryParseStep(
                "type=System.String|member=Empty|kind=field|bogus=1", out _, out string aErr1)
                && aErr1.Contains("bogus", StringComparison.Ordinal);

            // ② camelCase 與 snake_case 解出**同一個** step
            bool aCamel = SCP_Invoker.TryParseStep(
                "type=System.IO.File|member=ReadAllText|paramTypes=System.String", out SCP_InvokeStep? aA, out _);
            bool aSnake = SCP_Invoker.TryParseStep(
                "type=System.IO.File|member=ReadAllText|param_types=System.String", out SCP_InvokeStep? aB, out _);
            bool aSame = aCamel && aSnake && aA != null && aB != null
                         && aA.ParamTypes.Count == 1 && aB.ParamTypes.Count == 1
                         && aA.ParamTypes[0] == aB.ParamTypes[0];

            // ③ 缺 member ⇒ 擋下（⛔ 不是帶著一個空成員名往下跑）
            bool aNeedsMember = !SCP_Invoker.TryParseStep("type=System.String", out _, out string aErr3)
                                && aErr3.Contains("member", StringComparison.Ordinal);

            // 🔴 反向格：合法的一行**要真的解得出來** —— 否則上面三格可以靠「什麼都擋」全綠
            bool aGoodPasses = SCP_Invoker.TryParseStep(
                "type=System.String|member=Empty|kind=field", out SCP_InvokeStep? aGood, out _)
                && aGood != null && aGood.MemberName == "Empty" && aGood.Kind == "field";

            bool aOk = aRejected && aSame && aNeedsMember && aGoodPasses;
            return new CheckRow(aName,
                $"未知鍵被擋且指名={aRejected}／camelCase ≡ snake_case={aSame}／缺 member 被擋={aNeedsMember}"
                + $"／🔴 反向：合法的一行解得出來={aGoodPasses}（⛔ 不然「什麼都擋」也會全綠）"
                + "　⚠ 射程：camelCase 別名只在 **steps 這一層**；單步 `--arg` 那條路有 ArgSpec 預檢，只收 snake_case",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.Message, CheckResult.Fail); }
    }

}
