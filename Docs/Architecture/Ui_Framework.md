---
title: UI 框架 — 中間層與四種驅動方式
description: immediate-mode 撰寫 API → 節點樹 → renderer 的設計、id 產生規則（顯式 key 是契約）、事件慢一幀的語意、非 UI 操控介面與 session 狀態
last_updated: 2026-08-22
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🖼 UI 框架

## 形狀

```
頁面碼（一頁一個方法）
   │  SCP_Ui.Button / Label / Table / Box …
   ▼
SCP_GuiNode 樹（中間層 —— 一次繪製建一棵、用完丟）
   │
   ├─ SCP_GuiTextRenderer   → 純文字（可 diff、可貼給人看）
   ├─ GuiImGuiRenderer      → ImGui 視窗（Senate.Desktop）
   ├─ SCP_GuiQuery          → 可互動元件清單 / 整棵樹轉 JSON（給 agent／腳本）
   └─ SenateScreenshot      → PNG（給沒有眼睛的人驗收）
```

撰寫端是 `GUILayout` 手感 —— 但那些呼叫**不畫像素**，只往樹上掛節點：

```csharp
void Draw(SCP_Ui g)
{
    g.Title("問題回報管理");
    using (g.Row())
    {
        m_Filter = g.TextField("篩選", m_Filter, "bug/filter");
        if (g.Button("重新載入", "bug/reload")) Reload();   // 回傳值就是事件
    }
    using (g.Table("單號", "標題"))
        foreach (var b in m_Bugs) g.TableRow(b.Index.ToString(), b.Title);
}
```

## 為什麼值錢：不是「換畫布方便」

同一份頁面碼有四種驅動方式，**任兩種可以互為證人**：

| 驅動 | 誰用 | 它能證明什麼 |
|---|---|---|
| ImGui 視窗 | 人 | 實際觀感、互動手感 |
| 文字 renderer | 人／CI | 內容正確（可 diff、可快照） |
| 指令操作 | agent／腳本 | 互動可重放、可自動化 |
| 截圖 | 不在現場的人 | 真的畫出來了（字型、版位） |

⇒ UI 從「只能用眼睛驗」變成「有讀數可以對」。

---

## id 規則（這節最容易踩）

| 情況 | id 怎麼來 |
|---|---|
| 有傳顯式 key | **逐字採用** —— 不加 layout 前綴、不做 slug |
| 沒傳 key | layout 路徑 ＋ 文字 slug（例：`row/重新載入`）⇒ **會隨版面改動而漂** |
| 撞名 | 補 `#2` 並記進 `SCP_Ui.Diagnostics`（會漂的東西必須看得見） |

🩸 **血證（2026-08-22）**：第一版把顯式 key 也丟進路徑推導，於是
`Button("重新取讀數", "doctor/refresh")` 放在 `using (g.Row())` 裡變成 `row/doctor-refresh`。
把按鈕搬進／搬出一個 `Row` 就換 id —— 那正是傳顯式 key 要防的漂移。
抓到它的是 `senate ui --click doctor/refresh` 回 `✗ 畫面上沒有這個 id`（不是靜默沒反應）。

⇒ **顯式 key 是契約**：呼叫端寫什麼，操作端（agent／腳本／測試）就用什麼。
清單項目一律用**資料本身的鍵**（不要用序號）。

---

## 事件語意：慢一幀

ImGui renderer 的互動是「這一幀記下誰被按了 → 下一幀透過 `SCP_GuiInput` 餵回頁面」。
所以頁面看到的 `Button(...) == true` **比實際點擊晚一幀**。

⚠ 這是 immediate-mode 疊在 retained 畫布上的標準做法，不是 bug ——
但**要知道它慢一幀**，否則「按了沒反應」會被誤讀成事件掉了。

CLI 那側用**兩趟繪製**處理同一件事：第一趟帶 click 讓 handler 真的執行，
第二趟才是要顯示的畫面。只畫一趟的話，畫面顯示的是按下**前**的狀態。

---

## 非 UI 操控介面（`SCP_GuiDriver`）

- `SCP_GuiQuery.Interactive(tree)` → 可互動元件清單（id／類型／標籤／現值／**怎麼操作**）
- `SCP_GuiQuery.Find(tree, id)` → 呼叫端**必須**檢查；對不存在的 id 靜默成功會讓
  「我按了但沒反應」與「我按錯了」同形
- `SCP_GuiState` → 跨次要記住的欄位值與勾選（**點擊是事件，不進狀態**），序列化走 `SCP_JsonData`
- 檔案 IO 不在共用層：session 落在 `build/ui_session.json`，由 `Senate.Cli/UiDriver` 負責讀寫

指令清單見 [../API/Cli_Reference](../API/Cli_Reference.md)。

---

## 文字 renderer 的細節

- **全角字寬度算 2 格**（`SCP_GuiTextRenderer.Width`）—— 用 `string.Length` 對齊表格會歪，
  而歪掉的表格不會報錯，只會讓人不想讀。
- `Row` 全是 inline 節點時串成一行；含群組時退化為逐項換行
  （文字模式沒有真正的水平版位，硬排會互相蓋掉，寧可誠實換行）。
- ⚠ **已知缺口**：表格還不吃 `--width`（欄寬取自然寬度，窄視窗會超出）。

## 字型（Senate.Desktop）

判準不是「字型有沒有載」，是**「這一頁實際用到的每個字元有沒有 glyph」**。

🩸 只載 `GetGlyphRangesChineseFull()` 的第一版：中文正常，但 `✓ ≥ ⇒ ⚠` 全變 `?`
（那份 range 不含符號區），而**缺字不報錯**。
修法：`SenateFonts` 自訂 12 個字元區塊 ＋ merge `seguisym.ttf`。
pinned handle 存成 static —— font atlas 在設定函式回來之後才建，range 陣列被 GC 移動就會拿到垃圾。
