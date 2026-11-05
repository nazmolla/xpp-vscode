# F&O Studio roadmap

One backlog for the extension: the designer and product manager reviews of this repo, the applicable ideas from the
earlier review of the Visual Studio extension (`docs/session-handoff-2026-11-05.md`), and database access.
Status: **Done**, **In progress**, **Planned**, **Blocked** (with the reason).

Work order: verification first, then database access (DB), quick UI wins, the daily development loop, the
performance tools, then the larger lifecycle items.

## 0. Verify what is built

| ID | Item | Status |
|---|---|---|
| V1 | Run U1–U9 (`docs/bug-fix-progress.md`) in VS Code (F5) and confirm each | Automated part done: `node scripts/run-smoke.js` runs the 9 extension-host suites in a real VS Code (all pass); a manual F5 pass is still open |
| V2 | Debugger: confirm a breakpoint hit end to end on a UDE | Planned (needs a UDE session) |

## 1. Database access (DB)

Cloud-hosted and VHD environments have AxDB on the same machine (Windows authentication). Unified developer and
sandbox environments (UDE/USE) use Azure SQL with just-in-time credentials, the way Visual Studio's
"SQL Credentials for Dynamics 365 FinOps" does (Dataverse action `msprov_getfinopssqljitaccessasync`).

| ID | Item | Status |
|---|---|---|
| DB1 | Shared Dataverse client (one module for actions, records and errors) | Done |
| DB2 | Request database credentials (UDE/USE): reason, IPv4 address or range, Reader/Writer; kept in VS Code secret storage until they expire | Done; verified on a live UDE (Reader and Writer, ISO UTC expiry of about 16 h, activation takes up to a few minutes and is retried) |
| DB3 | One database connection for every environment type (Windows authentication locally, credentials in the cloud), with a status bar item and renew/forget actions | Done |
| DB4 | Server data service (`xpp/data`): parameterized table browse, read-only queries (SELECT only, always rolled back), row and cell limits; only enabled for the extension's own server process | Done (`scripts/data-test.js`, unit tests) |
| DB5 | Browse Data on tables (Application Explorer, project, designer): enum names, company filter, column filters, sorting, row limit, export CSV | Done |
| DB6 | Query panel for free SQL (read-only by default; writes need a switch and a confirmation and warn that X++ business logic is skipped) | Done |
| DB7 | Copilot tool `xpp_query_data` (SELECT only, row limit, confirmation, never sees credentials) | Done |
| DB8 | Open in SQL Server Management Studio / the SQL Server (mssql) extension | SSMS done (Windows authentication, or user name with the password on the clipboard). mssql: blocked; its API can't create a connection with a password, and writing one to settings would leak it |
| DB9 | As-of date for date-effective tables in Browse Data | Done |

## 2. User interface (UI)

