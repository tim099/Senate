// 區塊職責：CLI 入口 —— `senate init` / `doctor` / `ui`。
// 物理意義：**headless 優先**。這套後台的第一個實用價值是「Unity Editor 關著也能做事」，
//           所以入口是命令列；ImGui 視窗是同一份頁面碼的第二個 renderer，不是唯一入口。
// 數值影響：唯讀的指令不動任何檔（doctor / ui）；init 只在檔案**不存在**時建立，絕不覆寫。
// exit code 語意（刻意分開，讓腳本分辨得出「壞了」與「還沒設定」）：
//   0 = 一切正常   1 = 環境或設定有問題（doctor 判定不通過）
//   2 = 用法錯誤（未知指令）   3 = 設定檔存在但內容壞了
using Senate.Cli.Pages;
using Senate.Core;
using SCP.Core.Gui;
using Senate.Desktop;

namespace Senate.Cli;

public static class Program
{
    public static int Main(string[] iArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string aRepoRoot = RepoRoot();

        // 🩸 雙擊 senate.exe 原本會「閃一下就關」（console app 沒參數 ⇒ 跑 doctor ⇒ 印完結束）。
        //    使用者雙擊的期待是「開介面」，而在終端機裡打同一個指令的期待是「印文字」——
        //    兩者要分辨得出來，不是二選一。判準見 ConsoleHost（GetConsoleProcessList，不是猜）。
        if (iArgs.Length == 0 && ConsoleHost.LaunchedFromExplorer())
        {
            ConsoleHost.HideConsoleWindow();
            return CmdUi(aRepoRoot, new[] { "ui", "--window" });
        }

        string aCmd = iArgs.Length > 0 ? iArgs[0].ToLowerInvariant() : "doctor";

        try
        {
            return aCmd switch
            {
                "init" => CmdInit(aRepoRoot),
                "doctor" => CmdDoctor(aRepoRoot, iArgs),
                "ui" => CmdUi(aRepoRoot, iArgs),
                "selftest" => CmdSelfTest(aRepoRoot, iArgs),
                "--help" or "-h" or "help" => Usage(0),
                _ => Usage(2, $"認不得的指令 '{aCmd}'"),
            };
        }
        catch (InvalidDataException e)
        {
            // 設定檔在、但內容壞了 —— 這跟「還沒設定」是兩件事，exit code 也不同。
            Console.Error.WriteLine($"✗ 設定檔有問題：{e.Message}");
            return 3;
        }
    }

    // ── senate init ───────────────────────────────────────────
    static int CmdInit(string iRepoRoot)
    {
        string aTarget = SenateConfig.DefaultPath(iRepoRoot);
        string aExample = SenateConfig.ExamplePath(iRepoRoot);

        if (File.Exists(aTarget))
        {
            // 已存在就**不動它**並說清楚 —— 「幫你重設」會吃掉別人已經設好的專案清單。
            Console.WriteLine($"・已存在，未覆寫：{aTarget}");
        }
        else if (File.Exists(aExample))
        {
            File.Copy(aExample, aTarget);
            Console.WriteLine($"✓ 已建立：{aTarget}（樣板：{Rel(iRepoRoot, aExample)}）");
            Console.WriteLine("  下一步：編輯它的 projects[]，把要管的專案根目錄填進去。");
        }
        else
        {
            Console.Error.WriteLine($"✗ 找不到樣板 {aExample} —— repo 不完整？");
            return 1;
        }
        Console.WriteLine();
        return CmdDoctor(iRepoRoot, Array.Empty<string>());
    }

    // ── senate doctor ─────────────────────────────────────────
    static int CmdDoctor(string iRepoRoot, string[] iArgs)
    {
        var aModel = new DoctorModel(iRepoRoot);
        var (aEnv, aProjects, aCfgBroken) = (aModel.Env, aModel.Projects, aModel.ConfigBroken);
        // ⚠ 順序有意義：旗標的覆寫要在 Draw **之前**套進 style ——
        //   反過來的話畫面上那行「當前尺寸」印的是覆寫前的值（我第一版就是這樣，
        //   `--scale 9` 警告說夾成 4，畫面卻寫 scale=2）。**尺寸的讀數自己也會說謊。**
        var aStyle = StyleFrom(iArgs, aModel);
        var aUi = new SCP_Ui();
        // doctor 是一次性讀數，但照樣走 controller —— 標題與麵包屑都住那一層，
        // 繞過它就會變成「doctor 的畫面跟 ui 的畫面長得不一樣」而沒人知道為什麼。
        var aCtrl = new SCP_GuiPageController();
        aCtrl.Push(SenatePages.Root(aModel));
        aCtrl.Draw(aUi);
        Console.Write(SCP_GuiTextRenderer.Render(aUi.Root, aStyle));

        foreach (string d in aUi.Diagnostics) Console.Error.WriteLine($"⚠ gui: {d}");

        // 🩸 摘要只能宣告**它真的檢查過的東西**：停用的專案不計入通過條件，
        //    那就必須在摘要裡說出「跳過幾個」—— 不然畫面上明明有一列紅字、
        //    結論卻寫「全部通過」，那就是說法比實作大（本工具第一次跑就自己犯了一次）。
        int aChecked = aProjects.Count(p => p.Enabled);
        int aSkipped = aProjects.Count - aChecked;
        int aBad = aProjects.Count(p => p.Enabled && p.State != ProbeState.Ok);
        bool aOk = aEnv.DotnetSdkVersion != null && aEnv.GitOkForPathspec && !aCfgBroken && aBad == 0;
        Console.WriteLine(aOk
            ? $"⇒ 通過：環境 3 項＋啟用的專案 {aChecked} 個（停用未檢查 {aSkipped} 個）"
            : $"⇒ 不通過：啟用的專案有 {aBad} 個有問題（停用未檢查 {aSkipped} 個）");
        return aOk ? 0 : 1;
    }

