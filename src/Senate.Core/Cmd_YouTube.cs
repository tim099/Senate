// 區塊職責：`senate cmd youtube` —— persona 運營 YouTube 頻道（Blocksail 宣傳）：憑證、登入、上傳、改資料、列影片。
// 物理意義：底層（憑證、OAuth、HTTP）在 YouTubeClient.cs；本檔是交付單、檢查、上傳帳本與輸出。
//           方案與平台事實見 `Docs/Workflows/YouTube_Proposal.md`，用法見 `Docs/Workflows/YouTube.md`。
//           Tim 2026-11-09：「不需要太複雜的紀律 主要當成運營&遊戲宣傳」⇒ 沒有 confirm 關卡；`upload` 先跑檢查，沒過就不送。
// 數值影響：
//   · 公開度一律顯式送（API 沒給 privacyStatus ＝ 公開）⇒ 交付單的 `privacy` 必填、表外值擋。
//   · 帳本 `<secrets>/youtube_uploads.json` 記本工具上傳過的影片；`update` 只准改帳本裡的 ⇒ 頻道上的舊影片碰不到。
//     本工具也沒有刪除。
//   · 同一支檔案（SHA-256 相同）已經上傳過 ⇒ 擋下（`allow_duplicate=1` 才放），防重跑上傳兩次。
// ⚠ exit code：0 成功；1 API／連線失敗；2 被擋（參數、檢查、憑證不齊、帳本外的影片）；3 要重新登入。
#nullable enable
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Cmd;
using SCP.Core.Json;

namespace Senate.Core;

public sealed partial class Cmd_YouTube : SCP_Cmd
{
    public override string Name => "youtube";
    public override string Category => SCP_CmdCategory.Reading;

    public override string Summary => "YouTube 頻道運營：憑證（一步加密）、登入授權、上傳影片（交付單先檢查）、改標題／說明／公開度／縮圖、列頻道影片、播放清單（列出／建立／加影片）";

    public override string Details =>
        "· `op=status`（預設）：憑證與登入狀態、授權到的頻道、帳本筆數（不連網）。\n"
        + "· `op=set-credentials --arg client_path=<用戶端 JSON> --arg passphrase_path=<密碼檔> [--arg hint=] [--arg overwrite=1]`：⛔ 只收路徑。\n"
        + "· `op=login --arg passphrase_path=<密碼檔> [--arg timeout=300] [--arg open_browser=1]`：開瀏覽器 → Tim 登入、選頻道、同意 → refresh token 加密落檔。\n"
        + "· `op=whoami`：現抓頻道名、id、訂閱數（驗憑證通了）。\n"
        + "· `op=upload --arg slip_file=<交付單.json> [--arg dry_run=1] [--arg allow_duplicate=1] [--arg notify=0]`：先檢查，沒過不送。\n"
        + "· `op=update --arg video_id=<id> --arg slip_file=<只寫要改的欄位.json> [--arg dry_run=1]`：只准改本工具上傳過的影片。\n"
        + "· `op=list [--arg max=20]`：頻道最新影片＋公開度＋觀看數，標出哪些是 Senate 上傳的。\n"
        + "· `op=playlists`：頻道的播放清單＋公開度＋支數，標出哪些是 Senate 建的。\n"
        + "· `op=playlist-create --arg slip_file=<清單交付單.json> [--arg dry_run=1] [--arg allow_duplicate=1]`：建播放清單；同名的已經有了就擋。"
        + "交付單欄位：persona、title、privacy（必填）、description、videos（建好之後依序加進去的影片 id，只收本工具上傳的）。\n"
        + "· `op=playlist-add --arg playlist_id=<id> --arg video_ids=<id,id> [--arg dry_run=1]`：把本工具上傳的影片加進**本工具建的**清單；已經在裡面的跳過。\n"
        + "交付單欄位：persona、video、title、description、privacy（private／unlisted／public，必填）、tags、category_id（預設 20 遊戲）、"
        + "made_for_kids（預設 false）、synthetic_media（預設 false）、language、thumbnail、playlist_id。欄位表與限制看 `Docs/Workflows/YouTube.md`。";

