---
title: Senate 架構總覽
description: 四層分工（SCP_Core 共用碼 / Senate.Core / Senate.Desktop / Senate.Cli）、共用碼的邊界與方言限制、Unity 從宿主降級成 client 的過渡規矩
last_updated: 2026-08-22
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
├── Senate.Core/     設定、git CLI、專案探測（零 UI 依賴）
├── Senate.Desktop/  ImGui renderer、視窗、字型、截圖（碰得到硬體的那半）
└── Senate.Cli/      headless 入口 ＋ 後台頁面
```

依賴方向單向：`Cli → Desktop → SCP_Core`、`Cli → Core → SCP_Core`。
**SCP_Core 不依賴任何人**（那是它能被 Unity 吃下去的前提）。

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
