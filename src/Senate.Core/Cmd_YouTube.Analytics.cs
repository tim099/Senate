// 區塊職責：`senate cmd youtube` 的數據那一半（TASK-0016）——
//   · op=analytics：YouTube Analytics API —— 影片一張表（觀看、觀看分鐘、平均觀看、讚、分享、漲粉）；
//     帶 video_id ⇒ 單支的總覽、流量來源、留存（3 秒、30 秒時還剩幾成、對同長度影片的相對表現）。
//   · op=reach-setup／op=reach：YouTube Reporting API 的 reach 報表 `channel_reach_basic_a1` —— 縮圖曝光與點閱率。
// 物理意義：Tim 2026-11-12：「幫工具接 Analytics」。YouTube 運營觀察筆記的結論「同一支片 B 站百位數、YouTube 個位數 ⇒ 先卡在沒被推薦出去」
//           只是推測 —— 原本的工具只讀得到觀看、讚、留言。官方規格（2026-11-12 讀 developers.google.com）：
//   · Analytics API 的指標表**沒有縮圖曝光與點閱率**（`adImpressions` 是廣告的、`cardImpressions` 是資訊卡的）⇒ 那兩個只在 Reporting API 的 reach 報表。
//   · reach 報表是批次的：要先建一個報表工作（jobs.create），48 小時內出第一份，會回補建立前 30 天；一份 CSV 一天，留 60 天（回補的 30 天）。
//   · 留存報表（維度 elapsedVideoTimeRatio）的 filters 只收**一支**影片 ⇒ 留存只在帶 video_id 時查。
// 數值影響：
//   · 全部唯讀，只有 `reach-setup` 會建一個報表工作（頻道上別人看不到；已經有同類型的就不重建；`dry_run=1` 只印要送的內容）。
//   · 授權不夠（登入時沒拿到 yt-analytics.readonly／youtube.readonly）⇒ **先看 token 檔記的 scope 就擋下**（exit 3），不去撞一個看不懂的 403。
//   · 那兩支 API 在 Cloud 專案裡要個別啟用；沒啟用的錯誤翻成「去哪啟用哪一支」（`ClassifyDataApiError`）。
//   · 數字照 API 給的印：沒資料就說沒資料（⛔ 不補 0 —— 「0 觀看」跟「YouTube 還沒結算」是兩件事）。Analytics 通常落後 1～2 天。
//   · reach 的點閱率照曝光加權：Σ(曝光 × 點閱率)／Σ曝光。⚠ 文件沒寫點閱率是比例還是百分比 ⇒ 看到 > 1 的值就整批當百分比，並在輸出上講明。
#nullable enable
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using SCP.Core.Cmd;
using SCP.Core.Json;

namespace Senate.Core;

public sealed partial class Cmd_YouTube
{
    public const string ReachReportType = "channel_reach_basic_a1";
    public const string ReachJobName = "senate-reach-basic";
    /// <summary>`dimensions=video` 的報表 maxResults 上限是 200（channel reports「Top videos」）。</summary>
    public const int AnalyticsMaxVideos = 200;
    /// <summary>沒給 start ⇒ 往前抓這麼多天。</summary>
    public const int DefaultRangeDays = 365;

    static readonly Regex s_VideoId = new(@"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant);
    static readonly Regex s_Date = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant);

    static readonly string[] s_SummaryMetrics = { "views", "estimatedMinutesWatched", "averageViewDuration", "averageViewPercentage", "likes", "shares", "subscribersGained" };

    // ── 不連網的部分（自我測試量得到） ────────────────────────────

    /// <summary>Analytics 的 resultTable 攤平成欄名＋字串列。</summary>
    public sealed class ReportTable
    {
        public List<string> Columns = new();
        public List<string[]> Rows = new();
        public string Cell(string[] iRow, string iName) { int i = Columns.IndexOf(iName); return i >= 0 && i < iRow.Length ? iRow[i] : ""; }
        /// <summary>讀不到（沒這欄、空字串、不是數字）⇒ NaN。</summary>
        public double Num(string[] iRow, string iName)
            => double.TryParse(Cell(iRow, iName), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : double.NaN;
    }

    /// <summary>
    /// 讀 `reports.query` 的回應。沒有 `rows` ＝ 這段期間沒資料 ⇒ 空的 Rows，不是錯。
    /// 欄數對不上 ⇒ 錯（⛔ 不猜哪一欄是哪一欄）。
    /// </summary>
    public static bool TryParseReportTable(SCP_JsonData iJson, out ReportTable oTable, out string? oError)
    {
        oTable = new ReportTable();
        oError = null;
        if (!iJson["columnHeaders"].IsArray) { oError = "回應裡沒有 columnHeaders"; return false; }
        foreach (SCP_JsonData h in iJson["columnHeaders"]) oTable.Columns.Add(h.GetString("name", ""));
        SCP_JsonData aRows = iJson["rows"];
        if (aRows.IsMissing || aRows.IsNull) return true;
        if (!aRows.IsArray) { oError = "rows 不是陣列"; return false; }
        foreach (SCP_JsonData r in aRows)
        {
            if (!r.IsArray || r.Count != oTable.Columns.Count)
            { oError = $"有一列的欄數（{(r.IsArray ? r.Count : -1)}）跟欄名（{oTable.Columns.Count}）對不上"; return false; }
            var a = new string[r.Count];
            for (int i = 0; i < r.Count; i++) a[i] = r[i].IsNull ? "" : r[i].AsString();
            oTable.Rows.Add(a);
        }
        return true;
    }

