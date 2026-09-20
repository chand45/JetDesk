$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'src\JevDesktop.csproj'
$publishPath = Join-Path $PSScriptRoot 'app'
dotnet publish $projectPath -c Release --self-contained false -o $publishPath --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Host "Built: $(Join-Path $publishPath 'JevDesktop.exe')"
