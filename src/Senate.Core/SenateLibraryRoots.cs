// 區塊職責：把 Senate 設定接到 Library 入口 —— 資料根、信件庫根、外部漫畫庫根三格都走同一份路徑 registry。
// 物理意義：外部漫畫庫根是 `SCP_PathId.ComicRoot`（senate.local.json，唯一真相源；TASK-0400）。
//          舊的 `<Unity 專案根>/.comic_root.local` 快照（Unity 閱讀心得管理頁寫的）**不再被讀來當值**，
//          只在「本格空白、而快照有值」時用來把話說清楚。
// 數值影響：唯讀；不存在或無法解析的設定交回入口阻擋，不尋找替代資料樹。
using SCP.Core.Library;
using SCP.Core.Paths;

namespace Senate.Core;

public static class SenateLibraryRoots
{
    /// <summary>以同一份設定解析三個根；漫畫庫的錯誤不阻斷其他閱讀操作。</summary>
    public static SCP_LibraryRoots Resolve(SenateConfig? iConfig)
    {
        if (iConfig == null)
        {
            var aMissing = new SCP_PathResolution("", "設定檔", "尚未設定 Senate");
            return new SCP_LibraryRoots(aMissing, aMissing, aMissing);
        }
        SCP_PathResolution Root(SCP_PathId iId)
            => SCP_PathRegistry.Resolve(iId, id => SenatePathBinding.StoredOf(iConfig, id));
        return new SCP_LibraryRoots(Root(SCP_PathId.AgentCommandsRoot), Root(SCP_PathId.LettersRoot),
                                   ComicRoot(Root(SCP_PathId.ComicRoot), Root(SCP_PathId.UnityProjectRoot)));
    }

    /// <summary>
    /// 漫畫庫根：本格有值就用本格；空白時**不採用**舊快照，但把「快照有值」說出來。
    /// ⚠ 不說的話，「我在 Unity 頁設了路徑」與「Senate 沒收到」在畫面上同形（都是一句『沒設定』）。
    /// </summary>
    static SCP_PathResolution ComicRoot(SCP_PathResolution iComic, SCP_PathResolution iProject)
    {
        if (iComic.Error == null) return iComic;
        // 取不到（例：兩個啟用專案）或其他錯誤：照原樣交回，⛔ 不用快照遮掉。
        if (iComic.Origin != "未設定" || iProject.Error != null) return iComic;

        string aSnapshot = Path.Combine(iProject.Value, SCP_LibraryComics.ComicRootSnapshotFileName);
        string aLegacy = ReadLegacySnapshot(aSnapshot);
        string aHint = aLegacy.Length == 0
            ? "尚未設定外部漫畫庫 —— 到 `senate ui` 的路徑管理頁填「外部漫畫庫根」，或不需要外部漫畫就忽略。"
            : $"本格空白，但舊快照 `{aSnapshot}` 有值 `{aLegacy}`（Senate 不再讀它）—— "
              + "到路徑管理頁把這個值填進「外部漫畫庫根」。";
        return new SCP_PathResolution("", iComic.Origin, aHint);
    }

    static string ReadLegacySnapshot(string iPath)
    {
        try
        {
            if (!File.Exists(iPath)) return "";
            foreach (string aLine in File.ReadAllLines(iPath))
                if (aLine.Trim().StartsWith("comic_root=", StringComparison.Ordinal))
                    return aLine.Trim().Substring("comic_root=".Length).Trim();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }
}
