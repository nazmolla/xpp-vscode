param([Parameter(Mandatory)][string]$Tables)
# Creates/updates only the named tables in the scratch database (no full sync).
# SyncEngine runs inside XppLocalTestRunner so it gets the same runtime assembly resolution.
$env:ECA_ConfigurationFile = 'C:\XppLocalRuntime\runtime.config'
Set-Location C:\XppLocalRuntime\Packages
$bin = 'C:\XppLocalRuntime\Packages\bin'
$cs = 'Data Source=(LocalDB)\MSSQLLocalDB;Initial Catalog=AxDbLocalTest;Integrated Security=True;Enlist=True;Application Name=SyncEngine'
& "$bin\XppLocalTestRunner.exe" "/exec:$bin\SyncEngine.exe" -syncmode=partiallist "-synclist=$Tables" -skipInitialSchema "-metadatabinaries=C:\XppLocalRuntime\Packages" "-binfolder=C:\XppLocalRuntime\Packages" "-metadatafolder=C:\XppLocalRuntime\Packages" "-connect=$cs" -continueOnError=true *> C:\XppLocalRuntime\sync-tables.log
"exit $LASTEXITCODE"


