---
title: CLI 指令參考
description: senate 的所有指令與旗標、exit code 語意、非 UI 操控介面的完整用法與 session 檔位置
last_updated: 2026-08-22
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# ⌨ CLI 參考

執行方式（三種等價）：

```bash
./senate.exe <command>                               # 根層執行檔（build 之後）
.\senate.exe <command>                               # 同上，PowerShell / cmd
dotnet run --project src/Senate.Cli -- <command>      # 開發中直接跑
```

不給指令時預設 `doctor`。

---

## 指令

### `init`

從 `config/senate.local.example.json` 建立 `senate.local.json`，**已存在則不覆寫**（會印「已存在，未覆寫」），
接著跑一次 `doctor`。

### `doctor`

印環境與各專案的讀數（**唯讀**，不動任何檔）。

判定條件：`.NET SDK 問得到` ＋ `git ≥ 2.25` ＋ `設定檔沒壞` ＋ `所有啟用中的專案都是「可用」`。
⚠ 摘要會明說「跳過未檢查幾個」（停用的專案不計入通過）——
**摘要只能宣告它真的檢查過的東西**。

### `selftest`

SCP_Core 共用碼的自我對拍。目前三項：

| 項目 | 驗什麼 |
|---|---|
| Missing 語意 | 讀不存在的 key 會丟例外（訊息帶路徑）／fallback 可用／`Exists` 判定正確 |
| 輸出穩定性 | round-trip 逐字相同／key 保留插入順序／中文不轉義 |
| 讀真檔 | 拿 **Unity 端寫出來的** `commands_schema.json` 讀 → 寫 → 再讀，兩棵樹等價 |

⚠ 找不到樣本檔時回報「**跳過**」而非通過（沒測與測過而且對，不得同形）。

### `ui`

把後台頁輸出成純文字。旗標：

| 旗標 | 做什麼 |
|---|---|
| `--list` | 列出畫面上所有可互動元件（id／類型／標籤／現值／怎麼操作） |
| `--click <id>` | 按下按鈕 —— **真的會跑該頁的 handler**（兩趟繪製，見下） |
| `--set <id>=<值>` | 填欄位（跨次記住） |
| `--toggle <id>` | 切換勾選（讀現值後反轉） |
| `--json` | 整棵畫面樹輸出成 JSON（給程式讀；文字輸出是給人看的） |
| `--reset` | 清空 session（欄位與勾選回到頁面預設） |
| `--window` | 開原生 ImGui 視窗，關窗才結束 |
| `--screenshot <path>` | 開窗、畫幾幀、存 PNG 後**自己關掉** |
| `--width <n>` | 文字輸出寬度（字元格，預設 96），`doctor` / `selftest` 也吃 ⚠ 不吃 `--scale` |
| `--scale <x>` | 介面縮放（0.5〜4，預設 1.0）。**本次有效，不寫回設定檔** |
| `--size <段>` | `small`(1×) / `medium`(1.5×) / `big`(2×) / `xl`(2.5×) —— 同上，本次有效 |

**介面尺寸**：常設值住在 `senate.local.json` 的 `ui` 區塊，改它走畫面上的按鈕
（尺寸現在自己一頁，先 push 進去再按）：

```bash
./senate.exe ui --click doctor/open-style    # 進「介面尺寸」頁
./senate.exe ui --click style/big            # 存進設定檔（回讀確認後才說成功）
./senate.exe ui --click page/back            # 返回上一頁
```

`--scale` / `--size` 刻意不寫回檔案 —— 一道旗標改掉持久設定，
下一個沒帶旗標的人會拿到別人上一次的臨時值，而那不會報錯。
⚠ 視窗模式換尺寸時**間距與版位即時生效、字級要重開視窗**（ImGui 的字級綁在載入時的 font atlas）。

**兩趟繪製**：帶 `--click` 時先畫一趟（讓 handler 執行），再畫第二趟當顯示。
只畫一趟會顯示按下**前**的狀態，看起來像沒反應。

**session**：欄位、勾選、**現在停在哪一疊頁面**（`nav`，內容是 page key）存在
`build/ui_session.json`。每次 CLI 都是新 process ⇒ 不存檔的話多步操作不可能成立
（症狀會是「我按了進去，下一道指令卻回到首頁」，看起來像按鈕失效）。
**點擊不進 session**（它是事件不是狀態）。

**頁面導覽**：`page/back` 是固定 id（`Count > 1` 時才畫得出來）；
每一頁的 key 見 `--list` 的輸出前綴。`--reset` 會把導覽一起清回根頁。
⚠ session 裡的 key 對不上現在的頁面時**停在那裡並印警告**，不會悄悄退回首頁 ——
「你要的那頁不存在了」不可以長得像「你本來就在首頁」。

**id 不存在時擋下並回 exit 2**，並指向 `--list` —— 靜默失敗會讓「按了沒反應」與「按錯了」同形。

---

## exit code

| code | 意思 |
|---|---|
| 0 | 一切正常 |
| 1 | 環境或設定有問題（doctor 不通過 / 開窗失敗 / selftest 有失敗項） |
| 2 | 用法錯誤（未知指令、id 不存在、`--set` 格式錯） |
| 3 | 設定檔存在但內容壞了 |

「還沒設定」（0 或 1，看 doctor）與「設定壞了」（3）刻意分開 —— 那是兩種不同的處置。

⚠ 驗 exit code **不要接 pipeline**：`cmd | tail; echo $?` 讀到的是 `tail` 的狀態。

---

## 給 agent 的操作範式

```bash
./senate ui --list                    # ① 先看有哪些 id（不要猜）
./senate ui --click doctor/refresh    # ② 操作
./senate ui                           # ③ 看操作後的畫面（或直接看 ② 的輸出）
```

判斷「有沒有生效」看**讀數**，不要看畫面像不像：例如 Doctor 頁標題的「第 N 次取讀數」會加一。

---

## 相關文件

- 設定檔規格 → [Config_Schema](Config_Schema.md)
- UI 中間層與 id 規則 → [../Architecture/Ui_Framework](../Architecture/Ui_Framework.md)
