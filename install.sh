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
if [ "${1:-}" = "--uninstall" ]; then
  shift
  exec "$root/uninstall.sh" --artifacts "$@"
fi

# ── 參數 ────────────────────────────────────────────────────────
# ⛔ 認不得的參數擋下來，不靜默忽略（跟 build.sh／check.sh 同一條規矩）：
#   打錯 `--no-pat` 卻照樣把 PATH 換掉，畫面會跟「我本來就要掛」一模一樣。
skip_build=0
no_path=0
usage() {
  echo "用法：./install.sh [--skip-build] [--no-path]"
  echo "  （不帶參數）  前置檢查 → 補子模組 → build → 建本機設定 → 掛使用者 PATH → 驗收"
  echo "  --skip-build  沿用現有的 publish/，不重新 build"
  echo "  --no-path     試裝：**不動 PATH**（也不清別份 Senate 的註冊）。裝完用 publish/senate.exe 的完整路徑叫它"
  echo "  移除：./uninstall.sh（先看會動什麼：./uninstall.sh --dry-run）"
}
while [ $# -gt 0 ]; do
  case "$1" in
    --skip-build) skip_build=1 ;;
    --no-path)    no_path=1 ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "✗ 認不得的參數：$1" >&2; usage >&2; exit 2 ;;
  esac
  shift
done

# ── 安裝 ────────────────────────────────────────────────────────
echo '── Senate 安裝 ─────────────────────────────────'

# 前置：兩個外部相依。缺了就停在這裡 ——
# 讓它一路跑到 build 才炸的話，錯誤訊息會指向編譯，而真正的問題是環境。
# ⚠ 檔頭說「判斷邏輯不進腳本、環境判定在 senate doctor」—— 這一段是那條規矩的例外，而且只有這一段：
#   doctor 住在 exe 裡，而 exe 要靠這兩樣東西才 build 得出來。這裡只擋「連 exe 都生不出來」的那幾格，
#   exe 生出來之後的判定照舊交給 doctor（下面 init 之後會跑一次）。
command -v git >/dev/null 2>&1 || {
  echo '✗ 找不到 git —— 請先安裝 Git（需 2.25 以上）：https://git-scm.com/download/win' >&2
  echo '  或：winget install --id Git.Git -e' >&2
  exit 1
}
git_ver="$(git --version | sed 's/^git version //')"
git_major="$(echo "$git_ver" | cut -d. -f1)"; git_minor="$(echo "$git_ver" | cut -d. -f2)"
case "$git_major$git_minor" in
  *[!0-9]*|'') echo "⚠ 讀不懂 git 的版本（$git_ver）—— 不擋，繼續" >&2 ;;
  *) if [ "$git_major" -lt 2 ] || { [ "$git_major" -eq 2 ] && [ "$git_minor" -lt 25 ]; }; then
       echo "✗ git $git_ver 太舊 —— 需要 2.25 以上：winget upgrade --id Git.Git -e" >&2; exit 1
     fi ;;
esac
echo "  git    : $git_ver"

command -v dotnet >/dev/null 2>&1 || {
  echo '✗ 找不到 dotnet —— 請先安裝 .NET 10 SDK：https://dotnet.microsoft.com/download' >&2
  echo '  或：winget install --id Microsoft.DotNet.SDK.10 -e　（裝完**開新的終端機**再跑一次）' >&2
  exit 1
}
# 🩸 2026-11-10：舊版只問「dotnet 在不在」。只裝了 runtime、或只有舊的 SDK（這台就同時有 6 和 10）時它照樣放行，
#   然後在 build 裡撞一個指向編譯的錯（NETSDK1045…），而真正的問題是「沒有 10 的 SDK」。
#   ⇒ 量的是 **SDK 清單裡最大的主版號**（專案是 net10.0；更新的 SDK 也 build 得出來）。
#   ⚠ 用 --list-sdks 不用 --version：只有 runtime 時 --version 會失敗，而那個失敗訊息不提 SDK。
sdk_major="$(dotnet --list-sdks 2>/dev/null | sed 's/\..*//' | grep -E '^[0-9]+$' | sort -n | tail -1)"
if [ -z "$sdk_major" ]; then
  echo '✗ 有 dotnet，但沒有任何 SDK（只裝了 runtime？）—— 需要 .NET 10 SDK：' >&2
  echo '  winget install --id Microsoft.DotNet.SDK.10 -e　或　https://dotnet.microsoft.com/download' >&2
  exit 1
fi
if [ "$sdk_major" -lt 10 ]; then
  echo "✗ 最新的 .NET SDK 是 $sdk_major.x —— 需要 10 以上（專案是 net10.0）：" >&2
  echo '  winget install --id Microsoft.DotNet.SDK.10 -e　或　https://dotnet.microsoft.com/download' >&2
  exit 1
