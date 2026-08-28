---
title: CLI 指令參考
description: senate 的所有指令與旗標、exit code 語意、非 UI 操控介面的完整用法與 session 檔位置
last_updated: 2026-08-28
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

**旗標**

| 旗標 | 做什麼 |
|---|---|
| `--clipboard` | 多跑兩項剪貼簿對拍。⚠ **會覆蓋你的剪貼簿**（它就是在測寫入）⇒ 預設不跑 |

`--clipboard` 的兩項分工：`剪貼簿 round-trip` 驗 `SenateClipboard` 讀寫通不通（測試字串含中文與符號，
因為 Win32 是 UTF-16 而 ImGui 那端是 UTF-8，中間兩次轉碼）；`ImGui 剪貼簿 callback` 走**兩個
callback 本身**驗 marshalling（UTF-8 編碼／NUL 結尾／記憶體還活著）。
⚠ 「按 Ctrl+V 時 ImGui 會不會呼叫它」要人親手按一次 —— 程式驗不到，所以那一項的讀數裡寫明了。

### `submodule status` / `submodule sync`

多層 submodule 專案的日常痛點：`submodule update` 之後全員 detached HEAD、分支跑掉、
誰 ahead 誰 behind 沒人一眼看得到。`status` 把它收成一張表（**唯讀**），
`sync` 是唯一會動手的那條路。

兩者與 `Submodule 狀態` 頁吃**同一份掃描層**（`Senate.Core/SubmoduleScan`）。

#### 共用旗標（status 與 sync 都吃）

| 旗標 | 做什麼 |
|---|---|
| `--root <path>` | 對哪個 repo 動手。**sync 必填**（見下方安全設計）；status 不給就是 Senate 自己 |
| `--project <name>` | 改用 `senate.local.json` 裡的專案名（停用／路徑壞掉的會擋下並說原因） |
| `--branch <b>` | 全域預設 branch —— 目標解析的**第三層** |
| `--set-branch <path>=<b>` | 逐項指定目標 branch —— 目標解析的**最高層**。可重複 |
| `--fetch` | 先逐顆 fetch 再讀 ⇒ ahead/behind 才是即時值（只動 remote-tracking ref，不碰工作目錄） |
| `--only <path>` | 只處理某幾顆（**白名單**，可重複） |

**目標 branch 四層解析**：`--set-branch` ＞ `.gitmodules` 的 `branch =` ＞ `--branch` ＞ 啟發式
（只有一條分支就用它／否則 master，沒 master 才 main）。
⚠ 四層都空 ⇒ **那一顆跳過**，不會拿「目前所在」頂替（那等於沒有這個功能）。
表格的「來源」欄會說出它是哪一層來的 —— 使用者看到「它想把我切到 Dev」時的第一個問題是「憑什麼」。

⚠ `--only` 與 `--set-branch` 指到不存在的路徑一律**擋下**（exit 2），不靜默略過：
`--only` 打錯是「少做一顆」（看得出來），而 `--set-branch` 打錯是「那顆照舊用啟發式的目標」——
它會**照樣成功**，只是切到了另一條分支，而報告上是一排 ✓。

#### `sync` 專屬旗標

| 旗標 | 做什麼 |
|---|---|
| `--checkout` | 切到目標 branch（dirty、HEAD 有未合併 commit、branch 不存在，一律**跳過並列出**） |
| `--pull` | `pull --ff-only`（分岔就失敗列出，不替人 merge / rebase） |
| `--push` | 寫遠端 ⇒ **必須同時給 `--yes`** |
| `--push-all-remotes` | 推該 repo 的每一個 remote（關 ＝ 只推 origin）。⚠ pull 不跟進 |
| `--include-root` | root 也一起 pull / push。⚠ **root 永遠不切 branch** |
| `--yes` | `--push` 的明示 |
| `--dry-run` | 只印打算做什麼，不動任何東西 |

