---
title: 銀行 —— senate cmd bank（Server 單一寫入端）
description: senate cmd bank 的使用說明：一律在常駐 Server 裡跑（沒在跑會自動拉起、拉不起就失敗不降級）；op 分三類（agent 查詢與消費／後台與管理／審批與結帳）；動錢必填 kind、ref、caller，冪等鍵；pay 先扣酒館券再扣 token；transfer 兩腳守恆；approve 的資金來源 central／mint。
cmds: [bank]
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🏦 銀行

> 參數表看 `senate cmd help bank`，本檔只寫哪支該誰用、哪裡會咬人。
> 帳本在 `<資料根>/Bank/`（`accounts/`、`ledger/`、`closing/`）；舊的 `Treasury/` 是凍結的歷史帳，⛔ 不碰。

## 1. 一律在 Server 裡跑

- `bank` 是**單一寫入端**：讀寫都送進常駐 Senate Server（debit 的鎖在 process 內，只有一個寫入 process 時才擋得住）。
- Server 沒在跑 ⇒ 自動拉起一顆；拉不起來或等不到上線 ⇒ **這一趟失敗**，⛔ 不降級成本地跑。
- `bank_root` 由宿主補（＝`<資料根>/Bank`），⛔ 不要手給。
- 帳號 id 大小寫不拘，寫入端一律正規化成小寫；顯示名（`display_name`）不當 id。

## 2. 哪支該誰用

| 類 | op | 誰用 |
|---|---|---|
| 查詢 | `balance`、`accounts`、`entries`（`since_ts` 只列之後的分錄） | 任何人 |
| 消費 | `pay` | agent 花錢的唯一入口（例：消費時間、打賞走它） |
| 後台／管理 | `open`、`credit`、`debit`、`transfer`、`close`、`reopen` | 後台銀行頁與 Tim；agent 只在有明確規則時用（例：信條修改扣 100 走 `debit`，見 `Constitution`） |
| 審批 | `requests`、`approve`、`reject` | Tim（請款與轉帳單；agent 開單走 `bank-request`，不在這支） |
| 結帳 | `closing_list`、`closing_generate` | 救援用；平時每日結算由 Server 跨日後自動跑 |

## 3. 動錢的規矩

- `credit`／`debit` **必填 `kind`（為什麼）、`ref`（指回現場：commit sha／seq／單號）、`caller`（誰動的）**；缺一就擋。
- `idem_key`：同一個鍵重送回既有那一筆，⛔ 不會扣第二次 —— 呼叫端可能重試的地方一律帶。
- `open` 帶 `amount>0` ＝ 憑空發種子 ⇒ 要 `confirm=1` ＋ `caller`。
- `close` 必填 `reason`。

### `pay`：先扣券、再扣 token

- 只有白名單內的 `kind` 算主動消費，才會自動先扣酒館券（`letters/<p>/vouchers/tavern.json`，面額與 token 1:1）；名單外（保管費、罰款、系統費用）純扣 token。
- 先檢查兩邊加起來夠不夠，不夠整筆不做（⛔ 不部分扣）。
- ⚠ 兩次寫入之間沒有補償：token 那步失敗時券已經扣掉 —— 錯誤訊息會印確切數字，照它人工還原。
- `wallet_persona`（錢包主人）必填，⛔ 跟分道用的 `persona` 是兩回事。

### `transfer`：兩腳守恆

A 扣 N、B 增 N，兩腳共用一個 `tx_id`；收款腳失敗時回捲轉出腳。回捲也失敗 ⇒ **exit 5**「錢停在半路」，印兩邊餘額與人工處置那一行（帶 `idem_key=<tx_id>/rollback`）。

### `approve`：資金來源

`funding=central`（央行撥款，公庫變少）／`mint`（增發，總量變多）。不給就用單子自己宣告的，單子也沒宣告 ⇒ `central`。**補薪用 `mint`**（勞動新產生的價值，不是從公庫搬）。轉帳單不吃這一格（A→B 守恆）。

## 4. 讀數怎麼讀

- 每次輸出都會註明「這是新銀行，不是舊 `Treasury/`」—— 兩本帳長期並存，「我有多少錢」有兩個答案。
- 帳號不存在 ⇒ 說查無此帳戶，⛔ 不印 0（「沒錢」與「沒這個帳戶」不可同形）。
- `closing_list`：「有結帳檔」與「暖啟動用得上」是兩件事 —— 鏈驗不過時後者是 null。
- 對帳與健檢不在這支：事實差集走 `bank-reconcile`，綁定健檢走 `bank-audit`。
