// 區塊職責：Senate 的本機設定 —— 「這台機器上，這套系統管哪些專案」。
// 物理意義：Senate 是**專案外部**的獨立 repo（不住在任何 Unity 專案裡），
//           所以「要管誰」必須是資料，不能是寫死的路徑。
//           ⇒ 設定檔分兩份，職責不同：
//             · SenateData/config/senate.local.example.json —— **入版控**的樣板（沒有機器路徑）
//             · senate.local.json               —— **不入版控**的實際設定（有絕對路徑）
//           🩸 為什麼一定要分開：機器路徑一旦進了版控，下一台機器 clone 下來會拿到
//             「看起來設定好了、但指向不存在的磁碟」的狀態 —— 那跟「還沒設定」不同形卻同樣安靜。
// 數值影響：純資料 + 讀寫檔。找不到設定檔**不當錯誤**（回 null 並由呼叫端說「還沒 init」），
//           但「檔在、內容壞」是錯誤 —— 這兩態不得同形。
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SCP.Core.Gui;
using SCP.Core.Reflect;

namespace Senate.Core;

/// <summary>一個被 Senate 管理的專案。</summary>
public sealed class SenateProject
{
    /// <summary>顯示名稱（後台清單與 log 用）。空字串視為未設定。</summary>
    public string Name { get; set; } = "";

    /// <summary>專案 git repo 的根目錄（絕對路徑）。</summary>
    public string Root { get; set; } = "";

    // ⛔ 2026-10-07（TASK-0390）：資料根／詞典根／漫畫庫根**不再住在專案上** —— 搬到根物件的 `paths` 區塊
    //   （SenatePathsSettings）。Senate＋Valhalla 要能在沒有任何 Unity 專案時運作；專案只剩「Unity 開發目標」。
    //   舊檔的這三格由 SenateConfig.Load 一次搬過去（MigratePaths），之後只有 `paths` 一份。

    /// <summary>停用的專案仍留在清單裡（不是刪掉）—— 「我關掉它」與「我沒設定過它」是兩件事。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>分群規則 profile 名（對應 config/profiles/&lt;name&gt;.json）。空 ＝ 用內建預設。</summary>
    public string Profile { get; set; } = "";

    /// <summary>
    /// 本版不認得的欄位（含 <c>"//"</c> 註解鍵）—— 讀進來、寫回去，原樣保留。
    /// <para>🩸 2026-08-23：介面尺寸寫回設定檔的第一版把使用者手寫的 <c>"//"</c> 註解整行吃掉了。
    /// 那不是「格式化差異」，是**寫入端省略不可逆**：projects 還在，所以看起來一切正常。
    /// ⇒ 反序列化丟掉的東西，序列化就再也寫不回來 —— 除非像這樣顯式接住。</para>
    /// </summary>
    /// <remarks>⚠ [SCP_Ignore]：自動繪製與自動序列化都跳過它 ——
    /// 它是「本版不認得的欄位」的收容所，攤到畫面上讓人手改只會把它弄壞。</remarks>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>
/// 介面顯示偏好（尺寸／文字寬）。**這台機器的事**，所以住在不入版控的 senate.local.json ——
/// 進了版控就會變成「別人的螢幕決定我的字級」。
/// <para>⚠ 這裡只存「使用者選了什麼」，不存推導值。基準尺寸與縮放規則的唯一來源是
/// <see cref="SCP_GuiStyle"/>；設定檔複製一份基準值就是第二個真相源。</para>
/// </summary>
public sealed class SenateUiSettings
{
    /// <summary>全域縮放。預設走 <see cref="SCP_GuiStyle.DefaultScale"/>（2.0 —— 1.0 實測太小）。</summary>
    public float Scale { get; set; } = SCP_GuiStyle.DefaultScale;

    /// <summary>純文字輸出寬（字元格）。⚠ 不吃 Scale —— 終端機的一格是字元不是像素。</summary>
    public int TextWidth { get; set; } = SCP_GuiTextRenderer.DefaultWidth;

