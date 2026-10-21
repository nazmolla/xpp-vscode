$b="C:\XppLocalRuntime\Packages\bin"; $fw="C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$csc="C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\Roslyn\csc.exe"
$refs=@("$b\aoskernel.dll","$b\Microsoft.Dynamics.AX.AosConfig.dll","$b\Microsoft.Dynamics.AX.Xpp.Support.dll","$b\Microsoft.Dynamics.AX.Server.Core.dll","$b\Microsoft.Dynamics.AX.Xpp.AxShared.dll","C:\XppLocalRuntime\Packages\ApplicationFoundation\bin\Dynamics.AX.ApplicationFoundation.dll","C:\XppLocalRuntime\Packages\ApplicationPlatform\bin\Dynamics.AX.ApplicationPlatform.dll","$b\Microsoft.Dynamics.AX.Data.Management.dll","$b\Microsoft.Dynamics.AX.Metadata.dll","$fw\System.Data.dll","$fw\System.Configuration.dll","$fw\System.Core.dll","$fw\System.dll","$fw\mscorlib.dll") | % { "/reference:`"$_`"" }
& $csc /nologo /noconfig /nostdlib /platform:x64 /target:exe /langversion:latest /out:"$b\XppLocalTestRunner.exe" @refs "$PSScriptRoot\Program.cs" "$PSScriptRoot\SchemaSync.cs"
exit $LASTEXITCODE


