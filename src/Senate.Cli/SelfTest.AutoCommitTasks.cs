// 區塊職責：自動 commit 的 `tasks` 群（Tim 2026-11-09：任務板「本機版 但是住在D:\Senate\Valhalla的repo內 可以同步」）淨室。
// 物理意義：拿**真的寫入端**（SCP_TaskOps）在暫存資料根開單、留言、打勾，再把它寫出來的每一個檔丟給分群規則 ——
//           寫入端多寫了一種檔而規則沒跟上，症狀是那個檔靜默落 __other（永不自動收）；這支在那之前就叫。
//           反向：附件（有作者）一定落 __other，⛔ 不能被當機器檔收走。
// ⚠ `*.lock` 不算：資料根的 .gitignore 擋著（跨行程鎖，不進版控）。
#nullable enable
using SCP.Core.Git;
using SCP.Core.Tasks;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow AutoCommitTaskBoardGroup()
    {
        const string aName = "自動 commit 的 tasks 群（淨室；寫入端寫的檔全中、附件不中）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_actasks_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(aTmp);
            SCP_TaskOpResult Run(string op, params (string k, string v)[] kv)
                => SCP_TaskOps.Run(aRoot, op, "Template", kv.ToDictionary(x => x.k, x => x.v));
            string Group(string p) => SCP_AutoCommitRules.Classify(p, SCP_AutoCommitRules.AgentGroupDefs, false) ?? "(ephemeral)";
            var aFail = new List<string>();
            void Expect(bool c, string what) { if (!c) aFail.Add(what); }

            // ① 寫入端真的寫一輪：開兩張、留言、打勾
            Expect(Run("create", ("title", "甲"), ("criteria", "- [ ] a\n- [ ] b")).Index == 1, "create 沒拿到 1 號");
            Expect(Run("create", ("title", "乙"), ("criteria", "- [ ] a")).Index == 2, "create 沒拿到 2 號");
            Expect(Run("comment", ("index", "1"), ("body", "留言")).Wrote == 1, "comment 沒寫");
            Expect(Run("check", ("index", "1"), ("criteria_index", "1"), ("expect_text", "a")).Wrote == 1, "check 沒寫");

            // ② 它寫出來的每一個檔（扣掉 *.lock）都要落 tasks 群
            string aBase = aRoot.Value.Replace('\\', '/').TrimEnd('/') + "/";
            var aWritten = Directory.GetFiles(Path.Combine(aTmp, SCP_TaskIO.TasksDirName), "*", SearchOption.AllDirectories)
                .Select(f => f.Replace('\\', '/'))
                .Select(f => f.StartsWith(aBase, StringComparison.OrdinalIgnoreCase) ? f.Substring(aBase.Length) : f)
                .Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
                .OrderBy(f => f, StringComparer.Ordinal).ToList();
            Expect(aWritten.Contains("Tasks/_index.txt"), "寫入端沒寫 Tasks/_index.txt（量的路徑不對？）：" + string.Join("、", aWritten));
            Expect(aWritten.Contains("Tasks/tasks/0001.md") && aWritten.Contains("Tasks/tasks/0002.md"), "寫入端沒寫兩張單檔：" + string.Join("、", aWritten));
            foreach (string f in aWritten)
                Expect(Group(f) == "tasks", $"寫入端寫的 `{f}` 落 {Group(f)}，不是 tasks");

            // ③ 反向對照：附件、子目錄、不是 .md 的 ⇒ __other（有作者的東西不准被當機器檔收）
            foreach (string f in new[]
                     {
                         "Tasks/attachments/TASK-0001/README.md",
                         "Tasks/attachments/TASK-0001/tools/rec.py",
                         "Tasks/tasks/sub/0001.md",
                         "Tasks/tasks/0001.md.bak",
                         "Tasks/notes.md",
                     })
                Expect(Group(f) == SCP_AutoCommitRules.KeyOther, $"`{f}` 落 {Group(f)}，應該是 __other");

            return new CheckRow(aName,
                aFail.Count == 0 ? $"寫入端寫的 {aWritten.Count} 個檔全落 tasks；附件／子目錄／非 .md 5 條全落 __other" : string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "炸了：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }
}
