// 區塊職責：`senate cmd bilibili` —— persona 在 Tim 的 B 站帳號留言：內文由 persona 寫，**署名由本指令補**。
// 物理意義：帳號暱稱是帳號主人的；留言不署名，讀的人會以為是他本人說的。
//           Tim 2026-11-10：「留言自動帶上當前persona(參考酒館發言)」；2026-11-11：「可以自由配置屬名(每個persona可以配置1~N組 有多組可以隨機)」。
//           ⇒ 署名住 `<信件庫>/<persona>/profile/bilibili_signatures.md`（一行一組），留言時挑一組接在末行；沒配置就用預設「——<persona>（<登入時的模型>）」並說出來。
//             Tim 同日：「簽名可以直接自動顯示登入時的模型(例如 claude-fable-5-2)」⇒ 模型讀 `profile/model.md`（早安寫的）；自訂署名用 `{model}` 帶。
//           真正送出的是開源的 `bili`（bili-agent）：本檔組好全文寫成暫存檔，跑 `bili comment post／reply --file`，再用 `bili comment check` 讀回。
//           登入狀態在 `<資料根>/Secret/bilibili/`（`BILI_CLI_HOME`）——本檔只把那個資料夾指給 `bili`，⛔ 不讀裡面的憑證。
// 數值影響：
//   · 帳本 `<資料根>/Secret/bilibili/senate_comments.json` 記本指令發過的留言（BV、rpid、persona、用了哪一組署名、全文、時間）。
//     同一支影片已經留過根留言、同一則留言已經回過 ⇒ 擋下（`allow_duplicate=1` 才放）——同一個地方只說一次。
//   · 內文末行已經是署名 ⇒ 擋下（不署兩次）。
//   · `dry_run=1`：`bili --dry-run` 也照跑一次（讓它檢查參數），不送、帳本不動。
// ⚠ exit code：0 成功；1 `bili` 回報失敗（**確定沒發**）；2 被擋（參數、檢查、`bili` 不在、沒登入）；
//   7 **不知道**有沒有發（逾時、輸出讀不懂）⇒ 先 `bili comment list <BV>` 讀回，⛔ 別直接重送。
// ⚠ `risk_control`／`captcha_required`（`bili` 的 exit 5／6）⇒ 停下所有寫入、回報帳號主人；⛔ 本檔不重試。
#nullable enable
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace Senate.Core;

public sealed class Cmd_Bilibili : SCP_Cmd
{
    public override string Name => "bilibili";
    public override string Category => SCP_CmdCategory.Reading;

    public override string Summary => "B 站留言：內文由 persona 寫、署名由指令補（每個 persona 可以配 1～N 組，多組隨機挑）；發出後讀回、記帳本";

    public override string Details =>
        "· `op=status`（預設）[--arg persona=<P>]：`bili` 在不在、登入了沒、帳本幾筆、這個 persona 配了幾組署名。\n"
        + "· `op=signatures --arg persona=<P>`：列出署名。`op=signature-add --arg persona=<P> --arg text=<一行>`：加一組。"
        + "`op=signature-remove --arg persona=<P> --arg index=<第幾組>`：拿掉一組。\n"
        + "· `op=comment --arg persona=<P> --arg target=<BV號> --arg-file body=<內文檔> [--arg index=<第幾組署名>] [--arg dry_run=1] [--arg allow_duplicate=1]`："
        + "在影片底下留一則根留言。內文**不要**自己寫署名。\n"
        + "· `op=reply --arg persona=<P> --arg target=<BV號> --arg rpid=<要回的留言> [--arg root=<根留言>] --arg-file body=<內文檔> …`："
        + "回一則留言；回的是樓中樓時 `root` 填它所在的根留言。\n"
        + "· `op=log [--arg max=20]`：本指令發過的留言。\n"
        + "⚠ 結果三態：exit 0 已發／exit 1 **確定沒發**／exit 7 **不知道**（先 `bili comment list <BV>` 讀回，⛔ 別直接重送）。\n"
        + "⚠ 署名沒配置 ⇒ 用預設「——<persona>（<登入時的模型>）」（例：——Claude-Ojousama（claude-fable-5-2）；讀不到模型就只有名字）並在輸出裡說出來。\n"
        + "⚠ 自訂的署名裡寫 `{model}` 會換成登入時的模型、`{persona}` 換成名字。能留言的範圍與紀律看 `Docs/Workflows/Bilibili.md` §4、§5。";

