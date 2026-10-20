# Copilot instructions: xpp-vscode

## What this project is
A VS Code extension that brings Dynamics 365 Finance & Operations (D365FO) X++ development to VS Code and its AI agents, without Visual Studio. It is the VS Code successor of the Visual Studio extension "X++ AI Copilot Companion" (source: `C:\Users\Mohamed Nazmi\OneDrive - Long View Systems Corporation\Documents\DevExt\vsix-companion`, a separate repo and conversation). Goal: a public extension anyone can install from the Marketplace.

Target environments: unified developer environments (UDE, Power Platform hosted). Windows only.

## Architecture
- `src/` — TypeScript extension.
  - `environment.ts` reads the active D365FO configuration: registry `HKCU\Software\Microsoft\Dynamics\AX7\Development\Configurations` → `CurrentMetadataConfig` (JSON under `%LOCALAPPDATA%\Microsoft\Dynamics365\XPPConfig`). Shared with Visual Studio.
  - `mcpProvider.ts` registers the MCP server via `vscode.lm.registerMcpServerDefinitionProvider` (`contributes.mcpServerDefinitionProviders` id `xpp.mcp`). Launches `server/bin/XppVsCode.Server.exe --config <json> [--project <rnrproj>]`. Project = `xpp.project` setting, else first `**/*.rnrproj`.
- `server/` — .NET Framework 4.8 x64 MCP server (stdio, one JSON-RPC message per line; stdout is protocol only, logs go to stderr).
  - `Program.cs` sets `AssemblyResolve` to `<FrameworkDirectory>\bin` BEFORE touching D365FO types (`Run` is `NoInlining`).
  - `McpStdioServer.cs` transport; `initialize` returns the embedded `Resources/XppCopilotSystemPrompt.txt` as instructions.
  - `Host/` — headless replacements for what Visual Studio provided:
    - `MetadataHost`: `new MetadataProviderFactory().CreateDiskProvider(DiskProviderConfiguration)` + `new MetaModelServiceFactory().Create(provider)` (the `MetaModelService` class itself is internal). `Reset()` reloads after writes.
    - `LabelStore`: label text files `<root>\<Package>\<Model>\AxLabelFile\LabelResources\<lang>\<Id>.<lang>.label.txt` (`Id=Text`, comment lines start with ` ;`, sorted by Id). Writes only under the custom metadata folder.
    - `XppProject`: edits `.rnrproj` (MSBuild XML) the way VS saves it (`<Content Include="AxClass\Name">` + SubType/Name/Link, `<Folder>` entries).
    - `ThreadHelper` no-op shim (ported code asserted the VS UI thread), `BridgeClient` in-process.
  - `Ported/` — code ported from the VS extension; keep diffs minimal so fixes can flow both ways.
    - `MetaModel/MetaModelBridge*.cs` all metadata CRUD, extensions (table/enum/EDT/form), form layout survey, validation.
    - `MetaModel/MetaModelBridgeServer.cs` JSON action dispatcher (`Dispatch` holds a lock; the metadata provider is not thread-safe).
    - `Tools/ToolRouter.cs` the 18 MCP tools; `XRefQuery.cs` where-used via SQL against the cross-reference DB; `ModelObjectLister.cs` lists model contents from disk.

