---
title: senate.local.json 規格
description: 本機設定檔的欄位、schemaVersion 的處置、資料根的解析規則、讀檔的兩態
last_updated: 2026-10-08
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# ⚙ 設定檔規格

## 兩份檔，職責不同

| 檔案 | 入版控 | 內容 |
|---|---|---|
| `SenateData/config/senate.local.example.json` | ✅ | 樣板 —— **不含任何機器絕對路徑** |
| `SenateData/config/senate.local.json` | ❌（`.gitignore`） | 實際設定 —— 有絕對路徑 |

> 📂 兩份都住 `SenateData/config/`。整個資料根的版面與「新東西該往哪放」的判準見
> [`Data_Layout`](../Architecture/Data_Layout.md) —— 路徑只在 `SenatePaths` 決定一次。

🩸 為什麼一定要分開：機器路徑進了版控，下一台機器 clone 下來會拿到
「看起來設定好了、但指向不存在的磁碟」的狀態 —— 那跟「還沒設定」**不同形卻同樣安靜**。

---

## 欄位

```jsonc
{
  "schemaVersion": 1,
  "paths": {
    "agentCommandsRoot": "D:/Unity/Valhalla",
    "glossaryRoot": "auto",
    "comicRoot": "D:/commic"
  },
  "awakening": { "lettersRoot": "auto" },
  "ui": { "scale": 1, "textWidth": 96 },
  "install": { "pythonEnvRoot": "", "modelsRoot": "" }
}
```

| 欄位 | 型別 | 規則 |
|---|---|---|
| `schemaVersion` | int | 目前只認得 `1`。**讀到未知版本擋下並說出來**，不盡力而為 |
| `paths.agentCommandsRoot` | string | 資料根，**絕對路徑**。空 ＝ 還沒設定；⛔ 不支援 `auto` |
| `paths.glossaryRoot` | string | 詞典根。`auto` ＝ `<Senate 專案根>/Glossary` |
| `paths.comicRoot` | string | 外部漫畫庫根。空 ＝ 沒有外部漫畫庫（不是錯誤） |
| `awakening.lettersRoot` | string | persona 信件庫根。`auto` ＝ `<資料根>/ChatTavern/baton/letters` |
| `ui.scale` | float | 介面縮放（0.5〜4，**預設 1.0** —— 實機按過四段之後定的，見 D13）。基準尺寸的唯一來源是 `SCP_GuiStyle`，這裡只存「使用者選了什麼」 |
| `ui.textWidth` | int | 純文字輸出寬（字元格，預設 96）⚠ **不吃 `ui.scale`** —— 終端機的一格是字元不是像素 |
| `install.pythonEnvRoot` | string | 安裝系統把 Python 套件裝進哪一份 Python（資料夾：安裝目錄或 venv）。**空白＝自動找系統安裝的 Python**（TASK-0375，見 [`Install`](../Workflows/Install.md)） |
| `install.modelsRoot` | string | 模型快取根（＝`HF_HOME`）。**空白＝HF 預設**（`%USERPROFILE%/.cache/huggingface`） |

所有路徑格的解析（auto、推導、存不存在）統一走 `senate cmd paths`（描述表 `SCP_PathRegistry`，對映在 `SenatePathBinding`）。

`ui` 區塊是**這台機器的顯示偏好**，所以只住在不入版控的那一份：進了版控就會變成
「別人的螢幕決定我的字級」。舊設定檔沒有這個區塊 ⇒ 用預設（那是「沒設過」，不是 0）。
改常設值走畫面上的尺寸按鈕（`ui --click doctor/style/big`）；
`--scale` / `--size` 是**一次性覆寫，不寫回檔案** —— 一道旗標改掉持久設定，
下一個沒帶旗標的人會拿到別人的臨時值，而那不會報錯。

`"//"` 開頭的 key 當註解用（parser 也容忍 `//` 與 `/* */`，但那是給手改檔案的寬容，不是格式的一部分）。

舊檔的 `projects` 區塊讀入時拿掉（下次存檔就不在了）；舊檔沒有 `paths` 而 `projects` 只有一筆啟用的，
那一筆上的 `agentCommandsRoot`／`glossaryRoot`／`comicRoot` 搬進 `paths`（`auto` 不搬）。

### 寫回檔案時不會弄丟的東西（D12 的血證）

| 東西 | 寫回後 |
|---|---|
| 本版不認得的欄位（含 `"//"` 註解鍵） | **保留**（`[JsonExtensionData]`） |
| 中文 | **不轉義**（沒設 `Encoder` 會變成 `\uXXXX` —— 合法 JSON，但人看不懂了） |
| 註解鍵的**位置** | ⚠ 會被移到物件**尾端**（extension data 的寫出順序）—— 內容不丟，位置會變 |

🩸 D11 的第一版把 `"//"` 那行整條吃掉，而其餘欄位都還在 ⇒ 看起來一切正常。
現在 `senate selftest` 的「設定檔 round-trip」一項就是守這個。

### 讀檔兩態

**檔案不存在 → 回 `null`**（那是「還沒 init」，不是錯誤）；
**檔在但解析失敗／版本不認得 → 丟例外**（那是真的壞了，不可靜默降級）。

---

## 第二個本機檔：`SenateData/prefs/senate.pages.local.json`（頁面設定）

與 `senate.local.json` 刻意分開的另一份本機檔（同樣不入版控）：
**各頁面「儲存本頁設定」的落點**（目前：`submodule` 區塊）。分開的理由 ——
頁面每存一次就動一次主設定檔的 diff，而人開 diff 想看的是路徑設定有沒有變。

- 讀寫走 **SCP_Core 自帶的 JSON**（`SCP_JsonParser` / `SCP_JsonMapper` / `SCP_JsonWriter`，
  Tim 2026-08-28 指定）—— 讀寫端在 `Senate.Core/SenatePageStore`。
- 一頁一個頂層 key；存檔時**只換自己那格**，別頁的區塊原樣保留。
- 「沒存過」回 null 不是錯誤；「檔在但壞」會說出來且不覆寫（兩態不得同形）。
- 存檔值的語意是各頁欄位的**預設值**：session（這一輪動過的）＞ 存檔值 ＞ 硬預設。

---

## 第三個本機檔：`SenateData/config/selftest.json`（selftest 預設跑哪些）

人編輯的、不入版控。語意與新增測試的規矩見 [Cli_Reference › selftest](Cli_Reference.md)，**本節只放格式**。

```json
{
  "_說明": "…（程式第一次播種時寫的）",
  "enabled":  ["BankIdRules", "…"],
  "disabled": ["LlmTableParse", "…"]
}
```

- `enabled`＝常駐（預設會跑）；`disabled`＝預設不跑；兩邊都沒列的新項目會跑一次，通過就自動進 `disabled`。同一項兩邊都列 ⇒ `disabled` 贏。
- key 是 `senate selftest --list` 印的**項目名**（比對大小寫不敏感）。
- 其他欄位（含 `"_說明"`）寫回時**原樣保留**；檔案讀不懂 ⇒ exit 2，不退回預設、不覆蓋。
- 三條編輯路徑同一份檔：`senate selftest --enable／--disable`、後台頁「對拍設定」、手改。

## 相關文件

- 指令 → [Cli_Reference](Cli_Reference.md)
