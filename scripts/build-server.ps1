param([string]$Configuration = 'Release', [switch]$Test)
$ErrorActionPreference = 'Stop'

function Get-ConfigFramework([string]$path) {
    try { (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).FrameworkDirectory } catch { $null }
}

# Candidate PackagesLocalDirectory\bin folders, first match wins:
# XPP_BIN; the active UDE configuration (its FrameworkDirectory can be anywhere); the registry's FrameworkDirectory;
# any other UDE configuration, newest first; on cloud-hosted and VHD environments the local AOS's own configuration
# (the AOS web.config the batch service runs with, and Visual Studio's machine default DynamicsDevConfig.xml).
$key = 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations'
$registry = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
$frameworks = @()
if ($registry.CurrentMetadataConfig -like '*.json') {
    $frameworks += Get-ConfigFramework $registry.CurrentMetadataConfig
}
$frameworks += $registry.FrameworkDirectory
$frameworks += Get-ChildItem "$env:LOCALAPPDATA\Microsoft\Dynamics365\XPPConfig\*.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | ForEach-Object { Get-ConfigFramework $_.FullName }
$batch = (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\DynamicsAxBatch' -ErrorAction SilentlyContinue).ImagePath
if ($batch -match '-service\s+"?([^"]+?\.config)"?\s*$' -and [System.IO.File]::Exists($Matches[1])) {
    $webConfig = [System.IO.File]::ReadAllText($Matches[1])
    if ($webConfig -match '<add\s+key="Aos\.PackageDirectory"\s+value="([^"]*)"') { $frameworks += $Matches[1] }
}
$defaultConfig = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Dynamics\AX\Development' -ErrorAction SilentlyContinue).DefaultConfig
if ($defaultConfig) { $frameworks += Split-Path (Split-Path $defaultConfig -Parent) -Parent }
$candidates = @($env:XPP_BIN) + ($frameworks | Where-Object { $_ } | ForEach-Object { "$($_.TrimEnd('\'))\bin" })

# Join-Path throws on a missing drive; File.Exists never throws.
$bin = $candidates | Where-Object { $_ -and [System.IO.File]::Exists("$_\xppc.exe") } | Select-Object -First 1
if (-not $bin) {
    throw 'D365FO compiler (PackagesLocalDirectory\bin\xppc.exe) not found. Connect to a unified developer environment first, build on a cloud-hosted or VHD environment, or set XPP_BIN.'
}
Write-Host "Using $bin"

$root = Split-Path $PSScriptRoot -Parent
# The server carries the extension's version (file metadata; code signing checks it).
$version = (Get-Content (Join-Path $root 'package.json') -Raw | ConvertFrom-Json).version
dotnet build (Join-Path $root 'server\XppVsCode.Server.csproj') -c $Configuration -o (Join-Path $root 'server\bin') "-p:D365BinPath=$bin" "-p:Version=$version" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($Test) {
    dotnet test (Join-Path $root 'test\server\XppVsCode.Server.Tests.csproj') -c $Configuration "-p:D365BinPath=$bin" -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
