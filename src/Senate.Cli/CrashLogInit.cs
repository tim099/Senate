// 區塊職責：在 `Main` 之前裝上崩潰報告（Senate.Core/CrashLog）。
// 物理意義：ModuleInitializer 是本 exe 最早能跑碼的時間點 —— `Program` 的靜態初始化、設定檔解析、
//           Console 重開判斷（ConsoleHost）炸掉的話，都在 Main 的第一行之前或附近；裝在 Main 裡面會漏掉那一段。
// 數值影響：只訂閱例外事件，不改任何行為。repo 根走 `ServerBootstrap.FindRepoRoot`（與 Program.RepoRoot 同一條規則）。
using System.Runtime.CompilerServices;
using Senate.Core;

static class CrashLogInit
{
#pragma warning disable CA2255   // 「應用程式碼別用 ModuleInitializer」—— 這裡要的正是比 Main 早，理由見檔頭
    [ModuleInitializer]
    internal static void Init()
    {
        try { CrashLog.Install(ServerBootstrap.FindRepoRoot(), "senate"); }
        catch (Exception) { }   // 裝不上就不裝 —— ⛔ 不讓崩潰處理器本身成為啟動失敗的原因
    }
#pragma warning restore CA2255
}
