#!/usr/bin/env sh
# Senate 安裝／移除（Git Bash）—— clone 完只要跑這一支。
#
# 區塊職責：一台機器上「裝 Senate」的**唯一入口**：前置檢查 → build → 產生本機設定 → 掛使用者 PATH。
#           `--uninstall` 是它的反向操作：把安裝過程動過的東西還原回去。
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
# ⚠ 本檔與 install.ps1 是同一套規格的兩個宿主實作。**移除清單改一邊要同時改另一邊** ——
#   兩份清單漂掉的症狀是「用 .sh 裝、用 .ps1 移除，結果少刪兩樣」，而那不會有人喊。
#   判斷邏輯一律不進腳本（那會長成兩份會漂的真相源）：環境判定在 `senate doctor`（C#）。
set -e
root="$(cd "$(dirname "$0")" && pwd)"

# PATH 掛的是 **publish/**（執行檔住那裡），不是 repo 根。
# 🩸 2026-09-01 之前掛的是 repo 根（那時根層有一顆複製過去的 senate.exe）。
#   AssemblyName 改成 senate、複本取消之後，根層已經沒有可執行檔 ⇒ 舊條目會變成
#   「PATH 裡有一條指向沒有 exe 的目錄」，症狀是 `senate` 找不到，而人會去怪 PATH 沒生效。
#   ⇒ 安裝時**順手把舊條目移掉**（下面的 legacy 遷移），移除時兩條都清。
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
drop_entry() {  # $1 = 要拿掉的 Windows 路徑 ⇒ 印出新的 PATH
  echo "$current" | tr ';' '\n' | sed 's/[\\/]*$//' \
    | grep -vix "$(echo "$1" | sed 's/[\\/]*$//; s/\\/\\\\/g')" | paste -sd ';' -
}
contains() { has_entry "$winroot"; }

