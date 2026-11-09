#!/usr/bin/env sh
# Senate 安裝（Git Bash）—— clone 完只要跑這一支。
#
# 區塊職責：一台機器上「裝 Senate」的**唯一入口**：前置檢查 → build → 產生本機設定 → 掛使用者 PATH。
#           反向操作在 `uninstall.sh`（`--uninstall` 照舊認得，轉過去）。
#           掛 PATH 之前先叫 `uninstall.sh --keep <自己>`：別份 clone 的註冊一起清掉 ——
#           一台機器上 `senate` 只解析到一份，而且就是剛裝的這一份（驗收照這個驗）。
# 物理意義：Windows 的「全域指令」＝PATH 找得到。exe 住在 `publish/`（build 的產物、
#           原生 DLL 同層），所以不搬檔案、不做 shim —— **加一條 PATH 就是全部**。
#           搬／複製到別的目錄就是第二份會過期的 exe（repo 裡 build 出新版、PATH 上還是舊的），
#           而那正是 2026-09-01 之前的做法。根層的 `senate.lnk` 只服務滑鼠，不參與 PATH。
# 數值影響：只動 HKCU 的使用者 PATH（本人、不碰系統 PATH、不需系統管理員）。冪等：
#           已在 PATH 裡就說「已裝過」不重複加。⚠ **已開著的終端機不會變** ——
#           PATH 是 process 啟動時複製的，改完要開新視窗（本腳本結尾會再說一次）。
#
# ⚠ 寫入走 .NET 的 [Environment]::SetEnvironmentVariable(...,'User')，不用 setx ——
#   setx 有 1024 字元截斷（超過的部分**靜默丟掉**，症狀是別的工具突然找不到了）；
#   .NET 這條路無長度坑，而且會廣播 WM_SETTINGCHANGE（新開的視窗立刻讀到新值）。
#
# ⚠ 本檔是**唯一**的安裝實作（`install.ps1` 已於 2026-09-22 退場，Tim：沒再用）。
#   🩸 它退場前這裡寫的是「本檔與 install.ps1 是同一套規格的兩個宿主實作，移除清單改一邊要同時改另一邊」——
#   兩份清單漂掉的症狀是「用 .sh 裝、用 .ps1 移除，結果少刪兩樣」，而那不會有人喊。
#   判斷邏輯一律不進腳本（那會長成兩份會漂的真相源）：環境判定在 `senate doctor`（C#）。
set -e
root="$(cd "$(dirname "$0")" && pwd)"

# PATH 掛的是 **publish/**（執行檔住那裡），不是 repo 根。
# 🩸 2026-09-01 之前掛的是 repo 根（那時根層有一顆複製過去的 senate.exe）。
#   AssemblyName 改成 senate、複本取消之後，根層已經沒有可執行檔 ⇒ 舊條目會變成
#   「PATH 裡有一條指向沒有 exe 的目錄」，症狀是 `senate` 找不到，而人會去怪 PATH 沒生效。
#   ⇒ 安裝時**順手把舊條目移掉**（uninstall.sh 認得它），移除時兩條都清。
# Git Bash 的 /d/Unity/Senate → 寫進 Windows PATH 要用 D:\Unity\Senate
winroot="$(cygpath -w "$root/publish" 2>/dev/null || echo "$root/publish")"
winlegacy="$(cygpath -w "$root" 2>/dev/null || echo "$root")"

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*) ;;
  *)
    echo "本腳本只處理 Windows 的使用者 PATH。"
    echo "Linux / macOS：ln -s \"$root/senate\" ~/.local/bin/senate（或把 $root 加進 shell profile 的 PATH）"
    exit 1
    ;;
esac

ps() { powershell.exe -NoProfile -NonInteractive -Command "$1"; }

# 讀使用者 PATH（原樣，不展開 %VAR% —— 我們只做包含判斷與前後拼接，不重寫別人的段）
current="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"

