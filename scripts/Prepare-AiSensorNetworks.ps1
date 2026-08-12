[CmdletBinding()]
param(
    [string]$McpUrl = 'http://127.0.0.1:5111/mcp',
    [Parameter(Mandatory = $true)][string]$Plc,
    [Parameter(Mandatory = $true)][string]$BlockName,
    [Parameter(Mandatory = $true)][string]$Group
)

$ErrorActionPreference = 'Stop'

function Invoke-Mcp([int]$Id, [string]$Name, [hashtable]$Arguments) {
    $request = @{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = @{ name = $Name; arguments = $Arguments } } |
        ConvertTo-Json -Depth 20
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
if ($units.Count -ne 8) { throw "Expected 8 compile units, found $($units.Count)." }
$example = $units[3]
$exampleStartMarker = '<SW.Blocks.CompileUnit ID="' + $example.GetAttribute('ID') + '"'
$exampleStart = $export.xml.IndexOf($exampleStartMarker, [StringComparison]::Ordinal)
if ($exampleStart -lt 0) { throw 'The exact example-network start marker was not found.' }
$exampleEndMarker = '</SW.Blocks.CompileUnit>'
$exampleEnd = $export.xml.IndexOf($exampleEndMarker, $exampleStart, [StringComparison]::Ordinal)
if ($exampleEnd -lt 0) { throw 'The exact example-network end marker was not found.' }
$exampleEnd += $exampleEndMarker.Length
$findXml = $export.xml.Substring($exampleStart, $exampleEnd - $exampleStart)

$allIdValues = @($document.SelectNodes('//*[@ID]') | ForEach-Object { [Convert]::ToInt32($_.GetAttribute('ID'), 16) })
$allUidValues = @($document.SelectNodes('//*[@UId]') | ForEach-Object { [int]$_.GetAttribute('UId') })
$nextId = (($allIdValues | Measure-Object -Maximum).Maximum) + 1
$nextUid = (($allUidValues | Measure-Object -Maximum).Maximum) + 1

$replacementBuilder = [Text.StringBuilder]::new($findXml)
for ($sensor = 2; $sensor -le 32; $sensor++) {
    $clone = $example.CloneNode($true)
    $idMap = @{}
    foreach ($node in @($clone.SelectNodes('.//*[@ID]')) + @($clone | Where-Object { $_.HasAttribute('ID') })) {
        $old = $node.GetAttribute('ID')
        if (-not $idMap.ContainsKey($old)) { $idMap[$old] = $nextId; $nextId++ }
        $node.SetAttribute('ID', ([int]$idMap[$old]).ToString('X'))
    }
    $uidMap = @{}
    foreach ($node in @($clone.SelectNodes('.//*[@UId]'))) {
        $old = $node.GetAttribute('UId')
        if (-not $uidMap.ContainsKey($old)) { $uidMap[$old] = $nextUid; $nextUid++ }
        $node.SetAttribute('UId', [string]$uidMap[$old])
    }

    foreach ($component in @($clone.SelectNodes(".//*[local-name()='Component']"))) {
        $name = $component.GetAttribute('Name')
        if ($name -eq 'AI1') { $component.SetAttribute('Name', "AI$sensor") }
        elseif ($name -eq 'S1') { $component.SetAttribute('Name', "S$sensor") }
        elseif ($name -eq 'ALM_HighAlm_S1') { $component.SetAttribute('Name', "ALM_HighAlm_S$sensor") }
        elseif ($name -eq 'ALM_LowAlm_S1') { $component.SetAttribute('Name', "ALM_LowAlm_S$sensor") }
        elseif ($name -eq 'ALM_SenFault_S1') { $component.SetAttribute('Name', "ALM_SenFault_S$sensor") }
    }
    $title = $clone.SelectSingleNode(".//*[local-name()='MultilingualText' and @CompositionName='Title']//*[local-name()='Text']")
    if ($title) { $title.InnerText = "S$sensor" }
    [void]$replacementBuilder.Append($clone.OuterXml)
    [void]$example.ParentNode.InsertBefore($clone, $units[4])
}

$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = [Text.UTF8Encoding]::new($false)
$settings.Indent = $false
$builder = [Text.StringBuilder]::new()
$writer = [Xml.XmlWriter]::Create($builder, $settings)
$document.Save($writer)
$writer.Dispose()
$candidateXml = $builder.ToString()

$previewArgs = $target.Clone()
$previewArgs.baselineHash = $export.baselineHash
$previewArgs.xml = $candidateXml
$preview = Invoke-Mcp 2 'tia_preview_block_change' $previewArgs

[pscustomobject]@{
    baselineHash = $export.baselineHash
    originalNetworks = $units.Count
    proposedNetworks = 39
    addedNetworks = 31
    preview = $preview
    candidateXml = $candidateXml
    findXml = $findXml
    replacementXml = $replacementBuilder.ToString()
}