# ── 移除 ────────────────────────────────────────────────────────
# 三層，判準就是「這個東西掉了，使用者要不要重做工？」（見 Docs/Architecture/Data_Layout.md）：
#   PATH        → 一定移除（可逆、零資料損失）
#   build 產物  → 一定移除（可重建，跑一次 install 就回來）
#   SenateData/ → **只有 --purge 才動**（人手動編輯過的設定，掉了要重設）
# ⛔ 原始碼與 git 一個字都不動 —— 「移除安裝」不等於「刪掉這份 repo」。
if [ "$1" = "--uninstall" ]; then
  purge=0
  [ "$2" = "--purge" ] && purge=1

  echo '── Senate 移除 ─────────────────────────────────'

  # ① 使用者 PATH —— **兩條都要清**：現在掛的 publish/，以及 2026-09-01 以前掛的 repo 根。
  #    只清一條的話，舊機器上會剩一條指向沒有 exe 的目錄，而那不會報錯，只會讓
  #    「我明明移除了」與「怎麼還有一條」同時成立。
  pathchanged=0
  for target in "$winroot" "$winlegacy"; do
    has_entry "$target" || continue
    current="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"
    newpath="$(drop_entry "$target")"
    ps "[Environment]::SetEnvironmentVariable('Path','$newpath','User')"
    # 回讀驗證 —— 寫入端會替自己說謊
    current="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"
    if has_entry "$target"; then
      echo "✗ 移除後回讀仍看得到 $target —— 沒有成功" >&2; exit 1
    fi
    echo "✓ 已從使用者 PATH 移除 $target"
    pathchanged=1
  done
  [ "$pathchanged" -eq 0 ] && echo "・使用者 PATH 裡沒有 Senate 的條目 —— 這一格沒有東西要移除。"

  # ② build 產物（可重建 ⇒ 預設就移除）
  #    ⚠ 這份清單與 install.ps1 的 $aArtifacts 必須一致。
  #
  #    🩸 刪完一定要**回讀**，而且不准寫成 `rm -rf X; echo ✓`（2026-09-01 我自己犯過）：
  #      第一版用分號 ⇒ echo 無論成敗都印。實測時 src/Senate.Cli/bin 的內容被刪掉了、
  #      **目錄本身沒刪成**（Windows 上 Visual Studio／防毒會短暫抓著 handle），
  #      而畫面印的是「✓ 已移除中間產物」—— 報告比實作大，沿途沒有一格會紅。
  #      ⇒ 修法：刪 → 回讀 → 還在就重試一次 → 仍在就大聲說，並讓整支非零退出。
  removed=0
  failed=0
  # 刪一個東西並確認它真的不見了。回 0 ＝ 真的沒了；回 1 ＝ 還在。
  remove_verify() {  # $1=絕對路徑  $2=人看得懂的標籤
    [ -e "$1" ] || return 0
    rm -rf "$1" 2>/dev/null || true
    if [ -e "$1" ]; then sleep 1; rm -rf "$1" 2>/dev/null || true; fi   # 重試一次：handle 多半是暫時的
    if [ -e "$1" ]; then
      echo "✗ 移不掉：$2 —— 多半是 Visual Studio 或防毒正抓著它，關掉再跑一次" >&2
      failed=$((failed+1))
      return 1
    fi
    echo "✓ 已移除$2"
    return 0
  }
  for p in senate.lnk senate.exe senate.cmd senate cimgui.dll glfw3.dll publish build; do
    [ -e "$root/$p" ] || continue
    remove_verify "$root/$p" "產物：$p" && removed=$((removed+1))
  done
  # 各專案的 bin/obj —— 用 find 不用列舉，日後新增專案不必回來改這裡。
  # ⚠ 走 for 不走 `find | while`：後者的 while 跑在 pipeline 的子 shell 裡，
  #   failed / removed 在裡面加了也回不到這一層 ⇒ 計數永遠 0，而報告會看起來很正常。
  for d in $(find "$root/src" "$root/SCP_Core" -maxdepth 2 -type d \( -name bin -o -name obj \) 2>/dev/null); do
    remove_verify "$d" "中間產物：${d#"$root"/}" && removed=$((removed+1))
  done
  [ "$removed" -eq 0 ] && [ "$failed" -eq 0 ] && echo "・沒有 build 產物需要移除（本來就沒 build 過）"

  # ③ 使用者設定 —— 顯式才動
  if [ "$purge" -eq 1 ]; then
    if [ -d "$root/SenateData" ]; then
      rm -rf "$root/SenateData"
      echo "✓ 已移除 SenateData/（含本機設定、頁面偏好、runtime 狀態）"
    else
      echo "・沒有 SenateData/ 可移除"
    fi
  elif [ -d "$root/SenateData" ]; then
    echo
    echo "・**保留** SenateData/ —— 那是你手動設定過的東西（專案清單、頁面偏好），掉了要重設。"
    echo "  真的要一起刪：./install.sh --uninstall --purge"
  fi

  echo
  if [ "$failed" -gt 0 ]; then
    echo "⚠ 移除**未完成**：有 $failed 樣東西沒刪掉（上面標 ✗ 的那幾樣）。" >&2
    echo "  PATH 已經拿掉了 ⇒ senate 指令會消失，但那幾樣還佔著磁碟。" >&2
    echo "  關掉 Visual Studio／等防毒掃完，再跑一次 ./install.sh --uninstall（冪等）。" >&2
    exit 1
  fi
  echo "移除完成。⚠ 已開著的終端機不會變（PATH 是 process 啟動時複製的），要開新視窗。"
  echo "・原始碼與 git 歷史一個字都沒動 —— 要徹底清掉請自己刪 $winlegacy 這個資料夾。"
  exit 0
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
# 遷移：2026-09-01 以前掛的是 repo 根，而根層現在已經沒有可執行檔 ⇒ 留著只會讓
# `senate` 找不到而人去怪 PATH。先拿掉它，再掛新的（兩步都回讀）。
if has_entry "$winlegacy"; then
  ps "[Environment]::SetEnvironmentVariable('Path','$(drop_entry "$winlegacy")','User')"
  current="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"
  if has_entry "$winlegacy"; then
    echo "✗ 舊的 PATH 條目移不掉（$winlegacy）—— 停手，不做半套遷移" >&2; exit 1
  fi
  echo "✓ 已移除舊的 PATH 條目 $winlegacy（執行檔已搬到 publish/）"
fi

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
echo '── 驗收：用新 PATH 從別的目錄跑一次 ────────────'
ok="$(ps "\$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User'); Set-Location \$env:TEMP; if (Get-Command senate -ErrorAction SilentlyContinue) { senate --help | Select-Object -First 1; 'SENATE_OK' } else { 'SENATE_MISSING' }" | tr -d '\r')"
case "$ok" in
  *SENATE_OK*)
    echo "✓ 新 PATH 下 \`senate\` 解析得到、跑得動。"
    echo
    echo "安裝完成 —— **開一個新的 CMD / PowerShell / Git Bash** 就能直接用："
    echo "  senate cmd status"
    echo "  senate cmd run Task --persona <me> --arg op=show --arg index=8"
    echo "（已開著的終端機不會自動生效；移除：./install.sh --uninstall）"
    ;;
  *)
    echo "✗ 新 PATH 下解析不到 senate —— PATH 寫進去了但驗收沒過，詳查：" >&2
    echo "  powershell -c \"[Environment]::GetEnvironmentVariable('Path','User')\"" >&2
    exit 1
    ;;
esac
