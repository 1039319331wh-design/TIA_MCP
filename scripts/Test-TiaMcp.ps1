[CmdletBinding()]
param(
    [string]$McpUrl = 'http://127.0.0.1:5111/mcp',
    [string]$Plc,
    [string]$BlockName,
    [string]$Group
)

$ErrorActionPreference = 'Stop'

function Invoke-McpRequest([int]$Id, [string]$Method, [hashtable]$Parameters) {
    $json = @{ jsonrpc = '2.0'; id = $Id; method = $Method; params = $Parameters } | ConvertTo-Json -Depth 12
    $body = [Text.Encoding]::UTF8.GetBytes($json)
    Invoke-RestMethod -Uri $McpUrl -Method Post -ContentType 'application/json; charset=utf-8' -Body $body -TimeoutSec 60
}

function Read-ToolResult($Response) {
    $text = ($Response.result.content | Where-Object type -eq 'text').text
    if ([string]::IsNullOrWhiteSpace($text)) {
        throw 'MCP tool returned no text content.'
    }
    Write-Output -NoEnumerate ($text | ConvertFrom-Json)
}

$tools = Invoke-McpRequest 1 'tools/list' @{}
$projectsResponse = Invoke-McpRequest 2 'tools/call' @{ name = 'tia_list_projects'; arguments = @{} }

if (-not $tools.result.tools -or $tools.result.tools.name -notcontains 'tia_status') {
    throw 'MCP tool discovery did not return tia_status.'
}
if ($tools.result.tools.name -notcontains 'tia_diagnostics') {
    throw 'MCP tool discovery did not return tia_diagnostics.'
}
foreach ($requiredTool in 'tia_list_tag_tables', 'tia_export_tag_table', 'tia_search_tag_table', 'tia_get_plc_overview', 'tia_get_tag_table_overview', 'tia_get_block_interface', 'tia_search_plc_blocks', 'tia_get_block_dependencies', 'tia_get_hardware_overview', 'tia_create_project_snapshot', 'tia_compare_project_snapshot', 'tia_list_data_blocks', 'tia_get_data_block_overview', 'tia_get_block_networks', 'tia_get_block_references', 'tia_audit_plc_io', 'tia_audit_symbol_usage', 'tia_prepare_scl_block', 'tia_apply_prepared_scl_block', 'tia_export_scl_block_source', 'tia_prepare_scl_block_variant') {
    if ($tools.result.tools.name -notcontains $requiredTool) { throw "MCP tool discovery did not return $requiredTool." }
}

$result = [ordered]@{
    ok = $true
    toolCount = @($tools.result.tools).Count
    projects = Read-ToolResult $projectsResponse
}

if (-not [string]::IsNullOrWhiteSpace($BlockName)) {
    if ([string]::IsNullOrWhiteSpace($Plc)) {
        throw '-Plc is required when -BlockName is supplied.'
    }

    $blockArguments = @{ plc = $Plc; name = $BlockName }
    if (-not [string]::IsNullOrWhiteSpace($Group)) {
        $blockArguments.group = $Group
    }

    $exportResponse = Invoke-McpRequest 3 'tools/call' @{ name = 'tia_export_block'; arguments = $blockArguments }
    $export = Read-ToolResult $exportResponse
    $previewArguments = $blockArguments.Clone()
    $previewArguments.baselineHash = $export.baselineHash
    $previewArguments.xml = $export.xml
    $previewResponse = Invoke-McpRequest 4 'tools/call' @{ name = 'tia_preview_block_change'; arguments = $previewArguments }
    $preview = Read-ToolResult $previewResponse

    if (-not $preview.valid -or $preview.changed -or $preview.writePerformed) {
        throw 'The unchanged-block safety preview returned an unexpected result.'
    }

    $result.preview = [ordered]@{
        plc = $preview.plc
        group = $preview.group
        block = $preview.name
        type = $preview.type
        baselineHash = $preview.baselineHash
        unchanged = -not $preview.changed
        writePerformed = $preview.writePerformed
    }
}

[pscustomobject]$result
