// 區塊職責：任務的**專案**（TASK-0618）淨室 —— 寫入端的閘、讀的時候算的「跟著母單」、`cmd tasks` 的篩選。
// 物理意義：驗收條文①～④、⑧ 的那幾格；擋下＝零位元組（對單檔做前後位元組比對，不信回傳值）。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Tasks;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TaskProjectCleanRoom()
    {
        const string aName = "任務的專案（淨室；TASK-0618）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_taskproject_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(aTmp);
            SCP_TaskOpResult Run(string op, params (string k, string v)[] kv)
                => SCP_TaskOps.Run(aRoot, op, "Template", kv.ToDictionary(x => x.k, x => x.v));
            string Bytes(int i) { string p = SCP_TaskIO.TaskPath(aRoot, i); return File.Exists(p) ? File.ReadAllText(p) : "(不存在)"; }
            var aFail = new List<string>();
            void Expect(bool c, string what) { if (!c) aFail.Add(what); }

            // ① create 帶 project ⇒ 落一行；不帶 ⇒ 那一行不落（舊單零 diff 的前提）
            Expect(Run("create", ("title", "母單"), ("type", "epic"), ("criteria", "- [ ] a"), ("project", "Blockside")).Index == 1, "create 母單沒拿到 1 號");
            Expect(Run("create", ("title", "子單"), ("criteria", "- [ ] a"), ("epic_id", "TASK-0001")).Index == 2, "create 子單沒拿到 2 號");
            Expect(Run("create", ("title", "別的"), ("criteria", "- [ ] a")).Index == 3, "create 3 沒拿到 3 號");
            Expect(Bytes(1).Contains("\nproject: Blockside\n", StringComparison.Ordinal), "母單沒落 project 那一行");
            Expect(!Bytes(3).Contains("\nproject:", StringComparison.Ordinal), "沒標專案的單落了空的 project 行");
            Expect(SCP_TaskIO.Find(aRoot, 1)!.project == "Blockside", "project 讀回不是 Blockside");

            // ② 只差大小寫 ⇒ 擋、零位元組；保留字 ⇒ 擋；一模一樣的拼法不算撞
            string b3 = Bytes(3);
            Expect(Run("update", ("index", "3"), ("project", "blockside")).ExitCode == 1 && Bytes(3) == b3, "blockside 沒擋或寫了");
            Expect(Run("update", ("index", "3"), ("project", "none")).ExitCode == 1 && Bytes(3) == b3, "保留字 none 沒擋");
            Expect(Run("create", ("title", "撞"), ("criteria", "- [ ] a"), ("project", "BLOCKSIDE")).ExitCode == 1
                && SCP_TaskIO.Find(aRoot, 4) == null, "create 撞名沒擋或吃了號");
            Expect(Run("update", ("index", "3"), ("project", "Senate")).Wrote == 1, "反向對照：新專案名 Senate 沒寫進去");

            // 同一個值 ⇒ 不算變更；project 跟 unset=project 同時給 ⇒ 擋；unset ⇒ 那一行消失
            b3 = Bytes(3);
            Expect(Run("update", ("index", "3"), ("project", "Senate")).Wrote == 0 && Bytes(3) == b3, "同一個專案又寫了一次");
            Expect(Run("update", ("index", "3"), ("project", "X"), ("unset", "project")).ExitCode == 1 && Bytes(3) == b3, "project＋unset 沒擋");
            Expect(Run("update", ("index", "3"), ("unset", "project")).Wrote == 1 && !Bytes(3).Contains("\nproject:", StringComparison.Ordinal), "unset=project 沒清掉");
            Expect(SCP_TaskStore.ReadTimeline(SCP_TaskIO.TaskPath(aRoot, 3)).Any(l => l.Contains("project 清空", StringComparison.Ordinal)), "unset 沒進時間線");

            // ③ 跟著母單：2 沒標 ⇒ (Blockside, 1)；自己標了就用自己的；成環不卡死
            List<SCP_TaskEntry> aAll = SCP_TaskIO.LoadAll(aRoot);
            var aEff = SCP_TaskProjects.Effective(aAll);
            Expect(aEff[2] == ("Blockside", 1), $"子單沒跟著母單（{aEff[2]}）");
            Expect(aEff[1] == ("Blockside", 0) && aEff[3] == ("", 0), "母單／未分類的有效專案不對");
            Run("update", ("index", "2"), ("project", "Sculpture"));
            Expect(SCP_TaskProjects.Effective(SCP_TaskIO.LoadAll(aRoot))[2] == ("Sculpture", 0), "子單自己標了還在跟母單");
            var aLoopA = new SCP_TaskEntry { index = 10, epic_id = "11" };
            var aLoopB = new SCP_TaskEntry { index = 11, epic_id = "10" };
            Expect(SCP_TaskProjects.Effective(new List<SCP_TaskEntry> { aLoopA, aLoopB })[10] == ("", 0), "成環的母單鏈沒停");

            // ④ cmd tasks：project 篩選、none、認不得 ⇒ exit 2（⛔ 不回 0 張）
            Run("update", ("index", "2"), ("unset", "project"));
            int Listed(params (string k, string v)[] kv)
            {
                var a = new Dictionary<string, string> { ["data_root"] = aTmp };
                foreach (var (k, v) in kv) a[k] = v;
                SCP_CmdResult r = SCP_CmdRegistry.Dispatch("tasks", a);
                if (r.ExitCode != 0) return -r.ExitCode;
                return int.Parse(r.Values.First(x => x.Key == "listed_count").Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            Expect(Listed(("project", "Blockside")) == 2, $"project=Blockside 應列 2 張（母單＋跟著它的子單）：{Listed(("project", "Blockside"))}");
            Expect(Listed(("project", "blockside")) == 2, "project 篩選沒有不分大小寫");
            Expect(Listed(("project", "none")) == 1, $"project=none 應列 1 張：{Listed(("project", "none"))}");
            Expect(Listed(("project", "沒這個")) == -2, "認不得的專案沒有 exit 2");

            return new CheckRow(aName,
                aFail.Count == 0 ? "落行／撞名擋下零位元組／保留字／同值不寫／unset／跟著母單／成環／cmd tasks 篩選 全對" : string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "炸了：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }
}
