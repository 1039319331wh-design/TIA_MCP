[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$Plc,
    [string]$McpUrl = 'http://127.0.0.1:5111/mcp',
    [string]$Name = 'Codex_Preparation_Test'
)

$ErrorActionPreference = 'Stop'

function Invoke-McpTool([int]$Id, [string]$ToolName, [hashtable]$Arguments) {
    $json = @{ jsonrpc = '2.0'; id = $Id; method = 'tools/call'; params = @{ name = $ToolName; arguments = $Arguments } } | ConvertTo-Json -Depth 12
    $response = Invoke-RestMethod -Uri $McpUrl -Method Post -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 60
    if ($response.error) { throw $response.error.message }
    (($response.result.content | Where-Object type -eq 'text').text | ConvertFrom-Json)
}

$source = @"
FUNCTION_BLOCK "$Name"
VAR_INPUT
    Enable : Bool;
END_VAR
VAR_OUTPUT
    Active : Bool;
END_VAR
BEGIN
    Active := Enable;
END_FUNCTION_BLOCK
"@

$result = Invoke-McpTool 1 'tia_prepare_scl_block' @{ plc = $Plc; blockType = 'FB'; name = $Name; source = $source }
if ($result.writePerformed -or $result.blockType -ne 'FB' -or $result.name -ne $Name -or -not $result.changeId) {
    throw 'SCL preparation returned an invalid result.'
}
$result | ConvertTo-Json -Depth 12
