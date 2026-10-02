// 區塊職責：**skill／工具缺相依時的那個結果** —— 跟一般失敗不同形，內含要給使用者看的說明與安裝指令。
// 物理意義：Tim 2026-10-02：「當 skill 依賴到套件但未安裝時，詢問使用者是否安裝，使用者同意後透過 CLI 安裝」。
//           ⇒ 流程：skill 在 SKILL.md frontmatter 宣告 `requires_install: [項目 id, …]`
//             → 跑 `senate cmd install --arg op=check --arg skill=<名>`（或 `ids=`）
//             → 缺的話回 **exit 3**（量不到回 exit 4），印出每一項的大小、來源、會裝到哪裡、以及安裝指令
//             → agent 照實轉告、**問一次**；使用者同意才跑那行安裝指令。
// 數值影響：純讀（會起一個 Python 探針量 pip 項目）。
// ⛔ 本層**不替任何人決定要不要裝** —— 它只負責讓「缺什麼、代價多大」講得清楚到可以問人。
namespace Senate.Core;

public static class InstallRequire
{
    /// <summary>有項目沒裝（或壞了／不完整）⇒ 要問使用者要不要裝。</summary>
    public const int MissingExitCode = 3;

    /// <summary>量不到（例：找不到 Python）⇒ 不知道缺不缺，⛔ 不可以當成「缺」去問要不要裝。</summary>
    public const int UnknownExitCode = 4;

    public const string FrontmatterKey = "requires_install";

    /// <summary>找 skill 的 SKILL.md：先當路徑（檔或資料夾），再到各 agent 的安裝目錄找名字。</summary>
    public static string? FindSkillFile(string iSkill, IEnumerable<string> iSearchRoots, out List<string> oTried)
    {
        oTried = new List<string>();
        foreach (string aCand in new[] { iSkill, Path.Combine(iSkill, "SKILL.md") })
        {
            if (File.Exists(aCand) && aCand.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(aCand);
        }
        foreach (string aRoot in iSearchRoots)
        {
            string p = Path.Combine(aRoot, iSkill, "SKILL.md");
            oTried.Add(p.Replace('\\', '/'));
            if (File.Exists(p)) return Path.GetFullPath(p);
        }
        return null;
    }

    /// <summary>
    /// 讀 frontmatter 的 `requires_install`。接受 `[a, b]`、`a, b`、或下一行起的 `- a` 清單。
    /// 回 null ＝ 沒有這一格（＝這個 skill 沒宣告相依，不是「相依是空的」）。
    /// </summary>
    public static List<string>? ReadSkillRequires(string iSkillFile)
    {
        string[] aLines = File.ReadAllLines(iSkillFile);
        if (aLines.Length == 0 || aLines[0].Trim() != "---") return null;
        for (int i = 1; i < aLines.Length; i++)
        {
            string l = aLines[i];
            if (l.Trim() == "---") break;
            if (!l.StartsWith(FrontmatterKey + ":", StringComparison.Ordinal)) continue;
            string v = l.Substring(FrontmatterKey.Length + 1).Trim();
            var aIds = new List<string>();
            if (v.Length == 0)
            {
                for (int k = i + 1; k < aLines.Length && aLines[k].TrimStart().StartsWith("- ", StringComparison.Ordinal); k++)
                    aIds.Add(aLines[k].TrimStart().Substring(2).Trim().Trim('"', '\''));
            }
            else
            {
                foreach (string s in v.Trim('[', ']').Split(','))
                    if (s.Trim().Trim('"', '\'').Length > 0) aIds.Add(s.Trim().Trim('"', '\''));
            }
            return aIds;
        }
        return null;
    }

    /// <summary>給 agent 照念的安裝指令（只列真的要裝的那幾個；相依由 install 自己補）。</summary>
    public static string InstallCommand(IEnumerable<string> iIds)
        => "senate cmd install --arg op=install --arg ids=" + string.Join(",", iIds) + " --arg confirm=1";
}
