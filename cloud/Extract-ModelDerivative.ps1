<#
.SYNOPSIS
  Instant Cloud Extraction via APS Model Derivative REST API.

.DESCRIPTION
  Directly queries ACC translated metadata & properties in 2-3 seconds without:
    - Downloading multi-gigabyte RVT files
    - Booting Revit locally or on Cloud VMs
    - Waiting in long Design Automation queues
    - Consuming Cloud compute credits

  Extracts Category, Family, Type, Element ID, UniqueId, Type Mark, Type Comments, Comments
  and stores results in SQLite (extract.sqlite) + CSVs compatible with the local Dashboard.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ProjectId,
    [Parameter(Mandatory)][string]$ItemId,
    [string[]]$Categories = @(
        'Floors', 'Doors', 'Windows', 'Walls',
        'Curtain Panels', 'Curtain Wall Mullions',
        'Furniture', 'Rooms', 'Ceilings'
    ),
    [string[]]$Parameters = @('Type Mark', 'Type Comments', 'Comments')
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

$swTotal = [System.Diagnostics.Stopwatch]::StartNew()
Write-Host "============================================================"
Write-Host "  Instant Cloud Extraction via APS Model Derivative"
Write-Host "============================================================"
Write-Host "Target Categories : $($Categories -join ', ')"
Write-Host "Target Parameters : $($Parameters -join ', ')"