| ID | Item | Status |
|---|---|---|
| UI1 | Getting Started walkthrough: connect environment, pick project, open an element, build, browse data, ask Copilot | Done |
| UI2 | Command palette cleanup | Done: the Go To and designer commands were already hidden; view-only toggles are now hidden too |
| UI3 | Element designer as a real editor tab: dirty state, Ctrl+S, undo/redo, revert | Done: undo/redo (Ctrl+Z / Ctrl+Y) of property changes; properties save immediately through the metadata API, so there is no dirty state |
| UI4 | Editable form designer: drag fields from a field list, reorder controls, properties beside the preview | Done (existing): the form designer adds fields and groups, hides controls and changes labels through a form extension; the element designer adds, deletes and edits nodes with properties beside the tree |
| UI5 | Status bar: server state (starting, stopped with restart), project/model, last build result | Done |
| UI6 | File icon theme per element type for `xpp:` tabs, breadcrumbs and Quick Open | Done: X++ language icon, and file decorations with the element type and git state (a full icon theme would replace the user's own) |
| UI7 | Compare in VS Code's native diff editor | Done (existing): Compare Code and Compare Metadata already open `vscode.diff` |
| UI8 | Infolog view: severity filter, search, group by session | Done: severity and text filter, copy |
| UI9 | Accessibility and theming pass on every webview (theme colors, high contrast, keyboard, ARIA) | Done: shared `media/a11y.css` (focus rings, high contrast, reduced motion) in every webview, ARIA tree in the element designer |
| UI10 | Hover cards for elements: model, layer, label, extension and reference counts, quick actions | Done: type, model, label and links to the designer and references |
| UI11 | Review agent changes: after Copilot creates or changes an element, offer View diff / Open / Undo | Done: Copilot Activity lists the changes with Open; git decorations and Review My Changes show the diff (undo through git) |
| UI12 | Copilot activity log: the tool calls the server answered, with arguments and timing | Done (Copilot Activity view) |
| UI13 | Consistent error messages (what failed, why, what to do next) | Done: every command failure is logged with its stack and offers Show Log; server errors name the command that fixes them |
| UI14 | One product name: command categories "F&O Studio" instead of "X++" / "Dynamics 365" | Done |

## 3. Features (F)

| ID | Item | Status |
|---|---|---|
| F1 | Test Explorer integration (discover the project's SysTest classes, run on the environment, result per test method) | Done (runs on unified environments, like Run Tests) |
| F2 | `@fno` chat participant: `/extend`, `/report`, `/batch`, `/security`, `/explain`, `/design` | Done: `/design`, `/extend`, `/explain`, `/review`, `/support`, `/performance` (reports, batch jobs and security go through `/design` and the agent tools) |
| F3 | Rename (methods, fields, elements) with cross-reference updates | Done for methods of your models (calls from the cross-reference database, preview first); fields and elements planned |
| F4 | New Object wizards: table + form + menu item + privilege; SysOperation batch; report; data entity; integration (entity, staging, service, security) | SysOperation batch job, report, approval workflow and custom service done; table + form + menu item + privilege and data entity planned (the agent tools and `@fno /design` create them today) |
| F5 | On-demand best-practice check (`xppbp`) into Problems, with quick fixes | Done (quick fixes come from the existing code actions) |
| F6 | Label tooling: translate missing languages, unused labels, import/export | Translate and unused labels done; import/export planned |
| F7 | Git-aware project view: changed elements, "changed in this branch", readable metadata diffs | Done |
| F8 | Database sync of selected tables | Done for cloud-hosted and VHD (SyncEngine); not run here (this machine has a UDE, which syncs through `pac`) |
| F9 | Trace Parser integration (open a trace, jump to code) | Done: replaced by Analyze Trace (P7); every method opens in the code view |
| F10 | Review My Changes: best practice, CoC without `next`, missing security, literal text, for the branch diff | Done |
| F11 | Team knowledge pack: per-repo naming, prefix and pattern rules the guide and validation follow | Done: `.xpp/agent-rules.md` joins the agent instructions |
| F12 | Copilot tools: `xpp_check_best_practice`, `xpp_diff_changes`, `xpp_find_deprecations`, `xpp_package` (asks first) | Done |

## 4. Workflows (W)

| ID | Item | Status |
|---|---|---|
| W1 | Pre-deploy checklist: build, best practice, tests, security gaps, missing labels, then package and deploy | Done (Pre-Deploy Checklist; tests stay a separate run because they need the deployed code) |
| W2 | VS Code tasks for builds (`type: xpp`, `buildProject` / `buildModel`) | Done: no problem matcher needed, builds write to Problems directly. Deploy and sync tasks planned |
| W3 | Upgrade readiness report per model: obsolete calls, CoC wrappers of changed methods, broken extensions | Done: obsolete API uses (cross references); Chain of Command wrappers whose target is gone, changed signature or is no longer wrappable; event handlers whose target is gone; table, form and enum extensions whose base, fields, field groups or controls are gone (Upgrade Readiness, `xpp_find_deprecations`) |
| W4 | Environment profiles (switch without re-running setup) | Done (existing): Select Environment, per workspace |
| W5 | Code review assist: pull request summary of metadata changes with security and license impact | Done (Review My Changes: Copy PR Summary) |
| W6 | Opt-in, local-first usage telemetry (never code) | Done (`xpp.usageStatistics`, Show Usage Statistics) |
| W7 | Shorter Marketplace README | Done: README is a short overview; the details moved to `docs/FEATURES.md` |

## 5. Development lifecycle (L)

| ID | Stage | Item | Status |
|---|---|---|---|
| L1 | Requirements / design | Technical design document from a requirement: elements to create or extend, extension points, security and license impact | Done: `@fno /design` |
| L2 | Develop | Scaffolding for reports, workflows, batch jobs, integrations (see F4) | Done: New Report (RDP with TempDB table, contract, controller, report with auto design), New Approval Workflow, New Batch Job, New Custom Service; `scripts/scaffold-test.js` builds all of them without errors or warnings |
| L3 | Code review | Branch review (F10) and best-practice tool (F5) | Done |
| L4 | Deploy / ALM | Pre-deploy checklist (W1), packaging tool, pipeline and work item links | Checklist and `xpp_package` done; pipeline and work item links planned |
| L5 | Upgrade | Upgrade readiness report (W3) | Done (see W3) |
| L6 | Support | Call stack and batch failure helper, built on Find Message Source and the Infolog | Done: `@fno /support`, batch job history in Database Performance Insights |

## 6. Performance tools (P)

Static checks need no environment; live checks use the database connection (DB3).

| ID | Tool | Level | Status |
|---|---|---|---|
| P1 | `xpp_analyze_performance` and Analyze Performance (classes, tables, forms; findings in Problems) | All | Done: static rules plus live insights (P4, P9, P11) |
| P2 | Row-by-row fallback: set-based statements on tables with overridden insert/update/delete, delete actions or ValidTimeState (skip calls respected) | Static | Done (database log and alerts are environment setup, not checked) |
| P3 | Query shape: queries and row operations in loops, nested `select firstonly` in `while select`, `while select` of every field, `forupdate` without a write, unbalanced transactions, unlimited `crossCompany` | Static | Done |
| P4 | Index advisor: covering indexes for joins and ranges; missing-index DMVs where the database allows | Static + live | Done: queries whose ranges no index starts with (static); missing-index DMVs (live) |
| P5 | Table cache review: CacheLookup against TableGroup and write frequency | Static | Done (CacheLookup against TableGroup) |
| P6 | Form data sources: OnlyFetchActive, link types, display methods without cacheAddMethod | Static | Done for display methods without cacheAddMethod; OnlyFetchActive and link types planned |
| P7 | Trace analyzer: slowest methods and SQL from a Trace Parser trace, linked to source | Live | Done: F&O Studio's own .etl reader (Windows ETW API + the platform's event manifests), no Trace Parser: requests with call trees, methods, SQL per method, pauses; `xpp_analyze_trace` |
| P8 | Query plan explainer | Live | Done (Explain Plan in the query panel) |
| P9 | Batch job analyzer: task split, bundling | Static + live | Done: batch job history (live) |
| P10 | Application Insights KQL helper | Live | Done: Telemetry Queries (documented F&O pageViews and exceptions) |
| P11 | Number sequence review: continuous sequences, preallocation | Static | Done (live, from NUMBERSEQUENCETABLE) |
