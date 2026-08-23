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
        aRows.Add(DropdownWidget());
        aRows.Add(PageCatalogShape());
        aRows.Add(RowLayout());
        aRows.Add(SourceHint());
        aRows.Add(SourceCapabilityFallback());
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
    sealed class ProbeSourcePage : SCP_GuiToolPage
    {
        public ProbeSourcePage() : base() { }
        public override string Key => "probe-source";
        protected override void DrawContent(SCP_Ui iUi) { }
    }

    /// <summary>對拍用的假工具頁。</summary>
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
