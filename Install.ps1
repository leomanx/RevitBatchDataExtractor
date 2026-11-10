param([string]$RevitPath = 'C:\Program Files\Autodesk\Revit 2026')
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\Revit\Revit.csproj'
dotnet build $project -c Release "-p:RevitPath=$RevitPath"
if ($LASTEXITCODE -ne 0) { throw 'Build failed; add-in was not installed.' }
$dashboardProject = Join-Path $PSScriptRoot 'src\Dashboard\Dashboard.csproj'
$dashboardOutput = Join-Path $PSScriptRoot 'src\Revit\bin\Release\net8.0-windows\Dashboard'
dotnet publish $dashboardProject -c Release -r win-x64 --self-contained false -o $dashboardOutput
if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed; add-in was not installed.' }
$source = Join-Path $PSScriptRoot 'src\Revit\bin\Release\net8.0-windows'
$destination = Join-Path $env:LOCALAPPDATA 'RevitBatchDataExtractor\2026'
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destination -Recurse -Force
$assembly = [System.Security.SecurityElement]::Escape((Join-Path $destination 'RevitBatchDataExtractor.dll'))
$addinDirectory = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026'
New-Item -ItemType Directory -Force -Path $addinDirectory | Out-Null
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Command">
    <Name>Revit Batch Data Extractor</Name>
    <Assembly>$assembly</Assembly>
    <AddInId>F936265C-272D-4EAD-A925-BD214108B132</AddInId>
    <FullClassName>RevitBatchDataExtractor.Command</FullClassName>
    <Text>Revit Batch Data Extractor</Text>
    <VendorId>RBDE</VendorId>
    <VendorDescription>Batch RVT data extraction</VendorDescription>
    <AvailabilityClassName>RevitBatchDataExtractor.Availability</AvailabilityClassName>
  </AddIn>
</RevitAddIns>
"@
$manifest | Set-Content -LiteralPath (Join-Path $addinDirectory 'RevitBatchDataExtractor.addin') -Encoding utf8
Write-Host "Installed. Restart Revit 2026, then Add-Ins > External Tools > Revit Batch Data Extractor."
