// 區塊職責：自我對拍 —— 這套東西自己的讀數（不是「應該會動」）。
// 物理意義：SCP_Core 的 JSON 層是共用碼，它的第一個責任是**讀得懂既有資料**：
//           那些 json 是 Unity 端的 UCL JsonData 寫出來的，所以「能不能讀」不是單元測試問題，
//           是拿真檔案去試的問題。⇒ 每一項都印出**讀到什麼**，不是只印 ✓。
// 數值影響：純讀。找不到樣本檔時回報「跳過（沒有樣本）」，**不當成通過** ——
//           「沒測」與「測過而且對」同形是這個 repo 最貴的錯誤形狀。
using Senate.Core;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Prefs;
using SCP.Core.Skills;
using SCP.Core.Entry;
using SCP.Core.Letters;
using SCP.Core.Reflect;

using Senate.Cli.Pages;

namespace Senate.Cli;

public enum CheckResult { Pass, Fail, Skipped }

public sealed record CheckRow(string Name, string Reading, CheckResult Result);

public static class SelfTest
{
    public static List<CheckRow> Run(IReadOnlyList<ProjectReading> iProjects)
    {
        var aRows = new List<CheckRow>();
        aRows.Add(MissingSemantics());
        aRows.Add(WriterStability());
        aRows.Add(ConfigRoundTripKeepsUnknownKeys());
        aRows.Add(PrefsThreeStates());
        aRows.Add(PrefsKeepsOtherSections());
        aRows.Add(PathsSingleSource());
        aRows.Add(PathRegistryShape());
        aRows.Add(StyleRoundTrip());
        aRows.Add(PageStack());
        aRows.Add(TypeSchemaShape());
        aRows.Add(MapperRoundTrip());
        aRows.Add(InspectorEdits());
        aRows.Add(FoldSemantics());
        aRows.Add(DropdownWidget());
        aRows.Add(PageCatalogShape());
        aRows.Add(PageDiscovery());
        aRows.Add(EntryDocBlock());
        aRows.Add(EntryDocDefects());
        aRows.Add(EntryDocInstallIo());
        aRows.Add(SkillMirror());
        aRows.Add(RowLayout());
        aRows.Add(SourceHint());
        aRows.Add(SourceCapabilityFallback());
        aRows.Add(SourceMessageLifecycle());
        aRows.Add(ServerResultRoundTrip());
        aRows.Add(ErrorReportShape());
        aRows.Add(ProcessStatusClassification());
        aRows.AddRange(RealFileRoundTrip(iProjects));
        aRows.AddRange(RealPersonaScan(iProjects));
        return aRows;
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

    // 區塊職責：反射發現**真的會叫** —— 而且常態下不叫。
    // 物理意義: 🩸 這一格存在的理由：「畫面上沒有紅字」有兩種成因 ——
    //          真的沒漏登記，或**這支檢查根本沒在跑**。兩者在畫面上一模一樣。
    //          ⇒ 一定要有一次「故意漏掉」的讀數，證明它會叫。
    // 數值影響: 純反射，零 IO。
    static CheckRow PageDiscovery()
    {
        var aAsms = new[] { typeof(SCP_GuiToolPage).Assembly, typeof(SenateModel).Assembly };

        // ① 完整目錄 ⇒ 零筆（探針頁靠 [SCP_PageIgnore] 排除，不是靠命名）
        var aModel = new SenateModel(SenateRepoRoot());
        var aFull = SenatePages.BuildCatalog(aModel);
        List<string> aQuiet = aFull.Discover(aAsms);

        // ② 故意漏登記一頁 ⇒ 必須點名它（含 key），而不是靜靜通過
        var aPartial = new SCP_GuiPageCatalog();
        aPartial.Register(SCP_GuiHomePage.PageKey, () => new SCP_GuiHomePage(aModel, aPartial));
        List<string> aLoud = aPartial.Discover(aAsms);
        bool aNamesIt = false;
        foreach (string d in aLoud)
            if (d.Contains(SCP_GuiLoginStatusPage.PageKey, StringComparison.Ordinal)
                && d.Contains("沒有登記", StringComparison.Ordinal)) { aNamesIt = true; break; }

        // ③ [SCP_PageIgnore] 真的有排除（探針頁在同一顆 assembly 裡）
        bool aIgnoreWorks = true;
        foreach (string d in aLoud)
            if (d.Contains("ProbeToolPage", StringComparison.Ordinal)) { aIgnoreWorks = false; break; }

        bool aOk = aQuiet.Count == 0 && aNamesIt && aIgnoreWorks;
        return new CheckRow("頁面發現（反射）",
            $"完整目錄零噪音={aQuiet.Count == 0}（{aQuiet.Count} 筆）／故意漏一頁會點名它={aNamesIt}"
            + $"（漏掉時報 {aLoud.Count} 筆）／[SCP_PageIgnore] 有排除探針={aIgnoreWorks}",
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
}
