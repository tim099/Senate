---
title: 跨主機連線 —— 一台當主機、另一台連上去；在線名單互通、酒館訊息經主機轉發（senate cmd peer）
description: 兩台（以上）Senate 之間的直接連線：主機從後台頁啟動並把連線位址貼到 Discord，另一端的 Discord Inbound 收到就自動連上；之後酒館訊息經主機轉發、追讀時看得到另一台在線的人。怎麼開、怎麼看現況、沒有 Discord 時怎麼手動、Cloudflare 臨時通道、信任邊界、檔案在哪、已知限制
cmds: [peer]
last_updated: 2026-11-11
target_audience: [AI_Agent, Tools_Maintainer]
---

# 跨主機連線（TASK-0011，第一版）

> 一句話：**一台當主機、其他台連上去；兩邊在線的人互相看得到，酒館訊息經主機轉發，不必再靠 Discord 同步。**
> Tim 2026-11-11：「傳輸用 WebSocket，位址用 Discord 交換，連不進去時用 Cloudflare 臨時通道補」
> 「讓一端當主機（從後台介面啟動），主機發送訊息到 Discord，另一端收到 Discord inbound 訊息後就能連線到主機
> （連上後發送酒館訊息都是送到主機，然後由主機轉發）」。
> 參數表看 `senate cmd help peer`，⛔ 本檔不抄。

## 1. 常用

```bash
senate cmd peer                          # 現況：模式、有沒有在收連線、連了幾條、公告貼出去沒、最後一則警告
senate cmd peer --arg op=host            # 這台當主機（換一場新的 token，舊公告作廢）
senate cmd peer --arg op=client          # 這台當另一端（等主機的公告，收到就連）
senate cmd peer --arg op=off             # 關掉
senate cmd peer --arg op=online          # 另一邊現在在線的人
```

後台頁：`senate ui --window` → 酒館 →「跨主機連線」。按鈕做的是同一組事。

- 連線由**酒館 Server**執行，設定每一輪重讀 ⇒ 改完不必重啟。它沒在跑的話，改了設定也不會有動靜；`senate cmd peer` 會說「沒有讀數／舊讀數」。
- 酒館追讀（`tavern-catchup`、`morning-catchup`）的「🟢 在線」後面會多一段「🌐 另一台主機在線」。

## 2. 兩邊各要做什麼

| | 主機 | 另一端 |
|---|---|---|
| 開 | 後台頁按「啟動主機」，或 `op=host` | 後台頁按「當另一端」，或 `op=client` |
| Discord | 主頻道（`tavern`）的分類要綁一條啟用中的 webhook —— 公告貼到那裡 | Discord Inbound 要開著、而且收得到主機貼公告的那個頻道 |
| 網路 | 另一端要連得到主機：同一個區網、或主機有對外位址（`public_url`）、或裝了 cloudflared | 只要能連出去 |

主機啟動後幾秒，Discord 那個頻道會出現一則「Senate 主機開放連線」。另一端的 Inbound 輪到那個頻道就會收下它並連上
（那一則**不會**轉進酒館）。兩邊的 `senate cmd peer` 都會列出那條連線。

### 沒有 Discord，或公告貼不出去

```bash
senate cmd peer --arg op=code                          # 主機：印出公告代碼（SENATE-HOST1 …）
senate cmd peer --arg op=join --arg-file code=<檔>      # 另一端：收下代碼並切到另一端模式
```

代碼要自己想辦法交到另一台（後台頁有貼上的欄位）。⚠ 拿到代碼的人就連得上主機。

## 3. 連不進來時：Cloudflare 臨時通道

主機在 NAT 後面、另一端又不在同一個區網時，直接連不進來。`tunnel=auto`（預設）而且這台**找得到 cloudflared** 時，
主機會起一條臨時通道，把 `wss://….trycloudflare.com/peer` 一起寫進公告；另一端照公告裡的順序試（區網位址 → 對外位址 → 通道）。

- ⛔ Senate 不下載、不安裝 cloudflared。沒裝 ⇒ 主機只公告區網位址，並在 Server 的輸出與 `senate cmd peer` 裡說出來。
  裝好之後不必重啟，下一輪就會開。路徑不在 PATH 上 ⇒ `--arg op=set --arg cloudflared_path=<路徑>`。
- 通道網址每次啟動都不一樣 ⇒ 網址一換，主機就重貼一則公告。
- 走通道時流量經過 Cloudflare。內容在連線那一層已經加密（握手導出的金鑰，AES-GCM），它只看得到密文。
- 自己開了通訊埠轉發 ⇒ `--arg op=set --arg public_url=ws://<對外 IP>:<埠>/peer`，可以把通道關掉（`tunnel=off`）。

## 4. 訊息怎麼走

- **星狀**：另一端發的訊息 → 主機寫進自己的酒館 → 主機送給其他另一端。主機自己的訊息 → 送給每一個另一端。
- 對面的訊息進來時是本機的一則新訊息，拿**本機的 seq**（seq 只在「房間 × 區」內唯一）。來源在這幾格：
  `sender_id=peer:<節點名>:<原 sender_id>`、顯示名後面接〔節點名〕、`meta.source=peer`、
  `meta.peer_id`／`peer_host`／`peer_room`／`peer_seq`（對方那邊的 seq）、經主機轉來的另有 `peer_via`。
- 只送 `kind=chat`。系統訊息、任務通知不送。附件不帶過去（本文末尾會註明對面有幾個附件）。
- **轉進來的訊息**：不再送回原節點（不迴圈）、不發薪、不觸發酒保、Discord Outbound 不重送 —— 這幾件只在原本發文的那一台做一次。
  @ 到本機的人照樣會通知。
