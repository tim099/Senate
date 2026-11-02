// 區塊職責：任務後台頁的**打勾**淨室（Tim 2026-11-02「TaskManagerPage 可以支援打勾功能嗎」）——
//           ① 頁面預先判「這個署名勾不勾得掉」（TaskManagerPage.CheckBlocker）跟寫入端 `op=check` 的閘逐格一致；
//           ② 錨（expect_text）切在 `|` 之前、不切半個代理對；
//           ③ 真的畫頁面、真的按：按一次只是備妥（零寫入），再按一次才送 `op=check`，送出的參數交給寫入端落得了檔、簽名是署名的人。
// 物理意義：③ 換掉頁面的寫入那一步（TaskDispatch）—— 淨室不能真的跑 cmd task（會發酒館通知、sync.tasks 開著還會推遠端）；
//           換上的那一步直接呼叫寫入端本體 SCP_TaskOps，閘與落檔都是真的。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Gui;
using SCP.Core.Tasks;
using Senate.Cli.Pages;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow TaskTickCleanRoom()
    {
        const string aName = "任務後台頁打勾（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_tasktick_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aCfgDir = Path.Combine(aTmp, "SenateData", "config");
            Directory.CreateDirectory(aCfgDir);
            string aData = aTmp.Replace(Path.DirectorySeparatorChar, '/') + "/AgentCommands";
            Directory.CreateDirectory(aData);
            File.WriteAllText(Path.Combine(aCfgDir, "senate.local.json"),
                "{\n  \"schemaVersion\": 1,\n  \"paths\": {\n    \"agentCommandsRoot\": \"" + aData + "\"\n  }\n}\n");
            var aRoot = new SCP.Core.Paths.SCP_DataRoot(aData);
            var aFail = new List<string>();
            void Expect(bool c, string what) { if (!c) aFail.Add(what); }
            SCP_TaskOpResult Op(string op, string who, params (string k, string v)[] kv)
                => SCP_TaskOps.Run(aRoot, op, who, kv.ToDictionary(x => x.k, x => x.v));
            string Bytes(int i) => File.ReadAllText(SCP_TaskIO.TaskPath(aRoot, i));

            // 1 號：沒有 QA（開單 erina）；第一格帶 `|`、第二格指定 Tim 簽；中間夾一行散文、一格已勾
            Expect(Op("create", "erina", ("title", "打勾"), ("criteria",
                "- [ ] 第一格 a|b 兩種寫法都要\n說明的散文一行\n- [ ] [signer:Tim] Tim 看過\n- [x] 早就勾了")).Index == 1, "create 1 沒拿到 1 號");
            // 2 號：指名 QA kiara
            Expect(Op("create", "erina", ("title", "有 QA"), ("criteria", "- [ ] 交付\n- [ ] 驗收")).Index == 2, "create 2 沒拿到 2 號");
            Expect(Op("claim", "kiara", ("index", "2"), ("role", "qa")).ExitCode == 0, "kiara 認領 QA 失敗");

            // ① 預判 vs 閘：每一格 × 每個人，寫入端真的跑一次（跑完把單檔還原）
            int aPairs = 0;
            foreach (int i in new[] { 1, 2 })
            {
                SCP_TaskEntry e = SCP_TaskIO.Find(aRoot, i)!;
                List<string> aOpen = SCP_TaskStore.ListUncheckedCriteria(SCP_TaskStore.ReadCriteria(aRoot, i));
                for (int n = 1; n <= aOpen.Count; n++)
                    foreach (string who in new[] { "Tim", "erina", "kiara", "zenith" })
                    {
                        string b0 = Bytes(i);
                        bool aPredict = TaskManagerPage.CheckBlocker(e, aOpen[n - 1], who) == null;
                        SCP_TaskOpResult r = Op("check", who, ("index", i.ToString()), ("criteria_index", n.ToString()),
                            ("expect_text", TaskManagerPage.CheckAnchor(aOpen[n - 1])));
                        bool aActual = r.ExitCode == 0 && r.Wrote == 1;
                        Expect(aPredict == aActual, $"TASK-{i} 第 {n} 格 {who}：頁面判 {(aPredict ? "勾得掉" : "勾不掉")}、寫入端 exit {r.ExitCode} wrote {r.Wrote}");
                        Expect(aActual || Bytes(i) == b0, $"TASK-{i} 第 {n} 格 {who}：擋下了卻動了單檔");
                        File.WriteAllText(SCP_TaskIO.TaskPath(aRoot, i), b0);
                        aPairs++;
                    }
            }
            Expect(TaskManagerPage.CheckBlocker(SCP_TaskIO.Find(aRoot, 1)!, "第一格", "") != null, "署名空的還判勾得掉");

            // ② 錨
            Expect(TaskManagerPage.CheckAnchor("第一格 a|b 兩種寫法都要") == "第一格 a", "錨沒切在 | 之前");
            string aEmoji = TaskManagerPage.CheckAnchor(new string('x', 39) + "\U0001F600尾巴");
            Expect(aEmoji.Length == 39 && !char.IsHighSurrogate(aEmoji[^1]), $"錨切到半個代理對（{aEmoji.Length}）");

            // ③ 真的畫、真的按
            var aSent = new List<Dictionary<string, string>>();
            var aPage = new TaskManagerPage(new SenateModel(aTmp))
            {
                TaskDispatch = a =>
                {
                    aSent.Add(new Dictionary<string, string>(a));
                    var d = new Dictionary<string, string>(a); string op = d["op"], who = d["persona"];
                    d.Remove("op"); d.Remove("persona"); d.Remove("data_root");
                    SCP_TaskOpResult r = SCP_TaskOps.Run(aRoot, op, who, d);
                    return new SCP_CmdResult { ExitCode = r.ExitCode, Lines = { r.Headline } };
                },
            };
            aPage.OnPush();
            var aFields = new Dictionary<string, string>();
            var aFolds = new Dictionary<string, bool>();
            SCP_Ui Draw(string? iClick = null)
            {
                var aIn = new SCP_GuiInput { ClickedId = iClick };
                foreach (var kv in aFields) aIn.Fields[kv.Key] = kv.Value;
                foreach (var kv in aFolds) aIn.Folds[kv.Key] = kv.Value;
                var u = new SCP_Ui(aIn);
                aPage.Draw(u);
                foreach (var kv in u.FieldWrites) aFields[kv.Key] = kv.Value;
                return u;
            }
            List<SCP_GuiNode> Nodes(SCP_Ui u)
            {
                var L = new List<SCP_GuiNode>();
                void W(SCP_GuiNode x) { L.Add(x); foreach (SCP_GuiNode c in x.Children) W(c); }
                W(u.Root);
                return L;
            }
            SCP_GuiNode? Box(SCP_Ui u, string iHead) => Nodes(u).FirstOrDefault(x => x.Kind == SCP_GuiNodeKind.Box && x.Text.StartsWith(iHead, StringComparison.Ordinal));
            List<SCP_GuiNode> Ticks(SCP_Ui u) => Nodes(u).Where(x => x.Kind == SCP_GuiNodeKind.Button && x.Id.Contains("/check/1/", StringComparison.Ordinal)).ToList();

            SCP_Ui g = Draw();
            SCP_GuiNode? aItem = Box(g, "TASK-0001");
            Expect(aItem != null, "畫面上找不到 TASK-0001");
            if (aItem != null) aFolds[aItem.Id] = true;
            g = Draw();
            SCP_GuiNode? aCrit = Box(g, "驗收標準");
            Expect(aCrit != null, "展開 TASK-0001 後找不到驗收標準");
            if (aCrit != null) aFolds[aCrit.Id] = true;
            g = Draw();
            string aText = SCP_GuiTextRenderer.Render(g.Root, 400);
            List<SCP_GuiNode> aTicks = Ticks(g);
            // 署名預設 Tim：只有 [signer:Tim] 那一格有按鈕；第一格寫出勾不掉的原因；散文與已勾那一格照印
            Expect(aTicks.Count == 1 && aTicks[0].Id.EndsWith("/check/1/2", StringComparison.Ordinal), $"Tim 應只有第 2 格的按鈕：{string.Join(",", aTicks.Select(x => x.Id))}");
            Expect(aText.Contains("Tim 勾不掉：只有參與者與開單人能勾", StringComparison.Ordinal), "第一格沒寫勾不掉的原因");
            Expect(aText.Contains("說明的散文一行", StringComparison.Ordinal) && aText.Contains("- [x] 早就勾了", StringComparison.Ordinal), "散文或已勾的行沒印");

            string b1 = Bytes(1);
            if (aTicks.Count == 1)
            {
                Draw(aTicks[0].Id);                                       // 第一下：備妥（按下那一輪按鈕已經畫完 ⇒ 下一輪才看得到「確定勾？」）
                Expect(aSent.Count == 0 && Bytes(1) == b1, "按第一下就寫了");
                g = Draw();
                aTicks = Ticks(g);
                Expect(aTicks.Count == 1 && aTicks[0].Text.StartsWith("確定勾", StringComparison.Ordinal), "按第一下之後按鈕沒變成「確定勾？」");
                if (aTicks.Count == 1) g = Draw(aTicks[0].Id);            // 第二下：送出
            }
            Expect(aSent.Count == 1, $"送出次數 {aSent.Count}（應 1）");
            if (aSent.Count == 1)
            {
                Dictionary<string, string> s = aSent[0];
                Expect(s["op"] == "check" && s["index"] == "1" && s["criteria_index"] == "2" && s["persona"] == "Tim"
                    && s.GetValueOrDefault("expect_text") == "[signer:Tim] Tim 看過", "送出的參數不對：" + string.Join(" ", s.Select(kv => kv.Key + "=" + kv.Value)));
            }
            string aCritNow = SCP_TaskStore.ReadCriteria(aRoot, 1);
            Expect(aCritNow.Contains("- [x] [signer:Tim] Tim 看過　✅ Tim ", StringComparison.Ordinal), "單檔沒落「✅ Tim」");
            Expect(SCP_TaskStore.ListUncheckedCriteria(aCritNow).SequenceEqual(new[] { "第一格 a|b 兩種寫法都要" }), "勾錯格或多勾");
            g = Draw();
            Expect(Ticks(g).Count == 0, "勾完重畫，Tim 還有按鈕");

            // 換署名 erina（開單人）⇒ 第一格有按鈕
            SCP_GuiNode? aAuthor = Nodes(g).FirstOrDefault(x => x.Kind == SCP_GuiNodeKind.TextField && x.Text.StartsWith("署名", StringComparison.Ordinal));
            Expect(aAuthor != null, "找不到署名欄");
            if (aAuthor != null) aFields[aAuthor.Id] = "erina";
            g = Draw();
            aTicks = Ticks(g);
            Expect(aTicks.Count == 1 && aTicks[0].Id.EndsWith("/check/1/1", StringComparison.Ordinal), $"erina 應只有第 1 格的按鈕：{string.Join(",", aTicks.Select(x => x.Id))}");

            return new CheckRow(aName,
                aFail.Count == 0 ? $"預判與閘一致（{aPairs} 組）／錨切 | 與代理對／按一下零寫入、按兩下送 op=check／Tim 簽 [signer:Tim] 落檔／換署名按鈕跟著換 全對"
                    : string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "炸了：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }
}
