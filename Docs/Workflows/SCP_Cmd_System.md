---
title: SCP_CMD（`senate cmd`）—— 不依賴 Unity 的指令系統
description: SCP_Core 內建的指令目錄與派遣：沒有 queue、直接呼叫 C#、參數規格由 ArgSpecs 宣告、help 由系統產生；與 Unity 那套（senate ucmd）的分工
last_updated: 2026-10-06
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🧩 SCP_CMD（`senate cmd`）

> 一句話：**UCL_Core AgentCommand 的概念，拿掉 queue、拿掉 Unity。**
> CLI 直接呼叫 C#，同一個 process 同步跑完回來。

---

## 兩套 Cmd，兩個動詞（2026-08-29 拍板）

| | `senate ucmd` | `senate cmd` |
|---|---|---|
| 是什麼 | Unity 的 AgentCommand 派遣（client） | SCP_Core 的指令系統 |
| 怎麼跑 | 寫 `queue.json` ＋ `pending.trigger` → Editor 的 Watcher 接手 → 輪詢 result 檔 | **直接呼叫 C#**，同步回傳 |
| 需要 Unity Editor | **是**（Editor 沒開就沒有人執行，逾時 exit 3） | **否**（從頭到尾不需要） |
| 指令住哪 | 目標專案的 UCL_Core（Editor 端） | `SCP_Core/Runtime/Cmd/`（本 repo） |
| 文件 | [`AgentCmd_Dispatch`](AgentCmd_Dispatch.md) | 本文 |

> ⚠ **改名紀錄**：Unity 那套原本叫 `senate cmd`，2026-08-29 改成 `senate ucmd`，
> `cmd` 讓給本系統。舊指令**不保留別名** —— 留著會讓「打了不會動」變成
> 「打了做了另一件事」，而後者的代價高得多（一個要 Editor、一個不要）。

## 為什麼沒有 queue（⚠ 2026-09-02 起只對 `Native` 成立）

> ⚠ **前提已變**（Senate D20 / TASK-0103）：`⤷Server` 那一類 Cmd 的呼叫端與執行端**又是兩個 process** ——
> CLI 是呼叫端、`senate server` 是執行端，中間走的正是下面說「不需要」的那套 queue／trigger／result 檔協議
> （根是 Senate 自己的 `SenateData/runtime/server/`）。本節保留原文不改寫：它對 `Native` 仍然成立，
> 而且它列出的那些坑（`.running` 殘留、「消失＝結束」）正是 Server 執行器要照 Editor 的修法重做一次的清單。

queue 的存在理由是「呼叫端與執行端是兩個 process」。CLI 直接串到 C# 之後那個前提消失了，
於是連帶不存在的還有：trigger 檔、Watcher 輪詢、`.running` 殘留、
以及**「從 queue 消失代表結束」那套推論**（那套推論在 UCL 端出過事：失敗的 OneShot 也會出隊）。

⇒ 回傳值就是回傳值。沒有第二個地方需要對帳。

## 怎麼用

```bash
senate cmd                       # 依分類列出外層指令（等同 senate cmd help；內層由父指令的回傳指路）
senate cmd help 酒館                     # 只列一類（＝ --arg category=酒館；內層縮排在父指令底下）
senate cmd help --arg all=1              # 連內層全列
senate cmd help wake-brief               # 單支的參數說明（內層也照印，並說出從哪支開始走）
senate cmd wake-brief --help             # 同一件事的另一條路（`-h` 也通）
senate cmd help --arg name=wake-brief    # 舊寫法，照舊有效
senate cmd wake-brief --arg persona=Template --arg wake=4 --arg out_dir=D:/tmp/brief
```

旗標只有兩個：

| 旗標 | 做什麼 |
|---|---|
| `--arg k=v` | 指令參數，可重複 |
| `--arg-file k=<路徑>` | 參數值從檔案讀（UTF-8）—— **長內文不經過 shell** |

## exit code（四種失敗要分得出來）

| code | 意思 |
|---|---|
| 0 | 成功 |
| 1 | Cmd 自己回報失敗（例：persona 的信件夾不存在） |
| 2 | 用法錯：認不得的指令名、**沒宣告的參數名**、缺必填、值不在 Choices 裡 |
| 70 | Cmd 執行時丟出例外（由 Registry 接住轉成訊息） |

⚠ 2 與 70 一定要分開：混在一起的話，腳本會把**程式 bug** 當成「我自己打錯」。

失敗（只有 1／70 —— 真的有一端失敗才算）會落一份 `_cmd_errors/<id>.md` 並在 CLI 印 `📄 錯誤報告：<路徑>`；
2（用法錯）與 3（沒有結果，含逾時）刻意不落檔。細節見 [`Cli_Reference`](../API/Cli_Reference.md) 的「錯誤報告」節。
`SCP_CmdResult.Exception` 是 Dispatch 接住例外時留的原始現場 —— 給報告檔用，宿主印給使用者時不印它。

## ⭐ 沒宣告的參數名一律擋下

這是本系統相對 UCL 端刻意多做的一格。UCL 那邊 BUG-14／BUG-15 是同一天開的兩張單：

- **BUG-14**：沒宣告規格時 `value` 打錯名（`val=`）⇒ **靜默取空字串** ⇒ 欄位被清空。
- **BUG-15**：把它放進「必填」之後，**合法的空值進不來**（清空欄位本來就是 `value=`）。

兩張是同一個表達力缺口的兩面 —— 修 14 當天長出 15。收法是兩格：

