@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "tools\MorphFaceEditor.QA" -c Release -- %*
set "MFE_QA_EXIT=%ERRORLEVEL%"
echo.
if not "%MFE_QA_EXIT%"=="0" echo One or more QA cases failed or could not run. See the report path above.
pause
exit /b %MFE_QA_EXIT%
