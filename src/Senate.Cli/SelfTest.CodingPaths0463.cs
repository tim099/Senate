// 區塊職責：施工路徑退場閘的隔離測試（TASK-0463）。
// 物理意義：實跑暫存 .NET 工程與 session；Unity 用官方 Pipeline schema 的回應測試，無須開 Editor。
// 數值影響：暫存工程編譯三次；測試結束還原全域閘並刪除自己建立的暫存樹。
using Senate.Core;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow CodingPathsCleanRoom()
    {
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_coding0463_" + Guid.NewGuid().ToString("N"));
        var aFails = new List<string>();
        var aOldGate = SCP_CodingExitGateHost.Gate;
        void Check(bool iOk, string iWhy) { if (!iOk) aFails.Add(iWhy); }
        try
        {
            string aNet = Path.Combine(aTmp, "net project"), aUnity = Path.Combine(aTmp, "unity"), aUnknown = Path.Combine(aTmp, "unknown");
            Directory.CreateDirectory(aNet);
            Directory.CreateDirectory(Path.Combine(aUnity, "ProjectSettings"));
            Directory.CreateDirectory(Path.Combine(aUnity, "Assets", "nested"));
            Directory.CreateDirectory(aUnknown);
            string aSource = Path.Combine(aNet, "Probe.cs"), aProject = Path.Combine(aNet, "Probe.csproj");
            File.WriteAllText(aProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(aSource, "public class Probe { }");
            string aUnitySource = Path.Combine(aUnity, "Assets", "nested", "Probe.cs");
            File.WriteAllText(aUnitySource, "public class Probe { }");
            File.WriteAllText(Path.Combine(aUnity, "Assets", "nested", "generated.csproj"), "not a build target");
            SCP_CodingExitRequest Req(string iScope) => new(Path.Combine(aTmp, "unrelated data"), iScope, DateTime.UtcNow.ToString("O"));
            Check(SenateCodingExitGate.FindTarget(aSource).Input == aProject, "未找到 .NET 所屬工程");
            Check(SenateCodingExitGate.FindTarget(aUnitySource).Root == aUnity, "Unity 誤用產生式 csproj");
            Check(!SenateCodingExitGate.Run(Req(aUnknown)).Green, "未知工程放行");
            Check(!SenateCodingExitGate.Run(Req("")).Green, "未宣告範圍放行");

            // 真編譯錯誤必須保留 session；修掉後，同一場才能退場。
            var aRoot = new SCP_DataRoot(Path.Combine(aTmp, "data"));
            var aSession = new SCP_CodingSession { active = true, persona = "probe", scope = aSource,
                session_id = "coding-probe", start_ts = DateTime.UtcNow.ToString("O"), end_ts = DateTime.UtcNow.AddHours(1).ToString("O") };
            SCP_ActivitySessionStore.Save(aRoot, "probe", aSession, SCP_ActivitySessionKind.Coding);
            SCP_CodingExitGateHost.Gate = r => SenateCodingExitGate.Run(r);
            var aCmd = new SCP_Cmd_Coding();
            SCP_CmdResult End()
            {
                var (aArgs, aErrors) = SCP_CmdArgs.Bind(aCmd.ArgSpecs, new Dictionary<string, string>
                    { ["data_root"] = aRoot.Value, ["op"] = "end", ["persona"] = "probe" });
                if (aArgs == null) throw new InvalidOperationException(string.Join("；", aErrors));
                return aCmd.Execute(aArgs);
            }
            Check(SenateCodingExitGate.Run(Req(aSource)).Green, "正常 .NET 工程未通過");
            File.WriteAllText(aSource, "#error CODING_PATH_PROBE\npublic class Probe { }");
            SCP_CmdResult aRed = End();
            Check(aRed.ExitCode != 0 && string.Join("\n", aRed.Lines).Contains(aSource), "真編譯錯誤沒擋退場／沒指出路徑");
            Check(SCP_ActivitySessionStore.Load(aRoot, "probe")?.active == true, "紅燈關掉 session");
            File.WriteAllText(aSource, "public class Probe { }");
            Check(End().ExitCode == 0 && SCP_ActivitySessionStore.Load(aRoot, "probe")?.active == false, "修好後未放行");

            int aStatusCalls = 0, aNetCalls = 0, aTriggers = 0;
            bool aCompileFail = true, aMissingFlag = false, aUnavailable = false, aTransient = false;
            string aState = "completed";
            SenateCodingExitGate.CommandReading Json(string iResult)
                => new(0, "{\"success\":true,\"errors\":[],\"data\":{\"result\":" + iResult + "}}", "");
            SenateCodingExitGate.CommandReading Run(string iExe, IReadOnlyList<string> iArgs, string iDir)
            {
                if (iExe == "dotnet") { aNetCalls++; return new(0, "build green", ""); }
                Check(iDir == aUnity && iArgs.Contains(aUnity), "Unity CLI 指錯工程");
                if (aUnavailable) return new(6, "{\"success\":false,\"data\":null,\"errors\":[{\"message\":\"No Pipeline instance\"}]}", "");
                string aCommand = iArgs[3];
                if (aCommand == "console_status") return Json("{\"cursor\":42,\"session\":\"probe-session\"}");
                if (aCommand == "recompile") { aTriggers++; return Json("{\"status\":\"compiling\"}"); }
                if (aCommand == "recompile_status")
                {
                    aStatusCalls++;
                    if (aTransient && aStatusCalls == 1) return new(6, "reload", "");
                    return Json("{\"status\":\"" + aState + "\"" + (aMissingFlag ? "" : ",\"compilationFailed\":" + (aCompileFail ? "true" : "false")) + "}");
                }
                Check(iArgs.Contains("42") && iArgs.Contains("probe-session") && iArgs.Contains("0"), "錯誤明細沒有本趟游標／session／不限筆數");
                return Json("{\"cursor\":43,\"entries\":[{\"message\":\"Assets/Probe.cs(1,1): error CS1029: UNITY_PROBE\"}]}");
            }
            var aMixed = SenateCodingExitGate.Run(Req(aSource + "|" + aUnitySource), Run, () => { });
            Check(!aMixed.Green && aMixed.Summary.Contains(aSource + " → dotnet")
                && aMixed.Summary.Contains("通過；exit 0") && aMixed.Summary.Contains("error CS1029"), "只有 Unity 壞時未分別回報 .NET 綠／Unity 紅");
            aCompileFail = false; aState = "up_to_date";
            Check(SenateCodingExitGate.Run(Req(aUnitySource), Run, () => { }).Green, "有效的 up_to_date 判錯");
            aCompileFail = true;
            Check(!SenateCodingExitGate.Run(Req(aUnitySource), Run, () => { }).Green, "up_to_date 蓋掉 compilationFailed=true");
            aMissingFlag = true;
            Check(!SenateCodingExitGate.Run(Req(aUnitySource), Run, () => { }).Green, "缺 compilationFailed 被當 false");
            aMissingFlag = false; aUnavailable = true;
            var aNoEditor = SenateCodingExitGate.Run(Req(aUnitySource), Run, () => { });
            Check(!aNoEditor.Green && aNoEditor.Summary.Contains("No Pipeline instance"), "Editor 沒開放行／原因被蓋掉");
            aUnavailable = false; aTransient = true; aStatusCalls = 0; aTriggers = 0; aCompileFail = false; aState = "completed";
            Check(SenateCodingExitGate.Run(Req(aUnitySource), Run, () => { }).Green && aTriggers == 1 && aStatusCalls == 2, "reload 重送 recompile／未等重連");
            aTransient = false; aState = "compiling";
            Check(!SenateCodingExitGate.Run(Req(aUnitySource), Run, () => { }).Green, "未完成的編譯放行");
            aNetCalls = 0;
            var aRepeated = SenateCodingExitGate.Run(Req(aSource + "|" + aNet), Run, () => { });
            Check(aRepeated.Green && aNetCalls == 1 && aRepeated.Summary.Split('\n').Length == 2, "相同工程重跑／路徑結果合併");
        }
        catch (Exception e) { aFails.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            SCP_CodingExitGateHost.Gate = aOldGate;
            if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true);
        }
        return new("施工路徑編譯閘（TASK-0463）", aFails.Count == 0
            ? "真 .NET 錯誤擋退場且 session 留著，修好放行；混合路徑各自回報；Unity CLI schema／游標／失聯／缺欄位／逾時皆有反向對照（Unity 未跑活體）"
            : string.Join("／", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
