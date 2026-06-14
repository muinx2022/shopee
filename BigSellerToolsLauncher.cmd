@echo off
setlocal
set DOTNET=%ProgramFiles%\dotnet\dotnet.exe
if not exist "%DOTNET%" (
  echo Khong tim thay dotnet. Cai .NET 8 SDK.
  pause
  exit /b 1
)
cd /d "%~dp0bigseller-tools"
"%DOTNET%" run -c Release
pause
