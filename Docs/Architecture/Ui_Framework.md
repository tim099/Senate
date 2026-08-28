---
title: UI 框架 — 中間層與四種驅動方式
description: immediate-mode 撰寫 API → 節點樹 → renderer 的設計、id 產生規則（顯式 key 是契約）、事件慢一幀的語意、非 UI 操控介面與 session 狀態
last_updated: 2026-08-28
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

## Row 的排版規則（兩個 renderer，**同一棵樹、不同能力**）

| renderer | Row 怎麼排 |
|---|---|
| **ImGui**（有真正的水平版位） | 每個子節點都 `SameLine`；**群組包在 `BeginGroup`／`EndGroup` 裡** ⇒ 群組內每一行從群組起點開始，可以排在別人右邊 |
| **文字**（沒有水平版位） | 連續的 inline 併成一行；**遇到群組就換行**，並印一行 `· ⟨視窗模式：下面這塊排在上一行的右邊⟩` |

誰算 inline 只有一份 —— `SCP_GuiNode.IsInline`（Label／Note／Button／Toggle／TextField）。

🩸 **血證（2026-08-23，Tim 的截圖）**：ImGui renderer 原本對**每一個**子節點都 `SameLine()`，
包括群組。`SameLine` 只把游標移到前一個元件的右邊，而群組會往下長好幾行 ——
於是一顆鈕旁邊放一個**展開的下拉選單**時，整疊選項畫在那顆鈕上面，**疊成一團**。
不報錯，只是看不懂。

⚠ 值得記的是**為什麼我沒看到**：文字 renderer 當時的規則是「全部 inline 才併，
否則整列逐項換行」—— 它不會疊，所以同一棵樹在我這邊完全正常。
「兩個 renderer 互為證人」這件事**只有在有人真的去看另一個 renderer 時才成立**；
我一直只看文字那個。

| 判準 | 而不是 |
|---|---|
| ImGui：群組用 `BeginGroup` 排到右邊 | 直接 `SameLine` —— 群組會往下長，結果是**疊在前一項上面** |
| ImGui：第一版修法是「跟文字模式一樣換行」，後來改掉 | 那不疊了，但**放棄了 ImGui 做得到的事**（一顆鈕旁邊放一整塊垂直內容 ＝ Unity GUILayout 的手感） |
| 文字：換行 ＋ **印一行註記**說「視窗那側是排在右邊的」 | 靜默換行 —— 讀文字輸出的人會以為版面真的是上下排的（我就是這樣漏掉那次重疊的） |
| 文字：**不模擬** ImGui 的水平版位 | 自己再寫一套排版引擎去「預測」另一個 renderer —— 那是第二份產線，而它會漂 |
| 規則不同，但**分類只寫一份** | 各自判斷一次（D14 同一條：分類分岔的症狀不會報錯） |

⚠ **所以文字模式證明的是「內容與結構」，不是「版位」。** 它答得出
「有哪些元件、誰在誰裡面、誰跟誰同一行、順序是什麼」；
答不出「寬度、對齊、有沒有重疊」。⇒ **版位只有截圖是證人**（`--screenshot`）。

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
- `Row` 把**連續的 inline 子節點**串成一行，遇到群組（Box／Table／巢狀 Row）就換行。
  這條規則**兩個 renderer 共用**，而「誰算 inline」只有一份：`SCP_GuiNode.IsInline`。
- ⚠ **已知缺口**：表格還不吃 `--width`（欄寬取自然寬度，窄視窗會超出）。

## 跨輪狀態住在哪（對照 Unity 端的 `UCL_ObjectDictionary`）

UCL 那側的 `UCL_GUILayout.PopupSearch` 把自己的內部狀態（開闔 `_Show`、搜尋字 `_Search`、
分頁子 dict）塞進呼叫端傳進來的 `UCL_ObjectDictionary`。這裡有對應的東西，但**形狀刻意不同**：

| | UCL：`UCL_ObjectDictionary` | 這裡：`SCP_GuiInput` / `SCP_GuiState` |
|---|---|---|
| 誰持有 | **頁面自己**的欄位（`readonly UCL_ObjectDictionary m_Dic`） | **驅動端**：renderer 的三個字典，或 `build/ui_session.json` |
| 命名 | 呼叫端自己給的 dict ＋ 字串 key（＋ `GetSubDic` 巢狀） | **全域 id 命名空間** —— 跟 `--click` / `--set` / `--fold` 是同一組字 |
| 型別 | 任意 `object` | 只有 `string`（Fields）與 `bool`（Toggles / Folds） |
| 活多久 | 頁面物件活著的期間（記憶體） | 跨 process（存進 session 檔） |
| 外部能不能看／改 | 不能 | 能：`--list` 看得到、`--set` 改得動、檔案可 diff |

