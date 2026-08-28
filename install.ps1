# 全域安裝（PowerShell）—— 與 install.sh 等價：讓 `senate` 在任何終端機直接可用。
#
# 區塊職責：把 senate.exe 所在目錄（＝repo 根）寫進**使用者 PATH**；-Uninstall 移除。
# 物理意義／數值影響：見 install.sh 檔頭（兩支同一套規格，只是宿主 shell 不同）——
#   只動 HKCU 使用者 PATH、冪等、寫入走 .NET（setx 有 1024 截斷坑）、
#   已開著的終端機不會變（PATH 是 process 啟動時複製的）。
param([switch]$Uninstall)
$ErrorActionPreference = "Stop"
$aRoot = Split-Path -Parent $MyInvocation.MyCommand.Path

function Get-UserPath { [Environment]::GetEnvironmentVariable("Path", "User") }
function Test-Installed($iPath) {
    ($iPath -split ";") | Where-Object { $_.TrimEnd("\", "/") -ieq $aRoot.TrimEnd("\", "/") } | ForEach-Object { return $true }
    return $false
}

if ($Uninstall) {
    $aCur = Get-UserPath
    if (-not (Test-Installed $aCur)) { Write-Host "・使用者 PATH 裡本來就沒有 $aRoot —— 沒有東西要移除。"; exit 0 }
    $aNew = (($aCur -split ";") | Where-Object { $_.TrimEnd("\", "/") -ine $aRoot.TrimEnd("\", "/") }) -join ";"
    [Environment]::SetEnvironmentVariable("Path", $aNew, "User")
    if (Test-Installed (Get-UserPath)) { Write-Error "✗ 移除後回讀仍看得到 —— 沒有成功"; exit 1 }
    Write-Host "✓ 已從使用者 PATH 移除 $aRoot（已開的終端機要重開才會變）"
    exit 0
}

Write-Host "── Senate 全域安裝 ─────────────────────────────"
if (-not (Test-Path (Join-Path $aRoot "senate.exe"))) {
    Write-Error "✗ $aRoot 底下沒有 senate.exe —— 先跑 .\build.ps1 產出它，再跑本腳本"; exit 1
}

if (Test-Installed (Get-UserPath)) {
    Write-Host "・$aRoot 已經在使用者 PATH 裡 —— 不重複加。"
}
else {
    [Environment]::SetEnvironmentVariable("Path", (Get-UserPath) + ";" + $aRoot, "User")
    if (-not (Test-Installed (Get-UserPath))) { Write-Error "✗ 寫入後回讀不到 —— 沒有成功"; exit 1 }
    Write-Host "✓ 已把 $aRoot 加進使用者 PATH（回讀確認）"
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
