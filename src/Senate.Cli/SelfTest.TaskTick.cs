// 區塊職責：任務後台頁的**打勾**淨室（Tim 2026-11-02「TaskManagerPage 可以支援打勾功能嗎」）——
//           ① 頁面預先判「這個署名不代簽勾不勾得掉」（TaskManagerPage.CheckBlocker）跟寫入端 `op=check` 的閘逐格一致；
//             帶 qa_note（代簽，Tim 2026-11-02「全部都能勾」）⇒ 每一格每一個人都落得了檔，簽名與時間線註明代簽；
//           ② 錨（expect_text）切在 `|` 之前、不切半個代理對；
//           ③ 真的畫頁面、真的按：按一次只是備妥（零寫入），再按一次才送 `op=check`；勾得掉的是「勾」、勾不掉的是「代簽」（多帶 qa_note），
//             送出的參數交給寫入端落得了檔、簽名是署名的人。
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
                        // 代簽：同一格同一個人帶 qa_note ⇒ 一律落得了檔、簽名註明代簽
                        SCP_TaskOpResult p = Op("check", who, ("index", i.ToString()), ("criteria_index", n.ToString()),
                            ("expect_text", TaskManagerPage.CheckAnchor(aOpen[n - 1])), ("qa_note", TaskManagerPage.ProxyNote));
                        Expect(p.ExitCode == 0 && p.Wrote == 1 && Bytes(i).Contains($"✅ {who} ", StringComparison.Ordinal)
                            && Bytes(i).Contains("（代簽：後台頁）", StringComparison.Ordinal)
                            && SCP_TaskStore.ReadTimeline(SCP_TaskIO.TaskPath(aRoot, i)).Any(l => l.Contains("（代簽：後台頁）", StringComparison.Ordinal)),
                            $"TASK-{i} 第 {n} 格 {who} 代簽沒落檔或沒註明（exit {p.ExitCode}）");
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
            SCP_GuiNode? Tick(SCP_Ui u, int n) => Ticks(u).FirstOrDefault(x => x.Id.EndsWith($"/check/1/{n}", StringComparison.Ordinal));
            // 署名預設 Tim：兩格都有按鈕 —— 第 1 格（不在名單）是「代簽」、第 2 格（[signer:Tim]）是「勾」；散文與已勾那一格照印
            Expect(aTicks.Count == 2 && Tick(g, 1)?.Text == "代簽" && Tick(g, 2)?.Text == "勾", $"Tim 的按鈕不對：{string.Join(",", aTicks.Select(x => x.Id + "=" + x.Text))}");
            Expect(aText.Contains("只有參與者與開單人能勾：erina ⇒ Tim 是代簽", StringComparison.Ordinal), "第一格沒寫代簽的原因");
            Expect(aText.Contains("說明的散文一行", StringComparison.Ordinal) && aText.Contains("- [x] 早就勾了", StringComparison.Ordinal), "散文或已勾的行沒印");

            // 按兩下：第一下零寫入、按鈕變「確定…？」；第二下送出
            string? Press(int n, string iVerb)
            {
                SCP_GuiNode? b = Tick(g, n);
                if (b == null) return $"第 {n} 格沒有按鈕";
                int aSent0 = aSent.Count; string b1 = Bytes(1);
                Draw(b.Id);                                               // 按下那一輪按鈕已經畫完 ⇒ 下一輪才看得到「確定…？」
                if (aSent.Count != aSent0 || Bytes(1) != b1) return $"第 {n} 格按第一下就寫了";
                g = Draw();
                b = Tick(g, n);
                if (b == null || b.Text != $"確定{iVerb}？再按一次") return $"第 {n} 格按第一下之後按鈕是「{b?.Text}」";
                Draw(b.Id);
                g = Draw();
                return aSent.Count == aSent0 + 1 ? null : $"第 {n} 格按第二下送出 {aSent.Count - aSent0} 次";
            }
            string Args(Dictionary<string, string> s) => string.Join(" ", s.Where(kv => kv.Key != "data_root").OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value));

            // [signer:Tim] 那一格：正常勾、不帶 qa_note
            string? aErr = Press(2, "勾");
            Expect(aErr == null, aErr ?? "");
            if (aErr == null)
                Expect(Args(aSent[^1]) == "criteria_index=2 expect_text=[signer:Tim] Tim 看過 index=1 op=check persona=Tim", "送出的參數不對：" + Args(aSent[^1]));
            string aCritNow = SCP_TaskStore.ReadCriteria(aRoot, 1);
            Expect(aCritNow.Contains("- [x] [signer:Tim] Tim 看過　✅ Tim ", StringComparison.Ordinal) && !aCritNow.Contains("代簽", StringComparison.Ordinal), "[signer:Tim] 那格沒落「✅ Tim」或被標成代簽");
            Expect(SCP_TaskStore.ListUncheckedCriteria(aCritNow).SequenceEqual(new[] { "第一格 a|b 兩種寫法都要" }), "勾錯格或多勾");

            // 換署名 erina（開單人）⇒ 第 1 格是「勾」；署名清空 ⇒ 沒有按鈕
            SCP_GuiNode? aAuthor = Nodes(g).FirstOrDefault(x => x.Kind == SCP_GuiNodeKind.TextField && x.Text.StartsWith("署名", StringComparison.Ordinal));
            Expect(aAuthor != null, "找不到署名欄");
            if (aAuthor != null)
            {
                aFields[aAuthor.Id] = "erina"; g = Draw();
                Expect(Ticks(g).Count == 1 && Tick(g, 1)?.Text == "勾", $"erina 應只有第 1 格的「勾」：{string.Join(",", Ticks(g).Select(x => x.Id + "=" + x.Text))}");
                aFields[aAuthor.Id] = ""; g = Draw();
                Expect(Ticks(g).Count == 0 && SCP_GuiTextRenderer.Render(g.Root, 400).Contains("（先填署名）", StringComparison.Ordinal), "署名空的還有按鈕");
                aFields[aAuthor.Id] = "Tim"; g = Draw();
            }

            // Tim 代簽第 1 格：帶 qa_note=後台頁、簽名註明
            aErr = Press(1, "代簽");
            Expect(aErr == null, aErr ?? "");
            if (aErr == null)
                Expect(Args(aSent[^1]) == "criteria_index=1 expect_text=第一格 a index=1 op=check persona=Tim qa_note=後台頁", "代簽送出的參數不對：" + Args(aSent[^1]));
            aCritNow = SCP_TaskStore.ReadCriteria(aRoot, 1);
            Expect(aCritNow.Contains("- [x] 第一格 a|b 兩種寫法都要　✅ Tim ", StringComparison.Ordinal) && aCritNow.Contains("（代簽：後台頁）", StringComparison.Ordinal), "代簽那格沒落「✅ Tim …（代簽：後台頁）」");
            Expect(SCP_TaskStore.ListUncheckedCriteria(aCritNow).Count == 0 && Ticks(g).Count == 0, "全勾完還有未勾或按鈕");

            return new CheckRow(aName,
                aFail.Count == 0 ? $"預判與閘一致、帶 qa_note 一律代簽落檔（{aPairs} 組）／錨切 | 與代理對／按一下零寫入、按兩下送出／[signer:Tim] 正常勾／不在名單 ⇒ 代簽註明／換署名、清空署名按鈕跟著換 全對"
                    : string.Join("；", aFail),
                aFail.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "炸了：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); } catch { } }
    }
}
