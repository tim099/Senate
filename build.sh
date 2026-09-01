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

dotnet publish src/Senate.Cli \
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

echo
if [ "$code" -eq 0 ] && [ "$selftest" -eq 0 ] && [ "$gui" -eq 0 ]; then
  echo '✓ 出廠驗收全過。開 GUI：./senate.exe ui --window（或直接雙擊 senate.exe 會印用法）'
else
  # 三格分開印 —— 壓成一句「驗收未過」會讓人不知道要去看哪一格
  echo "⚠ 出廠驗收有項目未過（doctor=$code / selftest=$selftest / gui=$gui）"
fi
[ "$code" -eq 0 ] && [ "$selftest" -eq 0 ] && [ "$gui" -eq 0 ]
