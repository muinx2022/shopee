@echo off
setlocal
cd /d "%~dp0"

set "PY=%~dp0update-product-python\.venv\Scripts\python.exe"
if not exist "%PY%" set "PY=python"

echo Starting Data API on http://127.0.0.1:8012 ...
echo Workbook: data\data.xlsx
echo.
start "Shopee Data API (8012)" "%PY%" "%~dp0api\main.py"
timeout /t 2 /nobreak >nul
echo API window opened. Wait a few seconds before "Chay tiep" in Multi Brave Manager.
pause
