// 區塊職責：**頁面設定的持久化層** —— senate.pages.local.json（repo 根、不入版控）。
// 物理意義：頁面的操作狀態原本只住驅動端 session（build/ui_session.json）——
//           CLI 跨呼叫記得住，但**視窗每次開都是新 session** ⇒「我上次調好的範圍」全部蒸發，
//           而蒸發之後的畫面跟「我沒調過」一模一樣（Tim 2026-08-28 點名的坑）。
//           ⇒ 值得留的設定要有一個**顯式儲存**的落點；本類別是那一格。
//
//           ⭐ 2026-08-30：讀寫本體**搬進 SCP_Core**（`SCP.Core.Prefs.SCP_JsonPrefs`）——
//           本類別退化成「決定檔名在哪」＋ 舊呼叫端的相容外殼。
//           搬家的理由不是重構癖：Unity 那側也要有同一個 prefs，而
//           「atomic replace ＋ 未知 section 保留 ＋ 回讀驗證」這組動作**漏掉任何一格都是靜默的**，
//           所以它只能有一個落點（見 <SCP_Core>/Docs~/Coding_Standards.md §3、§5）。
// 數值影響：一個檔、各頁面／子系統各佔一個頂層 key。行為與搬家前相同（同一套動作）。
//           讀寫仍走 SCP_Core 自帶的 JSON（Tim 2026-08-28 指定）——
//           不用 System.Text.Json 是刻意的：頁面設定型別已經在 SCP_TypeSchema 的方言內
//           （inspector 畫得出 ⇒ mapper 存得進，同一套分類器），不引第二套序列化語意。
// ⚠ 與 senate.local.json 的分界：那份是「這台機器管哪些專案」（senate init 的主體、有樣板）；
//   本檔是「各頁面上次調成什麼樣」。混進去的話，頁面每存一次就動一次主設定檔的 diff，
//   而人開 diff 想看的是專案清單有沒有變，不是誰又存了一次畫面狀態。
using SCP.Core.Prefs;

namespace Senate.Core;

public static class SenatePageStore
{
    /// <summary>
    /// prefs 檔在哪 —— **這是 Senate 這側唯一決定檔名的地方**。
    /// <para>⚠ SCP_Core 的 prefs 層不推導路徑（路徑不該被推導，該被傳遞）；
    /// 它由這裡拿到絕對路徑，所以換宿主時只有這一行要動。</para>
    /// </summary>
    public static string DefaultPath(string iRepoRoot) => Path.Combine(iRepoRoot, "senate.pages.local.json");

    /// <summary>本 repo 的 prefs 實例。⚠ 各呼叫端**不要自己 new** —— 檔名只能有一個決定點。</summary>
    public static ISCP_Prefs For(string iRepoRoot) => new SCP_JsonPrefs(DefaultPath(iRepoRoot));

    /// <summary>
    /// 讀某一頁存過的設定。回 null ＝ **沒存過**（檔不在／沒這頁的區塊）——那不是錯誤；
    /// 檔在但壞掉 ⇒ 也回 null 但一定經過 <paramref name="iWarn"/> 說出來（兩態不得同形）。
    /// </summary>
    public static T? Load<T>(string iRepoRoot, string iPageKey, Action<string>? iWarn = null) where T : class
        => For(iRepoRoot).LoadSection<T>(iPageKey, iWarn);

    /// <summary>
    /// 把某一頁的設定寫回去 —— **只動自己那個 key，其他頁的區塊原樣保留**
    /// （讀整份 → 換一格 → atomic replace → 回讀）。回傳（成功, 人可讀的說法），失敗一定有話說。
    /// </summary>
    public static (bool ok, string message) Save(string iRepoRoot, string iPageKey, object iSettings)
    {
        var (aOk, aMsg) = For(iRepoRoot).SaveSection(iPageKey, iSettings);
        return (aOk, aMsg);
    }
}
