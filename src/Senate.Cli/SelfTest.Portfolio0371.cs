// 區塊職責：TASK-0371（交易所報酬率／法幣）的自我對拍。
// 物理意義：這幾格錯了都不會叫：
//           ① 法幣匯率表解析：方向反了（沒取倒數）、底幣不是 USD、表裡沒這個幣 —— 前兩種出來的數字都「合理」。
//           ② 平均成本重算：成本、已實現、報酬率；開帳快照不准重拍；帳上多出來的數量不准被當成 0 成本算進報酬。
//           ③ 兌換溢位：法幣單位極小（200 BTC→KRW 兩百多億張）⇒ 永久券是 long（TASK-0476 由 int 放寬）；超過 long 上限仍在落盤前擋下。
// 數值影響：只在 temp 目錄建假的資料根與 letters 根，跑完刪；⛔ 不碰真實資料。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Market;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow PortfolioFxParserCleanRoom()
    {
        const string aName = "法幣匯率表解析：取倒數、底幣必須 USD、缺幣／失敗／非正數一律拒收";
        try
        {
            const string aOk = "{\"result\":\"success\",\"base_code\":\"USD\",\"rates\":{\"USD\":1,\"JPY\":150,\"TWD\":32}}";
            var r = SCP_RateSourceParser.Parse("JPY", SCP_RateSourceKind.FxRatesPerUsd, aOk);
            bool aInv = r.Ok && r.Bid == 1m / 150m && r.Ask == r.Bid && !r.TwoSided;
            // 🔴 反向對照：底幣不是 USD ⇒ 倒數單位錯，必須拒收（數字本身看起來完全合理）
            bool aBase = !SCP_RateSourceParser.Parse("JPY", SCP_RateSourceKind.FxRatesPerUsd,
                "{\"base_code\":\"EUR\",\"rates\":{\"JPY\":160}}").Ok;
            bool aMissing = !SCP_RateSourceParser.Parse("KRW", SCP_RateSourceKind.FxRatesPerUsd, aOk).Ok;
            bool aErr = !SCP_RateSourceParser.Parse("JPY", SCP_RateSourceKind.FxRatesPerUsd,
                "{\"result\":\"error\",\"error-type\":\"invalid-key\"}").Ok;
            bool aNeg = !SCP_RateSourceParser.Parse("JPY", SCP_RateSourceKind.FxRatesPerUsd,
                "{\"base_code\":\"USD\",\"rates\":{\"JPY\":0}}").Ok;
            bool aKnown = SCP_RateSourceKind.IsKnown(SCP_RateSourceKind.FxRatesPerUsd);
            bool aAll = aInv && aBase && aMissing && aErr && aNeg && aKnown;
            return new CheckRow(aName,
                $"JPY 150/USD ⇒ {r.Bid:0.##########} USD（期望 1/150）={aInv}／🔴 底幣 EUR 拒收={aBase}／🔴 缺 KRW 拒收={aMissing}"
                + $"／🔴 result=error 拒收={aErr}／🔴 0 拒收={aNeg}／kind 登記={aKnown}",
                aAll ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow PortfolioReplayCleanRoom()
    {
        const string aName = "投資組合：開帳＝當下現值、不准重拍；兌換記事件；平均成本／已實現／報酬率；帳上多出的不計入（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_portfolio_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aData = Path.Combine(aTmp, "data");
            string aLettersDir = Path.Combine(aTmp, "letters");
            Directory.CreateDirectory(aData);
            Directory.CreateDirectory(Path.Combine(aLettersDir, "probe", "profile"));
            var aLetters = new SCP_LettersRoot(aLettersDir);

            var aCfg = new SCP_MarketRateConfig { TakerFeePct = 0m };
            aCfg.Quotes["BTC"] = new SCP_RateQuote { Symbol = "BTC", Bid = 100m, Ask = 100m, IsEnabled = true };
            aCfg.Quotes["JPY"] = new SCP_RateQuote { Symbol = "JPY", Bid = 0.01m, Ask = 0.01m, IsEnabled = true };
            if (!SCP_MarketRateCache.Save(aData, aCfg, out string? aCfgErr)) throw new Exception("config: " + aCfgErr);

            DateTime aT0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            SaveBook(aLetters, "probe", "BTC", 10, aT0);
            SaveBook(aLetters, "probe", "canvas", 5, aT0);

            // 開帳：10 BTC × 100 = 1000 USD；canvas 沒報價 ⇒ 不進快照
            var aProblems = new List<string>();
            var aOpen = SCP_Portfolio.BuildOpening(aData, aLetters, aT0, aProblems);
            bool aOpenOk = aOpen.Positions.Count == 1 && aOpen.Positions[0].ValueUsd == 1000m && aOpen.Unquoted.Count == 1
                           && SCP_Portfolio.TryWriteOpening(aLetters, aOpen, out var aWritten1, out _) && aWritten1.Count == 1
                           && File.Exists(SCP_Portfolio.OpeningPath(aLetters, "probe"));
            // 🔴 反向對照：第二次開帳必須被拒（同一份快照再寫一次 ⇒ 那一位寫不進去）；重新試算也必須把他列成不開帳
            bool aNoRedo = !SCP_Portfolio.TryWriteOpening(aLetters, aOpen, out var aWritten2, out var aRedoErrs)
                           && aWritten2.Count == 0 && aRedoErrs.Count == 1
                           && SCP_Portfolio.BuildOpening(aData, aLetters, aT0, new List<string>()).Positions.Count == 0;

            // BTC 漲到 120，換 2 BTC → JPY（免手續費）：2×120/0.01 = 24000 JPY
            aCfg.Quotes["BTC"].Bid = 120m; aCfg.Quotes["BTC"].Ask = 120m;
            SCP_MarketRateCache.Save(aData, aCfg, out _);
            var aSwap = SCP_VoucherSwap.ExecuteSwap(aLetters, aData, "probe", "btc", "jpy", 2, aT0.AddMinutes(1));
            bool aSwapOk = aSwap.Success && aSwap.PortfolioWarning == null && aSwap.ToAddedUnitsE8 == 24000L * SCP_VoucherBook.FractionScale;

            // 🔴 沒報價的券不記事件
            int aBefore = SCP_Portfolio.ReadEvents(aLetters, null, new List<string>()).Count;
            SCP_Portfolio.RecordFlow(aData, aLetters, "probe", "canvas", -1 * SCP_VoucherBook.FractionScale, "consume", "", aT0.AddMinutes(2), out bool aRecorded);
            bool aSkipUnquoted = !aRecorded && SCP_Portfolio.ReadEvents(aLetters, null, new List<string>()).Count == aBefore;

            var v = SCP_Portfolio.Build(aData, aLetters, "probe", aCfg, aT0.AddMinutes(3));
            var aBtc = v.Positions.Find(p => string.Equals(p.Symbol, "BTC", StringComparison.OrdinalIgnoreCase));
            var aJpy = v.Positions.Find(p => string.Equals(p.Symbol, "JPY", StringComparison.OrdinalIgnoreCase));
            // BTC：剩 8 張、成本 800（均價 100）、已實現 2×120−200 = 40、現值 960、報酬 +20%
            bool aBtcOk = aBtc != null && aBtc.TrackedE8 == 8 * SCP_VoucherBook.FractionScale && aBtc.CostUsd == 800m
                          && aBtc.RealizedUsd == 40m && aBtc.Roi == 0.2m && aBtc.BasisLabel == "上線時估值";
            // JPY：成本 240（＝放棄的 BTC 市值），標「實際成本」
            bool aJpyOk = aJpy != null && aJpy.CostUsd == 240m && aJpy.HasActualBasis && !aJpy.HasOpeningBasis;

            // 🔴 帳上憑空多 1 BTC（沒有事件）⇒ 只出現在差額，報酬率不變
            SaveBook(aLetters, "probe", "btc", 9, aT0.AddMinutes(4));
            var v2 = SCP_Portfolio.Build(aData, aLetters, "probe", aCfg, aT0.AddMinutes(5));
            var aBtc2 = v2.Positions.Find(p => string.Equals(p.Symbol, "BTC", StringComparison.OrdinalIgnoreCase));
            bool aDrift = aBtc2 != null && aBtc2.DriftE8 == SCP_VoucherBook.FractionScale && aBtc2.Roi == 0.2m;

            bool aAll = aOpenOk && aNoRedo && aSwapOk && aSkipUnquoted && aBtcOk && aJpyOk && aDrift && v.Problems.Count == 0;
            return new CheckRow(aName,
                $"開帳 1000 USD／canvas 不進={aOpenOk}／🔴 重拍被拒={aNoRedo}／兌換 24000 JPY＋事件={aSwapOk}／🔴 沒報價不記={aSkipUnquoted}"
                + $"／BTC 成本 {aBtc?.CostUsd}、已實現 {aBtc?.RealizedUsd}、報酬 {aBtc?.Roi}={aBtcOk}／JPY 成本 {aJpy?.CostUsd}={aJpyOk}"
                + $"／🔴 多 1 BTC 只進差額、報酬不變={aDrift}／problems={v.Problems.Count}",
                aAll ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    static CheckRow PortfolioSwapOverflowGuardCleanRoom()
    {
        const string aName = "大額貨幣券（TASK-0476）：200 BTC→KRW 兩百多億張成功並逐位落盤、大額扣券；目標券簿會超過 long 上限 ⇒ 落盤前拒絕、兩個券檔都不動（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_swapovf_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aData = Path.Combine(aTmp, "data");
            string aLettersDir = Path.Combine(aTmp, "letters");
            Directory.CreateDirectory(aData);
            Directory.CreateDirectory(Path.Combine(aLettersDir, "probe", "profile"));
            var aLetters = new SCP_LettersRoot(aLettersDir);
            var aCfg = new SCP_MarketRateConfig { TakerFeePct = 0m };
            aCfg.Quotes["BTC"] = new SCP_RateQuote { Symbol = "BTC", Bid = 100000m, Ask = 100000m, IsEnabled = true };
            aCfg.Quotes["KRW"] = new SCP_RateQuote { Symbol = "KRW", Bid = 0.0007m, Ask = 0.0007m, IsEnabled = true };
            SCP_MarketRateCache.Save(aData, aCfg, out _);
            DateTime aT0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
            SaveBook(aLetters, "probe", "BTC", 200, aT0);

            // 200 BTC × 100000 / 0.0007 ＝ 28,571,428,571.43 KRW（int 上限的 13 倍）⇒ 現在要成功，而且讀回逐位相同（TASK-0476）
            var aBig = SCP_VoucherSwap.ExecuteSwap(aLetters, aData, "probe", "btc", "krw", 200, aT0.AddMinutes(1));
            var aKrw = SCP_VoucherStore.Load(aLetters, "probe", "krw", out _);
            bool aBigOk = aBig.Success && aKrw.Permanent == 28_571_428_571L && aKrw.FractionalE8 == 42_857_142L
                          && SCP_VoucherStore.Load(aLetters, "probe", "btc", out _).Permanent == 0;
            // 大額扣券：扣 250 億張，存檔讀回
            bool aConsumed = SCP_VoucherStore.TryConsume(aKrw, 25_000_000_000L, aT0.AddMinutes(2), out string? aWhy)
                             && SCP_VoucherStore.Save(aLetters, aKrw, aT0.AddMinutes(2), "TEST", out _, out _)
                             && SCP_VoucherStore.Load(aLetters, "probe", "krw", out _).Permanent == 3_571_428_571L;
            // 🔴 守衛仍在：目標券簿已接近 long 上限 ⇒ 再換 1 BTC（≈1.43 億張）要擋下，BTC 與 KRW 都不動
            SaveBook(aLetters, "probe", "BTC", 1, aT0.AddMinutes(3));
            SaveBook(aLetters, "probe", "KRW", long.MaxValue - 10, aT0.AddMinutes(3));
            var aOver = SCP_VoucherSwap.ExecuteSwap(aLetters, aData, "probe", "btc", "krw", 1, aT0.AddMinutes(4));
            bool aRejected = !aOver.Success && (aOver.Error ?? "").Contains("上限")
                             && SCP_VoucherStore.Load(aLetters, "probe", "btc", out _).Permanent == 1
                             && SCP_VoucherStore.Load(aLetters, "probe", "krw", out _).Permanent == long.MaxValue - 10;
            // 指令層：`voucher` 的 amount 走 long 解析 —— 30 億張永久券發得進、扣得掉；限時券單批超過 int ⇒ exit 2、一張都不發
            SCP_Cmd aCmd = SCP_CmdRegistry.Find("voucher") ?? throw new InvalidOperationException("找不到 voucher Cmd");
            int Run(params (string K, string V)[] iArgs)
            {
                var a = new Dictionary<string, string> { ["letters_root"] = aLettersDir.Replace('\\', '/'), ["persona"] = "probe", ["voucher"] = "jpy", ["region"] = "TEST" };
                foreach (var (k, v) in iArgs) a[k] = v;
                (SCP_CmdArgs? aBound, List<string> aErr) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, a);
                if (aBound == null) throw new InvalidOperationException("Bind：" + string.Join("；", aErr));
                return aCmd.Execute(aBound).ExitCode;
            }
            // voucher 是 ⤷Server 指令 ⇒ 就地跑本體（同 SelfTest.BankArrival0441）：⛔ 不碰線上 Server、不碰真實資料樹
            bool aWasIn = Senate.Core.ServerContext.InServer; string aWasId = Senate.Core.ServerContext.ServerId;
            int aGrant, aUse, aExp; long aLeft;
            try
            {
                Senate.Core.ServerContext.InServer = true;
                Senate.Core.ServerContext.ServerId = SCP.Core.Proc.SCP_ServerIds.Default;
                aGrant = Run(("op", "grant"), ("amount", "3000000000"));
                aUse = Run(("op", "consume"), ("amount", "2500000000"));
                aLeft = SCP_VoucherStore.Load(aLetters, "probe", "jpy", out _).Permanent;
                aExp = Run(("op", "grant"), ("amount", "3000000000"), ("expires_at", "2099-01-01T00:00:00Z"));
            }
            finally { Senate.Core.ServerContext.InServer = aWasIn; Senate.Core.ServerContext.ServerId = aWasId; }
            int aBatches = SCP_VoucherStore.Load(aLetters, "probe", "jpy", out _).Expiring.Count;
            bool aCliOk = aGrant == 0 && aUse == 0 && aLeft == 500_000_000L && aExp == 2 && aBatches == 0;
            return new CheckRow(aName,
                $"CLI 發 30 億 exit {aGrant}／扣 25 億 exit {aUse}／剩 {aLeft}／限時券超 int exit {aExp}（批次 {aBatches}）={aCliOk}／200 BTC→KRW 成功={aBigOk}（{aKrw.Permanent} 張＋{aKrw.FractionalE8}/1e8；{aBig.Error}）／扣 250 億讀回={aConsumed}（{aWhy}）／🔴 近 long 上限再換 拒絕且兩檔不動={aRejected}（{aOver.Error}）",
                aCliOk && aBigOk && aConsumed && aRejected ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    static CheckRow PortfolioLettersMigrateCleanRoom()
    {
        const string aName = "投資組合帳住信件夾：舊落點沒搬要喊、試算零寫入、搬了算得出來、重跑不重複、券名顯示實際 ID（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_pfmig_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aData = Path.Combine(aTmp, "data");
            string aLettersDir = Path.Combine(aTmp, "letters");
            Directory.CreateDirectory(aData);
            Directory.CreateDirectory(Path.Combine(aLettersDir, "probe", "profile"));
            var aLetters = new SCP_LettersRoot(aLettersDir);
            var aCfg = new SCP_MarketRateConfig { TakerFeePct = 0m };
            aCfg.Quotes["GOLD"] = new SCP_RateQuote { Symbol = "GOLD", Bid = 10m, Ask = 10m, IsEnabled = true };
            SCP_MarketRateCache.Save(aData, aCfg, out _);
            DateTime aT0 = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

            // 券簿：實際檔名 `Gold`，15 張
            SaveBook(aLetters, "probe", "Gold", 15, aT0);

            // 舊落點（資料根 Market/portfolio/）：全員快照 10 張 ＋ 一筆 +5（舊紀錄的券名是全大寫）；另一筆屬於信件夾裡沒有的人
            string aLegacy = SCP_Portfolio.LegacyPortfolioDir(aData);
            Directory.CreateDirectory(Path.Combine(aLegacy, "events", "2026-10-02"));
            File.WriteAllText(Path.Combine(aLegacy, "opening.json"),
                "{\"schema_version\":1,\"at_utc\":\"2026-10-01T00:00:00Z\",\"positions\":[{\"persona\":\"probe\",\"symbol\":\"GOLD\",\"units_e8\":1000000000,\"bid_usd\":10,\"value_usd\":100}],\"unquoted\":[]}");
            File.WriteAllText(Path.Combine(aLegacy, "events", "2026-10-02", "20261002T000000000Z_probe_flow_aaaa0001.json"),
                "{\"schema_version\":1,\"kind\":\"flow\",\"at_utc\":\"2026-10-02T00:00:00Z\",\"persona\":\"probe\",\"symbol\":\"GOLD\",\"delta_e8\":500000000,\"bid_usd\":10,\"value_usd\":50,\"source\":\"demurrage\"}");
            File.WriteAllText(Path.Combine(aLegacy, "events", "2026-10-02", "20261002T000000000Z_ghost_flow_aaaa0002.json"),
                "{\"schema_version\":1,\"kind\":\"flow\",\"at_utc\":\"2026-10-02T00:00:00Z\",\"persona\":\"ghost\",\"symbol\":\"GOLD\",\"delta_e8\":100000000,\"bid_usd\":10,\"value_usd\":10,\"source\":\"demurrage\"}");

            // 🔴 反向對照①：沒搬之前，帳不讀舊落點 ⇒ 15 張全是差額，而且必須喊「還有沒搬的」
            var v0 = SCP_Portfolio.Build(aData, aLetters, "probe", aCfg, aT0.AddDays(2));
            var g0 = v0.Positions.Find(p => string.Equals(p.Symbol, "Gold", StringComparison.OrdinalIgnoreCase));
            bool aWarned = g0 != null && g0.DriftE8 == 15L * SCP_VoucherBook.FractionScale && v0.Problems.Exists(s => s.Contains("沒搬"));

            // 🔴 反向對照②：試算零寫入
            var aDry = SCP_Portfolio.MigrateLegacy(aData, aLetters, false, aT0.AddDays(2));
            string aPfDir = SCP_Portfolio.PortfolioDir(aLetters, "probe");
            bool aDryOk = aDry.EventsCopied == 1 && aDry.OpeningsWritten.Count == 1 && aDry.SkippedNoPersona.Count == 1
                          && !Directory.Exists(aPfDir) && !aDry.MarkerWritten;

            // 實搬：快照 10 ＋ 事件 5 ＝ 15 ⇒ 差額歸零；券名顯示券檔的實際 ID `Gold`
            var aRun = SCP_Portfolio.MigrateLegacy(aData, aLetters, true, aT0.AddDays(2));
            var v1 = SCP_Portfolio.Build(aData, aLetters, "probe", aCfg, aT0.AddDays(2));
            var g1 = v1.Positions.Find(p => string.Equals(p.Symbol, "Gold", StringComparison.OrdinalIgnoreCase));
            bool aRunOk = aRun.MarkerWritten && aRun.Problems.Count == 0 && g1 != null && g1.DriftE8 == 0
                          && g1.TrackedE8 == 15L * SCP_VoucherBook.FractionScale && g1.CostUsd == 150m && v1.Problems.Count == 0;
            bool aDisplay = g1 != null && g1.Symbol == "Gold";

            // 🔴 反向對照③：重跑不重複（已在信件夾的算已搬）
            var aAgain = SCP_Portfolio.MigrateLegacy(aData, aLetters, true, aT0.AddDays(2));
            bool aIdem = aAgain.EventsCopied == 0 && aAgain.EventsAlready == 1 && aAgain.OpeningsWritten.Count == 0
                         && aAgain.OpeningsAlready.Count == 1 && aAgain.Problems.Count == 0;

            // 新寫入的事件直接進信件夾、券名照原樣存
            SCP_Portfolio.RecordFlow(aData, aLetters, "probe", "Gold", 1 * SCP_VoucherBook.FractionScale, "grant", "", aT0.AddDays(3), out bool aRec);
            var aEvents = SCP_Portfolio.ReadEvents(aLetters, "probe", new List<string>());
            bool aNewLand = aRec && aEvents.Count == 2 && aEvents[1].Symbol == "Gold";

            bool aAll = aWarned && aDryOk && aRunOk && aDisplay && aIdem && aNewLand;
            return new CheckRow(aName,
                $"🔴 沒搬＝15 張全差額＋喊出來={aWarned}／🔴 試算零寫入（會搬 1、快照 1、沒這個人 1）={aDryOk}"
                + $"／實搬後差額 {g1?.DriftE8}、成本 {g1?.CostUsd}、標記={aRun.MarkerWritten}={aRunOk}／顯示「{g1?.Symbol}」={aDisplay}"
                + $"／🔴 重跑 copied={aAgain.EventsCopied} already={aAgain.EventsAlready}={aIdem}／新事件進信件夾＝{aNewLand}",
                aAll ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    static void SaveBook(SCP_LettersRoot iLetters, string iPersona, string iVoucher, long iPermanent, DateTime iNow)
    {
        var aBook = SCP_VoucherStore.Load(iLetters, iPersona, iVoucher, out string? aProblem);
        if (aProblem != null) throw new Exception("load: " + aProblem);
        aBook.Permanent = iPermanent;
        if (!SCP_VoucherStore.Save(iLetters, aBook, iNow, "TEST", out _, out string? aErr)) throw new Exception("save: " + aErr);
    }
}
