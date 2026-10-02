// 區塊職責：安裝系統的**計畫與動作** —— 要裝哪幾個（含相依）、怎麼裝、怎麼拆、能不能拆。
// 物理意義：計畫是純函式（清單＋讀數 ⇒ 步驟）；動作會改環境，所以每一步做完都**重新量一次**，
//           量到「已安裝」才算成功 —— pip 回 0 而 import 不進來、下載回 0 而缺檔，都是「看起來成功」。
// 數值影響：
//   · 安裝 pip：`python -m pip install <pipArgs>`（裝進選定的那一份；⛔ 不帶 -U，已滿足的相依不會被換掉 ——
//     🩸 舊的知識庫頁跑 `pip install -U torch`，會把 cu126 的 GPU 版換成 PyPI 的 CPU 版）。
//   · 安裝模型：`huggingface_hub.snapshot_download`（HF_HOME＝模型位置；中斷後再跑會接著下載）。
//   · 解除安裝 pip：`pip uninstall -y <dists>`，最多 3 輪直到量不到（user-site 與系統 site 可能各有一份）。
//   · 解除安裝模型：刪掉 `hub/models--…` 整個目錄（⚠ 不可復原；介面上要兩段式確認）。
// ⛔ 兩道擋（都在動手前）：還有**已安裝**的項目 requires 它 ⇒ 不拆；它**正在被使用** ⇒ 不拆。
using System.Diagnostics;

namespace Senate.Core;

/// <summary>一份計畫：照順序要做的項目（相依在前），以及擋下來的原因。</summary>
public sealed record InstallPlan(List<InstallItemStatus> Steps, List<string> Blocked, List<InstallItemStatus> AlreadyInstalled)
{
    public bool CanRun => Blocked.Count == 0 && Steps.Count > 0;
    public int SizeMb => Steps.Sum(s => s.Item.SizeMb);
}

public static class InstallPlanner
{
    /// <summary>安裝計畫：把要裝的項目連同（遞迴的）相依排好，已安裝的跳過。</summary>
    public static InstallPlan PlanInstall(InstallCatalog iCatalog, IReadOnlyList<InstallItemStatus> iStatus, IEnumerable<string> iIds)
    {
        var aBy = iStatus.ToDictionary(s => s.Item.Id, StringComparer.OrdinalIgnoreCase);
        var aSteps = new List<InstallItemStatus>();
        var aDone = new List<InstallItemStatus>();
        var aBlocked = new List<string>();
        var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string iId)
        {
            if (!aSeen.Add(iId)) return;
            InstallItem? aItem = iCatalog.Find(iId);
            if (aItem == null) { aBlocked.Add($"不認得的項目 '{iId}'（清單上沒有）"); return; }
            foreach (string r in aItem.Requires) Visit(r);   // 相依先排（深度優先 ⇒ 地基在前）
            InstallItemStatus s = aBy[aItem.Id];
            switch (s.State)
            {
                case InstallState.Installed: aDone.Add(s); break;
                // 量不到就不裝：「不知道有沒有」不是「沒有」—— 照裝可能蓋掉一份好的。
                case InstallState.Unknown: aBlocked.Add($"{aItem.Id}：{s.Detail}"); break;
                default: aSteps.Add(s); break;   // Missing／Broken／Partial
            }
        }
        foreach (string id in iIds) Visit(id.Trim());
        return new InstallPlan(aSteps, aBlocked, aDone);
    }

    /// <summary>還有哪些**已安裝**的項目（直接或間接）需要 <paramref name="iId"/>。</summary>
    public static List<string> InstalledDependents(InstallCatalog iCatalog, IReadOnlyList<InstallItemStatus> iStatus, string iId)
    {
        var aInstalled = new HashSet<string>(iStatus.Where(s => s.State is InstallState.Installed or InstallState.Broken or InstallState.Partial)
                                                    .Select(s => s.Item.Id), StringComparer.OrdinalIgnoreCase);
        var aOut = new List<string>();
        foreach (InstallItem i in iCatalog.Items)
        {
            if (string.Equals(i.Id, iId, StringComparison.OrdinalIgnoreCase) || !aInstalled.Contains(i.Id)) continue;
            if (DependsOn(iCatalog, i, iId, new HashSet<string>(StringComparer.OrdinalIgnoreCase))) aOut.Add(i.Id);
        }
        return aOut;
    }

    static bool DependsOn(InstallCatalog iCatalog, InstallItem iItem, string iTarget, HashSet<string> ioSeen)
    {
        foreach (string r in iItem.Requires)
        {
            if (string.Equals(r, iTarget, StringComparison.OrdinalIgnoreCase)) return true;
            InstallItem? aDep = iCatalog.Find(r);
            if (aDep != null && ioSeen.Add(r) && DependsOn(iCatalog, aDep, iTarget, ioSeen)) return true;
        }
        return false;
    }

    /// <summary>解除安裝前的檢查。回 null ＝ 可以拆；否則回擋下的原因。</summary>
    public static string? CheckUninstall(InstallCatalog iCatalog, InstallEnv iEnv, IReadOnlyList<InstallItemStatus> iStatus, string iId)
    {
        InstallItem? aItem = iCatalog.Find(iId);
        if (aItem == null) return $"不認得的項目 '{iId}'";
        InstallItemStatus s = iStatus.First(x => x.Item.Id == aItem.Id);
        if (s.State == InstallState.Missing) return $"{aItem.Id} 本來就沒安裝";
        if (s.State == InstallState.Unknown) return $"{aItem.Id} 量不到（{s.Detail}）—— 不知道有沒有就不拆";
        List<string> aDeps = InstalledDependents(iCatalog, iStatus, aItem.Id);
        if (aDeps.Count > 0)
            return $"還有已安裝的項目需要 {aItem.Id}：{string.Join("、", aDeps)} —— 先拆它們，或留著這一個";
        string? aInUse = InstallRunner.InUse(iEnv, aItem);
        return aInUse;
    }
}

