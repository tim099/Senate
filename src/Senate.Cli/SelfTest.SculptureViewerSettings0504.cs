// 淨室驗收（TASK-0504）：觀測頁的匯出設定與作品篩選 —— 預設只列主作品／關掉列全部／作者不分大小寫／關鍵字比 ID 與名稱；
// Inspector 點 vox、取消合併、填資料夾都真的寫進物件；🩸 清空字串欄位（Inspector 寫 null）不炸、篩選照跑；
// session JSON 讀寫一圈值不變、沒存過 ⇒ 預設值、JSON 沒變沿用同一個物件；草稿預設開、ApplyPending 不畫東西就先套這一輪的輸入。
// 走頁面同一支 FilterWorks／LoadCfg／SaveCfg／ApplyPending。
using Senate.Cli.Pages;
using SCP.Core.Gui;
using SCP.Core.Sculpture;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SculptureViewerSettingsCleanRoom()
    {
        const string name = "觀測頁設定：預設只列主作品／關掉列全部／作者不分大小寫／關鍵字比 ID 與名稱／類型（船看 meta kind、🔴 子作品不算船）／Inspector 寫進物件／🔴 清空字串欄位不炸／session JSON 一圈不變、沒存過＝預設／草稿預設開、這一輪的輸入在渲染前先套進物件／切換投影重置鏡頭／顯示大小可調／記住上次選取與預覽設定（淨室，TASK-0504）";
        var failures = new List<string>();
        void Check(bool value, string why) { if (!value) failures.Add(why); }
        try
        {
            var works = new List<SCP_SculptWork>
            {
                new() { id = "ship", title = "憲法號", owner = "basecamp", status = "ready" },
                new() { id = "ship-deck", title = "甲板", owner = "basecamp", status = "ready", parent_work = "ship", tags = new() { "sub" } },
                // TASK-0505 反向對照：掛在任務底下（parent_work）但沒標 sub ⇒ 仍是主作品（憲法號（組裝）的形狀）
                new() { id = "ship-assembly", title = "組裝", owner = "basecamp", status = "ready", parent_work = "ship", tags = new() { "wip" } },
                new() { id = "rain", title = "雨後留白", owner = "meadow", status = "ready" },
            };
            string Ids(SculptWorkFilter f) => string.Join(",", SculptureViewerPage.FilterWorks(works, f).Select(w => w.id));

            // ① 篩選
            Check(Ids(new SculptWorkFilter()) == "ship,ship-assembly,rain", "預設應只列沒標 sub 的（有 parent_work 沒標的也算主作品）：" + Ids(new SculptWorkFilter()));
            Check(Ids(new SculptWorkFilter { MainOnly = false }) == "ship,ship-deck,ship-assembly,rain", "關掉 MainOnly 應列全部");
            Check(Ids(new SculptWorkFilter { Owner = "BASECAMP" }) == "ship,ship-assembly", "作者應不分大小寫、且仍只列主作品");
            Check(Ids(new SculptWorkFilter { Keyword = "WIP" }) == "ship-assembly", "關鍵字應也比標籤");
            Check(Ids(new SculptWorkFilter { MainOnly = false, Keyword = "甲" }) == "ship-deck", "關鍵字應比名稱");
            Check(Ids(new SculptWorkFilter { MainOnly = false, Keyword = "DECK" }) == "ship-deck", "關鍵字應比 ID、不分大小寫");

            // ①b 類型（Tim 2026-10-13）：船 ＝ 主作品自己的 meta kind 有造船規格那幾種；🔴 子作品帶了砲也不是船（它是部件）；
            //     kind 不認得（lantern）／沒讀到 ⇒ 其他；反向對照：把 ShipKinds 那一格換掉，同一份資料就不該再判成船
            var kinds = new Dictionary<string, IReadOnlyCollection<string>>
            {
                ["ship-assembly"] = new[] { "magazine" },
                ["ship-deck"] = new[] { "gun", "ladders" },
                ["rain"] = new[] { "lantern" },
            };
            string IdsT(SculptWorkFilter f, SculptWorkType t) => string.Join(",", SculptureViewerPage.FilterWorks(works, f, t, kinds).Select(w => w.id));
            Check(IdsT(new SculptWorkFilter(), SculptWorkType.ship) == "ship-assembly", "類型＝船應只列帶 magazine 的組裝件：" + IdsT(new SculptWorkFilter(), SculptWorkType.ship));
            Check(IdsT(new SculptWorkFilter { MainOnly = false }, SculptWorkType.ship) == "ship-assembly", "🔴 關掉 MainOnly，帶砲的子作品也不該算船：" + IdsT(new SculptWorkFilter { MainOnly = false }, SculptWorkType.ship));
            Check(IdsT(new SculptWorkFilter { MainOnly = false }, SculptWorkType.other) == "ship,ship-deck,rain", "其他 ＝ 不是船的全部（含子作品、不認得的 kind、沒有 kind）：" + IdsT(new SculptWorkFilter { MainOnly = false }, SculptWorkType.other));
            Check(IdsT(new SculptWorkFilter(), SculptWorkType.all) == Ids(new SculptWorkFilter()), "類型＝全部應跟沒給類型一樣");
            Check(SculptureViewerPage.FilterWorks(works, new SculptWorkFilter(), SculptWorkType.ship, null).Count == 0, "沒有 kind 表 ⇒ 一艘船都沒有（不是全部）");
            Check(SculptureViewerPage.WorkTypeOf(works[2], new[] { "magazine-x" }) == SculptWorkType.other, "反向對照：kind 名不在表上就不是船");

            // ①c 房屋、城鎮（Tim 2026-10-14，TASK-0582）：照標籤 house／town；🔴 子作品也算、也不看 MainOnly（房屋就是城鎮的零件）；
            //     兩個標籤都有 ⇒ 城鎮；船優先（帶 magazine 的主作品就算標了 town 也是船）；反向對照：標籤拼錯（houses）不算
            var town = new List<SCP_SculptWork>(works)
            {
                new() { id = "port", title = "岩堡港", owner = "summit", status = "ready", tags = new() { "town" } },
                new() { id = "port-ground", title = "地形", owner = "summit", status = "ready", tags = new() { "sub", "town" } },
                new() { id = "port-house", title = "樓房", owner = "summit", status = "ready", tags = new() { "sub", "house" } },
                new() { id = "port-tavern", title = "酒館", owner = "summit", status = "ready", tags = new() { "house", "town" } },
                new() { id = "port-typo", title = "拼錯", owner = "summit", status = "ready", tags = new() { "sub", "houses" } },
            };
            var kinds2 = new Dictionary<string, IReadOnlyCollection<string>>(kinds) { ["port"] = new[] { "lantern" } };
            string IdsT2(SculptWorkFilter f, SculptWorkType t) => string.Join(",", SculptureViewerPage.FilterWorks(town, f, t, kinds2).Select(w => w.id));
            Check(IdsT2(new SculptWorkFilter(), SculptWorkType.building) == "port-house", "🔴 房屋：MainOnly 開著也要列出子作品 port-house（標了 town 的酒館算城鎮、拼錯的不算）：" + IdsT2(new SculptWorkFilter(), SculptWorkType.building));
            Check(IdsT2(new SculptWorkFilter(), SculptWorkType.town) == "port,port-ground,port-tavern", "城鎮：主作品＋子作品都列、兩個標籤都有算城鎮：" + IdsT2(new SculptWorkFilter(), SculptWorkType.town));
            Check(IdsT2(new SculptWorkFilter(), SculptWorkType.other) == "ship,rain", "其他（MainOnly 開）不該含房屋、城鎮、拼錯的子作品：" + IdsT2(new SculptWorkFilter(), SculptWorkType.other));
            Check(IdsT2(new SculptWorkFilter { MainOnly = false }, SculptWorkType.other) == "ship,ship-deck,rain,port-typo", "反向對照：標籤拼錯（houses）就是其他：" + IdsT2(new SculptWorkFilter { MainOnly = false }, SculptWorkType.other));
            Check(IdsT2(new SculptWorkFilter(), SculptWorkType.ship) == "ship-assembly", "加了房屋城鎮之後船的結果不能變：" + IdsT2(new SculptWorkFilter(), SculptWorkType.ship));
            Check(IdsT2(new SculptWorkFilter { Owner = "basecamp" }, SculptWorkType.building) == "", "作者篩選照樣套在房屋上");
            var shipTown = new SCP_SculptWork { id = "x", title = "x", owner = "a", status = "ready", tags = new() { "town" } };
            Check(SculptureViewerPage.WorkTypeOf(shipTown, new[] { "magazine" }) == SculptWorkType.ship, "船優先：帶 magazine 的主作品標了 town 也是船");

            // ② Inspector 真的改到物件（點 enum 鈕／取消勾／填欄位）
            var cfg = new SculptExportSettings();
            SCP_GuiInspector.Draw(new SCP_Ui(new SCP_GuiInput { ClickedId = "sculpt/export/Format=vox" }), cfg, "sculpt/export");
            var input = new SCP_GuiInput();
            input.Toggles["sculpt/export/MergeFaces"] = false;
            input.Fields["sculpt/export/OutDir"] = "D:/out";
            var changed = SCP_GuiInspector.Draw(new SCP_Ui(input), cfg, "sculpt/export");
            Check(cfg.Format == SculptExportFormat.vox, "點 Format=vox 沒寫進物件");
            Check(changed.Changed && !cfg.MergeFaces && cfg.OutDir == "D:/out", "取消合併／填資料夾沒寫進物件");

            // ③ 🩸 清空字串欄位：Inspector 對參考型別寫 null —— 不收成空字串的話，篩選與匯出一讀就 NullReferenceException
            var filter = new SculptWorkFilter { Owner = "basecamp" };
            var clear = new SCP_GuiInput();
            clear.Fields["sculpt/filter/Owner"] = "";
            SCP_GuiInspector.Draw(new SCP_Ui(clear), filter, "sculpt/filter");
            Check(filter.Owner == "", "清空 Owner 後應是空字串，實際：" + (filter.Owner == null ? "null" : "「" + filter.Owner + "」"));
            string afterClear = "";
            try { afterClear = Ids(filter); } catch (Exception e) { failures.Add("清空 Owner 後篩選炸了：" + e.GetType().Name); }
            Check(afterClear == "ship,ship-assembly,rain", "清空 Owner 後應回到只看 MainOnly：" + afterClear);

            // ④ session JSON：存一圈值不變；沒存過 ⇒ 預設；JSON 沒變 ⇒ 沿用同一個物件
            var save = new SCP_Ui();
            string? json = null;
            SculptureViewerPage.SaveCfg(save, "t/state", cfg, ref json);
            var load = new SCP_GuiInput();
            load.Fields["t/state"] = save.FieldValue("t/state", "");
            var loadUi = new SCP_Ui(load);
            SculptExportSettings? cache = null;
            string? cacheJson = null;
            var back = SculptureViewerPage.LoadCfg(loadUi, "t/state", ref cache, ref cacheJson);
            Check(back.Format == SculptExportFormat.vox && !back.MergeFaces && back.OutDir == "D:/out", "session JSON 讀回的值不對：" + load.Fields["t/state"]);
            Check(ReferenceEquals(back, SculptureViewerPage.LoadCfg(loadUi, "t/state", ref cache, ref cacheJson)), "JSON 沒變卻換了物件");
            SculptExportSettings? fresh = null;
            string? freshJson = null;
            var def = SculptureViewerPage.LoadCfg(new SCP_Ui(), "t/state", ref fresh, ref freshJson);
            Check(def.Format == SculptExportFormat.obj && def.MergeFaces && def.OutDir == "", "沒存過應是預設值");

            // ⑤ 觀測設定：草稿預設開；ApplyPending 不畫東西就把這一輪的勾與欄位套進物件（渲染在 Inspector 之前跑，靠它拿到新值）
            var view = new SculptWorkViewSettings { HideLinks = "self" };
            Check(view.Draft, "草稿模式預設應是開");
            Check(!SculptureViewerPage.ApplyPending(new SCP_Ui(), view, "sculpt/view") && view.Draft && view.HideLinks == "self", "沒有輸入卻改了物件");
            var pend = new SCP_GuiInput();
            pend.Toggles["sculpt/view/Draft"] = false;
            pend.Fields["sculpt/view/Links"] = "deck,mast";
            pend.Fields["sculpt/view/HideLinks"] = "";
            var pendUi = new SCP_Ui(pend);
            bool applied = SculptureViewerPage.ApplyPending(pendUi, view, "sculpt/view");
            Check(applied && !view.Draft && view.Links == "deck,mast" && view.HideLinks == "", "這一輪的輸入沒先套進物件：Draft=" + view.Draft + " Links=" + view.Links + " HideLinks=" + (view.HideLinks ?? "null"));
            Check(pendUi.Root.Children.Count == 0, "ApplyPending 不該畫任何東西");
            var badNum = new SculptExportSettings();
            Check(!SculptureViewerPage.ApplyPending(new SCP_Ui(), badNum, "x"), "反向對照：沒輸入的 export 設定不該被改");

            // ⑥ 預覽設定：檢視倍率範圍（預設 0.05–50）；滾輪夾在範圍內；不合法整組退回預設並說為什麼；小數欄位也先套
            var prev = new SculptPreviewSettings();
            var (lo, hi, why) = SculptureViewerPage.ViewScaleRange(prev);
            Check(lo == 0.05 && hi == 50 && why == null, "預設倍率範圍應是 0.05–50");
            Check(Math.Abs(SculptureViewerPage.WheelViewScale(0.1, -1, lo, hi) - 0.08) < 1e-9, "往下滾一格應是 ÷1.25");
            Check(SculptureViewerPage.WheelViewScale(0.1, -4, lo, hi) == 0.05, "往下滾到底應停在下限 0.05（0.1 時代的下限擋不到這裡）");
            Check(SculptureViewerPage.WheelViewScale(40, 3, lo, hi) == 50, "往上滾到頂應停在上限");
            var custom = SculptureViewerPage.ViewScaleRange(new SculptPreviewSettings { ViewScaleMin = 0.01, ViewScaleMax = 200 });
            Check(custom.Min == 0.01 && custom.Max == 200 && custom.Why == null && SculptureViewerPage.WheelViewScale(0.02, -5, custom.Min, custom.Max) == 0.01, "自訂範圍應生效");
            foreach (var (bad, label) in new[] { (new SculptPreviewSettings { ViewScaleMin = 0 }, "下限 0"), (new SculptPreviewSettings { ViewScaleMax = 2000 }, "上限超過渲染端"), (new SculptPreviewSettings { ViewScaleMin = 5, ViewScaleMax = 2 }, "下限大於上限") })
            {
                var r = SculptureViewerPage.ViewScaleRange(bad);
                Check(r.Min == 0.05 && r.Max == 50 && r.Why != null, label + " ⇒ 應整組退回預設並說為什麼");
            }
            // ⑦ 載入後重置鏡頭的判準：只看作品／展品／region（草稿、關聯、排除色變了不算換對象）
            string a = SculptureViewerPage.PlanSubject("work=ship;exhibit=;region=;exclude_color=;draft=0;links=;hide_links=");
            Check(a == SculptureViewerPage.PlanSubject("work=ship;exhibit=;region=;exclude_color=5;draft=1;links=deck;hide_links=self"), "草稿／關聯／排除色變了不該算換對象");
            Check(a != SculptureViewerPage.PlanSubject("work=rain;exhibit=;region=;exclude_color=;draft=0;links=;hide_links="), "換作品應算換對象");
            Check(SculptureViewerPage.PlanSubject("work=;exhibit=a;region=1..2,1..2,1..2") != SculptureViewerPage.PlanSubject("work=;exhibit=b;region=1..2,1..2,1..2"), "換展品應算換對象");
            var prevIn = new SCP_GuiInput();
            prevIn.Fields["sculpt/preview/ViewScaleMin"] = "0.02";
            Check(SculptureViewerPage.ApplyPending(new SCP_Ui(prevIn), prev, "sculpt/preview") && prev.ViewScaleMin == 0.02, "小數欄位應先套進物件：" + prev.ViewScaleMin);

            // ⑧ 切換正交／透視也重置一次鏡頭（Tim 2026-10-12）：同一件作品只換投影 ⇒ 鍵不同；只換草稿 ⇒ 鍵相同
            string sig = "work=ship;exhibit=;region=;exclude_color=;draft=0;links=;hide_links=";
            Check(SculptureViewerPage.RefitKey(sig, SCP_SculptProjection.Orthographic) != SculptureViewerPage.RefitKey(sig, SCP_SculptProjection.Perspective), "只換投影應重置鏡頭");
            Check(SculptureViewerPage.RefitKey(sig, SCP_SculptProjection.Perspective) == SculptureViewerPage.RefitKey(sig.Replace("draft=0", "draft=1"), SCP_SculptProjection.Perspective), "反向對照：同投影只換草稿不該重置");

            // ⑨ 渲染畫面顯示大小（Tim 2026-10-12）：預設 720；範圍內照用；超出 ⇒ 退回預設並說為什麼；整數欄位也先套
            Check(SculptureViewerPage.DisplaySide(new SculptPreviewSettings()) == (720f, null), "預設顯示邊長應是 720");
            Check(SculptureViewerPage.DisplaySide(new SculptPreviewSettings { ViewSide = 1200 }) == (1200f, null), "範圍內的 ViewSide 應照用");
            foreach (int badSide in new[] { 0, 100, 5000 })
            {
                var r = SculptureViewerPage.DisplaySide(new SculptPreviewSettings { ViewSide = badSide });
                Check(r.Side == 720f && r.Why != null, "ViewSide=" + badSide + " ⇒ 應退回 720 並說為什麼");
            }
            var sideIn = new SCP_GuiInput();
            sideIn.Fields["sculpt/preview/ViewSide"] = "960";
            var side = new SculptPreviewSettings();
            Check(SculptureViewerPage.ApplyPending(new SCP_Ui(sideIn), side, "sculpt/preview") && side.ViewSide == 960, "ViewSide 應先套進物件：" + side.ViewSide);

            // ⑩ 記住上次的選取（Tim 2026-10-12）：沒設過 ⇒ 空；記了讀得回來；換到共用展區只記空間、不洗掉上次的作品
            string tmpRepo = Path.Combine(Path.GetTempPath(), "senate_sculpt_lastsel_" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                Directory.CreateDirectory(tmpRepo);
                var none = Senate.Core.SculptureWorkPrefs.ReadLastSelection(tmpRepo, out string? e0);
                Check(none == ("", "") && e0 == null, "沒設過應回空：" + none + " " + e0);
                Check(Senate.Core.SculptureWorkPrefs.SaveLastSelection(tmpRepo, "work", "ship").Ok, "記不下選取");
                Check(Senate.Core.SculptureWorkPrefs.ReadLastSelection(tmpRepo, out _) == ("work", "ship"), "記了讀不回來");
                Check(Senate.Core.SculptureWorkPrefs.SaveLastSelection(tmpRepo, "shared", null).Ok, "只記空間失敗");
                Check(Senate.Core.SculptureWorkPrefs.ReadLastSelection(tmpRepo, out _) == ("shared", "ship"), "換到共用展區不該洗掉上次的作品");

                // ⑪ 預覽設定跨重開保留（Tim 2026-10-12）：沒設過 ⇒ 空；頁面寫的那份 JSON 落盤、讀回、填回物件，值一格不差
                Check(Senate.Core.SculptureWorkPrefs.ReadPreviewJson(tmpRepo, out string? pe0) == "" && pe0 == null, "預覽設定沒設過應回空：" + pe0);
                var keep = new SculptPreviewSettings { ViewScaleMin = 0.01, ViewScaleMax = 300, ViewSide = 960 };
                string keepJson = SCP.Core.Json.SCP_JsonMapper.ToJson(keep).ToJson(false);
                Check(Senate.Core.SculptureWorkPrefs.SavePreviewJson(tmpRepo, keepJson).Ok, "預覽設定存不進去");
                string prevBack = Senate.Core.SculptureWorkPrefs.ReadPreviewJson(tmpRepo, out _);
                var restored = new SculptPreviewSettings();
                SCP.Core.Json.SCP_JsonMapper.Populate(restored, SCP.Core.Json.SCP_JsonData.Parse(prevBack));
                Check(prevBack == keepJson && restored.ViewScaleMin == 0.01 && restored.ViewScaleMax == 300 && restored.ViewSide == 960,
                      "預覽設定讀回不一致：" + prevBack);
            }
            finally { try { Directory.Delete(tmpRepo, true); } catch { } }
        }
        catch (Exception e) { failures.Add(e.GetType().Name + ": " + e.Message); }
        return new(name, failures.Count == 0 ? "篩選 5 格＋類型 6 格＋房屋城鎮 7 格／Inspector 寫入／清空不炸／JSON 一圈與預設／草稿預設與先套輸入／投影重置／顯示大小／記住選取與預覽設定 全過" : string.Join("；", failures),
                   failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
