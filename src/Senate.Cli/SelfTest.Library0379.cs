// 區塊職責：驗證 Library 指令設定解析與零寫入拒絕邊界。
// 物理意義：資料根與信件庫根刻意分離，避免錯誤推導仍能寫檔而測試誤判成功。
// 數值影響：只在臨時目錄建立資料，結束還原宿主插座並刪除測試樹。
using SCP.Core.Cmd;
using SCP.Core.Library;
using SCP.Core.Paths;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow LibraryConfiguredRoots()
    {
        const string aName = "Library 設定根：分離信件庫／persona／路徑覆寫拒絕／缺設定零寫入";
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_library_" + Guid.NewGuid().ToString("N"));
        var aBefore = SCP_Cmd_Library.RootsProvider;
        try
        {
            string aProject = Path.Combine(aRoot, "project");
            string aData = Path.Combine(aRoot, "data");
            string aLetters = Path.Combine(aRoot, "independent-letters");
            string aComics = Path.Combine(aRoot, "comics");
            foreach (string p in new[] { aProject, aData, aLetters, aComics,
                       Path.Combine(aLetters, "tester", "profile") }) Directory.CreateDirectory(p);
            var aConfig = new SenateConfig();
            // TASK-0400：漫畫庫根是設定值（SCP_PathId.ComicRoot），不再是 .comic_root.local 快照
            aConfig.Projects.Add(new SenateProject { Root = aProject });
            aConfig.Paths.AgentCommandsRoot = aData;   // TASK-0390：資料根／漫畫庫根是全域那一格
            aConfig.Paths.ComicRoot = aComics;
            aConfig.Awakening.LettersRoot = aLetters;
            SCP_Cmd_Library.RootsProvider = () => SenateLibraryRoots.Resolve(aConfig);
            var aFails = new List<string>();
            SCP_CmdResult Run(string iOp, params (string Key, string Value)[] iExtra)
            {
                var aArgs = new Dictionary<string, string> { ["op"] = iOp,
                    ["persona"] = "tester", ["media_id"] = "book-test" };
                foreach (var e in iExtra) aArgs[e.Key] = e.Value;
                return SCP_CmdRegistry.Dispatch("library", aArgs);
            }
            void Need(bool iOk, string iMessage) { if (!iOk) aFails.Add(iMessage); }
            Need(Run("media_init", ("work_id", "test"), ("title", "Test")).ExitCode == 0, "建檔失敗");
            Need(Run("note_chapter", ("chapter_id", "0001"), ("body", "Test reading body")).ExitCode == 0, "心得失敗");
            var aTypedLetters = new SCP_LettersRoot(aLetters);
            string aRecall = SCP_LettersPaths.CmdPayload(aTypedLetters, "tester", "reading_recall", "book-test");
            Need(File.Exists(aRecall) && File.ReadAllText(aRecall).Contains("Test reading body"), "追回檔未落到設定信件庫");
            Need(File.Exists(Path.Combine(aLetters, "tester", SCP.Core.Letters.SCP_WakeBrief.BookshelfDirName, "book-test.md")), "信件庫閱讀卡轉發遺失");
            Need(!Directory.Exists(Path.Combine(aData, "ChatTavern")), "在資料根錯誤推導信件庫");
            Need(Run("comics").ExitCode == 0, "漫畫設定快照未解析");
            int aFiles = Directory.GetFiles(aRoot, "*", SearchOption.AllDirectories).Length;
            foreach (string k in new[] { "data_root", "letters_root", "comic_root" })
                Need(Run("note_chapter", ("chapter_id", "0002"), ("body", "reject"),
                         (k, Path.Combine(aRoot, "wrong"))).ExitCode == 2, k + "仍可手填");
            Need(Run("media_init", ("persona", "typo"), ("work_id", "other"), ("title", "Other")).ExitCode == 2,
                 "未知 persona 未擋下");
            Need(Run("paths", ("persona", "../tester")).ExitCode == 2, "persona 路徑逃逸未擋下");
            aConfig.Awakening.LettersRoot = Path.Combine(aRoot, "missing-letters");
            Need(Run("note_chapter", ("chapter_id", "0002"), ("body", "reject")).ExitCode == 3, "根設定變更未即時讀取");
            aConfig.Awakening.LettersRoot = aLetters;
            aConfig.Paths.AgentCommandsRoot = Path.Combine(aRoot, "missing-data");
            Need(Run("paths").ExitCode == 3, "不存在的資料根未擋下");
            aConfig.Paths.AgentCommandsRoot = aData;
            // TASK-0390：Unity 專案只是開發目標 ⇒ 多一個啟用專案**不影響**資料根（反向：舊版這裡會擋）
            aConfig.Projects.Add(new SenateProject { Root = aProject });
            Need(Run("paths").ExitCode == 0, "多個 Unity 開發目標不該影響資料根");
            aConfig.Projects.RemoveAt(1);
            // 本格空白 ⇒ comics 擋下（exit 3）而 paths 不受影響；舊快照有值時要**說出來**，⛔ 不採用
            aConfig.Paths.ComicRoot = "";
            string aLegacy = Path.Combine(aProject, SCP_LibraryComics.ComicRootSnapshotFileName);
            File.WriteAllText(aLegacy, "# local settings\ncomic_root=" + aComics + "\n");
            SCP_CmdResult aBlank = Run("comics");
            Need(aBlank.ExitCode == 3 && Run("paths").ExitCode == 0, "漫畫設定錯誤隔離失敗");
            Need(string.Join("\n", aBlank.Lines).Contains("舊快照"), "本格空白而舊快照有值時沒有說出來");
            File.Delete(aLegacy);
            SCP_Cmd_Library.RootsProvider = () => SenateLibraryRoots.Resolve(null);
            Need(Run("media_init", ("work_id", "other"), ("title", "Other")).ExitCode == 3, "缺設定未擋下");
            SCP_Cmd_Library.RootsProvider = null;
            Need(Run("paths").ExitCode == 3, "無宿主仍可解析");
            Need(Directory.GetFiles(aRoot, "*", SearchOption.AllDirectories).Length == aFiles
                 && !Directory.Exists(Path.Combine(aRoot, "wrong"))
                 && !Directory.Exists(Path.Combine(aRoot, "missing-letters")), "拒絕後仍有寫入");
            return new CheckRow(aName, aFails.Count == 0 ? "所有落點與拒絕邊界符合設定" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, e.ToString(), CheckResult.Fail); }
        finally
        {
            SCP_Cmd_Library.RootsProvider = aBefore;
            if (Directory.Exists(aRoot)) Directory.Delete(aRoot, true);
        }
    }
}
