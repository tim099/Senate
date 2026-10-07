// 區塊職責：`senate cmd install` —— 安裝系統的 CLI（TASK-0375）：看狀態、查相依、安裝、解除安裝、建 venv。
// 物理意義：跟「安裝管理」頁同一套實作（InstallCatalog／InstallEnv／InstallProbe／InstallPlanner／InstallRunner）。
//           ⭐ **不帶 confirm=1 一律只印計畫**（要裝哪些、多大、從哪下載、裝到哪裡），一個檔都不動 ——
//             這是 agent 拿來「問使用者」的那一份文字；使用者同意之後才帶 confirm=1 重跑。
// 數值影響：op=status／check 純讀；op=install／uninstall／create_env 帶 confirm=1 才會動環境。
//           長時間的輸出（pip、下載進度）即時印到 stderr；結論與機讀值在 stdout。
// ⚠ exit code：0 ＝ 成功／全都在；1 ＝ 擋下（零變動）；2 ＝ 用法錯；
//   3 ＝ **缺相依**（op=check：問使用者要不要裝）；4 ＝ **量不到**（不知道缺不缺，⛔ 不可當成缺）；5 ＝ 動手了但沒成功。
using System.Globalization;
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_Install : SCP_Cmd
{
    public override string Name => "install";
    public override string Category => SCP_CmdCategory.System;

    public override string Summary => "安裝系統：模型與 Python 套件的狀態／相依檢查／安裝／解除安裝（不帶 confirm=1 只印計畫）";

    public override string Details =>
        "清單在 `SenateData/config/install_catalog.json`（新增項目只改清單）；Python 與模型位置在「路徑管理」頁（PythonEnvRoot／ModelsRoot，空白＝用預設）。\n"
        + "⭐ **skill 缺相依時**：`op=check --arg skill=<名>` 回 exit 3 並印出大小、來源、安裝指令 ——\n"
        + "   ⛔ agent 要先照實問使用者，**同意之後**才跑那行 `op=install … --arg confirm=1`；同意一次只算那一次、那幾項。\n"
        + "⚠ exit 3（缺）與 exit 4（量不到，例如找不到 Python）不同形：量不到時不可以去問「要不要裝」。\n"
        + "⚠ 解除安裝會擋兩件事：還有已安裝的項目需要它／它正在被使用。";

    public override string Example => SCP_CmdRegistry.Invoke("install --arg op=check --arg skill=ucl-memory");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "status（預設）｜check｜install｜uninstall｜create_env｜clean_partial｜resume_partial", iDefault: "status",
                           iChoices: new[] { "status", "check", "install", "uninstall", "create_env", "clean_partial", "resume_partial" }),
        new SCP_CmdArgSpec("ids", "項目 id，逗號分隔（check／install；uninstall 一次一個）"),
        new SCP_CmdArgSpec("skill", "check：skill 名或 SKILL.md 路徑 —— 讀它 frontmatter 的 `requires_install`"),
        new SCP_CmdArgSpec("confirm", "install／uninstall／create_env／clean_partial／resume_partial：=1 才真的動手（不給＝只印計畫）"),
        // ⛔ 2026-10-07（TASK-0390）拿掉 `project_root`：skill 只裝在 Senate 自己 ⇒ check 只找 Senate 那幾個 skills 目錄。
        new SCP_CmdArgSpec("only", "status：只列某一種狀態（installed｜missing｜broken｜partial｜unknown）—— 要知道「裝了哪些、之後可以拆」用 installed",
                           iChoices: new[] { "installed", "missing", "broken", "partial", "unknown" }),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        if (UnityDelegateCmd.ConfigProvider == null || ServerDelegateCmd.RepoRootProvider == null)
            return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源／repo 根 —— 這是程式錯誤不是用法錯");
        (SenateConfig? aConfig, string aConfigPath) = UnityDelegateCmd.ConfigProvider();
        string aRepo = ServerDelegateCmd.RepoRootProvider();

        InstallCatalog? aCatalog = InstallCatalog.Load(InstallCatalog.DefaultPath(aRepo), out string? aCatErr);
        if (aCatalog == null) return SCP_CmdResult.Fail(1, "✗ " + aCatErr);
        InstallEnv aEnv = InstallEnv.Resolve(aConfig);
        if (aEnv.ConfigError != null) return SCP_CmdResult.Fail(InstallRequire.UnknownExitCode, "✗ 設定讀不了 ⇒ 量不到：" + aEnv.ConfigError, "  設定檔：" + aConfigPath);

        string aOp = iArgs.Get("op");
        if (aOp.Length == 0) aOp = "status";
        bool aConfirm = iArgs.Get("confirm") == "1";
        var r = new SCP_CmdResult();
        EnvLines(r, aEnv);

        switch (aOp)
        {
            case "status": return Status(r, aEnv, aCatalog, iArgs.Get("only"));
            case "check": return Check(r, aEnv, aCatalog, iArgs);
            case "install": return Install(r, aEnv, aCatalog, Ids(iArgs), aConfirm);
            case "uninstall": return Uninstall(r, aEnv, aCatalog, Ids(iArgs), aConfirm);
            case "create_env": return CreateEnv(r, aEnv, aConfirm);
            case "clean_partial": return CleanPartial(r, aEnv, aCatalog, Ids(iArgs), aConfirm);
            case "resume_partial": return ResumePartial(r, aEnv, aCatalog, Ids(iArgs), aConfirm);
            default: return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp);
        }
    }

    static List<string> Ids(SCP_CmdArgs iArgs)
        => iArgs.Get("ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    static void EnvLines(SCP_CmdResult r, InstallEnv e)
    {
        r.Lines.Add("# 📦 安裝系統");
        r.Lines.Add("· Python：" + (e.PythonExe ?? "（找不到）") + "　（" + e.PythonOrigin + (e.IsVenv ? "，venv" : "") + "）");
        if (e.PythonError != null) r.Lines.Add("  ⚠ " + e.PythonError);
        r.Lines.Add("· 模型位置（HF_HOME）：" + e.ModelsRoot + "　（" + e.ModelsOrigin + "）");
        r.Lines.Add("· 要改這兩格：`senate ui --page paths`（PythonEnvRoot／ModelsRoot；空白＝用預設）");
        r.Lines.Add("");
        r.AddValue("python", e.PythonExe ?? "");
        r.AddValue("models_root", e.ModelsRoot);
    }

    static void StatusTable(SCP_CmdResult r, IEnumerable<InstallItemStatus> iRows)
    {
        foreach (InstallItemStatus s in iRows)
        {
            r.Lines.Add($"- {InstallItemStatus.StateText(s.State)}　**{s.Item.Id}**　{s.Item.Name}"
                        + (s.Version.Length > 0 ? "　" + s.Version : "")
                        + (s.SizeBytes > 0 ? "　" + InstallProbe.FormatSize(s.SizeBytes) : ""));
            if (s.Detail.Length > 0) r.Lines.Add("    " + s.Detail);
        }
    }

    static SCP_CmdResult Status(SCP_CmdResult r, InstallEnv aEnv, InstallCatalog aCatalog, string iOnly)
    {
        List<InstallItemStatus> aAll = InstallProbe.ProbeAll(aEnv, aCatalog);
        List<InstallItemStatus> aShown = iOnly.Length == 0
            ? aAll
            : aAll.Where(x => string.Equals(x.State.ToString(), iOnly, StringComparison.OrdinalIgnoreCase)).ToList();
        r.Lines.Add(iOnly.Length == 0 ? $"## 項目（{aAll.Count}）" : $"## 項目：只列 {iOnly}（{aShown.Count}／{aAll.Count}）");
        StatusTable(r, aShown);

        // Tim 2026-10-02：CLI 要知道「目前裝了哪些」，之後才能拆 ⇒ 已安裝的逐項附上解除安裝指令（不帶 confirm＝只印計畫）。
        List<InstallItemStatus> aInstalled = aAll.Where(x => x.State == InstallState.Installed).ToList();
        if (aShown.Any(x => x.State == InstallState.Installed))
        {
            r.Lines.Add("");
            r.Lines.Add("### 解除安裝（先不帶 confirm＝看計畫與會不會被擋；確定了再加 `--arg confirm=1`）");
            foreach (InstallItemStatus s in aShown.Where(x => x.State == InstallState.Installed))
                r.Lines.Add($"- senate cmd install --arg op=uninstall --arg ids={s.Item.Id}");
        }
        foreach (InstallState s in Enum.GetValues<InstallState>())
            r.AddValue("count_" + s.ToString().ToLowerInvariant(), aAll.Count(x => x.State == s).ToString(CultureInfo.InvariantCulture));
        // 0 個也印（空字串）：只在有值時出現的欄位，讀者分不出「沒有裝任何東西」與「沒量」。
        r.AddValue("installed_ids", string.Join(",", aInstalled.Select(x => x.Item.Id)));
        return r;
    }

    // ── check：skill／工具要的東西齊不齊 ─────────────────────────────

    static SCP_CmdResult Check(SCP_CmdResult r, InstallEnv aEnv, InstallCatalog aCatalog, SCP_CmdArgs iArgs)
    {
        List<string> aIds = Ids(iArgs);
        string aSkill = iArgs.Get("skill");
        if (aSkill.Length > 0)
        {
            var aRoots = new List<string>();
            string aBase = ServerDelegateCmd.RepoRootProvider!();
            foreach (string sub in new[] { ".claude/skills", ".agents/skills", ".codex/skills" }) aRoots.Add(Path.Combine(aBase, sub));
            string? aFile = InstallRequire.FindSkillFile(aSkill, aRoots, out List<string> aTried);
            if (aFile == null)
                return SCP_CmdResult.Fail(2, $"✗ 找不到 skill '{aSkill}'", "  找過：" + string.Join("、", aTried));
            List<string>? aReq = InstallRequire.ReadSkillRequires(aFile);
            r.Lines.Add($"· skill：{aFile}");
            if (aReq == null)
            {
                r.Lines.Add($"· 這個 skill 的 frontmatter 沒有 `{InstallRequire.FrontmatterKey}` ⇒ 它沒有宣告要安裝的東西");
                r.AddValue("missing", "0");
                return r;
            }
            aIds.AddRange(aReq);
        }
        if (aIds.Count == 0) return SCP_CmdResult.Fail(2, "✗ op=check 要給 `ids=` 或 `skill=`");

        List<InstallItemStatus> aAll = InstallProbe.ProbeAll(aEnv, aCatalog);
        InstallPlan aPlan = InstallPlanner.PlanInstall(aCatalog, aAll, aIds);
        r.Lines.Add($"## 需要：{string.Join("、", aIds)}");
        StatusTable(r, aPlan.AlreadyInstalled.Concat(aPlan.Steps));

        if (aPlan.Blocked.Count > 0)
        {
            r.ExitCode = InstallRequire.UnknownExitCode;
            r.Lines.Add("");
            r.Lines.Add("⚠ **量不到**（不知道缺不缺）—— ⛔ 不要去問使用者要不要裝，先處理這幾格：");
            foreach (string b in aPlan.Blocked) r.Lines.Add("  - " + b);
            r.AddValue("unknown", aPlan.Blocked.Count.ToString(CultureInfo.InvariantCulture));
            return r;
        }
        r.AddValue("missing", aPlan.Steps.Count.ToString(CultureInfo.InvariantCulture));
        if (aPlan.Steps.Count == 0) { r.Lines.Add(""); r.Lines.Add("✓ 都裝好了"); return r; }

        r.ExitCode = InstallRequire.MissingExitCode;
        r.Lines.Add("");
        r.Lines.Add("## ⚠ 缺相依 —— 請先問使用者要不要安裝（⛔ 不要自己決定）");
        r.Lines.Add("要轉告使用者的內容：");
        foreach (InstallItemStatus s in aPlan.Steps) r.Lines.Add("  - " + PlanLine(aEnv, s));
        r.Lines.Add($"  合計約 {aPlan.SizeMb} MB");
        r.Lines.Add("使用者同意之後才跑：");
        string aCmd = InstallRequire.InstallCommand(aIds);
        r.Lines.Add("  " + aCmd);
        r.AddValue("missing_ids", string.Join(",", aPlan.Steps.Select(s => s.Item.Id)));
        r.AddValue("install_command", aCmd);
        return r;
    }

    static string PlanLine(InstallEnv iEnv, InstallItemStatus s)
    {
        string aWhere = s.Item.Kind == InstallKind.HfModel ? InstallProbe.ModelDir(iEnv, s.Item) : "Python：" + (iEnv.PythonExe ?? "?");
        string aWhy = s.State == InstallState.Missing ? "" : $"（現在：{InstallItemStatus.StateText(s.State)}，會重裝／重新下載完）";
        return $"{s.Item.Name}（{s.Item.Id}）約 {s.Item.SizeMb} MB，來源 {s.Item.Source}，裝到 {aWhere}{aWhy}";
    }

    // ── install ─────────────────────────────────────────────────────

    static SCP_CmdResult Install(SCP_CmdResult r, InstallEnv aEnv, InstallCatalog aCatalog, List<string> aIds, bool aConfirm)
    {
        if (aIds.Count == 0) return SCP_CmdResult.Fail(2, "✗ op=install 要給 `ids=`");
        List<InstallItemStatus> aAll = InstallProbe.ProbeAll(aEnv, aCatalog);
        InstallPlan aPlan = InstallPlanner.PlanInstall(aCatalog, aAll, aIds);
        if (aPlan.Blocked.Count > 0)
        {
            r.ExitCode = 1;
            r.Lines.Add("⛔ 擋下（零變動）：");
            foreach (string b in aPlan.Blocked) r.Lines.Add("  - " + b);
            return r;
        }
        if (aPlan.Steps.Count == 0) { r.Lines.Add("✓ 都已經裝好了，沒有要做的"); r.AddValue("installed", "0"); return r; }

        r.Lines.Add($"## 計畫（{aPlan.Steps.Count} 項，照順序；合計約 {aPlan.SizeMb} MB）");
        foreach (InstallItemStatus s in aPlan.Steps) r.Lines.Add("  - " + PlanLine(aEnv, s));
        if (!aConfirm)
        {
            r.Lines.Add("");
            r.Lines.Add("· 沒有帶 confirm=1 ⇒ **只印計畫，什麼都沒動**。要裝：加 `--arg confirm=1`");
            r.AddValue("dry_run", "1");
            return r;
        }

        int aOk = 0;
        foreach (InstallItemStatus s in aPlan.Steps)
        {
            Console.Error.WriteLine($"── 安裝 {s.Item.Id} ──");
            string? aErr = InstallRunner.Install(aEnv, s.Item, l => Console.Error.WriteLine("  " + l));
            if (aErr != null)
            {
                r.ExitCode = 5;
                r.Lines.Add($"✗ {s.Item.Id} 沒裝成功：{aErr}");
                r.Lines.Add($"  已完成 {aOk}／{aPlan.Steps.Count}；後面的沒有跑（它們可能需要這一項）");
                r.AddValue("installed", aOk.ToString(CultureInfo.InvariantCulture));
                return r;
            }
            aOk++;
            r.Lines.Add($"✓ {s.Item.Id}");
        }
        r.AddValue("installed", aOk.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    // ── uninstall ───────────────────────────────────────────────────

    static SCP_CmdResult Uninstall(SCP_CmdResult r, InstallEnv aEnv, InstallCatalog aCatalog, List<string> aIds, bool aConfirm)
    {
        if (aIds.Count != 1) return SCP_CmdResult.Fail(2, "✗ op=uninstall 一次一個 `ids=<id>`（拆東西不批次 —— 每一個都要看過擋不擋）");
        List<InstallItemStatus> aAll = InstallProbe.ProbeAll(aEnv, aCatalog);
        string? aWhy = InstallPlanner.CheckUninstall(aCatalog, aEnv, aAll, aIds[0]);
        if (aWhy != null) { r.ExitCode = 1; r.Lines.Add("⛔ 擋下（零變動）：" + aWhy); return r; }
        InstallItem aItem = aCatalog.Find(aIds[0])!;
        string aWhat = aItem.Kind == InstallKind.HfModel
            ? $"刪除模型目錄 {InstallProbe.ModelDir(aEnv, aItem)}（⚠ 不可復原，重裝要重新下載約 {aItem.SizeMb} MB）"
            : $"從 {aEnv.PythonExe} 移除 {string.Join("、", aItem.Dists)}";
        r.Lines.Add("## 計畫：" + aWhat);
        if (aItem.Shared) r.Lines.Add("  ⚠ 這是共用的地基（目前清單上沒有已安裝的項目需要它，但清單外的程式可能會用）");
        if (!aConfirm) { r.Lines.Add("· 沒有帶 confirm=1 ⇒ 只印計畫，什麼都沒動"); r.AddValue("dry_run", "1"); return r; }

        string? aErr = InstallRunner.Uninstall(aEnv, aItem, l => Console.Error.WriteLine("  " + l));
        if (aErr != null) { r.ExitCode = 5; r.Lines.Add("✗ " + aErr); return r; }
        r.Lines.Add($"✓ {aItem.Id} 已解除安裝（重新量過）");
        return r;
    }

    // ── create_env ──────────────────────────────────────────────────

    static SCP_CmdResult CreateEnv(SCP_CmdResult r, InstallEnv aEnv, bool aConfirm)
    {
        if (aEnv.CreatableEnvDir == null)
        {
            r.ExitCode = 1;
            r.Lines.Add(aEnv.PythonExe != null
                ? "⛔ 已經有一份 Python 了（" + aEnv.PythonExe + "）—— 要另建一份，先在「路徑管理」頁把 PythonEnvRoot 指到一個空資料夾"
                : "⛔ 沒有可以建 venv 的位置：" + aEnv.PythonError);
            return r;
        }
        r.Lines.Add($"## 計畫：在 {aEnv.CreatableEnvDir} 建一份 venv（用系統 Python 當基底；建完是空的，套件要另外裝）");
        if (!aConfirm) { r.Lines.Add("· 沒有帶 confirm=1 ⇒ 只印計畫，什麼都沒動"); r.AddValue("dry_run", "1"); return r; }
        string? aErr = InstallRunner.CreateVenv(aEnv.CreatableEnvDir, l => Console.Error.WriteLine("  " + l));
        if (aErr != null) { r.ExitCode = 5; r.Lines.Add("✗ " + aErr); return r; }
        r.Lines.Add("✓ venv 建好了");
        return r;
    }

    // ── clean_partial ───────────────────────────────────────────────

    // 清模型快取裡的 .incomplete 暫存檔（中斷的下載留下的；HF 不會再接續它們）。
    static SCP_CmdResult CleanPartial(SCP_CmdResult r, InstallEnv aEnv, InstallCatalog aCatalog, List<string> aIds, bool aConfirm)
    {
        if (aIds.Count != 1) return SCP_CmdResult.Fail(2, "✗ op=clean_partial 一次一個 `ids=<模型 id>`");
        InstallItem? aItem = aCatalog.Find(aIds[0]);
        if (aItem == null) return SCP_CmdResult.Fail(1, $"⛔ 不認得的項目 '{aIds[0]}'");
        if (aItem.Kind != InstallKind.HfModel) return SCP_CmdResult.Fail(1, $"⛔ {aItem.Id} 不是模型 —— 只有模型會留下載暫存檔");
        List<FileInfo> aFiles = InstallRunner.PartialFiles(aEnv, aItem);
        r.Lines.Add($"## 計畫：清掉 {aItem.Id} 的 {aFiles.Count} 個暫存檔（{InstallProbe.FormatSize(aFiles.Sum(f => f.Length))}）");
        foreach (FileInfo f in aFiles) r.Lines.Add($"  - {f.Name}　{InstallProbe.FormatSize(f.Length)}　{f.LastWriteTime:yyyy-MM-dd HH:mm}");
        r.AddValue("partial_files", aFiles.Count.ToString(CultureInfo.InvariantCulture));
        if (aFiles.Count == 0) { r.Lines.Add("✓ 沒有暫存檔"); return r; }
        r.Lines.Add("  ⚠ 正在被寫入的會跳過（不刪別人正在下載的檔）");
        if (!aConfirm) { r.Lines.Add("· 沒有帶 confirm=1 ⇒ 只印計畫，什麼都沒動"); r.AddValue("dry_run", "1"); return r; }
        long aFreed = InstallRunner.CleanPartial(aEnv, aItem, l => Console.Error.WriteLine("  " + l));
        r.Lines.Add($"✓ 清掉 {InstallProbe.FormatSize(aFreed)}");
        r.AddValue("freed_bytes", aFreed.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    // ── resume_partial ──────────────────────────────────────────────

    // 把中斷留下的暫存檔從斷點接續下載完（Tim 2026-10-02）。⚠ op=install 對「不完整」的模型本來就會先做這一步；
    // 這支是「只接續、不做其他事」的顯式入口。
    static SCP_CmdResult ResumePartial(SCP_CmdResult r, InstallEnv aEnv, InstallCatalog aCatalog, List<string> aIds, bool aConfirm)
    {
        if (aIds.Count != 1) return SCP_CmdResult.Fail(2, "✗ op=resume_partial 一次一個 `ids=<模型 id>`");
        InstallItem? aItem = aCatalog.Find(aIds[0]);
        if (aItem == null) return SCP_CmdResult.Fail(1, $"⛔ 不認得的項目 '{aIds[0]}'");
        if (aItem.Kind != InstallKind.HfModel) return SCP_CmdResult.Fail(1, $"⛔ {aItem.Id} 不是模型 —— 只有模型會留下載暫存檔");
        List<FileInfo> aFiles = InstallRunner.PartialFiles(aEnv, aItem);
        r.Lines.Add($"## 計畫：從斷點接續 {aItem.Id} 的 {aFiles.Count} 個暫存檔（已下載 {InstallProbe.FormatSize(aFiles.Sum(f => f.Length))}，只補剩下的部分；來源 {aItem.Source}）");
        foreach (FileInfo f in aFiles) r.Lines.Add($"  - {f.Name}　{InstallProbe.FormatSize(f.Length)}　{f.LastWriteTime:yyyy-MM-dd HH:mm}");
        r.Lines.Add("  ⚠ 完整檔已經在的暫存檔直接刪；伺服器不接受續傳的跳過（之後 op=install 會整份重下）；下完驗 sha256，對不上就刪掉那個壞檔");
        r.AddValue("partial_files", aFiles.Count.ToString(CultureInfo.InvariantCulture));
        if (aFiles.Count == 0) { r.Lines.Add("✓ 沒有暫存檔"); return r; }
        if (!aConfirm) { r.Lines.Add("· 沒有帶 confirm=1 ⇒ 只印計畫，什麼都沒動"); r.AddValue("dry_run", "1"); return r; }
        string? aErr = InstallRunner.ResumePartial(aEnv, aItem, l => Console.Error.WriteLine("  " + l));
        InstallItemStatus aAfter = InstallProbe.Probe(aEnv, new[] { aItem })[0];
        r.Lines.Add(aErr != null ? "✗ " + aErr : "· 接續跑完");
        r.Lines.Add($"· 重新量：{InstallItemStatus.StateText(aAfter.State)}" + (aAfter.Detail.Length > 0 ? "　" + aAfter.Detail : ""));
        if (aAfter.State != InstallState.Installed)
            r.Lines.Add($"  ↳ 還缺的檔用 `senate cmd install --arg op=install --arg ids={aItem.Id} --arg confirm=1` 補齊");
        if (aErr != null) r.ExitCode = 5;
        r.AddValue("remaining_partial", InstallRunner.PartialFiles(aEnv, aItem).Count.ToString(CultureInfo.InvariantCulture));
        return r;
    }
}
