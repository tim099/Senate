// 區塊職責：安裝系統的**相依清單**（TASK-0375）—— 每個可安裝項目是什麼、怎麼量、怎麼裝、需要誰。
// 物理意義：清單是資料不是程式（`SenateData/config/install_catalog.json`，入版控）。
//           ⭐ 「新增一個項目只改清單、不改程式」是這張單的第一條驗收 ——
//             Unity 那邊的 `media_admin.py` 有清單（PLUGINS）但安裝動作逐項寫死在 op_plugin 裡，
//             於是加一項要改兩處，而只改一處的症狀是「清單上有、按了沒反應」。
//           ⇒ 這裡把「怎麼裝」也收進清單：種類（kind）只有兩種，各自一份通用實作（見 InstallRunner）。
// 數值影響：純讀。載入時逐條驗證（重複 id／requires 指到不存在的項目／成環），壞了就**整份不收**並說為什麼。
//
// ⚠ 為什麼壞一筆就整份不收：只丟掉壞的那筆的話，症狀是「某個項目從清單上消失」，
//   而那跟「本來就沒有這一項」同形 —— 依賴它的 skill 會被回「不認得的項目」，不是「清單壞了」。
using System.Text.Json;

namespace Senate.Core;

/// <summary>項目的種類 —— 決定「怎麼量、怎麼裝、怎麼拆」走哪一份實作。</summary>
public enum InstallKind
{
    /// <summary>Python 套件：裝進選定的那一份 Python（pip）。</summary>
    Pip,
    /// <summary>Hugging Face 模型：下載到模型位置（HF_HOME/hub）。</summary>
    HfModel,
}

/// <summary>清單上的一個可安裝項目（欄位意思見 Docs/Workflows/Install.md）。</summary>
public sealed class InstallItem
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Desc { get; init; } = "";
    public InstallKind Kind { get; init; }

    /// <summary>需要先裝好的其他項目 id（安裝時會一起排進計畫；解除安裝時用來判斷「還有誰在用」）。</summary>
    public IReadOnlyList<string> Requires { get; init; } = Array.Empty<string>();

    /// <summary>pip：要量版本的 distribution 名（`importlib.metadata.version` 用）。</summary>
    public IReadOnlyList<string> Dists { get; init; } = Array.Empty<string>();

    /// <summary>pip：要在**新的子程序**裡實際 import 的模組名（import 得進來才算裝好）。</summary>
    public IReadOnlyList<string> Imports { get; init; } = Array.Empty<string>();

    /// <summary>pip：import 之後再算一個 Python 運算式，結果要是真（例：torch.cuda.is_available()）。空 ＝ 不檢查。</summary>
    public string Check { get; init; } = "";

    /// <summary>pip：<see cref="Check"/> 不成立時印給人看的一句話。</summary>
    public string CheckHint { get; init; } = "";

    /// <summary>pip：`pip install` 後面接的參數（套件名＋必要的 index-url 等）。</summary>
    public IReadOnlyList<string> PipArgs { get; init; } = Array.Empty<string>();

    /// <summary>hf_model：Hugging Face repo id（例：BAAI/bge-m3）。</summary>
    public string Repo { get; init; } = "";

    /// <summary>hf_model：snapshot 裡一定要有的檔（全部在才算裝好）。</summary>
    public IReadOnlyList<string> RequiredFiles { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 共用的地基（torch、huggingface_hub）。⚠ 這個旗標**本身不擋解除安裝** ——
    /// 擋的是「還有已安裝的項目 requires 它」；它只讓畫面多提醒一句。
    /// </summary>
    public bool Shared { get; init; }

    /// <summary>估計大小（MB，給人看的；實際大小以磁碟為準）。</summary>
    public int SizeMb { get; init; }

    /// <summary>從哪裡下載（給人看的；問使用者要不要裝時要照實說）。</summary>
    public string Source { get; init; } = "";
}

/// <summary>一份已驗證的清單。</summary>
public sealed class InstallCatalog
{
    public const string FileName = "install_catalog.json";
    public const int CurrentSchemaVersion = 1;

    public string Path { get; }
    public IReadOnlyList<InstallItem> Items { get; }

    InstallCatalog(string iPath, List<InstallItem> iItems) { Path = iPath; Items = iItems; }

    /// <summary>清單的預設位置（跟 senate.local.json 同一個資料夾，但這一份入版控）。</summary>
    public static string DefaultPath(string iRepoRoot) => System.IO.Path.Combine(SenatePaths.ConfigDir(iRepoRoot), FileName);

    public InstallItem? Find(string iId)
    {
        foreach (InstallItem i in Items) if (string.Equals(i.Id, iId, StringComparison.OrdinalIgnoreCase)) return i;
        return null;
    }

