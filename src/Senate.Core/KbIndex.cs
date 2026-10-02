// 區塊職責：知識庫的**索引**（TASK-0378）—— 建、讀、判過期、檢索。
// 物理意義：一個 target 一個資料夾 `<資料根>/_kb/<target>/`：
//             meta.json   塊清單（id／檔／行號／標題路徑／文字／雜湊）＋來源檔的 mtime/size＋模型與切塊版本
//             dense.f16   n×dim 的 float16（BGE-M3 dense，已正規化 ⇒ 內積＝cosine）
//             sparse.bin  每塊：int32 個數，接著 (int32 token, float32 權重) × 個數
//           🩸 舊版把每個 float 寫成 JSON 一行（全部 ~875 MB，coredocs 光解析就 8 秒）—— 二進位是階段 0 的修正之一。
//           sparse 這次就算好存起來：混合檢索（方案 A）之後開的話不必重編全部的塊；預設檢索仍是 dense。
// 數值影響：建索引會寫那三個檔（先寫 .tmp 再換名，meta 最後寫 —— meta 在＝其他兩個已經寫完）；檢索純讀。
// ⚠ 文字沒變的塊沿用舊向量（雜湊對上、模型與切塊版本都相同才算）—— 換模型或改切塊規則必須整份重算，
//   混用的話分數不可比，而且不報錯。
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Senate.Core;

public sealed class KbChunkMeta
{
    public string Id { get; set; } = "";
    public string File { get; set; } = "";
    public string Rel { get; set; } = "";
    public int Line { get; set; }
    public string Heading { get; set; } = "";
    public string Text { get; set; } = "";
    public string Hash { get; set; } = "";
}

public sealed class KbSourceMeta
{
    public string File { get; set; } = "";
    public long MTimeTicks { get; set; }
    public long Size { get; set; }
}

public sealed class KbIndexMeta
{
    public int Schema { get; set; } = 1;
    public string Target { get; set; } = "";
    public string Model { get; set; } = "";
    public string Chunker { get; set; } = "";
    public int Dim { get; set; }
    public string BuiltAt { get; set; } = "";
    public int DroppedDuplicates { get; set; }
    public List<KbSourceMeta> Sources { get; set; } = new();
    public List<KbChunkMeta> Chunks { get; set; } = new();
}

public sealed record KbStale(int Added, int Removed, int Modified)
{
    public bool Any => Added + Removed + Modified > 0;
    public override string ToString() => Any ? $"+{Added}／−{Removed}／改 {Modified}" : "最新";
}

public sealed record KbBuildResult(int Files, int Chunks, int Reused, int Embedded, int DroppedDuplicates, long Ms);

public sealed record KbHit(double Score, string Target, KbChunkMeta Chunk);

public sealed class KbIndex
{
    public const string Model = "BAAI/bge-m3";
    public const string DirName = "_kb";
    static readonly JsonSerializerOptions s_Json = new() { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public KbIndexMeta Meta { get; }
    readonly Half[] m_Dense;
    readonly List<(int[] Tok, float[] W)> m_Sparse;

    KbIndex(KbIndexMeta iMeta, Half[] iDense, List<(int[] Tok, float[] W)> iSparse) { Meta = iMeta; m_Dense = iDense; m_Sparse = iSparse; }

    public static string Dir(string iDataRoot, string iTarget) => Path.Combine(iDataRoot, DirName, iTarget);

    // ── 讀 ──────────────────────────────────────────────────────────

    /// <summary>讀一個 target 的索引。回 null ＝ 沒有或壞了（原因在 oWhy —— ⛔「沒建過」與「壞了」不同形）。</summary>
    public static KbIndex? Load(string iDataRoot, string iTarget, out string oWhy)
    {
        oWhy = "";
        string d = Dir(iDataRoot, iTarget);
        string aMetaPath = Path.Combine(d, "meta.json");
        if (!File.Exists(aMetaPath)) { oWhy = "還沒建過"; return null; }
        try
        {
            KbIndexMeta m = JsonSerializer.Deserialize<KbIndexMeta>(File.ReadAllText(aMetaPath), s_Json) ?? throw new InvalidDataException("meta 是 null");
            byte[] aDenseBytes = File.ReadAllBytes(Path.Combine(d, "dense.f16"));
            if (aDenseBytes.Length != (long)m.Chunks.Count * m.Dim * 2)
            { oWhy = $"壞了：dense.f16 有 {aDenseBytes.Length} bytes，meta 說 {m.Chunks.Count}×{m.Dim}×2"; return null; }
            // ⚠ Buffer.BlockCopy 不收 Half[]（不是 primitive）⇒ 走 MemoryMarshal（2026-10-02 第一次實跑炸在這裡）
            Half[] aDense = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(aDenseBytes).ToArray();
            var aSparse = new List<(int[], float[])>(m.Chunks.Count);
            using (var br = new BinaryReader(File.OpenRead(Path.Combine(d, "sparse.bin"))))
                for (int i = 0; i < m.Chunks.Count; i++)
                {
                    int n = br.ReadInt32();
                    var t = new int[n]; var w = new float[n];
                    for (int k = 0; k < n; k++) { t[k] = br.ReadInt32(); w[k] = br.ReadSingle(); }
                    aSparse.Add((t, w));
                }
            return new KbIndex(m, aDense, aSparse);
        }
        catch (Exception e) { oWhy = $"壞了：{e.GetType().Name}: {e.Message}"; return null; }
    }

    /// <summary>跟磁碟現況比：新增／刪除／改過（mtime 或大小變了）的來源檔數。</summary>
    public KbStale StaleAgainst(KbSources iNow)
    {
        var aOld = Meta.Sources.ToDictionary(s => s.File, StringComparer.OrdinalIgnoreCase);
        int add = 0, mod = 0;
        var aNowSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in iNow.Files)
        {
            aNowSet.Add(f);
            if (!aOld.TryGetValue(f, out KbSourceMeta? o)) { add++; continue; }
            var fi = new FileInfo(f);
            if (fi.Length != o.Size || fi.LastWriteTimeUtc.Ticks != o.MTimeTicks) mod++;
        }
        int rem = aOld.Keys.Count(k => !aNowSet.Contains(k));
        return new KbStale(add, rem, mod);
    }

