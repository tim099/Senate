// 區塊職責：**信件夾根設在哪** —— 讀寫 senate.local.json 的 `awakening` 區塊。
// 物理意義：⭐ 2026-08-30：掃描那一半（有哪些 persona、誰在線、lock 怎麼讀）
//           **搬進 SCP_Core** 的 `SCP.Core.Letters.SCP_PersonaLetters`（六步的第 2 步）。
//           留在這裡的只有「設定住哪個檔、長什麼形狀」—— 那是**宿主的政策**，
//           而 Unity 那側沒有 senate.local.json，帶著它就搬不動（Coding_Standards.md §3）。
//           ⇒ 本檔現在是一層薄殼：SenateConfig ←→ 掃描層之間的轉接。
// 數值影響：Load 純讀；SaveLettersRoot 走 SenateConfig.Save（讀→改→存，保留註解與未知欄位）。
//
// ⚠ 這裡**不再定義** PersonaScan / PersonaStatus / PersonaOnline —— 那三個型別在 SCP_Core
//   （改名為 SCP_ 前綴）。呼叫端請 `using SCP.Core.Letters;`。
//   留一份同名型別在這裡的代價是兩份定義各自演化，而編譯器只會在它們真的碰在一起時才喊，
//   那通常是很久以後。
using SCP.Core.Letters;

namespace Senate.Core;

public static class PersonaLetters
{
    /// <summary>`sessionDir` 填這個值 ＝ 由掃描層從信件夾往上推導。（值的定義在 SCP_Core。）</summary>
    public const string AutoSessionDir = SCP_PersonaLetters.AutoSessionDir;

    /// <summary>
    /// 讀設定檔並取出信件夾根。**設定檔不存在 → 回 null**（那是「還沒 init」不是錯誤）；
    /// 檔在但壞掉 → 照 <see cref="SenateConfig.Load"/> 丟例外（不可靜默降級成「沒設定」）。
    /// </summary>
    public static string? LoadLettersRoot(string iRepoRoot)
    {
        SenateConfig? aCfg = SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot));
        if (aCfg == null) return null;
        string aRoot = CleanPath(aCfg.Awakening.LettersRoot);
        return aRoot.Length > 0 ? aRoot : null;
    }

    /// <summary>設定檔整份讀出來（登入／早安流程要 sessionDir 這種其他欄位時用）。</summary>
    public static AwakeningSettings? LoadSettings(string iRepoRoot)
        => SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot))?.Awakening;

    /// <summary>
    /// 把信件夾根寫回設定檔。**讀→改→存**（不是重建一份）——
    /// 這樣使用者手寫的 <c>"//"</c> 註解與本版不認得的欄位才會被 Extra 接住寫回去。
    /// </summary>
    /// <returns>(成功, 人可讀訊息)。⚠ 失敗不丟例外：呼叫端是 UI，需要的是一句話不是堆疊。</returns>
    public static (bool Ok, string Message) SaveLettersRoot(string iRepoRoot, string iLettersRoot)
    {
        string aPath = SenateConfig.DefaultPath(iRepoRoot);
        SenateConfig? aCfg;
        try { aCfg = SenateConfig.Load(aPath); }
        catch (Exception e) { return (false, $"設定檔讀不了，沒有寫入（檔案沒有被動過）：{e.Message}"); }
        if (aCfg == null) return (false, $"還沒有 {Path.GetFileName(aPath)} —— 先跑 `senate init`");

        string aClean = CleanPath(iLettersRoot);
        aCfg.Awakening.LettersRoot = aClean;
        try { aCfg.Save(aPath); }
        catch (Exception e) { return (false, $"寫檔失敗：{e.GetType().Name}: {e.Message}"); }

        // 回讀確認 —— 寫入端會替自己說謊。
        try
        {
            string? aBack = SenateConfig.Load(aPath)?.Awakening.LettersRoot;
            return string.Equals(aBack, aClean, StringComparison.Ordinal)
                ? (true, $"已存進 {Path.GetFileName(aPath)}（回讀確認：{aBack}）")
                : (false, $"寫進去了但回讀是「{aBack}」（期望「{aClean}」）—— 有第二個寫入者？");
        }
        catch (Exception e) { return (false, $"寫完之後回讀失敗（檔案可能壞了）：{e.Message}"); }
    }

    /// <summary>掃一次信件夾（實作在 SCP_Core；這裡只是既有呼叫端的入口）。</summary>
    public static SCP_PersonaScan Scan(string? iLettersRoot, string? iConfiguredSessionDir)
        => SCP_PersonaLetters.Scan(iLettersRoot, iConfiguredSessionDir);

    /// <summary>去掉包住整串的引號與尾斜線。（實作在 SCP_Core —— 兩處各寫一份會分岔。）</summary>
    public static string CleanPath(string? iRaw) => SCP_PersonaLetters.CleanPath(iRaw);
}
