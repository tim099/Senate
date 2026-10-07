---
title: DeepSeek Harness 本地部署與啟動
description: Windows 與 LY 換機部署的工具鏈、啟停與驗收流程，以及本次問題、解法和未驗備案。
last_updated: 2026-10-07
target_audience: [AI_Agent, Tools_Maintainer, Tim]
---

# DeepSeek Harness 本地部署與啟動

本流程對應 Bar 的 TASK-0439 與 LY 的 TASK-0442。以原始碼建置後啟動官方 `dsh web` profile；資料與模型設定由 Harness 管理，不寫入 Senate 設定。雲端模型需有效 API 憑證；本地 Ollama 可依 §8 接入。

§1–5 保存 Bar 電腦的實測；§6 區分已遇到的問題與未驗備案；§7 提供換機參數、指令與驗收；§9 記錄 2026-10-07 的 LY 部署。LY 不必沿用 `Tim` 帳號、D 槽或 Codex runtime 路徑，也不必把 Harness 放進 Unity 的 `Assets`。

LY 部署由 **TASK-0442 — LY 區域部署 DeepSeek Harness** 追蹤，與已完成的 TASK-0439 關聯。查單：`senate cmd tasks --arg index=442`；進度與驗收勾選以任務單為準。

## 1. 環境與來源

| 項目 | 本次讀數 |
|---|---|
| 系統 | Windows x64，原生 Windows 工具鏈 |
| Checkout | `D:\Unity\deepseek-harness` |
| Git HEAD | `5badb15009ae1756c3afe0ae0cef1faafc290ccc` |
| 專案版本 | `0.2.1-alpha.1` |
| Node 要求 | `^22.19.0 || >=24.0.0`，且開啟 TypeScript type stripping |
| 系統預設 Node | `C:\Program Files\nodejs\node.exe`，20.11.0；不適用 |
| 本次 Node | `C:\Users\Tim\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`，24.19.0 |
| 鎖定 pnpm | 11.7.0，安裝後位於 `node_modules\pnpm\bin\pnpm.cjs` |
| Bootstrap pnpm | Codex 附帶的 11.19.0，用於第一次安裝；後續建置採 checkout 的 11.7.0 |
| 初始工作樹 | `git status --short` 無輸出 |
| 初始模型憑證 | process 無 `DEEPSEEK_API_KEY`，checkout 無 `.env`；不代表其他 profile 沒有已儲存憑證 |

工具鏈路徑是本機實測位置。Codex 更新若移動 runtime，請先找出相容 Node，再修改下列 `$dshNodeDir`；不要讓系統 Node 20 接手。

## 2. 安裝與建置

在 PowerShell 中只調整這個終端的 PATH，讓 pnpm lifecycle 裡的 `node`、TypeScript 與子程序都使用同一版 Node。

```powershell
Set-Location D:\Unity\deepseek-harness
$dshNodeDir = 'C:\Users\Tim\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin'
$env:PATH = "$dshNodeDir;$env:PATH"
node --version
node -p 'process.features.typescript'

# 首次安裝使用本機既有 pnpm；保留鎖檔及 allowBuilds 政策。
pnpm install --frozen-lockfile
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed' }

# 此後使用 package.json 鎖定且已安裝的 pnpm 11.7.0。
node node_modules/pnpm/bin/pnpm.cjs --version
node node_modules/pnpm/bin/pnpm.cjs run build
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
```

本機已有 checkout 與相容 Node，因此沒有 clone 第二份或全域更新 Node／pnpm。本次 node-pty 的 ConPTY 與 Koffi 均取得可用預建檔，沒有安裝 C++ 編譯器；這個結果只適用本次 Windows x64／Node 24.19 組合。`build:native-system --host-addon-only` 在 Windows 沒有 Linux／macOS addon 建置目標，正常返回。另一台機器若退回 `node-gyp rebuild`，需依實際錯誤補原生工具鏈，不能把本次成功推成所有平台都免編譯器。

安裝會執行專案允許的 dependency scripts、修補 node-pty，以及安裝此 checkout 的 Lefthook hooks。第一次安裝可能警告 `apps/cli/lib/bin.js` 尚不存在；不要因此改 package bins，正式啟動只走 `dsh` profile。

## 3. 啟動與使用

### 一鍵啟動（Windows）

雙擊 Harness 根目錄的 [start-dsh.bat](../../../deepseek-harness/start-dsh.bat)，它會呼叫同目錄的 [start-dsh.ps1](../../../deepseek-harness/start-dsh.ps1)。服務未開時會在背景啟動正式 Web profile，等 readiness 後用完整 token URL 開啟預設瀏覽器；若已在運行且 PID／登入紀錄匹配，直接開瀏覽器，不啟動第二份服務。關閉啟動器視窗不會停止服務。失敗會顯示訊息並暫停視窗。

