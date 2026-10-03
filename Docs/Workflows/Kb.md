---
title: 知識庫（Senate 版）—— 語意檢索、常駐嵌入程序、索引與評估
description: senate cmd kb 怎麼用：status／reindex／search／eval／sidecar；後台「知識庫」頁；常駐嵌入程序怎麼起、怎麼停；切塊規則；索引放哪、怎麼判過期；評估題庫怎麼量新舊版；缺套件時走安裝系統問使用者
cmds: [kb]
last_updated: 2026-10-03
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🧠 知識庫（Senate 版，TASK-0378）

> 一句話：**同一批文件、同一個模型（BGE-M3），模型常駐只載一次，切塊依標題，索引是二進位。**
> 取代 UCL_Core 的 `knowledge_base.py`；舊的那支在新版切換之前照常可用（兩邊讀同一份 `kb_targets.json`）。

## 1. 常用

```bash
senate cmd kb --arg op=search --arg target=fragments,alaya --arg query="<把想不起的那件事寫成一句話>"
senate cmd kb --arg op=search --arg target=all --arg query="<同上>" --arg topk=12 --arg format=json
senate cmd kb                                # 狀態：每個 target 幾檔幾塊、過期沒、常駐程序在不在
senate cmd kb --arg op=reindex --arg target=all
```

- ⚠ **輸入形狀是一句話，不是關鍵字** —— 語意檢索。關鍵字查失敗的樣子跟「這條記憶不存在」一模一樣。
- `search` 預設會先把過期的 target 重建（跟舊版同一個預設）；`auto_reindex=0` 照現有索引查。
- `format=json` 的欄位對齊舊版：`score／target／id／file／rel／line／preview`（多一個 `heading`：這一塊在哪一節）。
- 排序方式 `mode=dense`（預設）｜`hybrid`｜`rerank`，另有 `decay=1`（時間衰減）；讀數與取捨見 §6.1。
- 預設 target：`fragments,alaya,coredocs,docs,work_memory`。`all` 不含逐 persona 展開的 `frag_<名>`（它們跟 fragments 收同一批檔）。

## 2. 常駐嵌入程序

- 第一次檢索會拉起它（冷啟動約 1 分多鐘，模型檔已在作業系統快取裡時約 15 秒），之後一句約 0.1 秒。
- **閒置 30 分自己退**；手動：`senate cmd kb --arg op=sidecar --arg action=stop|start|status`。
- 它住 `SenateData/runtime/kb/`（腳本、token、info、log）；只聽 localhost，要帶 token。
- 用 Server 那條「脫離行程樹」的路拉起 ⇒ CLI 結束它還在。

## 3. 缺套件或模型

它需要安裝系統的 `py-flagembedding` 與 `model-bge-m3`。缺了 ⇒ **exit 3**，輸出列出大小、來源與安裝指令 ——
照 [`Install`](Install.md) §6：**先問使用者，同意了才裝**。exit 4 ＝ 量不到（⛔ 不要去問要不要裝）。

## 4. 切塊規則（`KbChunker`，版本 `v1`）

舊版只按空行切、超過 800 字硬切 ⇒ coredocs 13727 塊裡 38% 不到 40 字（多半只是一行標題）、lessons 被切成半截紀錄。新規則：

1. markdown 依標題切段；每一塊前面帶**標題路徑**（frontmatter title › # › ## …）⇒ 標題不再單獨成塊。code fence 裡的 `#` 不算標題。
2. 段內依空行分段落，累積到 900 字；單一段落超長就依句號切。
3. 正文短於 40 字的塊併進下一塊（檔尾的併進上一塊）。
4. jsonl 一筆紀錄一塊（title＋body）；讀不了的行原樣當一塊（⛔ 不丟）。
5. 同一個 target 內正文相同的塊只留第一塊。

改了規則就把 `KbChunker.Version` 加一 —— 版本不同的索引會整份重建，⛔ 不准新舊規則的塊混在一起。

## 5. 索引

- `<資料根>/_kb/<target>/`：`meta.json`（塊清單＋文字＋來源檔 mtime/size）、`dense.f16`（float16）、`sparse.bin`。
  `_kb/` 自帶 `.gitignore`（衍生物，不入版控）。
- **文字沒變的塊沿用舊向量**（雜湊＋模型＋切塊版本都相同才算）⇒ 改一個檔只重嵌那個檔變了的塊。
- 過期判定：來源檔新增／刪除／mtime 或大小變了。`status` 會列出來。
- 新架構兼容舊資料的方式是**從同一批文件重建**；舊的 `_vectors/` 不搬、不讀、不動。

## 6. 評估

```bash
senate cmd kb --arg op=eval                       # dense
senate cmd kb --arg op=eval --arg mode=hybrid     # dense＋0.3×sparse
senate cmd kb --arg op=eval --arg mode=rerank     # hybrid 取前 30 → bge-reranker-v2-m3 重排
senate cmd kb --arg op=eval --arg mode=compare    # 三種排序各跑「不衰減／衰減」，一張表比完
```

題庫在 `SenateData/config/kb_eval.json`（32 題：每題一句話＋該找到的檔；查詢刻意不抄標題用字）。
算 recall@5 與 MRR@10，逐 target 列。⚠ 評估前會把用到的 target 重建到最新 —— 拿過期索引評出來的分數不是檢索品質。

