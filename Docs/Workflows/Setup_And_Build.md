---
title: 配置與建置流程
description: setup / build 兩支腳本的職責邊界、出廠驗收要驗什麼、為什麼不用 PublishSingleFile（兩個實測的坑）、產物與版控
last_updated: 2026-08-28
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
---

# 🔧 配置與建置

## 兩支腳本，一條規矩

| 腳本 | 做什麼 |
|---|---|
| `setup.ps1` / `setup.sh` | 檢查前置 → `dotnet build` → `senate init`（建本機設定，已存在則不覆寫）→ doctor |
| `build.ps1` / `build.sh` | `dotnet publish`（self-contained）→ 放根層啟動器 → **出廠驗收** |

**規矩**：腳本只做編排，**所有判斷都在 C# 裡**（`senate doctor`）。
🩸 理由：檢查邏輯寫進腳本 = PowerShell 版與 sh 版兩份會漂的實作，
而漂掉的症狀是「兩台機器都說 OK，但檢查的東西不一樣」。

---

## 出廠驗收：build 綠燈不算數

`build` 的最後**真的跑兩件事**：

1. `senate doctor` —— 證明那顆 exe 起得來、路徑解析對、設定讀得到
2. `senate ui --screenshot build/build_check.png` —— **真的開一次窗**

⚠ 第 2 項不是裝飾。self-contained 最常壞的地方在執行期，而**文字模式照常運作**
⇒ 開窗的錯只有真的去開窗才會現形（見下節血證）。

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

🩸 實撞（Tim 回報）：`build.ps1` 跑完「沒看到執行檔」——
真因不是產物路徑，是腳本根本沒跑到 publish 那行。`setup.ps1` 同樣中彈。

修法：存 UTF-8 with BOM ＋ 檔頭加 `[Console]::OutputEncoding = [System.Text.Encoding]::UTF8`
＋ 不用 backtick 續行。

⚠ 而這隻**只有在 PowerShell 跑才會現形** —— 我全程用 Git Bash 測 `build.sh`，
所以「兩支腳本等價」當時是推論不是讀數。**等價的東西也要各跑一次。**

## 覆寫 exe 會撞鎖

`senate.exe` 正在執行中（Windows 不准覆寫）或防毒正在掃剛寫完的 74 MB 檔
⇒ 兩支腳本都重試三次，仍失敗就明說是哪一種原因（不要只丟 `IOException`）。

## 全域安裝（install.sh / install.ps1）

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
  `senate --help` —— 寫進 registry 不算數，解析得到才算。
- ⚠ 已開著的終端機不會生效：PATH 是 process 啟動時複製的，開新視窗。

## 產物與版控

一律不入版控：`bin/` `obj/` `build/` `publish/` `senate.cmd` `senate` `senate.local.json` `imgui.ini`

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