    // ── senate ui [--click <id>] ──────────────────────────────
    // 物理意義：`--click <id>` 讓「按下某顆鈕」在**沒有視窗**的環境也能執行 ——
    //           於是互動也有讀數可驗，不是只有靜態畫面。
    static int CmdUi(string iRepoRoot, string[] iArgs)
    {
        bool aWindow = HasFlag(iArgs, "--window");
        string? aShot = ArgValue(iArgs, "--screenshot");
        if (aWindow || aShot != null) return RunWindow(iRepoRoot, iArgs, aShot);

        var aState = UiDriver.Load(iRepoRoot);

        if (HasFlag(iArgs, "--reset"))
        {
            aState = new SCP_GuiState();
            UiDriver.Save(iRepoRoot, aState);
            Console.WriteLine("・session 已清空（欄位與勾選回到頁面預設）");
        }

        var aModel = new DoctorModel(iRepoRoot);
        var aStyle = StyleFrom(iArgs, aModel);

        // 先畫一趟拿到當前的樹（用來驗 id 是否存在）—— 對不存在的 id 下指令必須擋下
        var (aProbeTree, _) = UiDriver.Apply(aModel, aState, null, aStyle);

        string? aClick = ArgValue(iArgs, "--click");
        string? aSet = ArgValue(iArgs, "--set");
        string? aToggle = ArgValue(iArgs, "--toggle");

        foreach (string? aId in new[] { aClick, aToggle })
        {
            if (aId == null) continue;
            if (SCP_GuiQuery.Find(aProbeTree, aId) == null)
            {
                Console.Error.WriteLine($"✗ 畫面上沒有這個 id：{aId}");
                Console.Error.WriteLine("  用 `senate ui --list` 看目前有哪些可互動元件。");
                return 2;   // 靜默失敗會讓「按了沒反應」與「按錯了」同形
            }
        }

        if (aSet != null)
        {
            int eq = aSet.IndexOf('=');
            if (eq <= 0) { Console.Error.WriteLine("✗ --set 的格式是 <id>=<值>"); return 2; }
            string aSetId = aSet.Substring(0, eq);
            string aVal = aSet.Substring(eq + 1);
            if (SCP_GuiQuery.Find(aProbeTree, aSetId) == null)
            {
                Console.Error.WriteLine($"✗ 畫面上沒有這個 id：{aSetId}（`senate ui --list` 看清單）");
                return 2;
            }
            aState.Fields[aSetId] = aVal;
            Console.WriteLine($"・已設定 {aSetId} = {aVal}");
        }

        if (aToggle != null)
        {
            var aElem = SCP_GuiQuery.Find(aProbeTree, aToggle);
            bool aOld = aState.Toggles.TryGetValue(aToggle, out bool v) ? v : (aElem?.On ?? false);
            aState.Toggles[aToggle] = !aOld;
            Console.WriteLine($"・已切換 {aToggle}：{aOld} → {!aOld}");
        }

        var (aTree, aText) = UiDriver.Apply(aModel, aState, aClick, aStyle);
        UiDriver.Save(iRepoRoot, aState);

        if (HasFlag(iArgs, "--list")) { Console.Write(UiDriver.ListElements(aTree, aStyle)); return 0; }
        if (HasFlag(iArgs, "--json")) { Console.WriteLine(SCP_GuiQuery.ToJson(aTree).ToJson()); return 0; }

        Console.Write(aText);
        if (aClick != null) Console.WriteLine($"（已按下：{aClick}）");
        return 0;
    }

