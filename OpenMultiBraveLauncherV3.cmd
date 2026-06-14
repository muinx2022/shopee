@echo off
setlocal

set "ROOT=%~dp0"
set "BINDIR=%ROOT%open-multi-brave-v3\bin\Release\net8.0-windows"
set "EXE=%BINDIR%\OpenMultiBraveLauncherV3.exe"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

if exist "%DOTNET%" (
  cd /d "%ROOT%"
  start "" "%DOTNET%" run --project "%ROOT%open-multi-brave-v3\OpenMultiBraveLauncherV3.csproj" -c Release
  exit /b 0
)

if exist "%EXE%" (
  cd /d "%BINDIR%"
  start "" "%EXE%"
  exit /b 0
)

echo OpenMultiBraveLauncherV3.exe not found. Build with:
echo   dotnet build "%ROOT%open-multi-brave-v3\OpenMultiBraveLauncherV3.csproj" -c Release
pause
