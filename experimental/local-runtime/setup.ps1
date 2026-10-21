param(
    [string]$Root = 'C:\XppLocalRuntime',
    [string]$Database = 'AxDbLocalTest',
    [switch]$ResetDatabase
)
# Sets up the experimental local X++ test runtime: one packages folder (junctions to the reference and custom
# packages), a bin folder of hard links (the shared install is never modified), a scratch LocalDB database with a
# SQL login, runtime.config, the runner, and the minimal schema + seed data.
# Root must be on the same volume as PackagesLocalDirectory (hard links).
$ErrorActionPreference = 'Stop'
$sql = '(LocalDB)\MSSQLLocalDB'

$cfgFile = (Get-ItemProperty 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations').CurrentMetadataConfig
$cfg = Get-Content $cfgFile -Raw | ConvertFrom-Json
$pld = $cfg.FrameworkDirectory
$custom = $cfg.ModelStoreFolder
"Reference packages: $pld"
"Custom packages:    $custom"

# Packages: junction per package; bin is a real folder so runtime-only files can be added.
$packages = Join-Path $Root 'Packages'
New-Item -ItemType Directory -Force $packages, (Join-Path $Root 'webroot') | Out-Null
foreach ($dir in @(Get-ChildItem $pld -Directory | Where-Object Name -ne 'bin') + @(Get-ChildItem $custom -Directory)) {
    $link = Join-Path $packages $dir.Name
    if (-not (Test-Path $link)) { New-Item -ItemType Junction -Path $link -Target $dir.FullName | Out-Null }
}
$bin = Join-Path $packages 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
foreach ($file in Get-ChildItem (Join-Path $pld 'bin') -File) {
    $link = Join-Path $bin $file.Name
    if (-not (Test-Path $link)) { New-Item -ItemType HardLink -Path $link -Target $file.FullName | Out-Null }
}
foreach ($dir in Get-ChildItem (Join-Path $pld 'bin') -Directory) {
    $link = Join-Path $bin $dir.Name
    if (-not (Test-Path $link)) { New-Item -ItemType Junction -Path $link -Target $dir.FullName | Out-Null }
}
# The kernel reads static metadata from bin\StaticMetadata; System.ValueTuple is in bin\ModelUtilDlls only.
if (-not (Test-Path (Join-Path $bin 'StaticMetadata'))) { New-Item -ItemType Junction -Path (Join-Path $bin 'StaticMetadata') -Target (Join-Path $pld 'StaticMetadata') | Out-Null }
if (-not (Test-Path (Join-Path $bin 'System.ValueTuple.dll'))) { New-Item -ItemType HardLink -Path (Join-Path $bin 'System.ValueTuple.dll') -Target (Join-Path $pld 'bin\ModelUtilDlls\System.ValueTuple.dll') | Out-Null }

# Scratch database and a SQL login (the kernel uses SQL authentication).
if ($ResetDatabase) {
    sqlcmd -S $sql -E -Q "IF DB_ID('$Database') IS NOT NULL BEGIN ALTER DATABASE [$Database] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [$Database]; END"
}
sqlcmd -S $sql -E -Q "IF DB_ID('$Database') IS NULL BEGIN CREATE DATABASE [$Database]; ALTER DATABASE [$Database] SET RECOVERY SIMPLE; ALTER DATABASE [$Database] SET ALLOW_SNAPSHOT_ISOLATION ON; ALTER DATABASE [$Database] SET READ_COMMITTED_SNAPSHOT ON; END"
$password = 'Xpp!' + [guid]::NewGuid().ToString('N').Substring(0, 16)
sqlcmd -S $sql -E -Q "IF SUSER_ID('axlocal') IS NULL CREATE LOGIN axlocal WITH PASSWORD='$password', CHECK_POLICY=OFF ELSE ALTER LOGIN axlocal WITH PASSWORD='$password'; ALTER SERVER ROLE sysadmin ADD MEMBER axlocal;"

# runtime.config: the appSettings an AOS reads from web.config, loaded through ECA_ConfigurationFile.
$settings = [ordered]@{
    'Aos.DeploymentName' = 'XppLocalRuntime'; 'Aos.InstanceDisplayName' = 'XppLocalRuntime'
    'Aos.MetadataDirectory' = $packages; 'Aos.PackageDirectory' = $packages; 'Aos.AppRoot' = (Join-Path $Root 'webroot')
    'Common.BinDir' = $packages; 'Common.DevToolsBinDir' = $bin; 'Common.EnvironmentGuid' = [guid]::NewGuid().ToString()
    'Common.HostingEnvironment' = 'SyncEngine'
    'DataAccess.Database' = $Database; 'DataAccess.DbServer' = $sql; 'DataAccess.SqlUser' = 'axlocal'; 'DataAccess.SqlPwd' = $password
    'DataAccess.AxAdminSqlUser' = 'axlocal'; 'DataAccess.AxAdminSqlPwd' = $password; 'DataAccess.ReadOnlySecondaryDbServers' = ''
    'Infrastructure.WebRoot' = (Join-Path $Root 'webroot'); 'Infrastructure.ServerName' = $env:COMPUTERNAME
    'Aad.TenantDomainGUID' = '00000000-0000-0000-0000-000000000000'; 'Aad.AADTenantId' = '00000000-0000-0000-0000-000000000000'
    'Aos.SafeMode' = 'false'; 'Aos.NewDbSync' = 'true'; 'Aos.RunTimeServicesMode' = 'false'; 'Aos.ForceEnumValuesOnMetadataLoad' = 'false'; 'Aos.AosRole' = 'AOS'
    'Monitoring.IFXSessionName' = 'XppLocalRuntime'; 'Monitoring.MdmAccount' = 'XppLocalRuntime'
    'Monitoring.ActivityMetricNamespace' = 'XppLocalRuntime'; 'Monitoring.CustomMetricNamespace' = 'XppLocalRuntime'
    'IsInOfflineMode' = 'true'
}
$xml = '<?xml version="1.0" encoding="utf-8"?><configuration><appSettings>' +
    (($settings.GetEnumerator() | ForEach-Object { '<add key="{0}" value="{1}" />' -f $_.Key, [Security.SecurityElement]::Escape($_.Value) }) -join '') +
    '</appSettings></configuration>'
Set-Content (Join-Path $Root 'runtime.config') $xml -Encoding UTF8

# Runner (config: same assembly bindings as SysTestConsole), schema, security data, seed.
& (Join-Path $PSScriptRoot 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Runner build failed.' }
$runnerConfig = Join-Path $bin 'XppLocalTestRunner.exe.config'
if (Test-Path $runnerConfig) { Remove-Item $runnerConfig }
Copy-Item (Join-Path $bin 'SysTestConsole.17.0.exe.config') $runnerConfig

$env:ECA_ConfigurationFile = Join-Path $Root 'runtime.config'
Push-Location $packages
& (Join-Path $bin 'XppLocalTestRunner.exe') '/schema:SysFlighting,SysGlobalConfiguration,SysServerSessions,SysClientSessions,Deployment,Partitions,SysCacheFlush,SysLastValue,SysOccConfiguration,SysSignatureSetup,DataArea,FeatureManagementState,ModelSecPolRuntimeEx,VirtualDataAreaList,TableCollectionList,SecurityUserRole,SecurityUserRoleCondition,SysClientPerf' /security
Pop-Location
sqlcmd -S $sql -E -d $Database -i (Join-Path $PSScriptRoot 'seed.sql')
'Done. Run tests with run.ps1 -Test <Class>.'
