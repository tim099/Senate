#!/usr/bin/env sh
# Senate 解除註冊（Git Bash）—— 把 `senate` 這個指令從使用者 PATH 拿掉。
#
# 區塊職責：解除註冊的**唯一實作**。預設只動 PATH；`--artifacts` 再清 build 產物、
#           `--purge` 再清 SenateData/（`./install.sh --uninstall` ＝ 本腳本帶 `--artifacts`）。
#           install.sh 掛 PATH 之前也先叫本腳本（帶 `--keep` 自己那條）—— 清掉別份 Senate 的註冊。
# 物理意義：Windows 的全域指令＝PATH 找得到 ⇒ 解除註冊＝PATH 上**每一條**指向 Senate 的條目都拿掉，
#           不只這一份 clone 的。一台機器有兩份 clone 時兩條 publish\ 都在 PATH 上，
#           `senate` 解析到的是排前面那條 —— 跑的是哪一份、資料根是哪一棵樹，全看順序。
#   🩸 2026-10-22：D:\Senate 照 install.sh 裝好、驗收也過了，可是 PATH 前面還留著 D:\Unity\Senate\publish
#      ⇒ 打 `senate` 跑的是另一份，喚醒寫進的是另一棵資料樹。驗收只問「找不找得到」，沒問「找到的是誰」。
# 數值影響：只動 HKCU 的使用者 PATH，其他條目一個字都不改。系統 PATH 上也有 Senate（要管理員）⇒ 只列出來、
#           不動，並非零退出 —— 它排在使用者 PATH 前面，留著就永遠先解析到它。
#
# 用法：./uninstall.sh [--dry-run] [--artifacts] [--purge] [--keep <Windows 路徑>]
#   --dry-run    只印計畫，什麼都不動
#   --artifacts  連 build 產物一起清（可重建）
#   --purge      再連 SenateData/ 一起清（人設定過的東西；含 --artifacts）
#   --keep <p>   這一條留著（install.sh 用：清別份、留自己）
#
# 什麼算「Senate 的條目」（逐段判，大小寫不敏感、去尾斜線）：
#   · 這一份的 publish\ 與 repo 根（2026-09-01 以前掛的是根）—— 目錄還在不在都算
#   · 目錄裡有 senate.exe                   —— 別份 clone 的 publish\
#   · 目錄裡有 Senate.slnx                  —— 別份 clone 以前掛的 repo 根
#   · 目錄叫 publish、上一層有 Senate.slnx  —— 產物被清掉了、PATH 還留著
#   目錄已經不存在的別份條目判不了（不知道它以前是什麼）⇒ 不碰。
#
# ⚠ 測試接縫：給 SENATE_PATH_FILE（使用者 PATH）／SENATE_MACHINE_PATH_FILE（系統 PATH）⇒
#   讀寫那兩個檔、不碰 registry。只給驗這支腳本用。
set -e
root="$(cd "$(dirname "$0")" && pwd)"
winroot="$(cygpath -w "$root/publish" 2>/dev/null || echo "$root/publish")"
winlegacy="$(cygpath -w "$root" 2>/dev/null || echo "$root")"

dry=0; artifacts=0; purge=0; keep=""
while [ $# -gt 0 ]; do
  case "$1" in
    --dry-run)   dry=1 ;;
    --artifacts) artifacts=1 ;;
    --purge)     artifacts=1; purge=1 ;;
    --keep)      [ -n "$2" ] || { echo "✗ --keep 要帶一個路徑" >&2; exit 2; }; keep="$2"; shift ;;
    *) echo "✗ 不認得的參數：$1（認得：--dry-run --artifacts --purge --keep <路徑>）" >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$SENATE_PATH_FILE" ]; then
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) ;;
    *) echo "本腳本只處理 Windows 的使用者 PATH。Linux / macOS：拿掉 shell profile 裡那一行、或刪 ~/.local/bin/senate"; exit 1 ;;
  esac
fi

ps() { powershell.exe -NoProfile -NonInteractive -Command "$1"; }

path_get() {  # $1 = User | Machine
  if [ -n "$SENATE_PATH_FILE" ]; then
    if [ "$1" = User ]; then cat "$SENATE_PATH_FILE"; else cat "${SENATE_MACHINE_PATH_FILE:-/dev/null}"; fi
    return
  fi
  ps "[Environment]::GetEnvironmentVariable('Path','$1')" | tr -d '\r'
}
path_set_user() {  # $1 = 新的使用者 PATH（PowerShell 單引號字串裡的 ' 要寫成 ''）
  if [ -n "$SENATE_PATH_FILE" ]; then printf '%s' "$1" > "$SENATE_PATH_FILE"; return; fi
  ps "[Environment]::SetEnvironmentVariable('Path','$(printf '%s' "$1" | sed "s/'/''/g")','User')"
}

norm() { printf '%s' "$1" | tr '/' '\\' | sed 's/\\*$//' | tr '[:upper:]' '[:lower:]'; }

