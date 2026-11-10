@echo off
setlocal EnableExtensions
title Headless Skua - Build

cd /d "%~dp0"
if errorlevel 1 exit /b 1

set "OUTPUT_DIR=%~dp0Build"
set "PROJECT=src\HeadlessSkua.Host\HeadlessSkua.Host.csproj"
set "CONFIGURATION=Release"
set "RUNTIME=win-x64"

echo.
echo  ==========================================
echo           HEADLESS SKUA - BUILD
echo  ==========================================
echo.

where dotnet >nul 2>nul
if errorlevel 1 (
    echo [ERROR] .NET 10 SDK is required.
    echo Install it from https://dotnet.microsoft.com/download/dotnet/10.0
    goto :failed
)

dotnet --list-sdks | findstr /B "10." >nul
if errorlevel 1 (
    echo [ERROR] .NET 10 SDK was not found.
    goto :failed
)

where git >nul 2>nul
if errorlevel 1 (
    echo [ERROR] Git is required to initialize the VibeSkua submodule.
    goto :failed
)

echo [1/8] Pulling the latest Headless Skua changes...
git pull --ff-only
if errorlevel 1 (
    echo [ERROR] Could not update the repository. Check for local changes or a diverged branch.
    goto :failed
)

echo.
echo [2/8] Initializing and updating VibeSkua source...
git submodule update --init --recursive
if errorlevel 1 goto :failed

if not exist "vendor\VibeSkua\Skua.Core\Skua.Core.csproj" (
    echo [ERROR] VibeSkua submodule was not initialized correctly.
    goto :failed
)

echo.
echo [3/8] Restoring solution...
dotnet restore HeadlessSkua.sln
if errorlevel 1 goto :failed

echo.
echo [4/8] Building solution...
dotnet build HeadlessSkua.sln --configuration %CONFIGURATION% --no-restore
if errorlevel 1 goto :failed

set "PLAYWRIGHT_SCRIPT=src\HeadlessSkua.Host\bin\%CONFIGURATION%\net10.0\playwright.ps1"
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
echo [5/8] Installing headless Chromium...
%POWERSHELL% -NoProfile -ExecutionPolicy Bypass -File "%PLAYWRIGHT_SCRIPT%" install chromium
if errorlevel 1 goto :failed

echo.
echo [6/8] Installing Ruffle and the Skua game client...
%POWERSHELL% -NoProfile -ExecutionPolicy Bypass -File "runtime\ruffle-host\Install-Runtime.ps1"
if errorlevel 1 goto :failed

echo.
echo [7/8] Publishing standalone Windows build...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
dotnet publish "%PROJECT%" --configuration %CONFIGURATION% --runtime %RUNTIME% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o "%OUTPUT_DIR%"
if errorlevel 1 goto :failed

echo.
echo Copying Playwright driver files...
set "PLAYWRIGHT_DRIVER_DIR=src\HeadlessSkua.Host\bin\%CONFIGURATION%\net10.0\.playwright"
if not exist "%PLAYWRIGHT_DRIVER_DIR%\node\win32_x64\node.exe" (
    echo [ERROR] Playwright Node driver was not found in the build output.
    goto :failed
)
if not exist "%PLAYWRIGHT_DRIVER_DIR%\package\cli.js" (
    echo [ERROR] Playwright CLI entry point was not found in the build output.
    goto :failed
)
xcopy "%PLAYWRIGHT_DRIVER_DIR%" "%OUTPUT_DIR%\.playwright\\" /E /I /Y >nul
if errorlevel 1 goto :failed

echo.
echo Copying Ruffle runtime and game files...
xcopy "runtime\ruffle-host" "%OUTPUT_DIR%\runtime\ruffle-host\" /E /I /Y >nul
if errorlevel 1 goto :failed

if not exist "%OUTPUT_DIR%\HeadlessSkua.exe" (
    echo [ERROR] Published executable was not created.
    goto :failed
)

if not exist "%OUTPUT_DIR%\panel\index.html" (
    echo [ERROR] Remote panel assets were not published.
    goto :failed
)

echo.
echo [8/8] Checking published installation...
"%OUTPUT_DIR%\HeadlessSkua.exe" doctor
if errorlevel 1 goto :failed

echo.
echo  ==========================================
echo       HEADLESS SKUA BUILD COMPLETE
echo  ==========================================
echo.
echo Published application:
echo   %OUTPUT_DIR%\HeadlessSkua.exe
echo.
echo Starting Headless Skua...
echo.

if /I "%~1"=="-butler" goto :butler
if /I "%~1"=="run" goto :launch
goto :default_launch

:default_launch
pushd "%OUTPUT_DIR%"
HeadlessSkua.exe run
set "EXIT_CODE=%errorlevel%"
popd
exit /b %EXIT_CODE%

:launch
shift
pushd "%OUTPUT_DIR%"
HeadlessSkua.exe run %*
set "EXIT_CODE=%errorlevel%"
popd
exit /b %EXIT_CODE%

:butler
pushd "%OUTPUT_DIR%"
HeadlessSkua.exe %*
set "EXIT_CODE=%errorlevel%"
popd
exit /b %EXIT_CODE%

:failed
echo.
echo [ERROR] Build or setup failed. Review the error above.
echo.
pause
exit /b 1
