// 區塊職責：自我對拍 —— 這套東西自己的讀數（不是「應該會動」）。
// 物理意義：SCP_Core 的 JSON 層是共用碼，它的第一個責任是**讀得懂既有資料**：
//           那些 json 是 Unity 端的 UCL JsonData 寫出來的，所以「能不能讀」不是單元測試問題，
//           是拿真檔案去試的問題。⇒ 每一項都印出**讀到什麼**，不是只印 ✓。
// 數值影響：純讀。找不到樣本檔時回報「跳過（沒有樣本）」，**不當成通過** ——
//           「沒測」與「測過而且對」同形是這個 repo 最貴的錯誤形狀。
using Senate.Core;
using SCP.Core.Gui;
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
        aRows.Add(ConfigRoundTripKeepsUnknownKeys());
        aRows.Add(StyleRoundTrip());
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
    /// 設定檔 round-trip **不可以吃掉本版不認得的欄位**（含使用者手寫的 <c>"//"</c> 註解鍵）。
    /// <para>🩸 這一項是為一隻真的 bug 立的：2026-08-23 介面尺寸寫回設定檔的第一版，
    /// 把 <c>"//"</c> 那行整條吃掉 —— projects 還在，所以看起來一切正常。</para>
    /// </summary>
    static CheckRow ConfigRoundTripKeepsUnknownKeys()
    {
        string aPath = Path.Combine(Path.GetTempPath(), "senate_selftest_config.json");
        const string aSrc = """
            {
              "//": "手寫註解，不可以被吃掉",
              "schemaVersion": 1,
              "projects": [ { "name": "X", "root": "D:/X", "//p": "專案層註解" } ],
              "未來版本的欄位": 42
            }
            """;
        try
        {
            File.WriteAllText(aPath, aSrc);
            SenateConfig? aCfg = SenateConfig.Load(aPath);
            if (aCfg == null) return new CheckRow("設定檔 round-trip", "讀不到剛寫出的暫存檔", CheckResult.Fail);

            aCfg.Ui.Scale = 1.75f;          // 模擬「使用者改了尺寸」那條寫入路徑
            aCfg.Save(aPath);
            string aBack = File.ReadAllText(aPath);

            bool aRootNote = aBack.Contains("手寫註解", StringComparison.Ordinal);
            bool aProjNote = aBack.Contains("專案層註解", StringComparison.Ordinal);
            bool aFuture = aBack.Contains("未來版本的欄位", StringComparison.Ordinal);
            bool aUi = SenateConfig.Load(aPath)?.Ui.Scale == 1.75f;

            return new CheckRow("設定檔 round-trip",
                $"根層註解保留={aRootNote}／專案層註解保留={aProjNote}／未知欄位保留={aFuture}／ui.scale 回讀={aUi}",
                aRootNote && aProjNote && aFuture && aUi ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow("設定檔 round-trip", $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally { try { File.Delete(aPath); } catch { /* 暫存檔刪不掉不影響判定 */ } }
    }

    /// <summary>顯示參數的 round-trip：存進 JSON 再讀回來要是同一份，且缺欄位用預設（不是 0）。</summary>
    static CheckRow StyleRoundTrip()
    {
        var aStyle = new SCP_GuiStyle();
        aStyle.SetScale(1.5f);
        aStyle.TextWidth = 120;
        SCP_GuiStyle aBack = SCP_GuiStyle.FromJson(SCP_JsonData.Parse(aStyle.ToJson().ToJson()));
        bool aSame = Math.Abs(aBack.Scale - 1.5f) < 0.001f && aBack.TextWidth == 120;

        // 空物件 ⇒ 預設值（「沒設過」不可以變成 0）
        SCP_GuiStyle aEmpty = SCP_GuiStyle.FromJson(SCP_JsonData.Parse("{}"));
        bool aDefault = Math.Abs(aEmpty.Scale - SCP_GuiStyle.DefaultScale) < 0.001f && aEmpty.TextWidth >= 40;

        // 超出範圍要被夾，不可以照收（NaN／0 會讓每個尺寸都變 0 而版位不報錯）
        var aClamp = new SCP_GuiStyle();
        aClamp.SetScale(99f);
        bool aClamped = Math.Abs(aClamp.Scale - SCP_GuiStyle.MaxScale) < 0.001f;

        return new CheckRow("顯示參數 round-trip",
            $"存讀一致={aSame}／缺欄位用預設={aDefault}（{aEmpty.Scale:0.##}）／超範圍夾住={aClamped}",
            aSame && aDefault && aClamped ? CheckResult.Pass : CheckResult.Fail);
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
