// 區塊職責：`senate cmd youtube` 的播放清單那一半 —— 列出（op=playlists）、建立（op=playlist-create）、把影片加進去（op=playlist-add）。
// 物理意義：Tim 2026-11-10：「YT跟B站 是否可以把Senate相關做成系列」「工具加建清單指令」。
//           原本只有交付單的 `playlist_id`（把影片加進**已經存在**的清單），清單本身得有人到 YouTube 網頁上建。
// 數值影響：
//   · 紀律跟影片那一半同一條：**只碰本工具建的清單、只加本工具上傳的影片**（頻道上原本的清單與影片一律不動）。
//     本工具建過哪些清單記在 `<secrets>/youtube_playlists.json`（跟上傳帳本同一個資料夾、分開一個檔 —— 不去改上傳帳本的格式）。
//   · 建立前先讀頻道現有的清單：同名的已經有了 ⇒ 擋下（`allow_duplicate=1` 才照建）。🩸 重跑一次指令就多一個同名清單，而 API 不會擋。
//   · 加影片前先讀清單裡已經有誰：已經在的跳過（playlistItems.insert 不會擋重複，同一支會出現兩次）。
//   · `dry_run=1` ⇒ 只檢查、只印要送的內容；⛔ 不連網寫入（列清單那一步是唯讀的，照做，才知道會不會撞名）。
//   · 🩸 2026-11-10 第一次實跑：清單剛建好就加影片，三支裡第一支回 `HTTP 409 SERVICE_UNAVAILABLE：The operation was aborted`，後兩支成功
//     ⇒ 加影片遇到 409 等 2 秒重試（最多 3 次）。補加的那一支會排到最後 ⇒ `playlist-add` 多一個 `position`（插在第幾個）。
//   · 回讀印**清單裡實際的順序**（讀 playlistItems）—— 清單本身的 itemCount 會慢半拍（那次建完立刻讀是 1，實際已經 2 支）。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Json;

namespace Senate.Core;

public sealed partial class Cmd_YouTube
{
    public const string PlaylistLedgerFileName = "youtube_playlists.json";
    public const int PlaylistTitleMax = 150;
    public const int PlaylistPageMax = 50;

    public static string PlaylistLedgerPath(string iDataRoot) => YouTubeClient.SecretsDir(iDataRoot, out _) + "/" + PlaylistLedgerFileName;

    // ── 交付單 ──────────────────────────────────────────────────

    public sealed class PlaylistSlip
    {
        public string Persona = "", Title = "", Description = "", Privacy = "";
        public List<string> Videos = new();
    }

    static readonly string[] s_PlaylistKeys = { "persona", "title", "description", "privacy", "videos" };

    /// <summary>讀建清單的交付單。欄位名打錯一律擋（跟影片的交付單同一條：打錯的公開度會被當成沒給）。</summary>
    public static bool TryParsePlaylistSlip(string iJson, out PlaylistSlip oSlip, out List<string> oErrors)
    {
        oSlip = new PlaylistSlip();
        oErrors = new List<string>();
        SCP_JsonData j;
        try { j = SCP_JsonParser.Parse(iJson); }
        catch (Exception e) { oErrors.Add($"交付單不是 JSON：{e.Message}"); return false; }
        if (!j.IsObject) { oErrors.Add("交付單要是一個 JSON 物件"); return false; }
        foreach (string k in j.Keys)
        {
            if (Array.IndexOf(s_PlaylistKeys, k) < 0) { oErrors.Add($"不認得的欄位 `{k}`（可用：{string.Join("、", s_PlaylistKeys)}）"); continue; }
            SCP_JsonData v = j[k];
            try
            {
                switch (k)
                {
                    case "videos":
                        if (!v.IsArray) { oErrors.Add("`videos` 要是影片 id 的字串陣列"); break; }
                        foreach (SCP_JsonData t in v) oSlip.Videos.Add(t.AsString().Trim());
                        break;
                    case "persona": oSlip.Persona = v.AsString().Trim(); break;
                    case "title": oSlip.Title = v.AsString().Trim(); break;
                    case "description": oSlip.Description = v.AsString(); break;
                    case "privacy": oSlip.Privacy = v.AsString().Trim(); break;
                }
            }
            catch (SCP_JsonTypeException) { oErrors.Add($"`{k}` 的型別不對（{(k == "videos" ? "要字串陣列" : "要字串")}）"); }
        }
        return oErrors.Count == 0;
    }

