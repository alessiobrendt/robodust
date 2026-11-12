# Builds RoboDust for Windows into windows\dist\RoboDust.exe.
#   .\build.ps1          one self-contained .exe that runs on any 64-bit Windows 10/11 PC
#   .\build.ps1 -Small   a small .exe that needs the .NET 10 Desktop Runtime installed
param([switch]$Small)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$out = Join-Path $PSScriptRoot 'dist'
# Never package a stale binary.
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$common = @('publish', 'RoboDust.csproj', '-c', 'Release', '-r', 'win-x64', '-o', $out, '-p:PublishSingleFile=true', '-p:DebugType=none')
if ($Small) {
    dotnet @common --self-contained false
} else {
    dotnet @common --self-contained true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
}
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Get-ChildItem $out -Filter *.pdb | Remove-Item
$exe = Join-Path $out 'RoboDust.exe'
"{0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
