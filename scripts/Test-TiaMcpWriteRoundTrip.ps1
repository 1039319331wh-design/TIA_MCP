[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Plc,
    [Parameter(Mandatory)] [string]$BlockName,
    [string]$Group,
    [Parameter(Mandatory)] [string]$Find,
    [Parameter(Mandatory)] [string]$Replace,
    [switch]$ConfirmRoundTrip
)

$ErrorActionPreference = 'Stop'
if (-not $ConfirmRoundTrip) {
    throw 'This test temporarily imports and compiles a block. Pass -ConfirmRoundTrip to continue.'
}
if ($Find -eq $Replace) { throw 'Find and replacement text must differ.' }

$projectRoot = Split-Path -Parent $PSScriptRoot
$baseUrl = 'http://127.0.0.1:5111'
$original = $null
$firstApplyCompleted = $false
$headers = $null

function Stop-LocalBridge {
    $line = netstat -ano -p tcp | Select-String '127.0.0.1:5111\s+0.0.0.0:0\s+LISTENING\s+(\d+)$' | Select-Object -First 1
    if (-not $line) { return }
    if ($line.ToString() -notmatch 'LISTENING\s+(\d+)$') { throw 'Could not resolve the local bridge PID.' }
    $bridgePid = [int]$Matches[1]
    $process = Get-Process -Id $bridgePid -ErrorAction Stop
    if ($process.ProcessName -notin 'dotnet', 'TiaMcpBridge') {
        throw "Port 5111 belongs to unexpected process $($process.ProcessName) ($bridgePid)."
    }
    Stop-Process -Id $bridgePid -Force
    for ($attempt = 0; $attempt -lt 20 -and (Get-Process -Id $bridgePid -ErrorAction SilentlyContinue); $attempt++) {
        Start-Sleep -Milliseconds 250
    }
    if (Get-Process -Id $bridgePid -ErrorAction SilentlyContinue) { throw "Bridge process $bridgePid did not stop." }
}

function New-RandomHex([int]$Bytes) {
    $buffer = New-Object byte[] $Bytes
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($buffer)
    ([BitConverter]::ToString($buffer)).Replace('-', '').ToLowerInvariant()
}

function Invoke-JsonPost([string]$Path, $Body) {
    $json = $Body | ConvertTo-Json -Depth 20
    Invoke-RestMethod -Uri ($baseUrl + $Path) -Method Post -Headers $headers `
        -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 120
}

function Export-Target {
    $query = 'plc={0}&name={1}' -f [Uri]::EscapeDataString($Plc), [Uri]::EscapeDataString($BlockName)
    if (-not [string]::IsNullOrWhiteSpace($Group)) { $query += '&group=' + [Uri]::EscapeDataString($Group) }
    Invoke-RestMethod -Uri "$baseUrl/api/blocks/export?$query" -Headers $headers -TimeoutSec 120
}

function Preview-Xml($BaselineHash, $Xml) {
    Invoke-JsonPost '/api/blocks/preview' @{
        plc = $Plc; name = $BlockName; group = $Group; baselineHash = $BaselineHash; xml = $Xml
    }
}

function Apply-Xml($BaselineHash, $Xml, $ApplyToken) {
    Invoke-JsonPost '/api/blocks/apply' @{
        plc = $Plc; name = $BlockName; group = $Group
        baselineHash = $BaselineHash; xml = $Xml; applyToken = $ApplyToken
    }
}

try {
    Stop-LocalBridge
    $env:TIA_MCP_TOKEN = New-RandomHex 32
    $env:TIA_WRITE_SECRET = New-RandomHex 48
    $headers = @{ Authorization = 'Bearer ' + $env:TIA_MCP_TOKEN }
    & (Join-Path $PSScriptRoot 'Start-TiaMcp.ps1') -EnableWrite | Out-Host

    $original = Export-Target
    $occurrences = ([regex]::Matches($original.xml, [regex]::Escape($Find))).Count
    if ($occurrences -ne 1) { throw "Expected one exact source occurrence, found $occurrences." }

    $candidateXml = $original.xml.Replace($Find, $Replace)
    $candidatePreview = Preview-Xml $original.baselineHash $candidateXml
    if (-not $candidatePreview.valid -or -not $candidatePreview.changed -or [string]::IsNullOrWhiteSpace($candidatePreview.applyToken)) {
        throw 'Candidate preview did not produce a valid write token.'
    }

    $firstApply = Apply-Xml $original.baselineHash $candidateXml $candidatePreview.applyToken
    $firstApplyCompleted = $true
    if (-not $firstApply.ok -or -not $firstApply.compiled -or $firstApply.projectSaved) {
        throw 'Candidate apply did not complete with the expected safeguards.'
    }

    $changed = Export-Target
    $restorePreview = Preview-Xml $changed.baselineHash $original.xml
    if ([string]::IsNullOrWhiteSpace($restorePreview.applyToken)) { throw 'Restore preview did not produce a write token.' }
    $restoreApply = Apply-Xml $changed.baselineHash $original.xml $restorePreview.applyToken
    $restored = Export-Target
    if ($restored.baselineHash -ne $original.baselineHash) { throw 'Round-trip restoration hash mismatch.' }
    $firstApplyCompleted = $false

    [pscustomobject]@{
        ok = $true
        plc = $Plc
        block = $BlockName
        initialHash = $original.baselineHash
        candidateHash = $firstApply.appliedHash
        restoredHash = $restored.baselineHash
        candidateCompileErrors = $firstApply.compileResult.errorCount
        restoreCompileErrors = $restoreApply.compileResult.errorCount
        projectSaved = $false
        downloadedToPlc = $false
        backups = @($firstApply.backupPath, $restoreApply.backupPath)
    }
}
catch {
    if ($firstApplyCompleted -and $original) {
        try {
            $current = Export-Target
            $emergencyPreview = Preview-Xml $current.baselineHash $original.xml
            if (-not [string]::IsNullOrWhiteSpace($emergencyPreview.applyToken)) {
                $null = Apply-Xml $current.baselineHash $original.xml $emergencyPreview.applyToken
            }
        }
        catch {
            Write-Error "Emergency restoration failed. Use the generated backup before continuing: $($_.Exception.Message)"
        }
    }
    throw
}
finally {
    Stop-LocalBridge
    Remove-Item Env:TIA_ENABLE_WRITE -ErrorAction SilentlyContinue
    Remove-Item Env:TIA_ENABLE_SAVE -ErrorAction SilentlyContinue
    Remove-Item Env:TIA_MCP_TOKEN -ErrorAction SilentlyContinue
    Remove-Item Env:TIA_WRITE_SECRET -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'Start-TiaMcp.ps1') | Out-Host
}
