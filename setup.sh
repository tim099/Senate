#!/usr/bin/env sh
# 一鍵配置（Git Bash / Linux / macOS）—— 與 setup.ps1 等價。
#
# 區塊職責：檢查前置、build、產生本機設定、印讀數。
# 物理意義：同 setup.ps1 —— 只做編排，所有判斷都在 `senate doctor`（C#）裡。
#           兩支腳本各自實作檢查邏輯的話，就是兩份會漂的真相源。
set -e
root="$(cd "$(dirname "$0")" && pwd)"

echo '── Senate 一鍵配置 ─────────────────────────────'

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

echo '── build ───────────────────────────────────────'
dotnet build "$root/Senate.slnx" -c Release --nologo -v minimal

echo '── init & doctor ───────────────────────────────'
set +e
dotnet run --project "$root/src/Senate.Cli" -c Release --no-build -- init
code=$?
set -e

echo
if [ "$code" -eq 0 ]; then
  echo '✓ 配置完成，doctor 全部通過。'
else
  echo "⚠ 配置完成，但 doctor 有項目不通過（exit $code）—— 上面的表格就是哪一格。"
fi
exit $code
