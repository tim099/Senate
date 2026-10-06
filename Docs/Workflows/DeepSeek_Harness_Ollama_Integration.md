---
title: DeepSeek Harness 接入本地 Ollama 模型
description: 將 LlmModelPage 管理的 Ollama 模型接入 DSH，包含設定、查驗、故障排查與 LY 換機驗收。
last_updated: 2026-10-06
target_audience: [AI_Agent, Tools_Maintainer, Tim]
aliases: [DSH, DeepSeek Harness, Ollama, LlmModelPage, 本地模型]
---

# DeepSeek Harness 接入本地 Ollama 模型

DSH 可透過 Ollama 的 OpenAI 相容 API，使用 Senate `LlmModelPage` 管理的已安裝模型。模型權重由 Ollama 管理；DSH 負責自己的 provider 與對話設定。Senate 頁面的 prompt、system、think、num_predict 與測試紀錄不會自動同步到 DSH。

相關文件：[DSH 本地部署與啟動](DeepSeek_Harness_Local_Deployment.md)。本流程供 **TASK-0442 — LY 區域部署 DeepSeek Harness** 的模型接入驗收使用。

## 1. 前置條件與查驗

先完成 DSH 部署，雙擊 checkout 內的 `start-dsh.bat` 開啟瀏覽器。另確認同一台電腦上的 Ollama 已啟動，且模型管理頁顯示目標模型已安裝。

以下 PowerShell 指令只讀取 Ollama 狀態與模型列表，不下載、載入或刪除模型。完整模型 ID（含 tag）須用於後續 DSH 設定。

```powershell
senate cmd llm --arg op=status --arg format=json
Invoke-RestMethod -Uri 'http://127.0.0.1:11434/api/tags'
Invoke-RestMethod -Uri 'http://127.0.0.1:11434/v1/models'
```

**本機已驗證讀數（2026-10-06）**：Ollama 0.35.1 可連線，已安裝 `qwen3:4b`（Q4_K_M，約 2.5 GB），原生與 OpenAI 相容模型列表均列出同一 ID；查驗當時無載入中的模型。這是 Bar 電腦的讀數，LY 電腦須重新查驗。

**尚未驗證**：DSH provider 保存、實際模型回答、thinking 行為與工具呼叫。模型列表可讀不能替代推理驗收。

## 2. 在 DSH 添加本地模型

1. 進入 **設定 → 模型 → 添加模型提供商 → 自定義模型 API**。
2. 依下表填寫 provider；ID 使用小寫與連字號。
3. 點「取得可用模型」並加入目標模型；若探測失敗，可依 `/v1/models` 的結果手動加入完整 ID。
4. 保存設定。
5. 在對話的模型選單選取這個 provider 下的模型，**新建對話**再測試。

| 欄位 | Bar 本機填法 |
|---|---|
| 提供商 ID | `local-ollama` |
| 顯示名稱 | `本地 Ollama` |
| API 地址（Base URL） | `http://127.0.0.1:11434/v1` |
| API 協定 | `OpenAI Chat Completions`（`openai-completions`） |
| API 密鑰 | `ollama` |
| 模型 ID | `qwen3:4b` |
| 輸入類型 | `text` |

`ollama` 是提供給 client 的佔位字串，本地 Ollama 會忽略它，無須雲端 API key。Base URL 填到 `/v1`；DSH 會組成請求路徑。不要填 DSH 的 `3080`、Ollama 原生 `/api/chat`，或完整 `/v1/chat/completions`。

保存後的設定會在下一次請求套用，無需重啟 DSH。既有對話可能沿用原模型，因此要明確選取模型並新建對話。

### 2.1 容量與能力設定

上下文窗口依 Ollama 的實際運行配置填寫；模型 metadata 的最大容量不代表目前服務配置的容量。DSH 自訂模型未指定時的預設容量為 262,144／最大輸出 32,768，不能直接當成本機可用預算。Senate 的 `num_predict` 是生成上限，不是 context window。

本機 `qwen3:4b` 的能力列表包含 completion、tools、thinking，未列 vision，因此輸入類型填 `text`。Ollama 原生 API 的 `think` 與 DSH 的 reasoning 設定不會自動對應；工具能力宣告也不能替代 DSH 實際工具呼叫驗收。

### 2.2 設定保存位置

DSH 預設資料目錄是使用者家目錄下的 `.dsh`；若指定 `DSH_HOME`，以該設定為準。Web profile 的 provider 設定位於 `$DSH_HOME/profiles/web/cordis.patch.yml`，憑證由 `$DSH_HOME/.credentials.yaml` 管理。優先使用模型頁設定；手改 patch 時須保留同一 plugin 既有的其他 provider 與欄位。

## 3. 驗收與問題排查

先在新對話送出「請用一句繁體中文介紹你自己」，記錄所選 provider／模型、等待時間與實際回答。再另外測試工作區讀取等工具行為，確認 DSH 的工具循環能完成。

| 症狀 | 查驗與處理 |
|---|---|
| 連線遭拒或列表不可讀 | 確認 Ollama 已啟動；重新讀取 `/api/tags` 與 `/v1/models`，核對地址和 port。 |
| DSH 模型列表探測失敗 | 先確認 `/v1/models` 可讀；依該列表手動加入完整模型 ID。 |
| `model not found` | 核對完整 ID／tag；DSH 顯示名稱不能代替模型 ID。 |
| 缺憑證錯誤 | 在自訂 provider 保存 `ollama` 佔位 key。 |
| 保存後仍使用原模型 | 在對話模型選單重新選取 provider 下的模型，並新建對話。 |
| 回答慢、思考長或輸出被截斷 | 記錄回應與錯誤，核對實際容量、生成上限及 thinking 設定；與 Senate 原生測試結果分開記錄。 |
| API 拒絕請求格式 | 保留原始錯誤；參考 DSH 指南的 `compat.supportsDeveloperRole: false`／`compat.maxTokensField: max_tokens`。這是尚未在本機路由實測的備案。 |

## 4. LY 換機流程與紀錄

DSH 與 Ollama 在同一台 LY 電腦時，仍使用 `127.0.0.1:11434/v1`；重新查詢該電腦實際安裝的模型，再填寫完整 tag。`127.0.0.1` 指 DSH server 所在電腦，跨電腦時不會指向 Bar 的 Ollama。

- [ ] Ollama 服務與兩種模型列表 API 可讀，記錄版本、模型 ID 與量化。
- [ ] DSH 自訂 provider 已保存，Base URL、協定與模型 ID 正確。
- [ ] 已選取本地模型，新對話得到完整回答。
- [ ] thinking 與容量配置已記錄；若未測試，明確標示未驗。
- [ ] 工作區工具測試完成；若未測試，明確標示未驗。
- [ ] 將結果補回 TASK-0442 與本文件，保留症狀、錯誤、處理動作和修復後讀數。

驗收紀錄至少包含日期／電腦、Ollama 與 DSH 版本、provider ID／Base URL／模型 tag、實際上下文配置、測試問題／回答結果、工具結果及未解問題。不要將登入 token、cookie 或真實 API key 放入文件。

## 5. 依據

- Senate 模型管理頁：[LlmModelPage.cs](../../src/Senate.Cli/Pages/LlmModelPage.cs)。
- Senate Ollama 指令：[Cmd_Llm.cs](../../src/Senate.Core/Cmd_Llm.cs)。
- DSH checkout：[模型提供商指南](../../../deepseek-harness/docs/user/guide/providers.zh.md)。
- Ollama：[OpenAI 相容 API 官方文件](https://docs.ollama.com/api/openai-compatibility)。
