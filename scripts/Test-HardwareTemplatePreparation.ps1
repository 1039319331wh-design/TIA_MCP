param(
    [string]$BaseUrl = 'http://127.0.0.1:5111',
    [Parameter(Mandatory = $true)][string]$WorkbookPath,
    [string]$Token
)

$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($Token)) { $headers.Authorization = "Bearer $Token" }
$body = @{ workbookPath = (Resolve-Path -LiteralPath $WorkbookPath).Path } | ConvertTo-Json
$result = Invoke-RestMethod -Uri "$BaseUrl/api/hardware-template/prepare" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
if ([string]::IsNullOrWhiteSpace($result.changeId)) { throw 'Hardware template preparation did not return a changeId.' }
if ($result.writePerformed -ne $false) { throw 'Preparation unexpectedly reported a write.' }
$result | ConvertTo-Json -Depth 8
