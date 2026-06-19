@echo off
setlocal

set "ROOT=%~dp0"
set "BINDIR=%ROOT%update-product\bin\Release\net8.0-windows\publish"
set "EXE=%BINDIR%\UpdateProduct.exe"
set "PROJ=%ROOT%update-product\UpdateProduct.csproj"
set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

if exist "%EXE%" (
  cd /d "%BINDIR%"
  start "" "%EXE%"
  exit /b 0
)

if exist "%DOTNET%" (
  echo Building UpdateProduct...
  "%DOTNET%" publish "%PROJ%" -c Release
  if exist "%EXE%" (
    cd /d "%BINDIR%"
    start "" "%EXE%"
    exit /b 0
  )
)

echo [LOI] UpdateProduct.exe not found. Build lai:
echo   dotnet publish "%PROJ%" -c Release
pause
