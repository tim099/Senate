#!/usr/bin/env sh
# 一鍵 build —— 產出 publish/senate.exe，並在 repo 根放一個給人雙擊的捷徑 senate.lnk。
#
# 區塊職責：publish → 把執行檔與原生 DLL 放到 repo 根 → **真的跑一次＋真的開一次窗**。
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

# ── publish 前先停常駐 Server（TASK-0102）─────────────────────
# 🩸 D10：覆寫 publish 出來的 exe 會撞「exe 正在執行中」的鎖 —— Server 是前景永駐，
#   一定在鎖著它。stop 是冪等的（沒在跑也 exit 0），所以每次 build 都無條件呼叫。
#   ⚠ 用**舊的** exe 去停（新的還沒 build 出來）；舊 exe 不存在就沒有 Server 可停。
if [ -f "$root/publish/senate.exe" ]; then
  "$root/publish/senate.exe" server stop || echo "⚠ server stop 回非零 —— 若 publish 撞鎖，先手動收掉 Server 再重跑"
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
  echo '✓ 出廠驗收全過。開 GUI：./senate.exe ui --window（或直接雙擊 senate.exe 會印用法）'
else
  # 四格分開印 —— 壓成一句「驗收未過」會讓人不知道要去看哪一格
  echo "⚠ 出廠驗收有項目未過（doctor=$code / selftest=$selftest / gui=$gui / server=$server）"
fi
[ "$code" -eq 0 ] && [ "$selftest" -eq 0 ] && [ "$gui" -eq 0 ]
