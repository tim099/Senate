---
title: 配置與建置流程
description: setup / build 兩支腳本的職責邊界、**改完 code 先 build 再對 exe 驗**、出廠驗收三格、single-file 的真正判準（實測修正過一次）、產物與版控
last_updated: 2026-09-02
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🔧 配置與建置

## 兩支腳本，一條規矩

| 腳本 | 做什麼 |
|---|---|
| `install.ps1` / `install.sh` | **一台機器的唯一入口**：檢查前置 → 呼叫 `build.*` → `senate init`（建本機設定，已存在則不覆寫）→ 掛使用者 PATH → 驗收。`--uninstall` 還原 |
| `build.ps1` / `build.sh` | `dotnet publish`（self-contained，直接產出 `publish/senate.exe`）→ 在根層放雙擊用的 `senate.lnk` → **出廠驗收** |

> ⛔ **build 只有一個入口。** install 不准自己另寫一條 `dotnet build`。
> 🩸 2026-09-01 實測：這台當時有 **五顆可執行產物、三種年份** ——
> 舊的 `setup.*` 跑 `dotnet build Senate.slnx -c Release`（framework-dependent DLL），
> `build.*` 跑 publish（self-contained single-file exe），兩顆**跑起來長得一模一樣**，
> 而 setup 那顆落後整整一天。⇒ 「我測過了」測的是哪一顆沒有人答得出來。
> 第二條 build 路徑不是備援，是分岔。

**規矩**：腳本只做編排，**所有判斷都在 C# 裡**（`senate doctor`）。
🩸 理由：檢查邏輯寫進腳本 = PowerShell 版與 sh 版兩份會漂的實作，
而漂掉的症狀是「兩台機器都說 OK，但檢查的東西不一樣」。

---

## ⛔ 改完 code 要驗，就**先 build 再對 exe 跑**（Tim 2026-08-30 拍板）

**判準：你要交付的是 `senate.exe`，那驗收就必須跑在 `senate.exe` 上。**

```bash
./build.sh          # publish → 放根層 → 出廠驗收（doctor + selftest + 開窗）
./senate.exe <你要驗的那件事>
```

### 為什麼這是一條規矩而不是建議

`dotnet run --project src/Senate.Cli` 跑的是 **Debug、framework-dependent 的 DLL**
（`src/Senate.Cli/bin/Debug/net10.0/`）；根層的 `senate.exe` 是
**Release、self-contained、single-file**。**兩個不同的二進位檔。**

⇒ 「我改完了、`selftest` 全綠」與「你手上那顆 exe 全綠」是**兩本帳**，
而它們在畫面上長得**一模一樣**。

> 🩸 **2026-08-30 的現場**：agent 整個下午的驗證迴圈都是 `dotnet run`，
> 而 Tim 每次要測都得自己先跑一次 `build.sh` —— 他問「目前是如何驗證的」才發現
> 那兩條路從來沒接起來。當天量到的另一格：published single-file 底下**反射照常運作**
> （`頁面發現` 那項 exe 上也是 ✓）—— 那是讀數不是保證，所以更要每次都跑。

⚠ `dotnet run` 不是不能用 —— 它是**迭代**用的（秒級）。
但**收工前的那一次驗收必須是 exe**，而且報告裡要說清楚驗的是哪一個。
「只驗過 Debug」是完全合法的交付狀態，把它講成「驗過了」才不是。

---

## 出廠驗收：build 綠燈不算數

`build` 的最後**真的跑四件事**（都跑在剛產出的那顆 exe 上），跑完再開一顆**常駐視窗**：

1. `senate doctor` —— 證明那顆 exe 起得來、路徑解析對、設定讀得到
2. `senate selftest` —— 24 項自我對拍。**失敗回 exit 1，會讓整個 build 判未過**
3. `senate ui --screenshot build/build_check.png` —— **真的開一次窗**
4. **Server round-trip**（TASK-0100）—— 起一顆臨時 `senate server start`（背景）、`senate cmd server-ping --arg echo=build-check`、
   `server stop` 收掉。selftest 對拍的是 result 檔的 schema，這一格驗的是「一顆 CLI 送、一顆 Server 接、result 回來」那條路本身。
   ⚠ 它起的 Server 是驗收用的臨時 process，log 在 `build/build_server.log`／`build/build_ping.log`；publish 前的 `server stop` 保證沒有別顆在跑。


