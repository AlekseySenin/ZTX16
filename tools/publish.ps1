# Publishes ZTX16 as one self-contained executable (the .NET runtime and SDL3 inside) plus the sample
# cartridges, ready to copy anywhere:
#
#   dist/ZTX16/ZTX16.exe
#   dist/ZTX16/cartridges/*.ztx
#
# Usage:  powershell -ExecutionPolicy Bypass -File tools/publish.ps1 [-Runtime win-x64]
param([string]$Runtime = "win-x64")

$ErrorActionPreference = "Stop"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles "dotnet/dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "The .NET SDK (dotnet) was not found." }
$env:PATH = (Split-Path $dotnet) + [IO.Path]::PathSeparator + $env:PATH
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist/ZTX16"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force (Join-Path $out "cartridges") | Out-Null

& $dotnet publish (Join-Path $root "src/ZTX16.Host") -c Release -r $Runtime -p:PublishSingleFile=true -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $out -File | Where-Object { $_.Extension -in ".pdb", ".xml" } | Remove-Item

Get-ChildItem (Join-Path $root "samples") -Directory | ForEach-Object {
    $project = Get-ChildItem $_.FullName -Filter *.csproj | Select-Object -First 1
    if ($project) {
        & $dotnet build $project.FullName -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "building $($project.Name) failed" }
        Get-ChildItem (Join-Path $_.FullName "bin/Release") -Recurse -Filter *.ztx |
            Copy-Item -Destination (Join-Path $out "cartridges")
    }
}

Write-Host ""
Write-Host "Published to $out"
Get-ChildItem $out -Recurse -File | ForEach-Object { "  {0,-40} {1,10:N0} bytes" -f $_.FullName.Substring($out.Length + 1), $_.Length }
