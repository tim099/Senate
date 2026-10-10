// 區塊職責：作品建立付款、作者權限、免費雕刻與展區副本匯入（TASK-0460）。
// 物理意義：沿用既有銀行／券 gateway；pending 保留原付款計畫，ready 才可雕。
// 鎖順序：建立 registry→付款；匯入作品→展區→付款；觀測與免費雕刻只拿作品鎖。
using System.Globalization;
using SCP.Core.Bank;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Sculpture;

namespace Senate.Core;

public sealed partial class Cmd_Sculpture
{
    static bool ConfigureWork(Ctx c, out string error)
    {
        error = "";
        string id = c.Args.Get("work").Trim();
        if (id.Length == 0) return true;
        try
        {
            if (c.Op == "render-profile") throw new ArgumentException("render-profile 層仍以 shared/persona 管理，不接受 work");
            c.Work = c.Works.Load(id);
            if (c.Op == "exhibit" && c.Args.Get("sub").Trim() == "register" && c.Persona != c.Work.owner)
                throw new ArgumentException("只有作品作者可以登錄作品空間內的展品設定");
            c.Result.AddValue("work", c.Work.id);
            c.Result.AddValue("owner", c.Work.owner);
            c.Result.AddValue("space_size", c.Work.Dimensions);
            return true;
        }
        catch (Exception e) { error = e.Message; return false; }
    }

