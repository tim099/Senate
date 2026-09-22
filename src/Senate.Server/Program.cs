// 區塊職責：`Senate.Server.exe` 的入口 —— 薄到只剩「找 repo 根、解析 serverId、交給 ServerBootstrap」。
// 物理意義：常駐邏輯一行都不在這裡（在 Senate.Core/ServerBootstrap ＋ ServerHost）。
//           ⇒ `senate server start` 與這顆 exe 走**同一份前置與同一個本體**，
//             不會出現「用哪個入口啟動，行為不一樣」那種要靠現場才問得出來的差異。
// 數值影響：exit code 原樣轉出（0 正常退出／1 已有 Server 或拿不到單例鎖／2 serverId 不合法／70 登記不了）。
//
// 🔴 TASK-0244：`--id <serverId>` 一定要在這裡解析，⛔ 不能只在 CLI 那側解析。
//   🩸 理由是 autostart 的實際形狀：`ServerAutoStart.TrySpawn` **優先起旁邊那顆 `senate-server.exe`**，
//     而它照樣把 `server start …` 那整串參數傳過來（舊版這顆 exe 整串忽略，
//     靠的是「反正沒有參數要看」）。⇒ 一旦有了 serverId，「整串忽略」就變成**靜默起錯一顆**：
//     呼叫端要的是 `tavern`，起來的是 `main`；而 `main` 通常已經在跑
//     ⇒ 這顆拿不到單例鎖自退 ⇒ 呼叫端等到 `autostart_timeout`，
//     而 log 裡寫的是「拿不到單例鎖」—— **兩句話指不到彼此**。
using Senate.Core;
using SCP.Core.Proc;

Console.OutputEncoding = System.Text.Encoding.UTF8;

string aServerId = SCP_ServerIds.Default;
for (int i = 0; i < args.Length; i++)
{
    if (!string.Equals(args[i], "--id", StringComparison.Ordinal)) continue;
    if (i + 1 >= args.Length)
    {
        Console.Error.WriteLine("✗ --id 後面沒有值 —— ⛔ 不猜一個預設值（猜錯的樣子是起錯一顆 Server）。");
        return 2;
    }
    aServerId = args[i + 1];
    break;
}
// 環境變數只在沒給 `--id` 時才看：命令列是顯式的，⛔ 不讓環境悄悄蓋掉人打出來的字。
if (string.Equals(aServerId, SCP_ServerIds.Default, StringComparison.Ordinal)
    && Environment.GetEnvironmentVariable("SENATE_SERVER_ID") is { Length: > 0 } aEnvId)
    aServerId = aEnvId;

string aRepoRoot = ServerBootstrap.FindRepoRoot();
return ServerBootstrap.Run(aRepoRoot, aServerId, Console.WriteLine, Console.Error.WriteLine);
