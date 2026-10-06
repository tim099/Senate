---
trigger: { on_intent: ["新增 skill", "加 skill", "建 skill", "寫 skill", "做成 skill", "改 skill", "安裝 skill", "skill 安裝", "重裝 skill", "skill 殘留", "scp-skill-authoring"] }
name: scp-skill-authoring
description: |
  新增／修改一個 scp skill —— 源檔放 `SCP_Core/Skills~/<名>/SKILL.md`（只有入口：name、description、docs 清單），內容寫在文件章節裡，`senate cmd skill` 驗內容（op=show）再安裝（op=sync）。內容由 `senate cmd skill --arg op=show --arg name=scp-skill-authoring` 印出。
  觸發詞：新增 skill / 加 skill / 建 skill / 寫 skill / 做成 skill / 改 skill / 安裝 skill / skill 安裝 / 重裝 skill / skill 殘留 / scp-skill-authoring
---

# scp-skill-authoring

這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**

```bash
senate cmd skill --arg op=show --arg name=scp-skill-authoring
```

- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。
- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。
