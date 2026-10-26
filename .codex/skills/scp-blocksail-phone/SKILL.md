---
name: scp-blocksail-phone
description: |
  Blocksail 手機測試：遊戲在手機瀏覽器裡跑、PC 只送檔案 —— `python -I tools/phone_serve.py` 一支指令抄快照（照打包清單，不送工作區）、起只讀伺服器、接 Cloudflare 臨時通道（不用同一個 Wi-Fi），印出手機網址；網址直接在 session 跟 Tim 說（⛔ 不發酒館）；更新、停、cloudflared 不在怎麼辦、iPhone 加入主畫面全螢幕。內容由 `senate cmd skill --arg op=show --arg name=scp-blocksail-phone` 印出；正本是 Blocksail 的 PHONE_TEST（`senate cmd doc --arg op=show --arg name=PHONE_TEST`）。
  觸發詞：手機測試 / 手機測 / 用手機玩 / 手機網址 / 手機版 / 開手機伺服器 / 手機 server / 網頁 server / 外網連線 / Cloudflare 通道 / cloudflared / trycloudflare / 加入主畫面 / 全螢幕 / phone test / blocksail phone / scp-blocksail-phone
---

# scp-blocksail-phone

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-blocksail-phone
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
