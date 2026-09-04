#!/usr/bin/env sh
# 一鍵 build —— 產出 publish/senate.exe，並在 repo 根放一個給人雙擊的捷徑 senate.lnk。
#
# 區塊職責：publish → 把執行檔與原生 DLL 放到 repo 根 → **真的跑一次＋真的開一次窗** → 收尾留一顆常駐視窗。
# 物理意義：⭐ 最後那兩步才是重點。「build succeeded」只證明編譯器沒抱怨，
#           完全沒證明那顆 exe 跑得起來 —— 而 self-contained 最常壞的地方正好在執行期，
#           且**文字模式照常運作**，所以開窗的錯只有真的去開窗才會現形。
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

# ── 出廠驗收①：跑真的 exe（不是看 build 綠燈）──────
echo '── 出廠驗收① doctor ───────────────────────────'
set +e
"$exe" doctor
code=$?
set -e

# ── 出廠驗收②：**對 exe 跑自我對拍**（Tim 2026-08-30 拍板）────────
# 🩸 為什麼加這一格：agent 改完 code 的驗證迴圈是 `dotnet run`（Debug DLL），
#   而人跑的是這顆 exe（Release / self-contained / single-file）——**兩個不同的二進位檔**。
#   「Debug 全綠」與「你手上這顆 exe 全綠」是兩本帳，而它們在畫面上長得一模一樣。
#   ⇒ 把 selftest 綁在 build 上：驗收長在必經路上，不必靠誰記得跑。
echo '── 出廠驗收② selftest（對 exe，不是對 Debug DLL）──'
set +e
"$exe" selftest
selftest=$?
set -e

# ── 出廠驗收③：**真的開一次窗**（原生 DLL 的坑就是死在這一格）──
# ⚠ build/ 一定要先建出來 —— 截圖與 log 寫在那裡，而**沒有別人會建它**。
# 🩸 2026-09-01：runtime 狀態（_process_registry / ui_session.json）搬進 SenateData/ 之後，
#   build/ 就沒有任何產生者了。在此之前它是 doctor 順手建出來的副作用，
#   於是這一格的相依「一直成立但從來沒有人宣告過」—— fresh clone 會直接撞。
mkdir -p "$root/build"
echo '── 出廠驗收③ 開窗（截圖後自動關）──────────────'
set +e
"$exe" ui --screenshot "$root/build/build_check.png" > "$root/build/build_check.log" 2>&1
gui=$?
set -e
if [ "$gui" -eq 0 ]; then
  echo "✓ 開窗成功，截圖：build/build_check.png"
else
  echo "✗ 開窗失敗（exit $gui）—— 詳見 build/build_check.log"
  tail -3 "$root/build/build_check.log"
fi

# ── 出廠驗收④：**Server round-trip**（TASK-0100 主單那格）──────────────
# 物理意義：selftest 對拍的是 result 檔的 schema，不是「一顆 CLI 送、一顆 Server 接、result 回來」這條路。
#   那條路只有真的起一顆 Server 才有讀數 —— 所以在這裡起一顆、ping 一次、收掉。
# ⚠ 這顆 Server 是**驗收用的臨時 process**，起在背景、收在這一段結束前；publish 前的 `server stop` 已保證沒有別顆在跑。
#   ping 失敗不代表 exe 壞，可能是 Server 沒在 3 秒內起來 —— 所以印 Server 自己的 log 尾巴，不猜。
echo '── 出廠驗收④ Server round-trip（起一顆臨時 Server → server-ping → 收掉）──'
set +e
"$exe" server start > "$root/build/build_server.log" 2>&1 &
server_pid=$!
for _ in 1 2 3 4 5 6; do
  "$exe" server status > /dev/null 2>&1 && break
  sleep 0.5
done
"$exe" cmd server-ping --arg echo=build-check > "$root/build/build_ping.log" 2>&1
server=$?
"$exe" server stop > /dev/null 2>&1
wait "$server_pid" 2>/dev/null
set -e
if [ "$server" -eq 0 ] && grep -q "echo = build-check" "$root/build/build_ping.log"; then
  echo "✓ Server round-trip 通（$(grep -o 'server_pid = [0-9]*' "$root/build/build_ping.log" | head -1)）"
else
  server=1
  echo "✗ Server round-trip 失敗（ping exit $server）—— build/build_ping.log 與 build/build_server.log："
  tail -3 "$root/build/build_ping.log"; tail -3 "$root/build/build_server.log"
fi


echo
if [ "$code" -eq 0 ] && [ "$selftest" -eq 0 ] && [ "$gui" -eq 0 ] && [ "$server" -eq 0 ]; then
  echo '✓ 出廠驗收全過。'
else
  # 每一格分開印 —— 壓成一句「驗收未過」會讓人不知道要去看哪一格
  echo "⚠ 出廠驗收有項目未過（doctor=$code / selftest=$selftest / gui=$gui / server=$server）"
fi

# ── 收尾：Server 現在是停的（規則長在必經路上，不掛在誰的記性裡）───────────
# 物理意義：④ 起的那顆是**驗收用的臨時 process**，同一段就收掉了；而開頭那次 stop 收掉的是
#   使用者原本掛著的那顆。⇒ build 結束時**一定沒有 Server 在跑**，而下一個 `⤷Server` 的
#   Cmd 會 exit 3（不降級成本地跑）。那個 exit 3 長得像「壞了」，其實是「沒人起它」。
if [ "$had_server" -eq 1 ]; then
  echo '⚠ 你 build 前掛著的那顆 Server 已被停掉，而 build **不會**幫你起回來 ——'
  echo '   要用 ⤷Server 的 Cmd 就開一個終端機跑：senate server start'
else
  echo '· Server：本來就沒在跑，現在也沒有（⤷Server 的 Cmd 需要 `senate server start`）'
fi
# ── 收尾：開一顆**常駐**視窗（Tim 2026-09-04 拍板）──────────────
# 物理意義：build 之後你本來就要開它 —— 那一步交給腳本，人不用記得重開。
#   ⚠ 這顆會**鎖住 publish/senate.exe** ⇒ 下一次 build 開頭會自己把它收掉（見上面 ② 那段）。
#   ⛔ 它不是驗收格：不看它的 exit code、不擋 build 判定。開窗**畫得出來**由 ③ 的截圖擋。
#   ⚠ 要 nohup：不然關掉這個終端機時 SIGHUP 會把它一起帶走 —— 而「視窗自己消失」跟「它當掉了」同形。
nohup "$exe" ui --window > "$root/build/build_window.log" 2>&1 &
#   ⚠ 不印 pid：$! 給的是 Git Bash 的 MSYS pid（實測 2081），而工作管理員／Get-Process 看到的是
#     另一個號（14648）—— 印一個查不到的號比不印更糟。
echo "· 已開一顆常駐視窗（log：build/build_window.log）—— 下次 build 會自己收掉它"

[ "$code" -eq 0 ] && [ "$selftest" -eq 0 ] && [ "$gui" -eq 0 ]
