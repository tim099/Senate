// 區塊職責：`senate cmd sculpture` —— 3D 體積雕刻的入口＋收費＋分享，**不需要 Unity Editor**（TASK-0363）。
// 物理意義：Unity `ucmd run Sculpture`（Cmd_Sculpture，summit 2026-08-13）的搬家版。引擎**照舊是 python `sculpt.py`**
//          （gura 的幾何／渲染／快取，不碰錢）—— 本檔只做「參數 → 預授權 → 叫引擎 → 按實際結算 → 分享」。
//          ⚠ Senate 這側原則上不 spawn python（TASK-0360 自由時間搬家刻意不叫）；這裡是例外，理由寫在單上：
//            引擎 1500 行、只有一個消費端，C# 重寫是另一張單的事（D1：媒體類能力留給 python sidecar）。
//          收費三段（與 Unity 版同語意）：
//            ① 預授權：用「這一刀**最壞**會花多少」（⌈clamp 後體積/100⌉；貼圖＝圖面積 × thickness）擋餘額不足
//               ⇒ 不夠就**引擎不跑、一毛不扣**（驗收④）。
//            ② 引擎執行：引擎回報的實際落地數＝結算依據（禁覆蓋 skip 掉的不收）。
//            ③ 結算：⌈實際落地/100⌉，付款順序 auto＝限時券 → 永久券 → 酒館券 → token（與 canvas 同一支 `TryPlan`）。
// 數值影響：🩸 Unity 版靠「Editor 主執行緒序列化」所以沒有鎖；搬到 Senate 之後**兩個人可以同時雕**：
//            ⇒ 本檔拿兩把鎖包住「預授權 → 引擎 → 結算」整段：
//              · 雕刻全域鎖（`Sculpture/_engine.lock`）—— 引擎的快取檔是讀改寫，兩刀同時跑會吃掉其中一刀；
//              · 畫布付款鎖（與 `canvas place` **同一顆檔名**）—— 同一個人的券也可能同時被畫布花掉，
//                不鎖的話預授權看到的餘額在結算時已經不在了。
//          回傳檔 `letters/<P>/cmd/sculpture_<op>.md`（沒帶 persona 的觀測類不落檔，報告印在輸出）。
// exit：0 成功／2 參數不合／3 付款被拒（**零副作用**）／4 拿不到鎖／5 引擎拒絕（未落子、未扣費）／
//       1 **已落子但結算沒收齊**（大聲失敗，帳要人對 —— ⛔ 不假裝沒落子）／70 例外。
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SCP.Core.Bank;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace Senate.Core;

public sealed class Cmd_Sculpture : MorningLocalCmd
{
    public override string Name => "sculpture";

    public override string Summary =>
        "3D 體積雕刻（box/carve/stamp2d/stampimg/view/slice/stats）—— 落子收費 ⌈實際落地/100⌉，觀測免費；**不需要 Unity Editor**";

    public override string Details =>
        "落子類（box／carve／stamp2d／stampimg）要 persona；收費走 Senate 銀行與券（pay=auto：限時券 → 永久券 → 酒館券 → token）。\n"
        + "· 預授權用**最壞費用**擋餘額 —— 不夠 ⇒ 引擎不跑、一毛不扣（exit 3）。\n"
        + "· 結算只對**實際落地**收費（禁覆蓋 skip 掉的不收）；同一刀只扣一次（兩把鎖包住整段）。\n"
        + "· 落子成功會渲一張全景、以落子者身分發進酒館（`tavern-post` 帶圖）；`share=0` 關掉。分享失敗不讓落子失敗。\n"
        + "· 觀測類（view／slice／stats）免費，persona 選填。\n"
        + "· 引擎仍是 python `<UCL_Core>/Tools~/AgentCommands/sculpt.py`（掛載位置讀專案根的 `.gitmodules`）。\n"
        + "exit：0 成功／2 參數不合／3 付款被拒（零副作用）／4 拿不到鎖／5 引擎拒絕（未落子、未扣費）／1 已落子但結算沒收齊（要對帳）。";

    public override string Example => SCP_CmdRegistry.Invoke("sculpture --arg op=stats");

    protected override string CliNextHint => "";

    /// <summary>計費粒度：每 100 voxel 收 1 單位（Tim 拍板費率 ⌈實際落地數/100⌉）。</summary>
    public const int VoxelsPerUnit = 100;
    /// <summary>單次體積上限（與 sculpt.py 引擎端同值 —— 兩端對齊義務）。</summary>
    public const int MaxVolume = 1000000;
    const int EngineTimeoutMs = 120000;
    /// <summary>等雕刻全域鎖多久：要比一次引擎執行長（別人那一刀正在渲染就等它）。</summary>
    const double EngineLockTimeoutSec = 150;
    const string SpendKind = "sculpture_place";