    /// <summary>形式檢查（零網路）。`iUploadedIds` ＝ 上傳帳本裡的影片 id；null ⇒ 帳本讀不了（有寫 videos 就擋）。</summary>
    public static List<string> LintPlaylist(PlaylistSlip s, string iLettersRoot, ICollection<string>? iUploadedIds)
    {
        var e = new List<string>();
        if (s.Persona.Length == 0) e.Add("`persona` 必填（誰建的這個清單）");
        else if (iLettersRoot.Length > 0 && !Directory.Exists(Path.Combine(iLettersRoot, s.Persona))) e.Add($"persona `{s.Persona}` 不存在（信件庫裡沒有這個人）");
        if (s.Title.Length == 0) e.Add("`title` 必填");
        else
        {
            if (s.Title.Length > PlaylistTitleMax) e.Add($"`title` {s.Title.Length} 字，超過 {PlaylistTitleMax}");
            if (s.Title.IndexOfAny(new[] { '<', '>' }) >= 0) e.Add("`title` 不能有 `<` `>`");
        }
        int aBytes = Encoding.UTF8.GetByteCount(s.Description);
        if (aBytes > DescriptionMaxBytes) e.Add($"`description` {aBytes} bytes，超過 {DescriptionMaxBytes}");
        if (s.Description.IndexOfAny(new[] { '<', '>' }) >= 0) e.Add("`description` 不能有 `<` `>`");
        if (s.Privacy.Length == 0) e.Add("`privacy` 必填（private／unlisted／public）—— API 沒給公開度就是**公開**");
        else if (Array.IndexOf(Privacies, s.Privacy) < 0) e.Add($"`privacy` 要是 {string.Join("／", Privacies)} 其中一個（收到 `{s.Privacy}`）");
        LintVideoIds(s.Videos, iUploadedIds, "`videos`", e);
        return e;
    }

    /// <summary>影片 id 清單：不能空字串、不能重複、每一支都要是本工具上傳過的。</summary>
    public static void LintVideoIds(List<string> iIds, ICollection<string>? iUploadedIds, string iWhat, List<string> e)
    {
        if (iIds.Count == 0) return;
        if (iIds.Any(x => x.Length == 0)) e.Add($"{iWhat} 裡有空字串");
        foreach (string d in iIds.Where(x => x.Length > 0).GroupBy(x => x, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key))
            e.Add($"{iWhat} 裡 `{d}` 寫了不只一次");
        if (iUploadedIds == null) { e.Add($"上傳帳本讀不了 ⇒ 驗不了 {iWhat} 是不是本工具上傳的"); return; }
        foreach (string id in iIds.Where(x => x.Length > 0).Distinct(StringComparer.Ordinal))
            if (!iUploadedIds.Contains(id)) e.Add($"`{id}` 不是本工具上傳的影片 ⇒ 不准加（頻道上原本的影片一律不碰；`op=list` 看哪些是）");
    }

    // ── 本工具建過的清單（帳本） ────────────────────────────────

    public sealed class PlaylistEntry
    {
        public string PlaylistId = "", Persona = "", Title = "", Privacy = "", CreatedAt = "";
    }

    /// <summary>讀清單帳本。檔案不存在 ⇒ 空清單；讀不懂 ⇒ null ＋ 原因（⛔ 呼叫端不得拿空清單蓋掉它）。</summary>
    public static List<PlaylistEntry>? LoadPlaylistLedger(string iRoot, out string? oError)
    {
        oError = null;
        string aPath = PlaylistLedgerPath(iRoot);
        if (!File.Exists(aPath)) return new List<PlaylistEntry>();
        try
        {
            SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
            var aList = new List<PlaylistEntry>();
            foreach (SCP_JsonData x in j["playlists"])
                aList.Add(new PlaylistEntry
                {
                    PlaylistId = x["playlist_id"].AsString(), Persona = x.GetString("persona", ""), Title = x.GetString("title", ""),
                    Privacy = x.GetString("privacy", ""), CreatedAt = x.GetString("created_at", ""),
                });
            return aList;
        }
        catch (Exception e) { oError = $"{aPath}：{e.Message}"; return null; }
    }

