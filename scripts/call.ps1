param(
    [string]$CallsFile,
    [switch]$ListTools,
    [string]$Project,
    [int]$TimeoutSeconds = 300,
    [int]$MaxChars = 4000
)
# Runs tool calls from a JSON file (array of {name, arguments}) against a fresh server and prints each result.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$cfg = (Get-ItemProperty 'HKCU:\Software\Microsoft\Dynamics\AX7\Development\Configurations').CurrentMetadataConfig
$calls = if ($ListTools) { @() } else { Get-Content $CallsFile -Raw | ConvertFrom-Json }

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = Join-Path $root 'server\bin\XppVsCode.Server.exe'
$psi.Arguments = "--config `"$cfg`"" + $(if ($Project) { " --project `"$Project`"" } else { '' })
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$p = [Diagnostics.Process]::Start($psi)
$out = $p.StandardOutput.ReadToEndAsync()
$err = $p.StandardError.ReadToEndAsync()

$p.StandardInput.Write('{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"call","version":"1"}}}' + "`n")
if ($ListTools) { $p.StandardInput.Write('{"jsonrpc":"2.0","id":9999,"method":"tools/list","params":{}}' + "`n") }
$id = 1
foreach ($c in $calls) {
    $msg = @{ jsonrpc = '2.0'; id = $id++; method = 'tools/call'; params = @{ name = $c.name; arguments = $c.arguments } } | ConvertTo-Json -Depth 10 -Compress
    $p.StandardInput.Write($msg + "`n")
}
$p.StandardInput.Close()
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { $p.Kill(); 'TIMEOUT' }

foreach ($r in ($out.Result -split "`n" | Where-Object { $_ })) {
    $o = $r | ConvertFrom-Json
    if ($o.id -eq 0) { continue }
    if ($o.id -eq 9999) { $o.result.tools | ForEach-Object { "== $($_.name)`n$($_.description)`n$($_.inputSchema | ConvertTo-Json -Depth 12)" }; continue }
    $c = $calls[$o.id - 1]
    $text = if ($o.error) { 'RPC ERROR ' + $o.error.message } else { $o.result.content[0].text }
    "===== [$($o.id)] $($c.name) $(($c.arguments | ConvertTo-Json -Compress -Depth 5).Substring(0, [Math]::Min(160, ($c.arguments | ConvertTo-Json -Compress -Depth 5).Length)))  isError=$($o.result.isError)"
    if ($text.Length -gt $MaxChars) { $text.Substring(0, $MaxChars) + "`n... [$($text.Length) chars]" } else { $text }
}
if ($p.ExitCode -ne 0) { "exit=$($p.ExitCode) stderr: " + $err.Result.Trim() }
