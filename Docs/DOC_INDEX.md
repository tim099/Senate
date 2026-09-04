---
title: Senate 文件索引
description: 維護用文件入口 — 架構、UI 框架、建置流程、CLI 與設定規格、設計拍板紀錄。使用者導覽在 repo 根的 README.md
last_updated: 2026-09-04
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
| [Data_Layout](Architecture/Data_Layout.md) | **`SenateData/` 資料根**：三層分類判準（config / prefs / runtime）、新東西該往哪放、**路徑分兩族**（Senate 自己的檔 vs 外部動態路徑）與各自的唯一決定點、⛔ 「決定點包含值存在哪」、⛔ 改路徑必須同時做 migration |
| [Ui_Framework](Architecture/Ui_Framework.md) | UI 中間層：節點樹、撰寫 API、四種 renderer／驅動方式、**id 規則**、慢一幀的事件語意、⛔ **頁面要宿主的值一律問介面**（不自存第二份設定） |

## Workflows — 怎麼做事

| 文件 | 用途 |
|---|---|
| [Setup_And_Build](Workflows/Setup_And_Build.md) | 一鍵配置與一鍵 build 的流程、⛔ **改完 code 先 build 再對 exe 驗**（Debug DLL 與 exe 是兩本帳）、出廠驗收三格、**single-file 的真正判準** |
| [SCP_Cmd_System](Workflows/SCP_Cmd_System.md) | `senate cmd`：SCP_Core 內建的指令系統（**沒有 queue、不需要 Unity**）、參數規格與四種 exit code、怎麼寫一支新 Cmd |
| [AgentCmd_Dispatch](Workflows/AgentCmd_Dispatch.md) | `senate ucmd`：把 AgentCommand 派給目標 Unity 專案的 Editor（run_cmd.py 的 C# client）、專案設定、判定語意、**與 python 版的差距清單** |

## API — 介面規格

| 文件 | 用途 |
|---|---|
| [Cli_Reference](API/Cli_Reference.md) | 所有指令、旗標、exit code 語意、`letters_root`／`data_root` 沒給時從唯一那格設定補上（**而且印出來**） |
| [Config_Schema](API/Config_Schema.md) | `senate.local.json` 欄位規格與路徑解析規則 |

## 撰寫規範 — 動 code 前先讀

| 文件 | 用途 |
|---|---|
| [Agent 入口檔的受管區塊](../SCP_Core/Docs~/Entry_Doc_Blocks.md) | CLAUDE.md / AGENTS.md 的**附加式**安裝：marker 格式（成對 BEGIN/END）、七種狀態、舊版整檔安裝的遷移、備份與回讀 |
| [SCP 專案撰寫規範](../SCP_Core/Docs~/Coding_Standards.md) | 住在 **SCP_Core submodule** 裡（規則跟著共用碼走，Unity 那側才讀得到同一份）：方言限制、**JSON 一律走 `SCP_Json`**、**設定一律走專案層 prefs**、純函式邊界、血證登記處 |

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
