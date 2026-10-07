// 區塊職責：Senate 對 `SCP_LocalRootsCmd` 的宿主實作 —— 早安／晚安／酒館發文那幾支本地 Cmd 要的宿主能力。
// 物理意義：TASK-0406：那些 Cmd 的殼搬進 SCP_Core 之後，只有 Senate 才有的東西留在這裡：
//           選專案（`senate.local.json` ＋ UnityTargetResolver）／詞典根（PathsPage 那一格）／環境標記／
//           酒館寫入（Server 不在就排隊，TASK-0372）／發文指令的提示（用 `Cmd_TavernPost` 的型別動態組）。
//           （「Editor 在不在＋把觀影場交給 Editor 結算」那一段 TASK-0448 拔掉：晚安關場就地做。）
//           「在哪裡執行」那一句也由這裡說 —— 那是宿主的事實，不是共用層的。
// 數值影響：與搬家前的 `MorningLocalCmd.Execute` 同一套解析；零新增 IO。
using SCP.Core.Cmd;
using SCP.Core.Letters;

namespace Senate.Core;

public sealed class SenateLocalCmdHost : ISCP_LocalCmdHost
{
    public string WhereLine => "⤷ Senate 就地執行（不需要 Unity Editor）";

    public string HostId => "senate";

    public bool TryResolve(string iProject, string iTargetDataRoot, out SCP_LocalTarget oTarget, out string oError, out string oHint)
    {
        oTarget = new SCP_LocalTarget();
        oError = ""; oHint = "";
        if (UnityDelegateCmd.ConfigProvider == null)
        {
            oError = "宿主沒有裝上設定來源（UnityDelegateCmd.ConfigProvider）—— 程式錯誤，不是用法錯";
            return false;
        }
        (SenateConfig? aConfig, string aConfigPath) = UnityDelegateCmd.ConfigProvider();
        UnityTargetResolution aRes = iTargetDataRoot.Length > 0
            ? UnityTargetResolver.ResolveByDataRoot(aConfig, aConfigPath, iTargetDataRoot, iProject)
            : UnityTargetResolver.Resolve(aConfig, aConfigPath, iProject, ProjectArgSpelling.CmdArg);
        if (!aRes.Ok) { oError = aRes.Error; oHint = aRes.Hint; return false; }
        UnityTarget aWhere = aRes.Target!;
        oTarget.ProjectName = aWhere.ProjectName;
        oTarget.DataRoot = aWhere.DataRoot;
        oTarget.ProjectRoot = aWhere.ProjectRoot;
        oTarget.Describe = aWhere.Describe();
        oTarget.SelectionNote = aWhere.SelectionNote;
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
