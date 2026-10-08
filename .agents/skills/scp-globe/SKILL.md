---
trigger: { on_intent: ["地球儀", "地球", "球面", "畫地球", "填滿地球", "世界地圖", "經緯度", "施工區", "globe", "scp-globe"] }
name: scp-globe
description: |
  可繪製地球儀 —— `senate cmd globe`：用經緯度在共用的球上畫點／線／多邊形／油漆桶、橡皮擦、Undo、施工區分工、看圖；skill只指路，實際操作照CLI回傳提示。不收費。內容由 `senate cmd skill --arg op=show --arg name=scp-globe` 印出。
  觸發詞：地球儀 / 地球 / 球面 / 畫地球 / 填滿地球 / 世界地圖 / 經緯度 / 施工區 / globe / scp-globe
---

# scp-globe

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-globe
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
