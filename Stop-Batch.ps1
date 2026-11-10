param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath (Join-Path $RunDirectory 'config.snapshot.json'))) { throw 'Not a batch output directory.' }
New-Item -ItemType File -Path (Join-Path $RunDirectory 'STOP.txt') -Force | Out-Null
Write-Host 'Stop requested. Revit will stop at the next safe checkpoint after the current API call returns.'
