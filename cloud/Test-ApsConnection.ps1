<#
.SYNOPSIS
  Read-only APS smoke test for the Batch Extractor app (2LO).

.DESCRIPTION
  1. Requests a 2-legged token (client_credentials) from APS Authentication v2.
  2. Lists Automation API engines and shows the available Revit engines.
  Creates nothing in the cloud and runs no WorkItems, so no cloud credits are used.

  Credentials come from APS_CLIENT_ID / APS_CLIENT_SECRET (process env first,
  then the user environment in the registry so a fresh `setx` works without
  restarting the shell). The secret is never printed.

.EXAMPLE
  .\cloud\Test-ApsConnection.ps1
  .\cloud\Test-ApsConnection.ps1 -Region eu-west
#>
[CmdletBinding()]
param(
    [ValidateSet('us-east', 'eu-west')]
    [string]$Region = 'us-east',
    [string]$Scope = 'code:all data:read'
)

$ErrorActionPreference = 'Stop'
$base = 'https://developer.api.autodesk.com'

function Get-ApsSetting([string]$Name) {
    $value = [Environment]::GetEnvironmentVariable($Name, 'Process')
    if (-not $value) { $value = [Environment]::GetEnvironmentVariable($Name, 'User') }
    if (-not $value) {
        $dpapiPath = Join-Path $env:LOCALAPPDATA 'MMK_BIM\RBDE\credentials.dpapi.json'
        if (Test-Path $dpapiPath) {
            $json = Get-Content $dpapiPath -Raw | ConvertFrom-Json
            if ($Name -eq 'APS_CLIENT_ID') { return $json.clientId.Trim() }
            if ($Name -eq 'APS_CLIENT_SECRET' -and $json.encryptedSecret) {
                Add-Type -AssemblyName System.Security
                $entropy = [System.Text.Encoding]::UTF8.GetBytes('RBDE.CloudApp.ApsSecret.v1')
                $cipher = [Convert]::FromBase64String($json.encryptedSecret)
                $plain = [System.Security.Cryptography.ProtectedData]::Unprotect($cipher, $entropy, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
                return [System.Text.Encoding]::UTF8.GetString($plain).Trim()
            }
        }
    }
    if (-not $value) { throw "$Name is not set. Configure it in RBDE.CloudApp Settings (DPAPI encrypted)." }
    return $value.Trim()
}

$clientId = Get-ApsSetting 'APS_CLIENT_ID'
$clientSecret = Get-ApsSetting 'APS_CLIENT_SECRET'
$masked = if ($clientId.Length -gt 8) { $clientId.Substring(0, 4) + '...' + $clientId.Substring($clientId.Length - 4) } else { '****' }
Write-Host "Client ID : $masked"
Write-Host "Scope     : $Scope"
Write-Host "Region    : $Region"

# --- 1. 2LO token -----------------------------------------------------------
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${clientId}:${clientSecret}"))
try {
    $token = Invoke-RestMethod -Method Post -Uri "$base/authentication/v2/token" `
        -Headers @{ Authorization = "Basic $basic"; Accept = 'application/json' } `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'client_credentials'; scope = $Scope }
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    Write-Host "[FAIL] Token request failed (HTTP $status): $($_.ErrorDetails.Message)" -ForegroundColor Red
    Write-Host '       Check Client ID/Secret and that the app is Server-to-Server.'
    exit 1
}
Write-Host "[OK]   2LO token issued, expires in $($token.expires_in)s" -ForegroundColor Green

# --- 2. Automation engines (read-only, paginated) ---------------------------
$headers = @{ Authorization = "Bearer $($token.access_token)" }
$engines = New-Object System.Collections.Generic.List[string]
$page = $null
try {
    do {
        $uri = "$base/da/$Region/v3/engines"
        if ($page) { $uri += "?page=$([Uri]::EscapeDataString($page))" }
        $resp = Invoke-RestMethod -Method Get -Uri $uri -Headers $headers
        $resp.data | ForEach-Object { $engines.Add($_) }
        $page = $resp.paginationToken
    } while ($page)
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    Write-Host "[FAIL] Automation engines request failed (HTTP $status): $($_.ErrorDetails.Message)" -ForegroundColor Red
    Write-Host '       Check that Automation API is enabled on the app.'
    exit 1
}

$revit = $engines | Where-Object { $_ -like 'Autodesk.Revit+*' } | Sort-Object
Write-Host "[OK]   Automation API reachable: $($engines.Count) engines, $(@($revit).Count) Revit" -ForegroundColor Green
$revit | ForEach-Object { Write-Host "       $_" }

# --- 3. Data Management: hubs and projects visible to this app (read-only) --
# Requires the Client ID to be added under ACC Account Admin > Custom Integrations.
try {
    $hubs = (Invoke-RestMethod -Method Get -Uri "$base/project/v1/hubs" -Headers $headers).data
}
catch {
    $status = $_.Exception.Response.StatusCode.value__
    Write-Host "[FAIL] Data Management hubs request failed (HTTP $status): $($_.ErrorDetails.Message)" -ForegroundColor Red
    Write-Host '       Check that Data Management API is enabled and the ACC Custom Integration is saved.'
    exit 1
}
if (-not $hubs) {
    Write-Host '[WARN] Data Management reachable but no hubs visible. Add the Client ID in ACC Custom Integrations.' -ForegroundColor Yellow
}
else {
    Write-Host "[OK]   Data Management: $(@($hubs).Count) hub(s) visible" -ForegroundColor Green
    foreach ($hub in $hubs) {
        Write-Host "       Hub: $($hub.attributes.name)  [$($hub.attributes.extension.type -replace '^hubs:autodesk\.', '')] region=$($hub.attributes.region)"
        $projects = New-Object System.Collections.Generic.List[object]
        $next = "$base/project/v1/hubs/$($hub.id)/projects"
        try {
            while ($next) {
                $resp = Invoke-RestMethod -Method Get -Uri $next -Headers $headers
                $resp.data | ForEach-Object { $projects.Add($_) }
                $next = $resp.links.next.href
            }
            Write-Host "         $($projects.Count) project(s)"
            $projects | Sort-Object { $_.attributes.name } | ForEach-Object { Write-Host "         - $($_.attributes.name)" }
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            Write-Host "         [FAIL] projects request failed (HTTP $status): $($_.ErrorDetails.Message)" -ForegroundColor Red
        }
    }
}

Write-Host ''
Write-Host 'Not verified here: folder/file access inside projects, OSS buckets, AppBundle/Activity/WorkItem.'