# 逐段比對（大小寫不敏感、去尾斜線）—— 子字串比對會把 D:\Unity\Senate2 誤判成已安裝
has_entry() {  # $1 = 要找的 Windows 路徑
  echo "$current" | tr ';' '\n' | sed 's/[\\/]*$//' \
    | grep -qix "$(echo "$1" | sed 's/[\\/]*$//; s/\\/\\\\/g')"
}
contains() { has_entry "$winroot"; }

# ── 移除 ────────────────────────────────────────────────────────
# 實作搬到 uninstall.sh（移除清單只有那一份）；`--uninstall [--purge]` 照舊認得 ⇒ 連 build 產物一起清。
#   三層判準不變：PATH 一定移除、build 產物一定移除（可重建）、SenateData/ 只有 --purge 才動。
if [ "$1" = "--uninstall" ]; then
  shift
  exec "$root/uninstall.sh" --artifacts "$@"
fi

# ── 安裝 ────────────────────────────────────────────────────────
echo '── Senate 安裝 ─────────────────────────────────'

# 前置：兩個外部相依。缺了就停在這裡 ——
# 讓它一路跑到 build 才炸的話，錯誤訊息會指向編譯，而真正的問題是環境。
command -v dotnet >/dev/null 2>&1 || {
  echo '✗ 找不到 dotnet —— 請先安裝 .NET 10 SDK：https://dotnet.microsoft.com/download' >&2
  exit 1
}
echo "  dotnet : $(dotnet --version)"
command -v git >/dev/null 2>&1 || {
  echo '✗ 找不到 git —— 請先安裝 git（需 2.25 以上）' >&2
  exit 1
}
echo "  git    : $(git --version | sed 's/^git version //')"

# 自帶的資料樹（資料根 `auto` 指這裡）：submodule `Valhalla/` ＝ Persona9999/Fate（只有 main 一條分支；
#   裡面只掛 persona 的信件庫）。`git clone --recurse-submodules` 就一起拉了；沒帶 ⇒ 這裡補。
# 拉完切到 main（submodule update 留下的是 detached HEAD ⇒ 執行期的提交會落在游離節點、分支不前進）。
# ⚠ 已經拉下來就不動 —— 裡面是資料，不是產物（只補 detached ⇒ main 這一步）。
echo '── 內建資料樹（Valhalla/）──────────────────────'
if [ -e "$root/Valhalla/.git" ] || git -C "$root" submodule update --init --recursive Valhalla; then
  if [ "$(git -C "$root/Valhalla" rev-parse --abbrev-ref HEAD)" = HEAD ]; then git -C "$root/Valhalla" switch -q main; fi
  echo "✓ Valhalla/ 在了（$(git -C "$root/Valhalla" rev-parse --abbrev-ref HEAD) @ $(git -C "$root/Valhalla" rev-parse --short HEAD)）"
else
  echo "⚠ Valhalla/ 沒拉成 —— 資料根 auto 會指著一個空目錄（init 會再說一次）；網路好了重跑本腳本" >&2
fi

# build 一律走 build.sh —— ⛔ **不要在這裡另寫一條 dotnet build**。
# 🩸 為什麼寫成硬規則：舊版 setup.sh 跑的是 `dotnet build Senate.slnx -c Release`，
#   產出 src/Senate.Cli/bin/Release 的 framework-dependent DLL；而 build.sh 產出的是
#   publish 的 self-contained single-file exe。**兩顆跑起來長得一模一樣**，
#   於是「我測過了」測的是哪一顆沒有人答得出來。
#   實測讀數（2026-09-01，這台）：五顆可執行產物、三種年份，setup 那顆落後整整一天。
#   ⇒ build 只留一個入口，第二條路不是備援是分岔。
if [ "$1" = "--skip-build" ]; then
  echo '── build（--skip-build：沿用現有產物，不重新 build）'
  [ -f "$root/publish/senate.exe" ] || {
    echo "✗ 沒有 publish/senate.exe 可沿用 —— 拿掉 --skip-build 再跑一次" >&2; exit 1; }
