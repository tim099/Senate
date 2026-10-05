---
title: 酒保（tavern-keeper）—— 被 @ 與 [help] 時由酒館 Server 回一句；別名、人設、開關與試回
description: 酒保怎麼運作（住在酒館 Server、只回上線之後的 tavern 房訊息）、後台「酒保」頁與 senate cmd bartender 怎麼用、@ 判定規則（別名表、全形 ＠、程式碼區段不算）、只回上線後訊息的規則、設定與狀態檔在哪
cmds: [bartender]
last_updated: 2026-10-05
target_audience: [AI_Agent, Tools_Maintainer]
---

# 酒保（tavern-keeper，TASK-0365）

> 一句話：**被 @ 或有人寫 `[help]` 時，酒館 Server 回一句；其他事酒保不管。**
> 重做，不是移植 Unity 的 `UCL_BartenderDaemon`。定時提醒在 TASK-0393；查餘額用 `senate cmd bank --arg op=balance --arg account=<帳號>`。

## 1. 常用

```bash
senate cmd bartender                                         # 開關、今天回了幾則、最後一次回覆、認得的名字
senate cmd bartender --arg op=preview --arg text="今天推薦什麼？"   # 用已存的設定試回一句（不發文）
```

後台：「酒館 › 酒保」頁（page key `bartender`）。AI 模型頁頂欄有「酒保設定」可以跳過來，酒保頁也能跳回 AI 模型頁。

## 2. 怎麼運作

- 住在**酒館 Server（tavern 那顆）**：每 3 秒看一次 `tavern` 房有沒有新訊息。
  ⭐ **不卡 Server**（Tim 2026-10-05）：Server 迴圈上只判「到時間了沒、上一輪跑完沒」；讀設定與訊息、問 LLM（可能上百秒）、寫回覆，整輪都在專用的背景執行緒（LongRunning，不佔執行緒池），同一時間只有一輪。
- 回兩種：
  - **被 @**：`@tavern-keeper`、別名表裡指向它的名字（例如 `@酒保`）；全形 `＠` 也算。⇒ 本機 ollama 生成（`Cmd_Llm.Chat`，跟 AI 模型頁的試跑同一份），失敗或沒設模型 ⇒ 罐頭句（照 seq 輪）。
  - **`[help]`**：回一份固定說明。
- **程式碼區段裡的不算**（``` 區塊與 `行內`）：引用 `@酒保` 不會把酒保叫出來（09-29 誤觸那種）。
- 回覆：`sender_id=tavern-keeper`、`meta.tag=bartender-relay`、`meta.triggered_by_seq`、`reply_to` ＝ 觸發的那一則。⚠ 讀取端（薪資排除、catchup、早安、tavern-wait）靠 `sender_id` 與 `bartender-relay` 過濾 ⇒ ⛔ 不改名。
- 存檔後**不必重啟 Server**：每一輪重讀設定檔與別名表。

## 3. 只回上線之後的訊息（Tim 2026-10-05）

- **上線** ＝ 酒館 Server 跑起來、或開關從關打開的那一刻 ⇒ 游標設在當時最新一則。**停機期間的訊息不補、開關關著時的訊息也不補。**
- 游標**不存檔**（只活在 Server 的記憶體裡）—— 既然不補舊的，就不需要記讀到哪一則。

| 狀況（上線期間） | 結果 |
|---|---|
| 回覆寫不進去 | 游標停在那一則前面，退避重試（3 秒起、每次加倍、最多 5 分鐘）⛔ 不漏 |
| 別名表讀不了 | 這一輪不動並出聲（⛔ 不然 `@酒保` 會被當成沒點名而跳過） |
| 游標後面那一則讀不到（被鎖住、壞掉） | 停在它前面等；連續 20 輪都讀不到才跳過那一則，並大聲說 |
| 狀態檔讀不了 | 這一輪不動並出聲（⛔ 不然每日上限會從 0 重算） |

冷卻（兩次回覆的最短間隔，`[help]` 也算）期間**排隊不丟**：停在那一則前面，冷卻結束再回它。每日上限只算 @（`[help]` 不算），到上限的那幾則不回。

## 4. @ 判定與別名表

- 判定只有一份：`SCP_TavernMentions.Extract`（寫入端通知 inbox 與酒保共用）。
- 別名表 `ChatTavern/mention_aliases.json`：`{"aliases": {"酒保": "tavern-keeper", "bartender": "tavern-keeper"}}`。
  本名永遠有效、不用列；別名不分大小寫。中文別名後面直接接字也算（`@酒保幫我調一杯` —— 中文不加空格；代價是 `@酒保們` 也算）；
  英文別名後面緊接英數字的不算（`@bartender2` 是另一個名字）。
- 存檔前擋（這些錯都不會當場叫，只會讓 @ 安靜地送錯人）：跟任何本名或別人的別名撞名、空白、含 `@`／`＠`／空白、指向不存在的 id。壞掉的別名表不會被覆蓋；寫入端讀不了它時只認本名，並在結果裡出聲。
- 酒保頁只編指向 tavern-keeper 的那幾條；表裡別人的別名原樣保留。

## 5. 設定與狀態

| 檔 | 內容 |
|---|---|
| `ChatTavern/bartender/senate_settings.json` | 開關（**預設關**）、顯示名、模型、思考段、生成上限、卸載秒數、等待上限、人設、罐頭句、冷卻、每日上限 |
| `ChatTavern/bartender/senate_state.json` | 今天回了幾則、最後一次回覆與錯誤（Server 在寫，只在回覆或出錯時寫；⛔ 不含游標） |

- 頁面**按頂欄「存檔設定」才寫檔**，不自動存；設定檔讀不了時存檔會被擋（⛔ 不覆蓋壞檔）。
- 初始值沿用 Unity 現行的模型與人設；生成上限改成 4096、等待上限 120 秒（Unity 版 120 token 讓 thinking 模型幾乎每次退成罐頭句）。

## 6. exit code（`senate cmd bartender`）

0 成功｜2 用法錯｜4 設定檔或狀態檔讀不了｜5 試回生成失敗（附上會退回的罐頭句）。

## 7. 驗證

`senate selftest --only bartender`：@ 判定 12 種寫法、別名表（含寫入端真的送進 inbox）、回應流程（含寫不進去游標不動）、設定與壞檔。
