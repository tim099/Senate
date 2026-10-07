// 區塊職責：到新區的第一次早安補銀行綁定＋bank-audit ③ 計入的自我對拍（TASK-0441）。
// 物理意義：Tim 2026-10-07 拍板：本區沒綁 ⇒ 看本區有沒有別區那個帳號 —— 有 ⇒ 直接綁；沒有 ⇒ 開戶（種子 1000）再綁。
//           ① 借來的帳號本區沒開戶 ⇒ 開戶、餘額 1000、本區綁定寫成那個 id
//           ② 借來的帳號本區已開戶 ⇒ 只綁，**不再發種子**（餘額不動、帳戶檔不多）
//           ③ 本區已有綁定 ⇒ 零寫入（常態不出聲）
//           ④ 多區都有綁定 ⇒ 不挑、不寫
//           ⑤ 重跑 ① ⇒ 種子不會發第二次
//           ⑥ bank-audit ③：本區綁定指向沒開戶的帳號 ⇒ unmaterialized 計入、exit 5；🔴 反向：開了戶 ⇒ 0、exit 0
// 數值影響：temp 目錄造信件根與資料根，跑完刪。銀行用 ServerContext.InServer 就地跑本體（⛔ 不碰線上 Server、不碰真實資料樹）。
#nullable enable
using SCP.Core.Bank;
using SCP.Core.Cmd;
using SCP.Core.Letters;

namespace Senate.Cli;

