// 區塊職責：`senate.local.json` 的設定來源 —— Cmd 讀設定的唯一入口。
// 物理意義：Cmd 不知道 repo 根在哪，而本層**不推導**；宿主在啟動時把來源裝上（SenateHostPaths）。
// 數值影響：每次呼叫由宿主重讀設定檔（後台改完設定，下一支 Cmd 立刻吃到）。
namespace Senate.Core;

public static class SenateConfigSource
{
    /// <summary>
    /// 設定來源。**由宿主在啟動時裝上**（跟 <see cref="SCP.Core.Cmd.SCP_CmdRegistry.InvocationHint"/> 同形）。
    /// <para>⚠ 沒裝上時一律 fail loud，不准 fallback 到某個猜的路徑：
    /// 猜中的那次會讓人以為它本來就會找；猜錯的那次會讀到另一棵資料樹上。</para>
    /// </summary>
    public static Func<(SenateConfig? Config, string Path)>? Provider;
}