    /// <summary>ISO 8601 的影片長度（`PT3M29S`）⇒ 秒；讀不懂回 -1。</summary>
    public static int IsoDurationSeconds(string iIso)
    {
        try { return (int)Math.Round(XmlConvert.ToTimeSpan((iIso ?? "").Trim()).TotalSeconds); }
        catch (Exception) { return -1; }
    }

    /// <summary>留存曲線上的一點：進度（0～1）、還在看的比例（重看會 &gt; 1）、相對表現（0～1，0.5 ＝ 同長度影片的中位數；沒給是 NaN）。</summary>
    public readonly record struct RetentionPoint(double Ratio, double Watch, double Relative);

    public static List<RetentionPoint> ParseRetention(ReportTable iTable)
    {
        var a = new List<RetentionPoint>();
        foreach (string[] r in iTable.Rows)
        {
            double aRatio = iTable.Num(r, "elapsedVideoTimeRatio");
            if (double.IsNaN(aRatio)) continue;
            a.Add(new RetentionPoint(aRatio, iTable.Num(r, "audienceWatchRatio"), iTable.Num(r, "relativeRetentionPerformance")));
        }
        a.Sort((x, y) => x.Ratio.CompareTo(y.Ratio));
        return a;
    }

    /// <summary>第一個進度 ≥ 目標的點（報表每 1% 一格 ⇒ 最多晚 1%）；目標超過最後一點 ⇒ null。</summary>
    public static RetentionPoint? RetentionAt(IReadOnlyList<RetentionPoint> iPoints, double iTargetRatio)
    {
        foreach (RetentionPoint p in iPoints) if (p.Ratio >= iTargetRatio - 1e-9) return p;
        return null;
    }

    /// <summary>每支影片的 reach 加總。</summary>
    public sealed class ReachSum
    {
        public long Impressions;
        /// <summary>Σ(曝光 × 點閱率)：估出來的點擊數，用來算加權點閱率。</summary>
        public double Clicks;
        public int Days;
        public string FirstDate = "", LastDate = "";
        public double Ctr => Impressions > 0 ? Clicks / Impressions : double.NaN;
    }

    /// <summary>
    /// reach 報表的 CSV（一份一天，欄：date、channel_id、video_id、video_thumbnail_impressions、video_thumbnail_impressions_ctr）⇒ 每支影片加總。
    /// 缺欄的那一份整份跳過並記一句；壞掉的列跳過並記一句。
    /// <paramref name="oCtrIsPercent"/>：有任何一列點閱率 &gt; 1 ⇒ 整批當百分比（÷100）。
    /// </summary>
    public static Dictionary<string, ReachSum> AggregateReach(IEnumerable<string> iCsvs, out List<string> oWarnings, out bool oCtrIsPercent)
    {
        oWarnings = new List<string>();
        var aRows = new List<(string Video, string Date, long Imp, double Ctr)>();
        int aDoc = 0;
        foreach (string aCsv in iCsvs)
        {
            aDoc++;
            string[] aLines = (aCsv ?? "").Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (aLines.Length == 0) { oWarnings.Add($"第 {aDoc} 份是空的"); continue; }
            List<string> h = ParseCsvLine(aLines[0].TrimStart('﻿'));
            int iV = h.IndexOf("video_id"), iD = h.IndexOf("date"), iI = h.IndexOf("video_thumbnail_impressions"), iC = h.IndexOf("video_thumbnail_impressions_ctr");
            if (iV < 0 || iI < 0 || iC < 0) { oWarnings.Add($"第 {aDoc} 份缺欄（要 video_id、video_thumbnail_impressions、video_thumbnail_impressions_ctr；實際：{string.Join(",", h)}）⇒ 整份跳過"); continue; }
            for (int n = 1; n < aLines.Length; n++)
            {
                List<string> c = ParseCsvLine(aLines[n]);
                int aNeed = Math.Max(iV, Math.Max(iI, iC));
                if (c.Count <= aNeed
                    || !long.TryParse(c[iI], NumberStyles.Integer, CultureInfo.InvariantCulture, out long aImp)
                    || !double.TryParse(c[iC], NumberStyles.Float, CultureInfo.InvariantCulture, out double aCtr)
                    || c[iV].Length == 0)
                { oWarnings.Add($"第 {aDoc} 份第 {n + 1} 行讀不懂，跳過"); continue; }
                aRows.Add((c[iV], iD >= 0 && iD < c.Count ? c[iD] : "", aImp, aCtr));
            }
        }
        oCtrIsPercent = aRows.Any(r => r.Ctr > 1.0);
        var d = new Dictionary<string, ReachSum>(StringComparer.Ordinal);
        foreach (var r in aRows)
        {
            if (!d.TryGetValue(r.Video, out ReachSum? s)) d[r.Video] = s = new ReachSum();
            s.Impressions += r.Imp;
            s.Clicks += r.Imp * (oCtrIsPercent ? r.Ctr / 100.0 : r.Ctr);
            s.Days++;
            if (r.Date.Length > 0)
            {
                if (s.FirstDate.Length == 0 || string.CompareOrdinal(r.Date, s.FirstDate) < 0) s.FirstDate = r.Date;
                if (string.CompareOrdinal(r.Date, s.LastDate) > 0) s.LastDate = r.Date;
            }
        }
        return d;
    }

