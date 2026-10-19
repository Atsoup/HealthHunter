@echo off
REM Builds HealthHunter.exe into the "publish" folder.
REM Needs the .NET 8 SDK:  winget install Microsoft.DotNet.SDK.8
where dotnet >nul 2>nul || (echo .NET SDK not found. Run: winget install Microsoft.DotNet.SDK.8 & pause & exit /b 1)
dotnet publish "%~dp0HealthHunter.csproj" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o "%~dp0publish"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
echo.
echo Done: %~dp0publish\HealthHunter.exe
pause
