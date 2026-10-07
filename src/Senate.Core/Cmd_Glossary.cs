// 區塊職責：`senate cmd glossary` —— 新詞辭典的唯一入口（register／lookup／detect／attach／list），**不需要 Unity Editor**。
// 物理意義：TASK-0313。Tim 2026-09-27「glossary 功能遷移到 Senate CLI」、2026-09-28「詞典根留在 senate.local.json，
//          Unity 端相關功能也遷到 Senate CLI」「Unity 端應該不用呼叫到 Glossary」。
//          ⇒ 詞典根只有 Senate 讀（`SCP_PathId.GlossaryRoot`）；邏輯在 SCP_Core `SCP_Glossary`（唯一一份）。
//          Editor 的 `ucmd run Glossary` 已退場；Editor 發的文由寫入端 `tavern-write` 補附註。
// 數值影響：register 寫一個 .md（詞典根底下）；其餘純讀。attach 帶 `out` 時把結果**逐位元組**寫進那個檔
//          （給要拿回原文的呼叫端 —— ⛔ 不要從 stdout 拼回來：換行與行首縮排在那條路上不保證逐字）。
#nullable enable
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Glossary;

namespace Senate.Core;

public sealed class Cmd_Glossary : SCP_Cmd
{
    public override string Name => "glossary";
    public override string Category => SCP_CmdCategory.Reading;

    public override string Summary => "新詞辭典：register／lookup／detect／attach／list —— **不需要 Unity Editor**";

