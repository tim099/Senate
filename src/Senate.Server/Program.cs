// 區塊職責：`Senate.Server.exe` 的入口 —— 薄到只剩「找 repo 根、交給 ServerBootstrap」。
// 物理意義：常駐邏輯一行都不在這裡（在 Senate.Core/ServerBootstrap ＋ ServerHost）。
//           ⇒ `senate server start` 與這顆 exe 走**同一份前置與同一個本體**，
//             不會出現「用哪個入口啟動，行為不一樣」那種要靠現場才問得出來的差異。
// 數值影響：exit code 原樣轉出（0 正常退出／1 已有 Server 或拿不到單例鎖／70 登記不了）。
using Senate.Core;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string aRepoRoot = ServerBootstrap.FindRepoRoot();
return ServerBootstrap.Run(aRepoRoot, Console.WriteLine, Console.Error.WriteLine);