is_senate_entry() {  # $1 = PATH 的一段（Windows 路徑）
  n="$(norm "$1")"
  [ -n "$n" ] || return 1
  [ -n "$keep" ] && [ "$n" = "$(norm "$keep")" ] && return 1
  { [ "$n" = "$(norm "$winroot")" ] || [ "$n" = "$(norm "$winlegacy")" ]; } && return 0
  case "$1" in *%*) return 1 ;; esac   # 沒展開的 %VAR% ⇒ 判不了，不碰
  u="$(cygpath -u "$1" 2>/dev/null)" || return 1
  [ -f "$u/senate.exe" ] && return 0
  [ -f "$u/Senate.slnx" ] && return 0
  [ "$(basename "$u")" = publish ] && [ -f "$u/../Senate.slnx" ] && return 0
  return 1
}
senate_entries() {  # $1 = PATH ⇒ 每行一條 Senate 的條目（原樣）
  printf '%s\n' "$1" | tr ';' '\n' | while IFS= read -r e; do
    if is_senate_entry "$e"; then printf '%s\n' "$e"; fi
  done
}
drop_senate_entries() {  # $1 = PATH ⇒ 拿掉 Senate 的條目後的 PATH（其他條目原樣、順序不變）
  printf '%s\n' "$1" | tr ';' '\n' | while IFS= read -r e; do
    if ! is_senate_entry "$e"; then printf '%s\n' "$e"; fi
  done | paste -sd ';' -
}

[ "$dry" -eq 1 ] && echo '── Senate 解除註冊（--dry-run：只印計畫）──────' || echo '── Senate 解除註冊 ─────────────────────────────'

# ① 使用者 PATH
current="$(path_get User)"
found="$(senate_entries "$current")"
if [ -z "$found" ]; then
  echo "・使用者 PATH 裡沒有 Senate 的條目 —— 這一格沒有東西要移除。"
elif [ "$dry" -eq 1 ]; then
  printf '%s\n' "$found" | while IFS= read -r e; do echo "・會從使用者 PATH 移除 $e"; done
else
  path_set_user "$(drop_senate_entries "$current")"
  # 回讀驗證 —— 寫入端會替自己說謊
  current="$(path_get User)"
  left="$(senate_entries "$current")"
  if [ -n "$left" ]; then
    echo "✗ 寫入後回讀仍看得到這幾條 —— 沒有成功：" >&2
    printf '    %s\n' "$left" >&2
    exit 1
  fi
  printf '%s\n' "$found" | while IFS= read -r e; do echo "✓ 已從使用者 PATH 移除 $e"; done
fi
[ -n "$keep" ] && echo "・留著 $keep（--keep）"

# 系統 PATH —— 只看不動（要管理員），但留著就永遠先解析到它 ⇒ 非零退出
machine="$(senate_entries "$(path_get Machine)")"
if [ -n "$machine" ]; then
  echo "✗ 系統 PATH 上也有 Senate 的條目（要系統管理員才改得動，本腳本不動）：" >&2
  printf '    %s\n' "$machine" >&2
  echo "  系統 PATH 排在使用者 PATH 前面 ⇒ 它在，\`senate\` 就先解析到它。" >&2
  echo "  拿掉：系統內容 → 進階 → 環境變數 → 系統變數的 Path，或用管理員身分改。" >&2
  exit 1
fi

# ② build 產物（可重建）—— 帶 --artifacts 才動
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
  if [ "$dry" -eq 1 ]; then echo "・會移除$2"; return 0; fi
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
if [ "$artifacts" -eq 1 ]; then
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
fi

# ③ 使用者設定 —— 帶 --purge 才動
if [ "$purge" -eq 1 ]; then
  if [ ! -d "$root/SenateData" ]; then
    echo "・沒有 SenateData/ 可移除"
  elif [ "$dry" -eq 1 ]; then
    echo "・會移除 SenateData/（含本機設定、頁面偏好、runtime 狀態）"
  else
    rm -rf "$root/SenateData"
    echo "✓ 已移除 SenateData/（含本機設定、頁面偏好、runtime 狀態）"
  fi
elif [ "$artifacts" -eq 1 ] && [ -d "$root/SenateData" ]; then
  echo
  echo "・**保留** SenateData/ —— 那是你手動設定過的東西（專案清單、頁面偏好），掉了要重設。"
  echo "  真的要一起刪：./uninstall.sh --purge"
fi

[ -n "$keep" ] && exit 0   # install.sh 叫的 ⇒ 收尾的話由它說

echo
if [ "$failed" -gt 0 ]; then
  echo "⚠ 移除**未完成**：有 $failed 樣東西沒刪掉（上面標 ✗ 的那幾樣）。" >&2
  echo "  PATH 已經拿掉了 ⇒ senate 指令會消失，但那幾樣還佔著磁碟。" >&2
  echo "  關掉 Visual Studio／等防毒掃完，再跑一次（冪等）。" >&2
  exit 1
fi
if [ "$dry" -eq 1 ]; then
  echo "試算完成，什麼都沒動。拿掉 --dry-run 才會真的做。"
  exit 0
fi
echo "解除註冊完成。⚠ 已開著的終端機不會變（PATH 是 process 啟動時複製的），要開新視窗。"
[ "$artifacts" -eq 0 ] && echo "・build 產物留著（重新註冊：./install.sh --skip-build；連產物一起清：./uninstall.sh --artifacts）"
echo "・原始碼與 git 歷史一個字都沒動 —— 要徹底清掉請自己刪 $winlegacy 這個資料夾。"
exit 0
