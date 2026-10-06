// 區塊職責：早安 brief 可調參數、回傳檔大檔提示、brief 結尾標記（TASK-0419）的自我對拍。
// 物理意義：三格都是「格式正常、答案不對」會安靜發生的地方：
//           設定 —— 不合法的值被夾成沒人打過的數字、或寫壞了檔裡別的鍵；
//           大檔提示 —— 行數算錯一行（尾端換行）、量不到時印成 0 行；
//           結尾標記 —— N 跟檔案實際行數對不上，或被說成「讀完證明」（Codex 截中段時頭尾都在）。
// 數值影響：在 temp 目錄造資料根與信件夾，跑完刪；⛔ 不碰真實資料根。
#nullable enable
using SCP.Core.Cmd;
using SCP.Core.Letters;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow BriefSettingsCleanRoom()
    {
        const string aName = "早安 brief 設定：沒設過＝預設／不合法照預設跑並說出來（⛔ 不夾值）／寫入擋不合法、保留其他鍵、讀回／brief 照設定合併見樹（淨室，TASK-0419）";
        string d = Path.Combine(Path.GetTempPath(), "senate_briefset_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            Directory.CreateDirectory(Path.Combine(d, "ChatTavern"));
            string aPath = SCP_WakeBriefSettings.PathOf(d);

            // ① 沒設過 ⇒ 全預設、沒問題
            var s0 = SCP_WakeBriefSettings.Read(d, out bool aEx0, out string? aErr0);
            if (aEx0 || aErr0 != null || s0.Problems.Count != 0 || s0.MainLineCap != SCP_WakeBrief.BriefLineCap)
                aFails.Add("沒設過時不是乾淨的預設");

            // 🔴 ② 檔裡有不合法的格 ⇒ 那一格照預設（⛔ 不夾成 100），說出來；合法的格照用
            File.WriteAllText(aPath, "{\"main_line_cap\": 5, \"people_offline_top\": \"x\", \"big_file_lines\": 42, \"zz_keep\": 7}");
            var s1 = SCP_WakeBriefSettings.Read(d, out _, out string? aErr1);
            if (aErr1 != null) aFails.Add("單格不合法被當成整份讀不了：" + aErr1);
            if (s1.MainLineCap != SCP_WakeBrief.BriefLineCap) aFails.Add($"🔴 不合法的主檔上限被夾成 {s1.MainLineCap}（期望照預設 {SCP_WakeBrief.BriefLineCap}）");
            if (s1.PeopleOfflineTop != SCP_WakeBrief.PeopleOfflineTop) aFails.Add("字串值沒照預設");
            if (s1.BigFileLines != 42) aFails.Add("合法的格沒照用");
            if (s1.Problems.Count != 2) aFails.Add($"問題 {s1.Problems.Count} 筆（期望 2）");

            // 🔴 ③ 寫不合法 ⇒ 不寫、檔案位元組不變
            string aBefore = File.ReadAllText(aPath);
            var sBad = new SCP_WakeBriefSettings(); sBad.Set(SCP_WakeBriefSettings.KeyTreeMergeMaxExtra, 999);
            if (SCP_WakeBriefSettings.Write(d, sBad, out _) || File.ReadAllText(aPath) != aBefore) aFails.Add("🔴 不合法的值寫進去了");

            // ④ 寫合法 ⇒ 讀回一致、其他鍵保留
            var sOk = new SCP_WakeBriefSettings(); sOk.Set(SCP_WakeBriefSettings.KeyTreeMergeMaxExtra, 1);
            if (!SCP_WakeBriefSettings.Write(d, sOk, out string? aWErr)) aFails.Add("合法的值寫不進去：" + aWErr);
            var s2 = SCP_WakeBriefSettings.Read(d, out _, out _);
            if (s2.TreeMergeMaxExtra != 1 || s2.Problems.Count != 0) aFails.Add("寫完讀回對不上");
            if (!File.ReadAllText(aPath).Contains("zz_keep")) aFails.Add("檔裡其他鍵被丟掉了");

            // ⑤ brief 照設定走：5 封短信，最多往前補 1 封 ⇒ 見樹 2 封；預設（9）⇒ 5 封
            string aLetters = Path.Combine(d, "letters");
            string aWakes = Path.Combine(aLetters, "probe", "wakes");
            Directory.CreateDirectory(aWakes);
            for (int i = 1; i <= 5; i++)
                File.WriteAllText(Path.Combine(aWakes, $"{i:D6}_2026010{i}T000000Z.md"),
                    $"---\ntype: letter_to_future_self\nwritten_by_persona: probe\nwritten_at: 2026-01-0{i}T00:00:00.000Z\n---\n\n第 {i} 封\n");
            var b1 = SCP_WakeBrief.Build(aLetters, "probe", 6, d);
            var b0 = SCP_WakeBrief.Build(aLetters, "probe", 6, null, null, new SCP_WakeBriefSettings());
            if (!b1.Main.Contains("已往前合併 2 封")) aFails.Add("🔴 設定 1 封時見樹不是 2 封");
            if (!b0.Main.Contains("已往前合併 5 封")) aFails.Add("預設時見樹不是 5 封");

            // ⑥ brief 把不合法的格說出來（寫回一份壞的再 Build）
            File.WriteAllText(aPath, "{\"main_line_cap\": 5}");
            var b2 = SCP_WakeBrief.Build(aLetters, "probe", 6, d);
            if (!b2.Main.Contains("brief 設定有問題") || b2.SettingProblems.Count != 1) aFails.Add("🔴 brief 沒把不合法的設定說出來");
            if (b2.MainLineCap != SCP_WakeBrief.BriefLineCap) aFails.Add("brief 用了不合法的主檔上限");

            return new CheckRow(aName,
                aFails.Count == 0 ? "預設／單格不合法照預設（2 筆說出來、不夾值）／寫入擋壞值且位元組不變／合法讀回、保留其他鍵／見樹 1 封設定⇒2 封、預設⇒5 封／brief 標出壞設定（temp 目錄）" : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }

    static CheckRow ReadHintCleanRoom()
    {
        const string aName = "回傳檔大檔提示：行數規則（有無尾端換行）／小檔只帶大小／超行數或超 KB（含單行超長）才提示到第 N 行／門檻讀設定／量不到不印 0／結尾標記 N＝實際行數、截中段仍看得到但不稱讀完（淨室，TASK-0419）";
        string d = Path.Combine(Path.GetTempPath(), "senate_readhint_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var aFails = new List<string>();
            Directory.CreateDirectory(Path.Combine(d, "ChatTavern"));
            var aDef = new SCP_WakeBriefSettings();
            string F(string iName, string iText) { string p = Path.Combine(d, iName); File.WriteAllText(p, iText); return p; }
            int Lines(string p) { SCP_ReadHint.TryMeasure(p, out int n, out _, out _); return n; }

            // ① 行數規則：有沒有尾端換行都是 3 行
            if (Lines(F("a.md", "1\n2\n3")) != 3 || Lines(F("b.md", "1\n2\n3\n")) != 3) aFails.Add("尾端換行讓行數差一");

            // 🔴 ② 小檔只帶大小、沒有大檔提示
            var aSmall = SCP_ReadHint.Describe(Path.Combine(d, "a.md"), aDef);
            if (aSmall.Count != 1 || !aSmall[0].StartsWith("（3 行／")) aFails.Add($"小檔：「{string.Join("｜", aSmall)}」");

            // ③ 超行數 ⇒ 提示讀到第 N 行
            string aMany = F("many.md", string.Join("\n", Enumerable.Range(1, 600).Select(i => "L" + i)));
            var aBig = SCP_ReadHint.Describe(aMany, aDef);
            if (aBig.Count != 2 || !aBig[1].Contains("分段讀到第 600 行")) aFails.Add("超行數沒有提示到第 600 行");

            // ④ 單行超長（30 KB 一行）⇒ 照 KB 門檻提示（Sirius：一整則壓成一長行，行數再少也會超量）
            string aLong = F("long.md", new string('字', 10 * 1024));
            var aLongHint = SCP_ReadHint.Describe(aLong, aDef);
            if (aLongHint.Count != 2 || !aLongHint[1].Contains("第 1 行")) aFails.Add("單行超長沒有照 KB 門檻提示");

            // ⑤ 門檻讀資料根的設定：門檻 5 行 ⇒ 6 行就提示
            var aLow = new SCP_WakeBriefSettings(); aLow.Set(SCP_WakeBriefSettings.KeyBigFileLines, 5);
            SCP_WakeBriefSettings.Write(d, aLow, out _);
            string aSix = F("six.md", "1\n2\n3\n4\n5\n6");
            var aSixLines = SCP_ReadHint.Lines("📄 回傳檔：", aSix, d);
            if (aSixLines.Count != 2 || !aSixLines[0].EndsWith("（6 行／0.0 KB）")) aFails.Add($"門檻沒讀資料根設定：「{string.Join("｜", aSixLines)}」");

            // 🔴 ⑥ 量不到 ⇒ 明說，⛔ 不印 0 行
            var aMissing = SCP_ReadHint.Describe(Path.Combine(d, "nope.md"), aDef);
            if (aMissing.Count != 1 || !aMissing[0].Contains("量不到") || aMissing[0].Contains("0 行")) aFails.Add("量不到被印成 0 行");

            // ⑦ 結尾標記：寫成檔之後 N ＝ 量到的行數
            string aMarked = F("marked.md", SCP_WakeBrief.WithEndMarker("a\nb\nc"));
            string aLast = File.ReadAllLines(aMarked).Last();
            if (!aLast.StartsWith(SCP_WakeBrief.EndMarkerPrefix + Lines(aMarked) + " 行）")) aFails.Add($"結尾標記的 N 跟實際行數對不上：「{aLast}」（實際 {Lines(aMarked)}）");

            // 🔴 ⑧ 保留首尾、刪掉中段（Codex 的截法）⇒ 結尾標記照樣看得到 ⇒ 它的字面必須說「不證明中段」
            string[] aAll = File.ReadAllLines(aMarked);
            string aCut = aAll[0] + "\n…（中段被截）…\n" + aAll[aAll.Length - 1];
            if (!aCut.Contains(SCP_WakeBrief.EndMarkerPrefix)) aFails.Add("截中段後結尾標記不見了（前提變了）");
            if (!aLast.Contains("不證明中段")) aFails.Add("🔴 結尾標記沒說它不證明中段 ⇒ 會被當成讀完證明");

            return new CheckRow(aName,
                aFails.Count == 0 ? "行數規則一致；小檔只帶大小；600 行／單行 30 KB 都提示；門檻讀設定（5 行⇒6 行提示）；量不到不印 0；標記 N＝實際行數；截中段仍可見且字面不稱讀完（temp 目錄）" : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
