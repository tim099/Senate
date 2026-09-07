#!/usr/bin/env sh
# 出廠驗收 —— **跟 build 分離**（Tim 2026-09-07 拍板）。
#
# 區塊職責：對**已經在 publish/ 的那顆 exe** 跑四道驗收關；可以挑要跑哪幾關。
# 物理意義：驗收與 build 分家的理由是效率 —— 項目只會愈來愈多，而
#           「改一行就得付全套驗收」會讓人開始繞過它，而繞過去之後就沒有人在驗了。
#
# ⚠ 而分家有一個代價，2026-08-30 Tim 把 selftest 綁上 build 正是為了避免它：
#   **驗收不在必經路上就會沒有人跑。**
#   ⇒ 所以 `build.sh` 收尾會明講「本次沒有驗收」並印出這一行指令，⛔ 不靜默跳過。
#   兩件事分開了，但「沒驗」這件事**看得見**。
#
# 用法：
#   ./check.sh                      四關全跑
#   ./check.sh --gates doctor,self  只跑指定關（doctor / self / gui / server）
#   ./check.sh --only watch         第②關（selftest）只跑命中的項目 —— 見 senate selftest --list
#   ./check.sh --only real,book --gates self
#
# ⛔ 本腳本**不 build**。它驗的是磁碟上那顆 —— 而「那顆是哪一顆」由第①關的
#   doctor 首列（build stamp ＋ 對照 HEAD）自己講（TASK-0138）。
set -eu
root="$(cd "$(dirname "$0")" && pwd)"
exe="$root/publish/senate.exe"

gates="doctor,self,gui,server"
only=""
while [ $# -gt 0 ]; do
  case "$1" in
    --gates) gates="${2:-}"; shift 2 ;;
    --gates=*) gates="${1#*=}"; shift ;;
    --only) only="${2:-}"; shift 2 ;;
    --only=*) only="${1#*=}"; shift ;;
    -h|--help) sed -n '1,30p' "$0"; exit 0 ;;
    # ⛔ 不認得的參數**擋下來**，不靜默忽略：打錯 `--gate` 少一個 s 而全跑，
    #   讀數會跟「我選對了」一模一樣。
    *) echo "✗ 認不得的參數：$1（可用：--gates / --only / --help）" >&2; exit 2 ;;
  esac
done

[ -x "$exe" ] || { echo "✗ 找不到 $exe —— 先跑 ./build.sh" >&2; exit 2; }

has_gate() { echo ",$gates," | grep -q ",$1,"; }
# 沒被選到的關回「未跑」而**不是 0** —— 收尾判定只看真的跑過的那幾關，
# ⛔ 而「沒跑」絕不折算成「通過」。
code=- ; selftest=- ; gui=- ; server=-

echo "── 驗收對象 ──────────────────────────────────"
"$exe" --version || true
echo "   （這顆是哪一顆、有沒有落後 HEAD ⇒ 第①關 doctor 首兩列）"

if has_gate doctor; then
  echo '── 出廠驗收① doctor ───────────────────────────'
  set +e; "$exe" doctor; code=$?; set -e
fi

# 🩸 為什麼這一關要對 exe 跑而不是對 Debug DLL：agent 改完 code 的驗證迴圈是 `dotnet run`
#   （Debug DLL），而人跑的是這顆 exe（Release / self-contained / single-file）——
#   **兩個不同的二進位檔**，而「Debug 全綠」與「你手上這顆 exe 全綠」在畫面上長得一樣。
if has_gate self; then
  echo '── 出廠驗收② selftest（對 exe，不是對 Debug DLL）──'
  set +e
  if [ -n "$only" ]; then "$exe" selftest --only "$only"; else "$exe" selftest; fi
  selftest=$?
  set -e
fi

# ⚠ build/ 一定要先建出來 —— 截圖與 log 寫在那裡，而**沒有別人會建它**。
#   🩸 2026-09-01：runtime 狀態搬進 SenateData/ 之後 build/ 就沒有任何產生者了。
if has_gate gui; then
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
fi

# 物理意義：selftest 對拍的是 result 檔的 schema，不是「一顆 CLI 送、一顆 Server 接、result 回來」
#   那條路 —— 那條路只有真的起一顆 Server 才有讀數。
# ⚠ 與 build.sh 不同的一格：這裡**不保證**沒有別顆 Server 在跑（build 前會先 stop，
#   而本腳本不 build）。⇒ 已經有人掛著 Server 就**跳過這一關**，
#   ⛔ 不去停別人的（停掉它就是拿別人正在用的東西換我的一個讀數）。
if has_gate server; then
  mkdir -p "$root/build"
  echo '── 出廠驗收④ Server round-trip（起一顆臨時 Server → server-ping → 收掉）──'
  if "$exe" server status > /dev/null 2>&1; then
    echo "— 跳過：已經有一顆 Server 在跑（⛔ 不停別人的）。要驗這一關請先自己收掉它。"
    server=-
  else
    set +e
    "$exe" server start > "$root/build/build_server.log" 2>&1 &
    server_pid=$!
    for _ in 1 2 3 4 5 6; do
      "$exe" server status > /dev/null 2>&1 && break
      sleep 0.5
    done
    "$exe" cmd server-ping --arg echo=check > "$root/build/build_ping.log" 2>&1
    server=$?
    "$exe" server stop > /dev/null 2>&1
    wait "$server_pid" 2>/dev/null
    set -e
    if [ "$server" -eq 0 ] && grep -q "echo = check" "$root/build/build_ping.log"; then
      echo "✓ Server round-trip 通（$(grep -o 'server_pid = [0-9]*' "$root/build/build_ping.log" | head -1)）"
    else
      server=1
      echo "✗ Server round-trip 失敗 —— build/build_ping.log 與 build/build_server.log："
      tail -3 "$root/build/build_ping.log"; tail -3 "$root/build/build_server.log"
    fi
  fi
fi

echo
# ⚠ 射程印在收尾那一行：`全過` 在「四關都跑」與「只跑了一關」上同形，
#   而那個差別正是讀的人要據以放行的東西。
# ⚠ 這一行**不准用反引號**：雙引號裡的反引號是命令替換，`-` 會被當成指令去執行
#   （實測 2026-09-07：`line 121: -: command not found`，而那一格的字就這樣消失了）。
#   🩸 同一隻 summit 2026-08-05 一天被咬四次。⇒ 標記符號一律用單引號或全角。
echo "⇒ 關卡：doctor=$code／selftest=$selftest／gui=$gui／server=$server　（「-」＝**沒跑**，不是通過）"
[ -n "$only" ] && echo "⚠ 第②關帶了 --only $only ⇒ selftest **不是全部項目**"
bad=0
for v in "$code" "$selftest" "$gui" "$server"; do
  [ "$v" = "-" ] && continue
  [ "$v" -eq 0 ] || bad=1
done
[ "$bad" -eq 0 ] && echo "✓ 跑過的關卡全過。" || echo "✗ 有關卡未過。"
exit "$bad"