Node 依序從顯式 `DSH_NODE_EXE`、PATH、當前 Windows 使用者的 Codex runtime、Program Files 中選相容版本；不相容的 Node 20 會略過。顯式指定錯誤 Node 時會阻擋，不會默默換成另一份。原始碼路徑從腳本所在目錄取得，不寫死 Tim／D 槽；仍需先完成 §2 的安裝建置，啟動器不自動下載或 rebuild。

```powershell
# 可選：LY 電腦顯式指定相容 Node（只影響此終端及子程序）。
$env:DSH_NODE_EXE = 'C:\path\to\compatible\node.exe'
.\start-dsh.bat

# 其他選項：改埠、只驗前提、或啟動但不開瀏覽器。
.\start-dsh.bat -Port 3081
.\start-dsh.bat -CheckOnly
.\start-dsh.bat -NoBrowser
```

新服務的 stdout／stderr／PID 保存在 checkout 的 **`.dsh-build/local-launch/`**（已 gitignore）；stdout 含本次登入 token，不分享或提交。重用原有 Bar 部署時會核對 repo 外原本的 log／PID，因此本次 PID 32300 不必為了換啟動器而重啟。埠被其他服務占用、或沒有匹配登入紀錄時會拒絕，請查明或改埠。停止新背景服務沿用 §4 的 PID 核對方式，但改讀 `.dsh-build/local-launch/web.pid`；新 CLI command line 使用完整路徑，核對方法見 §7.4。

驗證：Windows PowerShell **5.1** 實跑 `-CheckOnly`；全新 3081 服務成功啟動，401／303／cookie／HTML 200／JS 200 通過；再按啟動器保持相同 PID。實際 `.bat` 重用正式 3080 的 PID 32300 並送出預設瀏覽器開啟請求。顯式 Node 20 preflight 回 exit 1。測試服務已停止，正式服務保留。這些讀數證明啟動、重用及瀏覽器 handoff，未額外宣稱實際 UI 渲染或模型回答已驗。

### 手動啟動

沿用上一節的終端與 PATH。使用正式 Web profile，綁定 loopback；`--no-open` 避免自動開瀏覽器。

```powershell
node node_modules/pnpm/bin/pnpm.cjs dsh web --host 127.0.0.1 --port 3080 --no-open
```

CLI 印出 `dsh web:` 的完整啟動 URL 後才代表所需 plugin tree 已就緒。首次使用在 Harness home 初始化 `profiles/web`。啟動 URL 帶本次 process 的認證 token，請在瀏覽器打開該完整 URL；取得 cookie 後會轉到不帶 token 的網址。不要把 token 貼到文件或版控。

### 本次背景啟動的重跑指令

下列命令呼叫與 `pnpm dsh` 相同的官方 CLI source entry，跳過額外的 pnpm 父 process，PID 直接對應 Web server。先依第 4 節確認並停止舊 process，才能覆用本次 log 路徑。

```powershell
Set-Location D:\Unity\deepseek-harness
$dshNodeDir = 'C:\Users\Tim\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin'
$env:PATH = "$dshNodeDir;$env:PATH"
$dshProcess = Start-Process -FilePath (Join-Path $dshNodeDir 'node.exe') `
    -ArgumentList '--import','tsx/esm','apps/cli/src/bin.ts','web', `
        '--host','127.0.0.1','--port','3080','--no-open' `
    -WorkingDirectory D:\Unity\deepseek-harness -WindowStyle Hidden `
    -RedirectStandardOutput D:\Unity\deepseek-harness-web.stdout.log `
    -RedirectStandardError D:\Unity\deepseek-harness-web.stderr.log -PassThru
$dshProcess.Id | Set-Content D:\Unity\deepseek-harness-web.pid

