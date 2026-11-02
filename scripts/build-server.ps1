param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

function Get-ConfigFramework([string]$path) {
    try { (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).FrameworkDirectory } catch { $null }
}

# Candidate PackagesLocalDirectory\bin folders, first match wins:
# XPP_BIN; the active UDE configuration (its FrameworkDirectory can be anywhere); the registry's FrameworkDirectory;
# any other UDE configuration, newest first; cloud-hosted (K:) and VHD (C: only) environments.
$key = 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations'
$registry = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
$frameworks = @()
if ($registry.CurrentMetadataConfig -like '*.json') {
    $frameworks += Get-ConfigFramework $registry.CurrentMetadataConfig
}
$frameworks += $registry.FrameworkDirectory
$frameworks += Get-ChildItem "$env:LOCALAPPDATA\Microsoft\Dynamics365\XPPConfig\*.json" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | ForEach-Object { Get-ConfigFramework $_.FullName }
$frameworks += 'K:\AosService\PackagesLocalDirectory', 'C:\AosService\PackagesLocalDirectory'
$candidates = @($env:XPP_BIN) + ($frameworks | Where-Object { $_ } | ForEach-Object { "$($_.TrimEnd('\'))\bin" })

# Join-Path throws on a missing drive; File.Exists never throws.
$bin = $candidates | Where-Object { $_ -and [System.IO.File]::Exists("$_\xppc.exe") } | Select-Object -First 1
if (-not $bin) {
    throw 'D365FO compiler (PackagesLocalDirectory\bin\xppc.exe) not found. Connect to a unified developer environment first, build on a cloud-hosted or VHD environment, or set XPP_BIN.'
}
Write-Host "Using $bin"

$root = Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $root 'server\XppVsCode.Server.csproj') -c $Configuration -o (Join-Path $root 'server\bin') "-p:D365BinPath=$bin" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