fi
echo "  dotnet : $(dotnet --version 2>/dev/null || echo "SDK $sdk_major.x")"

# 這份是不是 git clone 下來的。GitHub 的「Download ZIP」沒有 .git，子模組是空資料夾，
#   而且 exe 靠「往上找 .git」定位專案根 ⇒ 就算編得出來也找不到設定檔。早一點說。
[ -e "$root/.git" ] || {
  echo "✗ $root 不是 git clone 下來的（沒有 .git —— 下載的是 ZIP？）" >&2
  echo '  請改用：git clone --recurse-submodules https://github.com/tim099/Senate.git' >&2
  exit 1
}

# 子模組：SCP_Core（程式的一部分，沒有它編不出來）、Glossary（詞典與 persona 自介）、Valhalla（資料樹）。
#   `git clone --recurse-submodules` 就一起拉了；沒帶 ⇒ 這裡缺哪個補哪個。
# 🩸 2026-11-10：舊版只補 Valhalla。少了 SCP_Core 會一路跑到 build 才失敗，錯誤是一整排找不到型別。
# ⚠ 已經拉下來的不動（不 update、不換版本）—— 那是使用者的工作區。
echo '── 子模組 ──────────────────────────────────────'
need_sub() {  # $1 = 名字　$2 = fatal｜warn　$3 = 少了它會怎樣
  if [ -e "$root/$1/.git" ]; then
    echo "✓ $1/ 在了（@ $(git -C "$root/$1" rev-parse --short HEAD 2>/dev/null || echo '?')）"
  elif git -C "$root" submodule update --init --recursive "$1"; then
    echo "✓ $1/ 補拉好了（@ $(git -C "$root/$1" rev-parse --short HEAD 2>/dev/null || echo '?')）"
  elif [ "$2" = fatal ]; then
    echo "✗ $1/ 沒拉成 —— $3。網路好了重跑本腳本" >&2; exit 1
  else
    echo "⚠ $1/ 沒拉成 —— $3。網路好了重跑本腳本" >&2
  fi
}
need_sub SCP_Core fatal '它是程式的一部分，沒有它編不出來'
need_sub Glossary warn  '詞典與 persona 的自我介紹住在裡面，早安會卡在自介那一步'

# 自帶的資料樹（資料根 `auto` 指這裡）：submodule `Valhalla/` ＝ Persona9999/Fate（只有 main 一條分支；
#   裡面只掛 persona 的信件庫）。
# 拉完切到 main（submodule update 留下的是 detached HEAD ⇒ 執行期的提交會落在游離節點、分支不前進）。
# ⚠ 已經拉下來就不動 —— 裡面是資料，不是產物（只補 detached ⇒ main 這一步）。
need_sub Valhalla warn '資料根 auto 會指著一個空目錄（init 會再說一次），酒館與喚醒都用不了'
if [ -e "$root/Valhalla/.git" ]; then
  if [ "$(git -C "$root/Valhalla" rev-parse --abbrev-ref HEAD)" = HEAD ]; then
    if git -C "$root/Valhalla" switch -q main; then
      echo "・Valhalla/ 切到 main（@ $(git -C "$root/Valhalla" rev-parse --short HEAD)）"
    else
      echo "⚠ Valhalla/ 切不到 main —— 停在 detached HEAD，執行期的提交不會落在分支上" >&2
    fi
  fi
fi

# build 一律走 build.sh —— ⛔ **不要在這裡另寫一條 dotnet build**。
# 🩸 為什麼寫成硬規則：舊版 setup.sh 跑的是 `dotnet build Senate.slnx -c Release`，
#   產出 src/Senate.Cli/bin/Release 的 framework-dependent DLL；而 build.sh 產出的是
#   publish 的 self-contained single-file exe。**兩顆跑起來長得一模一樣**，
#   於是「我測過了」測的是哪一顆沒有人答得出來。
#   實測讀數（2026-09-01，這台）：五顆可執行產物、三種年份，setup 那顆落後整整一天。
#   ⇒ build 只留一個入口，第二條路不是備援是分岔。
if [ "$skip_build" -eq 1 ]; then
  echo '── build（--skip-build：沿用現有產物，不重新 build）'
  [ -f "$root/publish/senate.exe" ] || {
    echo "✗ 沒有 publish/senate.exe 可沿用 —— 拿掉 --skip-build 再跑一次" >&2; exit 1; }
else
  echo '── build（走 build.sh；第一次約一到數分鐘）────'
  # ⚠ `--no-window`：build.sh 在終端機裡跑時收尾會自己開一顆後台視窗 —— 安裝時不要：
  #   那一刻本機設定還沒建（init 在下面），而且新來的人不會知道那顆視窗是什麼。裝完的「下一步」會說怎麼開。
  "$root/build.sh" --no-window || {
    echo "✗ build 沒過 ⇒ **PATH 不掛**。" >&2
    echo "  裝一條指向壞產物的 PATH，之後每一個錯都會被怪到 PATH 上，而那是錯的方向。" >&2
    echo "  原因看上面最後幾行與 build/ 底下的 log；修好之後重跑 ./install.sh。" >&2
    exit 1
  }
