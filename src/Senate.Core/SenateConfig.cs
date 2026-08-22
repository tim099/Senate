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
using System.Text.Json;
using System.Text.Json.Serialization;

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
}

/// <summary>senate.local.json 的根物件。</summary>
public sealed class SenateConfig
{
    /// <summary>設定格式版本。讀到未知版本要**擋下並說出來**，不要盡力而為。</summary>
    public int SchemaVersion { get; set; } = 1;

    public List<SenateProject> Projects { get; set; } = new();

    public const int CurrentSchemaVersion = 1;

    static readonly JsonSerializerOptions s_Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
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
