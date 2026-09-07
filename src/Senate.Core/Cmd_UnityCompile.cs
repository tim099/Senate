// 區塊職責：Unity 編譯的兩支 CLI 入口 —— `unity-recompile`（觸發＋等那一趟）／
//           `unity-compile-status`（只讀現況，不觸發）。
// 物理意義：它們回答的是**兩個不同的問題**，所以是兩支不是一支：
//           「我這次改動編譯過了嗎」需要一個基準（送出觸發的那一刻）；
//           「現在磁碟上那份狀態說什麼」不需要基準，也不需要 Editor。
//           🩸 把後者當成前者，就是 python `check_compile.py --watch` 那隻 bug 的內容
//           （TASK-0154，2026-09-07 實測：觸發還沒開始時 `in_progress` 已經是 false
//            ⇒ 印出三天前的快照 `2026-09-04T17:14`，而且沒印 STALE 橫幅）。
//           ⇒ 本入口把那個洞**從結構上拿掉**：基準是我送出的那一刻，不是猜出來的。
// 數值影響：`unity-recompile` 讓 Editor 跑一次編譯（秒級，那是它的重點）；本 process 零寫入。
//
// ⛔ 兩支都只量 **Unity assemblies**，不涵蓋 `senate.exe` —— 射程行由
//   `SCP_UnityCompile.ScopeLine` 統一印，⚠ **不要在這裡另寫一句**（兩句話遲早各說各話）。
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Compile;

namespace Senate.Core;

// ── ① 觸發＋等那一趟 ──────────────────────────────────────────────

public sealed class Cmd_UnityRecompile : UnityDelegateCmd
{
    public override string Name => "unity-recompile";

    public override string Summary => "Unity 重編譯：觸發＋**等到那一趟結束**再印讀數 —— 由 Unity Editor 執行";

    public override string Details =>
        "送 Recompile 給 Editor，然後等狀態檔出現**晚於送出時刻**的那一份才印結論。\n"
        + "⚠ Editor 回 Success 只代表**觸發送到了** —— 編譯要再過幾秒才開始。\n"
        + "   所以「Cmd 成功」與「編譯跑完了」不同形，本 Cmd 等的是後者。\n"
        + "⚠ 新鮮度用**檔案 mtime**判，不用內嵌時間戳（那個只有秒精度，同一秒觸發會等於基準）。\n"
        + "⛔ 等不到就說等不到，**不會退回印上一次的快照**（那份格式完整、數字合理，比沒有東西可讀危險）。";

    public override string PortNote =>
        "觸發仍在 Editor（domain reload 是 Unity 的事）；狀態讀取與對帳已在本 CLI 就地跑";

