@echo off
setlocal
pushd "%~dp0"

set "APP_EXE=%~dp0bin\Release\net8.0-windows\publish\UpdateProduct.exe"

if not exist "%APP_EXE%" (
    echo Khong tim thay file release:
    echo "%APP_EXE%"
    echo.
    echo Hay build lai bang lenh:
    echo dotnet publish UpdateProduct.csproj -c Release
    pause
    exit /b 1
)

start "" "%APP_EXE%"
popd
