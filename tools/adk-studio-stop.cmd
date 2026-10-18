@echo off
rem Stops the local ADK studio started by tools\adk-studio.cmd, and everything it started: the uvicorn
rem server and any MCP server (bin\cli\Polson.CLI.dll) a run is still holding.
rem
rem   tools\adk-studio-stop.cmd [port]     port 8010 by default
rem
rem Matches only a uvicorn serving main:app on that port, so the observer studio and other Python stay up.
rem A run in progress is cut off: its record has no closing event. Ctrl+C in the server's own window stops
rem it gracefully instead.
setlocal
set "PORT=%~1"
if "%PORT%"=="" set "PORT=8010"
powershell -NoProfile -Command ^
  "$all = Get-CimInstance Win32_Process;" ^
  "$mine = @($all | Where-Object { $_.CommandLine -match 'uvicorn main:app' -and $_.CommandLine -match ('--port ' + '%PORT%' + '(\s|$)') });" ^
  "$ids = $mine.ProcessId;" ^
  "$roots = @($mine | Where-Object { $ids -notcontains $_.ParentProcessId });" ^
  "if (-not $roots) { Write-Host 'No ADK studio is running on port %PORT%.'; exit 0 }" ^
  "foreach ($r in $roots) { Write-Host ('Stopping ADK studio (pid ' + $r.ProcessId + ') and its children'); taskkill /PID $r.ProcessId /T /F | Out-Null }"
