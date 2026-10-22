// 區塊職責：`task-sync` —— 任務單跟遠端 repo（`sync.tasks`）的狀態與手動同步。
// 物理意義：平常不用打它：寫單時寫入端自己 commit＋push、Server 背景工作定時拉。
//           它給兩種時候用：① 想看現在跟遠端差多少（status）；② 背景工作推不上去、說「撞了」的時候，
//           由有人在場的這一支來**重放**（sync）—— 重放的結果（改號、衝突）要有人看。
// 數值影響：status 不帶 fetch ⇒ 純讀本機；帶 fetch=1 ⇒ 抓一次遠端。sync ⇒ clone／拉／推（可能重放）。
// ⚠ 跟 `task-write` 同一條 lane：同步與寫單不會同時動同一顆 repo。
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_TaskSync : ServerDelegateCmd
{
    public override string Name => "task-sync";
    public override string Category => SCP_CmdCategory.Task;

    public override string Summary =>
        "任務單跟遠端 repo（senate.local.json 的 sync.tasks）同步：status 看差多少／sync 手動拉推（撞了會重放）";

    public override string Details =>
        "平常不用打：寫單時寫入端自己 commit＋push，Server 背景工作每分鐘拉一次。\n"
        + "⚠ 背景工作推不上去又撞了 ⇒ 它不重放（重放的結果要有人看）⇒ 打 `op=sync` 由這一支重放。\n"
        + "⚠ 解不了的衝突：我的版本留在備份 ref（refs/senate/task-sync/…），報告在 Tasks 的 .git/senate-task-sync/ 底下。";

    public override string Example => SCP_CmdRegistry.Invoke("task-sync --arg op=status --arg fetch=1");

    protected override string Lane(SCP_CmdArgs iArgs) => Cmd_TaskWrite.TaskLane;
    protected override bool IsPersonaLane(string iLane) => false;

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("op", "status（預設）｜sync", iDefault: "status", iChoices: new[] { "status", "sync" }),
                new SCP_CmdArgSpec("data_root", "資料根（絕對路徑）；不給 ⇒ 照設定檔解析"),
                new SCP_CmdArgSpec("fetch", "status 用：1 ⇒ 先抓一次遠端（走網路）"),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        if (SenateConfigSource.Provider == null)
            return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上設定來源（SenateConfigSource.Provider）—— 程式錯誤，不是用法錯");
        (SenateConfig? aConfig, string aConfigPath) = SenateConfigSource.Provider();
        string aDataRoot = iArgs.Get("data_root").Trim();
        if (aDataRoot.Length == 0)
        {
            string? aRoot = SenatePathBinding.ResolveDataRoot(aConfig, out string? aErr);
            if (aRoot == null) return SCP_CmdResult.Fail(2, "✗ 資料根解不出來：" + aErr);
            aDataRoot = aRoot;
        }
        aDataRoot = aDataRoot.Replace('\\', '/').TrimEnd('/');
        TaskRepoTarget? aTarget = TaskRepoTarget.From(aConfig);

        var r = new SCP_CmdResult();
        r.Lines.Add($"⤷ 任務同步：Senate Server（{ServerContext.Describe()}）@ {SenateTaskRepoSync.TasksDir(aDataRoot)}");
        r.Lines.Add(aTarget == null
            ? $"· 遠端：**沒設定**（{aConfigPath} 的 sync.tasks 兩格都填才同步）"
            : $"· 遠端：`{aTarget.Repo}`（{aTarget.Branch}）");

        if (iArgs.Get("op").Trim() == "sync")
        {
            if (aTarget == null) return r.AddValue("sync", "off");
            TaskSyncReport s = SenateTaskRepoSync.Sync(aDataRoot, aTarget, iAllowReplay: true);
            foreach (string l in s.Lines) r.Lines.Add("- " + l);
            foreach (var n in s.Notices) r.Lines.Add($"📣（要讓大家知道）{n.TaskId} {n.Kind}：{n.Body}");
            if (s.State is TaskSyncState.Conflict or TaskSyncState.Failed or TaskSyncState.Mismatch) r.ExitCode = 1;
            if (s.ConflictFile.Length > 0) r.AddValue("sync_conflict_file", s.ConflictFile);
            return r.AddValue("sync", s.StateWord);
        }

        TaskRepoStatus st = SenateTaskRepoSync.Status(aDataRoot, aTarget, iArgs.Get("fetch").Trim() == "1");
        r.Lines.Add($"· 本機：{(st.Exists ? (st.IsRepo ? $"git repo，origin=`{st.Origin}`，branch=`{st.Branch}`" : "存在，不是 git repo") : "不存在")}");
        if (st.IsRepo) r.Lines.Add($"· 領先 {st.Ahead}／落後 {st.Behind}／沒提交的改動 {st.Dirty}（落後的數字是上一次抓遠端時的；要最新的帶 fetch=1）");
        r.Lines.Add("⇒ " + st.Note);
        return r.AddValue("ahead", st.Ahead.ToString()).AddValue("behind", st.Behind.ToString()).AddValue("dirty", st.Dirty.ToString());
    }
}
