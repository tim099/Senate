// 區塊職責：介面顯示偏好的**讀寫端** —— senate.local.json 的 `ui` 區塊 ↔ SCP_GuiStyle。
// 物理意義：共用層（SCP_Core）刻意沒有 IO，所以「存哪裡、存不存得成功」要有一個看得見的地方負責。
//           ⇒ 本類別是那一格：它只做兩件事，而**兩件都會回報結果**。
// 數值影響：
//   · Load：設定檔不存在／沒有 ui 區塊 ⇒ 回 style 預設（那是「沒設過」，不是 0）。
//     設定檔壞了 ⇒ 回預設**並且說出來**（不可靜默降級：那會讓「我設過」與「檔壞了」同形）。
//   · Save：設定檔不存在 ⇒ **不建檔**，回 false 並說要先 init。
//     🩸 為什麼不順手建：那份檔的主體是專案清單，替使用者生一份只有 ui 的設定檔
//        會讓後續的 `senate init` 印「已存在，未覆寫」而不做事 —— 於是「我 init 過了」
//        跟「我的專案清單被一個字級偏好擋掉了」同形。
using SCP.Core.Gui;

namespace Senate.Core;

public static class SenateUiStore
{
    /// <summary>讀出這台機器的顯示偏好。iWarn 收到的是「該讓人看到的話」（呼叫端決定印到哪）。</summary>
    public static SCP_GuiStyle Load(string iRepoRoot, Action<string>? iWarn = null)
    {
        string aPath = SenateConfig.DefaultPath(iRepoRoot);
        try
        {
            SenateConfig? aCfg = SenateConfig.Load(aPath);
            if (aCfg == null) return new SCP_GuiStyle();      // 還沒 init —— 用預設，不是錯誤
            return aCfg.Ui.ToStyle();
        }
        catch (InvalidDataException e)
        {
            iWarn?.Invoke($"設定檔讀不了，介面尺寸用預設（檔案沒有被覆寫）：{e.Message}");
            return new SCP_GuiStyle();
        }
    }

    /// <summary>
    /// 把顯示偏好寫回設定檔。回傳（成功, 人可讀的說法）——
    /// 失敗一定有話說，因為「我按了尺寸但下次開又變回來」是最難查的那一族。
    /// </summary>
    public static (bool ok, string message) Save(string iRepoRoot, SCP_GuiStyle iStyle)
    {
        string aPath = SenateConfig.DefaultPath(iRepoRoot);
        if (!File.Exists(aPath))
            return (false, $"還沒有 {Path.GetFileName(aPath)} —— 先跑 `senate init`，這次的尺寸只在本次有效");

        SenateConfig? aCfg;
        try { aCfg = SenateConfig.Load(aPath); }
        catch (InvalidDataException e) { return (false, $"設定檔壞了，沒有覆寫它：{e.Message}"); }
        if (aCfg == null) return (false, $"設定檔在讀取時消失了：{aPath}");

        aCfg.Ui = SenateUiSettings.FromStyle(iStyle);
        aCfg.Save(aPath);

        // 回讀驗證 —— 寫入類的動作不可以只回報「我寫了」（寫入端會替自己說謊）
        try
        {
            SenateConfig? aBack = SenateConfig.Load(aPath);
            float aGot = aBack?.Ui.Scale ?? float.NaN;
            if (Math.Abs(aGot - iStyle.Scale) > 0.001f)
                return (false, $"寫進去了但回讀是 {aGot:0.##}（期望 {iStyle.Scale:0.##}）—— 有第二個寫入者？");
        }
        catch (InvalidDataException e) { return (false, $"寫完之後回讀失敗：{e.Message}"); }

        return (true, $"已存進 {Path.GetFileName(aPath)}（scale={iStyle.Scale:0.##}，回讀確認）");
    }
}
