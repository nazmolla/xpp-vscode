# Reads a development configuration with Visual Studio's own parser (FinOps tools MetadataRuntimeConfiguration), the way
# Visual Studio's "Manage local XPP configurations" does: a file it cannot read is silently left out of the list.
# Usage: powershell -File scripts\vs-config-probe.ps1 <FinOpsTools folder> <config.json>   Prints OK or FAIL <reason>.
param([Parameter(Mandatory)][string]$ToolsFolder, [Parameter(Mandatory)][string]$ConfigFile)
$ErrorActionPreference = 'Stop'
$code = @"
using System; using System.IO; using System.Reflection; using System.Runtime.Serialization.Json;
public static class VsConfigProbe {
  public static string Read(string dll, string file) {
    AppDomain.CurrentDomain.AssemblyResolve += (s, e) => { var p = Path.Combine(Path.GetDirectoryName(dll), new AssemblyName(e.Name).Name + ".dll"); return File.Exists(p) ? Assembly.LoadFrom(p) : null; };
    var t = Assembly.LoadFrom(dll).GetType("Microsoft.Dynamics.Framework.Tools.Core.Configuration.MetadataRuntimeConfiguration", true);
    try { using (var s = File.OpenRead(file)) { new DataContractJsonSerializer(t).ReadObject(s); return "OK"; } }
    catch (Exception ex) { return "FAIL " + ex.Message; }
  }
}
"@
Add-Type -TypeDefinition $code -ReferencedAssemblies System.Runtime.Serialization, System.Xml
$dll = Get-ChildItem $ToolsFolder -Filter 'Microsoft.Dynamics.Framework.Tools.Core.*.dll' | Select-Object -First 1
if (-not $dll) { 'FAIL Microsoft.Dynamics.Framework.Tools.Core was not found in ' + $ToolsFolder; exit 1 }
$result = [VsConfigProbe]::Read($dll.FullName, $ConfigFile)
$result
if ($result -ne 'OK') { exit 1 }
