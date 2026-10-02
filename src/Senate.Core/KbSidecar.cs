// 區塊職責：知識庫**常駐嵌入程序**的管理與呼叫（TASK-0378）—— 拉起、健康檢查、嵌入、停止。
// 物理意義：舊版每次呼叫重新載入 BGE-M3（2026-10-02 量：每題 19–57 秒，載模型＋解析數百 MB 的 JSON 索引）。
//           常駐之後模型只載一次（冷啟動實測 74 秒），之後一句 ~90 ms。
//           程序本體是內嵌的 `kb_sidecar.py`，執行時落到 `SenateData/runtime/kb/`；用 Server 那條「脫離行程樹」的路拉起
//           ⇒ CLI 結束它還在，閒置 30 分自己退（不然它會一直佔著 GPU 記憶體，而沒有人記得它還在）。
// 數值影響：會起一個 Python 程序；runtime/kb/ 底下寫 sidecar.py、token、info、log。
// ⚠ 「info 檔在」不等於「它活著」：被硬砍的程序來不及刪它 ⇒ 一律再打一次 /health，對不上就當作沒在跑。
// ⚠ token：本機任何程序都打得到 localhost —— 沒有 token 的話，別的程式可以拿它當免費的 GPU。
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Senate.Core;

public sealed record KbEmbedding(float[][] Dense, List<Dictionary<int, float>> Sparse, int Dim);

public sealed class KbSidecar
{
    public const string ScriptResource = "kb_sidecar.py";
    public const int IdleSeconds = 1800;
    /// <summary>冷啟動實測 74 秒（2026-10-02，模型檔第一次讀進記憶體）；給足餘裕。</summary>
    public const int StartTimeoutSec = 300;

    /// <summary>這個常駐程序需要的安裝項目（缺了走 TASK-0375 的「缺相依」流程）。</summary>
    public static readonly string[] RequiredInstallIds = { "py-flagembedding", "model-bge-m3" };

    readonly string m_Dir;
    static readonly HttpClient s_Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public KbSidecar(string iRepoRoot) { m_Dir = Path.Combine(SenatePaths.RuntimeDir(iRepoRoot), "kb"); }

    string InfoPath => Path.Combine(m_Dir, "sidecar.json");
    string TokenPath => Path.Combine(m_Dir, "sidecar.token");
    public string LogPath => Path.Combine(m_Dir, "sidecar.log");

    public sealed record Health(int Pid, int Port, string Device, long LoadedMs, long Served);

    /// <summary>它現在活著嗎（info 檔＋/health 都對上）。回 null ＝ 沒在跑。</summary>
    public Health? Probe()
    {
        try
        {
            if (!File.Exists(InfoPath) || !File.Exists(TokenPath)) return null;
            using JsonDocument info = JsonDocument.Parse(File.ReadAllText(InfoPath));
            int aPort = info.RootElement.GetProperty("port").GetInt32();
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{aPort}/health");
            req.Headers.Add("X-Kb-Token", File.ReadAllText(TokenPath).Trim());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using HttpResponseMessage r = s_Http.Send(req, cts.Token);
            if (!r.IsSuccessStatusCode) return null;
            using JsonDocument h = JsonDocument.Parse(r.Content.ReadAsStringAsync().Result);
            JsonElement e = h.RootElement;
            return new Health(e.GetProperty("pid").GetInt32(), aPort, e.GetProperty("device").GetString() ?? "?",
                              e.GetProperty("loaded_ms").GetInt64(), e.GetProperty("served").GetInt64());
        }
        catch { return null; }
    }

