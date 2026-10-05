@echo off
rem ============================================================
rem  Patch DeepSeek Harness so its sidebar starts COLLAPSED.
rem
rem  This .bat is deliberately ASCII-only. cmd reads a .bat with the
rem  CONSOLE CODE PAGE (936 on Chinese Windows), not UTF-8, so Chinese
rem  comments in a UTF-8 .bat shift byte boundaries and split command
rem  lines (symptom: "'/nologo' is not recognized as an internal or
rem  external command"). The Chinese messages therefore live in
rem  patch-sidebar.ps1, which is saved as UTF-8 WITH BOM.
rem ============================================================
chcp 65001 >nul
setlocal
set "HERE=%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%HERE%patch-sidebar.ps1"
echo.
pause
