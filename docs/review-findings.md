# Review findings (2026-11-05)

Architecture review, SAST (manual, .NET analyzers, Semgrep, npm audit) and DAST (30 probes against the server).
Status: Open / Fixed / Partial. Verified with `npm run build`, `npm run test:server` (43 unit tests + contract check), `scripts/smoke-test.ps1` (16/16), `node scripts/security-probes.js` (34/34), the designer drop test and the extension-host smoke tests (drag and drop, Go To, history).

## Security

| ID | Sev | Finding | Status |
|---|---|---|---|
| S1 | Critical | `update_object` (bridge, `xpp_update_object`, `xpp_update_current_object`) writes to objects of standard models: no ownership check on the server. DAST D11 overwrote `Global`'s declaration. Ownership is checked per handler, not centrally. | Fixed: `IWritePolicy` checked by the dispatcher for every edit and create action (DAST D11, D11b) |
| S2 | High | `create_object` accepts any model the metadata API resolves (e.g. `Foundation`), not only custom models. | Fixed: `TargetModelDenial` for `create_object`/`duplicate_object` (DAST D10b) |
| S3 | High | `xpp_search_docs` URL allow-list is a prefix check: `https://learn.microsoft.com.invalid/...` and `https://learn.microsoft.com@127.0.0.1:9/` are fetched (SSRF, prompt-injection channel). Redirects are followed unchecked. | Fixed: `IsLearnUrl` (https, exact host, default port, no user info) on the URL and on every redirect hop (DAST D25-D28) |
| S4 | Medium | Responses carry unescaped control characters (DAST D30: NUL in a name), so the JSON-RPC line is invalid and the client waits for its timeout. | Fixed: one escaper (`JsonHelpers.EscapeJsonString`) escapes all control characters, U+2028/2029 (DAST D30) |
| S5 | Medium | `xpp.pacPath` and `xpp.debuggerProxyPath` can be set by a workspace (no `machine` scope) and are executed; pac runs through a shell (`exec`). No `capabilities.untrustedWorkspaces`. | Fixed: both settings `machine` scope; pac without a shell unless it is a .cmd shim, quotes refused; `untrustedWorkspaces.supported = false` |
| S6 | Low | Webview nonces from `Math.random` (security panel, diagrams, ERD). | Fixed: `crypto.randomBytes` in every webview |
| S7 | Low | Label file IDs and languages are not validated before `Path.Combine` in read paths (`LabelStore`). | Fixed: ID and language patterns before any path is built (DAST D20, D20b) |
| S8 | Low | Errors show "Exception has been thrown by the target of an invocation" instead of the cause (DAST D04-D06). | Fixed: `MetaModelBridge.Cause` unwraps reflection/aggregate exceptions in result messages and the dispatcher |
| S9 | Low | `RegExp` built from a webview path value (element designer method parameters); hard-coded `C:\XppLocalRuntime` path in the experimental runtime. | Fixed: name escaped; trace file in the temp folder |

## Architecture and code quality

| ID | Sev | Finding | Status |
|---|---|---|---|
| A1 | High | No central policy for write actions: ownership is checked inside each handler (root cause of S1/S2). | Fixed with S1 (`IWritePolicy`, `EditActions`/`CreateActions` in the dispatcher) |
| A2 | High | Bridge action names are string literals on both sides (about 52 server cases, 38 client strings) with no shared contract. | Fixed: `src/bridgeContract.ts` (`BridgeAction` union, `BridgeResult`) types every `bridge()`/`call()`; `scripts/check-bridge-actions.js` fails when the contract and the server differ (`npm run test:server`) |
| A3 | Medium | Hidden global state: `ElementDesigner` static panel registry/active panel, `ElementDragAndDrop.lastDrag`, server static caches. | Fixed: the last drag is state of the one controller, injected into designers with `DesignerServices`. Kept by design: the panel registry (one panel per element; VS Code webview menus don't say which panel) and the server's locked, invalidated caches |
| A4 | Medium | Swallowed errors: 42 empty `catch { }` in the server, 34 `.catch(() => undefined)` in the extension. | Fixed: 23 server sites (caches, indexes, descriptors, environment reads) now log via `LogIgnored`/`McpLogger`. Kept silent by design: reflection value probes, logger self-protection, pattern checks of arbitrary controls; extension catches are expected absences (no environment, file or project yet) in editor features |
| A5 | Medium | JSON built by hand in places (`McpStdioServer`, `JsonHelpers`) next to `JavaScriptSerializer` (cause of S4). | Fixed for correctness: one escaper for all hand-built JSON (S4), covered by unit tests |
| A6 | Medium | `bridge<T = any>` and 50 `any` in the extension: server results are untyped. | Fixed at the server boundary: `bridge`/`lang`/`call` return `BridgeResult` (no `any` in `serverClient.ts`); the new types found 10 unchecked `message` uses, fixed |
| A7 | Medium | Reflection without caching (187 call sites; `GetProperties` per call). | Fixed on the hot paths: `PublicProperties(Type)` cache used by the designer, dump, node edits, references and typed-metadata mapping |
| A8 | Medium | God objects: `MetaModelBridge` (25 partial files, 12.2k lines, `IMetaModelBridge` 56 members), `elementDesigner.ts`, `d365Tools.ts`, `codeIntel/providers.ts`. | Fixed for the server: `IMetaModelBridge` replaced by nine role interfaces (`MetadataServices.cs`); the dispatcher depends on `BridgeServices` (composed in `Program`); models, labels, project packages, table relations and builds are separate services over `IMetadataCore`. Code moved verbatim; `scripts/golden-master.js` (94 responses) is identical before and after. The element-centred roles (objects, discovery, designer, projects) stay on `MetaModelBridge`: they share its typed-metadata mapping. The TypeScript modules are unchanged |
| A9 | Medium | No unit tests: no C# test project; extension tests are integration smoke tests; the security probes live outside the repo. | Fixed: `test/server` xUnit project (43 tests: JSON escaping, URL allow-list, label names, write policy through the dispatcher), `npm run test:server`; `scripts/security-probes.js` (34 DAST probes) in the repo; both in `.github/copilot-instructions.md` |
| A10 | Low | Culture-sensitive string operations (CA1305/CA1310: 119 hits). | Fixed: all CA1310/CA1307 (ordinal comparisons), invariant culture where metadata values are converted; CA1310/CA1307 are build warnings now (`server/.editorconfig`). Remaining CA1305 are integer formatting and SQL scalars, culture-neutral |