⇒ 換來的是「元件的內部狀態也有讀數」：下拉選單開著沒開著、搜尋打了什麼、停在第幾頁，
全部是可以被別人檢查的資料，而不是某個頁面物件裡的私有欄位。
**代價要說**：這些內部狀態跟使用者的資料混在同一個 `Fields` 字典裡，
session 檔會看到 `home/page/open` 這種「不是資料的資料」。UCL 那側因為 dict 是頁面私有的，沒有這個問題。

### 缺的那一半：`SCP_Ui.SetField` / `FieldWrites`

`UCL_ObjectDictionary` 是**讀寫**的，元件可以隨手 `SetData`。而這裡原本只有單向：
使用者打字 → renderer 寫 → 頁面讀。於是「頁面自己想改一個欄位」沒有落點 ——
清空搜尋框、下拉選了一項要記起來、翻頁，全都做不到。

```csharp
string aOpen = g.FieldValue("d/open", "0");   // 讀（不畫任何節點）
g.SetField("d/open", "1");                    // 寫（只是記下請求）
```

`SetField` **不改任何狀態**，只把請求記進 `SCP_Ui.FieldWrites`；由驅動端在 Draw 之後套用
（`UiDriver.ApplyWrites` → session／`GuiImGuiRenderer.ApplyWrites` → renderer 的字典）。

| 判準 | 為什麼 |
|---|---|
| 只在**事件發生那一輪**寫 | 每輪無條件寫會跟使用者正在打的字打架，症狀是「我打的字自己跳回去」 |
| `FieldValue` 先看同一輪的 `FieldWrites` | 不然「這一輪選了 A、同一輪後面讀到舊值 B」，那種不一致在 immediate mode 最難查 |
| CLI 的**第一趟繪製之後就套** | 不套的話第二趟畫的是「選之前」的下拉 —— 看起來像選了沒反應 |
| 視窗那側在 Render 之後套 | 這一幀顯示頁面自己算出來的結果，套進 renderer 是給下一幀用（跟按鈕事件同一個「慢一幀」節奏） |

### 勾選那側：`SCP_Ui.ToggleValue`（讀，**沒有**對偶的寫）

```csharp
bool aOn = g.ToggleValue("submodule/only/SCP_Core", iFallback: true);   // 讀（不畫任何節點）
```

🩸 **為什麼需要它（2026-08-28，Submodule 頁）**：`Fold` 的契約是**收合時子節點根本不建**，
於是收合的那一輪讀不到區塊裡任何 `Toggle` 的回傳值。而那些勾選是**資料**
（哪幾顆 submodule 納入這一輪）—— 讓它隨「我把區塊收起來」消失，
等於使用者的設定被靜默丟掉，**而丟掉之後的畫面跟「使用者本來就沒設」長得一模一樣**。

| 判準 | 為什麼 |
|---|---|
| 有 `ToggleValue`（讀） | `FieldValue` 已經是欄位那側的同一條路；勾選少了它就是不對稱，而不對稱的那一側會靜默掉資料 |
| **沒有** `SetToggle`（寫） | 勾選是使用者的資料，頁面自己改它要有很好的理由。欄位那側有 `SetField` 是因為複合元件（下拉／分頁）的內部狀態非它不可 —— 目前沒有呼叫端需要寫勾選 ⇒ 不先開那個口 |
| 收合時把「設定仍然生效」**畫出來** | 一個藏在收合區塊裡的排除清單，看起來跟「沒有排除」一模一樣 |

⚠ 連帶的既有語意（不是 bug，但會咬）：**收合時 `--toggle <id>` 會被「畫面上沒有這個 id」擋下**
（節點不存在）。要改那些勾選得先 `--fold` 展開。

---

## 等寬群組：`SCP_GuiNode.UniformWidth`

`Column(iUniformWidth: true)` / `Box(…, iUniformWidth: true)` ⇒ **直接子節點裡的鈕等寬**
（寬度取那群鈕裡最寬的自然寬度；同群組裡的輸入框跟著切齊右緣）。

它是**意圖不是尺寸** —— 共用層不講像素，renderer 自己決定怎麼達成：

| renderer | 怎麼做 |
|---|---|
| ImGui | 量出最寬那顆，套給同群組的直接子鈕；輸入框用「剩下的寬度」 |
| 文字 | **刻意忽略** —— 終端機一格是字元，等寬只會補出尾隨空白，而那會弄髒 diff |

⚠ 只約束**直接**子節點：巢狀的分頁列（`◀ 上一頁 / 下一頁 ▶`）不會被撐成一樣寬。

🩸 兩格實測踩到的：
1. **無標題的 `Box` 在 ImGui 裡不畫任何東西**（沒有框、沒有標頭），所以它原本唯一的視覺效果
   就是那個 `Indent()` —— 而那個縮排沒有依據（沒有標頭可以縮在下面），只會讓內容跟外面對不齊。
   ⇒ 現在當成純群組容器（版面上透明）。文字那側**有**框，所以照舊縮排。
