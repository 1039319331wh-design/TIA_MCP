[CmdletBinding()]
param(
    [switch]$EnableWrite,
    [switch]$EnableSave,
    [string]$ListenUrl = 'http://127.0.0.1:5111',
    [ValidateSet('Auto', 'V16', 'V17', 'V18', 'V19', 'V20', 'V21')]
    [string]$TiaVersion = 'Auto',
    [string]$OpennessDll
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$runtimeDirectory = Join-Path $projectRoot '.runtime'
$dotnetCliHome = Join-Path $projectRoot '.dotnet-cli'
$pidFile = Join-Path $runtimeDirectory 'tia-mcp.pid'
$healthUrl = "$($ListenUrl.TrimEnd('/'))/health"
$healthHeaders = @{}
if (-not [string]::IsNullOrWhiteSpace($env:TIA_MCP_TOKEN)) {
    $healthHeaders.Authorization = 'Bearer ' + $env:TIA_MCP_TOKEN
}

New-Item -ItemType Directory -Path $runtimeDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

function Get-InstalledOpennessDlls {
    $versions = if ($TiaVersion -eq 'Auto') { 21..16 } else { [int]$TiaVersion.Substring(1) }
    foreach ($version in $versions) {
        $root = "C:\Program Files\Siemens\Automation\Portal V$version\PublicAPI"
        if (Test-Path -LiteralPath $root) {
            $files = @(Get-ChildItem -LiteralPath $root -Filter Siemens.Engineering.dll -File -Recurse -ErrorAction SilentlyContinue)
            $preferred = @($files | Where-Object { $_.Directory.Name -eq "V$version" })
            $fallback = @($files | Where-Object { $_.Directory.Name -ne "V$version" } | Sort-Object FullName -Descending)
            foreach ($file in @($preferred) + @($fallback)) {
                [pscustomobject]@{ Version = "V$version"; Path = $file.FullName }
            }
        }
    }
}

function Select-OpennessDll {
    if (-not [string]::IsNullOrWhiteSpace($OpennessDll)) {
        if (-not (Test-Path -LiteralPath $OpennessDll)) { throw "TIA Openness DLL was not found: $OpennessDll" }
        return (Resolve-Path -LiteralPath $OpennessDll).Path
    }

    $candidates = @(Get-InstalledOpennessDlls)
    if ($candidates.Count -eq 0) { throw "No TIA Portal $TiaVersion Openness DLL was found (supported: V16-V21)." }
    $worker = Join-Path $projectRoot 'bin\Debug\net8.0-windows\worker\TiaOpennessWorker.exe'
    if (-not (Test-Path -LiteralPath $worker)) {
        $worker = Join-Path $projectRoot 'TiaOpennessWorker\bin\Debug\net452\TiaOpennessWorker.exe'
    }
    if (Test-Path -LiteralPath $worker) {
        $previousDll = $env:TIA_OPENNESS_DLL
        try {
            foreach ($candidate in $candidates) {
                $env:TIA_OPENNESS_DLL = $candidate.Path
                try {
                    $probe = (& $worker status | ConvertFrom-Json)
                    if ($probe.ok -and [int]$probe.data.processCount -gt 0) { return $candidate.Path }
                }
                catch { }
            }
        }
        finally { $env:TIA_OPENNESS_DLL = $previousDll }
    }
    return $candidates[0].Path
}

try {
    $health = Invoke-RestMethod -Uri $healthUrl -Headers $healthHeaders -TimeoutSec 2
    Write-Output "TIA MCP is already running at $ListenUrl (TIA processes: $($health.processCount))."
    exit 0
}
catch {
    # No healthy bridge is listening; continue with startup.
}

if (Test-Path $pidFile) {
    $stalePid = (Get-Content $pidFile -Raw).Trim()
    if ($stalePid -match '^\d+$' -and (Get-Process -Id ([int]$stalePid) -ErrorAction SilentlyContinue)) {
        throw "A bridge process with PID $stalePid exists but its health endpoint is unavailable."
    }
    Remove-Item -LiteralPath $pidFile -Force
}

if ($EnableSave -and -not $EnableWrite) {
    throw '-EnableSave requires -EnableWrite.'
}

$launchEnvironment = @{
    TIA_MCP_URL = $ListenUrl
}

$selectedOpennessDll = Select-OpennessDll
$launchEnvironment.TIA_OPENNESS_DLL = $selectedOpennessDll

if ($EnableWrite) {
    foreach ($requiredName in 'TIA_MCP_TOKEN', 'TIA_WRITE_SECRET') {
        $requiredValue = [Environment]::GetEnvironmentVariable($requiredName, 'Process')
        if ([string]::IsNullOrWhiteSpace($requiredValue)) {
            throw "$requiredName must be set before starting write mode."
        }
    }
    $launchEnvironment.TIA_ENABLE_WRITE = 'true'
}

if ($EnableSave) {
    $launchEnvironment.TIA_ENABLE_SAVE = 'true'
}

$executable = Join-Path $projectRoot 'bin\Debug\net8.0-windows\TiaMcpBridge.exe'
if (Test-Path $executable) {
    $filePath = $executable
    $argumentList = @()
}
else {
    $dotnet = (Get-Command dotnet -ErrorAction Stop).Source
    $filePath = $dotnet
    $argumentList = @('run', '--project', (Join-Path $projectRoot 'TiaMcpBridge.csproj'), '--no-launch-profile')
}

$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $filePath
$startInfo.WorkingDirectory = $projectRoot
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.Arguments = ($argumentList | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '
$previousEnvironment = @{}
try {
    foreach ($entry in $launchEnvironment.GetEnumerator()) {
        $previousEnvironment[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key, 'Process')
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    $process = [Diagnostics.Process]::Start($startInfo)
}
finally {
    foreach ($entry in $previousEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
}
if (-not $process) {
    throw 'Failed to create the TIA MCP process.'
}

Set-Content -LiteralPath $pidFile -Value $process.Id -Encoding ascii

for ($attempt = 0; $attempt -lt 30; $attempt++) {
    Start-Sleep -Milliseconds 500
    if ($process.HasExited) {
        throw "TIA MCP exited during startup with code $($process.ExitCode)."
    }
    try {
        $health = Invoke-RestMethod -Uri $healthUrl -Headers $healthHeaders -TimeoutSec 2
        Write-Output "TIA MCP started at $ListenUrl using $selectedOpennessDll (PID $($process.Id), TIA processes: $($health.processCount))."
        exit 0
    }
    catch {
        # Retry until ASP.NET has bound the local endpoint.
    }
}

throw 'TIA MCP did not become healthy within 15 seconds.'
