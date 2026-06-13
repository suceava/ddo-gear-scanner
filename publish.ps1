# Build DdoGearScanner as a self-contained single-file Windows executable.
#
# Usage:
#   .\publish.ps1            — build only, output to dist\
#
# Output:
#   .\dist\DdoGearScanner.exe   (self-contained: bundled .NET runtime + OpenCV natives)
#
# Distributable: copy dist\DdoGearScanner.exe to any Windows 10/11 x64 machine. No .NET install
# required. DDO must run in windowed / borderless windowed mode for capture to work.

param(
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$proj = Join-Path $root 'src\DdoGearScanner\DdoGearScanner.csproj'
$publishDir = Join-Path $root 'src\DdoGearScanner\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish'
$distDir = Join-Path $root 'dist'

# Stop any running instance so the build can overwrite the exe.
Get-Process DdoGearScanner -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
if (Test-Path $distDir) { Remove-Item $distDir -Recurse -Force }

dotnet publish $proj `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    --nologo

if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

New-Item -ItemType Directory -Path $distDir | Out-Null
Copy-Item (Join-Path $publishDir 'DdoGearScanner.exe') $distDir

$exe = Join-Path $distDir 'DdoGearScanner.exe'
$size = (Get-Item $exe).Length / 1MB
Write-Output ""
Write-Output ("Built: {0} ({1:N0} MB)" -f $exe, $size)

if ($Zip) {
    $zipPath = Join-Path $distDir 'DdoGearScanner-windows.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $exe -DestinationPath $zipPath -CompressionLevel Optimal
    $zipSize = (Get-Item $zipPath).Length / 1MB
    Write-Output ("Zipped: {0} ({1:N0} MB)" -f $zipPath, $zipSize)
}
