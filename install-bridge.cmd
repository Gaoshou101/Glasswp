@echo off
setlocal
rem PowerShell 7 launched through cmd can leak incompatible module paths into 5.1.
set "PSModulePath=%SystemRoot%\System32\WindowsPowerShell\v1.0\Modules"
set "GLASSWP_INSTALLER=%~dp0scripts\install-bridge.ps1"
if not exist "%GLASSWP_INSTALLER%" (
  echo Installer files are missing. Extract the complete package before installing.
  exit /b 1
)
if exist "%~dp0payload\build-info.json" (
  "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%GLASSWP_INSTALLER%" -Source "%~dp0payload" %*
) else (
  "%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%GLASSWP_INSTALLER%" %*
)
set "GLASSWP_RESULT=%ERRORLEVEL%"
if not "%GLASSWP_RESULT%"=="0" echo Installation failed. Read the error above before retrying.
if "%~1"=="" pause
exit /b %GLASSWP_RESULT%