    // ── senate ui --window / --screenshot <path> ──────────────
    // 物理意義：**同一份頁面碼**餵給 ImGui renderer —— 頁面一行都沒改。
    //           --screenshot 是給沒有眼睛的人（CI／agent）用的驗收出口：
    //           原生視窗拍不到就沒有讀數，而「中文變方塊」這種事不會報錯。
    static int RunWindow(string iRepoRoot, string[] iArgs, string? iShot)
    {
        var aModel = new DoctorModel(iRepoRoot);   // 讀數在開窗前取好（探測不可以每幀跑）
        var aStyle = StyleFrom(iArgs, aModel);

        // ⭐ 一個 Window 一套 controller（不是全域單例）—— 開第二個視窗時兩邊不會互相蓋。
        //   視窗活著的期間導覽狀態就在記憶體裡，不必像 CLI 那樣存進 session。
        var aCtrl = new SCP_GuiPageController();
        aCtrl.Push(SenatePages.Root(aModel));

        // ⚠ 傳的是**同一顆 style 物件**（不是複本）—— 使用者在頁面上換尺寸時，
        //   renderer 下一幀就讀得到新的間距。字級例外（綁在載入時的 atlas），要重開視窗。
        var aWin = new SenateWindow("Senate", input =>
        {
            var aUi = new SCP_Ui(input);
            aCtrl.Tick();
            aCtrl.Draw(aUi);
            return aUi.Root;
        }, aStyle);

        Console.WriteLine($"介面尺寸：{aStyle.Describe()}");

        if (SenateWindow.FindCjkFont() == null)
            Console.WriteLine("⚠ 找不到中文字型 —— 中文會顯示為方塊（不是字型壞了，是沒載到）");

        try
        {
            aWin.Run(iShot);
            Console.WriteLine($"字型：{aWin.LoadedFonts}");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"✗ 開窗失敗：{e.GetType().Name}: {e.Message}");
            Console.Error.WriteLine("  這台機器有桌面 session 嗎？headless 環境請用 `senate ui`（純文字）。");
            return 1;
        }

