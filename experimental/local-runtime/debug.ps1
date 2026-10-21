param([string]$Test = 'XvsLocalRuntimeSmokeTest')
$env:ECA_ConfigurationFile = 'C:\XppLocalRuntime\runtime.config'
$env:_NT_SYMBOL_PATH = 'srv*C:\XppLocalRuntime\symbols*https://msdl.microsoft.com/download/symbols'
Set-Location C:\XppLocalRuntime\Packages
sqlcmd -S "(LocalDB)\MSSQLLocalDB" -E -d AxDbLocalTest -i $PSScriptRoot\seed.sql | Out-Null
& cdbX64 -G -logo C:\XppLocalRuntime\cdb2.log -cf $PSScriptRoot\cdb-crash.txt C:\XppLocalRuntime\Packages\bin\XppLocalTestRunner.exe "/test:$Test" /xml:C:\XppLocalRuntime\result.xml *> $null
$l = Get-Content C:\XppLocalRuntime\cdb2.log
($l | Select-String 'Last event:' | Select -Last 1).Line
($l | Select-String '^(STEP|FAILED|RESULT)' | % Line) -join ' / '
$i = ($l | Select-String '!clrstack' | Select -Last 1).LineNumber
$frames = $l[$i..($l.Count-1)] | ? { $_ -match '^[0-9a-f]{16} [0-9a-f]{16} ' } | % { ($_ -split ' ', 3)[2] }
"frames $($frames.Count)"
$frames | Select -First 30 | % { $_.Substring(0, [Math]::Min(190, $_.Length)) }
'...'
$frames | Select -Last 12 | % { $_.Substring(0, [Math]::Min(190, $_.Length)) }
$pe = $l | Select-String -Pattern '^(Exception type|Message|InnerException):' | Select -First 6 | % Line
$pe