    /// <summary>一行 CSV ⇒ 欄位（雙引號包住的欄位可以有逗號，`""` 是一個引號）。</summary>
    public static List<string> ParseCsvLine(string iLine)
    {
        var a = new List<string>();
        var sb = new StringBuilder();
        bool aQuoted = false;
        for (int i = 0; i < iLine.Length; i++)
        {
            char ch = iLine[i];
            if (aQuoted)
            {
                if (ch == '"' && i + 1 < iLine.Length && iLine[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') aQuoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') aQuoted = true;
            else if (ch == ',') { a.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        a.Add(sb.ToString());
        return a;
    }

    /// <summary>Analytics／Reporting 的錯誤 ⇒ exit code ＋ 一句「怎麼辦」。3 ＝ 授權不夠（要重登）；1 ＝ 其他（含 API 沒啟用）。</summary>
    public static int ClassifyDataApiError(string? iError, string iApiName, string iApiHost, out string oHint)
    {
        string e = iError ?? "";
        oHint = "";
        if (e.Contains("ACCESS_TOKEN_SCOPE_INSUFFICIENT", StringComparison.Ordinal) || e.Contains("insufficientPermissions", StringComparison.Ordinal)
            || e.Contains("insufficient authentication scopes", StringComparison.OrdinalIgnoreCase))
        {
            oHint = "⇒ 授權不夠：重跑 `op=login`，同意畫面上 YouTube 與 YouTube Analytics 的幾格都要勾";
            return 3;
        }
        if (e.Contains("accessNotConfigured", StringComparison.Ordinal) || e.Contains("SERVICE_DISABLED", StringComparison.Ordinal)
            || e.Contains("has not been used in project", StringComparison.Ordinal) || e.Contains("it is disabled", StringComparison.Ordinal))
        {
            oHint = $"⇒ Cloud 專案還沒啟用「{iApiName}」：console.cloud.google.com →「API 和服務 → 程式庫」搜「{iApiName}」→ 啟用（`{iApiHost}`）；剛啟用要等幾分鐘";
            return 1;
        }
        return 1;
    }

    /// <summary>日期範圍：沒給 end ⇒ 今天；沒給 start ⇒ end 往前 <see cref="DefaultRangeDays"/> 天。格式 YYYY-MM-DD，start ≤ end。</summary>
    public static bool TryDateRange(string iStart, string iEnd, DateTime iToday, out string oStart, out string oEnd, out string? oError)
    {
        oError = null;
        oEnd = (iEnd ?? "").Trim();
        if (oEnd.Length == 0) oEnd = iToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        oStart = (iStart ?? "").Trim();
        if (!IsDate(oEnd)) { oError = $"end 要是 YYYY-MM-DD：'{oEnd}'"; return false; }
        if (oStart.Length == 0)
            oStart = DateTime.ParseExact(oEnd, "yyyy-MM-dd", CultureInfo.InvariantCulture).AddDays(-DefaultRangeDays).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!IsDate(oStart)) { oError = $"start 要是 YYYY-MM-DD：'{oStart}'"; return false; }
        if (string.CompareOrdinal(oStart, oEnd) > 0) { oError = $"start（{oStart}）比 end（{oEnd}）晚"; return false; }
        return true;
    }

    static bool IsDate(string s)
        => s_Date.IsMatch(s) && DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>`video_ids` 給了就用它（逗號分隔、去重、檢查格式）；沒給 ⇒ 上傳帳本裡的全部（照上傳順序）。</summary>
    public static bool TryResolveVideoIds(string iArg, List<LedgerEntry>? iLedger, out List<string> oIds, out string? oError)
    {
        oError = null;
        oIds = new List<string>();
        var aSeen = new HashSet<string>(StringComparer.Ordinal);
        string aArg = (iArg ?? "").Trim();
        if (aArg.Length > 0)
        {
            foreach (string x in aArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!s_VideoId.IsMatch(x)) { oError = $"看起來不是影片 id：'{x}'（11 個字元的英數、- 與 _）"; return false; }
                if (aSeen.Add(x)) oIds.Add(x);
            }
        }
        else
        {
            if (iLedger == null) { oError = "上傳帳本讀不了，又沒給 video_ids"; return false; }
            foreach (LedgerEntry e in iLedger) if (aSeen.Add(e.VideoId)) oIds.Add(e.VideoId);
        }
        if (oIds.Count == 0) { oError = "沒有影片可以查（上傳帳本是空的 ⇒ 用 `--arg video_ids=<id,id>` 指定）"; return false; }
        if (oIds.Count > AnalyticsMaxVideos) { oError = $"一次最多 {AnalyticsMaxVideos} 支，給了 {oIds.Count} 支"; return false; }
        return true;
    }

    /// <summary>組查詢字串：每個值都 escape（`channel==MINE` 的 `==` 也是）。</summary>
    public static string BuildQuery(IEnumerable<(string Key, string Value)> iPairs)
        => string.Join("&", iPairs.Select(p => p.Key + "=" + Uri.EscapeDataString(p.Value)));

    static readonly Dictionary<string, string> s_TrafficLabel = new(StringComparer.Ordinal)
    {
        ["YT_SEARCH"] = "YouTube 搜尋",
        ["SUBSCRIBER"] = "首頁、訂閱內容等瀏覽功能",
        ["RELATED_VIDEO"] = "推薦影片（接著播放、側欄）",
        ["EXT_URL"] = "外部網站",
        ["NO_LINK_OTHER"] = "直接開或來源不明",
        ["NO_LINK_EMBEDDED"] = "嵌在別處的播放器",
        ["PLAYLIST"] = "播放清單",
        ["YT_PLAYLIST_PAGE"] = "播放清單頁",
        ["YT_CHANNEL"] = "頻道頁",
        ["YT_OTHER_PAGE"] = "YouTube 其他頁面",
        ["NOTIFICATION"] = "通知",
        ["END_SCREEN"] = "結束畫面",
        ["SHORTS"] = "Shorts 動態",
        ["HASHTAGS"] = "主題標籤頁",
        ["ADVERTISING"] = "廣告",
        ["ANNOTATION"] = "註解",
        ["CAMPAIGN_CARD"] = "資訊卡",
    };

    /// <summary>流量來源代碼 ⇒ 中文（原代碼留在括號裡，認不得的照原樣）。</summary>
    public static string TrafficLabel(string iCode) => s_TrafficLabel.TryGetValue(iCode, out string? s) ? $"{s}（{iCode}）" : iCode;

    static string FmtSec(double iSec) => double.IsNaN(iSec) ? "—" : $"{(int)(iSec / 60)}:{(int)Math.Round(iSec % 60):00}";
    static string FmtPct(double iFraction) => double.IsNaN(iFraction) ? "—" : (iFraction * 100).ToString("0.#", CultureInfo.InvariantCulture) + "%";
    static string FmtNum(double v) => double.IsNaN(v) ? "—" : v.ToString("#,0.#", CultureInfo.InvariantCulture);
    static string Cut(string s, int n) => (s.Length <= n ? s : s.Substring(0, n) + "…").Replace("|", "／");

    // ── 連網的部分 ──────────────────────────────────────────────

    /// <summary>授權範圍不夠 ⇒ 先擋（exit 3），附上 Tim 要做的三步。</summary>
    static SCP_CmdResult? RequireAnalytics(string iRoot)
    {
        YouTubeCredStatus s = YouTubeClient.Status(iRoot);
        if (!s.TokenReady || s.AnalyticsReady) return null;   // 沒登入的情況交給 Access() 去報
        List<string> aMissing = YouTubeClient.MissingScopes(s.GrantedScope, YouTubeClient.AnalyticsScopes);
        var r = SCP_CmdResult.Fail(3, "✗ 現在的登入沒有 Analytics 的授權（缺：" + string.Join("、", aMissing.Select(x => x.Substring(x.LastIndexOf('/') + 1))) + "）");
        r.Lines.Add("要 Tim 做的三步（只要一次）：");
        r.Lines.Add("1. console.cloud.google.com（專案就是用戶端所在的那個）→「API 和服務 → 程式庫」→ 啟用 **YouTube Analytics API** 與 **YouTube Reporting API**");
        r.Lines.Add("2. Google Auth Platform →「資料存取權」→ 手動新增範圍 `" + YouTubeClient.AnalyticsScope + "` 與 `" + YouTubeClient.ReadonlyScope + "` → 更新 → 儲存");
        r.Lines.Add("3. `senate cmd youtube --arg op=login --arg passphrase_path=<密碼檔>`，同意畫面上 YouTube 與 YouTube Analytics 的幾格都勾");
        return r;
    }

    static SCP_CmdResult ApiFail(string? iError, string iApiName, string iApiHost)
    {
        int aCode = ClassifyDataApiError(iError, iApiName, iApiHost, out string aHint);
        var r = SCP_CmdResult.Fail(aCode, "✗ " + iError);
        if (aHint.Length > 0) r.Lines.Add(aHint);
        return r;
    }

    const string AnalyticsApiName = "YouTube Analytics API", AnalyticsApiHost = "youtubeanalytics.googleapis.com";
    const string ReportingApiName = "YouTube Reporting API", ReportingApiHost = "youtubereporting.googleapis.com";

    /// <summary>Data API 抓標題與長度（一次 50 支）。抓不到的不在結果裡。</summary>
    static Dictionary<string, (string Title, int Sec)> FetchVideoInfo(string iAccess, IReadOnlyList<string> iIds, out string? oError)
    {
        oError = null;
        var d = new Dictionary<string, (string, int)>(StringComparer.Ordinal);
        for (int i = 0; i < iIds.Count; i += 50)
        {
            string aChunk = string.Join(",", iIds.Skip(i).Take(50));
            if (!YouTubeClient.TryGetJson(iAccess, "/videos?part=snippet,contentDetails&id=" + Uri.EscapeDataString(aChunk), out SCP_JsonData j, out oError)) return d;
            foreach (SCP_JsonData v in j["items"])
                d[v.GetString("id", "")] = (v["snippet"].GetString("title", ""), IsoDurationSeconds(v["contentDetails"].GetString("duration", "")));
        }
        return d;
    }

    // ── op=analytics ────────────────────────────────────────────

    static SCP_CmdResult Analytics(string iRoot, SCP_CmdArgs iArgs)
    {
        if (!TryDateRange(iArgs.Get("start"), iArgs.Get("end"), DateTime.Now, out string aStart, out string aEnd, out string? aErr))
            return SCP_CmdResult.Fail(2, "✗ " + aErr);
        string aOne = iArgs.Get("video_id").Trim();
        List<string> aIds;
        if (aOne.Length > 0)
        {
            if (!s_VideoId.IsMatch(aOne)) return SCP_CmdResult.Fail(2, $"✗ 看起來不是影片 id：'{aOne}'");
            aIds = new List<string> { aOne };
        }
        else
        {
            List<LedgerEntry>? aLedger = LoadLedger(iRoot, out string? aLedgerErr);
            if (!TryResolveVideoIds(iArgs.Get("video_ids"), aLedger, out aIds, out aErr))
                return SCP_CmdResult.Fail(2, "✗ " + aErr + (aLedger == null ? "（" + aLedgerErr + "）" : ""));
        }
        if (RequireAnalytics(iRoot) is SCP_CmdResult aNoScope) return aNoScope;
        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) return aFail;
        Dictionary<string, (string Title, int Sec)> aInfo = FetchVideoInfo(aAccess, aIds, out string? aInfoErr);
        return aOne.Length > 0
            ? AnalyticsOne(aAccess, aOne, aStart, aEnd, aInfo, aInfoErr)
            : AnalyticsTable(iRoot, aAccess, aIds, aStart, aEnd, aInfo, aInfoErr);
    }

    static SCP_CmdResult AnalyticsTable(string iRoot, string iAccess, List<string> iIds, string iStart, string iEnd,
                                        Dictionary<string, (string Title, int Sec)> iInfo, string? iInfoErr)
    {
        string q = BuildQuery(new[]
        {
            ("ids", "channel==MINE"), ("startDate", iStart), ("endDate", iEnd),
            ("dimensions", "video"), ("metrics", string.Join(",", s_SummaryMetrics)),
            ("filters", "video==" + string.Join(",", iIds)), ("sort", "-views"),
            ("maxResults", AnalyticsMaxVideos.ToString(CultureInfo.InvariantCulture)),
        });
        if (!YouTubeClient.TryAnalyticsReport(iAccess, q, out SCP_JsonData j, out string? aErr)) return ApiFail(aErr, AnalyticsApiName, AnalyticsApiHost);
        if (!TryParseReportTable(j, out ReportTable t, out aErr)) return SCP_CmdResult.Fail(1, "✗ Analytics 回應讀不懂：" + aErr);

        Dictionary<string, LedgerEntry> aMine = (LoadLedger(iRoot, out _) ?? new List<LedgerEntry>()).ToDictionary(x => x.VideoId, x => x, StringComparer.Ordinal);
        var r = SCP_CmdResult.Success($"# YouTube Analytics：{iIds.Count} 支（{iStart} ～ {iEnd}）");
        r.Lines.Add("| 影片 | 長度 | 觀看 | 觀看分鐘 | 平均觀看 | 平均看完 | 讚 | 分享 | 漲粉 |");
        r.Lines.Add("|---|---|---:|---:|---:|---:|---:|---:|---:|");
        var aSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string[] row in t.Rows)
        {
            string id = t.Cell(row, "video");
            aSeen.Add(id);
            string aTitle = iInfo.TryGetValue(id, out var inf) ? inf.Title : (aMine.TryGetValue(id, out LedgerEntry? m) ? m.Title : "");
            string aLen = iInfo.TryGetValue(id, out var inf2) && inf2.Sec >= 0 ? FmtSec(inf2.Sec) : "—";
            r.Lines.Add($"| {Cut(aTitle, 28)}（{id}） | {aLen} | {FmtNum(t.Num(row, "views"))} | {FmtNum(t.Num(row, "estimatedMinutesWatched"))}"
                + $" | {FmtSec(t.Num(row, "averageViewDuration"))} | {FmtPct(t.Num(row, "averageViewPercentage") / 100.0)}"
                + $" | {FmtNum(t.Num(row, "likes"))} | {FmtNum(t.Num(row, "shares"))} | {FmtNum(t.Num(row, "subscribersGained"))} |");
        }
        List<string> aNoData = iIds.Where(x => !aSeen.Contains(x)).ToList();
        if (aNoData.Count > 0)
            r.Lines.Add("- 這段期間沒有資料：" + string.Join("、", aNoData.Select(x => (iInfo.TryGetValue(x, out var i) ? Cut(i.Title, 20) : x) + $"（{x}）"))
                + " —— 沒有觀看，或 YouTube 還沒結算（Analytics 通常落後 1～2 天）；⛔ 不是 0");
        r.Lines.Add("- 數字是 Analytics 結算過的，跟 `op=list` 的即時觀看數不一定一樣；單支的流量來源與留存：`--arg video_id=<id>`");
        if (iInfoErr != null) r.Lines.Add("- ⚠ 標題與長度沒抓全：" + iInfoErr);
        r.AddValue("videos", iIds.Count.ToString(CultureInfo.InvariantCulture));
        r.AddValue("rows", t.Rows.Count.ToString(CultureInfo.InvariantCulture));
        r.AddValue("start", iStart);
        r.AddValue("end", iEnd);
        return r;
    }

