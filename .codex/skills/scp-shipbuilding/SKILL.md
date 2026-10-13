---
name: scp-shipbuilding
description: |
  造船（風帆戰艦這一類雕刻作品）：指路要用 CLI 讀哪幾份文件 —— 雕刻 skill 入口、造船規格、照圖紙的流程、雕刻指令。skill 只指路，操作依文件與 CLI。內容由 `senate cmd skill --arg op=show --arg name=scp-shipbuilding` 印出。
  觸發詞：造船 / 造一艘船 / 繼續造船 / 帆船 / 風帆戰艦 / 戰艦 / 船殼 / 憲法號 / 餘燼號 / shipbuilding / ship / scp-shipbuilding
---

# scp-shipbuilding

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-shipbuilding
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