2. 等寬群組裡的輸入框第一版沿用頁面級的 `LabelWidth`（150×scale ＝ 225px），
   而整個群組才 290px ⇒ 標籤先吃掉 225，輸入框只剩 65，我再把它撐回 165，**整條凸出群組 100px**。
   ⇒ 那個對齊欄是**頁面級的約定**，套進窄群組前提就不成立了。群組裡改成「標籤自然寬 ＋ 剩下全給輸入框」。

---

## 下拉選單（可搜尋）：`SCP_Ui.Dropdown`

概念取自 `UCL_GUILayout.PopupSearch`：一顆顯示現值的鈕 → 點開 → 搜尋框 ＋ 分頁的選項列。

```csharp
string aPick = g.Dropdown("頁面", aOptions, aDefaultKey, "home/page");
if (g.Button("開啟", "home/open")) Open(aPick);
```

⭐ **它沒有新增任何節點型別**。新增一種 `SCP_GuiNodeKind` 要同時改五個地方
（enum／撰寫端／文字 renderer／ImGui renderer／可互動元件清單），而漏掉的那一處不會報錯，
只會「某個 renderer 少畫一塊」。用既有節點（Button／TextField／Box）組出來的元件，
四種驅動方式**天生就會**。

它用的 id（全部是 `iKey` 的前綴，可以直接抄去下指令）：

| id | 是什麼 |
|---|---|
| `<key>` | 展開／收合那顆鈕 |
| `<key>/value` | 選中的值 —— **`--set` 可以直接指定，不必先點開** |
| `<key>/search`　`<key>/page` | 搜尋字／第幾頁（0 起算） |
| `<key>/pick/<value>` | 每一個選項的鈕（id 用 **value 本身**，不用序號 —— 搜尋與翻頁都會改變序號） |

| 判準 | 而不是 |
|---|---|
| 搜尋是**空白分隔的關鍵字，每個都要命中**（子字串、忽略大小寫） | regex —— UCL 那側編譯失敗時退回「不篩」，於是打一個 `(` 會讓清單看起來全部符合，而使用者以為自己在搜尋 |
| **預設摺疊**（`iDefaultOpen: false`） | 一進來就攤開 —— 那是替使用者決定他想選東西，而清單會把版面吃光 |
| 展開時把**頭與選項包成同一個等寬群組** | 頭在外、清單在內 —— 清單就得去對齊「別人的位置」，而它不知道別人在哪 |
| 收合時**子節點根本不建** | 畫了再隱藏（同 `Fold` 的判準） |
| 收合時**不包群組** | 一律包 —— 只有一顆鈕時包群組只會讓文字模式多換一行 |
| 頁碼被搜尋縮短時**夾回去並寫回** | 停在不存在的頁 ⇒ 畫面一片空白，跟「沒有符合的項目」同形 |
| 邊界上**不畫**上一頁／下一頁 | 畫一顆按了沒事的鈕（那看起來像壞的） |
| 現值不在清單裡 ⇒ 標 `⚠(不在清單裡)` | 靜默跳到第 0 項 —— 使用者會以為自己選的是那一項 |
| 選項為空 ⇒ 畫一行「(沒有可選的項目)」 | 什麼都不畫（「沒選項」與「元件沒畫出來」不得同形） |

---

## 頁面堆疊：`SCP_GuiPage` / `SCP_GuiPageController`

```
SCP_GuiPageController（一個 Window 一套 —— 沒有全域單例）
  ├── Push / Pop / PopUntil / PopUntilKey / PopAll / PopToRoot / Remove / Replace
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
| 「回首頁」＝ `PopToRoot`（留最底層那頁） | UCL 的 Close ＝ `PopAll` —— 這裡最底層就是入口頁，清空的結果不是關閉是空白畫面 |
| 頁面可宣告 `OwnsNavBar` ⇒ controller 不再自動畫返回鈕 | 兩邊都畫 —— 不會報錯，只會多一顆 id 是 `page/back#2` 的返回鈕，而 agent 照 `--list` 抄到的就是那顆 |

⚠ **兩側的導覽時序不同**：CLI 是兩趟繪製 ⇒ push／pop 同一次呼叫就看得到；
視窗是 retained 畫布 ⇒ **慢一幀**（跟按鈕回傳值同一個成因）。

⚠ **頁面自帶 id 命名空間**（`SCP_Ui.IdScope(page.Key)`，版面上透明）——
兩頁各有一個沒傳 key 的「篩選」欄位時不會互相吃到對方的 session 值。
顯式 key 不受影響（逐字採用是契約）。

---

### 標準頁骨架：`SCP_GuiToolPage`（工具列 ＋ 內容）

```
SCP_GuiToolPage : SCP_GuiPage
  ├── MenuGroup (string?)    ← 入口頁清單的 opt-in ＋ 分組名（null ＝ 不列）
  ├── DrawToolBar(ui)        ← ◀ 返回｜⌂ 首頁｜<子類的鈕>｜page key
  │     ├── ToolBarButtons(ui)   ← 子類的擴充點（＝ UCL 的 TopBarButtons）
  │     └── ShowBackButton / ShowHomeButton / ShowKeyHint
  ├── DrawContent(ui)        ← 子類實作（＝ UCL 的 ContentOnGUI）
  └── Draw(ui)  **sealed**   ← 工具列 ＋ 內容，不給覆寫
```