    public SCP_GuiStyle ToStyle()
    {
        var aStyle = new SCP_GuiStyle();
        aStyle.SetScale(Scale);
        aStyle.TextWidth = Math.Max(40, TextWidth);
        return aStyle;
    }

    public static SenateUiSettings FromStyle(SCP_GuiStyle iStyle)
        => new() { Scale = iStyle.Scale, TextWidth = iStyle.TextWidth };
}

/// <summary>
/// 喚醒／登入相關設定 —— **persona 資料在哪台機器的哪個資料夾**。
/// <para>跟 <see cref="SenateProject"/> 的分工：那邊宣告「Senate 管哪些專案」（cmd 派遣的對象），
/// 這邊宣告「persona 的信件庫在哪」。今天兩者在同一棵資料樹底下，但**那是巧合不是契約** ——
/// 信件庫可以被搬走、可以是另一台的網路磁碟，而 cmd 派遣的對象不會跟著動。
/// ⇒ 顯式一格，不從 projects[] 推導。</para>
/// </summary>
public sealed class AwakeningSettings
{
    /// <summary>
    /// persona 信件夾根目錄（絕對路徑），例如
    /// <c>&lt;資料根&gt;/ChatTavern/baton/letters</c>。空 ＝ 還沒設定。
    /// </summary>
    public string LettersRoot { get; set; } = "";

    /// <summary>本版不認得的欄位（含 <c>"//"</c> 註解鍵）—— 讀進來、寫回去，原樣保留。</summary>
    /// <remarks>⚠ [SCP_Ignore]：不進畫面、不進自動序列化（同 <see cref="SenateProject.Extra"/>）。</remarks>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>
/// 銀行設定。⚠ 2026-09-17 之後這一區**沒有任何生效的欄位**了（銀行根改成推導）。
/// 區塊留著是為了讓舊檔裡的值讀得回來、能被指著說「這一格已經不生效」。
/// </summary>
public sealed class BankSettings
{
    /// <summary>
    /// ⛔ **已停用**（Tim 2026-09-17）：銀行根改成 <c>&lt;資料根&gt;/Bank</c> 的**推導值**，不再有人填它。
    /// <para>欄位沒有直接刪掉，是因為刪了之後舊檔裡那個值會掉進 <see cref="Extra"/> ——
    /// 原樣保留、原樣寫回、而且**沒有任何一層會說它不生效**。
    /// 留在這裡才有東西可以拿去比對並出聲（見 <see cref="DeadBankRootWarning"/>）。</para>
    /// </summary>
    public string BankRoot { get; set; } = "";

    /// <summary>
    /// 設定檔還留著一個**不生效**的 <c>bankRoot</c> 時回一句話（沒有就回 null）。
    /// <para>⚠ 判準是「填過東西」而不是「填得對不對」：改設定卻沒生效的症狀是**什麼都沒發生**，
    /// 而那跟「我本來就沒設」在畫面上同形。</para>
    /// </summary>
    public string? DeadBankRootWarning()
        => string.IsNullOrWhiteSpace(BankRoot)
            ? null
            : $"⚠ 設定檔的 `bank.bankRoot` 還留著 `{BankRoot}`，而它 **2026-09-17 起不生效**"
              + "（銀行根已改成 `<資料根>/Bank` 的推導值）—— 要換銀行位置請改資料根。";

    /// <summary>本版不認得的欄位（含 <c>"//"</c> 註解鍵）—— 讀進來、寫回去，原樣保留。</summary>
    /// <remarks>⚠ [SCP_Ignore]：不進畫面、不進自動序列化（同其他區塊）。</remarks>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>
/// 安裝系統的設定（TASK-0375）：套件裝進哪一份 Python、模型下載到哪裡。
/// <para>兩格都是**空白＝用預設**（系統 Python／HF 預設快取），不是「還沒設定所以不能用」。</para>
/// </summary>
public sealed class InstallSettings
{
    /// <summary>Python 環境的資料夾（安裝目錄或 venv）。空 ＝ 自動找系統安裝的 Python。</summary>
    public string PythonEnvRoot { get; set; } = "";

