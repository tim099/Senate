// 區塊職責：`senate cmd wake-audit` —— 早安對帳（全 persona，唯讀），實際由 Unity Editor 執行。
// 物理意義：**委派基底的第一個子類別**，也是它的活體驗收。挑 audit 當第一支的理由只有一個：
//           它是 GoodMorning 四步裡唯一**不寫任何狀態**的一步（不碰 lock / registry / 酒館），
//           所以驗委派管線的時候，管線壞掉的代價是「沒讀到東西」，不是「弄壞了誰的登入」。
// 數值影響：本 process 零寫入；Editor 端寫一份對帳報告到
//           `AgentCommands/AwakenInit/_goodmorning_audit.md`（路徑由回傳檔印出來，不背路徑）。
// ⚠ 這支**不能**變成「早安跑過了」的憑據：它不登入、不推進 wake_count、不寫 lock。
//   少做的功能是選擇，不是遺漏。
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_WakeAudit : UnityDelegateCmd
{
    public override string Name => "wake-audit";

    public override string Summary => "早安對帳（全 persona，唯讀）—— 由 Unity Editor 執行";

    public override string Details =>
        "跑 UCL_Core 那側的 `GoodMorning step=audit`：逐個 persona 比對 registry 與磁碟上的收尾信、\n"
        + "lock 狀態、缺席天數，報告落檔在目標專案的 AgentCommands/AwakenInit/ 底下。\n"
        + "⛔ 這**不是登入**：不寫 lock、不推進 wake_count、不發酒館訊息。";

    public override string PortNote =>
        "整支還在 Editor 那側（UCL_AwakeningService.AuditReport）—— 要原生化得先搬 persona profile 讀取層";

    public override string Example =>
        SCP_CmdRegistry.Invoke("wake-audit --arg persona=basecamp");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                // ⚠ 這裡的 persona **不是「對帳誰」**（audit 一律掃全部），是**走哪一條 queue 分道**。
                //   名字沿用 persona 是因為協議那一層就叫這個；意義差別寫在說明裡，
                //   不改名 —— 改名會讓它跟 run_cmd.py 的 --persona 對不起來。
                new SCP_CmdArgSpec("persona",
                    "走哪一條 queue 分道（＝派這筆的人是誰）。audit 一律掃全部 persona，跟這個值無關"),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override string UnityCmdType => "GoodMorning";

    protected override Dictionary<string, string> BuildUnityArgs(SCP_CmdArgs iArgs)
        => new Dictionary<string, string> { ["step"] = "audit" };
}
