param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "WorldLineYggdrasil\WorldLineYggdrasil.csproj"

dotnet build $proj -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host "[WorldLineYggdrasil] built OK (config=$Configuration)"