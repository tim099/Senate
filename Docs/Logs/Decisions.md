---
title: 設計拍板紀錄（ADR）
description: Senate 與 SCP_Core 的關鍵決策、當時的理由、以及被實測推翻或修正的部分。新決策往下加，不改舊條目
last_updated: 2026-08-22
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🧭 設計拍板紀錄

> 規矩：**新決策往下加，不改舊條目。** 被推翻的條目留著並在下面標註被哪一條取代 ——
> 「當時為什麼那樣想」跟「現在怎麼做」一樣重要，砍掉前者會讓下一個人重犯同一次。

---

## D1 · 用 C#／.NET 做後台，不用 Python／TS（2026-08-22）

**決策**：Senate 以 .NET 10 為主，ML／媒體類能力（STT、OCR、影像）留給 python sidecar。

**理由**：① 同進程既是 server 又能長出介面（不必為 UI 開第二套資料通道）
② 編譯期擋錯，對「參數名打錯靜默取預設值」那一族特別對症 ③ Windows 單資料夾部署。

**代價**：ML 生態要走跨進程；UI 若要遠端存取需另做（見 D5）。

---

## D2 · Senate 是真相源，Unity 降級成 client（2026-08-22，Tim）

**決策**：規則與狀態的所有權往 Senate 收；Unity 只保留真的需要 Editor API 的事。

⚠ **但「以 Senate 為準」這句話本身不會讓任何事情發生** ——
真正讓 Unity 端不再是真相源的，是「Unity 端改成讀 Senate 那份 JSON」這個動作。
在那行碼落地之前，Senate 只是第二份實作，而兩份實作裡沒有一份是「準」的。

**過渡期規矩**：每種資料只有一個寫入者；git index 靠心跳互斥
（詳見 [../Architecture/Overview](../Architecture/Overview.md)）。

---

## D3 · 共用碼寫「Unity 方言」，放 SCP_Core submodule（2026-08-22，Tim）

**決策**：共用部分以 Unity 支援的語言／API 子集撰寫（C# 9 / netstandard2.1 / 零第三方套件），
放 `SCP_Core` submodule，同步給兩邊。

**護欄**（兩道，方向相反）：csproj 的 `LangVersion 9.0` 擋太新的語法；
asmdef 的 `noEngineReferences` 擋 Unity 專屬 API。⇒ 「請記得用舊方言」變成編譯錯誤。

**邊界**：只放純函式＋零依賴；碰 IO／log／UI 的不進來。

---

## D4 · JSON 層照概念重寫，不逐字搬 UCL_Core（2026-08-22，Tim）

**決策**：概念沿用 `UCL_Core.JsonData`（節點樹、下標取值、隱式轉換），但重寫。

**理由**：逐字搬會拖進 `IJsonSerializable → UCL.Core.UCLI_CopyPaste` 的相依鏈，
而 `JsonData` 裡還埋著一個 `OnGUI()`（IMGUI 繪製）—— 那等於把 Unity 帶進共用層。

**三個刻意的設計**：① **Missing 是型別不是空值**（讀它丟例外並附路徑；要寬鬆得顯式）
② key 保留插入順序、非 ASCII 不轉義、縮排用 tab（為了輸出可 diff）
③ 數字保留原文，讀取時才轉（不讓 `long` 繞一趟 `double` 掉尾數）。

**驗收**：拿 **Unity 端寫出來的真檔案**跑 round-trip，不是自己造樣本。

---

## D5 · UI 走「GUILayout 門面 ＋ 中間層節點樹」（2026-08-22，Tim）

**決策**：撰寫端保留 immediate-mode 手感，但呼叫只建樹；renderer 決定畫成什麼。
UI 底層（節點樹／撰寫 API／文字 renderer／操控介面）放 SCP_Core，
碰硬體的（ImGui／視窗／截圖）留 Senate.Desktop。

**理由**：純 ImGui 的兩個硬限制是「只能本機」與「中文 IME 待驗」；
中間層讓將來換 Blazor／HTML／Unity GUILayout 只換 renderer。
**而它順手買到的是「UI 有讀數」**：文字可 diff、互動可用指令重放、畫面可落 PNG。

**尚未驗**：中文 IME 輸入（畫面上還沒有輸入框可以打字）。

---

## D6 · 顯式 key 逐字採用（2026-08-22，實測修正）

**決策**：傳了顯式 key 就**逐字**當 id，不加 layout 前綴、不做 slug。