# 就緒後在自己的終端取完整登入 URL；這行輸出不要放進公開文件。
Select-String -Path D:\Unity\deepseek-harness-web.stdout.log -Pattern '^dsh web:'
```

本次預設 home 為 `C:\Users\Tim\.dsh`，Web profile 與使用者資料會跨 process 保留。

在 UI 的 **設定 → 模型** 輸入自己的 DeepSeek API key 並儲存，再新增及選中工作區。未選工作區時輸入框不可用；只收到 HTML 不表示模型已可呼叫。模型設定可即時生效，不必重啟。可替換的提供者設定以 checkout 的 [模型配置指南](../../../deepseek-harness/docs/user/guide/providers.zh.md) 為準。

## 4. 停止與重啟

前景終端按一次 **Ctrl+C**，CLI 會開始最多五秒的 plugin disposal；第二次才是強制退出。停止後檢查 3080 沒有 listener，再以第 3 節指令啟動。每次啟動的 token 會更換，需用新啟動 URL 認證。

```powershell
Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction SilentlyContinue
```

若以 `Start-Process` 在背景啟動，要使用 `-WindowStyle Hidden`、指定 checkout 為 WorkingDirectory，並保存啟動 PID 與 stdout／stderr 的本機檔案。停止時只處理該次啟動的 process，先確認 executable／command line；不要停止所有 `node`。Windows 的 `Stop-Process` 是強制終止，與前景 Ctrl+C 的優雅 disposal 不同。

```powershell
$dshPid = [int](Get-Content D:\Unity\deepseek-harness-web.pid)
$dshRunning = Get-CimInstance Win32_Process -Filter "ProcessId=$dshPid"
$dshExpectedNode = 'C:\Users\Tim\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
if (!$dshRunning -or $dshRunning.ExecutablePath -ne $dshExpectedNode -or
    $dshRunning.CommandLine -notlike '*apps/cli/src/bin.ts*web*') {
    throw 'PID no longer identifies this deployment; inspect before stopping'
}
$dshListener = Get-NetTCPConnection -LocalPort 3080 -State Listen -ErrorAction Stop
if ($dshListener.OwningProcess -ne $dshPid) { throw 'Port belongs to another process' }
Stop-Process -Id $dshPid
```

## 5. 本次驗證結果

驗證時間為 **2026-10-06 22:47（Asia/Taipei）**；本次保留服務執行，未新增 Windows 開機自動啟動。

| 檢查 | 結果 |
|---|---|
| Bootstrap install | exit 0，1393 套件，鎖檔供應鏈政策檢查通過 |
| 鎖定 pnpm 再驗安裝 | `node node_modules/pnpm/bin/pnpm.cjs install --frozen-lockfile` exit 0，11.7.0，Already up to date |
| 完整 build | exit 0，Host／Client／Web 完成，記錄 355 個 client artifacts |
| 建置紀錄讀回 | `readClientBuildRecord(process.cwd())` 通過實際產物摘要校驗；commit `5badb15`，version `0.2.1-alpha.1` |
| process／listener | PID **32300**，`127.0.0.1:3080`，CLI readiness URL 已印出 |
| 啟動 stderr | 當時空檔，沒有 required plugin boot failure |
| 未登入 API | `/api/deployment-smoke` 回 **401** |
| token 交換 | 根路徑回 **303**，Location `./`，有簽署 cookie |
| 已登入首頁 | **200**，HTML 35,527 bytes |
| 主要 JS | `assets/index-DjTxlw_T.js` 回 **200** |
| 已登入未知 API | `/api/deployment-smoke` 回 **404**；與未登入 401 區分，確認已進入認證後 routing |
| 真實模型回答 | **未測**，沒有提供 DeepSeek API key |
| 瀏覽器實際渲染 | **未驗**；已向 Codex 送出開啟登入頁的請求，工具回 `queued` |

建置有 plugin timing、其他作業系統 workspace 的平台提示與大 bundle 警告，完整 build 仍回 0，且本次 Web profile 沒有啟動錯誤。未因警告修改上游 source 或放寬檢查。

本機讀數與 log：`D:\Unity\deepseek-harness-build.log`、`D:\Unity\deepseek-harness-install.log`、`D:\Unity\deepseek-harness-smoke.json`、`D:\Unity\deepseek-harness-web.stdout.log`、`D:\Unity\deepseek-harness-web.stderr.log`、`D:\Unity\deepseek-harness-web.pid`。stdout 含認證 token，這些檔案都在 checkout 與 Senate repo 之外，不納入版控。

🩸 最初 HTTP smoke 把 token 交換預期寫成 302，實際回 303；cookie、首頁與 JS 都正常。查回 response 後將驗收值修正成 303，通過六個 HTTP 條件，沒有修改服務來迎合錯誤預期。初始系統 Node 20.11 也已排除：完整建置和啟動均使用 Node 24.19。

## 6. 問題與解法

### 6.1 本次實際遇到／觀察到的問題

| 症狀或讀數 | 判斷、採取的動作與結果 | LY 要照做的部分 |
|---|---|---|
| `node --version` 為 `v20.11.0` | 啟動前發現不符 `package.json` engine，沒有拿 Node 20 嘗試 build。找到 Codex 附帶 Node 24.19，把其目錄放到本次終端 PATH 最前；`process.features.typescript` 回 `strip`，完整 build 成功 | 先量 Node 版本和 type stripping；只指定頂層 node.exe 不夠，子程序也靠 PATH 找 Node |
| `pnpm --version` 為 11.19.0，專案鎖定 11.7.0 | 本機 pnpm.cmd 固定呼叫 Codex 附帶的 pnpm，沒有自動切到專案版本。11.19 首次 frozen install 成功；後續用已安裝的 `node_modules/pnpm/bin/pnpm.cjs` 11.7 build，並再以 11.7 frozen install 驗證 `Already up to date` | 不能只相信 `packageManager` 會被任何 shim 遵守；直接核對並使用鎖定版本。首次 bootstrap 可用 §7 的 npm 路徑，該路徑尚未在本機實跑 |
| 安裝打印 `Failed to create bin ... apps/cli/lib/bin.js.EXE`、ENOENT | 發生在原始碼未 build 的初次安裝；install 最後 exit 0。先跑完整 build，再由正式 `dsh web` CLI source entry 啟動，HTTP 驗證通過，未修 package bins | 不把安裝中間的警告單獨當成整體失敗；保存完整 log、看最終 exit code，再驗正式入口。若 install exit 非零則不能套用此處置 |
| 首次 install 有 cyclic workspace、darwin／linux `Unsupported platform` 與慢速下載提示 | 342 個 workspace，1393 套件；鎖檔供應鏈政策檢查通過，首次 install 約 1 分 49 秒且 exit 0。node-pty／Koffi 預建檔可用 | 保留 lockfile、patches、allowBuilds 政策；不因平台提示改掉非本機 workspace 或開放所有 install scripts |
| `node-pty` 的 prebuild／postinstall 搬動 `conpty.dll`、`OpenConsole.exe` | 此次找到 win10-x64 payload `1.25.260303002`，install／postinstall 均 Done，沒有進入失敗的 node-gyp fallback | 換機重新安裝依賴，不搬這台的 node_modules；若平台、Node 或原生套件版本不同，重新確認 payload |
| Lefthook 安裝有 `[DEP0190]` shell deprecation warning | hooks 安裝 Done，install exit 0；本次沒有使用者自訂 hooksPath 衝突，不需要覆蓋許可 | warning 不等於 hooks 失敗。若出現 `refusing ... user-owned core.hooksPath`，那是另一種阻擋，先整合既有 hooks；本次未驗，不直接加 override |
| Host／Client `tsc -b` 長時間不打印新行 | CPU 與記憶體持續使用；保留 process 等編譯結束，未殺掉或開第二份 build。log 建立 22:40:43、最後寫入 22:46:12，共約 **5 分 29 秒**，完整 build exit 0 | 不用「log 暫時不變」判定卡死；看 process、資源與最終結果。這是本機時間，LY 較慢不自動代表失敗 |
| bundler 有 `noExternal`、`inlineDynamicImports` deprecated，plugin timing 與大於 500 kB 的 chunk 警告 | build 仍 exit 0；355 artifacts 的 digest 可讀回，正式 Web profile 無 boot stderr，主要 JS 回 200 | 保留警告，不為了讓 log 全綠去改上游配置；若出現非零 exit 或 artifact 缺失，另查該錯誤 |
| 第一次 HTTP smoke 預期 token redirect 為 302，但實際 **303** | cookie 已發、Location `./`，HTML／JS 均 200。修正驗收預期成 303，六個條件全通過；服務沒有改動 | 用 §7 已修正的 smoke。必須驗 cookie 與認證後回應，不只看一個 redirect 或首頁 |
| 開啟 Codex browser／文件工具回 `queued` | 只證明 UI 開啟請求已排入，未證明頁面渲染。服務則已經以 HTTP 獨立驗證 | 開不到內嵌瀏覽器時，在一般瀏覽器使用 stdout 的完整 token URL；不要把 queued 記成 UI 成功 |
| 無 `DEEPSEEK_API_KEY`、無 repo `.env`，初始亦無 `.dsh` | 仍能啟動 Web 與交換 cookie；未送出模型請求，避免把服務就緒寫成真實回答已通過 | 模型設定與工作區選擇另驗；在 LY 填自己的 key，不從這台的 log／文件取密鑰 |
| Windows PowerShell 5.1 啟動器的 Node `-e` 初次回 `[eval]:1` | 嵌在 JS 參數裡的引號經 native argv 傳遞後失效。版本檢查改用不含嵌套引號的 JS；ESM artifact 檢查用 ASCII JS stdin 傳入。5.1 preflight 已通過，Node 20 拒絕檢查也通過 | PowerShell 7 語法檢查不替代 bat 實際呼叫的 5.1；此啟動器已直接用 `powershell.exe` 測過 |
| 啟動後讀 stdout 出現 `The process cannot access the file ... because it is being used by another process` | `Start-Process` 仍持有寫入 handle，普通 `ReadAllText` 的共享模式不合。改用允許 `FileShare.ReadWrite` 的唯讀 FileStream；全新服務與重用路徑均通過 | 讀正在寫入的 log 要允許既有 writer；只改成 retry 或延遲不會修共享模式。本次失敗留下的測試 process 已核對 PID 後停止 |

### 6.2 尚未在本次遇到的排錯備案

以下是依 checkout 文件與介面整理的備案，**不是本次實測修復紀錄**。

| 症狀 | 處理 |
|---|---|
| Node engine 不符／TypeScript strip 不可用 | 確認 `node --version`、`process.features.typescript` 與 PATH，檢查 `NODE_OPTIONS` 是否禁用 strip types |
| pnpm 版本落到全域工具 | 使用 `node node_modules/pnpm/bin/pnpm.cjs`，而非依賴 PATH 中另一個 pnpm shim |
| 啟動提示前端未建置 | 先跑完整 `run build`；`dsh web` 不自動建置 |
| EADDRINUSE | 查 listener 的 OwningProcess；保留既有服務，或以 `--port 3081` 改埠 |
| UI 打得開但 API 拒絕 | 用該 process 的完整 token URL 取得 cookie，別只驗 `/` 的 HTTP 200 |
| 沒有模型回答 | 檢查 UI 模型憑證、選中工作區及提供者 endpoint；服務啟動與真實模型呼叫是兩種驗證 |
| optional plugin 警告 | 依具體 plugin 診斷；區分可選能力缺失與阻止 readiness 的 required plugin 錯誤 |

原始操作依據：checkout 的 [README](../../../deepseek-harness/README.zh.md)、[開發指南](../../../deepseek-harness/docs/development.md)、[CLI reference](../../../deepseek-harness/apps/cli/reference/README.md)、[Web bundle](../../../deepseek-harness/packages/bundle/web-app/README.md) 與 [安全說明](../../../deepseek-harness/SAFETY.zh.md)。這些相對引用指向同在 `D:\Unity` 的 checkout；移動專案時需同步調整。

## 7. LY 電腦的重跑流程

本節是 **Windows x64** 的換機部署指引，LY 的實跑結果見 §9；若是其他平台，不能沿用 Windows 原生套件與停止方法。先保持與本次相同的 Git commit、Node 24.19、pnpm 11.7，讓差異主要是電腦環境；若刻意更新上游版本，先重新讀該 checkout 的 README、AGENTS 與 package.json，再記錄新組合。

### 7.1 換機先決定的值

| 值 | 設定方式 |
|---|---|
| `$dshRepo` | LY 電腦的 Harness clone 路徑；獨立於 LY Unity 的 Assets，例如 `D:\Unity\deepseek-harness` |
| `$dshNodeExe` | 該電腦可用的 Node 24.19 executable；Codex runtime 或獨立 Node 安裝均可，先確認存在 |
| `$dshLogDir` | 本機 log／PID／驗收輸出目錄，放在 repo 外，例如 `D:\Unity\deepseek-harness-local` |
| `$dshPort` | 3080；已有 listener 時改 3081，不停止不明 process |
| `$dshWorkDir` | Web 的預設工作目錄；首次建議 Harness repo。要操作 LY，啟動後在 UI 加入並選中 LY 實際根目錄 |
| `DSH_HOME` | 沒設定時使用該 Windows 使用者的 `~/.dsh`；若要隔離 profile，啟動前顯式設定新資料目錄 |

不要複製 Bar 的 `node_modules`、build outputs、PID、token URL 或 `.credentials.yaml`。在 LY 重新 frozen install、build、登入及設定模型。若要保留舊會話或設定，那是另一次資料遷移，與這份「新機部署」分開處理；不得把複製整個 `.dsh` 當預設步驟。

若要在 LY 同樣雙擊開啟，將本地新增的 **start-dsh.bat 與 start-dsh.ps1 兩檔一起**帶到 LY 的 Harness 根目錄；上游原始 commit 不含這兩份本地 helper。它們不依賴 Bar 的使用者名稱，Node 不在可探測位置時用 `DSH_NODE_EXE` 指定。使用方式與新 log／PID 落點見 §3。

### 7.2 準備 checkout 與相容工具鏈

LY 沒有 checkout 時，從本次核對的 origin `https://github.com/deepseek-ai/deepseek-harness.git` clone。新的 clone 可 checkout 到 §1 記錄的完整 SHA；若已有 checkout，先查工作樹，保留既有改動，不強制 reset。

