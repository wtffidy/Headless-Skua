@echo off
setlocal EnableExtensions
title Headless Skua - Build

cd /d "%~dp0"
if errorlevel 1 exit /b 1

set "OUTPUT_DIR=%~dp0Build"
set "PROJECT=src\HeadlessSkua.Host\HeadlessSkua.Host.csproj"
set "CONFIGURATION=Release"
set "RUNTIME=win-x64"

echo [0/6] Closing any running Headless Skua instances...
taskkill /F /T /IM HeadlessSkua.exe >nul 2>nul
ping 127.0.0.1 -n 2 >nul

echo.
echo  ==========================================
echo           HEADLESS SKUA - BUILD
echo  ==========================================
echo.

if exist "%ProgramFiles%\dotnet\dotnet.exe" set "PATH=%ProgramFiles%\dotnet;%PATH%"

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

if exist ".git" (
    echo [1/6] Pulling the latest Headless Skua changes...
    git pull --ff-only
    if errorlevel 1 (
        echo [ERROR] Could not update the repository. Check for local changes or a diverged branch.
        goto :failed
    )

    echo.
    echo [2/6] Initializing and updating VibeSkua source...
    git submodule update --init --recursive
    if errorlevel 1 goto :failed
) else (
    echo [1/6] Skipping git pull - no .git directory found.
    echo [2/6] Using existing VibeSkua source...
)

if not exist "vendor\VibeSkua\Skua.Core\Skua.Core.csproj" (
    echo [ERROR] VibeSkua submodule was not initialized correctly.
    goto :failed
)

echo.
echo [3/6] Restoring solution...
dotnet restore HeadlessSkua.sln
if errorlevel 1 goto :failed

echo.
echo [4/6] Building solution...
dotnet build HeadlessSkua.sln --configuration %CONFIGURATION% --no-restore
if errorlevel 1 goto :failed

echo.
echo [5/6] Publishing standalone Windows build...
if exist "%OUTPUT_DIR%" rmdir /s /q "%OUTPUT_DIR%"
dotnet publish "%PROJECT%" --configuration %CONFIGURATION% --runtime %RUNTIME% --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o "%OUTPUT_DIR%"
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
echo [6/6] Checking published installation...
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
start "Headless Skua" /D "%OUTPUT_DIR%" "%OUTPUT_DIR%\HeadlessSkua.exe" run %*
if errorlevel 1 goto :launch_failed
echo Headless Skua opened in a separate console window.
exit /b 0

:launch
set "RUN_ARGS=%*"
set "RUN_ARGS=%RUN_ARGS:* =%"
if /I "%RUN_ARGS%"=="run" set "RUN_ARGS="
start "Headless Skua" /D "%OUTPUT_DIR%" "%OUTPUT_DIR%\HeadlessSkua.exe" run %RUN_ARGS%
if errorlevel 1 goto :launch_failed
echo Headless Skua opened in a separate console window.
exit /b 0

:butler
start "Headless Skua" /D "%OUTPUT_DIR%" "%OUTPUT_DIR%\HeadlessSkua.exe" %*
if errorlevel 1 goto :launch_failed
echo Headless Skua opened in a separate console window.
exit /b 0

:launch_failed
echo.
echo [ERROR] Could not start Headless Skua in a separate console.
echo.
exit /b 1

:failed
echo.
echo [ERROR] Build or setup failed. Review the error above.
echo.
pause
exit /b 1
