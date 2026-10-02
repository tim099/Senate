// 區塊職責：把 Senate 設定與 Editor 輸出的漫畫庫快照接到 Library 入口。
// 物理意義：資料根與信件庫根使用共同路徑 registry；漫畫庫讀唯一啟用專案的快照。
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
                                   ComicRoot(Root(SCP_PathId.ProjectRoot)));
    }

    static SCP_PathResolution ComicRoot(SCP_PathResolution iProject)
    {
        if (iProject.Error != null) return iProject;
        string aSnapshot = Path.Combine(iProject.Value, SCP_LibraryComics.ComicRootSnapshotFileName);
        try
        {
            if (File.Exists(aSnapshot))
                foreach (string aLine in File.ReadAllLines(aSnapshot))
                    if (aLine.Trim().StartsWith("comic_root=", StringComparison.Ordinal))
                        return new SCP_PathResolution(aLine.Trim().Substring("comic_root=".Length).Trim(),
                                                      aSnapshot, null);
            return new SCP_PathResolution("", aSnapshot,
                "請在閱讀心得管理頁設定外部漫畫庫並輸出專案快照");
        }
        catch (IOException e) { return new SCP_PathResolution("", aSnapshot, e.Message); }
        catch (UnauthorizedAccessException e) { return new SCP_PathResolution("", aSnapshot, e.Message); }
    }
}
