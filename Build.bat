@echo off
setlocal EnableExtensions
title Headless Skua - All-in-One Build

cd /d "%~dp0"
if errorlevel 1 exit /b 1

echo.
echo  ==========================================
echo           HEADLESS SKUA - AIO
echo  ==========================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET 10 SDK is required.
    echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0
    exit /b 1
)

dotnet --list-sdks | findstr /B "10." >nul
if errorlevel 1 (
    echo [ERROR] .NET 10 SDK was not found.
    echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0
    exit /b 1
)

where git >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Git is required to initialize the VibeSkua submodule.
    exit /b 1
)

echo [1/6] Initializing VibeSkua source...
git submodule update --init --recursive
if errorlevel 1 goto :failed

if not exist "vendor\VibeSkua\Skua.Core\Skua.Core.csproj" (
    echo [ERROR] VibeSkua submodule was not initialized correctly.
    goto :failed
)

echo.
echo [2/6] Restoring solution...
dotnet restore HeadlessSkua.sln
if errorlevel 1 goto :failed

echo.
echo [3/6] Building Headless Skua...
dotnet build HeadlessSkua.sln --configuration Release --no-restore
if errorlevel 1 goto :failed

set "PLAYWRIGHT_SCRIPT=src\HeadlessSkua.Host\bin\Release\net10.0\playwright.ps1"
if not exist "%PLAYWRIGHT_SCRIPT%" (
    echo [ERROR] Playwright install script was not generated.
    goto :failed
)

where pwsh >nul 2>nul
if not errorlevel 1 (
    set "POWERSHELL=pwsh"
) else (
    set "POWERSHELL=powershell"
)

echo.
echo [4/6] Installing headless Chromium...
%POWERSHELL% -NoProfile -ExecutionPolicy Bypass -File "%PLAYWRIGHT_SCRIPT%" install chromium
if errorlevel 1 goto :failed

echo.
echo [5/6] Installing Ruffle and the Skua game client...
%POWERSHELL% -NoProfile -ExecutionPolicy Bypass -File "runtime\ruffle-host\Install-Runtime.ps1"
if errorlevel 1 goto :failed

echo.
echo [6/6] Checking installation...
dotnet run --project src/HeadlessSkua.Host --configuration Release --no-build -- doctor
if errorlevel 1 goto :failed

echo.
echo  ==========================================
echo       HEADLESS SKUA BUILD COMPLETE
echo  ==========================================
echo.
echo Run the headless client:
echo   Build.bat run
echo.
echo Run an existing Skua script:
echo   Build.bat run --script "C:\path\to\YourScript.cs"
echo.

if /I "%~1"=="run" (
    shift
    echo Starting Headless Skua...
    dotnet run --project src/HeadlessSkua.Host --configuration Release --no-build -- run %*
    exit /b %errorlevel%
)

exit /b 0

:failed
echo.
echo [ERROR] Build or setup failed. Review the error above.
exit /b 1
