// 區塊職責：知識庫的**目標清單**（TASK-0378）—— 每個 target 收哪些檔。
// 物理意義：定義檔是 `kb_targets.json`（位置走描述表 `KbTargetsFile`）。
//           前綴語意：無前綴＝Senate 專案根、`scp_core:`＝SCP_Core 根、`data:`＝資料根；`expand` 逐 persona 展開。
// 數值影響：純讀（列目錄）。
// ⚠ glob 自己寫（`**`／`*`／`?`／`[!_]`／`[abc]`）：.NET 內建的不支援 `[!_]`，而清單靠它排除 `_root_index.md` 那類機械產物。
// ⚠ 去重用正規化後的真實路徑（不分大小寫）：Windows 上 `Lessons/**` 與 `lessons/**` 回同一批檔、字串不同
//   ⇒ 只用字串去重的話每一塊被索引兩次（2026-10-02 量到 312 塊＝2×156）。
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Senate.Core;

/// <param name="HalfLifeDays">時間衰減的半衰期（天）；0 ＝ 不衰減。只給會過期的 target（碎片、工作記憶）設，文件類不設（TASK-0382）。</param>
public sealed record KbTarget(string Name, string Desc, string Kind, IReadOnlyList<string> Globs, bool ExcludeFromAll, double HalfLifeDays = 0);

/// <summary>一個 target 解析出來的來源檔（`Bases` 給「相對路徑」用：塊 id 不用裸檔名，同名檔才不會撞）。</summary>
public sealed record KbSources(KbTarget Target, List<string> Files, List<string> Bases);

/// <summary>
/// 知識庫的三個 glob 基準＋定義檔位置（TASK-0390：全部跟著 Senate＋Valhalla）。
/// 無前綴＝<see cref="RepoRoot"/>（Senate 專案根）、`scp_core:`＝<see cref="ScpCoreRoot"/>、`data:`＝<see cref="DataRoot"/>。
/// </summary>
public sealed class KbRoots
{
    public string RepoRoot { get; init; } = "";
    public string ScpCoreRoot { get; init; } = "";
    public string DataRoot { get; init; } = "";
    /// <summary>`kb_targets.json` 的完整路徑（描述表的 KbTargetsFile）。</summary>
    public string TargetsFile { get; init; } = "";
}

public static class KbTargets
{
    public const string FileName = "kb_targets.json";

    /// <summary>讀清單（含 expand 展開）。回 null ＝ 讀不了（原因在 oError；⛔ 不退回內建預設 —— 那會讓「設定壞了」長成「收的檔變少了」）。</summary>
    public static Dictionary<string, KbTarget>? Load(KbRoots iRoots, out string? oError)
    {
        oError = null;
        string aPath = iRoots.TargetsFile;
        if (!File.Exists(aPath)) { oError = "找不到目標清單：" + aPath; return null; }
        try
        {
            using JsonDocument aDoc = JsonDocument.Parse(File.ReadAllText(aPath));
            var d = new Dictionary<string, KbTarget>(StringComparer.Ordinal);
            if (!aDoc.RootElement.TryGetProperty("targets", out JsonElement aTargets) || aTargets.ValueKind != JsonValueKind.Object)
            { oError = "目標清單缺 targets 區塊：" + aPath; return null; }
            foreach (JsonProperty p in aTargets.EnumerateObject())
                foreach (string g in Arr(p.Value, "globs"))
                    if (UnknownPrefix(g) is string aBad)
                    { oError = $"target `{p.Name}` 的 glob `{g}` 用了不認得的前綴 `{aBad}:`（只認 無／scp_core:／data:）—— ⛔ 不猜，不然它安靜地匹配 0 檔：{aPath}"; return null; }
            foreach (JsonProperty p in aTargets.EnumerateObject())
                d[p.Name] = new KbTarget(p.Name, Str(p.Value, "desc"), Str(p.Value, "kind", "markdown"), Arr(p.Value, "globs"),
                                         p.Value.TryGetProperty("exclude_from_all", out var x) && x.ValueKind == JsonValueKind.True,
                                         Num(p.Value, "half_life_days"));
            if (aDoc.RootElement.TryGetProperty("expand", out JsonElement aExpand) && aExpand.ValueKind == JsonValueKind.Array)
                foreach (JsonElement spec in aExpand.EnumerateArray())
                    foreach (KbTarget t in Expand(spec, iRoots))
                        d.TryAdd(t.Name, t);   // 顯式定義優先，展開只補沒有的（同舊版）
            return d;
        }
        catch (Exception e) { oError = $"目標清單讀不了（{e.GetType().Name}: {e.Message}）：{aPath}"; return null; }
    }