概念取自 Unity 端的 `UCL_EditorPage`（TopBar：Back／Close／Help ＋ TopBarButtons ＋ ContentOnGUI）
與 `UCL_CommonEditorPage`（`ShowInPageMenu` 決定要不要列進選單）。**四格刻意不照抄：**

| 這裡 | UCL | 為什麼 |
|---|---|---|
| **一層**（`SCP_GuiToolPage`） | 兩層（`UCL_EditorPage` ＋ `UCL_CommonEditorPage`） | 兩層都只有同一批消費端；分兩層只多一個「該繼承哪一個」的問題 |
| `MenuGroup`（**string?**） | `ShowInPageMenu`（bool） | bool 只能答「要不要出現」，清單一長就是一坨沒結構的鈕；字串同時答「要不要」與「跟誰一國」⇒ 入口頁可以先篩分組。⚠ 空字串 ≠ null：空字串是「列進去、沒有分組名」 |
| `Draw` 是 **sealed** | `OnGUI()` 可覆寫 | 覆寫掉的話返回鈕會不見，而那個症狀看起來像框架壞了，不像自己少呼叫一行 |
| 工具列尾巴印 **page key** | 印類名 ＋ Copy 鈕 | 類名對使用者沒用途，page key 才是 `--page`／session `nav`／麵包屑共用的那個字。沒有 Copy 鈕：共用層碰不到剪貼簿，而「一顆按了沒事的鈕」比沒有那顆鈕糟 |

### 工具列上的「原始碼」鈕

打開這一頁的 `.cs` 所在資料夾（Windows 會**選取**那個檔）。它是 UCL 那顆 Help 鈕的同一格 ——
位置也一樣：導覽鈕之後、子類的自訂鈕之前。

路徑**雙軌**，因為單軌會安靜地壞：

| 來源 | 怎麼來 | 什麼時候用 |
|---|---|---|
| `SourceFilePath` | `[CallerFilePath]` 編譯時烤進去 | 精確；但**只有子類寫了 `: base()` 才有** |
| `SourceFileName` | `GetType().Name + ".cs"` | 退路；由宿主拿去 repo 裡找 |

🩸 **實測（2026-08-23，.NET 10）—— 這件事只能量不能推：**

```csharp
class Implicit : B { public Implicit(int x) { } }           // F = null
class Explicit : B { public Explicit(int x) : base() { } }  // F = "…\p.cs"  ✓
class NoCtor   : B { }                                      // F = null
```

⇒ 隱式 `base()` 與「根本沒寫 ctor」都拿不到。所以它**不能是唯一來源**：
忘了寫 `: base()` 的症狀會是「那顆鈕安靜地不見」，而那是這裡最不想要的失敗形狀。
現在忘了寫只會**掉精確度**（退回用類別名找），鈕還在。

⚠ 宿主端找檔時，找到**多個同名檔就停手並說出來**，不挑第一個 ——
「開到另一個同名檔」跟「開對了」在畫面上長得一樣。

#### 退路階梯：「這一頁是哪個 class」不可以在任何一條路徑上掉在地上

| 宿主有什麼 | 畫面上是什麼 |
|---|---|
| 能開檔案總管 | 「原始碼」鈕 → 選取那個 `.cs` |
| 開檔案總管**這次失敗**（headless／遠端桌面／路徑不在這台機器） | **自動退到複製類別名**，訊息寫「…／已改為複製類別名：HomePage」 |
| 沒裝 reveal、只有剪貼簿 | 換成「複製類別名」鈕（是**取代**不是並列 —— 兩顆回答的是同一個問題） |
| 兩種都沒有 | 兩顆鈕都不畫，`page key` 那行改印 `page key: home（HomePage）` |
| 連剪貼簿都失敗 | 訊息本身帶著那個名字（`⚠ 複製不了（…）—— 類別名是 HomePage`） |

📌 判準：**這條功能的價值是「讓人知道那是什麼」，所以每一種失敗都必須把那個名字說出來。**
⚠ 為什麼「reveal 失敗」要自動退而不是叫人去按另一顆鈕：那顆鈕的出現條件是
「**連 reveal 都沒裝**」，而實際會發生的是「裝了但這次失敗」—— 那時它根本不在畫面上。

⚠ page key **不等於**類別名（`home` ↔ `HomePage`），所以 key hint 不能拿來頂這一格。

⚠ `DrawToolBar` **先收集動作、離開 `Row` 之後才執行** —— handler 裡的 push／pop 會改變
`ShowBackButton` 的答案，在同一輪的 Row 中途改變版面會讓後面幾顆鈕的 id 跟著漂。

⚠ 工具列**不 try/catch**。UCL 那側包了 `Debug.LogException` 吞得起來，共用層沒有 logger ——
吞了就是真的沒有讀數（「那顆鈕沒反應」變成沒人查得到的事）。

