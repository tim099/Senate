#!/usr/bin/env sh
# 全域安裝（Git Bash）—— 讓 `senate` 在任何 CMD / PowerShell / Git Bash 直接可用，像 python 一樣。
#
# 區塊職責：把 senate.exe 所在目錄（＝repo 根）寫進**使用者 PATH**；--uninstall 移除。
# 物理意義：Windows 的「全域指令」＝PATH 找得到。exe 本來就在 repo 根（build.sh 的產物、
#           原生 DLL 同層），所以不搬檔案、不做 shim —— **加一條 PATH 就是全部**。
#           搬去別的目錄反而製造第二份會過期的 exe（repo 裡 build 出新版、PATH 上還是舊的）。
# 數值影響：只動 HKCU 的使用者 PATH（本人、不碰系統 PATH、不需系統管理員）。冪等：
#           已在 PATH 裡就說「已裝過」不重複加。⚠ **已開著的終端機不會變** ——
#           PATH 是 process 啟動時複製的，改完要開新視窗（本腳本結尾會再說一次）。
#
# ⚠ 寫入走 .NET 的 [Environment]::SetEnvironmentVariable(...,'User')，不用 setx ——
#   setx 有 1024 字元截斷（超過的部分**靜默丟掉**，症狀是別的工具突然找不到了）；
#   .NET 這條路無長度坑，而且會廣播 WM_SETTINGCHANGE（新開的視窗立刻讀到新值）。
set -e
root="$(cd "$(dirname "$0")" && pwd)"

# Git Bash 的 /d/Unity/Senate → 寫進 Windows PATH 要用 D:\Unity\Senate
winroot="$(cygpath -w "$root" 2>/dev/null || echo "$root")"

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

contains() {
  # 逐段比對（大小寫不敏感、去尾斜線）—— 子字串比對會把 D:\Unity\Senate2 誤判成已安裝
  echo "$current" | tr ';' '\n' | sed 's/[\\/]*$//' \
    | grep -qix "$(echo "$winroot" | sed 's/[\\/]*$//; s/\\/\\\\/g')"
}

if [ "$1" = "--uninstall" ]; then
  if ! contains; then echo "・使用者 PATH 裡本來就沒有 $winroot —— 沒有東西要移除。"; exit 0; fi
  newpath="$(echo "$current" | tr ';' '\n' | sed 's/[\\/]*$//' \
    | grep -vix "$(echo "$winroot" | sed 's/[\\/]*$//; s/\\/\\\\/g')" | paste -sd ';' -)"
  ps "[Environment]::SetEnvironmentVariable('Path','$newpath','User')"
  back="$(ps "[Environment]::GetEnvironmentVariable('Path','User')" | tr -d '\r')"
  case "$back" in
    *"$winroot"*) echo "✗ 移除後回讀仍看得到 $winroot —— 沒有成功"; exit 1 ;;
    *) echo "✓ 已從使用者 PATH 移除 $winroot（已開的終端機要重開才會變）" ;;
  esac
  exit 0
fi

echo '── Senate 全域安裝 ─────────────────────────────'

# exe 要先存在 —— 裝一條指向空氣的 PATH，症狀是「指令找不到」而人會去怪 PATH 沒生效
if [ ! -f "$root/senate.exe" ]; then
  echo "✗ $winroot 底下沒有 senate.exe —— 先跑 ./build.sh 產出它，再跑本腳本" >&2
  exit 1
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
