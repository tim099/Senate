// 區塊職責：`senate cmd youtube` 數據那一半（TASK-0016）不連網的部分（淨室）——
//   Analytics 回應的解析、影片長度、留存取點、reach CSV 的加總與點閱率加權、錯誤分類、日期範圍、影片 id、授權判斷。
// 物理意義：這幾格錯了不會叫 —— 讀錯欄、點閱率單位猜錯、沒資料印成 0，輸出照樣是一張整齊的表。
//           連網的那一半（真的打 Analytics／Reporting）這裡量不到 —— 那一半的讀數在 TASK-0016 的留言。
using SCP.Core.Json;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow YouTubeAnalyticsCleanRoom()
    {
        const string aName = "YouTube 數據：報表解析、影片長度、留存取點、reach 加總與點閱率加權、錯誤分類、日期與 id、授權判斷（淨室）";
        try
        {
            var aFails = new List<string>();
            int aChecks = 0;
            void Want(bool iOk, string iWhat) { aChecks++; if (!iOk) aFails.Add(iWhat); }

            // ── Analytics 回應 ──
            SCP_JsonData j = SCP_JsonParser.Parse("{\"columnHeaders\":[{\"name\":\"video\"},{\"name\":\"views\"},{\"name\":\"averageViewPercentage\"}],\"rows\":[[\"4qs_LnQ0R_I\",13,45.5]]}");
            Want(Cmd_YouTube.TryParseReportTable(j, out var t, out string? e) && t.Rows.Count == 1 && t.Cell(t.Rows[0], "video") == "4qs_LnQ0R_I"
                 && t.Num(t.Rows[0], "views") == 13 && Math.Abs(t.Num(t.Rows[0], "averageViewPercentage") - 45.5) < 1e-9, "正常的回應讀錯：" + e);
            Want(double.IsNaN(t.Num(t.Rows[0], "likes")), "沒這一欄應該是 NaN（⛔ 不是 0）");
            Want(Cmd_YouTube.TryParseReportTable(SCP_JsonParser.Parse("{\"columnHeaders\":[{\"name\":\"views\"}]}"), out t, out _) && t.Rows.Count == 0,
                 "沒有 rows ＝ 沒資料，應該讀成 0 列、不是錯");
            Want(!Cmd_YouTube.TryParseReportTable(SCP_JsonParser.Parse("{\"columnHeaders\":[{\"name\":\"a\"},{\"name\":\"b\"}],\"rows\":[[1]]}"), out _, out _),
                 "欄數對不上應該是錯（⛔ 不猜）");
            Want(!Cmd_YouTube.TryParseReportTable(SCP_JsonParser.Parse("{\"kind\":\"x\"}"), out _, out _), "沒有 columnHeaders 應該是錯");

            // ── 影片長度 ──
            Want(Cmd_YouTube.IsoDurationSeconds("PT3M29S") == 209, "PT3M29S 應該是 209 秒");
            Want(Cmd_YouTube.IsoDurationSeconds("PT55S") == 55, "PT55S 應該是 55 秒");
            Want(Cmd_YouTube.IsoDurationSeconds("PT1H2M3S") == 3723, "PT1H2M3S 應該是 3723 秒");
            Want(Cmd_YouTube.IsoDurationSeconds("not-a-duration") == -1, "讀不懂應該回 -1");

            // ── 留存：每 1% 一格、順序打亂也要排好 ──
            var aRows = new List<string>();
            for (int k = 100; k >= 1; k--) aRows.Add(FormattableString.Invariant($"[{k / 100.0:0.00},{1 - k / 200.0:0.000},0.4]"));
            var rt = SCP_JsonParser.Parse("{\"columnHeaders\":[{\"name\":\"elapsedVideoTimeRatio\"},{\"name\":\"audienceWatchRatio\"},{\"name\":\"relativeRetentionPerformance\"}],\"rows\":["
                + string.Join(",", aRows) + "]}");
            Want(Cmd_YouTube.TryParseReportTable(rt, out var rtt, out e), "留存回應讀不了：" + e);
            List<Cmd_YouTube.RetentionPoint> p = Cmd_YouTube.ParseRetention(rtt);
            Want(p.Count == 100 && p[0].Ratio < p[^1].Ratio, "留存點應該 100 個、照進度排好");
            Cmd_YouTube.RetentionPoint? q3 = Cmd_YouTube.RetentionAt(p, 3.0 / 209);   // 209 秒的片，3 秒 ≈ 1.44% ⇒ 第一個 ≥ 的是 2%
            Want(q3 != null && Math.Abs(q3.Value.Ratio - 0.02) < 1e-9, "3 秒（1.44%）應該取到 2% 那一格");
            Cmd_YouTube.RetentionPoint? q50 = Cmd_YouTube.RetentionAt(p, 0.5);
            Want(q50 != null && Math.Abs(q50.Value.Ratio - 0.5) < 1e-9 && Math.Abs(q50.Value.Watch - 0.75) < 1e-9 && Math.Abs(q50.Value.Relative - 0.4) < 1e-9,
                 "50% 那一格的值讀錯");
            Want(Cmd_YouTube.RetentionAt(p, 1.01) == null, "超過最後一點應該是 null");

            // ── reach CSV：加總、點閱率照曝光加權、百分比判斷、缺欄、壞列、引號 ──
            const string H = "date,channel_id,video_id,video_thumbnail_impressions,video_thumbnail_impressions_ctr";
            var aSum = Cmd_YouTube.AggregateReach(new[]
            {
                H + "\n20261110,UCx,vidAAAAAAAA,100,0.05\n20261110,UCx,vidBBBBBBBB,0,0\n",
                H + "\r\n20261111,UCx,vidAAAAAAAA,300,0.10\r\n",
            }, out List<string> aWarn, out bool aPct);
            Want(!aPct && aWarn.Count == 0, "比例格式不該被當成百分比、不該有警告");
            Want(aSum.TryGetValue("vidAAAAAAAA", out var sa) && sa.Impressions == 400 && Math.Abs(sa.Ctr - 0.0875) < 1e-9 && sa.Days == 2
                 && sa.FirstDate == "20261110" && sa.LastDate == "20261111", "加總或加權點閱率錯（應該 400 次、8.75%、2 天）");
            Want(aSum.TryGetValue("vidBBBBBBBB", out var sb) && sb.Impressions == 0 && double.IsNaN(sb.Ctr), "0 曝光的點閱率應該是 NaN（⛔ 不是 0%）");
            var aSumPct = Cmd_YouTube.AggregateReach(new[] { H + "\n20261110,UCx,vidAAAAAAAA,100,5\n20261111,UCx,vidAAAAAAAA,300,10\n" }, out _, out aPct);
            Want(aPct && Math.Abs(aSumPct["vidAAAAAAAA"].Ctr - 0.0875) < 1e-9, "點閱率 > 1 應該整批當百分比換算");
            Cmd_YouTube.AggregateReach(new[] { "date,video_id\n20261110,vidAAAAAAAA\n", H + "\n20261110,UCx,vidAAAAAAAA,abc,0.1\n" }, out aWarn, out _);
            Want(aWarn.Count == 2 && aWarn[0].Contains("缺欄") && aWarn[1].Contains("讀不懂"), "缺欄、壞列都要記一句：" + string.Join("／", aWarn));
            List<string> aCsv = Cmd_YouTube.ParseCsvLine("a,\"b,c\",\"d\"\"e\",");
            Want(aCsv.Count == 4 && aCsv[1] == "b,c" && aCsv[2] == "d\"e" && aCsv[3] == "", "CSV 引號欄位讀錯");

            // ── 錯誤分類 ──
            Want(Cmd_YouTube.ClassifyDataApiError("Analytics 報表 失敗：HTTP 403 insufficientPermissions：Request had insufficient authentication scopes.", "A", "a.googleapis.com", out string h1) == 3
                 && h1.Contains("op=login"), "授權不夠應該 exit 3、叫人重登");
            Want(Cmd_YouTube.ClassifyDataApiError("HTTP 403 accessNotConfigured：YouTube Analytics API has not been used in project 1 before or it is disabled.", "YouTube Analytics API", "youtubeanalytics.googleapis.com", out string h2) == 1
                 && h2.Contains("YouTube Analytics API") && h2.Contains("啟用"), "API 沒啟用應該說去哪啟用哪一支");
            Want(Cmd_YouTube.ClassifyDataApiError("HTTP 403 SERVICE_DISABLED：x", "YouTube Reporting API", "youtubereporting.googleapis.com", out string h3) == 1 && h3.Contains("Reporting"),
                 "SERVICE_DISABLED 也算沒啟用");
            Want(Cmd_YouTube.ClassifyDataApiError("HTTP 500：boom", "A", "a", out string h4) == 1 && h4.Length == 0, "其他錯誤不該亂給提示");

            // ── 日期範圍 ──
            Want(Cmd_YouTube.TryDateRange("", "", new DateTime(2026, 11, 12), out string s1, out string e1, out _) && s1 == "2025-11-12" && e1 == "2026-11-12",
                 "沒給日期應該是最近 365 天");
            Want(!Cmd_YouTube.TryDateRange("2026-11-10", "2026-11-01", DateTime.Today, out _, out _, out _), "start 比 end 晚應該擋");
            Want(!Cmd_YouTube.TryDateRange("2026-13-01", "", DateTime.Today, out _, out _, out _), "不存在的日期應該擋");

            // ── 影片 id ──
            var aLedger = new List<Cmd_YouTube.LedgerEntry> { new() { VideoId = "4qs_LnQ0R_I" }, new() { VideoId = "qtaUeqE1Zck" } };
            Want(Cmd_YouTube.TryResolveVideoIds(" 4qs_LnQ0R_I, qtaUeqE1Zck,4qs_LnQ0R_I ", null, out var ids, out _) && ids.Count == 2, "給的 id 應該去重成 2 支");
            Want(!Cmd_YouTube.TryResolveVideoIds("not an id", aLedger, out _, out _), "不像 id 的應該擋");
            Want(Cmd_YouTube.TryResolveVideoIds("", aLedger, out ids, out _) && ids.Count == 2 && ids[0] == "4qs_LnQ0R_I", "沒給 ⇒ 帳本裡的全部、照順序");
            Want(!Cmd_YouTube.TryResolveVideoIds("", null, out _, out _), "帳本讀不了又沒給 id 應該擋");
            Want(!Cmd_YouTube.TryResolveVideoIds("", new List<Cmd_YouTube.LedgerEntry>(), out _, out _), "帳本是空的又沒給 id 應該擋");

            // ── 授權判斷與查詢字串 ──
            List<string> aMissing = YouTubeClient.MissingScopes(YouTubeClient.Scope, YouTubeClient.AnalyticsScopes);
            Want(aMissing.Count == 2, "只有 force-ssl 時應該缺 2 個 Analytics 範圍");
            Want(YouTubeClient.MissingScopes(string.Join(" ", YouTubeClient.LoginScopes), YouTubeClient.AnalyticsScopes).Count == 0, "全拿到時不該缺");
            Want(new YouTubeCredStatus { TokenReady = true, GrantedScope = YouTubeClient.Scope }.AnalyticsReady == false
                 && new YouTubeCredStatus { TokenReady = true, GrantedScope = string.Join(" ", YouTubeClient.LoginScopes) }.AnalyticsReady
                 && new YouTubeCredStatus { TokenReady = false, GrantedScope = string.Join(" ", YouTubeClient.LoginScopes) }.AnalyticsReady == false,
                 "AnalyticsReady 判斷錯");
            Want(Cmd_YouTube.BuildQuery(new[] { ("ids", "channel==MINE"), ("filters", "video==a,b") }) == "ids=channel%3D%3DMINE&filters=video%3D%3Da%2Cb", "查詢字串沒 escape 對");
            Want(Cmd_YouTube.TrafficLabel("YT_SEARCH").Contains("YT_SEARCH") && Cmd_YouTube.TrafficLabel("SOMETHING_NEW") == "SOMETHING_NEW", "流量來源的代碼要留著、不認得的照原樣");

            return aFails.Count == 0
                ? new CheckRow(aName, $"{aChecks} 格都過（連網的 Analytics／Reporting 這裡不量）", CheckResult.Pass)
                : new CheckRow(aName, $"{aFails.Count}／{aChecks} 格沒過：" + string.Join("；", aFails), CheckResult.Fail);
        }
        catch (Exception ex) { return new CheckRow(aName, "例外：" + ex.GetType().Name + "：" + ex.Message, CheckResult.Fail); }
    }
}
