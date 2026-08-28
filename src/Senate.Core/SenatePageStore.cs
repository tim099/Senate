// 區塊職責：**頁面設定的持久化層** —— senate.pages.local.json（repo 根、不入版控）。
// 物理意義：頁面的操作狀態原本只住驅動端 session（build/ui_session.json）——
//           CLI 跨呼叫記得住，但**視窗每次開都是新 session** ⇒「我上次調好的範圍」全部蒸發，
//           而蒸發之後的畫面跟「我沒調過」一模一樣（Tim 2026-08-28 點名的坑）。
//           ⇒ 值得留的設定要有一個**顯式儲存**的落點；本類別是那一格。
// 數值影響：一個檔、頁面各佔一個頂層 key。讀寫走 SCP_Core 自帶的 JSON
//           （SCP_JsonParser / SCP_JsonMapper / SCP_JsonWriter，Tim 2026-08-28 指定）——
//           不用 System.Text.Json 是刻意的：頁面設定型別已經在 SCP_TypeSchema 的方言內
//           （inspector 畫得出 ⇒ mapper 存得進，同一套分類器），不引第二套序列化語意。
// ⚠ 與 senate.local.json 的分界：那份是「這台機器管哪些專案」（senate init 的主體、有樣板）；
//   本檔是「各頁面上次調成什麼樣」。混進去的話，頁面每存一次就動一次主設定檔的 diff，
//   而人開 diff 想看的是專案清單有沒有變，不是誰又存了一次畫面狀態。
using SCP.Core.Json;

namespace Senate.Core;

public static class SenatePageStore
{
    public static string DefaultPath(string iRepoRoot) => Path.Combine(iRepoRoot, "senate.pages.local.json");

    /// <summary>
    /// 讀某一頁存過的設定。回 null ＝ **沒存過**（檔不在／沒這頁的區塊）——那不是錯誤；
    /// 檔在但壞掉 ⇒ 也回 null 但一定經過 <paramref name="iWarn"/> 說出來（兩態不得同形）。
    /// </summary>
    public static T? Load<T>(string iRepoRoot, string iPageKey, Action<string>? iWarn = null) where T : class
    {
        string aPath = DefaultPath(iRepoRoot);
        if (!File.Exists(aPath)) return null;
        SCP_JsonData aRoot;
        try { aRoot = SCP_JsonParser.Parse(File.ReadAllText(aPath, System.Text.Encoding.UTF8)); }
        catch (Exception e)
        {
            iWarn?.Invoke($"{Path.GetFileName(aPath)} 讀不了（沒有被覆寫）：{e.Message}");
            return null;
        }
        SCP_JsonData? aSection = aRoot.Contains(iPageKey) ? aRoot[iPageKey] : null;
        if (aSection == null) return null;
        try { return SCP_JsonMapper.Create(typeof(T), aSection) as T; }
        catch (Exception e)
        {
            iWarn?.Invoke($"'{iPageKey}' 區塊對不上 {typeof(T).Name}（沒有被覆寫）：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把某一頁的設定寫回去 —— **只動自己那個 key，其他頁的區塊原樣保留**
    /// （讀整份 → 換一格 → atomic replace）。回傳（成功, 人可讀的說法），失敗一定有話說。
    /// </summary>
    public static (bool ok, string message) Save(string iRepoRoot, string iPageKey, object iSettings)
    {
        string aPath = DefaultPath(iRepoRoot);
        SCP_JsonData aRoot = SCP_JsonData.NewObject();
        if (File.Exists(aPath))
        {
            try { aRoot = SCP_JsonParser.Parse(File.ReadAllText(aPath, System.Text.Encoding.UTF8)); }
            catch (Exception e)
            {
                // 整份壞掉時不硬寫 —— 蓋掉會把**別頁**存的東西一起帶走，而那不是本次要救的錯。
                return (false, $"{Path.GetFileName(aPath)} 壞了，沒有覆寫（先修它或刪掉重存）：{e.Message}");
            }
        }
        aRoot[iPageKey] = SCP_JsonMapper.ToJson(iSettings);

        string aTmp = aPath + $".tmp{Environment.ProcessId}";
        try
        {
            File.WriteAllText(aTmp, aRoot.ToJson(iIndented: true) + "\n", new System.Text.UTF8Encoding(false));
            File.Move(aTmp, aPath, overwrite: true);
        }
        catch (Exception e)
        {
            try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { /* 殘檔清不掉不蓋真錯 */ }
            return (false, $"寫檔失敗：{e.GetType().Name}: {e.Message}");
        }

        // 回讀驗證 —— 寫入端會替自己說謊（round-trip 一次，section 在就算數；逐欄對拍歸 selftest）。
        try
        {
            SCP_JsonData aBack = SCP_JsonParser.Parse(File.ReadAllText(aPath, System.Text.Encoding.UTF8));
            if (!aBack.Contains(iPageKey))
                return (false, $"寫進去了但回讀找不到 '{iPageKey}' 區塊 —— 有第二個寫入者？");
        }
        catch (Exception e) { return (false, $"寫完之後回讀失敗（檔案可能壞了）：{e.Message}"); }
        return (true, $"✓ 已存進 {Path.GetFileName(aPath)}（'{iPageKey}' 區塊）");
    }
}
