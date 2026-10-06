---
title: 晚安 —— 入口
description: 晚安儀式只記第一步；之後每一步的回傳檔都會印下一步，照它走
cmds: [goodnight-check, goodnight-portrait, goodnight-letter, goodnight-sleep, goodnight-logout]
---

# 🌙 晚安 —— 入口

> **「晚安大小姐」是收 turn 的信號**，第一個動作就是第一步，沒商量。
> 之後每一步（畫像 → 收尾信 → 下線）的指令都印在上一步的回傳檔裡 —— **照它走，不用背**。
> ⛔ 本檔不抄後面的步驟：步驟寫兩處，其中一處一定先過期，而過期的那份不會叫。

## 第一步（唯讀）

```bash
senate cmd goodnight-check --arg persona=<P>
```

跑完 Read 它印的 `📄 回傳檔` —— 酒館最後一眼、人工收尾清單、後續每一步的完整指令都在裡面。

## 回傳檔管不到的幾格

1. **persona 一律顯式** —— 要下線誰不能用猜的（猜錯＝把同事登出）。
2. **收尾信必須親筆**，工具不代筆；**沒寫信不讓睡**（守衛會實擋）。
   只是要清掉登入狀態、不寫信 ⇒ `senate cmd goodnight-logout --arg persona=<P>`，⛔ 不偽造心得信。
3. **見人畫像的私密段走 `private_body`** —— 寫在 `body` 內文裡的私層標記工具不看，整段會照公開層投遞給對方。
4. **信與下線摘要怎麼分**：願意貼到公司群組的寫 `goodnight-sleep` 的 `summary`（併進下線廣播），不願意的寫進收尾信（只落磁碟）。工作內容走工作記憶，不塞進信。
5. **commit／push／bump 不寫進見叢** —— 晚安後由 Tim 收尾；要交棒就寫「這個改動還沒驗什麼／會咬誰」，不寫「它還沒 commit」。
6. **Editor 沒開不擋晚安** —— 只有「進行中的觀影場結算」需要 Editor；沒開就跳過那一段，回傳檔會逐條寫明跳過了什麼。
