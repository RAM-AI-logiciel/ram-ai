@echo off
cd /d "%~dp0"
dotnet publish . -c Release -r win-x64 --self-contained false > "%~dp0build_output.log" 2>&1
echo Exit code: %ERRORLEVEL% >> "%~dp0build_output.log"
