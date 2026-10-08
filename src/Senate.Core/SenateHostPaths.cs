// 區塊職責：**宿主宣告路徑入口的唯一一處**（TASK-0390，Tim 2026-10-07：路徑要有統一入口，不准各處自行推導）。
// 物理意義：SCP_Core 的共用層不推導任何根；它留插座，由宿主一次裝上。Senate 有兩個 process 入口 ——
//           CLI（`senate.exe`，Program.Main）與獨立 Server（`senate-server.exe`，ServerBootstrap.Run）——
//           兩邊都只呼叫本檔的 Install。
//   🩸 2026-10-07 量到：這些插座原本只在 Program.Main 裝 ⇒ autostart 優先起的 `senate-server.exe` 裡，
//     信件根退回慣例值、詞典根 auto 解不出來（「宿主沒有宣告」）、要設定檔的委派 Cmd 回 70 —— 兩邊各跑各的，
//     而路徑調整時只有 CLI 那半會跟上。
// 數值影響：只設靜態插座，不碰磁碟；設定檔一律在**用到時現讀**（後台改了路徑，常駐 process 下一次呼叫就跟上）。
using SCP.Core.Paths;

namespace Senate.Core;

public static class SenateHostPaths
{
    /// <summary>
    /// repo 根：從執行檔往上找第一個含 `.git` 的目錄；找不到就用當前目錄。
    /// ⚠ 只找 `.git`，**不猜第二個判準** —— CLI 與 Server 共用這一支（兩邊不一致時 Server 會服務另一棵樹）。
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

    /// <summary>裝上所有「路徑從哪來」的宿主插座。CLI 與 Server 的入口各呼叫一次。</summary>
    public static void Install(string iRepoRoot)
    {
        string aRepo = iRepoRoot.Replace('\\', '/').TrimEnd('/');

        // Senate 專案根（描述表的 Host 格）：詞典 `auto` 等「跟著 Senate 走」的路徑從這裡推。
        SenatePathBinding.HostRepoRoot = aRepo;

        // 設定檔來源：委派型 Cmd 與各個解析器都從這裡拿 senate.local.json（⛔ 不由下層自己找）。
        SenateConfigSource.Provider = () =>
        {
            string aCfgPath = SenateConfig.DefaultPath(iRepoRoot);
            return (SenateConfig.Load(aCfgPath), aCfgPath);
        };

        // 信件根：凡是從資料根推信件根的地方（SCP_DataPaths.Letters）都改問設定那一格 ——
        //   ⛔ 沒裝的話，只拿到資料根的那半邊程式會寫慣例那棵，而讀設定的那半邊寫另一棵。
        SenatePathBinding.InstallLettersResolver(iRepoRoot);

        // 文件根（TASK-0337）：Senate 的 Cmd ⇒ `Docs/`，SCP_Core 的 ⇒ `SCP_Core/Docs~/`；錨在 exe 所在的 repo，⛔ 不用 cwd。
        SCP.Core.Docs.SCP_DocStore.RootsProvider = () => new[]
        {
            new SCP.Core.Docs.SCP_DocRoot("senate", Path.Combine(iRepoRoot, "Docs")),
            new SCP.Core.Docs.SCP_DocRoot("scp_core", Path.Combine(iRepoRoot, "SCP_Core", "Docs~")),
        };

        // 工作記憶 related_docs 的具名根：沒前綴＝資料根；`senate:`／`scp_core:` 錨在 exe 所在的 repo。
        //   ⛔ 沒有 `ucl_core:` —— 那是 Unity 專案裡的檔，Senate 不讀。
        SCP.Core.WorkMemory.SCP_WorkMemory.HostNamedRoots = () => new Dictionary<string, string>
        {
            ["senate"] = aRepo,
            ["scp_core"] = aRepo + "/SCP_Core",
        };

        // 閱讀線三個根（資料根／信件根／漫畫庫根）走同一份描述表。
        SCP.Core.Cmd.SCP_Cmd_Library.RootsProvider = () =>
            SenateLibraryRoots.Resolve(SenateConfig.Load(SenateConfig.DefaultPath(iRepoRoot)));

        // skill（TASK-0406）：源是 SCP_Core 的 Skills~，安裝對象固定是 Senate 自己。
        SCP.Core.Cmd.SCP_Cmd_Skill.RootsProvider = () =>
            new SCP.Core.Cmd.SCP_SkillRoots(Path.Combine(iRepoRoot, "SCP_Core", "Skills~"), iRepoRoot);

        // 本地 Cmd 殼（早安／晚安／酒館發文）要的宿主能力：資料根、詞典根、環境標記。
        SCP.Core.Cmd.SCP_LocalRootsCmd.Host = new SenateLocalCmdHost();
    }
}
