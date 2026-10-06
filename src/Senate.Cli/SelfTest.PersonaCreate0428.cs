// 區塊職責：早安沒帶 persona 的候選清單＋`persona-create` 的規劃／寫入／登記 agent 的自我對拍（TASK-0428）。
// 物理意義：每一格驗一個「錯了也不會叫」的地方：
//           ① 候選：醒過的與沒醒過的都列；在線（有 lock）與測試殼不列 —— 🔴 反向：拿掉測試殼標記它就出現
//           ② 規劃：已存在／agent 不在表上／新開 agent 撞名或撞帳號 ⇒ 擋（零寫入）；只給名字就能規劃、其餘標「待本人填寫」
//           ③ fork：抄來源 vector 與血統、lineage 接上來源；hash 跟 python／awakening 同一條規則（固定 vector 的期望值是 python 算的）
//           ④ 寫入：照規劃建出的人 Exists、character.md 有待填標記 ⇒ 第一次 Wake 的 next 會提示補完角色設定
//           ⑤ 登記 agent：新增讀回、已存在不覆蓋、其他鍵保留、CRLF 保留
// 數值影響：temp 目錄造資料根與 letters，跑完刪；⛔ 不碰真實資料根、不碰 Server（開戶／開單／公告不在淨室裡跑）。
#nullable enable
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace Senate.Cli;

public static partial class SelfTest
{
    /// <summary>
    /// 帳號快取跨 process 失效（TASK-0428，erina 那一則沒領到薪）：別的 process 碰了綁定戳記 ⇒ 這邊下一次查詢重載。
    /// 🔴 對照：只改綁定檔、不碰戳記 ⇒ 仍是舊答案（證明快取真的在，這一格不是空測）。
    /// </summary>
    static CheckRow BankBindingStampCrossProcess()
    {
        const string aName = "帳號快取跨 process 失效：直接改綁定檔仍是舊答案（快取在）／別的 process 碰戳記 ⇒ 下一次查詢讀到新綁定／寫入端換綁立刻生效（淨室，TASK-0428）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_stamp_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aData = Path.Combine(aTmp, "AgentCommands").Replace('\\', '/');
            string aLetters = Path.Combine(aData, "ChatTavern", "baton", "letters").Replace('\\', '/');
            var aLr = new SCP_LettersRoot(aLetters);
            Directory.CreateDirectory(Path.Combine(aData, "AwakenInit"));
            File.WriteAllText(SCP_PersonaCreate.RegistryMetaPath(aData), "{\"agent_banks\":{}}");
            Directory.CreateDirectory(SCP_LettersPaths.ProfileDir(aLr, "newbie"));
            string aBankDir = Path.Combine(SCP_LettersPaths.PersonaDir(aLr, "newbie"), "bank");
            Directory.CreateDirectory(aBankDir);
            string aBind = Path.Combine(aBankDir, "Ducat.md");
            File.WriteAllText(aBind, "acc-a\n");
            string R() => SCP.Core.Bank.SCP_BankAccountResolver.Resolve(aLetters, aData, "Ducat", "newbie").AccountId;

            string r1 = R();
            File.WriteAllText(aBind, "acc-b\n");                       // 別的 process 改了綁定，但沒碰戳記
            string r2 = R();
            string aStamp = SCP.Core.Bank.SCP_BankAccountResolver.StampPath(aData);
            File.WriteAllText(aStamp, "other-process\n");               // 別的 process 碰了戳記（⛔ 不呼叫 Touch：那會順便清本 process 的快取）
            File.SetLastWriteTimeUtc(aStamp, DateTime.UtcNow.AddSeconds(5));
            string r3 = R();
            bool aWrote = SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aData, "newbie", "Ducat", "acc-c", "selftest", "TASK-0428", out _, out string aErr);
            string r4 = R();

            bool aCacheExists = r1 == "acc-a" && r2 == "acc-a";
            bool aCross = r3 == "acc-b";
            bool aWriter = aWrote && r4 == "acc-c";
            return new CheckRow(aName,
                $"首查 {r1}／🔴 只改綁定檔 {r2}（期望仍是 acc-a）={aCacheExists}／別的 process 碰戳記後 {r3}（期望 acc-b）={aCross}"
                + $"／寫入端換綁後 {r4}（期望 acc-c）={aWriter}{(aErr.Length > 0 ? "：" + aErr : "")}",
                aCacheExists && aCross && aWriter ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally
        {
            SCP.Core.Bank.SCP_BankAccountResolver.Invalidate();
            try { Directory.Delete(aTmp, true); } catch (Exception) { }
        }
    }

