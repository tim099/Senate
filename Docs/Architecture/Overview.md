---
title: Senate 架構總覽
description: 五層分工（SCP_Core 共用碼 / Senate.Core / Senate.Desktop / Senate.Cli / Senate.Server）、共用碼的邊界與方言限制、Unity 從宿主降級成 client 的過渡規矩
last_updated: 2026-09-15
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🏛 架構總覽

## 分層

```
SCP_Core/            submodule —— Unity 與 .NET **共用**（C# 9 / netstandard2.1 / 零第三方套件）
└── Runtime/
    ├── Json/        JSON 值樹＋parser＋writer
    └── Gui/         UI 中間層：節點樹、撰寫 API、文字 renderer、非 UI 操控介面
src/
├── Senate.Core/     設定、git CLI、專案探測、常駐 Server 本體（零 UI 依賴）
├── Senate.Desktop/  ImGui renderer、視窗、字型、截圖（碰得到硬體的那半）
├── Senate.Cli/      headless 入口 ＋ 後台頁面（**會拖著 Desktop** ⇒ 含 Silk.NET／cimgui／glfw3）
└── Senate.Server/   常駐 Server 的**獨立執行檔** —— 只參照 Senate.Core
                     ⛔ 不准參照 Senate.Desktop（csproj 有 <Error> 擋，TASK-0209 A1）
```

依賴方向單向：`Cli → Desktop → SCP_Core`、`Cli → Core → SCP_Core`、`Server → Core → SCP_Core`。
**SCP_Core 不依賴任何人**（那是它能被 Unity 吃下去的前提）。

### 為什麼 Server 要自己一顆 exe（TASK-0209 A7）
`Senate.Cli` 為了後台頁參照 `Senate.Desktop` ⇒ publish 實測 **78MB**、含 `cimgui.dll` ＋ `glfw3.dll`，
**而常駐那顆一格 UI 都不畫**。⇒ 分出來之後跑一整天的那顆不再載著 OpenGL。

出貨形狀：`build.sh` 兩次 publish、**同一個 build_id**（不一致 ⇒ 每支委派 Cmd 都 `build_mismatch`）：

| 產物 | 內容 |
|---|---|
| `publish/senate.exe` | CLI ＋ 後台頁（自足單檔，78MB） |
| `publish/server/senate-server.exe` | 常駐 Server（自足單檔，72MB，**零 GUI 原生層**） |

自動啟動優先起 `publish/server/senate-server.exe`；**找不到就退回用 CLI 自己起**
（開發樹、或還沒跑過 `build.sh` 的環境）—— 那是降級**路徑**不是降級**行為**：
兩個入口共用同一份前置（`ServerBootstrap`）與同一個本體（`ServerHost`）。

---

## SCP_Core 的兩條規矩（都有護欄，不是靠記得）

### ① 方言：C# 9 / netstandard2.1 / 零第三方套件

共用碼必須是 **Unity 編得過的子集**：

- `SCP_Core.csproj` 釘 `<LangVersion>9.0</LangVersion>` ⇒ 檔案級 namespace、`record`、
  raw string literal 這些**在 .NET 這側就編不過**，不會等到搬進 Unity 才發現。
  🩸 實測（2026-08-22）：塞一個檔案級 namespace 進去 →
  `error CS8773: Feature 'file-scoped namespace' is not available in C# 9.0`。護欄有咬。
- `Runtime/SCP_Core.asmdef` 帶 `"noEngineReferences": true` ⇒ **Unity 那側**擋下任何 `UnityEngine` 引用。
- **零 `PackageReference`**：Unity 不吃 NuGet。這條沒有自動護欄 ——
  加套件前先問「Unity 那邊哪來這個？」。`System.Text.Json` 就是因此不能用，也正是自帶 JSON 層的理由。
- `init` 存取子要 `System.Runtime.CompilerServices.IsExternalInit`，netstandard2.1 的 BCL 沒有
  ⇒ `Runtime/SCP_Polyfill.cs` 補一顆 `internal` 的（每個 assembly 各一份不衝突）。

