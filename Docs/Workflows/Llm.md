---
title: AI 模型（ollama）—— 本地大語言模型的狀態、目錄、下載／移除、顯存卸載、試跑
description: senate cmd llm 與後台「AI 模型」頁怎麼用：ollama 本體與服務狀態、模型目錄與顯存門檻、下載／移除（不帶 confirm 只印計畫）、從顯存卸載、試跑與紀錄；為什麼 ollama 不走安裝系統
cmds: [llm]
last_updated: 2026-10-07
target_audience: [AI_Agent, Tools_Maintainer]
---

# AI 模型（ollama，TASK-0383）

> 一句話：**模型是 ollama 的，我們只管兩件 ollama 沒有的事 —— 目錄（這個專案挑過哪幾顆、要多少顯存）與結構化輸出。**
> 酒保（TASK-0365）用的也是這裡的 `Cmd_Llm.Chat` —— 頂欄「酒保設定」跳到酒保頁，見 [`Bartender`](Bartender.md)。

## 1. 常用

```bash
senate cmd llm                                              # 狀態：ollama／服務／已安裝／顯存裡的／目錄×門檻
senate cmd llm --arg op=test --arg model=qwen3:0.6b         # 試跑一句
senate cmd llm --arg op=install --arg model=qwen3:1.7b      # 只印計畫；同意了再加 --arg confirm=1
senate cmd llm --arg op=uninstall --arg model=qwen3:1.7b    # 同上
senate cmd llm --arg op=ps                                  # 顯存裡有什麼（跟「磁碟上有什麼」是兩件事）
senate cmd llm --arg op=stop --arg model=qwen3:4b           # 從顯存卸載
```

後台：「工具 › AI 模型」頁（page key `llm`），走同一支 Cmd。

### 上下文容量沿用 Ollama

AI 模型試跑與酒保共用 `Cmd_Llm.Chat`，只設定生成上限 `num_predict`，不送 `num_ctx`。上下文容量由 Ollama 管理；模型自身的 `num_ctx` 可以覆蓋全域預設。Senate 不另存一份 context，也不改 Ollama App 的滑桿設定。

「載入顯存中」表格的 **實際 context（tokens）** 取自最近一次 `ollama ps` 的 CONTEXT 欄。CLI `op=status`／`op=ps` 的 JSON 同樣包含 `loaded[].context_length`；缺欄位時為 null，畫面顯示未知。模型尚未載入就沒有實際讀數，不以全域預設或模型 metadata 的上限代替。試跑後自動重新量狀態；外部調整設定後按「重新量狀態」查看新載入結果。