    public static bool TrySavePlaylistLedger(string iRoot, List<PlaylistEntry> iList, out string? oError)
    {
        oError = null;
        var arr = SCP_JsonData.NewArray();
        foreach (PlaylistEntry x in iList)
        {
            var o = SCP_JsonData.NewObject();
            o.Set("playlist_id", x.PlaylistId);
            o.Set("persona", x.Persona);
            o.Set("title", x.Title);
            o.Set("privacy", x.Privacy);
            o.Set("created_at", x.CreatedAt);
            arr.Add(o);
        }
        var j = SCP_JsonData.NewObject();
        j.Set("playlists", arr);
        try
        {
            string aPath = PlaylistLedgerPath(iRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            YouTubeClient.WriteReplace(aPath, new UTF8Encoding(false).GetBytes(j.ToJson(true)));
            return true;
        }
        catch (Exception e) { oError = e.Message; return false; }
    }

    // ── 連網的小工具 ────────────────────────────────────────────

    sealed class ChannelPlaylist { public string Id = "", Title = "", Privacy = ""; public int Count; }

    /// <summary>頻道現有的清單（唯讀；翻頁到底，最多 10 頁）。</summary>
    static bool TryListChannelPlaylists(string iAccess, out List<ChannelPlaylist> oList, out string? oError)
    {
        oList = new List<ChannelPlaylist>();
        string aPage = "";
        for (int i = 0; i < 10; i++)
        {
            string aQ = $"/playlists?part=snippet,status,contentDetails&mine=true&maxResults={PlaylistPageMax}" + (aPage.Length > 0 ? "&pageToken=" + Uri.EscapeDataString(aPage) : "");
            if (!YouTubeClient.TryGetJson(iAccess, aQ, out SCP_JsonData j, out oError)) return false;
            foreach (SCP_JsonData it in j["items"])
                oList.Add(new ChannelPlaylist
                {
                    Id = it.GetString("id", ""), Title = it["snippet"].GetString("title", ""),
                    Privacy = it["status"].GetString("privacyStatus", "?"), Count = it["contentDetails"].GetInt("itemCount", 0),
                });
            aPage = j.GetString("nextPageToken", "");
            if (aPage.Length == 0) break;
        }
        oError = null;
        return true;
    }

    /// <summary>清單裡已經有哪些影片（唯讀；翻頁到底，最多 20 頁 ＝ 1000 支）。</summary>
    static bool TryListPlaylistVideoIds(string iAccess, string iPlaylistId, out List<string> oIds, out string? oError)
    {
        oIds = new List<string>();
        string aPage = "";
        for (int i = 0; i < 20; i++)
        {
            string aQ = $"/playlistItems?part=contentDetails&maxResults={PlaylistPageMax}&playlistId={Uri.EscapeDataString(iPlaylistId)}" + (aPage.Length > 0 ? "&pageToken=" + Uri.EscapeDataString(aPage) : "");
            if (!YouTubeClient.TryGetJson(iAccess, aQ, out SCP_JsonData j, out oError)) return false;
            foreach (SCP_JsonData it in j["items"]) { string id = it["contentDetails"].GetString("videoId", ""); if (id.Length > 0) oIds.Add(id); }
            aPage = j.GetString("nextPageToken", "");
            if (aPage.Length == 0) break;
        }
        oError = null;
        return true;
    }

    public const int AddRetryMax = 3;
    public const int AddRetryDelayMs = 2000;

    /// <summary>這個錯誤值不值得重試：YouTube 在清單剛建好／連續寫入時會回 409「The operation was aborted」。</summary>
    public static bool IsRetryableAddError(string? iError) => iError != null && iError.Contains("HTTP 409", StringComparison.Ordinal);

    /// <summary>把影片依序加進清單；已經在裡面的跳過。`iPosition` ≥ 0 ⇒ 從那個位置開始依序插入；負的 ⇒ 加在最後。
    /// 回傳（加了幾支，跳過幾支，失敗幾支）。</summary>
    static (int Added, int Skipped, int Failed) AddVideos(SCP_CmdResult r, string iAccess, string iPlaylistId, List<string> iVideoIds, HashSet<string> iAlready, int iPosition = -1)
    {
        int aAdded = 0, aSkipped = 0, aFailed = 0;
        foreach (string id in iVideoIds)
        {
            if (iAlready.Contains(id)) { aSkipped++; r.Lines.Add($"- `{id}`：已經在清單裡，跳過"); continue; }
            int aPos = iPosition >= 0 ? iPosition + aAdded : -1;
            string? aErr = null;
            bool aOk = false;
            int aTry = 0;
            for (aTry = 1; aTry <= AddRetryMax; aTry++)
            {
                aOk = YouTubeClient.TryAddToPlaylist(iAccess, iPlaylistId, id, out aErr, aPos);
                if (aOk || !IsRetryableAddError(aErr) || aTry == AddRetryMax) break;
                Thread.Sleep(AddRetryDelayMs);
            }
            string aNote = (aPos >= 0 ? $"（位置 {aPos}）" : "") + (aTry > 1 ? $"（第 {aTry} 次才成；前面回 409）" : "");
            if (aOk) { aAdded++; iAlready.Add(id); r.Lines.Add($"- `{id}`：✓ 已加入{aNote}"); }
            else { aFailed++; r.Lines.Add($"- `{id}`：✗ {aErr}{(aTry > 1 ? $"（試了 {aTry} 次）" : "")}"); }
        }
        return (aAdded, aSkipped, aFailed);
    }

    static void PlaylistReadback(SCP_CmdResult r, string iAccess, string iPlaylistId)
    {
        if (!YouTubeClient.TryGetJson(iAccess, "/playlists?part=snippet,status,contentDetails&id=" + Uri.EscapeDataString(iPlaylistId), out SCP_JsonData j, out string? aErr))
        { r.Lines.Add("- ⚠ 回讀失敗：" + aErr); return; }
        if (j["items"].Count == 0) { r.Lines.Add("- ⚠ 回讀：查不到這個清單"); return; }
        SCP_JsonData it = j["items"][0];
        // 支數與順序讀 playlistItems（清單本身的 itemCount 會慢半拍）
        bool aGotItems = TryListPlaylistVideoIds(iAccess, iPlaylistId, out List<string> aIds, out string? aItemsErr);
        int aCount = aGotItems ? aIds.Count : it["contentDetails"].GetInt("itemCount", 0);
        r.Lines.Add($"- 回讀：公開度 **{it["status"].GetString("privacyStatus", "?")}**　{aCount} 支　標題「{it["snippet"].GetString("title", "")}」");
        if (aGotItems) r.Lines.Add("- 清單裡的順序：" + (aIds.Count == 0 ? "（空的）" : string.Join(" → ", aIds.Select(x => "`" + x + "`"))));
        else r.Lines.Add("- ⚠ 清單內容讀不到（支數是清單自己報的，可能慢半拍）：" + aItemsErr);
        r.AddValue("privacy", it["status"].GetString("privacyStatus", ""));
        r.AddValue("item_count", aCount.ToString(CultureInfo.InvariantCulture));
        if (aGotItems) r.AddValue("items", string.Join(",", aIds));
    }

    // ── op=playlists ────────────────────────────────────────────

    static SCP_CmdResult Playlists(string iRoot)
    {
        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) return aFail;
        if (!TryListChannelPlaylists(aAccess, out List<ChannelPlaylist> aAll, out string? aErr)) return SCP_CmdResult.Fail(1, "✗ " + aErr);
        List<PlaylistEntry>? aLedger = LoadPlaylistLedger(iRoot, out string? aLedgerErr);
        Dictionary<string, PlaylistEntry> aMine = (aLedger ?? new List<PlaylistEntry>()).ToDictionary(x => x.PlaylistId, x => x, StringComparer.Ordinal);
        var r = SCP_CmdResult.Success($"# 頻道的播放清單（{aAll.Count}）");
        if (aLedger == null) r.Lines.Add("⚠ 清單帳本讀不了：" + aLedgerErr);
        r.Lines.Add("| id | 標題 | 公開度 | 支數 | Senate |");
        r.Lines.Add("|---|---|---|---:|---|");
        foreach (ChannelPlaylist p in aAll)
            r.Lines.Add($"| {p.Id} | {p.Title.Replace("|", "／")} | {p.Privacy} | {p.Count} | {(aMine.TryGetValue(p.Id, out PlaylistEntry? m) ? m.Persona : "—")} |");
        r.Lines.Add("（Senate 欄是「—」的是頻道原本的清單：`op=playlist-add` 不准動）");
        foreach (PlaylistEntry m in aMine.Values.Where(m => aAll.All(p => p.Id != m.PlaylistId)))
            r.Lines.Add($"⚠ 帳本裡有、頻道上查不到：`{m.PlaylistId}`「{m.Title}」（被刪了？）");
        r.AddValue("count", aAll.Count.ToString(CultureInfo.InvariantCulture));
        r.AddValue("mine", aAll.Count(p => aMine.ContainsKey(p.Id)).ToString(CultureInfo.InvariantCulture));
        return r;
    }

