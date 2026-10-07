// 委託建立報酬只走既有銀行Server；固定ref與idem_key讓未知回執可安全重試。
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Paths;

namespace Senate.Core;

public interface ISculptureRewardGateway
{
    SCP_CanvasGateResult Credit(string account, string persona, string reference, string description);
}

public static class SenateSculptureRewards
{
    public static Func<SCP_DataRoot, ISculptureRewardGateway> Factory { get; set; } = data => new BankGateway(data);
    sealed class BankGateway(SCP_DataRoot data) : ISculptureRewardGateway
    {
        public SCP_CanvasGateResult Credit(string account, string persona, string reference, string description)
        {
            var result = SCP_CmdRegistry.Dispatch("bank", new Dictionary<string, string>
            {
                ["op"] = "credit", ["bank_root"] = Path.Combine(data.Value, SCP_PathRegistry.Get(SCP_PathId.BankRoot).DeriveSuffix),
                ["account"] = account, ["persona"] = persona, ["amount"] = "10", ["kind"] = "sculpture_commission",
                ["ref"] = reference, ["idem_key"] = reference, ["caller"] = persona, ["description"] = description,
            });
            return result.ExitCode == 0 ? SCP_CanvasGateResult.Good("委託報酬10 token已入帳")
                : SCP_CanvasGateResult.Bad("銀行尚無成功回執（exit " + result.ExitCode + "）；用相同ID重試建立，沿用冪等鍵");
        }
    }
}