1. **兩種必填**：`Required`（要有值）／`PresenceRequired`（在場即可，空值合法）。
   > 「沒給」與「給了空的」是兩件事，把它們壓成一件的驗證器會擋掉一半的合法輸入。
2. **未知參數名 ＝ 錯誤**。BUG-14 的根因不是「required 沒宣告」，是
   **打錯的名字沒有人反對**；只要未知參數會出聲，那一整族錯就不存在了。

## 寫一支新 Cmd

繼承 `SCP_Cmd`、有公開無參數建構子，就會被反射掃到（走既有的 `SCP_Reflect.AllTypes`）：

```csharp
public sealed class SCP_Cmd_Example : SCP_Cmd
{
    public override string Name => "example";                 // ⚠ 這是契約：進了別人的腳本就不能隨便改
    public override string Category => SCP_CmdCategory.System;  // 必填（外層）：help 依它分組；分類清單在 SCP_CmdCategory
    public override string Summary => "一句話說明";
    public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
    {
        new SCP_CmdArgSpec("who", "對誰做", iRequired: true),
    };
    public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        => SCP_CmdResult.Success("hello " + iArgs.Get("who")).AddValue("count", "1");
}
```

**外層與內層**：預設是外層（help 上看得到）。只有屬於某支指令子流程的那一步，才改填
`public override string Parent => SCP_CmdRegistry.NameOf<父類別>();`（不填 `Category`，跟著父指令走）——
例如 `morning-brief` 屬於 `morning-wake`。內層不出現在預設 help，靠父指令那條流程的回傳指路；
冷門、管理用、Server 臨界區都**不是**內層的理由。自測會檢查：外層都有已知分類、每支內層都有程式碼指路到它。

回傳的三種東西**分開放**（沿用 run_cmd 的慣例，agent 已經在讀）：

- `Lines`：人可讀的輸出
- `Outputs`：產出檔路徑 → 印成 `📄 回傳檔：<路徑>（N 行／K KB）`
- `Values`：純量 → 印成 `🔢 key = value`

> ⚠ 路徑與純量分開是有血證的：混在一起會讓 `seq` 這種數字被當成路徑去開。

## 讀回傳檔：先看行數，大檔分段讀到第 N 行

- 每個 `📄 回傳檔`（含早安的 `✓ brief：`）都帶 `（N 行／K KB）`。
- 行數或大小超過門檻（預設 500 行或 20 KB，後台「早安 brief」頁可調）⇒ 多一行 `⚠ 大檔`：
  **用你自己工具讀大檔的方法，分段讀到第 N 行**；任何一段被工具標成截斷，就縮小範圍補讀那一段。
- 早安 brief（主檔與續讀檔）最後一行是 `🔚 本檔結束（共 N 行）`。讀到的最後一行該是它。
  ⛔ 它只標結尾，**不證明中段讀到了** —— 有的工具截的是中段、頭尾都留著。

各家一次讀得到多少、怎麼分段（TASK-0419 實測）：

| Agent | 一次的上限 | 截斷形狀 | 分段讀法 |
|---|---|---|---|
| Claude Code `Read` | 約 25k token（預設最多 2000 行） | 砍尾巴，印 `PARTIAL view … lines 1-X of N` | `offset`／`limit` 逐段 |
| Antigravity `view_file` | 800 行／45 KB | 砍尾巴 | `StartLine`／`EndLine` 逐段 |
| Codex `exec_command` | 當次 `max_output_tokens`（預設 10000）＋外層預算 | **砍中段**，印 `N tokens truncated` | `Get-Content <檔> \| Select-Object -Skip <a> -First <b>` 逐段 |

⇒ 共同判準：**讀到第 N 行，而且沒有任何一段被工具標成截斷**，才算讀完。

## 宿主要掛的那一行

共用層**不准知道任何宿主的動詞**，所以錯誤訊息裡的「照著打就會動」那句由宿主宣告：

```csharp
SCP_CmdRegistry.InvocationHint = "senate cmd";
```

沒掛的症狀是**訊息教人打一個在這個宿主上不存在的指令** —— 不會編譯失敗、不會有人回報，
只會讓照著訊息打的人以為自己打錯。（第一版把動詞寫死在共用層，改名那天就撞到了。）

## 現有指令

`PortStatus` 四態：`Native`（本地跑）／`DelegatedToUnity`（Editor 沒開就跑不完）／
`DelegatedToServer`（`senate server start` 沒跑就跑不完，**且不降級成本地跑**）／`NotPorted`（登記在案的缺口）。
`help` 清單行尾標 `⤷Unity`／`⤷Server`／`⛔未實作`，統計行四欄分開印。

| 名字 | 做什麼 |
|---|---|
| `help` | 列出所有 Cmd／單支參數說明。**內容全部由 ArgSpecs 產生**，沒有一份手寫清單會漂 |
| `server-ping` | `⤷Server` 探針：回 Server 的 pid／build／thread —— 驗執行器與協議通不通（TASK-0103） |
| `wake-brief` | 讀 persona 信件庫組一份 wake brief（全量：憲法→見根→見叢→見森→見林→見樹→回憶→記憶維護狀態→見人→見書→今日動作清單） |

## 相關文件

- [`AgentCmd_Dispatch`](AgentCmd_Dispatch.md) —— Unity 那套（`senate ucmd`）
- [`Cli_Reference`](../API/Cli_Reference.md) —— 所有指令與旗標速查
