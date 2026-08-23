// 區塊職責：page key → page 實例（導覽路徑的復原用工廠）。
// 物理意義：CLI 的每一次呼叫都是新 process ⇒ 「我現在停在哪一頁」必須存成資料
//           （`build/ui_session.json` 的 `nav`，內容是 page key）。
//           要從 key 變回頁面，就得有一個地方知道 key 對應誰 —— 就是這裡。
//           ⇒ **page key 是契約**：它進了 session、進了 agent 的指令，跟顯式 id key 同一個道理。
// 數值影響：純建構，零 IO。
// ⚠ 認不得的 key 回 **null 而不是回根頁**：回根頁會讓「你要的那頁不存在了」
//   長得像「你本來就在首頁」，而使用者只會覺得按鈕沒反應。
//   由 controller 的 RestorePath 停在那裡並回報。
using SCP.Core.Gui;

namespace Senate.Cli.Pages;

public static class SenatePages
{
    /// <summary>根頁（stack 的第一層，永遠存在）。</summary>
    public static SCP_GuiPage Root(DoctorModel iModel) => new DoctorPage(iModel);

    /// <summary>依 key 造頁；認不得就回 null（不要猜、不要退回根頁）。</summary>
    public static SCP_GuiPage? Create(string iKey, DoctorModel iModel)
    {
        switch (iKey)
        {
            case DoctorPage.PageKey: return new DoctorPage(iModel);
            case StylePage.PageKey: return new StylePage(iModel);
            case SettingsPage.PageKey: return new SettingsPage(iModel);
            default: return null;
        }
    }
}
