// 區塊職責：TASK-0415（工作記憶與密封信移植成 Senate 指令）的淨室自測。
// 物理意義：① 工作記憶：寫出的檔是 CRLF／UTF-8 無 BOM（與既有資料同形）、fragment 正文不被改寫、
//              supersede 一步式接上取代鏈、link 雙向、`links: []   # 註解` 讀成空清單（不是逐字元拆開）、索引照 type 分節。
//           ② 密封信：master 的 .gitignore 沒有 `sealed/` ⇒ 拒跑且零寫入；有 ⇒ 信只進 private、master 的 tree 沒有它、HEAD 不動。
// 數值影響：在 temp 造資料根與 git repo，跑完刪除。
#nullable enable
using System.Text;
using SCP.Core.Git;
using SCP.Core.Letters;
using SCP.Core.WorkMemory;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow WorkMemoryCleanRoom()
    {
        const string aName = "工作記憶：CRLF 落檔／正文不改寫／supersede 取代鏈／link 雙向／帶註解的空清單／索引分節（淨室，TASK-0415）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_wm0415_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        try
        {
            string aData = Path.Combine(aTmp, "AgentCommands");
            Directory.CreateDirectory(aData);
            var wm = new SCP_WorkMemory(aData, new Dictionary<string, string>());
            if (wm.Init("t", "主題", "").Exit != 0) aFails.Add("init 失敗");
            if (wm.Add("t", "decision", "a", "決策甲", "第一行\n第二行：有冒號", "", "x.md:3", "probe").Exit != 0) aFails.Add("add 失敗");
            string aFrag = Path.Combine(aData, "WorkMemory", "t", "decision_a.md");
            byte[] aBytes = File.ReadAllBytes(aFrag);
            string aText = Encoding.UTF8.GetString(aBytes);
            if (aBytes.Length >= 3 && aBytes[0] == 0xEF) aFails.Add("寫出了 BOM");
            if (aText.Replace("\r\n", "").Contains('\n')) aFails.Add("有不是 CRLF 的換行");
            if (!aText.EndsWith("第一行\r\n第二行：有冒號\r\n", StringComparison.Ordinal)) aFails.Add("正文落檔不對");
            if (!aText.Contains("related_docs: [x.md:3]")) aFails.Add("related_docs 沒照單行清單寫");

            if (wm.Add("t", "decision", "a", "重複", "x", "", "", "").Exit != 2) aFails.Add("同 id 再 add 沒有擋");
            if (wm.Supersede("t", "decision_a", "", "b", "決策乙", "新內容", "").Exit != 0) aFails.Add("supersede 失敗");
            string aOld = File.ReadAllText(aFrag);
            if (!aOld.Contains("status: superseded") || !aOld.Contains("links: [t/decision_b]")) aFails.Add("舊 fragment 沒標 superseded／沒接到新的");
            if (!aOld.Contains("第一行\r\n第二行：有冒號")) aFails.Add("supersede 改寫了舊正文");
            if (!File.ReadAllText(Path.Combine(aData, "WorkMemory", "t", "decision_b.md")).Contains("links: [t/decision_a]")) aFails.Add("新 fragment 沒指回舊的");

            wm.Add("t", "pitfall", "p", "坑", "y", "", "", "");
            if (wm.Link("t/pitfall_p", "t/decision_b").Exit != 0) aFails.Add("link 失敗");
            if (!File.ReadAllText(Path.Combine(aData, "WorkMemory", "t", "pitfall_p.md")).Contains("links: [t/decision_b]")
                || !File.ReadAllText(Path.Combine(aData, "WorkMemory", "t", "decision_b.md")).Contains("t/pitfall_p"))
                aFails.Add("link 不是雙向");

            // 帶註解的空清單：不是單行 [..] ⇒ 不是清單，索引不得逐字元印出
            File.WriteAllText(Path.Combine(aData, "WorkMemory", "t", "knowhow_c.md"),
                "---\r\nid: knowhow_c\r\ntitle: 註解\r\ntype: knowhow\r\nstatus: active\r\nlinks: []   # 註解\r\n---\r\n\r\nz\r\n", new UTF8Encoding(false));
            wm.Index("t");
            string aIdx = File.ReadAllText(Path.Combine(aData, "WorkMemory", "t", "_index.md"));
            if (!aIdx.Contains("- **knowhow_c** — 註解\r\n")) aFails.Add("帶註解的 links 被當成清單印進索引");
            int iDec = aIdx.IndexOf("## decision", StringComparison.Ordinal), iKh = aIdx.IndexOf("## knowhow", StringComparison.Ordinal), iPit = aIdx.IndexOf("## pitfall", StringComparison.Ordinal);
            if (!(iDec >= 0 && iDec < iKh && iKh < iPit)) aFails.Add("索引沒照 type 固定序分節");
            if (!aIdx.Contains("decision_a** — 決策甲 ~~[superseded]~~")) aFails.Add("索引沒標 superseded");
            if (aIdx.EndsWith("\r\n\r\n", StringComparison.Ordinal)) aFails.Add("索引檔尾多一個空行");
        }
        catch (Exception e) { aFails.Add(e.GetType().Name + ": " + e.Message); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
        return new CheckRow(aName, aFails.Count == 0 ? "全部成立" : string.Join("／", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }

    static CheckRow SealedLetterCleanRoom()
    {
        const string aName = "密封信：沒有 .gitignore sealed/ ⇒ 拒跑零寫入／有 ⇒ 只進 private、master tree 沒有、HEAD 不動（淨室，TASK-0415）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_sl0415_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        try
        {
            string aRepo = Path.Combine(aTmp, "probe");
            Directory.CreateDirectory(aRepo);
            string G(params string[] a) { SCP_GitResult r = SCP_Git.Run(aRepo, a); if (r.Exit != 0) throw new InvalidOperationException("git " + string.Join(" ", a) + "：" + r.StdErr); return (r.StdOut ?? "").Trim(); }
            G("init", "-q", "-b", "master"); G("config", "user.email", "p@p"); G("config", "user.name", "p");
            File.WriteAllText(Path.Combine(aRepo, "a.txt"), "x\n");
            G("add", "a.txt"); G("commit", "-qm", "init");
            string aHead = G("rev-parse", "HEAD");

            SCP_SealedLetters s = SCP_SealedLetters.Open(aTmp, "probe", out string aErr) ?? throw new InvalidOperationException(aErr);
            SCP_SealedResult r1 = s.Write("t", "秘密", "", false);
            if (r1.Exit == 0) aFails.Add("沒有 .gitignore sealed/ 卻寫成了");
            if (Directory.Exists(Path.Combine(aRepo, "sealed"))) aFails.Add("拒跑但工作區長出 sealed/");
            if (SCP_Git.Run(aRepo, "rev-parse", "--verify", "refs/heads/private").Exit == 0) aFails.Add("拒跑但建了 private 分支");

            File.WriteAllText(Path.Combine(aRepo, ".gitignore"), "sealed/\n");
            G("add", ".gitignore"); G("commit", "-qm", "ignore");
            aHead = G("rev-parse", "HEAD");
            SCP_SealedResult r2 = s.Write("標題", "秘密內容", "", false);
            if (r2.Exit != 0) aFails.Add("有 .gitignore 仍失敗：" + string.Join(" ", r2.Lines));
            string aPriv = G("ls-tree", "-r", "--name-only", "private");
            if (!aPriv.Contains("sealed/")) aFails.Add("private 上沒有信");
            if (G("ls-tree", "-r", "--name-only", "master").Contains("sealed/")) aFails.Add("master 上出現信");
            if (G("rev-parse", "HEAD") != aHead || G("rev-parse", "--abbrev-ref", "HEAD") != "master") aFails.Add("HEAD 被動了");
            if (G("status", "--porcelain").Length > 0) aFails.Add("工作區不乾淨（信沒被 ignore 擋住）");
            if (!G("diff", "--name-only", "master", "private").Split('\n').All(x => x.StartsWith("sealed/", StringComparison.Ordinal))) aFails.Add("private 與 master 的差異不只 sealed/");
        }
        catch (Exception e) { aFails.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            try
            {
                foreach (string f in Directory.GetFiles(aTmp, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(aTmp, true);
            }
            catch (Exception) { }
        }
        return new CheckRow(aName, aFails.Count == 0 ? "全部成立" : string.Join("／", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
