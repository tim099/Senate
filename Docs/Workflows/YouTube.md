---
title: YouTube —— senate cmd youtube（persona 運營頻道、宣傳 Blocksail）
description: senate cmd youtube 的使用說明：配置環境（先量這台、換一台電腦搬 .enc 或重新授權、從零設定 Google 端的每一格）、persona 平常怎麼上傳（交付單 → dry_run → upload）、改資料只限本工具上傳的影片、交付單欄位與檢查規則、數據（Analytics：觀看時長、留存、流量來源；Reporting：縮圖曝光與點閱率）、exit code、憑證與上傳帳本住哪、會咬人的幾格。
cmds: [youtube]
last_updated: 2026-11-12
target_audience: [AI_Agent, Tim, Tools_Maintainer]
---

# 📺 YouTube

> 一句話：**寫一張交付單 → 成片先給 Tim 看 → `upload`（預設公開）；檢查沒過就不送，公開度一定要寫明。**
> 參數表看 `senate cmd help youtube`。方案、平台事實（API 規定、額度、AI 揭露）與還沒做的部分看 [YouTube_Proposal](YouTube_Proposal.md)。
> Tim 2026-11-09：「不需要太複雜的紀律 主要當成運營&遊戲宣傳」。
> Tim 2026-11-12：「之後投稿都預設公開 投稿前讓我看一下影片就好避免重複上傳」⇒ **上傳前把本機的成片給 Tim 看，他說可以才傳；交付單的 `privacy` 預設寫 `public`**。
> ⛔ 不先傳一支 `unlisted`／`private` 給他看再改公開。只有 Tim 明說要先不公開，才寫別的公開度。
> 🎬 **要自己做影片**（HTML 動畫錄成影片、日語配音、混音；ffmpeg／VOICEVOX 怎麼裝）⇒ **那時才讀** [YouTube_Video_Tools](YouTube_Video_Tools.md)
> （`senate cmd doc --arg op=show --arg name=YouTube_Video_Tools`）。只上傳現成影片檔不用讀。

## 1. 配置環境

先量這台：

```bash
senate cmd youtube            # 不連網：用戶端、登入兩格、授權的頻道、憑證資料夾在哪
```

| 讀數 | 走哪條 |
|---|---|
| `senate: command not found` | 先裝 Senate：SCP_Core 入口「先配置環境」（`<Senate>/install.sh`） |
| `認不得的指令 'youtube'` | 這台的 Senate 太舊 ⇒ pull 含 `youtube` 的版本（2026-11-09 `08547bf` 起）後 `./build.sh` |
| 用戶端、登入都「可用」 | 好了；`op=whoami` 回讀頻道名就收工，直接看 §2 |
| 用戶端、登入都「**沒有**」，別台已經設好 | §1.1 換一台電腦 |
| 用戶端可用、登入沒有，或指令回 exit 3 | 只缺授權 ⇒ §1.1 B 的第 3 步 |
| 登入可用，但「Analytics」那一行是「沒有授權」（數據的 op 回 exit 3） | 上傳不受影響；要看數據走 §3.2 的三步 |
| 連 Google 端都沒設過 | §1.2 從零開始 |

現況（2026-11-09 設好的那一套，換電腦時對得上用）：

| 項目 | 值 |
|---|---|
| Google Cloud 專案 | `FirebaseDemo`（project id `fir-demo-412cc`） |
| OAuth 用戶端 | 電腦版應用程式，client_id `972318882660-5ul4c297f44jh4t3gir81qsourt372t8.apps.googleusercontent.com` |
| 同意畫面 | 外部、**實際運作中**（正式版）；品牌名 `Farstar-studio YouTube`、授權網域 `farstar-studio.com` |
| 授權的頻道 | **Yeh Tim**（`UCp0kzj4Sr5NM3JEVggLlmfQ`，`@yehtim171`）—— Tim 原本的頻道，舊影片 `update` 改不了 |
| 授權範圍 | `youtube.force-ssl`（必要）。2026-11-12 起登入多要 `yt-analytics.readonly`、`youtube.readonly`（選用，數據用，§3.2）—— 2026-11-09 那一次登入沒有這兩個 |

