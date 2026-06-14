@echo off
setlocal

set "ROOT=%~dp0"
set "BINDIR=%ROOT%shopee-stat\bin\Release\net8.0-windows"
set "EXE=%BINDIR%\ShopeeStatApp.exe"
set "PROJ=%ROOT%shopee-stat\shopee-stat.csproj"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

if exist "%EXE%" (
  cd /d "%BINDIR%"
  start "" "%EXE%"
  exit /b 0
)

if exist "%DOTNET%" (
  echo Building ShopeeStatApp...
  "%DOTNET%" build "%PROJ%" -c Release
  if exist "%EXE%" (
    cd /d "%BINDIR%"
    start "" "%EXE%"
    exit /b 0
  )
)

echo [LOI] ShopeeStatApp.exe not found. Build lai:
echo   dotnet build "%PROJ%" -c Release
pause
