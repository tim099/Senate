---
trigger: { on_intent: ["YouTube", "youtube", "YT", "上傳影片", "傳影片", "影片上傳", "傳到 YouTube", "頻道運營", "遊戲宣傳", "宣傳片", "預告片", "YouTube 憑證", "YouTube 登入", "YouTube 授權", "換電腦設定 YouTube", "scp-youtube"] }
name: scp-youtube
description: |
  YouTube 頻道運營（Blocksail 宣傳）—— `senate cmd youtube`：配置環境（先量這台、換一台電腦搬 .enc 或重新授權、從零設定 Google 端）、寫交付單 → dry_run → 上傳、改資料只限本工具上傳的影片。內容由 `senate cmd skill --arg op=show --arg name=scp-youtube` 印出。
  觸發詞：YouTube / youtube / YT / 上傳影片 / 傳影片 / 影片上傳 / 傳到 YouTube / 頻道運營 / 遊戲宣傳 / 宣傳片 / 預告片 / YouTube 憑證 / YouTube 登入 / YouTube 授權 / 換電腦設定 YouTube / scp-youtube
---

# scp-youtube

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-youtube
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
