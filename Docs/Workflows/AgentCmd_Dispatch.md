---
title: AgentCommand 派遣（senate ucmd）
description: 用 senate.exe 把 AgentCommand 派給目標 Unity 專案的 Editor —— 檔案協議的 client 半邊、專案設定方式、判定與失敗語意、沒做的部分
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 📮 AgentCommand 派遣（`senate ucmd`）

> 一句話：**把 Cmd 派給 Unity Editor 執行的 client**（u＝Unity）。不依賴 Unity 的指令走 [`senate cmd`](SCP_Cmd_System.md)。
> 旗標與 exit code 速查在 [`Cli_Reference`](../API/Cli_Reference.md)，本文講機制、設定與邊界。

---

## 這是什麼／不是什麼

AgentCommand 派遣**從頭到尾是檔案協議**：

```
client（senate ucmd）                      Unity Editor（執行端）
  │ 1. append queues/<persona>/queue.json      │
  │ 2. 寫 pending.trigger ────────────────────▶│ 3. Watcher 每秒輪詢，File.Move 原子接手
  │                                            │ 4. Runner 執行 handler
  │ 6. 輪詢判定 ◀──────────────────────────────│ 5. 寫 _cmd_results/<id>.json ＋ 回傳檔
```

- ✅ **是**：client 半邊（`Senate.Core/AgentCmdClient.cs`），跨專案（對象由設定檔指定）。
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
- 後台 GUI 的「設定」頁可以直接改 projects[]，改完 CLI 立刻吃到。

## 判定語意

- **成功／失敗的權威來源是 `_cmd_results/<id>.json`**（Editor Runner 出隊前寫）。
  「從 queue 消失」只代表結束 —— 失敗的 OneShot 也會自動出隊。
  找不到 result 檔才退回推論，而且會明講「這是推論」。
- 成功時印 `📄 回傳檔：<路徑>`（handler 回報的落檔位置 —— **讀它印出的路徑，不要背路徑**）
  與 `🔢 key = value`（純量回報，跟路徑分開印：混在一起會讓 seq 被當成路徑去開）。
- 失敗時判決在 stderr＋stdout **各印一份**（PS 5.1 `2>&1` 會把 native stderr 用 cp950 重編碼，只印一邊會被吞），
  並附 `_cmd_errors/<id>.md` 節錄（60 行）。
- 逾時（exit 3）⇒ **回傳檔沒被更新**。下一步去讀它會拿到**上一輪**格式完整、數字合理的內容 —— 先看檔頭時間戳。

## 沒做的

| 沒做的 | 影響誰 |
|---|---|
| schema 預檢＋type 別名 | 打錯 type 的人 —— 多付一次 Editor round-trip 才被擋（有 did-you-mean） |
| Tavern `wait-reply` 握手 | 要等回覆的人 —— 改走 `senate cmd tavern-wait` |
| `op=post` 後 catch-up cursor 提交 | 常駐酒館的 persona —— 「開口＝確認讀完」那條線不會動 |
| lane（`--agent-id x/y`）路由 | 用 lane 分流的場景 —— 只認 persona 資料夾 |

環境標記：偵測表 CLAUDECODE → `claude-code`…；偵測不到時是 **`senate-cli`**。

## 協議三端同步

queue 路徑樣板、queue entry 欄位、trigger 內容、result 檔判定，由三個端共用：
`AgentCmdClient.cs`（client）／`UCL_AgentCommandQueue.cs`（Editor 端）／`Senate.Core/ServerExecutor.cs`（Senate Server 端）。
⚠ Server 端的路徑常數**全部走 `SCP_DataPaths`／`AgentCmdClient`**，沒有自己拼一份。
**任一端改樣板，三端要一起改** —— 落後那端的症狀是 trigger 寫在對方沒在看的地方，**靜默 pending 到 timeout**，沒有任何一格會紅。
