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
        const string name = "觀測頁三份設定：預設只列主作品／關掉列全部／作者不分大小寫／關鍵字比 ID 與名稱／Inspector 寫進物件／🔴 清空字串欄位不炸／session JSON 一圈不變、沒存過＝預設／草稿預設開、這一輪的輸入在渲染前先套進物件（淨室，TASK-0504）";
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
        }
        catch (Exception e) { failures.Add(e.GetType().Name + ": " + e.Message); }
        return new(name, failures.Count == 0 ? "篩選 5 格／Inspector 寫入／清空不炸／JSON 一圈與預設／草稿預設與先套輸入 全過" : string.Join("；", failures),
                   failures.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
