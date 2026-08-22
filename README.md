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

## 開啟畫面

```powershell
.\senate.exe ui --window
```

會跳出一個深色的視窗，裡面是各專案的狀態表。**關掉視窗就結束。**

視窗裡的兩顆按鈕：

| 按鈕 | 做什麼 |
|---|---|
| 重新取讀數 | 重新去看各專案現在的狀態（標題的「第 N 次」會加一） |
| 開啟設定檔 | 用系統預設的編輯器打開設定檔 |

---

## 不開視窗也能看

```powershell
.\senate.exe doctor
```

同一份內容，直接印在終端機裡（適合貼給別人看、或放進自動化流程）。

---

## 設定要管哪些專案

編輯 Senate 資料夾裡的 **`senate.local.json`**（`setup` 會幫你建好一份範本）：

```jsonc
{
  "schemaVersion": 1,
  "projects": [
    { "name": "LY", "root": "D:/Unity/Bar", "agentCommandsRoot": "auto", "enabled": true, "profile": "" }
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