```powershell
# 在 LY 電腦依實際位置填入這四個值。
$dshRepo = 'D:\Unity\deepseek-harness'
$dshNodeExe = 'C:\Program Files\nodejs\node.exe'
$dshLogDir = 'D:\Unity\deepseek-harness-local'
$dshPort = 3080

if (!(Test-Path -LiteralPath $dshNodeExe)) { throw 'Select an installed compatible Node executable' }
if (!(Test-Path -LiteralPath $dshRepo)) { throw 'Clone the repository before continuing' }
$dshNodeDir = Split-Path -Parent $dshNodeExe
$env:PATH = "$dshNodeDir;$env:PATH"
Set-Location -LiteralPath $dshRepo
New-Item -ItemType Directory -Path $dshLogDir -Force | Out-Null

& $dshNodeExe -e 'const [major,minor]=process.versions.node.split(String.fromCharCode(46)).map(Number); if(!((major===22&&minor>=19)||major>=24)||!process.features.typescript) process.exit(1); console.log(process.version,process.features.typescript);'
if ($LASTEXITCODE -ne 0) { throw 'Fix Node/PATH/NODE_OPTIONS before installation' }
Get-Command node,pnpm,npm,git -ErrorAction SilentlyContinue | Select-Object Name,Source
git rev-parse HEAD
git status --short
Get-NetTCPConnection -LocalPort $dshPort -State Listen -ErrorAction SilentlyContinue
```

