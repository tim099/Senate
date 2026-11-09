// 區塊職責：TASK-0406（skill 入口化）的自我對拍。
// 物理意義：淨室驗「錯了也不會叫」的地方：
//           入口模式的源檔 → 內容組合（整份／章節／程式碼區塊裡的 # 不是標題）、缺一份就整份失敗且不給內容、
//              改文件後下一次就是新內容、安裝只落一支 SKILL.md、Antigravity 帶 trigger、鏡像改入口後殘檔被清、
//              沒有觸發詞時退到 always_on（與 python 同字面）。
// 數值影響：在 temp 造目錄、暫時換掉文件庫的根，跑完還原並刪除。
#nullable enable
using SCP.Core.Docs;
using SCP.Core.Skills;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow SkillEntryCleanRoom()
    {
        const string aName = "skill 入口化：合併／章節／缺一份整份失敗／改文件即時生效／只裝入口檔／Antigravity trigger／鏡像改入口清殘檔／無觸發詞退 always_on（淨室，TASK-0406）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_skill0406_" + Guid.NewGuid().ToString("N")[..8]);
        Func<IReadOnlyList<SCP_DocRoot>>? aOldRoots = SCP_DocStore.RootsProvider;
        var aFails = new List<string>();
        try
        {
            string aSkillsRoot = Path.Combine(aTmp, "Skills~");
            string aDocs = Path.Combine(aTmp, "Docs");
            string aProj = Path.Combine(aTmp, "proj");
            Directory.CreateDirectory(Path.Combine(aSkillsRoot, "scp-probe"));
            Directory.CreateDirectory(aDocs);
            Directory.CreateDirectory(aProj);
            SCP_DocStore.RootsProvider = () => new[] { new SCP_DocRoot("probe", aDocs) };

            File.WriteAllText(Path.Combine(aSkillsRoot, "scp-probe", "SKILL.md"),
                "---\nname: scp-probe\ndescription: |\n  探針 skill。\n  觸發詞：探針大小姐 / probe wake / 探一下\ndocs:\n  - ProbeA\n  - ProbeB#乙節\n---\n\n這段源檔內文不該被裝出去。\n");
            File.WriteAllText(Path.Combine(aDocs, "ProbeA.md"), "---\ntitle: A\n---\n\n# 甲文件\n\n甲的內容 v1\n");
            File.WriteAllText(Path.Combine(aDocs, "ProbeB.md"),
                "# B 文件\n\n## 乙節\n\n乙的內容\n\n```bash\n# 這是註解不是標題\necho hi\n```\n\n乙節還沒完\n\n## 丙節\n\n丙的內容不該出現\n");

            // ① 合併＋章節（程式碼區塊裡的 # 不切斷）
            bool aOk = SCP_SkillContent.TryCompose(aSkillsRoot, "scp-probe", out List<SCP_SkillPart> aParts, out List<string> aErrs);
            string aAll = string.Join("\n", aParts.Select(x => x.Body));
            if (!aOk || aParts.Count != 2) aFails.Add($"合併：ok={aOk} parts={aParts.Count} errs={string.Join("|", aErrs)}");
            else
            {
                if (!aAll.Contains("甲的內容 v1")) aFails.Add("合併：少了甲");
                if (!aAll.Contains("乙節還沒完")) aFails.Add("章節：被程式碼區塊裡的 # 切斷");
                if (aAll.Contains("丙的內容")) aFails.Add("章節：沒在下一個同級標題停下");
            }

            // ② 改文件 ⇒ 下一次就是新內容（反向對照：舊字不再出現）
            File.WriteAllText(Path.Combine(aDocs, "ProbeA.md"), "# 甲文件\n\n甲的內容 v2\n");
            SCP_SkillContent.TryCompose(aSkillsRoot, "scp-probe", out List<SCP_SkillPart> aParts2, out _);
            string aAll2 = string.Join("\n", aParts2.Select(x => x.Body));
            if (!aAll2.Contains("v2") || aAll2.Contains("v1")) aFails.Add("改文件後沒有即時生效");

            // 🔴 ③ 缺一份 ⇒ 整份失敗、零內容、錯誤指名
            File.Delete(Path.Combine(aDocs, "ProbeB.md"));
            bool aOk3 = SCP_SkillContent.TryCompose(aSkillsRoot, "scp-probe", out List<SCP_SkillPart> aParts3, out List<string> aErrs3);
            if (aOk3 || aParts3.Count != 0 || !aErrs3.Any(e => e.Contains("ProbeB"))) aFails.Add($"缺文件：ok={aOk3} parts={aParts3.Count}（該是 false／0 且指名 ProbeB）");

            // ④ 安裝：只落一支 SKILL.md（＋標記）；內文不帶；Antigravity 帶 trigger；重跑零寫入
            SCP_SkillSyncResult aS1 = SCP_SkillInstall.Sync(aSkillsRoot, SCP_SkillTarget.Claude, aProj, "scp-probe");
            SCP_SkillSyncResult aS2 = SCP_SkillInstall.Sync(aSkillsRoot, SCP_SkillTarget.Antigravity, aProj, "scp-probe");
            string aClDir = SCP_SkillTarget.Claude.SkillDir(aProj, "scp-probe");
            string aAgFile = Path.Combine(SCP_SkillTarget.Antigravity.SkillDir(aProj, "scp-probe"), "SKILL.md");
            string[] aClFiles = Directory.GetFiles(aClDir).Select(Path.GetFileName).Where(n => n != SCP_SkillSource.MarkerFileName).ToArray()!;
            string aClText = File.ReadAllText(Path.Combine(aClDir, "SKILL.md"));
            if (!aS1.Ok || !aS2.Ok) aFails.Add("安裝失敗：" + aS1.Message + "／" + aS2.Message);
            if (aClFiles.Length != 1) aFails.Add("入口模式裝了 " + aClFiles.Length + " 支檔（該是 1）");
            if (aClText.Contains("這段源檔內文") || aClText.Contains("docs:") || !aClText.Contains(SCP_SkillEntry.ShowCommand("scp-probe")))
                aFails.Add("入口檔內容不對（帶了源檔內文／docs:，或少了那一行指令）");
            string aAgText = File.ReadAllText(aAgFile);
            if (!aAgText.StartsWith("---\ntrigger: { on_intent: [\"探針大小姐\", \"probe wake\", \"探一下\"] }", StringComparison.Ordinal))
                aFails.Add("Antigravity 沒帶對的 trigger：" + aAgText.Split('\n')[1]);
            SCP_SkillSyncResult aS3 = SCP_SkillInstall.Sync(aSkillsRoot, SCP_SkillTarget.Claude, aProj, "scp-probe");
            List<SCP_SkillStatus> aSt = SCP_SkillInstall.Status(aSkillsRoot, SCP_SkillTarget.Antigravity, aProj);
            if (aS3.Copied != 0 || aSt.First(x => x.Name == "scp-probe").State != SCP_SkillState.Synced) aFails.Add($"重跑：寫了 {aS3.Copied} 檔／狀態 {aSt.First(x => x.Name == "scp-probe").State}（該是 0／Synced）");

            // ⑤ 鏡像 → 入口：舊的鏡像殘檔被清（標記檔留著）
            File.WriteAllText(Path.Combine(aClDir, "OLD.md"), "鏡像時代的附檔");
            SCP_SkillInstall.Sync(aSkillsRoot, SCP_SkillTarget.Claude, aProj, "scp-probe");
            if (File.Exists(Path.Combine(aClDir, "OLD.md")) || !File.Exists(Path.Combine(aClDir, SCP_SkillSource.MarkerFileName)))
                aFails.Add("鏡像改入口：殘檔沒清，或標記檔被誤刪");

            // ⑥ 沒有觸發詞 ⇒ 與 python 同字面的 always_on
            string aNone = SCP_SkillEntry.DeriveAntigravityTrigger("---\nname: x\ndescription: 沒有那一行\n---\n", "x");
            if (aNone != "\"always_on\"") aFails.Add("無觸發詞：得到 " + aNone);

            // ⑦ 撞名只擋查那個名字的人（TASK-0999）：兩個根各一份 Dup.md
            string aDocs2 = Path.Combine(aTmp, "Docs2");
            Directory.CreateDirectory(aDocs2);
            File.WriteAllText(Path.Combine(aDocs, "Dup.md"), "# 第一份 Dup\n\n一號的內容\n");
            File.WriteAllText(Path.Combine(aDocs2, "Dup.md"), "# 第二份 Dup\n\n二號的內容\n");
            SCP_DocStore.RootsProvider = () => new[] { new SCP_DocRoot("probe", aDocs), new SCP_DocRoot("probe2", aDocs2) };
            void Skill(string iName, string iDocs) { Directory.CreateDirectory(Path.Combine(aSkillsRoot, iName)); File.WriteAllText(Path.Combine(aSkillsRoot, iName, "SKILL.md"), "---\nname: " + iName + "\ndescription: 探針\ndocs:\n" + iDocs + "---\n"); }
            Skill("scp-probe-a", "  - ProbeA\n");
            Skill("scp-probe-dup", "  - Dup\n");
            Skill("scp-probe-full", "  - probe2/Dup\n");
            //   ⑦a 沒用到撞名的那份 ⇒ 照樣組出來、撞名進警告（以前：整份失敗、一個字都不給）
            bool aOk7 = SCP_SkillContent.TryCompose(aSkillsRoot, "scp-probe-a", out List<SCP_SkillPart> aP7, out List<string> aE7, out List<string> aW7);
            if (!aOk7 || aP7.Count != 1 || !aW7.Any(w => w.Contains("Dup"))) aFails.Add($"撞名不相干：ok={aOk7} parts={aP7.Count} warn={string.Join("|", aW7)}（該是 true／1、警告提到 Dup）");
            //   ⑦b 用到撞名的那份 ⇒ 整份失敗、列出兩份的全名
            bool aOk8 = SCP_SkillContent.TryCompose(aSkillsRoot, "scp-probe-dup", out List<SCP_SkillPart> aP8, out List<string> aE8, out _);
            string aE8s = string.Join("\n", aE8);
            if (aOk8 || aP8.Count != 0 || !aE8s.Contains("probe/Dup") || !aE8s.Contains("probe2/Dup")) aFails.Add($"撞名相干：ok={aOk8} parts={aP8.Count}（該是 false／0、列出 probe/Dup 與 probe2/Dup）errs={aE8s}");
            //   ⑦c docs: 寫全名 ⇒ 解得開、拿到第二份
            bool aOk9 = SCP_SkillContent.TryCompose(aSkillsRoot, "scp-probe-full", out List<SCP_SkillPart> aP9, out List<string> aE9, out _);
            if (!aOk9 || aP9.Count != 1 || !aP9[0].Body.Contains("二號的內容")) aFails.Add($"全名：ok={aOk9} errs={string.Join("|", aE9)}（該拿到 probe2 那一份）");
            //   ⑦d 查名字：全名（含 .md、反斜線）只回一份；短名回兩份；FindCollisions 一組
            SCP_DocStore.TryList(out List<SCP_DocEntry> aAll7, out List<SCP_DocProblem> aPr7, out _);
            if (SCP_DocStore.FindByName(aAll7, "probe2\\Dup.md").Count != 1 || SCP_DocStore.FindByName(aAll7, "dup").Count != 2 || SCP_DocStore.FindCollisions(aAll7).Count != 1
                || !aPr7.Any(p => p.Kind == SCP_DocProblem.ProblemKind.Collision && p.Name == "Dup"))
                aFails.Add("查名字：全名／短名／撞名組數／問題種類不對");

            return new CheckRow(aName, aFails.Count == 0 ? "十一格逐格對上（temp 目錄、暫換文件根；撞名只擋用到的、全名解得開）" : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally
        {
            SCP_DocStore.RootsProvider = aOldRoots;
            try { Directory.Delete(aTmp, true); } catch { }
        }
    }
}