---

## 頁面目錄：`SCP_GuiPageCatalog`（key → 工廠 ＋ 選單中繼資料）

入口頁要問三件事：有哪些頁、分幾組、選了怎麼生出來。

```csharp
var aCatalog = new SCP_GuiPageCatalog();
aCatalog.Register(HomePage.PageKey, () => new HomePage(aModel, aCatalog));
aCatalog.Register(DoctorPage.PageKey, () => new DoctorPage(aModel));
SCP_GuiPage? aPage = aCatalog.Create("doctor");    // 認不得回 null
```

⭐ **顯式登記，不反射掃 assembly**（UCL 那側是反射）。兩個理由：

1. 這裡的頁面建構要吃 model（沒有無參 ctor），`Activator.CreateInstance` 生不出來。
2. 反射掃出來的清單會隨「哪些 assembly 剛好載入」而變，而那個差異**不會報錯** ——
   症狀是「同一份程式在別台機器少了兩頁」。

| 判準 | 而不是 |
|---|---|
| 一頁建不出來 ⇒ 記進 `Diagnostics` 並跳過，**由入口頁畫出來** | 靜默略過（一頁悄悄消失，跟「本來就沒有那頁」同形） |
| 同一個 key 登記兩次 ⇒ 丟例外 | 後蓋前 —— `Create` 回哪個、清單列哪個會變成看運氣 |
| 登記的 key 與頁面自己的 `Key` 不一致 ⇒ 記一筆診斷，**以頁面為準** | 沉默 —— session 的 `nav` 存的是頁面的 `Key`，兩份不一致會讓復原失敗 |
| `MenuGroup` 為 null 的頁**仍然造得出來** | 不列＝不存在（`--page` 應該還是進得去） |

⚠ **這個設計的隱含前提：頁面的建構子必須便宜。** 目錄為了讀標題與分組會把每一頁
**建一次再丟掉**（中繼資料快取一次，`Invalidate()` 可重掃）。
所以 `SettingsPage` 的讀檔從建構子搬到了 `OnPush` —— 光是「列出有哪些頁」不該去讀一次設定檔。

---

## 宿主能力：`SCP_GuiHost`

共用層的邊界是「純函式 ＋ 零依賴」，所以它開不了檔案總管、碰不到剪貼簿。
但頁面基底**知道自己想要那顆鈕** ⇒ 由宿主在啟動時把實作掛進來：

```csharp
SCP_GuiHost.RevealInFileManager = SenateShell.MakeRevealer(aRepoRoot);   // Program.Main
```

| 判準 | 為什麼 |
|---|---|
| 沒掛實作 ⇒ 那顆鈕**根本不畫** | 畫一顆按了不會有事的鈕，比沒有那顆鈕糟 |
| `CopyToClipboard` 是 `RevealInFileManager` 的**退路**，不是並列的第二顆鈕 | 兩顆都畫 —— 它們回答同一個問題，只是把工具列變長 |
| 委派回**一行人可讀的結果**（成功也要有話說） | 效果發生在另一個視窗 ⇒ 這個畫面不會有任何變化，沒有那行字的話「開起來了」與「什麼都沒發生」同形 |
| 它是 `static`（而 page controller 不是） | 同一條判準的兩側：**把「只有一個」縮到它真正只有一個的那一層**。「這台機器怎麼開檔案總管」是每個 process 一個；「現在停在哪一疊頁面」是每個視窗一個 |

### `ReadClipboard`（2026-08-28 新增）—— 因為 ImGui 的 `InputText` 吃不到 Ctrl+V

🩸 **全 repo 從來沒有接上 ImGui 的剪貼簿 callback**（`SetClipboardTextFn` /
`GetClipboardTextFn` 零命中；Silk.NET 的 `ImGuiController` 不會自己設）⇒
**視窗模式下每一個輸入框都貼不上**。而一個要求使用者手打絕對路徑的欄位，
實際上就是一個不會被用的欄位。

```csharp
SCP_GuiHost.ReadClipboard = SenateShell.Paste;   // Program.Main
```

回傳 `SCP_ClipboardRead` 的**三格**（`Ok` / `Text` / `Message`）——
「剪貼簿是空的」與「我讀不到剪貼簿」不得同形：壓成一個空字串之後，
一個壞掉的能力會看起來像「使用者沒複製東西」，而那會讓人一直重按那顆鈕。

⚠ 跟 `CopyToClipboard` **刻意分成兩個委派**：一個宿主可能只做得到其中一邊
（寫走 `clip.exe` 很容易，讀在 Windows 上要繞 PowerShell 約半秒 —— 所以那條路
**只掛在按鈕上，不放進每幀會跑的路徑**）。

⚠ 這顆鈕原本是**繞道**（Ctrl+V 還是不能用）。**2026-08-28 已把 callback 接上**，
所以現在它跟 Ctrl+V 走同一條路（見下一節）—— 鈕留著是因為它在**純文字模式**也能用
（`--click submodule/root/paste`），而那一側沒有鍵盤事件。

