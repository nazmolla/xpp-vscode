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

## More findings (don't redo)
- **X++ language server is a stub**: it only advertises document sync; hover/completion return "Method not found", and VS only enables it with env `UseNewLSPServer`. v1 provides language features itself (TextMate grammar `syntaxes/xpp.tmLanguage.json`, definition/hover via `find_object`).
- **xppc without VS** (14 s, same output as VS): `-metadata=<custom store> -compilermetadata=<RuntimePackagesDirectory> -modelmodule=<model> -output=<pkg>\bin -xmllog=... -log=... -appBase=<PLD\bin> -refPath=<PLD\bin> -referenceFolder=<PLD> -referenceFolder=<custom store>` [+ `-xref -xrefSqlServer -xrefDbName`]. LabelC: `-metadata -output=<pkg>\Resources -modelmodule -names`.
- **Compiler diagnostics**: code errors use paths `dynamics://Class/<name>/Method/<m>` and line numbers counted over the whole object as if methods sat inside the class body (declaration without closing `}`, then methods back to back). `compilerLineToDocumentLine` in `src/xppSource.ts` maps them; `scripts/diag-test.js` verifies.
- `SalesOrderHeaderV2Entity` is obsolete (staging `SalesOrderHeaderV2Staging`).

## Status (v0.2.0, uncommitted work landed after a04400c)
- Done and verified headless: 18 MCP tools (smoke test 16/16), write tools incl. security and data entity extensions, `add_to_project`, code view compose/split round-trip (30 objects, ~7,000 methods), code-view save path, designer bridge (`get_form_tree`, `get_table_fields`, add field/group, hide), xppc build + diagnostics mapping.
- Built but NOT run in VS Code yet: extension UI (code view FileSystemProvider `xpp:/AxType/Name.xpp`, project tree, navigation, form designer webview `src/designer.ts` + `media/`), build command, UDE setup (`src/setup.ts`), pac commands (`pac` not installed here), LabelC (testcust has no label file).
- Test objects in `testcust` to clean up (ask first): `SalesOrderHeaderV2Entity.XvsTest`, `SalesOrderHeaderV2Staging.XvsTest`, `XvsTestRemovalMaintain`, `SalesOrderIntegrationMaintain.XvsTest`, `TradeSalesClerk.XvsTest`, `XvsTestCodeView`, `VendGroup.XvsTest`.

## Next steps (in order)
1. User runs F5 (Extension Development Host) on the TestProj folder: Open Object, edit/save, Form Designer, Build, Check/Connect Environment, Copilot tool use. Fix what breaks.
2. Port back to the VS extension: robust method-name parsing (`ExtractMethodName`, `IsMacroLine`, `SkipBracketed`) and `XppProject` indentation.
3. Completion provider (object names), label file creation tool, delete tool.
4. Debugging spike through DebuggerProxy in vsdbg mode.
5. Packaging: license still undecided (`UNLICENSED`), `vsce package`, publish as `nazmolla`.