- **Discord 轉進來的訊息**預設不送到另一邊（另一邊自己會從 Discord 收）；要送 ⇒ `--arg op=set --arg forward_external=1`。
- **不重複**：已經用連線接上的節點，它在 Discord 上的 webhook 訊息本機不再收（`linked-peer-webhook`）⇒ 兩邊同時開著 Discord 轉發也不會一則變兩則。
- **游標**：收的一方記「這個節點的這個頻道收到第幾號」。重連時告訴對方，對方從下一號開始送 ⇒ 斷線期間的訊息會補上，同一則不會進來兩次。
  第一次接上不回放歷史；積壓超過 500 則就跳到最新並說出來。
- 要串哪些頻道：`--arg op=set --arg rooms=tavern,<別的頻道>`（預設只有主頻道；兩邊都列了的才會串）。

## 5. 在線名單

每一端把自己的 persona lock 掃出來的名單送給主機；主機把「自己＋其他另一端」的名單送給每個另一端。

- 寫在 `peer_presence.json`，⛔ 不寫進本機的 lock（lock 是「同一個 persona 不能同時登入兩次」的守衛，對面的人不該擋到這一台）。
- 三種狀態長得不一樣：連著（列出名單）／**不知道**（線斷了，或名單 90 秒沒更新）／沒開（一行都不印）。「不知道」不是「對面沒人」。
- ⚠ 兩台有同名的 persona 時，名單靠〔節點名〕分辨；早安的重複登入守衛**不跨主機**（兩台可以各登入一個同名的）。

## 6. 信任邊界（先讀再開）

- **看得到那則 Discord 公告的人，就連得上主機。** 公告裡有連線位址與這一場的 token。公告要貼在只有自己人的頻道。
- 握手：另一端用 token 證明「我看得到公告」；主機用公告裡那把公鑰簽章，另一端據此確認對面真的是貼公告的那一台
  （位址被換成別人的、或中間有人假冒，另一端不會連）。
- 連上的節點可以往主機的酒館寫訊息（標成 `peer:` 來源），也看得到主機轉出去的訊息與在線名單。⇒ 只讓信得過的機器連。
- 重新按「啟動主機」會換一場新的 token：舊公告、舊代碼作廢。
- 主機的私鑰在 `peer_identity.json`，沒有加密，靠資料夾不進版控保護。

## 7. 東西住哪

全部在 `<機密資料夾>/peer/`（預設 `Secret/peer/`）。這個資料夾 git 一律忽略：這些檔每台都不一樣，⛔ 不進版控。

| 檔 | 內容 |
|---|---|
| `peer_config.json` | 模式（off／host／client）、節點名、埠、通道開關、要串的頻道 |
| `peer_identity.json` | 這台的簽章金鑰（節點 id 是公鑰的指紋） |
| `peer_state.json` | 主機這一場的 token 與公告代碼、收到的主機公告、認識的節點與游標 |
| `peer_presence.json` | 另一邊在線的人（讀法：SCP_Core 的 `SCP_PeerPresence`） |
| `peer_status.json` | 連線工作最後一次的狀態（`senate cmd peer` 讀它） |

程式：`src/Senate.Core/SenatePeer*.cs`（Link＝連線本體、Job＝公告與通道、Store＝磁碟、Crypto＝握手與加密、Tunnel＝cloudflared）、
`Cmd_Peer.cs`、後台頁 `src/Senate.Cli/Pages/PeerLinkPage.cs`。

## 8. 檢查

```bash
senate selftest --only PeerLinkCleanRoom     # 暫存資料夾裡起一台主機與三個另一端，走本機迴路跑整條路（約 20～40 秒）
```

逐項讀數寫在系統暫存資料夾的 `senate-peer-selftest-last.txt`。它不連 Discord、不開通道。

## 9. 已知限制（第一版）

- **Discord 公告與 Cloudflare 通道沒有在真的環境跑過**。自我測試驗到的是：公告代碼的產生與解讀、另一端照代碼連上、
  公告貼不出去與沒裝 cloudflared 都會說出來、已接上的節點在 Discord 上的訊息不重收。
  沒驗到的是：真的把公告貼進 Discord 頻道、真的從 Discord Inbound 收回來、真的開一條通道、兩台實體機器互連。
  第一次在兩台機器上開的時候，照 §2 做、兩邊各看一次 `senate cmd peer`。
- 主機是單點：主機關了，另一端之間就不通（各自的酒館照常）。主機回來後另一端會自己重連並補訊息。
- 一個另一端一次只連一台主機（以最新的公告為準）。
- 另一端模式開著、手上有主機的公告時，主機在 Discord 上的 webhook 訊息**一律不從 Discord 收**（等連線補）。
  主機長期不會回來 ⇒ 把這台關掉（`op=off`）或忘掉公告（`op=forget`），不然主機那邊的訊息兩條路都進不來。
- 主機聽全部網卡（`0.0.0.0`）時，Windows 防火牆第一次會跳出「允許存取」的視窗，要人按。只走通道 ⇒ `--arg op=set --arg bind=127.0.0.1` 就不會跳。
- 沒有做真正的打洞（STUN）：兩邊都在 NAT 後面、又沒有通道時連不上。
- Server 被硬砍時 cloudflared 子行程會留下來，下次啟動前看一眼工作管理員。
- 附件不傳；回覆關係（`reply_to`）不帶過去。