        if (iShot != null)
        {
            bool aOk = File.Exists(iShot);
            long aSize = aOk ? new FileInfo(iShot).Length : 0;
            Console.WriteLine(aOk ? $"✓ 截圖已落檔：{iShot}（{aSize} bytes）" : $"✗ 截圖沒有落檔：{iShot}");
            return aOk ? 0 : 1;
        }
        return 0;
    }

    // ── senate selftest ───────────────────────────────────────
    // 物理意義：共用碼（SCP_Core）的 JSON 層必須讀得懂**既有資料** ——
    //           那些檔是 Unity 端寫出來的，所以驗收方式是拿真檔案去跑，不是自己造樣本。
    static int CmdSelfTest(string iRepoRoot, string[] iArgs)
    {
        var aModel = new DoctorModel(iRepoRoot);
        var aStyle = StyleFrom(iArgs, aModel);   // 同 doctor：覆寫先套，再畫
        var aRows = SelfTest.Run(aModel.Projects);

        var aUi = new SCP_Ui();
        aUi.Title("SCP_Core 自我對拍");
        using (aUi.Table("項目", "讀數", "判定"))
        {
            foreach (var r in aRows)
                aUi.TableRow(r.Name, r.Reading, r.Result switch
                {
                    CheckResult.Pass => "✓",
                    CheckResult.Fail => "✗",
                    _ => "— 跳過",
                });
        }
        Console.Write(SCP_GuiTextRenderer.Render(aUi.Root, aStyle));

        int aFail = aRows.Count(r => r.Result == CheckResult.Fail);
        int aSkip = aRows.Count(r => r.Result == CheckResult.Skipped);
        int aPass = aRows.Count(r => r.Result == CheckResult.Pass);
        // 跳過的項目**單獨報**，不併進通過數 —— 「沒測」與「測過而且對」不得同形。
        Console.WriteLine($"⇒ 通過 {aPass}／失敗 {aFail}／跳過 {aSkip}"
            + (aSkip > 0 ? "（跳過的項目沒有讀數，不算通過）" : ""));
        return aFail > 0 ? 1 : 0;
    }

    // ── 讀數收集 ──────────────────────────────────────────────
    // ── 雜項 ──────────────────────────────────────────────────
    /// <summary>
    /// repo 根：從執行檔往上找第一個含 `.git` 的目錄；找不到就用當前目錄。
    /// ⚠ 只找 `.git`，**不猜第二個判準** —— 猜錯的症狀是設定檔寫到別的地方而且不報錯。
    /// </summary>
    static string RepoRoot()
    {
        var aDir = new DirectoryInfo(AppContext.BaseDirectory);
        while (aDir != null)
        {
            if (Directory.Exists(Path.Combine(aDir.FullName, ".git"))) return aDir.FullName;
            aDir = aDir.Parent;
        }
        return Environment.CurrentDirectory;
    }

    static int Width(string[] iArgs)
        => int.TryParse(ArgValue(iArgs, "--width"), out int w) && w >= 40 ? w : SCP_GuiTextRenderer.DefaultWidth;

    /// <summary>
    /// 這一次要用的顯示參數：**設定檔的值**（model 讀好的）＋ 命令列的一次性覆寫。
    /// <para>⚠ `--scale` / `--size` 刻意**不寫回設定檔** —— 一道旗標改掉持久設定，
    /// 下一次沒帶旗標的人會拿到別人上一次的臨時值，而那不會報錯。要改常設值走頁面上的尺寸按鈕。</para>
    /// </summary>
    static SCP_GuiStyle StyleFrom(string[] iArgs, DoctorModel iModel)
    {
        SCP_GuiStyle aStyle = iModel.Style;

        string? aWidth = ArgValue(iArgs, "--width");
        if (aWidth != null)
        {
            if (int.TryParse(aWidth, out int w) && w >= 40) aStyle.TextWidth = w;
            else Console.Error.WriteLine($"⚠ --width {aWidth} 不是 ≥40 的整數 —— 這次用 {aStyle.TextWidth}");
        }

        string? aScale = ArgValue(iArgs, "--scale");
        if (aScale != null)
        {
            if (float.TryParse(aScale, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float s))
            {
                float aGot = aStyle.SetScale(s);
                if (Math.Abs(aGot - s) > 0.001f)
                    Console.Error.WriteLine($"⚠ --scale {s:0.##} 超出範圍，夾成 {aGot:0.##}"
                        + $"（{SCP_GuiStyle.MinScale:0.##}〜{SCP_GuiStyle.MaxScale:0.##}）");
            }
            else Console.Error.WriteLine($"⚠ --scale {aScale} 不是數字 —— 這次用 {aStyle.Scale:0.##}");
        }

        string? aSize = ArgValue(iArgs, "--size");
        if (aSize != null)
        {
            bool aHit = false;
            foreach (SCP_GuiSize s in SCP_GuiStyle.AllSizes)
                if (string.Equals(aSize, s.ToString(), StringComparison.OrdinalIgnoreCase))
                { aStyle.SetPreset(s); aHit = true; break; }
            if (!aHit)
                Console.Error.WriteLine($"⚠ --size {aSize} 認不得（small／medium／big／xl）—— 這次用 {aStyle.Scale:0.##}");
        }
        return aStyle;
    }

    static bool HasFlag(string[] iArgs, string iName)
    {
        foreach (string a in iArgs) if (string.Equals(a, iName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string? ArgValue(string[] iArgs, string iName)
    {
        for (int i = 0; i < iArgs.Length - 1; i++)
            if (string.Equals(iArgs[i], iName, StringComparison.OrdinalIgnoreCase)) return iArgs[i + 1];
        return null;
    }

    static string Rel(string iRoot, string iPath)
        => Path.GetRelativePath(iRoot, iPath).Replace('\\', '/');

    static int Usage(int iCode, string? iError = null)
    {
        if (iError != null) Console.Error.WriteLine($"✗ {iError}");
        Console.WriteLine("""
            senate <command>
              （不給指令 ＝ doctor；**從檔案總管雙擊 ＝ 直接開 GUI 視窗**）

              init                建立 senate.local.json（樣板：config/senate.local.example.json；已存在則不覆寫）
              doctor              印出環境與各專案的讀數（唯讀）。exit 1 ＝ 有項目不通過
              ui                  把後台頁面輸出成純文字
                --list            列出畫面上所有可互動元件（id / 類型 / 現值 / 怎麼操作）
                --click <id>      按下某顆鈕（會實際跑該頁的 handler）
                --set <id>=<值>   填欄位（跨次記住，存在 build/ui_session.json）
                --toggle <id>     切換勾選
                --reset           清空 session
                --json            整棵畫面樹輸出成 JSON（給程式讀）
              ui --window         開原生視窗（ImGui）—— 同一份頁面碼，換一個 renderer
              ui --screenshot <p> 開窗、畫幾幀、把畫面存成 PNG 後結束（給沒有眼睛的人驗收）
              selftest            SCP_Core 共用碼的自我對拍（拿真檔案跑 JSON round-trip）

            共用選項：
              --width <n>       文字輸出寬度（字元格，預設 96）⚠ 不吃 --scale
              --scale <x>       介面縮放（0.5〜4，預設 2.0；本次有效，不寫回設定檔）
              --size <段>       small(1×) / medium(1.5×) / big(2×) / xl(2.5×) —— 同上，本次有效
            常設尺寸改在畫面上（`ui --click doctor/style/big`，會寫回 senate.local.json 的 ui 區塊）
            """);
        return iCode;
    }
}
