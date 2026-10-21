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
