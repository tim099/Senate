// 區塊職責：`senate cmd llm` —— 本地大語言模型（ollama）的狀態／目錄／安裝／解除安裝／顯存卸載／試跑（TASK-0383）。
//           「AI 模型」後台頁走這一支。
// 物理意義：真正持有模型的是 **ollama**（下載、量化、磁碟位置、載入卸載都是它的事），本檔不重造那一層，只做 ollama 沒有的兩件事：
//           ① 目錄（哪些模型適合這個專案、要多少顯存 —— 策展知識）；② 結構化輸出（後台頁讀 `format=json`）。
//           ⭐ ollama 與它的模型**不進安裝系統**（Tim 2026-10-05 拍板）：狀態住在 ollama 服務裡，誰去讀 `ollama list` 都是同一份。
// 數值影響：install／uninstall 會動磁碟（模型 0.5–20 GB），⛔ 不帶 confirm=1 只印計畫；其餘 op 唯讀或只動顯存（stop）。
//           本檔**不啟動也不停止 ollama 服務**，也**不代跑 ollama 本體的安裝**（官方安裝是遠端 PowerShell 腳本 —— 只給連結）。
// ⚠ 三個「不得同形」：
//   · 找不到 ollama／服務打不到 ⇒ 已安裝清單是「不知道」（installed_known=false），⛔ 不是「0 個」。
//   · 顯存偵測失敗 ⇒ 門檻來源標 fallback，⛔ 不假裝是量到的。
//   · 試跑撞到生成上限 ⇒ ok=false＋truncated（半句話不是回答）。
// ⚠ exit code：0 成功；1 擋下（零變動）；2 用法錯；4 量不到（找不到 ollama／服務打不到）；5 動手了但沒成功。
// ⚠ 酒保（TASK-0365）走本檔的 Chat。
#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SCP.Core.Cmd;

namespace Senate.Core;

/// <summary>目錄裡的一顆模型。`SizeGb`＝下載量，`VramGb`＝實際顯存估值（含 KV cache 與執行期開銷）—— 兩個數字不要混用。</summary>
public sealed record LlmCatalogEntry(string Id, string Params, double SizeGb, double VramGb, int Zh, string Family, bool Recommend, string Note);

/// <summary>`ollama list`／`ollama ps` 的一列。</summary>
public sealed record LlmModelRow(string Id, string Size, string Processor, int? ContextLength = null);

/// <summary>顯存讀數與門檻。Source：manual／gpu_free／gpu_total／fallback。</summary>
public sealed record LlmVram(bool GpuOk, string GpuName, double TotalGb, double FreeGb, double UsedGb, string Error, double BudgetGb, string Source, string Basis);

public static class LlmOllama
{
    public const string ApiBase = "http://127.0.0.1:11434";
    public const string DownloadUrl = "https://ollama.com/download";
    public const string InstallCommand = "irm https://ollama.com/install.ps1 | iex";
    public const int DefaultTimeoutMs = 60_000;
    public const int PullTimeoutMs = 0;                // 不設上限：20 GB 的模型在慢線上可以跑好幾小時，下載慢不是異常；要停就用「中斷」
    public const double VramFallbackGb = 6.0;
    const double MibPerGb = 1024.0;

    // 區塊職責：模型目錄（策展清單，逐筆照搬 llm_admin.py 的 CATALOG）。
    // 物理意義：不是「ollama 有哪些模型」，是**這個專案挑過的那幾個**：純聊天（酒保）用得上、中文可用。
    // 數值影響：只影響顯示與預設篩選；裝了沒一律以 `ollama list` 的回報為準。
    // ⚠ 版本號會過期 —— 清單是候選，不保證存在；`ollama pull` 失敗時錯誤訊息會直說。
    public static readonly IReadOnlyList<LlmCatalogEntry> Catalog = new[]
    {
        new LlmCatalogEntry("qwen3:4b", "4B", 2.4, 3.2, 5, "Qwen", true, "純聊天首選 —— 中文語感最好、指令跟得住，6GB 卡還留得下餘裕。"),
        new LlmCatalogEntry("qwen3:1.7b", "1.7B", 1.1, 1.7, 4, "Qwen", true, "極省。酒保那種短句綽綽有餘，載入快、留給其他程式的顯存最多。"),
        new LlmCatalogEntry("qwen3:0.6b", "0.6B", 0.5, 0.9, 3, "Qwen", false, "最小。適合純罐頭句與極低配機器；語感會明顯變鈍。"),
        new LlmCatalogEntry("qwen2.5:3b", "3B", 1.9, 2.7, 4, "Qwen", false, "上一代，穩定成熟；Qwen3 拉不到時的退路。"),
        new LlmCatalogEntry("gemma3:4b", "4B", 2.6, 3.4, 4, "Gemma", false, "多語系穩、語氣自然；授權走 Gemma 條款（非 OSI），商用前先看。"),
        new LlmCatalogEntry("phi4-mini", "3.8B", 2.3, 3.1, 2, "Phi", false, "英文強、中文偏弱 —— 酒保講中文的話不推。MIT 授權。"),
        new LlmCatalogEntry("llama3.2:3b", "3B", 2.0, 2.8, 2, "Llama", false, "中文是弱項；列在這裡是為了對照，不是為了用。"),
        new LlmCatalogEntry("qwen3:8b", "8B", 4.7, 6.2, 5, "Qwen", false, "品質明顯高一階。6GB 卡剛好卡在邊界 —— 8GB 起跳才穩。"),
        new LlmCatalogEntry("qwen3:14b", "14B", 9.0, 11.0, 5, "Qwen", false, "12GB 卡的主力。聊天已經有明顯「懂梗」的差距。"),
        new LlmCatalogEntry("qwen3:30b-a3b", "30B-MoE", 18.0, 20.0, 5, "Qwen", false, "MoE：每次只活化約 3B ⇒ 算得快、可是顯存照整包吃。24GB 卡適用。"),
        new LlmCatalogEntry("qwen3:32b", "32B", 20.0, 23.0, 5, "Qwen", false, "24GB 卡的上限附近。純聊天用它是殺雞用牛刀，但中文最好。"),
        new LlmCatalogEntry("gemma3:12b", "12B", 8.1, 10.0, 4, "Gemma", false, "Gemma 家的中量級；多語系穩。"),
        new LlmCatalogEntry("gemma3:27b", "27B", 17.0, 19.5, 4, "Gemma", false, "Gemma 家上限；24GB 卡適用。"),
        new LlmCatalogEntry("llama3.1:8b", "8B", 4.9, 6.4, 3, "Llama", false, "英文生態最廣、工具鏈最多；中文普通。"),
        new LlmCatalogEntry("mistral-small", "24B", 14.0, 16.5, 3, "Mistral", false, "Apache-2.0 的中大型；歐語系強，中文一般。"),
    };