    static SCP_CmdResult AnalyticsOne(string iAccess, string iId, string iStart, string iEnd,
                                      Dictionary<string, (string Title, int Sec)> iInfo, string? iInfoErr)
    {
        (string aTitle, int aSec) = iInfo.TryGetValue(iId, out var inf) ? inf : ("", -1);
        string Q(params (string, string)[] iExtra) => BuildQuery(new[] { ("ids", "channel==MINE"), ("startDate", iStart), ("endDate", iEnd) }.Concat(iExtra));

        // ① 總覽
        if (!YouTubeClient.TryAnalyticsReport(iAccess, Q(("metrics", string.Join(",", s_SummaryMetrics)), ("filters", "video==" + iId)), out SCP_JsonData j, out string? aErr))
            return ApiFail(aErr, AnalyticsApiName, AnalyticsApiHost);
        if (!TryParseReportTable(j, out ReportTable sum, out aErr)) return SCP_CmdResult.Fail(1, "✗ Analytics 回應讀不懂：" + aErr);

        var r = SCP_CmdResult.Success($"# YouTube Analytics：{(aTitle.Length > 0 ? aTitle : iId)}（{iId}{(aSec >= 0 ? "，" + FmtSec(aSec) : "")}）　{iStart} ～ {iEnd}");
        if (iInfoErr != null) r.Lines.Add("- ⚠ 標題與長度沒抓到：" + iInfoErr);
        r.Lines.Add("## 總覽");
        if (sum.Rows.Count == 0 || double.IsNaN(sum.Num(sum.Rows[0], "views")))
            r.Lines.Add("- 這段期間沒有資料（沒有觀看，或 YouTube 還沒結算 —— Analytics 通常落後 1～2 天）");
        else
        {
            string[] s = sum.Rows[0];
            r.Lines.Add($"- 觀看 {FmtNum(sum.Num(s, "views"))}　觀看分鐘 {FmtNum(sum.Num(s, "estimatedMinutesWatched"))}　平均觀看 {FmtSec(sum.Num(s, "averageViewDuration"))}"
                + $"（平均看完 {FmtPct(sum.Num(s, "averageViewPercentage") / 100.0)}）　讚 {FmtNum(sum.Num(s, "likes"))}　分享 {FmtNum(sum.Num(s, "shares"))}　漲粉 {FmtNum(sum.Num(s, "subscribersGained"))}");
            r.AddValue("views", sum.Cell(s, "views"));
            r.AddValue("average_view_percentage", sum.Cell(s, "averageViewPercentage"));
        }

        // ② 流量來源
        r.Lines.Add("## 流量來源");
        if (!YouTubeClient.TryAnalyticsReport(iAccess, Q(("dimensions", "insightTrafficSourceType"), ("metrics", "views,estimatedMinutesWatched"),
                ("filters", "video==" + iId), ("sort", "-views")), out j, out aErr))
            r.Lines.Add("- ⚠ 讀不到：" + aErr);
        else if (!TryParseReportTable(j, out ReportTable tr, out aErr)) r.Lines.Add("- ⚠ 回應讀不懂：" + aErr);
        else if (tr.Rows.Count == 0) r.Lines.Add("- 這段期間沒有資料");
        else
        {
            r.Lines.Add("| 來源 | 觀看 | 觀看分鐘 |");
            r.Lines.Add("|---|---:|---:|");
            foreach (string[] row in tr.Rows)
                r.Lines.Add($"| {TrafficLabel(tr.Cell(row, "insightTrafficSourceType"))} | {FmtNum(tr.Num(row, "views"))} | {FmtNum(tr.Num(row, "estimatedMinutesWatched"))} |");
        }

        // ③ 留存
        r.Lines.Add("## 留存（還在看的比例；相對表現 0.5 ＝ 同長度影片的中位數）");
        if (!YouTubeClient.TryAnalyticsReport(iAccess, Q(("dimensions", "elapsedVideoTimeRatio"), ("metrics", "audienceWatchRatio,relativeRetentionPerformance"),
                ("filters", "video==" + iId)), out j, out aErr))
            r.Lines.Add("- ⚠ 讀不到：" + aErr);
        else if (!TryParseReportTable(j, out ReportTable rt, out aErr)) r.Lines.Add("- ⚠ 回應讀不懂：" + aErr);
        else
        {
            List<RetentionPoint> p = ParseRetention(rt);
            if (p.Count == 0) r.Lines.Add("- 資料不足（YouTube 只在觀看夠多時才給留存曲線）");
            else
            {
                if (aSec > 0)
                {
                    foreach (int aAt in new[] { 3, 30 })
                    {
                        if (aSec <= aAt) continue;
                        RetentionPoint? q = RetentionAt(p, (double)aAt / aSec);
                        if (q == null) continue;
                        r.Lines.Add($"- {aAt} 秒（報表上最近的一格是第 {q.Value.Ratio * aSec:0.#} 秒）：還在看 {FmtPct(q.Value.Watch)}　相對表現 {(double.IsNaN(q.Value.Relative) ? "—" : q.Value.Relative.ToString("0.00", CultureInfo.InvariantCulture))}");
                        r.AddValue($"retention_{aAt}s", q.Value.Watch.ToString("0.###", CultureInfo.InvariantCulture));
                    }
                    if (aSec > 30) r.Lines.Add("  （官方的量尺：30 秒後還有 5 成以上就算「開頭高於平均」）");
                }
                r.Lines.Add("| 進度 | 還在看 | 相對表現 |");
                r.Lines.Add("|---:|---:|---:|");
                for (int k = 1; k <= 10; k++)
                {
                    RetentionPoint? q = RetentionAt(p, k / 10.0);
                    if (q == null) continue;
                    r.Lines.Add($"| {k * 10}% | {FmtPct(q.Value.Watch)} | {(double.IsNaN(q.Value.Relative) ? "—" : q.Value.Relative.ToString("0.00", CultureInfo.InvariantCulture))} |");
                }
                r.AddValue("retention_points", p.Count.ToString(CultureInfo.InvariantCulture));
            }
        }
        r.AddValue("video_id", iId);
        r.AddValue("start", iStart);
        r.AddValue("end", iEnd);
        return r;
    }

