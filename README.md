# Senate

跨專案的 agent 後台 —— **不依賴 Unity**。用設定檔關聯到各個專案，所以同一套系統可以同時管多個 repo。

Unity Editor 在這個架構裡從「宿主」降級成「其中一個 client」：只有真的需要 Editor API 的事
（asset / build / compile）留在 Unity，其餘（git 自動提交、帳本、訊息、排程）由本系統做，
**Editor 關著也照樣能跑**。

## 一鍵配置

clone 完只要跑這一支（會檢查前置 → build → 建立本機設定 → 印讀數）：

```powershell
./setup.ps1
```

Git Bash / Linux / macOS：

```bash
./setup.sh
```

前置只有兩樣：**.NET 10 SDK** 與 **git ≥ 2.25**（`--pathspec-from-file` 需要）。
兩支腳本刻意只做編排，所有判斷都在 `senate doctor` 裡 —— 檢查邏輯寫在腳本裡就會變成兩份會漂的實作。

## 指令

| 指令 | 做什麼 |
|---|---|
| `senate init` | 從 `config/senate.local.example.json` 建立 `senate.local.json`（**已存在則不覆寫**），接著跑 doctor |
| `senate doctor` | 印環境與各專案的讀數（唯讀）。exit 1 ＝ 有啟用中的項目不通過 |
| `senate ui --click <id>` | 把後台頁輸出成純文字；`--click` 模擬按下某顆鈕（**沒有視窗也能驗互動**） |

```bash
dotnet run --project src/Senate.Cli -- doctor
```

## 設定：專案關聯

`senate.local.json`（**不入版控**，因為它有機器絕對路徑；樣板在 `config/`）：

```jsonc
{
  "schemaVersion": 1,
  "projects": [
    { "name": "LY", "root": "D:/Unity/Bar", "agentCommandsRoot": "auto", "enabled": true, "profile": "" }
  ]
}
```

- `agentCommandsRoot: "auto"` → 先讀 `<root>/.agentcommands_root.local` pointer 檔，沒有就用 `<root>/AgentCommands`。
  **只有這兩個位置，不猜第三個** —— 猜錯的症狀是寫進另一棵資料樹而且不報錯。
- `enabled: false` 的專案仍留在清單裡並顯示為「停用」：「我關掉它」與「我沒設定過它」是兩件事。

## 架構

```
src/
├── Senate.Core/   資料與外部世界（設定、git CLI、專案探測）。零 UI 依賴
├── Senate.Gui/    UI 中間層 —— immediate-mode 撰寫 API → 節點樹 → renderer
└── Senate.Cli/    headless 入口 ＋ 後台頁面
```

### UI 為什麼分中間層

撰寫端是 `GUILayout` 手感（一頁一個方法、從上往下寫、按鈕回傳值就是事件），但那些呼叫**不直接畫像素**，
而是長出一棵節點樹；再由 renderer 決定畫成什麼：

```csharp
void Draw(Ui g)
{
    g.Title("問題回報管理");
    using (g.Row())
    {
        m_Filter = g.TextField("篩選", m_Filter);
        if (g.Button("重新載入", "bug/reload")) Reload();
    }
}
```

- **`GuiTextRenderer`（已可用）** → 純文字。UI 於是能 diff、能快照測試、能貼進聊天室給人看
- **ImGui renderer（未做）** → 原生視窗，頁面碼一行都不用改
- 之後要換 HTML／Blazor 也只是第三個 renderer

⇒ 這一層的價值不是「換畫布方便」，是**UI 有讀數可以對**，不用「看起來對」。

### 幾條不打算讓步的規矩

- **id 用資料鍵，不用呼叫順序**。順序推導的 id 在清單增刪時會漂，而漂掉不報錯 ——
  只會讓勾選／滾動／focus 跑到別人身上。撞名退回序號時會記進 `Ui.Diagnostics`，讓它看得見。
- **git 一律呼叫真的 `git.exe`**（不用 libgit2 綁定）：`.gitignore` 邊界、submodule、CRLF、hooks
  換一套實作就有差異，而那種差異不報錯。所有呼叫釘 `-c core.quotepath=false`
  （否則非 ASCII 路徑會印成八進位轉義，比對時每個中文檔名都會被判成不一樣）。
- **兩態不得同形**：「沒設定」／「設定了但不存在」／「可用」是三個狀態，不准壓成「不可用」。
- **摘要只能宣告它真的檢查過的東西**（停用的專案不計入通過，就要說出跳過幾個）。

## 產物與版控

`bin/` `obj/` `build/` 全部不入版控 —— 其中 `obj/project.assets.json` 與 `*.nuget.g.props`
帶有 `packageFolders = C:\Users\<你>\.nuget\packages\`，那是**這台機器**的 NuGet 快取位置。
它是 restore 產物，不是設定：進了版控就是每個人每次 commit 都帶一筆假 diff。

## 進行中

- [ ] `senate autocommit scan|commit` —— 第一個實用功能（Editor 關著也能收檔）
- [ ] ImGui renderer（`--ui`）＋ CJK 字型與 IME 實測
- [ ] 表格 renderer 尚未吃 `--width`（欄寬取自然寬度，窄視窗會超出）
