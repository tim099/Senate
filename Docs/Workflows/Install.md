---
title: 安裝系統 —— 模型與 Python 套件的安裝、解除安裝、安裝位置（含 skill 缺相依時的詢問流程）
description: senate cmd install 與「安裝管理」頁怎麼用：相依清單怎麼加項目、五種狀態、Python 環境與模型位置怎麼選、兩道解除安裝的擋，以及 skill 缺相依時 agent 要先問使用者、同意後才用 CLI 安裝
cmds: [install]
last_updated: 2026-10-02
target_audience: [AI_Agent, Tools_Maintainer]
---

# 📦 安裝系統（TASK-0375）

> 一句話：**清單說有哪些東西可以裝，CLI 和後台頁用同一套實作去量、去裝、去拆。**
> 參數表看 `senate cmd help install`；本檔寫怎麼用、什麼時候用、有什麼紀律。

## 1. 三個入口，同一套實作

| 入口 | 用途 |
|---|---|
| `senate cmd install` | agent／腳本用。`op=status`（預設）／`check`／`install`／`uninstall`／`create_env` |
| 後台「安裝管理」頁（`senate ui --page install`） | 人用。每列一顆鈕（安裝／重裝／接著下載／解除安裝），兩段式確認 |
| 「路徑管理」頁（`senate ui --page paths`） | 改 Python 環境（`PythonEnvRoot`）與模型位置（`ModelsRoot`）—— 安裝頁本身**不存路徑** |

⭐ **不帶 `confirm=1` 一律只印計畫**（要裝哪些、多大、從哪下載、裝到哪），一個檔都不動。

**目前裝了哪些**（之後要拆用）：`senate cmd install --arg only=installed` —— 只列已安裝的項目，逐項附上解除安裝指令；
機讀值 `installed_ids`（逗號分隔；什麼都沒裝時是空字串，⛔ 不是不印）。`only=` 也收 `missing`／`broken`／`partial`／`unknown`。

## 2. 狀態有五種，⛔ 不互相冒充

| 狀態 | 意思 | 怎麼量 |
|---|---|---|
| ✓ 已安裝 | 能用 | pip：在**新的子程序**裡實際 import 成功（有 `check` 的還要它成立）；模型：snapshot 裡必要檔全在 |
| ・沒安裝 | 沒有 | pip：import 回 ModuleNotFoundError 而且讀不到版本；模型：快取目錄不在 |
| ✗ 裝了但壞了 | 有東西但不能用 | import 失敗、`check` 不成立（例：torch 是 CPU 版）、模型缺必要檔 |
| ◐ 不完整 | 下載到一半 | HF 快取裡有 `.incomplete` —— 再按一次安裝會接著下載 |
| ？ 量不到 | 不知道 | 找不到 Python、探針自己炸了 —— ⛔ **不是「沒安裝」**，不給裝也不給拆 |

⚠ 量一次 pip 狀態大約 40 秒（要 import torch）—— 後台頁在背景量。

## 3. Python 環境與模型位置（Tim 2026-10-02 拍板）

- **Python**：`PythonEnvRoot` 空白 ＝ 自動找系統安裝的那一顆（PATH 上；跳過 Microsoft Store 的轉址殼）。
  填一個資料夾 ＝ 用那一份（Python 安裝目錄或 venv）。資料夾是空的 ⇒ 可以 `op=create_env` 在那裡建一份 venv。
  ⚠ 指定了資料夾而裡面沒有 Python 時**不會退回系統那顆** —— 那會靜默改變安裝對象。
- **模型**：`ModelsRoot` 空白 ＝ HF 預設（`%USERPROFILE%/.cache/huggingface`）。子程序一律拿到顯式的 `HF_HOME`。
- **狀態一律看選定的那一份實際裝了什麼**：已經裝好的（例如系統 Python 裡的 torch、預設快取裡的 bge-m3）直接判為已安裝，不搬、不重裝。

## 4. 相依清單：新增項目只改清單

清單在 `SenateData/config/install_catalog.json`（入版控）。一筆的欄位：

