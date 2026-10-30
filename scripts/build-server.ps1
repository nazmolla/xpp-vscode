param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$key = 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations'
$config = (Get-ItemProperty -Path $key -Name CurrentMetadataConfig -ErrorAction SilentlyContinue).CurrentMetadataConfig
if ($config -and $config -like '*.json' -and (Test-Path $config)) {
    $framework = (Get-Content $config -Raw | ConvertFrom-Json).FrameworkDirectory
} else {
    # Cloud-hosted and VHD environments: the metadata assemblies are in PackagesLocalDirectory\bin.
    $framework = 'K:\AosService\PackagesLocalDirectory', 'C:\AosService\PackagesLocalDirectory' |
        Where-Object { Test-Path (Join-Path $_ 'bin\xppc.exe') } | Select-Object -First 1
    if (-not $framework) {
        throw 'No active D365FO configuration. Connect to a unified developer environment first, or build on a cloud-hosted or VHD environment.'
    }
}
$bin = Join-Path $framework 'bin'

$root = Split-Path $PSScriptRoot -Parent
dotnet build (Join-Path $root 'server\XppVsCode.Server.csproj') -c $Configuration -o (Join-Path $root 'server\bin') "-p:D365BinPath=$bin" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