⚠ 第 3 項不是裝飾。self-contained 最常壞的地方在執行期，而**文字模式照常運作**
⇒ 開窗的錯只有真的去開窗才會現形（見下節血證）。

⚠ 第 2 項是 2026-08-30 才加的。在那之前出廠驗收只有 doctor 與開窗 ——
**那 24 項自我對拍從來沒有對 exe 跑過**。
📌 修法選的是「長在必經路上」而不是「文件叫人記得跑」：
第三階（記得注意）只在前兩階都做不到時才用，而這一格做得到第二階。

四格**分開印**（`doctor=? / selftest=? / gui=? / server=?`）—— 壓成一句「驗收未過」
會讓人不知道要去看哪一格。

### 收尾：**開一顆常駐視窗**（不是驗收格，Tim 2026-09-04 拍板）

四格跑完之後 `build` 會 `senate ui --window` 開一顆**留著**的視窗 —— build 之後你本來就要開它，
那一步交給腳本，人不用記得重開。

- ⛔ **它不是第五格**：不看 exit code、不擋 build 判定。「畫得出來」由第 3 項的截圖擋。
- ⚠ 它會**鎖住 `publish/senate.exe`** ⇒ 下一次 build **開頭會自己把它收掉**
  （先 `CloseMainWindow()`，2 秒不走才 `Kill()`；比對的是 process 的 `Path`，別份 clone 的 senate 不動）。
  🩸 這一格是必要的配套：2026-09-03／09-04 各撞一次 `GenerateBundle … Access to the path … is denied`，
  兩次佔住 exe 的都是一顆開著的視窗，而**錯誤訊息不會告訴你是誰**。
- `nohup`／`Start-Process` 起，log 落在 `build/build_window.log`。

> 🩸 **退場的那一格**：原本第 5 項是 `ui --soak 10`（開真視窗轉十秒、門檻 10 fps），
> 2026-09-04 Tim 拍板換成常駐視窗、`--soak` 不再進 build。
> ⚠ 換掉的代價要講清楚：**「畫得動」從此沒有機器在量** —— 凍住的視窗截起來跟正常的一模一樣，
> 而那正是這一格當初存在的理由（@basecamp 2026-08-28 headless 全綠交付、Tim 開一次窗就卡死）。
> ⇒ 現在擋它的是**人的眼睛**：build 收尾那顆窗就在你面前，動不動一眼看得到。
> `senate ui --soak <秒>` 這支旗標**還在**（`Cli_Reference`），要量隨時可以手動跑。

---

## 🩸 single-file 的真正判準

實測 2026-08-22（**一開始我把結論下得太廣，說「不要用 single-file」——那是錯的**）：

| 組合 | 開窗 | repo 根解析 |
|---|---|---|
| `IncludeNativeLibrariesForSelfExtract=true` | ✗ `PlatformNotSupportedException: Couldn't find a suitable window platform` | ok |
| `IncludeAllContentForSelfExtract=true` | 沒測到 | ✗ app base 變 temp 目錄 ⇒「往上找 `.git`」失準且不報錯 |
| **single-file ＋ 原生 DLL 留在 exe 旁邊** | ✅ | ✅ 最好 |

⇒ 現在的做法是第三種。根層產物：

```
senate.exe     74 MB，self-contained 單檔（可直接雙擊）
cimgui.dll     ↖ 原生層，必須跟 exe 同層
glfw3.dll      ↙ 少一顆的症狀是「文字模式好、開窗掛」
```

**判準**：「這個做法不行」與「這個旗標不行」是兩件事。把旗標的失敗推廣成做法的失敗，
會讓一整條可用的路被自己封掉。

## ⚠ `.ps1` 必須存 UTF-8 with BOM

Windows PowerShell **5.1** 沒有 BOM 就用 ANSI(cp950) 讀 `.ps1`
⇒ 中文變亂碼、字串終止符被吃掉、**整支腳本 parse error**。

