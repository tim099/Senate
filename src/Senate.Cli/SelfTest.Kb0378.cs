// 區塊職責：TASK-0378（知識庫搬到 Senate）的自我對拍。
// 物理意義：三格各驗一個「錯了也不會叫」的地方：
//           ① 切塊：標題不單獨成塊、標題路徑帶進塊文字、code fence 裡的 # 不是標題、短塊併掉、長段落切到上限內、jsonl 一筆一塊。
//           ② 索引（假嵌入，temp 目錄）：寫讀往返；同 target 跨檔同文去重；🔴 改一個檔的一個字 ⇒ 只有那個檔變了的塊重嵌，其他沿用。
//           ③ glob：`[!_]*` 排除底線開頭的檔、`**` 可以是零層；🔴 大小寫不同的兩條 glob 指到同一批檔 ⇒ 只收一次。
// 數值影響：只在 temp 目錄造檔，跑完刪；⛔ 不起常駐程序、不碰真實資料根。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow KbChunkerRules()
    {
        const string aName = "知識庫・切塊：標題不單獨成塊／標題路徑進塊文字／fence 裡的 # 不是標題／短塊併掉／長段落切到上限／jsonl 一筆一塊（TASK-0378）";
        try
        {
            var aFails = new List<string>();
            string md = "---\ntitle: 甲文件\n---\n\n# 甲文件\n\n## 一節\n\n### 很短的小節\n\n短。\n\n### 正常小節\n\n"
                        + new string('字', 120) + "\n\n```\n# 這不是標題\n```\n\n## 二節\n\n" + string.Join("。", Enumerable.Repeat(new string('長', 100), 25)) + "。\n";
            List<KbChunk> c = KbChunker.ChunkMarkdown(md);
            if (c.Any(x => x.Body.Trim().Length == 0)) aFails.Add("有空正文的塊（標題單獨成塊）");
            if (c.Any(x => x.Body.Length < KbChunker.MinChars)) aFails.Add("有短於下限的塊沒被併掉");
            if (!c.Any(x => x.Text.StartsWith("甲文件 › 一節 › 正常小節\n", StringComparison.Ordinal))) aFails.Add("標題路徑沒進塊文字（或 title 與 # 重複）");
            if (c.Any(x => x.Heading.Contains("這不是標題"))) aFails.Add("🔴 code fence 裡的 # 被當成標題");
            if (!c.Any(x => x.Body.Contains("短。"))) aFails.Add("短塊的內容被丟了（應該併進下一塊）");
            int aMax = c.Max(x => x.Body.Length);
            if (aMax > KbChunker.MaxChars + KbChunker.MinChars * 2) aFails.Add($"最長的塊 {aMax} 字，超過上限太多");
            if (c.Count(x => x.Heading.EndsWith("二節", StringComparison.Ordinal)) < 2) aFails.Add("2600 字的段落沒有被切成多塊");

            List<KbChunk> j = KbChunker.ChunkJsonl("{\"title\":\"甲\",\"body\":\"第一筆\"}\n\n{\"body\":\"第二筆\"}\n不是 json 的一行\n");
            if (j.Count != 3) aFails.Add($"jsonl 要 3 塊（含讀不了的那行，⛔ 不丟）得 {j.Count}");
            else
            {
                if (j[0].Text != "甲\n第一筆" || j[0].Line != 1) aFails.Add("jsonl 第一筆的文字或行號不對");
                if (j[1].Line != 3) aFails.Add("jsonl 行號沒有照實（空行也要算）");
            }
            return new CheckRow(aName, aFails.Count == 0 ? $"md → {c.Count} 塊、jsonl → {j.Count} 塊，規則逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    /// <summary>假嵌入：文字的雜湊決定向量（同一段文字永遠同一個向量），並記下被嵌了幾句。</summary>
    sealed class FakeEmbedder
    {
        public int Calls;
        public KbEmbedding Embed(IReadOnlyList<string> iTexts)
        {
            Calls += iTexts.Count;
            const int Dim = 8;
            var d = iTexts.Select(t =>
            {
                var v = new float[Dim];
                int h = t.GetHashCode();
                for (int k = 0; k < Dim; k++) v[k] = ((h >> k) & 1) == 1 ? 1f : -1f;
                float n = (float)Math.Sqrt(v.Sum(x => x * x));
                return v.Select(x => x / n).ToArray();
            }).ToArray();
            var s = iTexts.Select(t => new Dictionary<int, float> { [t.Length] = 0.5f }).ToList();
            return new KbEmbedding(d, s, Dim);
        }
    }

    static CheckRow KbIndexRoundTrip()
    {
        const string aName = "知識庫・索引：寫讀往返／跨檔同文去重／🔴 改一個字 ⇒ 只重嵌變了的塊（假嵌入，TASK-0378）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_kbindex_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            string aDocs = Path.Combine(aTmp, "docs"), aData = Path.Combine(aTmp, "data");
            Directory.CreateDirectory(aDocs);
            string Para(string w) => string.Concat(Enumerable.Repeat(w, 60));
            File.WriteAllText(Path.Combine(aDocs, "a.md"), $"# A\n\n## 一\n\n{Para("甲")}\n\n## 二\n\n{Para("乙")}\n");
            File.WriteAllText(Path.Combine(aDocs, "b.md"), $"# A\n\n## 一\n\n{Para("甲")}\n\n## 三\n\n{Para("丙")}\n");   // 「甲」那段跟 a.md 同文
            var aTarget = new KbTarget("t", "", "markdown", new[] { "docs/*.md" }, false);
            var aRoots = new KbRoots { ProjectRoot = aTmp, DataRoot = aData, CoreRoot = aTmp };
            var fake = new FakeEmbedder();

            KbBuildResult b1 = KbIndex.Build(KbTargets.Resolve(aTarget, aRoots), aData, aTmp, fake.Embed, _ => { });
            if (b1.DroppedDuplicates != 1) aFails.Add($"跨檔同文要丟 1 塊，得 {b1.DroppedDuplicates}");
            KbIndex? ix = KbIndex.Load(aData, "t", out string aWhy);
            if (ix == null) aFails.Add("讀不回來：" + aWhy);
            else
            {
                if (ix.Meta.Chunks.Count != b1.Chunks) aFails.Add("讀回的塊數跟建的不一樣");
                if (ix.Meta.Chunks.Select(x => x.Id).Distinct().Count() != ix.Meta.Chunks.Count) aFails.Add("塊 id 有重複");
                // 自己找自己：每一塊用它自己的向量查，第一名要是它自己
                var aOwn = fake.Embed(new[] { ix.Meta.Chunks[0].Text }).Dense[0];
                if (ix.Score(aOwn, null, 0).OrderByDescending(h => h.Score).First().Chunk.Id != ix.Meta.Chunks[0].Id)
                    aFails.Add("用塊自己的向量查，第一名不是它自己");
                if (ix.StaleAgainst(KbTargets.Resolve(aTarget, aRoots)).Any) aFails.Add("剛建好就被判成過期");
            }

            // 🔴 改 a.md「二」那段的一個字 ⇒ 只有那一塊重嵌
            System.Threading.Thread.Sleep(20);
            File.WriteAllText(Path.Combine(aDocs, "a.md"), $"# A\n\n## 一\n\n{Para("甲")}\n\n## 二\n\n{Para("乙")}改\n");
            if (KbIndex.Load(aData, "t", out _)?.StaleAgainst(KbTargets.Resolve(aTarget, aRoots)).Modified != 1) aFails.Add("改了 a.md 卻沒判成過期");
            fake.Calls = 0;
            KbBuildResult b2 = KbIndex.Build(KbTargets.Resolve(aTarget, aRoots), aData, aTmp, fake.Embed, _ => { });
            if (b2.Embedded != 1 || fake.Calls != 1) aFails.Add($"🔴 改一個字要只重嵌 1 塊，得 {b2.Embedded}（實際送嵌 {fake.Calls}）");
            if (b2.Reused != b2.Chunks - 1) aFails.Add($"其他塊應該沿用（沿用 {b2.Reused}／{b2.Chunks}）");
            if (!File.Exists(Path.Combine(aData, KbIndex.DirName, ".gitignore"))) aFails.Add("_kb/.gitignore 沒寫");

            return new CheckRow(aName, aFails.Count == 0 ? $"首建 {b1.Chunks} 塊（去重 {b1.DroppedDuplicates}）；改一字只重嵌 {b2.Embedded} 塊" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }

    static CheckRow KbGlobRules()
    {
        const string aName = "知識庫・glob：[!_]* 排除底線檔／** 可以零層／🔴 大小寫不同的兩條 glob 指同一批檔 ⇒ 只收一次（TASK-0378）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_kbglob_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            Directory.CreateDirectory(Path.Combine(aTmp, "Lessons", "sub"));
            File.WriteAllText(Path.Combine(aTmp, "Lessons", "a.md"), "x");
            File.WriteAllText(Path.Combine(aTmp, "Lessons", "_index.md"), "x");
            File.WriteAllText(Path.Combine(aTmp, "Lessons", "sub", "b.md"), "x");
            var aRoots = new KbRoots { ProjectRoot = aTmp, DataRoot = aTmp, CoreRoot = aTmp };
            var t1 = new KbTarget("x", "", "markdown", new[] { "Lessons/[!_]*.md" }, false);
            var f1 = KbTargets.Resolve(t1, aRoots).Files.Select(Path.GetFileName).ToList();
            if (string.Join(",", f1) != "a.md") aFails.Add("[!_]* 得 " + string.Join(",", f1));
            var t2 = new KbTarget("y", "", "markdown", new[] { "Lessons/**/*.md" }, false);
            int n2 = KbTargets.Resolve(t2, aRoots).Files.Count;
            if (n2 != 3) aFails.Add($"** 零層＋一層要 3 檔，得 {n2}");
            var t3 = new KbTarget("z", "", "markdown", new[] { "Lessons/**/*.md", "lessons/**/*.md" }, false);
            int n3 = KbTargets.Resolve(t3, aRoots).Files.Count;
            if (n3 != 3) aFails.Add($"🔴 Lessons／lessons 兩條要去重成 3 檔，得 {n3}（舊版就是在這裡把每一塊算兩次）");
            return new CheckRow(aName, aFails.Count == 0 ? "三格逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }
}