    // ── op=playlist-create ──────────────────────────────────────

    static SCP_CmdResult PlaylistCreate(string iRoot, SCP_CmdArgs iArgs)
    {
        string aPath = iArgs.Get("slip_file").Trim().Trim('"');
        if (aPath.Length == 0 || !File.Exists(aPath)) return SCP_CmdResult.Fail(2, $"✗ slip_file 不存在：'{aPath}'");
        if (!TryParsePlaylistSlip(File.ReadAllText(aPath, Encoding.UTF8), out PlaylistSlip s, out List<string> aParseErr))
        {
            var aBad = SCP_CmdResult.Fail(2, "✗ 交付單讀不了：");
            foreach (string x in aParseErr) aBad.Lines.Add("  - " + x);
            return aBad;
        }
        List<LedgerEntry>? aUploads = LoadLedger(iRoot, out _);
        List<PlaylistEntry>? aLedger = LoadPlaylistLedger(iRoot, out string? aLedgerErr);
        if (aLedger == null) return SCP_CmdResult.Fail(2, "✗ 清單帳本讀不了（⛔ 不拿空的蓋掉它）：" + aLedgerErr);
        List<string> e = LintPlaylist(s, iArgs.Get("letters_root").Trim(), aUploads?.Select(x => x.VideoId).ToHashSet(StringComparer.Ordinal));
        var r = new SCP_CmdResult();
        r.Lines.Add($"# YouTube 建播放清單：{s.Title}");
        AddLint(r, e, new List<string>());
        if (e.Count > 0) { r.ExitCode = 2; r.Lines.Add("⛔ 沒有送出"); return r; }

        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) { aFail.Lines.InsertRange(0, r.Lines); return aFail; }
        if (!TryListChannelPlaylists(aAccess, out List<ChannelPlaylist> aAll, out string? aErr)) { r.ExitCode = 1; r.Lines.Add("✗ 讀不到頻道現有的清單：" + aErr); return r; }
        ChannelPlaylist? aSame = aAll.FirstOrDefault(p => string.Equals(p.Title, s.Title, StringComparison.Ordinal));
        if (aSame != null && iArgs.Get("allow_duplicate") != "1")
        {
            r.ExitCode = 2;
            r.Lines.Add($"⛔ 頻道上已經有同名的清單：`{aSame.Id}`（{aSame.Privacy}，{aSame.Count} 支）⇒ 沒有建。要加影片用 `op=playlist-add`；真的要再建一個同名的加 `--arg allow_duplicate=1`");
            r.AddValue("existing_playlist_id", aSame.Id);
            return r;
        }