---

## 宿主會不會一直重畫：`SCP_GuiHost.RedrawsContinuously`

⭐ **這是讓同一份頁面碼放得下「長時間工作」的那一格讀數。**

| 值 | 宿主 | 頁面該怎麼做 |
|---|---|---|
| `true` | ImGui 視窗（連續 render loop） | 丟到**背景執行緒**，每幀顯示進度。同步跑會凍成「沒有回應」 |
| `false`（預設） | 純文字／指令驅動（畫幾趟就結束 process） | **同步跑完才返回**。丟背景等於什麼都不會發生 |

⚠ 預設 `false` 是刻意的保守值：猜錯成 `true` 的症狀是「按了沒事」
（背景工作還沒跑完 process 就結束了），而那跟「這顆鈕壞了」同形；
猜錯成 `false` 只是「畫面卡住一陣子」，看得出來。

⚠ 它**不是**「支不支援背景執行緒」（那是 runtime 的事，兩邊都支援）——
是「**背景工作跑完的時候，還有人在看嗎**」。名字要照著那個問題取，
不然下一個人會在 CLI 模式把它設成 `true`。

⚠ 設定點在 `Program.RunWindow`（**那一種宿主**的性質），不在 `Main` ——
`RevealInFileManager` 那種是**這台機器**的性質，才屬於 Main。

### 第一個消費者：`SubmoduleSyncJob`（本 repo 第一個背景工作）

執行緒契約三條，寫在那個檔的檔頭，違反任何一條的症狀都不是編譯錯誤：

| 契約 | 違反的症狀 |
|---|---|
| 背景執行緒**只碰那個 job 物件**（不碰頁面欄位、不碰掃描結果、不碰 renderer 狀態） | 兩條執行緒同時改同一個集合 ⇒ 偶發的 `InvalidOperationException` 或更糟：讀到一半的資料 |
| UI 執行緒**只透過 `Snapshot()` 讀**（每幀拷一份，不持有內部集合的引用） | 一邊迭代一邊被改 |
| 結果**由 UI 執行緒搬進頁面**（背景不直接寫頁面） | 「誰擁有那份狀態」有兩個答案 |

⚠ 另外三格判準：`Finished` 一定要在 `finally` 裡設（否則畫面永遠停在「執行中」，
而操作鈕在執行中不畫 ⇒ 整頁鎖死）／背景例外一定要 catch 並存起來
（背景執行緒的例外不會自己出現在任何地方，症狀是「進度永遠停在 3/24」而畫面沒有錯誤）／
**進度條的計數是估計值，統計一律讀結構化的 `Rows`**。

⚠ **沒有取消**：git 跑到一半被 kill 可能留下 `index.lock`，而那個殘局比多等一會兒貴得多。

---
## 剪貼簿：Ctrl+C / Ctrl+V（`ImGuiClipboardBridge`）

```
ImGui（Ctrl+C / Ctrl+V）
   │  io.SetClipboardTextFn / io.GetClipboardTextFn   ← ImGui.NET 1.90.x 的位置
   ▼
ImGuiClipboardBridge（Senate.Desktop）── marshalling：UTF-8 ／ NUL 結尾 ／ 記憶體存活期
   ▼
SenateClipboard（Senate.Core）── Windows: Win32 CF_UNICODETEXT ／ 其他平台: SenateShell 的 process 路徑
   ▲
SCP_GuiHost.ReadClipboard / CopyToClipboard（「📋 貼上」鈕、「複製類別名」鈕）
```

⭐ **一份實作、三個消費端**（Ctrl+V／貼上鈕／複製鈕）—— 不會有「鈕能貼、Ctrl+V 不能」的分岔。

🩸 **接上之前**：`SetClipboardTextFn` / `GetClipboardTextFn` 在整個 repo 零命中
（Silk.NET 的 `ImGuiController` 不會自己設）⇒ 視窗模式下**每一個輸入框都貼不上**，
而且**不報錯**。Tim 是在 Submodule 頁的 repo 路徑欄踩到的，但那一格只是它最先被發現的地方。