至少要給一個動作（`--checkout` / `--pull` / `--push`），都不給會擋下並指回 `status`。

#### 安全設計（三格，都是為了同一件事）

1. **`sync` 不給預設對象** —— 必須 `--root` 或 `--project`。
   🩸 UCL 那邊的血證（2026-08-11）：設定漂移讓工具在 B 專案裡誠實地對 A 專案動手、
   回報一整排 ✓，而 B 的 submodule 一個位元組都沒動 —— **綠燈全亮，量到的是別的 repo。**
   ⇒ 會寫東西的指令不猜對象；唯讀的 `status` 才給預設（猜錯也不會壞東西）。
2. **`--push` 另外要 `--yes`** —— 互動式確認在這裡做不到（stdin 是 null device），
   所以確認的形態是「再打四個字」而不是「按 Enter」。
3. **順序由深到淺，root 最後** —— parent 的 bump commit 引用 child 的 SHA，
   先推 parent 會讓別人 pull 到指向遠端還不存在的 commit 的 gitlink（**靜默壞**，只有 clone 的人才發現）。

⚠ 安全線（dirty / 在不在目標 branch / 有哪些 remote）一律在**動手當下現場重問 git**，
不吃掃描快照 —— 掃描與按下去之間狀態會變，而「照片乾淨、現在髒了」會讓
「dirty 就跳過」的承諾靜默失效，報告還照印 ✓。

#### exit code

| code | 意思 |
|---|---|
| 0 | 沒有失敗（**跳過不算失敗** —— 那是刻意的保護，但一定會出現在 `✓n ⏭n ✗n` 那一行） |
| 1 | 有失敗 |
| 2 | 用法錯誤 |

⚠ 只印「✓ 完成」會讓「跳過 8 顆」看起來像「做完 8 顆」，所以摘要三個數字一起印。

#### 跟頁面的分工

`Submodule 狀態` 頁（`ui --click home/open/submodule`）**決定與動手都做得到**：

- **決定**：選 repo（可直接打路徑，預設 Senate 自己）、全域預設 branch、
  逐顆納入／排除、逐顆指定 branch、三個開關。
- **動手**：工具列三顆鈕 —— `切 → pull（不推）` / `Push` / `一鍵同步（切 → pull → push）`。
  寫遠端的兩顆走**兩段式確認**（按一次變成「⚠ 確定執行…」＋「取消」，再按才跑），
  而那道手勢跟這裡的 `--push` 要求 `--yes` 是同一個東西。

⚠ 頁面上**打字的兩格（repo 路徑、全域預設 branch）是草稿**，要按「✓ 套用並重新掃描」才生效
（勾選與下拉是立即生效）。理由：在視窗裡打字是逐字元的，值一變就重掃等於打一個路徑跑 N 輪 git。
生效值住 session，所以跨指令／跨開窗記得住。

```bash
./senate.exe ui --click home/open/submodule                 # 進頁
./senate.exe ui --set submodule/root=D:/Unity/LY            # 填草稿（不會掃）
./senate.exe ui --click submodule/apply                     # 套用 ⇒ 這一步才掃
./senate.exe ui --click submodule/discard                   # 放棄草稿，欄位回生效值
./senate.exe ui --click submodule/root/self                 # 改回 Senate 自己（立即生效）
./senate.exe ui --click submodule/root/paste                # 從剪貼簿貼路徑（只填草稿）
./senate.exe ui --fold submodule/per-item                   # 展開逐項設定（收合時裡面的 id 不存在）
./senate.exe ui --toggle submodule/only/<submodule 路徑>     # 排除／納入某一顆
./senate.exe ui --click submodule/run-pull                  # 切 → pull（不碰遠端，直接跑）
./senate.exe ui --click submodule/run-push                  # 進待確認態（**不會**直接推）
./senate.exe ui --click submodule/confirm                   # 確認 ⇒ 這一步才寫遠端
./senate.exe ui --click submodule/confirm-cancel            # 放棄那個待確認的動作
```

