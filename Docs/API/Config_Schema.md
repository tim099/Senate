---
title: senate.local.json 規格
description: 本機設定檔的欄位、schemaVersion 的處置、AgentCommands 資料根的解析規則、三態不得同形的驗證原則
last_updated: 2026-08-22
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# ⚙ 設定檔規格

## 兩份檔，職責不同

| 檔案 | 入版控 | 內容 |
|---|---|---|
| `config/senate.local.example.json` | ✅ | 樣板 —— **不含任何機器絕對路徑** |
| `senate.local.json` | ❌（`.gitignore`） | 實際設定 —— 有絕對路徑 |

🩸 為什麼一定要分開：機器路徑進了版控，下一台機器 clone 下來會拿到
「看起來設定好了、但指向不存在的磁碟」的狀態 —— 那跟「還沒設定」**不同形卻同樣安靜**。

---

## 欄位

```jsonc
{
  "schemaVersion": 1,
  "projects": [
    {
      "name": "LY",
      "root": "D:/Unity/Bar",
      "agentCommandsRoot": "auto",
      "enabled": true,
      "profile": ""
    }
  ]
}
```

| 欄位 | 型別 | 規則 |
|---|---|---|
| `schemaVersion` | int | 目前只認得 `1`。**讀到未知版本擋下並說出來**，不盡力而為 |
| `projects[].name` | string | 顯示與 log 的識別鍵。空白或重複 ⇒ `Validate()` 報錯（重複會讓兩個專案的讀數混在一起） |
| `projects[].root` | string | 專案 git repo 根，**必須絕對路徑** |
| `projects[].agentCommandsRoot` | string | `"auto"`（預設）或明確路徑；相對路徑以 `root` 為基準 |
| `projects[].enabled` | bool | `false` ⇒ 仍列出並標「停用」，但**不計入 doctor 的通過條件** |
| `projects[].profile` | string | 分群規則 profile 名（尚未實作，保留） |

`"//"` 開頭的 key 當註解用（parser 也容忍 `//` 與 `/* */`，但那是給手改檔案的寬容，不是格式的一部分）。

---

## AgentCommands 資料根怎麼解析

`agentCommandsRoot: "auto"` 的解析順序：

1. `<root>/.agentcommands_root.local` pointer 檔存在 → 讀第一行非註解內容當資料根
2. 否則 → `<root>/AgentCommands`

⚠ **只有這兩個位置，不猜第三個。** 猜錯的症狀是寫進另一棵資料樹，而且**不報錯** ——
那是這個 repo 最貴的錯誤形狀（讀寫都「成功」，只是對象不是你以為的那棵樹）。

---

## 驗證原則：三態不得同形

`ProjectProbe` 對每個專案回報四種狀態，**不准壓成兩種**：

| 狀態 | 意思 |
|---|---|
| `NotConfigured` | `root` 空白 —— 使用者還沒填 |
| `Missing` | 填了但那個路徑不存在 —— **設定壞了**，不是「這個專案沒事」 |
| `NotGitRepo` | 路徑在，但不是 git repo |
| `Ok` | 可用 |

同理，載入行為也分兩態：
**檔案不存在 → 回 `null`**（那是「還沒 init」，不是錯誤）；
**檔在但解析失敗／版本不認得 → 丟例外**（那是真的壞了，不可靜默降級）。

---

## 附帶讀數（`Ok` 時才有）

| 欄位 | 來源 | 用途 |
|---|---|---|
| `Branch` | `git rev-parse --abbrev-ref HEAD` | `HEAD` ＝ detached，是一種**擋下的理由**不是分支名 |
| `DirtyCount` | `git status --porcelain -uall` 的行數 | 工作區有多髒 |
| `StagedCount` | `git diff --cached --name-only` | **非 0 ⇒ 自動提交會跳過這個 repo**（呼叫前已 staged 的東西會被併進第一個群） |
| `EditorHeartbeat` | stat `<資料根>/ChatTavern/bartender/_heartbeat.txt` | mtime ≤ 4 秒 ＝ Unity Editor 在 tick ⇒ 不動它的 index |

---

## 相關文件

- 指令 → [Cli_Reference](Cli_Reference.md)
- 為什麼要看心跳 → [../Architecture/Overview](../Architecture/Overview.md#unity-的位置從宿主降級成-client)