    // ── op=reach-setup／op=reach（Reporting API）──────────────────

    /// <summary>翻頁讀完 Reporting API 的清單（`jobs`、`reportTypes`、`reports`）。</summary>
    static bool TryReportingList(string iAccess, string iPath, string iField, out List<SCP_JsonData> oItems, out string? oError)
    {
        oItems = new List<SCP_JsonData>();
        oError = null;
        string aToken = "";
        for (int aPage = 0; aPage < 50; aPage++)
        {
            string aUrl = iPath + (aToken.Length > 0 ? (iPath.Contains('?') ? "&" : "?") + "pageToken=" + Uri.EscapeDataString(aToken) : "");
            if (!YouTubeClient.TryReportingGet(iAccess, aUrl, out SCP_JsonData j, out oError)) return false;
            foreach (SCP_JsonData x in j[iField]) oItems.Add(x);
            aToken = j.GetString("nextPageToken", "");
            if (aToken.Length == 0) return true;
        }
        oError = "清單超過 50 頁，停下來（不正常）";
        return false;
    }

    static SCP_JsonData? FindReachJob(List<SCP_JsonData> iJobs)
        => iJobs.Where(x => x.GetString("reportTypeId", "") == ReachReportType).OrderBy(x => x.GetString("createTime", ""), StringComparer.Ordinal).FirstOrDefault();

