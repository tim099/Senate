// 回歸測試：宿主注入、外部漫畫來源與共用 task lane；只碰臨時資料樹。
using System.Reflection;
using SCP.Core.Cmd;
using SCP.Core.Library;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow HostInjectedLetters0410()
    {
        string root = Path.Combine(Path.GetTempPath(), "senate_0410_" + Guid.NewGuid().ToString("N"));
        var stdout = Console.Out;
        var stderr = Console.Error;
        try
        {
            Directory.CreateDirectory(root);
            string data = Path.Combine(root, "data"), letters = Path.Combine(root, "letters");
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(letters);
            var config = new SenateConfig();
            config.Paths.AgentCommandsRoot = data;   // 資料根是全域那一格（TASK-0390），不住在專案上
            config.Awakening.LettersRoot = letters;
            config.Save(SenateConfig.DefaultPath(root));
            var method = typeof(Program).GetMethod("CmdScp", BindingFlags.Static | BindingFlags.NonPublic)!;
            var output = new StringWriter();
            Console.SetOut(output);
            Console.SetError(output);
            int code = (int)method.Invoke(null, new object[] { root, new[] { "cmd", "bank-request", "--arg", "op=list" } })!;
            bool ok = code == 0 && !output.ToString().Contains("unread_args = letters_root");
            output.GetStringBuilder().Clear();
            int rejected = (int)method.Invoke(null, new object[] { root, new[] { "cmd", "bank-request", "--arg", "op=list", "--arg", "letters_root=" + letters } })!;
            var (bound, errors) = SCP_CmdArgs.Bind(new[] { new SCP_CmdArgSpec("letters_root", "test") },
                new Dictionary<string, string> { ["letters_root"] = letters });
            ok &= rejected == 2 && output.ToString().Contains("路徑參數不接受手給")
                  && errors.Count == 0 && bound!.UnreadExplicitArgs().Contains("letters_root");
            return new CheckRow("HostInjectedLetters0410", ok ? "CLI 注入不誤警告；手給拒絕與內部顯式追蹤保留" : "宿主注入或反向對照失敗", ok ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("HostInjectedLetters0410", e.ToString(), CheckResult.Fail); }
        finally { Console.SetOut(stdout); Console.SetError(stderr); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static CheckRow ComicSourceDirectories0411()
    {
        string root = Path.Combine(Path.GetTempPath(), "senate_0411_" + Guid.NewGuid().ToString("N"));
        try
        {
            string data = Path.Combine(root, "data"), comics = Path.Combine(root, "comics");
            Directory.CreateDirectory(data);
            void Put(string name) { string p = Path.Combine(comics, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, "fixture"); }
            Put("reader/__pycache__/reader.pyc"); Put("__pycache__/cache.pyc");
            Directory.CreateDirectory(Path.Combine(comics, "temp"));
            Put("Book/0001/002.JPG"); Put("Book/0001/001.png"); Put("Book/cache/info.txt");
            Put("Flat/001.webp"); Put("Flat/__pycache__/info.pyc");
            // 空的 0001 子目錄不能遮蔽根目錄單章。
            Directory.CreateDirectory(Path.Combine(comics, "Flat", "0001"));
            string Snapshot() => string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(p => p).Select(p => p + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))))
                + string.Join("\n", Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(p => p));
            string before = Snapshot();
            var series = SCP_LibraryComics.ScanExternalComics(data, comics, out var warning);
            var book = series.SingleOrDefault(s => s.MediaId == "comic-book");
            var flat = series.SingleOrDefault(s => s.MediaId == "comic-flat");
            var pages = SCP_LibraryComics.ListChapterPages(data, comics, "comic-book", "0001", out _);
            var flatPages = SCP_LibraryComics.ListChapterPages(data, comics, "comic-flat", "0001", out _);
            bool ok = warning == null && series.Count == 2 && book?.TotalPages == 2 && book.TotalChapters == 1
                && flat?.TotalPages == 1 && pages?.Pages.Count == 2 && pages.Pages.All(p => p.Exists)
                && flatPages?.Pages.Count == 1 && before == Snapshot();
            // 名稱不是黑名單：真的有頁面的 temp 必須可讀。
            Put("temp/001.jpg");
            ok &= SCP_LibraryComics.ScanExternalComics(data, comics, out _).Any(s => s.MediaId == "comic-temp" && s.TotalPages == 1);
            return new CheckRow("ComicSourceDirectories0411", ok ? "排除零頁目錄；保留章目錄／平放頁／任意作品名；掃描零寫入" : "來源辨識或頁數／零寫入失敗", ok ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("ComicSourceDirectories0411", e.ToString(), CheckResult.Fail); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    static CheckRow TaskLaneIdentity0412()
    {
        string root = Path.Combine(Path.GetTempPath(), "senate_0412_" + Guid.NewGuid().ToString("N"));
        try
        {
            var cmd = new Cmd_TaskWrite();
            var (args, errors) = SCP_CmdArgs.Bind(cmd.ArgSpecs, new Dictionary<string, string> {
                ["data_root"] = root, ["op"] = "sweep", ["persona"] = "Sirius", ["args_json"] = "{}" });
            var laneMethod = typeof(Cmd_TaskWrite).GetMethod("Lane", BindingFlags.Instance | BindingFlags.NonPublic)!;
            string lane = (string)laneMethod.Invoke(cmd, new object[] { args! })!;
            var classify = typeof(ServerDelegateCmd).GetMethod("IsPersonaLane", BindingFlags.Instance | BindingFlags.NonPublic);
            if (classify == null) return new CheckRow("TaskLaneIdentity0412", "共用 lane 缺少身分分類", CheckResult.Fail);
            bool inject = (bool)classify.Invoke(cmd, new object[] { lane })!;
            var logs = new List<string>();
            var payload = new Dictionary<string, string> { ["persona"] = "Sirius" };
            AgentCmdClient.Submit(root, lane, "task-write", payload, logs.Add, iInjectPersona: inject);
            bool ok = errors.Count == 0 && lane == Cmd_TaskWrite.TaskLane && !inject && logs.All(l => !l.Contains("身分宣告衝突"))
                && payload["persona"] == "Sirius" && File.Exists(AgentCmdClient.QueuePath(root, "task"));
            logs.Clear();
            AgentCmdClient.Submit(root, "other", "probe", new Dictionary<string, string> { ["persona"] = "Sirius" }, logs.Add);
            ok &= logs.Any(l => l.Contains("身分宣告衝突"));
            return new CheckRow("TaskLaneIdentity0412", ok ? "task 串行 lane 保留；作者保留；真身分衝突仍警告" : "lane 或身分辨識失敗", ok ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("TaskLaneIdentity0412", e.ToString(), CheckResult.Fail); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
