using SCP.Core.Bank;

namespace Senate.Cli;

public static partial class SelfTest
{
    // 結清清單換歸屬後，仍須保留逐則去重與讀不動的旗標，避免補款付第二次。
    static CheckRow ReconcileSettledRefsCleanRoom()
    {
        const string aName = "結清清單：缺檔／逐則去重／錯誤形狀與壞 JSON 明示未知（TASK-0464）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_settled_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(aTmp);
            string aFile = Path.Combine(aTmp, SCP_BankReconcile.SettledFileName);
            var aProblems = new List<string>();
            var aRefs = SCP_BankReconcile.ReadSettledRefs(aTmp, aProblems, out bool aUnreadable);
            bool aAbsent = aRefs.Count == 0 && !aUnreadable && aProblems.Count == 0;

            File.WriteAllText(aFile, "{\"settled\":[{\"refs\":[\"tavern#seq=1\",\"tavern#seq=1\",\"\"]},{\"refs\":[\"tavern#seq=2\"]}]}");
            aRefs = SCP_BankReconcile.ReadSettledRefs(aTmp, aProblems, out aUnreadable);
            bool aValid = !aUnreadable && aProblems.Count == 0
                && aRefs.SetEquals(new[] { "tavern#seq=1", "tavern#seq=2" });

            File.WriteAllText(aFile, "{\"settled\":{}}");
            aRefs = SCP_BankReconcile.ReadSettledRefs(aTmp, aProblems, out aUnreadable);
            bool aShape = aUnreadable && aRefs.Count == 0 && aProblems.Count > 0;
            aProblems.Clear();
            File.WriteAllText(aFile, "{broken");
            aRefs = SCP_BankReconcile.ReadSettledRefs(aTmp, aProblems, out aUnreadable);
            bool aBroken = aUnreadable && aRefs.Count == 0 && aProblems.Count > 0;
            return new(aName, $"缺檔={aAbsent}／有效且去重={aValid}／錯誤形狀={aShape}／壞 JSON={aBroken}",
                aAbsent && aValid && aShape && aBroken ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new(aName, e.Message, CheckResult.Fail); }
        finally { if (Directory.Exists(aTmp)) Directory.Delete(aTmp, true); }
    }
}