        var sn = SCP_JsonData.NewObject();
        sn.Set("title", s.Title);
        sn.Set("description", s.Description);
        var st = SCP_JsonData.NewObject();
        st.Set("privacyStatus", s.Privacy);
        var body = SCP_JsonData.NewObject();
        body.Set("snippet", sn);
        body.Set("status", st);
        if (iArgs.Get("dry_run") == "1")
        {
            r.Lines.Add("## 將送的內容（dry_run ⇒ 沒送）");
            r.Lines.Add("```json");
            r.Lines.Add(body.ToJson(true));
            r.Lines.Add("```");
            if (s.Videos.Count > 0) r.Lines.Add($"- 建好之後依序加入 {s.Videos.Count} 支：{string.Join("、", s.Videos.Select(x => "`" + x + "`"))}");
            return r;
        }
        if (!YouTubeClient.TrySendJson(aAccess, HttpMethod.Post, "/playlists?part=snippet,status", body.ToJson(false), out SCP_JsonData aCreated, out aErr))
        { r.ExitCode = 1; r.Lines.Add("✗ " + aErr); return r; }
        string aId = aCreated.GetString("id", "");
        if (aId.Length == 0) { r.ExitCode = 1; r.Lines.Add("✗ YouTube 沒回清單 id（建了沒有不知道 ⇒ 先跑 `op=playlists` 看，⛔ 別直接重跑）"); return r; }
        r.Lines.Add($"✅ 已建立：https://www.youtube.com/playlist?list={aId}");
        r.AddValue("playlist_id", aId);

