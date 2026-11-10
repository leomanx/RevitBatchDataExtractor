<#
.SYNOPSIS
  Builds the headless Automation add-in and packages it as an APS AppBundle zip.

.DESCRIPTION
  Output: cloud\dist\RBDE.Automation.zip with the layout Design Automation expects:
    RBDE.Automation.bundle\PackageContents.xml
    RBDE.Automation.bundle\Contents\RBDE.Automation.addin + DLLs
  Local only: does not contact APS and uses no cloud credits.
#>
[CmdletBinding()]
param([string]$RevitPath = 'C:\Program Files\Autodesk\Revit 2026')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Automation\Automation.csproj'
$output = Join-Path $root 'src\Automation\bin\Release\net8.0-windows'
$dist = Join-Path $PSScriptRoot 'dist'
$bundle = Join-Path $dist 'RBDE.Automation.bundle'
$zip = Join-Path $dist 'RBDE.Automation.zip'

dotnet build $project -c Release -nologo "-p:RevitPath=$RevitPath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed; AppBundle was not created.' }

if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
$contents = New-Item -ItemType Directory -Force -Path (Join-Path $bundle 'Contents')
Get-ChildItem $output -File | Where-Object { $_.Extension -in '.dll', '.addin', '.json' } |
    Copy-Item -Destination $contents

# Revit itself provides RevitAPI; it must never be shipped in the bundle.
if (Test-Path (Join-Path $contents 'RevitAPI.dll')) { throw 'RevitAPI.dll must not be packaged.' }

@'
<?xml version="1.0" encoding="utf-8"?>
<ApplicationPackage SchemaVersion="1.0" Version="1.0" ProductCode="{8C3F2A51-6D4E-4B9A-A1F7-2E5B9C0D7F13}"
    Name="RBDE Automation" Description="Headless Revit Batch Data Extractor" AutodeskProduct="Revit" ProductType="Application">
  <CompanyDetails Name="RBDE" />
  <Components Description="RBDE Automation for Revit 2026">
    <RuntimeRequirements OS="Win64" Platform="Revit" SeriesMin="R2026" SeriesMax="R2026" />
    <ComponentEntry AppName="RBDE Automation" Version="1.0" ModuleName="./Contents/RBDE.Automation.addin"
        AppDescription="Extracts elements and parameters to SQLite/CSV" LoadOnCommandInvocation="False" LoadOnRevitStartup="True" />
  </Components>
</ApplicationPackage>
'@ | Set-Content -LiteralPath (Join-Path $bundle 'PackageContents.xml') -Encoding utf8

Compress-Archive -Path $bundle -DestinationPath $zip
$size = [math]::Round((Get-Item $zip).Length / 1KB, 1)
Write-Host "AppBundle: $zip ($size KB)"
Get-ChildItem $contents | ForEach-Object { Write-Host "  Contents\$($_.Name)" }