    /// <summary>
    /// 確保它在跑（沒在跑就拉起並等到 /health 回應）。回 null ＝ 好了；否則回原因。
    /// <para>呼叫前要先過相依檢查（<see cref="RequiredInstallIds"/>）—— 缺套件的話它會在 import 那一行炸，而那只會寫進 log。</para>
    /// </summary>
    public string? EnsureRunning(InstallEnv iEnv, Action<string> iLog)
    {
        if (Probe() != null) return null;
        if (iEnv.PythonExe == null) return "沒有 Python：" + iEnv.PythonError;
        Directory.CreateDirectory(m_Dir);
        string aScript = Path.Combine(m_Dir, ScriptResource);
        using (Stream? s = typeof(KbSidecar).Assembly.GetManifestResourceStream(ScriptResource))
        {
            if (s == null) return "內嵌的 kb_sidecar.py 不見了（這是 build 的問題，不是環境的問題）";
            using var f = File.Create(aScript);
            s.CopyTo(f);
        }
        string aToken = Guid.NewGuid().ToString("N");
        File.WriteAllText(TokenPath, aToken);
        try { File.Delete(InfoPath); } catch { }

        var aInfo = new ProcessStartInfo(iEnv.PythonExe) { WorkingDirectory = m_Dir };
        foreach (string a in new[] { aScript, "--port", "0", "--token", aToken, "--info", InfoPath,
                                     "--idle", IdleSeconds.ToString(), "--hf-home", iEnv.ModelsRoot, "--log", LogPath })
            aInfo.ArgumentList.Add(a);
        iLog($"拉起常駐嵌入程序（第一次要載入模型，冷啟動約 1 分多鐘）：{iEnv.PythonExe}");
        if (!ServerSpawn.TrySpawnDetached(aInfo, false, out int aPid, out string aWhy))
        {
            // 退路：直接 spawn（會是本程序的子孫；CLI 結束時可能被一起收掉 —— 說出來，不安靜降級）
            iLog("⚠ 沒能脫離行程樹（" + aWhy + "）⇒ 直接起，CLI 結束後它可能跟著結束");
            aInfo.UseShellExecute = false; aInfo.CreateNoWindow = true;
            try { aPid = Process.Start(aInfo)?.Id ?? 0; } catch (Exception e) { return "起不來：" + e.Message; }
        }

        var sw = Stopwatch.StartNew();
        long aLastNote = 0;
        while (sw.Elapsed.TotalSeconds < StartTimeoutSec)
        {
            Thread.Sleep(1000);
            if (Probe() != null) { iLog($"✓ 常駐嵌入程序起來了（pid {aPid}，{sw.Elapsed.TotalSeconds:0} 秒）"); return null; }
            if (!Alive(aPid)) return "常駐嵌入程序起來之後就結束了 —— 看 log：" + LogPath + "\n" + InstallProbe.Tail(ReadLog(), 600);
            if (sw.ElapsedMilliseconds - aLastNote > 15000) { aLastNote = sw.ElapsedMilliseconds; iLog($"  …載入模型中（{sw.Elapsed.TotalSeconds:0} 秒）"); }
        }
        return $"等了 {StartTimeoutSec} 秒還沒起來 —— 看 log：{LogPath}";
    }

    string ReadLog() { try { return File.ReadAllText(LogPath); } catch { return ""; } }

    static bool Alive(int iPid)
    {
        if (iPid <= 0) return true;   // 不知道 pid（退路起的）⇒ 交給逾時判
        try { using Process p = Process.GetProcessById(iPid); return !p.HasExited; } catch { return false; }
    }

    /// <summary>嵌入一批文字（dense 必回；sparse 依 <paramref name="iSparse"/>）。</summary>
    public KbEmbedding Embed(IReadOnlyList<string> iTexts, bool iSparse, int iMaxLength = 1024)
    {
        Health h = Probe() ?? throw new InvalidOperationException("常駐嵌入程序沒在跑");
        string aBody = JsonSerializer.Serialize(new { texts = iTexts, sparse = iSparse, max_length = iMaxLength, batch_size = 16 });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{h.Port}/embed")
        { Content = new StringContent(aBody, Encoding.UTF8, "application/json") };
        req.Headers.Add("X-Kb-Token", File.ReadAllText(TokenPath).Trim());
        using HttpResponseMessage r = s_Http.Send(req);
        string aJson = r.Content.ReadAsStringAsync().Result;
        using JsonDocument d = JsonDocument.Parse(aJson);
        JsonElement e = d.RootElement;
        if (!r.IsSuccessStatusCode || !e.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("嵌入失敗：" + (e.TryGetProperty("error", out var er) ? er.GetString() : aJson));
        int aDim = e.GetProperty("dim").GetInt32();
        byte[] aBytes = Convert.FromBase64String(e.GetProperty("dense_b64").GetString() ?? "");
        var aDense = new float[iTexts.Count][];
        for (int i = 0; i < iTexts.Count; i++)
        {
            aDense[i] = new float[aDim];
            Buffer.BlockCopy(aBytes, i * aDim * 4, aDense[i], 0, aDim * 4);
        }
        var aSparse = new List<Dictionary<int, float>>();
        if (iSparse)
            foreach (JsonElement m in e.GetProperty("sparse").EnumerateArray())
            {
                var dict = new Dictionary<int, float>();
                foreach (JsonProperty p in m.EnumerateObject()) dict[int.Parse(p.Name)] = (float)p.Value.GetDouble();
                aSparse.Add(dict);
            }
        return new KbEmbedding(aDense, aSparse, aDim);
    }

    /// <summary>請它關掉。回 true ＝ 它本來在跑而且回應了。</summary>
    public bool Stop()
    {
        Health? h = Probe();
        if (h == null) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{h.Port}/shutdown") { Content = new StringContent("{}") };
            req.Headers.Add("X-Kb-Token", File.ReadAllText(TokenPath).Trim());
            using HttpResponseMessage r = s_Http.Send(req);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
}