    public override string Example =>
        SCP_CmdRegistry.Invoke("unity-recompile --arg persona=Template");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aList = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("persona",
                    "走哪一條 queue 分道（沒給會掉進共用分道，會跟別人互相阻塞）", iRequired: true),
                new SCP_CmdArgSpec("compile_timeout",
                    "等編譯跑完的秒數（**不是**等 Editor 回應的那個 timeout）",
                    iDefault: DefaultCompileWaitSec.ToString(CultureInfo.InvariantCulture)),
                new SCP_CmdArgSpec("max_messages", "最多列幾個錯", iDefault: DefaultMaxMessages.ToString()),
            };
            aList.AddRange(CommonSpecs());
            return aList;
        }
    }

    /// <summary>等編譯的預設秒數 —— 冷編譯（domain reload ＋ 全量）實測十幾秒起跳，留餘裕。</summary>
    public const double DefaultCompileWaitSec = 180.0;

    public const int DefaultMaxMessages = 20;

    /// <summary>輪詢間隔。⚠ 太密會讓「讀到寫到一半的檔」變成常態（解析失敗＝再等一輪，不當結論）。</summary>
    const double c_PollSec = 1.0;

    protected override string UnityCmdType => "Recompile";

    protected override string CliNextHint =>
        SCP_CmdRegistry.Invoke("unity-compile-status --arg project=<專案>") + "　（只讀，不再觸發）";

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => new Dictionary<string, string>();

    protected override void AfterDelegateSucceeded(SCP_CmdResult ioResult, UnityTarget iWhere,
                                                   SCP_CmdArgs iArgs, DateTime iTriggerUtc)
    {
        double aWait = ParseDouble(iArgs.Get("compile_timeout"), DefaultCompileWaitSec);
        int aMax = ParseInt(iArgs.Get("max_messages"), DefaultMaxMessages);

        ioResult.Lines.Add("");
        ioResult.Lines.Add("## 編譯讀數（等的是**晚於送出時刻**的那一份）");
        ioResult.AddValue("compile_baseline_utc", iTriggerUtc.ToString("O", CultureInfo.InvariantCulture));

        DateTime aDeadline = DateTime.UtcNow.AddSeconds(aWait);
        SCP_UnityCompileRead? aFresh = null;
        bool aSawInProgress = false;
        while (DateTime.UtcNow < aDeadline)
        {
            SCP_UnityCompileRead aRead = SCP_UnityCompile.Read(iWhere.DataRoot);
            if (SCP_UnityCompile.IsFresherThan(aRead, iTriggerUtc) && aRead.Status != null)
            {
                if (aRead.Status.in_progress) aSawInProgress = true;   // 看到它開始了 —— 這是好消息
                else { aFresh = aRead; break; }
            }
            System.Threading.Thread.Sleep((int)(c_PollSec * 1000));
        }

        if (aFresh?.Status == null)
        {
            // ⛔ 不退回讀舊的那份。逾時的意思是「我沒有量到」，而那跟「量到了是綠的」處置相反。
            ioResult.ExitCode = 4;
            ioResult.AddValue("compile_verdict", "timeout");
            ioResult.Lines.Add($"⛔ 等了 {aWait:0}s 仍沒有出現晚於送出時刻的編譯狀態 —— **沒有量到**，不是綠燈。");
            ioResult.Lines.Add(aSawInProgress
                ? "  · 期間看過 `in_progress=true` ⇒ 編譯真的開始了，只是還沒結束（加大 compile_timeout 再跑）。"
                : "  · 期間**一次都沒看到** `in_progress=true` ⇒ 觸發可能沒讓 Editor 進編譯"
                  + "（Editor 失焦時不會自動重編，這正是 Recompile 存在的理由 —— 先確認它還活著）。");
            ioResult.Lines.Add("  · 現況（**上一趟**的，不是這一趟）→ "
                               + SCP_CmdRegistry.Invoke("unity-compile-status"));
            ioResult.Lines.Add(SCP_UnityCompile.ScopeLine);
            return;
        }

        foreach (string aLine in SCP_UnityCompile.Render(aFresh, iWhere.ProjectRoot, iErrorsOnly: true, iMaxMessages: aMax))
            ioResult.Lines.Add(aLine);

        int aErrors = aFresh.Status.total_errors;
        ioResult.AddValue("compile_errors", aErrors.ToString(CultureInfo.InvariantCulture));
        ioResult.AddValue("compile_warnings", aFresh.Status.total_warnings.ToString(CultureInfo.InvariantCulture));
        ioResult.AddValue("compile_verdict", aErrors == 0 ? "clean" : "errors");
        if (aErrors > 0) ioResult.ExitCode = 1;
    }

    internal static double ParseDouble(string iValue, double iFallback)
        => double.TryParse(iValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double aOut) && aOut > 0
           ? aOut : iFallback;

    internal static int ParseInt(string iValue, int iFallback)
        => int.TryParse(iValue, NumberStyles.None, CultureInfo.InvariantCulture, out int aOut) && aOut > 0
           ? aOut : iFallback;
}

// ── ② 只讀現況 ────────────────────────────────────────────────────

