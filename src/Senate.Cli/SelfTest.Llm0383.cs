// 區塊職責：TASK-0383（AI 模型頁搬到 Senate）的自我對拍。
// 物理意義：四格各驗一個「錯了也不會叫」的地方：
//           ① 表格解析：照表頭欄位切 —— 欄位值含空白（「2.5 GB」「100% GPU」「4 minutes from now」）、ps 多了 CONTEXT 欄，都不能讀錯欄。
//           ② 目錄比對：精確／變體／🔴 `qwen3:14b` 不可以讓 `qwen3:4b` 算已裝（llm_admin.py 原版的病）／沒寫 tag 的補 `:latest`。
//           ③ 截斷判定：撞到生成上限就是被切斷，不管切在思考段還是回答段。
//           ④ Cmd ↔ 頁面的約定：🔴 量不到（Installed=null）傳到頁面還是「不知道」，⛔ 不變成 0 個；量了 0 個就是 0 個。
// 數值影響：純記憶體；⛔ 不呼叫 ollama、不碰網路與資料根。
#nullable enable
using Senate.Core;
using Senate.Cli.Pages;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow LlmTableParse()
    {
        const string aName = "AI 模型・ollama 表格解析：照表頭欄位切，值含空白與新增欄位都不讀錯（TASK-0383）";
        try
        {
            var aFails = new List<string>();
            string aList = "NAME          ID              SIZE      MODIFIED    \n"
                         + "qwen3:0.6b    7df6b6e09427    522 MB    6 weeks ago    \n"
                         + "qwen3:4b      359d7dd4bcda    2.5 GB    6 weeks ago    \n";
            List<LlmModelRow> l = LlmOllama.ToRows(LlmOllama.ParseTable(aList));
            if (l.Count != 2) aFails.Add($"list 要 2 列得 {l.Count}");
            else if (l[1].Id != "qwen3:4b" || l[1].Size != "2.5 GB") aFails.Add($"list 第二列讀成 {l[1].Id}／{l[1].Size}");

            string aPs = "NAME          ID              SIZE      PROCESSOR    CONTEXT    UNTIL              \n"
                       + "qwen3:0.6b    7df6b6e09427    930 MB    100% GPU     4096       4 minutes from now    \n";
            List<LlmModelRow> p = LlmOllama.ToRows(LlmOllama.ParseTable(aPs));
            if (p.Count != 1) aFails.Add($"ps 要 1 列得 {p.Count}");
            else if (p[0].Processor != "100% GPU" || p[0].Size != "930 MB") aFails.Add($"🔴 ps 讀錯欄：大小 {p[0].Size}／跑在哪 {p[0].Processor}");

            if (LlmOllama.ToRows(LlmOllama.ParseTable("NAME    ID    SIZE    PROCESSOR    CONTEXT    UNTIL \n")).Count != 0) aFails.Add("只有表頭卻讀出列");
            return new CheckRow(aName, aFails.Count == 0 ? "list／ps（含 CONTEXT 欄）／只有表頭，逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow LlmCatalogMatch()
    {
        const string aName = "AI 模型・目錄比對：精確／變體／14b 不算 4b／沒寫 tag 補 :latest（TASK-0383）";
        try
        {
            var aFails = new List<string>();
            void Expect(string iId, string[] iHave, bool iInst, bool iExact, string iWhy)
            {
                (bool a, bool b) = LlmOllama.Match(iId, iHave);
                if (a != iInst || b != iExact) aFails.Add($"{iWhy}：{iId} vs [{string.Join(",", iHave)}] 得 ({a},{b})");
            }
            Expect("qwen3:4b", new[] { "qwen3:4b" }, true, true, "精確");
            Expect("qwen3:4b", new[] { "qwen3:4b-instruct-q4_K_M" }, true, false, "變體");
            Expect("qwen3:4b", new[] { "qwen3:14b" }, false, false, "🔴 14b 被當成 4b");
            Expect("qwen3:4b", new[] { "qwen2.5:4b" }, false, false, "別的家族");
            Expect("phi4-mini", new[] { "phi4-mini:latest" }, true, true, "沒寫 tag 的要補 :latest 當精確");
            Expect("qwen3:4b", Array.Empty<string>(), false, false, "空清單");
            return new CheckRow(aName, aFails.Count == 0 ? "6 種組合逐格對上" : string.Join("；", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow LlmTruncation()
    {
        const string aName = "AI 模型・試跑截斷：撞到生成上限就是被切斷（思考段或回答段都算）（TASK-0383）";
        try
        {
            var aFails = new List<string>();
            if (Cmd_Llm.JudgeTruncation(20, 120, "您好").Truncated) aFails.Add("沒撞到上限卻判截斷");
            if (!Cmd_Llm.JudgeTruncation(120, 120, "").Truncated) aFails.Add("回答空、撞到上限卻沒判截斷");
            if (!Cmd_Llm.JudgeTruncation(120, 120, "首先，用户要求").Truncated) aFails.Add("🔴 回答欄有半句、撞到上限卻沒判截斷（推理寫進回答欄那種）");
            if (Cmd_Llm.JudgeTruncation(500, 0, "").Truncated) aFails.Add("上限 0（不限）卻判截斷");
            if (!Cmd_Llm.JudgeTruncation(30, 120, "半句", "length").Truncated) aFails.Add("ollama 說 done_reason=length 卻沒判截斷");
            return new CheckRow(aName, aFails.Count == 0 ? "5 種情況逐格對上" : string.Join("；", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow LlmPageStatusContract()
    {
        const string aName = "AI 模型・Cmd ↔ 頁面約定：量不到傳到頁面還是「不知道」、0 個就是 0 個（TASK-0383）";
        try
        {
            var aFails = new List<string>();
            var v = new LlmVram(false, "", 0, 0, 0, "找不到 nvidia-smi", LlmOllama.VramFallbackGb, "fallback", "free");
            string Json(Cmd_Llm.LlmStatusInput s) => string.Join("\n", Cmd_Llm.RenderStatus(s, true).Lines);

            // 服務打不到：installed／loaded 都量不到
            LlmModelPage.StatusView? down = LlmModelPage.ParseStatus(Json(new Cmd_Llm.LlmStatusInput("C:/x/ollama.exe", true, "0.35", null, null, v, "connection refused")), out string? e1);
            if (down == null) aFails.Add("服務打不到那份解析失敗：" + e1);
            else
            {
                if (down.Installed != null) aFails.Add($"🔴 量不到的已安裝清單到頁面變成 {down.Installed.Count} 個");
                if (down.Loaded != null) aFails.Add("🔴 量不到的顯存清單到頁面變成有值");
                if (down.Catalog.Any(c => c.Installed)) aFails.Add("量不到卻有目錄項目被標成已裝");
                if (down.Hint.Length == 0) aFails.Add("服務打不到卻沒有提示");
                if (down.Vram.Source != "fallback") aFails.Add("顯存保底值的來源沒傳到頁面");
            }

            // 量了、0 個
            LlmModelPage.StatusView? empty = LlmModelPage.ParseStatus(Json(new Cmd_Llm.LlmStatusInput("C:/x/ollama.exe", true, "0.35", new(), new(), v, "")), out string? e2);
            if (empty == null) aFails.Add("0 個那份解析失敗：" + e2);
            else if (empty.Installed == null || empty.Installed.Count != 0) aFails.Add("🔴 量了 0 個，頁面卻讀成量不到");

            // 有裝：精確＋目錄外，phi4-mini:latest 不能又算目錄外
            var have = new List<LlmModelRow> { new("qwen3:4b", "2.5 GB", ""), new("phi4-mini:latest", "2.3 GB", ""), new("all-minilm:latest", "45 MB", "") };
            LlmModelPage.StatusView? some = LlmModelPage.ParseStatus(Json(new Cmd_Llm.LlmStatusInput("C:/x/ollama.exe", true, "0.35", have, new(), v, "")), out string? e3);
            if (some == null) aFails.Add("有裝那份解析失敗：" + e3);
            else
            {
                if (!some.Catalog.Single(c => c.Id == "qwen3:4b").Exact) aFails.Add("qwen3:4b 沒標成已安裝");
                if (!some.Catalog.Single(c => c.Id == "phi4-mini").Exact) aFails.Add("phi4-mini:latest 沒對上 phi4-mini");
                if (some.Extra.Count != 1 || some.Extra[0].Id != "all-minilm:latest") aFails.Add("目錄外清單不對：" + string.Join(",", some.Extra.Select(x => x.Id)));
            }

            // 找不到 ollama
            LlmModelPage.StatusView? none = LlmModelPage.ParseStatus(Json(new Cmd_Llm.LlmStatusInput(null, false, "", null, null, v, "")), out string? e4);
            if (none == null) aFails.Add("找不到 ollama 那份解析失敗：" + e4);
            else if (none.Found || none.Installed != null || none.DownloadUrl.Length == 0) aFails.Add("找不到 ollama 時頁面沒拿到「找不到＋下載頁」");

            return new CheckRow(aName, aFails.Count == 0 ? "服務打不到／0 個／有裝／找不到 ollama 四份，逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow LlmPageSettings()
    {
        const string aName = "AI 模型・頁面設定：沒存過＝初始值／存了讀得回來／舊檔缺欄位逐格補／壞檔不冒充「沒存過」／沒碰過的欄位用存檔值（TASK-0383）";
        string aDir = Path.Combine(Path.GetTempPath(), "senate-selftest-llm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var aFails = new List<string>();
            Directory.CreateDirectory(aDir);
            string aPath = Path.Combine(aDir, LlmModelPage.SettingsFileName);

            if (LlmModelPage.LoadSettings(aPath, out string src0) != LlmModelPage.Defaults || !src0.Contains("還沒存過")) aFails.Add("沒有檔卻不是初始值");
            if (LlmModelPage.Defaults.NumPredict != "4096" || !LlmModelPage.Defaults.Think || LlmModelPage.Defaults.TestModel != "qwen3:0.6b") aFails.Add("初始值跟 Tim 指定的不同");

            var aMine = LlmModelPage.Defaults with { Basis = "total", Prompt = "改過的一句", Think = false, NumPredict = "256" };
            File.WriteAllText(aPath, LlmModelPage.SettingsJson(aMine));
            if (LlmModelPage.LoadSettings(aPath, out _) != aMine) aFails.Add("存了讀不回同一份");

            File.WriteAllText(aPath, "{\"prompt\":\"只有這一格\"}");
            var aPartial = LlmModelPage.LoadSettings(aPath, out _);
            if (aPartial.Prompt != "只有這一格" || aPartial.NumPredict != LlmModelPage.Defaults.NumPredict) aFails.Add("舊檔缺欄位沒有逐格補初始值");

            File.WriteAllText(aPath, "{壞掉");
            var aBad = LlmModelPage.LoadSettings(aPath, out string srcBad);
            if (aBad != LlmModelPage.Defaults || !srcBad.Contains("讀不了")) aFails.Add("🔴 壞檔被說成「沒存過」（按存檔會安靜蓋掉它）");

            // 存了 total、使用者沒碰過下拉 ⇒ 送出去的要是 total（沒碰過的欄位讀存檔值，⛔ 不是寫死的 free）
            var g = new SCP.Core.Gui.SCP_Ui();
            if (LlmModelPage.StatusArgs(g, aMine)["vram_basis"] != "total") aFails.Add("🔴 存檔是 total，沒碰過下拉卻送 free");
            if (LlmModelPage.Current(g, aMine) != aMine) aFails.Add("沒碰過任何欄位卻被判成有未存的修改");
            g.SetField("llm/test/prompt", "又改了");
            if (LlmModelPage.Current(g, aMine) == aMine) aFails.Add("改了欄位卻沒被判成有未存的修改");

            return new CheckRow(aName, aFails.Count == 0 ? "五種情況逐格對上（temp 目錄）" : string.Join("；", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aDir, true); } catch (IOException) { } }
    }

    static CheckRow LlmCancelRunning()
    {
        const string aName = "AI 模型・中斷：跑到一半的外部程式被殺掉並回「已中斷」（不是逾時、不是成功）；中斷之後下一個動作照常（TASK-0383）";
        if (!OperatingSystem.IsWindows()) return new CheckRow(aName, "只在 Windows 驗（拿 ping 當一支會拖 20 秒的程式）", CheckResult.Skipped);
        try
        {
            var aFails = new List<string>();
            string aPing = Path.Combine(Environment.SystemDirectory, "PING.EXE");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task<(int Code, string Out, string Err)> t = Task.Run(() => LlmOllama.Run(aPing, new[] { "-n", "20", "127.0.0.1" }, 0));
            Thread.Sleep(500);
            LlmOllama.CancelRunning();
            if (!t.Wait(5000)) aFails.Add("🔴 中斷後 5 秒還沒回來");
            else
            {
                if (t.Result.Code != LlmOllama.CancelledExitCode) aFails.Add($"回 exit {t.Result.Code}（要 {LlmOllama.CancelledExitCode}）");
                if (!t.Result.Err.Contains("已中斷")) aFails.Add("訊息沒說已中斷");
            }
            long aMs = sw.ElapsedMilliseconds;
            // 換了一顆新的取消源 ⇒ 下一個動作不受上一次中斷影響
            (int c2, _, _) = LlmOllama.Run(aPing, new[] { "-n", "1", "127.0.0.1" }, 10_000);
            if (c2 != 0) aFails.Add($"🔴 中斷之後的下一個動作也被中斷了（exit {c2}）");
            return new CheckRow(aName, aFails.Count == 0 ? $"{aMs} ms 內回「已中斷」，下一個動作 exit 0" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }

    static CheckRow LlmPageArgsAndTestContract()
    {
        const string aName = "AI 模型・頁面送出的參數與試跑結果：選「總量」真的送 total／截斷與失敗傳到頁面不變成功（TASK-0383）";
        try
        {
            var aFails = new List<string>();
            // ① 下拉選單選了 total ⇒ 送出去的是 total（值存在 <key>/value；讀錯 key 會永遠送 free）
            var g = new SCP.Core.Gui.SCP_Ui();
            var aOpts = new List<SCP.Core.Gui.SCP_GuiOption> { new("free"), new("total") };
            g.SetField("llm/sel/basis/value", "total");
            string aShown = SCP.Core.Gui.SCP_GuiWidgets.Dropdown(g, "門檻", aOpts, "free", "llm/sel/basis");
            string aSent = LlmModelPage.StatusArgs(g, LlmModelPage.Defaults)["vram_basis"];
            if (aShown != "total" || aSent != "total") aFails.Add($"🔴 下拉顯示 {aShown}、送出 {aSent}（選了總量卻被吃掉）");
            g.SetField("llm/vram-manual", "6.5");
            if (!LlmModelPage.StatusArgs(g, LlmModelPage.Defaults).TryGetValue("vram_budget", out string? aMan) || aMan != "6.5") aFails.Add("手動門檻沒送出去");

            // ② 試跑結果 Cmd → 頁面：截斷、失敗、成功各一份
            Cmd_Llm.TestResult T(bool ok, bool tr, string err) => new(ok, tr, "qwen3:0.6b", "p", 1.2, 8, 30, tr ? "" : "您好", "想", "", err);
            LlmModelPage.TestView? tv = LlmModelPage.ParseTest(Cmd_Llm.TestJson(T(false, true, ""), ""), out _);
            if (tv == null || tv.Ok || !tv.Truncated) aFails.Add("🔴 截斷的試跑到頁面變成功（或沒標截斷）");
            tv = LlmModelPage.ParseTest(Cmd_Llm.TestJson(T(false, false, "回答是空的"), "磁碟滿了"), out _);
            if (tv == null || tv.Ok || !tv.Error.Contains("回答是空的") || !tv.Error.Contains("磁碟滿了")) aFails.Add("失敗原因／紀錄錯誤沒傳到頁面");
            tv = LlmModelPage.ParseTest(Cmd_Llm.TestJson(T(true, false, ""), ""), out _);
            if (tv == null || !tv.Ok || tv.Output != "您好" || tv.Thinking != "想") aFails.Add("成功的試跑到頁面讀錯欄");
            if (LlmModelPage.ParseTest("{\"ok\":true,\"seconds\":\"不是數字\"}", out string? aErr) != null || aErr == null) aFails.Add("壞掉的一行沒被擋下（會在畫面上炸）");

            return new CheckRow(aName, aFails.Count == 0 ? "總量／手動門檻送得出去、截斷／失敗／成功三份與壞行，逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
    }
}