    static SCP_CmdResult ReachSetup(string iRoot, SCP_CmdArgs iArgs)
    {
        if (RequireAnalytics(iRoot) is SCP_CmdResult aNoScope) return aNoScope;
        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) return aFail;
        if (!TryReportingList(aAccess, "/jobs", "jobs", out List<SCP_JsonData> aJobs, out string? aErr)) return ApiFail(aErr, ReportingApiName, ReportingApiHost);
        SCP_JsonData? aHave = FindReachJob(aJobs);
        if (aHave != null)
            return SCP_CmdResult.Success($"✓ 已經有 reach 報表工作了：`{aHave.GetString("id", "")}`（{aHave.GetString("createTime", "?")} 建，名稱「{aHave.GetString("name", "")}」）⇒ 不重建",
                    "- 讀報表：`op=reach`")
                .AddValue("job_id", aHave.GetString("id", ""))
                .AddValue("created", "0");

        if (!TryReportingList(aAccess, "/reportTypes", "reportTypes", out List<SCP_JsonData> aTypes, out aErr)) return ApiFail(aErr, ReportingApiName, ReportingApiHost);
        if (!aTypes.Any(x => x.GetString("id", "") == ReachReportType))
            return SCP_CmdResult.Fail(1, $"✗ 這個頻道能建的報表類型裡沒有 `{ReachReportType}`（共 {aTypes.Count} 種）—— 不建");

