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
