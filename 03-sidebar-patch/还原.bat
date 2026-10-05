@echo off
rem ============================================================
rem  Restore the original app.asar (undo the sidebar patch).
rem  ASCII-only on purpose - see the note in the patch .bat file.
rem ============================================================
chcp 65001 >nul
setlocal
set "HERE=%~dp0"
set "NODE=D:\dsh-home\dsh-runtimes\dsh-primary-runtime\dependencies\node\bin\node.exe"
set "ASAR=D:\DeepSeekHarness-0.2.0-rc.2\resources\app.asar"
set "PATCH=%HERE%patch-asar-sidebar2.mjs"

echo.
echo  Restoring original app.asar ...
echo.

tasklist /FI "IMAGENAME eq DeepSeek Harness.exe" 2>nul | find /I "DeepSeek Harness.exe" >nul
if not errorlevel 1 (
  echo  [STOP] DSH is still running. Please quit DSH completely first,
  echo         otherwise the file is locked and cannot be replaced.
  echo.
  pause
  exit /b 1
)

if not exist "%NODE%" (
  echo  [ERROR] node.exe not found: %NODE%
  pause
  exit /b 1
)
if not exist "%ASAR%.bak-before-sidebar-fold" (
  echo  [ERROR] No backup found:
  echo          %ASAR%.bak-before-sidebar-fold
  pause
  exit /b 1
)

"%NODE%" "%PATCH%" --revert "%ASAR%"
echo.
echo  Done. You can start DSH again.
echo.
pause
