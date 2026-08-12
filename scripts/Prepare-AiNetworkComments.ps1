[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Plc,
    [Parameter(Mandatory = $true)][string]$BlockName,
    [Parameter(Mandatory = $true)][string]$Group,
    [Parameter(Mandatory = $true)][hashtable]$Remarks,
    [string]$McpUrl = 'http://127.0.0.1:5111/mcp'
)

$ErrorActionPreference = 'Stop'

function Invoke-Mcp([int]$Id, [string]$Name, [hashtable]$Arguments) {
    $request = @{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } } |
        ConvertTo-Json -Depth 20 -Compress
    $headers = @{}
    if ($env:TIA_MCP_TOKEN) { $headers.Authorization = 'Bearer ' + $env:TIA_MCP_TOKEN }
    $response = Invoke-RestMethod -Uri $McpUrl -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($request)) -TimeoutSec 180
    if ($response.error) { throw $response.error.message }
    $text = ($response.result.content | Where-Object type -eq 'text').text
    if (-not $text) { throw "MCP tool $Name returned no text." }
    return ($text | ConvertFrom-Json)
}

$target = @{ plc = $Plc; name = $BlockName; group = $Group }
$export = Invoke-Mcp 1 'tia_export_block' $target
$document = [xml]$export.xml
$units = @($document.SelectNodes("//*[substring(local-name(), string-length(local-name()) - string-length('CompileUnit') + 1) = 'CompileUnit']"))
$targets = @()

foreach ($unit in $units) {
    $aiComponent = @($unit.SelectNodes(".//*[local-name()='Component' and starts-with(@Name,'AI')]")) |
        Where-Object { $_.GetAttribute('Name') -match '^AI([2-9]|[12][0-9]|3[0-2])$' } | Select-Object -First 1
    if (-not $aiComponent) { continue }
    $aiName = $aiComponent.GetAttribute('Name')
    if (-not $Remarks.ContainsKey($aiName)) { throw "Missing remark for $aiName." }
    $sensor = [int]($aiName.Substring(2))
    $remark = [string]$Remarks[$aiName]
    $title = $unit.SelectSingleNode(".//*[local-name()='MultilingualText' and @CompositionName='Title']//*[local-name()='Text']")
    $comment = $unit.SelectSingleNode(".//*[local-name()='MultilingualText' and @CompositionName='Comment']//*[local-name()='Text']")
    if (-not $title -or -not $comment) { throw "Title or comment node was not found for $aiName." }
    $title.InnerText = "S$sensor $remark"
    $comment.InnerText = $remark
    $targets += $unit
}

if ($targets.Count -ne 31) { throw "Expected 31 AI networks, found $($targets.Count)." }
$firstMarker = '<SW.Blocks.CompileUnit ID="' + $targets[0].GetAttribute('ID') + '"'
$start = $export.xml.IndexOf($firstMarker, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'First target network was not found in the raw XML.' }
$lastMarker = '<SW.Blocks.CompileUnit ID="' + $targets[-1].GetAttribute('ID') + '"'
$lastStart = $export.xml.IndexOf($lastMarker, $start, [StringComparison]::Ordinal)
$endMarker = '</SW.Blocks.CompileUnit>'
$end = $export.xml.IndexOf($endMarker, $lastStart, [StringComparison]::Ordinal)
if ($lastStart -lt 0 -or $end -lt 0) { throw 'Last target network was not found in the raw XML.' }
$end += $endMarker.Length
$findXml = $export.xml.Substring($start, $end - $start)
$replaceXml = ($targets | ForEach-Object { $_.OuterXml }) -join ''

[pscustomobject]@{
    baselineHash = $export.baselineHash
    networkCount = $targets.Count
    findXml = $findXml
    replacementXml = $replaceXml
}