`C:\Program Files\nodejs\node.exe` 只是示例，**不是聲稱 LY 那裡已經裝好正確版本**。若該電腦只有 Node 20，先裝或定位 Node 24.19；若使用 Codex 附帶 Node，查該台 runtime 的實際路徑，不複製本文件的 `C:\Users\Tim`。

### 7.3 首次安裝、完整建置

**已實跑的路徑**：有可用 pnpm 時先 `pnpm install --frozen-lockfile`，之後一律用 checkout 自帶的 11.7。**未實跑的 bootstrap 備案**：沒有 pnpm、但相容 Node 安裝附帶 npm 時，可用 `npm exec --yes --package=pnpm@11.7.0 -- pnpm install --frozen-lockfile`；這是替代首次安裝的命令，兩條路徑選一條，不需要全域裝 pnpm。

```powershell
# 下列沿用 7.2 的變數與 PATH；沒有 pnpm 時替換成上面的 npm exec。
pnpm install --frozen-lockfile *> (Join-Path $dshLogDir 'install.log')
$dshInstallExit = $LASTEXITCODE
Get-Content (Join-Path $dshLogDir 'install.log') -Tail 25
if ($dshInstallExit -ne 0) { throw 'Installation failed; inspect install.log before retrying' }

$dshPnpm = Join-Path $dshRepo 'node_modules\pnpm\bin\pnpm.cjs'
& $dshNodeExe $dshPnpm --version
& $dshNodeExe $dshPnpm install --frozen-lockfile
if ($LASTEXITCODE -ne 0) { throw 'Pinned pnpm installation failed' }
& $dshNodeExe $dshPnpm run build *> (Join-Path $dshLogDir 'build.log')
$dshBuildExit = $LASTEXITCODE
Get-Content (Join-Path $dshLogDir 'build.log') -Tail 25
if ($dshBuildExit -ne 0) { throw 'Build failed; inspect build.log' }

'import {readClientBuildRecord} from "./scripts/client-build-environment.ts"; console.log(JSON.stringify(readClientBuildRecord(process.cwd())));' | & $dshNodeExe --import tsx/esm --input-type=module
if ($LASTEXITCODE -ne 0) { throw 'Built client artifact digest does not match' }
```

