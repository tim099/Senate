// 區塊職責：呼叫**真的 git.exe**（不是 libgit2 綁定）。
// 物理意義：判準是「跟人在終端機打的那顆 git 逐字同行為」——
//           .gitignore 邊界、submodule、CRLF、hooks、credential helper 的細節，
//           換一套實作就會有差異，而那種差異**不報錯**，只會在某天生出一筆內容不對的 commit。
// 數值影響：每次呼叫都釘兩個東西：
//           · `-c core.quotepath=false` —— 否則非 ASCII 路徑會印成八進位轉義，
//             拿去比對就會把每個中文檔名都判成「不一樣」（LY 專案 2026-08-22 實撞）
//           · `GIT_TERMINAL_PROMPT=0` —— 非互動環境不該彈認證視窗，彈了就是卡到 timeout 才有人發現
using System.Diagnostics;
using System.Text;

namespace Senate.Core;

public readonly record struct GitResult(int Exit, string StdOut, string StdErr)
{
    public bool Ok => Exit == 0;
    public string Message => string.IsNullOrWhiteSpace(StdErr) ? StdOut.Trim() : StdErr.Trim();
}

public static class GitCli
{
    public const int DefaultTimeoutMs = 120_000;

    /// <summary>`--pathspec-from-file` 需要的最低 git 版本（2.25，2020 年）。</summary>
    public static readonly Version MinVersionForPathspecFromFile = new(2, 25);

    public static GitResult Run(string iWorkDir, string iArgs, int iTimeoutMs = DefaultTimeoutMs)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = iWorkDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.quotepath=false");
        foreach (string a in SplitArgs(iArgs)) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder();
        var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data != null) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(iTimeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* 已經死了就算了 */ }
            return new GitResult(-1, so.ToString(), $"git 逾時（{iTimeoutMs} ms）：git {iArgs}");
        }
        p.WaitForExit();   // flush 非同步讀取的殘餘
        return new GitResult(p.ExitCode, so.ToString(), se.ToString());
    }

    /// <summary>git 版本；問不到回 null（**不要**回 0.0 —— 「問不到」與「很舊」是兩件事）。</summary>
    public static Version? Version()
    {
        var r = Run(Environment.CurrentDirectory, "--version", 15_000);
        if (!r.Ok) return null;
        // "git version 2.39.2.windows.1" → 取前兩段（第三段以後的形狀各平台不同）
        var parts = r.StdOut.Trim().Split(' ');
        string? ver = parts.Length >= 3 ? parts[2] : null;
        if (ver == null) return null;
        var seg = ver.Split('.');
        return seg.Length >= 2 && int.TryParse(seg[0], out int mj) && int.TryParse(seg[1], out int mn)
            ? new Version(mj, mn)
            : null;
    }

    public static bool IsRepo(string iDir)
        => Directory.Exists(iDir) && Run(iDir, "rev-parse --git-dir", 15_000).Ok;

    /// <summary>當前分支；detached 時回 "HEAD"（呼叫端要把它當一種**擋下的理由**，不是一個分支名）。</summary>
    public static string? Branch(string iDir)
    {
        var r = Run(iDir, "rev-parse --abbrev-ref HEAD", 15_000);
        return r.Ok ? r.StdOut.Trim() : null;
    }

    /// <summary>工作區有幾筆改動（含 untracked）。問不到回 null。</summary>
    public static int? DirtyCount(string iDir)
    {
        var r = Run(iDir, "status --porcelain=v1 --untracked-files=all");
        if (!r.Ok) return null;
        int n = 0;
        foreach (string line in r.StdOut.Split('\n')) if (line.Trim().Length > 0) n++;
        return n;
    }

    /// <summary>呼叫本工具**之前**就已經 staged 的檔案清單（空 ＝ index 乾淨）。</summary>
    public static List<string> StagedPaths(string iDir)
    {
        var list = new List<string>();
        var r = Run(iDir, "diff --cached --name-only");
        if (!r.Ok) return list;
        foreach (string line in r.StdOut.Split('\n'))
        {
            string s = line.Trim();
            if (s.Length > 0) list.Add(s);
        }
        return list;
    }

    /// <summary>
    /// 極簡引數切分：支援雙引號包住含空白的片段。
    /// ⚠ 刻意保持笨：**會帶使用者資料的參數（路徑清單／訊息）一律走檔案**
    /// （`-F <file>` / `--pathspec-from-file=<file>`），不從這裡拼字串。
    /// </summary>
    static IEnumerable<string> SplitArgs(string iArgs)
    {
        var cur = new StringBuilder();
        bool q = false;
        foreach (char c in iArgs)
        {
            if (c == '"') { q = !q; continue; }
            if (char.IsWhiteSpace(c) && !q)
            {
                if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); }
                continue;
            }
            cur.Append(c);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }
}