## Hard rules
- Never redistribute Microsoft D365FO assemblies. Compile against `PackagesLocalDirectory\bin` with `<Private>false</Private>`; load them at runtime from the user's installation.
- No Visual Studio dependencies (EnvDTE, Microsoft.VisualStudio.*, Microsoft.Dynamics.Framework.Tools.*) in `server/`. Only DLLs from the packages `bin` folder.
- Metadata changes go through the metadata API, never by writing AOT XML by hand. (`.rnrproj` and label `.txt` files are not AOT metadata and are edited directly.)
- Keep tool output bounded (the agent's context is the budget) and queries narrow.
- Git identity for this repo is repo-local: "Mohamed Nazmi" <nazmyeg@gmail.com>, GitHub user `nazmolla`, remote `https://github.com/nazmolla/xpp-vscode.git`. Never change global git config. Never handle credentials; the user signs in via the browser prompt.

## Build and test
- `npm install`, then `npm run build` (= `build:server` + `compile`).
- `scripts/build-server.ps1` finds `PackagesLocalDirectory\bin` from the active configuration and passes it as `D365BinPath`.
- `scripts/smoke-test.ps1` starts the server over stdio with `TestProj.rnrproj` and runs 16 read-only tool calls with a timeout; all must PASS. Run it after every server change.
- Test environment on this machine: config `org2f1d2adc___10.0.2790.83.json`, custom model `testcust` in `C:\CustomXppMetadata2vxeu0wu.5pd`, test project `C:\Users\Mohamed Nazmi\source\repos\TestProj\TestProj.rnrproj`, XRef DB `(LocalDB)\MSSQLLocalDB` / `XRef_org2f1d2adc100279083`.
- Testing tips: always run the server with a timeout (Process + `WaitForExit(ms)`); never leave a stdio session waiting. PowerShell reports `git push` progress as an error even when it succeeds.

## Research already done (don't redo)
- **UDE setup (what VS "Connect to Dataverse" does)**, from Power Platform Tools (`DevToolsServiceProxy.GetClientAssets`):
  1. Dataverse actions `RetrieveFinanceAndOperationsIntegrationDetails` (TenantId, Url, Id) and `msprov_getfinopsapplicationdetails` (`applicationdeploymenttype` must be `OnlineDev`, `applicationversion`, `platformversion`, `finopsenvironmentid`).
  2. `GET https://developertools.powerplatform.microsoft.com/api/clientmetadata/{resource}?version=<appVersion with dots as dashes>`, headers `Authorization: Bearer <Dataverse token>`, `dataverseEnvironmentUri: https://<org>.crm.dynamics.com/`. Response is a JSON string with a blob SAS URL. Resources: `systemmetadata` (PackagesLocalDirectory.zip), `crossreference` (DYNAMICSXREFDB.bak), `finopsvsix22` (VS extension, also contains the X++ language server), `traceparser`.
  3. Download to `%LOCALAPPDATA%\Microsoft\Dynamics365\<appVersion>\`; unzip PackagesLocalDirectory (long-path prefix, zip-slip check), delete zip. Needs 16 GB free.
  4. Config `XPPConfig\<org>___<appVersion>.json` from Power Platform Tools' `Template_xppconfig.json`: ReferencePackagesPaths=[PLD], FrameworkDirectory=PLD, XRef `(LocalDB)\MSSQLLocalDB` / `XRef_<org><versionNoDots>`, ModelStoreFolder/DebugSourceFolder (`C:\CustomXppMetadata<random>` if none).
  5. XRef restore: grant `NETWORK SERVICE` FullControl on the Dynamics365 folders, `RESTORE DATABASE ... WITH MOVE 'DYNAMICSXREFDB'/'DYNAMICSXREFDB_log'` into `XPPConfig\<config name>\`, delete .bak.
  6. `RuntimeSymLinks\<org>` junctions (`mklink /J`): `bin` → PLD\bin, each reference folder, custom folder as `ZZZZ__<name>`; saved as RuntimePackagesDirectory.
  7. Registry `CurrentMetadataConfig` and `FrameworkDirectory`; stop `xppcAgent`.
  The service is undocumented: ship it as experimental with the manual documented steps as fallback.
- **Build/deploy/sync**: `pac package init|compile|deploy|db-sync --package-type erp` (preview, `.erp/xpp.json`). DB sync Dataverse action: `msprov_dbsynctofinopsasync`; deploy entity `msprov_fnopackage`.
- **Command-line tools in PLD\bin**: xppc, LabelC, xppbp, SyncEngine, reportsc.
- **X++ language server**: `Microsoft.Dynamics.LSP.XppLanguageServer.exe` (in the FinOps VSIX; options `--xppSourcePath`, `--compilerMetadataPath`, `--hostPID`, `--referencePackages`, `--frameworkBin`).
- **Debugging**: VS uses `Microsoft.Dynamics.AX.DebuggerProxy.exe` (in the FinOps VSIX `DebuggerProxy` folder, .NET 8): local TCP port (default 5119) tunnelled over `wss://<env>/___sidecar/AOS1/remote-debugger/connect`, args `--remoteProxyHost --accessToken --aos --VSVersion`. It has an msvsmon mode and a **vsdbg** mode (VS Code's debugger) — attaching from VS Code looks feasible; needs a spike.

## Status (2026-10-20)
- Done: scaffold; all 18 tools ported and passing the read-only smoke test (8 s); extension registers the MCP server and passes the project.
- Not yet verified: write tools (`xpp_create_object`, `xpp_update_object`, `xpp_add_to_project`, `xpp_create_label`) — need test objects in `testcust` (ask the user first); speed of `MetadataHost.Reset()` after each write; loading the extension in VS Code (F5 / Extension Development Host) and Copilot picking up the server.
- Known gaps (also in the VS extension): no data entity extensions (`AxDataEntityViewExtension`), no tool to create a label file (`AxLabelFile`), no delete tool.

## Next steps (in order)
1. Verify the write tools on `Xvs*` test objects in `testcust` (with the user's OK); measure and, if slow, make `Reset()` cheaper.
2. Run the extension in an Extension Development Host; confirm Copilot lists and calls the X++ tools.
3. Add data entity extensions and label file creation (port back to the VS extension).
4. "X++: Connect to environment" command implementing the UDE setup above (spike sign-in first: does a VS Code Microsoft auth session token for the Dataverse scope work against the DevTools service?).
5. Build/deploy/sync commands over `pac`.
6. X++ language server client (syntax, IntelliSense).
7. Debugging spike through DebuggerProxy in vsdbg mode.
8. Form designer (webview) — later milestone.
9. Packaging/publishing: publisher `nazmolla`, license still undecided (`UNLICENSED`), README, `vsce package`.
