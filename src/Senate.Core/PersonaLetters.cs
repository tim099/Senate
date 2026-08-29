// 區塊職責：**persona 信件庫的定位與上線狀態讀取** —— 設定「信件夾根目錄」在哪、
//           那底下有哪些 persona、誰現在在線。
// 物理意義：這套 agent 系統的 persona 資料住在
//           `<AgentCommands>/ChatTavern/baton/letters/<persona>/`；
//           而「在線」的真相源**不在信件庫裡**，是 `<AgentCommands>/_session/_persona_<name>.json`
//           這顆 session lock（登入時寫、登出時刪）。⇒ 本檔只讀檔，不寫 lock、不動 registry。
// 數值影響：Scan 是純讀（列目錄 ＋ 讀 lock json）。會寫檔的只有設定那一支，走 SenateConfig.Save。
//
// ⚠ 三態不可壓成兩態（本檔最重要的一條）：
//   「沒有人在線」與「我量不到」必須看得出差別。`_session` 找不到的時候，
//   把每個人都印成「離線」是**捏造讀數** —— 那個畫面跟真的全體離線一模一樣。
//   ⇒ PersonaOnline.Unknown 存在的唯一理由就是這個。
//
// 📌 「誰是 persona」用資料判、不用名字猜：**letters 底下有 `profile/` 子目錄的那些**。
//   實測基準（2026-08-29，D:/Unity/Bar）：letters 底下 35 個目錄，其中 21 個有 `profile/`，
//   而那 21 個跟 `AwakenInit/_persona_profile_snapshot.json` 的 `pool` 陣列**逐字相同**。
//   用名字猜（跳過底線開頭、跳過 Template…）會在下一個命名慣例出現時安靜地漏人。
using System.Text.Json;

namespace Senate.Core;

/// <summary>persona 的上線狀態。⚠ 三態 —— <see cref="Unknown"/> 不是「大概離線」。</summary>
public enum PersonaOnline
{
    /// <summary>量不到（`_session` 目錄找不到／讀不了／lock 檔壞了）。**不可以顯示成離線。**</summary>
    Unknown = 0,

    /// <summary>有 session lock。</summary>
    Online = 1,

    /// <summary>`_session` 讀得到，而這個人沒有 lock。</summary>
    Offline = 2,
}

/// <summary>一位 persona 的現況（信件庫 ＋ lock 兩邊併起來的視圖）。</summary>
public sealed class PersonaStatus
{
    public string Name { get; set; } = "";

    public PersonaOnline Online { get; set; } = PersonaOnline.Unknown;

    /// <summary>信件庫目錄（絕對路徑）。</summary>
    public string LettersDir { get; set; } = "";

    // ── 以下只有 Online 時才有值（來自 lock 檔本身，不是別處的快取）──
    public string Agent { get; set; } = "";
    public string ActualAgent { get; set; } = "";
    public string Model { get; set; } = "";
    public string BankAccount { get; set; } = "";
    public string LockedAt { get; set; } = "";
    public string SessionKey { get; set; } = "";
    public int Pid { get; set; }
    public int WakeExpected { get; set; }
    public string LockPath { get; set; } = "";

    /// <summary>lock 檔在、但解析失敗時的原因。⚠ 有值時 <see cref="Online"/> 是 Unknown 不是 Offline。</summary>
    public string? LockError { get; set; }
}

/// <summary>一次掃描的結果。**問題清單是回傳值的一部分**，不是丟例外或印到別處。</summary>
public sealed class PersonaScan
{
    /// <summary>設定裡的信件夾根（已正規化）。空 ＝ 還沒設定。</summary>
    public string LettersRoot { get; set; } = "";

    /// <summary>這次實際用的 `_session` 目錄。空 ＝ 沒找到（此時所有人都是 Unknown）。</summary>
    public string SessionDir { get; set; } = "";

    /// <summary>`_session` 是推導來的還是設定裡指名的 —— 顯示端要說得出來源。</summary>
    public bool SessionDirDerived { get; set; }

    public List<PersonaStatus> Personas { get; } = new();

    /// <summary>人可讀的問題（空 ＝ 沒問題）。⚠ 有問題時畫面必須顯示，不可只顯示一張空清單。</summary>
    public List<string> Problems { get; } = new();

    /// <summary>
    /// 有沒有真的走到「列出信件夾底下的目錄」那一步。
    /// <para>🩸 為什麼需要這一格（2026-08-29 實測自摔）：路徑不存在時 Scan 提早返回，
    /// 而顯示端照樣把 <see cref="SessionDir"/>／<see cref="SessionDirDerived"/> 的**預設值**畫出來
    /// ⇒ 畫面說「來源：設定裡指名」，可是那一輪根本沒有解析過任何東西。
    /// 「沒查」與「查了是空的」在欄位值上同形 —— 靠這一格分開。</para>
    /// </summary>
    public bool Enumerated { get; set; }

