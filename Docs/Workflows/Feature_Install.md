---
title: 功能安裝 —— 統一入口（要裝、要接、要設定什麼，都從這裡找）
description: 使用者說「幫我裝／接／設定 X」時先讀：可以裝的功能一覽（Senate 本體、模型與 Python 套件、Discord、YouTube、Bilibili、Plurk、影片製作工具）、每一項先量什麼、流程在哪份文件、使用者要給什麼；每一種安裝都適用的共通規則（先量再動、憑證不經過對話、一次講一步、回讀才算裝好）；怎麼新增一個可安裝的功能
last_updated: 2026-11-11
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🧰 功能安裝：統一入口

> 一句話：**使用者說「幫我裝／接／設定 X」⇒ 在 §1 找到 X，先量這台，再讀那一列的文件照做；§2 的規則每一種安裝都適用。**
> 這一份只是索引：每個功能的步驟住在它自己的文件裡，用 `senate cmd doc --arg op=show --arg name=<文件名>` 讀。

## 1. 可以裝的功能

| 功能 | 使用者大概會怎麼說 | 先量（唯讀） | 流程在哪份文件 | 使用者要給的 |
|---|---|---|---|---|
| **Senate 本體** | 安裝 Senate、換一台電腦、`senate` 找不到 | `senate doctor`（找不到就看 `<Senate>/publish/senate.exe` 在不在） | repo 根的 `README.md`「讓 AI agent 替你安裝」；細節 `Setup_And_Build` | 沒有。缺 Git／.NET 時 Windows 的允許視窗要按「是」 |
| **模型與 Python 套件**（語意檢索、語音辨識、OCR…） | 某個功能說「沒安裝／後端缺席」 | `senate cmd install`；skill 缺相依用 `--arg op=check --arg skill=<名>` | `Install` | 一句同意。⛔ 沒同意不裝；同意只算那一次、那幾項 |
| **Discord 雙向轉發** | 幫我把酒館接到 Discord、webhook、Discord 沒同步過來 | `senate cmd discord-relay`、`senate cmd discord-bot` | `Discord_Install`（第 0 節是交給 persona 一路做完的流程） | webhook 網址、Bot token、一組加密密碼；在 Discord 端建 webhook、建 Bot、邀請 Bot |
| **YouTube 上傳** | 設定 YouTube、YouTube 憑證、換電腦後傳不了影片 | `senate cmd youtube` | `YouTube` 第 1 節「配置環境」 | Google Cloud 的用戶端 JSON 與加密密碼（都只給檔案路徑）；在瀏覽器按授權 |
| **Bilibili 投稿** | 安裝 bili、B 站登入、B 站掃碼 | `bili --version`、`bili auth status`（環境變數見該文件） | `Bilibili_Install` | 用手機掃碼登入 |
| **跨主機連線**（兩台 Senate 的酒館與在線名單互通） | 兩台電腦的酒館接起來、跨主機連線、另一台連不上 | `senate cmd peer` | `Peer_Link` 第 2 節 | 哪一台當主機；要用通道的話同意安裝 cloudflared |
| **Plurk 發噗** | 設定 Plurk 帳號、Plurk 憑證 | `senate cmd plurk --arg persona=<我> --arg op=resolve` | `Plurk_Admin_Page` 第 5 節 | Plurk 的憑證（內容與裝法看那一節） |
| **影片製作工具**（ffmpeg、VOICEVOX、無頭 Chrome） | 要做影片、配音跑不起來 | `YouTube_Video_Tools` 第 1 節那幾行 | `YouTube_Video_Tools` | 一句同意（會下載並安裝程式） |

- 表裡沒有的東西 ⇒ 先 `senate cmd doc` 找有沒有它的文件；沒有就照實跟使用者說還沒有安裝流程，⛔ 不自己拼一套。
- 一項裝好之後再裝下一項，不要兩項的步驟交錯著講。

## 2. 共通規則（每一種安裝都適用）

1. **先量再動。** 已經在跑的不重裝；量不到（指令炸了、找不到程式）跟「沒裝」不是同一件事，先處理量不到的原因。
2. **憑證不經過對話。** token、webhook 網址、密碼、用戶端 JSON 一律走檔案：
   - persona 在 repo 外面開一個暫存資料夾、建空檔，用記事本替使用者打開；使用者貼上、存檔、關掉；persona 只把**路徑**交給指令。
   - ⛔ persona 不讀、不印這些檔，不用 `@` 指它們，不把內容寫進回傳檔、酒館、commit 訊息。
   - 使用者把憑證貼進了對話 ⇒ 請他到發行的那一端重設那一顆，再用檔案給。
   - 裝好、驗過之後刪掉暫存檔（裡面是明文）。
3. **一次講一步，講完等他回。** 說要按哪裡、會看到什麼；不評價對方懂不懂技術。
4. **使用者那一端的事不代做、不繞過**：登入、掃碼、驗證碼、允許視窗、在後台視窗打密碼。做不了的照實說，請他做那一步。
5. **會下載、會花錢、會動到別的帳號的，先說清楚再做**：多大、從哪來、裝到哪。沒同意不做。
6. **回讀才算裝好。** 每個文件都有驗證那一節：量到它說的讀數，再跟使用者說裝好了，並把讀數的意思用白話講一遍。
7. **明文不進 git**；密文（`.enc`）要不要提交，問使用者（資料樹是公開 repo 時尤其要問）。
8. **換一台電腦**：設定多半跟著資料樹過來，憑證的明文不會。先量，缺的照該文件「換一台電腦」那一段補。

## 3. 新增一個可安裝的功能

1. 寫它的安裝文件（指令住哪、文件就住哪）。至少要有：先量這台／使用者要給的東西／persona 從頭到尾的流程／驗證／排查表／哪幾步一定要使用者做與怎麼說。
2. 在 §1 的表加一列。
3. `SCP_Core/Skills~/scp-install/SKILL.md` 的觸發詞補上使用者會講的那幾個詞。
4. `senate cmd skill --arg op=show --arg name=scp-install` 驗內容，再 `op=sync --arg confirm=1` 重裝（改了觸發詞才需要重裝；只改文件不用）。