⚠ `submodule/root/applied`、`submodule/default-branch/applied` 與 `submodule/pending`
是**內部狀態不是畫面元件** ⇒ `--set` 會被「畫面上沒有這個 id」擋下（那是對的：
換 repo 或確認動作都走上面那些 `--click`，跟人在畫面上做的動作完全一樣）。

### 🩸 這一頁曾經刻意不放寫入鈕

原本的理由是宿主形狀：一輪 fetch＋pull＋push 跨十幾顆 submodule 是**分鐘級**的事，
而純文字那側畫幾趟就結束 process（丟到背景等於什麼都不會發生），視窗那側同步跑又會凍住畫面。

⇒ 2026-08-28 那條限制被**正面解掉**而不是繞開：批次跑在背景執行緒（`SubmoduleSyncJob`，
本 repo 第一個背景工作），而 `SCP_GuiHost.RedrawsContinuously` 讓同一份頁面碼在兩種宿主上都對 ——
**會重畫的丟背景並每幀顯示進度，不重畫的同步跑完才返回**。
📌 那條舊理由**仍然是對的**，它只是不再是「不做」的理由，而是「怎麼做」的規格。

⚠ **沒有取消鈕**：git 跑到一半被 kill 可能留下 `index.lock` 或半完成的 fetch，
而那個殘局比多等一會兒貴得多。每一顆 git 自己有逾時上限（本機 120s、走網路 300s）。
⚠ 批次執行中**不重掃、也不畫操作鈕**；跑完會自動重讀一次狀態 ——
報告說「切好了」不算數，狀態表讀回來的才算。

⇒ 那麼這裡的 `sync` 還有什麼用？**腳本與 CI**，以及 `--dry-run`（只有指令這條路有）。
頁面照舊把等價指令印出來，而兩者吃**同一組設定** ⇒ 畫面上調好的範圍與複製去跑的範圍逐字相同。
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
| `--keydebug` | （視窗模式）畫面底部多一行**鍵盤／剪貼簿讀數** —— 見下方「Ctrl+V 沒反應時怎麼查」 |
| `--page <key>` | （視窗模式）開窗直接停在某一頁：`home` / `doctor` / `submodule` / `style` / `settings`。認不得的 key **exit 2** 並印出現有清單（清單由頁面目錄產生，不是寫死的） |

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

### Ctrl+V 沒反應時怎麼查（`--keydebug`）

```bash
./senate.exe ui --window --keydebug
```

畫面底部會多三行，把「Ctrl+V 沒反應」的**三個斷點**分開：

| 讀數 | 它回答什麼 |
|---|---|
| `io.KeyCtrl` / `Silk:Ctrl` / `V` | ImGui 有沒有收到 modifier 與 V 鍵（兩邊都印 ⇒ 分得出是 Silk 沒送還是 ImGui 沒收） |
| `clipboard callback: Get / Set` | **ImGui 到底有沒有呼叫我們的 callback** —— 這是最關鍵的一格 |
| 自我對拍（不需按鍵） | 注入 `ModCtrl=true` 之後 `io.KeyCtrl` 讀回什麼 ⇒ 驗「補 modifier 那條路本身有效」 |

判讀：**`Get` 不動** ⇒ ImGui 沒把組合鍵交給 `InputText`（往鍵盤那半查）；
**`Get` 有動但欄位沒字** ⇒ 剪貼簿是空的或格式不是文字（往剪貼簿那半查）。

🩸 為什麼需要這一整套：2026-08-28 那次，兩層自我對拍**全過**、`Install` 也回報「已接上」，
而 Tim 實測 Ctrl+V 仍然沒反應。把範圍切開的是他補的一句「**但是按鈕的貼上 OK**」——
那一句立刻把「剪貼簿實作」整段排除掉了。
⇒ 一個「三個斷點在畫面上長得一模一樣」的問題，必須有東西把它們分開，否則只能靠猜。

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
