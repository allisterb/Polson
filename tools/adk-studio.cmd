@echo off
rem Serves the studio on the ADK runtime, as the container runs it. The same as `polson studio`.
rem
rem   tools\adk-studio.cmd <projects-dir> [port]   then open http://localhost:<port>/studio/home
rem   tools\adk-studio-stop.cmd [port]             to stop it (or Ctrl+C here, which stops it gracefully)
rem
rem The checks and defaults are in src\adk_agent\serve.py, which this only calls: <projects-dir> is required and
rem must exist (usually ..\PolsonRuns), the port is 8010 unless given, and POLSON_BUDGET_TOKENS defaults to
rem 2000000. Set POLSON_ASK_SECONDS to change how long a question waits (120 s by default).
setlocal
for %%I in ("%~dp0..") do set "ROOT=%%~fI"
set "PY=%ROOT%\python-adk\Scripts\python.exe"
if defined POLSON_ADK_PYTHON set "PY=%POLSON_ADK_PYTHON%"
if not exist "%PY%" (
  echo No python-adk venv at %ROOT%\python-adk. See src\adk_agent\README.md, "First-time setup".
  exit /b 1
)
if "%~1"=="" (
  echo usage: tools\adk-studio.cmd ^<projects-dir^> [port]
  echo   e.g. tools\adk-studio.cmd ..\PolsonRuns
  exit /b 2
)
set "PYTHONUTF8=1"
set "POLSON_VERB=tools\adk-studio.cmd"
if "%~2"=="" (
  "%PY%" "%ROOT%\src\adk_agent\serve.py" "%~1"
) else (
  "%PY%" "%ROOT%\src\adk_agent\serve.py" "%~1" --port "%~2"
)
exit /b %ERRORLEVEL%