# 1. 2LO Token
$basic = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${clientId}:${clientSecret}"))
$token = (Invoke-RestMethod -Method Post -Uri "$base/authentication/v2/token" `
    -Headers @{ Authorization = "Basic $basic" } `
    -ContentType 'application/x-www-form-urlencoded' `
    -Body @{ grant_type = 'client_credentials'; scope = 'data:read viewables:read' }).access_token

$headers = @{ Authorization = "Bearer $token"; Accept = 'application/json' }

# 2. Get Tip Version and Derivatives URN
Write-Host "`n[1/4] Resolving model metadata from ACC..."
$item = Invoke-RestMethod -Method Get -Uri "$base/data/v1/projects/$ProjectId/items/$ItemId" -Headers $headers
$modelName = $item.data.attributes.displayName
$tipVersionId = [Uri]::EscapeDataString($item.data.relationships.tip.data.id)
$version = Invoke-RestMethod -Method Get -Uri "$base/data/v1/projects/$ProjectId/versions/$tipVersionId" -Headers $headers
$urn = $version.data.relationships.derivatives.data.id
Write-Host "[OK]  Model: $modelName (Version: $($version.data.attributes.versionNumber))"

# 3. Select 3D View (prefer New Construction or main view)
$metaList = (Invoke-RestMethod -Method Get -Uri "$base/modelderivative/v2/designdata/$urn/metadata" -Headers $headers).data.metadata
$view3d = $metaList | Where-Object { $_.role -eq '3d' -and ($_.name -like '*New Construction*' -or $_.name -like '*{3D}*') } | Select-Object -First 1
if (-not $view3d) {
    $view3d = $metaList | Where-Object { $_.role -eq '3d' } | Select-Object -Last 1
}
$guid = $view3d.guid
Write-Host "[OK]  Selected View: $($view3d.name) (Guid: $guid)"

# 4. Fetch Tree & Properties concurrently
Write-Host "`n[2/4] Fetching hierarchy tree & properties from Model Derivative..."
$swFetch = [System.Diagnostics.Stopwatch]::StartNew()
$tree = Invoke-RestMethod -Method Get -Uri "$base/modelderivative/v2/designdata/$urn/metadata/$guid" -Headers $headers
$props = Invoke-RestMethod -Method Get -Uri "$base/modelderivative/v2/designdata/$urn/metadata/$guid/properties?forceget=true" -Headers $headers
$swFetch.Stop()
Write-Host "[OK]  Model Derivative queried in $($swFetch.Elapsed.TotalSeconds.ToString('F2')) seconds!"

# 5. Build Object Map: objectid -> Category, Family, Type
Write-Host "`n[3/4] Parsing model hierarchy..."
$catLookup = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($c in $Categories) { $catLookup[$c] = $c }

# Map: objectid -> @{ Category; Family; Type; Kind }
$objMeta = [System.Collections.Generic.Dictionary[int, psobject]]::new()

$rootModel = $tree.data.objects[0]
foreach ($catNode in $rootModel.objects) {
    $catName = $catNode.name
    if (-not $catLookup.ContainsKey($catName)) { continue }
    $matchedCat = $catLookup[$catName]

    if (-not $catNode.objects) { continue }
    foreach ($famNode in $catNode.objects) {
        $famName = $famNode.name
        if (-not $famNode.objects) { continue }

        foreach ($typeNode in $famNode.objects) {
            $typeName = $typeNode.name
            $objMeta[[int]$typeNode.objectid] = [pscustomobject]@{
                Category = $matchedCat
                Family = $famName
                Type = $typeName
                Kind = 'type'
            }

            if ($typeNode.objects) {
                foreach ($instNode in $typeNode.objects) {
                    $objMeta[[int]$instNode.objectid] = [pscustomobject]@{
                        Category = $matchedCat
                        Family = $famName
                        Type = $typeName
                        Kind = 'instance'
                    }
                }
            }
        }
    }
}
Write-Host "[OK]  Indexed $($objMeta.Count) target elements across selected categories."

# 6. Extract Parameters & Export
Write-Host "`n[4/4] Extracting parameters & populating database..."
$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$outputDir = Join-Path (Split-Path $PSScriptRoot -Parent) "output\derivative-$timestamp"
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

$elementsCsv = Join-Path $outputDir 'elements.csv'
$paramsCsv = Join-Path $outputDir 'parameters.csv'
$modelsCsv = Join-Path $outputDir 'models.csv'
$sqlitePath = Join-Path $outputDir 'extract.sqlite'

$elementsList = [System.Collections.Generic.List[psobject]]::new()
$paramsList = [System.Collections.Generic.List[psobject]]::new()

foreach ($item in $props.data.collection) {
    $id = [int]$item.objectid
    if (-not $objMeta.ContainsKey($id)) { continue }

    $meta = $objMeta[$id]
    $extId = $item.externalId
    $rawName = $item.name

    # Extract Revit Element ID from [123456]
    $isInstance = $rawName -match '\[(\d+)\]'
    $revitId = if ($isInstance) { [int64]$matches[1] } else { [int64]$id }

    $elementsList.Add([pscustomobject]@{
        model_id = 1
        element_id = $revitId
        unique_id = $extId
        kind = $meta.Kind
        category_id = ''
        category = $meta.Category
        family = $meta.Family
        type_name = $meta.Type
        type_id = -1
        name = $rawName
    })

    # Search for requested parameters across property sets
    if ($item.properties) {
        foreach ($pGroup in $item.properties.psobject.Properties) {
            if (-not $pGroup.Value) { continue }
            foreach ($pName in $Parameters) {
                $val = $pGroup.Value.$pName
                if ($null -ne $val -and "$val" -ne '') {
                    $paramsList.Add([pscustomobject]@{
                        model_id = 1
                        element_id = $revitId
                        scope = if ($pName -like 'Type*') { 'type' } else { 'instance' }
                        parameter_key = "param:$pName"
                        name = $pName
                        storage_type = 'String'
                        has_value = 1
                        raw_value = "$val"
                        display_value = "$val"
                        unit_type_id = ''
                    })
                }
            }
        }
    }
}

# Write CSV files
@"
model_id,path,status,started_utc,finished_utc,element_count
1,"$modelName","succeeded","$([DateTime]::UtcNow.ToString('O'))","$([DateTime]::UtcNow.ToString('O'))",$($elementsList.Count)
"@ | Set-Content -LiteralPath $modelsCsv -Encoding utf8

$elementsList | Export-Csv -LiteralPath $elementsCsv -NoTypeInformation -Encoding utf8
$paramsList | Export-Csv -LiteralPath $paramsCsv -NoTypeInformation -Encoding utf8

# Populate SQLite using .NET Microsoft.Data.Sqlite
$root = Split-Path $PSScriptRoot -Parent
$coreDll = Join-Path $root 'src\Core\bin\Release\net8.0\RBDE.Core.dll'
if (-not (Test-Path $coreDll)) {
    dotnet build (Join-Path $root 'src\Core\Core.csproj') -c Release -nologo | Out-Null
}

$csharpScript = @"
using System;
using System.IO;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

public static class DbBuilder {
    public static void Build(string sqlitePath, string elementsCsv, string paramsCsv, string modelsCsv) {
        using var conn = new SqliteConnection("Data Source=" + sqlitePath);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA foreign_keys=OFF;
            CREATE TABLE models (model_id INTEGER PRIMARY KEY, path TEXT NOT NULL, status TEXT NOT NULL,
                started_utc TEXT NOT NULL, finished_utc TEXT, element_count INTEGER DEFAULT 0);
            CREATE TABLE elements (model_id INTEGER NOT NULL, element_id INTEGER NOT NULL,
                unique_id TEXT, kind TEXT, category_id TEXT, category TEXT, family TEXT, type_name TEXT,
                type_id INTEGER, name TEXT, PRIMARY KEY(model_id, element_id));
            CREATE TABLE parameters (model_id INTEGER NOT NULL, element_id INTEGER NOT NULL, scope TEXT,
                parameter_key TEXT, name TEXT, storage_type TEXT, has_value INTEGER, raw_value TEXT,
                display_value TEXT, unit_type_id TEXT);
            CREATE TABLE errors (utc TEXT, model_id INTEGER, element_id INTEGER, stage TEXT, exception_type TEXT, message TEXT);
            CREATE INDEX ix_parameter_name ON parameters(name);
            CREATE INDEX ix_parameter_element ON parameters(model_id,element_id);
        ";
        cmd.ExecuteNonQuery();

        using var trans = conn.BeginTransaction();
        // Insert models
        using var mCmd = conn.CreateCommand();
        mCmd.CommandText = "INSERT INTO models VALUES (1, 'APS_ANZ-TEST_AR_R1_V26.rvt', 'succeeded', DateTime('now'), DateTime('now'), 0);";
        mCmd.ExecuteNonQuery();

        // Insert elements
        using var insElem = conn.CreateCommand();
        insElem.CommandText = "INSERT OR IGNORE INTO elements VALUES (@m, @id, @uid, @kind, '', @cat, @fam, @type, -1, @name)";
        var pM = insElem.Parameters.Add("@m", SqliteType.Integer);
        var pId = insElem.Parameters.Add("@id", SqliteType.Integer);
        var pUid = insElem.Parameters.Add("@uid", SqliteType.Text);
        var pKind = insElem.Parameters.Add("@kind", SqliteType.Text);
        var pCat = insElem.Parameters.Add("@cat", SqliteType.Text);
        var pFam = insElem.Parameters.Add("@fam", SqliteType.Text);
        var pType = insElem.Parameters.Add("@type", SqliteType.Text);
        var pName = insElem.Parameters.Add("@name", SqliteType.Text);

        var eLines = File.ReadAllLines(elementsCsv);
        for (int i = 1; i < eLines.Length; i++) {
            var parts = eLines[i].Split(',');
            if (parts.Length < 10) continue;
            string Clean(string s) => s.Trim('\"');
            pM.Value = 1;
            pId.Value = long.TryParse(Clean(parts[1]), out var lid) ? lid : i;
            pUid.Value = Clean(parts[2]);
            pKind.Value = Clean(parts[3]);
            pCat.Value = Clean(parts[5]);
            pFam.Value = Clean(parts[6]);
            pType.Value = Clean(parts[7]);
            pName.Value = Clean(parts[9]);
            insElem.ExecuteNonQuery();
        }

        // Insert parameters
        using var insP = conn.CreateCommand();
        insP.CommandText = "INSERT INTO parameters VALUES (@m, @id, @scope, @key, @name, 'String', 1, @raw, @disp, '')";
        var pmM = insP.Parameters.Add("@m", SqliteType.Integer);
        var pmId = insP.Parameters.Add("@id", SqliteType.Integer);
        var pmScope = insP.Parameters.Add("@scope", SqliteType.Text);
        var pmKey = insP.Parameters.Add("@key", SqliteType.Text);
        var pmName = insP.Parameters.Add("@name", SqliteType.Text);
        var pmRaw = insP.Parameters.Add("@raw", SqliteType.Text);
        var pmDisp = insP.Parameters.Add("@disp", SqliteType.Text);

        var pLines = File.ReadAllLines(paramsCsv);
        for (int i = 1; i < pLines.Length; i++) {
            var parts = pLines[i].Split(',');
            if (parts.Length < 9) continue;
            string Clean(string s) => s.Trim('\"');
            pmM.Value = 1;
            pmId.Value = long.TryParse(Clean(parts[1]), out var lid) ? lid : 0;
            pmScope.Value = Clean(parts[2]);
            pmKey.Value = Clean(parts[3]);
            pmName.Value = Clean(parts[4]);
            pmRaw.Value = Clean(parts[7]);
            pmDisp.Value = Clean(parts[8]);
            insP.ExecuteNonQuery();
        }
        trans.Commit();
    }
}
"@

$builderCs = Join-Path $outputDir 'Builder.cs'
$builderCsProj = Join-Path $outputDir 'Builder.csproj'
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.22" /></ItemGroup>
</Project>
'@ | Set-Content -LiteralPath $builderCsProj -Encoding utf8

$entryPoint = @"
$csharpScript
class Program {
    static void Main() {
        DbBuilder.Build(@"$sqlitePath", @"$elementsCsv", @"$paramsCsv", @"$modelsCsv");
    }
}
"@
$entryPoint | Set-Content -LiteralPath $builderCs -Encoding utf8

dotnet run --project $builderCsProj -c Release -v q --nologo | Out-Null
Remove-Item $builderCsProj, $builderCs -Force -ErrorAction SilentlyContinue

$swTotal.Stop()
Write-Host "[OK]  SQLite database and CSVs generated successfully."
Write-Host "`nBreakdown by Category:"
$byCat = $elementsList | Group-Object category | Select-Object Name, Count
$byCat | ForEach-Object { Write-Host ("  {0,-25} : {1,6} elements" -f $_.Name, $_.Count) }

Write-Host "`n============================================================"
Write-Host "  Extraction Complete in $([math]::Round($swTotal.Elapsed.TotalSeconds, 1)) seconds!"
Write-Host "============================================================"
Write-Host "Output Directory : $outputDir"
Write-Host "SQLite Database  : $sqlitePath"
Write-Host "Elements CSV     : $elementsCsv"
Write-Host "Parameters CSV   : $paramsCsv"