    /// <summary>Hugging Face 快取根（HF_HOME）。空 ＝ HF 預設（%USERPROFILE%/.cache/huggingface）。</summary>
    public string ModelsRoot { get; set; } = "";

    /// <summary>本版不認得的欄位（含 <c>"//"</c> 註解鍵）—— 讀進來、寫回去，原樣保留。</summary>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>
/// 這台機器上 Senate 的**全域路徑**（TASK-0390，Tim 2026-10-07：Senate＝Server、Valhalla＝資料 repo，不依賴 Unity）。
/// <para>值的意義與解析在 <c>SCP_PathRegistry</c>；本類只負責「存在檔裡哪一格」—— 讀寫一律經 <see cref="SenatePathBinding"/>。</para>
/// </summary>
public sealed class SenatePathsSettings
{
    /// <summary>AgentCommands 資料根（`SCP_PathId.AgentCommandsRoot`）。空 ＝ 還沒設定（⛔ 不從任何專案推）。</summary>
    public string AgentCommandsRoot { get; set; } = "";

    /// <summary>詞典根（`SCP_PathId.GlossaryRoot`）。<c>"auto"</c> ＝ <c>&lt;Senate 專案根&gt;/Glossary</c>。</summary>
    public string GlossaryRoot { get; set; } = "auto";

    /// <summary>外部漫畫庫根（`SCP_PathId.ComicRoot`）。空字串 ＝ 沒有外部漫畫庫。</summary>
    public string ComicRoot { get; set; } = "";

    /// <summary>本版不認得的欄位 —— 讀進來、寫回去，原樣保留。</summary>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>senate.local.json 的根物件。</summary>
public sealed class SenateConfig
{
    /// <summary>設定格式版本。讀到未知版本要**擋下並說出來**，不要盡力而為。</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>全域路徑（資料根／詞典根／漫畫庫根）。⚠ 不屬於任何專案 —— 沒有 Unity 專案也要解得出來。</summary>
    public SenatePathsSettings Paths { get; set; } = new();

    /// <summary>
    /// 這次 Load 有沒有把舊檔「住在專案上的路徑」搬進 <see cref="Paths"/>（說明文字；空 ＝ 沒搬）。
    /// ⚠ 只在記憶體 —— 下一次 Save 才落檔（Load 不寫檔）。
    /// </summary>
    [JsonIgnore]
    [SCP_Ignore]
    public string PathsMigrationNote { get; set; } = "";

    /// <summary>Unity 開發目標（選填）。⚠ 只給「透過 Unity CLI 操作 Unity 專案」用，Senate 本身的路徑不從這裡推。</summary>
    public List<SenateProject> Projects { get; set; } = new();

    /// <summary>介面顯示偏好。舊設定檔沒有這個區塊 ⇒ 用預設（那是「沒設過」，不是 0）。</summary>
    public SenateUiSettings Ui { get; set; } = new();

    /// <summary>
    /// 喚醒／登入設定（persona 信件庫在哪）。舊設定檔沒有這個區塊 ⇒ 用預設，
    /// 而預設的 <c>lettersRoot</c> 是空字串 ＝「還沒設定」，**不是**某個猜出來的路徑。
    /// <para>純新增欄位 ⇒ schemaVersion 不動（1）：舊檔讀得進來、寫回去會多這一段。</para>
    /// </summary>
    public AwakeningSettings Awakening { get; set; } = new();

    /// <summary>
    /// 銀行設定（新版帳本住哪）。
    /// <para>⚠ **跨專案共用同一套**就是它存在的理由（Tim 2026-09-14）：LY／Bar／Senate 指到同一個根，
    /// 錢才只有一份。舊 Treasury 從各專案自己的資料根推出來 ⇒ **一個區一本帳**，
    /// 而「別區的綁定卻在本區有餘額」（`Codex` 246／`Luna` 84，2026-09-14 實測）
    /// 沒有任何一層會喊。</para>
    /// <para>純新增欄位 ⇒ schemaVersion 不動（1）：舊檔讀得進來、寫回去會多這一段。</para>
    /// </summary>
    public BankSettings Bank { get; set; } = new();