🩸 實撞①（Tim 回報）：`build.ps1` 跑完「沒看到執行檔」——
真因不是產物路徑，是腳本根本沒跑到 publish 那行。當時另一支腳本同樣中彈。

🩸 實撞②（2026-09-01，agent 自摔）：改寫 `install.ps1` 時**用不帶 BOM 的方式寫回檔案**，
下一步 parse-check 就吐出一整片亂碼與 `缺少 '}'`。
⚠ 值得記的不是「又撞了一次」，是**這條規矩就寫在本節、而我動手前沒有讀它** ——
同族的修法不是「記得加 BOM」（第三階），是**改完 `.ps1` 一律 parse-check**（第二階，長在必經路上）：

```bash
powershell -NoProfile -Command "\$e=\$null; [void][System.Management.Automation.Language.Parser]::ParseFile('<abs>.ps1',[ref]\$null,[ref]\$e); if(\$e.Count){\$e|%{\$_.Message}}else{'OK'}"
```

位元組層的快篩（`cmp` 家族，不用眼睛看）：`head -c 3 <檔> | xxd` 應該是 `efbb bf`。

修法：存 UTF-8 with BOM ＋ 檔頭加 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`
＋ 不用 backtick 續行。

⚠ 而這隻**只有在 PowerShell 跑才會現形** —— 我全程用 Git Bash 測 `build.sh`，
所以「兩支腳本等價」當時是推論不是讀數。**等價的東西也要各跑一次。**

## 執行檔只有一顆，住在 `publish/`

`<AssemblyName>senate</AssemblyName>` 讓 publish 直接產出 `publish/senate.exe` ——
**不再複製一份到根層**。PATH 掛的是 `publish/`；根層只留 `senate.lnk`（Windows 捷徑，只服務滑鼠）。

🩸 為什麼不是捷徑或連結（2026-09-01 實測，兩種都量過）：
- **symlink**：這台建不出來（權限不足，開發者模式沒開）⇒ 它是**每台機器的前置條件**。
- **hardlink**：建得出來，但 publish 會打斷它（link 數 2→1、inode 分岔），
  外層會**靜默停在舊版**。⚠ 那次 `cmp` 還回報 byte-identical（來源只 touch 過 mtime）——
  **內容比對在這一格給假綠燈，真正的證人是 link count**。

⇒ 連帶消失的還有「覆寫 exe 撞鎖」那個老問題：沒有複製動作，就沒有覆寫。

## publish 前先停 Server，並把 build id 塞進 exe（TASK-0102）

兩支 build 腳本在 `dotnet publish` **之前**多做兩件事：

1. **`publish/senate.exe server stop`**（舊 exe 存在才跑）。Server 是前景永駐（Tim 2026-09-02 拍板），
   publish 覆寫 exe 必撞「正在執行中」的鎖 —— 那正是上面 D10 那段重試三次在對付的東西。
   stop 是冪等的（沒在跑也 exit 0），所以無條件呼叫。⚠ 舊 exe 不認 `server` 時（升級到本版的第一次 build）
   會印 `⚠ server stop 回非零` 然後照常 publish —— 預期中的退路，之後每次都是新 exe。
2. **`-p:InformationalVersion=<git short sha>[-dirty].<UTC 時間>`** ＋ `IncludeSourceRevisionInInformationalVersion=false`。
   Server 心跳帶它、`server status` 拿自己的比；對不上就是「舊 exe 還在替新 exe 跑」那本帳。
   ⚠ 關掉 SDK 自動接 `+sha` 是必要的：不關的話兩邊字串永遠對不上，而症狀是「每次都說版本不符」。

⇒ 這一格是 §「先 build 再對 exe 跑」的機械版：**兩顆長得一樣的 exe，現在有一個地方會說出它們不一樣。**

## 安裝與移除（install.sh / install.ps1）

「像 python 一樣全域」＝ **PATH 找得到**，所以安裝工具只做一件事：把 repo 根
（senate.exe 與原生 DLL 的所在）寫進**使用者 PATH**（HKCU，不碰系統 PATH、免管理員）。
不搬檔案、不做 shim —— 搬出去的 exe 是第二份會過期的產物（repo 裡 build 了新版、
PATH 上還是舊的，而兩顆 exe 印一樣的 usage）。

- **寫入走 .NET `[Environment]::SetEnvironmentVariable(...,'User')`，不用 `setx`** ——
  setx 有 1024 字元截斷，超過的部分**靜默丟掉**（症狀是別的工具突然找不到了）；
  .NET 這條路無長度限制且會廣播 WM_SETTINGCHANGE。
- 冪等（已在 PATH 不重複加）；`--uninstall` / `-Uninstall` 逐段比對移除
  （**逐段**不是子字串 —— 子字串會把 `D:\Unity\Senate2` 誤判成同一條）。
- 出廠驗收：用「系統＋使用者 PATH 重組」模擬新視窗，從 `%TEMP%` 解析並跑一次
- **build 沒過就不掛 PATH** —— 裝一條指向壞產物的 PATH，之後每個錯都會被怪到 PATH 上。

> ⛔ **刻意沒有 `--no-gui`**（Tim 2026-09-01 拍板）。開窗那格在遠端桌面／無 GPU 的機器上會失敗，
> 而出口已經有一個：`./install.sh --skip-build`（`-SkipBuild`）。
> 再加一個「跳過開窗」的旗標，等於在必經路上開一條**驗收其實沒跑完**的岔路 ——
> 而那條路一旦存在，趕時間的人就會走它，然後「我 build 過了」與「我驗收過了」重新變成同形。
  開窗那格在遠端桌面／無 GPU 會失敗 ⇒ 出口是 `--skip-build` / `-SkipBuild`（沿用現有產物）。

### 移除做到哪一層（判準：**這個東西掉了，使用者要不要重做工？**）

| 層 | `--uninstall` | `--uninstall --purge` |
|---|---|---|
| 使用者 PATH | ✅ 移除（可逆、零資料損失） | ✅ |
| build 產物（`senate.exe` / 原生 DLL / `publish/` / `build/` / 各專案 `bin` `obj`） | ✅ 移除（可重建） | ✅ |
| `SenateData/`（人編輯過的設定與偏好） | ⛔ **保留**，並印出怎麼刪 | ✅ 移除 |
| 原始碼與 git 歷史 | ⛔ 一個字都不動 | ⛔ 一個字都不動 |

⚠ 移除清單在 `install.sh` 與 `install.ps1` **各有一份**，改一邊要同時改另一邊 ——
漂掉的症狀是「用 .sh 裝、用 .ps1 移除，結果少刪兩樣」，而那不會有人喊。
  `senate --help` —— 寫進 registry 不算數，解析得到才算。
- ⚠ 已開著的終端機不會生效：PATH 是 process 啟動時複製的，開新視窗。

## 產物與版控

一律不入版控：`bin/` `obj/` `build/` `publish/` `senate.cmd` `senate` 與整個 `SenateData/`
（只放行樣板 `SenateData/config/senate.local.example.json`；版面與判準見
[`Data_Layout`](../Architecture/Data_Layout.md)）

其中 `obj/project.assets.json` 與 `*.nuget.g.props` 帶有
`packageFolders = C:\Users\<你>\.nuget\packages\` —— 那是**這台機器**的 NuGet 快取位置。
它是 restore 產物不是設定：進了版控就是每個人每次 commit 都帶一筆假 diff，
而 clone 到別台機器還會指著不存在的路徑。

---

## Solution 檔的兩個坑

- `.NET 10` 的 `dotnet new sln` 產出的是 **`Senate.slnx`**（新的 XML 格式）。
  `dotnet` CLI 完全支援，但舊版 Visual Studio 開不了。
- 🩸 **「參照得到」與「IDE 看得到」是兩件事**：`SCP_Core` 一開始只有 `ProjectReference`，
  沒被加進 `.slnx` ⇒ `dotnet build` 一路綠燈，但方案總管裡看不到它（`3 of 3 projects`）。
  ⇒ 加進 `/submodule/` solution folder（不是 `/src/`），讓「這是外部 submodule、改它要另外 commit」
  在方案總管裡一眼看得出來。

---

## 相關文件

- 指令與 exit code → [../API/Cli_Reference](../API/Cli_Reference.md)
- 分層與共用碼邊界 → [../Architecture/Overview](../Architecture/Overview.md)
