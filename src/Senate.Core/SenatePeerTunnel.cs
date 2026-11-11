// 區塊職責：**Cloudflare 臨時通道**（TASK-0011）—— 主機在 NAT 後面、別人連不進來時的退路。
// 物理意義：起一個 `cloudflared tunnel --url http://127.0.0.1:<埠>` 子行程，從它的輸出讀出 `https://….trycloudflare.com`；
//           另一端用 `wss://….trycloudflare.com/peer` 連進來。網址每次啟動都不一樣 ⇒ 由主機公告帶給另一端。
//           ⚠ 流量經過 Cloudflare；內容在連線那一層已經加密過，它只看得到密文。
// 數值影響：一個子行程。⛔ 本檔不下載、不安裝 cloudflared：找不到就回報「沒裝」，由人決定要不要裝。
// ⚠ Server 被硬砍（不是正常收工）時子行程會留下來 —— 下一次啟動前看一眼工作管理員。
#nullable enable
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Senate.Core;

public sealed class SenatePeerTunnel : IDisposable
{
    static readonly Regex s_Url = new(@"https://[a-z0-9\-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);

    Process? m_Proc;
    int m_Port;
    volatile string m_Url = "";
    volatile string m_LastLine = "";

    public string Url => m_Url;
    public string LastLine => m_LastLine;
    public bool Running => m_Proc is { HasExited: false };
    public DateTime StartedUtc { get; private set; }

    /// <summary>從 cloudflared 的一行輸出裡找通道網址；沒有 ⇒ 空字串。</summary>
    public static string ParseUrl(string? iLine)
    {
        Match m = s_Url.Match(iLine ?? "");
        return m.Success ? m.Value : "";
    }

    /// <summary>`https://x.trycloudflare.com` ⇒ `wss://x.trycloudflare.com/peer`。</summary>
    public static string ToWs(string iHttpsUrl)
        => iHttpsUrl.Length == 0 ? "" : "wss://" + iHttpsUrl.Substring("https://".Length).TrimEnd('/') + SenatePeerLink.WsPath;

    /// <summary>找 cloudflared：設定給的路徑 ＞ PATH。找不到 ⇒ 空字串。</summary>
    public static string Find(string iConfigured)
    {
        if (iConfigured.Length > 0) return File.Exists(iConfigured) ? iConfigured : "";
        string aExe = OperatingSystem.IsWindows() ? "cloudflared.exe" : "cloudflared";
        foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string p = Path.Combine(d.Trim(), aExe);
                if (File.Exists(p)) return p;
            }
            catch (Exception) { }
        }
        return "";
    }

    /// <summary>起通道（已經為同一個埠在跑 ⇒ 不動）。回錯誤訊息；成功 ⇒ null（網址要等幾秒才會出現在 <see cref="Url"/>）。</summary>
    public string? Start(string iExe, int iPort)
    {
        if (Running && m_Port == iPort) return null;
        Stop();
        try
        {
            var psi = new ProcessStartInfo(iExe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (string a in new[] { "tunnel", "--no-autoupdate", "--url", "http://127.0.0.1:" + iPort }) psi.ArgumentList.Add(a);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler aOn = (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                m_LastLine = e.Data;
                string u = ParseUrl(e.Data);
                if (u.Length > 0) m_Url = u;
            };
            p.OutputDataReceived += aOn;
            p.ErrorDataReceived += aOn;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            m_Proc = p;
            m_Port = iPort;
            m_Url = "";
            StartedUtc = DateTime.UtcNow;
            return null;
        }
        catch (Exception e) { return e.GetType().Name + "：" + e.Message; }
    }

    public void Stop()
    {
        Process? p = m_Proc;
        m_Proc = null;
        m_Url = "";
        if (p == null) return;
        try { if (!p.HasExited) p.Kill(true); } catch (Exception) { }
        try { p.Dispose(); } catch (Exception) { }
    }

    public void Dispose() => Stop();
}