public static partial class SelfTest
{
    static CheckRow BankArrivalOpensAndBinds()
    {
        const string aName = "到新區早安補綁定：本區沒那個戶 ⇒ 開戶 1000＋綁／有 ⇒ 只綁不發種子／已綁・多區不動／重跑不重發；bank-audit ③ 沒開戶計入（淨室，TASK-0441）";
        string aTmp = Path.Combine(Path.GetTempPath(), "senate_arrival_" + Guid.NewGuid().ToString("N")[..8]);
        bool aWas = Senate.Core.ServerContext.InServer;
        string aWasId = Senate.Core.ServerContext.ServerId;
        try
        {
            string aLetters = Path.Combine(aTmp, "letters").Replace('\\', '/');
            string aData = Path.Combine(aTmp, "data").Replace('\\', '/');
            Directory.CreateDirectory(aData);
            var aR = new SCP_MorningRoots { DataRoot = aData, LettersRoot = aLetters, ProjectRoot = aTmp };
            string aBankRoot = aR.BankRoot;
            Directory.CreateDirectory(SCP_BankAccounts.AccountsDir(aBankRoot));

            Senate.Core.ServerContext.InServer = true;
            Senate.Core.ServerContext.ServerId = SCP.Core.Proc.SCP_ServerIds.Default;

            SCP_BankAccounts.TryOpen(aBankRoot, "Spectre", "", "selftest", out _, out _);
            void Persona(string iName, params (string Region, string Account)[] iBinds)
            {
                Directory.CreateDirectory(Path.Combine(aLetters, iName, "profile"));
                Directory.CreateDirectory(Path.Combine(aLetters, iName, "bank"));
                foreach (var b in iBinds) File.WriteAllText(Path.Combine(aLetters, iName, "bank", b.Region + ".md"), b.Account + "\n");
            }
            Persona("p-new", ("Florin", "cc"));
            Persona("p-has", ("Florin", "Spectre"));
            Persona("p-own", ("Florin", "cc"), ("BTC", "Spectre"));
            Persona("p-amb", ("Florin", "cc"), ("Ducat", "zeta"));

            string Own(string p) => SCP_PersonaProfile.ReadOwnBankBinding(aLetters, p, "BTC", out _);
            int Bal(string a) => SCP_BankLedger.GetBalance(aBankRoot, a, "tavern_token");
            int Files() => Directory.GetFiles(SCP_BankAccounts.AccountsDir(aBankRoot), "*.json").Length;

            // ① 本區沒有 cc ⇒ 開戶 1000 ＋ 綁
            var l1 = SCP_Morning.EnsureRegionBinding(aR, "BTC", "p-new", "selftest");
            bool a1 = SCP_BankAccounts.CheckUsable(aBankRoot, "cc").Ok && Bal("cc") == SCP_Morning.ArrivalSeed && Own("p-new") == "cc";

            // ② 本區已有 Spectre ⇒ 只綁、不發種子
            int aFilesBefore = Files();
            var l2 = SCP_Morning.EnsureRegionBinding(aR, "BTC", "p-has", "selftest");
            bool a2 = Own("p-has") == "Spectre" && Bal("spectre") == 0 && Files() == aFilesBefore;

            // ③ 已綁 ⇒ 零寫入、不出聲
            var l3 = SCP_Morning.EnsureRegionBinding(aR, "BTC", "p-own", "selftest");
            bool a3 = l3.Count == 0 && Own("p-own") == "Spectre";

            // ④ 多區都有 ⇒ 不挑
            var l4 = SCP_Morning.EnsureRegionBinding(aR, "BTC", "p-amb", "selftest");
            bool a4 = Own("p-amb") == "" && !SCP_BankAccounts.CheckUsable(aBankRoot, "zeta").Ok;

            // ⑤ 重跑 ① ⇒ 已有本區綁定，種子不重發
            var l5 = SCP_Morning.EnsureRegionBinding(aR, "BTC", "p-new", "selftest");
            bool a5 = l5.Count == 0 && Bal("cc") == SCP_Morning.ArrivalSeed;

            // ⑥ bank-audit ③：本區自己的綁定指向沒開戶的帳號
            Persona("p-ghost", ("BTC", "ghost"));
            SCP_CmdResult Audit() =>
                SCP_CmdRegistry.Dispatch("bank-audit", new Dictionary<string, string>(StringComparer.Ordinal)
                    { ["letters_root"] = aLetters, ["data_root"] = aData, ["region"] = "BTC" });
            string V(SCP_CmdResult r, string k) => r.Values.LastOrDefault(kv => kv.Key == k).Value ?? "";
            var r6 = Audit();
            bool a6 = r6.ExitCode == 5 && V(r6, "unmaterialized") == "1" && string.Join("\n", r6.Lines).Contains("p-ghost → `ghost`");
            SCP_BankAccounts.TryOpen(aBankRoot, "ghost", "", "selftest", out _, out _);
            Directory.Delete(Path.Combine(aLetters, "p-amb"), true);   // ④ 那位沒綁定是刻意的，這一格只量 ③
            var r7 = Audit();
            bool a7 = r7.ExitCode == 0 && V(r7, "unmaterialized") == "0";

            bool aOk = a1 && a2 && a3 && a4 && a5 && a6 && a7;
            string aDiag = aOk ? "" : "｜①" + string.Join(" ", l1) + "｜②" + string.Join(" ", l2) + "｜④" + string.Join(" ", l4);
            return new CheckRow(aName,
                $"①開戶 cc 餘額={Bal("cc")} 綁={Own("p-new")}={a1}／②只綁 Spectre、種子沒發={a2}／③已綁零寫入={a3}／④多區不挑={a4}"
                + $"／⑤重跑不重發={a5}／⑥audit ③ 沒開戶 exit {r6.ExitCode} unmat={V(r6, "unmaterialized")}={a6}"
                + $"／🔴開了戶 exit {r7.ExitCode} unmat={V(r7, "unmaterialized")}={a7}" + aDiag,
                aOk ? CheckResult.Pass : CheckResult.Fail);
        }
        catch (Exception e) { return new CheckRow(aName, "例外：" + e.GetType().Name + ": " + e.Message, CheckResult.Fail); }
        finally
        {
            Senate.Core.ServerContext.InServer = aWas;
            Senate.Core.ServerContext.ServerId = aWasId;
            SCP_BankAccountResolver.Invalidate();
            try { Directory.Delete(aTmp, true); } catch (Exception) { }
        }
    }
}
