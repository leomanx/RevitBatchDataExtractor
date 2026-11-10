param(
    [Parameter(Mandatory)][string]$ModelsFolder,
    [string]$ConfigPath = (Join-Path $PSScriptRoot 'batch.json'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'output'),
    [switch]$Recurse
)
$ErrorActionPreference = 'Stop'
if (Test-Path -LiteralPath $ConfigPath) { throw "Config already exists: $ConfigPath. Choose a new path to preserve existing settings." }
$models = @(Get-ChildItem -LiteralPath $ModelsFolder -Filter '*.rvt' -File -Recurse:$Recurse |
    Where-Object { $_.Name -notmatch '\.\d{4}\.rvt$' } | Sort-Object FullName | ForEach-Object FullName)
if ($models.Count -eq 0) { throw 'No RVT models found.' }
$config = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'config.example.json') -Raw | ConvertFrom-Json
$config.models = $models
$config.outputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$config | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ConfigPath -Encoding utf8
Write-Host "Created $ConfigPath with $($models.Count) models."
