// 區塊職責：把 `senate.local.json` 的 `awakening` 區塊**包成 ISCP_Prefs**，給搬進 SCP_Core 的頁面用。
// 物理意義：某一頁要讀寫「信件夾根」，而那個值住在 senate.local.json ——
//           一個 Unity 那側不存在的檔。頁面直接讀它就永遠搬不動（Coding_Standards.md §3）。
//           ⇒ 這裡是**轉接頭**：對外是 prefs 介面，對內仍然走 `SenateConfig.Load/Save`。
//
// ⚠ **目前沒有讀者**（2026-09-05）：唯一的呼叫端「登入狀態」頁已改走
//   `ISCP_GuiAppContext.LettersRoot`（解析器），因為這一格支援 `auto` ——
//   讀原始值會讓頁面拿字面 `"auto"` 去掃目錄。⇒ 本轉接頭現在只剩**路由登記**還活著。
//   要刪它請連 `SenateModel` 的 `.Route(...)` 一起刪；留著的唯一理由是
//   「下一個要寫 awakening 區塊的頁面不必重造轉接頭」，而那還不是讀數，只是預期。
//
//           🩸 為什麼不讓 SCP_JsonPrefs 直接寫這個檔（那樣一行程式都不用寫）：
//           那會讓 senate.local.json 有**兩個寫入端**，各有一套欄位保留與格式化規則。
//           `SenateConfig` 有它自己的 `Extra`（`"//"` 註解與未知欄位）與型別化欄位，
//           而 2026-08-23 已經有一筆血證是「寫入端省略不可逆」把註解整行吃掉。
//           **一個檔只能有一個寫入端。**
// 數值影響：Read 純讀（每次讀整份 —— 這個檔很小，換來的是不必管快取失效）；
//           Write 走 SenateConfig 讀→改→存，**含回讀確認**。
// ⚠ 只認得 awakening 底下的已知 key。其他 key 回 ReadError 並說出原因 ——
//   靜靜回 Missing 會讓「打錯 key」跟「沒設定過」同形，而那正是 prefs 這一層要治的病。
using SCP.Core.Prefs;

namespace Senate.Core;

public sealed class SenateAwakeningPrefs : ISCP_Prefs
{
    /// <summary>本轉接頭負責的 section 名 —— 跟 senate.local.json 的頂層 key 同名。</summary>
    public const string SectionName = "awakening";

    public const string KeyLettersRoot = "lettersRoot";

    /// <summary>信件夾根（絕對路徑）。空 ＝ 還沒設定。</summary>
    public static readonly SCP_PrefKey<string> LettersRoot =
        SCP_PrefKey.String(SectionName, KeyLettersRoot, "");

    readonly string m_RepoRoot;

    public SenateAwakeningPrefs(string iRepoRoot) { m_RepoRoot = iRepoRoot; }

    // ── 讀 ────────────────────────────────────────────────────────

    public SCP_PrefRead<string> Read(SCP_PrefKey<string> iKey)
    {
        if (iKey.Section != SectionName) return Unsupported<string>(iKey.Path);

        AwakeningSettings? aCfg;
        try { aCfg = SenateConfig.Load(SenateConfig.DefaultPath(m_RepoRoot))?.Awakening; }
        catch (Exception e)
        {
            // 檔在但壞掉 ⇒ ReadError，**不是** Missing。混起來的話「壞檔」會長得像「還沒設定」，
            // 而那時按下儲存就是一次不可逆的覆寫。
            return SCP_PrefRead<string>.Failed($"senate.local.json 讀不了（{iKey.Path}）：{e.Message}");
        }
        if (aCfg == null) return SCP_PrefRead<string>.Missing();       // 還沒 init —— 不是錯誤

        if (iKey.Name == KeyLettersRoot)
        {
            string aRoot = aCfg.LettersRoot ?? "";
            return aRoot.Length == 0 ? SCP_PrefRead<string>.Missing() : SCP_PrefRead<string>.Present(aRoot);
        }
        return Unsupported<string>(iKey.Path);
    }

    public SCP_PrefRead<long> Read(SCP_PrefKey<long> iKey) => Unsupported<long>(iKey.Path);
    public SCP_PrefRead<bool> Read(SCP_PrefKey<bool> iKey) => Unsupported<bool>(iKey.Path);
    public SCP_PrefRead<double> Read(SCP_PrefKey<double> iKey) => Unsupported<double>(iKey.Path);

    public string Get(SCP_PrefKey<string> iKey) { var r = Read(iKey); return r.IsPresent ? r.Value : iKey.Default; }
    public long Get(SCP_PrefKey<long> iKey) => iKey.Default;
    public bool Get(SCP_PrefKey<bool> iKey) => iKey.Default;
    public double Get(SCP_PrefKey<double> iKey) => iKey.Default;

    // ── 寫 ────────────────────────────────────────────────────────

    public (bool Ok, string Message) Write(SCP_PrefKey<string> iKey, string iValue)
    {
        if (iKey.Section != SectionName || iKey.Name != KeyLettersRoot)
            return (false, $"{iKey.Path}：本轉接頭只支援寫 {SectionName}.{KeyLettersRoot}");
        // 走既有的唯一寫入端（讀→改→存＋回讀確認＋保留註解與未知欄位）
        return PersonaLetters.SaveLettersRoot(m_RepoRoot, iValue);
    }

    public (bool Ok, string Message) Write(SCP_PrefKey<long> iKey, long iValue) => WriteUnsupported(iKey.Path);
    public (bool Ok, string Message) Write(SCP_PrefKey<bool> iKey, bool iValue) => WriteUnsupported(iKey.Path);
    public (bool Ok, string Message) Write(SCP_PrefKey<double> iKey, double iValue) => WriteUnsupported(iKey.Path);

    /// <summary>⛔ 不支援 —— 這個 section 是型別化設定，整段覆寫要走 SenateConfig 自己的路。</summary>
    public T? LoadSection<T>(string iSection, Action<string>? iWarn = null) where T : class
    {
        iWarn?.Invoke($"'{iSection}' 由 SenateAwakeningPrefs 轉接，不支援整段讀成 model（請用具名 key）");
        return null;
    }

    /// <summary>⛔ 同上：整段寫入不從這裡走，避免繞過 SenateConfig 的欄位保留。</summary>
    public (bool Ok, string Message) SaveSection(string iSection, object iSettings)
        => (false, $"'{iSection}' 由 SenateAwakeningPrefs 轉接，不支援整段寫入（請用具名 key）");

    // ── 共用 ──────────────────────────────────────────────────────

    static SCP_PrefRead<T> Unsupported<T>(string iPath)
        => SCP_PrefRead<T>.Failed($"{iPath}：SenateAwakeningPrefs 不認得這個 key（只有 "
                                  + $"{SectionName}.{KeyLettersRoot}）");

    static (bool Ok, string Message) WriteUnsupported(string iPath)
        => (false, $"{iPath}：SenateAwakeningPrefs 只支援寫 {SectionName}.{KeyLettersRoot}");
}