    public override string Example => SCP_CmdRegistry.Invoke("youtube --arg op=upload --arg slip_file=D:/tmp/slip.json --arg dry_run=1");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "做什麼", iDefault: "status", iChoices: new[] { "status", "set-credentials", "login", "whoami", "upload", "update", "list", "playlists", "playlist-create", "playlist-add" }),
        new SCP_CmdArgSpec("client_path", "set-credentials：Google Cloud 下載的用戶端 JSON（電腦版應用程式）", iDefault: ""),
        new SCP_CmdArgSpec("passphrase_path", "set-credentials／login：放加密密碼的檔（⛔ 不收密碼本身）", iDefault: ""),
        new SCP_CmdArgSpec("hint", "set-credentials／login：密碼提示（不參與加密）", iDefault: ""),
        new SCP_CmdArgSpec("overwrite", "set-credentials：1＝允許覆寫既有的用戶端 .enc", iDefault: "0", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("timeout", "login：等瀏覽器回來的秒數", iDefault: "300"),
        new SCP_CmdArgSpec("open_browser", "login：1＝自動開瀏覽器；0＝只印網址", iDefault: "1", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("slip_file", "upload／update／playlist-create：交付單（JSON，絕對路徑）", iDefault: ""),
        new SCP_CmdArgSpec("video_id", "update：要改的影片 id（必須是本工具上傳過的）", iDefault: ""),
        new SCP_CmdArgSpec("dry_run", "upload／update／playlist-create／playlist-add：1＝只檢查並印出要送的內容，不送", iDefault: "0", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("allow_duplicate", "upload：1＝同一支檔案已上傳過也照傳；playlist-create：1＝頻道上已經有同名清單也照建", iDefault: "0", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("notify", "upload：1＝通知訂閱者（預設 1）", iDefault: "1", iChoices: new[] { "0", "1" }),
        new SCP_CmdArgSpec("upload_timeout", "upload：上傳檔案的逾時秒數", iDefault: "3600"),
        new SCP_CmdArgSpec("max", "list：最多列幾支（1–50）", iDefault: "20"),
        new SCP_CmdArgSpec("playlist_id", "playlist-add：要加進去的播放清單 id（必須是本工具建的）", iDefault: ""),
        new SCP_CmdArgSpec("video_ids", "playlist-add：影片 id，逗號分隔（必須是本工具上傳的）", iDefault: ""),
        new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（憑證住 <資料根>/Secret/）", iRequired: true),
        new SCP_CmdArgSpec("letters_root", "信件庫根（驗 persona 存在）", iDefault: ""),
    };

    public static readonly string[] Privacies = { "private", "unlisted", "public" };
    public const string DefaultCategoryId = "20";   // 20 ＝ Gaming
    public const int TitleMax = 100;
    public const int DescriptionMaxBytes = 5000;
    public const int TagsMaxTotal = 500;
    public const long ThumbnailMaxBytes = 50L * 1024 * 1024;

    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
    {
        string aRoot = iArgs.Get("data_root").Trim();
        if (aRoot.Length == 0 || !Directory.Exists(aRoot)) return SCP_CmdResult.Fail(2, $"✗ 資料根不存在：{aRoot}");
        return iArgs.Get("op").Trim() switch
        {
            "set-credentials" => SetCredentials(aRoot, iArgs),
            "login" => Login(aRoot, iArgs),
            "whoami" => WhoAmI(aRoot),
            "upload" => Upload(aRoot, iArgs),
            "update" => Update(aRoot, iArgs),
            "list" => List(aRoot, iArgs),
            "playlists" => Playlists(aRoot),
            "playlist-create" => PlaylistCreate(aRoot, iArgs),
            "playlist-add" => PlaylistAdd(aRoot, iArgs),
            _ => Status(aRoot),
        };
    }

    // ── status ──────────────────────────────────────────────────

    static SCP_CmdResult Status(string iRoot)
    {
        YouTubeCredStatus s = YouTubeClient.Status(iRoot);
        List<LedgerEntry>? aLedger = LoadLedger(iRoot, out string? aLedgerErr);
        var r = SCP_CmdResult.Success("# YouTube");
        r.Lines.Add($"- 用戶端：.enc {(s.ClientEncExists ? "有" : "**沒有**")}　明文 {(s.ClientReady ? "可用" : "**沒有**")}"
            + (s.ClientId.Length > 0 ? $"　client_id `{s.ClientId}`" : "") + (s.ClientReady ? "" : " ⇒ `op=set-credentials`"));
        r.Lines.Add($"- 登入：.enc {(s.TokenEncExists ? "有" : "**沒有**")}　明文 {(s.TokenReady ? "可用" : "**沒有**")}"
            + (s.TokenReady ? $"　頻道 **{s.ChannelTitle}**（{s.ChannelId}，{s.ObtainedAt} 授權）" : " ⇒ `op=login`"));
        if (s.TokenReady && s.ClientId.Length > 0 && s.TokenClientId.Length > 0 && s.TokenClientId != s.ClientId)
            r.Lines.Add("  ⚠ token 是舊用戶端拿的 ⇒ 重跑 `op=login`");
        if (s.Problem.Length > 0) r.Lines.Add("  ⚠ " + s.Problem);
        if (s.DirWarning.Length > 0) r.Lines.Add("  ⚠ " + s.DirWarning);
        r.Lines.Add(aLedger == null ? "- 上傳帳本：✗ " + aLedgerErr : $"- 上傳帳本：{aLedger.Count} 支（`{YouTubeClient.LedgerPath(iRoot)}`）");
        r.Lines.Add($"- 憑證資料夾：`{s.SecretsDir}`");
        r.AddValue("client_ready", s.ClientReady ? "1" : "0");
        r.AddValue("token_ready", s.TokenReady ? "1" : "0");
        r.AddValue("channel_id", s.ChannelId);
        r.AddValue("uploads", aLedger == null ? "" : aLedger.Count.ToString(CultureInfo.InvariantCulture));
        return r;
    }

    // ── set-credentials／login ──────────────────────────────────

    static bool TryReadPassphrase(SCP_CmdArgs iArgs, out string oPass, out string? oError)
    {
        oPass = ""; oError = null;
        string aPath = iArgs.Get("passphrase_path").Trim().Trim('"');
        if (aPath.Length == 0 || !File.Exists(aPath)) { oError = $"✗ passphrase_path 不存在：'{aPath}'（放加密密碼的純文字檔）"; return false; }
        oPass = File.ReadAllText(aPath, Encoding.UTF8).TrimEnd('\r', '\n').TrimStart('\uFEFF');
        if (oPass.Length == 0) { oError = "✗ 密碼檔是空的"; return false; }
        return true;
    }

    static SCP_CmdResult SetCredentials(string iRoot, SCP_CmdArgs iArgs)
    {
        string aClientPath = iArgs.Get("client_path").Trim().Trim('"');
        if (aClientPath.Length == 0 || !File.Exists(aClientPath)) return SCP_CmdResult.Fail(2, $"✗ client_path 不存在：'{aClientPath}'");
        if (!TryReadPassphrase(iArgs, out string aPass, out string? aErr)) return SCP_CmdResult.Fail(2, aErr!);
        string aJson = File.ReadAllText(aClientPath, Encoding.UTF8);
        if (!YouTubeClient.TrySetClient(iRoot, aJson, aPass, iArgs.Get("hint"), iArgs.Get("overwrite") == "1", out aErr))
            return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
        YouTubeCredStatus s = YouTubeClient.Status(iRoot);
        return SCP_CmdResult.Success($"✅ 用戶端已寫入（回讀 client_id `{s.ClientId}`）",
                $"- .enc：`{s.ClientEncPath}`",
                $"- 明文：`{s.ClientPlainPath}`（本機用；資料根的 .gitignore 擋掉 Secret/，⛔ 不會進公開 repo）",
                $"- 下載的原檔 `{aClientPath}` 沒動 —— 收好或刪掉都行，Senate 不再讀它",
                "- 下一步：`op=login`（開瀏覽器授權）")
            .AddValue("client_id", s.ClientId);
    }

    static SCP_CmdResult Login(string iRoot, SCP_CmdArgs iArgs)
    {
        if (!TryReadPassphrase(iArgs, out string aPass, out string? aErr)) return SCP_CmdResult.Fail(2, aErr!);
        int aTimeout = iArgs.GetInt("timeout", 300, out _);
        if (aTimeout < 30) aTimeout = 30;
        if (!YouTubeClient.TryLogin(iRoot, aPass, iArgs.Get("hint"), aTimeout, iArgs.Get("open_browser") != "0",
                l => Console.Error.WriteLine(l), out YouTubeClient.LoginResult aRes, out aErr))
            return SCP_CmdResult.Fail(2, "✗ 登入沒有完成：" + aErr);
        YouTubeCredStatus s = YouTubeClient.Status(iRoot);
        return SCP_CmdResult.Success($"✅ 已授權頻道 **{aRes.ChannelTitle}**（{aRes.ChannelId}）",
                $"- refresh token 已加密：`{s.TokenEncPath}`（明文 `{s.TokenPlainPath}`）" + (aRes.Replaced ? "（蓋掉了上一次的登入）" : ""),
                "- 之後上傳不用再登入；token 失效時指令會回 exit 3 並叫你重登")
            .AddValue("channel_id", aRes.ChannelId)
            .AddValue("channel_title", aRes.ChannelTitle);
    }

    static SCP_CmdResult? Access(string iRoot, out string oAccess)
    {
        if (YouTubeClient.TryAccessToken(iRoot, out oAccess, out bool aNeedLogin, out string? aErr)) return null;
        return SCP_CmdResult.Fail(aNeedLogin ? 3 : (aErr != null && aErr.Contains("set-credentials", StringComparison.Ordinal) ? 2 : 1), "✗ " + aErr);
    }

    // ── whoami ──────────────────────────────────────────────────

    static SCP_CmdResult WhoAmI(string iRoot)
    {
        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) return aFail;
        if (!YouTubeClient.TryGetJson(aAccess, "/channels?part=snippet,statistics&mine=true", out SCP_JsonData j, out string? aErr))
            return SCP_CmdResult.Fail(1, "✗ " + aErr);
        if (j["items"].Count == 0) return SCP_CmdResult.Fail(1, "✗ 授權的帳號底下沒有頻道");
        SCP_JsonData c = j["items"][0];
        SCP_JsonData st = c["statistics"];
        YouTubeCredStatus s = YouTubeClient.Status(iRoot);
        var r = SCP_CmdResult.Success($"✅ 頻道 **{c["snippet"].GetString("title", "")}**（{c.GetString("id", "")}）");
        string aSubs = st.GetBool("hiddenSubscriberCount", false) ? "（隱藏）" : st.GetString("subscriberCount", "?");
        r.Lines.Add($"- 訂閱 {aSubs}　影片 {st.GetString("videoCount", "?")}　總觀看 {st.GetString("viewCount", "?")}");
        if (c["snippet"].GetString("customUrl", "").Length > 0) r.Lines.Add($"- https://www.youtube.com/{c["snippet"].GetString("customUrl", "")}");
        if (s.ChannelId.Length > 0 && s.ChannelId != c.GetString("id", "")) r.Lines.Add($"  ⚠ 跟登入時記下的頻道（{s.ChannelId}）不一樣");
        r.AddValue("channel_id", c.GetString("id", ""));
        r.AddValue("channel_title", c["snippet"].GetString("title", ""));
        return r;
    }

    // ── 交付單 ──────────────────────────────────────────────────

    public sealed class Slip
    {
        public string Persona = "", Video = "", Title = "", Description = "", Privacy = "", CategoryId = "", Language = "", Thumbnail = "", PlaylistId = "";
        public List<string> Tags = new();
        public bool MadeForKids, SyntheticMedia;
        /// <summary>交付單裡實際寫了哪些欄位（update 只改這些）。</summary>
        public HashSet<string> Given = new(StringComparer.Ordinal);
    }

    static readonly string[] s_UploadKeys = { "persona", "video", "title", "description", "privacy", "tags", "category_id", "made_for_kids", "synthetic_media", "language", "thumbnail", "playlist_id" };
    static readonly string[] s_UpdateKeys = { "persona", "title", "description", "privacy", "tags", "category_id", "made_for_kids", "synthetic_media", "language", "thumbnail", "playlist_id" };

    /// <summary>讀交付單。欄位名打錯（例如 `privacy_status`）一律擋 —— 打錯的公開度會被當成沒給，而沒給 ＝ 公開。</summary>
    public static bool TryParseSlip(string iJson, bool iForUpdate, out Slip oSlip, out List<string> oErrors)
    {
        oSlip = new Slip();
        oErrors = new List<string>();
        SCP_JsonData j;
        try { j = SCP_JsonParser.Parse(iJson); }
        catch (Exception e) { oErrors.Add($"交付單不是 JSON：{e.Message}"); return false; }
        if (!j.IsObject) { oErrors.Add("交付單要是一個 JSON 物件"); return false; }
        string[] aAllowed = iForUpdate ? s_UpdateKeys : s_UploadKeys;
        foreach (string k in j.Keys)
        {
            if (Array.IndexOf(aAllowed, k) < 0)
            {
                oErrors.Add(iForUpdate && k == "video" ? "update 不能換影片檔（要換就重新上傳）" : $"不認得的欄位 `{k}`（可用：{string.Join("、", aAllowed)}）");
                continue;
            }
            oSlip.Given.Add(k);
            SCP_JsonData v = j[k];
            try
            {
                switch (k)
                {
                    case "tags":
                        if (!v.IsArray) { oErrors.Add("`tags` 要是字串陣列"); break; }
                        foreach (SCP_JsonData t in v) oSlip.Tags.Add(t.AsString().Trim());
                        break;
                    case "made_for_kids": oSlip.MadeForKids = v.AsBool(); break;
                    case "synthetic_media": oSlip.SyntheticMedia = v.AsBool(); break;
                    default:
                        string s = v.AsString();
                        switch (k)
                        {
                            case "persona": oSlip.Persona = s.Trim(); break;
                            case "video": oSlip.Video = s.Trim(); break;
                            case "title": oSlip.Title = s.Trim(); break;
                            case "description": oSlip.Description = s; break;
                            case "privacy": oSlip.Privacy = s.Trim(); break;
                            case "category_id": oSlip.CategoryId = s.Trim(); break;
                            case "language": oSlip.Language = s.Trim(); break;
                            case "thumbnail": oSlip.Thumbnail = s.Trim(); break;
                            case "playlist_id": oSlip.PlaylistId = s.Trim(); break;
                        }
                        break;
                }
            }
            catch (SCP_JsonTypeException) { oErrors.Add($"`{k}` 的型別不對（{(k is "made_for_kids" or "synthetic_media" ? "要 true／false" : "要字串")}）"); }
        }
        if (!iForUpdate && !oSlip.Given.Contains("category_id")) oSlip.CategoryId = DefaultCategoryId;
        return oErrors.Count == 0;
    }

    static readonly Regex s_Lang = new(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant);

    /// <summary>形式檢查（零網路）。errors 非空 ⇒ 不送。</summary>
    public static (List<string> Errors, List<string> Warns) Lint(Slip s, bool iForUpdate, string iLettersRoot)
    {
        var e = new List<string>();
        var w = new List<string>();
        bool Has(string k) => !iForUpdate || s.Given.Contains(k);

        if (!iForUpdate || s.Given.Contains("persona"))
        {
            if (s.Persona.Length == 0) e.Add("`persona` 必填（誰做的這支）");
            else if (iLettersRoot.Length > 0 && !Directory.Exists(Path.Combine(iLettersRoot, s.Persona))) e.Add($"persona `{s.Persona}` 不存在（信件庫裡沒有這個人）");
        }
        if (!iForUpdate)
        {
            if (s.Video.Length == 0) e.Add("`video` 必填（影片檔的絕對路徑）");
            else if (!Path.IsPathRooted(s.Video)) e.Add($"`video` 要絕對路徑：{s.Video}");
            else if (!File.Exists(s.Video)) e.Add($"影片檔不存在：{s.Video}");
            else if (!YouTubeClient.IsVideoFile(s.Video)) e.Add($"不認得的影片副檔名：{Path.GetExtension(s.Video)}（mp4／mov／webm／mkv…）");
            else if (new FileInfo(s.Video).Length == 0) e.Add("影片檔是空的");
        }
        if (Has("title"))
        {
            if (s.Title.Length == 0) e.Add("`title` 必填");
            else if (s.Title.Length > TitleMax) e.Add($"`title` {s.Title.Length} 字，上限 {TitleMax}");
            if (s.Title.IndexOfAny(new[] { '<', '>' }) >= 0) e.Add("`title` 不能有 < 或 >（YouTube 會拒收）");
        }
        if (Has("description"))
        {
            int aBytes = Encoding.UTF8.GetByteCount(s.Description);
            if (aBytes > DescriptionMaxBytes) e.Add($"`description` {aBytes} bytes，上限 {DescriptionMaxBytes}（中文一個字 3 bytes）");
            if (s.Description.IndexOfAny(new[] { '<', '>' }) >= 0) e.Add("`description` 不能有 < 或 >（YouTube 會拒收）");
            if (!iForUpdate && s.Description.Trim().Length == 0) w.Add("沒有說明 —— 宣傳片建議附遊戲連結與一兩句介紹");
        }
        if (Has("privacy"))
        {
            if (s.Privacy.Length == 0) e.Add("`privacy` 必填（private／unlisted／public）—— API 沒給公開度就是**公開**，所以一定要寫明");
            else if (Array.IndexOf(Privacies, s.Privacy) < 0) e.Add($"`privacy` 只能是 private／unlisted／public，不是 `{s.Privacy}`");
        }
        if (s.Given.Contains("tags"))
        {
            if (s.Tags.Any(t => t.Length == 0)) e.Add("`tags` 裡有空字串");
            if (s.Tags.Any(t => t.IndexOfAny(new[] { '<', '>', ',' }) >= 0)) e.Add("`tags` 不能有 < > 或逗號（一個標籤一格）");
            int aTotal = TagsTotal(s.Tags);
            if (aTotal > TagsMaxTotal) e.Add($"`tags` 合計 {aTotal} 字元，上限 {TagsMaxTotal}（含空白的標籤多算 2，標籤之間各算 1）");
        }
        if (Has("category_id") && s.CategoryId.Length > 0 && !s.CategoryId.All(char.IsAsciiDigit)) e.Add($"`category_id` 要是數字（20＝遊戲），不是 `{s.CategoryId}`");
        if (s.Given.Contains("language") && s.Language.Length > 0 && !s_Lang.IsMatch(s.Language)) e.Add($"`language` 看起來不是語言代碼：`{s.Language}`（例：zh-Hant、en、ja）");
        if (s.Given.Contains("thumbnail") && s.Thumbnail.Length > 0)
        {
            string ext = Path.GetExtension(s.Thumbnail).ToLowerInvariant();
            if (!Path.IsPathRooted(s.Thumbnail)) e.Add($"`thumbnail` 要絕對路徑：{s.Thumbnail}");
            else if (!File.Exists(s.Thumbnail)) e.Add($"縮圖不存在：{s.Thumbnail}");
            else if (ext is not (".png" or ".jpg" or ".jpeg")) e.Add($"縮圖要 png 或 jpg，不是 {ext}");
            else if (new FileInfo(s.Thumbnail).Length > ThumbnailMaxBytes) e.Add("縮圖超過 50 MB");
        }
        if (s.Given.Contains("synthetic_media") && s.SyntheticMedia) w.Add("標了 AI 合成揭露（synthetic_media）—— 只有擬真人聲、擬真人像才需要；遊戲畫面、像素、體素不用");
        if (iForUpdate && s.Given.Count == 0) e.Add("交付單沒有任何要改的欄位");
        return (e, w);
    }

    public static int TagsTotal(IReadOnlyList<string> iTags)
        => iTags.Count == 0 ? 0 : iTags.Sum(t => t.Length + (t.Contains(' ') ? 2 : 0)) + iTags.Count - 1;

    /// <summary>上傳用的 metadata（snippet ＋ status）。⚠ privacyStatus 一律顯式送。</summary>
    public static SCP_JsonData BuildUploadMeta(Slip s)
    {
        var sn = SCP_JsonData.NewObject();
        sn.Set("title", s.Title);
        sn.Set("description", s.Description);
        if (s.Tags.Count > 0) { var t = SCP_JsonData.NewArray(); foreach (string x in s.Tags) t.Add(x); sn.Set("tags", t); }
        sn.Set("categoryId", s.CategoryId.Length > 0 ? s.CategoryId : DefaultCategoryId);
        if (s.Language.Length > 0) { sn.Set("defaultLanguage", s.Language); sn.Set("defaultAudioLanguage", s.Language); }
        var st = SCP_JsonData.NewObject();
        st.Set("privacyStatus", s.Privacy);
        st.Set("selfDeclaredMadeForKids", s.MadeForKids);
        st.Set("containsSyntheticMedia", s.SyntheticMedia);
        var j = SCP_JsonData.NewObject();
        j.Set("snippet", sn);
        j.Set("status", st);
        return j;
    }

    static SCP_CmdResult? LoadSlipFile(SCP_CmdArgs iArgs, bool iForUpdate, out Slip oSlip)
    {
        oSlip = new Slip();
        string aPath = iArgs.Get("slip_file").Trim().Trim('"');
        if (aPath.Length == 0 || !File.Exists(aPath)) return SCP_CmdResult.Fail(2, $"✗ slip_file 不存在：'{aPath}'");
        if (!TryParseSlip(File.ReadAllText(aPath, Encoding.UTF8), iForUpdate, out oSlip, out List<string> aErr))
        {
            var r = SCP_CmdResult.Fail(2, "✗ 交付單讀不了：");
            foreach (string x in aErr) r.Lines.Add("  - " + x);
            return r;
        }
        return null;
    }

    static void AddLint(SCP_CmdResult r, List<string> e, List<string> w)
    {
        r.Lines.Add($"## 檢查：{(e.Count == 0 ? "✓ 通過" : $"✗ {e.Count} 個錯誤")}{(w.Count > 0 ? $"，⚠ {w.Count} 個提醒" : "")}");
        foreach (string x in e) r.Lines.Add("- ✗ " + x);
        foreach (string x in w) r.Lines.Add("- ⚠ " + x);
        r.AddValue("lint_errors", e.Count.ToString(CultureInfo.InvariantCulture));
    }

    // ── upload ──────────────────────────────────────────────────

    static SCP_CmdResult Upload(string iRoot, SCP_CmdArgs iArgs)
    {
        if (LoadSlipFile(iArgs, false, out Slip s) is SCP_CmdResult aBad) return aBad;
        var (e, w) = Lint(s, false, iArgs.Get("letters_root").Trim());
        List<LedgerEntry>? aLedger = LoadLedger(iRoot, out string? aLedgerErr);
        if (aLedger == null) e.Add("上傳帳本讀不了（⛔ 不蓋掉它）：" + aLedgerErr);
        string aSha = "";
        if (e.Count == 0)
        {
            aSha = Sha256(s.Video);
            LedgerEntry? aDup = aLedger!.FirstOrDefault(x => x.FileSha256 == aSha);
            if (aDup != null && iArgs.Get("allow_duplicate") != "1")
                e.Add($"同一支檔案已經上傳過：{aDup.VideoId}「{aDup.Title}」（{aDup.UploadedAt}，{aDup.Persona}）⇒ 真的要再傳一次加 `--arg allow_duplicate=1`");
        }
        SCP_JsonData aMeta = BuildUploadMeta(s);
        var r = new SCP_CmdResult();
        r.Lines.Add($"# YouTube 上傳：{s.Title}");
        AddLint(r, e, w);
        if (e.Count > 0) { r.ExitCode = 2; r.Lines.Add("⛔ 沒有送出"); return r; }

        if (iArgs.Get("dry_run") == "1")
        {
            r.Lines.Add($"## 將送的內容（dry_run ⇒ 沒送）");
            r.Lines.Add($"- 檔案：`{s.Video}`（{new FileInfo(s.Video).Length / 1048576.0:0.0} MB）");
            r.Lines.Add("```json");
            r.Lines.Add(aMeta.ToJson(true));
            r.Lines.Add("```");
            if (s.Thumbnail.Length > 0) r.Lines.Add($"- 縮圖：`{s.Thumbnail}`");
            if (s.PlaylistId.Length > 0) r.Lines.Add($"- 播放清單：`{s.PlaylistId}`");
            return r;
        }

        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) { aFail.Lines.InsertRange(0, r.Lines); return aFail; }
        int aUpTimeout = Math.Max(60, iArgs.GetInt("upload_timeout", 3600, out _));
        if (!YouTubeClient.TryUploadVideo(aAccess, s.Video, aMeta.ToJson(false), iArgs.Get("notify") != "0", aUpTimeout, out SCP_JsonData v, out string? aErr))
        {
            r.ExitCode = 1;
            r.Lines.Add("✗ " + aErr);
            return r;
        }
        string aId = v.GetString("id", "");
        if (aId.Length == 0) { r.ExitCode = 1; r.Lines.Add("✗ 上傳回應裡沒有影片 id —— 到 YouTube Studio 看有沒有多一支"); return r; }

        // 先記帳再做縮圖與播放清單：後面失敗也不能讓這支變成「帳本外」的影片（那樣就改不了它了）。
        aLedger!.Add(new LedgerEntry
        {
            VideoId = aId, Persona = s.Persona, Title = s.Title, Privacy = s.Privacy,
            FileName = Path.GetFileName(s.Video), FileSha256 = aSha,
            UploadedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        });
        if (!TrySaveLedger(iRoot, aLedger, out string? aSaveErr)) r.Lines.Add($"⚠ 影片傳上去了，但帳本寫不進去（之後 update 會被擋）：{aSaveErr}");

        string aUrl = "https://youtu.be/" + aId;
        r.Lines.Add($"✅ 已上傳：{aUrl}");
        AfterExtras(r, aAccess, aId, s);
        Readback(r, aAccess, aId, s.Given.Contains("privacy") ? s.Privacy : "");
        r.AddValue("video_id", aId);
        r.AddValue("url", aUrl);
        r.AddValue("privacy", s.Privacy);
        return r;
    }

    static void AfterExtras(SCP_CmdResult r, string iAccess, string iId, Slip s)
    {
        if (s.Thumbnail.Length > 0)
            r.Lines.Add(YouTubeClient.TrySetThumbnail(iAccess, iId, s.Thumbnail, out string? aThErr) ? "- 縮圖：✓ 已設定" : "- ⚠ 縮圖沒設成：" + aThErr);
        if (s.PlaylistId.Length > 0)
            r.Lines.Add(YouTubeClient.TryAddToPlaylist(iAccess, s.PlaylistId, iId, out string? aPlErr) ? $"- 播放清單：✓ 已加入 `{s.PlaylistId}`" : "- ⚠ 沒加進播放清單：" + aPlErr);
    }

    public const int ReadbackTries = 6;
    public const int ReadbackWaitMs = 2500;

    /// <summary>
    /// 回讀：照 id 再抓一次，印公開度與處理狀態（上傳完通常還在 processing）。
    /// 🩸 2026-11-09：update 把公開度改成 public 後立刻回讀，API 還回舊的 unlisted；10 秒後才讀到 public
    ///   ⇒ 首版當場印「⚠ 跟交付單不一樣」，其實改成功了。讀取端有延遲 ⇒ 對不上（或還查不到這支）就等一下再讀，
    ///   最多 <see cref="ReadbackTries"/> 次；等完還是對不上才報 ⚠（那時才算真的不一樣）。
    /// </summary>
    static void Readback(SCP_CmdResult r, string iAccess, string iId, string iExpectPrivacy)
    {
        SCP_JsonData? aItem = null;
        string aPriv = "?";
        int aTry = 0;
        while (true)
        {
            aTry++;
            if (!YouTubeClient.TryGetJson(iAccess, "/videos?part=status,snippet&id=" + Uri.EscapeDataString(iId), out SCP_JsonData j, out string? aErr))
            { r.Lines.Add("- ⚠ 回讀失敗：" + aErr); return; }
            aItem = j["items"].Count > 0 ? j["items"][0] : null;
            aPriv = aItem == null ? "" : aItem["status"].GetString("privacyStatus", "?");
            bool aSettled = aItem != null && (iExpectPrivacy.Length == 0 || aPriv == iExpectPrivacy);
            if (aSettled || aTry >= ReadbackTries) break;
            Thread.Sleep(ReadbackWaitMs);
        }
        string aWaited = aTry > 1 ? $"（第 {aTry} 次讀，等了 {(aTry - 1) * ReadbackWaitMs / 1000.0:0.#} 秒）" : "";
        if (aItem == null) { r.Lines.Add($"- ⚠ 回讀查不到這支{aWaited} —— 剛上傳可能要再等一下，晚點用 `op=list` 看"); return; }
        SCP_JsonData st = aItem["status"];
        r.Lines.Add($"- 回讀：公開度 **{aPriv}**　處理狀態 {st.GetString("uploadStatus", "?")}　標題「{aItem["snippet"].GetString("title", "")}」{aWaited}");
        if (iExpectPrivacy.Length > 0 && aPriv != iExpectPrivacy)
            r.Lines.Add($"  ⚠ 等了 {(aTry - 1) * ReadbackWaitMs / 1000.0:0.#} 秒還是 `{aPriv}`，跟交付單寫的 `{iExpectPrivacy}` 不一樣 —— 晚點用 `op=list` 再看一次；還是不一樣就是沒改成");
        string aReject = st.GetString("rejectionReason", "");
        if (aReject.Length > 0) r.Lines.Add($"  ⚠ 被拒：{aReject}");
    }

    // ── update ──────────────────────────────────────────────────

    static readonly string[] s_SnippetWritable = { "title", "description", "tags", "categoryId", "defaultLanguage", "defaultAudioLanguage" };
    static readonly string[] s_StatusWritable = { "privacyStatus", "embeddable", "license", "publicStatsViewable", "publishAt", "selfDeclaredMadeForKids", "containsSyntheticMedia" };

    static SCP_CmdResult Update(string iRoot, SCP_CmdArgs iArgs)
    {
        string aId = iArgs.Get("video_id").Trim();
        if (aId.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=update 要給 --arg video_id=<id>");
        List<LedgerEntry>? aLedger = LoadLedger(iRoot, out string? aLedgerErr);
        if (aLedger == null) return SCP_CmdResult.Fail(2, "✗ 上傳帳本讀不了：" + aLedgerErr);
        LedgerEntry? aMine = aLedger.FirstOrDefault(x => x.VideoId == aId);
        if (aMine == null) return SCP_CmdResult.Fail(2, $"✗ `{aId}` 不是本工具上傳的影片 ⇒ 不准改（頻道上原本的影片一律不碰；`op=list` 看哪些是）");
        if (LoadSlipFile(iArgs, true, out Slip s) is SCP_CmdResult aBad) return aBad;
        var (e, w) = Lint(s, true, iArgs.Get("letters_root").Trim());
        var r = new SCP_CmdResult();
        r.Lines.Add($"# YouTube 修改：{aId}「{aMine.Title}」");
        AddLint(r, e, w);
        if (e.Count > 0) { r.ExitCode = 2; r.Lines.Add("⛔ 沒有送出"); return r; }

        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) { aFail.Lines.InsertRange(0, r.Lines); return aFail; }
        if (!YouTubeClient.TryGetJson(aAccess, "/videos?part=snippet,status&id=" + Uri.EscapeDataString(aId), out SCP_JsonData cur, out string? aErr))
        { r.ExitCode = 1; r.Lines.Add("✗ 讀不到現況：" + aErr); return r; }
        if (cur["items"].Count == 0) { r.ExitCode = 1; r.Lines.Add("✗ 頻道上查不到這支（被刪了？）"); return r; }

        // videos.update 會把**沒送的欄位清掉** ⇒ 先抄現況的可寫欄位，再蓋上交付單寫的。
        SCP_JsonData aCurSn = cur["items"][0]["snippet"], aCurSt = cur["items"][0]["status"];
        var sn = SCP_JsonData.NewObject();
        foreach (string k in s_SnippetWritable) if (aCurSn.Contains(k)) sn.Set(k, SCP_JsonParser.Parse(aCurSn[k].ToJson(false)));
        var st = SCP_JsonData.NewObject();
        foreach (string k in s_StatusWritable) if (aCurSt.Contains(k)) st.Set(k, SCP_JsonParser.Parse(aCurSt[k].ToJson(false)));
        if (!st.Contains("selfDeclaredMadeForKids") && aCurSt.Contains("madeForKids")) st.Set("selfDeclaredMadeForKids", aCurSt["madeForKids"].AsBool());
        if (s.Given.Contains("title")) sn.Set("title", s.Title);
        if (s.Given.Contains("description")) sn.Set("description", s.Description);
        if (s.Given.Contains("tags")) { var t = SCP_JsonData.NewArray(); foreach (string x in s.Tags) t.Add(x); sn.Set("tags", t); }
        if (s.Given.Contains("category_id")) sn.Set("categoryId", s.CategoryId);
        if (s.Given.Contains("language")) { sn.Set("defaultLanguage", s.Language); sn.Set("defaultAudioLanguage", s.Language); }
        if (s.Given.Contains("privacy")) st.Set("privacyStatus", s.Privacy);
        if (s.Given.Contains("made_for_kids")) st.Set("selfDeclaredMadeForKids", s.MadeForKids);
        if (s.Given.Contains("synthetic_media")) st.Set("containsSyntheticMedia", s.SyntheticMedia);
        if (!st.Contains("privacyStatus")) { r.ExitCode = 1; r.Lines.Add("✗ 現況讀不到公開度 —— 不送（送出去會被當成公開）"); return r; }
        var body = SCP_JsonData.NewObject();
        body.Set("id", aId);
        body.Set("snippet", sn);
        body.Set("status", st);

        if (iArgs.Get("dry_run") == "1")
        {
            r.Lines.Add("## 將送的內容（dry_run ⇒ 沒送）");
            r.Lines.Add("```json");
            r.Lines.Add(body.ToJson(true));
            r.Lines.Add("```");
            return r;
        }
        if (!YouTubeClient.TrySendJson(aAccess, HttpMethod.Put, "/videos?part=snippet,status", body.ToJson(false), out _, out aErr))
        { r.ExitCode = 1; r.Lines.Add("✗ " + aErr); return r; }
        r.Lines.Add($"✅ 已修改：https://youtu.be/{aId}（{string.Join("、", s.Given.Where(k => k is not "thumbnail" and not "playlist_id" and not "persona"))}）");
        AfterExtras(r, aAccess, aId, s);

        if (s.Given.Contains("title")) aMine.Title = s.Title;
        if (s.Given.Contains("privacy")) aMine.Privacy = s.Privacy;
        if (!TrySaveLedger(iRoot, aLedger, out string? aSaveErr)) r.Lines.Add("⚠ 帳本沒更新：" + aSaveErr);
        Readback(r, aAccess, aId, s.Given.Contains("privacy") ? s.Privacy : "");
        r.AddValue("video_id", aId);
        return r;
    }

    // ── list ────────────────────────────────────────────────────

    static SCP_CmdResult List(string iRoot, SCP_CmdArgs iArgs)
    {
        int aMax = Math.Clamp(iArgs.GetInt("max", 20, out _), 1, 50);
        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) return aFail;
        if (!YouTubeClient.TryGetJson(aAccess, "/channels?part=snippet,contentDetails&mine=true", out SCP_JsonData ch, out string? aErr)) return SCP_CmdResult.Fail(1, "✗ " + aErr);
        if (ch["items"].Count == 0) return SCP_CmdResult.Fail(1, "✗ 授權的帳號底下沒有頻道");
        string aUploads = ch["items"][0]["contentDetails"]["relatedPlaylists"].GetString("uploads", "");
        string aTitle = ch["items"][0]["snippet"].GetString("title", "");
        if (aUploads.Length == 0) return SCP_CmdResult.Fail(1, "✗ 讀不到頻道的「上傳」清單");
        if (!YouTubeClient.TryGetJson(aAccess, $"/playlistItems?part=contentDetails&maxResults={aMax}&playlistId={Uri.EscapeDataString(aUploads)}", out SCP_JsonData pl, out aErr))
            return SCP_CmdResult.Fail(1, "✗ " + aErr);
        var aIds = new List<string>();
        foreach (SCP_JsonData it in pl["items"]) { string id = it["contentDetails"].GetString("videoId", ""); if (id.Length > 0) aIds.Add(id); }
        var r = SCP_CmdResult.Success($"# 頻道 {aTitle} 最新 {aIds.Count} 支");
        r.AddValue("count", aIds.Count.ToString(CultureInfo.InvariantCulture));
        if (aIds.Count == 0) return r;
        if (!YouTubeClient.TryGetJson(aAccess, "/videos?part=snippet,status,statistics&id=" + string.Join(",", aIds), out SCP_JsonData vs, out aErr))
            return SCP_CmdResult.Fail(1, "✗ " + aErr);
        Dictionary<string, LedgerEntry> aMine = (LoadLedger(iRoot, out _) ?? new List<LedgerEntry>()).ToDictionary(x => x.VideoId, x => x, StringComparer.Ordinal);
        r.Lines.Add("| id | 標題 | 公開度 | 狀態 | 觀看 | 讚 | 留言 | Senate |");
        r.Lines.Add("|---|---|---|---|---|---|---|---|");
        foreach (SCP_JsonData v in vs["items"])
        {
            string id = v.GetString("id", "");
            SCP_JsonData st = v["statistics"];
            string aWho = aMine.TryGetValue(id, out LedgerEntry? m) ? m.Persona : "—";
            r.Lines.Add($"| {id} | {v["snippet"].GetString("title", "").Replace("|", "／")} | {v["status"].GetString("privacyStatus", "?")} | {v["status"].GetString("uploadStatus", "?")}"
                + $" | {st.GetString("viewCount", "?")} | {st.GetString("likeCount", "?")} | {st.GetString("commentCount", "?")} | {aWho} |");
        }
        r.Lines.Add("（Senate 欄是「—」的是頻道原本的影片：`op=update` 不准改）");
        return r;
    }

    // ── 上傳帳本 ────────────────────────────────────────────────

    public sealed class LedgerEntry
    {
        public string VideoId = "", Persona = "", Title = "", Privacy = "", FileName = "", FileSha256 = "", UploadedAt = "";
    }

    /// <summary>讀帳本。檔案不存在 ⇒ 空清單；讀不懂 ⇒ null ＋ 原因（⛔ 呼叫端不得拿空清單蓋掉它）。</summary>
    public static List<LedgerEntry>? LoadLedger(string iRoot, out string? oError)
    {
        oError = null;
        string aPath = YouTubeClient.LedgerPath(iRoot);
        if (!File.Exists(aPath)) return new List<LedgerEntry>();
        try
        {
            SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
            var aList = new List<LedgerEntry>();
            foreach (SCP_JsonData x in j["uploads"])
                aList.Add(new LedgerEntry
                {
                    VideoId = x["video_id"].AsString(), Persona = x.GetString("persona", ""), Title = x.GetString("title", ""),
                    Privacy = x.GetString("privacy", ""), FileName = x.GetString("file_name", ""), FileSha256 = x.GetString("file_sha256", ""),
                    UploadedAt = x.GetString("uploaded_at", ""),
                });
            return aList;
        }
        catch (Exception e) { oError = $"{aPath}：{e.Message}"; return null; }
    }

    static bool TrySaveLedger(string iRoot, List<LedgerEntry> iList, out string? oError)
    {
        oError = null;
        var arr = SCP_JsonData.NewArray();
        foreach (LedgerEntry x in iList)
        {
            var o = SCP_JsonData.NewObject();
            o.Set("video_id", x.VideoId);
            o.Set("persona", x.Persona);
            o.Set("title", x.Title);
            o.Set("privacy", x.Privacy);
            o.Set("file_name", x.FileName);
            o.Set("file_sha256", x.FileSha256);
            o.Set("uploaded_at", x.UploadedAt);
            arr.Add(o);
        }
        var j = SCP_JsonData.NewObject();
        j.Set("uploads", arr);
        try
        {
            string aPath = YouTubeClient.LedgerPath(iRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            YouTubeClient.WriteReplace(aPath, new UTF8Encoding(false).GetBytes(j.ToJson(true)));
            return true;
        }
        catch (Exception e) { oError = e.Message; return false; }
    }

    static string Sha256(string iPath)
    {
        using FileStream fs = File.OpenRead(iPath);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}