    /// <summary>安裝系統（TASK-0375）。純新增欄位 ⇒ schemaVersion 不動；舊檔沒有這一段 ⇒ 兩格空白＝用預設。</summary>
    public InstallSettings Install { get; set; } = new();

    /// <summary>
    /// 本版不認得的欄位（含 <c>"//"</c> 註解鍵）—— 讀進來、寫回去，原樣保留。
    /// <para>🩸 2026-08-23：介面尺寸寫回設定檔的第一版把使用者手寫的 <c>"//"</c> 註解整行吃掉了。
    /// 那不是「格式化差異」，是**寫入端省略不可逆**：projects 還在，所以看起來一切正常。
    /// ⇒ 反序列化丟掉的東西，序列化就再也寫不回來 —— 除非像這樣顯式接住。</para>
    /// </summary>
    /// <remarks>⚠ [SCP_Ignore]：同上 —— 不進畫面、不進自動序列化。</remarks>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();


    public const int CurrentSchemaVersion = 1;

    static readonly JsonSerializerOptions s_Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // 🩸 不設 Encoder 的話中文會被寫成 \uXXXX：檔案還是合法 JSON，但**人看不懂了** ——
        //    而這份檔的前提就是「使用者會自己手改」。只有機器讀得懂的註解等於沒有註解。
        //    （這裡的 Unsafe 指的是不做 HTML 轉義；本檔寫的是磁碟，不會被塞進網頁。）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>本機設定檔的預設位置。⚠ 版面的決定點是 <see cref="SenatePaths"/>，本處只是轉呼叫。</summary>
    public static string DefaultPath(string iRepoRoot) => SenatePaths.LocalConfig(iRepoRoot);

    /// <summary>入版控的樣板位置。⚠ 同上，版面在 <see cref="SenatePaths"/>。</summary>
    public static string ExamplePath(string iRepoRoot) => SenatePaths.ExampleConfig(iRepoRoot);

    /// <summary>
    /// 讀設定。**檔案不存在 → 回 null**（那是「還沒 init」，不是錯誤）；
    /// 檔在但解析失敗或版本不認得 → 丟例外（那是真的壞了，不可靜默降級）。
    /// </summary>
    public static SenateConfig? Load(string iPath)
    {
        if (!File.Exists(iPath)) return null;
        string aText = File.ReadAllText(iPath);
        SenateConfig? aCfg;
        try { aCfg = JsonSerializer.Deserialize<SenateConfig>(aText, s_Json); }
        catch (JsonException e) { throw new InvalidDataException($"設定檔解析失敗：{iPath}\n{e.Message}", e); }

        if (aCfg == null) throw new InvalidDataException($"設定檔內容是 null：{iPath}");
        if (aCfg.SchemaVersion != CurrentSchemaVersion)
            throw new InvalidDataException(
                $"設定檔 schemaVersion={aCfg.SchemaVersion}，本版只認得 {CurrentSchemaVersion}：{iPath}");
        aCfg.MigratePaths(HasTopLevel(aText, "paths"));
        return aCfg;
    }

