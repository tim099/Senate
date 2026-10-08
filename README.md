# Senate

**Senate 是一套後台工具**，用來管理你電腦上的多個專案 —— 例如自動把改動存檔（commit）、看各專案現在的狀態。

它跟 Unity 是分開的：**Unity 關著的時候它照樣能做事。**

---

## 安裝（第一次才需要）

需要先裝好兩樣東西：

| 要裝什麼 | 從哪裡拿 |
|---|---|
| .NET 10 SDK | <https://dotnet.microsoft.com/download> |
| Git（2.25 以上） | <https://git-scm.com/download/win> |

裝好之後，在 Senate 這個資料夾裡（用 **Git Bash**）執行：

```bash
./install.sh
```

它會自己檢查東西齊不齊、編譯一次、幫你建立設定檔，最後把 Senate 加進**你自己的 PATH**
（不碰系統 PATH、不用系統管理員）。**畫面最後會印一張表**，每一列後面有 `✓` 就是那一項沒問題。

---

## 打包（改過程式之後跑一次）

```bash
./build.sh
```

跑完你會在 Senate 資料夾裡看到 **`senate.exe`** —— 那就是執行檔（旁邊那兩顆 `cimgui.dll` / `glfw3.dll` 是它要用的，別刪）。

build 的最後會**自己試跑一次、也自己開一次視窗**確認真的能用；
有問題它會直接說是哪一格，不會只印「成功」。

---

## 裝完之後：`senate` 在哪都能打

上一步已經把 `publish` 資料夾（執行檔住那裡）加進你自己的 PATH，
所以**新開的** CMD / PowerShell / Git Bash 裡直接打 `senate` 就能用 —— 跟 python 一樣：

```
senate cmd
senate doctor
```

⚠ 已經開著的終端機不會自動生效（PATH 是視窗開起來那一刻複製的）—— 開新的。
### 要移除

```bash
./install.sh --uninstall
```

拿掉 PATH，並清掉編譯產生的東西（`senate.exe`、原生 DLL、`publish/`、`build/`、各專案的 `bin` `obj`）。

**你設定過的東西會留著**（`SenateData/` —— 專案清單、頁面偏好），因為那些掉了要重設。
真的要一併清掉：`./install.sh --uninstall --purge`。

⚠ 兩種都**不會動原始碼**。要徹底移除，自己把 Senate 這個資料夾刪掉就好。

> Git Bash：`./install.sh --uninstall`（`--purge` 同理）。

---

## 開啟畫面

```powershell
.\senate.exe ui --window
```

會跳出一個深色的視窗，開在**入口頁**。**關掉視窗就結束。**

入口頁上有兩區：

| 區塊 | 做什麼 |
|---|---|
| 介面尺寸 | 四顆按鈕（小／中／大／特大）—— 按了會記住（寫回設定檔）。⚠ 字要等**重開視窗**才會跟著變大，間距則是馬上變 |
| 頁面 | 進到別的頁：「Senate 環境檢查」看環境讀數、「Submodule 狀態」看與同步 repo 的 submodule、「Process 管理」看本程式開了哪些外部 process（kill 前三重身分驗證，防誤殺）、「設定」改整份設定檔、「介面尺寸」有比較詳細的說明。頁面多的時候可以用上面的下拉選單（可以打字搜尋） |

進到別的頁之後，左上角有「◀ 返回」；旁邊的「原始碼」會在檔案總管裡打開**這一頁的程式碼**
（給要改東西的人用的；開不起來時畫面上會寫原因，不會沒反應）。

「Senate 環境檢查」那一頁的兩顆按鈕：

| 按鈕 | 做什麼 |
|---|---|
| 重新取讀數 | 重新去看各專案現在的狀態（「第 N 次」會加一） |
| 開啟設定檔 | 用系統預設的編輯器打開設定檔 |

---

## 不開視窗也能看

```powershell
.\senate.exe doctor
```