    public int OnlineCount => Personas.Count(p => p.Online == PersonaOnline.Online);
    public int OfflineCount => Personas.Count(p => p.Online == PersonaOnline.Offline);
    public int UnknownCount => Personas.Count(p => p.Online == PersonaOnline.Unknown);
}

/// <summary>
/// 信件庫設定的讀寫與掃描。**之後的登入／早安流程從這裡拿路徑**，不要各自推導一份。
/// <para>設定住在 `senate.local.json` 的 <c>awakening</c> 區塊 —— 刻意**不另開一份檔**：
/// 同一份資料兩個檔就是漂移的起點（專案關聯頁同一條規矩）。</para>
/// </summary>
public static class PersonaLetters
{
    /// <summary>`sessionDir` 填這個值 ＝ 由 <see cref="ResolveSessionDir"/> 從信件夾往上推導。</summary>
    public const string AutoSessionDir = "auto";

    /// <summary>lock 檔名前綴（awakening 端 write_lock() 的格式，是跨端契約不是本檔的私事）。</summary>
    public const string LockPrefix = "_persona_";

    /// <summary>persona 的判準：信件夾底下有這個子目錄。</summary>
    public const string ProfileDirName = "profile";

    // ── 設定檔讀寫 ────────────────────────────────────────────────

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

    // ── 路徑推導 ──────────────────────────────────────────────────

    /// <summary>
    /// 找 `_session` 目錄。設定裡指名了就**逐字採用**（存不存在由 <see cref="Scan"/> 出聲）；
    /// 填 <see cref="AutoSessionDir"/> 或留空 → 從信件夾**逐層往上找第一個含 `_session` 的祖先**。
    /// <para>⚠ 刻意不寫死「往上三層」：`letters → baton → ChatTavern → AgentCommands` 是這個
    /// 專案今天的形狀，不是契約。寫死層數的錯法是**安靜的** —— 指到一個不存在的目錄，
    /// 然後所有人顯示離線。</para>
    /// </summary>
    public static (string Dir, bool Derived) ResolveSessionDir(string iLettersRoot, string? iConfigured)
    {
        string aCfg = CleanPath(iConfigured ?? "");
        if (aCfg.Length > 0 && !string.Equals(aCfg, AutoSessionDir, StringComparison.OrdinalIgnoreCase))
            return (aCfg, false);

        string aRoot = CleanPath(iLettersRoot);
        if (aRoot.Length == 0) return ("", true);

        DirectoryInfo aDir;
        try { aDir = new DirectoryInfo(aRoot); }
        catch { return ("", true); }

        // 從信件夾自己開始往上（含自己）—— 找得到就停，找不到回空字串讓 Scan 講話。
        for (DirectoryInfo? d = aDir; d != null; d = d.Parent)
        {
            string aCandidate = Path.Combine(d.FullName, "_session");
            if (Directory.Exists(aCandidate)) return (CleanPath(aCandidate), true);
        }
        return ("", true);
    }

    // ── 掃描 ──────────────────────────────────────────────────────

    /// <summary>
    /// 掃一次：信件夾底下有 `profile/` 的目錄 ＝ persona，再對照 `_session` 的 lock 判上線。
    /// <para>⚠ 找不到 `_session` 時所有人是 <see cref="PersonaOnline.Unknown"/> 並在
    /// <see cref="PersonaScan.Problems"/> 留一句 —— **不會退化成「全部離線」**。</para>
    /// </summary>
    public static PersonaScan Scan(string? iLettersRoot, string? iConfiguredSessionDir)
    {
        var aScan = new PersonaScan();
        string aRoot = CleanPath(iLettersRoot ?? "");
        aScan.LettersRoot = aRoot;

        if (aRoot.Length == 0)
        {
            aScan.Problems.Add("還沒設定 persona 信件夾根目錄。");
            return aScan;
        }
        if (!Directory.Exists(aRoot))
        {
            aScan.Problems.Add($"信件夾根目錄不存在：{aRoot}");
            return aScan;
        }

        (string aSessionDir, bool aDerived) = ResolveSessionDir(aRoot, iConfiguredSessionDir);
        aScan.SessionDir = aSessionDir;
        aScan.SessionDirDerived = aDerived;

        // lock 表：persona（不分大小寫）→ lock 檔路徑。null ＝ 量不到（跟「空表」不同！）
        Dictionary<string, string>? aLocks = null;
        if (aSessionDir.Length == 0)
        {
            aScan.Problems.Add(
                $"從 {aRoot} 往上找不到 `_session` 目錄 ⇒ 上線狀態**量不到**（顯示為「未知」，不是離線）。");
        }
        else if (!Directory.Exists(aSessionDir))
        {
            aScan.Problems.Add(
                $"`_session` 目錄不存在：{aSessionDir} ⇒ 上線狀態**量不到**（顯示為「未知」，不是離線）。");
        }
        else
        {
            try
            {
                var aFound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string aFile in Directory.GetFiles(aSessionDir, LockPrefix + "*.json"))
                {
                    string aName = Path.GetFileNameWithoutExtension(aFile);
                    if (aName.Length <= LockPrefix.Length) continue;
                    aFound[aName.Substring(LockPrefix.Length)] = aFile;
                }
                aLocks = aFound;
            }
            catch (Exception e)
            {
                aScan.Problems.Add($"讀 `_session` 失敗 ⇒ 上線狀態量不到：{e.GetType().Name}: {e.Message}");
            }
        }

