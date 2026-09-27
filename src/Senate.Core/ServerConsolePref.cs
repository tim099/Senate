// 區塊職責：「要不要顯示 Console 視窗」的兩個設定 —— 讀、寫、與單次覆寫的決議。
//   · `server.showConsole`：Server 啟動時（Tim 2026-09-26：預設不顯示；CLI 可以決定；ServerAdminPage 可以設定）。
//     生出 Server 行程的路有三條（委派 autostart／管理頁「啟動」鈕／`server start --detach`），
//     ⇒ 三條都問這一支，⛔ 各自判的話，同一個設定會有三個答案。
//   · `senate.showConsole`：雙擊 senate.exe 開介面時（Tim 2026-09-26：預設不顯示；設定放在 ServerAdminPage）。
// 數值影響：只讀寫 `senate.pages.local.json` 的那兩格（由 SenatePageStore 決定檔名）。
//           ⚠ 從終端機打 `senate …` 不受 `senate.showConsole` 影響：那時 console 是 shell 的，不是它的。
#nullable enable
using SCP.Core.Prefs;

namespace Senate.Core;

public static class ServerConsolePref
{
    /// <summary>Server 啟動時顯示 Console。預設 false ＝ 不顯示。</summary>
    public static readonly SCP_PrefKey<bool> ShowConsole = SCP_PrefKey.Bool("server", "showConsole", false);

    /// <summary>雙擊 senate.exe 開介面時顯示 Console。預設 false ＝ 不顯示。</summary>
    public static readonly SCP_PrefKey<bool> SenateShowConsole = SCP_PrefKey.Bool("senate", "showConsole", false);

    /// <summary>Server 那一格的決議（單次覆寫 ＞ 設定 ＞ 預設）。</summary>
    public static bool Resolve(string iRepoRoot, bool? iOverride, out string oSource)
        => Resolve(iRepoRoot, ShowConsole, iOverride, out oSource);

    /// <summary>
    /// 這一次要不要顯示：單次覆寫 ＞ 持久設定 ＞ 預設。
    /// <para><paramref name="oSource"/> 說出答案是哪一層給的 —— 「我沒設」與「我設了 false」
    /// 印出來的結果一樣，但它們是兩件事（設定檔讀壞了的時候尤其要分得開）。</para>
    /// </summary>
    public static bool Resolve(string iRepoRoot, SCP_PrefKey<bool> iKey, bool? iOverride, out string oSource)
    {
        if (iOverride.HasValue) { oSource = "單次覆寫"; return iOverride.Value; }
        SCP_PrefRead<bool> aRead = SenatePageStore.For(iRepoRoot).Read(iKey);
        switch (aRead.State)
        {
            case SCP_PrefState.Present: oSource = "設定 " + iKey.Path; return aRead.Value;
            case SCP_PrefState.Missing: oSource = "預設（沒設過）"; return iKey.Default;
            default:
                oSource = "預設（⚠ 設定讀不了：" + (aRead.Error ?? "沒有說明") + "）";
                return iKey.Default;
        }
    }

    /// <summary>寫回 Server 那一格。回（成功, 人可讀的說法）。</summary>
    public static (bool Ok, string Message) Save(string iRepoRoot, bool iShow)
        => Save(iRepoRoot, ShowConsole, iShow);

    /// <summary>寫回指定那一格。回（成功, 人可讀的說法）。</summary>
    public static (bool Ok, string Message) Save(string iRepoRoot, SCP_PrefKey<bool> iKey, bool iShow)
        => SenatePageStore.For(iRepoRoot).Write(iKey, iShow);
}
