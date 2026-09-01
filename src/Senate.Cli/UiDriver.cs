// 區塊職責：`senate ui` 的**非 UI 驅動**入口 —— 看畫面、列元件、下操作，全部走命令列。
// 物理意義：⭐ 這是給「沒有眼睛也沒有滑鼠的操作者」（agent／CI／腳本）用的介面，
//           而它跟人用的 ImGui 視窗**共用同一份頁面碼**。
//           ⇒ 一頁後台從此有三種驅動方式，任兩種可以互為證人：
//             人點視窗 ／ 文字看畫面 ／ 指令操作
// 數值影響：狀態（欄位值、勾選）落在 SenateData/runtime/ui_session.json（版面見 SenatePaths）——
//           因為每次 CLI 呼叫都是新 process，不存檔的話「上一步做過什麼」會消失，
//           而那會讓多步操作變成不可能。點擊**不進狀態**（它是事件，不是狀態）。
// ⚠ 兩趟繪製：第一趟帶著 click 讓頁面的 handler 真的執行，第二趟才是要給人看的畫面。
//   只畫一趟的話，畫面顯示的是**按下前**的狀態 —— 看起來就像「按了沒反應」。
using Senate.Cli.Pages;
using Senate.Core;
using SCP.Core.Gui;
using SCP.Core.Json;

namespace Senate.Cli;

public static class UiDriver
{
    public static string SessionPath(string iRepoRoot)
        => SenatePaths.UiSession(iRepoRoot);

    public static SCP_GuiState Load(string iRepoRoot)
    {
        string p = SessionPath(iRepoRoot);
        if (!File.Exists(p)) return new SCP_GuiState();
        try { return SCP_GuiState.FromJson(SCP_JsonData.Parse(File.ReadAllText(p))); }
        catch (Exception e)
        {
            // 壞掉的 session 不可以靜默重置 —— 那會讓「我的設定不見了」變成無法解釋的事
            Console.Error.WriteLine($"⚠ session 讀取失敗（{e.Message}）—— 這次用空狀態，檔案沒有被覆寫");
            return new SCP_GuiState();
        }
    }

    public static void Save(string iRepoRoot, SCP_GuiState iState)
    {
        string p = SessionPath(iRepoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, iState.ToJson().ToJson() + "\n");
    }

    /// <summary>
    /// 依 session 裡的導覽路徑重建這一疊頁面（根頁永遠是第一層）。
    /// <para>復原不了時**停在那裡並說出來** —— 悄悄退回首頁會讓
    /// 「你要的那頁不存在了」長得像「你本來就在首頁」。</para>
    /// </summary>
    static SCP_GuiPageController BuildController(SCP_GuiPageCatalog iCatalog, SCP_GuiState iState)
    {
        var aCtrl = new SCP_GuiPageController();
        aCtrl.Push(SenatePages.Root(iCatalog));

        string? aBad = aCtrl.RestorePath(iState.Nav, k => iCatalog.Create(k));
        if (aBad != null)
            Console.Error.WriteLine(
                $"⚠ 回不到頁面 '{aBad}'（session 的導覽路徑對不上現在的頁面）—— 這次停在：{aCtrl.PathText}");
        return aCtrl;
    }

    /// <summary>
    /// 跑一次操作：重建頁面堆疊 → 套用動作 → 兩趟繪製 → 回傳（樹, 文字）。
    /// <para>⚠ 導覽（push／pop）在**第一趟**就發生，所以第二趟畫的是**新的那一頁** ——
    /// CLI 這側沒有「慢一幀」（視窗那側有，那是 retained 畫布的性質）。
    /// 兩側行為不同這件事要知道，不然「同一顆按鈕在視窗要按兩次」會被當成 bug。</para>
    /// </summary>
    public static (SCP_GuiNode tree, string text) Apply(
        SCP_GuiPageCatalog iCatalog, SCP_GuiState iState, string? iClickId, SCP_GuiStyle iStyle)
    {
        var aCtrl = BuildController(iCatalog, iState);

        // 第一趟：帶 click，讓 handler 真的跑（回傳的樹是舊畫面，不拿來顯示）
        if (iClickId != null)
        {
            var aFirst = new SCP_Ui(iState.ToInput(iClickId));
            aCtrl.Draw(aFirst);
            ApplyWrites(aFirst, iState);   // ⚠ 要在第二趟之前套 —— 不然第二趟畫的是「選之前」的下拉
        }
        // 第二趟：不帶 click，這才是操作之後的畫面
        var aUi = new SCP_Ui(iState.ToInput(null));
        aCtrl.Draw(aUi);
        ApplyWrites(aUi, iState);

        // 導覽是狀態不是事件 ⇒ 存回去，否則下一道指令會回到根頁（看起來像按鈕沒反應）
        iState.Nav = aCtrl.PathKeys;
        return (aUi.Root, SCP_GuiTextRenderer.Render(aUi.Root, iStyle));
    }

    /// <summary>
    /// 把頁面這一輪要求的欄位寫入套進 session 狀態（複合元件的內部狀態靠這條路，見 SCP_Ui.FieldWrites）。
    /// <para>⚠ 為什麼要存進 session 而不是留在記憶體：下拉「開著」「搜尋字是什麼」「選了哪一項」
    /// 都得撐過 process 邊界 —— 不然「點開下拉」與「按下其中一項」是兩道指令，
    /// 第二道會發現下拉已經關上了。</para>
    /// </summary>
    static void ApplyWrites(SCP_Ui iUi, SCP_GuiState oState)
    {
        foreach (var kv in iUi.FieldWrites) oState.Fields[kv.Key] = kv.Value;
    }

    /// <summary>列出畫面上所有可互動元件 —— 「看不見畫面的人」的操作目錄。</summary>
    public static string ListElements(SCP_GuiNode iTree, SCP_GuiStyle iStyle)
    {
        var aUi = new SCP_Ui();
        aUi.Title("可互動元件");
        using (aUi.Table("id", "類型", "標籤", "現值", "怎麼操作"))
        {
            foreach (var e in SCP_GuiQuery.Interactive(iTree))
                aUi.TableRow(e.Id, e.Kind.ToString(), e.Label,
                    e.Kind == SCP_GuiNodeKind.Toggle ? (e.On ? "on" : "off") : e.Value,
                    e.HowTo);
        }
        return SCP_GuiTextRenderer.Render(aUi.Root, iStyle);
    }
}
