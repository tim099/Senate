# 一鍵配置（Windows / PowerShell）—— clone 完只要跑這一支。
#
# 區塊職責：檢查前置、build、產生本機設定、印讀數。
# 物理意義：這支腳本刻意**只做編排**，所有判斷都在 `senate doctor` 裡（C#）——
#           檢查邏輯寫在腳本裡的話，PowerShell 版與 sh 版就是兩份會漂的實作，
#           而漂掉的症狀是「在我機器上說 OK、在你機器上說 OK，但兩邊檢查的東西不一樣」。
# 數值影響：不覆寫既有的 senate.local.json（那是 init 的保證，不是這裡的）。

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = $PSScriptRoot

Write-Host '── Senate 一鍵配置 ─────────────────────────────'

# ① dotnet 在不在（不在就明確說要裝什麼，不要讓後面的 build 丟一句看不懂的錯）
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Error '找不到 dotnet —— 請先安裝 .NET 10 SDK：https://dotnet.microsoft.com/download'
    exit 1
}
Write-Host ("  dotnet : " + (& dotnet --version))

# ② git 在不在（本系統的核心工作就是呼叫真的 git）
$git = Get-Command git -ErrorAction SilentlyContinue
if (-not $git) { Write-Error '找不到 git —— 請先安裝 git（需 2.25 以上）'; exit 1 }
Write-Host ("  git    : " + ((& git --version) -replace '^git version ',''))

# ③ build（restore 由 build 自己帶）
Write-Host '── build ───────────────────────────────────────'
& dotnet build (Join-Path $root 'Senate.slnx') -c Release --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Write-Error 'build 失敗 —— 上面的編譯錯誤就是原因'; exit 1 }

# ④ init + doctor（真正的檢查在這裡；exit code 直接當本腳本的結果）
Write-Host '── init & doctor ───────────────────────────────'
& dotnet run --project (Join-Path $root 'src/Senate.Cli') -c Release --no-build -- init
$code = $LASTEXITCODE

Write-Host ''
if ($code -eq 0) {
    Write-Host '✓ 配置完成，doctor 全部通過。'
} else {
    Write-Host "⚠ 配置完成，但 doctor 有項目不通過（exit $code）—— 上面的表格就是哪一格。"
}
exit $code
