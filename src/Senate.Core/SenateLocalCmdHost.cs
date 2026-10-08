// 區塊職責：Senate 對 `SCP_LocalRootsCmd` 的宿主實作 —— 早安／晚安／酒館發文那幾支本地 Cmd 要的宿主能力。
// 物理意義：TASK-0406：那些 Cmd 的殼搬進 SCP_Core 之後，只有 Senate 才有的東西留在這裡：
//           資料根（唯一入口 SenatePathBinding.ResolveDataRoot，TASK-0390）／詞典根（PathsPage 那一格）／環境標記／
//           酒館寫入（Server 不在就排隊，TASK-0372）／發文指令的提示（用 `Cmd_TavernPost` 的型別動態組）。
//           （「Editor 在不在＋把觀影場交給 Editor 結算」那一段 TASK-0448 拔掉：晚安關場就地做。）
//           「在哪裡執行」那一句也由這裡說 —— 那是宿主的事實，不是共用層的。
// 數值影響：與搬家前的 `MorningLocalCmd.Execute` 同一套解析；零新增 IO。
using SCP.Core.Cmd;
using SCP.Core.Letters;

namespace Senate.Core;

public sealed class SenateLocalCmdHost : ISCP_LocalCmdHost
{
    public string WhereLine => "⤷ Senate 就地執行";

    public string HostId => "senate";

    public bool TryResolve(string iTargetDataRoot, out SCP_LocalTarget oTarget, out string oError, out string oHint)
    {
        oTarget = new SCP_LocalTarget();
        oError = ""; oHint = "";
        if (SenateConfigSource.Provider == null)
        {
            oError = "宿主沒有裝上設定來源（SenateConfigSource.Provider）—— 程式錯誤，不是用法錯";
            return false;
        }
        (SenateConfig? aConfig, string aConfigPath) = SenateConfigSource.Provider();
        // 資料根：**唯一入口**（TASK-0390）。
        string? aConfigured = SenatePathBinding.ResolveDataRoot(aConfig, out string? aDataErr);
        if (aConfigured == null)
        {
            oError = "資料根解不出來：" + aDataErr;
            oHint = "到 `senate ui` 的「路徑管理」頁設定 AgentCommands 資料根（" + aConfigPath + "）";
            return false;
        }
        string aDataRoot = aConfigured.Replace('\\', '/').TrimEnd('/');
        // 呼叫端指名的資料根（例：畫布分享的 target_data_root）一定要是設定那一組 —— 資料根只有一組，⛔ 不服務第二棵樹
        if (iTargetDataRoot.Length > 0
            && !string.Equals(iTargetDataRoot.Replace('\\', '/').TrimEnd('/'), aDataRoot, StringComparison.OrdinalIgnoreCase))
        {
            oError = $"指名的資料根 `{iTargetDataRoot}` 不是設定的那一組（`{aDataRoot}`）—— 資料根只有一組，不服務第二棵樹";
            oHint = "確認呼叫端帶的 data_root，或到「路徑管理」頁改設定";
            return false;
        }
        oTarget.ProjectName = SCP.Core.Paths.SCP_DataPaths.ProjectNameOf(aDataRoot);
        oTarget.DataRoot = aDataRoot;
        // 這一格只是詞典附註等顯示路徑的基準 ⇒ Senate 專案根（詞典是 Senate 的 submodule）
        oTarget.ProjectRoot = SenatePathBinding.HostRepoRoot;
        oTarget.Describe = $"資料根 {aDataRoot}";
        oTarget.GlossaryRoot = SenatePathBinding.ResolveGlossaryRoot(aConfig, out string? aGlossaryErr);
        oTarget.GlossaryError = aGlossaryErr;
        return true;
    }

    public string DetectEnvMarker() => AgentCmdClient.DetectEnvMarker();

    public SCP_LocalTavernWrite WriteTavern(IReadOnlyDictionary<string, string> iArgs)
    {
        SCP_CmdResult aR = SenateTavernWrite.WriteOrQueue(new Dictionary<string, string>(iArgs, StringComparer.Ordinal));
        return new SCP_LocalTavernWrite { Result = aR, Queued = SenateTavernWrite.IsQueued(aR) };
    }

    public string TavernPostHint(string iPersona)
        => SCP_CmdRegistry.InvokeOf<Cmd_TavernPost>($"--arg persona={iPersona} --arg-file body=<檔>");
}
