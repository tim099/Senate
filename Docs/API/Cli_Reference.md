---
title: CLI 指令參考
description: senate 的所有指令與旗標、exit code 語意、非 UI 操控介面的完整用法與 session 檔位置
last_updated: 2026-08-23
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

SCP_Core 共用碼的自我對拍。⚠ 這張表**只挑幾項舉例**，真正的清單以 `senate selftest` 的輸出為準
（之前這裡寫「目前三項」，而實際上早就不只三項 —— 文件比實作小一樣會誤導）：

| 項目 | 驗什麼 |
|---|---|
| Missing 語意 | 讀不存在的 key 會丟例外（訊息帶路徑）／fallback 可用／`Exists` 判定正確 |
| 輸出穩定性 | round-trip 逐字相同／key 保留插入順序／中文不轉義 |
| 頁面堆疊／摺疊 | 只畫最上頁、生命週期順序、同實例 push 擋下、收合時子節點不存在 |
| 下拉選單 | 收合不建子節點／關鍵字（非 regex）比對／分頁夾取／選取有寫回 |
| 頁面目錄 | opt-in（`MenuGroup`）／分組篩選／壞頁記一筆不擋清單／重複 key 丟例外 |
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
| `--fold <id>` | 摺疊／展開一個區塊。⚠ 收合時**內容不會被建出來**，所以 `--list` 也看不到那一段的欄位 |
| `--json` | 整棵畫面樹輸出成 JSON（給程式讀；文字輸出是給人看的） |
| `--reset` | 清空 session（欄位與勾選回到頁面預設） |
| `--window` | 開原生 ImGui 視窗，關窗才結束 |
| `--screenshot <path>` | 開窗、畫幾幀、存 PNG 後**自己關掉** |
| `--width <n>` | 文字輸出寬度（字元格，預設 96），`doctor` / `selftest` 也吃 ⚠ 不吃 `--scale` |
| `--scale <x>` | 介面縮放（0.5〜4，預設 1.0）。**本次有效，不寫回設定檔** |
| `--size <段>` | `small`(1×) / `medium`(1.5×) / `big`(2×) / `xl`(2.5×) —— 同上，本次有效 |
| `--seed-session` | （視窗模式）開窗時接續 CLI session 的欄位／勾選／摺疊（**截圖模式自動開**）。不帶的話視窗從乾淨狀態開始 —— 下拉一律是收合的 |
| `--page <key>` | （視窗模式）開窗直接停在某一頁：`home` / `doctor` / `style` / `settings`。認不得的 key **exit 2** 並印出現有清單（清單由頁面目錄產生，不是寫死的） |

**入口頁（根頁，key `home`）**：只有兩件事 —— 調介面尺寸、進到別的頁。

```bash
./senate.exe ui                              # 入口頁
./senate.exe ui --click home/size/big        # 直接在入口頁換尺寸（寫回設定檔）
./senate.exe ui --click home/open/doctor     # 直達某一頁（id 是 home/open/<page key>）
./senate.exe ui --click home/reload          # 重掃頁面清單（丟掉目錄的中繼資料快取）
```

**頁面下拉（可搜尋）**：頁面多起來時走這條。它的 id 全部是 `<key>/…` 前綴：

```bash
./senate.exe ui --click home/page                 # ① 點開下拉
./senate.exe ui --set home/page/search=set        # ② 搜尋（選項 ≥ 8 個才有這個欄位，見下）
./senate.exe ui --click home/page/pick/settings   # ③ 選一項（id 用 value 本身，不用序號；選完自動收起來）
./senate.exe ui --click home/open                 # ④ 開啟選中的那一頁
```

⚠ **兩件事會讓 id「現在不存在」，而那不是壞掉**：

- 選項按鈕與搜尋框**收合時根本不建**（同 `Fold` 的判準）⇒ 沒先 `--click home/page`
  就按 `home/page/pick/...` 會回 `✗ 畫面上沒有這個 id`。
- 搜尋框只在**選項 ≥ 8 個**時才畫（少量選項時它只是多一行雜訊）。
  Senate 現在只有 3 頁 ⇒ 那個欄位不會出現，`--set home/page/search=...` 會被擋下。
- `home/page/value`（選中的值）是**內部狀態不是畫面元件** ⇒ 一樣不能 `--set`。
  要直接進某一頁的話有更短的路：`--click home/open/<page key>`。

分組篩選是同一套（`home/group`）：`--click home/group` 點開 → `--click home/group/pick/設定`。
篩完之後 `home/page` 的清單會跟著變短。

**介面尺寸**：常設值住在 `senate.local.json` 的 `ui` 區塊。入口頁與「介面尺寸」頁都能改
（後者多了字級／文字寬的說明）：

```bash
./senate.exe ui --click home/open/style      # 進「介面尺寸」頁
./senate.exe ui --click style/big            # 存進設定檔（回讀確認後才說成功）
./senate.exe ui --click page/back            # 返回上一頁
```