| 判準 | 為什麼 |
|---|---|
| Windows 走 **Win32**，不走 `clip.exe`／PowerShell | callback 是「使用者按下組合鍵的那一幀」，而 process 啟動要 300〜500ms ⇒ 卡半秒的貼上會被讀成視窗當掉 |
| delegate 存成 **static 欄位** | `GetFunctionPointerForDelegate` **不會**讓 delegate 活著 ⇒ 放區域變數的話 GC 隨時可回收，然後某次貼上跳進一塊已經不是函式的記憶體。症狀是隨機 crash，而且離安裝那一行很遠 |
| 回給 ImGui 的 buffer 在**下一次**讀取時才釋放 | ImGui 的契約是「你回一個指標，我讀完就不管了」，它不替我們釋放；而讀完立刻釋放會在它還在讀的時候把地板抽掉 |
| UTF-8 尾巴補 **NUL** | C 端靠它判斷長度，少一個位元組就會讀到別人的記憶體 |
| callback 裡 **絕不讓例外飛出去** | native → managed 邊界上那是 undefined behavior，而 ImGui 沒有地方接它 ⇒ 最壞回 `IntPtr.Zero`（ImGui 當成剪貼簿是空的） |
| `SetClipboardData` 成功後**不釋放**那塊記憶體 | 所有權轉移給系統 ⇒ 釋放它的症狀不是報錯，是別的程式貼出一段垃圾 |
| `OpenClipboard` **重試** 6 次 | 剪貼簿是全機唯一資源，輸入法／剪貼簿管理員剛好開著時第一次會失敗 —— 那是常態不是錯誤。不重試的症狀是「Ctrl+V 有時候沒反應」，而間歇性失敗最難被回報清楚 |
| 「剪貼簿是空的」與「讀不到剪貼簿」**分開回報** | 壓成空字串之後，一個壞掉的能力會看起來像「使用者沒複製東西」，而那會讓人一直重按 |
| 「裡面是圖片／檔案」也是一種**成功** | `IsClipboardFormatAvailable(CF_UNICODETEXT)` 為 false ⇒ 那不是失敗，是「沒有文字可以貼」 |

**讀數**（`senate selftest --clipboard`，⚠ **opt-in**，因為它會覆蓋使用者的剪貼簿）：

| 項目 | 驗什麼 |
|---|---|
| 剪貼簿 round-trip | `SenateClipboard` 寫入→讀回逐字相同（測試字串含中文與符號 —— Win32 是 UTF-16、ImGui 那端是 UTF-8，兩次轉碼） |
| ImGui 剪貼簿 callback | 走**兩個 callback 本身**：Set 寫入 → Get 讀回逐字相同 ＋ UTF-8 結尾真的有 NUL |

開窗時工具列上方會印一行 `剪貼簿：已接上 ImGui（Ctrl+C / Ctrl+V 可用）`——
⚠ 那行必須存在：沒接上的症狀是「按 Ctrl+V 安靜地沒反應」，跟「這個宿主本來就不支援」同形。

⚠ **仍然沒有讀數的一格**：「ImGui 真的會在 Ctrl+V 時呼叫那個 callback」要人親手按一次 ——
程式驗不到（截圖模式沒有鍵盤事件），所以**不假裝它被驗過**。
⚠ 非 Windows 的 process 路徑（`pbpaste`／`xclip`）同樣沒有讀數（手上沒有那些平台）。
⚠ ImGui.NET **1.91 之後** 這兩格搬到 `ImGui.GetPlatformIO().Platform_*` ——
升版時要跟著改，而接錯地方的症狀是**靜默無效**（所以 `Install` 會讀回指標並回報）。

---

## 視窗接續 CLI 的狀態：`SenateWindow.Seed`

視窗模式原本不吃 session ⇒ 「展開的下拉／收起來的區塊在 ImGui 裡長怎樣」沒有截圖讀數
（`--screenshot` 開起來一定是收合狀態）。現在開窗前會把 session 的
欄位／勾選／摺疊灌成初始值：

```bash
./senate.exe ui --click home/page                          # 用 CLI 擺好狀態（這一側會驗 id）
./senate.exe ui --screenshot build/x.png --page home       # 再開窗截圖
```

⚠ **預設不接續**（要 `--seed-session`，截圖模式自動開）。
🩸 第一版是無條件接續，於是我在終端機測試時點開的下拉，變成 Tim 開窗時「**預設就是展開的**」——
那不是他的操作，是**我的殘留狀態漏過了驅動端的邊界**。
📌 一般形：**把兩個驅動端的狀態接起來很方便，而方便的方向就是髒東西流動的方向。**

⚠ **單向**：視窗不會把使用者在視窗裡的操作寫回 session（雙向要處理「誰後寫誰贏」，
而那是沒有人要求過的功能；單向講出來就不會被誤會）。
⚠ **不含導覽（`nav`）**：視窗要停在哪一頁走 `--page`（兩個機制搶著決定同一件事的結果是
「我明明指定了頁卻開在別頁」）。

---

## 摺疊：狀態是資料，不是 renderer 的內部秘密

```csharp
using (var aFold = g.Fold("執行環境", "doctor/env"))
    if (aFold.Open) { …畫內容… }        // ⚠ 收合時不要建子節點
```

