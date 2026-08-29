// 區塊職責：Senate 的本機設定 —— 「這台機器上，這套系統管哪些專案」。
// 物理意義：Senate 是**專案外部**的獨立 repo（不住在任何 Unity 專案裡），
//           所以「要管誰」必須是資料，不能是寫死的路徑。
//           ⇒ 設定檔分兩份，職責不同：
//             · config/senate.local.example.json —— **入版控**的樣板（沒有機器路徑）
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

    /// <summary>
    /// AgentCommands 資料根。<c>"auto"</c> ＝ 照專案慣例推導
    /// （先讀 <c>&lt;Root&gt;/.agentcommands_root.local</c> pointer 檔，沒有則 <c>&lt;Root&gt;/AgentCommands</c>）。
    /// <para>⚠ 推導失敗不會亂猜第二個位置 —— 猜錯的症狀是「寫到另一棵資料樹而且不報錯」。</para>
    /// </summary>
    public string AgentCommandsRoot { get; set; } = "auto";

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
    /// <c>D:/Unity/Bar/AgentCommands/ChatTavern/baton/letters</c>。空 ＝ 還沒設定。
    /// </summary>
    public string LettersRoot { get; set; } = "";

    /// <summary>
    /// session lock 目錄。<c>"auto"</c> ＝ 從 <see cref="LettersRoot"/> 逐層往上找第一個
    /// <c>_session</c>（見 <c>PersonaLetters.ResolveSessionDir</c>）。
    /// <para>⚠ 這一格存在的理由是「信件庫與 lock 不一定同一棵樹」——
    /// 不是為了讓人有第二個地方可以填錯。預設 auto，填了就逐字採用。</para>
    /// </summary>
    public string SessionDir { get; set; } = PersonaLetters.AutoSessionDir;

    /// <summary>本版不認得的欄位（含 <c>"//"</c> 註解鍵）—— 讀進來、寫回去，原樣保留。</summary>
    /// <remarks>⚠ [SCP_Ignore]：不進畫面、不進自動序列化（同 <see cref="SenateProject.Extra"/>）。</remarks>
    [JsonExtensionData]
    [SCP_Ignore]
    public Dictionary<string, JsonElement> Extra { get; set; } = new();
}

/// <summary>senate.local.json 的根物件。</summary>
public sealed class SenateConfig
{
    /// <summary>設定格式版本。讀到未知版本要**擋下並說出來**，不要盡力而為。</summary>
    public int SchemaVersion { get; set; } = 1;

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

    /// <summary>本機設定檔的預設位置：repo 根的 senate.local.json。</summary>
    public static string DefaultPath(string iRepoRoot) => Path.Combine(iRepoRoot, "senate.local.json");

    /// <summary>入版控的樣板位置。</summary>
    public static string ExamplePath(string iRepoRoot)
        => Path.Combine(iRepoRoot, "config", "senate.local.example.json");

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
        return aCfg;
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
