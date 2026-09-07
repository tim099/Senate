#!/usr/bin/env sh
# 一鍵 build —— 產出 publish/senate.exe，並在 repo 根放一個給人雙擊的捷徑 senate.lnk。
#
# 區塊職責：publish → 驗原生 DLL 與捷徑 → 收尾留一顆常駐視窗。**本腳本不做驗收。**
# 物理意義：出廠驗收（doctor／selftest／開窗／Server round-trip）**已搬到 `check.sh`**
#           （Tim 2026-09-07：測試流程跟 build 分離、另外跑、可挑項目 —— 項目只會愈來愈多，
#           而「改一行就得付全套驗收」會讓人開始繞過它）。
# ⚠ 而那四關存在的理由沒有變：「build succeeded」只證明編譯器沒抱怨，
#   完全沒證明那顆 exe 跑得起來 —— self-contained 最常壞在執行期，且**文字模式照常運作**，
#   所以開窗的錯只有真的去開窗才會現形。
#   ⇒ 所以本腳本收尾會**明講「本次沒有驗收」**並印出指令；⛔ 不靜默結束。
#   `--check` ＝ build 完接著驗（同一份實作，只是把兩步串起來）。
#
# 🩸 single-file 的真正判準（實測 2026-08-22，一開始我把結論下得太廣）：
#   ✗ `IncludeNativeLibrariesForSelfExtract=true` —— 原生 DLL 被包進單檔後 Silk.NET 找不到，
#     開窗丟 `PlatformNotSupportedException: Couldn't find a suitable window platform`。
#   ✗ `IncludeAllContentForSelfExtract=true` —— app base 變成 temp 解壓目錄 ⇒
#     本程式「往上找 .git 定位 repo 根」會失準，設定檔就找錯地方而且不報錯。
#   ✅ **single-file ＋ 原生 DLL 留在 exe 旁邊** —— 兩個坑都沒有，而且 exe 就在 repo 根，
#     `AppContext.BaseDirectory` 直接是 repo 根（路徑解析最短、最不會錯）。
#   ⇒ 所以根層會有三個檔：senate.exe / cimgui.dll / glfw3.dll（都不入版控）。
set -e
root="$(cd "$(dirname "$0")" && pwd)"
cd "$root"

# ── 參數：驗收**預設不跑**（Tim 2026-09-07：測試流程跟 build 分離、另外跑）────────
#   `--check` ＝ build 完接著跑 `check.sh`（把兩件事串起來的便利入口，實作只有一份）。
#   `--only` / `--gates` 原樣轉給 `check.sh` ⇒ 挑項目的規則只有一份，不在這裡再解一次。
# ⛔ 認不得的參數擋下來，不靜默忽略：打錯 `--chek` 卻照樣 build 完就走，
#   畫面會跟「我本來就沒要求驗收」一模一樣。
do_check=0
check_args=""
while [ $# -gt 0 ]; do
  case "$1" in
    --check) do_check=1; shift ;;
    --only|--gates) do_check=1; check_args="$check_args $1 ${2:-}"; shift 2 ;;
    --only=*|--gates=*) do_check=1; check_args="$check_args $1"; shift ;;
    -h|--help) echo "用法：./build.sh [--check] [--gates doctor,self,gui,server] [--only <selftest 篩選>]";
               echo "  預設**只 build 不驗收**；驗收另外跑：./check.sh"; exit 0 ;;
    *) echo "✗ 認不得的參數：$1（可用：--check / --gates / --only / --help）" >&2; exit 2 ;;
  esac
done

echo '── Senate 一鍵 build ───────────────────────────'
command -v dotnet >/dev/null 2>&1 || { echo '✗ 找不到 dotnet —— 先跑 ./install.sh' >&2; exit 1; }