public sealed class Cmd_UnityCompileStatus : SCP_Cmd
{
    public override string Name => "unity-compile-status";

    public override string Summary => "Unity 編譯現況：讀狀態檔＋ErrorLog 交叉對帳 —— **本地跑，不需要 Editor**";

    public override string Details =>
        "只回答「**現在磁碟上那份狀態說什麼**」，⛔ 不觸發編譯、也不判斷它是不是你這次改動的結果。\n"
        + "要後者走 `unity-recompile`（它拿送出時刻當基準）。\n"
        + "⚠ 狀態檔不存在時說「沒有讀數」，**不印 0 errors** —— 那兩件事的處置相反。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("unity-compile-status --arg project=<專案>");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("project",
            "看哪個 Unity 專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略"),
        new SCP_CmdArgSpec("max_messages", "最多列幾個錯",
            iDefault: Cmd_UnityRecompile.DefaultMaxMessages.ToString()),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        // 專案解析走委派那條同一支解析器 —— 兩份解析遲早對同一個 --project 給出不同答案。
        if (UnityDelegateCmd.ConfigProvider == null)
            return SCP_CmdResult.Fail(70,
                "✗ 宿主沒有裝上設定來源（UnityDelegateCmd.ConfigProvider）——",
                "  這是程式錯誤不是用法錯：本層不推導專案路徑。");

        (SenateConfig? aConfig, string aConfigPath) = UnityDelegateCmd.ConfigProvider();
        UnityTargetResolution aTarget = UnityTargetResolver.Resolve(aConfig, aConfigPath, iArgs.Get("project"));
        if (!aTarget.Ok) return SCP_CmdResult.Fail(2, "✗ " + aTarget.Error, "  " + aTarget.Hint);

        UnityTarget aWhere = aTarget.Target!;
        var aResult = new SCP_CmdResult();
        aResult.Lines.Add($"📖 讀 {aWhere.Describe()} 的編譯狀態（**本地讀檔，沒有問 Editor**）");
        if (aWhere.SelectionNote.Length > 0) aResult.Lines.Add("· " + aWhere.SelectionNote);
        aResult.AddValue("project", aWhere.ProjectName);
        aResult.AddValue("data_root", aWhere.DataRoot);

        SCP_UnityCompileRead aRead = SCP_UnityCompile.Read(aWhere.DataRoot);
        int aMax = Cmd_UnityRecompile.ParseInt(iArgs.Get("max_messages"), Cmd_UnityRecompile.DefaultMaxMessages);
        foreach (string aLine in SCP_UnityCompile.Render(aRead, aWhere.ProjectRoot, iErrorsOnly: true, iMaxMessages: aMax))
            aResult.Lines.Add(aLine);
        aResult.AddOutput(aRead.Path);

        if (!aRead.Found || aRead.Status == null)
        {
            aResult.ExitCode = 2;
            aResult.AddValue("compile_verdict", "no_reading");
            return aResult;
        }

        // ⚠ 這一句是本 Cmd 與 `unity-recompile` 最重要的差別，所以它印在結論旁邊而不是說明裡。
        aResult.Lines.Add("⚠ 本 Cmd **不知道**這份是不是你這次改動的結果 —— 要那個答案走 "
                          + SCP_CmdRegistry.Invoke("unity-recompile --arg persona=<me>"));
        aResult.AddValue("compile_errors", aRead.Status.total_errors.ToString(CultureInfo.InvariantCulture));
        aResult.AddValue("compile_warnings", aRead.Status.total_warnings.ToString(CultureInfo.InvariantCulture));
        aResult.AddValue("compile_in_progress", aRead.Status.in_progress ? "1" : "0");
        aResult.AddValue("compile_verdict", aRead.Status.total_errors == 0 ? "clean" : "errors");
        if (aRead.Status.total_errors > 0) aResult.ExitCode = 1;
        return aResult;
    }
}