`num_predict` 限制輸出長度，與上下文容量不同。顯存門檻只是篩選條件；實際顯存需求還受模型權重、量化、context 與 CPU/GPU 分配影響。Ollama 預設與模型 override 的行為見 [官方上下文文件](https://docs.ollama.com/context-length)。

## 2. 為什麼不走安裝系統（Tim 2026-10-05 拍板）

- 模型的真實狀態住在 **ollama 服務**裡：AI 模型頁、CLI、酒保讀的都是同一份 `ollama list` ⇒ 不需要另一層帳。
- 它跟安裝系統管的東西不同種：pip 套件與 HF 模型是「我們下載、我們放進資料夾」；ollama 自己下載、自己存。
- ollama **本體**也不代裝：官方 Windows 安裝是下載並執行遠端腳本（`irm https://ollama.com/install.ps1 | iex`），頁面只給下載頁與指令的複製鈕。
- 代價：skill 的 `requires_install` 管不到 ollama 模型；缺模型時由本頁／本 Cmd 提示。

## 3. 三個「不得同形」

| 狀況 | 畫成 | ⛔ 不可以畫成 |
|---|---|---|
| 找不到 ollama／服務打不到 | 已安裝＝「不知道」，Cmd exit 4 | 每顆都「未安裝」、0 個 |
| 顯存偵測失敗（沒有 nvidia-smi） | 門檻來源＝保底值（不是量到的） | 一個看起來像量到的數字 |
| 試跑撞到生成上限 | 失敗＋被截斷（不管切在思考段還是回答段） | 成功＋半句話 |

`format=json` 裡 `installed_known`／`loaded_known` 為 false 時，`installed`／`loaded` 是 `null`（不是空陣列）；`installed_count` 這個機讀值給空字串。

## 4. 目錄與顯存門檻

- 目錄在 `Cmd_Llm.cs` 的 `LlmOllama.Catalog`。每顆兩個數字：**下載量**（磁碟）與**顯存約**（含 KV cache 與執行期開銷）——不要混用。
- 門檻優先序：手動（`vram_budget=`）＞ 偵測（nvidia-smi 的 free，或 `vram_basis=total`）＞ 保底 6 GB。只影響目錄預設列不列，不影響能不能下載。
- 顯存不夠時 ollama **不報錯**，只把層數丟給 CPU ⇒ `op=ps` 的「跑在哪」不是 100% GPU 就是這個。
- 「裝了沒」的比對：精確，或同家族且 tag 是「這個 tag」或「這個 tag-變體」（`qwen3:4b-instruct-q4_K_M` 算 `qwen3:4b` 的變體）。
  ⚠ 只認 tag 開頭（不是「tag 出現在名稱裡」—— 那樣裝了 `qwen3:14b` 會把 `qwen3:4b` 判成已裝）。沒寫 tag 的（`phi4-mini`）ollama 記成 `:latest`。

## 5. 試跑

- 走 HTTP API（`127.0.0.1:11434/api/chat`），拿得回逾時、生成上限、思考段。第一次會把模型載進顯存（冷啟動可能幾十秒）。
- 逾時不代表它死了：thinking 模型可能還在想 ⇒ `op=ps` 看它在不在顯存、`op=stop` 卸載。殺掉發問的那一方**不會**讓模型離開顯存。
- 頁面上下載與試跑都能「中斷」：下載 ＝ 殺掉 `ollama pull`（ollama 保留已下載的部分，下次接著下）；試跑 ＝ 關掉連線（ollama 才會停止生成 —— 只按 `stop` 的話它會等這次請求跑完才卸載）。下載不設時間上限，要停就按中斷。
- 頁面的試跑預設 `keep_alive=120`（用完兩分鐘卸載）；CLI 預設 -1（ollama 自己的 5 分鐘）。

## 5.5 頁面設定（Tim 2026-10-05）

- 顯存門檻（依據／手動值／只列放得下的）與試跑參數，**按頂欄「存檔設定」才寫檔，⛔ 不自動存**；頂欄旁標「有未存的修改／與存檔相同」。
- 存在 `SenateData/config/llm_page.json`（本機設定，不入版控）。沒存過 ⇒ 用初始值：`qwen3:0.6b`／「跟剛進門的客人打個招呼」／system「你是傲嬌的貓娘」／開思考段／上限 4096／卸載 120 秒／等 60 秒。
- 設定檔讀不了 ⇒ 用初始值，但頁面明講「讀不了、按存檔會覆蓋它」（⛔ 不說成「還沒存過」）；舊檔少欄位逐格補初始值。
- 只是頁面的初值 —— CLI `senate cmd llm --arg op=test` 的預設不變（120 token、不開思考段）。
- 每次試跑 append 一行到 `<資料根>/LLMAdmin/test_log.jsonl`（欄位：`ok`／`model`／`prompt`／`seconds`／`eval_count`／`tokens_per_sec`／`output`／`thinking`／`note`／`error`／`ts`／`system`）；寫不進去只警告、不擋。

## 6. exit code

0 成功｜1 擋下｜2 用法錯（例如沒給 model）｜4 量不到（找不到 ollama／服務打不到）｜5 動手了但沒成功（含試跑被截斷）。

## 7. 驗證

- `senate selftest --only llm`：表格解析（ps 多了 CONTEXT 欄也不讀錯）、目錄比對（含 14b 不算 4b）、截斷判定、Cmd ↔ 頁面「量不到 ≠ 0 個」的約定。
