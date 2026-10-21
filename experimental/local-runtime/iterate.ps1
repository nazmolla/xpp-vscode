param([int]$MaxRounds = 8, [string]$Test = 'XvsLocalRuntimeSmokeTest')
# Runs the local runner, collects the tables the kernel/test could not find (from SQL errors), syncs only those, repeats.
$sql = '(LocalDB)\MSSQLLocalDB'
$env:ECA_ConfigurationFile = 'C:\XppLocalRuntime\runtime.config'
$seen = @{}
for ($round = 1; $round -le $MaxRounds; $round++) {
    Remove-Item C:\XppLocalRuntime\xe\* -ErrorAction SilentlyContinue
    sqlcmd -S $sql -E -i $PSScriptRoot\xe.sql | Out-Null
    sqlcmd -S $sql -E -d AxDbLocalTest -i $PSScriptRoot\seed.sql | Out-Null
    $result = & $PSScriptRoot\run.ps1 -Test $Test
    Start-Sleep 2
    sqlcmd -S $sql -E -Q "ALTER EVENT SESSION XppLocal ON SERVER STATE = STOP" | Out-Null
    $errors = sqlcmd -S $sql -E -I -W -h -1 -s "|" -i $PSScriptRoot\xe-errors.sql
    $steps = (Get-Content C:\XppLocalRuntime\runner.out) -match '^(STEP|FAILED|RESULT)'
    "--- round $round ($result): $($steps -join ' / ')"
    $missing = New-Object System.Collections.Generic.List[string]
    foreach ($line in $errors) {
        if ($line -match "Invalid object name '(?:DBO\.)?SEQ_(\d+)'") {
            $name = (sqlcmd -S $sql -E -d AxDbLocalTest -h -1 -W -Q "SET NOCOUNT ON; SELECT NAME FROM TABLEIDTABLE WHERE ID=$($Matches[1])" | Select -First 1)
            if ($name) { $missing.Add($name.Trim()) }
        } elseif ($line -match "Invalid object name '(?:DBO\.)?([A-Z0-9_]+)'") { $missing.Add($Matches[1]) }
        elseif ($line -match "Invalid column name .*FROM ([A-Z0-9_]+) ") { $missing.Add($Matches[1]) }
    }
    $new = $missing | Sort-Object -Unique | Where-Object { -not $seen.ContainsKey($_) }
    if (-not $new) { "no new missing tables"; $errors | Select -First 8; break }
    $new | ForEach-Object { $seen[$_] = 1 }
    "syncing: $($new -join ', ')"
    Push-Location C:\XppLocalRuntime\Packages
    & C:\XppLocalRuntime\Packages\bin\XppLocalTestRunner.exe "/schema:$($new -join ',')" 2>&1 | % { "$_" } | Out-File C:\XppLocalRuntime\schema.log
    Pop-Location
    (Select-String -Path C:\XppLocalRuntime\schema.log -Pattern 'Syncing Table Finished|STEP FAILED' | % { ($_.Line -replace '.*(Finished: |FAILED )','') -replace '\. Time.*','' }) -join ', '
}