⚠ 密碼檔與用戶端 JSON **只給路徑**（打字貼路徑）。⛔ 不要貼內容，也不要用 `@` 指它們 —— `@` 會把整份內容讀進對話。

### 1.1 換一台電腦（Google 端已經設好）

Google 那邊（專案、用戶端、正式版）是帳號層級的，**不用重做**。這台只缺 `<資料根>/Secret/` 裡的檔。二選一：

**A. 搬加密檔過來（不用重新登入）**

1. 從設好的那台拿 `<資料根>/Secret/` 的 `youtube_client.enc`、`youtube_token.enc`。
   要在這台改 Senate 傳過的影片，連 `youtube_uploads.json`（上傳帳本）一起拿。
   - Secret 若是獨立的 private repo（`.gitignore` 只放行 `*.enc`，Bar 的做法）：那台 commit＋push `.enc`、這台 pull。
   - ⛔ `.txt` 是明文，不搬、不進任何 git、不丟雲端或聊天軟體。
2. 放進這台的 `<資料根>/Secret/`（`senate cmd youtube` 印的「憑證資料夾」就是它；不存在就建）。
3. 解密安裝：`senate ui --page secrets`（加密檔管理）→「一鍵解密全部」→ 輸入當初加密用的密碼（明文已在的跳過，⛔ 不覆寫）。
   ⚠ 這一步要在視窗裡打密碼 ⇒ **Tim 做**，agent 做不了。
4. `senate cmd youtube --arg op=whoami` 讀到頻道名 ⇒ 好了。

**B. 重新授權（手上沒有 `.enc`，或忘了密碼）**

1. 用戶端 JSON：Google **只在建立當下給一次** client secret。原檔不見了 ⇒ Google Auth Platform →「用戶端」→ 點那個用戶端 → 新增一組密鑰，當下存下來。
2. 密碼檔：記事本打一行自己取的密碼（⛔ 不是 Google 密碼），存在 repo 以外。它只用來加密 `.enc` 備份；平常上傳讀的是本機明文，用不到它。
3. 寫入並授權：

```bash
senate cmd youtube --arg op=set-credentials --arg client_path=<用戶端.json> --arg passphrase_path=<密碼檔>
senate cmd youtube --arg op=login --arg passphrase_path=<密碼檔>     # 瀏覽器：選帳號 → 選頻道 →「尚未驗證」按「進階 → 前往」→ 勾 YouTube 權限 → 允許
senate cmd youtube --arg op=whoami
```

- `login` 在終端機即時印出授權網址（瀏覽器沒自己開就手動開），預設等 300 秒（`--arg timeout=<秒>`）。
  agent 代跑時放背景跑，Tim 在瀏覽器按完它就會收到結果。
- 每台各自 `login` 會各拿一顆 refresh token，互不影響。
- 換頻道 ＝ 再跑一次 `login` 選另一個；換用戶端 ＝ `set-credentials --arg overwrite=1` 再 `login`。

### 1.2 從零開始（Google 端也沒設過）

🩸 下面幾格是 2026-11-09 第一次設定時一格一格撞出來的。

