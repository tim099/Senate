// 區塊職責：信裡作者寫的現地與機器值不同時的自我對拍（TASK-0418）。
// 物理意義：calli wake#63 的內文寫「📍 現地：區域 BTC ／ 專案 Bar」，實際寫在 Florin／LY；
//           寫入端一個字都沒提，隔天 brief 把兩個矛盾的現地並排印出來。每一格驗一個「錯了也不會叫」的地方：
//           ① 判讀：frontmatter 留痕＋內文那行都抓得到；跟機器值相同的那行不算衝突；機器值 unstated 不判
//           ② 小歇信寫入端：落檔後的結果帶出衝突清單（Cmd 靠它印 ⚠）
//           🔴 ③ brief 見樹：內文跟機器值相同的那行不印第二次；不同的那行換成警告，⛔ 不原樣並排
//           ④ brief：信的區域跟本次醒來的區域不同 ⇒ 標「不是本區」；反向對照：同區不標
// 數值影響：temp 目錄造信件根，跑完刪。⛔ 不碰真實信件庫。
#nullable enable
using SCP.Core.Letters;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow LetterLocaleConflictCleanRoom()
    {
        const string aName = "收尾信現地衝突：判讀（留痕＋內文行）／小歇信回傳衝突清單／brief 去重並把寫錯的那行換成警告／不是本區才標（淨室，TASK-0418）";
        string d = Path.Combine(Path.GetTempPath(), "senate_locale0418_" + Guid.NewGuid().ToString("N")[..8]);
        var aFails = new List<string>();
        try
        {
            const string aSame = "📍 現地：區域 `Florin` ／ 專案 `LY`";
            const string aWrong = "📍 現地：區域 `BTC` ／ 專案 `Bar`";
            string aGood = "---\ntype: letter_to_future_self\nwritten_by_persona: probe\nwritten_at: 2026-01-01T00:00:00.000Z\n"
                           + "region: Florin\nproject: LY\n---\n\n" + aSame + "\n\n第一封\n";
            string aBad = "---\ntype: letter_to_future_self\nwritten_by_persona: probe\nwritten_at: 2026-01-02T00:00:00.000Z\n"
                          + "region: Florin\nproject: LY\nregion_as_written: BTC\nproject_as_written: Bar\n---\n\n" + aWrong + "\n\n第二封\n";

            // ① 判讀
            int n1 = SCP_LetterText.LocaleConflicts(aBad).Count;
            int n0 = SCP_LetterText.LocaleConflicts(aGood).Count;
            int nU = SCP_LetterText.LocaleConflicts(aBad.Replace("region: Florin", "region: unstated")).Count;
            if (n1 != 3) aFails.Add($"🔴 寫錯的那封抓到 {n1} 項（期望 3：region／project 留痕＋內文那行）");
            if (n0 != 0) aFails.Add($"跟機器值相同的那封被報了 {n0} 項");
            if (nU != 0) aFails.Add($"機器值 unstated 時還是判了 {nU} 項（沒有基準不該判）");

            // ② 小歇信寫入端
            string aLetters = Path.Combine(d, "letters");
            string aDataRoot = Path.Combine(d, "LY", "AgentCommands");
            Directory.CreateDirectory(aDataRoot);
            Directory.CreateDirectory(Path.Combine(aLetters, "probe", "profile"));
            var w = SCP_LetterWriter.WriteSelfLetter(aLetters, "probe", "probe-agent", aWrong + "\n\n小歇內文", iRegion: "Florin", iDataRoot: aDataRoot);
            if (w.LocaleConflicts.Count != 1) aFails.Add($"🔴 小歇信寫入端回 {w.LocaleConflicts.Count} 項衝突（期望 1）");

            // ③④ brief：兩封收尾信（小歇信不進 wakes/）
            string aWakes = Path.Combine(aLetters, "probe", "wakes");
            Directory.CreateDirectory(aWakes);
            File.WriteAllText(Path.Combine(aWakes, "000001_20260101T000000Z.md"), aGood);
            File.WriteAllText(Path.Combine(aWakes, "000002_20260102T000000Z.md"), aBad);
            File.Copy(Path.Combine(aWakes, "000002_20260102T000000Z.md"), Path.Combine(aLetters, "probe", "_latest.md"), true);
            string aMain = SCP_WakeBrief.Build(aLetters, "probe", 3, null, "BTC", new SCP_WakeBriefSettings()).Main;
            // 在 BTC 醒來 ⇒ 每一行 LocaleLine 都帶「不是本區」尾巴；**一字不差**等於 aSame 的行只可能是內文那行被重印。
            int aSameCount = aMain.Split('\n').Count(l => l.Trim() == aSame);
            if (aMain.Contains(aWrong)) aFails.Add("🔴 brief 把寫錯的現地原樣印出來了");
            if (!aMain.Contains("以機器值為準")) aFails.Add("🔴 brief 沒把寫錯的那行換成警告");
            if (aSameCount != 0) aFails.Add($"🔴 內文那行（跟機器值相同）被原樣重印了 {aSameCount} 次（期望 0：LocaleLine 已經印過）");
            if (!aMain.Contains("不是本區")) aFails.Add("🔴 在 BTC 醒來讀 Florin 的信，沒有標「不是本區」");
            string aMainSame = SCP_WakeBrief.Build(aLetters, "probe", 3, null, "Florin", new SCP_WakeBriefSettings()).Main;
            if (aMainSame.Contains("不是本區")) aFails.Add("反向對照：同區醒來也標了「不是本區」");

            return new CheckRow(aName,
                aFails.Count == 0
                    ? $"判讀 寫錯={n1}／相同={n0}／unstated={nU}；小歇信衝突 {w.LocaleConflicts.Count}；brief 不印錯值、換成警告、內文同值行重印 {aSameCount} 次；BTC 醒來標不是本區、Florin 醒來不標（temp 目錄）"
                    : string.Join("；", aFails),
                aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally { try { Directory.Delete(d, true); } catch (Exception) { } }
    }
}