    /// <summary>讀＋驗證。回 null ＝ 讀不了或不合法（原因在 <paramref name="oError"/>，⛔ 不回半份清單）。</summary>
    public static InstallCatalog? Load(string iPath, out string? oError)
    {
        oError = null;
        if (!File.Exists(iPath)) { oError = $"找不到相依清單：{iPath}"; return null; }
        JsonDocument aDoc;
        try
        {
            aDoc = JsonDocument.Parse(File.ReadAllText(iPath),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e) { oError = $"相依清單不是合法 JSON：{iPath}\n{e.Message}"; return null; }

        using (aDoc)
        {
            JsonElement aRoot = aDoc.RootElement;
            int aVer = aRoot.TryGetProperty("schemaVersion", out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (aVer != CurrentSchemaVersion)
            { oError = $"相依清單 schemaVersion={aVer}，本版只認得 {CurrentSchemaVersion}：{iPath}"; return null; }
            if (!aRoot.TryGetProperty("items", out JsonElement aArr) || aArr.ValueKind != JsonValueKind.Array)
            { oError = $"相依清單缺 items 陣列：{iPath}"; return null; }

            var aItems = new List<InstallItem>();
            var aProblems = new List<string>();
            int n = 0;
            foreach (JsonElement e in aArr.EnumerateArray())
            {
                n++;
                InstallItem? aItem = ParseItem(e, n, aProblems);
                if (aItem != null) aItems.Add(aItem);
            }
            aProblems.AddRange(Validate(aItems));
            if (aProblems.Count > 0)
            {
                oError = $"相依清單有 {aProblems.Count} 個問題（整份不收）：{iPath}\n  - " + string.Join("\n  - ", aProblems);
                return null;
            }
            return new InstallCatalog(iPath, aItems);
        }
    }

    static InstallItem? ParseItem(JsonElement e, int iOrdinal, List<string> oProblems)
    {
        string Str(string k) => e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "";
        List<string> Arr(string k)
        {
            var a = new List<string>();
            if (e.TryGetProperty(k, out JsonElement x) && x.ValueKind == JsonValueKind.Array)
                foreach (JsonElement s in x.EnumerateArray()) if (s.ValueKind == JsonValueKind.String) a.Add(s.GetString() ?? "");
            return a;
        }

        string aId = Str("id");
        string aWho = aId.Length > 0 ? aId : $"items[{iOrdinal - 1}]";
        if (aId.Length == 0) { oProblems.Add($"{aWho}：缺 id"); return null; }
        string aKindRaw = Str("kind");
        InstallKind aKind;
        if (aKindRaw == "pip") aKind = InstallKind.Pip;
        else if (aKindRaw == "hf_model") aKind = InstallKind.HfModel;
        else { oProblems.Add($"{aWho}：kind='{aKindRaw}' 不認得（只有 pip／hf_model）"); return null; }

        var aItem = new InstallItem
        {
            Id = aId,
            Name = Str("name").Length > 0 ? Str("name") : aId,
            Desc = Str("desc"),
            Kind = aKind,
            Requires = Arr("requires"),
            Dists = Arr("dists"),
            Imports = Arr("imports"),
            Check = Str("check"),
            CheckHint = Str("checkHint"),
            PipArgs = Arr("pipArgs"),
            Repo = Str("repo"),
            RequiredFiles = Arr("requiredFiles"),
            Shared = e.TryGetProperty("shared", out JsonElement sh) && sh.ValueKind == JsonValueKind.True,
            SizeMb = e.TryGetProperty("sizeMb", out JsonElement sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt32() : 0,
            Source = Str("source"),
        };

        // 每種 kind 少了哪一格就**量不到或裝不了** —— 這裡擋，不要等到按下安裝才發現。
        if (aKind == InstallKind.Pip)
        {
            if (aItem.Dists.Count == 0) oProblems.Add($"{aWho}：pip 項目要有 dists（量版本、解除安裝都靠它）");
            if (aItem.Imports.Count == 0) oProblems.Add($"{aWho}：pip 項目要有 imports（import 得進來才算裝好）");
            if (aItem.PipArgs.Count == 0) oProblems.Add($"{aWho}：pip 項目要有 pipArgs（不然不知道要裝什麼）");
        }
        else
        {
            if (aItem.Repo.Split('/').Length != 2) oProblems.Add($"{aWho}：hf_model 的 repo 要是 `組織/名稱`（現在是 '{aItem.Repo}'）");
            if (aItem.RequiredFiles.Count == 0) oProblems.Add($"{aWho}：hf_model 要有 requiredFiles（不然「下載到一半」會被判成裝好）");
        }
        return aItem;
    }

    /// <summary>清單層的檢查：重複 id、requires 指到不存在的項目、相依成環。</summary>
    public static List<string> Validate(IReadOnlyList<InstallItem> iItems)
    {
        var aProblems = new List<string>();
        var aById = new Dictionary<string, InstallItem>(StringComparer.OrdinalIgnoreCase);
        foreach (InstallItem i in iItems)
            if (!aById.TryAdd(i.Id, i)) aProblems.Add($"{i.Id}：id 重複");
        foreach (InstallItem i in iItems)
            foreach (string r in i.Requires)
                if (!aById.ContainsKey(r)) aProblems.Add($"{i.Id}：requires 的 '{r}' 不在清單上");
        foreach (InstallItem i in iItems)
            if (HasCycle(i.Id, aById, new HashSet<string>(StringComparer.OrdinalIgnoreCase)))
                aProblems.Add($"{i.Id}：requires 成環");
        return aProblems;
    }

    static bool HasCycle(string iId, Dictionary<string, InstallItem> iById, HashSet<string> ioPath)
    {
        if (!ioPath.Add(iId)) return true;
        if (iById.TryGetValue(iId, out InstallItem? aItem))
            foreach (string r in aItem.Requires)
                if (HasCycle(r, iById, ioPath)) return true;
        ioPath.Remove(iId);
        return false;
    }
}
