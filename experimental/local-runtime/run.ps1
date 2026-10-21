param([string]$Test = 'XvsLocalRuntimeSmokeTest', [switch]$Diag, [switch]$Trace, [switch]$NoSession, [int]$TimeoutSeconds = 600)
$env:ECA_ConfigurationFile = 'C:\XppLocalRuntime\runtime.config'
$args2 = @("/test:$Test", '/xml:C:\XppLocalRuntime\result.xml') + $(if ($Diag) { '/diag' } else { @() }) + $(if ($Trace) { '/trace' } else { @() }) + $(if ($NoSession) { '/nosession' } else { @() })
$p = Start-Process -FilePath C:\XppLocalRuntime\Packages\bin\XppLocalTestRunner.exe -ArgumentList $args2 -WorkingDirectory C:\XppLocalRuntime\Packages -RedirectStandardOutput C:\XppLocalRuntime\runner.out -RedirectStandardError C:\XppLocalRuntime\runner.err -NoNewWindow -PassThru
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id; 'TIMEOUT' } else { "exit $($p.ExitCode)" }



