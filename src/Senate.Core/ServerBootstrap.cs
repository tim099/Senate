// 區塊職責：常駐 Server **最小可用的宿主前置** —— 讓「跑 Server」這件事不必先變成一顆完整的 CLI。
// 物理意義：TASK-0209 A1。`senate.exe` 為了後台頁而參照 `Senate.Desktop` ⇒ 拖著
//           Silk.NET／ImGui／GLFW（`publish/` 實測 78MB，含 `cimgui.dll`＋`glfw3.dll`）。
//           而常駐那顆**一格 UI 都不畫**：它只要檔案協議、registry、Cmd registry。
//           ⇒ 把前置抽成這一支，`Senate.Server` 那顆 exe 的 Program 就只有幾行，
//             而 `senate server start` 照舊呼叫同一支 —— **兩個入口、一份前置**。
// 數值影響：不碰 queue、不碰 ledger；只建目錄、設 registry 路徑、掛 RepoRootProvider。
//
// ⚠ 這裡**刻意只做 Server 需要的那幾格**，不是 Program.cs 的複製：
//   資料搬遷、GUI 宿主能力、Unity 委派設定、coding 退場閘……那些是 CLI 的事。
//   🩸 抄一份完整前置過來的失效樣子是**兩份會漂**，而漂掉時兩邊都不報錯 ——
//     症狀是「同一個動作在 CLI 跑跟在 Server 跑，結果不一樣」。
//   ⇒ 少即是可維護：Server 需要什麼就加什麼，而每加一格都要說得出「Server 為什麼需要它」。
using SCP.Core.Cmd;
using SCP.Core.Proc;

namespace Senate.Core;

public static class ServerBootstrap
{
    /// <summary>
    /// 從 <paramref name="iStartDir"/> 往上找 `.git` 當 repo 根；找不到就用目前工作目錄。
    /// <para>⚠ 與 CLI 同一條規則（`Program.RepoRoot()`）——兩邊不一致的話，
    /// Server 會服務**另一棵樹**的 queue，而兩邊的輸出看起來都正常。</para>
    /// </summary>
    public static string FindRepoRoot(string? iStartDir = null)
    {
        var aDir = new DirectoryInfo(iStartDir ?? AppContext.BaseDirectory);
        while (aDir != null)
        {
            if (Directory.Exists(Path.Combine(aDir.FullName, ".git"))) return aDir.FullName;
            aDir = aDir.Parent;
        }
        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// 裝上 Server 需要的宿主能力，然後前景常駐直到停止。回傳 exit code。
    /// </summary>
    public static int Run(string iRepoRoot, Action<string> iOut, Action<string> iErr)
    {
        SenatePaths.EnsureDirectories(iRepoRoot);

        // ⚠ 沒 Configure ＝ 整個 registry 服務停用 ⇒ `RunForeground` 會拒絕啟動
        //   （沒登記的常駐是沒人管得到的孤兒）。掛在最前面，理由同 Program.cs。
        SCP_ProcessRegistry.Configure(SenatePaths.ProcessRegistry(iRepoRoot));
        SCP_ProcessRegistry.Warn = iMessage => iErr($"⚠ {iMessage}");

        // 錯誤訊息要教人打**這個宿主上真的存在**的指令。
        // ⚠ Server 端跑出來的 Cmd 訊息會被 CLI 原樣轉給人看 ⇒ 動詞要跟 CLI 那邊同一個。
        SCP_CmdRegistry.InvocationHint = "senate cmd";

        // 委派型 Cmd 要知道 Server 根在哪。Server 自己也可能執行到委派型 Cmd（它就是被派的那一端），
        // 沒裝的症狀是 exit 70「宿主沒裝上」——⛔ 不會靜默猜一個根。
        ServerDelegateCmd.RepoRootProvider = () => iRepoRoot;

        return ServerHost.RunForeground(iRepoRoot, iOut, iErr);
    }
}
