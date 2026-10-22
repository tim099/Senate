// 區塊職責：任務單同步到遠端 repo（sync.tasks）的淨室 —— 本機 bare repo 當遠端、兩棵資料樹當兩台機器。
// 物理意義：Tim 2026-10-22 要的幾格逐一走過：缺目錄自動 clone、寫完自動 commit＋push、
//           **兩邊同時開單撞號 ⇒ 自動改號**、同一張同時留言 ⇒ 兩則都在、
//           兩邊同時整段改同一張 ⇒ 不自動解（擋下＋衝突通知＋備份 ref、遠端那份不被蓋掉）；
//           反向對照：沒設遠端 ⇒ 照舊只寫本機；設了遠端卻 clone 不下來 ⇒ 不長出空任務板。
//           「同時」用 `iPreSync=false` 模擬：B 還沒看到 A 剛推上去的那一筆就寫了。
//           2026-10-22 git 搬到 CLI 那一邊之後多驗五格：⑨ Server 那一側只寫檔、下一次 CLI 同步收成一筆推上去；
//           ⑩ Server 那一側沒 clone 照樣擋；⑪ 讀單前的同步有節流；⑫ 鎖被別人拿著 ⇒ 這次不同步；⑬ 入口 `task` 在本行程寫＋推。
#nullable enable
using SCP.Core.Git;
using SCP.Core.Paths;
using SCP.Core.Tasks;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TaskRepoSyncCleanRoom()
    {
        const string aName = "任務單同步到遠端 repo：clone／commit＋push／撞號自動改號／解不了的擋下（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_tasksync_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFail = new List<string>();
            void Expect(bool c, string what) { if (!c) aFail.Add(what); }
            SCP_GitResult G(string dir, params string[] a) => SCP_Git.Run(dir, a);
            string[] Id = { "-c", "user.name=selftest", "-c", "user.email=selftest@localhost" };

            // ── 遠端：bare repo，種一份跟 tim099/Tasks 同形的空板（.gitignore ＋ _index.txt）──
            Directory.CreateDirectory(aTmp);
            string aRemote = Path.Combine(aTmp, "remote.git").Replace('\\', '/');
            G(aTmp, "init", "--bare", "-b", "main", aRemote);
            string aSeed = Path.Combine(aTmp, "seed");
            G(aTmp, "clone", aRemote, aSeed);
            // ⚠ CRLF ＋ 空行：tim099/Tasks 的 .gitignore 就是這個形狀，而它會讓 `git check-ignore Tasks/` 在外層 repo 回錯的「已忽略」（2026-10-22 實撞）
            File.WriteAllText(Path.Combine(aSeed, ".gitignore"), "# 任務單\r\n_last_task_report.md\r\n\r\n_index.txt.lock\r\n");
            File.WriteAllText(Path.Combine(aSeed, "_index.txt"), "0");
            G(aSeed, "add", "-A");
            G(aSeed, Id.Concat(new[] { "commit", "-q", "-m", "seed" }).ToArray());
            Expect(G(aSeed, "push", "-q", "origin", "HEAD:refs/heads/main").Ok, "種遠端失敗");

            string Root(string n) { string p = Path.Combine(aTmp, n).Replace('\\', '/'); Directory.CreateDirectory(Path.Combine(p, "ChatTavern")); return p; }
            string A = Root("A"), B = Root("B");
            G(A, "init", "-q", ".");   // A 的資料樹本身是 git repo（像 Valhalla）；B 不是 ⇒ 兩種都走過
            var aTarget = new TaskRepoTarget(aRemote, "main");
            (SCP_TaskOpResult R, TaskSyncReport S) W(string root, bool pre, string op, params (string k, string v)[] kv)
                => SenateTaskRepoSync.RunWrite(root, aTarget, op, "Template", kv.ToDictionary(x => x.k, x => x.v), pre);
            string RemoteFile(int i) { var r = G(aSeed, "--git-dir=" + aRemote, "show", $"main:tasks/{i:0000}.md"); return r.Ok ? r.StdOut : ""; }
            string Title(int i) { foreach (string l in RemoteFile(i).Split('\n')) if (l.StartsWith("title: ")) return l.Substring(7).Trim(); return ""; }

            // ① A 第一次用到 ⇒ 自動 clone；開單 ⇒ commit＋push
            var a1 = W(A, true, "create", ("title", "A1"), ("criteria", "- [ ] a"));
            Expect(a1.R.Index == 1 && a1.S.State == TaskSyncState.Pushed, $"① A 開單：index={a1.R.Index} sync={a1.S.StateWord}");
            Expect(Directory.Exists(Path.Combine(A, "Tasks", ".git")), "① A 沒有 clone 出 Tasks/.git");
            Expect(Title(1) == "A1", $"① 遠端沒有 0001=A1（{Title(1)}）");
            Expect(!G(A, "status", "--porcelain").OutLines().Any(l => l.Contains("Tasks/")),
                $"① A 的資料樹把 Tasks/ 列成未追蹤（巢狀 repo 沒排除）：{string.Join(" / ", G(A, "status", "--porcelain").OutLines())}");

            // ② B 第一次用到 ⇒ clone 下來看得到 0001 ⇒ 開出 0002
            var b1 = W(B, true, "create", ("title", "B1"), ("criteria", "- [ ] b"));
            Expect(b1.R.Index == 2 && b1.S.State == TaskSyncState.Pushed, $"② B 開單：index={b1.R.Index} sync={b1.S.StateWord}");

            // ③ 兩邊同時開單：A 先推 0003；B 還沒看到就也配了 0003 ⇒ 推被拒 ⇒ 重放 ⇒ 0004
            var a2 = W(A, true, "create", ("title", "A2"), ("criteria", "- [ ] a"));
            var b2 = W(B, false, "create", ("title", "B2"), ("criteria", "- [ ] b"));
            Expect(a2.R.Index == 3, $"③ A2 應是 0003（{a2.R.Index}）");
            Expect(b2.R.Index == 4 && b2.R.ExitCode == 0 && b2.S.State == TaskSyncState.Pushed,
                $"③ B2 撞號沒自動改到 0004：index={b2.R.Index} exit={b2.R.ExitCode} sync={b2.S.StateWord}｜{string.Join(" / ", b2.S.Lines)}");
            Expect(Title(3) == "A2" && Title(4) == "B2", $"③ 遠端 0003/0004 應是 A2/B2（{Title(3)}/{Title(4)}）");
            Expect(b2.S.Lines.Any(l => l.Contains("🔀")), "③ 回報沒說改號");
            Expect(b2.R.Notices.Any(n => (n.TaskId + n.Body).Contains("0004")), "③ 開單通知不是新號 0004（入口會公告錯的號碼）");

            // ④ 同一張同時留言：兩則都要在
            var a3 = W(A, true, "comment", ("index", "1"), ("body", "A 的留言"));
            var b3 = W(B, false, "comment", ("index", "1"), ("body", "B 的留言"));
            Expect(a3.S.State == TaskSyncState.Pushed && b3.S.State == TaskSyncState.Pushed && b3.R.ExitCode == 0,
                $"④ 同時留言：A={a3.S.StateWord} B={b3.S.StateWord} exit={b3.R.ExitCode}｜{string.Join(" / ", b3.S.Lines)}");
            Expect(RemoteFile(1).Contains("A 的留言") && RemoteFile(1).Contains("B 的留言"), "④ 遠端 0001 沒有兩則留言");

            // ⑤ 不同張同時改 ⇒ rebase 就接得上（不用重放）
            var a4 = W(A, true, "comment", ("index", "3"), ("body", "A 在 3"));
            var b4 = W(B, false, "comment", ("index", "4"), ("body", "B 在 4"));
            Expect(b4.S.State == TaskSyncState.Pushed && b4.S.Lines.Any(l => l.Contains("rebase 接上")),
                $"⑤ 不同張應 rebase 接上：{b4.S.StateWord}｜{string.Join(" / ", b4.S.Lines)}");
            Expect(RemoteFile(3).Contains("A 在 3") && RemoteFile(4).Contains("B 在 4"), "⑤ 遠端少了一邊的留言");

            // ⑥ 兩邊同時整段改同一張的標題 ⇒ 不自動解：B 擋下、零寫入、發 sync_conflict、遠端保留 A 的、B 的版本進備份 ref
            var a5 = W(A, true, "update", ("index", "2"), ("title", "A 改的標題"));
            var b5 = W(B, false, "update", ("index", "2"), ("title", "B 改的標題"));
            Expect(a5.S.State == TaskSyncState.Pushed, $"⑥ A 改標題沒推上去（{a5.S.StateWord}）");
            Expect(b5.R.ExitCode == 1 && b5.R.Wrote == 0 && b5.S.State == TaskSyncState.Conflict,
                $"⑥ B 應擋下：exit={b5.R.ExitCode} wrote={b5.R.Wrote} sync={b5.S.StateWord}｜{string.Join(" / ", b5.S.Lines)}");
            Expect(b5.S.Notices.Any(n => n.Kind == "sync_conflict"), "⑥ 沒有 sync_conflict 通知");
            Expect(b5.S.ConflictFile.Length > 0 && File.Exists(b5.S.ConflictFile), "⑥ 衝突報告沒寫出來");
            Expect(Title(2) == "A 改的標題", $"⑥ 遠端的標題被蓋掉了（{Title(2)}）");
            string aBTasks = Path.Combine(B, "Tasks");
            Expect(G(aBTasks, "for-each-ref", "refs/senate/task-sync/").OutLines().Count > 0, "⑥ B 沒有備份 ref");
            Expect(SCP_TaskIO.Find(new SCP_DataRoot(B), 2)?.title == "A 改的標題", "⑥ B 的共用板沒退回遠端版");

            // ⑦ 反向對照：沒設遠端 ⇒ 照舊只寫本機（沒有 .git、State=Off）
            string C = Root("C");
            var c1 = SenateTaskRepoSync.RunWrite(C, null, "create", "Template",
                new Dictionary<string, string> { ["title"] = "C1", ["criteria"] = "- [ ] c" });
            Expect(c1.Result.Index == 1 && c1.Sync.State == TaskSyncState.Off && !Directory.Exists(Path.Combine(C, "Tasks", ".git")),
                $"⑦ 沒設遠端應只寫本機：index={c1.Result.Index} sync={c1.Sync.StateWord}");

            // ⑧ 反向對照：設了遠端卻 clone 不下來 ⇒ 不寫、不長出空的 Tasks/
            string D = Root("D");
            var d1 = SenateTaskRepoSync.RunWrite(D, new TaskRepoTarget(Path.Combine(aTmp, "沒有這個.git"), "main"), "create", "Template",
                new Dictionary<string, string> { ["title"] = "D1", ["criteria"] = "- [ ] d" });
            Expect(d1.Result.ExitCode == 1 && !Directory.Exists(Path.Combine(D, "Tasks")),
                $"⑧ clone 不下來應擋下且不建目錄：exit={d1.Result.ExitCode} 有Tasks={Directory.Exists(Path.Combine(D, "Tasks"))}");

            // ── 2026-10-22 起 git 只在 CLI 那一邊（Server 跑 git 會被 Defender 砍，Repo_Sync §0）──
            int FindTitle(string t) { for (int i = 1; i <= 20; i++) if (Title(i) == t) return i; return 0; }

            // ⑨ Server 那一側（WriteWithoutGit）：只寫檔、不提交、回 pending；下一次 CLI 同步收成一筆推上去
            var s1 = SenateTaskRepoSync.WriteWithoutGit(A, "create", "Template", new Dictionary<string, string> { ["title"] = "Server 寫的", ["criteria"] = "- [ ] s" });
            Expect(s1.Result.ExitCode == 0 && s1.Result.Wrote > 0 && s1.Sync.State == TaskSyncState.Pending,
                $"⑨ Server 那一側應只寫檔、回 pending：exit={s1.Result.ExitCode} sync={s1.Sync.StateWord}");
            Expect(G(Path.Combine(A, "Tasks"), "status", "--porcelain").OutLines().Count > 0 && FindTitle("Server 寫的") == 0,
                "⑨ Server 那一側不該提交／推（工作區應該是髒的、遠端還沒有）");
            var s2 = SenateTaskRepoSync.Sync(A, aTarget, iAllowReplay: false);
            Expect(s2.State == TaskSyncState.Pushed && FindTitle("Server 寫的") > 0, $"⑨ 下一次 CLI 同步沒把它推上去：{s2.StateWord}｜{string.Join(" / ", s2.Lines)}");

            // ⑩ Server 那一側：沒 clone 下來照樣擋（不長空板）
            var s3 = SenateTaskRepoSync.WriteWithoutGit(D, "create", "Template", new Dictionary<string, string> { ["title"] = "D2", ["criteria"] = "- [ ] d" });
            Expect(s3.Result.ExitCode == 1 && !Directory.Exists(Path.Combine(D, "Tasks")), $"⑩ Server 那一側沒 clone 應擋下：exit={s3.Result.ExitCode}");

            // ⑪ 讀單前的同步有節流：B 剛抓過 ⇒ 不抓（看不到 A 剛開的）；不節流 ⇒ 看得到
            SenateTaskRepoSync.Sync(B, aTarget, iAllowReplay: false);
            var a6 = W(A, true, "create", ("title", "A6"), ("criteria", "- [ ] a"));
            var t1 = SenateTaskRepoSync.Sync(B, aTarget, iAllowReplay: false, iPullEverySec: SenateTaskRepoSync.PullEverySec);
            Expect(t1.State == TaskSyncState.UpToDate && SCP_TaskIO.Find(new SCP_DataRoot(B), a6.R.Index) == null, $"⑪ 節流內不該抓：{t1.StateWord}｜{string.Join(" / ", t1.Lines)}");
            SenateTaskRepoSync.Sync(B, aTarget, iAllowReplay: false);
            Expect(SCP_TaskIO.Find(new SCP_DataRoot(B), a6.R.Index)?.title == "A6", "⑪ 不節流應拉到 A6");

            // ⑫ 任務板的鎖被別人拿著 ⇒ 等不到就這次不同步（pending），⛔ 不卡住
            using (var aHeld = new ManualResetEventSlim())
            using (var aRelease = new ManualResetEventSlim())
            {
                var aHolder = new Thread(() =>
                {
                    using (SenateTaskRepoSync.TaskLock.TryAcquire(A, 5)) { aHeld.Set(); aRelease.Wait(15_000); }
                });
                aHolder.Start();
                aHeld.Wait(10_000);
                var t2 = SenateTaskRepoSync.Sync(A, aTarget, iAllowReplay: false, iLockWaitSec: 1);
                aRelease.Set();
                aHolder.Join();
                Expect(t2.State == TaskSyncState.Pending && t2.Lines.Any(l => l.Contains("正被另一個")), $"⑫ 鎖被拿著應回 pending：{t2.StateWord}｜{string.Join(" / ", t2.Lines)}");
            }

            // ⑬ 入口 `task` 全程（sync.tasks 開著 ⇒ 在本行程寫＋推，不交給 Server）—— 設定換成淨室這一份、酒館閘拿掉（不發真的通知）
            var aSavedProvider = SenateConfigSource.Provider;
            var aSavedGate = SCP.Core.Letters.SCP_TavernPostGatewayHost.Factory;
            try
            {
                var aCfg = new SenateConfig();
                aCfg.Sync.Tasks.Repo = aRemote;
                aCfg.Sync.Tasks.Branch = "main";
                SenateConfigSource.Provider = () => (aCfg, "(selftest)");
                SCP.Core.Letters.SCP_TavernPostGatewayHost.Factory = null;
                var e2e = SCP.Core.Cmd.SCP_CmdRegistry.Dispatch("task", new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["op"] = "create", ["persona"] = "Template", ["data_root"] = A, ["title"] = "經入口開的單", ["criteria"] = "- [ ] e",
                });
                string V(string k) => e2e.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";
                Expect(e2e.ExitCode == 0 && V("sync") == "pushed" && e2e.Lines.Any(l => l.Contains("本機 CLI")),
                    $"⑬ 入口應在本行程寫＋推：exit={e2e.ExitCode} sync={V("sync")}｜{string.Join(" / ", e2e.Lines.Take(6))}");
                Expect(FindTitle("經入口開的單") > 0, "⑬ 遠端沒有經入口開的那張");
            }
            finally
            {
                SenateConfigSource.Provider = aSavedProvider;
                SCP.Core.Letters.SCP_TavernPostGatewayHost.Factory = aSavedGate;
            }

            return new CheckRow(aName,
                aFail.Count == 0 ? "clone／commit＋push／撞號改號 0003→0004／同張留言兩則都在／不同張 rebase／同張整段改擋下＋通知＋備份／沒設遠端只寫本機／clone 不下來不長空板"
                                   + "／Server 那一側只寫檔、下次 CLI 同步推上去／Server 那一側沒 clone 擋下／讀單前節流／鎖被拿著回 pending／入口在本行程寫＋推 全對"
                                 : string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "炸了：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { DeleteTreeForce(aTmp); }
    }

    /// <summary>刪暫存樹 —— git 的物件檔是唯讀的，`Directory.Delete` 直接刪會被擋（Windows）。</summary>
    static void DeleteTreeForce(string iDir)
    {
        try
        {
            if (!Directory.Exists(iDir)) return;
            foreach (string f in Directory.GetFiles(iDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(iDir, true);
        }
        catch { }
    }
}