    static string? OpWork(Ctx c)
    {
        try
        {
            string sub = c.Args.Get("sub").Trim();
            if (sub == "list")
            {
                var cards = c.Works.List(c.Persona);
                foreach (var card in cards) c.Report.Append("- ").Append(card.id).Append("｜").Append(card.title).Append("｜by ").Append(card.owner).Append("｜").Append(card.status).Append("｜").Append(card.Dimensions).Append('\n');
                c.Result.AddValue("count", cards.Count.ToString(CultureInfo.InvariantCulture));
                return Finish(c, 0, "✓ 作品 " + cards.Count + " 件");
            }
            string id = c.Args.Get("work").Trim();
            if (id.Length == 0) id = c.Args.Get("id").Trim();
            id = SCP_SculptWorks.NormalizeId(id);
            if (sub == "create") return CreateWork(c, id);
            if (sub == "assemble") return AssembleWork(c, id);
            if (sub != "show" && sub != "update" && sub != "import" && sub != "move" && sub != "undo" && sub != "redo" && sub != "history") return Blocked(c, 2, "work sub=create|list|show|update|import|assemble|move|undo|redo|history");
            c.Works.Load(id); // 先確認存在，不能為不存在的ID建立鎖目錄而佔掉它。
            using var workLock = SCP_FileLock.Acquire(c.Works.EngineLock(id), EngineLockTimeoutSec);
            var cardNow = c.Works.Load(id);
            c.Result.AddValue("work", id);
            c.Result.AddValue("owner", cardNow.owner);
            c.Result.AddValue("space_size", cardNow.Dimensions);
            if (sub == "show")
            {
                var space = c.Works.Engine(cardNow, c.Roots.DataRoot).LoadSpace();
                c.Report.Append(SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(cardNow), SCP_JsonStyle.UclLegacy))
                    .Append("\n\n## 心得與續作\n").Append(c.Works.ReadText(id, false)).Append("\n\n## TODO\n").Append(c.Works.ReadText(id, true));
                AppendCredits(c, id);
                c.Result.AddValue("revision", SCP_SculptWorks.Revision(space, cardNow.Dimensions));
                c.Result.AddValue("total_voxels", space.Voxels.Count.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("path", c.Works.Folder(id));
                WorkNextSteps(c, id);
                return Finish(c, 0, "✓ " + cardNow.title + "｜" + cardNow.Dimensions + "｜by " + cardNow.owner);
            }
            if (sub == "history") return WorkHistory(c, id);
            if (sub == "import")
            {
                if (c.Persona.Length == 0 || !Directory.Exists(SCP_LettersPaths.ProfileDir(c.Letters, c.Persona))) return Blocked(c, 2, "匯入展區需要已存在的persona支付落地費");
                return ImportWork(c, cardNow); // 允許他人作品副本；作者Credit保留，費用由匯入者支付。
            }
            if (c.Persona.Length == 0 || c.Persona != cardNow.owner) return Blocked(c, 2, "只有作品作者 " + cardNow.owner + " 可以修改原作");
            if (sub == "move" || sub == "undo" || sub == "redo") return EditWork(c, cardNow, sub);
            string title = c.Args.Get("title");
            if (c.Args.IsExplicit("title") && string.IsNullOrWhiteSpace(title)) return Blocked(c, 2, "作品名稱不可空白");
            if (c.Args.IsExplicit("size"))
            {
                int maxAxis = WorkMaxAxis(c);
                var dimensions = ParseWorkSize(c.Args.Get("size"), maxAxis, true);
                // 政策上限只擋「往上長」：維持原尺寸（GUI 存筆記時一定帶著 size）或縮小永遠放行
                string? sizeWhy = SCP_SculptWorks.PolicyViolation(dimensions, new[] { cardNow.SizeX, cardNow.SizeY, cardNow.SizeZ }, maxAxis);
                if (sizeWhy != null) return Blocked(c, 2, sizeWhy + "；未修改");
                var space = c.Works.Engine(cardNow, c.Roots.DataRoot).LoadSpace();
                foreach (var voxel in space.Voxels.Entries())
                    if (voxel.X >= dimensions[0] || voxel.Y >= dimensions[1] || voxel.Z >= dimensions[2])
                        return Blocked(c, 2, "縮小會切掉現有voxel；未修改。請先挖除範圍外內容，或選擇較大尺寸");
                SCP_SculptWorks.SetSize(cardNow, dimensions);
            }
            if (c.Args.IsExplicit("title"))
            {
                cardNow.title = title.Trim();
            }
            if (c.Args.IsExplicit("meters_per_voxel"))
            {
                if (!TryPositive(c.Args.Get("meters_per_voxel"), out double mpv) || mpv > 1000) return Blocked(c, 2, "meters_per_voxel 要是 0–1000 的正數（一格幾公尺；0.1 ＝ 每格 10 公分）");
                cardNow.meters_per_voxel = mpv;
            }
            if (c.Args.IsExplicit("notes")) c.Works.WriteText(id, false, c.Args.Get("notes"));
            if (c.Args.IsExplicit("todo")) c.Works.WriteText(id, true, c.Args.Get("todo"));
            c.Works.Save(cardNow);
            c.Result.AddValue("space_size", cardNow.Dimensions);
            return Finish(c, 0, "✓ 作品尺寸與筆記已保存（免費）｜" + cardNow.Dimensions);
        }
        catch (SCP_FileLockTimeoutException e) { return Blocked(c, 4, "作品鎖：" + e.Message); }
        catch (ArgumentException e) { return Blocked(c, 2, e.Message); }
        catch (InvalidOperationException e) { return Blocked(c, 2, e.Message); }
    }

