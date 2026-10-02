// 區塊職責：知識庫的**切塊**（TASK-0378 階段 0 的主要修正）。
// 物理意義：舊版只按空行切、超過 800 字硬切 —— 2026-10-02 量：coredocs 13727 塊裡 38% 不到 40 字（多半只是一行標題）、
//           2158 塊內容重複 ⇒「6 筆同分 0.5754」那種標題雜訊；lessons.jsonl 沒有空行，被硬切成半截紀錄。
//           新規則：
//           ① markdown 依標題切段；每一塊的文字前面帶**標題路徑**（frontmatter title › # › ## …），
//              ⇒ 標題不再單獨成塊，而塊本身帶著「它在講哪一節」。
//           ② 段內依空行分段落，累積到 MaxChars 為止；單一段落超過上限就依句號切。
//           ③ 正文短於 MinChars 的塊併進同一檔的下一塊（檔尾的併進上一塊）。
//           ④ jsonl 一筆紀錄一塊（title＋body），超長的再依句切。
//           ⑤ 同一個 target 內正文相同的塊只留第一塊（去重在 KbIndex 做，這裡只產生）。
// 數值影響：純函式（字串進、塊出）。版本號 Version 進索引 meta —— 規則改了就要重建，⛔ 不准新舊規則的塊混在同一份索引。
// ⚠ code fence（```）裡的 `#` 不是標題。
using System.Text;
using System.Text.Json;

namespace Senate.Core;

public sealed record KbChunk(string Text, string Body, string Heading, int Line);

public static class KbChunker
{
    /// <summary>規則版本 —— 改了切塊規則就加一（索引 meta 記它；不同版本的索引整份重建）。上限值也算進版本。</summary>
    public static string Version => "v2-" + MaxChars + (HeadingInText ? "" : "-nohead");

    /// <summary>
    /// 一塊正文的上限（字）。⚠ 2026-10-02 實測：塊越大語意越被稀釋 ——
    /// 同一句查詢對同一個檔：505 字的塊 0.526、只取第一段 0.626（core-05 因此從前 10 掉出去）。
    /// 值由評估題庫挑（`KB_CHUNK_MAX` 環境變數只給評估實驗用，⛔ 平常不設）。
    /// </summary>
    public static int MaxChars { get; } = int.TryParse(Environment.GetEnvironmentVariable("KB_CHUNK_MAX"), out int v) && v >= 200 ? v : 900;
    public const int MinChars = 40;

