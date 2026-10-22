// 區塊職責：閱讀筆記同步到遠端 repo（sync.bookNotes）的淨室 —— 本機 bare repo 當遠端、幾棵資料樹當幾台機器。
// 物理意義：Tim 2026-10-22 要的幾格逐一走過：
//           ① 已經有 BookNotes、還不是 git repo ⇒ 就地接上（本機獨有的推上去、遠端獨有的拉下來、兩邊不同的用遠端版＋備份＋通知）；
//           ② 缺目錄 ⇒ 自動 clone；③ 兩台同時寫不同檔 ⇒ rebase 接上、兩邊都在；④ 用之前的拉有節流；
//           ⑤ 兩台同時改同一個檔 ⇒ 不自動解（本機提交留著、遠端不被蓋、衝突報告＋通知，同一個衝突只通知一次）；
//           反向對照：⑥ clone 不下來 ⇒ 不長出空目錄；⑦ 接上時連不到遠端 ⇒ 收掉剛建的 .git、本機的檔一個都不動；
//           ⑧ 遠端是全新的空 repo ⇒ 本機那一份就是第一版。
#nullable enable
using SCP.Core.Git;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow BookNotesSyncCleanRoom()
    {
        const string aName = "閱讀筆記同步到遠端 repo：就地接上／clone／不同檔 rebase／節流／同檔衝突擋下（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bnsync_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFail = new List<string>();
            void Expect(bool c, string what) { if (!c) aFail.Add(what); }
            SCP_GitResult G(string dir, params string[] a) => SCP_Git.Run(dir, a);
            string[] Id = { "-c", "user.name=selftest", "-c", "user.email=selftest@localhost" };
            string Lines(RepoSyncReport r) => r.StateWord + "｜" + string.Join(" / ", r.Lines);

            // ── 遠端：bare repo，種一份跟 tim099/BookNotes 同形的庫（作品檔、媒材檔、別人的書架）──
            Directory.CreateDirectory(aTmp);
            string aRemote = Path.Combine(aTmp, "remote.git").Replace('\\', '/');
            G(aTmp, "init", "--bare", "-b", "main", aRemote);
            string aSeed = Path.Combine(aTmp, "seed");
            G(aTmp, "clone", aRemote, aSeed);
            void Put(string root, string rel, string text)
            {
                string p = Path.Combine(root, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, text);
            }
            const string Work = "Library/works/w1/work.json", Media = "Library/media/m1/media.json", Shelf = "Library/media/m1/readers/other/bookshelf.md";
            Put(aSeed, Work, "{\"title\":\"遠端的完整作品檔\"}");
            Put(aSeed, Media, "{\"id\":\"m1\"}");
            Put(aSeed, Shelf, "別人的書架\n");
            G(aSeed, "add", "-A");
            G(aSeed, Id.Concat(new[] { "commit", "-q", "-m", "seed" }).ToArray());
            Expect(G(aSeed, "push", "-q", "origin", "HEAD:refs/heads/main").Ok, "種遠端失敗");
            string RemoteFile(string rel) { var r = G(aSeed, "--git-dir=" + aRemote, "show", "main:" + rel); return r.Ok ? r.StdOut : ""; }

            string Root(string n) { string p = Path.Combine(aTmp, n).Replace('\\', '/'); Directory.CreateDirectory(p); return p; }
            string BN(string root) => SenateBookNotesSync.Dir(root);
            var aTarget = new RepoSyncTarget(aRemote, "main");

            // ① A 的資料樹本身是 git repo（像 Valhalla），BookNotes 已經有東西、還不是 repo ⇒ 就地接上
            string A = Root("A");
            G(A, "init", "-q", ".");
            Put(BN(A), Work, "{\"title\":\"本機自動長的簡陋作品檔\"}");          // 兩邊都有、內容不同 ⇒ 用遠端版
            Put(BN(A), Media, "{\"id\":\"m1\"}");                               // 兩邊一樣
            Put(BN(A), "Library/media/m1/readers/A/chapters/0001/r1.md", "A 的第一話心得\n");   // 本機獨有
            var a1 = SenateBookNotesSync.BeforeUse(A, aTarget, iPullEverySec: 0);
            Expect(a1.State == RepoSyncState.Pushed, $"① 接上後應推上去：{Lines(a1)}");
            Expect(Directory.Exists(Path.Combine(BN(A), ".git")), "① 沒有長出 BookNotes/.git");
            Expect(RemoteFile("Library/media/m1/readers/A/chapters/0001/r1.md").Contains("A 的第一話心得"), "① 遠端沒有 A 的心得");
            Expect(File.ReadAllText(Path.Combine(BN(A), Work)).Contains("遠端的完整作品檔"), "① 兩邊不同的作品檔沒有用遠端版");
            Expect(File.Exists(Path.Combine(BN(A), Shelf)), "① 遠端獨有的書架沒拉下來");
            Expect(RemoteFile(Work).Contains("遠端的完整作品檔"), "① 遠端的作品檔被蓋掉了");
            string aGitDirA = G(BN(A), "rev-parse", "--absolute-git-dir").StdOut.Trim();
            Expect(Directory.GetFiles(Path.Combine(aGitDirA, "senate-booknotes-sync"), "work.json", SearchOption.AllDirectories)
                       .Any(f => File.ReadAllText(f).Contains("簡陋")), "① 本機那份作品檔沒有備份");
            Expect(a1.Notices.Count == 1 && a1.Notices[0].Contains("work.json"), $"① 應有一則「兩邊不同」的通知：{a1.Notices.Count}");
            Expect(!G(A, "status", "--porcelain").OutLines().Any(l => l.Contains("BookNotes/")),
                $"① A 的資料樹把 BookNotes/ 列成未追蹤（巢狀 repo 沒排除）：{string.Join(" / ", G(A, "status", "--porcelain").OutLines())}");

            // ② B 沒有 BookNotes ⇒ 第一次用到就 clone，看得到 A 的心得
            string B = Root("B");
            var b1 = SenateBookNotesSync.BeforeUse(B, aTarget);
            Expect(b1.Lines.Any(l => l.Contains("clone")) && File.Exists(Path.Combine(BN(B), "Library/media/m1/readers/A/chapters/0001/r1.md")),
                $"② B 沒 clone 下來：{Lines(b1)}");

            // ③ 兩台同時寫不同的檔：A 先推；B 還沒拉就寫 ⇒ 推被拒 ⇒ rebase 接上
            Put(BN(A), "Library/media/m1/readers/A/chapters/0002/r1.md", "A 的第二話\n");
            var a2 = SenateBookNotesSync.AfterWrite(A, aTarget, "library(note_chapter) m1 0002 A", "A");
            Put(BN(B), "Library/media/m1/readers/B/chapters/0001/r1.md", "B 的第一話\n");
            var b2 = SenateBookNotesSync.AfterWrite(B, aTarget, "library(note_chapter) m1 0001 B", "B");
            Expect(a2.State == RepoSyncState.Pushed, $"③ A 沒推上去：{Lines(a2)}");
            Expect(b2.State == RepoSyncState.Pushed && b2.Lines.Any(l => l.Contains("rebase 接上")), $"③ B 應 rebase 接上再推：{Lines(b2)}");
            Expect(RemoteFile("Library/media/m1/readers/A/chapters/0002/r1.md").Length > 0
                   && RemoteFile("Library/media/m1/readers/B/chapters/0001/r1.md").Length > 0, "③ 遠端少了一邊");
            Expect(G(BN(A), "log", "-1", "--format=%s").StdOut.Contains("booknotes: library(note_chapter) m1 0002 A (auto)"), "③ commit 訊息不是預期的形狀");

            // ④ 節流：A 剛抓過 ⇒ 這次不抓（看不到 B 的）；不節流 ⇒ 抓到
            var a3 = SenateBookNotesSync.BeforeUse(A, aTarget);
            Expect(!a3.Fetched && !File.Exists(Path.Combine(BN(A), "Library/media/m1/readers/B/chapters/0001/r1.md")), $"④ 節流內不該抓：{Lines(a3)}");
            var a4 = SenateBookNotesSync.BeforeUse(A, aTarget, iPullEverySec: 0);
            Expect(a4.Fetched && File.Exists(Path.Combine(BN(A), "Library/media/m1/readers/B/chapters/0001/r1.md")), $"④ 不節流應拉到 B 的：{Lines(a4)}");

            // ⑤ 兩台同時改同一個檔 ⇒ B 擋下：本機提交留著、遠端是 A 的、報告＋通知；再跑一次不重複通知
            SenateBookNotesSync.BeforeUse(B, aTarget, iPullEverySec: 0);
            Put(BN(A), Shelf, "A 改的書架\n");
            var a5 = SenateBookNotesSync.AfterWrite(A, aTarget, "library(bookmark) m1 A", "A");
            Put(BN(B), Shelf, "B 改的書架\n");
            var b5 = SenateBookNotesSync.AfterWrite(B, aTarget, "library(bookmark) m1 B", "B");
            Expect(a5.State == RepoSyncState.Pushed, $"⑤ A 沒推上去：{Lines(a5)}");
            Expect(b5.State == RepoSyncState.Conflict, $"⑤ B 應是衝突：{Lines(b5)}");
            Expect(b5.ConflictFile.Length > 0 && File.Exists(b5.ConflictFile), "⑤ 衝突報告沒寫出來");
            Expect(b5.Notices.Count == 1 && b5.Notices[0].Contains("bookshelf.md"), $"⑤ 應有一則衝突通知：{b5.Notices.Count}");
            Expect(RemoteFile(Shelf).Contains("A 改的書架"), $"⑤ 遠端的書架被蓋掉了：{RemoteFile(Shelf).Trim()}");
            Expect(File.ReadAllText(Path.Combine(BN(B), Shelf)).Contains("B 改的書架"), "⑤ B 本機的改動不見了");
            Expect(G(BN(B), "rev-list", "--count", "origin/main..HEAD").StdOut.Trim() != "0", "⑤ B 的提交不在了（應該留著等手動解）");
            Expect(G(BN(B), "status", "--porcelain").OutLines().Count == 0 && !Directory.Exists(Path.Combine(G(BN(B), "rev-parse", "--absolute-git-dir").StdOut.Trim(), "rebase-merge")),
                "⑤ B 停在 rebase 一半（應已退回）");
            Expect(G(BN(B), "for-each-ref", "refs/senate/booknotes-sync/").OutLines().Count > 0, "⑤ B 沒有備份 ref");
            var b6 = SenateBookNotesSync.AfterWrite(B, aTarget, "library(bookmark) m1 B", "B");
            Expect(b6.State == RepoSyncState.Conflict && b6.Notices.Count == 0, $"⑤ 同一個衝突又通知了一次：{b6.Notices.Count}｜{Lines(b6)}");

            // ⑥ 反向對照：clone 不下來 ⇒ 不長出空的 BookNotes/
            string C = Root("C");
            var c1 = SenateBookNotesSync.BeforeUse(C, new RepoSyncTarget(Path.Combine(aTmp, "沒有這個.git"), "main"));
            Expect(c1.State == RepoSyncState.Failed && !Directory.Exists(BN(C)), $"⑥ clone 不下來應失敗且不建目錄：{Lines(c1)}");

            // ⑦ 反向對照：接上時連不到遠端 ⇒ 收掉剛建的 .git，本機的檔一個都不動
            string D = Root("D");
            Put(BN(D), "Library/x.md", "D 的筆記\n");
            var d1 = SenateBookNotesSync.BeforeUse(D, new RepoSyncTarget(Path.Combine(aTmp, "沒有這個.git"), "main"));
            Expect(d1.State == RepoSyncState.Failed && !Directory.Exists(Path.Combine(BN(D), ".git"))
                   && File.ReadAllText(Path.Combine(BN(D), "Library/x.md")) == "D 的筆記\n", $"⑦ 接不上應原封不動：{Lines(d1)}");

            // ⑧ 遠端是全新的空 repo ⇒ 本機那一份就是第一版
            string aEmpty = Path.Combine(aTmp, "empty.git").Replace('\\', '/');
            G(aTmp, "init", "--bare", "-b", "main", aEmpty);
            string E = Root("E");
            Put(BN(E), "Library/e.md", "E 的筆記\n");
            var e1 = SenateBookNotesSync.BeforeUse(E, new RepoSyncTarget(aEmpty, "main"), iPullEverySec: 0);
            var aEmptyShow = G(aTmp, "--git-dir=" + aEmpty, "show", "main:Library/e.md");
            Expect(e1.State == RepoSyncState.Pushed && aEmptyShow.Ok && aEmptyShow.StdOut.Contains("E 的筆記"), $"⑧ 空遠端應推上第一版：{Lines(e1)}");

            return new CheckRow(aName,
                aFail.Count == 0 ? "就地接上（獨有推上、遠端拉下、不同用遠端版＋備份＋通知）／clone／不同檔 rebase／節流／同檔衝突擋下＋報告＋只通知一次／clone 不下來不長目錄／接不上原封不動／空遠端推第一版 全對"
                                 : string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "炸了：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { DeleteTreeForce(aTmp); }
    }
}
