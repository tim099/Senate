// 區塊職責：`server-ping` —— 第一支走 Server 的 Cmd，**探針**：回 Server 的 pid／build／thread，原樣回 echo。
// 物理意義：TASK-0103 的驗收工具。它不做任何事，所以「它通了」只證明**協議與執行器**通了：
//           CLI 寫 queue → Server 接手 → 跑 → result 檔 → CLI 讀回。這正是 seq／ledger（0106）
//           搬進來之前要先驗的那條路。⚠ 沒有它，執行器的讀數只能靠「等 0106 做完再看」。
// 數值影響：零 IO（result 檔由執行器寫）。
using SCP.Core.Cmd;

namespace Senate.Core;

public sealed class Cmd_ServerPing : ServerDelegateCmd
{
    public override string Name => "server-ping";
    public override string Summary => "Server 探針：回 Server 的 pid／build／thread（驗執行器通不通）—— 由 Senate Server 執行";
    public override string PortNote => "探針本身就是終局；它的用途是驗 0103 的協議，不會被原生化";
    public override string Example => SCP_CmdRegistry.Invoke("server-ping --arg echo=hi");

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("echo", "原樣回傳的字（驗參數有沒有穿過協議）", iDefault: ""),
                new SCP_CmdArgSpec("persona", "走哪條分道；空 ＝ 公用分道 `server`", iDefault: ""),
                // 探針要能故意壞：驗「Server 端失敗 → 錯誤報告 → CLI 指路」那條路（TASK-0104），不然那條路只有 selftest 的合成樣本。
                new SCP_CmdArgSpec("fail", "故意失敗：fail＝回 exit 1；throw＝丟例外（exit 70）", iDefault: "", iChoices: new[] { "", "fail", "throw" }),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aEcho = iArgs.Get("echo");
        string aFail = iArgs.Get("fail");
        if (aFail == "fail") return SCP_CmdResult.Fail(1, "✗ 探針被要求失敗（fail=fail）—— 這一行就是「哪一格不成立」").AddValue("echo", aEcho);
        if (aFail == "throw") throw new InvalidOperationException("探針被要求丟例外（fail=throw）—— stack 應該出現在錯誤報告裡");
        var aResult = SCP_CmdResult.Success(
            $"pong　thread={Environment.CurrentManagedThreadId}　utc={DateTime.UtcNow:O}",
            aEcho.Length > 0 ? $"echo：{aEcho}" : "echo：（空）");
        aResult.AddValue("echo", aEcho);
        aResult.AddValue("server_thread", Environment.CurrentManagedThreadId.ToString());
        return aResult;
    }
}