**被推翻的第一版**：所有 id 都走「layout 路徑 ＋ slug」
⇒ `Button("重新取讀數", "doctor/refresh")` 在 `Row` 裡變成 `row/doctor-refresh`，
**把按鈕搬進／搬出一個 Row 就換 id** —— 正是傳 key 要防的漂移。

**抓到它的是**：`senate ui --click doctor/refresh` 回 `✗ 畫面上沒有這個 id`。
那個守衛（不存在的 id 擋下並 exit 2，不靜默）當場回本。

---

## D7 · 不用 `PublishSingleFile`（2026-08-22，實測）　⚠ **結論下得太廣，見 D9**

**決策**：資料夾 publish ＋ 根層啟動器（`senate.cmd` / `senate`）。

**兩個實測的坑**：① Silk.NET 原生 GLFW 在單檔自解壓後找不到 ⇒ 開窗丟
`PlatformNotSupportedException`，而**文字模式照常運作**（所以只有真的開窗才會現形）
② `IncludeAllContentForSelfExtract` 讓 app base 變成 temp 目錄 ⇒ repo 根解析失準且不報錯。

**推論（寫進 build 腳本）**：出廠驗收必須**真的跑一次＋真的開一次窗**，
「build succeeded」只證明編譯器沒抱怨。

---

## D8 · 文件拆兩份（2026-08-22，Tim）

**決策**：`README.md` 給使用者（非程式人員）；維護用文件放 `Docs/` 並分類，
參考 LY 專案 `Docs/` 的慣例（frontmatter ＋ 主題分類），**不做多語系**。

**理由**：兩種讀者要的東西相反 —— 使用者要「怎麼做」，維護者要「為什麼這樣」。
混在一份裡的結果是兩邊都嫌長。多語系鏡像則是一份會漂的翻譯，比沒有翻譯糟。

---

## D9 · single-file 可以用，但**原生 DLL 必須留在 exe 旁邊**（2026-08-22，修正 D7）

**D7 錯在哪**：我把「包原生 DLL 的單檔開不了窗」推廣成「單檔不能用」，
中間少了一次量測 —— 而那次量測很便宜（換一個旗標再 publish 一次而已）。

**實測三種組合**：

| 組合 | 開窗 | repo 根解析 |
|---|---|---|
| `IncludeNativeLibrariesForSelfExtract=true` | ✗ `PlatformNotSupportedException`（Silk.NET 找不到原生層） | ok |
| `IncludeAllContentForSelfExtract=true` | ？（沒測到這步） | ✗ app base 變 temp 目錄 |
| **single-file ＋ 原生 DLL 留在旁邊** | **✓** | **✓ 最好**（exe 就在 repo 根，`AppContext.BaseDirectory` 直接是根） |

**決策**：走第三種。根層產物 = `senate.exe`（74 MB）＋ `cimgui.dll` ＋ `glfw3.dll`，三個都不入版控。
⇒ Tim 要的「在 Senate 這一層看得到執行檔」成立，而且是真的 `.exe`（可雙擊）。

**判準（可以搬去別處用）**：**「這個做法不行」與「這個旗標不行」是兩件事。**
把旗標的失敗推廣成做法的失敗，會讓一整條可用的路被自己封掉。

---

## D10 · `.ps1` 一律存 UTF-8 **with BOM**（2026-08-22，Tim 回報）

**現象**：Tim 跑 `build.ps1` 之後「根目錄沒看到執行檔」。
真因不是產物路徑，是**整支腳本 parse error**：

```
The string is missing the terminator: '.
Missing closing '}' in statement block or type definition.
```

**成因**：Windows PowerShell **5.1** 讀 `.ps1` 沒有 BOM 時用 ANSI(cp950)
⇒ 中文變亂碼，連字串終止符都被吃掉。`setup.ps1` 同樣中彈。

**決策**：本 repo 所有 `.ps1` 存成 **UTF-8 with BOM**，並在檔頭第二行加
`[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`（讓 PS 自己印的中文也對）。
順帶不再用 backtick 續行（跨行參數改寫成單行）。

⚠ **這隻只有在 PowerShell 跑才會現形** —— 我全程用 Git Bash 測 `build.sh`，
所以「兩支腳本等價」這句話當時是**推論不是讀數**。等價的東西也要各跑一次。

**附帶修的**：覆寫剛 publish 出來的 exe 會撞兩種鎖（exe 正在執行中／防毒正在掃 74MB 檔）
⇒ 兩支腳本都加重試三次，仍失敗就明說是哪一種原因（實撞過一次 `IOException`，重跑就好）。
