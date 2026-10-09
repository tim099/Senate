// 淨室驗收（TASK-0475）：暫存 git repo，`ly` 分支放來源歷史、main 放本地歷史；驗「只補本地缺的那一段」與四種跳過、零寫入試算、重跑不重複、CLI 接線。
using SCP.Core.Cmd;
using SCP.Core.Git;
using SCP.Core.Market;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow RateHistoryImportCleanRoom()
    {
        const string name = "匯率歷史跨區匯入：只補本地缺的幣與時段／不冒充最新版／不轉手匯入／試算零寫入／重跑不重複";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        string root = Path.Combine(Path.GetTempPath(), "senate_rateimport_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            SCP_GitResult Git(params string[] a)
            {
                var all = new List<string> { "-c", "user.name=selftest", "-c", "user.email=selftest@example.invalid", "-c", "commit.gpgsign=false" };
                all.AddRange(a);
                return SCP_Git.Run(root, all.ToArray());
            }
            static SCP_MarketRateConfig Cfg(params (string Sym, decimal Mid)[] q)
            {
                var c = new SCP_MarketRateConfig();
                c.Quotes["USD"] = new SCP_RateQuote { Symbol = "USD", Bid = 1, Ask = 1 };
                foreach (var (s, m) in q) c.Quotes[s] = new SCP_RateQuote { Symbol = s, Bid = m, Ask = m };
                return c;
            }
            DateTime D(int month, int day) => new DateTime(2026, month, day, 0, 0, 0, DateTimeKind.Utc);
            bool Write(SCP_MarketRateConfig c, DateTime t, string origin = "sync") => SCP_RateHistory.TryWriteVersion(root, c, t, origin, out _, out _);

            Check(Git("init", "-q", "-b", "main").Ok, "git init");
            File.WriteAllText(Path.Combine(root, "README.md"), "x\n");
            Check(Git("add", "README.md").Ok && Git("commit", "-q", "-m", "init").Ok, "初始 commit");
            Check(Git("checkout", "-q", "-b", "ly").Ok, "建 ly 分支");
            Check(Write(Cfg(("BTC", 100), ("JPY", 0.0063m)), D(9, 23))   // BTC 不早於本地第一個 BTC ⇒ 只匯 JPY
                && Write(Cfg(("BTC", 101), ("JPY", 0.0064m)), D(9, 25))
                && Write(Cfg(("JPY", 0.0065m)), D(9, 26), "import:elsewhere@abc")   // 來源本身是匯入的 ⇒ 不轉手
                && Write(Cfg(("BTC", 103), ("JPY", 0.0066m)), D(9, 29)), "寫來源版本");   // 晚於本地最新 ⇒ 不冒充最新版
            Check(Git("add", "Market").Ok && Git("commit", "-q", "-m", "ly history").Ok, "ly commit");
            Check(Git("checkout", "-q", "main").Ok && !Directory.Exists(Path.Combine(root, "Market")), "回 main（沒有 ly 的檔）");
            Check(Write(Cfg(("BTC", 99)), D(9, 22)) && Write(Cfg(("BTC", 102), ("JPY", 0.0067m)), D(9, 28)), "寫本地版本");

            int files = SCP_RateHistory.ListVersionFiles(root).Count;
            int btcBefore = SCP_RateHistory.BuildSeries(root, "BTC", null, null).Points.Count;
            var plan = SCP_RateHistoryImport.Plan(root, "ly", null);
            string Skips() => string.Join("；", plan.Items.Select(i => i.VersionId[..8] + ":" + (i.Skip.Length > 0 ? i.Skip : string.Join(",", i.Symbols))));
            Check(plan.Errors.Count == 0 && plan.ToWrite == 2 && SCP_RateHistory.ListVersionFiles(root).Count == files, "試算：寫 2 版且零寫入（" + Skips() + "）");
            Check(plan.Items.Count == 4 && plan.Items.All(i => i.Skip.Length > 0 || (i.Symbols.Count == 1 && i.Symbols[0] == "JPY")), "每版只匯本地缺的 JPY，BTC 不交錯：" + Skips());

            int written = SCP_RateHistoryImport.Apply(root, plan, out var errors);
            var jpy = SCP_RateHistory.BuildSeries(root, "JPY", null, null);
            Check(written == 2 && errors.Count == 0 && jpy.Points.Count == 3 && jpy.Points[0].FetchedAtUtc == D(9, 23), "寫入 2 版、JPY 走勢 3 點從 09-23 起（實際 " + jpy.Points.Count + "）");
            Check(SCP_RateHistory.BuildSeries(root, "BTC", null, null).Points.Count == btcBefore, "反向對照：BTC 點數不變");
            Check(SCP_RateHistory.TryReadLatest(root, out var latest, out _) && latest!.FetchedAtUtc == D(9, 28) && latest.Origin == "sync", "最新一版仍是本地自己的 sync 版");
            Check(SCP_RateHistory.TryReadVersion(SCP_RateHistory.VersionPath(root, SCP_RateHistory.VersionIdOf(D(9, 25))), out var imported, out _)
                && imported.Origin.StartsWith("import:ly@", StringComparison.Ordinal) && imported.Config.Quotes.Count == 2 && imported.Config.Quotes.ContainsKey("JPY"),
                "匯入版只留 USD＋JPY、origin 標來源 ref");
            Check(SCP_RateHistory.HasSyncVersionOnOrAfter(root, D(9, 28), out string? syncId, out _) && syncId == SCP_RateHistory.VersionIdOf(D(9, 28)), "每日閘照舊命中本地的 sync 版");

            var again = SCP_RateHistoryImport.Plan(root, "ly", null);
            Check(again.ToWrite == 0 && SCP_RateHistoryImport.Apply(root, again, out _) == 0, "重跑：0 版");
            var cli = SCP_CmdRegistry.Dispatch("rate", new Dictionary<string, string> { ["data_root"] = root, ["op"] = "import", ["from_ref"] = "ly" });
            Check(cli.ExitCode == 0 && cli.Values.Any(v => v.Key == "would_write" && v.Value == "0") && cli.Values.Any(v => v.Key == "written" && v.Value == "0"), "CLI 接線（op=import 試算）：" + string.Join(" ", cli.Lines.Take(2)));
            var bad = SCP_CmdRegistry.Dispatch("rate", new Dictionary<string, string> { ["data_root"] = root, ["op"] = "import" });
            Check(bad.ExitCode == 2, "region／from_ref 都沒給 ⇒ exit 2");
            return new CheckRow(name, failures.Count == 0 ? "只補 JPY 兩版、BTC 不交錯、晚於最新版與轉手匯入都跳過、試算零寫入、重跑 0 版、每日閘照舊、CLI 接線通過" : string.Join("；", failures), failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(name, e.ToString(), CheckResult.Fail); }
        finally
        {
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
            catch (Exception) { }
        }
    }
}
