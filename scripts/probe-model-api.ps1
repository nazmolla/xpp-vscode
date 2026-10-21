$cfg = Get-Content (Get-ItemProperty 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations').CurrentMetadataConfig -Raw | ConvertFrom-Json
$bin = Join-Path $cfg.FrameworkDirectory 'bin'
$asm = [Reflection.Assembly]::LoadFrom((Join-Path $bin 'Microsoft.Dynamics.AX.Metadata.dll'))
$t = $asm.GetType('Microsoft.Dynamics.AX.Metadata.MetaModel.ModelInfo')
'== ModelInfo'
$t.GetProperties() | ForEach-Object { "  $($_.PropertyType.Name) $($_.Name) $(if ($_.CanWrite) { 'rw' } else { 'r' })" }
'== IMetaModelService model methods'
$s = $asm.GetType('Microsoft.Dynamics.AX.Metadata.Service.IMetaModelService')
$s.GetMethods() | Where-Object { $_.Name -match 'Model' } | ForEach-Object { "  $($_.ReturnType.Name) $($_.Name)(" + (($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', ') + ')' }
