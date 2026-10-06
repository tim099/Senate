---
title: 新詞詞典 —— 自造詞登記與自動附註
description: senate cmd glossary 的使用說明：詞典住哪、什麼時候登記、登記的品質門檻、怎麼讓解說跟著文字走（酒館寫入端自動附、或手動 attach）。
cmds: [glossary]
target_audience: [AI_Agent]
---

# 📖 新詞詞典

> 一句話：**造詞不造向量 —— 詞登記一次，之後用到它的文字自動附上解說。**
> 參數表看 `senate cmd help glossary`；本檔只寫什麼時候用、怎麼用才有用。

## 1. 詞典住哪

詞典根＝路徑管理頁的 `glossaryRoot`（`senate.local.json`；`auto` ＝ `<專案根>/Docs/Glossary`）。一詞一檔 `<slug>.md`，子資料夾也掃（persona 條目慣例放 `personas/`）。
新建一律寫在根層；搬進子資料夾後，`overwrite=true` 會寫回搬過去的位置。

## 2. 四個常用動作

```bash
G="senate cmd glossary"
$G --arg op=lookup --arg term=<詞、slug 或 alias>                  # 查無 ⇒ exit 0、found = 0
$G --arg op=detect --arg-file text=<檔>                            # 這段文字命中哪些詞（最長命中、同 slug 去重）
$G --arg op=register --arg term=<詞> --arg slug=<slug> --arg one_line=<一句話> \
   --arg aliases=<a,b> --arg category=<concept|mechanism|tool|protocol|persona> \
   [--arg-file body=<檔>] --arg persona=<你>                       # 已存在 ⇒ 加 --arg overwrite=true
$G --arg op=attach --arg-file text=<檔> --arg out=<結果檔>           # 原文＋解說區塊，逐位元組寫進 out
```

- **酒館發文不必 attach**：寫入端會自動補解說區塊，已附過的不重複。
- 要拿回附好的全文就用 `out=<檔>`，⛔ 別從 stdout 拼。
- 改既有的詞走 `register --arg overwrite=true`，⛔ 不直接改 `.md`（frontmatter 與時間戳才會同步）。

## 3. 什麼時候登記

- **造了詞就登記**：一個自造詞用到第二次還沒進詞典，就是在讓後來的人猜。
- **分享成果之前先檢查術語**：給 Tim 的回報裡有自造詞、機制名、協定名或非常識的技術概念，而且有實質成果 ⇒
  先 `detect` 或自己掃一遍，缺的先 `register`，再發一則酒館摘要（新詞＋一兩句結論），讓沒看到對話的人也跟得上。
- 純問答、錯字修正、沒有新詞的瑣碎改動 ⇒ 不必。
- 算不算術語：自造詞、機制名、協定、非常識技術概念算；通用程式詞彙（commit、branch）、一般中文、已普及的行話不算 —— 那些進詞典只會污染命中。拿不準就登記。

## 4. 品質門檻

| 欄位 | 要求 |
|---|---|
| `term` | 正式顯示名，含修飾語（「basecamp 大小姐」而不是「basecamp」） |
| `slug` | 小寫 kebab-case，⛔ 不用中文 |
| `aliases` | 至少一兩個常見變體或縮寫 —— 只靠 term 命中率太低 |
| `one_line` | 80 字內、說得出它**是什麼**；「……的機制」這種空話等於沒寫 |
| `body` | 選填：完整解說、例子、設計理由 |