    /// <summary>
    /// 建立／調尺寸時的每軸上限：設定 `sculpture.workMaxAxis`（TASK-0479，預設 4096）。上限與來源都印進回傳值 ——
    /// 被擋下時才分得出「我給太大」與「這台的設定比較小」。⚠ 只管建立與調尺寸；雕刻、渲染只認結構上限。
    /// </summary>
    static int WorkMaxAxis(Ctx c)
    {
        int aMax = SculptureWorkPrefs.ResolveMaxAxis(c.Args.Get("repo_root").Trim(), out string aSource);
        c.Result.AddValue("work_max_axis", aMax.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("work_max_axis_source", aSource);
        return aMax;
    }

    /// <summary>
    /// 建立／調尺寸的 size 解析：這裡只做結構檢查（政策上限交給 PolicyViolation），但**錯字講政策上限** ——
    /// 結構上限 1048576 是呼叫端用不到的數字，照它重試只會換來另一句範圍不同的錯（第二輪審查）。
    /// </summary>
    static int[] ParseWorkSize(string iText, int iMaxAxis, bool iUpdate)
    {
        try { return SCP_SculptWorks.ParseSize(iText, SCP_SculptWorks.MaxAxisHard); }
        catch (ArgumentException)
        {
            throw new ArgumentException("size 要是邊長或 X,Y,Z 的正整數，各軸1–" + iMaxAxis.ToString(CultureInfo.InvariantCulture)
                + "（作品尺寸上限 sculpture.workMaxAxis" + (iUpdate ? "；縮小或維持原尺寸不受上限限制" : "") + "）");
        }
    }

    static string ResolveWorkAccount(Ctx c)
    {
        string account = c.Args.Get("account").Trim();
        return account.Length > 0 ? account : SCP_BankAccountResolver.ResolvePersonaAccount(c.Roots.LettersRoot, c.Roots.DataRoot, c.Roots.Region, c.Persona, out _);
    }

    static string? CreateWork(Ctx c, string id)
    {
        if (c.Persona.Length == 0 || !Directory.Exists(SCP_LettersPaths.ProfileDir(c.Letters, c.Persona)))
            return Blocked(c, 2, "建立作品需要已存在的 persona");
        string commission = c.Args.Get("commission").Trim(), commissionRef = c.Args.Get("commission_ref").Trim();
        if ((commission.Length == 0) != (commissionRef.Length == 0)) return Blocked(c, 2, "委託內容與commission_ref要一起給；自發作品兩者留空");
        // 先只做結構檢查；政策上限只套在**新**作品上 —— 重試 pending 的付款不能被之後調小的上限卡住（錢已經付了）。
        // 沒給 size ⇒ 預設 64，但不超過上限（否則一個沒帶 size 的人會收到一句講 size 的錯）。
        int maxAxis = WorkMaxAxis(c);
        int[] dimensions = ParseWorkSize(c.Args.IsExplicit("size") ? c.Args.Get("size")
            : Math.Min(SCP_SculptWorks.Size, maxAxis).ToString(CultureInfo.InvariantCulture), maxAxis, false);
        using var registryLock = SCP_FileLock.Acquire(c.Works.RegistryLock, EngineLockTimeoutSec);
        if ((c.Args.IsExplicit("parent_work") || !c.Works.Exists(id)) && SCP_SculptWorks.PolicyViolation(dimensions, null, maxAxis) is string createWhy)
            return Blocked(c, 2, createWhy + "；未建立、未扣費");
        if (c.Args.IsExplicit("parent_work")) return CreateChildWork(c, id, dimensions, commission);
        SCP_SculptWork? card = null;
        if (c.Works.Exists(id))
        {
            card = c.Works.Load(id, false);
            if (card.status == "ready" || card.owner != c.Persona) return Blocked(c, 2, "作品 ID 已存在：" + id);
            if (c.Args.IsExplicit("size") && card.Dimensions != string.Join(",", dimensions)) return Blocked(c, 2, "重試建立不可改尺寸；完成後用work update調整");
        }
        string account = card?.account ?? ResolveWorkAccount(c);
        if (card?.commission.Length > 0 || (card == null && commission.Length > 0))
            return CreateCommission(c, id, card, account, commission, commissionRef, dimensions);
        if (commission.Length > 0) return Blocked(c, 2, "不能將付費作品改成委託");
        var gate = SCP_CanvasGatewayHost.For(c.Roots.DataRoot);
        if (gate == null) return Blocked(c, 1, "沒有付款閘，不能建立作品");
        using var payLock = SCP_CanvasPlace.TryAcquireLock(new SCP_CanvasPaths(c.Data), account.Length > 0 ? account : "noaccount", c.Persona, out string lockError);
        if (payLock == null) return Blocked(c, 4, lockError);
        if (card == null)
        {
            string pay = PayOf(c);
            if (!SCP_CanvasPlace.TryPlan(gate, c.Persona, account, SCP_SculptWorks.CreationFee, pay, out var plan, out string why))
                return Blocked(c, 3, "建立費 10 單位付款預驗沒過；未建立、未扣費：" + why);
            card = new SCP_SculptWork { schema = 1, id = id, owner = c.Persona, title = c.Args.Get("title").Trim(), size = SCP_SculptWorks.Size,
                created_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), status = "pending", payment_ref = "sculpture-work:" + Guid.NewGuid().ToString("N"),
                account = account, pay = pay, freetime = plan.Expiring, voucher = plan.Permanent, tavern = plan.Tavern, token = plan.Token };
            if (card.title.Length == 0) return Blocked(c, 2, "建立作品需要 title");
            SCP_SculptWorks.SetSize(card, dimensions);
            c.Works.Save(card); // 付款前先保留唯一 ref；中途停止可用同 ID 繼續。
        }
        var refs = new List<string>();
        var failures = new List<string>();
        SettleWork(gate, card.owner, card.account, new SCP_CanvasPayPlan(card.freetime, card.voucher, card.tavern, card.token), card.payment_ref, "建立作品 " + id, refs, failures);
        c.Result.AddValue("payment_ref", card.payment_ref);
        if (failures.Count > 0)
        {
            c.Report.Append(string.Join("\n", failures));
            return Finish(c, 1, "✗ 建立付款尚未完成；不可雕刻。用同一 persona、ID 重試 create，沿用原付款 ref 對帳");
        }
        card.status = "ready";
        c.Works.Save(card);
        c.Works.SpacePaths(id).EnsureBase();
        c.Result.AddValue("work", id);
        c.Result.AddValue("charged", "10");
        c.Result.AddValue("path", c.Works.Folder(id));
        c.Report.Append("- 建立費：10；後續雕刻免費\n- 付款：").Append(string.Join(", ", refs)).Append('\n');
        WorkNextSteps(c, id);
        c.Result.AddValue("space_size", card.Dimensions);
        return Finish(c, 0, "✓ 建立作品 " + id + "｜" + card.title + "｜" + card.Dimensions + "｜收費 10");
    }

