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
            c.Result.AddValue("space_size", c.Size.ToString(CultureInfo.InvariantCulture));
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
                foreach (var card in cards) c.Report.Append("- ").Append(card.id).Append("｜").Append(card.title).Append("｜by ").Append(card.owner).Append("｜").Append(card.status).Append("｜64³\n");
                c.Result.AddValue("count", cards.Count.ToString(CultureInfo.InvariantCulture));
                return Finish(c, 0, "✓ 作品 " + cards.Count + " 件");
            }
            string id = c.Args.Get("work").Trim();
            if (id.Length == 0) id = c.Args.Get("id").Trim();
            id = SCP_SculptWorks.NormalizeId(id);
            if (sub == "create") return CreateWork(c, id);
            if (sub != "show" && sub != "update" && sub != "import") return Blocked(c, 2, "work sub=create|list|show|update|import");
            c.Works.Load(id); // 先確認存在，不能為不存在的ID建立鎖目錄而佔掉它。
            using var workLock = SCP_FileLock.Acquire(c.Works.EngineLock(id), EngineLockTimeoutSec);
            var cardNow = c.Works.Load(id);
            c.Result.AddValue("work", id);
            c.Result.AddValue("owner", cardNow.owner);
            if (sub == "show")
            {
                var space = new SCP_SculptEngine(c.Works.SpacePaths(id), c.Roots.DataRoot, SCP_SculptWorks.Size).LoadSpace();
                c.Report.Append(SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(cardNow), SCP_JsonStyle.UclLegacy))
                    .Append("\n\n## 心得與續作\n").Append(c.Works.ReadText(id, false)).Append("\n\n## TODO\n").Append(c.Works.ReadText(id, true));
                c.Result.AddValue("revision", SCP_SculptWorks.Revision(space));
                c.Result.AddValue("total_voxels", space.Voxels.Count.ToString(CultureInfo.InvariantCulture));
                c.Result.AddValue("path", c.Works.Folder(id));
                WorkNextSteps(c, id);
                return Finish(c, 0, "✓ " + cardNow.title + "｜64³｜by " + cardNow.owner);
            }
            if (c.Persona.Length == 0 || c.Persona != cardNow.owner) return Blocked(c, 2, "只有作品作者 " + cardNow.owner + " 可以修改或匯入");
            if (sub == "import") return ImportWork(c, cardNow);
            string title = c.Args.Get("title");
            if (c.Args.IsExplicit("title"))
            {
                if (string.IsNullOrWhiteSpace(title)) return Blocked(c, 2, "作品名稱不可空白");
                cardNow.title = title.Trim();
                c.Works.Save(cardNow);
            }
            if (c.Args.IsExplicit("notes")) c.Works.WriteText(id, false, c.Args.Get("notes"));
            if (c.Args.IsExplicit("todo")) c.Works.WriteText(id, true, c.Args.Get("todo"));
            return Finish(c, 0, "✓ 作品筆記已保存（免費）");
        }
        catch (SCP_FileLockTimeoutException e) { return Blocked(c, 4, "作品鎖：" + e.Message); }
        catch (ArgumentException e) { return Blocked(c, 2, e.Message); }
        catch (InvalidOperationException e) { return Blocked(c, 2, e.Message); }
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
        using var registryLock = SCP_FileLock.Acquire(c.Works.RegistryLock, EngineLockTimeoutSec);
        SCP_SculptWork? card = null;
        if (c.Works.Exists(id))
        {
            card = c.Works.Load(id, false);
            if (card.status == "ready" || card.owner != c.Persona) return Blocked(c, 2, "作品 ID 已存在：" + id);
        }
        string account = card?.account ?? ResolveWorkAccount(c);
        if (card?.commission.Length > 0 || (card == null && commission.Length > 0))
            return CreateCommission(c, id, card, account, commission, commissionRef);
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
        return Finish(c, 0, "✓ 建立作品 " + id + "｜" + card.title + "｜64³｜收費 10");
    }

    static string? CreateCommission(Ctx c, string id, SCP_SculptWork? card, string account, string request, string source)
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
        c.Result.AddValue("account", card.account); c.Result.AddValue("commission_ref", card.commission_ref); c.Result.AddValue("path", c.Works.Folder(id));
        c.Report.Append("- 使用者委託：").Append(card.commission).Append("\n- 來源：").Append(card.commission_ref).Append("\n- 建立免費；報酬10 token已入帳；後續雕刻免費\n");
        WorkNextSteps(c, id);
        return Finish(c, 0, "✓ 委託作品 " + id + "｜建立免費，已發10 token");
    }

    static void WorkNextSteps(Ctx c, string id)
    {
        c.Report.Append("\n## 下一步\n先看 `senate cmd help sculpture` 的座標與操作參數。\n")
            .Append("每次雕刻帶 `--arg work=").Append(id).Append(" --arg persona=").Append(c.Persona)
            .Append("`；box/carve/stamp免費，0..63。\n觀測：op=view；續作：op=work sub=update，用--arg-file notes=與todo=保存。\n")
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
            c.Works.Load(work.id); // 在鎖內重驗 ready。
            var result = run!(NewEngine(c));
            c.Report.Append(result.Render());
            if (!result.Ok || result.Status != "success") return Finish(c, 5, "✗ 作品未落子（免費）");
            c.Result.AddValue("charged", "0");
            c.Result.AddValue("placed", PlacedOf(result).ToString(CultureInfo.InvariantCulture));
            c.Result.AddValue("event_file", EventFileOf(result));
            c.Result.AddValue("revision", SCP_SculptWorks.Revision(NewEngine(c).LoadSpace()));
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
        var source = new SCP_SculptEngine(c.Works.SpacePaths(card.id), c.Roots.DataRoot, SCP_SculptWorks.Size).LoadSpace();
        var target = NewEngine(c); // work 管理 op 的 Ctx 不切換空間，這裡就是共用展區。
        using var exhibitLock = SCP_FileLock.Acquire(c.EngineLockTarget, EngineLockTimeoutSec);
        var plan = target.PreviewWorkImport(source, card.id, card.owner, at);
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
