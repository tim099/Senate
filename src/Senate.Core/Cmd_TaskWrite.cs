// 區塊職責：`task-write` —— 任務單的**寫入臨界區**，由 Senate Server 執行（TASK-0349）。
// 物理意義：D20「只有一顆 process 在寫」落到任務單身上就是這一支 —— **任務單唯一的寫入端**。
//           配號（原子建檔）、讀改寫（檔案鎖）、所有閘與狀態機都在 `SCP_TaskOps`／`SCP_TaskStore`（SCP_Core），
//           本檔只做「JSON 參數 → 呼叫 → 把回報與待辦（通知／記憶）交回去」。
//           ⚠ 酒館通知、work_memory.py、Coding 場**不在這裡做**：它們要等別的 process，
//             在持鎖的寫入端裡做會卡住所有人 ⇒ 交回入口 `senate cmd task`（CLI 那一側）寫完之後做。
// 數值影響：一次寫入 ＝ 至多一張單（link 兩張、sweep N 張）一次讀 ＋ 一次寫。
//
// ⚠ 參數**整包以 JSON 傳**（`args_json`），⛔ 不是一個欄位一個 `--arg`（同 `tavern-write` 的 `msg_json`）：
//   13 個 op 的參數聯集有 30 幾格，逐個穿協議的話加一格要改三處（宣告／打包／解包），漏掉的那格
//   **不報錯、只是安靜地沒被讀到**。整包傳 ⇒ 參數宣告只有入口那一份，寫入端照 `SCP_TaskOps.Known` 讀。
// ⚠ 本支是**內部通道**：人與 agent 一律打 `senate cmd task`（驗參數、落回傳檔、發通知）。
using System.Text.Json;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Proc;
using SCP.Core.Tasks;

namespace Senate.Core;

public class Cmd_TaskWrite : ServerDelegateCmd
{
    public override string Name => "task-write";

    public override string Summary =>
        "任務單寫入臨界區（配號＋單檔讀改寫）—— 由 Senate Server 執行；任務單唯一的寫入端（內部通道，人請打 `task`）";

    public override string PortNote =>
        "終局就是這裡：配號與讀改寫**必須**在單一 process 內，所以它不會被原生化回 CLI";

    public override string Details =>
        "⚠ 內部通道：驗參數、回傳檔、酒館通知、工作記憶都在入口 `senate cmd task` —— 直接打本支只會寫單，**不會通知任何人**。\n"
        + "   args_json＝該 op 的參數（JSON 物件，值一律字串），鍵照 `SCP_TaskOps.Known`。";

    public override string Example =>
        SCP_CmdRegistry.Invoke("task-write --arg data_root=<資料根> --arg op=comment --arg persona=Template --arg-file args_json=<檔>");

    /// <summary>
    /// **固定一條 lane <c>task</c>** —— 所有寫入序列化在同一條分道上。
    /// <para>⚠ 判準跟酒館那一格一樣是「前提少」：任務單的寫入量一天幾十筆，per-persona lane 買到的並行收益是 0，
    /// 而它的代價是多依賴一個前提（檔案鎖必須真的擋得住同 process 的兩條執行緒）。
    /// ⛔ 這道 lane **不是**正確性的依靠（那由 <see cref="SCP_TaskStore"/> 的兩把鎖保證）——它降的是鎖競爭。</para>
    /// </summary>
    protected override string Lane(SCP_CmdArgs iArgs) => TaskLane;

    /// <summary>任務寫入唯一那條 lane。⚠ 必須是一層資料夾名（`ServerExecutor.Tick` 掃的是 `queues/*` 這一層）。</summary>
    public const string TaskLane = "task";

    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
    {
        get
        {
            var aSpecs = new List<SCP_CmdArgSpec>
            {
                new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— ⛔ 本層不推導", iRequired: true),
                new SCP_CmdArgSpec("op", "寫入 op", iRequired: true, iChoices: SCP_TaskOps.WriteOps),
                new SCP_CmdArgSpec("persona", "動手的人（時間線與署名用）", iRequired: true),
                new SCP_CmdArgSpec("args_json", "該 op 的參數（JSON 物件，值一律字串）。長內容走 `--arg-file`", iRequired: true),
            };
            aSpecs.AddRange(CommonSpecs());
            return aSpecs;
        }
    }

    protected override SCP_CmdResult ExecuteOnServer(SCP_CmdArgs iArgs)
    {
        string aDataRoot = iArgs.Get("data_root").Trim();
        string aOp = iArgs.Get("op").Trim();
        string aPersona = iArgs.Get("persona").Trim();
        Dictionary<string, string>? aArgs;
        try { aArgs = JsonSerializer.Deserialize<Dictionary<string, string>>(iArgs.Get("args_json")); }
        catch (Exception e)
        {
            return SCP_CmdResult.Fail(2, "✗ args_json 解析不了：" + e.Message, "⛔ 這不是「沒帶」，是**帶了但讀不出**，不猜。");
        }
        if (aArgs == null) return SCP_CmdResult.Fail(2, "✗ args_json 是 null —— 要的是一個 JSON 物件（`{}` 也行）");

        SCP_TaskOpResult r = SCP_TaskOps.Run(new SCP_DataRoot(aDataRoot), aOp, aPersona, aArgs);
        var aResult = new SCP_CmdResult { ExitCode = r.ExitCode };
        aResult.Lines.Add("⤷ 任務單寫入端：Senate Server（" + ServerContext.Describe() + "）");
        foreach (string aLine in r.Report.ToString().Replace("\r", "").TrimEnd().Split('\n')) aResult.Lines.Add(aLine);
        aResult.AddValue("headline", r.Headline);
        foreach (var kv in r.Values) aResult.AddValue(kv.Key, kv.Value);
        aResult.AddValue("notices_json", JsonSerializer.Serialize(
            r.Notices.Select(n => new Dictionary<string, string> { ["kind"] = n.Kind, ["task"] = n.TaskId, ["body"] = n.Body }).ToList()));
        if (r.Memory != null)
            aResult.AddValue("memory_json", JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["topic"] = r.Memory.Topic, ["type"] = r.Memory.Type, ["id"] = r.Memory.Id,
                ["title"] = r.Memory.Title, ["body"] = r.Memory.Body, ["by"] = r.Memory.By,
            }));
        return aResult;
    }
}
