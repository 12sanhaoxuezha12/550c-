@echo off
setlocal

rem ============================================================
rem  Build SysPanel.exe with the system C# compiler.
rem  No SDK, no NuGet, no third-party references.
rem
rem  NOTE: this file is deliberately ASCII-only.
rem  cmd reads a .bat using the CONSOLE CODE PAGE (936 on Chinese
rem  Windows), not UTF-8. Chinese comments in a UTF-8 .bat shift byte
rem  boundaries and split command lines - the symptom is:
rem      '/nologo' is not recognized as an internal or external command
rem      'el.exe'   is not recognized as an internal or external command
rem  A BOM does not help either (cmd tries to run the BOM as a command).
rem  The C# sources DO contain Chinese and are fine, because csc is told
rem  the encoding explicitly via /codepage:65001 below.
rem ============================================================

cd /d "%~dp0"

set "CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found - .NET Framework 4.x is required.
  exit /b 1
)

echo Compiler: %CSC%
echo.

rem /codepage:65001 - sources are UTF-8 (Chinese comments and string literals)
rem /resource      - embed the web page so a single exe can serve it
"%CSC%" /nologo /target:exe /optimize+ /platform:anycpu /codepage:65001 ^
  /out:SysPanel.exe ^
  /resource:web\index.html,index.html ^
  /r:System.Management.dll ^
  src\Native.cs src\Metrics.cs src\PanelServer.cs src\Program.cs

if errorlevel 1 (
  echo.
  echo [FAILED] compilation error - see the messages above.
  exit /b 1
)

echo.
echo [OK] SysPanel.exe built.
echo      self-check : SysPanel.exe --once
echo      run        : SysPanel.exe --open
endlocal
