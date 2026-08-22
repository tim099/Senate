// 區塊職責：自我對拍 —— 這套東西自己的讀數（不是「應該會動」）。
// 物理意義：SCP_Core 的 JSON 層是共用碼，它的第一個責任是**讀得懂既有資料**：
//           那些 json 是 Unity 端的 UCL JsonData 寫出來的，所以「能不能讀」不是單元測試問題，
//           是拿真檔案去試的問題。⇒ 每一項都印出**讀到什麼**，不是只印 ✓。
// 數值影響：純讀。找不到樣本檔時回報「跳過（沒有樣本）」，**不當成通過** ——
//           「沒測」與「測過而且對」同形是這個 repo 最貴的錯誤形狀。
using Senate.Core;
using SCP.Core.Json;

namespace Senate.Cli;

public enum CheckResult { Pass, Fail, Skipped }

public sealed record CheckRow(string Name, string Reading, CheckResult Result);

public static class SelfTest
{
    public static List<CheckRow> Run(IReadOnlyList<ProjectReading> iProjects)
    {
        var aRows = new List<CheckRow>();
        aRows.Add(MissingSemantics());
        aRows.Add(WriterStability());
        aRows.AddRange(RealFileRoundTrip(iProjects));
        return aRows;
    }

    /// <summary>「不存在」不可以長得像「空值」—— 讀 Missing 必須丟例外。</summary>
    static CheckRow MissingSemantics()
    {
        var aData = SCP_JsonData.Parse("{\"a\":1}");
        bool aThrew = false;
        string aPath = "";
        try { _ = aData["b"].AsString(); }
        catch (SCP_JsonMissingException e) { aThrew = true; aPath = e.Message; }

        bool aFallbackOk = aData.GetString("b", "預設值") == "預設值";
        bool aExistsOk = !aData["b"].Exists && aData["a"].Exists;

        return new CheckRow(
            "Missing 語意",
            aThrew
                ? $"讀不存在的 key 會丟例外（訊息帶路徑）／fallback={aFallbackOk}／Exists 判定={aExistsOk}"
                : "⚠ 讀不存在的 key **沒有**丟例外",
            aThrew && aFallbackOk && aExistsOk ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>同樣的資料輸出兩次必須逐字相同，且 key 照插入順序（不然 diff 會滿江紅）。</summary>
    static CheckRow WriterStability()
    {
        var aObj = SCP_JsonData.NewObject();
        aObj.Set("zebra", "斑馬");
        aObj.Set("apple", 42);
        aObj.Set("中文鍵", true);
        string a1 = aObj.ToJson();
        string a2 = SCP_JsonData.Parse(a1).ToJson();
        bool aOrderKept = a1.IndexOf("zebra", StringComparison.Ordinal) < a1.IndexOf("apple", StringComparison.Ordinal);
        bool aCjkRaw = a1.Contains("中文鍵", StringComparison.Ordinal);

        return new CheckRow(
            "輸出穩定性",
            $"round-trip 逐字相同={a1 == a2}／插入順序保留={aOrderKept}／中文不轉義={aCjkRaw}",
            a1 == a2 && aOrderKept && aCjkRaw ? CheckResult.Pass : CheckResult.Fail);
    }

    /// <summary>
    /// 拿**真的、由 Unity 端 UCL JsonData 寫出來的檔**過一遍：讀 → 寫 → 再讀，
    /// 兩次的樹必須等價（逐 key 比較），而且第一次就要讀得到預期的欄位。
    /// </summary>
    static IEnumerable<CheckRow> RealFileRoundTrip(IReadOnlyList<ProjectReading> iProjects)
    {
        bool aAny = false;
        foreach (var p in iProjects)
        {
            if (p.State != ProbeState.Ok || p.AgentCommandsRoot == null) continue;
            string aFile = Path.Combine(p.AgentCommandsRoot, "commands_schema.json");
            if (!File.Exists(aFile)) continue;
            aAny = true;

            string aText = File.ReadAllText(aFile);
            SCP_JsonData? aRoot = null;
            string aParseError = "";
            // yield 不能寫在 catch 裡（CS1631）⇒ 先把結果收進變數，離開 try 之後才 yield
            try { aRoot = SCP_JsonData.Parse(aText); }
            catch (SCP_JsonParseException e) { aParseError = e.Message; }
            if (aRoot == null)
            {
                yield return new CheckRow($"讀真檔（{p.Name}）", $"解析失敗：{aParseError}", CheckResult.Fail);
                continue;
            }

            int aCmdCount = aRoot["commands"].Count;
            string aGen = aRoot.GetString("generator", "(沒有這個欄位)");
            string aOut = aRoot.ToJson();
            SCP_JsonData aAgain = SCP_JsonData.Parse(aOut);
            bool aSame = Equivalent(aRoot, aAgain);

            yield return new CheckRow(
                $"讀真檔（{p.Name}）",
                $"{Path.GetFileName(aFile)}：{aText.Length} 字元／commands={aCmdCount}／generator={aGen}／"
                + $"寫回再讀等價={aSame}",
                aSame && aCmdCount > 0 ? CheckResult.Pass : CheckResult.Fail);
        }

        if (!aAny)
            yield return new CheckRow("讀真檔",
                "找不到樣本（沒有可用專案或該專案沒有 commands_schema.json）—— **這是跳過，不是通過**",
                CheckResult.Skipped);
    }

    /// <summary>兩棵樹等價比較（型別 → 結構 → 原文）。</summary>
    static bool Equivalent(SCP_JsonData iA, SCP_JsonData iB)
    {
        if (iA.Type != iB.Type) return false;
        switch (iA.Type)
        {
            case SCP_JsonType.Object:
                if (iA.Count != iB.Count) return false;
                for (int i = 0; i < iA.Keys.Count; i++)
                {
                    string k = iA.Keys[i];
                    if (iB.Keys[i] != k) return false;          // 順序也要一樣（diff 穩定性）
                    if (!Equivalent(iA[k], iB[k])) return false;
                }
                return true;
            case SCP_JsonType.Array:
                if (iA.Count != iB.Count) return false;
                for (int i = 0; i < iA.Count; i++)
                    if (!Equivalent(iA[i], iB[i])) return false;
                return true;
            case SCP_JsonType.Null:
                return true;
            default:
                return iA.ToJson(false) == iB.ToJson(false);
        }
    }
}