同一份內容，直接印在終端機裡（適合貼給別人看、或放進自動化流程）。

---

## 操作 Unity 專案（Unity CLI）

要碰 Unity Editor（重編譯、讀編譯錯誤、跑 C#、截圖）一律用官方 Unity CLI：
`unity command <指令> --project-path <Unity 專案根>`。Editor 要開著、專案要裝 `com.unity.pipeline`。

- 流程與讀法 → [`Docs/Workflows/Unity_CLI.md`](Docs/Workflows/Unity_CLI.md)
- ⛔ `senate ucmd` 已廢棄，不要再用。

---

## 不需要 Unity 的指令（`senate cmd`）

Senate 自己內建的一套指令系統（SCP_CMD）——**沒有佇列、直接跑**，Unity 關著也能用：

```
senate cmd                                  # 有哪些指令
senate cmd help --arg name=wake-brief       # 這支要哪些參數
senate cmd wake-brief --arg persona=Template --arg wake=4
```

- 參數一律 `--arg 名=值`（可重複）；長內文用 `--arg-file 名=檔案路徑`。
- **參數名打錯會被擋下並列出它吃哪些參數** —— 不會安靜地當成沒給。
- 說明是從程式本身產生的，不是另外維護的一張表（所以不會跟實作漂掉）。
- 細節見 [`Docs/Workflows/SCP_Cmd_System.md`](Docs/Workflows/SCP_Cmd_System.md)。

---

## Submodule 同步（畫面版）

「Submodule 狀態」頁把各專案 submodule 的分支／髒不髒／領先落後收成一張表，
工具列的按鈕可以直接 `切 → pull` 或 `Push`（寫遠端的動作都要按兩次確認）。

調好的範圍（管哪個 repo、排除哪幾顆、各自切哪條分支、三個開關）按
「**💾 儲存本頁設定**」就會記住 —— 下次開視窗自動回到上次的樣子；
這一輪臨時改的優先，存檔值墊底當預設。

---

## 設定路徑

開視窗、進「**路徑管理**」頁，把**資料根**（AgentCommands 資料樹，例：`D:/Unity/Valhalla`）填好就能用；
詞典根、信件庫根預設是 `auto`（自動推導），外部漫畫庫沒有就留空。每一格都會顯示解析結果與「存不存在」。

不想開視窗的話，直接編輯 **`SenateData/config/senate.local.json`**（`senate init` 會從範本建一份）：

```jsonc
{
  "schemaVersion": 1,
  "paths": { "agentCommandsRoot": "D:/Unity/Valhalla", "glossaryRoot": "auto", "comicRoot": "" },
  "awakening": { "lettersRoot": "auto" }
}
```

想確認每一格解析到哪：`senate cmd paths`。

> 這個檔案裡有你電腦的路徑，所以**不會**被上傳到 GitHub。要分享設定請改 `SenateData/config/senate.local.example.json`。

---

## 遇到問題

| 畫面上寫什麼 | 意思 / 怎麼處理 |
|---|---|
| `路徑不存在` | 設定檔裡的 `root` 打錯，或那個資料夾被搬走了 |
| `非 git repo` | 那個資料夾不是 git 專案 |
| `⚠ N 已 staged` | 你自己先把檔案加進待提交清單了 ⇒ 自動提交會**跳過**這個專案，先自己提交或取消 |
| `Editor 在跑` | Unity 開著，自動提交會讓 Unity 那邊做，Senate 不動它 |
| `⚠ 找不到中文字型` | 視窗裡的中文會變方塊（不是壞了，是這台機器沒有那顆字型） |
| `✗ 開窗失敗` | 這台機器沒有桌面環境（例如遠端連線）⇒ 用上面「不開視窗也能看」那招 |

---

## 給開發／維護的人

程式架構、設計決定、指令完整清單、設定檔規格 → **[`Docs/DOC_INDEX.md`](Docs/DOC_INDEX.md)**