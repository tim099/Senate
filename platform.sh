# 平台判定（由 build.sh / check.sh / install.sh 用 `. "$root/platform.sh"` 載入，不單獨執行）。
#
# 區塊職責：一個地方決定三件事 ——
#   rid          dotnet publish 的 Runtime Identifier
#   x            執行檔副檔名（Windows 是 .exe，Linux 是空字串）
#   native_libs  Silk.NET 開窗要跟執行檔同層的原生層（少一顆的症狀是「文字模式好、開窗掛」）
# ⚠ 判定只寫在這裡：三支腳本各寫一份的話，加一個平台時遲早漏改一支。
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    rid=win-x64; x=.exe; native_libs="cimgui.dll glfw3.dll" ;;
  Linux)
    case "$(uname -m)" in
      x86_64)        rid=linux-x64 ;;
      aarch64|arm64) rid=linux-arm64 ;;
      *) echo "✗ 不支援的 CPU：$(uname -m)（Linux 只支援 x86_64 / aarch64）" >&2; exit 1 ;;
    esac
    x=""; native_libs="libcimgui.so libglfw.so.3" ;;
  *)
    echo "✗ 不支援的系統：$(uname -s)（目前只支援 Windows（Git Bash）與 Linux）" >&2; exit 1 ;;
esac
