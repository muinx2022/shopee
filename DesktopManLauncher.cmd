@echo off
setlocal
set "ROOT=%~dp0"
set "APP=%ROOT%desktop-man\bin\Release\net8.0-windows\publish\DesktopMan.exe"

if not exist "%APP%" (
  echo DesktopMan release build not found.
  echo Run: dotnet publish "%ROOT%desktop-man\DesktopMan.csproj" -c Release
  pause
  exit /b 1
)

start "" "%APP%"
