---
title: 操作 Unity 專案（Unity CLI）
description: 重編譯、讀編譯錯誤、確認 Editor 活著 —— 一律走官方 Unity CLI（`unity command …`）；senate ucmd 已廢棄
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🎮 操作 Unity 專案（Unity CLI）

> 一句話：**要碰 Unity Editor 就用官方 Unity CLI**（`unity command <指令> --project-path <專案根>`）。
> ⛔ `senate ucmd`（AgentCommand 佇列）已廢棄。
> 不需要 Unity 的事一律走 [`senate cmd`](SCP_Cmd_System.md)。

## 前提

- 本機裝了 CLI（`unity --version`）；目標專案裝了 `com.unity.pipeline`（看 `Packages/manifest.json`）。
- **Editor 要開著** —— CLI 經 Pipeline 的 7800 埠問正在跑的 Editor，⛔ 它不是 headless。
- `--project-path` 是 Unity 專案的 repo 根：`senate cmd paths` 的 `UnityProjectRoot` 那一格（`senate.local.json` 的 `projects[].root`）。
- 每次呼叫約 1～1.5 s 啟動成本 ⇒ ⛔ 不放進每次刷新都跑的路徑。
- 機器讀一律帶 `--json --no-banner`；結果在 `data.result`，CLI 自己的錯誤在頂層 `errors[]`。

## 重編譯並拿到這一趟的錯誤

```bash
P=--project-path=<Unity 專案根>
unity --json --no-banner command console_status $P      # ① 記下 data.result.cursor（基準）
unity --json --no-banner command recompile $P           # ② 觸發（Editor 失焦／最小化也會編）
unity --json --no-banner command recompile_status $P    # ③ 每秒輪詢到 completed 或 up_to_date
unity --json --no-banner command console $P --level error --since <cursor>   # ④ 只取這一趟的錯
```

- ② 回 `compiling` ＝ 開始編；回 `up_to_date` ＝ **沒有檔需要重編**（不是「編過而且綠」）。
- ③ 的狀態：`idle`／`triggered`／`compiling`／`completed`／`up_to_date`。
- **編譯有沒有錯看 `recompile_status` 的 `compilationFailed`。**
  ⚠ 同一份回傳裡的 `failed` 與 `errors[]` **不是編譯錯誤**：編不過時它們照樣是 `false`／`[]`。
- 錯誤明細只在 ④：`logType=Error`、`message` 形如 `Assets\…\X.cs(1,8): error CS1029: …`。
  console 會混進別的例外（例如 Odin 的 NullReferenceException）⇒ 只數 `error CS` 開頭的那段。
- ⛔ 不帶 `--since` 會讀到上一趟的錯；`--tail 0` 是**不限筆數**，不是 0 筆。
- ⛔ 頂層的 `unity recompile`（不是 `unity command recompile`）在 Editor 失焦時回 `up_to_date` 而不編，別用。

## Editor 活著嗎

```bash
unity --json --no-banner command editor_status $P --timeout 3
```

- 回 `status: ready`、`compiling`、`domainReloadInProgress`、`lastHeartbeat`。
- 它經過 Editor 主執行緒 ⇒ **主執行緒凍住時它也卡住**，所以要帶逾時：逾時＝卡住，不是「沒開」。
- Editor 沒開（或專案沒裝 Pipeline）：exit **6**，`errors[0].message` 是 `No Pipeline instance found for project …`。
- ⛔ `unity status` 在主執行緒凍住時照樣回 ready，不能當存活讀數。

## 其他常用

| 要做的事 | 指令 |
|---|---|
| 讀 Console（游標、等級篩選） | `console`／`console_status`（`groundTruth.compilationFailed` 與 recompile_status 同一個判準） |
| 跑一段 C# | `eval`／`eval_file`（任意程式碼執行，有權限閘） |
| 場景階層 | `get_scene_hierarchy`、`find_gameobjects`、`list_open_scenes` |
| 執行選單項 | `menu` |
| 截圖 | `capture_game_view`、`capture_scene_view`、`screenshot` |
| 跑測試 | 頂層 `unity test` |

完整清單：`unity command $P --detail compact`；單支：`unity command $P --query <名字>`。