    /// <summary>letters 變成 git repo（TASK-0428，Tim 選 (a)、remote 自己處理）—— 淨室：拋棄式父層 repo ＋ 本機 bare repo 當遠端。</summary>
    static CheckRow PersonaLettersRepoCleanRoom()
    {
        const string aName = "persona letters repo：init 後 master、cmd/ 不進版控、.gitignore 基線 sha 對／接遠端只提交 .gitmodules＋指向（父層其他髒檔不跟著）／重跑不重複登記／origin 不同擋下（淨室，TASK-0428）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_lrepo_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aParent = Path.Combine(aTmp, "AgentCommands");
            string aLetters = Path.Combine(aParent, "ChatTavern", "baton", "letters");
            var aLr = new SCP_LettersRoot(aLetters);
            Directory.CreateDirectory(Path.Combine(aLetters, "Template"));
            string aBase = "### base\r\ncmd/\r\nsealed/\r\n";
            File.WriteAllText(Path.Combine(aLetters, "Template", ".gitignore"), aBase);
            File.WriteAllText(Path.Combine(aParent, "seed.txt"), "seed");
            SCP.Core.Git.SCP_Git.Run(aParent, "init");
            SCP.Core.Git.SCP_Git.Run(aParent, "add", "seed.txt");
            SCP.Core.Git.SCP_Git.Run(aParent, "commit", "-m", "seed");
            File.WriteAllText(Path.Combine(aParent, "dirty.txt"), "別人還沒提交的檔");
            SCP.Core.Git.SCP_Git.Run(aParent, "add", "dirty.txt");   // ⚠ 刻意 stage：pathspec 提交不能把它帶走

            string aP = "newbie";
            Directory.CreateDirectory(SCP_LettersPaths.ProfileDir(aLr, aP));
            File.WriteAllText(Path.Combine(SCP_LettersPaths.ProfileDir(aLr, aP), "layer_role.md"), "x\n");
            Directory.CreateDirectory(Path.Combine(SCP_LettersPaths.PersonaDir(aLr, aP), "cmd"));
            File.WriteAllText(Path.Combine(SCP_LettersPaths.PersonaDir(aLr, aP), "cmd", "persona_create.md"), "回傳檔");
            string d = SCP_LettersPaths.PersonaDir(aLr, aP);

            var aLines = new List<string>();
            bool aInit = SCP_PersonaCreate.InitLettersRepo(aLetters, aP, aLines, out string e1);
            string aTracked = SCP.Core.Git.SCP_Git.Run(d, "ls-files").StdOut;
            string aBranch = SCP.Core.Git.SCP_Git.Run(d, "branch", "--show-current").StdOut.Trim();
            string aIgnore = File.ReadAllText(Path.Combine(d, ".gitignore"));
            string aShaLf;
            using (var sha = System.Security.Cryptography.SHA256.Create())
                aShaLf = string.Concat(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(aBase.Replace("\r\n", "\n"))).Select(b => b.ToString("x2")));
            bool aInitOk = aInit && aBranch == "master" && aTracked.Contains("profile/layer_role.md") && !aTracked.Contains("cmd/")
                           && aIgnore.Contains("baseline_sha256: " + aShaLf) && aIgnore.Contains("BASELINE END");
            bool aInitAgain = SCP_PersonaCreate.InitLettersRepo(aLetters, aP, aLines, out _) && aLines.Exists(l => l.Contains("已經是 git repo"));

            string aBare = Path.Combine(aTmp, "remote_newbie.git");
            SCP.Core.Git.SCP_Git.Run(aTmp, "init", "--bare", aBare);
            bool aAttach = SCP_PersonaCreate.AttachLettersRemote(aLetters, aP, aBare, aLines, out string e2);
            string aLastFiles = SCP.Core.Git.SCP_Git.Run(aParent, "show", "--name-only", "--format=", "HEAD").StdOut;
            bool aOnlyMine = aAttach && aLastFiles.Contains(".gitmodules") && aLastFiles.Contains("letters/" + aP) && !aLastFiles.Contains("dirty.txt")
                             && SCP.Core.Git.SCP_Git.Run(aParent, "diff", "--cached", "--name-only").StdOut.Contains("dirty.txt");
            bool aAgain = SCP_PersonaCreate.AttachLettersRemote(aLetters, aP, aBare, aLines, out _) && aLines.Exists(l => l.Contains("沒有重複登記"));
            bool aConflict = !SCP_PersonaCreate.AttachLettersRemote(aLetters, aP, aBare + "-other", aLines, out string e3) && e3.Contains("不覆蓋");