/// <summary>真正動手的那一層。所有輸出逐行交給 <c>iLog</c>（CLI 印出來、後台頁收進進度框）。</summary>
public static class InstallRunner
{
    const int PipTimeoutSec = 60 * 60;       // torch 一份 2.5 GB，慢網路要很久
    const int ModelTimeoutSec = 3 * 60 * 60;

    /// <summary>裝一個項目（不處理相依 —— 那是 PlanInstall 的事）。回 null ＝ 成功（重新量到已安裝）。</summary>
    public static string? Install(InstallEnv iEnv, InstallItem iItem, Action<string> iLog)
    {
        if (iEnv.PythonExe == null) return "沒有 Python：" + iEnv.PythonError;
        InstallProcessResult r;
        if (iItem.Kind == InstallKind.Pip)
        {
            var aArgs = new List<string> { "-m", "pip", "install", "--disable-pip-version-check", "--no-input" };
            aArgs.AddRange(iItem.PipArgs);
            iLog($"$ {iEnv.PythonExe} {string.Join(" ", aArgs)}");
            r = InstallProcess.Run(iEnv.PythonExe, aArgs, iEnv.ChildEnvironment(), PipTimeoutSec, iLog, "pip install " + iItem.Id);
        }
        else
        {
            Directory.CreateDirectory(iEnv.ModelsRoot);
            string aPy = "from huggingface_hub import snapshot_download\n"
                       + $"p = snapshot_download(repo_id={System.Text.Json.JsonSerializer.Serialize(iItem.Repo)})\n"
                       + "print('snapshot:', p)";
            iLog($"$ 下載 {iItem.Repo} → {InstallProbe.ModelDir(iEnv, iItem)}（HF_HOME={iEnv.ModelsRoot}）");
            r = InstallProcess.Run(iEnv.PythonExe, new[] { "-c", aPy }, iEnv.ChildEnvironment(), ModelTimeoutSec, iLog, "download " + iItem.Id);
        }

        if (r.StartError != null) return "起不了子程序：" + r.StartError;
        if (r.TimedOut) return "逾時，已中止（再按一次安裝：pip 會重來、模型會接著下載）";

        // ⭐ 判定看「重新量一次」，不看 exit code。
        InstallItemStatus aAfter = InstallProbe.Probe(iEnv, new[] { iItem })[0];
        if (aAfter.State == InstallState.Installed)
        {
            iLog($"✓ {iItem.Id}：重新量過 ⇒ 已安裝 {aAfter.Version}");
            return null;
        }
        return $"指令回 exit {r.ExitCode}，但重新量是「{InstallItemStatus.StateText(aAfter.State)}」{(aAfter.Detail.Length > 0 ? "：" + aAfter.Detail : "")}"
               + (r.ExitCode != 0 ? "\n" + InstallProbe.Tail(r.StdErr) : "");
    }

