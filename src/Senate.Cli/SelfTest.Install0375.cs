// 區塊職責：TASK-0375（安裝系統）的自我對拍。
// 物理意義：四格各驗一個「錯了也不會叫」的地方：
//           ① 清單：真的那一份讀得進來；🔴 反向 —— 重複 id／requires 指到不存在的項目／成環，**整份不收**。
//           ② 模型狀態（temp 目錄造假快取）：沒目錄＝沒安裝／必要檔齊＝已安裝／🔴 有 .incomplete ＝不完整（就算必要檔都在）／
//              有檔但缺必要檔＝壞了（不是沒安裝）。
//           ③ 計畫：相依排在前面、已安裝的跳過、量不到的擋下；🔴 還有已安裝的項目需要它 ⇒ 不准拆。
//           ④ skill frontmatter：三種寫法都讀得到；🔴 沒寫那一格 ⇒ null（＝沒宣告），不是空清單。
// 數值影響：只在 temp 目錄造檔，跑完刪；⛔ 不碰真實的 Python 與模型快取，也不起任何子程序。
#nullable enable
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow InstallCatalogShape()
    {
        const string aName = "安裝清單：真檔讀得進來／🔴 重複 id、requires 不存在、成環 ⇒ 整份不收（TASK-0375）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_installcat_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            InstallCatalog? aReal = InstallCatalog.Load(InstallCatalog.DefaultPath(SenateRepoRoot()), out string? aRealErr);
            if (aReal == null) aFails.Add("真清單讀不進來：" + aRealErr);

            Directory.CreateDirectory(aTmp);
            string Write(string iName, string iItems)
            {
                string p = Path.Combine(aTmp, iName);
                File.WriteAllText(p, "{\"schemaVersion\":1,\"items\":[" + iItems + "]}");
                return p;
            }
            const string aA = "{\"id\":\"a\",\"kind\":\"pip\",\"dists\":[\"a\"],\"imports\":[\"a\"],\"pipArgs\":[\"a\"]";
            bool Rejects(string iFile, string iWant)
                => InstallCatalog.Load(iFile, out string? e) == null && (e ?? "").Contains(iWant, StringComparison.Ordinal);
            if (!Rejects(Write("dup.json", aA + "}," + aA + "}"), "id 重複")) aFails.Add("重複 id 沒被擋");
            if (!Rejects(Write("ghost.json", aA + ",\"requires\":[\"nope\"]}"), "不在清單上")) aFails.Add("requires 指到不存在的項目沒被擋");
            if (!Rejects(Write("cycle.json",
                    aA + ",\"requires\":[\"b\"]},{\"id\":\"b\",\"kind\":\"pip\",\"dists\":[\"b\"],\"imports\":[\"b\"],\"pipArgs\":[\"b\"],\"requires\":[\"a\"]}"),
                    "成環")) aFails.Add("成環沒被擋");
            // 對照組：同形但合法的一份要收（⛔ 不然「什麼都擋」也會全綠）
            if (InstallCatalog.Load(Write("ok.json", aA + "}"), out string? aOkErr) == null) aFails.Add("合法的一份被擋了：" + aOkErr);

            return new CheckRow(aName, aFails.Count == 0 ? $"真清單 {aReal!.Items.Count} 項；三種壞法都擋、合法的收" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }

    static CheckRow InstallModelStates()
    {
        const string aName = "安裝・模型狀態：沒目錄＝沒安裝／齊＝已安裝／🔴 有 .incomplete＝不完整／缺必要檔＝壞了（假快取，TASK-0375）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_installmodel_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aEnv = new InstallEnv { ModelsRoot = aTmp.Replace('\\', '/') };
            var aItem = new InstallItem { Id = "m", Kind = InstallKind.HfModel, Repo = "org/name", RequiredFiles = new[] { "config.json", "w.bin" } };
            var aFails = new List<string>();
            void Expect(string iStep, InstallState iWant)
            {
                InstallState s = InstallProbe.ProbeModel(aEnv, aItem).State;
                if (s != iWant) aFails.Add($"{iStep}：要 {iWant} 得 {s}");
            }
            Expect("沒目錄", InstallState.Missing);

            string aSnap = Path.Combine(InstallProbe.ModelDir(aEnv, aItem), "snapshots", "abc123");
            Directory.CreateDirectory(aSnap);
            File.WriteAllText(Path.Combine(aSnap, "config.json"), "{}");
            Expect("缺權重檔", InstallState.Broken);

            File.WriteAllText(Path.Combine(aSnap, "w.bin"), "weights");
            Expect("必要檔齊", InstallState.Installed);

            string aBlobs = Path.Combine(InstallProbe.ModelDir(aEnv, aItem), "blobs");
            Directory.CreateDirectory(aBlobs);
            File.WriteAllText(Path.Combine(aBlobs, "deadbeef.incomplete"), "half");
            Expect("🔴 必要檔齊但有 .incomplete", InstallState.Partial);

            return new CheckRow(aName, aFails.Count == 0 ? "四態逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }

    static CheckRow InstallPlannerRules()
    {
        const string aName = "安裝・計畫：相依在前／已安裝跳過／量不到擋下／🔴 還有已安裝的項目需要它 ⇒ 不准拆（TASK-0375）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_installplan_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            string p = Path.Combine(aTmp, "c.json");
            string Pip(string iId, string iReq) => "{\"id\":\"" + iId + "\",\"kind\":\"pip\",\"dists\":[\"" + iId + "\"],\"imports\":[\"" + iId + "\"],\"pipArgs\":[\"" + iId + "\"]"
                                                   + (iReq.Length > 0 ? ",\"requires\":[" + iReq + "]" : "") + "}";
            // base ← mid ← top；other 獨立
            File.WriteAllText(p, "{\"schemaVersion\":1,\"items\":[" + Pip("top", "\"mid\"") + "," + Pip("mid", "\"base\"") + "," + Pip("base", "") + "," + Pip("other", "") + "]}");
            InstallCatalog aCat = InstallCatalog.Load(p, out string? aErr) ?? throw new InvalidOperationException(aErr);
            // ⚠ 沒列到的項目＝沒安裝。⛔ 不用 default(InstallState) 判「沒給」—— 它是 Unknown，會把顯式給的 Unknown 吃掉。
            List<InstallItemStatus> St(params (string Id, InstallState S)[] iStates)
            {
                var aMap = iStates.ToDictionary(x => x.Id, x => x.S);
                return aCat.Items.Select(i => new InstallItemStatus(i, aMap.TryGetValue(i.Id, out InstallState s) ? s : InstallState.Missing, "", "", 0)).ToList();
            }
            var aFails = new List<string>();

            InstallPlan a = InstallPlanner.PlanInstall(aCat, St(("base", InstallState.Installed)), new[] { "top" });
            string aOrder = string.Join(">", a.Steps.Select(s => s.Item.Id));
            if (aOrder != "mid>top") aFails.Add($"順序要 mid>top（base 已安裝要跳過），得 {aOrder}");

            InstallPlan b = InstallPlanner.PlanInstall(aCat, St(("base", InstallState.Unknown)), new[] { "top" });
            if (b.Blocked.Count == 0) aFails.Add("相依量不到時沒有擋下");

            var aEnv = new InstallEnv();   // 沒有 Python ⇒ InUse 不會去找程序
            string? c = InstallPlanner.CheckUninstall(aCat, aEnv, St(("base", InstallState.Installed), ("mid", InstallState.Installed), ("top", InstallState.Installed)), "base");
            if (c == null || !c.Contains("mid") || !c.Contains("top")) aFails.Add($"🔴 base 還被 mid／top 需要卻沒擋（得 {c ?? "null"}）");
            // 對照組：沒有人需要它的那一個要放行（⛔ 不然「全部都擋」也會全綠）
            string? d = InstallPlanner.CheckUninstall(aCat, aEnv, St(("other", InstallState.Installed), ("base", InstallState.Installed)), "other");
            if (d != null) aFails.Add("沒有人需要的 other 被擋了：" + d);

            return new CheckRow(aName, aFails.Count == 0 ? "順序 mid>top、量不到擋、被需要的不准拆、獨立的放行" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }

    static CheckRow InstallSkillRequires()
    {
        const string aName = "安裝・skill 相依宣告：[a, b]／a, b／- a 清單都讀得到；🔴 沒寫 ⇒ null 不是空清單（TASK-0375）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_installskill_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(aTmp);
            string F(string iName, string iFront)
            {
                string p = Path.Combine(aTmp, iName);
                File.WriteAllText(p, "---\nname: x\n" + iFront + "---\n\n# body\nrequires_install: [不算，這行在本文]\n");
                return p;
            }
            var aFails = new List<string>();
            string J(List<string>? l) => l == null ? "null" : string.Join(",", l);
            string r1 = J(InstallRequire.ReadSkillRequires(F("a.md", "requires_install: [model-bge-m3, py-flagembedding]\n")));
            string r2 = J(InstallRequire.ReadSkillRequires(F("b.md", "requires_install: model-bge-m3, py-flagembedding\n")));
            string r3 = J(InstallRequire.ReadSkillRequires(F("c.md", "requires_install:\n  - model-bge-m3\n  - py-flagembedding\n")));
            string r4 = J(InstallRequire.ReadSkillRequires(F("d.md", "description: 沒有相依\n")));
            const string aWant = "model-bge-m3,py-flagembedding";
            if (r1 != aWant) aFails.Add("[a, b] 得 " + r1);
            if (r2 != aWant) aFails.Add("a, b 得 " + r2);
            if (r3 != aWant) aFails.Add("- 清單得 " + r3);
            if (r4 != "null") aFails.Add("🔴 沒寫那一格得 " + r4 + "（要 null）");
            return new CheckRow(aName, aFails.Count == 0 ? "三種寫法一致、沒寫＝null、本文裡的同名字不算" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }

    static CheckRow InstallEnvResolve()
    {
        const string aName = "安裝・環境解析：空白＝系統 Python／指定空資料夾＝可建 venv（不退回系統那顆）／指定非空無 Python＝不建／有 python.exe＝用它（TASK-0375）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_installenv_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            InstallEnv R(string iPy) => InstallEnv.Resolve(new SenateConfig { Install = new InstallSettings { PythonEnvRoot = iPy, ModelsRoot = "" } });

            InstallEnv a = R("");
            if (a.PythonOrigin.Contains("手填")) aFails.Add("空白卻當成手填");
            if (a.ModelsRoot != InstallEnv.DefaultModelsRoot()) aFails.Add("模型位置空白沒有走 HF 預設：" + a.ModelsRoot);

            string aEmpty = Path.Combine(aTmp, "empty");
            InstallEnv b = R(aEmpty);   // 不存在的資料夾也算「空」：可以在那裡建
            if (b.PythonExe != null) aFails.Add("🔴 指定了空資料夾卻退回某一顆 Python：" + b.PythonExe);
            if (b.CreatableEnvDir == null) aFails.Add("指定空資料夾卻沒有給「可以建 venv」");

            string aJunk = Path.Combine(aTmp, "junk");
            Directory.CreateDirectory(aJunk);
            File.WriteAllText(Path.Combine(aJunk, "readme.txt"), "x");
            InstallEnv c = R(aJunk);
            if (c.PythonExe != null || c.CreatableEnvDir != null) aFails.Add("🔴 非空、沒有 Python 的資料夾不該用也不該建");

            string aVenv = Path.Combine(aTmp, "venv");
            Directory.CreateDirectory(Path.Combine(aVenv, "Scripts"));
            File.WriteAllText(Path.Combine(aVenv, "Scripts", "python.exe"), "");
            File.WriteAllText(Path.Combine(aVenv, "pyvenv.cfg"), "home = x");
            InstallEnv d = R(aVenv);
            if (d.PythonExe == null || !d.PythonExe.EndsWith("Scripts/python.exe", StringComparison.Ordinal) || !d.IsVenv)
                aFails.Add($"venv 資料夾沒有認出來（{d.PythonExe ?? "null"}，venv={d.IsVenv}）");

            return new CheckRow(aName, aFails.Count == 0 ? "四種情況逐格對上" : string.Join("；", aFails),
                                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch { } }
    }
}