    // ── 建 ──────────────────────────────────────────────────────────

    /// <summary>
    /// 建（或增量重建）一個 target 的索引。文字沒變的塊沿用舊向量，只把新的送去嵌入。
    /// <para><paramref name="iEmbed"/> 是嵌入函式（實務上＝常駐程序；selftest 給假的）—— 本層不知道 HTTP。</para>
    /// </summary>
    public static KbBuildResult Build(KbSources iSrc, string iDataRoot, string iProjectRoot,
                                      Func<IReadOnlyList<string>, KbEmbedding> iEmbed, Action<string> iLog, string iModel = Model)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string aTarget = iSrc.Target.Name;

        // ① 切塊＋同 target 內正文去重
        var aChunks = new List<KbChunkMeta>();
        var aSeenBody = new HashSet<string>(StringComparer.Ordinal);
        int aDropped = 0;
        var aSources = new List<KbSourceMeta>();
        foreach (string f in iSrc.Files)
        {
            string aText;
            var fi = new FileInfo(f);
            try { aText = File.ReadAllText(f); } catch (Exception e) { iLog($"⚠ 讀不了 {f}：{e.Message}（這個檔這次不進索引）"); continue; }
            aSources.Add(new KbSourceMeta { File = f, MTimeTicks = fi.LastWriteTimeUtc.Ticks, Size = fi.Length });
            string aPrefix = RelTo(f, iSrc.Bases);
            int ord = 0;
            foreach (KbChunk c in KbChunker.Chunk(iSrc.Target.Kind, f, aText))
            {
                string aNorm = string.Join(' ', c.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (!aSeenBody.Add(aNorm)) { aDropped++; continue; }
                aChunks.Add(new KbChunkMeta
                {
                    Id = $"{aPrefix}#{ord++}", File = f, Rel = RelTo(f, new[] { iProjectRoot }), Line = c.Line,
                    Heading = c.Heading, Text = c.Text, Hash = Sha(c.Text),
                });
            }
        }

        // ② 沿用舊向量（模型與切塊版本都相同才算）
        var aReuse = new Dictionary<string, (Half[] D, (int[] T, float[] W) S)>(StringComparer.Ordinal);
        KbIndex? aOld = Load(iDataRoot, aTarget, out _);
        if (aOld != null && aOld.Meta.Model == iModel && aOld.Meta.Chunker == KbChunker.Version)
            for (int i = 0; i < aOld.Meta.Chunks.Count; i++)
                aReuse.TryAdd(aOld.Meta.Chunks[i].Hash, (aOld.Row(i), aOld.m_Sparse[i]));

        // ③ 只嵌沒有的
        int aDim = aOld?.Meta.Dim ?? 0;
        var aDenseRows = new Half[aChunks.Count][];
        var aSparseRows = new (int[] T, float[] W)[aChunks.Count];
        var aTodo = new List<int>();
        for (int i = 0; i < aChunks.Count; i++)
        {
            if (aReuse.TryGetValue(aChunks[i].Hash, out var r)) { aDenseRows[i] = r.D; aSparseRows[i] = r.S; }
            else aTodo.Add(i);
        }
        iLog($"{aTarget}：{aSources.Count} 檔 → {aChunks.Count} 塊（去重丟掉 {aDropped}）；沿用 {aChunks.Count - aTodo.Count}、要嵌 {aTodo.Count}");
        const int Batch = 64;
        for (int b = 0; b < aTodo.Count; b += Batch)
        {
            var aIdx = aTodo.Skip(b).Take(Batch).ToList();
            KbEmbedding e = iEmbed(aIdx.Select(i => aChunks[i].Text).ToList());
            aDim = e.Dim;
            for (int k = 0; k < aIdx.Count; k++)
            {
                aDenseRows[aIdx[k]] = e.Dense[k].Select(x => (Half)x).ToArray();
                var sp = e.Sparse.Count > k ? e.Sparse[k] : new Dictionary<int, float>();
                aSparseRows[aIdx[k]] = (sp.Keys.ToArray(), sp.Values.ToArray());
            }
            if ((b / Batch) % 10 == 0 || b + Batch >= aTodo.Count) iLog($"  嵌入 {Math.Min(b + Batch, aTodo.Count)}／{aTodo.Count}");
        }

        // ④ 寫檔（.tmp → 換名；meta 最後）
        string d = Dir(iDataRoot, aTarget);
        Directory.CreateDirectory(d);
        EnsureGitIgnore(iDataRoot);
        using (var fs = File.Create(Path.Combine(d, "dense.f16.tmp")))
            foreach (Half[] row in aDenseRows)
                fs.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()));
        using (var bw = new BinaryWriter(File.Create(Path.Combine(d, "sparse.bin.tmp"))))
            foreach (var (t, w) in aSparseRows)
            {
                bw.Write(t.Length);
                for (int k = 0; k < t.Length; k++) { bw.Write(t[k]); bw.Write(w[k]); }
            }
        try { File.Delete(Path.Combine(d, "meta.json")); } catch { }   // 先撤 meta：中途斷掉時「meta 在」不會配上半套向量
        File.Move(Path.Combine(d, "dense.f16.tmp"), Path.Combine(d, "dense.f16"), overwrite: true);
        File.Move(Path.Combine(d, "sparse.bin.tmp"), Path.Combine(d, "sparse.bin"), overwrite: true);
        var aMeta = new KbIndexMeta
        {
            Target = aTarget, Model = iModel, Chunker = KbChunker.Version, Dim = aDim,
            BuiltAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"), DroppedDuplicates = aDropped,
            Sources = aSources, Chunks = aChunks,
        };
        File.WriteAllText(Path.Combine(d, "meta.json.tmp"), JsonSerializer.Serialize(aMeta, s_Json));
        File.Move(Path.Combine(d, "meta.json.tmp"), Path.Combine(d, "meta.json"), overwrite: true);
        return new KbBuildResult(aSources.Count, aChunks.Count, aChunks.Count - aTodo.Count, aTodo.Count, aDropped, sw.ElapsedMilliseconds);
    }

    Half[] Row(int i) { var r = new Half[Meta.Dim]; Array.Copy(m_Dense, i * Meta.Dim, r, 0, Meta.Dim); return r; }

    // ── 檢索 ────────────────────────────────────────────────────────

    /// <summary>
    /// 對這份索引打分。dense ＝ 內積（正規化過 ⇒ cosine，與舊版同一個分數定義）；
    /// hybrid ＝ dense ＋ <paramref name="iSparseWeight"/>×sparse（BGE-M3 的 lexical 內積；方案 A 評估用）。
    /// </summary>
    public IEnumerable<KbHit> Score(float[] iQuery, Dictionary<int, float>? iQuerySparse, double iSparseWeight)
    {
        int dim = Meta.Dim;
        for (int i = 0; i < Meta.Chunks.Count; i++)
        {
            double s = 0;
            int off = i * dim;
            for (int k = 0; k < dim; k++) s += (float)m_Dense[off + k] * iQuery[k];
            if (iQuerySparse != null && iSparseWeight > 0)
            {
                var (t, w) = m_Sparse[i];
                double sp = 0;
                for (int k = 0; k < t.Length; k++) if (iQuerySparse.TryGetValue(t[k], out float qw)) sp += qw * w[k];
                s += iSparseWeight * sp;
            }
            yield return new KbHit(s, Meta.Target, Meta.Chunks[i]);
        }
    }

    // ── 雜項 ────────────────────────────────────────────────────────

    /// <summary>
    /// `_kb/` 自帶一份 `.gitignore`（同舊 `_vectors/` 的做法）：索引是可以重建的衍生物，⛔ 不進資料根的版控。
    /// 🩸 沒有它的話，第一次建索引就會讓資料根的 git status 多出數百 MB 的檔，而自動 commit 會把它們列進「未分類」。
    /// </summary>
    static void EnsureGitIgnore(string iDataRoot)
    {
        string p = Path.Combine(iDataRoot, DirName, ".gitignore");
        if (File.Exists(p)) return;
        File.WriteAllText(p, "# 知識庫索引（senate cmd kb，TASK-0378）：由文件重建的衍生物，不入版控\n*\n!.gitignore\n");
    }

    static string Sha(string s) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>相對路徑（找不到對應的根就回完整路徑）—— 塊 id 用它，同名檔才不會撞（舊版用裸檔名，library 883 個檔撞名）。</summary>
    public static string RelTo(string iFile, IEnumerable<string> iBases)
    {
        string f = Path.GetFullPath(iFile).Replace('\\', '/');
        foreach (string b0 in iBases)
        {
            if (b0.Length == 0) continue;
            string b = Path.GetFullPath(b0).Replace('\\', '/').TrimEnd('/') + "/";
            if (f.StartsWith(b, StringComparison.OrdinalIgnoreCase)) return f.Substring(b.Length);
        }
        return f;
    }
}
