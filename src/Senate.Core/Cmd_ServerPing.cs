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
                // 探針要能故意**慢**：收工排乾（TASK-0209 A6）那兩條路 ——「等手上的跑完」與
                // 「排不乾就拒絕退出」—— 只有在真的有 lane 在跑時才存在。
                // 🩸 2026-09-14 掃過全部 Cmd：**沒有一支跑得夠久**（零 Thread.Sleep／Task.Delay，
                //    也沒有 sleep/duration 這類參數）⇒ 那兩格當時是「編譯過了但零讀數」。
                // ⇒ 本參數是**造現場的工具**，不是功能。加在既有探針上而不另開一支 cmd：
                //    同一個理由（驗執行器那條路）不該長出第二個入口。
                new SCP_CmdArgSpec("sleep", "在 Server 端睡幾秒再回（造出「lane 還在跑」的現場，驗收工排乾）"
                                            + $"—— 上限 {MaxSleepSeconds}s，超過就夾住並在輸出說明", iDefault: "0"),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    /// <summary>`sleep` 的上限。夾住而不是照單全收 —— 打錯一個零就是把 Server 的一條 lane 綁住兩小時。</summary>
    public const int MaxSleepSeconds = 120;

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aEcho = iArgs.Get("echo");
        string aFail = iArgs.Get("fail");

        // 睡在 fail 之前：要驗「排乾時它還在跑」就得先真的佔住這條 lane。
        // ⚠ 夾住要**說出來** —— 靜默夾住的話，`sleep=600` 回得飛快會被讀成「排乾沒生效」，
        //   而那個結論跟「功能壞了」長得一模一樣。
        string aSleepNote = "";
        string aSleepRaw = iArgs.Get("sleep");
        if (!string.IsNullOrEmpty(aSleepRaw) && aSleepRaw != "0")
        {
            if (!int.TryParse(aSleepRaw, System.Globalization.NumberStyles.Integer,
                              System.Globalization.CultureInfo.InvariantCulture, out int aSec) || aSec < 0)
                return SCP_CmdResult.Fail(2, $"✗ sleep 讀不出來：'{aSleepRaw}'（要非負整數秒）"
                                             + " —— ⛔ 這不是「沒帶」，是**帶了但解析不出**，不猜。");
            if (aSec > MaxSleepSeconds)
            {
                aSleepNote = $"⚠ sleep={aSec}s 超過上限，**夾成 {MaxSleepSeconds}s**（不是照睡）";
                aSec = MaxSleepSeconds;
            }
            System.Threading.Thread.Sleep(aSec * 1000);
            if (aSleepNote.Length == 0) aSleepNote = $"睡了 {aSec}s（造現場用）";
        }
        if (aFail == "fail") return SCP_CmdResult.Fail(1, "✗ 探針被要求失敗（fail=fail）—— 這一行就是「哪一格不成立」").AddValue("echo", aEcho);
        if (aFail == "throw") throw new InvalidOperationException("探針被要求丟例外（fail=throw）—— stack 應該出現在錯誤報告裡");
        var aResult = SCP_CmdResult.Success(
            $"pong　thread={Environment.CurrentManagedThreadId}　utc={DateTime.UtcNow:O}",
            aEcho.Length > 0 ? $"echo：{aEcho}" : "echo：（空）");
        if (aSleepNote.Length > 0) aResult.Lines.Add(aSleepNote);
        aResult.AddValue("echo", aEcho);
        aResult.AddValue("server_thread", Environment.CurrentManagedThreadId.ToString());
        return aResult;
    }
}