| 欄位 | 必填 | 說明 |
|---|---|---|
| `id` | ✓ | 唯一識別（skill 宣告相依用它） |
| `kind` | ✓ | `pip`（Python 套件）或 `hf_model`（Hugging Face 模型） |
| `name`／`desc` | | 給人看的 |
| `requires` | | 要先裝好的其他 id（安裝時一起排進計畫；解除安裝時用來判斷還有誰在用） |
| `dists` | pip ✓ | 量版本、解除安裝用的 distribution 名 |
| `imports` | pip ✓ | 要實際 import 的模組名 |
| `check` | | import 之後再算的 Python 運算式（要為真），例：`torch.cuda.is_available()` |
| `checkHint` | | `check` 不成立時的說明 |
| `pipArgs` | pip ✓ | `pip install` 後面接的參數（含必要的 `--index-url`） |
| `repo` | hf ✓ | `組織/名稱` |
| `requiredFiles` | hf ✓ | snapshot 裡一定要有的檔 —— **要列權重檔**，不然只下載了 config 也會被判成裝好 |
| `shared` | | 共用地基（只是讓畫面多提醒一句；真正擋解除安裝的是 `requires`） |
| `sizeMb`／`source` | | 估計大小與下載來源 —— 問使用者時要照實說 |

載入時整份驗證：重複 id、`requires` 指到不存在的項目、相依成環、該 kind 缺必填欄位 ⇒ **整份不收**並說為什麼。

⚠ 安裝 pip 項目**不帶 `-U`**：已經滿足的相依不會被換掉。舊的知識庫頁跑 `pip install -U torch`，會把 GPU 版 torch 換成 PyPI 的 CPU 版 —— 所以 torch 是獨立一項、從 cu126 索引裝、排在 FlagEmbedding 前面。

## 5. 解除安裝的兩道擋（都在動手前，被擋 ＝ 零變動）

1. **還有已安裝的項目需要它** ⇒ 不拆（例：torch 還被 FlagEmbedding、whisper 需要）。
2. **它正在被使用** ⇒ 不拆。pip：同一顆 python.exe 有程序在跑；模型：有檔案獨佔開不了（有人正讀著）。

另外：一次只拆一個；模型的解除安裝是**刪掉整個快取目錄，不可復原**；後台頁按下確認之後會**重新量一次、重新判一次**才動手。

## 6. ⭐ skill 缺相依時：先問，同意了才裝

skill 在 `SKILL.md` 的 frontmatter 宣告它需要的項目：

```yaml
requires_install: [py-flagembedding, model-bge-m3]
```

agent 用到那個 skill、而它的工具回「沒安裝／後端缺席」時：

```bash
senate cmd install --arg op=check --arg skill=<skill 名>      # 或 --arg ids=<id,id>
```

| exit | 意思 | agent 要做的 |
|---|---|---|
| 0 | 都裝好了 | 問題不在安裝，照工具自己的錯誤訊息查 |
| **3** | **缺相依** | 把輸出裡「要轉告使用者的內容」（項目、大小、來源、會裝到哪）**照實**轉告，**問一次要不要裝** |
| **4** | **量不到** | ⛔ **不要問要不要裝** —— 不知道缺不缺。先處理輸出列的原因（多半是找不到 Python） |

使用者**明確同意之後**，才跑輸出裡印的那一行：

```bash
senate cmd install --arg op=install --arg ids=<那幾個 id> --arg confirm=1
```

- ⛔ agent **不得自己決定安裝**；⛔ 同意一次只算那一次、那幾項 —— 不延伸到下一個項目或下一次。
- 安裝輸出（pip、下載進度）即時印在 stderr；結論與機讀值在 stdout。exit 5 ＝ 動手了但重新量不是「已安裝」。
- 安裝會跑很久（torch 2.5 GB、模型數 GB）：跑之前就跟使用者說大概多大。

## 7. 不在這裡的

- Unity 那三頁（影音管理、知識庫管理、AI 模型管理）之後廢棄、搬到 Senate 並依賴這套系統 —— **搬頁是另外的單**。
- `media_admin.py` 的幾個特例（onnxruntime-gpu 的拆裝順序、faster-whisper 的 `--no-deps`）還沒收進清單；搬影音頁時一起處理。
- ollama（LLM 模型）不是 pip 也不是 HF 模型 —— 要收的話是新增一種 kind。
