@echo off
setlocal EnableDelayedExpansion

:: ─────────────────────────────────────────────────────────────────────────────
::  RAM-AI Phase 3 — install.bat
::  Run as Administrator.
::  Usage:
::    install.bat            → build (Release) + register + start service
::    install.bat /uninstall → stop + delete service
::    install.bat /console   → run in console (no SCM) for debugging
:: ─────────────────────────────────────────────────────────────────────────────

set SERVICE_NAME=RamAI-Phase3
set PROJECT_DIR=%~dp0
set EXE=%PROJECT_DIR%bin\Release\net10.0-windows\win-x64\RamAI.Phase3.exe

:: Check for admin rights
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] This script must be run as Administrator.
    echo         Right-click install.bat ^> "Run as administrator"
    pause & exit /b 1
)

:: ── /uninstall ────────────────────────────────────────────────────────────────
if /i "%1"=="/uninstall" (
    echo Stopping service %SERVICE_NAME% ...
    sc stop "%SERVICE_NAME%" >nul 2>&1
    timeout /t 3 /nobreak >nul

    echo Deleting service %SERVICE_NAME% ...
    sc delete "%SERVICE_NAME%"
    if !ERRORLEVEL! EQU 0 (
        echo Service removed.
    ) else (
        echo [WARN] sc delete returned !ERRORLEVEL! -- may already be gone.
    )
    pause & exit /b 0
)

:: ── /console ─────────────────────────────────────────────────────────────────
if /i "%1"=="/console" (
    echo Running in console mode (Ctrl+C to stop) ...
    "%EXE%"
    pause & exit /b 0
)

:: ── Build ─────────────────────────────────────────────────────────────────────
echo.
echo [1/3] Building Phase3 (Release x64) ...
dotnet publish "%PROJECT_DIR%Phase3.csproj" ^
    -c Release -r win-x64 --self-contained false ^
    -o "%PROJECT_DIR%bin\Release\net10.0-windows\win-x64"

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Build failed.
    pause & exit /b 1
)
echo Build OK.

:: ── Verify model exists ───────────────────────────────────────────────────────
set MODEL=C:\projettoto\RAM-AI\Phase2\model\ram-ai.zip
if not exist "%MODEL%" (
    echo.
    echo [ERROR] Model not found: %MODEL%
    echo         Run Phase 2 first to train and save ram-ai.zip.
    pause & exit /b 1
)
echo Model found: %MODEL%

:: ── Register + start via the exe's built-in --install flag ───────────────────
echo.
echo [2/3] Registering Windows Service ...
"%EXE%" --install

if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Service registration failed (exit %ERRORLEVEL%).
    pause & exit /b 1
)

:: ── Verify ───────────────────────────────────────────────────────────────────
echo.
echo [3/3] Verifying ...
sc query "%SERVICE_NAME%"

echo.
echo -------------------------------------------------------------------------
echo  Service : %SERVICE_NAME%
echo  Exe     : %EXE%
echo  Model   : C:\projettoto\RAM-AI\Phase2\model\ram-ai.zip
echo  Log     : C:\projettoto\RAM-AI\Phase3\logs\events.log
echo  Cache   : C:\projettoto\RAM-AI\Phase3\cache\ram-ai.cache
echo.
echo  Useful commands:
echo    sc stop  %SERVICE_NAME%
echo    sc start %SERVICE_NAME%
echo    sc query %SERVICE_NAME%
echo    install.bat /uninstall
echo    install.bat /console
echo -------------------------------------------------------------------------
pause
