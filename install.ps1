# Senate 安裝／移除（PowerShell）—— 與 install.sh 等價：clone 完只要跑這一支。
#
# 區塊職責：一台機器上「裝 Senate」的**唯一入口**：前置檢查 → build → 產生本機設定 → 掛使用者 PATH。
#           -Uninstall 是它的反向操作；-Purge 連使用者設定一起清。
# 物理意義／數值影響：見 install.sh 檔頭（兩支同一套規格，只是宿主 shell 不同）——
#   只動 HKCU 使用者 PATH、冪等、寫入走 .NET（setx 有 1024 截斷坑）、
#   已開著的終端機不會變（PATH 是 process 啟動時複製的）。
#
# ⚠ **移除清單（$aArtifacts）改這邊要同時改 install.sh 的那份** ——
#   兩份漂掉的症狀是「用 .sh 裝、用 .ps1 移除，結果少刪兩樣」，而那不會有人喊。
param([switch]$Uninstall, [switch]$Purge, [switch]$SkipBuild)
$ErrorActionPreference = "Stop"
$aRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
# PATH 掛的是 publish/（執行檔住那裡）；$aLegacy 是 2026-09-01 以前掛的 repo 根，
# 現在根層已經沒有可執行檔 ⇒ 留著只會讓 `senate` 找不到而人去怪 PATH。安裝時遷移、移除時一起清。
$aBin = Join-Path $aRoot "publish"
$aLegacy = $aRoot

