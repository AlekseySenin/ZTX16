# Publishes ZTX16 as one self-contained executable (the .NET runtime and SDL3 inside) with the ZTX16
# environment and the sample cartridges, ready to copy anywhere:
#
#   dist/ZTX16/ZTX16.exe               the machine (double-click: boots into the Console)
#   dist/ZTX16/system/*.ztx            system apps (Console, editors)
#   dist/ZTX16/core/*.cs               the core library compiled into every cartridge
#   dist/ZTX16/ref/*.dll               reference assemblies for building cartridges inside ZTX16
#   dist/ZTX16/cartridges/*.ztx        sample cartridges
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

# Refuse before touching anything if a previous ZTX16.exe is still running (it would be half deleted).
$exe = Join-Path $out "ZTX16.exe"
if (Test-Path $exe) {
    try { [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None').Close() }
    catch { throw "dist/ZTX16/ZTX16.exe is in use: close ZTX16 and run this script again. Nothing was changed." }
}

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force (Join-Path $out "cartridges"), (Join-Path $out "system"), (Join-Path $out "ref") | Out-Null

# Build the toolchain and the system apps first: cartridge builds use the plain (non-RID) toolchain output.
& $dotnet build (Join-Path $root "src/ZTX16.Toolchain") -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "building the toolchain failed" }
Get-ChildItem (Join-Path $root "apps") -Directory | ForEach-Object {
    & $dotnet build (Get-ChildItem $_.FullName -Filter *.csproj | Select-Object -First 1).FullName -c Release --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { throw "building $($_.Name) failed" }
}

& $dotnet publish (Join-Path $root "src/ZTX16.Host") -c Release -r $Runtime -p:PublishSingleFile=true -o $out --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem $out -File | Where-Object { $_.Extension -in ".pdb", ".xml" } | Remove-Item

# System apps (built as part of the host) and sample cartridges.
Get-ChildItem (Join-Path $root "apps") -Directory | ForEach-Object {
    Get-ChildItem (Join-Path $_.FullName "bin/Release") -Recurse -Filter *.ztx | Copy-Item -Destination (Join-Path $out "system")
}

Get-ChildItem (Join-Path $root "samples") -Directory | ForEach-Object {
    $project = Get-ChildItem $_.FullName -Filter *.csproj | Select-Object -First 1
    if ($project) {
        & $dotnet build $project.FullName -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "building $($project.Name) failed" }
        Get-ChildItem (Join-Path $_.FullName "bin/Release") -Recurse -Filter *.ztx |
            Copy-Item -Destination (Join-Path $out "cartridges")
    }
}

# Reference assemblies for the in-ZTX16 build: the runtime's CoreLib and System.Runtime, plus the ZTX16 SDK.
$runtimeDir = Get-ChildItem (Join-Path (Split-Path $dotnet) "shared/Microsoft.NETCore.App") -Directory |
    Where-Object { $_.Name -like "10.*" } | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
if (-not $runtimeDir) { throw "The .NET 10 runtime was not found." }
Copy-Item (Join-Path $runtimeDir.FullName "System.Private.CoreLib.dll"), (Join-Path $runtimeDir.FullName "System.Runtime.dll") (Join-Path $out "ref")
Copy-Item (Join-Path $root "src/ZTX16.Sdk/bin/Release/net10.0/ZTX16.Sdk.dll") (Join-Path $out "ref")

Write-Host ""
Write-Host "Published to $out"
Get-ChildItem $out -Recurse -File | Where-Object { $_.DirectoryName -notlike "*\core*" } |
    ForEach-Object { "  {0,-40} {1,12:N0} bytes" -f $_.FullName.Substring($out.Length + 1), $_.Length }
"  core\*.cs                                {0,12} files" -f (Get-ChildItem (Join-Path $out "core") -Recurse -File).Count
