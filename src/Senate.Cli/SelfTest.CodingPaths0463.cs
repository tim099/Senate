// 區塊職責：施工路徑退場閘的隔離測試（TASK-0463）。
// 物理意義：實跑暫存 .NET 工程與 session；混合路徑與同工程去重用假 runner 量，不另起編譯。
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
            string aNet = Path.Combine(aTmp, "net project"), aUnknown = Path.Combine(aTmp, "unknown");
            Directory.CreateDirectory(aNet);
            Directory.CreateDirectory(aUnknown);
            string aSource = Path.Combine(aNet, "Probe.cs"), aProject = Path.Combine(aNet, "Probe.csproj");
            File.WriteAllText(aProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.WriteAllText(aSource, "public class Probe { }");
            SCP_CodingExitRequest Req(string iScope) => new(Path.Combine(aTmp, "unrelated data"), iScope, DateTime.UtcNow.ToString("O"));
            Check(SenateCodingExitGate.FindTarget(aSource).Input == aProject, "未找到 .NET 所屬工程");
            var aNoProject = SenateCodingExitGate.Run(Req(aUnknown));
            Check(!aNoProject.Green && aNoProject.Summary.Contains("找不到 .NET 工程"), "未知工程放行／沒明說找不到工程");
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

            int aNetCalls = 0;
            SenateCodingExitGate.CommandReading Run(string iExe, IReadOnlyList<string> iArgs, string iDir)
            {
                Check(iExe == "dotnet" && iDir == aNet, "跑了 dotnet 以外的建置／指錯工程：" + iExe + " @ " + iDir);
                aNetCalls++;
                return new(0, "build green", "");
            }
            var aMixed = SenateCodingExitGate.Run(Req(aSource + "|" + aUnknown), Run);
            Check(!aMixed.Green && aMixed.Summary.Contains(aSource + " → dotnet") && aMixed.Summary.Contains("通過；exit 0")
                && aMixed.Summary.Contains(aUnknown + "：未驗"), "混合路徑未分別回報 .NET 綠／找不到工程紅");
            aNetCalls = 0;
            var aRepeated = SenateCodingExitGate.Run(Req(aSource + "|" + aNet), Run);
            Check(aRepeated.Green && aNetCalls == 1 && aRepeated.Summary.Split('\n').Length == 2, "相同工程重跑／路徑結果合併");
        }
        catch (Exception e) { aFails.Add(e.GetType().Name + ": " + e.Message); }
        finally
        {
            SCP_CodingExitGateHost.Gate = aOldGate;
            if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true);
        }
        return new("施工路徑編譯閘（TASK-0463）", aFails.Count == 0
            ? "真 .NET 錯誤擋退場且 session 留著，修好放行；找不到工程明說並擋下；混合路徑各自回報；同工程只跑一次、只跑 dotnet"
            : string.Join("／", aFails), aFails.Count == 0 ? CheckResult.Pass : CheckResult.Fail);
    }
}
