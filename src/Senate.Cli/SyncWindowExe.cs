// 區塊職責：`senate-sync.exe` —— 只開 Submodule 狀態頁、**不拉常駐 Server** 的那顆入口。
// 物理意義：Tim 2026-10-02：「單獨 build 另一個 exe 直接開啟 SubmoduleSyncPage（我會先用這個進行同步），
//           避免我啟動 Senate 時 Server 一起啟動就導致 repo dirty」。
//           🩸 起因：senate.exe 一啟動就依 `server.autostartOnLaunch` 拉 main Server（TASK-0329），
//             而 main 一起來就補跑跨日結算（發券寫 `letters/<P>/vouchers/<region>.json`）⇒
//             還沒同步，十幾顆信件庫就先髒了 ⇒ 同步頁的「dirty 一律跳過」把它們全擋下。
//           ⇒ 順序要反過來：**先同步、再讓 Server 起來**。這顆 exe 就是「先同步」那一步。
// 數值影響：同一份二進位（build.sh 把 publish/senate.exe 複製成 publish/senate-sync.exe），
//           分辨靠**自己的檔名** —— 頁面碼一行都不分岔。
//           ⚠ 只有「沒帶任何參數」時才改道：帶了參數的 senate-sync.exe 跟 senate.exe 行為完全一樣
//             （拿它跑 `cmd …` 照樣會拉 Server —— 射程只到「雙擊開同步頁」這件事）。
namespace Senate.Cli;

static class SyncWindowExe
{
    /// <summary>複製出來那顆的檔名（不含副檔名）。build.sh 的複製與捷徑用的是同一個字。</summary>
    public const string ExeName = "senate-sync";

    /// <summary>子命令名 —— 也可以從 senate.exe 直接叫（`senate sync-window`），方便驗收。</summary>
    public const string Subcommand = "sync-window";

    /// <summary>
    /// 目前這顆 process 是不是 senate-sync.exe。
    /// <para>⚠ 讀 <see cref="Environment.ProcessPath"/> 而不是 <c>args[0]</c>：Windows 的 Main 拿不到 argv[0]；
    /// 單檔 publish 下 <c>Assembly.Location</c> 是空字串（那條路會永遠回 false，而且不報錯）。</para>
    /// </summary>
    public static bool IsCurrent()
    {
        string? aPath = Environment.ProcessPath;
        return aPath != null
               && string.Equals(Path.GetFileNameWithoutExtension(aPath), ExeName, StringComparison.OrdinalIgnoreCase);
    }
}