    /// <summary>解除安裝一個項目（呼叫前要先過 <see cref="InstallPlanner.CheckUninstall"/>）。回 null ＝ 成功（重新量到沒安裝）。</summary>
    public static string? Uninstall(InstallEnv iEnv, InstallItem iItem, Action<string> iLog)
    {
        if (iItem.Kind == InstallKind.HfModel)
        {
            string aDir = InstallProbe.ModelDir(iEnv, iItem);
            iLog($"刪除 {aDir}");
            try { if (Directory.Exists(aDir)) Directory.Delete(aDir, recursive: true); }
            catch (Exception e) { return $"刪不掉（可能有檔案正在被讀）：{e.Message} —— 可能已刪掉一部分，狀態請以重新量為準"; }
        }
        else
        {
            if (iEnv.PythonExe == null) return "沒有 Python：" + iEnv.PythonError;
            // 最多 3 輪：user-site 與系統 site 各有一份時，pip 一次只拆它先看到的那份。
            for (int aRound = 1; aRound <= 3; aRound++)
            {
                var aArgs = new List<string> { "-m", "pip", "uninstall", "-y", "--disable-pip-version-check" };
                aArgs.AddRange(iItem.Dists);
                iLog($"$ （第 {aRound} 輪）{iEnv.PythonExe} {string.Join(" ", aArgs)}");
                InstallProcessResult r = InstallProcess.Run(iEnv.PythonExe, aArgs, iEnv.ChildEnvironment(), 600, iLog, "pip uninstall " + iItem.Id);
                if (r.StartError != null) return "起不了子程序：" + r.StartError;
                if (InstallProbe.Probe(iEnv, new[] { iItem })[0].State == InstallState.Missing) break;
            }
        }
        InstallItemStatus aAfter = InstallProbe.Probe(iEnv, new[] { iItem })[0];
        if (aAfter.State == InstallState.Missing) { iLog($"✓ {iItem.Id}：重新量過 ⇒ 沒安裝"); return null; }
        return $"拆完重新量還是「{InstallItemStatus.StateText(aAfter.State)}」{(aAfter.Detail.Length > 0 ? "：" + aAfter.Detail : "")}";
    }

    /// <summary>
    /// 它現在是不是正在被使用。回 null ＝ 沒有；否則回原因。
    /// <para>pip：有任何一顆**同一個 python.exe** 在跑 ⇒ 那顆可能正 import 著它（Windows 上拆一半會留殘檔）。</para>
    /// <para>模型：逐檔試著獨佔開啟；開不了 ＝ 有人正讀著（⛔ 不刪一半）。</para>
    /// </summary>
    public static string? InUse(InstallEnv iEnv, InstallItem iItem)
    {
        if (iItem.Kind == InstallKind.HfModel)
        {
            string aDir = InstallProbe.ModelDir(iEnv, iItem);
            if (!Directory.Exists(aDir)) return null;
            foreach (string f in Directory.EnumerateFiles(aDir, "*", SearchOption.AllDirectories))
            {
                try { using var _ = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) { return $"模型檔正在被使用：{f} —— 先關掉用它的程式（例：知識庫檢索）"; }
                catch (UnauthorizedAccessException) { /* 唯讀檔：不代表有人在用 */ }
            }
            return null;
        }

        if (iEnv.PythonExe == null) return null;
        string aExe = Path.GetFullPath(iEnv.PythonExe);
        var aUsers = new List<string>();
        foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(aExe)))
        {
            try
            {
                string? aPath = p.MainModule?.FileName;
                if (aPath != null && string.Equals(Path.GetFullPath(aPath), aExe, StringComparison.OrdinalIgnoreCase))
                    aUsers.Add($"pid {p.Id}");
            }
            catch { /* 權限不足讀不到路徑：不當成在用（但也不當成沒在用 —— 見下） */ }
            finally { p.Dispose(); }
        }
        return aUsers.Count > 0
            ? $"這一份 Python 現在有 {aUsers.Count} 個程序在跑（{string.Join("、", aUsers)}）—— 它們可能正在用 {iItem.Id}，先停掉再拆"
            : null;
    }

    /// <summary>在使用者指定的空資料夾建一份 venv（用系統 Python 當基底）。回 null ＝ 成功。</summary>
    public static string? CreateVenv(string iDir, Action<string> iLog)
    {
        string? aBase = InstallEnv.FindSystemPython(out string aWhy);
        if (aBase == null) return "建 venv 要一份系統 Python 當基底：" + aWhy;
        iLog($"$ {aBase} -m venv {iDir}");
        InstallProcessResult r = InstallProcess.Run(aBase, new[] { "-m", "venv", iDir }, null, 600, iLog, "create venv");
        if (r.StartError != null) return "起不了子程序：" + r.StartError;
        string? aExe = InstallEnv.PythonInDir(iDir);
        if (aExe == null) return $"venv 建完找不到 python.exe（exit {r.ExitCode}）：{InstallProbe.Tail(r.StdErr)}";
        iLog($"✓ venv 建好了：{aExe}");
        return null;
    }
}
