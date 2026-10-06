// 區塊職責：`bank-audit` 借別區綁定的自我對拍（TASK-0440）。
// 物理意義：收付走**本區**帳本，而新銀行「沒開戶的帳號不能收付」⇒ 借來的帳號本區沒開戶＝錢收不進來。
//           舊版把所有借用都算「不是錯」，於是 erina 在 BTC 借 Florin 的 `cc`（本區沒有這個戶）時健檢照樣不計。
//           ① 借來的帳號本區沒開戶 ⇒ borrowed_unopened 計入、exit 5
//           ② 反向對照：借來的帳號本區有開戶（大小寫不同也算）⇒ 仍只是 borrowed、exit 0
// 數值影響：temp 目錄造信件根與資料根，跑完刪。⛔ 不碰真實資料樹。
#nullable enable
using SCP.Core.Cmd;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow BankAuditBorrowedUnopened()
    {
        const string aName = "bank-audit 借別區綁定：借來的帳號本區沒開戶 ⇒ borrowed_unopened 計入問題（exit 5）／本區有開戶 ⇒ 仍只是 borrowed（exit 0）（淨室，TASK-0440）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_bankaudit_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string aLetters = Path.Combine(aTmp, "letters");
            string aData = Path.Combine(aTmp, "data");
            string aAccounts = Path.Combine(aData, "Bank", "accounts");
            Directory.CreateDirectory(aAccounts);
            File.WriteAllText(Path.Combine(aAccounts, "spectre.json"), "{}");
            void Persona(string iName, string iFlorinAccount)
            {
                Directory.CreateDirectory(Path.Combine(aLetters, iName, "profile"));
                Directory.CreateDirectory(Path.Combine(aLetters, iName, "bank"));
                File.WriteAllText(Path.Combine(aLetters, iName, "bank", "Florin.md"), iFlorinAccount);
            }
            Persona("probe-unopened", "cc");
            Persona("probe-opened", "Spectre");

            SCP_CmdResult Audit() =>
                SCP_CmdRegistry.Dispatch("bank-audit", new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["letters_root"] = aLetters, ["data_root"] = aData, ["region"] = "BTC" });
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";

            // ① 一位借來的帳號本區沒開戶
            var r1 = Audit();
            string aText1 = string.Join("\n", r1.Lines);
            bool aHit = r1.ExitCode == 5 && V(r1, "borrowed") == "2" && V(r1, "borrowed_unopened") == "1"
                        && aText1.Contains("probe-unopened → `cc`") && !aText1.Contains("probe-opened → `Spectre`");

            // ② 反向對照：只剩有開戶的那位
            Directory.Delete(Path.Combine(aLetters, "probe-unopened"), true);
            var r2 = Audit();
            bool aControl = r2.ExitCode == 0 && V(r2, "borrowed") == "1" && V(r2, "borrowed_unopened") == "0";

            return new CheckRow(aName,
                $"本區沒開戶 exit {r1.ExitCode}（期望 5）borrowed={V(r1, "borrowed")} borrowed_unopened={V(r1, "borrowed_unopened")}（期望 2／1）={aHit}"
                + $"／本區有開戶 exit {r2.ExitCode}（期望 0）borrowed_unopened={V(r2, "borrowed_unopened")}={aControl}",
                aHit && aControl ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally
        {
            try { Directory.Delete(aTmp, true); } catch (Exception) { }
        }
    }
}
