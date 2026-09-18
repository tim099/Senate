---
title: 券系統（Voucher）— 資料格式與規矩
description: 券＝以 id 區分的貨幣，一種券一個檔，綁 persona、跨區共用、由 Server 單一寫入、不記歷史
last_updated: 2026-09-18
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🎟 券系統 — 資料格式與規矩

> **一句話**：券＝**以 id 區分的貨幣**。一種券一個檔，檔名就是券 id，
> 綁 persona、跨區共用、**由 Senate Server 單一寫入**、**不記歷史**。

拍板：Tim 2026-09-18（TASK-0243）。

---

## 1. 它住哪

```
<letters_root>/<persona>/vouchers/<券 id>.json
```

- **`letters_root` 是參數，⛔ 不推導**。券是跨專案共用的東西，
  推導那一格等於讓「券住哪」跟著呼叫它的專案漂。
- **券 id ＝ 檔名** ⇒ 先過 `SCP_LettersPaths.IsValidVoucherName`：
  含 `/`、`\`、`..` 或檔名非法字元一律擋下。
  ⚠ 不擋的話它會**憑空長出一個資料夾而不報錯**。
- ⛔ **沒有區（region）的維度** —— 券跨區共用。區只出現在「誰動過它」（`updated_region`）
  與「哪一區遷過了」（`migrated_regions`）兩個欄位裡。

現有券 id：

| 券 id | 用途 | 狀態 |
|---|---|---|
| `canvas` | 繪圖券（畫布放點、雕刻） | ✅ 已遷入本系統（2026-09-18） |
| （酒館券） | 自由時間券 | ⛔ **還沒遷** —— 仍在 `ChatTavern/agent_bonus_quota.json`，由 Editor 寫 |

---

## 2. 檔案長這樣（真檔案，⛔ 不是設計稿）

```json
{
	"persona": "Template",
	"voucher": "canvas",
	"permanent": 5,
	"expiring": [
		{
			"amount": 4,
			"granted": 7,
			"expires_at_utc": "2026-09-18T05:59:51.000Z",
			"granted_at_utc": "2026-09-18T03:59:51.7Z",
			"source": "freetime",
			"ref": "ft-20260918T032633Z-basecamp"
		}
	],
	"updated_at_utc": "2026-09-18T04:00:09.7072273Z",
	"updated_region": "Florin",
	"migrated_regions": [],
	"schema_version": 1
}
```

| 欄位 | 意思 | ⚠ |
|---|---|---|
| `permanent` | 永久券張數 | 純量，⛔ 不是批次 |
| `expiring[]` | 限時券，**可以有多筆**，各自到期 | 一次 `grant` ＝ 一筆 |
| `expiring[].amount` | 這一批**還剩**幾張 | 會被消費扣減 |
| `expiring[].granted` | 這一批**當初發了**幾張 | 🩸 **發放後永不變動** —— 見 §5 |
| `updated_at_utc` / `updated_region` | 誰動過它 | 券不記歷史 ⇒ 這是**唯一**的線索 |
| `migrated_regions[]` | 哪幾區已經遷過 | 冪等鍵，見 §6 |

**三個數字，三個問題**（⛔ 沒有一個叫「餘額」的單數）：

```
可花總額 = permanent + （未過期的 expiring[].amount 之和）
```

⚠ 可花總額**不是任何一批的餘額**。一個 `GetBalance` 同時回答「他存了多少」與
「這筆付得起嗎」＝ 改對一邊等於默默弄錯另一邊，**而被弄錯的那邊不會喊**。

---

## 3. 誰能寫 —— **只有 Server**

`senate cmd voucher` 是 `ServerDelegateCmd` ⇒ 一律在常駐 Server 裡跑。

```bash
senate cmd voucher --arg op=balance|list|usage|grant|consume|migrate \
    --arg letters_root=<絕對路徑> --arg persona=<誰> --arg voucher=<券 id> \
    [--arg amount=<正整數>] [--arg region=<區>] [--arg expires_at=<ISO8601>] \
    [--arg source=<為什麼>] [--arg ref=<指回現場>]