    static string? CreateCommission(Ctx c, string id, SCP_SculptWork? card, string account, string request, string source, int[] dimensions)
    {
        if (card == null)
        {
            if (account.Length == 0) return Blocked(c, 2, "委託需要可解析的收款帳戶");
            if (c.Works.List().Exists(w => w.commission_ref == source)) return Blocked(c, 2, "這個委託來源已建立作品；不可換ID重領");
            string title = c.Args.Get("title").Trim();
            if (title.Length == 0) return Blocked(c, 2, "建立作品需要title");
            card = new SCP_SculptWork { schema = 1, id = id, owner = c.Persona, title = title, size = SCP_SculptWorks.Size,
                created_at = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture), status = "pending", account = account,
                payment_ref = "sculpture-commission:" + Guid.NewGuid().ToString("N"), commission = request, commission_ref = source, reward = 10 };
            SCP_SculptWorks.SetSize(card, dimensions);
            c.Works.Save(card);
        }
        else if (request.Length > 0 && (request != card.commission || source != card.commission_ref))
            return Blocked(c, 2, "重試不可改委託內容或来源；可省略兩者，沿用原記錄");
        c.Result.AddValue("payment_ref", card.payment_ref);
        var credit = SenateSculptureRewards.Factory(c.Data).Credit(card.account, card.owner, card.payment_ref, "委託作品 " + id + "｜" + card.commission_ref + "｜" + card.commission);
        if (!credit.Ok)
        {
            c.Report.Append(credit.Detail);
            return Finish(c, 1, "✗ 委託報酬回執未完成；作品pending。用相同ID重試create，不重領");
        }
        card.status = "ready";
        c.Works.Save(card);
        c.Works.SpacePaths(id).EnsureBase();
        c.Result.AddValue("work", id); c.Result.AddValue("charged", "0"); c.Result.AddValue("reward", "10");
        c.Result.AddValue("space_size", card.Dimensions);
        c.Result.AddValue("account", card.account); c.Result.AddValue("commission_ref", card.commission_ref); c.Result.AddValue("path", c.Works.Folder(id));
        c.Report.Append("- 使用者委託：").Append(card.commission).Append("\n- 來源：").Append(card.commission_ref).Append("\n- 建立免費；報酬10 token已入帳；後續雕刻免費\n");
        WorkNextSteps(c, id);
        return Finish(c, 0, "✓ 委託作品 " + id + "｜建立免費，已發10 token");
    }

    static void WorkNextSteps(Ctx c, string id)
    {
        c.Report.Append("\n## 下一步\n先看 `senate cmd help sculpture` 的座標與操作參數。\n")
            .Append("每次雕刻帶 `--arg work=").Append(id).Append(" --arg persona=").Append(c.Persona)
            .Append("`；box/carve/stamp免費，各軸從0到該軸尺寸減1。\n觀測：op=view；續作：op=work sub=update，用size=邊長或X,Y,Z免費調整尺寸，用--arg-file notes=與todo=保存。\n")
            .Append("零件：op=work sub=assemble，source_work=來源ID、at=x,y,z，可用他人作品並自動Credit；匯入是一般voxel副本，不連動原作。\n")
            .Append("移動：sub=move、region=選區、delta=平移量；撤銷/重做：sub=undo/redo；歷史：sub=history。任務子作品：sub=create、parent_work=父作品，免費不重領薪。\n")
            .Append("建築家具比例：1公尺=32 voxel；2公尺床長64格。\n")
            .Append("展區匯入：op=work sub=import先預覽，按實際落地收費；委託不免匯入費。\n");
    }

    // 同一券 ledger 的限時／永久券合成一筆，避免相同 ref 被第二段判成重複。
    static void SettleWork(SCP_ICanvasGateway gate, string persona, string account, SCP_CanvasPayPlan plan, string reference, string description, List<string> refs, List<string> failures)
    {
        void Take(int amount, string channel, Func<SCP_CanvasGateResult> run)
        {
            if (amount == 0) return;
            var result = run();
            if (result.Ok) refs.Add(channel + ":" + reference); else failures.Add(channel + " " + amount + "：" + result.Detail);
        }
        int vouchers = plan.Expiring + plan.Permanent;
        Take(vouchers, "繪圖券", () => gate.ConsumeVouchers(persona, vouchers, reference, description));
        Take(plan.Tavern, "酒館券", () => gate.ConsumeTavernVouchers(persona, plan.Tavern, reference, description));
        Take(plan.Token, "token", () => gate.DebitTokens(account, plan.Token, SpendKind, reference, description));
    }

    static string? OpPlaceWork(Ctx c)
    {
        var work = c.Work!;
        if (c.Persona.Length == 0 || c.Persona != work.owner) return Blocked(c, 2, "只有作品作者 " + work.owner + " 可以雕刻");
        if (!TryBuildPlace(c, out var run, out long worst, out string where, out string error)) return Blocked(c, 2, error);
        if (worst <= 0 || worst > MaxVolume) return Blocked(c, 2, "體積不合法或超出單刀上限");
        try
        {
            using var workLock = SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec);
            c.Work = c.Works.Load(work.id); // 在鎖內重驗ready與尺寸，防止等待鎖時作品縮小。
            if (!TryBuildPlace(c, out run, out worst, out where, out error)) return Blocked(c, 2, error);
            var result = run!(NewEngine(c));
            c.Report.Append(result.Render());
            if (!result.Ok || result.Status != "success") return Finish(c, 5, "✗ 作品未落子（免費）");
            c.Result.AddValue("charged", "0");
            c.Result.AddValue("placed", PlacedOf(result).ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("event_file", EventFileOf(result));
            c.Result.AddValue("revision", SCP_SculptWorks.Revision(NewEngine(c).LoadSpace(), c.Work.Dimensions));
            return Finish(c, 0, "✓ 作品 " + work.id + " " + c.Op + " " + PlacedOf(result) + " voxels，免費");
        }
        catch (SCP_FileLockTimeoutException e) { return Blocked(c, 4, e.Message); }
    }

    // 進來時已持有來源作品鎖；之後拿展區鎖與付款鎖，預覽與提交共享同一個 voxel 計畫。
    static string? ImportWork(Ctx c, SCP_SculptWork card)
    {
        string[] atText = c.Args.Get("at").Split(',');
        var at = new int[3];
        if (atText.Length != 3) return Blocked(c, 2, "匯入需要 at=x,y,z（作品原點的展區座標）");
        for (int i = 0; i < 3; i++) if (!int.TryParse(atText[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out at[i])) return Blocked(c, 2, "at 需要三個整數");
        var source = c.Works.Engine(card, c.Roots.DataRoot).LoadSpace();
        var target = NewEngine(c); // work 管理 op 的 Ctx 不切換空間，這裡就是共用展區。
        using var exhibitLock = SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec);
        var plan = target.PreviewWorkImport(source, card.id, card.owner, at);
        plan.Revision = SCP_SculptWorks.Revision(source, card.Dimensions);
        plan.Credits.Add(new SCP_SculptCredit { work = card.id, title = card.title, author = card.owner, revision = plan.Revision });
        plan.Credits.AddRange(SCP_SculptHistory.Read(c.Works.SpacePaths(card.id)).Credits());
        int charge = CeilDiv(plan.Placed.Count, VoxelsPerUnit);
        c.Result.AddValue("revision", plan.Revision);
        c.Result.AddValue("would_place", plan.Placed.Count.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("skipped_occupied", plan.Occupied.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("out_of_bounds", plan.OutOfBounds.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("estimated_charge", charge.ToString(CultureInfo.InvariantCulture));
        c.Report.Append("- 版本：").Append(plan.Revision).Append("\n- 落地：").Append(plan.Placed.Count).Append("；跳過已佔用：").Append(plan.Occupied).Append("；越界：").Append(plan.OutOfBounds).Append("；費用：").Append(charge).Append('\n');
        if (plan.OutOfBounds != 0 || plan.Placed.Count == 0) return Finish(c, 5, "✗ 匯入越界或沒有可落地內容；未匯入、未扣費");
        string exhibitId = c.Args.Get("exhibit_id").Trim();
        if (exhibitId.Length > 0)
        {
            exhibitId = SCP_SculptWorks.NormalizeId(exhibitId);
            if (File.Exists(target.Paths.ExhibitJson(exhibitId))) return Blocked(c, 2, "展品 ID 已存在；匯入不覆寫或擴充舊展品");
        }
        if (c.Args.Get("confirm").Trim() != "1") return Finish(c, 0, "✓ 匯入預覽；尚未扣費。提交需 confirm=1、expect_revision、expect_placed 與新 exhibit_id");
        if (exhibitId.Length == 0) return Blocked(c, 2, "提交需要新的 exhibit_id");
        if (c.Args.Get("expect_revision").Trim() != plan.Revision || !TryInt(c, "expect_placed", out int expected) || expected != plan.Placed.Count)
            return Blocked(c, 5, "來源版本或展區落地數已變動／沒有提供預覽讀數；重新預覽，未落地、未扣費");
        var gate = SCP_CanvasGatewayHost.For(c.Roots.DataRoot);
        if (gate == null) return Blocked(c, 1, "沒有付款閘，不能匯入");
        string account = ResolveWorkAccount(c);
        using var payLock = SCP_CanvasPlace.TryAcquireLock(new SCP_CanvasPaths(c.Data), account.Length > 0 ? account : "noaccount", c.Persona, out string why);
        if (payLock == null) return Blocked(c, 4, why);
        if (!SCP_CanvasPlace.TryPlan(gate, c.Persona, account, charge, PayOf(c), out var payment, out why)) return Blocked(c, 3, "匯入付款預驗沒過；未落地、未扣費：" + why);
        var result = target.ImportWork(plan, c.Persona, exhibitId, card.title);
        var refs = new List<string>(); var failures = new List<string>();
        SettleWork(gate, c.Persona, account, payment, "sculpture:" + Path.GetFileName(result.EventFile), "匯入作品 " + card.id, refs, failures);
        c.Result.AddValue("placed", result.PlacedCount.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("charged", charge.ToString(CultureInfo.InvariantCulture));
        c.Result.AddValue("event_file", result.EventFile);
        c.Result.AddValue("exhibit", exhibitId);
        c.Report.Append(result.Render()).Append("\n- 付款：").Append(string.Join(", ", refs));
        if (failures.Count > 0)
        {
            c.Report.Append("\n").Append(string.Join("\n", failures));
            return Finish(c, 1, "✗ 已匯入但結算未收齊；勿重跑，按 event_file 交易 ref 對帳");
        }
        return Finish(c, 0, "✓ 作品副本匯入展品 " + exhibitId + "；收 " + charge + " 單位；原稿保留");
    }
}
