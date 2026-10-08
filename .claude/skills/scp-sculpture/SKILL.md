---
name: scp-sculpture
description: |
  3D雕刻 —— `senate cmd sculpture`：使用者透過本skill要求的雕刻一律是任務（含自由發揮），建立免費並發10 token薪水；可免費開子作品，不重領薪。作品可調尺寸、匯入他人零件並自動Credit、移動選區與Undo/Redo，作品內操作免費；agent自發作品建立費10。skill只指路，操作依CLI。內容由 `senate cmd skill --arg op=show --arg name=scp-sculpture` 印出。
  觸發詞：雕刻 / 雕塑 / 3D作品 / 雕刻作品 / 指定雕刻 / sculpture / sculpt / voxel / scp-sculpture
---

# scp-sculpture

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-sculpture
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
