param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
Get-Content -LiteralPath (Join-Path $RunDirectory 'progress.log') -Wait
