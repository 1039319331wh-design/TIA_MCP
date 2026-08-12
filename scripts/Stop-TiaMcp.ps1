[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pidFile = Join-Path $projectRoot '.runtime\tia-mcp.pid'

if (-not (Test-Path $pidFile)) {
    Write-Output 'No managed TIA MCP process is recorded.'
    exit 0
}

$processId = (Get-Content $pidFile -Raw).Trim()
if ($processId -notmatch '^\d+$') {
    throw "Invalid PID file: $pidFile"
}

$process = Get-Process -Id ([int]$processId) -ErrorAction SilentlyContinue
if ($process) {
    Stop-Process -Id $process.Id
    $process.WaitForExit(5000) | Out-Null
    Write-Output "Stopped TIA MCP process $processId."
}
else {
    Write-Output "TIA MCP process $processId was no longer running."
}

Remove-Item -LiteralPath $pidFile -Force
