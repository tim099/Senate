// 區塊職責：**頁面目錄的組裝點** —— 這個 app 有哪些頁、根頁是誰。
// 物理意義：CLI 的每一次呼叫都是新 process ⇒ 「我現在停在哪一頁」必須存成資料
//           （`SenateData/runtime/ui_session.json` 的 `nav`，內容是 page key）。
//           要從 key 變回頁面，就得有一個地方知道 key 對應誰 —— 就是這裡。
//           ⇒ **page key 是契約**：它進了 session、進了 agent 的指令，跟顯式 id key 同一個道理。
// 數值影響：純建構，零 IO。⚠ 但目錄為了讀「標題／分組」會把每一頁**建一次再丟掉**
//           （見 SCP_GuiPageCatalog）⇒ 頁面的建構子必須便宜，讀檔要放 OnPush（SettingsPage 已照做）。
// ⚠ 認不得的 key 回 **null 而不是回根頁**：回根頁會讓「你要的那頁不存在了」
//   長得像「你本來就在首頁」，而使用者只會覺得按鈕沒反應。
//   由 controller 的 RestorePath 停在那裡並回報。
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public static class SenatePages
{
    /// <summary>根頁的 key —— stack 的第一層永遠是它。</summary>
    public const string RootKey = SCP_GuiHomePage.PageKey;

    /// <summary>
    /// 建這個 app 的頁面目錄。
    /// <para>⚠ 每個呼叫端各建一份（跟 controller 一樣「一個 Window 一套」）——
    /// 目錄裡的工廠閉包抓著 model，做成 static 就等於把 model 變成全域單例，
    /// 而那正是 D13 把 <c>Ins</c> 拿掉的理由。</para>
    /// </summary>
    public static SCP_GuiPageCatalog BuildCatalog(SenateModel iModel)
    {
        var aCatalog = new SCP_GuiPageCatalog();

        // 🔴 **只有這一行是顯式的，而它逃不掉**（雞生蛋）：入口頁要拿著目錄才畫得出清單，
        //    而目錄正在被建 ⇒ 反射沒有第二個參數可以遞。⇒ 它的 ctor 是 `(ctx, catalog)`，
        //    落在 `AutoRegister` 的「形狀不符」那一格，所以這裡先佔住 key（顯式優先，不算缺陷）。
        //   （它自己 MenuGroup = null，所以不會把自己列進自己的清單 —— 同 UCL 排除 EditorMenuPage 那一格）
        aCatalog.Register(SCP_GuiHomePage.PageKey, () => new SCP_GuiHomePage(iModel, aCatalog));

        // ⭐ 其餘**全部自動收**（TASK-0276，Tim 2026-09-22 拍板 C）：
        //    判準是「繼承 `SCP_GuiPage`」，⛔ 不看建構子；不想被收的頁貼 `[SCP_PageIgnore("理由")]`。
        //    ⇒ 以後在 SCP_Core 加一支頁，**這個檔案一個字都不用改**（那才是這一筆要買的東西：
        //      此前 13 支裡有 6 支住 SCP_Core，每加一支都要跑來這裡補一行、再 bump 一次父層指標）。
        //    ⚠ assembly 清單**顯式給**（見 SenateModel.PageAssemblies）——
        //      ⛔ 不是 `AppDomain.GetAssemblies()`：CLI 是用到才載，那個差異不報錯。
        //    缺陷（沒有 PageKey／key 撞名／ctor 形狀不符）落進 `aCatalog.Diagnostics`，
        //    由入口頁畫出來；build 階段另有一道閘會讓撞名變成紅的（`senate pages-check`）。
        aCatalog.AutoRegister(iModel.PageAssemblies, iModel);

        return aCatalog;
    }

    /// <summary>根頁（stack 的第一層，永遠存在）。</summary>
    public static SCP_GuiPage Root(SCP_GuiPageCatalog iCatalog)
        => iCatalog.Create(RootKey)
           ?? throw new System.InvalidOperationException(
               $"目錄裡沒有根頁 '{RootKey}' —— BuildCatalog 少登記了（這是程式錯誤，不是設定問題）");
}
