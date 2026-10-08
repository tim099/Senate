// 區塊職責：手動收尾信遷移淨室驗證。只用 temp 資料，不登入任何真實 persona。
// 數值影響：測試目錄內造原信、衝突與在線 lock；驗副本位元組與原檔快照，最後清除 temp。
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow LettersMigrationCleanRoom()
    {
        const string aName = "手動信件遷移：frontmatter 篩選／dry-run 零寫入／原檔不變／重跑／衝突／在線阻擋／不自動遷移";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_letters_migrate_" + Guid.NewGuid().ToString("N"));
        try
        {
            var aRoot = new SCP_LettersRoot(Path.Combine(aTmp, "letters"));
            string Persona(string p)
            {
                string d = SCP_LettersPaths.PersonaDir(aRoot, p);
                Directory.CreateDirectory(Path.Combine(d, "profile"));
                return d;
            }
            byte[] Bytes(string body) => System.Text.Encoding.UTF8.GetBytes(body);
            byte[] Letter(string trigger, string body = "親筆信\r\n") => Bytes("---\r\ntrigger: " + trigger + "\r\n---\r\n" + body);
            string aMe = Persona("legacy");
            var aOriginals = new Dictionary<string, byte[]>
            {
                ["20260102T000000Z.md"] = Letter("cmd_goodnight"),
                // BOM 與引號也必須能讀，複製保持原始 bytes。
                ["20260101T000000Z.md"] = new byte[] { 239, 187, 191 }.Concat(Letter("'cmd_goodnight'", "第一封\r\n")).ToArray(),
                ["_latest.md"] = Letter("cmd_goodnight"),
                ["draft.md"] = Bytes("---\ntrigger: manual\n---\ntrigger: cmd_goodnight\n"),
                ["rest.md"] = Letter("cmd_rest"),
                ["manual-logout.md"] = Letter("cmd_goodnight", "Manual logout via UCL_LoginStatusPage (Editor IMGUI)."),
                ["prefix.md"] = Letter("cmd_goodnight_extra"),
                ["body.md"] = Bytes("# 正文\ntrigger: cmd_goodnight\n"),
                ["unclosed.md"] = Bytes("---\ntrigger: cmd_goodnight\n"),
                ["nested.md"] = Bytes("---\ntype: note\n---\n---\ntrigger: cmd_goodnight\n---\n"),
            };
            foreach (var f in aOriginals) File.WriteAllBytes(Path.Combine(aMe, f.Key), f.Value);
            string aWakes = SCP_LettersPaths.WakesDir(aRoot, "legacy");
            string[] aBefore = Directory.GetFileSystemEntries(aMe).Order().ToArray();
            var aDry = SCP_LettersMigration.Run(aRoot, "legacy", false);
            bool aDryOk = aDry.Ok && aDry.Candidates == 2 && aDry.Copied == 0 && !Directory.Exists(aWakes)
                && aBefore.SequenceEqual(Directory.GetFileSystemEntries(aMe).Order());
            var aMorningRoots = new SCP_MorningRoots { LettersRoot = aRoot.Value };
            bool aGuard = SCP_Morning.LettersMigrationPending(aMorningRoots, "legacy") && !Directory.Exists(aWakes);
            var aRun = SCP_LettersMigration.Run(aRoot, "legacy", true);
            bool aCopy = aRun.Ok && aRun.Copied == 2 && Directory.GetFiles(aWakes).Length == 2
                && File.ReadAllBytes(Path.Combine(aWakes, "000001_20260101T000000Z.md")).SequenceEqual(aOriginals["20260101T000000Z.md"])
                && File.ReadAllBytes(Path.Combine(aWakes, "000002_20260102T000000Z.md")).SequenceEqual(aOriginals["20260102T000000Z.md"])
                && aOriginals.All(f => File.ReadAllBytes(Path.Combine(aMe, f.Key)).SequenceEqual(f.Value))
                && !SCP_Morning.LettersMigrationPending(aMorningRoots, "legacy")
                && SCP_Consolidate.WakeLetterCount(aRoot.Value, "legacy") == 2;
            var aRepeat = SCP_LettersMigration.Run(aRoot, "legacy", true);
            bool aIdempotent = aRepeat.Ok && aRepeat.Copied == 0 && aRepeat.Existing == 2 && Directory.GetFiles(aWakes).Length == 2;

            // 早於既有信的補件也不重編既有序號。
            File.WriteAllBytes(Path.Combine(aMe, "20251201T000000Z.md"), Letter("cmd_goodnight"));
            var aAppend = SCP_LettersMigration.Run(aRoot, "legacy", true);
            bool aAppendOk = aAppend.Ok && aAppend.Copied == 1 && File.Exists(Path.Combine(aWakes, "000003_20251201T000000Z.md"));

            // 有一個副本壞掉：即使前面有待複製的新信，也必須在任何寫入之前拒絕。
            File.WriteAllText(Path.Combine(aWakes, "000002_20260102T000000Z.md"), "衝突");
            File.WriteAllBytes(Path.Combine(aMe, "20251101T000000Z.md"), Letter("cmd_goodnight"));
            var aConflict = SCP_LettersMigration.Run(aRoot, "legacy", true);
            bool aConflictOk = !aConflict.Ok && Directory.GetFiles(aWakes).Length == 3
                && File.ReadAllText(Path.Combine(aWakes, "000002_20260102T000000Z.md")) == "衝突";

            string aOnline = Persona("online");
            File.WriteAllBytes(Path.Combine(aOnline, "20260101.md"), Letter("cmd_goodnight"));
            File.WriteAllText(SCP_LettersPaths.SessionLockPath(aRoot, "online"), "壞 lock 也要擋");
            var aBlocked = SCP_LettersMigration.Run(aRoot, "online", true);
            bool aOnlineOk = !aBlocked.Ok && !Directory.Exists(SCP_LettersPaths.WakesDir(aRoot, "online"));
            Persona("empty");
            var aEmpty = SCP_LettersMigration.Run(aRoot, "empty", true);
            bool aEmptyOk = aEmpty.Ok && aEmpty.Candidates == 0 && Directory.Exists(SCP_LettersPaths.WakesDir(aRoot, "empty"));
            bool aInvalid = !SCP_LettersMigration.Run(aRoot, "../outside", true).Ok
                && !SCP_LettersMigration.Run(aRoot, "missing", true).Ok && !Directory.Exists(Path.Combine(aRoot.Value, "missing"));
            bool aCli = SCP_CmdRegistry.Find("letters-migrate") is SCP_Cmd_LettersMigrate;
            string aManualOnly = Persona("manual-only");
            File.WriteAllBytes(Path.Combine(aManualOnly, "20260101.md"), aOriginals["manual-logout.md"]);
            bool aManualExcluded = !SCP_Morning.LettersMigrationPending(aMorningRoots, "manual-only")
                && SCP_LettersMigration.Run(aRoot, "manual-only", false).Candidates == 0;
            bool aOk = aDryOk && aGuard && aCopy && aIdempotent && aAppendOk && aConflictOk && aOnlineOk && aEmptyOk && aInvalid && aCli && aManualExcluded;
            return new CheckRow(aName,
                $"dry-run={aDryOk}／守衛只查不遷移={aGuard}／複製與原檔={aCopy}／重跑={aIdempotent}／補件={aAppendOk}"
                + $"／衝突零寫入={aConflictOk}／在線={aOnlineOk}／空目錄={aEmptyOk}／非法與查無={aInvalid}／CLI={aCli}／手動登出排除={aManualExcluded}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); }
    }
}
