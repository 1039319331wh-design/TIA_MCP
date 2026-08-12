[CmdletBinding()]
param(
    [string]$ListenUrl = 'http://127.0.0.1:5111',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnetCliHome = Join-Path $projectRoot '.dotnet-cli'
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null

$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET 8 SDK was not found. Install it, then run this script again.'
}

if (-not $SkipBuild) {
    & dotnet build (Join-Path $projectRoot 'TiaMcpBridge.csproj')
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }
}

$configDirectory = Join-Path $env:USERPROFILE '.codex'
$configPath = Join-Path $configDirectory 'config.toml'
Write-Output ''
Write-Output 'Client initialization completed.'
Write-Output "Add the following block to $configPath (keep other existing settings):"
Write-Output ''
Write-Output '[mcp_servers.tia-openness]'
Write-Output ('url = "' + $ListenUrl.TrimEnd('/') + '/mcp"')
Write-Output ''
Write-Output 'Machine-specific secrets and Siemens installation paths are intentionally not synchronized.'
