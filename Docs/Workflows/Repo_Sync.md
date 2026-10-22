---
title: 資料同步到遠端 repo（sync.*）—— 任務單、閱讀筆記、工作記憶
description: 資料樹裡的資料夾跟遠端 repo 的某條 branch 同步：怎麼設、誰在什麼時候拉推、缺目錄與已有資料夾怎麼自動補上、兩台同時寫怎麼接、接不上的衝突怎麼手動解；以及經 WMI 起的 Server 跑 git 同步會被 Defender 砍的實測。
cmds: [booknotes-sync, workmemory-sync, task-sync]
---

# 資料同步到遠端 repo（`sync.*`）

> 一句話：資料樹裡的某個資料夾 ＝ 遠端某個 repo 某條 branch 的**工作副本**；讀寫的路徑一行都不用改，寫完自動 commit＋push，兩台同時寫接得上的自動接、接不上的擋下來交給人。
> Tim 2026-10-22：「支援填寫git repo與branch 自動同步到遠端的git repo」「最小功能repo內 缺的資料夾&設定都能在使用時自動補上」「不能自動解的要抱錯通知手動處理」。

設定在後台「路徑管理」頁最下面的「同步遠端」格（`senate.local.json` 的 `sync.<名>`）：**repo、branch 兩格都填才同步**；空 ＝ 只寫本機（跟以前一模一樣）。

