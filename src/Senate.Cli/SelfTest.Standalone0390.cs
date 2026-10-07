// 回歸測試（TASK-0390）：Senate＋Valhalla 在**沒有任何 Unity 專案**時照常解出路徑；舊檔的路徑一次搬進全域 `paths`。只碰臨時資料樹。
using System.Text.Json;
using SCP.Core.Cmd;
using Senate.Core;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow StandaloneWithoutUnityProject0390()
    {
        const string aName = "沒有 Unity 專案也照常運作：資料根唯一入口／本地 Cmd 殼／舊檔路徑搬家（TASK-0390）";
        string aRoot = Path.Combine(Path.GetTempPath(), "senate_0390_" + Guid.NewGuid().ToString("N")[..8]);
        var aSavedProvider = UnityDelegateCmd.ConfigProvider;
        var aFails = new List<string>();
        try
        {
            string aData = Path.Combine(aRoot, "Valhalla").Replace('\\', '/');
            Directory.CreateDirectory(aData);
            string aCfgPath = Path.Combine(aRoot, "senate.local.json");

            // ── ① 沒有任何專案：資料根照樣解得出來 ──
            var aCfg = new SenateConfig();
            aCfg.Paths.AgentCommandsRoot = aData;
            aCfg.Save(aCfgPath);
            SenateConfig aLoaded = SenateConfig.Load(aCfgPath)!;
            UnityDelegateCmd.ConfigProvider = () => (aLoaded, aCfgPath);

            string? aResolved = SenatePathBinding.ResolveDataRoot(aLoaded, out string? aErr);
            if (aResolved != aData) aFails.Add($"沒有專案時資料根應解成 {aData}（得 {aResolved ?? "null"}：{aErr}）");

            bool aHostOk = new SenateLocalCmdHost().TryResolve("", "", out SCP_LocalTarget aT, out string aHostErr, out _);
            if (!aHostOk || aT.DataRoot != aData)
                aFails.Add($"本地 Cmd 殼（早安／晚安／發文）沒有專案時應照常解出資料根（{(aHostOk ? aT.DataRoot : aHostErr)}）");
            if (aHostOk && aT.ProjectRoot != SenatePathBinding.HostRepoRoot)
                aFails.Add($"本地 Cmd 殼的顯示基準應是 Senate 專案根（得 {aT.ProjectRoot}）");

            // 🔴 反向：真的要派給 Unity 的那幾支，沒有專案 ⇒ 明說沒有目標，⛔ 不能假裝成功
            UnityTargetResolution aUnity = UnityTargetResolver.Resolve(aLoaded, aCfgPath, null, ProjectArgSpelling.CmdArg);
            if (aUnity.Ok) aFails.Add("沒有 Unity 專案時 Unity 派工不該解得出目標");

            // 🔴 反向：指名一個不是設定那組的資料根 ⇒ 擋（資料根只有一組）
            if (new SenateLocalCmdHost().TryResolve("", Path.Combine(aRoot, "other"), out _, out _, out _))
                aFails.Add("指名第二棵資料根應被擋下");

            // ── ② 舊檔（路徑住在專案上）⇒ Load 搬進 `paths`，專案上的舊鍵拿掉 ──
            string aLegacyPath = Path.Combine(aRoot, "legacy.json");
            File.WriteAllText(aLegacyPath, "{\"schemaVersion\":1,\"projects\":[{\"name\":\"Bar\",\"root\":\"D:/x/Bar\","
                + "\"agentCommandsRoot\":\"" + aData + "\",\"glossaryRoot\":\"auto\",\"comicRoot\":\"D:/comic\",\"enabled\":true}]}");
            SenateConfig aLegacy = SenateConfig.Load(aLegacyPath)!;
            if (aLegacy.Paths.AgentCommandsRoot != aData || aLegacy.Paths.ComicRoot != "D:/comic")
                aFails.Add($"舊檔的路徑應搬進 paths（資料根 {aLegacy.Paths.AgentCommandsRoot}／漫畫 {aLegacy.Paths.ComicRoot}）");
            if (aLegacy.PathsMigrationNote.Length == 0) aFails.Add("搬家時應留說明");
            aLegacy.Save(aLegacyPath);
            string aSaved = File.ReadAllText(aLegacyPath);
            using (JsonDocument aDoc = JsonDocument.Parse(aSaved))
            {
                JsonElement aProj = aDoc.RootElement.GetProperty("projects")[0];
                if (aProj.TryGetProperty("agentCommandsRoot", out _)) aFails.Add("存回去後專案上不該再有 agentCommandsRoot（兩個真相源）");
                if (!aDoc.RootElement.TryGetProperty("paths", out _)) aFails.Add("存回去後應有 paths 區塊");
            }

            // 🔴 反向：舊的 "auto" 是「從 Unity 專案推」—— 不搬（新架構沒有那條路），留空讓人明說
            File.WriteAllText(aLegacyPath, "{\"schemaVersion\":1,\"projects\":[{\"name\":\"Bar\",\"root\":\"D:/x/Bar\",\"agentCommandsRoot\":\"auto\",\"enabled\":true}]}");
            SenateConfig aAuto = SenateConfig.Load(aLegacyPath)!;
            if (aAuto.Paths.AgentCommandsRoot.Length != 0) aFails.Add($"舊的 auto 不該被搬成資料根（得 {aAuto.Paths.AgentCommandsRoot}）");
            if (SenatePathBinding.ResolveDataRoot(aAuto, out _) != null) aFails.Add("資料根沒設時應解不出來（⛔ 不從專案推）");

            return new CheckRow(aName, aFails.Count == 0
                ? "無專案⇒資料根／本地殼照常、顯示基準＝Senate 專案根；Unity 派工明說沒有目標；第二棵資料根擋下；舊檔搬進 paths 且不留雙份；舊 auto 不搬"
                : string.Join("／", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, $"例外：{e.GetType().Name}: {e.Message}", CheckResult.Fail); }
        finally
        {
            UnityDelegateCmd.ConfigProvider = aSavedProvider;
            try { Directory.Delete(aRoot, true); } catch (Exception) { }
        }
    }
}