        if (iArgs.Get("dry_run") == "1")
            return SCP_CmdResult.Success("## 將建的報表工作（dry_run ⇒ 沒送）",
                    $"- `POST https://youtubereporting.googleapis.com/v1/jobs`　reportTypeId `{ReachReportType}`　name `{ReachJobName}`",
                    "- 建好之後 YouTube 每天產一份 CSV，48 小時內出第一份，並回補建立前 30 天")
                .AddValue("created", "0");

        if (!YouTubeClient.TryReportingCreateJob(aAccess, ReachReportType, ReachJobName, out SCP_JsonData aJob, out aErr)) return ApiFail(aErr, ReportingApiName, ReportingApiHost);
        return SCP_CmdResult.Success($"✅ 已建 reach 報表工作：`{aJob.GetString("id", "")}`（{aJob.GetString("createTime", "?")}）",
                "- YouTube 從今天起每天產一份縮圖曝光與點閱率的 CSV；**48 小時內**出第一份，並回補建立前 30 天",
                "- 之後用 `op=reach` 讀（報表留 60 天；回補的那 30 天只留 30 天）")
            .AddValue("job_id", aJob.GetString("id", ""))
            .AddValue("created", "1");
    }

    static SCP_CmdResult Reach(string iRoot, SCP_CmdArgs iArgs)
    {
        string aStart = iArgs.Get("start").Trim(), aEnd = iArgs.Get("end").Trim();
        if ((aStart.Length > 0 && !IsDate(aStart)) || (aEnd.Length > 0 && !IsDate(aEnd))) return SCP_CmdResult.Fail(2, "✗ start／end 要是 YYYY-MM-DD");
        List<string>? aOnly = null;
        if (iArgs.Get("video_ids").Trim().Length > 0)
        {
            if (!TryResolveVideoIds(iArgs.Get("video_ids"), null, out List<string> x, out string? aIdErr)) return SCP_CmdResult.Fail(2, "✗ " + aIdErr);
            aOnly = x;
        }
        if (RequireAnalytics(iRoot) is SCP_CmdResult aNoScope) return aNoScope;
        if (Access(iRoot, out string aAccess) is SCP_CmdResult aFail) return aFail;
        if (!TryReportingList(aAccess, "/jobs", "jobs", out List<SCP_JsonData> aJobs, out string? aErr)) return ApiFail(aErr, ReportingApiName, ReportingApiHost);
        SCP_JsonData? aJob = FindReachJob(aJobs);
        if (aJob == null) return SCP_CmdResult.Fail(2, "✗ 還沒有 reach 報表工作 ⇒ 先跑 `op=reach-setup`（建好後 48 小時內才有第一份）");
        string aJobId = aJob.GetString("id", "");
        if (!TryReportingList(aAccess, "/jobs/" + Uri.EscapeDataString(aJobId) + "/reports", "reports", out List<SCP_JsonData> aReports, out aErr))
            return ApiFail(aErr, ReportingApiName, ReportingApiHost);

        // 同一天可能有不只一份（重產）⇒ 每天留建立時間最晚的那一份。
        var aByDay = new SortedDictionary<string, SCP_JsonData>(StringComparer.Ordinal);
        foreach (SCP_JsonData rep in aReports)
        {
            string aDay = rep.GetString("startTime", "");
            aDay = aDay.Length >= 10 ? aDay.Substring(0, 10) : aDay;
            if (aStart.Length > 0 && string.CompareOrdinal(aDay, aStart) < 0) continue;
            if (aEnd.Length > 0 && string.CompareOrdinal(aDay, aEnd) > 0) continue;
            if (!aByDay.TryGetValue(aDay, out SCP_JsonData? old) || string.CompareOrdinal(rep.GetString("createTime", ""), old.GetString("createTime", "")) > 0)
                aByDay[aDay] = rep;
        }
        if (aByDay.Count == 0)
            return SCP_CmdResult.Success($"# YouTube reach：還沒有報表",
                    $"- 報表工作 `{aJobId}` 建於 {aJob.GetString("createTime", "?")}；建好後 **48 小時內**才出第一份（並回補前 30 天）"
                    + (aReports.Count > 0 ? $"；有 {aReports.Count} 份，但都不在 {aStart}～{aEnd} 裡" : ""))
                .AddValue("reports", "0");

        var aTexts = new List<string>();
        var aDlFail = new List<string>();
        foreach (var kv in aByDay)
        {
            if (YouTubeClient.TryDownloadReport(aAccess, kv.Value.GetString("downloadUrl", ""), out string aCsv, out string? aDlErr)) aTexts.Add(aCsv);
            else aDlFail.Add($"{kv.Key}：{aDlErr}");
        }
        Dictionary<string, ReachSum> aSum = AggregateReach(aTexts, out List<string> aWarn, out bool aPercent);
        if (aOnly != null) aSum = aSum.Where(kv => aOnly.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        List<KeyValuePair<string, ReachSum>> aRows = aSum.OrderByDescending(kv => kv.Value.Impressions).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        Dictionary<string, (string Title, int Sec)> aInfo = FetchVideoInfo(aAccess, aRows.Take(50).Select(kv => kv.Key).ToList(), out string? aInfoErr);
        Dictionary<string, LedgerEntry> aMine = (LoadLedger(iRoot, out _) ?? new List<LedgerEntry>()).ToDictionary(x => x.VideoId, x => x, StringComparer.Ordinal);

        string aFirst = aByDay.Keys.First(), aLast = aByDay.Keys.Last();
        var r = SCP_CmdResult.Success($"# YouTube reach：縮圖曝光與點閱率（報表 {aTexts.Count} 份，{aFirst} ～ {aLast}）");
        if (aRows.Count == 0) r.Lines.Add("- 這些報表裡沒有任何影片的曝光" + (aOnly != null ? "（或指定的影片不在裡面）" : ""));
        else
        {
            r.Lines.Add("| 影片 | Senate | 曝光 | 點閱率 | 有曝光的天數 |");
            r.Lines.Add("|---|---|---:|---:|---:|");
            foreach (var kv in aRows.Take(50))
            {
                string aTitle = aInfo.TryGetValue(kv.Key, out var inf) ? inf.Title : (aMine.TryGetValue(kv.Key, out LedgerEntry? m) ? m.Title : "");
                r.Lines.Add($"| {Cut(aTitle, 28)}（{kv.Key}） | {(aMine.TryGetValue(kv.Key, out LedgerEntry? who) ? who.Persona : "—")} | {kv.Value.Impressions.ToString("#,0", CultureInfo.InvariantCulture)}"
                    + $" | {FmtPct(kv.Value.Ctr)} | {kv.Value.Days} |");
            }
            if (aRows.Count > 50) r.Lines.Add($"- …還有 {aRows.Count - 50} 支（曝光較少）沒列");
        }
        r.Lines.Add($"- 點閱率照曝光加權（Σ 曝光×點閱率 ／ Σ 曝光）；報表裡的點閱率{(aPercent ? "有 > 1 的值 ⇒ 當成**百分比**換算" : "都 ≤ 1 ⇒ 當成**比例**")}");
        r.Lines.Add("- 只算 YouTube 站內的縮圖曝光（官方定義）；外部連結點進來的觀看沒有曝光");
        foreach (string w in aWarn.Take(5)) r.Lines.Add("- ⚠ " + w);
        if (aWarn.Count > 5) r.Lines.Add($"- ⚠ …還有 {aWarn.Count - 5} 句");
        foreach (string f in aDlFail.Take(5)) r.Lines.Add("- ⚠ 沒下載到：" + f);
        if (aInfoErr != null) r.Lines.Add("- ⚠ 標題沒抓全：" + aInfoErr);
        r.AddValue("reports", aTexts.Count.ToString(CultureInfo.InvariantCulture));
        r.AddValue("videos", aRows.Count.ToString(CultureInfo.InvariantCulture));
        r.AddValue("impressions_total", aRows.Sum(kv => kv.Value.Impressions).ToString(CultureInfo.InvariantCulture));
        r.AddValue("first_day", aFirst);
        r.AddValue("last_day", aLast);
        if (aDlFail.Count > 0) r.ExitCode = 1;
        return r;
    }
}
