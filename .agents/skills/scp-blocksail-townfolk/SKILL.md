---
trigger: { on_intent: ["新增鎮民", "鎮民圖片", "鎮民立繪", "居民素材", "生成鎮民", "小孩立繪", "少女立繪", "換色鎮民", "townfolk art", "scp-blocksail-townfolk"] }
name: scp-blocksail-townfolk
description: |
  Blocksail 鎮民圖片：新增或替換鎮民立繪，直接生成青色頭髮與洋紅主衣料，設定 _people.js 的人物資料與實際身高，匯入遊戲圖集、驗換色邊界並同步 ArtGallery。內容由 `senate cmd skill --arg op=show --arg name=scp-blocksail-townfolk` 印出；操作正本在 Blocksail TOWNFOLK_ART 的「鎮民新增與換色規格」。
  觸發詞：新增鎮民 / 鎮民圖片 / 鎮民立繪 / 居民素材 / 生成鎮民 / 小孩立繪 / 少女立繪 / 換色鎮民 / townfolk art / scp-blocksail-townfolk
---

# scp-blocksail-townfolk

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-blocksail-townfolk
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
