<#
.SYNOPSIS
    Stops the local ADK studio on a port, and everything it started: the server and any MCP server
    (bin\cli\Polson.CLI.dll) a run is still holding.

.DESCRIPTION
    tools\adk-studio-stop.cmd [port]     port 8010 by default

    Found by the port, not by the command line: `serve.py` runs uvicorn in-process, so its command line names
    neither uvicorn nor a port when the default is used. The process listening on the port is checked to be the
    studio (`serve.py`, or `uvicorn main:app` from before serve.py) before anything is stopped, so whatever else
    holds the port - the observer studio on 8000, say - is left alone and named. A run in progress is cut off:
    its record has no closing event. Ctrl+C in the server's own window stops it gracefully instead.
#>
param([int] $Port = 8010)

$listening = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $listening) {
    Write-Output "No ADK studio is running on port $Port."
    exit 0
}

$byId = @{}
Get-CimInstance Win32_Process | ForEach-Object { $byId[[int] $_.ProcessId] = $_ }
function Test-Studio($p) { $p -and $p.CommandLine -match 'serve\.py|uvicorn main:app' }

$p = $byId[[int] $listening.OwningProcess]
if (-not (Test-Studio $p)) {
    $what = if ($p) { $p.CommandLine } else { "process $($listening.OwningProcess)" }
    [Console]::Error.WriteLine("Port $Port is held by something that is not the ADK studio, so it was left running:`n  $what")
    exit 1
}

# A venv's python.exe is a launcher that starts the real interpreter as its child: climb to the top of the chain,
# so the whole tree goes, children included.
while ($byId.ContainsKey([int] $p.ParentProcessId) -and (Test-Studio $byId[[int] $p.ParentProcessId])) {
    $p = $byId[[int] $p.ParentProcessId]
}
Write-Output "Stopping ADK studio on port $Port (pid $($p.ProcessId)) and its children"
taskkill /PID $p.ProcessId /T /F | Out-Null
exit 0