            bool aOk = aInitOk && aInitAgain && aOnlyMine && aAgain && aConflict;
            return new CheckRow(aName,
                $"init（master、cmd/ 不追、基線 sha）={aInitOk}{(e1.Length > 0 ? "：" + e1 : "")}／重跑不重 init={aInitAgain}"
                + $"／接遠端只提交 .gitmodules＋指向、dirty.txt 仍 staged 沒被帶走={aOnlyMine}{(e2.Length > 0 ? "：" + e2 : "")}／重跑不重複登記={aAgain}／origin 不同擋下={aConflict}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { ForceDelete(aTmp); } catch (Exception) { } }

        // git 物件檔是唯讀 ⇒ Directory.Delete 會失敗；先拿掉唯讀再刪。
        static void ForceDelete(string iDir)
        {
            if (!Directory.Exists(iDir)) return;
            foreach (string f in Directory.GetFiles(iDir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(iDir, true);
        }
    }

    static CheckRow PersonaCreateCleanRoom()
    {
        const string aName = "persona-create／早安候選：候選排除在線與測試殼（反向：拿掉標記就出現）／規劃擋已存在與撞號／fork 血統與 hash／建出的人 Wake 提示補設定／登記 agent 不覆蓋（淨室，TASK-0428）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_pcreate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aData = Path.Combine(aTmp, "AgentCommands").Replace('\\', '/');
            string aLetters = Path.Combine(aData, "ChatTavern", "baton", "letters").Replace('\\', '/');
            var aRoots = new SCP_MorningRoots { DataRoot = aData, LettersRoot = aLetters, ProjectRoot = aTmp.Replace('\\', '/') };
            var aLr = new SCP_LettersRoot(aLetters);
            Directory.CreateDirectory(Path.Combine(aData, "AwakenInit"));
            string aReg = SCP_PersonaCreate.RegistryMetaPath(aData);
            File.WriteAllText(aReg, "{\r\n\t\"_schema_version\":2,\r\n\t\"agent_banks\":{\r\n\t\t\"claude-code\":\"cc\",\r\n\t\t\"Zeta\":\"zeta\"\r\n\t},\r\n\t\"_人加的\":7\r\n}");

            void Person(string p, int iWakes, bool iLock = false, bool iFixture = false)
            {
                string d = SCP_LettersPaths.ProfileDir(aLr, p);
                Directory.CreateDirectory(d);
                File.WriteAllText(Path.Combine(d, "layer_role.md"), p + " 的一句話\n");
                if (iWakes > 0)
                {
                    string w = SCP_LettersPaths.WakesDir(aLr, p);
                    Directory.CreateDirectory(w);
                    for (int i = 1; i <= iWakes; i++) File.WriteAllText(Path.Combine(w, i.ToString("D6") + "_x.md"), "信");
                }
                if (iLock) File.WriteAllText(SCP_LettersPaths.SessionLockPath(aLr, p), "{\"persona\":\"" + p + "\"}");
                if (iFixture) File.WriteAllText(Path.Combine(d, "test_fixture.md"), "1\n");
            }
            Person("woke", 3);
            Person("never", 0);
            Person("online", 5, iLock: true);
            Person("shell", 2, iFixture: true);

            // ① 候選
            string C() => string.Join("\n", SCP_Morning.CandidateLines(aRoots, out _));
            string c1 = SCP_Morning.CandidateLines(aRoots, out int n1).Count > 0 ? C() : "";
            bool aCand = n1 == 2 && c1.Contains("**woke**") && c1.Contains("wake 3") && c1.Contains("從沒醒過") && c1.Contains("**never**")
                         && !c1.Contains("**online**") && !c1.Contains("**shell**") && c1.Contains("在線（不能再登入）：online") && c1.Contains("1 個測試殼");
            File.Delete(Path.Combine(SCP_LettersPaths.ProfileDir(aLr, "shell"), "test_fixture.md"));
            SCP_Morning.CandidateLines(aRoots, out int n2);
            bool aReverse = n2 == 3 && C().Contains("**shell**");
            File.WriteAllText(Path.Combine(SCP_LettersPaths.ProfileDir(aLr, "shell"), "test_fixture.md"), "1\n");

            // ② 規劃：擋
            var rng = new Random(428);
            SCP_PersonaCreatePlan? P(SCP_PersonaCreateSpec s, out string e) => SCP_PersonaCreate.Plan(aLetters, aData, "Ducat", s, "ClaudeCode", rng, "2026-10-06T00:00:00.000Z", out e);
            bool aRefuse = P(new SCP_PersonaCreateSpec { Persona = "woke" }, out string e1) == null && e1.Contains("已經存在")
                           && P(new SCP_PersonaCreateSpec { Persona = "x", Agent = "nosuch" }, out string e2) == null && e2.Contains("不在 agent_banks")
                           && P(new SCP_PersonaCreateSpec { Persona = "x", Agent = "Zeta", NewAgentAccount = "zz" }, out string e3) == null && e3.Contains("已經存在")
                           && P(new SCP_PersonaCreateSpec { Persona = "x", Agent = "Elf", NewAgentAccount = "cc" }, out string e4) == null && e4.Contains("一個帳號不開第二次")
                           && P(new SCP_PersonaCreateSpec { Persona = "_x" }, out _) == null
                           && !Directory.Exists(SCP_LettersPaths.ProfileDir(aLr, "x"));
            // ② 只給名字：agent 由 actual_agent 推、其餘待填
            var pMin = P(new SCP_PersonaCreateSpec { Persona = "frieren" }, out string e5);
            bool aMin = pMin != null && pMin.Agent == "claude-code" && pMin.Account == "cc" && pMin.AgentSource.Contains("建議值")
                        && pMin.Vector.Count == SCP_PersonaCreate.VectorDim && pMin.Pending.Contains("一人稱") && pMin.Pending.Contains("layer_role")
                        && pMin.Character.Contains(SCP_PersonaCreate.PendingMark);

            // ③ fork 與 hash
            string dW = SCP_LettersPaths.ProfileDir(aLr, "woke");
            File.WriteAllText(Path.Combine(dW, "identity_vector.md"), "[0.1234,-1,0.5,0,0.9876,-0.0042]\n");
            File.WriteAllText(Path.Combine(dW, "fork_lineage.md"), "[\"root\"]\n");
            var pFork = P(new SCP_PersonaCreateSpec { Persona = "child", ForkFrom = "woke", Agent = "Zeta" }, out string e6);
            bool aFork = pFork != null && pFork.Vector.Count == 6 && pFork.Vector[0] == 0.1234 && pFork.Lineage.Count == 2
                         && pFork.Lineage[0] == "root" && pFork.Lineage[1] == "woke" && pFork.Account == "zeta"
                         && pFork.VectorHash == "fca542c1";   // python: sha256("0.1234,-1.0000,0.5000,0.0000,0.9876,-0.0042")[:8]

            // ④ 寫入 → Wake 提示補設定
            string e7 = "";
            bool aWrote = pMin != null && SCP_PersonaProfileWrite.Create(aLetters, aData, "frieren", "Ducat", pMin.Account, pMin.Fields,
                              "selftest", "TASK-0428", out _, out e7);
            bool aExists = SCP_PersonaProfile.Exists(aLetters, "frieren") && SCP_PersonaCreate.HasPendingSelfFill(aLetters, "frieren")
                           && !SCP_PersonaCreate.HasPendingSelfFill(aLetters, "woke");
            SCP_MorningStepResult aWake = SCP_Morning.Wake(aRoots, "frieren", "probe-model", "ClaudeCode", "selftest");
            bool aWakeHint = aWake.Ok && aWake.Report.Contains("補完自己的角色設定") && aWake.Report.Contains("character.md");

            // ⑤ 登記 agent
            bool aAdd = SCP_PersonaCreate.AddAgentBank(aData, "Elf", "elf", out string e8);
            string aRegText = File.ReadAllText(aReg);
            bool aNoOverwrite = !SCP_PersonaCreate.AddAgentBank(aData, "Zeta", "other", out _)
                                && SCP_PersonaCreate.AgentBanks(aData).Exists(kv => kv.Key == "Zeta" && kv.Value == "zeta");
            bool aKeep = aAdd && aRegText.Contains("\"_人加的\"") && aRegText.Contains("\r\n") && aRegText.Contains("\"Elf\"");

            bool aOk = aCand && aReverse && aRefuse && aMin && aFork && aWrote && aExists && aWakeHint && aNoOverwrite && aKeep;
            return new CheckRow(aName,
                $"候選 {n1} 位（期望 2：排除在線與測試殼）={aCand}／🔴 拿掉標記後 {n2} 位、測試殼出現={aReverse}"
                + $"／擋已存在／不在表上／撞名／撞帳號／壞名且零寫入={aRefuse}／只給名字可規劃（agent 建議 claude-code→cc、其餘待填）={aMin}"
                + $"／fork 血統與 vector、hash {pFork?.VectorHash}（期望 fca542c1）={aFork}"
                + $"／寫入 {aWrote}{(e7.Length > 0 ? "：" + e7 : "")}、待填偵測={aExists}／Wake 提示補設定={aWakeHint}{(aWake.Ok ? "" : "（Wake 沒過：" + Excerpt(aWake.Report) + "）")}"
                + $"／登記 agent 讀回={aAdd}{(e8.Length > 0 ? "：" + e8 : "")}、不覆蓋={aNoOverwrite}、其他鍵與 CRLF 保留={aKeep}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }

        static string Excerpt(string s) { s = s.Replace("\r", " ").Replace("\n", " "); return s.Length > 160 ? s.Substring(s.Length - 160) : s; }
    }
}
