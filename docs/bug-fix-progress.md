# Bug-fix round: progress

Keep this file current so a new session can resume from it (the original chat stalled at ~64 MB).

## Goal

Fix every issue in [ai-tools-test-log.md](./ai-tools-test-log.md), re-test, build, commit and push to
`https://github.com/nazmolla/xpp-vscode.git` (git identity `nazmyeg@gmail.com`). Then re-run the end-to-end scenario (model `testcust`) with the fixed tools.

## Status (2026-10-21)

| Step | Status |
|---|---|
| Tool issues #1–#31 | Fixed; all re-tested by `scripts/tool-regression.js` (38/38 pass) except #31 end to end |
| Issues found while re-testing (#32–#35) | Fixed and covered by the regression cases |
| UI issues U1–U5, #8 | Implemented, extension compiles; to confirm by hand in VS Code |
| Server build (`npm run build:server`) and extension compile (`npx tsc -p .`) | Pass |
| README | Updated (Build Project, Run Tests, Delete/Exclude, new agent tools, regression script) |
| Package, install, commit and push | Done: v0.3.0 installed in VS Code, pushed as `1f1997e` |

## Visual Studio extension sync

The fixes and new tools from this round (not the test features) are ported to the Visual Studio extension
(nazmolla/XppAiCopilotCompanion, v0.5.2): delete, exclude from project, select project, and build (off the VS UI thread).
Still to check by hand inside Visual Studio: delete/exclude/select through DTE and a long `xpp_build`.

## Still to do

1. In VS Code, confirm U1–U5 and #8: method indentation in the code view, Build Project, a Fatal build reported as
   failed, Delete / Exclude from Project, the inline Add to Project button, and the MCP server restarting after a
   project switch.
2. Run `xpp_run_tests` / Run Tests once against a deployed package (#31). Local (no deploy) test runtime: experimental, see [experimental/local-runtime](../experimental/local-runtime/README.md) — kernel boots in-process; blocked on table security.
3. Re-run the end-to-end scenario in `testcust` with the fixed tools. Known fixes for objects created before this
   round (the last `xpp_build` reports them):
   - Enums created before the fix: set `UseEnumValue = No` (build errors).
   - Tab pages in the form extensions need a pattern; field-group-bound controls need their fields (warnings).
   - Remaining design items: labels in every language applied to captions, a SysTest test class
     for the validator/message/processor logic, then a clean build.

## Round 2 (2026-10-22): open items #37-#46, XRef note, U6-U10

Order: #39, #40, #41+#42, #38, #37, #43+#44, U6, U7, U8, U9, #45, #46, XRef (optional), U10 (spike doc only), release, VS port.
Per item: reproduce with the server binary, fix, add a case to `scripts/tool-regression.json` (probe objects `XvsProbe*`,
deleted by later cases), run `node scripts/tool-regression.js --project C:\Users\Mohamed Nazmi\source\repos\TestProj\TestProj.rnrproj`,
mark Fixed in the test log. `testcust`/TestProj hold the user's current run (`HS*` objects): never change or delete those.

Decisions:
- #48: full composite entity support this round (user, 2026-10-22).
- U7: after selecting or creating a project, switch automatically to a multi-root workspace with the project folder and the model folder (user, 2026-10-22).

| Item | Status | Notes |
|---|---|---|
| Log entries #37-#46, U6-U10 committed | Done | `b8e3104` |
| Baseline regression | 41/42 | #12 #16 case used `SalesTable.testcust` (gone from the user's run); now self-contained (`CustGroup.XvsProbe`) |
| #39 (+ new #47) | Done | 57/57 pass. Harness has `--issue <regex>` for quick runs. Server built, not yet packaged/released |
| #40 (+ new #48 composite entities) | Done | 72/72 pass. User asked (2026-10-22) for full AxCompositeDataEntityView create/update + staging checks |
| #41 #42 | Done | updateControls / removeControls / removeControlModifications; modifiable list from AxFormExtensionValidator |
| #38 | Done | 87/87 pass |
| #37 | Done | 90/90 pass |
| #43 #44 | Done | 104/104 pass. Form extensions: pattern auto-values + compiler validators after save with rollback; layout shows pattern guidance |
| U6 U7 U8 U9 | Done | All compile; confirm by hand in VS Code. Harness: cases with "method" send raw JSON-RPC (xpp/bridge) |
| #45 #46 XRef | Done | 120/120 pass |
| U10 spike | Done | docs/u10-code-intelligence-spike.md |
| Release (VS Code), port to VS, deploy, DevExt docs | Done | VS Code v0.4.0 installed (`52545dd`); VS extension 0.5.6 built, installed with VSIXInstaller, DevExt docs updated, pushed (`f41e06b`) |

Round 2 is complete. Still to check by hand:
- VS Code: U6 (project view refresh, empty-project text, Create D365FO Project), U7 (multi-root workspace switch),
  U8 (label editor), U9 (F12 / Peek / Shift+F12 / hover in `xpp:` documents).
- Visual Studio (0.5.6): the ported tools inside VS (no automated run there); `xpp_create_label` uses the VS label
  controllers, security label search resolves labels through them.
- Version note: the VS extension went 0.5.2 -> 0.5.6 because auto-version bumps on every build attempt.
- Next work, if wanted: the U10 backlog (docs/u10-code-intelligence-spike.md), P1 items first.

## Round 3 (2026-10-22): code intelligence (U10 backlog)

User decisions (2026-10-22):
- Build the U10 backlog. Slow features get an on/off setting with UI in the Dynamics 365 view (bottom-left tools tree).
- CoC: Visual Studio's ExtensionOf lookup is unreliable and hangs VS for "which CoC extends this method"; build a better one.
- Visual Studio's X++ language service may be used (developers have VS / its components); also look for open-source alternatives.

| Item | Status | Notes |
|---|---|---|
| CoC / extension index investigation | Done | XRef: ExtensionOf attribute + TypeReference on same line = extended element; wrapper = MethodCall to same-named method. Pre/Post/SubscribesTo same way. Form/data event handlers not in XRef: class-file scan (cached). Server: `ExtensionIndex.cs`, `CodeIntel.cs` (xpp/lang), `MetaModelBridge.Members.cs` (type_members). Compiler parser Pass1 works standalone (syntax). VS SourceCodeFormatter needs a running VS -> own formatter. No open-source X++ parser exists. |
| Settings UI (Code intelligence node, toggles) | Done | Code intelligence node with checkboxes in the tools tree; 11 `xpp.codeIntelligence.*` settings; slow ones marked. |
| Outline / document symbols | Done | Macro spans filtered, selection range clamped. |
| Implementations (overrides, CoC, handlers) + code lens | Done | SalesFormLetter: 9 CoC classes, 5 implementations of run. |
| Call hierarchy, type hierarchy | Done | run: 44 incoming / 47 outgoing; 10 subtypes. |
| Completion, signature help, label completion | Done | Members incl. base types and CoC extension methods; `::` statics; @ labels; multi-line signatures. |
| Hover (labels, EDT, enum) | Done |  |
| Workspace symbols | Done |  |
| Diagnostics on save | Done | Off by default (slow). |
| Live syntax diagnostics, formatting, semantic highlighting | Done | Compiler Pass1 parser; own formatter; semantic tokens off by default. |
| Release + VS port (tools only) | Done | 0.5.0 released and installed (mnazmi.xpp-vscode). Smoke test test/codeIntelSmoke.js all pass; regression 120/120. Publisher renamed MNazmi, Apache-2.0, preview. VS port not needed (xpp/lang is VS Code UI only). |

Published to the VS Code Marketplace as `MNazmi.xpp-vscode` (0.5.0, then 0.5.1 with homepage/issues links), public + preview. Publish later versions with `npx @vscode/vsce publish` after `vsce login MNazmi`. The repo nazmolla/xpp-vscode is being made public by the user so the listing links work.

## Round 4 (2026-10-22): user feedback on 0.5.1

| Item | Status | Notes |
|---|---|---|
| Open X++ object search buries exact names (SALESTABLE listed classes only) | Done | find_object collects all candidates (cap 5000), ranks exact > prefix > shorter, then resolves models for the top N. |
| View References: entries not clickable | Done | Diagram shows each reference (member, line, kind) under its element; click opens the code at the line (same as Find References). |
| Designer root: Application Explorer right-click actions | Done | Root node carries the element context; webview/context entries mirror the AOT element menu (xppRoot). |
| ERD (VS Code only) | Done | View Entity Relationship Diagram on tables (AOT, project, designer root). Levels 1-5, outgoing / incoming / both, keys or all fields; settings xpp.erd.*; changeable in the diagram; double-click re-centres; export SVG / Mermaid, copy Mermaid. Server: table_erd, incoming_relations (table XML scan, cached, ~2 s cold / 0.1 s warm). |
| Events nodes, copy event handler, find event handlers | Done | Events folders on tables, entities, forms, data sources, data source fields, controls (VS event enums). Copy Event Handler Method (double-click too) generates VS's handler; methods give Pre/Post or delegate subscriber. Find Event Handlers uses the extension index (xpp/lang event_handlers). |
| Designer tree looks like VS | Done | Codicons per node type (bundled media/codicons), chevrons (the gray blob was formPreview.css's .toggle switch style), VS node order per element type, inline class methods / enum values / child controls / entity view metadata, Name (Control Type), root Name [Model], no counts. |
| AOT categories in VS order; AI > Skills | Done | Data Types, Data Model, Code, User Interface, AI (Skills = AxSkill), Analytics, Reports, Business Process and Workflow, Label Files, Resources, Configuration, Security, References, Services. Folder names corrected (AxWorkflowTemplate, report templates, providers, direct access permissions). System Documentation (kernel) not available. |

### Open (reported 2026-10-22 14:25, fix after the user's end-to-end test run finishes; do not reinstall or reload VS Code during the run)

| Item | Status | Notes |
|---|---|---|
| Select D365FO Project does not set the workspace | Done | Re-selecting the configured project changed no setting, so the setting listener never synced; selectProject now syncs directly in that case. |
| Empty-project view keeps Select / Create D365FO Project buttons | Done | Empty selected project shows a hint only; Select/Create stay in the view title bar. |
| Extension designer shows only the extension's own nodes | Done | Server merges the base element tree (inherited, read-only, properties read from the base) with the extension's own nodes (bold) for every extension type; extension-only folders (modifications) at the end. Other extensions' nodes are not shown. |
| New tab pages in form extensions get no sub-pattern | Done | New containers get the first allowed non-Custom sub-pattern of their slot (latest version) plus its required values. |
| Setting Pattern with updateControls does not apply the pattern's required values | Done | updateControls targets get their pattern's required values; unknown/missing PatternVersion becomes the latest version (reported). |
| Form extension validation is very slow on big forms | Done | Cause: AxFormExtensionValidator validates+merges every other extension first (~60 s on SalesTable, twice per update). Its FormExtensionLoader is replaced (reflection) by one listing only this extension and those it builds on; pattern validator only on own + modified extensions. SalesTable update 120 s -> ~7 s. |
| Cannot change an existing field (EDT, type, label) | Done | updateFields (EDT/enum/label/help/properties, same base type only), removeFields/removeIndexes/removeFieldGroups/removeRelations, same-name index/group/relation replaced; base-type change refused; removed fields remembered (%LOCALAPPDATA%\XppVsCode\removed-fields.json, also on delete) so a name never returns with another base type; removal refused while own indexes/relations use the field. |
| Query create with unnamed ranges: unclear error | Done | Range name defaults to the field (SalesStatus, SalesStatus1, ...); 'Update failed' unwraps reflection exceptions. |
| Form extension update times out but saves | Done | Updates now ~7 s; create/update descriptions tell the agent to read the object before retrying after a client-side timeout (the server has no timeout of its own). |
| Port AI tool changes to the VS companion (vsix-companion) | Done | VS companion 0.5.10 (DevExt 9835552): ranked find_object, scoped form extension validation, pattern defaults/versions, updateFields/remove*, range names, best-practice review + warnings; DevExt README/ARCHITECTURE/copilot-instructions updated. Installed with VSIXInstaller (exit 0); the running XppMcpServer belongs to an elevated Visual Studio and could not be stopped: restart VS to load it. Built only, not run against the environment inside VS. |
| Best-practice review in plans | Done | User choice (15:3x): plan + tool checks, warn only. System prompt PHASE 2 has a mandatory best-practice review checklist (data model, UI, security, code, extensibility); CoC naming fixed to end with _Extension. |
| Best-practice warnings from the tools | Done | After every create/update: fields without EDT/label, literal texts (not labels), tables without alternate key / clustered index / CacheLookup / TableGroup / AutoIdentification / AutoLookup, enum/EDT/menu item/security labels, menu items no privilege of the model grants, CoC classes not final / not ending _Extension, literal texts on form extension controls. Indexes accept alternateKey. |
| Regression | Done | 139/139 (R5 cases: fields, ranges, bp, patterns). |

0.7.0 published to the Marketplace (VS Code repo 8cce940).

0.7.1: README screenshots of standard objects only (docs/images, captured with scripts/screenshots.ps1 + test/screenshots.js in a throw-away profile without a project); fixes: View References separator showed '?' (PowerShell pipe mangled the middle dot), form preview rendered an action pane's buttons twice when it has no tabs.

## Round 6 (2026-10-23): find and reuse standard code, developer productivity

User request: tools developers use in other languages; above all "find a similar standard implementation and reuse or copy it" as tooling and as agent instructions. Chosen: 1 examples, 2 code search, 3 usage in context, 4 precedent step, 5 snippets, 6 quick fixes, 8 obsolete warnings, 9 SysTest generation, 10 run snippet; VS companion gets the agent-tool parts. Not chosen: rename.

| Item | Status | Notes |
|---|---|---|
| xpp_find_examples / xpp/lang find_examples | Done | Server/Ported/Tools/CodeExamples.cs. By task: ~25 framework recipes (SysOperation, data contract, number sequence, business event, report DP, service, lookup, workflow, CoC, handlers, tests, ...), smallest focused examples first; forms by Design pattern (cached scan %LOCALAPPDATA%\XppVsCode\cache\form-patterns.json); fallback by class name. By object: XRef objects of the same kind sharing the most distinctive references (IDF-weighted), reason lists the shared targets. Excludes the user's custom models and Microsoft test packages (unless tests are asked for). |
| xpp_search_code / xpp/lang search_code | Done | Server/Ported/Tools/CodeSearch.cs: parallel scan of method sources and declarations (classes, tables, forms, views, entities, queries, maps) in all metadata folders; literal or regex; objectTypes/model filters; first search reads from disk, then OS cache (SysOperationServiceController in classes: ~50 ms with early stop). Nested form methods get short paths (DataSources/ds/Fields/f/Methods/m, Controls/c/Methods/m). |
| Short element paths | Done | WalkElementPath normalizes DataFields -> Fields and Controls/<name> -> full Design path, so XRef and search paths open in the code view. |
| xpp_find_references includeCode | Done | Shows the calling code lines per reference (method source read from the metadata file, token match). |
| Obsolete index / xpp/lang obsolete | Done | XRef [SysObsolete] attribute users + message/isError parsed from the source; cached per XRef state (obsolete-<db>.json); 3,645 items here. |
| Precedent step in agent instructions | Done | System prompt: "STANDARD PRECEDENT FIRST" in PHASE 1 (find with xpp_find_examples / xpp_search_code / includeCode, read, reuse > extend > copy), plan lists the precedent per object, tool-first rule 4b. |
| VS Code UI (search, examples, obsolete diagnostics, snippets, quick fixes, SysTest, run snippet) | Done | src/productivity/, snippets/xpp.json. Smoke (test/productivitySmoke.js via test/runInDownloadedCode.js, a downloaded VS Code): commands registered, obsolete warning shown, Extract to label and Implement interface methods offered, 40 snippets. Not run end to end (need input): Generate find/exist, CoC wrapper, Add to Privilege, Generate SysTest, Run Snippet. |
| Regression | Done | 149/149 (R6: 10 cases). |
| VS companion port | Done | 0.5.14: mcp-server CodeSearch.cs, CodeExamples.cs (cache folder XppCopilotCompanion), XRefQuery helpers, ToolRouter tools + includeCode, prompt. |

0.8.0 released (round 6).

## Round 7 (2026-10-23): end-to-end run 3 findings (0.8.0, all tools)

Run: the design built as 46 objects, build 0 errors / 93 warnings (all ReferencedObjectIsObsolete from
the dual-write header entity chain). Precedent step worked (every object named its OOB precedent and reuse/extend/copy).
Chat dump and tool statistics kept with the session.

| # | Tool | Finding | Status |
|---|---|---|---|
| R7-1 | xpp_find_examples | Plain-language queries without a recipe word fall back to class-name matching; "similar to" a menu item returns nothing; ~13 s per call. | Fixed: free text scores class names (all words first, stemmed, stopwords dropped, no test classes) then method names; similar-to a menu item compares what it opens; explanations only for the top 5. |
| R7-2 | xpp_search_code | First searches 28-44 s (only the first is documented as slow); model filter with a package name (ApplicationSuite) silently scans 0 files; scanned counts vary between identical calls (parallel early stop). | Fixed: model filter accepts a package (all its models); unknown model/package is an error; truncated results explain the varying counts; description says how to make it fast. |
| R7-3 | xpp_find_references | Missed a call to findActiveLine from inside the same class; fields added by a table extension are only found when searching the extension; includeCode code lines also match an enum of the same name; menu items say "not built with cross reference" (misleading). | Fixed: member queries include calls from the same object; table member queries include fields added by table extensions; includeCode prefers .member/::member lines; clearer no-entries message (menu items point to xpp_analyze_security op=access). |
| R7-4 | xpp_read_object | methodNames together with the methods section reported the method missing; one method of SalesTable still returned 48 KB; a composite query shows no data sources or ranges. | Fixed: methodNames gets its own Methods section (and selects it); large reads with methodNames return only the methods; composite queries show their ranges (typedMetadata) and the base query's data sources. |
| R7-5 | xpp_create_object (form patterns) | New tab pages on AssetTable and CustParameters got the DimensionEntryControl sub-pattern (first allowed) instead of FieldsFieldGroups, then CustParameters rejected the tool's own choice; missed the pattern on the new line-details tab of SalesTable (build flagged it). | Fixed: sub-pattern chosen by structural fit with the control's children (FieldsFieldGroups first on ties); new FastTab pages outside any pattern node (SalesTable LineDetailsTab) get the best-fitting pattern. |
| R7-6 | xpp_update_object | Menu items cannot be updated at all (so the privilege best-practice check cannot be re-run either). | Fixed: generic update (MetaService.Get/Update by reflection) for menu items, services, tiles, configuration keys, ... |
| R7-7 | xpp_get_object_type_schema | Lists HelpText as an AxEnum property, which create rejects. | Fixed: schema omits read-only scalars (AxEnum.HelpText alias). |
| R7-8 | xpp_search_docs | A developer question (posting journals in code) returned an end-user page. | Fixed: code questions add 'developer' and rank dev-itpro pages first; Business Central pages dropped. |
| R7-9 | server | Tool calls run one at a time; parallel calls queue. | Done: read-only tools run in parallel (ParallelTools in McpStdioServer). |
| R7-10 | best practices | Alternate key / CacheLookup warnings on an append-only log table are noise the agent has to justify. | Decided: keep the warnings. |

Security (R8, user request): server SecurityAnalysis.cs + xpp_analyze_security (license, access, gaps, sod, report, roles,
children, refresh) done and spot-tested; system prompt security-design rules added; VS Code Security view/panel/commands
built (src/security/*), host smoke test test/securitySmoke.js passes. R7 + R8 regression cases added: 175/175 pass.
Released 0.9.0 (commit d27e06c; Marketplace publish pending: token revoked). VS companion 0.5.20 ported, deployed, DevExt docs updated (2e72bd2). Round 7 and security complete.
