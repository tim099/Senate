// 區塊職責：**Senate 自己的資料根版面** —— repo 根底下 `SenateData/` 的唯一決定點。
// 物理意義：Senate 執行時會產生三類東西 —— 人編輯的設定、程式替使用者寫的偏好、進程活著才有意義的
//           狀態。它們原本散在 repo 根（`senate.local.json` / `senate.pages.local.json` / `imgui.ini`）
//           與**產物目錄**（`build/_process_registry` / `build/ui_session.json`）。
//           後者是實害不是美觀問題：`build/` 是**產物目錄** —— gitignored、不在任何備份裡、
//           而且是人「東西壞了就整個刪掉重來」時第一個下手的地方（`rm -rf build/` / 換一台 clone）。
//           把狀態放進去等於託付給一個隨時會被合理刪除的位置，而刪掉之後的行為
//           跟「這台機器沒設定過」一模一樣 —— 三態同形（見 <SCP_Core>/Docs~/Coding_Standards.md §3.3）。
//           ⚠ 讀數（2026-09-01 實測）：`build.sh` / `build.ps1` 目前**並不會**清 `build/`，
//             所以危險不是「每次 build 都會沒」，是「它沒有任何理由被保住」。
//           ⇒ 收進 `SenateData/`，並依「**這個檔掉了，使用者要不要重做工？**」切三層。
// 數值影響：純字串組裝，不碰磁碟（`EnsureDirectories` 例外，且只建目錄不寫檔）。
//
// ⚠ 這是 Senate **宿主**的版面，刻意不進 SCP_Core：
//   SCP_Core 管的是跨端契約的版面（`SCP_ProjectPaths` / `SCP_DataPaths` / `SCP_LettersPaths`），
//   而 `SenateData/` 只有 Senate 這一個宿主會用 —— Unity 那側沒有這個東西。
//   規則是「一個路徑只能有一個決定點」，不是「路徑一定要在 Core 算」
//   （見 <SCP_Core>/Docs~/Coding_Standards.md §4）。
//
// ⛔ 呼叫端不要自己 `Path.Combine(repoRoot, "SenateData", ...)` —— 那就是第二個決定點。
//    要新的落點就往本類別加一支具名成員。
using System.IO;

namespace Senate.Core;

/// <summary>
/// Senate 資料根（`&lt;repo&gt;/SenateData/`）的版面。**檔名與目錄名只在本檔出現一次。**
/// </summary>
public static class SenatePaths
{
    /// <summary>資料根的目錄名。⚠ 改它要同時改 `.gitignore` 與 Docs/Architecture/Data_Layout.md。</summary>
    public const string DataDirName = "SenateData";

    /// <summary>人編輯的設定 —— 掉了使用者要**重新設定**。</summary>
    public const string ConfigDirName = "config";

    /// <summary>程式替使用者寫的偏好 —— 掉了回預設值，不必重做工。</summary>
    public const string PrefsDirName = "prefs";

    /// <summary>進程活著才有意義的狀態 —— **可隨時刪，而且應該被清**。</summary>
    public const string RuntimeDirName = "runtime";

    public static string DataRoot(string iRepoRoot) => Path.Combine(iRepoRoot, DataDirName);

    public static string ConfigDir(string iRepoRoot) => Path.Combine(DataRoot(iRepoRoot), ConfigDirName);
    public static string PrefsDir(string iRepoRoot) => Path.Combine(DataRoot(iRepoRoot), PrefsDirName);
    public static string RuntimeDir(string iRepoRoot) => Path.Combine(DataRoot(iRepoRoot), RuntimeDirName);

    // ── config/：人編輯的 ──────────────────────────────────────────────
    /// <summary>本機設定（不入版控，含機器絕對路徑）。</summary>
    public static string LocalConfig(string iRepoRoot) => Path.Combine(ConfigDir(iRepoRoot), "senate.local.json");

    /// <summary>入版控的樣板（**不得含任何機器絕對路徑**，見 Coding_Standards §3.4）。</summary>
    public static string ExampleConfig(string iRepoRoot) => Path.Combine(ConfigDir(iRepoRoot), "senate.local.example.json");

    // ── prefs/：程式替使用者寫的 ───────────────────────────────────────
    /// <summary>各頁「儲存本頁設定」的落點。</summary>
    public static string PageStore(string iRepoRoot) => Path.Combine(PrefsDir(iRepoRoot), "senate.pages.local.json");

    /// <summary>ImGui 視窗佈局（使用者自己拖出來的）。</summary>
    public static string ImGuiIni(string iRepoRoot) => Path.Combine(PrefsDir(iRepoRoot), "imgui.ini");

    // ── runtime/：進程活著才有意義 ─────────────────────────────────────
    /// <summary>外部進程註冊表（`SCP_ProcessRegistry`）。</summary>
    public static string ProcessRegistry(string iRepoRoot) => Path.Combine(RuntimeDir(iRepoRoot), "_process_registry");

    /// <summary>CLI 跨呼叫的 UI session（每次 CLI 都是新 process，靠它記住多步操作）。</summary>
    public static string UiSession(string iRepoRoot) => Path.Combine(RuntimeDir(iRepoRoot), "ui_session.json");

    /// <summary>
    /// 把三層目錄建出來。**只建目錄、不寫任何檔**，重複呼叫無副作用。
    /// <para>⚠ 存在的理由是 ImGui：它存 ini 時**不會替你建目錄**，
    /// 目錄不在就靜默不存 —— 而「沒存成功」跟「使用者沒調過版面」在畫面上同形。</para>
    /// </summary>
    public static void EnsureDirectories(string iRepoRoot)
    {
        Directory.CreateDirectory(ConfigDir(iRepoRoot));
        Directory.CreateDirectory(PrefsDir(iRepoRoot));
        Directory.CreateDirectory(RuntimeDir(iRepoRoot));
    }
}
