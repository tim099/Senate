# 一鍵 build（Windows / PowerShell）-- 產出 repo 根的 senate.exe（可直接雙擊）。
#
# 區塊職責：publish → 把執行檔與原生 DLL 放到 repo 根 → **真的跑一次＋真的開一次窗**。
# 物理意義：最後那兩步才是重點。「build succeeded」只證明編譯器沒抱怨，
#           完全沒證明那顆 exe 跑得起來 —— 而 self-contained 最常壞的地方正好在執行期，
#           且文字模式照常運作，所以開窗的錯只有真的去開窗才會現形。
#
# single-file 的真正判準（實測 2026-08-22，一開始結論下得太廣）：
#   ✗ IncludeNativeLibrariesForSelfExtract=true -- 原生 DLL 被包進單檔後 Silk.NET 找不到，
#     開窗丟 PlatformNotSupportedException: Couldn't find a suitable window platform。
#   ✗ IncludeAllContentForSelfExtract=true -- app base 變成 temp 解壓目錄，
#     本程式「往上找 .git 定位 repo 根」會失準，設定檔就找錯地方而且不報錯。
#   ✅ single-file 加原生 DLL 留在 exe 旁邊 -- 兩個坑都沒有，且 exe 就在 repo 根，
#     AppContext.BaseDirectory 直接是 repo 根（路徑解析最短、最不會錯）。
#   ⇒ 根層會有三個檔：senate.exe / cimgui.dll / glfw3.dll（都不入版控）。
#
# ⚠ 本檔必須存成 **UTF-8 with BOM**：Windows PowerShell 5.1 沒有 BOM 就用 ANSI(cp950) 讀，
#   中文會變亂碼、連字串終止符都被吃掉 ⇒ 整支腳本 parse error（2026-08-22 實撞，Tim 回報）。

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$root = $PSScriptRoot
Set-Location $root

Write-Host '-- Senate 一鍵 build ---------------------------'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error '找不到 dotnet -- 先跑 .\setup.ps1'; exit 1
}

& dotnet publish (Join-Path $root 'src/Senate.Cli') -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o (Join-Path $root 'publish') --nologo -v minimal
if ($LASTEXITCODE -ne 0) { Write-Error 'publish 失敗 -- 上面的編譯錯誤就是原因'; exit 1 }

# 覆寫剛 publish 出來的 exe 會撞兩種鎖：① exe 正在執行中（Windows 不准覆寫）
# ② 防毒正在掃描剛寫完的 74MB 檔。兩者都是暫時的 ⇒ 重試三次，仍失敗就講清楚是哪一種。
function Copy-WithRetry($src, $dst) {
    for ($i = 1; $i -le 3; $i++) {
        try { Copy-Item $src $dst -Force -ErrorAction Stop; return }
        catch {
            if ($i -eq 3) {
                Write-Host "失敗 無法寫入 $dst"
                Write-Host "  可能原因：senate.exe 正在執行中（關掉 GUI 視窗再試），或防毒正在掃描。"
                Write-Host "  原始訊息：$($_.Exception.Message)"
                exit 1
            }
            Start-Sleep -Milliseconds 800
        }
    }
}
Copy-WithRetry (Join-Path $root 'publish/Senate.Cli.exe') (Join-Path $root 'senate.exe')
# 原生 DLL 必須跟 exe 同層（見下方註解）-- 少一顆的症狀是「文字模式好、開窗掛」
foreach ($dll in @('cimgui.dll', 'glfw3.dll')) {
    $src = Join-Path $root "publish/$dll"
    if (Test-Path $src) { Copy-WithRetry $src (Join-Path $root $dll) }
    else { Write-Host "警告 publish/$dll 不存在 -- 開窗可能會失敗（Silk.NET 找不到原生層）" }
}

$mb = [math]::Round((Get-Item (Join-Path $root 'senate.exe')).Length / 1MB, 1)
Write-Host ''
Write-Host ("完成 產物：" + (Join-Path $root 'senate.exe') + " ($mb MB) 加 cimgui.dll / glfw3.dll")

Write-Host '-- 出廠驗收(1) doctor -------------------------'
& (Join-Path $root 'senate.exe') doctor
$code = $LASTEXITCODE

Write-Host '-- 出廠驗收(2) selftest（對 exe，不是對 Debug DLL）--'
# 🩸 理由同 build.sh 的同一格：agent 驗的是 Debug DLL、人跑的是這顆 exe，
#   兩者的「全綠」是兩本帳，而它們在畫面上長得一模一樣。
& (Join-Path $root 'senate.exe') selftest
$selftest = $LASTEXITCODE

Write-Host '-- 出廠驗收(3) 開窗（截圖後自動關）------------'
$shot = Join-Path $root 'build/build_check.png'
$log = Join-Path $root 'build/build_check.log'
& (Join-Path $root 'senate.exe') ui --screenshot $shot > $log 2>&1
$gui = $LASTEXITCODE
if ($gui -eq 0) { Write-Host '完成 開窗成功，截圖：build/build_check.png' }
else {
    Write-Host "失敗 開窗失敗（exit $gui）-- 詳見 build/build_check.log"
    Get-Content $log -Tail 3
}

Write-Host ''
if ($code -eq 0 -and $selftest -eq 0 -and $gui -eq 0) {
    Write-Host '完成 出廠驗收全過。開 GUI：.\senate.exe ui --window'
    exit 0
}
# 三格分開印 -- 壓成一句「驗收未過」會讓人不知道要去看哪一格
Write-Host "警告 出廠驗收有項目未過（doctor=$code / selftest=$selftest / gui=$gui）"
exit 1
