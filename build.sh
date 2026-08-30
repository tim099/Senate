#!/usr/bin/env sh
# 一鍵 build —— 產出 repo 根的 senate.exe（可直接雙擊）。
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
command -v dotnet >/dev/null 2>&1 || { echo '✗ 找不到 dotnet —— 先跑 ./setup.sh' >&2; exit 1; }

dotnet publish src/Senate.Cli \
  -c Release \
  -r win-x64 \
  --self-contained \
  -p:PublishSingleFile=true \
  -o publish \
  --nologo -v minimal

# 覆寫剛 publish 出來的 exe 會撞兩種鎖：① exe 正在執行中（Windows 不准覆寫）
# ② 防毒正在掃描剛寫完的 74MB 檔。兩者都是暫時的 ⇒ 重試三次，仍失敗就講清楚是哪一種。
copy_retry() {
  i=1
  while [ $i -le 3 ]; do
    if cp -f "$1" "$2" 2>/dev/null; then return 0; fi
    i=$((i+1)); sleep 1
  done
  echo "✗ 無法寫入 $2"
  echo "  可能原因：senate.exe 正在執行中（關掉 GUI 視窗再試），或防毒正在掃描。"
  return 1
}
copy_retry publish/Senate.Cli.exe "$root/senate.exe"
# 原生 DLL 必須跟 exe 同層（見檔頭）—— 少一顆的症狀是「文字模式好、開窗掛」
for dll in cimgui.dll glfw3.dll; do
  if [ -f "publish/$dll" ]; then copy_retry "publish/$dll" "$root/$dll"; else
    echo "⚠ publish/$dll 不存在 —— 開窗可能會失敗（Silk.NET 找不到原生層）"; fi
done

mb=$(( $(stat -c %s "$root/senate.exe" 2>/dev/null || stat -f %z "$root/senate.exe") / 1048576 ))
echo
echo "✓ 產物：$root/senate.exe（${mb} MB）＋ cimgui.dll / glfw3.dll"

# ── 出廠驗收①：跑真的 exe（不是看 build 綠燈）──────
echo '── 出廠驗收① doctor ───────────────────────────'
set +e
"$root/senate.exe" doctor
code=$?
set -e

# ── 出廠驗收②：**對 exe 跑自我對拍**（Tim 2026-08-30 拍板）────────
# 🩸 為什麼加這一格：agent 改完 code 的驗證迴圈是 `dotnet run`（Debug DLL），
#   而人跑的是這顆 exe（Release / self-contained / single-file）——**兩個不同的二進位檔**。
#   「Debug 全綠」與「你手上這顆 exe 全綠」是兩本帳，而它們在畫面上長得一模一樣。
#   ⇒ 把 selftest 綁在 build 上：驗收長在必經路上，不必靠誰記得跑。
echo '── 出廠驗收② selftest（對 exe，不是對 Debug DLL）──'
set +e
"$root/senate.exe" selftest
selftest=$?
set -e

# ── 出廠驗收③：**真的開一次窗**（原生 DLL 的坑就是死在這一格）──
echo '── 出廠驗收③ 開窗（截圖後自動關）──────────────'
set +e
"$root/senate.exe" ui --screenshot "$root/build/build_check.png" > "$root/build/build_check.log" 2>&1
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
