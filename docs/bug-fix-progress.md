# Bug-fix round: progress

Keep this file current so a new session can resume from it (the original chat stalled at ~64 MB).

## Goal

Fix every issue in [ai-tools-test-log.md](./ai-tools-test-log.md), re-test, build, commit and push to
`https://github.com/nazmolla/xpp-vscode.git` (git identity `nazmyeg@gmail.com`). Then re-run the end-to-end scenario
(`the design document`, model `testcust`) with the fixed tools.

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
   - `XvsDemoOrderType`, `XvsDemoReason`: set `UseEnumValue = No` (build errors).
   - Tab pages in the form extensions need a pattern; field-group-bound controls need their fields (warnings).
   - Remaining design items: label file `testcust` labels in en-US and fr-CA applied to captions, a SysTest test class
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

### Open (reported 2026-10-22 14:25, fix after the user's design test run finishes; do not reinstall or reload VS Code during the run)

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
| Port AI tool changes to the VS companion (vsix-companion) | Todo | User 15:1x: every AI tool (MCP) change goes to the VS companion too, now and earlier ones: find_object ranking (round 4, was reverted there), form extension validation speed, pattern defaults on create/update, updating existing fields, query range names, timeout reporting. UI-only features (ERD, Events nodes, designer, code intelligence) stay VS Code only. Build with vsix-companion\build.cmd Release, deploy without launching VS. |
| Best-practice review in plans | Done | User choice (15:3x): plan + tool checks, warn only. System prompt PHASE 2 has a mandatory best-practice review checklist (data model, UI, security, code, extensibility); CoC naming fixed to end with _Extension. |
| Best-practice warnings from the tools | Done | After every create/update: fields without EDT/label, literal texts (not labels), tables without alternate key / clustered index / CacheLookup / TableGroup / AutoIdentification / AutoLookup, enum/EDT/menu item/security labels, menu items no privilege of the model grants, CoC classes not final / not ending _Extension, literal texts on form extension controls. Indexes accept alternateKey. |
| Regression | Done | 139/139 (R5 cases: fields, ranges, bp, patterns). |