| 設定 | 本機資料夾 | 誰在什麼時候同步 | 細節 |
|---|---|---|---|
| `sync.tasks` | `<資料根>/Tasks` | 寫入端（Server 的 task lane）每次寫單；主 Server 每分鐘拉 | [Task_Management §12](Task_Management.md#12-跨機器同步synctasks) |
| `sync.bookNotes` | `<資料根>/BookNotes`（閱讀庫 `Library/`、書的草稿） | **CLI**：`library`／`book` 跑之前拉、跑完推 | 本檔 §1 |
| `sync.workMemory` | `<資料根>/WorkMemory`（各主題資料夾、`_tombstones.md`） | **CLI**：`work-memory`／`task`（wrapup 寫工作記憶的是 CLI 入口）跑之前拉、跑完推 | 本檔 §2 |

§1、§2 是同一個本體（`SenateFolderSync`，差別只在 `FolderSyncSpec`）：**加一種資料夾 ＝ 加一個 spec 成員＋ `SyncSettings` 一格＋一支手動 Cmd**，路徑管理頁與 CLI 前後掛勾照 spec 清單走。

## 0. ⚠ Server 經 WMI 起 ⇒ 跑 git 同步會被 Defender 砍

🩸 2026-10-22 實測：`senate server start --detach`（WMI `Win32_Process.Create` 脫樹，TASK-0204）起的 Server 一跑 git 同步，
就被 Microsoft Defender 的行為偵測 **`Behavior:Win32/Ransomware!GenRemote.X1`** 整顆砍掉（action=Remove，由 SYSTEM 執行）。

- 樣子：Server 起來 2–70 秒後消失，**exit code 0**，沒有 ProcessExit、沒有 signal、沒有 console ctrl 事件 —— 從行程裡面看不到任何東西。
- 證據在事件檢視器 `Microsoft-Windows-Windows Defender/Operational` 的 1116／1117（會寫出 `senate-server.exe` 跟那顆的 pid）。
- 對照：同一顆 exe、同一份環境變數，改從 shell 直接起 ⇒ 開著同步跑 4 分鐘以上、0 次偵測。關掉同步 ⇒ WMI 起的照樣活一整天。
- ⇒ **新的同步一律放在 CLI 那一邊**（CLI 是使用者的行程樹，不是「遠端起的」）。`sync.tasks` 的寫入端在 Server 裡，所以受影響：
  修法（加 Defender 排除、換一種脫樹方式、或把 git 搬到 CLI）是 Tim 要拍板的事，拍板之前**這台的 `sync.tasks` 先不開**。

## 1. 閱讀筆記（`sync.bookNotes`）

- **`<資料根>/BookNotes` 就是那條 branch 的工作副本**：`library`、`book` 讀寫的路徑不變。
- **用之前先拉**：`senate cmd library|book` 跑之前，CLI 先 fetch＋快轉（每分鐘最多一次 —— 一次閱讀會連跑好幾支）。
- **寫完自動 commit＋push**：跑成功、而且 BookNotes 有改動 ⇒ 收成一筆 `booknotes: library(note_chapter) <media> <chapter> <persona> (auto)`，推上去。
  stdout 多一行 `🔢 booknotes_sync` = `pushed`／`up_to_date`／`pending`（斷網、沒權限：已在本機提交，下次用到再推）／`conflict`／`mismatch`／`failed`；過程印在 stderr。
- **自動補上**：
  - 缺 `BookNotes/` ⇒ 第一次用到時 clone。
  - **已經有 `BookNotes/`、可是還不是 git repo**（設同步之前就在這台寫過）⇒ **就地接上**：
    遠端獨有的拉下來；這台獨有的收成一筆推上去；**兩邊都有而內容不同 ⇒ 用遠端的版本**，這台那份備份在 `BookNotes/.git/senate-booknotes-sync/adopt-<時間>/`，並發酒館通知（要用這台的就複製回去，下次寫時一起推）。
  - 遠端是全新的空 repo（還沒有那條 branch）⇒ 這台的就是第一版。
  - 接上時連不到遠端 ⇒ 收掉剛建的 `.git`，本機的檔一個都不動，下次用到再試。
  - 資料樹本身是 git repo ⇒ 自動把 `BookNotes/` 加進它的 `info/exclude`（不然巢狀 repo 會被列成未追蹤）。
- **兩台同時寫**：
  - 不同的檔（各人寫各人的心得，最常見）⇒ push 被拒 → rebase 接上 → 再推，兩邊都在。
  - ⛔ **同一個檔、rebase 接不上 ⇒ 不自動解**：rebase 退回，**這台的提交留著**（推不上去、不會丟；照常寫沒關係），
    我的版本另存備份 ref `refs/senate/booknotes-sync/backup-*`，衝突報告（兩邊都改了哪幾個檔、還沒推的提交、手動解的步驟）在
    `BookNotes/.git/senate-booknotes-sync/conflict_*.md`，並發酒館通知（署那一支的 `persona`；**同一個衝突只通知一次**）。
    照報告解完 ⇒ `senate cmd booknotes-sync --arg op=sync` 推上去。
- 手動：`senate cmd booknotes-sync --arg op=status --arg fetch=1`（看差多少）／`--arg op=sync`（不節流地拉推一次）。**本地跑。**
- BookNotes 的 origin 或 branch 跟設定不同、或停在一次沒做完的 rebase ⇒ **不同步**（⛔ 不替人改 origin、不切 branch、不碰手上的 rebase），讀寫照舊落本機並說出來。
- 同一顆 BookNotes 同時有好幾個 CLI 在跑 ⇒ git 操作排隊（跨行程的具名 mutex）；等超過 60 秒 ⇒ 這次不同步（`pending`），⛔ 不擋閱讀。

## 2. 工作記憶（`sync.workMemory`）

跟 §1 同一套（clone／就地接上／用之前拉／寫完推／不同檔 rebase／同檔衝突擋下），只差這幾格：

- 掛在 `work-memory`（本身）與 `task`（`wrapup` 帶 `why` 時，CLI 入口把它寫進工作記憶）。commit 前綴 `workmemory:`，stdout 是 `🔢 workmemory_sync`；手動那支是 `workmemory-sync`。
- ⚠ **資料樹原本就在追蹤 `WorkMemory/` 的檔**（Fate 資料樹就是：2026-10-21 複製過來的兩個主題）：
  🩸 外層已經追蹤了的資料夾**不算**巢狀 repo 的邊界 —— 改過的照樣列成 ` M`、遠端拉下來的上千個檔照樣列成 `??`，外層一跑 `add -A` 就整包收進去（2026-10-22 實驗）。
  ⇒ 接上時自動做兩件**本機、不提交**的事：未追蹤的加進資料樹的 `info/exclude`、原本就追蹤的標成 `skip-worktree`（外層不再看它們的改動）。
  ⛔ **不**替人 `git rm --cached`：那是資料樹的一筆提交，會從每一份資料樹 clone 刪掉那些檔 —— 要正式從資料樹拿掉由人決定（回報那一行附指令）。
- 工作記憶的讀摘要（`WorkMemoryReadBriefs/`）不在同步範圍（它是每台自己的讀取紀錄）。

