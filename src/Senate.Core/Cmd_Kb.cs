// 區塊職責：`senate cmd kb` —— 知識庫（語意檢索）的 Senate 版（TASK-0378）：status／reindex／search／eval／sidecar。
// 物理意義：取代 UCL_Core 的 knowledge_base.py（那支在新版能取代它之前照常可用；兩邊讀**同一份** kb_targets.json、
//           同一批文件，⛔ 不搬舊的向量檔 —— Tim：新架構要兼容舊資料，而資料本來就是文件）。
//           嵌入由常駐程序（KbSidecar）做 ⇒ 模型只載一次；索引是二進位（KbIndex）；切塊依標題（KbChunker）。
// 數值影響：status／search 純讀（search 預設會先把過期的 target 重建，跟舊版同一個預設；`auto_reindex=0` 關掉）；
//           reindex 寫 `<資料根>/_kb/<target>/`；sidecar 起／停常駐程序。
// ⚠ exit：0 成功／1 擋下／2 用法錯／3 **缺相依**（照 Install §6 問使用者）／4 **量不到**（⛔ 不當成缺）／5 動手了但沒成功。
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_Kb : SCP_Cmd
{
    public override string Name => "kb";

    public override string Summary => "知識庫語意檢索（Senate 版）：status／reindex／search／eval／sidecar —— 模型常駐只載一次";

    public override string Details =>
        "目標清單讀 UCL_Core 的 `Tools~/AgentCommands/kb_targets.json`（與舊 knowledge_base.py 同一份）；索引在 `<資料根>/_kb/<target>/`。\n"
        + "⚠ 輸入形狀是**一句話**不是關鍵字（語意檢索）。`mode=hybrid`（dense＋sparse）目前只給評估用，預設 dense。\n"
        + "⚠ 第一次檢索要拉起常駐嵌入程序（冷啟動約 1 分多鐘），之後一句約 0.1 秒；閒置 30 分它自己退。\n"
        + "⚠ 缺套件或模型 ⇒ exit 3，照 `senate cmd doc --arg op=show --arg name=Install` §6 問使用者，同意了才裝。";

    public override string Example => SCP_CmdRegistry.Invoke("kb --arg op=search --arg target=fragments,alaya --arg query=\"<想不起的那件事寫成一句話>\"");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "status（預設）｜reindex｜search｜eval｜sidecar", iDefault: "status",
                           iChoices: new[] { "status", "reindex", "search", "eval", "sidecar" }),
        new SCP_CmdArgSpec("target", "target 名，逗號分隔或 all（search 預設 fragments,alaya,coredocs,docs,work_memory；reindex／status 預設 all）"),
        new SCP_CmdArgSpec("query", "search：要找的事（一句話）"),
        new SCP_CmdArgSpec("topk", "search：回幾筆（預設 5）", iDefault: "5"),
        new SCP_CmdArgSpec("mode", "search／eval：dense（預設）｜hybrid（dense＋sparse）｜rerank（hybrid 取前 30 再交給 bge-reranker-v2-m3 重排；分數是 0..1 的重排分，與 dense 內積不同尺度）｜compare（只給 eval：dense／hybrid／rerank 各跑一遍，各自帶與不帶衰減）", iDefault: "dense",
                           iChoices: new[] { "dense", "hybrid", "rerank", "compare" }),
        new SCP_CmdArgSpec("sparse_weight", "hybrid／rerank 時 sparse 的權重（預設 0.3）", iDefault: "0.3"),
        new SCP_CmdArgSpec("decay", "search／eval：=1 ⇒ 時間衰減（只對 kb_targets.json 設了 half_life_days 的 target：碎片、工作記憶；文件類不衰減）。預設 0", iDefault: "0"),
        new SCP_CmdArgSpec("decay_weight", "衰減的最大扣分（預設 0.05；很久沒動的塊趨近被扣這麼多）", iDefault: "0.05"),
        new SCP_CmdArgSpec("format", "status／search：text（預設）｜json（後台頁讀這個）", iDefault: "text", iChoices: new[] { "text", "json" }),
        new SCP_CmdArgSpec("auto_reindex", "search：過期的 target 先重建（預設 1；0＝照現有索引查）", iDefault: "1"),
        new SCP_CmdArgSpec("dry_run", "reindex：=1 只切塊、印統計（塊數、太短的、去重丟掉的、最長），不嵌入不寫檔"),
        new SCP_CmdArgSpec("action", "sidecar：status（預設）｜start｜stop", iDefault: "status", iChoices: new[] { "status", "start", "stop" }),
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（沒給 ⇒ 用設定檔那一格）"),
        new SCP_CmdArgSpec("project_root", "專案根（沒給 ⇒ 用設定檔那一格；UCL_Core 由它的 .gitmodules 找）"),
    };

    public const string DefaultSearchTargets = "fragments,alaya,coredocs,docs,work_memory";

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        if (UnityDelegateCmd.ConfigProvider == null || ServerDelegateCmd.RepoRootProvider == null)
            return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源／repo 根 —— 這是程式錯誤不是用法錯");
        (SenateConfig? aConfig, _) = UnityDelegateCmd.ConfigProvider();
        string aRepo = ServerDelegateCmd.RepoRootProvider();
        string aData = iArgs.Get("data_root"), aProj = iArgs.Get("project_root");
        if (aData.Length == 0 || aProj.Length == 0) return SCP_CmdResult.Fail(2, "✗ 解不出資料根或專案根（設定檔那兩格）—— 用 `senate cmd paths` 看");
        string? aKbTargets = Cmd_Task.UclCoreTool(aProj, KbTargets.FileName, out string aWhy);
        if (aKbTargets == null) return SCP_CmdResult.Fail(1, "✗ 找不到 UCL_Core：" + aWhy);
        var aRoots = new KbRoots { ProjectRoot = aProj, DataRoot = aData, CoreRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(aKbTargets)!, "..", "..")) };
        Dictionary<string, KbTarget>? aAll = KbTargets.Load(aRoots, out string? aErr);
        if (aAll == null) return SCP_CmdResult.Fail(1, "✗ " + aErr);

        var ctx = new Ctx(aRoots, aAll, InstallEnv.Resolve(aConfig), new KbSidecar(aRepo), aRepo);
        string aOp = iArgs.Get("op");
        return aOp switch
        {
            "reindex" => Reindex(ctx, iArgs),
            "search" => Search(ctx, iArgs),
            "eval" => Eval(ctx, iArgs),
            "sidecar" => Sidecar(ctx, iArgs),
            _ => Status(ctx, iArgs),
        };
    }

    sealed record Ctx(KbRoots Roots, Dictionary<string, KbTarget> All, InstallEnv Env, KbSidecar Car, string Repo);

    static void Log(string s) => Console.Error.WriteLine("  " + s);

    // ── 相依＋常駐程序 ───────────────────────────────────────────────

    /// <summary>確保常駐程序在跑；缺相依回 exit 3、量不到回 exit 4（照 Install §6）。回 null ＝ 好了。</summary>
    static SCP_CmdResult? Ready(Ctx c, bool iRerank = false)
    {
        // 在跑（而且要重排的話它會重排）⇒ 相依一定齊了（模型載得起來）。
        // 重排另外需要重排模型：它是第一次 /rerank 才載入的，所以用重排時不論 sidecar 在不在都量一次（模型檔存在與否，便宜）。
        KbSidecar.Health? aHealth = c.Car.Probe();
        if (aHealth != null && !iRerank) return null;
        InstallCatalog? aCat = InstallCatalog.Load(InstallCatalog.DefaultPath(c.Repo), out string? aCatErr);
        if (aCat == null) return SCP_CmdResult.Fail(InstallRequire.UnknownExitCode, "✗ 安裝清單讀不了 ⇒ 量不到相依：" + aCatErr);
        IEnumerable<string> aIds = aHealth == null ? KbSidecar.RequiredInstallIds : Array.Empty<string>();
        if (iRerank) aIds = aIds.Concat(KbSidecar.RerankInstallIds);
        var aItems = aIds.Select(id => aCat.Find(id)).Where(i => i != null).Cast<InstallItem>().ToList();
        List<InstallItemStatus> aStatus = InstallProbe.Probe(c.Env, aItems);
        if (aStatus.Any(s => s.State == InstallState.Unknown))
            return SCP_CmdResult.Fail(InstallRequire.UnknownExitCode,
                "⚠ **量不到**相依（不知道缺不缺）—— ⛔ 不要去問使用者要不要裝：",
                string.Join("\n", aStatus.Where(s => s.State == InstallState.Unknown).Select(s => $"  - {s.Item.Id}：{s.Detail}")));
        var aMissing = aStatus.Where(s => s.State != InstallState.Installed).ToList();
        if (aMissing.Count > 0)
        {
            var r = new SCP_CmdResult { ExitCode = InstallRequire.MissingExitCode };
            r.Lines.Add("## ⚠ 缺相依 —— 請先問使用者要不要安裝（⛔ 不要自己決定）");
            foreach (InstallItemStatus s in aMissing)
                r.Lines.Add($"  - {s.Item.Name}（{s.Item.Id}）約 {s.Item.SizeMb} MB，來源 {s.Item.Source}（現在：{InstallItemStatus.StateText(s.State)}）");
            string aCmd = InstallRequire.InstallCommand(aMissing.Select(s => s.Item.Id));
            r.Lines.Add("使用者同意之後才跑：");
            r.Lines.Add("  " + aCmd);
            r.AddValue("missing_ids", string.Join(",", aMissing.Select(s => s.Item.Id)));
            r.AddValue("install_command", aCmd);
            return r;
        }
        string? aStartErr = iRerank ? c.Car.EnsureRunningWithRerank(c.Env, Log) : c.Car.EnsureRunning(c.Env, Log);
        return aStartErr == null ? null : SCP_CmdResult.Fail(5, "✗ 常駐嵌入程序起不來：" + aStartErr);
    }

    static KbEmbedding EmbedVia(KbSidecar iCar, IReadOnlyList<string> iTexts) => iCar.Embed(iTexts, iSparse: true);

    // ── status ──────────────────────────────────────────────────────

    static SCP_CmdResult Status(Ctx c, SCP_CmdArgs iArgs)
    {
        var r = new SCP_CmdResult();
        string aArg = iArgs.Get("target");
        List<string> aNames = aArg.Length == 0 ? c.All.Keys.ToList() : KbTargets.Parse(aArg, c.All);
        KbSidecar.Health? h = c.Car.Probe();
        bool aJson = iArgs.Get("format") == "json";
        string aIndexDir = Path.Combine(c.Roots.DataRoot, KbIndex.DirName).Replace('\\', '/');
        r.Lines.Add("# 🧠 知識庫（Senate 版）");
        r.Lines.Add("· 常駐嵌入程序：" + (h == null ? "沒在跑（第一次檢索時會自己拉起）" : $"在跑（pid {h.Pid}，{h.Device}，載入花了 {h.LoadedMs / 1000.0:0.0} 秒，已嵌 {h.Served} 句）"));
        r.Lines.Add("· 索引位置：" + aIndexDir);
        r.Lines.Add("");
        int aStaleN = 0, aMissingN = 0;
        // 後台頁（`format=json`）與文字輸出讀**同一圈**：每個 target 一列 state／files／chunks／built_at／detail。
        // state：fresh 最新｜stale 落後磁碟或規則換了｜unbuilt 還沒建索引｜unknown 不認得的 target。
        var aRows = new List<object>();
        foreach (string n in aNames)
        {
            if (!c.All.TryGetValue(n, out KbTarget? t)) { r.Lines.Add($"- ✗ {n}：不認得的 target"); aRows.Add(new { name = n, state = "unknown", files = 0, chunks = 0, built_at = "", detail = "不認得的 target" }); continue; }
            KbSources s = KbTargets.Resolve(t, c.Roots);
            KbIndex? ix = KbIndex.Load(c.Roots.DataRoot, n, out string aWhy);
            if (ix == null) { aMissingN++; r.Lines.Add($"- ・{n}：{s.Files.Count} 檔，索引{aWhy}"); aRows.Add(new { name = n, state = "unbuilt", files = s.Files.Count, chunks = 0, built_at = "", detail = "索引" + aWhy }); continue; }
            KbStale st = ix.StaleAgainst(s);
            bool aRules = ix.Meta.Chunker != KbChunker.Version || ix.Meta.Model != KbIndex.Model;
            if (st.Any || aRules) aStaleN++;
            string aDetail = aRules ? "切塊規則或模型換了，要整份重建" : st.ToString();
            r.Lines.Add($"- {(st.Any || aRules ? "◐" : "✓")} {n}：{s.Files.Count} 檔／{ix.Meta.Chunks.Count} 塊（去重丟 {ix.Meta.DroppedDuplicates}）　建於 {ix.Meta.BuiltAt}　{(aRules ? "⚠ " + aDetail : aDetail)}");
            aRows.Add(new { name = n, state = st.Any || aRules ? "stale" : "fresh", files = s.Files.Count, chunks = ix.Meta.Chunks.Count, built_at = ix.Meta.BuiltAt, detail = aDetail });
        }
        r.AddValue("stale_targets", aStaleN.ToString(CultureInfo.InvariantCulture));
        r.AddValue("unbuilt_targets", aMissingN.ToString(CultureInfo.InvariantCulture));
        r.AddValue("sidecar", h == null ? "stopped" : "running");
        if (aJson)
        {
            r.Lines.Clear();
            r.Lines.Add(JsonSerializer.Serialize(new
            {
                ok = true, index_dir = aIndexDir,
                sidecar = new { running = h != null, pid = h?.Pid ?? 0, device = h?.Device ?? "", loaded_ms = h?.LoadedMs ?? 0, served = h?.Served ?? 0 },
                targets = aRows,
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        return r;
    }

    // ── reindex ─────────────────────────────────────────────────────

    static SCP_CmdResult Reindex(Ctx c, SCP_CmdArgs iArgs)
    {
        string aArg = iArgs.Get("target");
        List<string> aNames = aArg.Length == 0 ? KbTargets.Parse("all", c.All) : KbTargets.Parse(aArg, c.All);
        foreach (string n in aNames) if (!c.All.ContainsKey(n)) return SCP_CmdResult.Fail(2, $"✗ 不認得的 target '{n}'（可用：{string.Join("、", c.All.Keys)}）");
        var r = new SCP_CmdResult();
        if (iArgs.Get("dry_run") == "1")
        {
            // 只切塊：量切塊規則本身（不需要模型）—— 舊版的毛病（短塊、重複）要在這裡就看得到改善
            foreach (string n in aNames)
            {
                KbSources s = KbTargets.Resolve(c.All[n], c.Roots);
                var aBodies = new List<string>(); var aSeen = new HashSet<string>(StringComparer.Ordinal); int aDup = 0;
                foreach (string f in s.Files)
                    foreach (KbChunk ch in KbChunker.Chunk(c.All[n].Kind, f, File.ReadAllText(f)))
                    {
                        string aNorm = string.Join(' ', ch.Body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                        if (!aSeen.Add(aNorm)) { aDup++; continue; }
                        aBodies.Add(ch.Body);
                    }
                int aShort = aBodies.Count(b => b.Length < KbChunker.MinChars);
                r.Lines.Add($"- {n}：{s.Files.Count} 檔 → {aBodies.Count} 塊　正文 <{KbChunker.MinChars} 字 {aShort}（{(aBodies.Count == 0 ? 0 : 100.0 * aShort / aBodies.Count):0.0}%）　去重丟 {aDup}　最長 {(aBodies.Count == 0 ? 0 : aBodies.Max(b => b.Length))}　中位數 {(aBodies.Count == 0 ? 0 : aBodies.Select(b => b.Length).OrderBy(x => x).ElementAt(aBodies.Count / 2))}");
            }
            r.AddValue("dry_run", "1");
            return r;
        }
        SCP_CmdResult? aNotReady = Ready(c);
        if (aNotReady != null) return aNotReady;
        foreach (string n in aNames)
        {
            KbBuildResult b = KbIndex.Build(KbTargets.Resolve(c.All[n], c.Roots), c.Roots.DataRoot, c.Roots.ProjectRoot,
                                            t => EmbedVia(c.Car, t), Log);
            r.Lines.Add($"✓ {n}：{b.Files} 檔 → {b.Chunks} 塊（沿用 {b.Reused}、新嵌 {b.Embedded}、去重丟 {b.DroppedDuplicates}）　{b.Ms / 1000.0:0.0} 秒");
            r.AddValue(n + "_chunks", b.Chunks.ToString(CultureInfo.InvariantCulture));
            r.AddValue(n + "_embedded", b.Embedded.ToString(CultureInfo.InvariantCulture));
        }
        return r;
    }

    // ── search ──────────────────────────────────────────────────────

    public sealed record SearchOutcome(List<KbHit> Hits, long QueryMs, long TotalMs, List<string> Reindexed, List<string> Notes, int SearchedChunks);

    /// <summary>
    /// 一次檢索的排序設定。Mode：dense（預設）｜hybrid（dense＋sparse）｜rerank（hybrid 取前 <see cref="RerankPool"/> 再交給重排模型）。
    /// Decay：只對有設半衰期的 target 生效（碎片、工作記憶），分數減掉 DecayWeight×(1−2^(−年齡天數/半衰期))。
    /// </summary>
    public sealed record SearchOpts(string Mode, double SparseWeight, bool Decay, double DecayWeight)
    {
        public bool UsesSparse => Mode != "dense";
        public bool Rerank => Mode == "rerank";
        public string Label => Mode + (Decay ? "＋衰減" : "");
    }

    public const int RerankPool = 30;
    public const double DefaultDecayWeight = 0.05;

    static SearchOpts ParseOpts(SCP_CmdArgs iArgs)
    {
        double aSw = double.TryParse(iArgs.Get("sparse_weight"), NumberStyles.Float, CultureInfo.InvariantCulture, out double w) ? w : 0.3;
        double aDw = double.TryParse(iArgs.Get("decay_weight"), NumberStyles.Float, CultureInfo.InvariantCulture, out double dw) && dw >= 0 ? dw : DefaultDecayWeight;
        return new SearchOpts(iArgs.Get("mode"), aSw, iArgs.Get("decay") == "1", aDw);
    }

    /// <summary>時間衰減的扣分：0（剛改過）→ 趨近 <paramref name="iWeight"/>（很久沒動）。半衰期 ≤ 0 ＝ 不扣。純函式，給 selftest 釘住。</summary>
    public static double DecayPenalty(double iAgeDays, double iHalfLifeDays, double iWeight)
        => iHalfLifeDays <= 0 || iWeight <= 0 ? 0 : iWeight * (1 - Math.Pow(2, -Math.Max(0, iAgeDays) / iHalfLifeDays));

    static SearchOutcome? DoSearch(Ctx c, List<string> iTargets, string iQuery, int iTopK, SearchOpts iOpt, bool iAuto, out SCP_CmdResult? oFail)
    {
        oFail = null;
        var sw = Stopwatch.StartNew();
        var aIdx = new List<KbIndex>();
        var aRe = new List<string>(); var aNotes = new List<string>();
        foreach (string n in iTargets)
        {
            if (!c.All.TryGetValue(n, out KbTarget? t)) { oFail = SCP_CmdResult.Fail(2, $"✗ 不認得的 target '{n}'"); return null; }
            KbSources s = KbTargets.Resolve(t, c.Roots);
            KbIndex? ix = KbIndex.Load(c.Roots.DataRoot, n, out string aWhy);
            bool aNeed = ix == null || ix.StaleAgainst(s).Any || ix.Meta.Chunker != KbChunker.Version;
            if (aNeed && iAuto)
            {
                KbIndex.Build(s, c.Roots.DataRoot, c.Roots.ProjectRoot, x => EmbedVia(c.Car, x), Log);
                ix = KbIndex.Load(c.Roots.DataRoot, n, out aWhy);
                aRe.Add(n);
            }
            else if (aNeed && ix != null) aNotes.Add($"{n} 的索引過期了（auto_reindex=0 ⇒ 照舊的查）");
            if (ix == null) { aNotes.Add($"{n}：索引{aWhy}（這個 target 沒查到）"); continue; }
            aIdx.Add(ix);
        }
        var qsw = Stopwatch.StartNew();
        KbEmbedding q = c.Car.Embed(new[] { iQuery }, iSparse: iOpt.UsesSparse);
        var aPool = aIdx.SelectMany(ix => ix.Score(q.Dense[0], iOpt.UsesSparse ? q.Sparse[0] : null, iOpt.UsesSparse ? iOpt.SparseWeight : 0));
        List<KbHit> aHits;
        if (iOpt.Rerank)
        {
            // 先用 hybrid 取候選池（寬一點，召回要夠），再交給重排模型；最後的順序＝重排分數（0..1，跟前一段的內積不同尺度）
            List<KbHit> aCand = aPool.OrderByDescending(h => h.Score).Take(Math.Max(RerankPool, iTopK)).ToList();
            double[] aRr = aCand.Count == 0 ? Array.Empty<double>() : c.Car.Rerank(iQuery, aCand.Select(h => h.Chunk.Text).ToList());
            aHits = aCand.Select((h, i) => h with { Score = aRr[i] }).ToList();
        }
        else aHits = aPool.ToList();
        if (iOpt.Decay)
        {
            // 每個 target 的「來源檔 → mtime」查表先建好（不在每一塊裡線性找：評估一次跑幾十題、每題幾千塊）
            var aMtime = aIdx.ToDictionary(ix => ix.Meta.Target,
                ix => ix.Meta.Sources.GroupBy(s => s.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().MTimeTicks, StringComparer.OrdinalIgnoreCase));
            aHits = aHits.Select(h => h with { Score = h.Score - DecayOf(c, aMtime, h, iOpt.DecayWeight) }).ToList();
        }
        aHits = aHits.OrderByDescending(h => h.Score).Take(iTopK).ToList();
        return new SearchOutcome(aHits, qsw.ElapsedMilliseconds, sw.ElapsedMilliseconds, aRe, aNotes, aIdx.Sum(ix => ix.Meta.Chunks.Count));
    }

    /// <summary>這一塊該被扣多少分（來源檔的 mtime 當年齡；target 沒設半衰期 ⇒ 0，文件類不衰減）。</summary>
    static double DecayOf(Ctx c, Dictionary<string, Dictionary<string, long>> iMtime, KbHit iHit, double iWeight)
    {
        if (!c.All.TryGetValue(iHit.Target, out KbTarget? t) || t.HalfLifeDays <= 0) return 0;
        if (!iMtime.TryGetValue(iHit.Target, out var aFiles) || !aFiles.TryGetValue(iHit.Chunk.File, out long aTicks)) return 0;
        double aAge = (DateTime.UtcNow - new DateTime(aTicks, DateTimeKind.Utc)).TotalDays;
        return DecayPenalty(aAge, t.HalfLifeDays, iWeight);
    }

    static SCP_CmdResult Search(Ctx c, SCP_CmdArgs iArgs)
    {
        string aQuery = iArgs.Get("query");
        if (aQuery.Trim().Length == 0) return SCP_CmdResult.Fail(2, "✗ op=search 要給 `query=`（一句話）");
        string aArg = iArgs.Get("target");
        List<string> aTargets = KbTargets.Parse(aArg.Length == 0 ? DefaultSearchTargets : aArg, c.All);
        int aTopK = int.TryParse(iArgs.Get("topk"), out int k) && k > 0 ? k : 5;
        SearchOpts aOpt = ParseOpts(iArgs);
        if (aOpt.Mode == "compare") return SCP_CmdResult.Fail(2, "✗ mode=compare 只給 op=eval（一次比三種排序）；search 請給 dense／hybrid／rerank");
        SCP_CmdResult? aNotReady = Ready(c, aOpt.Rerank);
        if (aNotReady != null) return aNotReady;
        SearchOutcome? o = DoSearch(c, aTargets, aQuery, aTopK, aOpt, iArgs.Get("auto_reindex") != "0", out SCP_CmdResult? aFail);
        if (o == null) return aFail!;

        var r = new SCP_CmdResult();
        if (iArgs.Get("format") == "json")
        {
            // 欄位對齊舊 knowledge_base.py 的 search JSON（score／target／id／file／rel／line／preview），呼叫端不必改讀法
            r.Lines.Add(JsonSerializer.Serialize(new
            {
                ok = true, targets = aTargets, query = aQuery, mode = aOpt.Mode, decay = aOpt.Decay,
                latency_ms = o.TotalMs, query_ms = o.QueryMs, auto_reindexed = o.Reindexed, notes = o.Notes,
                searched_chunks = o.SearchedChunks, note = string.Join("；", o.Notes),   // 舊版的兩個頂層欄位（呼叫端不必改讀法）
                hits = o.Hits.Select(h => new
                {
                    score = Math.Round(h.Score, 4), target = h.Target, id = h.Chunk.Id, file = h.Chunk.File.Replace('\\', '/'),
                    rel = h.Chunk.Rel, line = h.Chunk.Line, heading = h.Chunk.Heading, preview = Preview(h.Chunk.Text, 200),
                }),
            }, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return r;
        }
        r.Lines.Add($"# 🔍 {aQuery}　（{string.Join(",", aTargets)}；{aOpt.Label}；嵌入 {o.QueryMs} ms／全程 {o.TotalMs} ms）");
        foreach (string n in o.Notes) r.Lines.Add("⚠ " + n);
        if (o.Reindexed.Count > 0) r.Lines.Add("· 先重建了過期的：" + string.Join("、", o.Reindexed));
        foreach (KbHit h in o.Hits)
        {
            r.Lines.Add($"  [{h.Score:0.0000}] ({h.Target}) {h.Chunk.Id}");
            r.Lines.Add($"     📄 {h.Chunk.Rel}:{h.Chunk.Line}" + (h.Chunk.Heading.Length > 0 ? $"　§ {h.Chunk.Heading}" : ""));
            r.Lines.Add("     " + Preview(h.Chunk.Text, 200));
        }
        if (o.Hits.Count == 0) r.Lines.Add("（沒有結果 —— 索引是空的，不是「沒有相關的東西」：看上面的 ⚠）");
        r.AddValue("hits", o.Hits.Count.ToString(CultureInfo.InvariantCulture));
        r.AddValue("latency_ms", o.TotalMs.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    static string Preview(string s, int n)
    {
        s = s.Replace("\r", "").Replace('\n', ' ');
        return s.Length <= n ? s : s.Substring(0, n) + "…";
    }

    // ── eval ────────────────────────────────────────────────────────

    // 題庫 `SenateData/config/kb_eval.json`：每題一句話＋該找到的檔（lessons 另附 expect_text 判是哪一筆）。
    // 算 recall@5 與 MRR@10，逐 target 列；⚠ 先把用到的 target 重建到最新 —— 拿過期索引評出來的分數不是檢索品質。
    sealed record EvalRow(string Id, string Target, int? Rank, long Ms);

    sealed record EvalRun(SearchOpts Opt, List<EvalRow> Rows, List<(string Id, string Target, string Why)> Skipped);

    static SCP_CmdResult Eval(Ctx c, SCP_CmdArgs iArgs)
    {
        string aPath = Path.Combine(SenatePaths.ConfigDir(c.Repo), "kb_eval.json");
        if (!File.Exists(aPath)) return SCP_CmdResult.Fail(1, "✗ 找不到評估題庫：" + aPath);
        using JsonDocument d = JsonDocument.Parse(File.ReadAllText(aPath));
        SearchOpts aBase = ParseOpts(iArgs);
        bool aCompare = aBase.Mode == "compare";
        // compare ＝ 三種排序各跑「不衰減／衰減」；單一模式照給的跑
        var aOpts = aCompare
            ? new[] { "dense", "hybrid", "rerank" }.SelectMany(m => new[] { false, true }.Select(dc => aBase with { Mode = m, Decay = dc })).ToList()
            : new List<SearchOpts> { aBase };
        SCP_CmdResult? aNotReady = Ready(c, aOpts.Any(o => o.Rerank));
        if (aNotReady != null) return aNotReady;

        var aRuns = new List<EvalRun>();
        foreach (SearchOpts o in aOpts)
        {
            EvalRun? run = RunEval(c, d.RootElement.GetProperty("items"), o, out SCP_CmdResult? aFail);
            if (run == null) return aFail!;
            aRuns.Add(run);
        }

        var r = new SCP_CmdResult();
        if (!aCompare) { EvalReport(r, aRuns[0]); return r; }

        // ── compare：一張表比三種排序（各自帶與不帶衰減）＋ 反向對照題（core-05）逐一列名次 ──
        EvalRun aFirst = aRuns[0];
        r.Lines.Add($"# 📏 知識庫評估・排序比較（算分 {aFirst.Rows.Count} 題，跳過 {aFirst.Skipped.Count} 題；每題一句話，名次是前 10 裡預期檔第一次出現的位置）");
        if (aFirst.Skipped.Count > 0)
            r.Lines.Add("⚠ 跳過：" + string.Join("、", aFirst.Skipped.Select(x => x.Id)) + "（本專案答不出來，不進任何一列的分母）");
        r.Lines.Add("");
        r.Lines.Add("| 排序 | recall@5 | MRR@10 | 查詢中位數 ms | 未命中（前 10 沒有） |");
        r.Lines.Add("|---|---|---|---|---|");
        foreach (EvalRun run in aRuns)
        {
            int aOk = run.Rows.Count(x => x.Rank is <= 5);
            double aMrr = run.Rows.Sum(x => x.Rank is int k ? 1.0 / k : 0) / Math.Max(1, run.Rows.Count);
            r.Lines.Add($"| {run.Opt.Label} | {aOk}／{run.Rows.Count} | {aMrr:0.000} | {Median(run.Rows.Select(x => x.Ms))} | {string.Join("、", run.Rows.Where(x => x.Rank == null).Select(x => x.Id))} |");
            r.AddValue($"{run.Opt.Label}_recall5", $"{aOk}/{run.Rows.Count}");
            r.AddValue($"{run.Opt.Label}_mrr10", aMrr.ToString("0.000", CultureInfo.InvariantCulture));
        }
        r.Lines.Add("");
        r.Lines.Add("反向對照（已知 dense 救不回的題）逐排序的名次：");
        foreach (string aId in new[] { "core-05", "core-04" })
            if (aRuns.Any(x => x.Rows.Any(y => y.Id == aId)))
                r.Lines.Add($"- {aId}：" + string.Join("　", aRuns.Select(x => $"{x.Opt.Label}＝{(x.Rows.FirstOrDefault(y => y.Id == aId)?.Rank?.ToString() ?? "—")}")));
        r.Lines.Add("");
        r.Lines.Add("逐 target（recall@5／MRR）：");
        foreach (var g in aFirst.Rows.GroupBy(x => x.Target))
            r.Lines.Add($"- {g.Key}（{g.Count()} 題）：" + string.Join("　", aRuns.Select(x =>
            {
                var rows = x.Rows.Where(y => y.Target == g.Key).ToList();
                return $"{x.Opt.Label} {rows.Count(y => y.Rank is <= 5)}／{rows.Count}・{rows.Sum(y => y.Rank is int k ? 1.0 / k : 0) / Math.Max(1, rows.Count):0.00}";
            })));
        return r;
    }

    /// <summary>跑一遍題庫（一種排序設定）。回 null ＝ 檢索本身失敗（原因在 oFail）。</summary>
    static EvalRun? RunEval(Ctx c, JsonElement iItems, SearchOpts iOpt, out SCP_CmdResult? oFail)
    {
        oFail = null;
        var aRows = new List<EvalRow>();
        // 🔴 本專案答不出來的題（預期檔／預期那段不在這個專案的來源裡）**不進分母**，另外列：
        //    題庫綁著某個專案的文件，換專案跑時那些題一定「沒排上」—— 而那跟「排序變差」在分數上同形（TASK-0382，Bar 實測 19／32）。
        var aSkipped = new List<(string Id, string Target, string Why)>();
        var aSources = new Dictionary<string, KbSources>();
        foreach (JsonElement it in iItems.EnumerateArray())
        {
            string id = it.GetProperty("id").GetString()!, tgt = it.GetProperty("target").GetString()!, q = it.GetProperty("q").GetString()!;
            var aExpect = it.GetProperty("expect").EnumerateArray().Select(x => x.GetString()!.Replace('\\', '/').ToLowerInvariant()).ToList();
            string? aText = it.TryGetProperty("expect_text", out var et) ? et.GetString() : null;
            if (!c.All.TryGetValue(tgt, out KbTarget? aTgt)) { aSkipped.Add((id, tgt, $"題庫的 target '{tgt}' 這份設定裡沒有")); continue; }
            if (!aSources.TryGetValue(tgt, out KbSources? aSrc)) aSources[tgt] = aSrc = KbTargets.Resolve(aTgt, c.Roots);
            string? aWhy = EvalUnanswerable(aSrc, aExpect, aText);
            if (aWhy != null) { aSkipped.Add((id, tgt, aWhy)); continue; }
            SearchOutcome? o = DoSearch(c, new List<string> { tgt }, q, 10, iOpt, true, out oFail);
            if (o == null) return null;
            int? aRank = null;
            for (int i = 0; i < o.Hits.Count; i++)
            {
                string f = o.Hits[i].Chunk.File.Replace('\\', '/').ToLowerInvariant();
                if (!aExpect.Any(e => f.EndsWith(e, StringComparison.Ordinal))) continue;
                if (aText != null && !o.Hits[i].Chunk.Text.Contains(aText, StringComparison.Ordinal)) continue;
                aRank = i + 1; break;
            }
            aRows.Add(new EvalRow(id, tgt, aRank, o.QueryMs));
            Console.Error.WriteLine($"  [{iOpt.Label}] {id}　rank={(aRank?.ToString() ?? "—")}　{o.QueryMs} ms");
        }
        return new EvalRun(iOpt, aRows, aSkipped);
    }

    static void EvalReport(SCP_CmdResult r, EvalRun iRun)
    {
        List<EvalRow> aRows = iRun.Rows;
        r.Lines.Add($"# 📏 知識庫評估（{iRun.Opt.Label}{(iRun.Opt.UsesSparse ? $"，sparse×{iRun.Opt.SparseWeight}" : "")}，算分 {aRows.Count} 題" + (iRun.Skipped.Count > 0 ? $"，跳過 {iRun.Skipped.Count} 題" : "") + "）");
        if (iRun.Skipped.Count > 0)
        {
            r.Lines.Add($"⚠ **跳過 {iRun.Skipped.Count} 題（本專案答不出來，不進下面的分母）**：題庫綁著別的專案的文件時，這些題在這裡一定沒排上，不是排序的問題。");
            foreach (var g in iRun.Skipped.GroupBy(x => x.Why))
                r.Lines.Add($"  · {g.Key}：{string.Join("、", g.Select(x => x.Id))}");
        }
        foreach (var g in aRows.GroupBy(x => x.Target))
            r.Lines.Add($"- {g.Key}：recall@5 {g.Count(x => x.Rank is <= 5)}／{g.Count()}　MRR@10 {g.Sum(x => x.Rank is int k ? 1.0 / k : 0) / g.Count():0.000}");
        int aOk5 = aRows.Count(x => x.Rank is <= 5);
        double aMrr = aRows.Sum(x => x.Rank is int k ? 1.0 / k : 0) / Math.Max(1, aRows.Count);
        r.Lines.Add($"**合計：recall@5 {aOk5}／{aRows.Count}　MRR@10 {aMrr:0.000}　查詢中位數 {Median(aRows.Select(x => x.Ms))} ms**");
        r.Lines.Add("未命中（前 10 沒有）：" + string.Join("、", aRows.Where(x => x.Rank == null).Select(x => x.Id)));
        r.AddValue("recall_at_5", $"{aOk5}/{aRows.Count}");
        r.AddValue("mrr_at_10", aMrr.ToString("0.000", CultureInfo.InvariantCulture));
        r.AddValue("skipped", iRun.Skipped.Count.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 這題在目前專案裡**有沒有可能答對**：回 null ＝ 有；否則回原因（會被跳過、不進 recall／MRR 的分母）。
    /// <para>判準跟命中判定同一把：預期檔用「檔尾比對」（同 Eval 的 EndsWith），預期那段用**切塊後的 Text** 找
    /// （⛔ 不是檔案原文 —— jsonl 的跳脫字元、標題路徑都會讓原文比對說謊）。</para>
    /// </summary>
    public static string? EvalUnanswerable(KbSources iSrc, IReadOnlyList<string> iExpectNorm, string? iText)
    {
        var aFiles = iSrc.Files.Where(f =>
        {
            string n = f.Replace('\\', '/').ToLowerInvariant();
            return iExpectNorm.Any(e => n.EndsWith(e, StringComparison.Ordinal));
        }).ToList();
        if (aFiles.Count == 0) return "預期檔不在本專案這個 target 的來源裡";
        if (iText == null) return null;
        foreach (string f in aFiles)
        {
            string aBody;
            try { aBody = File.ReadAllText(f); } catch (IOException) { continue; }
            if (KbChunker.Chunk(iSrc.Target.Kind, f, aBody).Any(ch => ch.Text.Contains(iText, StringComparison.Ordinal))) return null;
        }
        return "預期檔在，但沒有預期的那一段";
    }

    static long Median(IEnumerable<long> xs) { var a = xs.OrderBy(x => x).ToList(); return a.Count == 0 ? 0 : a[a.Count / 2]; }

    // ── sidecar ─────────────────────────────────────────────────────

    static SCP_CmdResult Sidecar(Ctx c, SCP_CmdArgs iArgs)
    {
        var r = new SCP_CmdResult();
        switch (iArgs.Get("action"))
        {
            case "start":
                SCP_CmdResult? aNotReady = Ready(c);
                if (aNotReady != null) return aNotReady;
                break;
            case "stop":
                r.Lines.Add(c.Car.Stop() ? "✓ 已請常駐嵌入程序關閉" : "· 它本來就沒在跑");
                return r;
        }
        KbSidecar.Health? h = c.Car.Probe();
        r.Lines.Add(h == null ? "· 常駐嵌入程序沒在跑" : $"✓ 在跑：pid {h.Pid}，port {h.Port}，{h.Device}，載入花了 {h.LoadedMs / 1000.0:0.0} 秒，已嵌 {h.Served} 句");
        r.Lines.Add("· log：" + c.Car.LogPath.Replace('\\', '/'));
        r.AddValue("sidecar", h == null ? "stopped" : "running");
        return r;
    }
}
