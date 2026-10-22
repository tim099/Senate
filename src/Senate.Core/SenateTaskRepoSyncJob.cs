// 區塊職責：任務單同步的**常駐工作** —— 掛在主 Server（任務寫入端那一顆），定時把本機那一份跟遠端對齊。
// 物理意義：讀的人（`tasks`、後台頁）讀的是本機那一份 ⇒ 要有人定時拉，別台開的單才看得到；
//           缺目錄 ⇒ 第一圈就 clone（「沒有 Tasks 資料夾時自動補上」）；有推不上去的 ⇒ 再推。
//           ⛔ 不重放：重放會改號、會發衝突通知，那要有人在場 —— 撞了只說一聲，交給下一次寫單或 `task-sync op=sync`。
// 數值影響：每 IntervalSec 秒一次 fetch（網路在背景執行緒，⛔ 不擋心跳）；同一個訊息只印一次。
#nullable enable

namespace Senate.Core;

public static class SenateTaskRepoSyncJob
{
    public const int IntervalSec = 60;

    static Task? s_Running;
    static DateTime s_NextUtc = DateTime.MinValue;
    static string s_LastNote = "";

    public static bool IsRunning => s_Running is { IsCompleted: false };

    public static void Tick(string iDataRoot, Action<string> iOut, Action<string> iErr)
    {
        DateTime aNow = DateTime.UtcNow;
        if (aNow < s_NextUtc || IsRunning) return;
        s_NextUtc = aNow.AddSeconds(IntervalSec);

        TaskRepoTarget? aTarget = TaskRepoTarget.From(SenateConfigSource.Provider?.Invoke().Config);   // 每一圈重讀：後台改完設定下一圈就生效
        if (aTarget == null) return;

        s_Running = Task.Run(() =>
        {
            try
            {
                TaskSyncReport r = SenateTaskRepoSync.Sync(iDataRoot, aTarget, iAllowReplay: false);
                if (r.State is TaskSyncState.UpToDate) s_LastNote = "";
                foreach (string l in r.Lines)
                {
                    bool aBad = l.StartsWith("⚠", StringComparison.Ordinal) || l.StartsWith("✗", StringComparison.Ordinal);
                    if (!aBad) { iOut("· 🔄 任務同步：" + l); continue; }
                    if (l == s_LastNote) continue;
                    s_LastNote = l;
                    iErr("⚠ 任務同步：" + l);
                }
            }
            catch (Exception e) { iErr($"⚠ 任務同步這一圈炸了（{e.GetType().Name}：{e.Message}）—— 下一圈再試"); }
        });
    }
}
