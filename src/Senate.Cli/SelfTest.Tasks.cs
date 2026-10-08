// 區塊職責：任務單寫入端（TASK-0349）的 selftest —— 三格：
//   ① 真單逐位元組對拍：既有單經寫入端 `Render` 重排必須逐字相同（行尾無關）
//   ② 配號與讀改寫的淨室：多執行緒同時開單不撞號、建構丟例外不吃號、撞檔往下一號、同時留言不掉則
//   ③ op 閘的淨室：必填／QA／blocker／dry-run／冪等 —— **擋下＝零位元組**（對單檔做前後位元組比對，不信回傳值）
// 物理意義：驗收條文 ②（配號有讀數，不是推論）與 ⑥（selftest）的那幾格。
// ⚠ ② 的射程：它量的是**同一顆 process 的多條執行緒**（`Monitor` ＋ 檔案鎖都在路上）；
//   跨 process 那一格由 `SCP_FileLock`（TASK-0263 已有自己的淨室）與「寫入端只在 Server」兩件事擔保，這裡沒有另外 spawn process。
#nullable enable
using System.Collections.Concurrent;
using SCP.Core.Tasks;
using Senate.Core;
using System.Text.RegularExpressions;

namespace Senate.Cli;

public static partial class SelfTest
{
    static IEnumerable<CheckRow> RealTaskRenderRoundTrip(IReadOnlyList<SelfTestTarget> iTargets)
    {
        bool aAny = false;
        foreach (SelfTestTarget p in iTargets)
        {
            if (p.AgentCommandsRoot == null) continue;
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(p.AgentCommandsRoot);
            string aDir = SCP_TaskIO.TasksDir(aRoot);
            if (!Directory.Exists(aDir)) continue;
            aAny = true;
            int aSame = 0;
            var aDiff = new List<string>();
            int aLegacy = 0;
            var aLegacyKeys = new List<string>();
            string[] aFiles = Directory.GetFiles(aDir, "*.md");
            foreach (string f in aFiles)
            {
                SCP_TaskEntry? e = SCP_TaskIO.LoadFile(f);
                if (e == null) { aDiff.Add(Path.GetFileName(f) + "（解析不了）"); continue; }
                e.resolution_note = SCP_TaskStore.ReadSection(f, "## 結單說明");
                string aBack = SCP_TaskStore.Render(e, SCP_TaskStore.ReadSection(f, "## 驗收標準"),
                    SCP_TaskStore.ReadSection(f, "## 任務描述"), SCP_TaskStore.ReadTimeline(f));
                string aDisk = File.ReadAllText(f, new System.Text.UTF8Encoding(false));
                if (Eol(aDisk) == Eol(aBack)) { aSame++; continue; }
                // ⚠ 舊單形狀（2026-09-30 讀數：352 張裡 32 張）—— 舊寫入端重寫它們時**同樣會改成新形狀**，那不是新舊寫入端的分歧：
                //   · 缺鍵：`last_wrapup_at`（2026-08-25 才加）／`memory_*` 之前開的單沒有那幾行 ⇒ 重寫補一行空值
                //   · 缺 `## 留言` 區塊（留言功能之前的單）⇒ 重寫補空區塊
                //   · 檔尾多餘空行（手改過）⇒ 重寫收掉
                //   ⇒ 只容許這三種、逐一還原後再比；其餘一律算**真不符**。
                string aStripped = StripAddedEmptyKeys(Eol(aDisk), Eol(aBack), out string aKeys);
                string aDiskN = Eol(aDisk);
                if (!aDiskN.Contains("\n## 留言\n", StringComparison.Ordinal))
                {
                    aStripped = aStripped.Replace("## 留言\n\n_(還沒有人留言)_\n\n", "", StringComparison.Ordinal);
                    aKeys += (aKeys.Length > 0 ? "+" : "") + "留言區塊";
                }
                // 多餘空行（手改過：區塊尾或檔尾多了空行，`ReadSection` 會 Trim 掉）—— 只壓空行，⛔ 不動任何有字的行
                if (CollapseBlank(aDiskN) == CollapseBlank(aStripped))
                {
                    aLegacy++;
                    aLegacyKeys.Add((aKeys.Length > 0 ? aKeys : "") + (aDiskN.TrimEnd('\n') == aStripped.TrimEnd('\n') ? "" : (aKeys.Length > 0 ? "+" : "") + "多餘空行"));
                    continue;
                }
                aDiff.Add(Path.GetFileName(f) + FirstDiff(Eol(aDisk), Eol(aBack)));
            }
            yield return new CheckRow($"任務單逐位元組對拍（{p.Name}）",
                $"{aFiles.Length} 張／新寫入端重排逐字相符 {aSame}"
                + $"／舊單形狀、重寫只會補空欄／收空行 {aLegacy}（{string.Join("、", aLegacyKeys.Distinct())}）"
                + (aDiff.Count == 0 ? "／真不符 0" : $"／**真不符 {aDiff.Count}**：{string.Join("；", aDiff.Take(4))}"),
                aDiff.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        if (!aAny) yield return new CheckRow("任務單逐位元組對拍", "沒有任何專案的 Tasks/tasks 可讀", CheckResult.Skipped);
    }

    static string CollapseBlank(string s) => Regex.Replace(s.TrimEnd('\n'), "\n{3,}", "\n\n");

    /// <summary>重排裡「磁碟 frontmatter 完全沒有、而重排補了一行 `key: `（空值）」的那些行拿掉。</summary>
    static string StripAddedEmptyKeys(string iDisk, string iBack, out string oKeys)
    {
        var aDiskKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string l in iDisk.Split('\n'))
        {
            if (l.Length == 0 || l[0] == ' ') continue;
            int c = l.IndexOf(':');
            if (c > 0) aDiskKeys.Add(l.Substring(0, c));
        }
        var aKeys = new List<string>();
        var aOut = new List<string>();
        foreach (string l in iBack.Split('\n'))
        {
            int c = l.IndexOf(':');
            if (c > 0 && l[0] != ' ' && l.Substring(c + 1).Trim().Length == 0 && !aDiskKeys.Contains(l.Substring(0, c))
                && l.IndexOf(' ') > c) { aKeys.Add(l.Substring(0, c)); continue; }
            aOut.Add(l);
        }
        oKeys = string.Join("+", aKeys);
        return string.Join("\n", aOut);
    }

    /// <summary>第一個不同的位置（行號＋兩邊那一行的前 60 字）—— 不符時要看得出是哪一格漂了。</summary>
    static string FirstDiff(string a, string b)
    {
        string[] la = a.Split('\n'), lb = b.Split('\n');
        for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
        {
            string x = i < la.Length ? la[i] : "(EOF)", y = i < lb.Length ? lb[i] : "(EOF)";
            if (x != y) return $" L{i + 1} 磁碟「{Cut(x)}」≠ 重排「{Cut(y)}」";
        }
        return "";
        static string Cut(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "…";
    }

    static CheckRow TaskStoreCleanRoom()
    {
        const string aName = "任務寫入端：配號／讀改寫（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_taskstore_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(aTmp);
            SCP_TaskEntry Mk(int i) => new SCP_TaskEntry { index = i, title = "探針 " + i, reporter = "Template", created_at = "2026-09-30T00:00:00.000Z", updated_at = "2026-09-30T00:00:00.000Z" };

            // ① 8 條執行緒 × 5 張同時開 ⇒ 40 張、號碼 1..40 恰好各一次
            var aGot = new ConcurrentBag<int>();
            Parallel.For(0, 40, new ParallelOptions { MaxDegreeOfParallelism = 8 }, _ =>
                aGot.Add(SCP_TaskStore.Create(aRoot, n => (Mk(n), SCP_TaskWrite.Body("t　`todo`　由 Template 開單", "- [ ] a", ""))).Index));
            var aSorted = aGot.OrderBy(x => x).ToList();
            bool aNoDup = aSorted.SequenceEqual(Enumerable.Range(1, 40));
            int aFiles = Directory.GetFiles(SCP_TaskIO.TasksDir(aRoot), "*.md").Length;
            int aCounter = SCP_TaskIO.ReadCurrentIndex(aRoot);

            // ② 建構丟例外 ⇒ 沒有檔、計數檔不動（🩸 0351／0352 那一族）
            bool aThrew = false;
            try { SCP_TaskStore.Create(aRoot, n => throw new InvalidOperationException("參數不合法（探針）")); }
            catch (InvalidOperationException) { aThrew = true; }
            bool aNoConsume = aThrew && SCP_TaskIO.ReadCurrentIndex(aRoot) == 40
                              && !File.Exists(SCP_TaskIO.TaskPath(aRoot, 41));
            int aNext = SCP_TaskStore.Create(aRoot, n => (Mk(n), SCP_TaskWrite.Line("t　`todo`　由 Template 開單"))).Index;

            // ③ 撞檔：下一號被人手建 ⇒ 往下一號，佔位檔一個位元組都不動
            string aSquat = SCP_TaskIO.TaskPath(aRoot, 42);
            File.WriteAllText(aSquat, "佔位", new System.Text.UTF8Encoding(false));
            // 計數檔落後（模擬「有人建了 42 卻沒寫回計數檔」之外的另一形：計數檔還是 41，磁碟最大檔名 42）
            SCP_TaskCreateResult aHealed = SCP_TaskStore.Create(aRoot, n => (Mk(n), SCP_TaskWrite.Line("t　`todo`　由 Template 開單")));
            bool aSquatIntact = File.ReadAllText(aSquat) == "佔位";

            // ④ 20 條執行緒同時對同一張留言 ⇒ 20 則、id 1..20
            Parallel.For(0, 20, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
                SCP_TaskStore.Mutate(aRoot, 1, m =>
                {
                    int aId = SCP_TaskStore.NextCommentId(m);
                    m.comments.Add(new SCP_TaskComment { id = aId, persona = "Template", at = "2026-09-30T00:00:00.000Z", body = "第 " + i + " 則\n## 驗收標準（內文裡的標題要逃脫）" });
                    return SCP_TaskWrite.Line("t　`comment`　Template 留言 #" + aId);
                }));
            SCP_TaskEntry? aOne = SCP_TaskIO.Find(aRoot, 1);
            bool aComments = aOne != null && aOne.comments.Select(c => c.id).OrderBy(x => x).SequenceEqual(Enumerable.Range(1, 20))
                             && aOne.comments.All(c => c.body.Contains("## 驗收標準（內文", StringComparison.Ordinal));
            int aTimeline = SCP_TaskStore.ReadTimeline(SCP_TaskIO.TaskPath(aRoot, 1)).Count;

            // ⑤ 雙向連結在鎖內寫兩張
            SCP_TaskStore.Link(aRoot, 2, 3, "blocked_by", "Template", out string aErr);
            SCP_TaskEntry? a2 = SCP_TaskIO.Find(aRoot, 2), a3 = SCP_TaskIO.Find(aRoot, 3);
            bool aLink = aErr.Length == 0 && a2 != null && a3 != null && a2.blocked_by.Contains(3) && a3.blocks.Contains(2);

            bool aOk = aNoDup && aFiles == 40 && aCounter == 40 && aNoConsume && aNext == 41
                       && aHealed.Index == 43 && aHealed.HealAttempts == 0 && aSquatIntact
                       && aComments && aTimeline == 21 && aLink;
            return new CheckRow(aName,
                $"並行開 40 張 ⇒ 號碼不重複且連續={aNoDup}、檔 {aFiles}、計數檔 {aCounter}"
                + $"｜建構丟例外 ⇒ 沒吃號={aNoConsume}（下一張拿到 {aNext}）"
                + $"｜42 被手建 ⇒ 下一張 {aHealed.Index}（計數檔落後讀數：{(aHealed.CounterNote.Length > 0 ? "有" : "無")}）、佔位檔不動={aSquatIntact}"
                + $"｜同時 20 則留言 ⇒ id 1..20 且內文 `##` 保留={aComments}、時間線 {aTimeline} 行"
                + $"｜雙向連結={aLink}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }

    static CheckRow TaskOpsGatesCleanRoom()
    {
        const string aName = "任務寫入 op 的閘（淨室；擋下＝零位元組）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_taskops_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(aTmp);
            SCP_TaskOpResult Run(string op, string who, params (string k, string v)[] kv)
                => SCP_TaskOps.Run(aRoot, op, who, kv.ToDictionary(x => x.k, x => x.v));
            string Bytes(int i) { string p = SCP_TaskIO.TaskPath(aRoot, i); return File.Exists(p) ? File.ReadAllText(p) : "(不存在)"; }
            var aFail = new List<string>();
            void Expect(bool c, string what) { if (!c) aFail.Add(what); }

            // create：缺 criteria ⇒ 擋、零檔；priority 打錯 ⇒ 擋、⛔ 不吃號（0351／0352 的原型）
            Expect(Run("create", "Template", ("title", "探針")).ExitCode == 1, "缺 criteria 沒擋");
            Expect(Run("create", "Template", ("title", "探針"), ("criteria", "- [ ] a"), ("priority", "medium")).ExitCode == 1, "priority=medium 沒擋");
            Expect(!Directory.Exists(SCP_TaskIO.TasksDir(aRoot)) || Directory.GetFiles(SCP_TaskIO.TasksDir(aRoot), "*.md").Length == 0, "被擋的 create 留了檔");
            SCP_TaskOpResult c1 = Run("create", "Template", ("title", "探針一"), ("criteria", "- [ ] 第一格\n- [ ] 第二格"));
            Expect(c1.ExitCode == 0 && c1.Index == 1 && c1.Notices.Count == 1, $"create 沒拿到 1 號（{c1.Index}）或沒組通知");
            SCP_TaskOpResult bug = Run("create", "Template", ("title", "探針二"), ("type", "bug"), ("evidence", "讀數"));
            Expect(bug.Index == 2 && SCP_TaskStore.ReadCriteria(aRoot, 2).Contains("Fixes TASK-0002", StringComparison.Ordinal), "bug 骨架沒帶 Fixes TASK-0002");

            // check：非參與者 ⇒ 擋、零位元組；開單人 dry-run ⇒ 零位元組；expect_text 對不上 ⇒ 零位元組
            string b0 = Bytes(1);
            Expect(Run("check", "summit", ("index", "1"), ("criteria_index", "1")).ExitCode == 1 && Bytes(1) == b0, "非參與者勾格沒擋或寫了");
            Expect(Run("check", "Template", ("index", "1")).ExitCode == 0 && Bytes(1) == b0, "dry-run 寫了");
            Expect(Run("check", "Template", ("index", "1"), ("criteria_index", "1"), ("expect_text", "第二格")).ExitCode == 1 && Bytes(1) == b0, "expect_text 對不上沒擋");
            SCP_TaskOpResult ck = Run("check", "Template", ("index", "1"), ("criteria_index", "2"), ("expect_text", "第二格"));
            Expect(ck.ExitCode == 0 && SCP_TaskStore.ReadCriteria(aRoot, 1).Contains("- [x] 第二格　✅ Template", StringComparison.Ordinal), "勾第 2 格沒簽名");

            // update：推 done ⇒ 擋；優先級 → high 寫進時間線
            b0 = Bytes(1);
            Expect(Run("update", "Template", ("index", "1"), ("status", "done")).ExitCode == 1 && Bytes(1) == b0, "update 推 done 沒擋");
            Expect(Run("update", "Template", ("index", "1"), ("priority", "high")).Wrote == 1, "update priority 沒寫");

            // QA 閘：指派 summit 為 qa ⇒ Template 結單沒帶 qa_note ⇒ 擋；dry-run ⇒ 零位元組
            Run("assign", "Template", ("index", "1"), ("target_persona", "summit"), ("role", "qa"));
            b0 = Bytes(1);
            Expect(Run("resolve", "Template", ("index", "1"), ("confirm", "1")).ExitCode == 1 && Bytes(1) == b0, "QA 閘沒擋");
            Expect(Run("resolve", "summit", ("index", "1")).ExitCode == 0 && Bytes(1) == b0, "resolve dry-run 寫了");

            // blocker 閘：1 被 2 阻塞 ⇒ QA 本人結 done 也擋；commit fixes 不推進
            Run("link", "Template", ("index", "1"), ("target", "TASK-0002"), ("op_link", "blocked_by"));
            b0 = Bytes(1);
            Expect(Run("resolve", "summit", ("index", "1"), ("confirm", "1")).ExitCode == 1 && Bytes(1) == b0, "blocker 閘沒擋");
            SCP_TaskOpResult cm = Run("commit", "Template", ("index", "1"), ("sha", "abc1234"), ("mode", "fixes"));
            Expect(cm.ExitCode == 0 && SCP_TaskIO.Find(aRoot, 1)!.status == SCP_TaskStatus.todo && cm.Notices.Count == 0, "有 blocker 的 commit fixes 推了狀態或發了通知");

            // 解掉 blocker ⇒ commit fixes 推 in_review（有 QA）
            Run("resolve", "Template", ("index", "2"), ("status", "cancelled"), ("confirm", "1"));
            SCP_TaskOpResult cm2 = Run("commit", "Template", ("index", "1"), ("sha", "def5678"));
            Expect(SCP_TaskIO.Find(aRoot, 1)!.status == SCP_TaskStatus.in_review && cm2.Notices.Count == 1, "commit fixes 沒推 in_review");

            // wrapup：last_wrapup_at == updated_at；wrapup_skip 寫進時間線
            Run("wrapup", "summit", ("index", "1"), ("progress", "還剩 QA"));
            SCP_TaskEntry w = SCP_TaskIO.Find(aRoot, 1)!;
            Expect(w.last_wrapup_at.Length > 0 && w.last_wrapup_at == w.updated_at, "wrapup 的等號陷阱");
            Run("wrapup_skip", "summit", ("index", "1"), ("reason", "今晚沒東西"));
            Expect(SCP_TaskStore.ReadTimeline(SCP_TaskIO.TaskPath(aRoot, 1)).Any(l => l.Contains("`wrapup-skip`　summit 顯式跳過收工：今晚沒東西", StringComparison.Ordinal)), "wrapup_skip 沒進時間線");

            // 未知 op ⇒ exit 1 且回報有原因
            SCP_TaskOpResult bad = Run("nonsense", "Template");
            Expect(bad.ExitCode == 1 && bad.Report.ToString().Contains("## ❌ 失敗", StringComparison.Ordinal), "未知 op 沒擋");

            return new CheckRow(aName, aFail.Count == 0 ? "create／check／update／assign／resolve／link／commit／wrapup／wrapup_skip 各閘全過（擋下皆零位元組）"
                                                       : "不過：" + string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }
}