    public override string Example => SCP_CmdRegistry.Invoke("bilibili --arg op=comment --arg persona=Template --arg target=BV1xx411c7mD --arg-file body=D:/tmp/comment.txt --arg dry_run=1");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "做什麼", iDefault: "status", iChoices: new[] { "status", "signatures", "signature-add", "signature-remove", "comment", "reply", "log" }),
        new SCP_CmdArgSpec("persona", "誰在說話（status／log 以外必填；一律顯式）", iDefault: ""),
        new SCP_CmdArgSpec("target", "comment／reply：留言區所在的影片（BV 號、av 號或網址）", iDefault: ""),
        new SCP_CmdArgSpec("rpid", "reply：要回的那則留言的 id", iDefault: ""),
        new SCP_CmdArgSpec("root", "reply：回的是樓中樓時，它所在的根留言 id", iDefault: ""),
        new SCP_CmdArgSpec("body", "comment／reply：留言內文，**不含署名**。長內文走 --arg-file", iDefault: ""),
        new SCP_CmdArgSpec("text", "signature-add：一組署名（一行；`{model}` 會換成登入時的模型、`{persona}` 換成名字。例：——{persona}（{model}，借用這個帳號發言））", iDefault: ""),
        new SCP_CmdArgSpec("index", "signature-remove：拿掉第幾組（1 起算）；comment／reply：指定用第幾組（不給 ＝ 多組時隨機）", iDefault: ""),
        new SCP_CmdArgSpec("dry_run", "comment／reply：1＝印出要送的全文、不送", iDefault: "0", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("allow_duplicate", "comment／reply：1＝同一支影片已經留過、同一則已經回過也照發", iDefault: "0", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("timeout", "等 `bili` 的秒數", iDefault: "90"),
        new SCP_CmdArgSpec("max", "log：最多列幾筆", iDefault: "20"),
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（`bili` 的登入狀態住 <資料根>/Secret/bilibili/）", iRequired: true),
        new SCP_CmdArgSpec("letters_root", "信件庫根（驗 persona 存在、讀署名）", iRequired: true),
    };

    public const string SignatureFileName = "bilibili_signatures.md";
    public const string LedgerFileName = "senate_comments.json";
    public const int CommentMax = 1000;       // B 站一則留言的字數上限
    public const int SignatureMax = 80;
    public const int SignaturesMaxCount = 20;

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aRoot = iArgs.Get("data_root").Trim();
        if (aRoot.Length == 0 || !Directory.Exists(aRoot)) return SCP_CmdResult.Fail(2, $"✗ 資料根不存在：{aRoot}");
        string aLetters = iArgs.Get("letters_root").Trim();
        if (aLetters.Length == 0 || !Directory.Exists(aLetters)) return SCP_CmdResult.Fail(2, $"✗ 信件庫不存在：{aLetters}");
        string aOp = iArgs.Get("op").Trim();
        string aPersona = iArgs.Get("persona").Trim();
        if (aOp is "signatures" or "signature-add" or "signature-remove" or "comment" or "reply")
        {
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ `persona` 必填（誰在說話）—— 不替你挑");
            if (!Directory.Exists(SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(aLetters), aPersona)))
                return SCP_CmdResult.Fail(2, $"✗ persona `{aPersona}` 不存在（信件庫裡沒有這個人）");
        }
        return aOp switch
        {
            "signatures" => Signatures(aLetters, aPersona),
            "signature-add" => SignatureAdd(aLetters, aPersona, iArgs.Get("text")),
            "signature-remove" => SignatureRemove(aLetters, aPersona, iArgs.Get("index").Trim()),
            "comment" => Post(aRoot, aLetters, aPersona, iArgs, false),
            "reply" => Post(aRoot, aLetters, aPersona, iArgs, true),
            "log" => Log(aRoot, iArgs),
            _ => Status(aRoot, aLetters, aPersona, iArgs),
        };
    }

    // ── 署名 ────────────────────────────────────────────────────

    public static string SignaturePath(string iLettersRoot, string iPersona)
        => SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), iPersona) + "/" + SignatureFileName;

    /// <summary>沒配置時用的那一組：「——」接 persona 的名字（Tim 2026-11-11：「沒有配置屬名時 要有一組預設(根據persona 例如 ——Claude-Ojousama)」），知道登入時的模型就接在括號裡。</summary>
    public static string DefaultSignature(string iPersona, string iModel = "")
        => iModel.Trim().Length > 0 ? $"——{iPersona}（{iModel.Trim()}）" : "——" + iPersona;

    public const string ModelFileName = "model.md";

    /// <summary>
    /// 登入時的模型：早安寫進 `profile/model.md` 的那一行（Tim 2026-11-11：「簽名可以直接自動顯示登入時的模型(例如 claude-fable-5-2)」）。
    /// 沒有檔、讀不了、是空的 ⇒ ""（＝不知道；預設署名就不帶括號，自訂署名用了 `{model}` 的會擋下）。
    /// </summary>
    public static string ReadLoginModel(string iLettersRoot, string iPersona)
    {
        try
        {
            string aPath = SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), iPersona) + "/" + ModelFileName;
            if (!File.Exists(aPath)) return "";
            foreach (string l in File.ReadAllText(aPath, Encoding.UTF8).Replace("\r", "").Split('\n'))
                if (l.Trim().TrimStart('\uFEFF').Trim().Length > 0) return l.Trim().TrimStart('\uFEFF').Trim();
            return "";
        }
        catch { return ""; }
    }

    /// <summary>把署名裡的 `{persona}`、`{model}` 換成實際的值。用了 `{model}` 卻不知道模型 ⇒ false ＋ 原因（⛔ 不留一個空括號發出去）。</summary>
    public static bool TryExpandSignature(string iSignature, string iPersona, string iModel, out string oText, out string? oError)
    {
        oError = null;
        oText = iSignature.Replace("{persona}", iPersona);
        if (oText.Contains("{model}"))
        {
            if (iModel.Trim().Length == 0) { oError = "這一組署名用了 `{model}`，可是讀不到登入時的模型（`profile/model.md`）⇒ 先早安登入，或換一組"; return false; }
            oText = oText.Replace("{model}", iModel.Trim());
        }
        return true;
    }

    /// <summary>署名檔的內文 → 清單：一行一組；空行與 `#` 開頭的行不算。</summary>
    public static List<string> ParseSignatures(string iText)
    {
        var aList = new List<string>();
        foreach (string aRaw in iText.Replace("\r", "").Split('\n'))
        {
            string s = aRaw.Trim().TrimStart('\uFEFF').Trim();
            if (s.Length == 0 || s.StartsWith("#", StringComparison.Ordinal)) continue;
            if (!aList.Contains(s)) aList.Add(s);
        }
        return aList;
    }

    /// <summary>讀署名。沒有檔 ⇒ 空清單（＝還沒配置）；讀不了 ⇒ null ＋ 原因（⛔ 不能當成沒配置而換成預設）。</summary>
    public static List<string>? LoadSignatures(string iLettersRoot, string iPersona, out string? oError)
    {
        oError = null;
        string aPath = SignaturePath(iLettersRoot, iPersona);
        if (!File.Exists(aPath)) return new List<string>();
        try { return ParseSignatures(File.ReadAllText(aPath, Encoding.UTF8)); }
        catch (Exception e) { oError = $"{aPath}：{e.Message}"; return null; }
    }

    /// <summary>一組署名合不合格；合格回 null。</summary>
    public static string? LintSignature(string iText)
    {
        string s = iText.Trim();
        if (s.Length == 0) return "署名是空的";
        if (s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0) return "署名只能一行";
        if (s.StartsWith("#", StringComparison.Ordinal)) return "署名不能用 `#` 開頭（署名檔裡那是註解）";
        if (new StringInfo(s).LengthInTextElements > SignatureMax) return $"署名超過 {SignatureMax} 字";
        return null;
    }

    static string SignatureFileText(List<string> iList)
        => "# B 站留言的署名：一行一組；多組 ⇒ 留言時隨機挑一組。用 `senate cmd bilibili --arg op=signature-add／signature-remove` 改。\n"
           + string.Join("\n", iList) + "\n";

    static SCP_CmdResult Signatures(string iLetters, string iPersona)
    {
        List<string>? aList = LoadSignatures(iLetters, iPersona, out string? aErr);
        if (aList == null) return SCP_CmdResult.Fail(1, "✗ 署名檔讀不了：" + aErr);
        var r = SCP_CmdResult.Success($"# {iPersona} 的 B 站署名（{aList.Count} 組）");
        string aModel = ReadLoginModel(iLetters, iPersona);
        if (aList.Count == 0) r.Lines.Add($"- （還沒配置）⇒ 留言會用預設：{DefaultSignature(iPersona, aModel)}");
        for (int i = 0; i < aList.Count; i++)
            r.Lines.Add($"{i + 1}. {aList[i]}" + (aList[i].Contains("{model}") || aList[i].Contains("{persona}")
                ? (TryExpandSignature(aList[i], iPersona, aModel, out string aShown, out string? aWhy) ? $"　⇒ 發出去是：{aShown}" : $"　⚠ {aWhy}") : ""));
        r.Lines.Add($"- 登入時的模型：{(aModel.Length > 0 ? "`" + aModel + "`" : "**讀不到**")}（署名裡寫 `{{model}}` 會換成它、`{{persona}}` 換成名字；預設那一組自動帶）");
        if (aList.Count > 1) r.Lines.Add("- 多組 ⇒ 留言時隨機挑一組；要指定就帶 `--arg index=<第幾組>`");
        r.Lines.Add($"- 檔：`{SignaturePath(iLetters, iPersona)}`");
        r.AddValue("signature_count", aList.Count.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    static bool TrySaveSignatures(string iLetters, string iPersona, List<string> iList, out string? oError)
    {
        oError = null;
        try
        {
            string aPath = SignaturePath(iLetters, iPersona);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            YouTubeClient.WriteReplace(aPath, new UTF8Encoding(false).GetBytes(SignatureFileText(iList)));
            List<string>? aBack = LoadSignatures(iLetters, iPersona, out oError);          // 寫完回讀
            if (aBack == null) return false;
            if (aBack.Count != iList.Count || aBack.Where((s, i) => s != iList[i]).Any()) { oError = "回讀的內容跟寫進去的不一樣"; return false; }
            return true;
        }
        catch (Exception e) { oError = e.Message; return false; }
    }

    static SCP_CmdResult SignatureAdd(string iLetters, string iPersona, string iText)
    {
        string? aBad = LintSignature(iText);
        if (aBad != null) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aBad);
        List<string>? aList = LoadSignatures(iLetters, iPersona, out string? aErr);
        if (aList == null) return SCP_CmdResult.Fail(1, "✗ 署名檔讀不了：" + aErr);
        string s = iText.Trim();
        if (aList.Contains(s)) return SCP_CmdResult.Fail(2, $"✗ 沒有寫入：這一組已經有了（第 {aList.IndexOf(s) + 1} 組）");
        if (aList.Count >= SignaturesMaxCount) return SCP_CmdResult.Fail(2, $"✗ 沒有寫入：已經有 {aList.Count} 組（上限 {SignaturesMaxCount}）");
        aList.Add(s);
        if (!TrySaveSignatures(iLetters, iPersona, aList, out aErr)) return SCP_CmdResult.Fail(1, "✗ 寫入失敗：" + aErr);
        SCP_CmdResult r = Signatures(iLetters, iPersona);
        r.Lines.Insert(0, $"✅ 已加入第 {aList.Count} 組");
        return r;
    }

    static SCP_CmdResult SignatureRemove(string iLetters, string iPersona, string iIndex)
    {
        List<string>? aList = LoadSignatures(iLetters, iPersona, out string? aErr);
        if (aList == null) return SCP_CmdResult.Fail(1, "✗ 署名檔讀不了：" + aErr);
        if (!int.TryParse(iIndex, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > aList.Count)
            return SCP_CmdResult.Fail(2, $"✗ 沒有寫入：`index` 要是 1～{aList.Count}（收到 '{iIndex}'）");
        string aGone = aList[n - 1];
        aList.RemoveAt(n - 1);
        if (!TrySaveSignatures(iLetters, iPersona, aList, out aErr)) return SCP_CmdResult.Fail(1, "✗ 寫入失敗：" + aErr);
        SCP_CmdResult r = Signatures(iLetters, iPersona);
        r.Lines.Insert(0, $"✅ 已拿掉：{aGone}");
        return r;
    }

    /// <summary>
    /// 挑署名。iIndex：空 ＝ 自動（一組就用那一組、多組用 iRandom 挑）；給了就要在 1～N 之內。
    /// 沒配置 ⇒ 預設那一組（oSource ＝ "default"、oPicked ＝ 0）。
    /// </summary>
    public static bool TryPickSignature(List<string> iList, string iPersona, string iIndex, Func<int, int> iRandom,
        out string oSignature, out int oPicked, out string oSource, out string? oError)
    {
        oSignature = ""; oPicked = 0; oSource = ""; oError = null;
        string aIdx = iIndex.Trim();
        if (iList.Count == 0)
        {
            if (aIdx.Length > 0) { oError = "還沒配置署名，`index` 用不上（先 `op=signature-add`，或拿掉 `index` 用預設那一組）"; return false; }
            oSignature = DefaultSignature(iPersona); oSource = "default"; return true;
        }
        if (aIdx.Length > 0)
        {
            if (!int.TryParse(aIdx, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n < 1 || n > iList.Count)
            { oError = $"`index` 要是 1～{iList.Count}（收到 '{aIdx}'）"; return false; }
            oPicked = n; oSource = "index";
        }
        else if (iList.Count == 1) { oPicked = 1; oSource = "only"; }
        else
        {
            int k = iRandom(iList.Count);
            if (k < 0 || k >= iList.Count) { oError = $"亂數挑到範圍外（{k}／{iList.Count}）"; return false; }
            oPicked = k + 1; oSource = "random";
        }
        oSignature = iList[oPicked - 1];
        return true;
    }

    // ── 內文 ────────────────────────────────────────────────────

    public static string NormalizeBody(string iBody) => iBody.Replace("\r\n", "\n").Replace('\r', '\n').Trim().TrimStart('\uFEFF').Trim();

    /// <summary>內文的最後一行（去掉空白）。</summary>
    public static string LastLine(string iBody)
    {
        string[] a = NormalizeBody(iBody).Split('\n');
        for (int i = a.Length - 1; i >= 0; i--) if (a[i].Trim().Length > 0) return a[i].Trim();
        return "";
    }

    /// <summary>內文末行是不是已經是署名：等於任何一組配置的／預設的，或是「——」開頭又帶著 persona 的名字。</summary>
    public static bool AlreadySigned(string iBody, string iPersona, List<string> iSignatures)
    {
        string aLast = LastLine(iBody);
        if (aLast.Length == 0) return false;
        if (aLast == DefaultSignature(iPersona) || iSignatures.Contains(aLast)) return true;
        return (aLast.StartsWith("——", StringComparison.Ordinal) || aLast.StartsWith("--", StringComparison.Ordinal))
               && aLast.IndexOf(iPersona, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static string Compose(string iBody, string iSignature) => NormalizeBody(iBody) + "\n" + iSignature.Trim();

    /// <summary>影片的代號：抓得到 BV 號就用它（網址、大小寫前綴都收斂成同一個），抓不到就原樣。</summary>
    public static string TargetKey(string iTarget)
    {
        Match m = Regex.Match(iTarget, @"BV[0-9A-Za-z]{10}", RegexOptions.IgnoreCase);
        if (!m.Success) return iTarget.Trim();
        return "BV" + m.Value.Substring(2);
    }

    /// <summary>送出前的檢查。回錯誤清單（空 ＝ 過）。</summary>
    public static List<string> Lint(bool iReply, string iPersona, string iTarget, string iRpid, string iRoot, string iBody,
        List<string> iSignatures, string iComposed)
    {
        var e = new List<string>();
        if (iTarget.Trim().Length == 0) e.Add("`target` 必填（影片的 BV 號）");
        if (iReply)
        {
            if (!IsId(iRpid)) e.Add($"`rpid` 要是留言的數字 id（收到 '{iRpid}'）");
            if (iRoot.Trim().Length > 0 && !IsId(iRoot)) e.Add($"`root` 要是留言的數字 id（收到 '{iRoot}'）");
        }
        else if (iRpid.Trim().Length > 0 || iRoot.Trim().Length > 0) e.Add("`rpid`／`root` 是 `op=reply` 用的；根留言不帶");
        string aBody = NormalizeBody(iBody);
        if (aBody.Length == 0) e.Add("`body` 是空的（留言內文；長內文走 `--arg-file body=<檔>`）");
        else if (AlreadySigned(aBody, iPersona, iSignatures)) e.Add("內文末行已經是署名 ⇒ 拿掉它，署名由本指令補（不署兩次）");
        int aLen = new StringInfo(iComposed).LengthInTextElements;
        if (aLen > CommentMax) e.Add($"連署名一共 {aLen} 字，超過 {CommentMax} 字");
        return e;
    }

    static bool IsId(string s) => s.Trim().Length > 0 && s.Trim().All(ch => ch >= '0' && ch <= '9');

    /// <summary>要跑的 `bili` 參數（不含執行檔）。`--dry-run` 是全域旗標，放最前面。</summary>
    public static List<string> BuildBiliArgs(bool iReply, string iTarget, string iRpid, string iRoot, string iFile, bool iDryRun)
    {
        var a = new List<string>();
        if (iDryRun) a.Add("--dry-run");
        a.Add("comment");
        a.Add(iReply ? "reply" : "post");
        a.Add(iTarget.Trim());
        if (iReply)
        {
            a.Add(iRpid.Trim());
            if (iRoot.Trim().Length > 0) { a.Add("--root"); a.Add(iRoot.Trim()); }
        }
        a.Add("--file"); a.Add(iFile);
        return a;
    }

    // ── 帳本 ────────────────────────────────────────────────────

    public sealed class CommentEntry
    {
        public string Rpid = "", Target = "", Kind = "", ParentRpid = "", RootRpid = "", Persona = "", Signature = "", Text = "", PostedAt = "";
    }

    public static string BiliHome(string iDataRoot) => SCP.Core.Secret.SCP_SecretStore.ResolveDir(iDataRoot, out _) + "/bilibili";
    public static string LedgerPath(string iDataRoot) => BiliHome(iDataRoot) + "/" + LedgerFileName;

    /// <summary>讀帳本。檔案不存在 ⇒ 空清單；讀不懂 ⇒ null ＋ 原因（⛔ 呼叫端不得拿空清單蓋掉它）。</summary>
    public static List<CommentEntry>? LoadLedger(string iDataRoot, out string? oError)
    {
        oError = null;
        string aPath = LedgerPath(iDataRoot);
        if (!File.Exists(aPath)) return new List<CommentEntry>();
        try
        {
            SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
            var aList = new List<CommentEntry>();
            foreach (SCP_JsonData x in j["comments"])
                aList.Add(new CommentEntry
                {
                    Rpid = x["rpid"].AsString(), Target = x["target"].AsString(), Kind = x["kind"].AsString(),
                    ParentRpid = x.GetString("parent_rpid", ""), RootRpid = x.GetString("root_rpid", ""), Persona = x.GetString("persona", ""),
                    Signature = x.GetString("signature", ""), Text = x.GetString("text", ""), PostedAt = x.GetString("posted_at", ""),
                });
            return aList;
        }
        catch (Exception e) { oError = $"{aPath}：{e.Message}"; return null; }
    }

    public static bool TrySaveLedger(string iDataRoot, List<CommentEntry> iList, out string? oError)
    {
        oError = null;
        var arr = SCP_JsonData.NewArray();
        foreach (CommentEntry x in iList)
        {
            var o = SCP_JsonData.NewObject();
            o.Set("rpid", x.Rpid);
            o.Set("target", x.Target);
            o.Set("kind", x.Kind);
            o.Set("parent_rpid", x.ParentRpid);
            o.Set("root_rpid", x.RootRpid);
            o.Set("persona", x.Persona);
            o.Set("signature", x.Signature);
            o.Set("text", x.Text);
            o.Set("posted_at", x.PostedAt);
            arr.Add(o);
        }
        var j = SCP_JsonData.NewObject();
        j.Set("comments", arr);
        try
        {
            string aPath = LedgerPath(iDataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            YouTubeClient.WriteReplace(aPath, new UTF8Encoding(false).GetBytes(j.ToJson(true)));
            return true;
        }
        catch (Exception e) { oError = e.Message; return false; }
    }

    /// <summary>同一個地方是不是已經說過：根留言看同一支影片有沒有根留言；回覆看同一則留言有沒有回過。</summary>
    public static CommentEntry? FindDuplicate(List<CommentEntry> iLedger, bool iReply, string iTarget, string iRpid)
    {
        string aKey = TargetKey(iTarget);
        foreach (CommentEntry x in iLedger)
        {
            if (iReply) { if (x.Kind == "reply" && x.ParentRpid == iRpid.Trim()) return x; }
            else if (x.Kind == "comment" && TargetKey(x.Target) == aKey) return x;
        }
        return null;
    }

    // ── 跑 `bili` ───────────────────────────────────────────────

    public sealed class BiliRun
    {
        public bool Started, TimedOut;
        public int Exit = -1;
        public string Out = "", Err = "", StartError = "";
    }

    /// <summary>找 `bili`：PATH 上的，找不到再看 `~/.local/bin`（`uv tool` 裝的位置；舊的終端機讀不到新 PATH）。</summary>
    public static string? FindBili()
    {
        var aDirs = new List<string>((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        aDirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"));
        return FindBiliIn(aDirs);
    }

    /// <summary>在這幾個資料夾裡找 `bili` 的執行檔；都沒有 ⇒ null。（拆出來是為了 selftest 釘得住「找不到就是找不到」。）</summary>
    public static string? FindBiliIn(IEnumerable<string> iDirs)
    {
        string aName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "bili.exe" : "bili";
        foreach (string d in iDirs)
        {
            if (d.Trim().Length == 0) continue;
            try { string p = Path.Combine(d.Trim().Trim('"'), aName); if (File.Exists(p)) return p; }
            catch { /* PATH 裡的怪字串：跳過 */ }
        }
        return null;
    }

    static BiliRun RunBili(string iDataRoot, IReadOnlyList<string> iArgs, int iTimeoutSec)
    {
        var r = new BiliRun();
        string? aExe = FindBili();
        if (aExe == null) { r.StartError = "找不到 `bili`（PATH 與 ~/.local/bin 都沒有）"; return r; }
        var psi = new ProcessStartInfo(aExe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string a in iArgs) psi.ArgumentList.Add(a);
        psi.Environment["BILI_CLI_HOME"] = BiliHome(iDataRoot);
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment.Remove("BILI_CLI_NO_RATE_LIMIT");       // ⛔ 不關 bili 的本地限流（就算呼叫端的環境裡設了）
        try
        {
            using var p = new Process { StartInfo = psi };
            p.Start();
            r.Started = true;
            Task<string> aOut = p.StandardOutput.ReadToEndAsync(), aErr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Math.Max(5, iTimeoutSec) * 1000))
            {
                r.TimedOut = true;
                try { p.Kill(true); } catch { /* 已經結束 */ }
            }
            else r.Exit = p.ExitCode;
            if (aOut.Wait(5000)) r.Out = aOut.Result;
            if (aErr.Wait(5000)) r.Err = aErr.Result;
        }
        catch (Exception e) { r.StartError = e.Message; }
        return r;
    }

    /// <summary>`bili` 的 stdout（一個 JSON）→ ok／data／錯誤。讀不懂回 false。</summary>
    public static bool TryParseBili(string iStdout, out bool oOk, out SCP_JsonData? oData, out string oErrCode, out string oErrMessage)
    {
        oOk = false; oData = null; oErrCode = ""; oErrMessage = "";
        try
        {
            SCP_JsonData j = SCP_JsonParser.Parse(iStdout);
            oOk = j["ok"].AsBool();
            if (oOk) { oData = j["data"]; return true; }
            SCP_JsonData e = j["error"];
            oErrCode = e.GetString("code", "");
            oErrMessage = e.GetString("message", "");
            return true;
        }
        catch { return false; }
    }

    static string Tail(string s, int n) { s = s.Trim(); return s.Length <= n ? s : "…" + s.Substring(s.Length - n); }

    // ── status／log ─────────────────────────────────────────────

    static SCP_CmdResult Status(string iRoot, string iLetters, string iPersona, SCP_CmdArgs iArgs)
    {
        var r = SCP_CmdResult.Success("# Bilibili（留言署名）");
        string? aExe = FindBili();
        r.Lines.Add(aExe == null ? "- `bili`：**找不到** ⇒ 讀安裝文件 `senate cmd doc --arg op=show --arg name=Bilibili_Install`" : $"- `bili`：`{aExe.Replace('\\', '/')}`");
        string aHome = BiliHome(iRoot);
        r.Lines.Add($"- 登入狀態的資料夾：`{aHome}`" + (Directory.Exists(aHome) ? "" : "（**不存在** ⇒ 還沒登入）"));
        string aLogged = "unknown";
        if (aExe != null && Directory.Exists(aHome))
        {
            iArgs.GetInt("timeout", 90, out _);
            BiliRun x = RunBili(iRoot, new[] { "auth", "status" }, 30);
            if (x.Started && !x.TimedOut && TryParseBili(x.Out, out bool aOk, out SCP_JsonData? aData, out string aCode, out string aMsg))
            {
                aLogged = aOk && aData != null && aData.GetBool("logged_in", false) ? "1" : "0";
                r.Lines.Add(aLogged == "1" ? "- 登入：✓" : $"- 登入：**沒有**（{(aCode.Length > 0 ? aCode + "：" + aMsg : "logged_in=false")}）⇒ 帳號主人重新掃碼");
            }
            else r.Lines.Add("- 登入：**不知道**（`bili auth status` " + (x.TimedOut ? "逾時" : x.StartError.Length > 0 ? "起不來：" + x.StartError : "的輸出讀不懂：" + Tail(x.Out + x.Err, 160)) + "）");
        }
        List<CommentEntry>? aLedger = LoadLedger(iRoot, out string? aLErr);
        r.Lines.Add(aLedger == null ? "- 留言帳本：✗ " + aLErr : $"- 留言帳本：{aLedger.Count} 筆（`{LedgerPath(iRoot)}`）");
        if (iPersona.Length > 0)
        {
            List<string>? aSigs = Directory.Exists(SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLetters), iPersona)) ? LoadSignatures(iLetters, iPersona, out string? aSErr) : null;
            r.Lines.Add(aSigs == null ? $"- {iPersona} 的署名：✗ 讀不了（persona 不存在或署名檔壞了）"
                : aSigs.Count == 0 ? $"- {iPersona} 的署名：還沒配置 ⇒ 用預設「{DefaultSignature(iPersona, ReadLoginModel(iLetters, iPersona))}」"
                : $"- {iPersona} 的署名：{aSigs.Count} 組" + (aSigs.Count > 1 ? "（留言時隨機挑）" : ""));
            r.AddValue("signature_count", aSigs == null ? "" : aSigs.Count.ToString(CultureInfo.InvariantCulture));
        }
        r.AddValue("bili_found", aExe == null ? "0" : "1");
        r.AddValue("logged_in", aLogged);
        r.AddValue("comments", aLedger == null ? "" : aLedger.Count.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    static SCP_CmdResult Log(string iRoot, SCP_CmdArgs iArgs)
    {
        List<CommentEntry>? aLedger = LoadLedger(iRoot, out string? aErr);
        if (aLedger == null) return SCP_CmdResult.Fail(1, "✗ 留言帳本讀不了：" + aErr);
        int aMax = Math.Clamp(iArgs.GetInt("max", 20, out _), 1, 200);
        var r = SCP_CmdResult.Success($"# 本指令發過的 B 站留言（{aLedger.Count} 筆，列最新 {Math.Min(aMax, aLedger.Count)} 筆）");
        foreach (CommentEntry x in Enumerable.Reverse(aLedger).Take(aMax))
        {
            string aFirst = x.Text.Split('\n')[0];
            if (aFirst.Length > 40) aFirst = aFirst.Substring(0, 40) + "…";
            r.Lines.Add($"- {x.PostedAt}　{x.Target}　{(x.Kind == "reply" ? "回 " + x.ParentRpid : "根留言")}　rpid {x.Rpid}　{x.Persona}　「{aFirst}」");
        }
        r.AddValue("comments", aLedger.Count.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    // ── comment／reply ──────────────────────────────────────────

    static SCP_CmdResult Post(string iRoot, string iLetters, string iPersona, SCP_CmdArgs iArgs, bool iReply)
    {
        string aTarget = iArgs.Get("target").Trim(), aRpid = iArgs.Get("rpid").Trim(), aRootId = iArgs.Get("root").Trim();
        string aBody = iArgs.Get("body");
        bool aDry = iArgs.Get("dry_run") == "1";

        List<string>? aSigs = LoadSignatures(iLetters, iPersona, out string? aErr);
        if (aSigs == null) return SCP_CmdResult.Fail(2, "✗ 沒有送出：署名檔讀不了（" + aErr + "）⇒ 修好它，⛔ 不拿預設署名頂");
        if (!TryPickSignature(aSigs, iPersona, iArgs.Get("index"), n => Random.Shared.Next(n), out string aSig, out int aPicked, out string aSource, out aErr))
            return SCP_CmdResult.Fail(2, "✗ 沒有送出：" + aErr);
        string aModel = ReadLoginModel(iLetters, iPersona);
        if (aSource == "default") aSig = DefaultSignature(iPersona, aModel);
        else if (!TryExpandSignature(aSig, iPersona, aModel, out aSig, out aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有送出：" + aErr);
        string aText = Compose(aBody, aSig);
        var aKnown = new List<string> { DefaultSignature(iPersona, aModel) };      // 內文末行等於其中任何一個 ⇒ 已經署過
        foreach (string s in aSigs) { aKnown.Add(s); if (TryExpandSignature(s, iPersona, aModel, out string x2, out _)) aKnown.Add(x2); }

        List<string> e = Lint(iReply, iPersona, aTarget, aRpid, aRootId, aBody, aKnown, aText);
        List<CommentEntry>? aLedger = LoadLedger(iRoot, out string? aLErr);
        if (aLedger == null) e.Add("留言帳本讀不了（" + aLErr + "）⇒ 沒辦法確認是不是說過了");
        else if (e.Count == 0 && iArgs.Get("allow_duplicate") != "1")
        {
            CommentEntry? aDup = FindDuplicate(aLedger, iReply, aTarget, aRpid);
            if (aDup != null)
                e.Add((iReply ? $"這一則留言已經回過了（rpid {aDup.Rpid}，{aDup.PostedAt}，{aDup.Persona}）" : $"這支影片已經留過根留言了（rpid {aDup.Rpid}，{aDup.PostedAt}，{aDup.Persona}）")
                      + " ⇒ 真的要再說一次加 `--arg allow_duplicate=1`");
        }
        if (FindBili() == null) e.Add("找不到 `bili` ⇒ 讀安裝文件 `senate cmd doc --arg op=show --arg name=Bilibili_Install`");
        else if (!Directory.Exists(BiliHome(iRoot))) e.Add($"`{BiliHome(iRoot)}` 不存在 ⇒ 還沒登入（`Bilibili_Install` §3，帳號主人掃碼）");
        if (e.Count > 0)
        {
            var f = SCP_CmdResult.Fail(2, $"✗ 沒有送出（{e.Count} 項）：");
            foreach (string x in e) f.Lines.Add("  - " + x);
            return f;
        }

        string aSigNote = aSource switch
        {
            "default" => "預設那一組（還沒配置署名 ⇒ `op=signature-add`）",
            "random" => $"第 {aPicked} 組（{aSigs.Count} 組裡隨機挑的）",
            "index" => $"第 {aPicked} 組（指定的）",
            _ => "第 1 組（只配了一組）",
        };
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bili_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            File.WriteAllText(aTmp, aText, new UTF8Encoding(false));
            List<string> aArgs = BuildBiliArgs(iReply, aTarget, aRpid, aRootId, aTmp, aDry);
            BiliRun x = RunBili(iRoot, aArgs, iArgs.GetInt("timeout", 90, out _));
            string aShown = "bili " + string.Join(" ", aArgs.Select(a => a == aTmp ? "<全文檔>" : a));

            if (!x.Started) return SCP_CmdResult.Fail(2, "✗ 沒有送出：`bili` 起不來（" + x.StartError + "）");
            if (x.TimedOut)
                return Unknown(aTarget, aDry, $"`{aShown}` 跑了 {iArgs.GetInt("timeout", 90, out _)} 秒沒結束，已停掉");
            if (!TryParseBili(x.Out, out bool aOk, out SCP_JsonData? aData, out string aCode, out string aMsg))
                return Unknown(aTarget, aDry, $"`{aShown}` 的輸出讀不懂（exit {x.Exit}）：{Tail(x.Out + " " + x.Err, 240)}");
            if (!aOk)
            {
                var f = SCP_CmdResult.Fail(1, $"✗ 確定沒發：`bili` 回 `{aCode}`（exit {x.Exit}）：{aMsg}");
                if (aCode is "risk_control" or "captcha_required")
                    f.Lines.Add("  ⛔ 風控／驗證碼 ⇒ 停下所有寫入、回報帳號主人；不重試、不換參數繞過。");
                else if (aCode == "rate_limited") f.Lines.Add("  ⇒ 等它說的秒數再來；⛔ 不硬闖。");
                else if (aCode is "not_logged_in" or "auth_expired") f.Lines.Add("  ⇒ 帳號主人重新掃碼（`Bilibili_Install` §3）。");
                f.AddValue("error_code", aCode).AddValue("bili_exit", x.Exit.ToString(CultureInfo.InvariantCulture));
                return f;
            }

            if (aDry)
            {
                var d = SCP_CmdResult.Success("🧪 dry-run：沒有送出、帳本沒有動。");
                d.Lines.Add($"- 會跑：`{aShown}`");
                d.Lines.Add($"- 署名：{aSigNote}");
                d.Lines.Add($"- 全文（{new StringInfo(aText).LengthInTextElements} 字）：");
                foreach (string l in aText.Split('\n')) d.Lines.Add("  > " + l);
                d.AddValue("dry_run", "1").AddValue("signature_index", aPicked.ToString(CultureInfo.InvariantCulture)).AddValue("signature_source", aSource);
                return d;
            }

            string aNewRpid;
            try { aNewRpid = aData!["rpid"].AsLong().ToString(CultureInfo.InvariantCulture); }
            catch { return Unknown(aTarget, false, "`bili` 說 ok，可是回傳裡沒有 rpid：" + Tail(x.Out, 240)); }

            // 先記帳再讀回：讀回失敗也不能讓這一則從帳本上消失（那樣下次會再發一次）
            aLedger!.Add(new CommentEntry
            {
                Rpid = aNewRpid, Target = TargetKey(aTarget), Kind = iReply ? "reply" : "comment", ParentRpid = iReply ? aRpid : "", RootRpid = iReply ? aRootId : "",
                Persona = iPersona, Signature = aSig, Text = aText, PostedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            });
            bool aSaved = TrySaveLedger(iRoot, aLedger, out string? aSaveErr);

            var r = SCP_CmdResult.Success($"✅ 已{(iReply ? "回覆" : "留言")}：{TargetKey(aTarget)}　rpid {aNewRpid}");
            r.Lines.Add($"- 署名：{aSigNote}");
            foreach (string l in aText.Split('\n')) r.Lines.Add("  > " + l);
            if (!aSaved) r.Lines.Add($"- ⚠ 帳本沒寫進去（{aSaveErr}）⇒ 這一則已經發了，請手動記下 rpid；下次的重複檢查看不到它");

            BiliRun c = RunBili(iRoot, new[] { "comment", "check", aTarget, aNewRpid }, 45);
            string aVisible = "unknown";
            if (c.Started && !c.TimedOut && TryParseBili(c.Out, out bool cOk, out SCP_JsonData? cData, out string cCode, out _) && cOk && cData != null)
            {
                bool aFound = cData.GetBool("found", false), aVis = cData.GetBool("visible", false), aFold = cData.GetBool("folded", false), aHid = cData.GetBool("hidden", false);
                aVisible = aVis ? "1" : "0";
                r.Lines.Add($"- 讀回（從這個帳號看）：{(aFound ? "找得到" : "**找不到**")}、{(aVis ? "看得到" : "**看不到**")}、{(aFold ? "**被折疊**" : "沒折疊")}、{(aHid ? "**被隱藏**" : "沒隱藏")}");
                r.AddValue("found", aFound ? "1" : "0").AddValue("folded", aFold ? "1" : "0").AddValue("hidden", aHid ? "1" : "0");
            }
            else r.Lines.Add($"- 讀回：**沒讀到**（`bili comment check {TargetKey(aTarget)} {aNewRpid}` 自己再跑一次）");
            r.AddValue("rpid", aNewRpid).AddValue("target", TargetKey(aTarget)).AddValue("visible", aVisible)
             .AddValue("signature_index", aPicked.ToString(CultureInfo.InvariantCulture)).AddValue("signature_source", aSource)
             .AddValue("ledger_saved", aSaved ? "1" : "0");
            return r;
        }
        finally { try { File.Delete(aTmp); } catch { /* 暫存檔刪不掉不影響結果 */ } }
    }

    static SCP_CmdResult Unknown(string iTarget, bool iDry, string iWhy)
    {
        if (iDry) return SCP_CmdResult.Fail(1, "✗ dry-run 沒跑完（本來就不會送出）：" + iWhy);
        var f = SCP_CmdResult.Fail(7, "⚠ **不知道**有沒有發出去：" + iWhy);
        f.Lines.Add($"  ⇒ 先讀回：`bili comment list {TargetKey(iTarget)}`（看有沒有這一則），⛔ 別直接重送；帳本沒有記這一則。");
        return f;
    }
}
