param([string]$Configuration = "Release")

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "../Tests/OsService.Tests/OsService.Tests.csproj"
$results = Join-Path $PSScriptRoot "../Tests/OsService.Tests/TestResults/$([guid]::NewGuid())"
dotnet test $project --configuration $Configuration --collect:"XPlat Code Coverage" --results-directory $results -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=cobertura,opencover
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$reports = @(Get-ChildItem -Path $results -Filter coverage.cobertura.xml -Recurse -File)
if ($reports.Count -ne 1) { throw "Esperado um relatorio Cobertura; encontrados $($reports.Count)" }
[xml]$coverage = Get-Content -LiteralPath $reports[0].FullName
$packages = @($coverage.coverage.packages.package)
if ($packages.Count -ne 1 -or $packages[0].name -ne 'OsService') { throw "Relatorio nao cobre apenas a assembly OsService" }
$valid = [int]$coverage.coverage.'lines-valid'
$covered = [int]$coverage.coverage.'lines-covered'
if ($valid -le 0 -or $covered -lt 0 -or $covered -gt $valid) { throw "Contagem de linhas invalida" }
$rate = 100 * $covered / $valid
"Cobertura de linhas do OS Service: {0:N2}% ({1}/{2})" -f $rate, $covered, $valid
if ($covered * 100 -lt $valid * 80) { throw "Cobertura abaixo da meta de 80%" }
