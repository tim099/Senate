// 區塊職責：安裝系統的**環境解析** —— 套件要裝進哪一顆 Python、模型要下載到哪裡；以及跑子程序的那一支。
// 物理意義：Tim 2026-10-02 拍板：「自己管理（或可以指定路徑），也可以預設去找系統安裝的 Python，
//           但使用者可以手動指定新路徑去安裝」「看該路徑下 Python 環境配置」。
//           ⇒ 兩格路徑（`PythonEnvRoot`／`ModelsRoot`，見 SCP_PathRegistry）都是**空白＝用預設**：
//             Python 空白 ＝ 從 PATH 找系統那一顆；模型空白 ＝ HF 自己的預設快取。
// 數值影響：Resolve 純讀（只看檔案在不在）。Run 會起子程序 —— 呼叫端決定跑什麼。
//
// 🩸 為什麼要收斂成一份（2026-10-02 量）：Unity 那邊三套各自叫 Python ——
//   runner 從 PATH 叫 `python`、`memory.py` 用 `sys.executable`、`media_admin.py` 裝到 `--user`
//   而 `knowledge_base.py` 不帶 `--user` ⇒「裝在哪一份」由呼叫端決定，而它不會叫。
//   `media_admin.py` 解除安裝要跑最多 4 輪，就是在收這個爛攤子（user-site 與系統 site 各一份）。
// ⚠ 「找不到 Python」是 **量不到**，不是「每一項都沒裝」—— 狀態頁要把它印成「？」，⛔ 不是一排「沒安裝」。
using System.Diagnostics;
using System.Text;
using SCP.Core.Paths;
using SCP.Core.Proc;

namespace Senate.Core;

/// <summary>解析出來的安裝環境（一次快照；設定改了要重新 Resolve）。</summary>
public sealed class InstallEnv
{
    /// <summary>要用的 python.exe（null ＝ 找不到，原因在 <see cref="PythonError"/>）。</summary>
    public string? PythonExe { get; init; }

    /// <summary>這顆 Python 是怎麼決定的（給人看：「手填」／「自動找到系統的」…）。</summary>
    public string PythonOrigin { get; init; } = "";

    public string? PythonError { get; init; }

    /// <summary>使用者指定了一個資料夾，而那裡還沒有 Python ⇒ 可以在那裡建一份 venv。</summary>
    public string? CreatableEnvDir { get; init; }

    /// <summary>這顆 Python 是不是 venv（有 pyvenv.cfg）。</summary>
    public bool IsVenv { get; init; }

    /// <summary>HF_HOME（模型快取根；模型在它底下的 hub/）。</summary>
    public string ModelsRoot { get; init; } = "";

    public string ModelsOrigin { get; init; } = "";

    /// <summary>設定檔本身壞掉時的原因（那是「量不到」，不是「用預設」）。</summary>
    public string? ConfigError { get; init; }

