<#
.SYNOPSIS
  Submits a headless extraction WorkItem to APS Design Automation for Revit 2026.

.DESCRIPTION
  1. Resolves the S3 download URL for the target RVT in ACC.
  2. Builds and uploads params.json with filtered categories and parameters.
  3. Prepares a direct S3 upload target in the app's transient OSS bucket.
  4. Submits the WorkItem to Activity: {NICKNAME}.RBDEExtract+prod.
  5. Polls until completion, completes the S3 upload, downloads and unpacks the results.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectId,
    [Parameter(Mandatory)][string]$ItemId,
    [string[]]$Categories = @(
        'Floors', 'Doors', 'Windows', 'Walls',
        'Curtain Panels', 'Curtain Wall Mullions',
        'Furniture', 'Furniture Systems', 'Rooms', 'Ceilings'
    ),
    [string[]]$Parameters = @('Type Mark', 'Type Comments', 'Comments'),
    [ValidateSet('us-east', 'eu-west')]
    [string]$Region = 'us-east'
)

$ErrorActionPreference = 'Stop'
$base = 'https://developer.api.autodesk.com'

function Get-ApsSetting([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if (-not $value) { $value = [Environment]::GetEnvironmentVariable($Name, 'User') }
    if (-not $value) { throw "$Name is not set." }
    return $value.Trim()
}

$clientId = Get-ApsSetting 'APS_CLIENT_ID'
$clientSecret = Get-ApsSetting 'APS_CLIENT_SECRET'

Write-Host "============================================================"
Write-Host "  APS Automation WorkItem: Revit Batch Data Extractor"
Write-Host "============================================================"
Write-Host "Categories : $($Categories -join ', ')"
Write-Host "Parameters : $($Parameters -join ', ')"
Write-Host "Region     : $Region"

# --- 1. Token ---
Write-Host "`n[1/6] Requesting 2LO Token..."
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${clientId}:${clientSecret}"))
$token = (Invoke-RestMethod -Method Post -Uri "$base/authentication/v2/token" `
    -Headers @{ Authorization = "Basic $basic" } `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; scope = 'data:read data:write bucket:create bucket:read code:all' }).access_token

$headers = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }
$nickname = Invoke-RestMethod -Method Get -Uri "$base/da/$Region/v3/forgeapps/me" -Headers $headers
$bucketKey = "rbde-output-" + ($clientId.ToLower() -replace '[^a-z0-9]', '').Substring(0, 16)

# --- 2. ACC Model Download URL ---
Write-Host "[2/6] Resolving model download URL from ACC..."
$item = Invoke-RestMethod -Method Get -Uri "$base/data/v1/projects/$ProjectId/items/$ItemId" -Headers $headers
$tipVersionId = [Uri]::EscapeDataString($item.data.relationships.tip.data.id)
$version = Invoke-RestMethod -Method Get -Uri "$base/data/v1/projects/$ProjectId/versions/$tipVersionId" -Headers $headers
$storageId = $version.data.relationships.storage.data.id

if ($storageId -match 'urn:adsk.objects:os.object:([^/]+)/(.+)') {
    $srcBucket = $matches[1]
    $srcObject = [Uri]::EscapeDataString($matches[2])
    # Request 60 minutes lifetime so the URL does not expire while waiting in the cloud queue.
    $rvtDownload = (Invoke-RestMethod -Method Get -Uri "$base/oss/v2/buckets/$srcBucket/objects/$srcObject/signeds3download?minutesExpiration=60" -Headers $headers).url
} else {
    throw "Unexpected storageId format: $storageId"
}
Write-Host "[OK]  Model: $($item.data.attributes.displayName) ($([math]::Round($version.data.attributes.fileSize/1MB, 2)) MB)"

# --- 3. Prepare params.json ---
Write-Host "[3/6] Preparing extraction config (params.json)..."
$paramsConfig = @{
    categories = $Categories
    parameters = $Parameters
    includeTypes = $true
    exportCsv = $true
    exportSqlite = $true
    csvSafeForExcel = $true
} | ConvertTo-Json -Depth 5 -Compress

# Pass params as an inline data: URI so it never expires while in queue.
$paramsDownloadUrl = "data:application/json," + [Uri]::EscapeDataString($paramsConfig)
Write-Host "[OK]  params.json prepared as inline data URI."

