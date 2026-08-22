// 區塊職責：CLI 入口 —— `senate init` / `doctor` / `ui`。
// 物理意義：**headless 優先**。這套後台的第一個實用價值是「Unity Editor 關著也能做事」，
//           所以入口是命令列；ImGui 視窗是同一份頁面碼的第二個 renderer，不是唯一入口。
// 數值影響：唯讀的指令不動任何檔（doctor / ui）；init 只在檔案**不存在**時建立，絕不覆寫。
// exit code 語意（刻意分開，讓腳本分辨得出「壞了」與「還沒設定」）：
//   0 = 一切正常   1 = 環境或設定有問題（doctor 判定不通過）
//   2 = 用法錯誤（未知指令）   3 = 設定檔存在但內容壞了
using Senate.Cli.Pages;
using Senate.Core;
using Senate.Gui;

namespace Senate.Cli;

public static class Program
{
    public static int Main(string[] iArgs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        string aCmd = iArgs.Length > 0 ? iArgs[0].ToLowerInvariant() : "doctor";
        string aRepoRoot = RepoRoot();

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
        var (aEnv, aProjects, aCfgBroken) = Collect(iRepoRoot);
        var aUi = new Ui();
        new DoctorPage(aEnv, aProjects).Draw(aUi);
        Console.Write(GuiTextRenderer.Render(aUi.Root, Width(iArgs)));

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
        string? aClick = ArgValue(iArgs, "--click");
        var (aEnv, aProjects, _) = Collect(iRepoRoot);
        var aInput = new GuiInput { ClickedId = aClick };
        var aUi = new Ui(aInput);
        new DoctorPage(aEnv, aProjects).Draw(aUi);
        Console.Write(GuiTextRenderer.Render(aUi.Root, Width(iArgs)));
        if (aClick != null) Console.WriteLine($"（模擬點擊：{aClick}）");
        return 0;
    }

    // ── senate selftest ───────────────────────────────────────
    // 物理意義：共用碼（SCP_Core）的 JSON 層必須讀得懂**既有資料** ——
    //           那些檔是 Unity 端寫出來的，所以驗收方式是拿真檔案去跑，不是自己造樣本。
    static int CmdSelfTest(string iRepoRoot, string[] iArgs)
    {
        var (_, aProjects, _) = Collect(iRepoRoot);
        var aRows = SelfTest.Run(aProjects);

        var aUi = new Ui();
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
        Console.Write(GuiTextRenderer.Render(aUi.Root, Width(iArgs)));

        int aFail = aRows.Count(r => r.Result == CheckResult.Fail);
        int aSkip = aRows.Count(r => r.Result == CheckResult.Skipped);
        int aPass = aRows.Count(r => r.Result == CheckResult.Pass);
        // 跳過的項目**單獨報**，不併進通過數 —— 「沒測」與「測過而且對」不得同形。
        Console.WriteLine($"⇒ 通過 {aPass}／失敗 {aFail}／跳過 {aSkip}"
            + (aSkip > 0 ? "（跳過的項目沒有讀數，不算通過）" : ""));
        return aFail > 0 ? 1 : 0;
    }

    // ── 讀數收集 ──────────────────────────────────────────────
    static (EnvReading, List<ProjectReading>, bool) Collect(string iRepoRoot)
    {
        string aCfgPath = SenateConfig.DefaultPath(iRepoRoot);
        bool aBroken = false;
        SenateConfig? aCfg = null;
        try { aCfg = SenateConfig.Load(aCfgPath); }
        catch (InvalidDataException e) { aBroken = true; Console.Error.WriteLine($"✗ {e.Message}"); }

        var aEnv = new EnvReading(
            DotnetCli.SdkVersion(),
            DotnetCli.RuntimeVersion,
            GitCli.Version(),
            aCfgPath,
            File.Exists(aCfgPath));

        var aList = new List<ProjectReading>();
        if (aCfg != null)
        {
            foreach (string err in aCfg.Validate()) Console.Error.WriteLine($"⚠ 設定：{err}");
            foreach (var p in aCfg.Projects) aList.Add(ProjectProbe.Probe(p));
        }
        return (aEnv, aList, aBroken);
    }

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
        => int.TryParse(ArgValue(iArgs, "--width"), out int w) && w >= 40 ? w : GuiTextRenderer.DefaultWidth;

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

              init                建立 senate.local.json（樣板：config/senate.local.example.json；已存在則不覆寫）
              doctor              印出環境與各專案的讀數（唯讀）。exit 1 ＝ 有項目不通過
              ui [--click <id>]   把後台頁面輸出成純文字；--click 模擬按下某顆鈕（無視窗也能驗互動）
              selftest            SCP_Core 共用碼的自我對拍（拿真檔案跑 JSON round-trip）

            共用選項：--width <n>   文字輸出寬度（預設 96）
            """);
        return iCode;
    }
}
