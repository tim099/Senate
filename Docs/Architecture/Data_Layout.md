---
title: SenateData 資料根版面
description: Senate 自己的設定檔與專案內資料一律住 SenateData/ — 三層分類的判準（config / prefs / runtime）、新東西該往哪放、路徑的唯一決定點、以及改路徑必須同時做 migration 的理由
last_updated: 2026-09-02
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🗂 SenateData —— Senate 自己的資料根

> **一句話：Senate 執行時產生的東西，一律住 `<repo>/SenateData/`，repo 根只留原始碼與建置入口。**

## 為什麼要有這一層

散在 repo 根不只是難看。`_process_registry` 與 `ui_session.json` 原本住在 **`build/`** ——
那是**產物目錄**：gitignored、不在任何備份裡，而且是人「東西壞了就整個刪掉重來」時
第一個下手的地方（`rm -rf build/`、換一台機器 clone）。
狀態放在那裡等於託付給一個隨時會被**合理**刪除的位置，而消失之後的行為
跟「這台機器沒設定過」**一模一樣**：沒有錯誤、沒有紅字，畫面完全正常。

> ⚠ 讀數（2026-09-01 實測）：`build.sh` 與 `build.ps1` 目前**並不會**清 `build/`
> （`grep -n "rm -rf\|Remove-Item" build.sh build.ps1` 零命中）。
> ⇒ 危險不是「每次 build 都會沒」，是**「它沒有任何理由被保住」** ——
> 沒有人承諾過那個目錄的壽命，而現在也沒有人需要承諾了。

⇒ 這是三態同形（見 [`SCP 撰寫規範 §3.3`](../../SCP_Core/Docs~/Coding_Standards.md)）的又一個實例：
**「設定不見了」與「還沒設定」不得長成同一個樣子。**

## 版面

```
<repo>/SenateData/
├─ config/     人編輯的設定
│   ├─ senate.local.json           ❌ 不入版控（含機器絕對路徑）
│   └─ senate.local.example.json   ✅ 入版控（樣板，不得含絕對路徑）
├─ prefs/      程式替使用者寫的偏好
│   ├─ senate.pages.local.json     ❌ 各頁「儲存本頁設定」的落點
│   └─ imgui.ini                   ❌ ImGui 視窗版面
└─ runtime/    進程活著才有意義的狀態
    ├─ _process_registry/          ❌ 外部進程登記中心
    ├─ ui_session.json             ❌ CLI 跨呼叫的 UI session
    ├─ _server_heartbeat.json      ❌ 常駐 Server 的心跳（pid／build id／時間戳，每 0.5 秒覆寫）
    ├─ _server_stop.request        ❌ `senate server stop` 留給 Server 的停止請求（Server 看到就自退並刪掉）
    └─ server/                     ❌ Server 自己的資料根（版面同 AgentCommands：queues/<lane>/、_cmd_results/、_cmd_errors/）
```

## ⭐ 新東西該放哪 —— 判準只有一句

> **「這個檔掉了，使用者要不要重做工？」**

| 答案 | 放哪 | 意思 |
|---|---|---|
| 要重新設定 | `config/` | 人手動編輯過的東西。備份要含它 |
| 不用，但會不習慣 | `prefs/` | 程式替使用者記下來的偏好。掉了回預設，無痛 |
| 完全無感 | `runtime/` | 進程活著才有意義。**可隨時刪，而且應該被清** |

⚠ 判準刻意不是「是不是 JSON」「入不入版控」「是不是使用者可見」——
那幾個問題在邊界上會給出互相矛盾的答案，而這一句不會。

## 路徑的唯一決定點

**所有檔名與目錄名只在 [`src/Senate.Core/SenatePaths.cs`](../../src/Senate.Core/SenatePaths.cs) 出現一次。**

```csharp
SenatePaths.LocalConfig(iRepoRoot)       // SenateData/config/senate.local.json
SenatePaths.PageStore(iRepoRoot)         // SenateData/prefs/senate.pages.local.json
SenatePaths.ImGuiIni(iRepoRoot)          // SenateData/prefs/imgui.ini
SenatePaths.ProcessRegistry(iRepoRoot)   // SenateData/runtime/_process_registry
SenatePaths.UiSession(iRepoRoot)         // SenateData/runtime/ui_session.json
SenatePaths.ServerHeartbeat(iRepoRoot)   // SenateData/runtime/_server_heartbeat.json
SenatePaths.ServerStopRequest(iRepoRoot) // SenateData/runtime/_server_stop.request
SenatePaths.ServerRoot(iRepoRoot)        // SenateData/runtime/server/（底下走 SCP_DataPaths）
```