不要直接 `node scripts/build.ts`：內部的 `pnpmInvocation` 需要由 pnpm run 提供 `npm_execpath`。上面的 artifact 讀回是唯讀驗收，所以可直接呼叫 Node。若 checkout 缺 patch 檔案或 frozen install 說鎖檔不合，先查 clone 的完整性與版本，別先刪 lockfile 或改成非 frozen install。

### 7.4 啟動、登入與停止

首次建議前景啟動，能直接看到 readiness、完整登入 URL 與錯誤，也能以 Ctrl+C 停止。

```powershell
& $dshNodeExe $dshPnpm dsh web --host 127.0.0.1 --port $dshPort --no-open
```

確認前景可以啟動後，如需留在背景，先 Ctrl+C 停舊 process，再啟動：

```powershell
$dshListener = Get-NetTCPConnection -LocalPort $dshPort -State Listen -ErrorAction SilentlyContinue
if ($dshListener) { throw 'Port already has a listener; inspect it or select another port' }
$dshCliEntry = Join-Path $dshRepo 'apps\cli\src\bin.ts'
# CLI 路徑加引號，支援另一台機器的含空白路徑。
$dshArgs = @('--import','tsx/esm',('"{0}"' -f $dshCliEntry), 'web',
    '--host','127.0.0.1','--port',"$dshPort",'--no-open')
$dshProcess = Start-Process -FilePath $dshNodeExe -ArgumentList $dshArgs `
    -WorkingDirectory $dshRepo -WindowStyle Hidden -PassThru `
    -RedirectStandardOutput (Join-Path $dshLogDir 'web.stdout.log') `
    -RedirectStandardError (Join-Path $dshLogDir 'web.stderr.log')
