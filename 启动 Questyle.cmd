@echo off
set "QUESTYLE_EXE=%~dp0dist\Questyle-Windows-x64\Questyle.exe"
if not exist "%QUESTYLE_EXE%" (
  echo Please run build.ps1 first.
  pause
  exit /b 1
)
start "" "%QUESTYLE_EXE%"
