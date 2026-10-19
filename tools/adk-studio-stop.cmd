@echo off
rem Stops the local ADK studio started by `polson studio` or tools\adk-studio.cmd, and everything it started.
rem
rem   tools\adk-studio-stop.cmd [port]     port 8010 by default
rem
rem The logic is in adk-studio-stop.ps1: it finds the studio by the port it listens on, checks that what holds
rem the port is the studio, and stops that process tree. Ctrl+C in the server's own window stops it gracefully.
setlocal
set "PORT=%~1"
if "%PORT%"=="" set "PORT=8010"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0adk-studio-stop.ps1" -Port %PORT%
exit /b %ERRORLEVEL%