```

- **寫入一律要 `region`** —— 券不記歷史，`updated_region` 是唯一的「誰動過它」。
- Unity 那側走 `UCL_VoucherAuthority`（`Assets/.../UCL_AgentCommands/Voucher/`），
  它把每一筆讀寫都派給這支 Cmd。⛔ Unity **不碰券檔**。
- python 那側走 `Cmd_CanvasVoucher`（Editor），而它現在也是薄殼 ⇒ 同樣落到 Server。

> 🩸 **為什麼「單一寫入端」不是偏好，是這個設計成立的唯一前提**：
> 券存的是**狀態不是事件**。兩個寫入端互相覆蓋之後，留下的是一個完全合法的數字，
> **而沒有歷史可以回推**。

---

## 4. 到期怎麼算 —— **讀取端過濾，寫入端才清**

- 讀：到期的批次**不計入**可花總額（餘額自動少掉）。
- 寫：下一次寫入時才把到期批次清掉（`dropped` 會回報幾筆）。

⇒ 所以「過期了」與「被清掉了」是兩個時刻。⛔ 不要在讀取端刪東西 ——
那會讓一次單純的查詢變成一次寫入，而查詢失敗時沒有人會去看。

**消費順序：先花快過期的**（限時券按到期時刻升冪 → 永久券）。
⚠ 反過來的話限時券會在永久券的陰影下爛掉，而使用者看到的只是「我的券變少了」。

---

## 5. `granted` 為什麼要存 —— 「用了幾張」在沒有歷史的檔上答不出來

只有 `amount` 的話：剩 3 張時，**「發 10 用 7」與「發 3 用 0」逐位元組相同**。

```bash
senate cmd voucher --arg op=usage --arg persona=<誰> --arg voucher=<券 id> --arg ref=<那一場>
#   🔢 found / granted / remain / alive / used
```

🩸 **`found=0` 時五個數字全部是 0，⛔ 不回一個「真實的發放量」**。
回了的話呼叫端只要做一次「發放量 − 0」就會印出「全數用畢」——
那是 TASK-0195 那隻病（**查無被算成用完**），而兩者在畫面上一模一樣。
⇒ 讓那個減法**在物理上拿不到數字**。

> ⚠ **已知代價**：券不記歷史 ⇒ 批次過期被清掉之後，`op=usage` 只答得出 `found=0`
> （＝「我不知道」，⛔ 不是「沒用」）。這是「不留歷史」這個拍板的代價，**不是漏掉的一格**。

---

## 6. 分區遷移 —— 冪等鍵是**區名**

```bash
senate cmd voucher --arg op=migrate --arg persona=<誰> --arg voucher=<券 id> \
    --arg region=<來源區> --arg from_path=<舊檔>
```

- 同一區跑第二次 ⇒ 說「這一區已經遷過」且**數量一格未變**（⛔ 不是靜默成功）。
- ⚠ **標記要在加總之後跟著同一次寫入落盤** —— 先寫標記再加總的話，
  中途失敗留下的是「標記說遷過了、而券一張都沒加」，**而它之後永遠不會再跑一次**。
- ⚠ 遷移取舊檔的 `remain` **不是** `amount` —— 拿 `amount` 遷＝把已經花掉的券再發一次，
  而它是一個完全合法的數字。

跨區共用靠的是**營運前提**（兩個專案接力跑，同時只有一個區在動），
⛔ **不是機械**。這一格今天沒有守衛。

---

## 7. 🩸 這套東西是為了修哪一隻病（2026-09-18 的讀數）

券的**消費端**先搬到新系統、而**發放端**留在 Unity 舊帳本
⇒ 每場自由時間發的 10 張限時券花不到、到期原地作廢，而舊帳面上它看起來還在。

| persona | 舊帳（可花） | 新帳（可花） | 差 | 那天死掉的限時券 |
|---|---:|---:|---:|---:|
| Sirius | 113 | 103 | 10 | 10 |
| apex-one | 99 | 89 | 10 | 10 |
| basecamp | 67 | 57 | 10 | 10 |
| calli | 9 | 0 | 9 | 10 |

⛔ **沒有任何一層會喊** —— 兩邊都是合法數字。
⇒ 修法不是「把發放端也改掉」（那只是把同一個賭注再押一次），
是**讓 Unity 那側不存在第二個寫入端**。

📌 對照組：meadow（有 10 張死券）與 summit（3 張）當天沒有花券 ⇒ **零差額**，
這是自然的陰性對照，不是我造的。

---

## 8. 還沒做的（⛔ 不要讀成已完成）

- **酒館券未遷** —— 仍在 `ChatTavern/agent_bonus_quota.json`，由 Editor 直寫，
  且 key 是 (bank, persona) 不是 persona。
  ⚠ Tim 的構想是「酒館券當 Token ＝ 個人錢包，跟銀行帳戶的 Token 相等」——
  **「相等」有兩種讀法，處置相反**：
  ① 券**就是**帳戶餘額的別名（沒有第二本帳）／② 券是另一本帳、面額等於 1 Token。
  🩸 **②正是本文 §7 那隻病的形狀。** 這一格等拍板，⛔ 不要自己挑一個開始寫。
- **舊繪圖券檔未刪** —— `<資料根>/Canvas/vouchers/<persona>.json` 已經沒有寫入端，
  但**故意留著**當對帳基準（TASK-0243 ⑩ / TASK-0242）。
- **中斷安全（⑨）零讀數**、**跨區實測（⑦）等 BTC 區**。