    static readonly string[] s_Ops = { "box", "carve", "stamp2d", "stampimg", "view", "slice", "stats" };

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("op", "做什麼", iRequired: true, iChoices: s_Ops),
        new SCP_CmdArgSpec("persona", "誰（落子類**必填** —— 錢記在人頭上；觀測類選填）"),
        new SCP_CmdArgSpec("project", "哪個專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略"),
        new SCP_CmdArgSpec("account", "付 token 的帳號；不給 ⇒ 由 persona 的權威綁定檔解（⛔ 解不出來不猜）"),
        new SCP_CmdArgSpec("pay", "付款方式", iDefault: "auto", iChoices: new[] { "auto", "freetime", "voucher", "token" }),
        new SCP_CmdArgSpec("x1", "box/carve：AABB 一角 x（0-255）"),
        new SCP_CmdArgSpec("x2", "box/carve：另一角 x"),
        new SCP_CmdArgSpec("y1", "box/carve：一角 y"),
        new SCP_CmdArgSpec("y2", "box/carve：另一角 y"),
        new SCP_CmdArgSpec("z1", "box/carve：一角 z"),
        new SCP_CmdArgSpec("z2", "box/carve：另一角 z"),
        new SCP_CmdArgSpec("color", "box：顏色 index（0-255，RGB332）", iDefault: "19"),
        new SCP_CmdArgSpec("src_x1", "stamp2d：2D 畫布來源區域一角 x（0-2047）"),
        new SCP_CmdArgSpec("src_y1", "stamp2d：一角 y"),
        new SCP_CmdArgSpec("src_x2", "stamp2d：另一角 x"),
        new SCP_CmdArgSpec("src_y2", "stamp2d：另一角 y"),
        new SCP_CmdArgSpec("png", "stampimg：RGBA PNG 路徑（透明像素不落地）"),
        new SCP_CmdArgSpec("resize", "stampimg：W,H（NEAREST）"),
        new SCP_CmdArgSpec("at", "stamp 類：圖左上角貼在哪（x,y,z）"),
        new SCP_CmdArgSpec("facing", "stamp 類：貼片法線 x+|x-|y+|y-|z+|z-（引擎預設 z+）"),
        new SCP_CmdArgSpec("thickness", "stamp 類：層數", iDefault: "1"),
        new SCP_CmdArgSpec("overwrite", "stamp 類：1 ＝ 覆蓋既有 voxel（預設跳過）"),
        new SCP_CmdArgSpec("expect_pixels", "stamp 類：預覽印出的非透明像素數，對不上即拒絕（**強烈建議帶**）"),
        new SCP_CmdArgSpec("alpha_threshold", "stamp 類：alpha 門檻（引擎預設 128）"),
        new SCP_CmdArgSpec("allow_clip", "stamp 類：1 ＝ 接受越界裁切（預設越界即拒絕）"),
        new SCP_CmdArgSpec("exhibit_id", "stamp 類：貼完自動登錄／擴充展品"),
        new SCP_CmdArgSpec("exhibit_title", "stamp 類：展品標題"),
        new SCP_CmdArgSpec("exhibit_desc", "stamp 類：展品描述"),
        new SCP_CmdArgSpec("exhibit_margin", "stamp 類：展品邊界格數（引擎預設 2）"),
        new SCP_CmdArgSpec("region", "view：裁切範圍／slice **必填**（x1..x2,y1..y2,z1..z2）"),
        new SCP_CmdArgSpec("exclude_color", "view：不畫哪些顏色（c,c,…）"),
        new SCP_CmdArgSpec("exhibit", "view：展品 preset id"),
        new SCP_CmdArgSpec("light_dir", "view：光線方向 x,y,z"),
        new SCP_CmdArgSpec("ambient", "view：環境光 0-1"),
        new SCP_CmdArgSpec("zoom", "view：倍率（省略＝自動縮放）"),
        new SCP_CmdArgSpec("axis", "slice：法線與近端方向（引擎預設 z+）"),
        new SCP_CmdArgSpec("out", "slice：輸出 PNG 路徑（引擎預設 Sculpture/_last_slice.png）"),
        new SCP_CmdArgSpec("share", "落子成功後發酒館帶圖（0 ＝ 不發）", iDefault: "1"),
    };

    protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
    {
        string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
        string aPersona = iArgs.Get("persona").Trim();
        if (aPersona.Length > 0 && !SCP_Cmd_FreeTimeActivity.IsSafePersona(aPersona))
        {
            Fail(ioResult, 2, "✗ persona 不合法（不可含 `/` `\\` `:` 或是 `.` / `..`）：'" + aPersona + "'");
            return null;
        }
        string? aScript = Cmd_Task.UclCoreTool(iRoots.ProjectRoot, "sculpt.py", out string aWhy);
        if (aScript == null)
        {
            Fail(ioResult, 1, "✗ 找不到雕刻引擎：" + aWhy);
            return null;
        }
        var aCtx = new Ctx(iRoots, iArgs, ioResult, aOp, aPersona, aScript);
        switch (aOp)
        {
            case "box":
            case "carve":
            case "stamp2d":
            case "stampimg":
                return OpPlace(aCtx);
            default:
                return OpReadOnly(aCtx);
        }
    }

    sealed class Ctx
    {
        public readonly SCP_MorningRoots Roots;
        public readonly SCP_CmdArgs Args;
        public readonly SCP_CmdResult Result;
        public readonly string Op;
        public readonly string Persona;
        public readonly string Script;
        public readonly StringBuilder Report = new StringBuilder();

        public Ctx(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult iResult, string iOp, string iPersona, string iScript)
        {
            Roots = iRoots; Args = iArgs; Result = iResult; Op = iOp; Persona = iPersona; Script = iScript;
            Report.Append("# Sculpture op=").Append(iOp)
                  .Append(iPersona.Length > 0 ? " persona=" + iPersona : "")
                  .Append("  ts=`").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture))
                  .Append("`（本地時間）\n\n");
        }

        public string SculptDir => Path.Combine(Roots.DataRoot, "Sculpture").Replace('\\', '/');
    }

    // ===========================================================
    // 區塊職責：落子四 op（box／carve／stamp2d／stampimg）—— 預授權 → 引擎 → 結算 → 分享。
    // 物理意義：四個 op 只差「最壞費用怎麼量」與「引擎參數怎麼組」，閘門與帳完全同型 ⇒ 共用本方法。
    //          ⚠ 最壞費用一律**本檔自己量**（stampimg 讀 PNG IHDR）—— 用呼叫端申報的數字守自己的門，門就是假的。
    // ===========================================================
    static string? OpPlace(Ctx c)
    {
        if (c.Persona.Length == 0)
            return Blocked(c, 2, "落子需要 --arg persona=<名字>（錢認 persona 的券與帳戶，不能用猜的）");

        if (!TryBuildPlace(c, out List<string> aEngineArgs, out long aWorstVolume, out string aWhere, out string aBad))
            return Blocked(c, 2, aBad);
        if (aWorstVolume > MaxVolume)
            return Blocked(c, 2, $"最壞體積 {aWorstVolume:N0} 超過上限 {MaxVolume:N0}（{aWhere}）—— 拆多刀、縮圖或降 thickness");

        string aPay = PayOf(c);
        string aAccount = c.Args.Get("account").Trim();
        if (aAccount.Length == 0)
        {
            aAccount = SCP_BankAccountResolver.ResolvePersonaAccount(c.Roots.LettersRoot, c.Roots.DataRoot,
                                                                   c.Roots.Region, c.Persona, out string aTrace);
            c.Report.Append("- 帳戶：").Append(aAccount.Length > 0 ? "`" + aAccount + "`" : "（解不出來 ⇒ 只能用券）")
                    .Append("〔").Append(aTrace).Append("〕\n");
        }
        else c.Report.Append("- 帳戶：`").Append(aAccount).Append("`（顯式給的）\n");

        SCP_ICanvasGateway? aGate = SCP_CanvasGatewayHost.For(c.Roots.DataRoot);
        if (aGate == null)
            return Blocked(c, 1, "這個宿主沒有裝上付款閘 ⇒ 收不了錢，所以不落子（⛔ 不「先雕再說」—— 那等於免費 voxel）");
        c.Report.Append("- ").Append(aGate.HostQualifier).Append('\n');

        int aMaxUnits = CeilDiv(aWorstVolume, VoxelsPerUnit);

        // ── 臨界區①：雕刻全域鎖（引擎快取是讀改寫）──
        SCP_FileLock aEngineLock;
        try { aEngineLock = SCP_FileLock.Acquire(Path.Combine(c.SculptDir, "_engine"), EngineLockTimeoutSec); }
        catch (Exception e) { return Blocked(c, 4, "拿不到雕刻鎖（別人那一刀還在跑？）：" + e.Message + " —— 未落子、未扣費"); }
        using (aEngineLock)
        {
            SCP_EngineResult aRes;
            SCP_CanvasPayPlan aPlan = default;
            int aCharge = 0;
            var aLedgerRefs = new List<string>();
            var aShortPaid = new List<string>();
            string aRef = "";

            // ── 臨界區②：畫布付款鎖（與 canvas place 同一顆檔 —— 同一個人的券不會在預授權與結算之間被花掉）──
            var aCanvasPaths = new SCP_CanvasPaths(new SCP_DataRoot(c.Roots.DataRoot));
            IDisposable? aPayLock = SCP_CanvasPlace.TryAcquireLock(aCanvasPaths, aAccount.Length > 0 ? aAccount : "noaccount",
                                                                  c.Persona, out string aLockWhy);
            if (aPayLock == null)
                return Blocked(c, 4, "拿不到付款鎖：" + aLockWhy + " —— 未落子、未扣費（⛔ 不強奪：對方可能還在扣款中）");
            try
            {
                // ① 預授權（最壞費用）—— 不夠就引擎不跑
                if (!SCP_CanvasPlace.TryPlan(aGate, c.Persona, aAccount, aMaxUnits, aPay, out _, out string aPlanWhy))
                    // ⚠ 不寫成「預授權不足」：TryPlan 失敗有兩種 —— 真的不夠，或**查不到**（-1）。
                    //   把「不知道」說成「不夠」，使用者會照著去加值（2026-10-01 實測：版本不符的 Server 問不到券，訊息卻說不足）。
                    return Blocked(c, 3, $"付款預驗沒過 —— 本刀最壞費用 {aMaxUnits} 單位（{aWhere}，⌈{aWorstVolume:N0}/{VoxelsPerUnit}⌉）："
                                         + aPlanWhy + "\n- 引擎未執行，未扣任何費用（不夠 ⇒ 縮小範圍／換 pay 模式；查不到 ⇒ 照上面那句的出口走）");

                // ② 引擎
                aRes = RunEngine(c, aEngineArgs);
                if (aRes.Exit != 0 || aRes.Json == null
                    || (aRes.Json.GetString("status", "success") != "success"))
                {
                    string aStatus = aRes.Json?.GetString("status", "") ?? "";
                    string aReason = aRes.Json?.GetString("reason", "") ?? "";
                    c.Report.Append("## blocked\n- reason: 引擎未落子（exit=").Append(aRes.Exit)
                            .Append(aStatus.Length > 0 ? ", status=" + aStatus : "").Append("）—— 未扣任何費用\n");
                    if (aReason.Length > 0) c.Report.Append("- engine: ").Append(aReason).Append('\n');
                    if (aStatus == "mismatch")
                        c.Report.Append("- how: expect_pixels 是「你看的預覽」與「引擎吃的圖」的對帳閘門 —— 重跑預覽拿新數字。\n");
                    if (aStatus == "out_of_bounds")
                        c.Report.Append("- how: 改小 at、用 resize 縮圖，或顯式 allow_clip=1（別讓「只貼了一角」看起來像成功）。\n");
                    c.Report.Append("```\n").Append(aRes.Combined()).Append("\n```\n");
                    return Finish(c, 5, "✗ 引擎未落子（exit " + aRes.Exit + (aStatus.Length > 0 ? "，" + aStatus : "") + "）—— 未扣任何費用");
                }

                // ③ 結算：只對實際落地收費
                int aActual = c.Op == "carve" ? aRes.Json.GetInt("carved_count", 0) : aRes.Json.GetInt("placed_count", 0);
                aCharge = aActual > 0 ? CeilDiv(aActual, VoxelsPerUnit) : 0;
                string aEventFile = aRes.Json.GetString("event_file", "");
                aRef = "sculpture:" + Path.GetFileName(aEventFile);
                c.Result.AddValue("placed", aActual.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("charged", aCharge.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("event_file", aEventFile);

                if (aCharge > 0)
                {
                    // 鎖還握著 ⇒ 重新規劃一次（拿實際費用，讀的是此刻的餘額）
                    if (!SCP_CanvasPlace.TryPlan(aGate, c.Persona, aAccount, aCharge, aPay, out aPlan, out string aSettleWhy))
                        aShortPaid.Add("結算規劃失敗：" + aSettleWhy);
                    else
                        Settle(aGate, c.Persona, aAccount, aCharge, aPlan, aRef, aLedgerRefs, aShortPaid);
                }

                c.Report.Append("## result（引擎回報＝結算依據）\n");
                AppendPlaceFacts(c, aRes.Json, aActual, aWhere);
                c.Report.Append("- charged: **").Append(aCharge).Append(" 單位**（⌈").Append(aActual).Append('/')
                        .Append(VoxelsPerUnit).Append("⌉；預授權上限 ").Append(aMaxUnits).Append("；帳單跟著事實走，不跟著意圖走）\n");
                c.Report.Append("- pay_breakdown: freetime(限時券)=").Append(aPlan.Expiring)
                        .Append(" voucher(永久券)=").Append(aPlan.Permanent)
                        .Append(" tavern(酒館券)=").Append(aPlan.Tavern)
                        .Append(" token=").Append(aPlan.Token).Append("（pay=").Append(aPay).Append("）\n");
                c.Report.Append("- ledger_refs: ").Append(aLedgerRefs.Count > 0 ? string.Join(", ", aLedgerRefs) : "（無）").Append('\n');
                c.Report.Append("- event: `").Append(aEventFile).Append("`\n");
                c.Result.AddValue("pay_freetime", aPlan.Expiring.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("pay_voucher", aPlan.Permanent.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("pay_tavern", aPlan.Tavern.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("pay_token", aPlan.Token.ToString(CultureInfo.InvariantCulture));
            }
            finally { aPayLock.Dispose(); }

            if (aShortPaid.Count > 0)
            {
                // 🩸 voxel 已經落了而錢沒收齊 ⇒ 大聲失敗，⛔ 不假裝沒落子、⛔ 不重跑（重跑是另一刀）
                c.Report.Append("## ✗ 結算沒收齊（voxel 已落，帳要對）\n");
                foreach (string s in aShortPaid) c.Report.Append("- ").Append(s).Append('\n');
                c.Report.Append("- 對帳用：ref `").Append(aRef).Append("`\n");
                return Finish(c, 1, "✗ 已落子但結算沒收齊（應收 " + aCharge + "）—— 見回傳檔，帳要人對");
            }

            int aPlaced = int.Parse(ValueOf(c.Result, "placed"), CultureInfo.InvariantCulture);
            c.Report.Append("## next\n- 看成品：`").Append(SCP_CmdRegistry.Invoke("sculpture --arg op=view [--arg region=…]"))
                    .Append("`（免費）\n");
            if (c.Op == "stamp2d" || c.Op == "stampimg")
                c.Report.Append("- 下次貼圖：先 `").Append(SCP_CmdRegistry.Invoke("canvas --arg op=view --arg region=x,y,w,h --arg scale=1"))
                        .Append("` 看預覽 → 把它印的 non_transparent_pixels 當 expect_pixels 帶回來。\n");
            SCP_FreeTimeHint.Append(c.Report, new SCP_DataRoot(c.Roots.DataRoot), c.Persona, out string aHintWarn);
            if (aHintWarn.Length > 0) c.Result.Lines.Add("⚠ " + aHintWarn);

            // ── 分享（best-effort；仍握著雕刻鎖 —— view 讀的是同一份快取）──
            if (aPlaced > 0 && c.Args.Get("share").Trim() != "0")
                TrySharePreview(c, aPlaced, aWhere);

            return Finish(c, 0, "✓ " + c.Op + " " + aPlaced + " voxels，收 " + aCharge + " 單位");
        }
    }

    static string PayOf(Ctx c)
    {
        string aPay = c.Args.Get("pay").Trim().ToLowerInvariant();
        return aPay.Length == 0 ? "auto" : aPay;
    }

    /// <summary>
    /// 照計畫逐通道扣；任何一段失敗記進 <paramref name="ioShort"/>（⛔ 不回滾前面已扣的 —— 那些是真的扣了，要留帳）。
    /// <para>⚠ 限時券與永久券走**同一支 consume**（ledger 內部先花快過期的）—— 分兩筆只為了帳面分得出哪幾張是限時券。</para>
    /// </summary>
    static void Settle(SCP_ICanvasGateway iGate, string iPersona, string iAccount, int iCharge, SCP_CanvasPayPlan iPlan,
                       string iRef, List<string> ioRefs, List<string> ioShort)
    {
        string aDesc = "3D sculpture " + iCharge + " unit(s) by " + iPersona;
        if (iPlan.Expiring > 0)
        {
            SCP_CanvasGateResult r = iGate.ConsumeVouchers(iPersona, iPlan.Expiring, iRef, aDesc + "（限時券）");
            if (r.Ok) ioRefs.Add("voucher-expiring:" + iRef); else ioShort.Add("限時券 " + iPlan.Expiring + "：" + r.Detail);
        }
        if (iPlan.Permanent > 0)
        {
            SCP_CanvasGateResult r = iGate.ConsumeVouchers(iPersona, iPlan.Permanent, iRef, aDesc);
            if (r.Ok) ioRefs.Add("voucher:" + iRef); else ioShort.Add("永久券 " + iPlan.Permanent + "：" + r.Detail);
        }
        if (iPlan.Tavern > 0)
        {
            SCP_CanvasGateResult r = iGate.ConsumeTavernVouchers(iPersona, iPlan.Tavern, iRef, aDesc + "（酒館券）");
            if (r.Ok) ioRefs.Add("voucher-tavern:" + iRef); else ioShort.Add("酒館券 " + iPlan.Tavern + "：" + r.Detail);
        }
        if (iPlan.Token > 0)
        {
            SCP_CanvasGateResult r = iGate.DebitTokens(iAccount, iPlan.Token, SpendKind, iRef, aDesc);
            if (r.Ok) ioRefs.Add("bank:" + iRef); else ioShort.Add("token " + iPlan.Token + "：" + r.Detail);
        }
    }

    /// <summary>組引擎參數＋量最壞體積。⚠ 一律 `--opt=value`：值可能以 `-` 開頭（facing=z-、light_dir=-1,…），空格分隔會被 argparse 當旗標吃掉。</summary>
    static bool TryBuildPlace(Ctx c, out List<string> oArgs, out long oWorst, out string oWhere, out string oBad)
    {
        oArgs = new List<string> { c.Op };
        oWorst = 0; oWhere = ""; oBad = "";
        if (c.Op == "box" || c.Op == "carve")
        {
            if (!TryInt(c, "x1", out int x1) || !TryInt(c, "x2", out int x2) || !TryInt(c, "y1", out int y1)
                || !TryInt(c, "y2", out int y2) || !TryInt(c, "z1", out int z1) || !TryInt(c, "z2", out int z2))
            { oBad = c.Op + " 需要 x1 x2 y1 y2 z1 z2 六個整數（0-255）"; return false; }
            oWorst = ClampedVolume(ref x1, ref x2, ref y1, ref y2, ref z1, ref z2);
            oWhere = $"({x1}..{x2},{y1}..{y2},{z1}..{z2})";
            oArgs.AddRange(new[] { "--x1=" + x1, "--x2=" + x2, "--y1=" + y1, "--y2=" + y2, "--z1=" + z1, "--z2=" + z2,
                                   "--persona=" + c.Persona });
            if (c.Op == "box")
            {
                if (!TryInt(c, "color", out int aColor) || aColor < 0 || aColor > 255) { oBad = "color 要是 0-255 的整數"; return false; }
                oArgs.Add("--color=" + aColor);
            }
            return true;
        }

        string aAt = c.Args.Get("at").Trim();
        if (aAt.Length == 0) { oBad = "缺 --arg at=<x,y,z>（圖左上角要貼在 3D 的哪一點）"; return false; }
        int aThick = TryInt(c, "thickness", out int t) ? Math.Max(1, t) : 1;
        long aArea;
        if (c.Op == "stamp2d")
        {
            if (!TryInt(c, "src_x1", out int sx1) || !TryInt(c, "src_y1", out int sy1)
                || !TryInt(c, "src_x2", out int sx2) || !TryInt(c, "src_y2", out int sy2))
            { oBad = "stamp2d 需要 src_x1 src_y1 src_x2 src_y2 四個整數（2D 畫布座標 0-2047）"; return false; }
            int w = Math.Abs(sx2 - sx1) + 1, h = Math.Abs(sy2 - sy1) + 1;
            aArea = (long)w * h;
            oWhere = $"2D 畫布 ({sx1},{sy1})-({sx2},{sy2}) = {w}x{h} × thickness {aThick} @({aAt})";
            oArgs.AddRange(new[] { "--src-x1=" + sx1, "--src-y1=" + sy1, "--src-x2=" + sx2, "--src-y2=" + sy2 });
        }
        else
        {
            string aPng = c.Args.Get("png").Trim();
            if (aPng.Length == 0 || !File.Exists(aPng)) { oBad = "stampimg 需要存在的 --arg png=<路徑>（got '" + aPng + "'）"; return false; }
            if (!TryReadPngSize(aPng, out int pw, out int ph)) { oBad = "讀不到 PNG 尺寸（非 PNG 或檔案損毀）：" + aPng; return false; }
            string aResize = c.Args.Get("resize").Trim();
            if (aResize.Length > 0)
            {
                if (!TryParseWh(aResize, out int rw, out int rh)) { oBad = "resize 要是 W,H（正整數）：'" + aResize + "'"; return false; }
                pw = rw; ph = rh;
                oArgs.Add("--resize=" + rw + "," + rh);
            }
            aArea = (long)pw * ph;
            oWhere = $"PNG {Path.GetFileName(aPng)} = {pw}x{ph} × thickness {aThick} @({aAt})";
            oArgs.Add("--png=" + aPng);
        }
        oWorst = aArea * aThick;
        oArgs.AddRange(new[] { "--at=" + aAt, "--persona=" + c.Persona, "--thickness=" + aThick });
        AddOpt(c, oArgs, "facing", "--facing=");
        if (TryInt(c, "expect_pixels", out int aExpect)) oArgs.Add("--expect-pixels=" + aExpect);
        if (TryInt(c, "alpha_threshold", out int aAlpha)) oArgs.Add("--alpha-threshold=" + aAlpha);
        if (Truthy(c.Args.Get("overwrite"))) oArgs.Add("--overwrite");
        if (Truthy(c.Args.Get("allow_clip"))) oArgs.Add("--allow-clip");
        if (c.Args.Get("exhibit_id").Trim().Length > 0)
        {
            AddOpt(c, oArgs, "exhibit_id", "--exhibit-id=");
            AddOpt(c, oArgs, "exhibit_title", "--exhibit-title=");
            AddOpt(c, oArgs, "exhibit_desc", "--exhibit-desc=");
            if (TryInt(c, "exhibit_margin", out int aMargin)) oArgs.Add("--exhibit-margin=" + aMargin);
        }
        return true;
    }

    static void AppendPlaceFacts(Ctx c, SCP_JsonData iJson, int iActual, string iWhere)
    {
        if (c.Op == "box" || c.Op == "carve")
        {
            int aSkipped = iJson.GetInt("skipped_count", 0);
            c.Report.Append("- ").Append(c.Op == "box" ? "placed" : "carved").Append(": **").Append(iActual).Append("** @")
                    .Append(iWhere).Append(aSkipped > 0 ? "（skip " + aSkipped + " —— 禁覆蓋，不收費）" : "").Append('\n');
            return;
        }
        int aPainted = iJson.GetInt("painted_source_pixels", 0);
        int aSkip = iJson.GetInt("skipped_occupied", 0);
        int aOob = iJson.GetInt("out_of_bounds", 0);
        int aBlack = iJson.GetInt("remapped_black", 0);
        c.Report.Append("- source: ").Append(iWhere).Append(" → 非透明像素 **").Append(aPainted).Append("**（透明＝未繪製，不放 voxel）\n");
        c.Report.Append("- placed: **").Append(iActual).Append("** voxels")
                .Append(aSkip > 0 ? "；skip " + aSkip + "（禁覆蓋，不收費）" : "")
                .Append(aOob > 0 ? "；越界裁掉 " + aOob : "").Append('\n');
        if (aBlack > 0)
            c.Report.Append("- remapped_black: ").Append(aBlack).Append("（純黑 index 0 在 3D 代表「空」，重映到最近非零暗色 —— 不靜默改色）\n");
        SCP_JsonData aEx = iJson["exhibit"];
        if (aEx.IsObject)
        {
            c.Report.Append("- exhibit: **").Append(aEx.GetString("mode", "") == "created" ? "新建" : "擴充").Append("** `")
                    .Append(aEx.GetString("id", "")).Append("`《").Append(aEx.GetString("title", "")).Append("》 by ")
                    .Append(aEx.GetString("author", "")).Append(" — region `").Append(aEx.GetString("region", "")).Append("`\n");
            string aWarn = aEx.GetString("warning", "");
            if (aWarn.Length > 0) c.Report.Append("- ⚠ ").Append(aWarn).Append('\n');
            string aPhoto = aEx.GetString("photo", "");
            if (aPhoto.Length > 0 && File.Exists(aPhoto)) c.Result.AddOutput(aPhoto);
        }
    }

    // ===========================================================
    // 區塊職責：落子後的自動預覽分享（Tim 2026-08-20 拍板）—— 渲全景 view → 複製成獨立檔名 → `tavern-post` 帶圖。
    // 物理意義：`_last_view.png` 是共用的、下一次 view 就被蓋掉 ⇒ 複製到 `previews/share_*.png`，refs 才不會指著一張會變的圖。
    //          ⭐ 走 `tavern-post`（Senate 原生），⛔ 不走畫布閘的 `Share`（那條還派 Unity 的 Cmd_Tavern）。
    // 數值影響：任何失敗只是一行警告 —— 錢已扣、voxel 已落，分享失敗不能讓主動作看起來失敗。
    //          ⚠ exit 7（不知道有沒有發）照實印出來，⛔ 不補發（同一則發兩次就是付兩次薪水）。
    // ===========================================================
    static void TrySharePreview(Ctx c, int iPlaced, string iWhere)
    {
        try
        {
            SCP_EngineResult aView = RunEngine(c, new List<string> { "view" });
            string aViewPng = Path.Combine(c.SculptDir, "_last_view.png");
            if (aView.Exit != 0 || !File.Exists(aViewPng))
            {
                c.Result.Lines.Add("⚠ 分享預覽渲染失敗（exit " + aView.Exit + "）—— 落子不受影響");
                return;
            }
            string aDir = Path.Combine(c.SculptDir, "previews");
            Directory.CreateDirectory(aDir);
            string aShare = Path.Combine(aDir, "share_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff", CultureInfo.InvariantCulture) + ".png")
                                .Replace('\\', '/');
            File.Copy(aViewPng, aShare, true);

            string aBody = c.Op == "box" || c.Op == "carve"
                ? "🧊 " + c.Persona + " 雕刻落子（" + c.Op + "）：" + iPlaced + " voxels @" + iWhere
                : "🧊 " + c.Persona + " 貼圖入 3D（" + c.Op + "）：" + iPlaced + " voxels — " + iWhere;
            var aArgs = new Dictionary<string, string>
            {
                ["persona"] = c.Persona,
                ["body"] = aBody,
                ["tag"] = "sculpt-share",
                ["refs"] = aShare,
            };
            string aProject = c.Args.Get("project").Trim();
            if (aProject.Length > 0) aArgs["project"] = aProject;
            SCP_CmdResult aPost = SCP_CmdRegistry.Dispatch("tavern-post", aArgs);
            string aSeq = ValueOf(aPost, "post_seq");
            if (aPost.ExitCode == 0)
            {
                c.Result.Lines.Add("📣 已分享到酒館" + (aSeq.Length > 0 ? "（seq " + aSeq + "）" : "") + "，附預覽 " + aShare);
                c.Result.AddValue("share_seq", aSeq);
            }
            else if (aPost.ExitCode == 7)
                c.Result.Lines.Add("⚠ 分享**不知道有沒有發**（tavern-post exit 7）—— ⛔ 別補發，先 `"
                                   + SCP_CmdRegistry.Invoke("tavern-query --arg kind=tail") + "` 回讀");
            else
                c.Result.Lines.Add("⚠ 分享沒發出去（tavern-post exit " + aPost.ExitCode + "："
                                   + (aPost.Lines.Count > 0 ? aPost.Lines[0] : "") + "）—— 落子不受影響");
        }
        catch (Exception e)
        {
            c.Result.Lines.Add("⚠ 分享例外（" + e.GetType().Name + ": " + e.Message + "）—— 落子不受影響");
        }
    }

    // ===========================================================
    // 區塊職責：觀測三 op（view／slice／stats）—— 免費 pass-through（驗收管道要零門檻）。
    // 數值影響：仍拿雕刻鎖 —— 引擎讀快取時可能自我修復重寫它，與落子撞在一起會吃掉一刀。
    // ===========================================================
    static string? OpReadOnly(Ctx c)
    {
        var aArgs = new List<string> { c.Op };
        if (c.Op == "view")
        {
            AddOpt(c, aArgs, "region", "--region=");
            AddOpt(c, aArgs, "exclude_color", "--exclude-color=");
            AddOpt(c, aArgs, "exhibit", "--exhibit=");
            AddOpt(c, aArgs, "light_dir", "--light-dir=");
            AddOpt(c, aArgs, "ambient", "--ambient=");
            AddOpt(c, aArgs, "zoom", "--zoom=");
        }
        else if (c.Op == "slice")
        {
            if (c.Args.Get("region").Trim().Length == 0)
                return Blocked(c, 2, "slice 需要 --arg region=<x1..x2,y1..y2,z1..z2>（法線軸跨度＝厚度）");
            AddOpt(c, aArgs, "region", "--region=");
            AddOpt(c, aArgs, "axis", "--axis=");
            AddOpt(c, aArgs, "out", "--out=");
        }

        SCP_EngineResult aRes;
        try
        {
            using (SCP_FileLock.Acquire(Path.Combine(c.SculptDir, "_engine"), EngineLockTimeoutSec))
                aRes = RunEngine(c, aArgs);
        }
        catch (SCP_FileLockTimeoutException e) { return Blocked(c, 4, "拿不到雕刻鎖：" + e.Message); }

        c.Report.Append("```\n").Append(aRes.Combined()).Append("\n```\n");
        if (c.Op == "view")
        {
            string aPng = Path.Combine(c.SculptDir, "_last_view.png").Replace('\\', '/');
            if (aRes.Exit == 0 && File.Exists(aPng)) c.Result.AddOutput(aPng);
        }
        else if (c.Op == "slice")
        {
            // 引擎把實際落檔路徑印在 output_path（--out 可覆寫預設）—— 讀它，不重推路徑
            string aOut = aRes.Json?.GetString("output_path", "") ?? "";
            if (aRes.Exit == 0 && aOut.Length > 0 && File.Exists(aOut)) c.Result.AddOutput(aOut.Replace('\\', '/'));
        }
        return aRes.Exit == 0
            ? Finish(c, 0, "✓ " + c.Op + " 完成")
            : Finish(c, 1, "✗ 引擎失敗（exit " + aRes.Exit + "）");
    }

    // ───────────────────────────── 引擎 ─────────────────────────────

    sealed class SCP_EngineResult
    {
        public int Exit;
        public string Stdout = "";
        public string Stderr = "";
        /// <summary>stdout 裡第一個頂層 JSON 物件；撈不到 ＝ null（⛔ 不回一個全零的物件 —— 那會讓「引擎沒印 JSON」看起來像「什麼都沒放下」）。</summary>
        public SCP_JsonData? Json;

        public string Combined()
            => (Stdout.Trim() + (Stderr.Trim().Length > 0 ? "\n── stderr ──\n" + Stderr.Trim() : "")).Trim();
    }

    /// <summary>spawn `python sculpt.py …`（參數逐個走 ArgumentList，不經 shell）。逾時 ⇒ kill 並回 exit -1。</summary>
    static SCP_EngineResult RunEngine(Ctx c, List<string> iArgs)
    {
        var aRes = new SCP_EngineResult();
        var aPsi = new ProcessStartInfo
        {
            FileName = "python", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = c.Roots.ProjectRoot,
        };
        aPsi.ArgumentList.Add(c.Script);
        foreach (string a in iArgs) aPsi.ArgumentList.Add(a);
        aPsi.Environment["PYTHONIOENCODING"] = "utf-8";
        try
        {
            using Process? aProc = Process.Start(aPsi);
            if (aProc == null) { aRes.Exit = -1; aRes.Stderr = "Process.Start 回 null"; return aRes; }
            var aOut = aProc.StandardOutput.ReadToEndAsync();
            var aErr = aProc.StandardError.ReadToEndAsync();
            if (!aProc.WaitForExit(EngineTimeoutMs))
            {
                try { aProc.Kill(true); } catch { }
                aRes.Exit = -1;
                aRes.Stderr = "sculpt.py 超過 " + (EngineTimeoutMs / 1000) + " 秒，已終止";
                return aRes;
            }
            aRes.Exit = aProc.ExitCode;
            aRes.Stdout = aOut.Result;
            aRes.Stderr = aErr.Result;
        }
        catch (Exception e)
        {
            aRes.Exit = -1;
            aRes.Stderr = "叫不起 python（" + e.GetType().Name + ": " + e.Message + "）";
            return aRes;
        }
        aRes.Json = ParseEngineJson(aRes.Stdout);
        return aRes;
    }

    public static SCP_JsonData? ParseEngineJson(string iStdout)
    {
        if (string.IsNullOrEmpty(iStdout)) return null;
        int aStart = iStdout.IndexOf('{');
        int aEnd = iStdout.LastIndexOf('}');
        if (aStart < 0 || aEnd <= aStart) return null;
        try
        {
            SCP_JsonData aJd = SCP_JsonData.Parse(iStdout.Substring(aStart, aEnd - aStart + 1));
            return aJd.IsObject ? aJd : null;
        }
        catch (Exception) { return null; }
    }

    // ───────────────────────────── 回傳 ─────────────────────────────

    static string? Blocked(Ctx c, int iExit, string iReason)
    {
        c.Report.Append("## blocked\n- reason: ").Append(iReason).Append('\n');
        return Finish(c, iExit, "✗ blocked：" + iReason.Split('\n')[0]);
    }

    /// <summary>落回傳檔（有 persona 才落）並設定 exit。沒有 persona ⇒ 報告印在輸出。</summary>
    static string? Finish(Ctx c, int iExit, string iHeadline)
    {
        c.Result.ExitCode = iExit;
        c.Result.Lines.Add(iHeadline);
        if (c.Persona.Length == 0) { c.Result.Lines.Add(c.Report.ToString().TrimEnd()); return null; }
        string aPath = SCP_LettersPaths.CmdPayload(c.Roots.Letters, c.Persona, "sculpture", c.Op);
        try { SCP_CmdPayload.Write(aPath, c.Report.ToString()); return aPath; }
        catch (Exception e)
        {
            c.Result.Lines.Add("⚠ 回傳檔寫不進去（報告附在下面）：" + e.Message);
            c.Result.Lines.Add(c.Report.ToString().TrimEnd());
            return null;
        }
    }

    static void Fail(SCP_CmdResult ioResult, int iExit, string iLine)
    {
        ioResult.ExitCode = iExit;
        ioResult.Lines.Add(iLine);
    }

    // ───────────────────────────── 小工具 ─────────────────────────────

    static void AddOpt(Ctx c, List<string> ioArgs, string iKey, string iFlag)
    {
        string v = c.Args.Get(iKey).Trim();
        if (v.Length > 0) ioArgs.Add(iFlag + v);
    }

    static bool TryInt(Ctx c, string iKey, out int oVal)
        => int.TryParse(c.Args.Get(iKey).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oVal);

    static bool Truthy(string iVal)
    {
        string v = (iVal ?? "").Trim().ToLowerInvariant();
        return v == "1" || v == "true" || v == "yes";
    }

    static string ValueOf(SCP_CmdResult iResult, string iKey)
    {
        foreach (KeyValuePair<string, string> kv in iResult.Values)
            if (string.Equals(kv.Key, iKey, StringComparison.Ordinal)) return kv.Value;
        return "";
    }

    public static int CeilDiv(long a, int b) => (int)((a + b - 1) / b);

    /// <summary>兩角任意順序、clamp 0..255（與 sculpt.py 同語意）。回傳 clamp 後體積。</summary>
    public static int ClampedVolume(ref int x1, ref int x2, ref int y1, ref int y2, ref int z1, ref int z2)
    {
        static void Norm(ref int a, ref int b) { if (a > b) (a, b) = (b, a); a = Math.Max(0, a); b = Math.Min(255, b); }
        Norm(ref x1, ref x2); Norm(ref y1, ref y2); Norm(ref z1, ref z2);
        if (x2 < x1 || y2 < y1 || z2 < z1) return 0;
        return (x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
    }

    /// <summary>
    /// 讀 PNG 寬高（不解碼本體）：8 bytes 簽章 ＋ IHDR 後 big-endian 的寬、高。
    /// 這個尺寸是預授權的分母 —— 由本檔自己量，不接受呼叫端申報。失敗回 false，⛔ 不猜預設值。
    /// </summary>
    public static bool TryReadPngSize(string iPath, out int oWidth, out int oHeight)
    {
        oWidth = oHeight = 0;
        try
        {
            var aHead = new byte[24];
            using (FileStream aFs = File.OpenRead(iPath))
            {
                int aRead = 0;
                while (aRead < aHead.Length)
                {
                    int n = aFs.Read(aHead, aRead, aHead.Length - aRead);
                    if (n <= 0) return false;
                    aRead += n;
                }
            }
            if (aHead[0] != 0x89 || aHead[1] != 0x50 || aHead[2] != 0x4E || aHead[3] != 0x47) return false;
            if (aHead[12] != 'I' || aHead[13] != 'H' || aHead[14] != 'D' || aHead[15] != 'R') return false;
            oWidth = (aHead[16] << 24) | (aHead[17] << 16) | (aHead[18] << 8) | aHead[19];
            oHeight = (aHead[20] << 24) | (aHead[21] << 16) | (aHead[22] << 8) | aHead[23];
            return oWidth > 0 && oHeight > 0;
        }
        catch (Exception) { return false; }
    }

    static bool TryParseWh(string iVal, out int oW, out int oH)
    {
        oW = oH = 0;
        string[] p = iVal.Split(',');
        return p.Length == 2
               && int.TryParse(p[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oW)
               && int.TryParse(p[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oH)
               && oW > 0 && oH > 0;
    }
}
