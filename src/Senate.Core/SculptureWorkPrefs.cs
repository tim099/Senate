// 區塊職責：個人雕刻作品「每軸最多幾格」的政策上限（TASK-0479）—— 讀、寫、決議。
// 物理意義：Tim 2026-10-10：「256 可以廢除嗎」「如果寫死的話，希望改成參數化」。
//           起因是 0.1 公尺／voxel 的風帆戰艦（TASK-0478）：256 格只有 25.6 公尺，連龍骨都放不下。
//           ⚠ 這一格只管**建立與調整尺寸**；讀取、雕刻、渲染只認 SCP_Core 的結構上限（SCP_SculptWorks.MaxAxisHard）
//             ⇒ 把設定調小，既有的大作品照樣打得開、雕得動，只是不能再建新的大作品。
// 數值影響：只讀寫 `senate.pages.local.json` 的 `sculpture.workMaxAxis` 一格（由 SenatePageStore 決定檔名）；
//           預設 ＝ SCP_SculptWorks.DefaultMaxAxis（4096）；超出 1..結構上限的值夾回範圍，並在來源說出來。
//           另外兩格 `sculpture.lastSpace`／`sculpture.lastWork`：觀測頁上次選的空間與作品（Tim 2026-10-12：下次進來自動切回去）；
//           `sculpture.preview`：觀測頁的預覽設定（檢視倍率範圍、畫面大小）整份 JSON（Tim 2026-10-12：重開視窗也保留）。
#nullable enable
using SCP.Core.Prefs;
using SCP.Core.Sculpture;

namespace Senate.Core;

public static class SculptureWorkPrefs
{
    /// <summary>個人作品每軸上限（格）。預設 4096。</summary>
    public static readonly SCP_PrefKey<long> WorkMaxAxis = SCP_PrefKey.Long("sculpture", "workMaxAxis", SCP_SculptWorks.DefaultMaxAxis);

    /// <summary>
    /// 決議上限，<paramref name="oSource"/> 說出是哪一層給的。
    /// <para>沒有 repo 根（例：淨室直接呼叫 Cmd、沒經過宿主補參數）⇒ 預設，並明說「宿主沒給 repo_root」——
    /// 「沒設過」與「讀不到設定檔」不同形。</para>
    /// </summary>
    public static int ResolveMaxAxis(string iRepoRoot, out string oSource)
    {
        long aVal;
        if (string.IsNullOrWhiteSpace(iRepoRoot))
        {
            aVal = WorkMaxAxis.Default;
            oSource = "預設（宿主沒給 repo_root，讀不到設定）";
        }
        else
        {
            SCP_PrefRead<long> aRead = SenatePageStore.For(iRepoRoot).Read(WorkMaxAxis);
            switch (aRead.State)
            {
                case SCP_PrefState.Present: aVal = aRead.Value; oSource = "設定 " + WorkMaxAxis.Path; break;
                case SCP_PrefState.Missing: aVal = WorkMaxAxis.Default; oSource = "預設（沒設過 " + WorkMaxAxis.Path + "）"; break;
                default:
                    aVal = WorkMaxAxis.Default;
                    oSource = "預設（⚠ 設定讀不了：" + (aRead.Error ?? "沒有說明") + "）";
                    break;
            }
        }
        if (aVal < 1 || aVal > SCP_SculptWorks.MaxAxisHard)
        {
            long aClamped = Math.Max(1, Math.Min(SCP_SculptWorks.MaxAxisHard, aVal));
            oSource += "（⚠ 值 " + aVal + " 超出 1.." + SCP_SculptWorks.MaxAxisHard + " ⇒ 用 " + aClamped + "）";
            aVal = aClamped;
        }
        return (int)aVal;
    }

    /// <summary>觀測頁上次選的空間（shared｜work）與作品 id。</summary>
    public static readonly SCP_PrefKey<string> LastSpace = SCP_PrefKey.String("sculpture", "lastSpace");
    public static readonly SCP_PrefKey<string> LastWork = SCP_PrefKey.String("sculpture", "lastWork");

    /// <summary>
    /// 讀上次的選取。沒設過 ⇒ 空字串；讀不了 ⇒ 空字串並在 <paramref name="oError"/> 說出來
    /// （⛔ 不讓壞檔擋住開頁 —— 最壞是退回預設挑選，而那是這個功能出現之前的行為）。
    /// </summary>
    public static (string Space, string Work) ReadLastSelection(string iRepoRoot, out string? oError)
    {
        oError = null;
        if (string.IsNullOrWhiteSpace(iRepoRoot)) return ("", "");
        var aStore = SenatePageStore.For(iRepoRoot);
        string One(SCP_PrefKey<string> iKey, ref string? ioErr)
        {
            SCP_PrefRead<string> r = aStore.Read(iKey);
            if (r.State == SCP_PrefState.Present) return r.Value ?? "";
            if (r.State != SCP_PrefState.Missing) ioErr ??= iKey.Path + " 讀不了：" + (r.Error ?? "沒有說明");
            return "";
        }
        string aSpace = One(LastSpace, ref oError), aWork = One(LastWork, ref oError);
        return (aSpace, aWork);
    }

    /// <summary>記下這一次的選取。<paramref name="iWork"/> 為 null ⇒ 只記空間（換到共用展區時不洗掉上次的作品）。</summary>
    public static (bool Ok, string Message) SaveLastSelection(string iRepoRoot, string iSpace, string? iWork)
    {
        if (string.IsNullOrWhiteSpace(iRepoRoot)) return (false, "宿主沒給 repo_root");
        var aStore = SenatePageStore.For(iRepoRoot);
        var r = aStore.Write(LastSpace, iSpace);
        if (!r.Ok || iWork == null) return r;
        return aStore.Write(LastWork, iWork);
    }

    /// <summary>觀測頁預覽設定（SculptPreviewSettings）整份 JSON —— 欄位由頁面那一側的類別決定，這裡只存字串。</summary>
    public static readonly SCP_PrefKey<string> PreviewJson = SCP_PrefKey.String("sculpture", "preview");

    /// <summary>讀預覽設定 JSON。沒設過 ⇒ 空字串；讀不了 ⇒ 空字串並在 <paramref name="oError"/> 說出來（照預設開頁）。</summary>
    public static string ReadPreviewJson(string iRepoRoot, out string? oError)
    {
        oError = null;
        if (string.IsNullOrWhiteSpace(iRepoRoot)) return "";
        SCP_PrefRead<string> r = SenatePageStore.For(iRepoRoot).Read(PreviewJson);
        if (r.State == SCP_PrefState.Present) return r.Value ?? "";
        if (r.State != SCP_PrefState.Missing) oError = PreviewJson.Path + " 讀不了：" + (r.Error ?? "沒有說明");
        return "";
    }

    public static (bool Ok, string Message) SavePreviewJson(string iRepoRoot, string iJson)
        => string.IsNullOrWhiteSpace(iRepoRoot) ? (false, "宿主沒給 repo_root") : SenatePageStore.For(iRepoRoot).Write(PreviewJson, iJson);

    public static (bool Ok, string Message) SaveMaxAxis(string iRepoRoot, int iMaxAxis)
    {
        if (iMaxAxis < 1 || iMaxAxis > SCP_SculptWorks.MaxAxisHard)
            return (false, "上限要在 1.." + SCP_SculptWorks.MaxAxisHard + " 之間");
        return SenatePageStore.For(iRepoRoot).Write(WorkMaxAxis, iMaxAxis);
    }
}