# --- 4. Prepare Result Upload Target ---
Write-Host "[4/6] Preparing result upload slot..."
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultObjectKey = "result-$timestamp.zip"
$resultUpload = Invoke-RestMethod -Method Get -Uri "$base/oss/v2/buckets/$bucketKey/objects/$resultObjectKey/signeds3upload?minutesExpiration=60" -Headers $headers
$resultUploadUrl = $resultUpload.urls[0]
$resultUploadKey = $resultUpload.uploadKey

# --- 5. Submit WorkItem ---
Write-Host "[5/6] Submitting WorkItem to APS Design Automation..."
$activityFullId = "$nickname.RBDEExtract+prod"
$workItemPayload = @{
    activityId = $activityFullId
    arguments = @{
        inputRvt = @{
            url = $rvtDownload
        }
        params = @{
            url = $paramsDownloadUrl
        }
        result = @{
            verb = 'put'
            url = $resultUploadUrl
        }
    }
} | ConvertTo-Json -Depth 5

$wi = Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/workitems" -Headers $headers -Body $workItemPayload
$wiId = $wi.id
Write-Host "[OK]  WorkItem submitted!"
Write-Host "      WorkItem ID : $wiId"
Write-Host "      Status      : $($wi.status)"

# --- 6. Polling ---
Write-Host "`n[6/6] Waiting for cloud execution..."
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$terminal = @('success', 'failedLimitDataSize', 'failedDownload', 'failedInstructions', 'failedUpload', 'cancelled')
$status = $wi.status

while ($status -notin $terminal) {
    Start-Sleep -Seconds 12
    $wiPoll = Invoke-RestMethod -Method Get -Uri "$base/da/$Region/v3/workitems/$wiId" -Headers $headers
    $status = $wiPoll.status
    $elapsed = $sw.Elapsed.ToString('mm\:ss')
    $extra = if ($wiPoll.stats.timeDownloadStarted) { "(running in Revit engine)" }
             elseif ($wiPoll.approximateQueuePosition) { "(Queue pos: $($wiPoll.approximateQueuePosition))" }
             else { "" }
    Write-Host "[$elapsed] Status: $status $extra"
}

$sw.Stop()
Write-Host "`nFinal Status: $status (Elapsed: $($sw.Elapsed.ToString('mm\:ss')))"
Write-Host "Report URL  : $($wiPoll.reportUrl)"

if ($status -ne 'success') {
    Write-Host "`n--- Cloud Execution Report ---" -ForegroundColor Red
    try {
        $report = (Invoke-RestMethod -Method Get -Uri $wiPoll.reportUrl)
        Write-Host $report
    } catch {
        Write-Host "Could not fetch report: $($_.Exception.Message)"
    }
    throw "WorkItem ended with status: $status"
}

# --- Download Result ---
Write-Host "`n--- Finalizing and Downloading Results ---"
Invoke-RestMethod -Method Post -Uri "$base/oss/v2/buckets/$bucketKey/objects/$resultObjectKey/signeds3upload" `
    -Headers $headers -Body (@{ uploadKey = $resultUploadKey } | ConvertTo-Json) | Out-Null

$resultDownload = Invoke-RestMethod -Method Get -Uri "$base/oss/v2/buckets/$bucketKey/objects/$resultObjectKey/signeds3download" -Headers $headers
$outputDir = Join-Path (Split-Path $PSScriptRoot -Parent) "output\aps-$timestamp"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$localZip = Join-Path $outputDir 'result.zip'

Write-Host "Downloading to: $localZip..."
Invoke-WebRequest -Method Get -Uri $resultDownload.url -OutFile $localZip
Expand-Archive -Path $localZip -DestinationPath $outputDir -Force

Write-Host "`n[SUCCESS] Extraction completed and unpacked to:"
Write-Host "Folder: $outputDir"
Get-ChildItem $outputDir | ForEach-Object { Write-Host "  $($_.Name) ($([math]::Round($_.Length/1KB, 1)) KB)" }

if (Test-Path (Join-Path $outputDir 'summary.json')) {
    Write-Host "`nSummary:"
    Get-Content (Join-Path $outputDir 'summary.json')
}