**設定頁（自動繪製）**：畫的是**整份 `senate.local.json`**，欄位由反射產生 ⇒ id 就是成員路徑：

```bash
./senate.exe ui --click home/open/settings     # 進設定頁
./senate.exe ui --set settings/Ui/Scale=1.5    # 改值（只改草稿，不寫檔）
./senate.exe ui --fold settings/Projects       # 把專案清單收起來
./senate.exe ui --click settings/save          # 寫檔（Validate 過才寫，寫完回讀確認）
./senate.exe ui --click settings/revert        # 重新讀檔，丟掉未存的改動
```

改了不存**不會**寫檔（刻意不自動存：打字打到一半就落地的字級可能讓人看不見還原按鈕）。
存檔走 `SenateConfig.Save` ⇒ `"//"` 註解與未知欄位照樣保留（D12）——
實測：改一個值存檔、改回來再存，檔案與原檔**逐字相同**。
⚠ 設定檔壞掉時這一頁**不提供編輯**（不用空白頂上去 —— 那會讓「檔壞了」長得像「還沒設定」，
而按下儲存就把壞掉的內容換成一份空的，不可逆）。

**視窗那側的驗收**：`ui --screenshot <p> --page settings` 可以在沒有人點的情況下
拍到指定頁面 —— 加這條旗標的理由就是「視窗裡的頁面本來只有人點得到，所以沒有讀數」。

⭐ 視窗開起來時會**接續 CLI session 的欄位／勾選／摺疊**（單向，不含 `nav`），
所以「展開的下拉在視窗裡長怎樣」也拍得到：

```bash
./senate.exe ui --click home/page                        # 用 CLI 擺好狀態（這一側會驗 id）
./senate.exe ui --screenshot build/x.png --page home     # 再開窗截圖（截圖模式自動接續）
./senate.exe ui --window --seed-session                  # 互動模式要接續得顯式講
```

⚠ **視窗預設不接續** —— 🩸 第一版是無條件接續，於是在終端機測試時點開的下拉，
變成別人開窗時「預設就是展開的」。那不是他的操作，是殘留狀態漏過了驅動端的邊界。

`--scale` / `--size` 刻意不寫回檔案 —— 一道旗標改掉持久設定，
下一個沒帶旗標的人會拿到別人上一次的臨時值，而那不會報錯。
⚠ 視窗模式換尺寸時**間距與版位即時生效、字級要重開視窗**（ImGui 的字級綁在載入時的 font atlas）。

**兩趟繪製**：帶 `--click` 時先畫一趟（讓 handler 執行），再畫第二趟當顯示。
只畫一趟會顯示按下**前**的狀態，看起來像沒反應。

**session**：欄位、勾選、**現在停在哪一疊頁面**（`nav`，內容是 page key）存在
`build/ui_session.json`。每次 CLI 都是新 process ⇒ 不存檔的話多步操作不可能成立
（症狀會是「我按了進去，下一道指令卻回到首頁」，看起來像按鈕失效）。
**點擊不進 session**（它是事件不是狀態）。

**每一頁工具列上的固定 id**：`page/back`（返回）、`page/home`（回首頁）、
`page/source`（在檔案總管裡開這一頁的 `.cs`）、
`page/copy-class`（**開不了檔案總管的宿主才會有**：把類別名複製到剪貼簿）。

⚠ 兩顆是**階梯不是並列**：有 `page/source` 就沒有 `page/copy-class`。
而 `page/source` 這次失敗時會**自動退到複製類別名**，訊息會寫出來 ——
不管走到哪一步，「這一頁是哪個 class」都看得到（page key `home` ≠ 類別名 `HomePage`）。

```bash
./senate.exe ui --click page/source    # 印出 ✓ 已在檔案總管顯示 …\HomePage.cs
```

**頁面導覽**：`page/back` 是固定 id（`Count > 1` 時才畫得出來），`page/home` 是「回首頁」
（深度 > 2 才畫 —— 深度剛好 2 時它跟返回是同一個動作）。
⚠ 繼承 `SCP_GuiToolPage` 的頁面**自己畫這兩顆**，controller 讓位（否則會出現 `page/back#2`）；
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
./senate ui --list                       # ① 先看有哪些 id（不要猜）
./senate ui --click home/open/doctor     # ② 導覽
./senate ui --click doctor/refresh       # ③ 操作
./senate ui                              # ④ 看操作後的畫面（或直接看 ③ 的輸出）
```

判斷「有沒有生效」看**讀數**，不要看畫面像不像：例如 Doctor 頁標題的「第 N 次取讀數」會加一。

---

## 相關文件

- 設定檔規格 → [Config_Schema](Config_Schema.md)
- UI 中間層與 id 規則 → [../Architecture/Ui_Framework](../Architecture/Ui_Framework.md)
