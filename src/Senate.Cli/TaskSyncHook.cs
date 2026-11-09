// 區塊職責：`senate cmd tasks`（讀單）之前的任務單同步（sync.tasks）—— 每分鐘最多拉一次，順便推還沒推上去的（含 Server 只寫檔沒提交的）。
// 物理意義：原本是主 Server 的背景工作每分鐘拉一次；2026-10-22 起 Server 不跑 git（經 WMI 起的 Server 跑 git 會被 Defender 砍，
//           Repo_Sync §0）⇒ 改成「有人要讀的時候才拉」，在 CLI 這一邊。寫單本來就先拉（RunWrite），這裡只管讀。
// 數值影響：沒設遠端 ⇒ 整段不存在（一行都不印）。節流內 ⇒ 只多開一次鎖；任務板被別人寫著 ⇒ 最多等 5 秒，等不到就照本機讀（⛔ 不擋讀單）。
//           ⛔ 不重放（重放會改號、發通知，要有人明說：`task-sync op=sync`）。過程印在 stderr。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

static class TaskSyncHook
{
    const int ReadLockWaitSec = 5;

    public static void BeforeRead(string iRepoRoot, string iCmdName)
    {
        if (!string.Equals(iCmdName, "tasks", StringComparison.OrdinalIgnoreCase)) return;
        SenateConfig? aCfg;
        try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot)); }
        catch (Exception) { return; }   // 設定檔壞掉由 CmdScp 自己那一格擋、自己說
        TaskRepoTarget? aTarget = TaskRepoTarget.From(aCfg);
        if (aCfg == null || aTarget == null) return;
        string? aRoot = SenatePathBinding.ResolveDataRoot(aCfg, out string? aErr);
        if (aRoot == null)
        {
            Console.Error.WriteLine($"⚠ 🔄 Tasks 同步：資料根解不出來（{aErr}）⇒ 這次不同步");
            return;
        }
        TaskSyncReport r;
        try
        {
            r = SenateTaskRepoSync.Sync(aRoot.Replace('\\', '/').TrimEnd('/'), aTarget, iAllowReplay: false,
                                        iPullEverySec: SenateTaskRepoSync.PullEverySec, iLockWaitSec: ReadLockWaitSec);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"⚠ 🔄 Tasks 同步（讀單之前）炸了：{e.GetType().Name}: {e.Message} —— 照本機的版本讀");
            return;
        }
        foreach (string l in r.Lines) Console.Error.WriteLine("🔄 Tasks 同步：" + l);
    }
}
