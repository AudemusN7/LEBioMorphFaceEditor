@echo off
cd /d "%~dp0"
dotnet build tools\MorphFaceEditor.Localiser\MorphFaceEditor.Localiser.csproj -c Release
if errorlevel 1 (
    pause
    exit /b 1
)
start "" "tools\MorphFaceEditor.Localiser\bin\Release\net10.0-windows\MorphFaceEditor.Localiser.exe" %*