### ② 邊界：只放「純函式 ＋ 零依賴」

| 可以進 SCP_Core | 留在各自那邊 |
|---|---|
| 資料結構、解析／序列化、分類決策、路徑正規化、UI 節點樹與排版 | 檔案 IO、跑 git、log、開視窗、設定檔載入 |

⇒ 判準一句話：**它開始長出「服務」就是越界了。**
共用一個純函式的成本是零；共用一個會碰 IO 的東西，成本是兩邊的生命週期、執行緒模型與錯誤處理全綁在一起。

---

## Senate 全域規則：JSON 一律走 `SCP_Json`（D25，Tim 2026-09-14 拍板）

上面 §① 那句「`System.Text.Json` 就是因此不能用」原本**只綁 `SCP_Core/**`**。
D25 把它升成**整個 repo 的規則**：`src/Senate.Core` / `Senate.Desktop` / `Senate.Cli` 一樣不得用。

> ⛔ **取消的是「純宿主專屬、確定不會搬 ⇒ 可用宿主自己的」那個例外**
> （`<SCP_Core>/Docs~/Coding_Standards.md` §2.1 第三格）。

**為什麼取消**：那一格的判準是**一個預測**，而預測會錯。
`ServerHost` / `ServerExecutor` 當初都落在「確定不會搬」那格 ——
2026-09-14 要把常駐 Server 拆成獨立 exe 時，卡住它們往下搬進 `SCP_Core` 的正是 `System.Text.Json`。
⇒ §2.1 自己那句「**搬家時才換等於把移植成本延後並放大**」，應驗在寫它的人身上。

第二個理由跟依賴無關：**同一批磁碟檔同時被 Unity（`SCP_Json`）與 python 讀寫**，
兩套 writer 的跳脫／數字格式／鍵序不保證同形，而**位元組漂掉不會報錯**。

📌 射程、逐檔讀數、以及 `SenateConfig.cs` 那三處 `[JsonExtensionData]` 為什麼**不是機械替換** → 見 [D25](../Logs/Decisions.md)。
⚠ 規則已立，**程式碼尚未遷移**（7 檔 ~70 處）。新寫的碼從現在起就照這條。

---

## Unity 的位置：從宿主降級成 client

Senate 之前的形狀是「Unity Editor 當執行端，檔案匯流排當 RPC」。新形狀反過來：

| 資料 | 過渡期誰寫 | 最終誰寫 |
|---|---|---|
| 分群規則（哪個檔進哪一筆 commit） | Senate 的 JSON（Unity 端改成讀它） | Senate |
| git index / commit | 誰先搶到誰做，但**互斥** | Senate 獨佔 |
| asset / build / compile | Unity（只有 Editor 有那些 API） | Unity，但被 Senate 呼叫 |

**互斥怎麼做**：`ProjectProbe` stat 專案的 `AgentCommands/ChatTavern/bartender/_heartbeat.txt`，
mtime 在 4 秒內＝Unity Editor 還在 tick ⇒ Senate 不動那個 repo 的 index。
一個檔的 stat，不必 round-trip；它擋掉的是「兩個寫入者搶同一個 index」——
那種錯不會報錯，只會生出混批的 commit。

---

## 為什麼不共用 C# 服務碼（而是共用資料與純函式）

考慮過「把整套邏輯做成 submodule 兩邊共編」，代價是：**共用碼從此永遠住在舊方言裡**、
`.meta` 與 GUID 的雜訊、以及每個消費專案多一個要 bump 的 pointer。
⇒ 結論：**規則是資產、讀規則的程式是消耗品**。規則走資料（JSON ＋ fixture 對拍），
只有真正兩邊都要跑的**純函式**才進 SCP_Core。決策細節見 [Logs/Decisions](../Logs/Decisions.md)。

---

## 相關文件

- UI 中間層的設計 → [Ui_Framework](Ui_Framework.md)
- 建置與出廠驗收 → [../Workflows/Setup_And_Build](../Workflows/Setup_And_Build.md)
- 指令與 exit code → [../API/Cli_Reference](../API/Cli_Reference.md)
