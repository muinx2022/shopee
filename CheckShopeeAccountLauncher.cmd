@echo off
setlocal

set "ROOT=%~dp0"
set "BINDIR=%ROOT%check-shopee-account\bin\Release\net8.0-windows"
set "EXE=%BINDIR%\CheckShopeeAccount.exe"
set "PROJ=%ROOT%check-shopee-account\check-shopee-account.csproj"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

if exist "%EXE%" (
  cd /d "%BINDIR%"
  start "" "%EXE%"
  exit /b 0
)

if exist "%DOTNET%" (
  echo Building CheckShopeeAccount...
  "%DOTNET%" build "%PROJ%" -c Release
  if exist "%EXE%" (
    cd /d "%BINDIR%"
    start "" "%EXE%"
    exit /b 0
  )
)

echo [LOI] CheckShopeeAccount.exe not found. Build lai:
echo   dotnet build "%PROJ%" -c Release
pause
