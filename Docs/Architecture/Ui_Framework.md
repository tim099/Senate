---
title: UI 框架 — 中間層與四種驅動方式
description: immediate-mode 撰寫 API → 節點樹 → renderer 的設計、id 產生規則（顯式 key 是契約）、事件慢一幀的語意、非 UI 操控介面與 session 狀態
last_updated: 2026-08-23
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

## 頁面堆疊：`SCP_GuiPage` / `SCP_GuiPageController`

```
SCP_GuiPageController（一個 Window 一套 —— 沒有全域單例）
  ├── Push / Pop / PopUntil / PopUntilKey / PopAll / Remove / Replace
  ├── Draw(ui)     ← 只畫 TopPage；Count>1 時自動畫麵包屑＋返回鈕（id 固定 page/back）
  ├── Tick()       ← 只給 TopPage
  ├── PathText     ← 「首頁 ▸ 細節」（人看的）
  └── PathKeys / RestorePath(keys, factory)   ← 導覽狀態（機器讀的，進 session 的 nav）

SCP_GuiPage（abstract）
  ├── Key          ← **契約**：進 session、進 agent 指令。用資料本身的鍵，⛔ 不用序號
  ├── Title        ← controller 畫（麵包屑也吃它）
  ├── Draw(ui)     ← 撰寫端一頁一個方法（GUILayout 手感）
  ├── OnPush / OnPause / OnResume / OnClose / CloseEvent
  └── Controller?.Push(new OtherPage(...))    ← 導覽就是 push
```

| 為什麼這樣 | 而不是 |
|---|---|
| **一個 Window 一套 controller** | UCL 的 `Ins` 單例 —— 開第二個視窗會互相蓋，而畫面只像「那頁跑到別的窗去了」 |
| 同一個 page 實例 push 兩次 ⇒ **丟例外** | 安靜接受 —— stack 裡兩個相同引用會讓 `Pop`／`Remove` 移掉哪一個變成看運氣 |
| 導覽路徑存進 session（`nav`） | 只放記憶體 —— CLI 每次都是新 process，兩步操作會變成「按了進去又跳回首頁」 |
| 復原不了的 key **停手並回報** | 悄悄退回根頁 —— 「那頁不存在了」會長得像「你本來就在首頁」 |
| 空堆疊畫一行說明 | 留白 —— 分不出「沒有頁面」與「頁面畫不出來」 |

⚠ **兩側的導覽時序不同**：CLI 是兩趟繪製 ⇒ push／pop 同一次呼叫就看得到；
視窗是 retained 畫布 ⇒ **慢一幀**（跟按鈕回傳值同一個成因）。

⚠ **頁面自帶 id 命名空間**（`SCP_Ui.IdScope(page.Key)`，版面上透明）——
兩頁各有一個沒傳 key 的「篩選」欄位時不會互相吃到對方的 session 值。
顯式 key 不受影響（逐字採用是契約）。

---

## 顯示參數：`SCP_GuiStyle`（尺寸／間距／字級／顏色的單一來源）

```
SCP_GuiStyle
  ├── Scale（0.5〜4，**預設 1.0**）＋ 四段預設 小1× / 中1.5× / 大2× / 特大2.5×
  ├── Scaled(n) / ScaledInt(n)        ← 等同 UCL_GUIStyle.GetScaledSize
  ├── FontSize / TitleFontSize / ItemSpacing* / FramePadding* / CellPadding*
  │   IndentSpacing / ScrollbarSize / ButtonMinWidth / WindowWidth …（＝基準值 × Scale）
  ├── NoteColor / BackgroundColor      ← renderer 無關的顏色（不碰 Vector4 / UnityEngine.Color）
  ├── Text* （TextWidth / TextIndent / TextColumnGap …）⚠ **不吃 Scale**
  ├── Describe()                       ← 一行人可讀的當前設定（尺寸也要有讀數）
  ├── DrawPicker(ui)                   ← 畫四顆尺寸按鈕，回傳被按的那一段（不自己套用、不寫檔）
  └── ToJson() / FromJson()            ← 持久化由呼叫端做（本層零 IO）
```

誰吃它：

| 消費端 | 吃到什麼 |
|---|---|
| `SCP_GuiTextRenderer.Render(root, style)` | `TextWidth` / 縮排 / 欄距（**不含 Scale**） |
| `GuiImGuiRenderer` | Note 顏色、按鈕最小寬、輸入框寬、標題字型 |
| `SenateWindow` | 字級（載入時）、ImGui 全域 metrics（**每幀重灌 ⇒ 版位即時跟著換**）、底色、視窗預設尺寸 |
| `senate.local.json` 的 `ui` 區塊 | 使用者選的 `scale` / `textWidth`（走 `SenateUiStore`） |

⚠ 三件要記住的：

1. **文字模式不吃 Scale** —— 終端機的一格是字元不是像素，乘 2 只會讓表格超出視窗。
2. **字級改了要重開視窗** —— ImGui 的字級綁在載入時建好的 font atlas；
   間距／padding 每幀重灌所以即時生效。這件事頁面上有寫，不假裝生效。
3. **視窗預設尺寸要夾在螢幕內** —— scale 2 時 1280×800 變 2560×1600，
   在 1920×1080 的機器上那是一個標題欄在螢幕外的視窗，而它不會報錯。

（設計理由與血證 → [../Logs/Decisions](../Logs/Decisions.md)（D11／D12））

---

## 字型（Senate.Desktop）

判準不是「字型有沒有載」，是**「這一頁實際用到的每個字元有沒有 glyph」**。

🩸 只載 `GetGlyphRangesChineseFull()` 的第一版：中文正常，但 `✓ ≥ ⇒ ⚠` 全變 `?`
（那份 range 不含符號區），而**缺字不報錯**。
修法：`SenateFonts` 自訂 12 個字元區塊 ＋ merge `seguisym.ttf`。
字級由 `SCP_GuiStyle` 決定，且**本文與標題各載一顆**（同一顆字型兩個字級）——
ImGui 沒辦法把既有 atlas 的字放大而不模糊，所以「標題比本文大」必須在載入時就決定。
pinned handle 存成 static —— font atlas 在設定函式回來之後才建，range 陣列被 GC 移動就會拿到垃圾。
