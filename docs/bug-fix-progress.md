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
| U10 spike | In progress | docs/u10-code-intelligence-spike.md |
| Release (VS Code), port to VS, deploy, DevExt docs | Todo | |