    public static List<KbChunk> Chunk(string iKind, string iFileName, string iText)
    {
        iText = iText.Replace("\r\n", "\n").Replace('\r', '\n');
        return iFileName.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) ? ChunkJsonl(iText) : ChunkMarkdown(iText);
    }

    // ── markdown ─────────────────────────────────────────────────────

    sealed class Section { public string Heading = ""; public int Line; public readonly List<(string Para, int Line)> Paras = new(); }

    public static List<KbChunk> ChunkMarkdown(string iText)
    {
        string[] aLines = iText.Split('\n');
        int i = 0;
        string aTitle = "";
        // frontmatter：只取 title（給標題路徑用），其餘不進正文 —— 那是機器欄位，進正文只會變成雜訊
        if (aLines.Length > 0 && aLines[0].Trim() == "---")
        {
            for (int k = 1; k < aLines.Length; k++)
            {
                if (aLines[k].Trim() == "---") { i = k + 1; break; }
                if (aLines[k].StartsWith("title:", StringComparison.Ordinal)) aTitle = aLines[k].Substring(6).Trim().Trim('"', '\'');
            }
        }

        var aSections = new List<Section>();
        var aStack = new List<(int Level, string Text)>();
        var cur = new Section { Heading = aTitle, Line = i + 1 };
        aSections.Add(cur);
        var aPara = new StringBuilder();
        int aParaLine = 0;
        bool aFence = false;

        void FlushPara()
        {
            string p = aPara.ToString().Trim();
            if (p.Length > 0) cur.Paras.Add((p, aParaLine));
            aPara.Clear();
        }

        for (; i < aLines.Length; i++)
        {
            string l = aLines[i];
            if (l.TrimStart().StartsWith("```", StringComparison.Ordinal)) aFence = !aFence;
            int aLevel = aFence ? 0 : HeadingLevel(l);
            if (aLevel > 0)
            {
                FlushPara();
                string h = l.TrimStart().Substring(aLevel).Trim().Trim('#').Trim();
                while (aStack.Count > 0 && aStack[^1].Level >= aLevel) aStack.RemoveAt(aStack.Count - 1);
                aStack.Add((aLevel, h));
                // 文件第一個 # 跟 frontmatter title 常常是同一句 ⇒ 不重複帶
                var aParts = new List<string>();
                if (aTitle.Length > 0) aParts.Add(aTitle);
                // ⚠ 去重用「前綴」不只用相等：frontmatter title 常是「X — 說明」而第一個 # 是「X」（2026-10-02 量到
                //   `UCL_GitFlattenSyncPage — Git 攤平同步頁 › UCL_GitFlattenSyncPage`，同一個名字在標題路徑裡出現兩次）
                foreach (var s in aStack)
                    if (aParts.Count == 0 || !(aParts[^1].StartsWith(s.Text, StringComparison.Ordinal) || s.Text.StartsWith(aParts[^1], StringComparison.Ordinal)))
                        aParts.Add(s.Text);
                cur = new Section { Heading = string.Join(" › ", aParts), Line = i + 1 };
                aSections.Add(cur);
                continue;
            }
            if (!aFence && l.Trim().Length == 0) { FlushPara(); continue; }
            if (aPara.Length == 0) aParaLine = i + 1;
            aPara.Append(l).Append('\n');
        }
        FlushPara();

        // 段 → 塊：段落累積到上限；單一段落超長就依句切
        var aRaw = new List<(string Body, string Heading, int Line)>();
        foreach (Section s in aSections)
        {
            var sb = new StringBuilder();
            int aStart = 0;
            foreach (var (p, ln) in s.Paras)
            {
                foreach (string piece in p.Length > MaxChars ? SplitSentences(p, MaxChars) : new List<string> { p })
                {
                    if (sb.Length > 0 && sb.Length + 2 + piece.Length > MaxChars)
                    { aRaw.Add((sb.ToString(), s.Heading, aStart)); sb.Clear(); }
                    if (sb.Length == 0) aStart = ln;
                    else sb.Append("\n\n");
                    sb.Append(piece);
                }
            }
            if (sb.Length > 0) aRaw.Add((sb.ToString(), s.Heading, aStart));
        }
        return MergeShort(aRaw);
    }

    static int HeadingLevel(string l)
    {
        string t = l.TrimStart();
        if (l.Length - t.Length > 3) return 0;   // 縮排 4 格以上是程式碼區塊
        int n = 0;
        while (n < t.Length && n < 7 && t[n] == '#') n++;
        return n >= 1 && n <= 6 && (t.Length == n || t[n] == ' ') ? n : 0;
    }

    /// <summary>短塊併進下一塊（同一檔內；檔尾的併進上一塊）。只有一塊的檔不併 —— 短檔就是短。</summary>
    static List<KbChunk> MergeShort(List<(string Body, string Heading, int Line)> iRaw)
    {
        var aOut = new List<(string Body, string Heading, int Line)>();
        (string Body, string Heading, int Line)? aCarry = null;
        foreach (var r in iRaw)
        {
            var x = r;
            if (aCarry is { } c)
            {
                // 併的時候把短塊的標題留在正文裡 —— 那一行常常是下一塊的引言
                x = (MarkHeading(c.Heading, c.Body) + "\n\n" + x.Body, x.Heading, c.Line);
                aCarry = null;
            }
            if (x.Body.Length < MinChars) { aCarry = x; continue; }
            aOut.Add(x);
        }
        if (aCarry is { } tail)
        {
            if (aOut.Count > 0) { var last = aOut[^1]; aOut[^1] = (last.Body + "\n\n" + MarkHeading(tail.Heading, tail.Body), last.Heading, last.Line); }
            else aOut.Add(tail);
        }
        return aOut.Select(r => new KbChunk(Compose(r.Heading, r.Body), r.Body, r.Heading, r.Line)).ToList();
    }

    static string MarkHeading(string h, string body) => h.Length > 0 && !body.StartsWith(LastPart(h), StringComparison.Ordinal) ? LastPart(h) + "：" + body : body;
    static string LastPart(string h) { int k = h.LastIndexOf(" › ", StringComparison.Ordinal); return k < 0 ? h : h.Substring(k + 3); }

    /// <summary>送去嵌入的文字＝標題路徑＋正文（`KB_HEADING=0` 只給評估實驗用：只嵌正文）。</summary>
    public static string Compose(string iHeading, string iBody) => iHeading.Length > 0 && HeadingInText ? iHeading + "\n" + iBody : iBody;

    public static bool HeadingInText { get; } = Environment.GetEnvironmentVariable("KB_HEADING") != "0";

    static readonly char[] s_Enders = { '。', '！', '？', '!', '?', '\n' };

    /// <summary>依句尾切成不超過上限的片段；一句本身超長才硬切（最後的退路）。</summary>
    public static List<string> SplitSentences(string iText, int iMax)
    {
        var aSent = new List<string>();
        int aFrom = 0;
        for (int k = 0; k < iText.Length; k++)
        {
            bool aEnd = Array.IndexOf(s_Enders, iText[k]) >= 0 || (iText[k] == '.' && (k + 1 == iText.Length || iText[k + 1] == ' '));
            if (aEnd) { aSent.Add(iText.Substring(aFrom, k - aFrom + 1)); aFrom = k + 1; }
        }
        if (aFrom < iText.Length) aSent.Add(iText.Substring(aFrom));
        var aOut = new List<string>();
        var sb = new StringBuilder();
        foreach (string s0 in aSent)
        {
            string s = s0;
            while (s.Length > iMax) { if (sb.Length > 0) { aOut.Add(sb.ToString().Trim()); sb.Clear(); } aOut.Add(s.Substring(0, iMax)); s = s.Substring(iMax); }
            if (sb.Length + s.Length > iMax) { aOut.Add(sb.ToString().Trim()); sb.Clear(); }
            sb.Append(s);
        }
        if (sb.ToString().Trim().Length > 0) aOut.Add(sb.ToString().Trim());
        return aOut.Where(x => x.Length > 0).ToList();
    }

    // ── jsonl ────────────────────────────────────────────────────────

    /// <summary>一筆紀錄一塊：title（有的話）＋ body。讀不了的行原樣當一塊（⛔ 不丟 —— 丟掉的那筆不會叫）。</summary>
    public static List<KbChunk> ChunkJsonl(string iText)
    {
        var aOut = new List<KbChunk>();
        string[] aLines = iText.Split('\n');
        for (int i = 0; i < aLines.Length; i++)
        {
            string l = aLines[i].Trim();
            if (l.Length == 0) continue;
            string aTitle = "", aBody = l;
            try
            {
                using JsonDocument d = JsonDocument.Parse(l);
                JsonElement r = d.RootElement;
                if (r.ValueKind == JsonValueKind.Object)
                {
                    aTitle = r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
                    aBody = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : l;
                }
            }
            catch (JsonException) { }
            foreach (string piece in aBody.Length > MaxChars ? SplitSentences(aBody, MaxChars) : new List<string> { aBody })
                aOut.Add(new KbChunk(Compose(aTitle, piece), piece, aTitle, i + 1));
        }
        return aOut;
    }
}
