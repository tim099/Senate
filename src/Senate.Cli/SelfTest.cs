// 區塊職責：自我對拍 —— 這套東西自己的讀數（不是「應該會動」）。
// 物理意義：SCP_Core 的 JSON 層是共用碼，它的第一個責任是**讀得懂既有資料**：
//           那些 json 是 Unity 端的 UCL JsonData 寫出來的，所以「能不能讀」不是單元測試問題，
//           是拿真檔案去試的問題。⇒ 每一項都印出**讀到什麼**，不是只印 ✓。
// 數值影響：純讀。找不到樣本檔時回報「跳過（沒有樣本）」，**不當成通過** ——
//           「沒測」與「測過而且對」同形是這個 repo 最貴的錯誤形狀。
using Senate.Core;
using SCP.Core.Gui;
using SCP.Core.Json;
using SCP.Core.Reflect;

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
        aRows.Add(StyleRoundTrip());
        aRows.Add(PageStack());
        aRows.Add(TypeSchemaShape());
        aRows.Add(MapperRoundTrip());
        aRows.Add(InspectorEdits());
        aRows.Add(FoldSemantics());
        aRows.AddRange(RealFileRoundTrip(iProjects));
        return aRows;
    }

    /// <summary>「不存在」不可以長得像「空值」—— 讀 Missing 必須丟例外。</summary>
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

    /// <summary>對拍用的假頁 —— 把生命週期呼叫記成可比對的字串。</summary>
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
}