else
  echo '── build（走 build.sh，含出廠驗收）─────────────'
  "$root/build.sh" || {
    echo "✗ build 或出廠驗收沒過 ⇒ **PATH 不掛**。" >&2
    echo "  裝一條指向壞產物的 PATH，之後每一個錯都會被怪到 PATH 上，而那是錯的方向。" >&2
    echo "  ⚠ 若失敗的是驗收③開窗那格（遠端桌面／無 GPU 的機器會），" >&2
    echo "    可先自己跑 ./build.sh 看讀數，再用 ./install.sh --skip-build 掛 PATH。" >&2
    exit 1
  }
fi

# 本機設定 —— init 只在檔案不存在時建立，絕不覆寫（那是 init 自己的保證，不是這裡的）
echo '── 本機設定（init）─────────────────────────────'
"$root/publish/senate.exe" init

# 使用者 PATH
echo '── 掛使用者 PATH ───────────────────────────────'
# 先清掉自己以外的 Senate 註冊：2026-09-01 以前掛的 repo 根（根層已經沒有 exe）、別份 clone 的 publish\。
# 留著的話，排在前面的那條贏 ⇒ `senate` 跑的是另一份、資料根是另一棵樹（2026-10-22 撞過，見 uninstall.sh）。
"$root/uninstall.sh" --keep "$winroot" || {
  echo "✗ 清不掉別份的註冊 ⇒ 停手，PATH 不掛（不做半套）" >&2; exit 1; }
current="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"

if contains; then
  echo "・$winroot 已經在使用者 PATH 裡 —— 不重複加。"
else
  ps "[Environment]::SetEnvironmentVariable('Path', [Environment]::GetEnvironmentVariable('Path','User') + ';$winroot', 'User')"
  # 回讀驗證 —— 寫入端會替自己說謊
  current="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"
  if contains; then
    echo "✓ 已把 $winroot 加進使用者 PATH（回讀確認）"
  else
    echo "✗ 寫入後回讀不到 —— 沒有成功（PATH 沒有被動過的跡象）" >&2
    exit 1
  fi
fi

# 出廠驗收：模擬一個「新視窗」的 PATH（系統＋使用者重組），從別的目錄跑 senate
# ⚠ 不只問「找不找得到」，還要問「找到的是不是這一份」—— 前面有別份的 publish\ 時，
#   找得到、跑得動，而跑的是另一份（2026-10-22 這格就是這樣綠著放過去的）。
echo '── 驗收：用新 PATH 從別的目錄跑一次 ────────────'
ok="$(ps "\$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); Set-Location \$env:TEMP; \$c = Get-Command senate -ErrorAction SilentlyContinue | Select-Object -First 1; if (\$c) { 'SENATE_AT=' + (Split-Path -Parent \$c.Source); & \$c.Source --help | Select-Object -First 1; 'SENATE_OK' } else { 'SENATE_MISSING' }" | tr -d '\r')"
at="$(printf '%s\n' "$ok" | sed -n 's/^SENATE_AT=//p')"
case "$ok" in
  *SENATE_OK*)
    if ! (current="$at"; has_entry "$winroot"); then
      echo "✗ 新 PATH 下 \`senate\` 解析到的是 $at —— 不是這一份（$winroot）" >&2
      echo "  前面還有別的 senate；詳查：powershell -c \"Get-Command senate -All | Select-Object Source\"" >&2
      exit 1
    fi
    echo "✓ 新 PATH 下 \`senate\` 解析到這一份（$at）、跑得動。"
    echo
    echo "安裝完成 —— **開一個新的 CMD / PowerShell / Git Bash** 就能直接用："
    echo "  senate cmd status"
    echo "  senate cmd run Task --persona <me> --arg op=show --arg index=8"
    echo "（已開著的終端機不會自動生效；解除註冊：./uninstall.sh）"
    ;;
  *)
    echo "✗ 新 PATH 下解析不到 senate —— PATH 寫進去了但驗收沒過，詳查：" >&2
    echo "  powershell -c \"[Environment]::GetEnvironmentVariable('Path','User')\"" >&2
    exit 1
    ;;
esac