$dshProcess.Id | Set-Content (Join-Path $dshLogDir 'web.pid')
```

另開終端讀 stdout／stderr。此命令返回 PID 時不保證已就緒；等 `dsh web:` 行，再打開它的完整 URL。取得 cookie 後，設定 API key、加入並選中 LY 工作區，再送一個簡單問題，另記錄該模型測試的結果。

停止背景 process 時，依 §4 核對 PID 的 executable、**完整 CLI 路徑**及 listener。7.4 用完整 CLI entry，與 §4 本機相對 entry 的範例不同，不能直接套用 `*apps/cli/src/bin.ts*` 的辨認式。可在同一個啟動終端使用下列檢查：

```powershell
$dshPid = [int](Get-Content (Join-Path $dshLogDir 'web.pid'))
$dshRunning = Get-CimInstance Win32_Process -Filter "ProcessId=$dshPid"
if (!$dshRunning -or $dshRunning.ExecutablePath -ne $dshNodeExe -or
    !$dshRunning.CommandLine.Contains($dshCliEntry)) {
    throw 'PID no longer identifies this CLI deployment'
}
$dshListener = Get-NetTCPConnection -LocalPort $dshPort -State Listen -ErrorAction Stop
if ($dshListener.OwningProcess -ne $dshPid) { throw 'Listener belongs to another process' }
Stop-Process -Id $dshPid
```

### 7.5 LY 驗收與要帶回的紀錄

- [ ] 記錄電腦／OS 架構、checkout SHA、工作樹狀態、Node executable／版本／type stripping、pnpm 實際入口／版本。
- [ ] frozen install 與完整 build exit 0；`readClientBuildRecord` 對實際 artifacts 驗證通過。355 是本次版本讀數，不是未來版本的固定要求。
- [ ] listener 是自己的 PID，CLI readiness URL 已打印；stderr 的錯誤與可選 plugin 警告各自記錄。
- [ ] 不帶 cookie 的 API 回 401；token 根路徑回 303 並發 cookie；有 cookie 的首頁與主要 JS 回 200；已登入的未知 API 回 404。
- [ ] 一般瀏覽器實際載入 UI；新增及選中 LY 工作區；模型設定完成且一個簡單問題真的得到回答。若缺 key，標「模型未驗」，不要整組簽通過。
- [ ] 能停止且 listener 消失，再啟動取得新 token。不要把這台 Bar 的 PID 32300 帶去停止 LY 的 process。
- [ ] 每個新問題保留「症狀原文／使用指令／exit code／環境差異／採取動作／修復後讀數」；將已實測解法補回 §6.1，未解或未實測的放 §6.2。

只傳遞原始碼版本、此文件及去除機密的環境／錯誤／驗收摘要；新的 log 保留在 LY 電腦的 `$dshLogDir`。完整 stdout、token URL、cookie、API key 不放進 Senate 文件或 Task 留言。完成本地提交後，仍須推送 Senate 並在 LY 電腦同步文件及 DOC_INDEX，才有跨機可取得的交付。

## 8. 接入 LlmModelPage 管理的本地模型

完整步驟見 [DSH 接入本地 Ollama 模型](DeepSeek_Harness_Ollama_Integration.md)，包含模型列表查驗、DSH 設定表、故障排查與 TASK-0442 的 LY 驗收。

Bar 已確認 Ollama 服務與 qwen3:4b 模型列表正常；LY 的模型回答結果見 §9。工作區工具呼叫須另驗，不能由簡單問答推定成功。

## 9. LY 實際部署（2026-10-07）

### 9.1 環境與建置

本次在 LY 電腦重新 clone 官方 origin，再 detached checkout 到相同基準 SHA；沒有搬 Bar 的 node_modules、資料目錄或認證。Tim 指定的目標為 `D:\Unity\deepseek-harness`。

| 項目 | LY 實測 |
|---|---|
| 電腦／系統 | DESKTOP-BC18H3C，Windows 11 Professional x64 |
| 工作區 | `D:\Unity\LY` |
| Harness／版本 | `D:\Unity\deepseek-harness`；`5badb15009ae1756c3afe0ae0cef1faafc290ccc`；`0.2.1-alpha.1` |
| 建置與啟動 Node | `C:\Users\crespirit\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe`；24.19.0；type stripping 可用 |
| 系統 PATH Node | 24.14.0，符合 engine；PowerShell 5.1 helper preflight 通過。正式驗收仍顯式指定 24.19.0 |
| Bootstrap pnpm | Codex fallback 的 11.19.0，frozen install exit 0，1393 套件，約 2 分 20 秒 |
| 鎖定 pnpm | `node_modules/pnpm/bin/pnpm.cjs` 11.7.0；再做 frozen install exit 0 |
| 完整 build | exit 0；09:16:27–09:23:08，約 6 分 41 秒；355 client artifacts |
| 產物讀回 | `readClientBuildRecord(process.cwd())` 通過 digest 驗證 |
| 原生依賴 | ConPTY win10-x64 預建 payload `1.25.260303002` 可用；沒有另裝 C++ 編譯器 |
| Ollama／GPU | 0.35.1；RTX 2060 6 GB；既有 `qwen3:0.6b`、`qwen3:4b` |
| DSH 資料 | `C:\Users\crespirit\.dsh`，正式 `profiles/web` |

安裝的 bin ENOENT、平台提示、DEP0190，以及 build 的 plugin timing／大 chunk 警告均保留在 log；最終 install 與 build 都回 0。沒有修改上游 source、lockfile 或 install-script 政策。

### 9.2 本地啟動器與驗收紀錄

原始 commit 不含 Bar 的本地 helper，因此在 LY 根目錄重建 `start-dsh.bat` 與 `start-dsh.ps1`；兩檔保持本地 untracked。使用 §3 的雙擊及 `-CheckOnly`／`-NoBrowser` 方式，顯式 Node 仍由 `DSH_NODE_EXE` 控制。

LY helper 的紀錄依 port 命名：`.dsh-build/local-launch/web-3080.pid`、`web-3080.stdout.log`、`web-3080.stderr.log`；停止時要讀這個 PID，不能套用 Bar 的 `web.pid`。helper 核對 Node executable、完整 CLI path、單一 listener 的 PID 及 loopback address。

瀏覽器自動驗收依 Harness AGENTS 加上 `apps/web/tests/pin-browse-picker.overlay.yml`，讓資料夾 picker 在網頁內可操作。這是驗收 overlay，日常啟動不帶它。CLI loader 選項 `--profile web --patch <overlay>` 必須放在 Web app 的 `--host`／`--port` 選項之前；首次放在 app flags 後面回 `unknown option '--patch'` 並退出，調整順序後成功就緒。

驗收 log 位於 `D:\Unity\deepseek-harness-local`：`install.log`、`pinned-install.log`、`build.log`、`smoke-3080.json`。登入 stdout 在上述 gitignored 目錄，含 token，不提交。HTTP smoke 的 JS 只输出去除認證資料的狀態碼。

首次瀏覽器驗收 PID 31960 綁定 `127.0.0.1:3080`；HTTP 檢查為未登入 API **401**、token 交換 **303** 並發 cookie、認證後首頁 **200**（35,591 bytes）、主要 JS **200**（634,085 bytes）、認證後未知 API **404**。實際 IAB 瀏覽器已渲染 `DSH 本地构建 0.2.1-alpha.1-5badb15`，已新增並選中 LY 工作區及本地 provider。

### 9.3 模型容量排錯

首次 provider 宣告 `qwen3:4b` contextWindow 8192、maxTokens 2048，但 Ollama `/api/ps` 實際只有 **4096**。DSH 執行上下文壓縮後，用了 1 分 24 秒回覆與時間戳相關的英文，沒有回答「2 加 3」，因此該次不算問答驗收通過。

以既有權重建立 `qwen3:4b-dsh`，Modelfile 設 `PARAMETER num_ctx 16384`；DSH model 的 contextWindow 同步為 16384、maxTokens 2048，並改預設模型後新建 LY 對話。`/api/ps` 已確認 alias 真正載入 **16384**，VRAM 約 4.17 GB。容量設定與後續問答結果詳見 [Ollama 接入紀錄](DeepSeek_Harness_Ollama_Integration.md)。DSH 的 contextWindow 只聲明 client 預算，不能替代 Ollama 的 num_ctx。

16K 的 4B 路徑仍不適合作為本次預設：每秒約 5 tokens，持續反覆思考；停止該次後明確送出 `reasoning_effort: none`，另一新對話仍把分析當答案輸出，32 秒後在 `2 + 3 =` 截斷。曾在專用 alias 試改 template 的結尾思考前綴，API 短答仍不通過；已將 4B alias 恢復為原模型 template、只保留 num_ctx 16384，沒有修改原 `qwen3:4b`。

原生 API 查驗既有 `qwen3:0.6b` 在 `think: false` 下能完整回答 `2 + 3 = 5.`。因此另建立 **`qwen3:0.6b-dsh`**，num_ctx 與 DSH contextWindow 均為 **32768**，maxTokens 2048；provider 明確配置 Off → `none`、`supportsDeveloperRole: false` 及 `maxTokensField: max_tokens`。Ollama 實際 `context_length` 32768、VRAM 4,269,549,812 bytes。

**09:37 的 LY UI 問答通過**：新建 LY 標準模式對話、所選 `local-ollama/qwen3:0.6b-dsh`、推理 Off，提問「請只用一句繁體中文回答：2 加 3 等於多少？這是本地部署驗收，請勿讀取檔案或呼叫工具。」完整回覆 **「2 加 3 等於 5。」**，用時 **13 秒**，約 52 tok/s，上下文使用 43%。沒有把早先兩種失敗輸出算成通過。

這項結果驗證本地模型接入與基本問答；0.6B 的程式修改能力、工作區工具循環與 Low 推理仍未驗。4B 的推理問題保留為模型接入限制，沒有宣稱已修復。

### 9.4 正式重啟與文件交付

核對並停止自己的 PID 31960 後，3080 listener 已消失。以不含 picker overlay 的正式 Web profile 重新啟動，**PID 8588** 綁定 `127.0.0.1:3080`；09:38:56 的 HTTP smoke 再次通過 401／303／cookie／首頁 200／JS 200／認證後未知 API 404。啟動 stderr 為空，瀏覽器用新 token 登入並恢復既有 LY 對話。

PowerShell 5.1 的啟動器重用測試保持 PID 31960，顯式指定不存在的 Node 回 exit 1；正式重啟則建立新 PID。最後保留 PID 8588 服務供使用，不新增開機自啟。日常雙擊 `D:\Unity\deepseek-harness\start-dsh.bat` 即可重用服務並登入。

本文件就在同一台 LY 電腦的 `D:\Unity\Senate\Docs\Workflows\DeepSeek_Harness_Local_Deployment.md`，已從 LY 環境讀回；無須另一台電腦 pull 才能取得本地交付。跨機同步仍要另外推送 Senate，本次只作本地提交。