⛔ **呼叫端不要自己 `Path.Combine(repoRoot, "SenateData", ...)`** —— 那就是第二個決定點，
而兩個決定點對「現在該在哪」的看法遲早會分岔（見
[`SCP 撰寫規範 §4`](../../SCP_Core/Docs~/Coding_Standards.md)：同一個路徑不准在兩個地方各解析一次）。
要新落點就往 `SenatePaths` 加一支具名成員。

📌 這一層**刻意不進 SCP_Core**：SCP_Core 管的是跨端契約的版面，而 `SenateData/`
只有 Senate 這一個宿主會用（Unity 那側沒有這個東西）。
規則是「一個路徑只能有一個決定點」，不是「路徑一定要在 Core 算」。

## ⛔ 改路徑 ＝ 同時要做 migration

**動了 `SenatePaths` 的任何一格，就要在
[`SenateDataMigration`](../../src/Senate.Core/SenateDataMigration.cs) 補一條搬遷。**

理由跟上面那格是同一個：舊檔還躺在原地時，程式會在**空的新位置**讀到「沒設定過」，
然後表現得完全正常 —— 專案清單空了、頁面設定回預設、視窗版面重來。
使用者不會回報 bug，他只會以為自己忘了設定。

搬遷跑在 `Program.Main` 的最前面（**任何讀設定的動作之前**），冪等，且遵守三條規則：

1. **不覆寫** —— 新舊都有時兩份都留著，回報 `Conflict`，交給人決定。
   ⛔ 不比 mtime 猜「哪份比較新」：猜對了沒人知道，猜錯了設定就沒了，而兩者同形。
2. **不靜默** —— 搬了／衝突／失敗一定印出來。靜默搬檔比不搬更糟。
3. **四態不得同形** —— `NothingToDo` / `AlreadyMigrated` / `Moved` / `Conflict` / `Failed`
   的後續動作完全不同。

### 兩類東西不走 migration，理由要寫出來

| 不搬的 | 為什麼 |
|---|---|
| `runtime/` 那幾格 | 判準的答案是「完全無感」，重生成本是零。而 `_process_registry` **更該重生而不是搬** —— 裡面是活著的 PID，搬一份舊的過去等於把早就死掉的進程當成還活著；那比沒有註冊表危險，因為它會回答問題，只是答錯 |
| `senate.local.example.json` | **它入版控，git 自己會搬。** 在 migration 裡多寫一條 ＝ 同一個檔有兩個搬運工 |

## .gitignore 的形狀

用「**先全擋、再放行樣板**」，不逐檔列舉：

```gitignore
/SenateData/
!/SenateData/config/
!/SenateData/config/senate.local.example.json
```

⚠ 逐檔列舉的話，日後新增一個帶機器路徑的檔會**預設入版控** ——
而那件事不會有人發現，它長得就像一筆正常的 diff。
（機器路徑進版控的代價見 [`SCP 撰寫規範 §3.4`](../../SCP_Core/Docs~/Coding_Standards.md)：
下一台機器 clone 下來會拿到「看起來設定好了、但指向不存在的磁碟」。）

驗 ignore 規則時**問具體檔案，不要問目錄**：

```bash
git check-ignore -v SenateData/config/senate.local.json          # 該被擋
git check-ignore -v SenateData/config/senate.local.example.json  # 該放行
```

## repo 根還留著什麼

| 留在根 | 為什麼不收進來 |
|---|---|
| `src/` `SCP_Core/` `Docs/` | 原始碼與文件，不是執行期資料 |
| `build.sh` `build.ps1` `install.*` `Senate.slnx` | 建置與安裝入口，人要找得到 |
| `build/` `publish/` | **產物**，跟資料是兩回事（產物可以整個刪掉重建，資料不行）。執行檔 `publish/senate.exe` 住這裡 |
| `senate.lnk` | 一鍵 build 放的雙擊捷徑，**只服務滑鼠**（PATH 掛的是 `publish/`） |
| `.claude/` `.codex/` `.agents/` | 那是 agent 工具的家，不是 Senate 的 |

## 相關

- [`Config_Schema`](../API/Config_Schema.md) — `senate.local.json` 的欄位規格
- [`Setup_And_Build`](../Workflows/Setup_And_Build.md) — 一鍵配置與出廠驗收
- [`SCP 撰寫規範`](../../SCP_Core/Docs~/Coding_Standards.md) — §3 設定層、§3.4 機器路徑、§4 路徑單一決定點