    // 區塊職責：找 ollama 執行檔（PATH 優先，找不到再查已知安裝位置）。
    // 物理意義：Windows 裝完 ollama 之後，**已經在跑的行程拿到的 PATH 是舊的** ⇒ PATH 找不到而磁碟上明明在，
    //          症狀跟「沒安裝」一模一樣，使用者會照提示再裝一次（閉環）。⇒ 找到後一律用絕對路徑跑。
    public static string? FindExe(out bool oOnPath) => FindTool("ollama", new[]
    {
        @"%LOCALAPPDATA%\Programs\Ollama\ollama.exe",
        @"%PROGRAMFILES%\Ollama\ollama.exe",
    }, out oOnPath);

    static string? FindNvidiaSmi() => FindTool("nvidia-smi", new[]
    {
        @"%SYSTEMROOT%\System32\nvidia-smi.exe",
        @"%PROGRAMFILES%\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    }, out _);

    static string? FindTool(string iName, string[] iKnown, out bool oOnPath)
    {
        oOnPath = false;
        string[] aExts = OperatingSystem.IsWindows() ? new[] { ".exe", "" } : new[] { "" };
        foreach (string aDir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (string aExt in aExts)
            {
                string c;
                try { c = Path.Combine(aDir.Trim('"'), iName + aExt); } catch (ArgumentException) { continue; }
                if (File.Exists(c)) { oOnPath = true; return c; }
            }
        foreach (string raw in iKnown)
        {
            string c = Environment.ExpandEnvironmentVariables(raw);
            if (File.Exists(c)) return c;
        }
        return null;
    }

    // 區塊職責：中斷正在跑的 ollama 動作（下載、試跑）。
    // 物理意義：後台頁在同一個行程裡呼叫本 Cmd ⇒ 用一個行程內的取消源；每次中斷後換一顆新的，之後的動作不受影響。
    //          試跑的「中斷」必須是**關掉連線**：`ollama stop` 只把模型標成到期，ollama 會等進行中的請求跑完才卸載。
    static readonly object s_CancelLock = new();
    static CancellationTokenSource s_Cancel = new();

    /// <summary>中斷目前在跑的下載／試跑（殺掉 ollama pull、關掉試跑的連線）。沒有在跑的話什麼都不做。</summary>
    public static void CancelRunning()
    {
        lock (s_CancelLock) { s_Cancel.Cancel(); s_Cancel = new CancellationTokenSource(); }
    }

    internal static CancellationToken CurrentToken { get { lock (s_CancelLock) return s_Cancel.Token; } }

    public const int CancelledExitCode = -2;

    /// <summary>跑一次外部程式。回 (exit, stdout, stderr)；啟動失敗或逾時回 exit -1、被中斷回 -2，原因放在 stderr。iTimeoutMs ≤ 0 ＝ 不設上限（只能靠中斷）。</summary>
    public static (int Code, string Out, string Err) Run(string iExe, IEnumerable<string> iArgs, int iTimeoutMs)
    {
        CancellationToken aCancel = CurrentToken;
        var psi = new ProcessStartInfo(iExe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in iArgs) psi.ArgumentList.Add(a);
        try
        {
            using Process p = Process.Start(psi)!;
            Task<string> aOut = p.StandardOutput.ReadToEndAsync(), aErr = p.StandardError.ReadToEndAsync();
            var sw = Stopwatch.StartNew();
            while (!p.WaitForExit(200))
            {
                bool aTimedOut = iTimeoutMs > 0 && sw.ElapsedMilliseconds > iTimeoutMs;
                if (!aTimedOut && !aCancel.IsCancellationRequested) continue;
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return aTimedOut
                    ? (-1, "", $"逾時（{iTimeoutMs / 1000} 秒）—— {Path.GetFileName(iExe)} {string.Join(' ', iArgs)}")
                    : (CancelledExitCode, "", $"已中斷 —— {Path.GetFileName(iExe)} {string.Join(' ', iArgs)}");
            }
            p.WaitForExit();
            return (p.ExitCode, aOut.Result, aErr.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, "", "執行失敗：" + e.Message);
        }
    }

    // 區塊職責：解析 ollama 的表格輸出（list／ps）。
    // 物理意義：**照表頭的欄位起點切**，不照空白切 —— 欄位值本身含空白（「2.5 GB」「100% GPU」「4 minutes from now」），
    //          而 ollama 改版會加欄（ps 多了 CONTEXT），照空白數第幾格的寫法會安靜地讀錯欄。
    public static List<Dictionary<string, string>> ParseTable(string iText)
    {
        var aRows = new List<Dictionary<string, string>>();
        string[] aLines = iText.Replace("\r", "").Split('\n');
        int h = Array.FindIndex(aLines, l => l.Trim().Length > 0);
        if (h < 0) return aRows;
        string aHead = aLines[h];
        var aCols = new List<(string Name, int Start)>();
        for (int i = 0; i < aHead.Length;)
        {
            if (aHead[i] == ' ' || aHead[i] == '\t') { i++; continue; }
            int s = i;
            while (i < aHead.Length && aHead[i] != ' ' && aHead[i] != '\t') i++;
            aCols.Add((aHead.Substring(s, i - s), s));
        }
        for (int k = h + 1; k < aLines.Length; k++)
        {
            string l = aLines[k];
            if (l.Trim().Length == 0) continue;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < aCols.Count; c++)
            {
                int s = aCols[c].Start, e = c + 1 < aCols.Count ? aCols[c + 1].Start : l.Length;
                d[aCols[c].Name] = s >= l.Length ? "" : l.Substring(s, Math.Min(e, l.Length) - s).Trim();
            }
            // 第一欄（NAME）一定不含空白：用空白切的第一格補正，防表頭與內容對不齊時把名字切斷。
            d[aCols[0].Name] = l.Trim().Split(' ', '\t')[0];
            aRows.Add(d);
        }
        return aRows;
    }

    static string Col(Dictionary<string, string> d, string k) => d.TryGetValue(k, out string? v) ? v : "";

    public static List<LlmModelRow> ToRows(List<Dictionary<string, string>> iTable)
        => iTable.Select(d => new LlmModelRow(Col(d, "NAME"), Col(d, "SIZE"), Col(d, "PROCESSOR"),
            int.TryParse(Col(d, "CONTEXT"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : null))
            .Where(r => r.Id.Length > 0).ToList();

    /// <summary>
    /// 目錄的這顆裝了沒。精確命中，或**同家族、且已裝的 tag 是「這個 tag」或「這個 tag-變體」**（`qwen3:4b` ↔ `qwen3:4b-instruct-q4_K_M`）——
    /// 刻意寬鬆：使用者手動 pull 過變體時，不能說「未安裝」而磁碟上明明有。回 (命中, 是否精確)。
    /// ⚠ llm_admin.py 原版判的是「tag 出現在名稱裡」⇒ 裝了 `qwen3:14b` 會把 `qwen3:4b` 判成已裝（14b 含 4b）。這裡只認 tag 的開頭。
    /// 沒寫 tag 的（`phi4-mini`）ollama 會補成 `:latest`。
    /// </summary>
    public static (bool Installed, bool Exact) Match(string iId, IEnumerable<string> iHave)
    {
        var aHave = iHave.ToList();
        string aId = iId.Contains(':') ? iId : iId + ":latest";
        if (aHave.Contains(iId) || aHave.Contains(aId)) return (true, true);
        int c = aId.IndexOf(':');
        string aFamily = aId[..c], aTag = aId[(c + 1)..];
        bool aVariant = aHave.Any(h =>
        {
            int k = h.IndexOf(':');
            if (k < 0 || h[..k] != aFamily) return false;
            string t = h[(k + 1)..];
            return t == aTag || t.StartsWith(aTag + "-", StringComparison.Ordinal);
        });
        return (aVariant, false);
    }

    // 區塊職責：顯存門檻。優先序 **手動 > 偵測 > 保底**。
    // 物理意義：偵測失敗必須明講（source=fallback＋error）—— 靜默退回保底值的話，「這張卡放不下」跟「沒量到你的卡」長得一樣。
    // 數值影響：只影響 fits_budget（預設列不列），不影響能不能安裝、不影響實際載入。
    public static LlmVram ResolveVram(double iManualGb, string iBasis)
    {
        string aBasis = iBasis == "total" ? "total" : "free";
        bool ok = false; string aName = "", aErr = ""; double aTotal = 0, aFree = 0, aUsed = 0;
        string? aSmi = FindNvidiaSmi();
        if (aSmi == null) aErr = "找不到 nvidia-smi —— 沒有 NVIDIA 獨顯，或驅動剛裝完（本行程的 PATH 是舊的）。AMD／Intel 顯卡請改用手動填寫。";
        else
        {
            (int c, string o, string e) = Run(aSmi, new[] { "--query-gpu=name,memory.total,memory.free,memory.used", "--format=csv,noheader,nounits" }, 20_000);
            string aLine = o.Replace("\r", "").Split('\n').FirstOrDefault(x => x.Trim().Length > 0) ?? "";
            string[] p = aLine.Split(',').Select(x => x.Trim()).ToArray();
            if (c != 0) aErr = $"nvidia-smi 退出碼 {c}：{(e + o).Trim()}";
            else if (p.Length < 4) aErr = "nvidia-smi 輸出無法解析：" + aLine;
            else if (double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t)
                  && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double f)
                  && double.TryParse(p[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double u))
            {
                ok = true; aName = p[0];
                aTotal = Math.Round(t / MibPerGb, 2); aFree = Math.Round(f / MibPerGb, 2); aUsed = Math.Round(u / MibPerGb, 2);
            }
            else aErr = "顯存數值無法解析：" + aLine;
        }
        if (iManualGb > 0) return new LlmVram(ok, aName, aTotal, aFree, aUsed, aErr, Math.Round(iManualGb, 2), "manual", aBasis);
        if (ok) return new LlmVram(ok, aName, aTotal, aFree, aUsed, "", aBasis == "free" ? aFree : aTotal, "gpu_" + aBasis, aBasis);
        return new LlmVram(ok, aName, aTotal, aFree, aUsed, aErr, VramFallbackGb, "fallback", aBasis);
    }

    public static string VramSourceText(string iSource) => iSource switch
    {
        "manual" => "手動指定",
        "gpu_free" => "偵測可用量",
        "gpu_total" => "偵測總量",
        "fallback" => "保底值（不是量到的）",
        _ => iSource,
    };
}

public sealed class Cmd_Llm : SCP_Cmd
{
    public override string Name => "llm";
    public override string Category => SCP_CmdCategory.System;

    public override string Summary => "本地大語言模型（ollama）：狀態／目錄／安裝／解除安裝／顯存卸載／試跑（install／uninstall 不帶 confirm=1 只印計畫）";

    public override string Details =>
        "真正持有模型的是 ollama；本支只做目錄（這個專案挑過的模型與顯存估值）與結構化輸出。⭐ ollama 與它的模型**不走安裝系統**。\n"
        + "⚠ 找不到 ollama 或服務打不到 ⇒ exit 4（量不到），已安裝清單是「不知道」不是「0 個」。\n"
        + "⚠ ollama 本體不代裝：到 " + LlmOllama.DownloadUrl + "（Windows 官方指令：`" + LlmOllama.InstallCommand + "`），裝完重開 Senate 讓 PATH 生效。\n"
        + "⚠ install／uninstall 會動磁碟（模型 0.5–20 GB）：先不帶 confirm 看計畫，同意了再加 `--arg confirm=1`。\n"
        + "⚠ 試跑撞到生成上限 ⇒ ok=false（被切斷的半句話不是回答）；thinking 模型要看 `think=1` 的思考段才知道它在想還是死了。";

    public override string Example => SCP_CmdRegistry.Invoke("llm --arg op=test --arg model=qwen3:0.6b");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "status（預設；含目錄×已安裝×顯存）｜install｜uninstall｜ps｜stop｜test", iDefault: "status",
                           iChoices: new[] { "status", "install", "uninstall", "ps", "stop", "test" }),
        new SCP_CmdArgSpec("model", "install／uninstall／stop／test：ollama tag（例 qwen3:4b）"),
        new SCP_CmdArgSpec("confirm", "install／uninstall：=1 才真的動手（不給＝只印計畫）"),
        new SCP_CmdArgSpec("format", "status／ps／test：text（預設）｜json（後台頁讀這個）", iDefault: "text", iChoices: new[] { "text", "json" }),
        new SCP_CmdArgSpec("vram_budget", "status：手動指定顯存門檻（GB）；不給或 <=0 ＝ 自動偵測（nvidia-smi）"),
        new SCP_CmdArgSpec("vram_basis", "status：自動偵測時拿 free（可用量，預設）還是 total（總量）當門檻", iDefault: "free", iChoices: new[] { "free", "total" }),
        new SCP_CmdArgSpec("prompt", "test：要問的一句（預設一句吧檯招呼）"),
        new SCP_CmdArgSpec("system", "test：system prompt（例如酒保人設）"),
        new SCP_CmdArgSpec("think", "test：=1 ⇒ 把思考段一起要回來（診斷 thinking 模型用）", iDefault: "0"),
        new SCP_CmdArgSpec("num_predict", "test：生成上限（token，預設 120）", iDefault: "120"),
        new SCP_CmdArgSpec("keep_alive", "test：用完幾秒後把模型從顯存卸載（-1＝用 ollama 預設 5 分鐘）", iDefault: "-1"),
        new SCP_CmdArgSpec("timeout", "test：等待上限（秒，預設 60）—— 逾時不代表它死了，用 op=ps 看、op=stop 卸載", iDefault: "60"),
        new SCP_CmdArgSpec("data_root", "test：試跑紀錄寫到 <資料根>/LLMAdmin/test_log.jsonl（沒給 ⇒ 用設定檔那一格）"),
    };

    public const string DefaultPrompt = "用繁體中文說一句吧檯招呼，20 字以內。";
    public const string TestLogRelative = "LLMAdmin/test_log.jsonl";

    static readonly JsonSerializerOptions s_Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aOp = iArgs.Get("op");
        if (aOp.Length == 0) aOp = "status";
        bool aJson = iArgs.Get("format") == "json";
        string aModel = iArgs.Get("model").Trim();
        if (aOp is "install" or "uninstall" or "stop" or "test" && aModel.Length == 0)
            return SCP_CmdResult.Fail(2, $"✗ op={aOp} 要給 --arg model=<ollama tag>");

        string? aExe = LlmOllama.FindExe(out bool aOnPath);
        if (aOp == "status") return Status(aExe, aOnPath, iArgs, aJson);
        if (aExe == null)
            return SCP_CmdResult.Fail(4, "✗ 找不到 ollama ⇒ 量不到（⛔ 不是「沒有模型」）", "  安裝：" + LlmOllama.DownloadUrl + "　裝完重開 Senate 讓 PATH 生效");
        return aOp switch
        {
            "install" => Install(aExe, aModel, iArgs.Get("confirm") == "1"),
            "uninstall" => Uninstall(aExe, aModel, iArgs.Get("confirm") == "1"),
            "ps" => Ps(aExe, aJson),
            "stop" => Stop(aExe, aModel),
            "test" => Test(aModel, iArgs, aJson),
            _ => SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp),
        };
    }

    // ── status：本體／服務／目錄×已安裝／顯存裡的／顯存門檻，一次量完 ─────────────

    static SCP_CmdResult Status(string? iExe, bool iOnPath, SCP_CmdArgs iArgs, bool iJson)
    {
        string aManualRaw = iArgs.Get("vram_budget").Trim();
        double aManual = 0;
        // 填了但讀不懂 ⇒ 擋下來；⛔ 不默默退回自動偵測（使用者會以為自己填的數字生效了）。
        if (aManualRaw.Length > 0 && !double.TryParse(aManualRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out aManual))
            return SCP_CmdResult.Fail(2, $"✗ 手動顯存門檻讀不懂：'{aManualRaw}'（要一個數字，小數點用 .，例如 6.5；空白＝自動偵測）");
        LlmVram v = LlmOllama.ResolveVram(aManual, iArgs.Get("vram_basis"));
        string aVersion = "", aListErr = "";
        List<LlmModelRow>? aInstalled = null, aLoaded = null;      // null ＝ 量不到（⛔ 不是 0 個）
        if (iExe != null)
        {
            (int vc, string vo, string ve) = LlmOllama.Run(iExe, new[] { "--version" }, LlmOllama.DefaultTimeoutMs);
            aVersion = vc == 0 ? vo.Trim() : "";
            // 服務活著 ≠ 執行檔存在：`ollama list` 會去打本機服務，打不到就是沒跑。
            (int lc, string lo, string le) = LlmOllama.Run(iExe, new[] { "list" }, LlmOllama.DefaultTimeoutMs);
            if (lc == 0) aInstalled = LlmOllama.ToRows(LlmOllama.ParseTable(lo));
            else aListErr = (le + lo).Trim();
            if (aInstalled != null)
            {
                (int pc, string po, _) = LlmOllama.Run(iExe, new[] { "ps" }, LlmOllama.DefaultTimeoutMs);
                if (pc == 0) aLoaded = LlmOllama.ToRows(LlmOllama.ParseTable(po));
            }
            if (aVersion.Length == 0 && ve.Trim().Length > 0) aVersion = ve.Trim();
        }
        return RenderStatus(new LlmStatusInput(iExe, iOnPath, aVersion, aInstalled, aLoaded, v, aListErr), iJson);
    }

    /// <summary>status 量到的原料。Installed／Loaded 為 null ＝ 量不到（⛔ 不是 0 個）。</summary>
    public sealed record LlmStatusInput(string? Exe, bool OnPath, string Version, List<LlmModelRow>? Installed, List<LlmModelRow>? Loaded, LlmVram Vram, string ListError);

    /// <summary>把量到的原料組成輸出（文字或 json）—— 跟量測分開，selftest 才能不靠 ollama 驗「量不到 ≠ 0 個」這條與頁面的約定。</summary>
    public static SCP_CmdResult RenderStatus(LlmStatusInput s, bool iJson)
    {
        string? iExe = s.Exe; bool iOnPath = s.OnPath; string aVersion = s.Version, aListErr = s.ListError;
        List<LlmModelRow>? aInstalled = s.Installed, aLoaded = s.Loaded;
        LlmVram v = s.Vram;
        bool aServing = aInstalled != null;
        string aHint = iExe == null ? $"找不到 ollama —— 到 {LlmOllama.DownloadUrl} 安裝，裝完重開 Senate（PATH 才會更新）。"
                     : !aServing && !iOnPath ? "ollama 找得到但不在本行程的 PATH 上（剛裝完？），而服務也打不到 —— 重開 Senate 或確認背景服務在跑。"
                     : !aServing ? "ollama 有裝但服務打不到 —— 開一個終端機跑 `ollama serve`（或確認背景服務在跑）。"
                     : "";

        var aHave = aInstalled?.Select(m => m.Id).ToList() ?? new List<string>();
        var aCatalog = LlmOllama.Catalog.Select(c =>
        {
            (bool inst, bool exact) = aServing ? LlmOllama.Match(c.Id, aHave) : (false, false);
            return new { c, inst, exact, fits = c.VramGb <= v.BudgetGb };
        }).ToList();
        // 目錄外 ＝ 沒有精確對上任何一顆目錄項目的（變體也列 —— 它不是目錄挑的那一顆；`phi4-mini` 與 `phi4-mini:latest` 是同一顆）
        var aExtra = aInstalled?.Where(m => !LlmOllama.Catalog.Any(c => LlmOllama.Match(c.Id, new[] { m.Id }).Exact)).ToList() ?? new List<LlmModelRow>();

        var r = new SCP_CmdResult();
        r.AddValue("ollama_found", iExe != null ? "1" : "0");
        r.AddValue("service_reachable", aServing ? "1" : "0");
        // 量不到時刻意給空字串：「0」是讀數，空是沒有讀數。
        r.AddValue("installed_count", aInstalled?.Count.ToString(CultureInfo.InvariantCulture) ?? "");
        r.AddValue("vram_budget_gb", v.BudgetGb.ToString("0.##", CultureInfo.InvariantCulture));
        r.AddValue("vram_budget_source", v.Source);
        if (iJson)
        {
            r.Lines.Add(JsonSerializer.Serialize(new
            {
                ok = true,
                ollama_found = iExe != null, ollama_path = iExe ?? "", on_path = iOnPath, version = aVersion,
                service_reachable = aServing, installed_known = aInstalled != null, loaded_known = aLoaded != null,
                installed = aInstalled?.Select(m => new { id = m.Id, size = m.Size }), loaded = aLoaded?.Select(m => new { id = m.Id, size = m.Size, processor = m.Processor, context_length = m.ContextLength }),
                catalog = aCatalog.Select(x => new
                {
                    id = x.c.Id, @params = x.c.Params, size_gb = x.c.SizeGb, vram_gb = x.c.VramGb, zh = x.c.Zh, family = x.c.Family,
                    recommend = x.c.Recommend, note = x.c.Note, installed = x.inst, exact = x.exact, fits_budget = x.fits,
                }),
                not_in_catalog = aExtra.Select(m => new { id = m.Id, size = m.Size }),
                vram = new { gpu_ok = v.GpuOk, gpu_name = v.GpuName, total_gb = v.TotalGb, free_gb = v.FreeGb, used_gb = v.UsedGb, error = v.Error, budget_gb = v.BudgetGb, source = v.Source, basis = v.Basis },
                error = aListErr, hint = aHint,
                download_url = LlmOllama.DownloadUrl, install_command = LlmOllama.InstallCommand,
            }, s_Json));
            return r;
        }

        r.Lines.Add("# 本地 LLM（ollama）");
        r.Lines.Add("· ollama：" + (iExe == null ? "✗ 找不到" : $"✓ {aVersion}　{iExe}{(iOnPath ? "" : "（不在 PATH 上）")}"));
        r.Lines.Add("· 服務：" + (iExe == null ? "—" : aServing ? "✓ 可連線" : "✗ 打不到"));
        r.Lines.Add("· 已安裝：" + (aInstalled == null ? "量不到（⛔ 不是 0 個）" : $"{aInstalled.Count} 個"));
        foreach (LlmModelRow m in aInstalled ?? new()) r.Lines.Add($"    · {m.Id}　{m.Size}");
        r.Lines.Add("· 載入顯存中：" + (aLoaded == null ? "量不到" : $"{aLoaded.Count} 個"));
        foreach (LlmModelRow m in aLoaded ?? new()) r.Lines.Add($"    · {m.Id}　{m.Size}　{m.Processor}　context {m.ContextLength?.ToString(CultureInfo.InvariantCulture) ?? "未知"}");
        r.Lines.Add($"· 顯存門檻：{v.BudgetGb:0.##} GB（{LlmOllama.VramSourceText(v.Source)}）"
                    + (v.GpuOk ? $"　{v.GpuName} total {v.TotalGb} / used {v.UsedGb} / free {v.FreeGb} GB" : ""));
        if (v.Error.Length > 0) r.Lines.Add("  ⚠ 顯存偵測：" + v.Error);
        if (aHint.Length > 0) r.Lines.Add("  ⚠ " + aHint);
        if (aListErr.Length > 0) r.Lines.Add("  ⚠ " + aListErr);
        r.Lines.Add("");
        r.Lines.Add("## 目錄（★＝推薦；✓＝已裝，~＝裝的是變體）");
        foreach (var x in aCatalog)
            r.Lines.Add($"- {(!aServing ? "?" : x.exact ? "✓" : x.inst ? "~" : "·")}{(x.c.Recommend ? "★" : " ")} {x.c.Id}　{x.c.Params}　下載 {x.c.SizeGb} GB／顯存約 {x.c.VramGb} GB　中文 {x.c.Zh}/5"
                        + (x.fits ? "" : $"　⚠ 超過 {v.BudgetGb:0.##} GB 門檻"));
        if (aExtra.Count > 0)
        {
            r.Lines.Add("## 目錄外（自己 pull 的）");
            foreach (LlmModelRow m in aExtra) r.Lines.Add($"- {m.Id}　{m.Size}");
        }
        return r;
    }

    // ── install／uninstall：不帶 confirm 只印計畫 ──────────────────────────

    static SCP_CmdResult Install(string iExe, string iModel, bool iConfirm)
    {
        LlmCatalogEntry? c = LlmOllama.Catalog.FirstOrDefault(x => x.Id == iModel);
        if (!iConfirm)
            return SCP_CmdResult.Success(
                $"## 計畫：下載 {iModel}（ollama pull）",
                c != null ? $"· 下載量約 {c.SizeGb} GB、顯存約 {c.VramGb} GB" : "· 不在目錄裡 ⇒ 大小量不到，ollama 會自己找這個 tag",
                "· 放在 ollama 自己的模型目錄（不是安裝系統的 ModelsRoot）",
                "⛔ 這次沒有動手。同意了再跑：" + SCP_CmdRegistry.Invoke($"llm --arg op=install --arg model={iModel} --arg confirm=1"));
        var sw = Stopwatch.StartNew();
        (int code, string o, string e) = LlmOllama.Run(iExe, new[] { "pull", iModel }, LlmOllama.PullTimeoutMs);
        if (code == LlmOllama.CancelledExitCode) return SCP_CmdResult.Fail(5, $"✗ 下載 {iModel} 已中斷（ollama 會保留已下載的部分，下次下載從那裡接）");
        if (code != 0) return SCP_CmdResult.Fail(5, $"✗ 下載 {iModel} 沒成功（exit {code}）：" + Tail((e + "\n" + o).Trim()));
        return SCP_CmdResult.Success($"✓ 已下載 {iModel}（{sw.Elapsed.TotalSeconds:0.0} 秒）").AddValue("model", iModel);
    }

    static SCP_CmdResult Uninstall(string iExe, string iModel, bool iConfirm)
    {
        if (!iConfirm)
            return SCP_CmdResult.Success(
                $"## 計畫：移除 {iModel}（ollama rm）",
                "· 會刪掉 ollama 模型目錄裡這顆的權重；要用時得重新下載",
                "⚠ 酒保若設定成用這顆，移除後它會退回罐頭句",
                "⛔ 這次沒有動手。確定了再跑：" + SCP_CmdRegistry.Invoke($"llm --arg op=uninstall --arg model={iModel} --arg confirm=1"));
        (int code, string o, string e) = LlmOllama.Run(iExe, new[] { "rm", iModel }, LlmOllama.DefaultTimeoutMs);
        if (code != 0) return SCP_CmdResult.Fail(5, $"✗ 移除 {iModel} 沒成功（exit {code}）：" + Tail((e + "\n" + o).Trim()));
        return SCP_CmdResult.Success($"✓ 已移除 {iModel}").AddValue("model", iModel);
    }

    // ── ps／stop：顯存裡有什麼（跟「磁碟上有什麼」是兩件事）──────────────────

    static SCP_CmdResult Ps(string iExe, bool iJson)
    {
        (int code, string o, string e) = LlmOllama.Run(iExe, new[] { "ps" }, LlmOllama.DefaultTimeoutMs);
        if (code != 0) return SCP_CmdResult.Fail(4, "✗ 量不到顯存裡的模型（服務打不到？）：" + (e + o).Trim());
        List<LlmModelRow> aRows = LlmOllama.ToRows(LlmOllama.ParseTable(o));
        var r = new SCP_CmdResult().AddValue("loaded_count", aRows.Count.ToString(CultureInfo.InvariantCulture));
        if (iJson) { r.Lines.Add(JsonSerializer.Serialize(new { ok = true, loaded = aRows.Select(m => new { id = m.Id, size = m.Size, processor = m.Processor, context_length = m.ContextLength }) }, s_Json)); return r; }
        r.Lines.Add($"## 載入顯存中（{aRows.Count}）");
        foreach (LlmModelRow m in aRows) r.Lines.Add($"- {m.Id}　{m.Size}　{m.Processor}　context {m.ContextLength?.ToString(CultureInfo.InvariantCulture) ?? "未知"}");
        return r;
    }

    // 殺掉發問的那一方不會讓模型離開顯存（它是 ollama 服務持有的）⇒ 卡住時要停的是模型。
    static SCP_CmdResult Stop(string iExe, string iModel)
    {
        (int code, string o, string e) = LlmOllama.Run(iExe, new[] { "stop", iModel }, LlmOllama.DefaultTimeoutMs);
        if (code != 0) return SCP_CmdResult.Fail(5, $"✗ 卸載 {iModel} 沒成功（exit {code}；舊版 ollama 沒有 stop）：" + (e + o).Trim());
        return SCP_CmdResult.Success($"✓ 已把 {iModel} 從顯存卸載");
    }

    // ── test：走 HTTP API（逾時、生成上限、思考段都拿得回來）─────────────────

    /// <summary>試跑結果。欄位名就是紀錄檔 jsonl 的欄位名。</summary>
    public sealed record TestResult(bool ok, bool truncated, string model, string prompt, double seconds, int eval_count, double tokens_per_sec,
                                    string output, string thinking, string note, string error);

    /// <summary>撞到生成上限就是被切斷 —— 不管切在思考段還是回答段（thinking 模型常把推理寫進回答欄）。</summary>
    public static (bool Truncated, string Note) JudgeTruncation(int iEvalCount, int iNumPredict, string iContent, string iDoneReason = "")
    {
        // ollama 自己說的停止原因優先（length ＝ 撞到上限）；舊版沒有這一欄才退回去數 token。
        bool t = iDoneReason == "length" || (iNumPredict > 0 && iEvalCount >= iNumPredict);
        if (!t) return (false, "");
        return (true, iContent.Length == 0
            ? $"生成上限 {iNumPredict} token 用完，思考還沒結束 ⇒ 回答是空的（被截斷，不是失敗）。提高上限，或換一顆不 thinking 的小模型。"
            : $"生成上限 {iNumPredict} token 用完 ⇒ 這段是被切斷的半句。thinking 模型常把推理寫進回答欄 ⇒ 看起來像回答，其實是它在自言自語。");
    }

    /// <summary>
    /// 對 ollama 問一句（HTTP API）。試跑與酒保回覆共用這一份：逾時、生成上限、思考段、截斷判定都在這裡。
    /// <paramref name="iCancel"/> 沒給 ＝ 用行程內的「中斷」取消源（後台頁的中斷鈕）。
    /// </summary>
    public static TestResult Chat(string iModel, string iPrompt, string iSystem, bool iThink, int iNumPredict, int iKeepAlive, int iTimeoutSec,
                                  CancellationToken? iCancel = null)
    {
        var aMessages = new List<object>();
        if (iSystem.Length > 0) aMessages.Add(new { role = "system", content = iSystem });
        aMessages.Add(new { role = "user", content = iPrompt });
        var aBody = new Dictionary<string, object> { ["model"] = iModel, ["stream"] = false, ["think"] = iThink, ["options"] = new { num_predict = iNumPredict }, ["messages"] = aMessages };
        // 上下文由 Ollama 管理（全域預設／模型 num_ctx）；不送 num_ctx，也不以生成上限冒充容量。
        if (iKeepAlive >= 0) aBody["keep_alive"] = iKeepAlive.ToString(CultureInfo.InvariantCulture) + "s";

        var sw = Stopwatch.StartNew();
        CancellationToken aCancel = iCancel ?? LlmOllama.CurrentToken;
        TestResult t;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(iTimeoutSec) };
            using var aContent = new StringContent(JsonSerializer.Serialize(aBody, s_Json), Encoding.UTF8, "application/json");
            using HttpResponseMessage resp = http.PostAsync(LlmOllama.ApiBase + "/api/chat", aContent, aCancel).GetAwaiter().GetResult();
            string aText = resp.Content.ReadAsStringAsync(aCancel).GetAwaiter().GetResult();
            double sec = Math.Round(sw.Elapsed.TotalSeconds, 1);
            if (!resp.IsSuccessStatusCode)
                t = new TestResult(false, false, iModel, iPrompt, sec, 0, 0, "", "", "", $"HTTP {(int)resp.StatusCode}：{Tail(aText)}");
            else
            {
                using JsonDocument d = JsonDocument.Parse(aText);
                JsonElement root = d.RootElement;
                JsonElement m = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out JsonElement mm) ? mm : default;
                string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString()!.Trim() : "";
                long Num(string k) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.Number && x.TryGetInt64(out long v) ? v : 0;
                bool aDone = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("done", out JsonElement dn) && dn.ValueKind == JsonValueKind.True;
                string aOut = Str(m, "content"), aThinkText = Str(m, "thinking");
                int n = (int)Num("eval_count");
                double tps = Math.Round(n / Math.Max(Num("eval_duration") / 1e9, 1e-6), 1);
                (bool trunc, string note) = JudgeTruncation(n, iNumPredict, aOut, Str(root, "done_reason"));
                // 回應沒說完成、或沒有 message ⇒ 不是一個回答；回答是空的也不算成功（空字串發進酒館比退罐頭更糟）。
                string aErr = m.ValueKind != JsonValueKind.Object || !aDone ? "ollama 的回應不完整（沒有 done=true 或沒有 message）：" + Tail(aText)
                            : !trunc && aOut.Length == 0 ? "回答是空的" : "";
                t = new TestResult(!trunc && aErr.Length == 0, trunc, iModel, iPrompt, sec, n, tps, aOut, aThinkText, note, aErr);
            }
        }
        catch (OperationCanceledException) when (aCancel.IsCancellationRequested)
        {
            t = new TestResult(false, false, iModel, iPrompt, Math.Round(sw.Elapsed.TotalSeconds, 1), 0, 0, "", "", "",
                "已中斷（連線已關閉，ollama 會停止這次生成；模型還在顯存裡，要卸載按「從顯存卸載」）");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            t = new TestResult(false, false, iModel, iPrompt, Math.Round(sw.Elapsed.TotalSeconds, 1), 0, 0, "", "", "",
                $"連線失敗／逾時（{iTimeoutSec} 秒）：{e.Message} ⇒ 逾時不代表它死了，thinking 模型可能還在想；用 op=ps 看它載在哪、op=stop 把它從顯存放掉。");
        }
        return t;
    }

    static SCP_CmdResult Test(string iModel, SCP_CmdArgs iArgs, bool iJson)
    {
        string aPrompt = iArgs.Get("prompt"); if (aPrompt.Trim().Length == 0) aPrompt = DefaultPrompt;
        string aSystem = iArgs.Get("system");
        int aNum = Int(iArgs.Get("num_predict"), 120), aKeep = Int(iArgs.Get("keep_alive"), -1), aTimeout = Math.Max(1, Int(iArgs.Get("timeout"), 60));
        bool aThink = iArgs.Get("think") == "1";

        TestResult t = Chat(iModel, aPrompt, aSystem, aThink, aNum, aKeep, aTimeout);

        var r = new SCP_CmdResult { ExitCode = t.ok ? 0 : 5 };
        string? aLogErr = AppendTestLog(iArgs.Get("data_root"), t, aSystem, out string aLogPath);
        if (aLogPath.Length > 0) r.AddValue("test_log", aLogPath);
        r.AddValue("truncated", t.truncated ? "1" : "0");
        if (iJson)
        {
            r.Lines.Add(TestJson(t, aLogErr ?? ""));
            return r;
        }
        r.Lines.Add($"# 試跑 {t.model}");
        r.Lines.Add($"- 結果：{(t.ok ? "✓ 成功" : "✗ 失敗")}　耗時 {t.seconds} 秒　{t.tokens_per_sec} tok/s　{t.eval_count} token");
        if (t.thinking.Length > 0) { r.Lines.Add(""); r.Lines.Add("## 思考過程"); r.Lines.Add(t.thinking); }
        if (t.output.Length > 0) { r.Lines.Add(""); r.Lines.Add("## 回答"); r.Lines.Add(t.output); }
        if (t.note.Length > 0) { r.Lines.Add(""); r.Lines.Add("⚠ " + t.note); }
        if (t.error.Length > 0) { r.Lines.Add(""); r.Lines.Add("⚠ " + t.error); }
        if (aLogErr != null) r.Lines.Add("⚠ 試跑紀錄沒寫進去（結果仍在上面）：" + aLogErr);
        return r;
    }

    /// <summary>試跑結果的 json（後台頁讀這個；selftest 拿它對拍頁面的解析）。</summary>
    public static string TestJson(TestResult t, string iLogError)
        => JsonSerializer.Serialize(new { t.ok, t.truncated, t.model, t.prompt, t.seconds, t.eval_count, t.tokens_per_sec, t.output, t.thinking, t.note, t.error, log_error = iLogError }, s_Json);

    /// <summary>試跑落一行到 jsonl（append-only）。失敗只回原因、不擋 —— 試跑本身比紀錄重要，但要出聲。</summary>
    public static string? AppendTestLog(string iDataRoot, TestResult t, string iSystem, out string oPath)
    {
        oPath = "";
        if (iDataRoot.Length == 0 || !Directory.Exists(iDataRoot)) return $"資料根不存在（{iDataRoot}）";
        try
        {
            oPath = Path.GetFullPath(Path.Combine(iDataRoot, TestLogRelative));
            Directory.CreateDirectory(Path.GetDirectoryName(oPath)!);
            string aLine = JsonSerializer.Serialize(new
            {
                t.ok, t.model, t.prompt, seconds = t.seconds, t.eval_count, tokens_per_sec = t.tokens_per_sec, t.output, t.thinking, t.note, t.error,
                ts = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture), system = iSystem,
            }, s_Json);
            File.AppendAllText(oPath, aLine + "\n", new UTF8Encoding(false));
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return e.Message; }
    }

    /// <summary>讀回最後 N 筆試跑（新→舊）。檔不存在回空清單；讀不了回 null（「沒有紀錄」與「讀不到」不同形）。</summary>
    public static List<string>? TailTestLog(string iDataRoot, int iCount)
    {
        string aPath = Path.Combine(iDataRoot, TestLogRelative.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(aPath)) return new List<string>();
        try { return File.ReadAllLines(aPath).Where(l => l.Trim().Length > 0).Reverse().Take(iCount).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static int Int(string s, int iDefault) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : iDefault;

    static string Tail(string s) => s.Length <= 1500 ? s : "…" + s[^1500..];
}