    static bool HasTopLevel(string iJson, string iName)
    {
        try
        {
            using JsonDocument aDoc = JsonDocument.Parse(iJson, new JsonDocumentOptions
                { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            foreach (JsonProperty p in aDoc.RootElement.EnumerateObject())
                if (string.Equals(p.Name, iName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        catch (JsonException) { }
        return false;
    }

    // 舊檔（2026-10-07 之前）的三格路徑住在專案上 ⇒ 專案已不認得它們，落在專案的 Extra。
    static readonly string[] s_LegacyProjectPathKeys = { "agentCommandsRoot", "glossaryRoot", "comicRoot" };

    /// <summary>
    /// 舊檔沒有 `paths` 區塊 ⇒ 從**唯一啟用的專案**把三格搬過來（只在記憶體，Save 才落檔），並把專案上的舊鍵拿掉 ——
    /// ⛔ 兩邊都留的話就是兩個真相源。有 `paths` 區塊 ⇒ 專案上殘留的舊鍵一律拿掉（`paths` 才是那一份）。
    /// 啟用專案不唯一 ⇒ 不替人挑，留空並在 <see cref="PathsMigrationNote"/> 說明。
    /// </summary>
    void MigratePaths(bool iHasPathsBlock)
    {
        static string? Take(SenateProject iP, string iKey)
        {
            foreach (string k in iP.Extra.Keys.ToList())
            {
                if (!string.Equals(k, iKey, StringComparison.OrdinalIgnoreCase)) continue;
                JsonElement v = iP.Extra[k];
                iP.Extra.Remove(k);
                return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            }
            return null;
        }

        if (iHasPathsBlock)
        {
            foreach (SenateProject p in Projects) foreach (string k in s_LegacyProjectPathKeys) Take(p, k);
            return;
        }
        var aEnabled = Projects.Where(p => p.Enabled).ToList();
        if (aEnabled.Count != 1)
        {
            if (Projects.Any(p => s_LegacyProjectPathKeys.Any(k => p.Extra.Keys.Any(e => string.Equals(e, k, StringComparison.OrdinalIgnoreCase)))))
                PathsMigrationNote = $"舊檔的路徑住在專案上，但啟用的專案有 {aEnabled.Count} 個 ⇒ 不替你挑；到「路徑管理」頁填全域那幾格";
            return;
        }
        SenateProject aP = aEnabled[0];
        string? aData = Take(aP, "agentCommandsRoot");
        string? aGlo = Take(aP, "glossaryRoot");
        string? aComic = Take(aP, "comicRoot");
        foreach (SenateProject p in Projects) foreach (string k in s_LegacyProjectPathKeys) Take(p, k);
        // 舊的 "auto" 是「從 Unity 專案推」—— 新架構沒有那條路 ⇒ 不搬 auto，留空讓人明說
        if (aData != null && !string.Equals(aData.Trim(), "auto", StringComparison.OrdinalIgnoreCase)) Paths.AgentCommandsRoot = aData;
        if (aGlo != null) Paths.GlossaryRoot = aGlo;
        if (aComic != null) Paths.ComicRoot = aComic;
        PathsMigrationNote = $"舊檔的資料根／詞典根／漫畫庫根住在專案「{aP.Name}」上 ⇒ 已搬進全域 `paths`（下次存檔落盤）";
    }

    public void Save(string iPath)
    {
        string? aDir = Path.GetDirectoryName(iPath);
        if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
        File.WriteAllText(iPath, JsonSerializer.Serialize(this, s_Json) + "\n");
    }

    /// <summary>逐條檢查，回傳人可讀的問題清單（空 ＝ 沒問題）。⚠ 只驗**設定本身**，不碰磁碟。</summary>
    public List<string> Validate()
    {
        var aErrors = new List<string>();
        var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < Projects.Count; i++)
        {
            var p = Projects[i];
            string aWho = string.IsNullOrWhiteSpace(p.Name) ? $"projects[{i}]" : p.Name;
            if (string.IsNullOrWhiteSpace(p.Name)) aErrors.Add($"{aWho}: name 空白");
            if (string.IsNullOrWhiteSpace(p.Root)) aErrors.Add($"{aWho}: root 空白");
            else if (!Path.IsPathRooted(p.Root)) aErrors.Add($"{aWho}: root 不是絕對路徑（{p.Root}）");
            if (!string.IsNullOrWhiteSpace(p.Name) && !aSeen.Add(p.Name))
                aErrors.Add($"{aWho}: name 重複 —— 名字是後台與 log 的識別鍵，重複會讓兩個專案的讀數混在一起");
        }
        return aErrors;
    }
}
