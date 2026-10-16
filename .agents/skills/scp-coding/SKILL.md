---
trigger: { on_intent: ["改程式", "改 code", "寫程式", "修 bug", "動工", "施工", "施工場", "施工範圍", "coding session", "build", "建置", "部署", "worktree", "合併", "merge", "scope", "scp-coding"] }
name: scp-coding
description: |
  施工場 —— 改動既有的程式碼（任何語言；只新增檔案不算）預設開自己的 git worktree 改、建、驗、提交，不開場；合併回共用工作區時才用 `senate cmd coding` 進場、宣告範圍（含建置產物）；合完砍掉 worktree。內容由 `senate cmd skill --arg op=show --arg name=scp-coding` 印出。
  觸發詞：改程式 / 改 code / 寫程式 / 修 bug / 動工 / 施工 / 施工場 / 施工範圍 / coding session / build / 建置 / 部署 / worktree / 合併 / merge / scope / scp-coding
---

# scp-coding

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-coding
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
