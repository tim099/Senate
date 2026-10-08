// 區塊職責：把 Senate 設定接到 Library 入口 —— 資料根、信件庫根、外部漫畫庫根三格都走同一份路徑 registry。
// 物理意義：外部漫畫庫根是 `SCP_PathId.ComicRoot`（senate.local.json，唯一真相源；TASK-0400）。
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
                                   Root(SCP_PathId.ComicRoot));
    }
}
