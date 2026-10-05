// 區塊職責：純邏輯 markdown parser —— 把 raw .md 字串切成 block list（frontmatter／heading／paragraph／
//          code fence／bullet／quote／hr／empty／table／mermaid），給 MarkdownViewerPage 渲染。
// 物理意義：移植自 Unity 端 `UCL_MarkdownParser`（UCL_Core EditorCore）的**解析那一半**；
//          反向輸出（Render）沒搬 —— Senate 這側沒有「結構化建檔再寫成 .md」的呼叫端，搬了是死碼。
//          frontmatter `related:` 的解析也一起收在這裡（Unity 端放在 viewer 頁裡）：它同樣是純字串處理。
// 數值影響：line-based 掃描；純函式、零 IO。
// ⚠ 支援範圍與 Unity 版相同：不支援 setext heading、巢狀清單、HTML 區塊、腳註、複雜 mermaid。
#nullable enable
using System.Text;
using System.Text.RegularExpressions;

namespace Senate.Cli.Pages;

public enum MdBlockType { Heading, Paragraph, CodeFence, Bullet, Quote, HorizontalRule, Empty, Table, Mermaid }

/// <summary>一個 block。欄位有沒有意義看 <see cref="Type"/>（同 Unity 版 `UCL_MdBlock`）。</summary>
public sealed class MdBlock
{
    public MdBlockType Type;
    public int HeadingLevel;              // Heading：1~6
    public string Text = "";              // Heading／Paragraph／Bullet／Quote
    public string CodeLang = "";          // CodeFence／Mermaid
    public string CodeBody = "";          // CodeFence；Mermaid 也留原文當退路
    public List<string[]> TableRows = new();   // Table：第 0 列是 header；分隔列已剃除
    public MdMermaidGraph? Graph;         // Mermaid
}

public sealed class MdMermaidNode { public string Id = "", Label = "", Shape = "rect"; }
public sealed class MdMermaidEdge { public string From = "", To = ""; public string? Label; }

public sealed class MdMermaidGraph
{
    public string Direction = "LR";
    public Dictionary<string, MdMermaidNode> Nodes = new();
    public List<MdMermaidEdge> Edges = new();
}

/// <summary>frontmatter `related:` 的一筆：`  - <url> | <label> [| <desc>]`。</summary>
public sealed class MdRelatedDoc { public string Url = "", Label = ""; public string? Description; }

public sealed class MdDocument
{
    /// <summary>frontmatter 原文（不含包夾的 ---）；沒有 ⇒ null。</summary>
    public string? Frontmatter;
    public List<MdBlock> Blocks = new();
}

public static class MarkdownDoc
{
    public static MdDocument Parse(string? iContent)
    {
        var doc = new MdDocument();
        if (string.IsNullOrEmpty(iContent)) return doc;

        string[] lines = iContent.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int idx = 0;

        // frontmatter：第一行 "---" 起、到下一個 "---" 為止；沒收尾 ⇒ 當一般內容
        if (lines.Length > 0 && lines[0].Trim() == "---")
        {
            var fm = new StringBuilder();
            int j = 1;
            bool closed = false;
            for (; j < lines.Length; j++)
            {
                if (lines[j].Trim() == "---") { closed = true; j++; break; }
                fm.Append(lines[j]).Append('\n');
            }
            if (closed) { doc.Frontmatter = fm.ToString().TrimEnd(); idx = j; }
        }

        var blocks = doc.Blocks;
        var para = new StringBuilder();
        for (int i = idx; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            // code fence：到下一個 ``` 為止，中間不解析；lang == mermaid 另外 parse
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                Flush(para, blocks);
                string lang = trimmed.Length > 3 ? trimmed.Substring(3).Trim() : "";
                var body = new StringBuilder();
                for (i++; i < lines.Length; i++)
                {
                    if (lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                    body.Append(lines[i]).Append('\n');
                }
                string code = body.ToString().TrimEnd('\n');
                bool mermaid = string.Equals(lang, "mermaid", StringComparison.OrdinalIgnoreCase);
                blocks.Add(new MdBlock
                {
                    Type = mermaid ? MdBlockType.Mermaid : MdBlockType.CodeFence,
                    CodeLang = lang, CodeBody = code,
                    Graph = mermaid ? ParseMermaid(code) : null,
                });
                continue;
            }

            if (TryHeading(line, out int lv, out string ht))
            {
                Flush(para, blocks);
                blocks.Add(new MdBlock { Type = MdBlockType.Heading, HeadingLevel = lv, Text = ht });
                continue;
            }

            if (s_RxRule.IsMatch(line))
            {
                Flush(para, blocks);
                blocks.Add(new MdBlock { Type = MdBlockType.HorizontalRule });
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                Flush(para, blocks);
                blocks.Add(new MdBlock { Type = MdBlockType.Empty });
                continue;
            }

            if (IsTableLine(line) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                Flush(para, blocks);
                var rows = new List<string[]> { SplitRow(line) };
                i += 2;
                while (i < lines.Length && IsTableLine(lines[i])) rows.Add(SplitRow(lines[i++]));
                i--;   // 抵銷外層 i++：非表格那行下一輪重新處理
                blocks.Add(new MdBlock { Type = MdBlockType.Table, TableRows = rows });
                continue;
            }

            Match bm = s_RxBullet.Match(line);
            if (bm.Success)
            {
                Flush(para, blocks);
                string marker = bm.Groups[2].Value;
                string display = marker.Length == 1 ? "•" : marker;   // - * + 一律畫成 •；數字保留
                blocks.Add(new MdBlock { Type = MdBlockType.Bullet, Text = bm.Groups[1].Value + display + " " + bm.Groups[3].Value });
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                Flush(para, blocks);
                blocks.Add(new MdBlock { Type = MdBlockType.Quote, Text = trimmed.Length > 2 ? trimmed.Substring(2) : "" });
                continue;
            }

            para.Append(line).Append('\n');
        }
        Flush(para, blocks);
        return doc;
    }