fi

# 本機設定 —— init 只在檔案不存在時建立，絕不覆寫（那是 init 自己的保證，不是這裡的）
echo '── 本機設定（init）─────────────────────────────'
"$root/publish/senate.exe" init

# 環境檢查：exe 生出來了 ⇒ 判定交給 doctor（C#），這裡只看它的結束碼。
# ⚠ 上面的 init 自己會印一次那張表；這裡通過時只印一行結論，沒過才整張再印一次，並且停手不掛 PATH。
echo '── 環境檢查（senate doctor）────────────────────'
if doctor_out="$("$root/publish/senate.exe" doctor 2>&1)"; then
  echo "✓ $(printf '%s\n' "$doctor_out" | grep '^⇒' | tail -1 | sed 's/^⇒ *//')　（整張表：senate doctor）"
else
  printf '%s\n' "$doctor_out"
  echo "✗ 環境檢查沒過 ⇒ **PATH 不掛**。照上面那張表修好之後重跑 ./install.sh --skip-build" >&2
  exit 1
fi

# 裝完之後要做什麼 —— 兩條出口（掛了 PATH／試裝）共用，只差「怎麼叫 senate」那一格。
# 🩸 2026-11-10：舊版結尾印的是 `senate cmd status` 與 `senate cmd run Task …`，兩支都已經不存在 ——
#   新來的人照著打的第一行就是「沒有名叫 status 的 Cmd」。⇒ 這裡只印**現在跑得動**的東西，
#   而且 check 時要真的各跑一次（見 Docs/Workflows/Setup_And_Build.md「安裝與移除」）。
# ⚠ 路徑印 Windows 寫法（D:\…）：照著貼的人可能在 CMD、PowerShell 或 Git Bash，三邊都認得帶引號的這一種；
#   `$root` 是 Git Bash 的寫法（/d/…），貼到另外兩邊會找不到。
winhere="$(cygpath -w "$root" 2>/dev/null || echo "$root")"
next_steps() {  # $1 = 怎麼叫 senate（`senate` 或完整路徑）
  echo
  echo "接下來（第一次使用）："
  echo "  1. 在這個資料夾開你的 agent（Claude Code／Codex／Antigravity／Gemini 都可以）："
  echo "       cd \"$winhere\""
  echo "  2. 對它說：早安大小姐"
  echo "     它會列出可以叫醒的 persona 讓你選；想要一位自己的，就說「建一位新的」，它會一題一題問你。"
  echo "  3. 醒來之後它會讀自己的簡報、在酒館打招呼，然後就可以開始交代工作了。"
  echo
  echo "想先自己看看："
  echo "  $1 cmd            # 有哪些指令"
  echo "  $1 doctor         # 環境讀數"
  echo "  $1 ui --window    # 後台頁（視窗）"
}

# ── 試裝：不動 PATH ─────────────────────────────────────────────
# 物理意義：想先看看、或在已經裝了另一份 Senate 的機器上測這一份 —— 掛 PATH 會先把別份的註冊清掉
#   （一台機器只留一份），而那會把正在用的那一份換掉。⇒ 這條路**一個位元組都不寫進 PATH**。
if [ "$no_path" -eq 1 ]; then
  echo '── PATH（--no-path：不掛、也不清別份的註冊）────'
  winexe="$(cygpath -w "$root/publish/senate.exe" 2>/dev/null || echo "$root/publish/senate.exe")"
  "$root/publish/senate.exe" --help >/dev/null 2>&1 || {
    echo "✗ publish/senate.exe 跑不起來 —— 試裝沒成功" >&2; exit 1; }
  echo "✓ 試裝完成 —— \`senate\` **沒有**進 PATH。要叫它得用完整路徑："
  echo "    $winexe"
  echo "  ⚠ agent 的 skill 打的是 \`senate …\`：試裝的這一份要給 agent 用，得先正式安裝（./install.sh --skip-build）。"
  next_steps "\"$winexe\""
  exit 0
fi

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
    echo "安裝完成 —— ⚠ **先開一個新的終端機**（CMD／PowerShell／Git Bash 都行）："
    echo "  已經開著的終端機和 agent session 讀不到新的 PATH（PATH 是視窗開起來那一刻複製的）。"
    next_steps senate
    echo
    echo "（解除註冊：./uninstall.sh）"
    ;;
  *)
    echo "✗ 新 PATH 下解析不到 senate —— PATH 寫進去了但驗收沒過，詳查：" >&2
    echo "  powershell -c \"[Environment]::GetEnvironmentVariable('Path','User')\"" >&2
    exit 1
    ;;
esac
