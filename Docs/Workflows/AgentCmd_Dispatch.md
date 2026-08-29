---
title: AgentCommand 派遣（senate ucmd）
description: 用 senate.exe 把 AgentCommand 派給目標 Unity 專案的 Editor —— 檔案協議的 C# client 半邊、專案設定方式、判定與失敗語意、與 run_cmd.py 的差距清單
last_updated: 2026-08-28
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 📮 AgentCommand 派遣（`senate ucmd`）

> ⚠ **2026-08-29 改名**：本系統的動詞從 `senate cmd` 改成 **`senate ucmd`**（u＝Unity）。
> `cmd` 讓給不依賴 Unity 的 [SCP_CMD](SCP_Cmd_System.md)。舊動詞**不保留別名** ——
> 留著會讓「打了不會動」變成「打了做了另一件事」，而這兩套一個要 Editor、一個不要。
> ⚠ 下方 2026-08-28 的驗收讀數是用舊動詞跑的；**讀數本身沒變**，只有指令名換了。

> 一句話：**`run_cmd.py` 的 C# 版 client** —— 沒有 python 的環境（Codex）也能派 Cmd 給
> Unity Editor 執行。旗標與 exit code 速查在 [`Cli_Reference`](../API/Cli_Reference.md)，
> 本文講機制、設定與邊界。

---

## 這是什麼／不是什麼

UCL_Core 的 AgentCommand 派遣**從頭到尾是檔案協議**：

```
client（run_cmd.py 或 senate ucmd）         Unity Editor（執行端）
  │ 1. append queues/<persona>/queue.json      │
  │ 2. 寫 pending.trigger ────────────────────▶│ 3. Watcher 每秒輪詢，File.Move 原子接手
  │                                            │ 4. Runner 執行 handler
  │ 6. 輪詢判定 ◀──────────────────────────────│ 5. 寫 _cmd_results/<id>.json ＋ 回傳檔
```

協議雙方誰都不知道對面是誰 ⇒ Senate 只重做 **client 半邊**（`Senate.Core/AgentCmdClient.cs`），
Editor 端零改動。所以：

- ✅ **是**：一支不依賴 python 的派遣工具，跨專案（對象由設定檔指定）。
- ⛔ **不是**：Cmd 的另一個執行端。**Editor 沒開就沒有人執行** ——
  `ucmd run` 會在逾時（預設 120s）後 exit 3。Senate 自己不跑任何 handler。

## 設定：連到哪個 Unity 專案

對象專案就是 `senate.local.json` 的 `projects[]`（與 doctor / submodule 共用同一份設定，
欄位規格見 [`Config_Schema`](../API/Config_Schema.md)）：

```jsonc
{
  "projects": [
    { "name": "LY", "root": "D:/Unity/LY", "agentCommandsRoot": "auto", "enabled": true }
  ]
}
```

- `agentCommandsRoot: "auto"` ＝ 先讀 `<root>/.agentcommands_root.local` pointer 檔，
  沒有則 `<root>/AgentCommands`（解析器與 doctor 同一支，`ProjectProbe.ResolveAgentCommandsRoot`）。
- 選誰：`--project <name>` 點名 ＞ **只有一個啟用專案時自動選**（會印出選了誰）＞ 其餘擋下。
  多啟用專案不猜 —— 派錯專案的 Cmd 會在**別人的 Editor 上真的執行**。
- 後台 GUI 的「設定」頁可以直接改 projects[]（反射自動畫欄位），改完 CLI 立刻吃到。

## 判定語意（跟 `run_cmd.py` 同一套，別自己發明第三種）

- **成功／失敗的權威來源是 `_cmd_results/<id>.json`**（Editor Runner 出隊前寫）。
  「從 queue 消失」只代表結束 —— 失敗的 OneShot 也會自動出隊。
  找不到 result 檔才退回舊推論，而且會明講「這是推論」。
- 成功時印 `📄 回傳檔：<路徑>`（handler 回報的落檔位置 —— **讀它印出的路徑，不要背路徑**）
  與 `🔢 key = value`（純量回報，跟路徑分開印：混在一起會讓 seq 被當成路徑去開）。
- 失敗時判決在 stderr＋stdout **各印一份**
  （🩸 PS 5.1 `2>&1` 會把 native stderr 用 cp950 重編碼，✗ 判決整段被吞 —— run_cmd.py 同一課），
  並附 `_cmd_errors/<id>.md` 節錄（60 行）。
- 逾時（exit 3）⇒ **回傳檔沒被更新**。下一步去讀它會拿到**上一輪**格式完整、數字合理的內容
  （🩸 UCL 2026-08-16 血證）—— 先看檔頭時間戳。

## 與 `run_cmd.py` 的差距（v1 刻意不做，要做去讀 run_cmd.py 對應段）

| 沒做的 | 影響誰 | run_cmd.py 對應段 |
|---|---|---|
| schema 預檢＋type 別名 | 打錯 type 的人 —— 多付一次 Editor round-trip 才被擋（有 did-you-mean） | `precheck_cmd_type` / `normalize_cmd_type` |
| Tavern `wait-reply` 握手引擎 | 拿 senate 發酒館訊息**等回覆**的人 —— 送得出去、等不到 | `_tavern_cmd` 的 wait-reply 段 |
| `op=post` 後 catch-up cursor 提交 | 常駐酒館的 persona —— 「開口＝確認讀完」那條線不會動，🆕 會累積 | `_commit_catchup_cursor_if_post` |
| lane（`--agent-id x/y`）路由 | 用 lane 分流的進階場景 —— senate 只認 persona 資料夾 | `_split_queue_id` |

⚠ 環境標記：`_caller_env_marker` 偵測表與 python 端同（CLAUDECODE → `claude-code`…），
差一格 —— python 的 fallback 是 `unknown`，senate 是 **`senate-cli`**，讓 Treasury 帳上分得出
走哪條 client 進來的。

## 協議三端同步警告

queue 路徑樣板、queue entry 欄位、trigger 內容、result 檔判定，現在有**三個端**共用：
`run_cmd.py`（python client）／`AgentCmdClient.cs`（本 repo client）／
`UCL_AgentCommandQueue.cs`（Editor 端）。**任一端改樣板，三端要一起改** ——
落後那端的症狀是 trigger 寫在對方沒在看的地方，**靜默 pending 到 timeout**，沒有任何一格會紅。

## 驗收讀數（2026-08-28 首發，LY 活 Editor 實測）

- `ucmd status`：列出 LY 資料根下 14 條 persona queue 的 state 與殘量。
- `ucmd run Task --persona basecamp --arg op=show --arg index=8` ⇒ Editor 接手、
  Success（result 檔判定）、回傳檔檔頭 ts 為當下。
- `index=99999` ⇒ exit 2、判決雙印、錯誤報告節錄。
- 出廠 `senate.exe` 跑 `Tavern op=catchup` ⇒ `🔢 unread = 16`（values 回報接通）。
