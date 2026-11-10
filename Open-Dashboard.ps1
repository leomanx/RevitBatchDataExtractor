param([Parameter(Mandatory)][string]$RunDirectory)
$ErrorActionPreference = 'Stop'
$database = Join-Path ([System.IO.Path]::GetFullPath($RunDirectory)) 'extract.sqlite'
if (-not (Test-Path -LiteralPath $database)) { throw "No extract.sqlite found in $RunDirectory. Run a batch with enableDashboard or exportSqlite enabled." }

$projPath = Join-Path $PSScriptRoot 'src\Dashboard\Dashboard.csproj'
$executable = Join-Path $PSScriptRoot 'src\Dashboard\bin\Release\net8.0\RBDE.Dashboard.exe'
$srcHtml = Join-Path $PSScriptRoot 'src\Dashboard\index.html'
$srcProg = Join-Path $PSScriptRoot 'src\Dashboard\Program.cs'

$needsBuild = -not (Test-Path -LiteralPath $executable)
if (-not $needsBuild) {
    $exeTime = (Get-Item $executable).LastWriteTimeUtc
    if ((Get-Item $srcHtml).LastWriteTimeUtc -gt $exeTime -or (Get-Item $srcProg).LastWriteTimeUtc -gt $exeTime) {
        $needsBuild = $true
    }
}

if ($needsBuild) {
    Stop-Process -Name "RBDE.Dashboard" -Force -ErrorAction SilentlyContinue
    dotnet build $projPath -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed.' }
}
Start-Process -FilePath $executable -ArgumentList ('"' + $database + '"') -WindowStyle Hidden
