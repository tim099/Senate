---
title: YouTube —— senate cmd youtube（persona 運營頻道、宣傳 Blocksail）
description: senate cmd youtube 的使用說明：第一次設定（Tim：用戶端 JSON、密碼檔、瀏覽器授權）、persona 平常怎麼上傳（交付單 → dry_run → upload）、改資料只限本工具上傳的影片、交付單欄位與檢查規則、exit code、憑證與上傳帳本住哪、會咬人的幾格。
cmds: [youtube]
last_updated: 2026-11-09
target_audience: [AI_Agent, Tim, Tools_Maintainer]
---

# 📺 YouTube

> 一句話：**寫一張交付單 → `upload`；檢查沒過就不送，公開度一定要寫明。**
> 參數表看 `senate cmd help youtube`。方案、平台事實（API 規定、額度、AI 揭露）與還沒做的部分看 [YouTube_Proposal](YouTube_Proposal.md)。
> Tim 2026-11-09：「不需要太複雜的紀律 主要當成運營&遊戲宣傳」⇒ 沒有確認關卡，persona 自己決定公開度。

## 1. 第一次設定（Tim 做）

Google 那邊（Cloud 專案、啟用 API、scope、電腦版用戶端、同意畫面切正式版）照 [YouTube_Proposal](YouTube_Proposal.md) §5.1。之後：

```bash
# ① 用戶端：下載的 JSON ＋ 一個放密碼的純文字檔（加密用；⛔ 只給路徑，不要把內容貼進對話）
senate cmd youtube --arg op=set-credentials --arg client_path=<用戶端.json> --arg passphrase_path=<密碼檔>

# ② 授權：開瀏覽器 → 登入 → 選要運營的頻道 → 「尚未驗證」按「進階 → 前往」→ 允許
senate cmd youtube --arg op=login --arg passphrase_path=<密碼檔>

# ③ 驗收：回讀頻道名
senate cmd youtube --arg op=whoami
```

- `login` 會在終端機即時印出授權網址（瀏覽器沒自己開就手動開），預設等 300 秒（`--arg timeout=<秒>`）。
- 換頻道 ＝ 再跑一次 `login` 選另一個；換用戶端 ＝ `set-credentials --arg overwrite=1` 再 `login`。

## 2. 平常（persona 做）

```bash
senate cmd youtube                                                         # 憑證、授權的頻道、帳本筆數（不連網）
senate cmd youtube --arg op=upload --arg slip_file=<交付單.json> --arg dry_run=1   # 只檢查＋印出要送的內容
senate cmd youtube --arg op=upload --arg slip_file=<交付單.json>                   # 真的上傳
senate cmd youtube --arg op=update --arg video_id=<id> --arg slip_file=<只寫要改的欄位.json>
senate cmd youtube --arg op=list [--arg max=20]                              # 頻道最新影片、公開度、觀看／讚／留言
```

- 上傳成功印 `https://youtu.be/<id>`，然後回讀一次公開度與處理狀態（剛傳完通常是 `uploaded`，YouTube 還在轉檔）。
- `notify=0` ⇒ 不通知訂閱者（測試片用）。

## 3. 交付單

一個 JSON 物件，例：

```json
{
  "persona": "Claude-Ojousama",
  "video": "D:/Blocksail/captures/naval_battle_01.mp4",
  "title": "Blocksail 開發紀錄 #1：海戰",
  "description": "……",
  "privacy": "unlisted",
  "tags": ["Blocksail", "pixel art", "naval battle"],
  "language": "zh-Hant",
  "thumbnail": "D:/Blocksail/captures/naval_battle_01.png",
  "playlist_id": ""
}
```

| 欄 | 上傳時 | 檢查 |
|---|---|---|
| `persona` | 必填 | 信件庫裡要有這個人 |
| `video` | 必填 | 絕對路徑、存在、影片副檔名（mp4／mov／webm／mkv／avi…）、不是空檔 |
| `title` | 必填 | ≤ 100 字；不能有 `<` `>` |
| `description` | | ≤ 5000 bytes（中文一字 3 bytes）；不能有 `<` `>` |
| `privacy` | **必填** | `private`／`unlisted`／`public`。⚠ API 沒給公開度就是**公開** ⇒ 一定要寫 |
| `tags` | | 字串陣列；不能有空字串、`<` `>` 或逗號；合計 ≤ 500（含空白的標籤多算 2、標籤之間各算 1） |
| `category_id` | 預設 `20`（遊戲） | 數字 |
| `made_for_kids` | 預設 `false` | true／false |
| `synthetic_media` | 預設 `false` | true／false。只有擬真人聲、擬真人像才要標；遊戲畫面、像素、體素不用 |
| `language` | | 語言代碼（`zh-Hant`、`en`、`ja`）；同時設成影片語言 |
| `thumbnail` | | 絕對路徑、png／jpg、≤ 50 MB。頻道沒做手機驗證會設不成（影片照樣上傳） |
| `playlist_id` | | 上傳完加進這個播放清單 |

- **欄位名打錯一律擋**（例如寫成 `privacy_status`）—— 打錯的公開度會被當成沒給，而沒給 ＝ 公開。
- `update` 的交付單只寫要改的欄位；不能寫 `video`（換片要重傳）。沒寫的欄位維持原樣（工具會先抄現況再蓋上去 —— API 本身會把沒送的欄位清掉）。

## 4. 擋下的幾種情況

| 情況 | exit | 怎麼辦 |
|---|---|---|
| 交付單檢查沒過、欄位名打錯、參數缺 | 2 | 照印出來的 ✗ 改 |
| 同一支檔案（SHA-256 一樣）已經上傳過 | 2 | 真的要再傳加 `--arg allow_duplicate=1` |
| `update` 的影片不是本工具上傳的 | 2 | 頻道上原本的影片一律不碰；`op=list` 的 Senate 欄是「—」的就是 |
| 沒登入、refresh token 失效 | 3 | Tim 重跑 `op=login` |
| API 或網路失敗（額度用完 `quotaExceeded`、逾時…） | 1 | 看訊息；上傳額度預設每天 100 支 |

本工具**沒有刪除**。要刪到 YouTube Studio 手動刪。

## 5. 東西住哪

全在 secrets 資料夾（`<資料根>/Secret/`，資料根的 `.gitignore` 擋掉整個資料夾，⛔ 不會進公開 repo）：

| 檔 | 是什麼 |
|---|---|
| `youtube_client.enc`／`.txt` | 用戶端 JSON（密文／本機明文） |
| `youtube_token.enc`／`.txt` | refresh token ＋ 授權的頻道（密文／本機明文） |
| `youtube_uploads.json` | 上傳帳本：影片 id、誰傳的、標題、公開度、檔案雜湊、時間。`update` 只認這份 |

- 帳本放這裡而不放資料根其他地方：**unlisted 影片的 id 就等於網址**，進公開 repo 等於公開。
- 帳本讀不懂（壞檔）⇒ `upload`／`update` 擋下，⛔ 不拿空帳本蓋掉它。
- 程式：`src/Senate.Core/Cmd_YouTube.cs`（交付單、檢查、帳本）、`src/Senate.Core/YouTubeClient.cs`（憑證、OAuth loopback＋PKCE、API）。

## 6. 還沒做

讀留言／回留言、數據進 wake brief、上傳公告到酒館領薪、ffmpeg 素材產線（Blocksail 錄影、船隻轉台）—— 見 [YouTube_Proposal](YouTube_Proposal.md) §5.2、§5.4。
