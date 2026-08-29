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

裝好之後，在 Senate 這個資料夾裡執行：

```powershell
.\setup.ps1
```

它會自己檢查東西齊不齊、編譯一次，然後幫你建立設定檔。**畫面最後會印一張表**，
每一列後面有 `✓` 就是那一項沒問題。

> 用 Git Bash 的話跑 `./setup.sh`，效果一樣。

---

## 打包（改過程式之後跑一次）

```powershell
.\build.ps1
```

跑完你會在 Senate 資料夾裡看到 **`senate.exe`** —— 那就是執行檔（旁邊那兩顆 `cimgui.dll` / `glfw3.dll` 是它要用的，別刪）。

build 的最後會**自己試跑一次、也自己開一次視窗**確認真的能用；
有問題它會直接說是哪一格，不會只印「成功」。

---

## 全域安裝（選用 —— 讓 `senate` 在任何地方直接打）

```powershell
.\install.ps1
```

> Git Bash 的話跑 `./install.sh`，效果一樣。

它把 Senate 資料夾加進**你自己的 PATH**（不碰系統 PATH、不用系統管理員），
之後**新開的** CMD / PowerShell / Git Bash 裡直接打 `senate` 就能用 —— 跟 python 一樣：

```
senate ucmd status
senate doctor
```

⚠ 已經開著的終端機不會自動生效（PATH 是視窗開起來那一刻複製的）—— 開新的。
要移除：`.\install.ps1 -Uninstall`（或 `./install.sh --uninstall`）。

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
| 頁面 | 進到別的頁：「Senate 環境檢查」看各專案狀態、「專案關聯」管理要管哪些專案、「Submodule 狀態」看與同步各專案的 submodule、「設定」改整份設定檔、「介面尺寸」有比較詳細的說明。頁面多的時候可以用上面的下拉選單（可以打字搜尋） |

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

## 把指令派給 Unity（`senate ucmd`）

專案裡那套 AgentCommand（平常用 `run_cmd.py` 派的），**沒有 python 也能派** ——
Senate 直接當派遣端，Unity Editor 照舊執行：

```
senate ucmd run Task --persona summit --arg op=show --arg index=8
senate ucmd status
```

- `ucmd run <指令名>`：派一筆給目標專案的 Unity，**等它跑完**並印出回傳檔路徑。
  參數用 `--arg 名=值`（可重複）；長內文用 `--arg-file 名=檔案路徑`。
- `ucmd status`：看各身分的佇列現在是空的、排隊中、還是執行中（純看，不動任何東西）。
- 對哪個專案？設定檔只有一個啟用專案時**自動選**（會印出選了誰）；
  多個時加 `--project 名字` 點名 —— 它不猜，派錯專案的指令會在別人的 Unity 上真的跑。
- ⚠ **目標專案的 Unity Editor 要開著**才有人執行；沒開的話等 120 秒後會明說逾時。
- ⚠ **2026-08-29 改名**：這套原本叫 `senate cmd`，現在叫 `senate ucmd`（u＝Unity）；
  `cmd` 讓給下面那套不需要 Unity 的。舊動詞不保留別名。

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

## 設定要管哪些專案

最順的路是開視窗、進「**專案關聯**」頁：

1. 在「＋ 新增專案」貼上專案資料夾路徑（直接用檔案總管「複製路徑」貼過來也行，引號會自己去掉）
2. 按「加入草稿」—— 每一列會**當場顯示探測結果**（路徑在不在、是不是 git 專案、
   資料根找不找得到、Unity 有沒有開），打錯馬上看得到
3. 按「**儲存**」才會真的寫進設定檔（存之前都只是草稿，按「放棄改動」可以反悔）

> 探測讀數是快取的 —— 改了路徑或想看 Unity 現在開著沒，按「🔄 重新探測全部」。

不想開視窗的話，直接編輯 Senate 資料夾裡的 **`senate.local.json`**（`setup` 會幫你建好一份範本）：

```jsonc
{
  "schemaVersion": 1,
  "projects": [
    { "name": "LY", "root": "D:/Unity/LY", "agentCommandsRoot": "auto", "enabled": true, "profile": "" }
  ]
}
```

| 欄位 | 意思 |
|---|---|
| `name` | 你自己看的名字（會出現在畫面上） |
| `root` | 專案資料夾的完整路徑（用 `/` 或 `\\`） |
| `agentCommandsRoot` | 填 `"auto"` 就好，它會自己找 |
| `enabled` | `false` ＝ 暫時不管這個專案（**還是會列出來，標成「停用」**） |

> 這個檔案裡有你電腦的路徑，所以**不會**被上傳到 GitHub。要分享設定請改 `config/senate.local.example.json`。

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
