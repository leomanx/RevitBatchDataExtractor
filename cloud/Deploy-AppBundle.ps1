<#
.SYNOPSIS
  Registers the RBDEAutomation AppBundle and RBDEExtract Activity on APS Design Automation (us-east).

.DESCRIPTION
  1. Compiles and packages RBDE.Automation.zip (via Build-AppBundle.ps1).
  2. Creates or increments AppBundle 'RBDEAutomation' for Revit 2026.
  3. Uploads the zip package to the presigned S3 endpoint provided by DA.
  4. Points the 'prod' alias to the uploaded version.
  5. Creates or updates Activity 'RBDEExtract' and points its 'prod' alias.

  This configures cloud infrastructure and does NOT consume cloud processing credits.
#>
[CmdletBinding()]
param(
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

# 1. Package AppBundle zip
$buildScript = Join-Path $PSScriptRoot 'Build-AppBundle.ps1'
Write-Host "--- 1. Packaging AppBundle ---"
& pwsh -NoProfile -File $buildScript
$zipPath = Join-Path $PSScriptRoot 'dist\RBDE.Automation.zip'
if (-not (Test-Path $zipPath)) { throw "AppBundle zip not found at $zipPath" }

# 2. Get 2LO Token
Write-Host "`n--- 2. Requesting 2LO Token ---"
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${clientId}:${clientSecret}"))
$token = (Invoke-RestMethod -Method Post -Uri "$base/authentication/v2/token" `
    -Headers @{ Authorization = "Basic $basic" } `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; scope = 'code:all data:read' }).access_token

$headers = @{ Authorization = "Bearer $token"; 'Content-Type' = 'application/json' }
$nickname = (Invoke-RestMethod -Method Get -Uri "$base/da/$Region/v3/forgeapps/me" -Headers $headers)
Write-Host "DA Nickname: $nickname"

# 3. Create or Update AppBundle
$bundleId = 'RBDEAutomation'
Write-Host "`n--- 3. Registering AppBundle '$bundleId' ---"
$bundleSpec = @{
    id = $bundleId
    engine = 'Autodesk.Revit+2026'
    description = 'Headless Revit Batch Data Extractor'
}

$bundleResp = $null
try {
    # Try creating new version
    $bundleResp = Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/appbundles" `
        -Headers $headers -Body ($bundleSpec | ConvertTo-Json)
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -eq 409) {
        Write-Host "AppBundle already exists; creating a new version..."
        $bundleResp = Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/appbundles/$bundleId/versions" `
            -Headers $headers -Body (@{ engine = 'Autodesk.Revit+2026'; description = 'Updated version' } | ConvertTo-Json)
    } else {
        throw $_
    }
}

$version = $bundleResp.version
Write-Host "AppBundle version: $version"

# 4. Upload Zip to S3 via uploadParameters
Write-Host "`n--- 4. Uploading AppBundle Package ($([math]::Round((Get-Item $zipPath).Length/1KB, 1)) KB) ---"
$uploadParams = $bundleResp.uploadParameters
$form = [System.Collections.Specialized.OrderedDictionary]::new()
foreach ($prop in $uploadParams.formData.psobject.Properties) {
    $form[$prop.Name] = $prop.Value
}
$form['file'] = Get-Item $zipPath
$uploadResult = Invoke-RestMethod -Method Post -Uri $uploadParams.endpointURL -Form $form
Write-Host "[OK] Package uploaded successfully to DA storage."

# 5. Set AppBundle Alias 'prod'
Write-Host "`n--- 5. Configuring AppBundle Alias 'prod' -> Version $version ---"
try {
    Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/appbundles/$bundleId/aliases" `
        -Headers $headers -Body (@{ id = 'prod'; version = $version } | ConvertTo-Json) | Out-Null
    Write-Host "[OK] Created alias 'prod'."
}
catch {
    Invoke-RestMethod -Method Patch -Uri "$base/da/$Region/v3/appbundles/$bundleId/aliases/prod" `
        -Headers $headers -Body (@{ version = $version } | ConvertTo-Json) | Out-Null
    Write-Host "[OK] Updated alias 'prod' to version $version."
}

# 6. Create or Update Activity 'RBDEExtract'
$actId = 'RBDEExtract'
Write-Host "`n--- 6. Registering Activity '$actId' ---"
$actSpec = @{
    id = $actId
    engine = 'Autodesk.Revit+2026'
    appbundles = @("$nickname.$bundleId+prod")
    commandLine = @('$(engine.path)\\revitcoreconsole.exe /al "$(appbundles[' + $bundleId + '].path)"')
    parameters = @{
        inputRvt = @{
            verb = 'get'
            localName = 'input.rvt'
            required = $true
            description = 'Revit 2026 model'
        }
        params = @{
            verb = 'get'
            localName = 'params.json'
            required = $false
            description = 'Extraction settings'
        }
        result = @{
            verb = 'put'
            localName = 'result'
            zip = $true
            required = $true
            description = 'Zipped result folder'
        }
    }
    description = 'Revit Batch Data Extractor - headless extraction'
}

$actResp = $null
try {
    $actResp = Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/activities" `
        -Headers $headers -Body ($actSpec | ConvertTo-Json -Depth 5)
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    if ($status -eq 409) {
        Write-Host "Activity already exists; creating a new version..."
        $actSpec.Remove('id')
        $actResp = Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/activities/$actId/versions" `
            -Headers $headers -Body ($actSpec | ConvertTo-Json -Depth 5)
    } else {
        throw $_
    }
}

$actVersion = $actResp.version
Write-Host "Activity version: $actVersion"

# 7. Set Activity Alias 'prod'
try {
    Invoke-RestMethod -Method Post -Uri "$base/da/$Region/v3/activities/$actId/aliases" `
        -Headers $headers -Body (@{ id = 'prod'; version = $actVersion } | ConvertTo-Json) | Out-Null
    Write-Host "[OK] Created Activity alias 'prod'."
}
catch {
    Invoke-RestMethod -Method Patch -Uri "$base/da/$Region/v3/activities/$actId/aliases/prod" `
        -Headers $headers -Body (@{ version = $actVersion } | ConvertTo-Json) | Out-Null
    Write-Host "[OK] Updated Activity alias 'prod' to version $actVersion."
}

Write-Host "`n[SUCCESS] AppBundle & Activity ready on APS Automation ($Region)!"
Write-Host "Activity Full ID: $nickname.$actId+prod"