# ── publish 前先收掉會鎖住 exe 的東西：① 常駐 Server ② 上一顆 GUI 視窗（TASK-0102＋2026-09-04）
# 🩸 D10：覆寫 publish 出來的 exe 會撞「exe 正在執行中」的鎖。鎖它的有兩種 process：
#   前景永駐的 Server，以及**收尾留下來的那顆視窗**（2026-09-04 起 build 會自己開一顆）。
#   兩者都要收 —— 2026-09-03／09-04 各撞一次 `GenerateBundle … Access to the path … is denied`，
#   兩次佔住它的都是一顆開著的視窗，而錯誤訊息不會告訴你是誰。
#   ⚠ 用**舊的** exe 去停 Server（新的還沒 build 出來）；舊 exe 不存在就沒有東西可停。
had_server=0
if [ -f "$root/publish/senate.exe" ]; then
  if "$root/publish/senate.exe" server status > /dev/null 2>&1; then had_server=1; fi
  "$root/publish/senate.exe" server stop || echo "⚠ server stop 回非零 —— 若 publish 撞鎖，先手動收掉 Server 再重跑"
  # ⚠ 寫成 `[ ... ] && echo` 會在**沒有 Server 在跑**時讓整支腳本當場 abort：
  #   `set -e` 底下 `A && B` 的 A 失敗 ⇒ 整個 list 回非零、且它不在條件位置。用 if，不用短路。
  if [ "$had_server" -eq 1 ]; then
    echo "· 你本來有一顆 Server 在跑 —— 已停；**build 完不會自動起回來**（收尾會再提醒一次）"
  fi
  # ② 視窗：先請它自己關（CloseMainWindow），2 秒不走才 kill。只收**這顆 exe** 開的，
  #    比對的是 Path 不是 process 名 —— 別台／別份 clone 的 senate 不干我的事。
  if command -v powershell.exe > /dev/null 2>&1; then
    # ⚠ 這段 PowerShell 整個住在 bash 的 '...' 裡 ⇒ **裡面一律只用雙引號**。
    #   🩸 2026-09-04：寫了 'Open', 'Write' ⇒ bash 在第一個單引號就把字串收掉，
    #     PS 拿到被切碎的碼、回非零、零輸出，而畫面上只有一行「收視窗那步回非零」。
    SENATE_EXE_WIN="$(cygpath -w "$root/publish/senate.exe" 2>/dev/null || echo "$root/publish/senate.exe")" \
    powershell.exe -NoProfile -NonInteractive -Command '
      $t = $env:SENATE_EXE_WIN
      $ps = @(Get-Process -Name senate -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $t })
      if ($ps.Count -gt 0) {
        Write-Host ("· 收掉 " + $ps.Count + " 顆還開著的 senate（它們鎖著 publish/senate.exe）")
        foreach ($p in $ps) { try { $null = $p.CloseMainWindow() } catch { } }
        foreach ($p in $ps) { try { $null = $p.WaitForExit(2000) } catch { } }
        foreach ($p in $ps) { try { if (-not $p.HasExited) { $p.Kill(); $null = $p.WaitForExit(3000) } } catch { } }
      }
      $free = $false
      for ($i = 0; $i -lt 20; $i++) {
        try { $fs = [System.IO.File]::Open($t, "Open", "Write"); $fs.Close(); $free = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
      }
      if (-not $free) { Write-Host "⚠ publish/senate.exe 仍被鎖著（等了 5 秒）—— publish 大概會撞 Access denied" }
      elseif ($ps.Count -gt 0) { Write-Host "· exe 已可寫入" }' 2>/dev/null || echo "⚠ 收視窗那步回非零 —— 若 publish 撞鎖，手動關掉開著的 senate 視窗再重跑"
  fi
fi


# build id：git SHA ＋ UTC 時間 ⇒ 進 AssemblyInformationalVersion，Server 心跳與 CLI 拿它對「是不是同一顆 exe」。
# ⚠ IncludeSourceRevisionInInformationalVersion 關掉：不然 SDK 會再接一段 +sha，兩邊字串就對不上。
build_sha="$(git -C "$root" rev-parse --short HEAD 2>/dev/null || echo nogit)"
build_dirty=""; [ -n "$(git -C "$root" status --porcelain --untracked-files=no 2>/dev/null | head -1)" ] && build_dirty="-dirty"
build_id="${build_sha}${build_dirty}.$(date -u +%Y%m%dT%H%M%SZ)"
echo "· build id：$build_id"

dotnet publish src/Senate.Cli   -p:InformationalVersion="$build_id" -p:IncludeSourceRevisionInInformationalVersion=false \
  -c Release \
  -r win-x64 \
  --self-contained \
  -p:PublishSingleFile=true \
  -o publish \
  --nologo -v minimal

# 執行檔就住在 publish/ —— **不複製到根層**（Tim 2026-09-01 拍板）。
# 🩸 舊版把 publish/Senate.Cli.exe 複製成根層 senate.exe，理由只是「指令要叫 senate」。
#   代價是 78 MB 的第二顆檔案、會過期、而且複製本身會撞「exe 正在執行中」的鎖。
#   ⇒ 改由 csproj 的 <AssemblyName>senate</AssemblyName> 直接產出對的檔名，
#     PATH 指向 publish/（install.sh 負責），根層只留一個給人雙擊的捷徑。
exe="$root/publish/senate.exe"
[ -f "$exe" ] || { echo "✗ publish/senate.exe 不存在 —— publish 沒成功？" >&2; exit 1; }

# 原生 DLL 必須跟 exe 同層 —— 少一顆的症狀是「文字模式好、開窗掛」。
# publish 會自己把它們放進 publish/，所以這裡只驗不搬（搬就是又一份會過期的複本）。
for dll in cimgui.dll glfw3.dll; do
  [ -f "$root/publish/$dll" ] || echo "⚠ publish/$dll 不存在 —— 開窗可能會失敗（Silk.NET 找不到原生層）"
done

# 根層捷徑：**只服務滑鼠**，完全不參與 PATH（PATH 指的是 publish/）。
# ⚠ 這是 Windows .lnk，不是 symlink —— symlink 需要 admin／開發者模式（這台實測建不出來），
#   hardlink 則會被下一次 publish 打斷（實測 link 數 2→1，而外層會靜默停在舊版）。
if command -v powershell.exe >/dev/null 2>&1; then
  winexe="$(cygpath -w "$exe" 2>/dev/null || echo "$exe")"
  winlnk="$(cygpath -w "$root/senate.lnk" 2>/dev/null || echo "$root/senate.lnk")"
  windir="$(cygpath -w "$root/publish" 2>/dev/null || echo "$root/publish")"
  powershell.exe -NoProfile -NonInteractive -Command \
    "\$s=(New-Object -ComObject WScript.Shell).CreateShortcut('$winlnk'); \$s.TargetPath='$winexe'; \$s.WorkingDirectory='$windir'; \$s.Save()" >/dev/null 2>&1 \
    && echo "✓ 根層捷徑：senate.lnk → publish/senate.exe（雙擊用）" \
    || echo "⚠ 捷徑沒建成 —— 不影響指令，publish/senate.exe 照樣能跑"
fi

mb=$(( $(stat -c %s "$exe" 2>/dev/null || stat -f %z "$exe") / 1048576 ))
echo
echo "✓ 產物：$exe（${mb} MB）＋ 同層的 cimgui.dll / glfw3.dll"


# ── 收尾 ────────────────────────────────────────────────────────────────
# ⚠ 出廠驗收**已經搬到 `check.sh`**（Tim 2026-09-07：測試流程跟 build 分離、另外跑、可挑項目）。
#
# 🩸 而 2026-08-30 Tim 把 selftest 綁上 build 的理由仍然成立：
#   **驗收不在必經路上就會沒有人跑。**
#   ⇒ 所以這裡**明講「本次沒有驗收」並印出那一行指令** —— 分家的是流程，不是那個事實。
#   ⛔ 靜默結束就是把「沒驗」與「驗過了」變成同一個畫面，而那正是這個專案一直在修的形狀。
if [ "$had_server" -eq 1 ]; then
  echo '⚠ 你 build 前掛著的那顆 Server 已被停掉，而 build **不會**幫你起回來 ——'
  echo '   要用 ⤷Server 的 Cmd 就開一個終端機跑：senate server start'
else
  echo '· Server：本來就沒在跑，現在也沒有（⤷Server 的 Cmd 需要 `senate server start`）'
fi

if [ "$do_check" -eq 1 ]; then
  echo
  # ⚠ 一定要 `set +e`：`set -e` 底下 check.sh 回非零會讓本腳本**當場 abort**，
  #   於是收尾那顆常駐視窗不會被開 —— 而「驗收失敗」與「順便少開一顆視窗」是兩件事，
  #   ⛔ 不該由同一個非零綁在一起。判定留到最後一行 exit。
  set +e
  "$root/check.sh" ${check_args:+$check_args}
  check_rc=$?
  set -e
else
  echo
  echo '⚠ **本次沒有跑出廠驗收** —— build 綠燈只證明編譯器沒抱怨，'
  echo '   完全沒證明那顆 exe 跑得起來（self-contained 最常壞在執行期，而文字模式照常運作）。'
  echo '   ⇒ 驗收：./check.sh          （四關全跑）'
  echo '           ./check.sh --only watch   （第②關只跑命中的項目；清單見 senate selftest --list）'
  echo '           ./build.sh --check        （build 完接著驗）'
  check_rc=0
fi

# ── 開一顆**常駐**視窗（Tim 2026-09-04 拍板）──────────────
# 物理意義：build 之後你本來就要開它 —— 那一步交給腳本，人不用記得重開。
#   ⚠ 這顆會**鎖住 publish/senate.exe** ⇒ 下一次 build 開頭會自己把它收掉。
#   ⛔ 它不是驗收格：不看它的 exit code、不擋 build 判定。
#   ⚠ 要 nohup：不然關掉這個終端機時 SIGHUP 會把它一起帶走 ——
#     而「視窗自己消失」跟「它當掉了」同形。
mkdir -p "$root/build"
nohup "$exe" ui --window > "$root/build/build_window.log" 2>&1 &
#   ⚠ 不印 pid：$! 給的是 Git Bash 的 MSYS pid，而工作管理員看到的是另一個號 ——
#     印一個查不到的號比不印更糟。
echo "· 已開一顆常駐視窗（log：build/build_window.log）—— 下次 build 會自己收掉它"

exit "$check_rc"
