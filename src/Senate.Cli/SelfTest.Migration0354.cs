// 區塊職責：TASK-0354（課程筆記／好感度／persona 設定的寫入端）的自我對拍。
// 物理意義：三支寫入端的輸出要與既有檔逐位元組相容 ⇒ 驗兩件事：
//           ① 淨室：寫出來的形狀、去重、閘（反向對照：該擋的零寫入）。
//           ② 真實資料：每一份既有的 `_current.md`，用現在的重算邏輯從同一批事件再算一次，數字要逐格相同
//              —— 那是「重算沒有悄悄改掉任何人的好感度」唯一拿得到的讀數。
// 數值影響：淨室在 temp 目錄裡寫、跑完刪；真實資料那一列**只讀**。
#nullable enable
using System.Globalization;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Lessons;
using SCP.Core.Letters;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow LessonLogCleanRoom()
    {
        const string aName = "lesson 庫寫入：一行形狀／去重只看 body／空 body 零寫入（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_lesson_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aIn = new SCP_LessonInput { Body = "反引號 `x` 與 \"引號\"\t與換行\n", Actor = "Template", Category = "debug",
                                            Title = "t", Tags = SCP_LessonLog.ParseTags("a, b,a,,c") };
            var r1 = SCP_LessonLog.Note(aTmp, aIn, "AgentCommands/Lessons/lessons.jsonl");
            string aLine = File.ReadAllText(SCP_LessonLog.JsonlPath(aTmp));
            string aWant = "{\"ts\":\"" + r1.Ts + "\",\"actor\":\"Template\",\"category\":\"debug\","
                           + "\"body\":\"反引號 `x` 與 \\\"引號\\\"\\t與換行\\n\",\"title\":\"t\",\"tags\":[\"a\",\"b\",\"c\"]}\n";
            bool aShape = aLine == aWant;
            // 去重：同 body、不同 actor／category ⇒ 仍是重複
            var r2 = SCP_LessonLog.Note(aTmp, new SCP_LessonInput { Body = aIn.Body, Actor = "x", Category = "y" }, "rel");
            bool aDup = r2.Duplicate && File.ReadAllLines(SCP_LessonLog.JsonlPath(aTmp)).Length == 1;
            // 🔴 反向：空 body ⇒ 丟例外、一行都不多
            bool aBlank;
            try { SCP_LessonLog.Note(aTmp, new SCP_LessonInput { Body = "  " }, "rel"); aBlank = false; }
            catch (ArgumentException) { aBlank = File.ReadAllLines(SCP_LessonLog.JsonlPath(aTmp)).Length == 1; }
            bool aOk = aShape && aDup && aBlank;
            return new CheckRow(aName, $"一行形狀逐字={aShape}／同 body 判重複且零 append={aDup}／🔴 空 body 擋下零寫入={aBlank}",
                                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    static CheckRow RelationshipStoreCleanRoom()
    {
        const string aName = "好感度寫入：事件→投影重算／上限截斷／看法去重／rebuild 不疊後綴夾／show 零寫入（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_rel_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(Path.Combine(aTmp, "calli", "profile"));
            Directory.CreateDirectory(Path.Combine(aTmp, "Tim"));
            // 正規化：letters 有 `Tim` ⇒ `tim` 收斂成 `Tim`；沒有的 ⇒ 大寫開頭
            bool aCanon = SCP_RelationshipStore.CanonicalTarget(aTmp, "tim") == "Tim"
                          && SCP_RelationshipStore.CanonicalTarget(aTmp, "nobody") == "Nobody";
            SCP_RelationshipEvent Ev(string at, float trust) => new SCP_RelationshipEvent
            {
                At = at, Persona = "calli", Target = "Tim", Reason = "r" + at,
                AxisDeltas = new List<KeyValuePair<string, float>> { new("trust", trust), new("respect", 0.3f) },
            };
            SCP_RelationshipStore.WriteEvent(aTmp, Ev("2026-10-01T00:00:00.001Z", 0.7f), out string p1, out _);
            SCP_RelationshipStore.WriteEvent(aTmp, Ev("2026-10-01T00:00:00.002Z", 0.7f), out _, out _);
            bool aDupEvent = !SCP_RelationshipStore.WriteEvent(aTmp, Ev("2026-10-01T00:00:00.001Z", 0.7f), out _, out _);
            var c = SCP_RelationshipStore.RebuildCurrent(aTmp, "calli", "Tim", true);
            // trust 1.4 ⇒ clamp 1；respect 0.6 ⇒ (1*2 + 0.6*1.5)/11.5*100 = 25.2 ⇒ 25（在意）
            bool aMath = c.EmotionVector["trust"] == 1f && Math.Abs(c.EmotionVector["respect"] - 0.6f) < 1e-6
                         && c.SurfaceScore == 25 && c.Tier == "在意" && c.EventCount == 2;
            string aEvText = File.ReadAllText(p1);
            bool aEvShape = aEvText.StartsWith("---\nat: 2026-10-01T00:00:00.001Z\npersona: calli\ntarget: Tim\nsource: live\naxis_deltas:\n  trust: 0.7\n  respect: 0.3\nsurface_score_after: 0   # ", StringComparison.Ordinal)
                            && aEvText.EndsWith("---\n\nr2026-10-01T00:00:00.001Z\n", StringComparison.Ordinal)
                            && Path.GetFileName(p1) == "20261001T000000001Z.md";
            bool aOp1 = SCP_RelationshipStore.WriteOpinion(aTmp, "calli", "Tim", "同一句", "2026-10-01T00:00:00.000Z", out _);
            bool aOp2 = SCP_RelationshipStore.WriteOpinion(aTmp, "calli", "Tim", "同一句", "2026-10-01T00:00:01.000Z", out _);
            bool aOpDedup = aOp1 && !aOp2;
            // 帶後綴的夾：主人 `Kaguya` 住在 `Kaguya__b557`（`kaguya` 夾已被小寫占用）⇒ rebuild 用主人名不疊新夾
            string aRel = SCP_RelationshipStore.PersonaRelDir(aTmp, "calli");
            Directory.CreateDirectory(Path.Combine(aRel, "kaguya")); File.WriteAllText(Path.Combine(aRel, "kaguya", "_target.txt"), "kaguya\n");
            string aAltName = "Kaguya__" + SCP_RelationshipStore.Sha1Hex("Kaguya", 4);
            Directory.CreateDirectory(Path.Combine(aRel, aAltName)); File.WriteAllText(Path.Combine(aRel, aAltName, "_target.txt"), "Kaguya\n");
            int aDirsBefore = Directory.GetDirectories(aRel).Length;
            string? aOwner = SCP_RelationshipStore.OwnerOf(Path.Combine(aRel, aAltName));
            SCP_RelationshipStore.RebuildCurrent(aTmp, "calli", aOwner!, true);
            bool aNoStack = Directory.GetDirectories(aRel).Length == aDirsBefore
                            && File.Exists(Path.Combine(aRel, aAltName, "_current.md"));
            // 🔴 反向：iWrite=false 對一個不存在的 target ⇒ 一個檔都不生
            SCP_RelationshipStore.RebuildCurrent(aTmp, "calli", "Ghost", false);
            bool aZero = !Directory.Exists(Path.Combine(aRel, "Ghost"));
            bool aOk = aCanon && aDupEvent && aMath && aEvShape && aOpDedup && aNoStack && aZero;
            return new CheckRow(aName,
                $"正規化={aCanon}／同時戳同 reason 判重複={aDupEvent}／重算＋clamp＋分數 25 在意={aMath}／事件檔逐字={aEvShape}"
                + $"／看法內容去重={aOpDedup}／rebuild 不疊後綴夾={aNoStack}／🔴 不寫模式零寫入={aZero}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    static CheckRow PersonaProfileWriteCleanRoom()
    {
        const string aName = "persona 設定寫入：結構欄 UCL 位元組／純量欄字面／審計轉義／閘與零寫入／換區重綁（淨室）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_pp_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aLetters = Path.Combine(aTmp, "ChatTavern", "baton", "letters");
            Directory.CreateDirectory(Path.Combine(aLetters, "Template", "profile"));
            Directory.CreateDirectory(Path.Combine(aLetters, "calli", "profile"));
            string Field(string f) => File.ReadAllText(Path.Combine(aLetters, "Template", "profile", f + ".md"));
            bool S(string f, string v) => SCP_PersonaProfileWrite.SetField(aLetters, aTmp, "Template", f, v, "basecamp", "淨室", out _, out _);

            bool aVec = S("identity_vector", "[0.8527,-1,2.50]") && Field("identity_vector") == "\r\n[\r\n\t0.8527,\r\n\t-1,\r\n\t2.5\r\n]\n";
            bool aEmpty = S("fork_lineage", "[]") && Field("fork_lineage") == "\r\n[\r\n\r\n]\n";
            bool aObj = S("vector_history", "[{\"at\":\"x\",\"n\":0,\"s\":\"中\"}]")
                        && Field("vector_history") == "\r\n[\r\n\t{\r\n\t\t\"at\":\"x\",\r\n\t\t\"n\":0,\r\n\t\t\"s\":\"\\u4e2d\"\r\n\t}\r\n]\n";
            bool aScalar = S("email", "a@b.c\n") && Field("email") == "a@b.c\n";
            string aAudit = File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Last();
            bool aAuditEsc = aAudit.EndsWith(",\"persona\":\"Template\",\"fields\":\"profile/email\",\"actor\":\"basecamp\",\"reason\":\"\\u6de8\\u5ba4\"}", StringComparison.Ordinal);
            int aAuditN = File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Length;

            // 🔴 反向：每一格都要零寫入（審計行數不變、檔不變）
            bool aGates = !S("identity_vector", "[\"x\"]") && !S("identity_vector", "not json") && !S("fork_lineage", "")
                          && !S("agent", "Myth") && !S("wake_count", "3")
                          && !SCP_PersonaProfileWrite.SetField(aLetters, aTmp, "Template", "email", "x", "", "r", out _, out _)
                          && !SCP_PersonaProfileWrite.SetField(aLetters, aTmp, "nobody", "email", "x", "a", "r", out _, out _)
                          && !SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aTmp, "nobody", "Florin", "x", "a", "r", out _, out _)
                          && !SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aTmp, "Template", "Florin", "a\nb", "a", "r", out _, out _);
            bool aUnsetAbsent = SCP_PersonaProfileWrite.UnsetField(aLetters, aTmp, "Template", "plurk_account", "a", "r", out bool aHad, out _, out _) && !aHad;
            bool aZero = aGates && aUnsetAbsent && Field("email") == "a@b.c\n"
                         && File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Length == aAuditN
                         && !Directory.Exists(Path.Combine(aLetters, "nobody"));

            // 換區重綁：dry-run 零寫入；新區同值 ⇒ 跳過；新區不同值 ⇒ 衝突
            SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aTmp, "Template", "Florin", "T1", "a", "r", out _, out _);
            SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aTmp, "calli", "Florin", "Myth", "a", "r", out _, out _);
            SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aTmp, "calli", "BTC", "Other", "a", "r", out _, out _);
            var aDry = SCP_PersonaProfileWrite.CopyRegionAll(aLetters, aTmp, "Florin", "BTC", "a", "r", true);
            bool aDryZero = aDry.Copied == 1 && aDry.Conflicts == 1 && !File.Exists(Path.Combine(aLetters, "Template", "bank", "BTC.md"));
            var aReal = SCP_PersonaProfileWrite.CopyRegionAll(aLetters, aTmp, "Florin", "BTC", "a", "r", false);
            bool aRebind = aReal.Copied == 1 && aReal.Conflicts == 1
                           && File.ReadAllText(Path.Combine(aLetters, "Template", "bank", "BTC.md")) == "T1\n"
                           && File.ReadAllText(Path.Combine(aLetters, "calli", "bank", "BTC.md")) == "Other\n";
            // 建 persona（TASK-0361）：綁定＋身分欄一次寫；已存在 ⇒ 擋、零寫入
            var aNew = (SCP_UclLegacyObject)SCP_UclLegacyJson.Parse("{\"identity_vector\":[0.5],\"fork_lineage\":[],\"forked_from\":null,\"created_at\":\"t\",\"wake_count\":3}")!;
            int aAuditBefore = File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Length;
            bool aCreate = SCP_PersonaProfileWrite.Create(aLetters, aTmp, "newbie", "Florin", "NB", aNew, "a", "r", out _, out _)
                           && File.ReadAllText(Path.Combine(aLetters, "newbie", "bank", "Florin.md")) == "NB\n"
                           && File.ReadAllText(Path.Combine(aLetters, "newbie", "profile", "forked_from.md")) == "\n"
                           && !File.Exists(Path.Combine(aLetters, "newbie", "profile", "wake_count.md"))
                           && File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Last().Contains("\"profile:[forked_from,fork_lineage,created_at,identity_vector] skipped(\\u63a8\\u5c0e\\u6b04):[wake_count]\"");   // 中文在審計裡是 \u 轉義
            int aAuditMid = File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Length;
            bool aCreateAgain = !SCP_PersonaProfileWrite.Create(aLetters, aTmp, "newbie", "Florin", "X", aNew, "a", "r", out _, out _)
                                && File.ReadAllLines(SCP_PersonaProfileWrite.AuditPath(aTmp)).Length == aAuditMid
                                && aAuditMid - aAuditBefore == 6;   // 綁定 1 ＋ 四欄 ＋ 總結 1
            bool aOk = aVec && aEmpty && aObj && aScalar && aAuditEsc && aZero && aDryZero && aRebind && aCreate && aCreateAgain;
            return new CheckRow(aName,
                $"數字陣列 CRLF＋R 格式={aVec}／空陣列={aEmpty}／物件陣列＋\\u 轉義={aObj}／純量欄去尾換行={aScalar}／審計 \\u 轉義={aAuditEsc}"
                + $"／🔴 九道閘＋unset 不存在＝零寫入={aZero}／重綁 dry-run 零寫入={aDryZero}／重綁不覆寫衝突={aRebind}／建 persona={aCreate}／🔴 重建擋下零寫入={aCreateAgain}",
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(aTmp, true); } catch (Exception) { } }
    }

    /// <summary>
    /// 真實資料：每一份既有的 `_current.md`，由同一批 events/ 用現在的邏輯重算 ⇒ 八軸／分數／tier／事件數要逐格相同。
    /// <para>⚠ 有 `opening_balance`（遷移反推的期初餘額）的那幾份跳過並照實計數 —— 新寫入端不產生它，重算也拿不到它。</para>
    /// </summary>
    static IEnumerable<CheckRow> RealRelationshipRecomputeMatchesEditor(IReadOnlyList<SelfTestTarget> iTargets)
    {
        bool aAny = false;
        foreach (SelfTestTarget p in iTargets)
        {
            if (p.AgentCommandsRoot == null) continue;
            string aLetters = SCP.Core.Paths.SCP_DataPaths.Letters(new SCP.Core.Paths.SCP_DataRoot(p.AgentCommandsRoot)).Value;
            if (!Directory.Exists(aLetters)) continue;
            aAny = true;
            int aSame = 0, aSkipOpening = 0;
            var aDiff = new List<string>();
            foreach (string aPersonaDir in Directory.GetDirectories(aLetters))
            {
                string aRel = Path.Combine(aPersonaDir, SCP_Relationship.RelationshipDirName);
                if (!Directory.Exists(aRel)) continue;
                foreach (string aT in Directory.GetDirectories(aRel))
                {
                    string aCurPath = Path.Combine(aT, SCP_Relationship.CurrentFileName);
                    if (!File.Exists(aCurPath)) continue;
                    var aDisk = ReadCurrentFields(aCurPath);
                    if (aDisk.TryGetValue("opening_balance", out string? ob) && ob != "null") { aSkipOpening++; continue; }
                    var aEvents = SCP_RelationshipStore.LoadEventDeltas(Path.Combine(aT, SCP_RelationshipStore.EventsDirName));
                    var aVec = SCP_RelationshipStore.Recompute(aEvents);
                    int aScore = SCP_RelationshipAxes.SurfaceScore(aVec);
                    var aMine = new Dictionary<string, string>
                    {
                        ["surface_score"] = aScore.ToString(CultureInfo.InvariantCulture),
                        ["tier"] = SCP_RelationshipAxes.Tier(aScore),
                        ["event_count"] = aEvents.Count.ToString(CultureInfo.InvariantCulture),
                    };
                    foreach (string ax in SCP_RelationshipAxes.Names) aMine["ev." + ax] = SCP_RelationshipAxes.Fmt(aVec[ax]);
                    var aBad = aMine.Where(kv => !aDisk.TryGetValue(kv.Key, out string? d) || d != kv.Value).Select(kv => kv.Key).ToList();
                    if (aBad.Count == 0) aSame++;
                    else aDiff.Add(Path.GetFileName(aPersonaDir) + "/" + Path.GetFileName(aT) + "（" + string.Join(",", aBad) + "）");
                }
            }
            string aDetail = $"{aSame} 份逐格相同／{aDiff.Count} 份不同／{aSkipOpening} 份帶期初餘額（跳過）";
            if (aDiff.Count > 0) aDetail += "：" + string.Join("、", aDiff.Take(6)) + (aDiff.Count > 6 ? "…" : "");
            yield return new CheckRow($"好感度重算 vs Editor 寫的投影（{p.Name}）", aDetail,
                aDiff.Count == 0 && aSame > 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        if (!aAny) yield return new CheckRow("好感度重算 vs Editor 寫的投影", "沒有任何專案讀得到 letters ⇒ 沒量（⛔ 不是通過）", CheckResult.Skipped);
    }

    /// <summary>`_current.md` frontmatter 的平面欄位；`emotion_vector` 底下的八軸記成 `ev.&lt;軸&gt;`。</summary>
    static Dictionary<string, string> ReadCurrentFields(string iPath)
    {
        var aOut = new Dictionary<string, string>(StringComparer.Ordinal);
        bool aIn = false; string? aBlock = null;
        foreach (string ln in File.ReadAllLines(iPath, Encoding.UTF8))
        {
            if (ln.StartsWith("---", StringComparison.Ordinal)) { if (aIn) break; aIn = true; continue; }
            int c = ln.IndexOf(':');
            if (c <= 0) continue;
            string k = ln.Substring(0, c).Trim(), v = ln.Substring(c + 1);
            int h = v.IndexOf('#'); if (h >= 0) v = v.Substring(0, h);
            v = v.Trim();
            if (ln.StartsWith("  ", StringComparison.Ordinal) && aBlock != null) { aOut[aBlock + "." + k] = v; continue; }
            aBlock = k == "emotion_vector" ? "ev" : null;
            aOut[k] = v;
        }
        return aOut;
    }
}
