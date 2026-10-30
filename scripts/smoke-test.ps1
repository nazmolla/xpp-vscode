param(
    [string]$Project = 'C:\Users\Mohamed Nazmi\source\repos\TestProj\TestProj.rnrproj',
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cfg = (Get-ItemProperty 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations').CurrentMetadataConfig

# Read-only calls only: [name, arguments, text expected in the result]
$calls = @(
    @('xpp_get_environment', @{}, 'Custom'),
    @('xpp_list_models', @{}, 'testcust'),
    @('xpp_get_model_info', @{ modelName = 'testcust' }, 'testcust'),
    @('xpp_find_object', @{ objectName = 'CustTable'; objectType = 'AxTable'; exactMatch = $true }, 'CustTable'),
    @('xpp_list_objects', @{ nameFilter = 'CustTable'; objectType = 'AxTable'; maxResults = 5 }, 'CustTable'),
    @('xpp_list_model_objects', @{ modelName = 'testcust' }, 'AxClass'),
    @('xpp_read_object', @{ objectType = 'AxClass'; objectName = 'SalesFormLetter'; methodNames = @('construct') }, 'construct'),
    @('xpp_read_object', @{ objectType = 'AxEnum'; objectName = 'SalesStatus' }, 'Invoiced'),
    @('xpp_read_object', @{ objectType = 'AxMap'; objectName = 'CustVendTable' }, 'CustVendTable'),
    @('xpp_get_form_layout', @{ formName = 'CustParameters'; maxDepth = 2 }, 'Design'),
    @('xpp_find_references', @{ objectType = 'AxClass'; objectName = 'SalesFormLetter'; memberName = 'construct' }, 'SalesFormLetter'),
    @('xpp_read_label', @{ labelFileId = 'AccountsPayable'; labelId = 'AccountField' }, 'Account'),
    @('xpp_read_label', @{ labelFileId = 'AccountsPayable'; searchText = 'Vendor balance'; maxResults = 3 }, 'AccountsPayable'),
    @('xpp_list_project_items', @{}, 'HSRemovalProcessor'),
    @('xpp_validate_object', @{ objectType = 'AxClass'; objectName = 'HSRemovalProcessor' }, 'HSRemovalProcessor'),
    @('xpp_get_object_type_schema', @{ objectType = 'AxEnum' }, 'EnumValues')
)

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $root 'server\bin\XppVsCode.Server.exe'
$psi.Arguments = "--config `"$cfg`" --project `"$Project`""
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$sw = [Diagnostics.Stopwatch]::StartNew()
$p = [Diagnostics.Process]::Start($psi)
$err = $p.StandardError.ReadToEndAsync()

$p.StandardInput.Write('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}' + "`n")
$p.StandardInput.Write('{"jsonrpc":"2.0","method":"notifications/initialized"}' + "`n")
$p.StandardInput.Write('{"jsonrpc":"2.0","id":2,"method":"tools/list"}' + "`n")
$id = 10
foreach ($c in $calls) {
    $msg = @{ jsonrpc = '2.0'; id = $id++; method = 'tools/call'; params = @{ name = $c[0]; arguments = $c[1] } } | ConvertTo-Json -Depth 6 -Compress
    $p.StandardInput.Write($msg + "`n")
}
# Read-only calls run in parallel, so stdin stays open until every response has arrived.
$lines = New-Object System.Collections.Generic.List[string]
while ($lines.Count -lt $calls.Count + 2 -and $sw.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
    $next = $p.StandardOutput.ReadLineAsync()
    if (-not $next.Wait([int](($TimeoutSeconds - $sw.Elapsed.TotalSeconds) * 1000)) -or $null -eq $next.Result) { break }
    if ($next.Result) { $lines.Add($next.Result) }
}
$p.StandardInput.Close()

if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); 'TIMEOUT' }
"exit=$($p.ExitCode) seconds=$([int]$sw.Elapsed.TotalSeconds)"
"stderr: " + $err.Result.Trim()

$pass = 0; $fail = 0
foreach ($r in $lines) {
    $o = $r | ConvertFrom-Json
    if ($o.id -eq 1) { "init: $($o.result.serverInfo.name) $($o.result.serverInfo.version), instructions $($o.result.instructions.Length) chars"; continue }
    if ($o.id -eq 2) { "tools: $($o.result.tools.Count)"; continue }
    $c = $calls[$o.id - 10]
    $text = if ($o.error) { 'RPC ERROR ' + $o.error.message } else { $o.result.content[0].text }
    $ok = -not $o.error -and -not $o.result.isError -and $text -match [regex]::Escape($c[2])
    if ($ok) { $pass++ } else { $fail++ }
    $first = ($text -split "`n" | Where-Object { $_.Trim() } | Select-Object -First 2) -join ' | '
    "{0} {1,-28} {2,7} chars  {3}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $c[0], $text.Length, ($first.Substring(0, [Math]::Min(150, $first.Length)))
}
"passed $pass, failed $fail"
