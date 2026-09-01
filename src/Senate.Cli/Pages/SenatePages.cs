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
        // 入口頁要拿著目錄才畫得出清單 ⇒ 用閉包把 aCatalog 帶進去
        //（它自己 MenuGroup = null，所以不會把自己列進自己的清單 —— 同 UCL 排除 EditorMenuPage 那一格）
        aCatalog.Register(SCP_GuiHomePage.PageKey, () => new SCP_GuiHomePage(iModel, aCatalog));
        aCatalog.Register(DoctorPage.PageKey, () => new DoctorPage(iModel));
        aCatalog.Register(SubmoduleSyncPage.PageKey, () => new SubmoduleSyncPage(iModel));
        aCatalog.Register(SCP_GuiStylePage.PageKey, () => new SCP_GuiStylePage(iModel));
        aCatalog.Register(SettingsPage.PageKey, () => new SettingsPage(iModel));
        aCatalog.Register(ProjectsPage.PageKey, () => new ProjectsPage(iModel));
        aCatalog.Register(PathsPage.PageKey, () => new PathsPage(iModel));
        aCatalog.Register(SCP_GuiLoginStatusPage.PageKey, () => new SCP_GuiLoginStatusPage(iModel));
        aCatalog.Register(SCP_GuiSkillManagerPage.PageKey, () => new SCP_GuiSkillManagerPage(iModel));
        return aCatalog;
    }

    /// <summary>根頁（stack 的第一層，永遠存在）。</summary>
    public static SCP_GuiPage Root(SCP_GuiPageCatalog iCatalog)
        => iCatalog.Create(RootKey)
           ?? throw new System.InvalidOperationException(
               $"目錄裡沒有根頁 '{RootKey}' —— BuildCatalog 少登記了（這是程式錯誤，不是設定問題）");
}
