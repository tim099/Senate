// 區塊職責：CLI 入口 —— `senate init` / `doctor` / `ui`。
// 物理意義：**headless 優先**。這套後台的第一個實用價值是「Unity Editor 關著也能做事」，
//           所以入口是命令列；ImGui 視窗是同一份頁面碼的第二個 renderer，不是唯一入口。
// 數值影響：唯讀的指令不動任何檔（doctor / ui）；init 只在檔案**不存在**時建立，絕不覆寫。
// exit code 語意（刻意分開，讓腳本分辨得出「壞了」與「還沒設定」）：
//   0 = 一切正常   1 = 環境或設定有問題（doctor 判定不通過）
//   2 = 用法錯誤（未知指令）   3 = 設定檔存在但內容壞了
using Senate.Cli.Pages;
using Senate.Core;
using SCP.Core.Git;
using SCP.Core.Gui;
using SCP.Core.Proc;
using SCP.Core.Paths;
using Senate.Desktop;

namespace Senate.Cli;

public static class Program
{
    public static int Main(string[] iArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string aRepoRoot = RepoRoot();

        // 資料根版面（`SenateData/`）與舊版面搬遷 —— **必須在任何讀設定的動作之前**。
        // 順序不是風格問題：晚一步跑，前面那些讀取端就會在空的新位置讀到「沒設定過」，
        // 而那個狀態看起來完全正常（三態同形）。搬完一定印出來，⛔ 不做靜默搬檔。
        SenatePaths.EnsureDirectories(aRepoRoot);
        var aMigration = SenateDataMigration.Run(aRepoRoot);
        if (SenateDataMigration.NeedsAttention(aMigration))
        {
            Console.Error.WriteLine($"[SenateData] 舊版面搬遷（→ {SenatePaths.DataRoot(aRepoRoot)}）：");
            foreach (var aStep in aMigration)
            {
                string aMark = aStep.Outcome switch
                {
                    SenateMigrationOutcome.Moved => "✓ 已搬",
                    SenateMigrationOutcome.Conflict => "⚠ 衝突",
                    SenateMigrationOutcome.Failed => "✗ 失敗",
                    _ => "",
                };
                if (aMark.Length > 0) Console.Error.WriteLine($"  {aMark}  {aStep.What}　{aStep.Detail}");
            }
        }

        // 宿主能力：共用層想要「開啟原始碼所在位置」那顆鈕，但它不准碰 OS ⇒ 由這裡掛實作。
        // ⚠ 沒掛的話那顆鈕**根本不會畫**（不是畫一顆按了沒事的鈕）—— 見 SCP_GuiHost。
        SCP_GuiHost.RevealInFileManager = SenateShell.MakeRevealer(aRepoRoot);

        // 宿主能力：child process 的登記中心。共用層不知道狀態該落在哪 ⇒ 由這裡指定。
        // ⚠ 沒 Configure 的話整個服務停用（每顆 process 都沒人接管得到），所以掛在最前面、
        //   不掛在「會用到它的那個指令」裡 —— 漏掛的症狀是孤兒 process，而那不會當場叫。
        // 落點在 SenateData/runtime/：這是 runtime 狀態不是設定 —— 可隨時刪，且應該被清。
        SCP_ProcessRegistry.Configure(SenatePaths.ProcessRegistry(aRepoRoot));
        SCP_ProcessRegistry.Warn = iMessage => Console.Error.WriteLine($"⚠ {iMessage}");
        // CLI 是「一次呼叫一顆 process」⇒ 每次啟動就是一個**一定會經過**的時機。
        // 不清的話殘檔會無聲累積，而堆積出來的畫面跟屍潮長得一樣，一樣會訓練人忽略那張表。
        try { SCP_ProcessRegistry.CleanupStale(); }
        catch (Exception e) { Console.Error.WriteLine($"⚠ process 登記清理失敗：{e.Message}"); }
        // 退路：開不了檔案總管的宿主至少要能把類別名複製起來（見 SCP_GuiToolPage.ShowCopyClassButton）
        // ⭐ 兩個方向都走 SenateClipboard（Windows Win32、其他平台委給 SenateShell 的 process 路徑）。
        //   原本 Copy 走 clip.exe、Read 走 PowerShell，各要 300〜500ms —— 掛按鈕還可以，
        //   但 ImGui 的 Ctrl+V callback 是「使用者按下組合鍵的那一幀」，卡半秒會被讀成視窗當掉。
        //   ⇒ 收斂成一份快的實作，按鈕與 callback 吃同一條路（不會有「鈕能貼、Ctrl+V 不能」的分岔）。
        SCP_GuiHost.CopyToClipboard = SenateClipboard.Write;
        SCP_GuiHost.ReadClipboard = SenateClipboard.Read;

        // 區塊職責：submodule 目標 branch 的**家規** —— 「資料夾名前綴 → 該追哪條 branch」。
        // 物理意義：這是專案的命名慣例，不是 git 的性質 ⇒ SCP_GitSubmodule 刻意讓它預設為空、
        //           由宿主宣告（寫死在共用層等於把一個專案的家規變成所有專案的預設）。
        // 🩸 而 Senate 原本**沒有宣告這一半**，於是對 LY 跑的時候：
        //    `Assets/Plugins/UCL_Core` 目前在 Dev，而啟發式算出 master（走到「其餘 → master」那條）
        //    ⇒ 表格顯示 `⚠ Dev / 目標 master`，checkout 會被「HEAD 不在 master 歷史上」擋下並跳過。
        //    不會切錯（安全線攔住了），但那一顆**永遠對不齊、永遠被跳過**，
        //    而「被跳過」的訊息看起來完全像盡責。⇒ 機制在共用層、規則在這裡，兩半都要有人接。
        // ⚠ 前綴比對區分大小寫、先命中先贏。要對非 UCL 系的 repo 停用就拿掉這一行；
        //   之後若需要 per-repo 不同的家規，正解是搬進 senate.local.json（反射三層會自動畫出欄位）。
        SCP_GitSubmodule.PrefixBranchRules.Add(new KeyValuePair<string, string>("UCL_", "Dev"));

        // 宿主能力：SCP_CMD 的訊息要教人「照著打就會動」的指令 ⇒ 動詞由宿主宣告。
        // ⚠ 共用層不准知道任何宿主的動詞（它預設只會說裸的 `cmd`）——
        //   沒掛這一行的症狀是**錯誤訊息教人打一個在這個宿主上不存在的指令**，
        //   而那不會編譯失敗、不會有人回報，只會讓照著訊息打的人以為自己打錯。
        SCP.Core.Cmd.SCP_CmdRegistry.InvocationHint = "senate cmd";

        // 宿主能力②：委派型 Cmd 要知道「派給哪個專案」，而共用層與 Cmd 本身都**不推導路徑**。
        // ⇒ 設定來源由這裡裝上（同上一條的形狀：能力由宿主宣告，不由下層去找）。
        // Server 委派也一樣：Cmd 不知道 Server 根在哪，由宿主給 repo 根（ServerDelegateCmd.RepoRootProvider）。
        // 沒裝的症狀跟上面那格同形：委派 Cmd 回 70 並說「宿主沒裝上」—— 不會靜默猜一個根。
        ServerDelegateCmd.RepoRootProvider = () => aRepoRoot;
        UnityDelegateCmd.ConfigProvider = () =>
        {
            string aCfgPath = SenateConfig.DefaultPath(aRepoRoot);
            return (SenateConfig.Load(aCfgPath), aCfgPath);
        };

        // 宿主能力③：畫布閘（付款／自由時間資格／分享）——本宿主的實作是「派給 Unity Editor」。
        // ⚠ 工廠吃資料根當參數，**不自己解析** —— Cmd 吃的 `--arg data_root` 與閘用的根若是兩個來源，
        //   不一致時會安靜地把付款派到另一個專案（錢那邊扣、像素這邊落）。
        // ⚠ 定語不由這裡宣告：閘自己從資料根算專案標籤。
        //   實測過相反的做法 —— 傳 repo 根的 basename 會印出「@ Senate（D:/Unity/Bar/…）」，
        //   而那是兩個來源拼出來的定語，比沒有定語更毒（它有出處的樣子）。
        SCP.Core.Canvas.SCP_CanvasGatewayHost.Factory = aDataRoot => new SenateCanvasGateway(aDataRoot);

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
                "submodule" => CmdSubmodule(aRepoRoot, iArgs),
                "ucmd" => CmdAgent(aRepoRoot, iArgs),   // Unity 那套（AgentCommand，走檔案協議）
                "cmd" => CmdScp(aRepoRoot, iArgs),      // SCP_CMD（直接呼叫 C#，不依賴 Unity）
                "selftest" => CmdSelfTest(aRepoRoot, iArgs),
                "server" => ServerCommand.Run(aRepoRoot, iArgs),   // 常駐 Server 生命週期（TASK-0102；前景、永駐、手動啟動）
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
        var aModel = new SenateModel(iRepoRoot);
        var (aEnv, aProjects, aCfgBroken) = (aModel.Env, aModel.Projects, aModel.ConfigBroken);
        // ⚠ 順序有意義：旗標的覆寫要在 Draw **之前**套進 style ——
        //   反過來的話畫面上那行「當前尺寸」印的是覆寫前的值（我第一版就是這樣，
        //   `--scale 9` 警告說夾成 4，畫面卻寫 scale=2）。**尺寸的讀數自己也會說謊。**
        var aStyle = StyleFrom(iArgs, aModel);
        var aUi = new SCP_Ui();
        // doctor 是一次性讀數，但照樣走 controller —— 標題與麵包屑都住那一層，
        // 繞過它就會變成「doctor 的畫面跟 ui 的畫面長得不一樣」而沒人知道為什麼。
        // ⚠ 這裡 push 的是**診斷頁**不是根頁：`senate doctor` 這道指令的意思是「印環境讀數」，
        //   根頁換成入口頁之後如果跟著換，那道指令會安靜地變成印一份選單。
        var aCtrl = new SCP_GuiPageController();
        var aCatalog = SenatePages.BuildCatalog(aModel);
        aCtrl.Push(aCatalog.Create(DoctorPage.PageKey)
            ?? throw new InvalidOperationException("頁面目錄裡沒有 doctor 頁"));
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

        // ⭐ `--soak` **隱含開窗** —— 它量的就是「真視窗轉得動嗎」，在文字模式下沒有意義。
        // 🩸 2026-09-03：漏了這一格時 `ui --soak 4` 靜默掉進文字模式 ——
        //   印出一張文字畫面、`exit 0`、**一個讀數都沒有**。
        //   ⇒ 那正是它要抓的形狀（成功與沒做同形），而它發生在這支工具自己身上。
        bool aSoak = ArgValue(iArgs, "--soak") != null;
        if (aWindow || aSoak || aShot != null) return RunWindow(iRepoRoot, iArgs, aShot);

        var aState = UiDriver.Load(iRepoRoot);

        if (HasFlag(iArgs, "--reset"))
        {
            aState = new SCP_GuiState();
            UiDriver.Save(iRepoRoot, aState);
            Console.WriteLine("・session 已清空（欄位與勾選回到頁面預設）");
        }

        var aModel = new SenateModel(iRepoRoot);
        var aStyle = StyleFrom(iArgs, aModel);
        var aCatalog = SenatePages.BuildCatalog(aModel);

        // 先畫一趟拿到當前的樹（用來驗 id 是否存在）—— 對不存在的 id 下指令必須擋下
        var (aProbeTree, _) = UiDriver.Apply(aCatalog, aState, null, aStyle);

        string? aClick = ArgValue(iArgs, "--click");
        string? aSet = ArgValue(iArgs, "--set");
        string? aToggle = ArgValue(iArgs, "--toggle");
        string? aFold = ArgValue(iArgs, "--fold");

        foreach (string? aId in new[] { aClick, aToggle, aFold })
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

        if (aFold != null)
        {
            var aElem = SCP_GuiQuery.Find(aProbeTree, aFold);
            bool aOld = aState.Folds.TryGetValue(aFold, out bool v) ? v : (aElem?.On ?? true);
            aState.Folds[aFold] = !aOld;
            Console.WriteLine($"・已{(aOld ? "收合" : "展開")} {aFold}");
        }

        var (aTree, aText) = UiDriver.Apply(aCatalog, aState, aClick, aStyle);
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
        var aModel = new SenateModel(iRepoRoot);   // 讀數在開窗前取好（探測不可以每幀跑）
        var aStyle = StyleFrom(iArgs, aModel);

        // ⭐ 一個 Window 一套 controller（不是全域單例）—— 開第二個視窗時兩邊不會互相蓋。
        //   視窗活著的期間導覽狀態就在記憶體裡，不必像 CLI 那樣存進 session。
        var aCatalog = SenatePages.BuildCatalog(aModel);
        var aCtrl = new SCP_GuiPageController();
        aCtrl.Push(SenatePages.Root(aCatalog));

        // `--page <key>` 直接停在某一頁。⭐ 存在的理由是**驗收**：
        //    視窗裡的頁面本來只有人點得到（截圖模式沒有點擊入口），
        //    於是「那一頁在視窗裡畫不畫得出來」就沒有讀數。這條旗標把它變成有。
        string? aPage = ArgValue(iArgs, "--page");
        if (aPage != null)
        {
            SCP_GuiPage? aTarget = aCatalog.Create(aPage);
            if (aTarget == null)
            {
                Console.Error.WriteLine($"✗ 認不得的頁面 key：{aPage}");
                // 清單從目錄印，不寫死 —— 寫死的那一行會在加頁的時候安靜地過期
                Console.Error.WriteLine($"  現有：{string.Join(" / ", aCatalog.AllKeys)}");
                return 2;   // 靜默開在首頁會讓「打錯 key」與「那頁是空的」同形
            }
            if (aTarget.Key != SenatePages.RootKey) aCtrl.Push(aTarget);
        }

        // ⚠ 傳的是**同一顆 style 物件**（不是複本）—— 使用者在頁面上換尺寸時，
        //   renderer 下一幀就讀得到新的間距。字級例外（綁在載入時的 atlas），要重開視窗。
        var aWin = new SenateWindow("Senate", input =>
        {
            var aUi = new SCP_Ui(input);
            aCtrl.Tick();
            aCtrl.Draw(aUi);
            return aUi;   // ⚠ 回整個 SCP_Ui 不是只回 Root —— 頁面要求的欄位寫入掛在它身上
        }, aStyle);

        // ImGui 版面檔的落點由宿主指定 —— 不指定的話 ImGui 寫的是**相對 cwd** 的 imgui.ini，
        // 於是同一顆 exe 從不同目錄啟動會讀寫不同份版面，而症狀只是「版面有時候會不見」。
        aWin.IniPath = SenatePaths.ImGuiIni(iRepoRoot);

        // 🩸 視窗**預設不接續** CLI session。
        //    第一版是無條件接續，於是我在終端機測試時點開的下拉，變成 Tim 開窗時「預設就是展開的」——
        //    那不是他的操作，是我的殘留狀態漏過了驅動端的邊界。
        //    ⇒ 只有在**要驗收**的時候才接（截圖模式自動開，或顯式 --seed-session）。
        if (iShot != null || HasFlag(iArgs, "--seed-session"))
        {
            aWin.Seed(UiDriver.Load(iRepoRoot));
            Console.WriteLine($"・已接續 CLI session（{UiDriver.SessionPath(iRepoRoot)}）的欄位／勾選／摺疊");
        }

        // ⭐ **這一行決定長時間工作跑在哪裡。** 視窗是連續 render loop ⇒ 頁面可以把批次丟到背景、
        //   每幀顯示進度。純文字那一側**不設**（預設 false）⇒ 頁面會同步跑完才返回，
        //   因為那一側畫幾趟就結束 process，丟到背景等於什麼都不會發生。
        //   ⚠ 設在這裡而不是 Main 開頭：它是**這一種宿主**的性質，不是這台機器的性質
        //   （同一支 exe 兩條路，`SCP_GuiHost.RevealInFileManager` 那種才屬於 Main）。
        SCP_GuiHost.RedrawsContinuously = true;

        // 鍵盤／剪貼簿診斷 —— 「Ctrl+V 沒反應」有三個斷點，而它們在畫面上長得一樣（見 DrawKeyDebug）。
        aWin.KeyDebug = HasFlag(iArgs, "--keydebug");
        if (aWin.KeyDebug) Console.WriteLine("・keydebug 開著 —— 畫面底部會多一行鍵盤／剪貼簿讀數");

        // ⭐ soak：開真視窗**轉一段時間**再收工。截圖的 8 幀證明「畫得出來」，
        //   證明不了「每幀成本」與「背景工作跑的時候畫面凍不凍」——
        //   而那兩件事壞掉的樣子是畫面看起來正常、只是不動，跟截圖同形。
        if (ArgValue(iArgs, "--soak") is { } aSoakText)
        {
            if (!double.TryParse(aSoakText, out double aSoak) || aSoak <= 0)
            {
                Console.Error.WriteLine($"✗ --soak 要一個正的秒數（收到 '{aSoakText}'）");
                return 2;
            }
            aWin.SoakSeconds = aSoak;
            Console.WriteLine($"・soak 開著 —— 視窗會真的轉 {aSoak:0.#} 秒再收工，收工時印幀數讀數");
        }

        Console.WriteLine($"介面尺寸：{aStyle.Describe()}");

        if (SenateWindow.FindCjkFont() == null)
            Console.WriteLine("⚠ 找不到中文字型 —— 中文會顯示為方塊（不是字型壞了，是沒載到）");

        try
        {
            aWin.Run(iShot);
            if (aWin.SoakReading is { } aReading) Console.WriteLine(aReading);
            Console.WriteLine($"字型：{aWin.LoadedFonts}");
            Console.WriteLine($"{aWin.ClipboardStatus}");
            Console.WriteLine($"{aWin.WindowIconStatus}");
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
        var aModel = new SenateModel(iRepoRoot);
        var aStyle = StyleFrom(iArgs, aModel);   // 同 doctor：覆寫先套，再畫
        var aRows = SelfTest.Run(aModel.Projects);

        // 剪貼簿 round-trip 是 **opt-in**（`--clipboard`）。
        // ⚠ 為什麼不進預設清單：它會**覆蓋使用者的剪貼簿**，而那是不可逆的
        //   （沒有人保存舊內容；而且舊內容可能是圖片，寫回去也還原不了）。
        //   一個「跑一下自我檢查」的指令不該有這種副作用 ——
        //   但這條路又必須有讀數，否則「Ctrl+V 到底通不通」只能靠人去按。
        if (HasFlag(iArgs, "--clipboard"))
        {
            aRows.Add(ClipboardRoundTrip());
            aRows.Add(ImGuiClipboardRoundTrip());
        }

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

    /// <summary>
    /// 剪貼簿 round-trip —— 寫進去、讀回來、逐字比對。
    /// <para>⚠ 這是 <c>SCP_GuiHost</c> 那兩個委派**實際走的同一條路**
    /// （<see cref="SenateClipboard"/>），所以它同時是「📋 貼上」鈕與
    /// ImGui 的 Ctrl+C／Ctrl+V callback 的讀數 —— 三者吃同一份實作，不會有其中一個偷偷壞掉。</para>
    /// <para>⚠ 測試字串刻意含**中文與符號**：Win32 走 <c>CF_UNICODETEXT</c>（UTF-16），
    /// 而 ImGui 那一端是 UTF-8 ⇒ 兩次轉碼。只用 ASCII 測的話，
    /// 編碼漏掉的那一格會等到有人貼中文路徑才炸。</para>
    /// </summary>
    static CheckRow ClipboardRoundTrip()
    {
        const string aProbe = "剪貼簿對拍 ✓ ⇒ D:/Unity/LY ⚠ 測試";
        string aWrote = SenateClipboard.Write(aProbe);
        SCP_ClipboardRead aRead = SenateClipboard.Read();

        if (!aRead.Ok)
            return new CheckRow("剪貼簿 round-trip", $"寫入={aWrote}／讀取失敗={aRead.Message}", CheckResult.Fail);
        if (aRead.Text != aProbe)
            return new CheckRow("剪貼簿 round-trip",
                $"讀回來的字**不一樣**（寫 {aProbe.Length} 字元、讀 {aRead.Text.Length} 字元）"
                + $"—— 寫入={aWrote}", CheckResult.Fail);

        return new CheckRow("剪貼簿 round-trip",
            $"寫入→讀回逐字相同（{aProbe.Length} 字元，含中文與符號）／{aRead.Message}"
            + "　⚠ 使用者原本的剪貼簿內容已被覆蓋（本項是 --clipboard 才跑的）",
            CheckResult.Pass);
    }

    /// <summary>
    /// 走 ImGui 那**兩個 callback 本身**的 round-trip —— 驗 marshalling，不是驗剪貼簿。
    /// <para>⚠ 這一項與上一項的分工要講清楚：上一項驗「剪貼簿讀寫通不通」，
    /// 這一項驗「ImGui 那一側的介面對不對」（UTF-8 編碼／NUL 結尾／記憶體還活著）。
    /// 兩者都過之後，唯一沒有讀數的就只剩「ImGui 真的會在 Ctrl+V 時呼叫它」——
    /// 那一格要人按一次鍵盤，程式驗不到，所以**不假裝它被驗過**。</para>
    /// </summary>
    static CheckRow ImGuiClipboardRoundTrip()
    {
        const string aProbe = "ImGui callback 對拍 ✓ ⇒ 中文 D:/Unity/LY";
        var (aOk, aReading) = Senate.Desktop.ImGuiClipboardBridge.SelfCheck(aProbe);
        return new CheckRow("ImGui 剪貼簿 callback",
            aReading + "　⚠ 「按 Ctrl+V 時 ImGui 會不會呼叫它」要人親手按一次，本項驗不到",
            aOk ? CheckResult.Pass : CheckResult.Fail);
    }

    // ── senate submodule status / sync ────────────────────────
    // 區塊職責：submodule 的**寫入端**（唯讀那半是 status，跟頁面同一份掃描層）。
    // 物理意義：為什麼寫入端在 CLI 而不在頁面上 —— CLI 一次呼叫一顆 process，
    //           而一輪 fetch＋pull＋push 跨十幾個 submodule 是分鐘級的事。
    //           塞進「按鈕按下去那一幀」在 CLI 模式做不到，會變成一顆按了沒事的鈕，
    //           而那比沒有鈕糟。這裡同步跑完、印報告、用 exit code 說結果。
    // 數值影響：status 唯讀。sync 會移動各 submodule 的 HEAD（--checkout / --pull）
    //           與寫遠端（--push）。
    // exit：0 ＝ 沒有失敗　1 ＝ 有失敗　2 ＝ 用法錯誤
    static int CmdSubmodule(string iRepoRoot, string[] iArgs)
    {
        string aSub = iArgs.Length > 1 ? iArgs[1].ToLowerInvariant() : "";
        if (aSub != "status" && aSub != "sync")
            return Usage(2, $"submodule 要 status 或 sync（收到 '{(aSub.Length == 0 ? "(空)" : aSub)}'）");

        bool aWrite = aSub == "sync";
        bool aCheckout = HasFlag(iArgs, "--checkout");
        bool aPull = HasFlag(iArgs, "--pull");
        bool aPush = HasFlag(iArgs, "--push");
        bool aDryRun = HasFlag(iArgs, "--dry-run");

        // ⚠ 目標 repo：sync **不給預設值**。
        //   🩸 UCL 那邊的血證（2026-08-11）：設定漂移讓工具在 B 專案裡誠實地對 A 專案動手、
        //      回報一整排 ✓，而 B 的 submodule 一個位元組都沒動 —— 綠燈全亮，量到的是別的 repo。
        //   ⇒ 會寫東西的指令必須**顯式**指定對象；唯讀的 status 才給預設（猜錯也不會壞東西）。
        string? aRoot = ResolveSubmoduleRoot(iRepoRoot, iArgs, aWrite, out string aRootWhy);
        // 提示要跟著**使用者剛打的**子指令走 —— 對 status 印 sync 的用法就是指錯地方，
        // 而指錯地方的提示比沒有提示糟（它讓人照著做，然後撞第二次）。
        if (aRoot == null) return SubmoduleUsageError(aRootWhy, $"senate submodule {aSub} --root <repo 路徑>");

        if (aWrite && !aCheckout && !aPull && !aPush)
            return SubmoduleUsageError("sync 要至少一個動作：--checkout / --pull / --push", "都不給就等於 status —— 那條路唯讀，直接跑 senate submodule status");

        // push 會寫遠端 ⇒ 要一個明示。互動式確認在這裡做不到（stdin 是 null device），
        // 所以確認的形態是「再打四個字」而不是「按 Enter」。
        if (aWrite && aPush && !aDryRun && !HasFlag(iArgs, "--yes"))
            return SubmoduleUsageError("--push 會把本地 commit 寫到遠端 —— 需要 --yes 明示", "先看它要做什麼：同一條指令加 --dry-run（不動任何東西）");

        string? aBranch = ArgValue(iArgs, "--branch");
        bool aFetch = HasFlag(iArgs, "--fetch");
        bool aIncludeRoot = HasFlag(iArgs, "--include-root");
        bool aPushAll = HasFlag(iArgs, "--push-all-remotes");
        List<string> aOnly = ArgValues(iArgs, "--only");

        // 逐項覆寫：`--set-branch <path>=<branch>`（可重複）。四層解析裡最高優先的那一層。
        // 為什麼需要它：`--branch` 只有**全域**一格，而一個 repo 底下的 submodule 常常各追不同分支
        //（UCL_* 追 Dev、其餘追 master）。沒有這一格的話，「這顆要切別的」只能一顆一顆分開跑，
        // 而分開跑會破壞 push 的深→淺順序（那條不變量是**整批**成立的，不是逐顆成立的）。
        // ⚠ 語法錯（缺 `=`）立刻擋下並說出收到什麼 —— 靜默忽略一個打錯的覆寫，
        //   會讓使用者以為自己指定了目標，然後看著它被切到另一條「看起來也合理」的分支。
        var aOverrides = new Dictionary<string, string>();
        foreach (string aPair in ArgValues(iArgs, "--set-branch"))
        {
            int aEq = aPair.IndexOf('=');
            if (aEq <= 0 || aEq == aPair.Length - 1)
                return SubmoduleUsageError($"--set-branch 要 <path>=<branch> 的形狀（收到 '{aPair}'）",
                    "例：--set-branch Assets/Plugins/UCL_Core=Dev");
            string aOvPath = aPair.Substring(0, aEq);
            string aOvBranch = aPair.Substring(aEq + 1);
            if (aOverrides.TryGetValue(aOvPath, out string? aPrev) && aPrev != aOvBranch)
                return SubmoduleUsageError($"--set-branch 對同一個路徑給了兩個不同的分支（{aOvPath}：{aPrev} / {aOvBranch}）",
                    "同一顆 submodule 只能有一個目標 —— 留下要的那一個");
            aOverrides[aOvPath] = aOvBranch;
        }

        Console.WriteLine($"· 對象：{aRoot}　（{aRootWhy}）");
        if (aBranch != null) Console.WriteLine($"· 全域預設 branch：{aBranch}");
        if (aOnly.Count > 0) Console.WriteLine($"· 只處理：{string.Join(" , ", aOnly)}");
        foreach (var aOv in aOverrides) Console.WriteLine($"· 指定 branch：{aOv.Key} → {aOv.Value}");

        var aScan = SubmoduleScan.Scan(aRoot, aFetch, aBranch, aOverrides,
            iProgress: aFetch ? p => Console.Error.WriteLine($"  … fetch {p}") : null);
        if (!aScan.Ok)
        {
            Console.Error.WriteLine($"✗ {aScan.Error}");
            return 1;
        }
        foreach (string aWarning in aScan.Warnings) Console.Error.WriteLine(aWarning);

        // 掃描結果先攤開 —— 動手之前一定要先看得到「它認為每一顆該去哪」。
        PrintScan(aScan, aFetch);

        // 指到不存在的路徑要擋下：靜默處理 0 顆會長得像「都做完了」。
        // ⚠ `--set-branch` 也要驗，而且理由更硬：`--only` 打錯是「少做一顆」（看得出來），
        //   `--set-branch` 打錯是「那顆照舊用啟發式的目標」—— 它會**照樣成功**，
        //   只是切到了另一條分支，而報告上是一排 ✓。
        if (aOnly.Count > 0 || aOverrides.Count > 0)
        {
            var aKnown = new HashSet<string>();
            foreach (var aItem in aScan.Items) aKnown.Add(aItem.Entry.Path);

            var aMissing = aOnly.FindAll(p => !aKnown.Contains(p));
            if (aMissing.Count > 0)
                return SubmoduleUsageError($"--only 指到不存在的 submodule：{string.Join(" , ", aMissing)}", "上面那份清單就是這個 repo 真的有的 submodule");

            var aMissingOv = new List<string>();
            foreach (var aOv in aOverrides) if (!aKnown.Contains(aOv.Key)) aMissingOv.Add(aOv.Key);
            if (aMissingOv.Count > 0)
                return SubmoduleUsageError($"--set-branch 指到不存在的 submodule：{string.Join(" , ", aMissingOv)}", "上面那份清單就是這個 repo 真的有的 submodule（路徑要跟它逐字一樣）");
        }

        if (!aWrite) return 0;

        var aOptions = new SCP_GitSyncOptions
        {
            Checkout = aCheckout,
            Pull = aPull,
            Push = aPush,
            PushAllRemotes = aPushAll,
        };

        if (aDryRun)
        {
            Console.WriteLine();
            Console.WriteLine("── dry-run：以下是**打算**做的事，這一輪不會動任何東西 ──");
            string aPlan = (aCheckout ? "切到目標 branch → " : "")
                           + (aPull ? "pull（ff-only）→ " : "")
                           + (aPush ? (aPushAll ? "push（該 repo 所有 remote）" : "push（origin）") : "");
            Console.WriteLine($"  動作：{aPlan.TrimEnd(' ', '→', ' ')}");
            Console.WriteLine($"  順序：由深到淺（巢狀最深先動）{(aIncludeRoot ? "，root 最後（root 永不切 branch）" : "，不含 root")}");
            Console.WriteLine("  ⚠ dirty / detached 有未合併 commit / 解析不到目標的，動手當下會現場重問並跳過 ——");
            Console.WriteLine("    所以這份清單是「範圍」，不是「保證會成功的名單」。");
            return 0;
        }

        Console.WriteLine();
        Console.WriteLine("── 執行 ──");
        var aRows = SubmoduleScan.RunBatch(aScan, aOptions, aIncludeRoot,
            aOnly.Count > 0 ? aOnly : null, iLog: Console.WriteLine);

        int aOk = 0, aSkip = 0, aFail = 0;
        foreach (var aRow in aRows)
        {
            if (aRow.Outcome == SCP_GitSyncOutcome.Ok) aOk++;
            else if (aRow.Outcome == SCP_GitSyncOutcome.Skipped) aSkip++;
            else aFail++;
        }

        Console.WriteLine();
        Console.WriteLine($"⇒ ✓{aOk} ⏭{aSkip} ✗{aFail}");
        foreach (var aRow in aRows)
        {
            if (aRow.Outcome != SCP_GitSyncOutcome.Ok) Console.WriteLine($"   {aRow.Label}：{aRow.Summary}");
        }
        // ⚠ 跳過**不算失敗**（那是刻意的保護），但它一定要出現在上面那一行裡 ——
        //   只印「✓ 完成」會讓「跳過 8 顆」看起來像「做完 8 顆」。
        return aFail > 0 ? 1 : 0;
    }

    // ── senate ucmd ──────────────────────────────────────────
    // 區塊職責：AgentCommands 派遣的 CLI 入口 —— run / status 兩個子動作。
    // 物理意義：run_cmd.py 的 C# 對應（協議本體在 Senate.Core/AgentCmdClient.cs）——
    //           **沒有 python 的環境（Codex）也能派 Cmd**。對象專案由 senate.local.json 的
    //           projects[] 指定（--project 挑名字；只有一個啟用專案時可省略）。
    // 數值影響：run 會寫目標專案的 queue/trigger（Editor 端接手執行）；status 唯讀。
    //           exit code 與 run_cmd.py 對齊：0 成功／2 失敗或用法錯／3 逾時。
    // ⚠ v1 刻意不做的（跟 run_cmd.py 的差距，別當成壞掉）：
    //   schema 預檢與 type 別名（fail-open —— 打錯 type 由 Editor 端擋並附 did-you-mean）、
    //   Tavern wait-reply 握手引擎、op=post 成功後的 catch-up cursor 提交。
    //   後兩格對「拿 senate 發酒館訊息」的人是真差距 —— 要做的時候去讀 run_cmd.py 對應段。
    static int CmdAgent(string iRepoRoot, string[] iArgs)
    {
        string aSub = iArgs.Length > 1 ? iArgs[1].ToLowerInvariant() : "";
        if (aSub != "run" && aSub != "status")
            return AgentUsageError($"cmd 要 run 或 status（收到 '{(aSub.Length == 0 ? "(空)" : aSub)}'）",
                "senate ucmd run <CmdType> [--project <名>] [--persona <p>] [--arg k=v]… [--arg-file k=<路徑>]…");

        // ── 對象專案解析：--project 名字 ＞ 唯一啟用專案自動選（會說出來）＞ 擋下 ──
        // ⚠ 解析本體在 UnityTargetResolver（Senate.Core）—— 委派型 SCP_Cmd 問的是同一個問題，
        //   而**兩份實作會在「多專案時該不該猜」上給出不同答案**，猜錯那次 Cmd 會在別人的
        //   Editor 上真的執行。這裡只負責把結果轉成 CLI 的輸出形狀。
        string aCfgPath = SenateConfig.DefaultPath(iRepoRoot);
        UnityTargetResolution aResolved = UnityTargetResolver.Resolve(
            SenateConfig.Load(aCfgPath), aCfgPath, ArgValue(iArgs, "--project"));
        if (!aResolved.Ok) return AgentUsageError(aResolved.Error, aResolved.Hint);
        UnityTarget aTarget = aResolved.Target!;
        if (aTarget.SelectionNote.Length > 0) Console.WriteLine("· " + aTarget.SelectionNote);
        string aDataRoot = aTarget.DataRoot;

        string? aPersona = ArgValue(iArgs, "--persona");

        if (aSub == "status")
        {
            // 唯讀：印 trigger 狀態與 queue 殘量 —— 給「卡住了嗎」這一問一個讀數。
            Console.WriteLine($"· 專案 {aTarget.ProjectName}　資料根 {aDataRoot}");
            string aQueuesDir = SCP_DataPaths.Queues(new SCP_DataRoot(aDataRoot));
            var aFolders = aPersona != null
                ? new[] { AgentCmdClient.QueueFolder(aDataRoot, aPersona) }
                : Directory.Exists(aQueuesDir) ? Directory.GetDirectories(aQueuesDir) : Array.Empty<string>();
            foreach (string aDir in aFolders)
            {
                string aWho = Path.GetFileName(aDir);
                string aState = AgentCmdClient.TriggerState(aDataRoot, aWho);
                int aCount = 0;
                string aQp = AgentCmdClient.QueuePath(aDataRoot, aWho);
                try
                {
                    if (File.Exists(aQp) && System.Text.Json.Nodes.JsonNode.Parse(
                            File.ReadAllText(aQp)) is System.Text.Json.Nodes.JsonObject aQ
                        && aQ["Commands"] is System.Text.Json.Nodes.JsonArray aArr) aCount = aArr.Count;
                }
                catch { aCount = -1; }   // 壞檔要看得出來，不是印 0
                Console.WriteLine($"  · {aWho}　state={aState}　queue={(aCount < 0 ? "⚠壞檔" : aCount.ToString())}");
            }
            return 0;
        }

        // ── run ──
        string? aCmdType = iArgs.Length > 2 && !iArgs[2].StartsWith("--") ? iArgs[2] : null;
        if (aCmdType == null)
            return AgentUsageError("run 少了 <CmdType>", "senate ucmd run Task --arg op=show --arg index=8");

        var aCmdArgs = new Dictionary<string, string>();
        foreach (string aPair in ArgValues(iArgs, "--arg"))
        {
            int aEq = aPair.IndexOf('=');
            if (aEq <= 0) return AgentUsageError($"--arg 要 k=v 的形狀（收到 '{aPair}'）", "");
            aCmdArgs[aPair[..aEq]] = aPair[(aEq + 1)..];
        }
        // --arg-file k=<路徑>：長內文不經過 shell（run_cmd.py 同律）—— 讀檔失敗直接擋，不寫 queue。
        foreach (string aPair in ArgValues(iArgs, "--arg-file"))
        {
            int aEq = aPair.IndexOf('=');
            if (aEq <= 0) return AgentUsageError($"--arg-file 要 k=<路徑> 的形狀（收到 '{aPair}'）", "");
            string aFile = aPair[(aEq + 1)..];
            if (!File.Exists(aFile)) return AgentUsageError($"--arg-file 指到不存在的檔：{aFile}", "");
            aCmdArgs[aPair[..aEq]] = File.ReadAllText(aFile, System.Text.Encoding.UTF8);
        }
        double aTimeout = double.TryParse(ArgValue(iArgs, "--timeout"), out var t) ? t : AgentCmdClient.DefaultWaitTimeoutSec;
        bool aNoWait = iArgs.Contains("--no-wait");

        // ── queue 路由 auto-route（TASK-0107，Tim 2026-09-02 拍板；與 run_cmd.py
        //    `AUTO_ROUTE_BY_ARG_PERSONA` 同律）────────────────────────────────────
        // `--arg persona=` 是**身分**、`--persona` 是**路由**，而幾乎所有既有指路字串只帶前者
        // （Cmd 回傳檔印的那一行就是）⇒ 不推就落 anonymous，而且**回 Success 不會紅**。
        // 🩸 summit 2026-08-16 親踩：觀影同場四人，兩次 `ensure_idle` 逾時，錯誤訊息裡是
        //    `queues/anonymous/pending.trigger`，而 `queues/summit/` 好端端空在旁邊。
        //
        // ⚠ **為什麼在這裡而不是在 `AgentCmdClient.Submit()` 裡**（summit 2026-09-02 第一版的血證）：
        //    一次派遣有四個地方吃 persona —— EnsureIdle／Submit／畫面那行／Wait。
        //    第一版只在 Submit 內部改，於是 **queue 寫進 `queues/summit/`，而 Wait 在
        //    `queues/anonymous/` 等 result** ⇒ 判定退化成「Cmd disappeared → 推論 Success」。
        //    那比不修更糟：修之前四個地方一致地錯，修之後它們不一致，而**畫面照樣印綠**。
        // ⇒ 路由這種東西要嘛在**進入點**改一次讓全鏈吃到，要嘛不要改。
        if (string.IsNullOrWhiteSpace(aPersona)
            && aCmdArgs.TryGetValue("persona", out var aRoutedPersona)
            && !string.IsNullOrWhiteSpace(aRoutedPersona))
        {
            aPersona = aRoutedPersona.Trim();
            Console.WriteLine($"  ↪ queue 路由：由 --arg persona={aPersona} 推得 → queues/{aPersona}/"
                              + "（未帶 --persona；要走別條通道請顯式帶 --persona）");
        }

        if (!AgentCmdClient.EnsureIdle(aDataRoot, aPersona, AgentCmdClient.DefaultAckTimeoutSec,
                Console.WriteLine, out string aIdleWhy))
        {
            Console.Error.WriteLine($"✗ {aIdleWhy}");
            return 2;
        }
        string aCmdId = AgentCmdClient.Submit(aDataRoot, aPersona, aCmdType, aCmdArgs, Console.WriteLine);
        Console.WriteLine($"Submitted: {aCmdId}");
        Console.WriteLine($"  Type={aCmdType}, Mode=OneShot → {aTarget.ProjectName}:{(string.IsNullOrWhiteSpace(aPersona) ? AgentCmdClient.AnonymousQueueId : aPersona)}");
        Console.WriteLine("  Trigger written → pending.trigger（Editor 的 Auto-Watcher ~1s 內接手；沒動靜就檢查 Editor 開著沒）");
        if (aNoWait) return 0;
        return (int)AgentCmdClient.Wait(aDataRoot, aPersona, aCmdId, aTimeout,
            AgentCmdClient.DefaultPollSec, Console.WriteLine, Console.Error.WriteLine);
    }

    static int AgentUsageError(string iError, string iHint)
    {
        Console.Error.WriteLine($"✗ {iError}");
        if (iHint.Length > 0) Console.Error.WriteLine($"  ↳ {iHint}");
        Console.Error.WriteLine("  完整說明：senate --help");
        return 2;
    }

    /// <summary>
    /// 參數少一格／給錯了的出口。
    /// <para>⚠ 刻意**不吐整份 Usage**：40 行說明會把「你少給了 --yes」那一句擠到看不見，
    /// 而那一句才是使用者現在需要的東西。要完整說明的人自己打 --help。</para>
    /// <para>認不得的**子指令**是另一回事（那時人不知道有什麼可打）—— 那條仍走 Usage。</para>
    /// </summary>
    static int SubmoduleUsageError(string iError, string iHint)
    {
        Console.Error.WriteLine($"✗ {iError}");
        if (iHint.Length > 0) Console.Error.WriteLine($"  ↳ {iHint}");
        Console.Error.WriteLine("  完整說明：senate --help");
        return 2;
    }

    /// <summary>
    /// 決定要對哪個 repo 動手。
    /// <para>--root 顯式路徑 ＞ --project 設定檔裡的名字 ＞（唯讀時）Senate 自己。</para>
    /// </summary>
    static string? ResolveSubmoduleRoot(string iRepoRoot, string[] iArgs, bool iWrite, out string oWhy)
    {
        string? aRoot = ArgValue(iArgs, "--root");
        if (aRoot != null)
        {
            oWhy = "--root 指定";
            if (!Directory.Exists(aRoot)) { oWhy = $"--root 路徑不存在：{aRoot}"; return null; }
            return aRoot;
        }

        string? aProject = ArgValue(iArgs, "--project");
        if (aProject != null)
        {
            string aCfgPath = SenateConfig.DefaultPath(iRepoRoot);
            SenateConfig? aCfg = SenateConfig.Load(aCfgPath);
            // ⚠ 「還沒有設定檔」與「檔在但沒這個專案」是兩件事，訊息必須分得出來：
            //   前者要跑 senate init，後者要去改 projects[]。壓成同一句會讓人改錯地方。
            if (aCfg == null)
            {
                oWhy = $"還沒有設定檔（{aCfgPath}）—— 先跑 senate init，或直接用 --root";
                return null;
            }
            foreach (var aItem in aCfg.Projects)
            {
                if (!string.Equals(aItem.Name, aProject, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(aItem.Root)) { oWhy = $"專案 '{aProject}' 沒有設 root"; return null; }
                // ⚠ 停用的專案要擋下並說出來 —— 「我關掉它」與「找不到」是兩件事。
                if (!aItem.Enabled) { oWhy = $"專案 '{aProject}' 在設定檔裡是停用的（enabled=false）"; return null; }
                oWhy = $"--project {aItem.Name}";
                return aItem.Root;
            }
            oWhy = $"設定檔裡沒有名叫 '{aProject}' 的專案";
            return null;
        }

        if (iWrite)
        {
            // 見 CmdSubmodule 的血證註解：會寫東西的指令不猜對象。
            oWhy = "sync 必須顯式指定對象：--root <路徑> 或 --project <設定檔裡的名字>";
            return null;
        }
        oWhy = "預設（Senate 自己）";
        return iRepoRoot;
    }

    static void PrintScan(SubmoduleScanResult iScan, bool iFetched)
    {
        if (iScan.Items.Count == 0)
        {
            Console.WriteLine("· 這個 repo 沒有 submodule（掃描成功，真的是零）");
            return;
        }
        Console.WriteLine(iFetched
            ? "· 已 fetch ⇒ ahead/behind 是即時值"
            : "· 未 fetch ⇒ ahead/behind 以各列自己「上次 fetch」為準");
        foreach (var aItem in iScan.Items)
        {
            string aCurrent = aItem.Entry.Uninitialized ? "⛔未init"
                : aItem.CurrentBranch == null ? "⚠問不到"
                : aItem.CurrentBranch == SCP_Git.DetachedHead ? "⛔detached"
                : (aItem.OnTarget ? "✓" : "⚠") + aItem.CurrentBranch;
            string aDirty = aItem.Entry.Uninitialized ? "-"
                : aItem.Dirty == SCP_GitDirtyState.Clean ? "乾淨"
                : aItem.Dirty == SCP_GitDirtyState.Dirty ? "⚠dirty" : "⚠status問不到";
            string aAheadBehind = aItem.Entry.Uninitialized ? "-"
                : aItem.AheadBehind.Known ? $"↑{aItem.AheadBehind.Ahead} ↓{aItem.AheadBehind.Behind}" : "未知";
            Console.WriteLine($"  · {aItem.Entry.Path}"
                + $"　目前={aCurrent}"
                + $"　目標={(aItem.TargetBranch.Length > 0 ? aItem.TargetBranch : "—")}"
                + $"（{SubmoduleScan.SourceText(aItem.TargetSource)}）"
                + $"　{aDirty}　{aAheadBehind}　fetch={aItem.FetchAgeText}"
                + (aItem.Remotes.Count > 1 ? $"　⇈{string.Join("/", aItem.Remotes)}" : ""));
        }
    }

    /// <summary>可重複的旗標（<c>--only a --only b</c>）—— 單值的 ArgValue 只拿第一個。</summary>
    static List<string> ArgValues(string[] iArgs, string iName)
    {
        var aList = new List<string>();
        for (int i = 0; i < iArgs.Length - 1; ++i)
        {
            if (iArgs[i] == iName && !iArgs[i + 1].StartsWith("--")) aList.Add(iArgs[i + 1]);
        }
        return aList;
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
    static SCP_GuiStyle StyleFrom(string[] iArgs, SenateModel iModel)
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

    // ── senate cmd ────────────────────────────────────────────
    // 區塊職責：**SCP_CMD 的 CLI 宿主** —— 把命令列的字串交給 SCP_Core 的指令目錄跑。
    // 物理意義：跟 `senate ucmd`（Unity 的 AgentCommand）是**兩套東西**，刻意不共用動詞：
    //           那套是「寫檔案 → Unity Editor 接手 → 輪詢結果」，這套是**直接呼叫 C#**，
    //           沒有 queue、沒有 Watcher、沒有「從 queue 消失代表結束」那套推論。
    //           ⇒ Editor 沒開它照樣跑，因為它從頭到尾不需要 Editor。
    // 數值影響：exit code 直接沿用 Cmd 的（0 成功／1 Cmd 失敗／2 用法錯／70 例外）。
    static int CmdScp(string iRepoRoot, string[] iArgs)
    {
        string aName = "";
        var aRawArgs = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 1; i < iArgs.Length; i++)
        {
            string aToken = iArgs[i];
            if (aToken == "--arg" || aToken == "--arg-file")
            {
                if (i + 1 >= iArgs.Length) return Usage(2, $"{aToken} 少了 k=v");
                string aPair = iArgs[++i];
                int aEq = aPair.IndexOf('=');
                if (aEq <= 0) return Usage(2, $"{aToken} 要 k=v 的形狀（收到 '{aPair}'）");
                string aKey = aPair.Substring(0, aEq);
                string aValue = aPair.Substring(aEq + 1);
                if (aToken == "--arg-file")
                {
                    // 長內文不經過 shell —— 反引號那一族咬過太多次，判準不是「含不含特殊字元」。
                    if (!File.Exists(aValue)) return Usage(2, $"--arg-file 指到不存在的檔：{aValue}");
                    aValue = File.ReadAllText(aValue);
                }
                aRawArgs[aKey] = aValue;
            }
            else if (aToken.StartsWith("--", StringComparison.Ordinal))
            {
                return Usage(2, $"cmd 認不得的旗標 '{aToken}'");
            }
            else if (aName.Length == 0)
            {
                aName = aToken;
            }
            else
            {
                return Usage(2, $"cmd 只吃一個指令名（已經有 '{aName}'，又收到 '{aToken}'）");
            }
        }

        if (aName.Length == 0) aName = "help";   // 不給名字 ＝ 印清單（那是使用者這時唯一想要的）

        // 便利：letters_root 沒給就用設定檔那一格。**印出來**，不靜默注入 ——
        // 靜默注入的症狀是「我明明沒指定，它卻讀了別人的信件庫」。
        SCP.Core.Cmd.SCP_Cmd? aCmd = SCP.Core.Cmd.SCP_CmdRegistry.Find(aName);
        if (aCmd != null && !aRawArgs.ContainsKey("letters_root") && DeclaresArg(aCmd, "letters_root"))
        {
            string? aRoot = null;
            try { aRoot = PersonaLetters.LoadLettersRoot(iRepoRoot); }
            catch (InvalidDataException e) { Console.Error.WriteLine($"✗ 設定檔有問題：{e.Message}"); return 3; }
            if (aRoot != null)
            {
                aRawArgs["letters_root"] = aRoot;
                Console.WriteLine($"· letters_root 沒給 ⇒ 用設定檔的 awakening.lettersRoot：{aRoot}");
            }
        }

        SCP.Core.Cmd.SCP_CmdResult aResult = SCP.Core.Cmd.SCP_CmdRegistry.Dispatch(aName, aRawArgs);

        foreach (string aLine in aResult.Lines)
        {
            if (aResult.Ok) Console.WriteLine(aLine);
            else Console.Error.WriteLine(aLine);
        }
        // 產出檔與純量分開印 —— 混在一起會讓數字被當成路徑去開（run_cmd 那邊的血證）。
        foreach (string aOutput in aResult.Outputs) Console.WriteLine($"📄 回傳檔：{aOutput}");
        foreach (KeyValuePair<string, string> aValue in aResult.Values)
            Console.WriteLine($"🔢 {aValue.Key} = {aValue.Value}");

        // ── 錯誤報告（TASK-0104）：exit 1／70 一律寫，3 只在真的送出過（有 cmd_id）才寫，2 不寫 ──
        // 落點是 Senate 自己的 runtime（不是某個專案的資料根）：原生 Cmd 不知道「哪個專案」，
        // 拿「唯一啟用的專案」去猜會在多專案時靜默寫到別人那棵樹（路徑不該被推導）。
        // 委派 Unity 的那批**不在這裡寫**：Editor 端已經有自己那份，AgentCmdClient 會節錄它。
        if (!aResult.Ok && aCmd != null && aCmd.PortStatus != SCP.Core.Cmd.SCP_CmdPortStatus.DelegatedToUnity)
        {
            string? aCmdId = null;
            foreach (var kv in aResult.Values) if (kv.Key == "cmd_id") aCmdId = kv.Value;
            if (CmdErrorReport.ShouldReport(aResult.ExitCode, aCmdId != null))
            {
                string aReportId = aCmdId ?? $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}-{aName.ToLowerInvariant()}";
                string aHost = aResult.Values.Exists(v => v.Key == "delegate_host" && v.Value == "server") ? "server" : "local";
                // Server 跑的那筆，報告由 Server 寫在它的根（cmd_id 同一個）；這裡只指路，不再寫第二份。
                string? aPath = aHost == "server"
                    ? Path.Combine(SenatePaths.ServerRoot(iRepoRoot), CmdErrorReport.DirName, aReportId + ".md")
                    : CmdErrorReport.Write(SenatePaths.RuntimeDir(iRepoRoot), aReportId, aName, WithClient(aRawArgs), aResult, aHost,
                        m => Console.Error.WriteLine(m));
                if (aPath != null)
                {
                    // 三行固定形狀：哪一格不成立（上面 Lines 已印）／報告路徑／exit code。stdout＋stderr 各一份（PS 5.1 那課）。
                    string aLine = File.Exists(aPath) ? $"📄 錯誤報告：{aPath}" : $"📄 錯誤報告：{aPath}　⚠ 檔案不在（Server 端沒寫成？）";
                    Console.WriteLine(aLine); Console.Error.WriteLine(aLine);
                }
            }
        }
        Console.WriteLine($"🔢 exit_code = {aResult.ExitCode}");
        return aResult.ExitCode;
    }

    /// <summary>CLI 直跑的 Cmd 沒有經過 Submit，不會有 `_caller_client` —— 報告裡補上，不然「諰送的」會印成 unstated（那是給舊 client 留的態）。</summary>
    static Dictionary<string, string> WithClient(Dictionary<string, string> iArgs)
    {
        if (iArgs.ContainsKey("_caller_client")) return iArgs;
        var aCopy = new Dictionary<string, string>(iArgs, StringComparer.Ordinal) { ["_caller_client"] = AgentCmdClient.ClientId };
        return aCopy;
    }

    static bool DeclaresArg(SCP.Core.Cmd.SCP_Cmd iCmd, string iName)
    {
        foreach (SCP.Core.Cmd.SCP_CmdArgSpec aSpec in iCmd.ArgSpecs)
            if (aSpec.Name == iName) return true;
        return false;
    }

    static int Usage(int iCode, string? iError = null)
    {
        if (iError != null) Console.Error.WriteLine($"✗ {iError}");
        Console.WriteLine("""
            senate <command>
              （不給指令 ＝ doctor；**從檔案總管雙擊 ＝ 直接開 GUI 視窗**）

              init                建立 SenateData/config/senate.local.json（樣板：同目錄的 senate.local.example.json；已存在則不覆寫）
              doctor              印出環境與各專案的讀數（唯讀）。exit 1 ＝ 有項目不通過
              ui                  把後台頁面輸出成純文字
                --list            列出畫面上所有可互動元件（id / 類型 / 現值 / 怎麼操作）
                --click <id>      按下某顆鈕（會實際跑該頁的 handler）
                --set <id>=<值>   填欄位（跨次記住，存在 SenateData/runtime/ui_session.json）
                --toggle <id>     切換勾選
                --fold <id>       摺疊／展開一個區塊（收合時內容不會被建出來）
                --reset           清空 session
                --json            整棵畫面樹輸出成 JSON（給程式讀）
              ui --window         開原生視窗（ImGui）—— 同一份頁面碼，換一個 renderer
                --page <key>      開窗直接停在某一頁（home / doctor / submodule / style / settings / projects / paths / login / skills / process）—— 給截圖驗收用
                --seed-session    開窗時接續 CLI session 的欄位／勾選／摺疊（截圖模式自動開）
              ui --screenshot <p> 開窗、畫幾幀、把畫面存成 PNG 後結束（給沒有眼睛的人驗收）
              ui --soak <秒>      開窗**真的轉這麼多秒**再收工，印幀數／fps／最慢一幀（可跟 --screenshot 併用）
              cmd [<name>]        SCP_CMD —— 不依賴 Unity 的指令系統（沒有 queue，直接呼叫 C#）
                                  不給 name ＝ 印出所有可用指令（等同 cmd help）
                --arg k=v         指令參數，可重複。**沒宣告的參數名會被擋下**，不會靜默取預設
                --arg-file k=<路徑>  參數值從檔案讀（UTF-8）—— 長內文不經過 shell
              submodule status    列出 submodule 的 branch / 髒不髒 / 領先落後（唯讀）
                --root <path>     對哪個 repo（status 不給就是 Senate 自己）
                --project <name>  改用 senate.local.json 裡的專案（停用的會擋下並說原因）
                --branch <b>      全域預設 branch（解析順序的第三層）
                --set-branch <path>=<b>  逐項指定目標 branch（解析順序的**最高**層；可重複）
                --fetch           先逐顆 fetch 再讀 ⇒ ahead/behind 才是即時值
              submodule sync      切 branch / pull / push（**會改變狀態**）
                --checkout        切到目標 branch（dirty、HEAD 有未合併 commit 一律跳過並列出）
                --pull            pull --ff-only（分岔就列出來，不替人 merge）
                --push            寫遠端 ⇒ **必須同時給 --yes**
                --push-all-remotes  推該 repo 的每一個 remote（關 ＝ 只推 origin）
                --include-root    root 也一起 pull / push（**root 永遠不切 branch**）
                --only <path>     只處理某幾顆（可重複；指到不存在的會擋下）
                --dry-run         只印打算做什麼，不動任何東西
                ⚠ sync **不給預設對象** —— 必須 --root 或 --project（對錯的 repo 動手是最貴的錯）
              ucmd run <CmdType>  派一筆 AgentCommand 給目標專案的 Unity Editor（= run_cmd.py 的 C# 版）
                                  ⚠ 2026-08-29 改名：這套 Unity 專用的從 `cmd` 改叫 `ucmd`，
                                     `cmd` 讓給不依賴 Unity 的 SCP_CMD（見上）
                --project <name>  對哪個專案（senate.local.json projects[]；只有一個啟用時可省）
                --persona <p>     身分（決定 queue 路由並戳進 args；沒給走 anonymous）
                --arg k=v         指令參數（可重複）
                --arg-file k=<路徑>  參數值從檔案讀（長內文不經過 shell）
                --timeout <秒>    等待逾時（預設 120）；--no-wait 送出就返回
                ⚠ 需要目標專案的 Unity Editor 開著（Watcher 執行）—— 這是派遣不是代跑
              ucmd status         看各 persona queue 的 trigger 狀態與殘量（唯讀）
              selftest            SCP_Core 共用碼的自我對拍（拿真檔案跑 JSON round-trip）
              server start        常駐 Server（**前景**，開一個終端機掛著；Ctrl+C 停）。已有一顆在跑會拒絕
              server stop         請 Server 自退，5 秒等不到才 kill；沒在跑也 exit 0（build 腳本每次都先呼叫它）
              server status       身分／心跳／build id 三格分開印；沒在跑 exit 3

            共用選項：
              --width <n>       文字輸出寬度（字元格，預設 96）⚠ 不吃 --scale
              --scale <x>       介面縮放（0.5〜4，預設 2.0；本次有效，不寫回設定檔）
              --size <段>       small(1×) / medium(1.5×) / big(2×) / xl(2.5×) —— 同上，本次有效
            常設尺寸改在畫面上（入口頁 `ui --click home/size/big`，會寫回 senate.local.json 的 ui 區塊）
            """);
        return iCode;
    }
}