        string[] aDirs;
        try { aDirs = Directory.GetDirectories(aRoot); }
        catch (Exception e)
        {
            aScan.Problems.Add($"列不出信件夾底下的目錄：{e.GetType().Name}: {e.Message}");
            return aScan;
        }
        aScan.Enumerated = true;

        foreach (string aPersonaDir in aDirs.OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            string aName = Path.GetFileName(aPersonaDir);
            if (!Directory.Exists(Path.Combine(aPersonaDir, ProfileDirName))) continue;   // 不是 persona

            var aStatus = new PersonaStatus { Name = aName, LettersDir = CleanPath(aPersonaDir) };
            if (aLocks == null)
            {
                aStatus.Online = PersonaOnline.Unknown;                 // 量不到，不是離線
            }
            else if (aLocks.TryGetValue(aName, out string? aLockPath))
            {
                aStatus.LockPath = CleanPath(aLockPath);
                ReadLock(aLockPath, aStatus);
            }
            else
            {
                aStatus.Online = PersonaOnline.Offline;
            }
            aScan.Personas.Add(aStatus);
        }

        // lock 有、而信件夾沒有那個人 ⇒ 說出來（那是兩份資料對不上，不是沒事）
        if (aLocks != null)
        {
            var aKnown = new HashSet<string>(aScan.Personas.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
            foreach (string aOrphan in aLocks.Keys.Where(k => !aKnown.Contains(k)).OrderBy(k => k))
                aScan.Problems.Add($"`_session` 有 {aOrphan} 的 lock，但信件夾裡沒有這個人（兩份資料對不上，不是沒事）。");
        }
        return aScan;
    }

    /// <summary>讀一顆 lock 檔。解析失敗 ⇒ Unknown ＋ LockError（**不是 Offline**：檔明明在）。</summary>
    static void ReadLock(string iPath, PersonaStatus oStatus)
    {
        try
        {
            using JsonDocument aDoc = JsonDocument.Parse(File.ReadAllText(iPath));
            JsonElement aRoot = aDoc.RootElement;
            oStatus.Online = PersonaOnline.Online;
            oStatus.Agent = Str(aRoot, "agent");
            oStatus.ActualAgent = Str(aRoot, "actual_agent");
            oStatus.Model = Str(aRoot, "model");
            oStatus.BankAccount = Str(aRoot, "bank_account");
            oStatus.LockedAt = Str(aRoot, "locked_at");
            oStatus.SessionKey = Str(aRoot, "session_key");
            oStatus.Pid = Num(aRoot, "pid");
            oStatus.WakeExpected = Num(aRoot, "wake_expected");
        }
        catch (Exception e)
        {
            oStatus.Online = PersonaOnline.Unknown;
            oStatus.LockError = $"{e.GetType().Name}: {e.Message}";
        }
    }

    static string Str(JsonElement iRoot, string iName)
        => iRoot.TryGetProperty(iName, out JsonElement v) && v.ValueKind == JsonValueKind.String
           ? (v.GetString() ?? "") : "";

    static int Num(JsonElement iRoot, string iName)
        => iRoot.TryGetProperty(iName, out JsonElement v) && v.ValueKind == JsonValueKind.Number
           && v.TryGetInt32(out int n) ? n : 0;

    /// <summary>去掉包住整串的引號與尾斜線（檔案總管「複製路徑」帶雙引號 —— 專案關聯頁同一課）。</summary>
    public static string CleanPath(string? iRaw)
    {
        string s = (iRaw ?? "").Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        return s.Replace('\\', '/').TrimEnd('/');
    }
}