function Get-UserPath { [Environment]::GetEnvironmentVariable("Path", "User") }
function Test-Entry($iPath, $iTarget) {
    ($iPath -split ";") | Where-Object { $_.TrimEnd("\", "/") -ieq $iTarget.TrimEnd("\", "/") } | ForEach-Object { return $true }
    return $false
}
function Drop-Entry($iPath, $iTarget) {
    (($iPath -split ";") | Where-Object { $_.TrimEnd("\", "/") -ine $iTarget.TrimEnd("\", "/") }) -join ";"
}
function Test-Installed($iPath) { Test-Entry $iPath $aBin }

# build 產物 —— 可重建 ⇒ 移除時預設就清。⚠ 與 install.sh 的 for 迴圈清單必須一致。
$aArtifacts = @("senate.lnk", "senate.exe", "senate.cmd", "senate", "cimgui.dll", "glfw3.dll", "publish", "build")

if ($Uninstall) {
    Write-Host "── Senate 移除 ─────────────────────────────────"

    # ① 使用者 PATH —— **兩條都要清**：現在掛的 publish/，以及舊版掛的 repo 根。
    $aPathChanged = $false
    foreach ($aTarget in @($aBin, $aLegacy)) {
        if (-not (Test-Entry (Get-UserPath) $aTarget)) { continue }
        [Environment]::SetEnvironmentVariable("Path", (Drop-Entry (Get-UserPath) $aTarget), "User")
        # 回讀驗證 —— 寫入端會替自己說謊
        if (Test-Entry (Get-UserPath) $aTarget) { Write-Error "✗ 移除後回讀仍看得到 $aTarget —— 沒有成功"; exit 1 }
        Write-Host "✓ 已從使用者 PATH 移除 $aTarget"
        $aPathChanged = $true
    }
    if (-not $aPathChanged) { Write-Host "・使用者 PATH 裡沒有 Senate 的條目 —— 這一格沒有東西要移除。" }

    # ② build 產物
    #
    #    🩸 刪完一定要**回讀**，不准 Remove-Item 之後直接印 ✓（2026-09-01 在 .sh 那支犯過）：
    #      實測時 src/Senate.Cli/bin 的內容被刪掉了、**目錄本身沒刪成**
    #      （Windows 上 Visual Studio／防毒會短暫抓著 handle），而畫面照樣印「✓ 已移除」——
    #      報告比實作大，沿途沒有一格會紅。
    #      ⇒ 刪 → 回讀 → 還在就重試一次 → 仍在就大聲說，並讓整支非零退出。
    $aRemoved = 0
    $aFailed = 0
    function Remove-Verify($iPath, $iLabel) {
        if (-not (Test-Path $iPath)) { return $false }
        Remove-Item $iPath -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path $iPath) {
            Start-Sleep -Seconds 1   # handle 多半是暫時的，重試一次
            Remove-Item $iPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path $iPath) {
            Write-Host "✗ 移不掉：$iLabel —— 多半是 Visual Studio 或防毒正抓著它，關掉再跑一次"
            $script:aFailed++
            return $false
        }
        Write-Host "✓ 已移除$iLabel"
        return $true
    }
    foreach ($p in $aArtifacts) {
        if (Remove-Verify (Join-Path $aRoot $p) "產物：$p") { $aRemoved++ }
    }
    # 各專案的 bin/obj —— 用搜尋不用列舉，日後新增專案不必回來改這裡
    foreach ($d in @("src", "SCP_Core")) {
        $aBase = Join-Path $aRoot $d
        if (-not (Test-Path $aBase)) { continue }
        Get-ChildItem $aBase -Directory -Recurse -Depth 1 |
            Where-Object { $_.Name -in @("bin", "obj") } |
            ForEach-Object {
                $aRel = $_.FullName.Substring($aRoot.Length + 1)
                if (Remove-Verify $_.FullName "中間產物：$aRel") { $aRemoved++ }
            }
    }
    if ($aRemoved -eq 0 -and $aFailed -eq 0) { Write-Host "・沒有 build 產物需要移除（本來就沒 build 過）" }

    # ③ 使用者設定 —— 顯式才動
    $aData = Join-Path $aRoot "SenateData"
    if ($Purge) {
        if (Test-Path $aData) {
            Remove-Item $aData -Recurse -Force
            Write-Host "✓ 已移除 SenateData/（含本機設定、頁面偏好、runtime 狀態）"
        }
        else { Write-Host "・沒有 SenateData/ 可移除" }
    }
    elseif (Test-Path $aData) {
        Write-Host ""
        Write-Host "・**保留** SenateData/ —— 那是你手動設定過的東西（專案清單、頁面偏好），掉了要重設。"
        Write-Host "  真的要一起刪：.\install.ps1 -Uninstall -Purge"
    }

    Write-Host ""
    if ($aFailed -gt 0) {
        Write-Host "⚠ 移除**未完成**：有 $aFailed 樣東西沒刪掉（上面標 ✗ 的那幾樣）。"
        Write-Host "  PATH 已經拿掉了 ⇒ senate 指令會消失，但那幾樣還佔著磁碟。"
        Write-Host "  關掉 Visual Studio／等防毒掃完，再跑一次 .\install.ps1 -Uninstall（冪等）。"
        exit 1
    }
    Write-Host "移除完成。⚠ 已開著的終端機不會變（PATH 是 process 啟動時複製的），要開新視窗。"
    Write-Host "・原始碼與 git 歷史一個字都沒動 —— 要徹底清掉請自己刪 $aRoot 這個資料夾。"
    exit 0
}

Write-Host "── Senate 安裝 ─────────────────────────────────"

# 前置：兩個外部相依。缺了就停在這裡 ——
# 讓它一路跑到 build 才炸的話，錯誤訊息會指向編譯，而真正的問題是環境。
$aDotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)
if (-not $aDotnet) { Write-Error "✗ 找不到 dotnet —— 請先安裝 .NET 10 SDK：https://dotnet.microsoft.com/download"; exit 1 }
Write-Host ("  dotnet : " + (& dotnet --version))
$aGit = (Get-Command git -ErrorAction SilentlyContinue)
if (-not $aGit) { Write-Error "✗ 找不到 git —— 請先安裝 git（需 2.25 以上）"; exit 1 }
Write-Host ("  git    : " + ((& git --version) -replace '^git version '))

# build 一律走 build.ps1 —— ⛔ 不要在這裡另寫一條 dotnet build。
# 🩸 舊版 setup.ps1 跑的是 `dotnet build Senate.slnx -c Release`（framework-dependent DLL），
#   而 build.ps1 產出的是 publish 的 self-contained single-file exe。兩顆跑起來長得一模一樣，
#   於是「我測過了」測的是哪一顆沒有人答得出來。實測（2026-09-01）：五顆可執行產物、三種年份。
#   ⇒ build 只留一個入口，第二條路不是備援是分岔。
$aExe = Join-Path $aBin "senate.exe"
if ($SkipBuild) {
    Write-Host "── build（-SkipBuild：沿用現有產物，不重新 build）"
    if (-not (Test-Path $aExe)) { Write-Error "✗ 沒有 publish/senate.exe 可沿用 —— 拿掉 -SkipBuild 再跑一次"; exit 1 }
}
else {
    Write-Host "── build（走 build.ps1，含出廠驗收）────────────"
    & (Join-Path $aRoot "build.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Error @"
✗ build 或出廠驗收沒過 ⇒ **PATH 不掛**。
  裝一條指向壞產物的 PATH，之後每一個錯都會被怪到 PATH 上，而那是錯的方向。
  ⚠ 若失敗的是驗收③開窗那格（遠端桌面／無 GPU 的機器會），
    可先自己跑 .\build.ps1 看讀數，再用 .\install.ps1 -SkipBuild 掛 PATH。
"@
        exit 1
    }
}

# 本機設定 —— init 只在檔案不存在時建立，絕不覆寫（那是 init 自己的保證，不是這裡的）
Write-Host "── 本機設定（init）─────────────────────────────"
& $aExe init

# 使用者 PATH
Write-Host "── 掛使用者 PATH ───────────────────────────────"
# 遷移：舊版掛 repo 根，而根層現在沒有可執行檔 ⇒ 先拿掉再掛新的（兩步都回讀）。
if (Test-Entry (Get-UserPath) $aLegacy) {
    [Environment]::SetEnvironmentVariable("Path", (Drop-Entry (Get-UserPath) $aLegacy), "User")
    if (Test-Entry (Get-UserPath) $aLegacy) { Write-Error "✗ 舊的 PATH 條目移不掉（$aLegacy）—— 停手，不做半套遷移"; exit 1 }
    Write-Host "✓ 已移除舊的 PATH 條目 $aLegacy（執行檔已搬到 publish/）"
}

if (Test-Installed (Get-UserPath)) {
    Write-Host "・$aBin 已經在使用者 PATH 裡 —— 不重複加。"
}
else {
    [Environment]::SetEnvironmentVariable("Path", (Get-UserPath) + ";" + $aBin, "User")
    if (-not (Test-Installed (Get-UserPath))) { Write-Error "✗ 寫入後回讀不到 —— 沒有成功"; exit 1 }
    Write-Host "✓ 已把 $aBin 加進使用者 PATH（回讀確認）"
}

# 出廠驗收：模擬新視窗的 PATH，從別的目錄跑一次
Write-Host "── 驗收：用新 PATH 從別的目錄跑一次 ────────────"
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + (Get-UserPath)
Push-Location $env:TEMP
try {
    if (Get-Command senate -ErrorAction SilentlyContinue) {
        senate --help | Select-Object -First 1 | Write-Host
        Write-Host ""
        Write-Host "✓ 安裝完成 —— **開一個新的 CMD / PowerShell** 就能直接用：senate cmd status"
        Write-Host "（已開著的終端機不會自動生效；移除：.\install.ps1 -Uninstall）"
    }
    else { Write-Error "✗ 新 PATH 下解析不到 senate —— PATH 寫進去了但驗收沒過"; exit 1 }
}
finally { Pop-Location }
