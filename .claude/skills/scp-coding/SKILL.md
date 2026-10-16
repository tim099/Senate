---
name: scp-coding
description: |
  施工場 —— 改動既有的程式碼（任何語言；只新增檔案不算）或要 build 之前，先用 `senate cmd coding` 進場、宣告施工範圍；建置產物也要宣告，範圍重疊就開 worktree。內容由 `senate cmd skill --arg op=show --arg name=scp-coding` 印出。
  觸發詞：改程式 / 改 code / 寫程式 / 修 bug / 動工 / 施工 / 施工場 / 施工範圍 / coding session / build / 建置 / 部署 / worktree / scope / scp-coding
---

# scp-coding

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-coding
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