    /// <summary>HF 的預設位置（HF_HOME 沒設時它自己用的那個）。</summary>
    public static string DefaultModelsRoot()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "huggingface").Replace('\\', '/');

    /// <summary>從設定檔解析。<paramref name="iConfig"/> 為 null ＝ 沒有設定檔（兩格都走預設）。</summary>
    public static InstallEnv Resolve(SenateConfig? iConfig)
    {
        SCP_PathResolution aPy = iConfig == null
            ? new SCP_PathResolution("", "未設定", "沒有設定檔")
            : SCP_PathRegistry.Resolve(SCP_PathId.PythonEnvRoot, id => SenatePathBinding.StoredOf(iConfig, id));
        SCP_PathResolution aModels = iConfig == null
            ? new SCP_PathResolution("", "未設定", "沒有設定檔")
            : SCP_PathRegistry.Resolve(SCP_PathId.ModelsRoot, id => SenatePathBinding.StoredOf(iConfig, id));

        // 「取不到」（設定狀態壞了）不准退成預設 —— 那會讓「我設的路徑沒生效」長成「一切正常」。
        if (aPy.Origin == "取不到" || aModels.Origin == "取不到")
            return new InstallEnv { ConfigError = aPy.Error ?? aModels.Error, PythonError = "設定讀不了 ⇒ 量不到" };

        string aModelsRoot, aModelsOrigin;
        if (aModels.Error == null) { aModelsRoot = aModels.Value; aModelsOrigin = "手填（路徑管理頁）"; }
        else { aModelsRoot = DefaultModelsRoot(); aModelsOrigin = "預設（HF 自己的快取位置）"; }

        if (aPy.Error == null)
        {
            string aDir = aPy.Value;
            string? aExe = PythonInDir(aDir);
            if (aExe != null)
                return new InstallEnv
                {
                    PythonExe = aExe, PythonOrigin = "手填（路徑管理頁）",
                    IsVenv = IsVenvExe(aExe), ModelsRoot = aModelsRoot, ModelsOrigin = aModelsOrigin,
                };
            // 指定了資料夾但裡面沒有 Python ⇒ 不退回系統那顆（那是靜默改變安裝對象），而是說「可以在這裡建一份」。
            bool aCreatable = !File.Exists(aDir) && (!Directory.Exists(aDir) || !Directory.EnumerateFileSystemEntries(aDir).Any());
            return new InstallEnv
            {
                PythonOrigin = "手填（路徑管理頁）",
                PythonError = aCreatable
                    ? $"指定的資料夾裡還沒有 Python：{aDir} —— 可以在這裡建一份 venv（`op=create_env`）"
                    : $"指定的資料夾裡找不到 python.exe（也不是空資料夾，不會在那裡建 venv）：{aDir}",
                CreatableEnvDir = aCreatable ? aDir : null,
                ModelsRoot = aModelsRoot, ModelsOrigin = aModelsOrigin,
            };
        }

        string? aSys = FindSystemPython(out string aWhy);
        return new InstallEnv
        {
            PythonExe = aSys,
            PythonOrigin = aSys != null ? "自動找到系統安裝的（PATH）" : "自動找系統的",
            PythonError = aSys == null ? aWhy : null,
            IsVenv = aSys != null && IsVenvExe(aSys),
            ModelsRoot = aModelsRoot, ModelsOrigin = aModelsOrigin,
        };
    }

    /// <summary>資料夾裡的 python：安裝目錄（python.exe）或 venv（Scripts/python.exe、bin/python）。</summary>
    public static string? PythonInDir(string iDir)
    {
        foreach (string aRel in new[] { "python.exe", "Scripts/python.exe", "bin/python" })
        {
            string p = Path.Combine(iDir, aRel);
            if (File.Exists(p)) return Path.GetFullPath(p).Replace('\\', '/');
        }
        return null;
    }

    static bool IsVenvExe(string iExe)
    {
        string? aDir = Path.GetDirectoryName(iExe);
        return aDir != null && (File.Exists(Path.Combine(aDir, "pyvenv.cfg"))
                                || File.Exists(Path.Combine(Path.GetDirectoryName(aDir) ?? aDir, "pyvenv.cfg")));
    }

    /// <summary>
    /// 從 PATH 找系統的 python（照 PATH 順序，跟 `where python` 同一個答案）。
    /// <para>⚠ 跳過 `WindowsApps` 底下那顆：那是 Microsoft Store 的轉址殼，執行它會開商店或回 9009，
    /// 而它在 PATH 上排得很前面 ⇒ 不跳過的話「找到 Python」跟「找到一個會開商店的捷徑」同形。</para>
    /// </summary>
    public static string? FindSystemPython(out string oWhy)
    {
        oWhy = "";
        string aPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        string[] aNames = OperatingSystem.IsWindows() ? new[] { "python.exe" } : new[] { "python3", "python" };
        int aSkipped = 0;
        foreach (string aDir in aPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string aName in aNames)
            {
                string p;
                try { p = Path.Combine(aDir.Trim('"'), aName); } catch (ArgumentException) { continue; }
                if (!File.Exists(p)) continue;
                if (p.IndexOf("WindowsApps", StringComparison.OrdinalIgnoreCase) >= 0) { aSkipped++; continue; }
                return Path.GetFullPath(p).Replace('\\', '/');
            }
        }
        oWhy = "PATH 上找不到 Python" + (aSkipped > 0 ? $"（跳過了 {aSkipped} 個 Microsoft Store 轉址殼）" : "")
               + " —— 裝一份 Python，或在「路徑管理」頁指定一個資料夾讓這裡建 venv";
        return null;
    }

    /// <summary>給子程序的環境變數：UTF-8 輸出，以及模型位置（HF_HOME）。</summary>
    public Dictionary<string, string> ChildEnvironment()
    {
        var d = new Dictionary<string, string>
        {
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
            // ⚠ 一律顯式給 HF_HOME（就算是預設值）：使用者機器上若另外設過 HF_HOME，
            //   不給的話子程序會照它自己的那一格下載 ⇒ 「狀態頁量的位置」與「實際下載的位置」分家，而它不會叫。
            ["HF_HOME"] = ModelsRoot,
            ["HF_HUB_DISABLE_SYMLINKS_WARNING"] = "1",
        };
        return d;
    }
}

/// <summary>一次子程序的結果。</summary>
public sealed record InstallProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut, string? StartError);

/// <summary>跑子程序（ArgumentList、兩條輸出非同步讀、逾時就砍、登記進程序名冊）。</summary>
public static class InstallProcess
{
    public const string RegistryTag = "install";

    public static InstallProcessResult Run(string iExe, IEnumerable<string> iArgs, IReadOnlyDictionary<string, string>? iEnv,
                                           int iTimeoutSec, Action<string>? iOnLine = null, string iDescription = "")
    {
        var aPsi = new ProcessStartInfo(iExe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in iArgs) aPsi.ArgumentList.Add(a);
        if (iEnv != null) foreach (var kv in iEnv) aPsi.Environment[kv.Key] = kv.Value;

        var aOut = new StringBuilder();
        var aErr = new StringBuilder();
        using var aProc = new Process { StartInfo = aPsi };
        // ⚠ 兩條都要即時讀：只讀一條的話，另一條的管線塞滿（pip 的進度輸出很多）子程序就卡住，
        //   而那個症狀跟「下載很慢」同形。
        aProc.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (aOut) aOut.AppendLine(e.Data); iOnLine?.Invoke(e.Data); };
        aProc.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (aErr) aErr.AppendLine(e.Data); iOnLine?.Invoke(e.Data); };
        try { aProc.Start(); }
        catch (Exception e) { return new InstallProcessResult(-1, "", "", false, $"{e.GetType().Name}: {e.Message}"); }

        using (SCP_ProcessRegistry.RegisterScope(aProc, RegistryTag, iDescription, "senate-install"))
        {
            aProc.BeginOutputReadLine();
            aProc.BeginErrorReadLine();
            bool aDone = aProc.WaitForExit(iTimeoutSec * 1000);
            if (!aDone)
            {
                try { aProc.Kill(entireProcessTree: true); } catch { /* 已經結束 */ }
                aProc.WaitForExit(5000);
                return new InstallProcessResult(-1, aOut.ToString(), aErr.ToString(), true, null);
            }
            aProc.WaitForExit();   // 等非同步讀完最後幾行
            return new InstallProcessResult(aProc.ExitCode, aOut.ToString(), aErr.ToString(), false, null);
        }
    }
}