| 判準 | 為什麼 |
|---|---|
| 摺疊狀態住在 `SCP_GuiInput.Folds` / `SCP_GuiState.Folds`（存 session） | 讓 ImGui 自己記的話，狀態在它的 id 空間裡 —— 頁面／CLI／session 都讀不到，於是「我摺起來的東西」換個驅動方式就散了 |
| 收合時**子節點根本不建** | 畫了再隱藏等於沒摺：深樹照樣付整棵的錢，而且文字模式會印出「看不見的內容」 |
| `Folds` 跟 `Toggles` **分開** | 摺疊是看畫面的人的偏好，勾選是資料。混在一起「我把區塊收起來」會被存成一筆資料修改，然後出現在 diff 裡 |
| 可摺疊的框進 `--list`（`HowTo` ＝ `--fold <id>`） | 看不見畫面的人要知道「有東西被收起來」，否則那段內容在他眼裡等於不存在 |
| 文字模式畫 `▼` / `▶` | 沒有標記的話「收起來了」與「裡面是空的」長得一模一樣 |

⚠ 視窗那側**慢一幀**：這一幀點開的區塊，內容要等下一幀頁面重畫才會有
（子節點在收合時沒被建出來 —— 那正是它省事的原因）。

## 版位：欄位名稱在左邊

ImGui 原生把 label 畫在控件**右邊**，一排欄位下來眼睛要左右跳。
`GuiImGuiRenderer.LabelLeft` 把它挪到左邊並對齊到 `SCP_GuiStyle.LabelWidth`（基準 150 × scale）。
⚠ 標籤比欄寬長時**不裁字、直接推開** —— 裁掉的字不會報錯，只會讓人讀不懂那一格是什麼
（現況：`AgentCommandsRoot` 這種長名字會把自己的輸入框推右邊，這是已知取捨）。
文字 renderer 本來就是 `名稱: ⟨值⟩`，兩側一致。

---

## 自動繪製與自動序列化（反射三層）

```
SCP_Reflect（快取層 —— 唯一入口）
  ├── SchemaOf(type)      ← 型別 → 成員描述（快取；immediate mode 每幀都要，不能每次重掃）
  ├── AllTypes / TypeByFullName / ResolveTypes(name)   ← 撞名時回全部，不自動挑第一個
  ├── TryParse(type, text, allowNull, out value, out err)   ← 字串 → 值（invariant）
  ├── TryCreate(type, out err) / ClearCache() / Describe()
  ▼
SCP_TypeSchema ＋ SCP_MemberSchema（描述層 —— 兩個消費端共用同一份分類）
  ├── SCP_ValueKind：Bool / Integer / Decimal / Text / Choice / Nested / ListOf / MapOf / Unsupported
  ├── Get(owner) / TrySet(owner, value, out err) / CanWrite / IsNullable / ElementType
  └── [SCP_Ignore] 兩邊都跳過
  ▼                                    ▼
SCP_JsonMapper（物件 ↔ SCP_JsonData）   SCP_GuiInspector（物件 ↔ 畫面）
  ToJson / Populate / Create             Draw(ui, target, key) → { Changed, Notes }
```

⭐ **為什麼要有中間那層**：序列化與繪製各自判斷「這個成員是數字還是清單」的話，
遲早出現**畫得出來但存不進去**（或反過來）—— 而那不會報錯，只會有一個欄位改了之後回不來。
⇒ 分類只有一份，兩邊都吃它。以後型別加一個欄位，設定頁**一行都不用改**就會出現。

| 判準 | 而不是 |
|---|---|
| 不支援的成員**留在清單裡並帶原因**（畫面上一行灰字、JSON 那側進 Diagnostics） | 靜默略過 —— 消失的欄位讓人以為資料本來就沒有那一格 |
| 讀取端：JSON 缺 key ⇒ **保留物件現值** | 寫 0／null —— 「沒設過」與「設成 0」不得同形 |
| 型別不合 ⇒ 不寫入 ＋ 記一筆 | 盡力而為的轉換（`"abc"` → `0` 比整筆失敗難查十倍） |
| 打錯字 ⇒ 不寫入、**不清掉使用者打的字**，畫一行「現值還是 X」 | 靜默還原 —— 「我打了字它自己跳回去」找不到人問 |
| struct 成員改完**寫回去** | 就地改 —— 值型別是複本，不寫回等於沒改而且不報錯 |
| 循環參考／超過深度 ⇒ 停手並記一筆 | 遞迴到 stack overflow（崩潰訊息不會說是哪個欄位） |
| 介面／抽象成員 ⇒ Unsupported | 猜實作型別 —— 猜錯的症狀是「存進去的是另一個型別的資料」 |

⚠ **已知邊界**（都寫在 `UnsupportedReason` 裡，不是漏看）：陣列（長度變更要重建實例，用 `List<T>`）、
非 string key 的字典、非 `List<T>` 的序列、多型（沒有型別標記）、private 成員（**刻意**不收：
預設把別人的內部狀態攤到畫面上並存進 JSON 是不可逆的決定）。

⚠ **清單項目的 id 是索引** ⇒ 增刪之後後面每一項的 id 都位移（欄位值可能跟到隔壁）。
本層沒有穩定的項目鍵可用，所以**畫一行警告**而不是假裝沒事；字典用 key 當 id，沒有這個問題。

Senate 的第一個消費者是 `SettingsPage`（`ui --click doctor/open-settings`）——
那一頁沒有一行欄位碼。

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