    static IEnumerable<KbTarget> Expand(JsonElement iSpec, KbRoots iRoots)
    {
        (string aBase, string aPat) = Split(Str(iSpec, "enumerate_dirs"), iRoots);
        int aOwnerAt = iSpec.TryGetProperty("owner_dir_index", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : -2;
        string aNameTpl = Str(iSpec, "name_tpl", "{name}"), aGlobTpl = Str(iSpec, "glob_tpl"), aDescTpl = Str(iSpec, "desc_tpl", "{name}");
        foreach (string aDir in Glob(aBase, aPat, iDirs: true).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string[] aParts = aDir.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            int k = aOwnerAt < 0 ? aParts.Length + aOwnerAt : aOwnerAt;
            if (k < 0 || k >= aParts.Length) continue;
            string aOwner = aParts[k];
            yield return new KbTarget(aNameTpl.Replace("{name}", aOwner), aDescTpl.Replace("{name}", aOwner),
                                      Str(iSpec, "kind", "markdown"), new[] { aGlobTpl.Replace("{name}", aOwner) }, ExcludeFromAll: true,
                                      HalfLifeDays: Num(iSpec, "half_life_days"));
        }
    }

    /// <summary>`all` ＝ 全部（⛔ 不含 exclude_from_all 的展開 target：它們跟母 target 收同一批檔，同時進 all 會同分並列兩次）。</summary>
    public static List<string> Parse(string iArg, Dictionary<string, KbTarget> iAll)
    {
        if (string.Equals(iArg.Trim(), "all", StringComparison.OrdinalIgnoreCase))
            return iAll.Values.Where(t => !t.ExcludeFromAll).Select(t => t.Name).ToList();
        return iArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public static KbSources Resolve(KbTarget iTarget, KbRoots iRoots)
    {
        var aByReal = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var aBases = new List<string>();
        foreach (string g in iTarget.Globs)
        {
            (string aBase, string aPat) = Split(g, iRoots);
            aBases.Add(aBase);
            foreach (string f in Glob(aBase, aPat, iDirs: false))
                aByReal.TryAdd(Path.GetFullPath(f), f.Replace('\\', '/'));
        }
        return new KbSources(iTarget, aByReal.Values.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), aBases);
    }

    static (string Base, string Pat) Split(string iGlob, KbRoots iRoots)
    {
        if (iGlob.StartsWith(ScpCorePrefix, StringComparison.Ordinal)) return (iRoots.ScpCoreRoot, iGlob.Substring(ScpCorePrefix.Length));
        if (iGlob.StartsWith(DataPrefix, StringComparison.Ordinal)) return (iRoots.DataRoot, iGlob.Substring(DataPrefix.Length));
        return (iRoots.RepoRoot, iGlob);
    }

    const string ScpCorePrefix = "scp_core:", DataPrefix = "data:";

    /// <summary>
    /// glob 的前綴不是「無／scp_core:／data:」⇒ 回那個前綴名；否則 null。
    /// 🩸 不認得的前綴（例：已不支援的 `core:`）若不擋，會被當成 repo 相對的字面路徑、安靜地匹配 0 檔。
    /// </summary>
    static string? UnknownPrefix(string iGlob)
    {
        Match m = Regex.Match(iGlob, @"^([a-z_]+):");
        if (!m.Success) return null;
        string aName = m.Groups[1].Value + ":";
        return aName == ScpCorePrefix || aName == DataPrefix ? null : m.Groups[1].Value;
    }

    // ── glob ──────────────────────────────────────────────────────

    /// <summary>
    /// 在 <paramref name="iBase"/> 底下找符合 <paramref name="iPattern"/> 的檔（或目錄）。
    /// 語意同 Python pathlib：一段一段比；`**` 匹配零到多層目錄；段內 `*`／`?`／`[!x]`／`[abc]`。不分大小寫。
    /// </summary>
    public static List<string> Glob(string iBase, string iPattern, bool iDirs)
    {
        var aOut = new List<string>();
        if (iBase.Length == 0 || !Directory.Exists(iBase)) return aOut;
        string[] aSegs = iPattern.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        Walk(iBase, aSegs, 0, iDirs, aOut, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return aOut;
    }

    static void Walk(string iDir, string[] iSegs, int i, bool iDirs, List<string> oOut, HashSet<string> ioSeen)
    {
        if (i == iSegs.Length) return;
        bool aLast = i == iSegs.Length - 1;
        string s = iSegs[i];
        if (s == "**")
        {
            if (aLast) return;   // 尾巴是 `**` 的形狀清單裡沒有；不支援就不猜
            Walk(iDir, iSegs, i + 1, iDirs, oOut, ioSeen);   // 零層
            foreach (string sub in SafeDirs(iDir)) Walk(sub, iSegs, i, iDirs, oOut, ioSeen);
            return;
        }
        Regex aRx = SegmentRegex(s);
        if (aLast)
        {
            foreach (string e in iDirs ? SafeDirs(iDir) : SafeFiles(iDir))
                if (aRx.IsMatch(Path.GetFileName(e)) && ioSeen.Add(e)) oOut.Add(e);
            return;
        }
        foreach (string sub in SafeDirs(iDir))
            if (aRx.IsMatch(Path.GetFileName(sub))) Walk(sub, iSegs, i + 1, iDirs, oOut, ioSeen);
    }

    static readonly Dictionary<string, Regex> s_Rx = new();

    static Regex SegmentRegex(string iSeg)
    {
        lock (s_Rx)
        {
            if (s_Rx.TryGetValue(iSeg, out Regex? r)) return r;
            var sb = new System.Text.StringBuilder("^");
            for (int k = 0; k < iSeg.Length; k++)
            {
                char c = iSeg[k];
                if (c == '*') sb.Append(".*");
                else if (c == '?') sb.Append('.');
                else if (c == '[')
                {
                    int end = iSeg.IndexOf(']', k + 1);
                    if (end < 0) { sb.Append(@"\["); continue; }
                    string body = iSeg.Substring(k + 1, end - k - 1);
                    sb.Append('[').Append(body.StartsWith("!") ? "^" + Regex.Escape(body.Substring(1)) : Regex.Escape(body)).Append(']');
                    k = end;
                }
                else sb.Append(Regex.Escape(c.ToString()));
            }
            sb.Append('$');
            r = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            s_Rx[iSeg] = r;
            return r;
        }
    }

    static IEnumerable<string> SafeDirs(string d) { try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); } }
    static IEnumerable<string> SafeFiles(string d) { try { return Directory.GetFiles(d); } catch { return Array.Empty<string>(); } }

    static double Num(JsonElement e, string k)
        => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out double d) && d > 0 ? d : 0;

    static string Str(JsonElement e, string k, string iDefault = "")
        => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? iDefault : iDefault;

    static List<string> Arr(JsonElement e, string k)
    {
        var a = new List<string>();
        if (e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.Array)
            foreach (JsonElement s in x.EnumerateArray()) if (s.ValueKind == JsonValueKind.String) a.Add(s.GetString() ?? "");
        return a;
    }
}
