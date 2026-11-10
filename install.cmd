@echo off
chcp 65001 >nul
setlocal
rem ── Senate 安裝入口（檔案總管雙擊／CMD／PowerShell 用）──────────────────
rem 區塊職責：找到 Git Bash，把參數原樣交給同一個資料夾的 install.sh。只做這一件事。
rem 物理意義：install.sh 要在 Git Bash 裡跑，而剛 clone 完的人多半站在 CMD、PowerShell 或檔案總管裡。
rem ⛔ 這裡不放任何安裝邏輯（前置檢查、build、PATH 都不在這裡）—— 安裝的實作只有 install.sh 一份。
rem    install.ps1 就是因為長成第二份實作、兩邊清單漂掉，才在 2026-09-22 退場的。
rem ⚠ 上面那行 chcp 65001 要留在最前面：本檔是 UTF-8，cmd 逐行讀檔，換了字碼頁之後的中文才讀得對。
rem ⚠ 刻意不用標籤（goto／call :label）也不用括號區塊：UTF-8 的批次檔裡這兩樣最容易出怪事，
rem    而這支檔案壞掉的症狀是「雙擊之後視窗一閃就沒了」。

set "BASH_EXE="
for %%P in ("%ProgramFiles%\Git\bin\bash.exe" "%ProgramFiles(x86)%\Git\bin\bash.exe" "%LocalAppData%\Programs\Git\bin\bash.exe") do if not defined BASH_EXE if exist "%%~P" set "BASH_EXE=%%~P"
rem 裝在別的地方：git.exe 在 Git 資料夾的 cmd 底下，bash.exe 在同一個資料夾的 bin 底下
if not defined BASH_EXE for /f "delims=" %%G in ('where git 2^>nul') do if not defined BASH_EXE if exist "%%~dpG..\bin\bash.exe" set "BASH_EXE=%%~dpG..\bin\bash.exe"

set "RC=1"
if not defined BASH_EXE echo ✗ 找不到 Git Bash —— 請先安裝 Git（2.25 以上）：https://git-scm.com/download/win
if not defined BASH_EXE echo   或：winget install --id Git.Git -e
if not defined BASH_EXE echo   裝完開一個新的視窗，再跑一次 install.cmd。
if defined BASH_EXE "%BASH_EXE%" "%~dp0install.sh" %*
if defined BASH_EXE set "RC=%ERRORLEVEL%"

rem 雙擊（或從 PowerShell）進來的視窗跑完就關，錯誤訊息會跟著不見 ⇒ 停一下。
rem 判準：cmd 是被 /c 叫起來跑這支的（雙擊、PowerShell 都是）；在 CMD 裡直接打 install.cmd 不會停。
rem 標準輸入不是鍵盤時（agent、導向輸入）pause 會立刻返回，不會卡住。
echo %CMDCMDLINE% | find /i " /c " >nul && pause
exit /b %RC%