        aLedger.Add(new PlaylistEntry { PlaylistId = aId, Persona = s.Persona, Title = s.Title, Privacy = s.Privacy, CreatedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) });
        if (!TrySavePlaylistLedger(iRoot, aLedger, out string? aSaveErr)) r.Lines.Add($"⚠ 清單帳本沒寫進去：{aSaveErr}（清單已經建了；之後 `op=playlist-add` 會不認得它）");

        if (s.Videos.Count > 0)
        {
            var (aAdded, _, aFailed) = AddVideos(r, aAccess, aId, s.Videos, new HashSet<string>(StringComparer.Ordinal));
            r.AddValue("added", aAdded.ToString(CultureInfo.InvariantCulture));
            if (aFailed > 0) r.ExitCode = 1;
        }
        PlaylistReadback(r, aAccess, aId);
        return r;
    }

    // ── op=playlist-add ─────────────────────────────────────────

    static SCP_CmdResult PlaylistAdd(string iRoot, SCP_CmdArgs iArgs)
    {
        string aPl = iArgs.Get("playlist_id").Trim();
        List<string> aIds = iArgs.Get("video_ids").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (aPl.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=playlist-add 要給 --arg playlist_id=<id>");
        if (aIds.Count == 0) return SCP_CmdResult.Fail(2, "✗ op=playlist-add 要給 --arg video_ids=<id,id,…>");
        int aPosition = -1;
        string aPosArg = iArgs.Get("position").Trim();
        if (aPosArg.Length > 0 && (!int.TryParse(aPosArg, NumberStyles.None, CultureInfo.InvariantCulture, out aPosition) || aPosition < 0))
            return SCP_CmdResult.Fail(2, $"✗ `position` 要是 0 以上的整數（0 ＝ 最前面；收到 '{aPosArg}'）");
        List<PlaylistEntry>? aLedger = LoadPlaylistLedger(iRoot, out string? aLedgerErr);
        if (aLedger == null) return SCP_CmdResult.Fail(2, "✗ 清單帳本讀不了：" + aLedgerErr);
        PlaylistEntry? aMine = aLedger.FirstOrDefault(x => x.PlaylistId == aPl);
        if (aMine == null) return SCP_CmdResult.Fail(2, $"✗ `{aPl}` 不是本工具建的清單 ⇒ 不准動（頻道上原本的清單一律不碰；`op=playlists` 看哪些是）");
        var e = new List<string>();
        LintVideoIds(aIds, LoadLedger(iRoot, out _)?.Select(x => x.VideoId).ToHashSet(StringComparer.Ordinal), "`video_ids`", e);
        var r = new SCP_CmdResult();
        r.Lines.Add($"# YouTube 清單加影片：{aPl}「{aMine.Title}」");
        AddLint(r, e, new List<string>());
        if (e.Count > 0) { r.ExitCode = 2; r.Lines.Add("⛔ 沒有送出"); return r; }

        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) { aFail.Lines.InsertRange(0, r.Lines); return aFail; }
        if (!TryListPlaylistVideoIds(aAccess, aPl, out List<string> aNow, out string? aErr)) { r.ExitCode = 1; r.Lines.Add("✗ 讀不到清單現在的內容：" + aErr); return r; }
        var aAlready = new HashSet<string>(aNow, StringComparer.Ordinal);
        if (iArgs.Get("dry_run") == "1")
        {
            r.Lines.Add("## 將做的事（dry_run ⇒ 沒送）");
            int aN = 0;
            foreach (string id in aIds)
                r.Lines.Add(aAlready.Contains(id) ? $"- `{id}`：已經在清單裡，會跳過" : $"- `{id}`：會加入{(aPosition >= 0 ? $"（位置 {aPosition + aN++}）" : "（最後）")}");
            return r;
        }
        var (aAdded, aSkipped, aFailed) = AddVideos(r, aAccess, aPl, aIds, aAlready, aPosition);
        r.AddValue("playlist_id", aPl);
        r.AddValue("added", aAdded.ToString(CultureInfo.InvariantCulture));
        r.AddValue("skipped", aSkipped.ToString(CultureInfo.InvariantCulture));
        if (aFailed > 0) r.ExitCode = 1;
        PlaylistReadback(r, aAccess, aPl);
        return r;
    }
}