    public override string Details =>
        "詞典根＝PathsPage 的 `glossaryRoot`（senate.local.json；auto ＝ <Senate 專案根>/Glossary）。\n"
        + "  register：term／slug／one_line 必填；aliases 逗號分隔；已存在要 overwrite=true（寫回原位置，created_at 不變）。\n"
        + "            created_by 沒給 ⇒ 用 persona；兩者都沒有 ⇒ 寫 unknown（⛔ 不編一個名字）。\n"
        + "  lookup：term 可以是詞、slug 或 alias。\n"
        + "  detect／attach：text 必填（長文走 --arg-file）；attach 帶 out=<檔> ⇒ 結果逐位元組寫進去。\n"
        + "  list：可用 category 篩。\n"
        + "⚠ 酒館發文的自動附註**不必呼叫本支**：寫入端 `tavern-write` 會補（已附過的不重複）。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("glossary --arg op=register --arg term=\"basecamp 大小姐\" --arg slug=basecamp "
                               + "--arg category=persona --arg one_line=<一句話> --arg-file body=<檔> --arg persona=basecamp");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>
    {
        new SCP_CmdArgSpec("op", "操作", iRequired: true,
                           iChoices: new[] { "register", "lookup", "detect", "attach", "list" }),
        new SCP_CmdArgSpec("project", "哪個專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略"),
        new SCP_CmdArgSpec("term", "register：詞本身／lookup：詞、slug 或 alias"),
        new SCP_CmdArgSpec("slug", "register：檔名 slug（`<slug>.md`）"),
        new SCP_CmdArgSpec("aliases", "register：別名，逗號分隔"),
        new SCP_CmdArgSpec("category", "register：分類；list：篩選（register 預設 concept）"),
        new SCP_CmdArgSpec("one_line", "register：一句話解說（附註區塊顯示用，建議 < 80 字）"),
        new SCP_CmdArgSpec("body", "register：完整 markdown（長文走 --arg-file）"),
        new SCP_CmdArgSpec("created_by", "register：作者（沒給 ⇒ 用 persona）"),
        new SCP_CmdArgSpec("persona", "register：呼叫者（created_by 沒給時當作者）"),
        new SCP_CmdArgSpec("overwrite", "register：已存在時覆寫", iDefault: "false",
                           iChoices: new[] { "true", "false", "1", "0" }),
        new SCP_CmdArgSpec("text", "detect／attach：要掃的文字（長文走 --arg-file）"),
        new SCP_CmdArgSpec("cap", "detect／attach：命中上限（detect 預設 10、attach 預設 5）"),
        new SCP_CmdArgSpec("out", "attach：把結果逐位元組寫進這個檔（UTF-8 無 BOM）"),
    };

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        if (UnityDelegateCmd.ConfigProvider == null)
            return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源（UnityDelegateCmd.ConfigProvider）—— 程式錯誤，不是用法錯");
        (SenateConfig? aConfig, _) = UnityDelegateCmd.ConfigProvider();
        // 顯示基準＝Senate 專案根（詞典是 Senate 的 submodule，TASK-0390）。🩸 原本要一個啟用的 Unity 專案才肯動。
        string aProjectRoot = SenatePathBinding.HostRepoRoot;

        // 詞典根：唯一真相源是 senate.local.json 那一格。解不出來 ⇒ ⛔ 不猜一個預設（register 會寫錯樹、讀取會安靜地回 0 命中）。
        string? aRoot = SenatePathBinding.ResolveGlossaryRoot(aConfig, out string? aRootErr);
        if (aRoot == null)
            return SCP_CmdResult.Fail(2, $"✗ 詞典根解不出來：{aRootErr}", "  到 PathsPage 設 `glossaryRoot`（或留 auto）。");
        string aPrefix = SCP_Glossary.DisplayPrefix(aProjectRoot, aRoot);

        string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
        SCP_CmdResult aResult = aOp switch
        {
            "register" => Register(iArgs, aRoot, aPrefix),
            "lookup" => Lookup(iArgs, aRoot, aPrefix),
            "detect" => Detect(iArgs, aRoot, aPrefix),
            "attach" => Attach(iArgs, aRoot, aPrefix),
            _ => List(iArgs, aRoot),
        };
        aResult.AddValue("glossary_root", aRoot.Replace('\\', '/'));
        return aResult;
    }

    static SCP_CmdResult Register(SCP_CmdArgs iArgs, string iRoot, string iPrefix)
    {
        string aCreatedBy = iArgs.Get("created_by").Trim();
        if (aCreatedBy.Length == 0) aCreatedBy = iArgs.Get("persona").Trim();
        string aOverwrite = iArgs.Get("overwrite");
        string aCategory = iArgs.Get("category").Trim();
        if (aCategory.Length == 0) aCategory = "concept";

        SCP_GlossaryRegisterResult aR = SCP_Glossary.Register(iRoot, iArgs.Get("term").Trim(), iArgs.Get("slug").Trim(),
            iArgs.Get("aliases"), aCategory, iArgs.Get("one_line").Trim(), iArgs.Get("body"), aCreatedBy,
            aOverwrite == "true" || aOverwrite == "1", DateTime.UtcNow);
        if (!aR.Ok)
        {
            var aFail = SCP_CmdResult.Fail(2, "✗ " + aR.Error);
            if (aR.Rel.Length > 0) aFail.AddValue("path", iPrefix + "/" + aR.Rel);
            return aFail;
        }
        var aOut = SCP_CmdResult.Success(
            $"✅ glossary registered: **{iArgs.Get("term").Trim()}** (slug: {iArgs.Get("slug").Trim()})",
            $"- category: {aCategory}",
            $"- aliases: {(aR.Aliases.Count > 0 ? string.Join(", ", aR.Aliases) : "(none)")}",
            $"- path: {iPrefix}/{aR.Rel}",
            $"- created_by: {aR.CreatedBy}" + (aR.Overwrote ? "　（覆寫：created_at 沿用、寫入 updated_at）" : ""));
        aOut.AddValue("path", iPrefix + "/" + aR.Rel);
        aOut.AddValue("overwrote", aR.Overwrote ? "1" : "0");
        aOut.AddValue("created_by", aR.CreatedBy);
        return aOut;
    }

    static SCP_CmdResult Lookup(SCP_CmdArgs iArgs, string iRoot, string iPrefix)
    {
        string aTerm = iArgs.Get("term").Trim();
        if (aTerm.Length == 0) return SCP_CmdResult.Fail(2, "✗ lookup 缺少 term");
        SCP_GlossaryEntry? aHit = SCP_Glossary.Resolve(aTerm, SCP_Glossary.LoadEntries(iRoot));
        if (aHit == null)
            // ⚠ 查無不是失敗（exit 0）—— 但讀數要分得出來：`found=0`
            return SCP_CmdResult.Success($"❌ glossary not found: `{aTerm}` (沒有 term/alias 命中)").AddValue("found", "0");
        return SCP_CmdResult.Success(
                $"✅ glossary hit: **{aHit.Term}**",
                $"- slug: {aHit.Slug}",
                $"- category: {aHit.Category}",
                $"- aliases: {(aHit.Aliases.Count > 0 ? string.Join(", ", aHit.Aliases) : "(none)")}",
                $"- one_line: {aHit.OneLine}",
                $"- path: {iPrefix}/{aHit.Rel}")
            .AddValue("found", "1").AddValue("slug", aHit.Slug).AddValue("path", iPrefix + "/" + aHit.Rel);
    }

    static SCP_CmdResult Detect(SCP_CmdArgs iArgs, string iRoot, string iPrefix)
    {
        string aText = iArgs.Get("text");
        if (aText.Length == 0) return SCP_CmdResult.Fail(2, "✗ detect 缺少 text");
        int aCap = Cap(iArgs, 10);
        List<SCP_GlossaryHit> aHits = SCP_Glossary.DetectHits(aText, SCP_Glossary.LoadEntries(iRoot), aCap);
        var aOut = SCP_CmdResult.Success($"📖 detect 結果 ({aHits.Count} 命中, cap={aCap}):");
        foreach (SCP_GlossaryHit h in aHits)
            aOut.Lines.Add($"- **{h.Entry.Term}** (matched: `{h.Matched}`) → {iPrefix}/{h.Entry.Rel}");
        if (aHits.Count == 0) aOut.Lines.Add("_(無命中)_");
        aOut.AddValue("hits", aHits.Count.ToString());
        return aOut;
    }

    static SCP_CmdResult Attach(SCP_CmdArgs iArgs, string iRoot, string iPrefix)
    {
        string aText = iArgs.Get("text");
        if (aText.Length == 0) return SCP_CmdResult.Fail(2, "✗ attach 缺少 text");
        // op=attach 是「使用者要看附註長什麼樣」⇒ 已含 marker 也重附（同 Editor 版 forceReattach: true）
        string aAttached = SCP_Glossary.AppendRefs(aText, iRoot, iPrefix, Cap(iArgs, SCP_Glossary.AutoAttachCap), iForce: true);
        string aOutPath = iArgs.Get("out").Trim();
        var aOut = SCP_CmdResult.Success();
        if (aOutPath.Length > 0)
        {
            File.WriteAllText(aOutPath, aAttached, new UTF8Encoding(false));
            aOut.Lines.Add($"✓ 結果已寫入：{aOutPath}");
            aOut.AddValue("out", aOutPath);
        }
        else
        {
            foreach (string aLine in aAttached.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) aOut.Lines.Add(aLine);
        }
        aOut.AddValue("attached", aAttached == aText ? "0" : "1");
        return aOut;
    }

    static SCP_CmdResult List(SCP_CmdArgs iArgs, string iRoot)
    {
        string aCategory = iArgs.Get("category").Trim();
        List<SCP_GlossaryEntry> aEntries = SCP_Glossary.LoadEntries(iRoot);
        if (aCategory.Length > 0) aEntries = aEntries.Where(e => e.Category == aCategory).ToList();
        var aOut = SCP_CmdResult.Success(
            $"📚 glossary list ({aEntries.Count} entries{(aCategory.Length == 0 ? "" : ", category=" + aCategory)}):",
            "",
            "| Term | Category | Aliases | One-line |",
            "|---|---|---|---|");
        foreach (SCP_GlossaryEntry e in aEntries.OrderBy(x => x.Category, StringComparer.Ordinal).ThenBy(x => x.Slug, StringComparer.Ordinal))
            aOut.Lines.Add($"| `{e.Slug}` **{e.Term}** | {e.Category} | {(e.Aliases.Count > 0 ? string.Join(", ", e.Aliases) : "—")} | {e.OneLine} |");
        aOut.AddValue("entries", aEntries.Count.ToString());
        return aOut;
    }

    static int Cap(SCP_CmdArgs iArgs, int iDefault)
        => int.TryParse(iArgs.Get("cap"), out int c) ? Math.Max(1, c) : iDefault;
}
