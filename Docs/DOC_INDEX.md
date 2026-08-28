---
title: Senate 文件索引
description: 維護用文件入口 — 架構、UI 框架、建置流程、CLI 與設定規格、設計拍板紀錄。使用者導覽在 repo 根的 README.md
last_updated: 2026-08-28
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 📚 Senate 文件索引

> **這裡是給維護的人看的。** 使用者導覽（怎麼裝、怎麼開、怎麼設定）在 [`../README.md`](../README.md)。
> 兩者刻意分開：README 不談內部規矩，本目錄不重複安裝步驟。
> 單一語系（繁中），不做多語系鏡像 —— 一份會漂的翻譯比沒有翻譯糟。

---

## Architecture — 這套東西長什麼樣

| 文件 | 用途 |
|---|---|
| [Overview](Architecture/Overview.md) | 分層（SCP_Core / Core / Desktop / Cli）、共用碼的邊界與**方言限制**、單一寫入者原則 |
| [Ui_Framework](Architecture/Ui_Framework.md) | UI 中間層：節點樹、撰寫 API、四種 renderer／驅動方式、**id 規則**、慢一幀的事件語意 |

## Workflows — 怎麼做事

| 文件 | 用途 |
|---|---|
| [Setup_And_Build](Workflows/Setup_And_Build.md) | 一鍵配置與一鍵 build 的流程、**為什麼不用 PublishSingleFile**、出廠驗收要驗什麼 |
| [AgentCmd_Dispatch](Workflows/AgentCmd_Dispatch.md) | `senate cmd`：把 AgentCommand 派給目標 Unity 專案的 Editor（run_cmd.py 的 C# client）、專案設定、判定語意、**與 python 版的差距清單** |

## API — 介面規格

| 文件 | 用途 |
|---|---|
| [Cli_Reference](API/Cli_Reference.md) | 所有指令、旗標、exit code 語意 |
| [Config_Schema](API/Config_Schema.md) | `senate.local.json` 欄位規格與路徑解析規則 |

## Logs — 決策紀錄

| 文件 | 用途 |
|---|---|
| [Decisions](Logs/Decisions.md) | 拍板紀錄（ADR）：誰是真相源、共用碼寫哪個方言、UI 為什麼分中間層… |

---

## 寫文件的規矩（本 repo 版）

1. **frontmatter 必填**：`title` / `description` / `last_updated` / `target_audience`。
2. **一個主題一個檔**，放進上面四個分類；分類放不下就先問「這是不是其實屬於別的主題」。
3. **改了行為就改文件**，而且改的是 `last_updated` 那一行也要動 ——
   文件說的比實作大，比沒有文件糟：它會讓人照著做然後撞牆。
4. **血證要留**（🩸 標記）：踩過的坑寫出「當時的讀數」，不要只寫結論。
   結論會被下一個人合理地推翻，讀數不會。
