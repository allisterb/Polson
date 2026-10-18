@echo off
rem Serves the ADK runtime locally as the container runs it: main.py, with the studio mounted at /studio.
rem
rem   tools\adk-studio.cmd [port]          then open http://localhost:<port>/studio/home
rem   tools\adk-studio-stop.cmd [port]     to stop it (or Ctrl+C here, which stops it gracefully)
rem
rem Each setting below is a default: set it beforehand to override it.
rem   POLSON_PROJECTS_DIR   where projects are created; outside the repo, so a run never inherits its CLAUDE.md
rem   POLSON_BUDGET_TOKENS  the runaway cap, cache-adjusted (see src\adk_agent\README.md); 0 means none
rem   POLSON_ASK_SECONDS    how long a question waits for the director; the runtime's own default is 120
rem
rem Needs the python-adk venv (src\adk_agent\README.md, "First-time setup") and a current bin\cli (build.cmd).
rem Port 8010 by default because 8000 is where the observer studio (python -m studio) usually runs.
setlocal
for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "PORT=%~1"
if "%PORT%"=="" set "PORT=8010"
if not defined POLSON_PROJECTS_DIR for %%I in ("%ROOT%\..\PolsonRuns\adk") do set "POLSON_PROJECTS_DIR=%%~fI"
if not defined POLSON_BUDGET_TOKENS set "POLSON_BUDGET_TOKENS=2000000"
set "PYTHONUTF8=1"

if not exist "%ROOT%\python-adk\Scripts\python.exe" (
  echo No python-adk venv at %ROOT%\python-adk. See src\adk_agent\README.md, "First-time setup".
  exit /b 1
)
if not exist "%ROOT%\bin\cli\Polson.CLI.dll" (
  echo No bin\cli\Polson.CLI.dll. Run build.cmd from the repository root first.
  exit /b 1
)
if not exist "%POLSON_PROJECTS_DIR%" mkdir "%POLSON_PROJECTS_DIR%"

echo Polson ADK studio: http://localhost:%PORT%/studio/home
echo   projects  %POLSON_PROJECTS_DIR%
echo   token cap %POLSON_BUDGET_TOKENS%
cd /d "%ROOT%"
"%ROOT%\python-adk\Scripts\python.exe" -m uvicorn main:app --host 127.0.0.1 --port %PORT% --app-dir src/adk_agent
