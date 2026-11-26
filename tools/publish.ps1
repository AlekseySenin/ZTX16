# Publishes ZTX16 as one self-contained executable with the sample cartridges, ready to copy anywhere:
#
#   dist/ZTX16/ZTX16.exe               the whole machine: .NET runtime, SDL3, the Console and the code
#                                      editor, and everything needed to build cartridges are inside
#   dist/ZTX16/cartridges/*.ztx        sample cartridges
#
# ZTX16.exe works on its own; a system/ folder next to it can hold replacement system apps.
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
New-Item -ItemType Directory -Force (Join-Path $out "cartridges") | Out-Null

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

Get-ChildItem (Join-Path $root "samples") -Directory | ForEach-Object {
    $project = Get-ChildItem $_.FullName -Filter *.csproj | Select-Object -First 1
    if ($project) {
        & $dotnet build $project.FullName -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "building $($project.Name) failed" }
        Get-ChildItem (Join-Path $_.FullName "bin/Release") -Recurse -Filter *.ztx |
            Copy-Item -Destination (Join-Path $out "cartridges")
    }
}

# The toolchain's core/ copy is embedded in ZTX16.exe; the published folder keeps only the executable.
$core = Join-Path $out "core"
if (Test-Path $core) { Remove-Item -Recurse -Force $core }

Write-Host ""
Write-Host "Published to $out"
Get-ChildItem $out -Recurse -File |
    ForEach-Object { "  {0,-40} {1,12:N0} bytes" -f $_.FullName.Substring($out.Length + 1), $_.Length }
