# Downloads Pdb2Pdb (Microsoft.DiaSymReader.Pdb2Pdb, MIT) into tools\pdb2pdb, where the extension ships it.
# xppc writes Windows PDBs; the C# extension's debugger (vsdbg) reads only portable ones, so builds convert them.
# The version and hash are pinned; nothing is downloaded when the pinned version is already there.
$ErrorActionPreference = 'Stop'
$version = '1.1.0-beta2-26525-01'
$sha256 = '119185307EAF5DE7CACD897251FE5633E08E78FBFB9628DB0585B0D316C5D50F'
$feed = 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/flat2/microsoft.diasymreader.pdb2pdb'

$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'tools\pdb2pdb'
$marker = Join-Path $target 'VERSION'
if ((Test-Path (Join-Path $target 'Pdb2Pdb.exe')) -and (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $version)) {
    Write-Host "Pdb2Pdb $version already in $target"
    exit 0
}

$work = Join-Path ([IO.Path]::GetTempPath()) "xpp-pdb2pdb-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $package = Join-Path $work 'pdb2pdb.zip'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest "$feed/$version/microsoft.diasymreader.pdb2pdb.$version.nupkg" -OutFile $package -UseBasicParsing
    # .NET directly: Windows PowerShell started from PowerShell 7 (the release workflow) cannot load Get-FileHash or Expand-Archive.
    $stream = [IO.File]::OpenRead($package)
    try { $actual = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose() }
    if ($actual -ne $sha256) {
        throw "Pdb2Pdb $version has hash $actual, expected $sha256."
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($package, (Join-Path $work 'pkg'))
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    # The tool and its assemblies; the localized resource folders are not needed.
    Get-ChildItem (Join-Path $work 'pkg\tools') -File | Copy-Item -Destination $target
    Set-Content -Path $marker -Value $version -NoNewline
    Write-Host "Pdb2Pdb $version -> $target"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
