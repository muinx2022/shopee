@echo off
setlocal enableextensions enabledelayedexpansion
title Cap nhat va Build cac app Shopee

rem ====== Thu muc repo = noi dat file .cmd nay ======
set "REPO=%~dp0"
if "%REPO:~-1%"=="\" set "REPO=%REPO:~0,-1%"

echo ==========================================================
echo    CAP NHAT ^& BUILD 4 APP SHOPEE
echo    Repo: %REPO%
echo ==========================================================
echo.
echo Cac app se bi DONG truoc khi cap nhat:
echo    - Open Multi Brave   (OpenMultiBraveLauncherV31)
echo    - Check Shopee Acc   (CheckShopeeAccount)
echo    - Shopee Stat        (ShopeeStatApp)
echo    - Update Product     (UpdateProduct)
echo.

set "ANS="
set /p "ANS=Ban co muon cap nhat khong? (Y/N): "
if /i not "%ANS%"=="Y" (
    echo.
    echo Da huy. Khong cap nhat.
    echo.
    pause
    exit /b 0
)

rem ====== Tim dotnet ======
set "DOTNET=dotnet"
where dotnet >nul 2>nul || set "DOTNET=C:\Program Files\dotnet\dotnet.exe"

rem ====== Tim git ======
set "GIT=git"
where git >nul 2>nul || set "GIT=C:\Program Files\Git\cmd\git.exe"

echo.
echo ----------------------------------------------------------
echo [BUOC 1/3] Dang dong cac app...
echo ----------------------------------------------------------
for %%A in (OpenMultiBraveLauncherV31 CheckShopeeAccount ShopeeStatApp UpdateProduct) do (
    taskkill /F /IM %%A.exe >nul 2>nul && (echo    - Da dong %%A.exe) || (echo    - %%A.exe khong chay)
)

echo.
echo ----------------------------------------------------------
echo [BUOC 2/3] Dang pull code tu GitHub...
echo ----------------------------------------------------------
pushd "%REPO%"
"%GIT%" pull
if errorlevel 1 (
    echo.
    echo    !!! LOI khi pull code. Dung lai, khong build.
    popd
    echo.
    pause
    exit /b 1
)
popd

echo.
echo ----------------------------------------------------------
echo [BUOC 3/3] Dang build 4 app (ban Release)...
echo ----------------------------------------------------------
set "FAILED="
call :build "open-multi-brave-v31\OpenMultiBraveLauncherV31.csproj"  "Open Multi Brave"
call :build "check-shopee-account\check-shopee-account.csproj"        "Check Shopee Account"
call :build "shopee-stat\shopee-stat.csproj"                          "Shopee Stat"
call :build "update-product\UpdateProduct.csproj"                     "Update Product"

echo.
echo ==========================================================
if defined FAILED (
    echo    HOAN TAT - NHUNG CO LOI BUILD:!FAILED!
    echo    Vui long kiem tra log o tren.
) else (
    echo    DONE - DA CAP NHAT ^& BUILD XONG 4 APP
)
echo ==========================================================
echo.
pause
exit /b 0

rem ====== Ham build 1 project ======
:build
echo.
echo    --- Dang build: %~2 ...
"%DOTNET%" build "%REPO%\%~1" -c Release --nologo -v minimal
if errorlevel 1 (
    echo    !!! BUILD LOI: %~2
    set "FAILED=!FAILED! %~2"
) else (
    echo    OK: %~2
)
goto :eof