    static readonly Regex s_RxRule = new(@"^\s*(-{3,}|\*{3,}|_{3,})\s*$", RegexOptions.Compiled);
    static readonly Regex s_RxBullet = new(@"^(\s*)([-*+]|\d+\.)\s+(.*)$", RegexOptions.Compiled);

    static void Flush(StringBuilder ioBuf, List<MdBlock> oBlocks)
    {
        if (ioBuf.Length == 0) return;
        oBlocks.Add(new MdBlock { Type = MdBlockType.Paragraph, Text = ioBuf.ToString().TrimEnd('\n', ' ', '\t') });
        ioBuf.Clear();
    }

    static bool TryHeading(string iLine, out int oLevel, out string oText)
    {
        oLevel = 0; oText = "";
        int n = 0;
        while (n < iLine.Length && iLine[n] == '#') n++;
        if (n == 0 || n > 6 || n >= iLine.Length || iLine[n] != ' ') return false;
        oLevel = n;
        oText = iLine.Substring(n + 1).Trim();
        return true;
    }

    static bool IsTableLine(string iLine)
    {
        string t = iLine.Trim();
        if (t.Length < 3 || !t.StartsWith('|') || !t.EndsWith('|')) return false;
        int count = 0;
        foreach (char c in t) if (c == '|') count++;
        return count >= 3;
    }

    static bool IsTableSeparator(string iLine)
    {
        string t = iLine.Trim();
        if (t.Length < 3 || !t.StartsWith('|') || !t.EndsWith('|')) return false;
        bool dash = false;
        foreach (char c in t.AsSpan(1, t.Length - 2))
        {
            if (c == '-') dash = true;
            else if (c != '|' && c != ':' && c != ' ') return false;
        }
        return dash;
    }

    static string[] SplitRow(string iLine)
    {
        string t = iLine.Trim();
        if (t.StartsWith('|')) t = t.Substring(1);
        if (t.EndsWith('|')) t = t.Substring(0, t.Length - 1);
        string[] cells = t.Split('|');
        for (int k = 0; k < cells.Length; k++) cells[k] = cells[k].Trim();
        return cells;
    }

    // ── mermaid（簡化版，同 Unity 版：graph/flowchart 方向＋三種 shape＋-->|label|）──────

    static readonly Regex s_RxDir = new(@"^(?:graph|flowchart)\s+(\w+)", RegexOptions.Compiled);
    static readonly Regex s_RxEdge = new(
        @"(\w+)(?:\[([^\]]+)\]|\(([^)]+)\)|\{([^}]+)\})?" +
        @"\s*-->\s*" +
        @"(?:\|([^|]+)\|\s*)?" +
        @"(\w+)(?:\[([^\]]+)\]|\(([^)]+)\)|\{([^}]+)\})?",
        RegexOptions.Compiled);