1. **啟用 API**：[console.cloud.google.com](https://console.cloud.google.com) 選專案 →「API 和服務 → 程式庫」→ **YouTube Data API v3** → 啟用。
   ⚠ Google Auth Platform 自己的側欄**沒有**這個入口 ⇒ 用頂端搜尋列直接搜，或左上 ☰。
   要看數據（§3.2）再啟用 **YouTube Analytics API** 與 **YouTube Reporting API** —— 跟 Data API v3 是不同的 API，各自啟用。
2. **Google Auth Platform → 品牌**：
   - 應用程式名稱、使用者支援電子郵件、開發人員聯絡信箱；
   - **應用程式首頁**與**隱私權政策連結** —— ⚠ 缺這兩格，「發布應用程式」會是灰的；
   - 授權網域加上那兩個網址的網域（只填網域：不加 `https://`、不加路徑）；
   - ⛔ **不上傳標誌**（一放就要求品牌驗證）。
3. **目標對象**：使用者類型「外部」→「**發布應用程式**」→ 狀態變「**實際運作中**」。
   留在「測試」⇒ refresh token **7 天過期**，而且只有測試使用者名單上的帳號登得進來。
   旁邊「使用者人數上限 100」「未經驗證的應用程式」不用理 —— 只有自己的帳號在用，不送驗證。
4. **資料存取權**：加 `https://www.googleapis.com/auth/youtube.force-ssl`（必要：上傳、改資料、縮圖、播放清單、留言）。
   要看數據（§3.2）再加 `https://www.googleapis.com/auth/yt-analytics.readonly` 與 `https://www.googleapis.com/auth/youtube.readonly`。
   ⚠ 篩選條件比對**完整字串**：搜 `.../auth/youtube` 只撈得到 `youtube` 那一個 ⇒ 把全名貼進「手動新增範圍」→「新增至資料表」→ 勾起來 →「更新」→「儲存」。
   它會落在「**機密範圍**」（sensitive；不是「受限範圍」）⇒ 不用安全評估。
5. **用戶端**：建立 → 類型「**電腦版應用程式**」→ **建好的當下就下載 JSON**（密鑰只顯示一次）。
6. 接 §1.1 B 的第 2、3 步。

可以省的：頻道手機驗證（只有自訂縮圖、超過 15 分鐘的影片才要）、YouTube Studio 上傳預設值（工具每次都顯式送公開度、兒童內容、類別）、ffmpeg 與 VOICEVOX（上傳現成影片檔用不到；自己做影片才裝，見 [YouTube_Video_Tools](YouTube_Video_Tools.md)）。

## 2. 平常（persona 做）

```bash
senate cmd youtube                                                         # 憑證、授權的頻道、帳本筆數（不連網）
senate cmd youtube --arg op=upload --arg slip_file=<交付單.json> --arg dry_run=1   # 只檢查＋印出要送的內容
senate cmd youtube --arg op=upload --arg slip_file=<交付單.json>                   # 真的上傳
senate cmd youtube --arg op=update --arg video_id=<id> --arg slip_file=<只寫要改的欄位.json>
senate cmd youtube --arg op=list [--arg max=20]                              # 頻道最新影片、公開度、觀看／讚／留言
senate cmd youtube --arg op=playlists                                        # 頻道的播放清單、公開度、支數（唯讀）
senate cmd youtube --arg op=playlist-create --arg slip_file=<清單交付單.json> [--arg dry_run=1]   # 建播放清單（系列）
senate cmd youtube --arg op=playlist-add --arg playlist_id=<id> --arg video_ids=<id,id> [--arg position=0] [--arg dry_run=1]   # 把影片加進清單
senate cmd youtube --arg op=analytics [--arg video_ids=<id,id>] [--arg start=YYYY-MM-DD] [--arg end=YYYY-MM-DD]   # 數據一張表（§3.2）
senate cmd youtube --arg op=analytics --arg video_id=<id>                    # 單支：總覽＋流量來源＋留存
senate cmd youtube --arg op=reach                                            # 縮圖曝光與點閱率（先 op=reach-setup 一次）
```

- 上傳成功印 `https://youtu.be/<id>`，然後回讀公開度與處理狀態（剛傳完通常是 `uploaded`，YouTube 還在轉檔）。
- 回讀對不上交付單的公開度 ⇒ 每 2.5 秒重讀、最多 6 次，讀到了就註明「第幾次讀、等了幾秒」。
  🩸 API 讀取端有延遲：2026-11-09 改成 public 後立刻讀還是 unlisted、10 秒後才對。等完還對不上才報 ⚠（那才是真的沒改成）。
- `notify=0` ⇒ 不通知訂閱者（測試片用）。

## 3. 交付單

一個 JSON 物件，例：

```json
{
  "persona": "Claude-Ojousama",
  "video": "D:/Blocksail/captures/naval_battle_01.mp4",
  "title": "Blocksail 開發紀錄 #1：海戰",
  "description": "……",
  "privacy": "public",
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
| `privacy` | **必填** | `private`／`unlisted`／`public`。預設寫 `public`（Tim 2026-11-12；上傳前先給他看成片）。⚠ API 沒給公開度就是**公開** ⇒ 一定要寫 |
| `tags` | | 字串陣列；不能有空字串、`<` `>` 或逗號；合計 ≤ 500（含空白的標籤多算 2、標籤之間各算 1） |
| `category_id` | 預設 `20`（遊戲） | 數字 |
| `made_for_kids` | 預設 `false` | true／false |
| `synthetic_media` | 預設 `false` | true／false。只有擬真人聲、擬真人像才要標；遊戲畫面、像素、體素不用 |
| `language` | | 語言代碼（`zh-Hant`、`en`、`ja`）；同時設成影片語言 |
| `thumbnail` | | 絕對路徑、png／jpg、≤ 50 MB。頻道沒做手機驗證會設不成（影片照樣上傳） |
| `playlist_id` | | 上傳完加進這個播放清單 |

- **欄位名打錯一律擋**（例如寫成 `privacy_status`）—— 打錯的公開度會被當成沒給，而沒給 ＝ 公開。
- `update` 的交付單只寫要改的欄位；不能寫 `video`（換片要重傳）。沒寫的欄位維持原樣（工具會先抄現況再蓋上去 —— API 本身會把沒送的欄位清掉）。

### 3.1 播放清單（把同一個主題的影片收成系列）

Tim 2026-11-10：「YT跟B站 是否可以把Senate相關做成系列」。清單交付單也是一個 JSON 物件：

```json
{
  "persona": "Claude-Ojousama",
  "title": "Senate｜讓 AI agent 有名字、有記憶、有同事",
  "description": "……",
  "privacy": "public",
  "videos": ["<影片 id>", "<影片 id>"]
}
```

| 欄 | 必填 | 檢查 |
|---|---|---|
| `persona` | ✅ | 信件庫裡要有這個人 |
| `title` | ✅ | ≤ 150 字；不能有 `<` `>` |
| `privacy` | ✅ | `private`／`unlisted`／`public`。沒寫就擋（API 沒給公開度就是公開） |
| `description` | | ≤ 5000 bytes；不能有 `<` `>` |
| `videos` | | 建好之後**依序**加進去的影片 id；只收本工具上傳的、不能重複 |

- **只碰本工具建的清單、只加本工具上傳的影片**：`playlist-add` 的 `playlist_id` 要在清單帳本裡、`video_ids` 要在上傳帳本裡，否則擋。頻道上原本的清單與影片一律不動。
- **同名的清單已經有了 ⇒ `playlist-create` 擋下**並印那個清單的 id（要加影片用 `playlist-add`）。重跑一次指令就會多一個同名清單，API 不會擋 ⇒ 這一格在送出前先讀頻道現有的清單。真的要再建一個同名的才加 `--arg allow_duplicate=1`。
- `playlist-add` 先讀清單裡已經有誰：已經在的跳過（API 不擋重複，同一支會出現兩次）。
  順序＝加進去的順序；要插隊給 `--arg position=<第幾個>`（0 ＝ 最前面，多支就從那個位置依序排下去）。
- 🩸 清單剛建好就加影片，YouTube 可能回 `HTTP 409 SERVICE_UNAVAILABLE：The operation was aborted`（2026-11-10：三支裡第一支被擋、後兩支成功）
  ⇒ 工具遇到 409 會等 2 秒重試、最多 3 次，輸出會註明「第幾次才成」。試完還是失敗 ⇒ 過一會兒用 `playlist-add` 補那一支。
- 回讀印的是**清單裡實際的順序**；清單自己報的支數會慢半拍，以那一行為準。
- 上傳或 `op=update` 時在影片交付單寫 `playlist_id` 也能把那一支加進清單（那條路不查帳本、不查重複）。
- ⚠ 公開的清單裡放了不公開（unlisted）的影片 ⇒ 那支影片的網址等於跟著清單公開。
- 沒有改清單標題／說明、移出影片、刪清單的 op ⇒ 到 YouTube Studio 手動做。

### 3.2 數據（Analytics／Reporting）

Tim 2026-11-12：「幫工具接 Analytics」（TASK-0016）。全部唯讀；只有 `reach-setup` 會在 YouTube 那邊建一個報表工作（頻道上別人看不到）。

**第一次要 Tim 做的三步**（`op=status` 的「Analytics」那一行是「沒有授權」時；數據的 op 也會 exit 3 並印出同樣這三步）：

1. Cloud Console（專案 `fir-demo-412cc`）→「API 和服務 → 程式庫」→ 啟用 **YouTube Analytics API** 與 **YouTube Reporting API**。
2. Google Auth Platform →「資料存取權」→ 手動新增範圍 `https://www.googleapis.com/auth/yt-analytics.readonly`、`https://www.googleapis.com/auth/youtube.readonly` → 更新 → 儲存。
3. `op=login` 重新授權，同意畫面上 YouTube 與 YouTube Analytics 的幾格都勾。
   沒勾 Analytics 也算登入成功（上傳照常），只是數據的 op 會擋。
   要 `youtube.readonly` 的原因：Analytics 的 reports.query 說明頁寫「API requests to this method now require access to the youtube.readonly scope」。

```bash
senate cmd youtube --arg op=analytics                                  # 本工具上傳的全部，最近 365 天
senate cmd youtube --arg op=analytics --arg video_ids=<id,id> --arg start=2026-11-01
senate cmd youtube --arg op=analytics --arg video_id=<id>              # 單支：總覽＋流量來源＋留存
senate cmd youtube --arg op=reach-setup --arg dry_run=1                # 看會建什麼
senate cmd youtube --arg op=reach-setup                                # 建 reach 報表工作（只要一次；已經有就不重建）
senate cmd youtube --arg op=reach [--arg video_ids=<id,id>] [--arg start=] [--arg end=]
```

| 讀數 | 意思 |
|---|---|
| 觀看、觀看分鐘 | Analytics 結算過的數字，跟 `op=list` 的即時觀看數不一定一樣 |
| 平均觀看、平均看完 | `averageViewDuration`（秒）、`averageViewPercentage` |
| 3 秒、30 秒還在看 | 留存曲線上最近的那一格（報表每 1% 一格 ⇒ 是「約」，輸出會寫實際是第幾秒）。官方的量尺：30 秒後還有 5 成以上就算「開頭高於平均」 |
| 相對表現 | `relativeRetentionPerformance`：0.5 ＝ 同長度影片的中位數 |
| 流量來源 | `insightTrafficSourceType`；中文後面括號是原代碼 |
| 曝光、點閱率 | reach 報表的 `video_thumbnail_impressions`、`video_thumbnail_impressions_ctr`；點閱率照曝光加權（Σ 曝光×點閱率 ／ Σ 曝光） |

- 🩸 **曝光與點閱率不在 Analytics API**（它的指標表沒有；`adImpressions` 是廣告的、`cardImpressions` 是資訊卡的）⇒ 只能走 Reporting API 的 `channel_reach_basic_a1`：
  建報表工作後 **48 小時內**才出第一份、會回補建立前 30 天；一份 CSV 一天；報表留 60 天（回補的那 30 天只留 30 天）。
- Analytics 通常落後 1～2 天；這段期間沒有資料的影片照實印「沒有資料」，⛔ 不是 0。
- 留存只在觀看夠多時才有（YouTube 不給就是不給）；而且一次只能查一支（留存報表的 filters 只收一支）。
- 點閱率是比例還是百分比，文件沒寫 ⇒ 看到 > 1 的值就整批當百分比，輸出會講這一批當成哪一種。
- 曝光只算 YouTube 站內的縮圖曝光；外部連結（噗浪、Discord）點進來的觀看沒有曝光。
- 下載報表的網址是 API 回應裡讀來的 ⇒ 只送到 `*.googleapis.com`，access token 不跟不認得的網址出去。

## 4. 擋下的幾種情況

| 情況 | exit | 怎麼辦 |
|---|---|---|
| 交付單檢查沒過、欄位名打錯、參數缺 | 2 | 照印出來的 ✗ 改 |
| 同一支檔案（SHA-256 一樣）已經上傳過 | 2 | 真的要再傳加 `--arg allow_duplicate=1` |
| `update` 的影片不是本工具上傳的 | 2 | 頻道上原本的影片一律不碰；`op=list` 的 Senate 欄是「—」的就是 |
| 沒登入、refresh token 失效 | 3 | Tim 重跑 `op=login` |
| 數據的 op：登入時沒拿到 Analytics 的授權 | 3 | §3.2 的三步（連網前就擋，不去撞 403） |
| 數據的 op：Cloud 專案沒啟用 Analytics／Reporting API | 1 | 輸出會說是哪一支、去哪啟用；剛啟用要等幾分鐘 |
| `op=reach`：還沒有 reach 報表工作 | 2 | 先 `op=reach-setup` |
| API 或網路失敗（額度用完 `quotaExceeded`、逾時…） | 1 | 看訊息；上傳額度預設每天 100 支 |

本工具**沒有刪除**。要刪到 YouTube Studio 手動刪。

## 5. 東西住哪

全在 secrets 資料夾（`<資料根>/Secret/`，資料根的 `.gitignore` 擋掉整個資料夾，⛔ 不會進公開 repo）：

| 檔 | 是什麼 |
|---|---|
| `youtube_client.enc`／`.txt` | 用戶端 JSON（密文／本機明文） |
| `youtube_token.enc`／`.txt` | refresh token ＋ 授權的頻道（密文／本機明文） |
| `youtube_uploads.json` | 上傳帳本：影片 id、誰傳的、標題、公開度、檔案雜湊、時間。`update` 只認這份 |
| `youtube_playlists.json` | 清單帳本：本工具建過的播放清單 id、誰建的、標題、公開度、時間。`playlist-add` 只認這份 |

- 帳本放這裡而不放資料根其他地方：**unlisted 影片的 id 就等於網址**，進公開 repo 等於公開。
- 帳本讀不懂（壞檔）⇒ `upload`／`update`／`playlist-create`／`playlist-add` 擋下，⛔ 不拿空帳本蓋掉它。
- reach 報表工作住在 YouTube 那邊（`op=reach-setup` 先列出來、有同類型的就不重建），本機不記。
- 程式：`src/Senate.Core/Cmd_YouTube.cs`（交付單、檢查、帳本）、`src/Senate.Core/Cmd_YouTube.Playlists.cs`（播放清單那一半）、`src/Senate.Core/Cmd_YouTube.Analytics.cs`（數據那一半）、`src/Senate.Core/YouTubeClient.cs`（憑證、OAuth loopback＋PKCE、API）。
  不連網的部分有自我測試：`senate selftest --only YouTubePlaylistSlipAndLedgerCleanRoom,YouTubeAnalyticsCleanRoom`。

## 6. 還沒做

讀留言／回留言、數據進 wake brief（數據本身 2026-11-12 起讀得到了，§3.2）、上傳公告到酒館領薪、Blocksail 錄影與船隻轉台的素材產線 —— 見 [YouTube_Proposal](YouTube_Proposal.md) §5.2、§5.4。
（HTML 動畫 → 影片、VOICEVOX 配音、ffmpeg 混音這條已經走通一次，做法在 [YouTube_Video_Tools](YouTube_Video_Tools.md)。）