⚠ **題庫綁著某個專案的文件**（目前是 LY 的）。換專案跑時，預期檔不在的題一定「沒排上」，而那跟「排序變差」在分數上同形（TASK-0382：Bar 實測 19／32 全是這個）。
所以評估先檢查每題**答不答得出來**：預期檔不在這個專案的來源裡，或預期那段不在切塊後的文字裡 ⇒ **跳過**，另外列出原因與題號、**不進 recall／MRR 的分母**。
回傳值 `skipped` ＝ 跳過幾題。⛔ 跳過的題不能拿來比排序；擴充題庫時也要想清楚那題屬於哪個專案。

## 6.1 排序方式與時間衰減（TASK-0382）

| 模式 | 做法 | 要什麼 |
|---|---|---|
| `dense`（預設） | BGE-M3 的 dense 內積（cosine） | 嵌入模型 |
| `hybrid` | dense ＋ 0.3×sparse（BGE-M3 lexical） | 同上（sparse 本來就存在索引裡） |
| `rerank` | hybrid 取前 30，交給 `bge-reranker-v2-m3` 重排；最後順序＝重排分 | 另需安裝項目 `model-bge-reranker-v2-m3`；缺了走「缺相依」（exit 3） |

- ⚠ **分數尺度不同**：dense／hybrid 是內積（約 0.4–0.9），rerank 是 0..1 的重排分（很開：相關的 0.99、不相關的 0.01）。**不能跨排序比大小**；ucl-memory 的分數帶（`>0.58` 之類）只對 dense 成立。
- 重排模型第一次 `/rerank` 才載入（多佔約 1GB 顯存）；舊版常駐程序（沒有 `/rerank`）會被自動關掉重起。重排不是用 FlagReranker：它呼叫 `tokenizer.prepare_for_model`，新版 transformers 已經沒有，改直接載序列分類模型。
- **時間衰減**（`decay=1`）：分數 −= `decay_weight`（預設 0.05）×（1 − 2^(−年齡天數／半衰期)），年齡取來源檔 mtime。
  ⛔ **只對 `kb_targets.json` 設了 `half_life_days` 的 target 生效**（目前：fragments、work_memory、逐 persona 的 frag_*，各 90 天）；文件類沒設 ⇒ 不衰減。
  舊的 `knowledge_base.py` 讀同一份檔、只認它要的欄位，多一個 `half_life_days` 無害。

**讀數（2026-10-03，Bar，36 題可算；fragments 24／coredocs 6／work_memory 6；dense 預設）**

| 排序 | recall@5 | MRR@10 | 查詢中位數 |
|---|---|---|---|
| dense | 33／36 | 0.736 | 102 ms |
| hybrid | 34／36 | 0.802 | 109 ms |
| rerank | 34／36 | **0.907** | 346 ms |

- 衰減（權重 0.05）：dense 0.736→0.771、hybrid 0.802→0.793、rerank 不變；recall 都沒退步。權重 0.15：dense →0.789、hybrid recall 35／36 但 MRR 0.792、rerank 不變。
  **題庫沒有「新舊衝突」的題，所以量不出衰減的好處，只量得出它沒有害**。
- 已知難題 `core-05`（中文查詢、英文文件）在**所有排序都不在前 10**；`core-04` 只有 hybrid（第 5）與 rerank（第 1）救得回；rerank 反而丟了 `core-06`。
- ⚠ 題庫的 `mem-01～15` 是 kotoko 自己的碎片當「回憶測試」（第一人稱口吻、不抄標題用字）；在 Bar 以外的專案這些題的預期檔不在 ⇒ 會被跳過。

## 7. 後台「知識庫」頁（TASK-0381）

`senate ui --page kb`（或後台首頁 ▸ 設定 ▸ 知識庫）。Unity 的 `UCL_KnowledgeBaseAdminPage` 已於 2026-10-03 廢棄，功能搬到這裡。

- 頁面**不自己算任何東西**：狀態、重建、檢索、評估、常駐程序的起停全走 `senate cmd kb`（同一套實作）。
  狀態與檢索讀 `format=json`（`status` 的 JSON：`sidecar{running,pid,device,loaded_ms,served}`＋每個 target 的 `state／files／chunks／built_at／detail`；
  `state` ＝ `fresh` 最新｜`stale` 落後磁碟或規則換了｜`unbuilt` 還沒建｜`unknown` 不認得）。
- 各 target 一列，各有「建立／重建」鈕；另有「重建全部」。檢索可選範圍（預設／單一 target／all）與排序方式（dense／hybrid）。
- 三個不得同形：**沒在跑** ≠ 0 句（沒在跑只畫一句話）；**量不到狀態** ≠ 沒有 target（畫錯誤框，不畫空表）；
  **缺相依（exit 3）** ≠ 一般錯誤（畫專屬的框、指去「安裝管理」頁，本頁不自己裝）。
- 檢索結果每列有「定位」鈕（檔案不在磁碟上時畫「檔案不存在」＝索引比磁碟舊，要重建）。
- 「評估題庫」摺疊區跑 `op=eval`（自己的排序下拉，多一個 `compare`＝三種排序一張表比完），給 TASK-0382 比較排序用。
- 檢索可選 dense／hybrid／rerank 與「時間衰減」（選項讀 Cmd 宣告的 `mode` 清單，加新排序頁面不必改）。
- 動作都跑背景 job（重建與檢索共用常駐程序，同一時間只有一個）；冷啟動要 1 分多鐘，畫面不卡。

## 8. 還沒做的

- **預設排序的拍板**（dense → hybrid 或 rerank）：讀數在 §6.1；改預設要連帶重量 ucl-memory 的分數帶，且 rerank 要每台機器都裝重排模型。
- 衰減是否預設開：題庫量不出好處，目前預設關。