    public static MdMermaidGraph ParseMermaid(string iBody)
    {
        var g = new MdMermaidGraph();
        foreach (string raw in iBody.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("%%", StringComparison.Ordinal)) continue;
            Match dm = s_RxDir.Match(line);
            if (dm.Success) { g.Direction = dm.Groups[1].Value; continue; }
            for (Match m = s_RxEdge.Match(line); m.Success; m = m.NextMatch())
            {
                EnsureNode(g, m.Groups[1].Value, m, 2);
                EnsureNode(g, m.Groups[6].Value, m, 7);
                g.Edges.Add(new MdMermaidEdge
                {
                    From = m.Groups[1].Value, To = m.Groups[6].Value,
                    Label = m.Groups[5].Success ? m.Groups[5].Value.Trim() : null,
                });
            }
        }
        return g;
    }

    /// <summary>登記或補完節點：shape 群組依序是 [rect]／(round)／{diamond}。已有的 label／shape 不被 bare ref 洗掉。</summary>
    static void EnsureNode(MdMermaidGraph g, string iId, Match m, int iFirstShapeGroup)
    {
        if (iId.Length == 0) return;
        string? label = null, shape = null;
        string[] shapes = { "rect", "round", "diamond" };
        for (int k = 0; k < 3; k++)
            if (m.Groups[iFirstShapeGroup + k].Success) { label = m.Groups[iFirstShapeGroup + k].Value; shape = shapes[k]; break; }
        if (!g.Nodes.TryGetValue(iId, out MdMermaidNode? n))
        {
            g.Nodes[iId] = new MdMermaidNode { Id = iId, Label = label ?? iId, Shape = shape ?? "rect" };
            return;
        }
        if (!string.IsNullOrEmpty(label)) n.Label = label;
        if (!string.IsNullOrEmpty(shape)) n.Shape = shape;
    }

    // ── frontmatter related: ──────────────────────────────────

    /// <summary>
    /// 解析 frontmatter 的 `related:` 區塊：`related:` 之後連續的 `- <url> | <label> [| <desc>]`。
    /// 缺欄位的那一行**記進 <paramref name="oSkipped"/>**（Unity 版丟 Debug.LogWarning；這裡沒有 logger，丟了就是沒讀數）。
    /// </summary>
    public static List<MdRelatedDoc> ParseRelated(string? iFrontmatter, List<string> oSkipped)
    {
        var list = new List<MdRelatedDoc>();
        if (string.IsNullOrEmpty(iFrontmatter)) return list;
        string[] lines = iFrontmatter.Split('\n');
        int i = 0;
        while (i < lines.Length && !lines[i].TrimEnd().StartsWith("related:", StringComparison.Ordinal)) i++;
        for (i++; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd('\r');
            int dash = line.IndexOf("- ", StringComparison.Ordinal);
            if (dash < 0 || line.Substring(0, dash).Trim().Length > 0) break;   // 下一個 frontmatter key
            string content = line.Substring(dash + 2).Trim();
            if (content.Length == 0) continue;
            string[] parts = content.Split('|', 3);
            if (parts.Length < 2) { oSkipped.Add(content); continue; }
            string url = parts[0].Trim();
            if (url.Length >= 2 && url[0] == '<' && url[^1] == '>') url = url.Substring(1, url.Length - 2).Trim();   // autolink 風格 <repo:...>
            var d = new MdRelatedDoc { Url = url, Label = parts[1].Trim(), Description = parts.Length >= 3 ? parts[2].Trim() : null };
            if (d.Url.Length == 0 || d.Label.Length == 0) { oSkipped.Add(content); continue; }
            list.Add(d);
        }
        return list;
    }

    // ── inline：Senate 的兩個 renderer 都不吃 rich-text ⇒ 把標記**拿掉**而不是換成 tag ──────

    static readonly Regex s_RxImage = new(@"!\[([^\]]*)\]\(([^)]+)\)", RegexOptions.Compiled);
    static readonly Regex s_RxBold = new(@"\*\*([^*\n]+)\*\*", RegexOptions.Compiled);
    static readonly Regex s_RxItalic = new(@"(?<!\*)\*([^*\n]+)\*(?!\*)", RegexOptions.Compiled);
    static readonly Regex s_RxLink = new(@"\[([^\]]+)\]\(([^)]+)\)", RegexOptions.Compiled);

    /// <summary>
    /// inline 標記 → 純文字：圖 → `[圖：alt]`、粗體／斜體 → 拿掉星號、連結 → 只留文字；**反引號裡的不動**。
    /// <para>⚠ 與 Unity 版不同：不認 `_斜體_` —— 純文字顯示時它最常咬到的是 `snake_case` 識別字，
    /// 拿掉底線會把名字改掉，而且看起來像原文就長那樣。</para>
    /// </summary>
    public static string Inline(string? iText)
    {
        if (string.IsNullOrEmpty(iText)) return "";
        // 以反引號切段：奇數段是 code，原樣保留（含反引號，讓人看得出那是 code）
        string[] parts = iText.Split('`');
        if (parts.Length % 2 == 0) return Plain(iText);   // 反引號沒成對 ⇒ 不切，整段當一般文字
        var sb = new StringBuilder();
        for (int k = 0; k < parts.Length; k++)
        {
            if (k % 2 == 1) sb.Append('`').Append(parts[k]).Append('`');
            else sb.Append(Plain(parts[k]));
        }
        return sb.ToString();
    }

    static string Plain(string s)
    {
        s = s_RxImage.Replace(s, m => "[圖：" + m.Groups[1].Value + "]");
        s = s_RxBold.Replace(s, m => m.Groups[1].Value);
        s = s_RxItalic.Replace(s, m => m.Groups[1].Value);
        s = s_RxLink.Replace(s, m => m.Groups[1].Value);
        return s;
    }
}
