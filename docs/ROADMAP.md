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

## 7. Second review round (R2)

From `docs/reviews/2026-11-product-review.md` (PM) and `docs/reviews/2026-11-ux-review.md` (UX). Work order: quick
wins, build loop and navigation, views, designers, then the lifecycle features.

### Quick wins (QW)

| ID | Item | Status |
|---|---|---|
| QW1 | Text polish: "…" instead of "?" in titles and progress, settings section "F&O Studio", README command names, code folding in FEATURES.md | Done |
| QW2 | Welcome text for the empty Infolog and Copilot Activity views | Done (Infolog also shows itself on the first message of a debug session) |
| QW3 | Friendly type names (Class, Table) instead of `AxClass` in the project view and Open Object | Done |
| QW4 | Walkthrough steps with real screenshots and alt text | Done: Application Explorer screenshot; the other steps have short pages (`media/walkthrough/*.md`) |
| QW5 | Element designer actions in the editor title (View Code, Form Layout, Refresh, Create Extension on read-only elements) | Done |
| QW6 | Ctrl+Shift+B and the project status bar item run the same build | Done (project build; a model build without a project) |

### User experience (UX)

| ID | Item | Status |
|---|---|---|
| UX1 | New Element: Visual Studio's "Add New Item" as a quick pick (types grouped, recent first, name check, wizards) | Done (`src/scaffolding/newElement.ts`; `+` on the project view and its folders) |
| UX2 | Task-based Dynamics 365 view (Environment, Create, Build and check, Deploy, Data, Analyze, Find, Security, Labels, ...) | Done (Data shows the credential state) |
| UX3 | Build–fix loop: save before build, no Output focus, failed-build notification with Show Problems, error badges on project items | Done (`xpp.build.saveBeforeBuild`; error badges come from VS Code's problem decorations on the items' URIs) |
| UX4 | Project view title bar: New, Build, Open Object; the rest in submenus | Done (Project and Check submenus) |
| UX5 | One Open Element: recent items, type prefixes, item buttons (code, to the side, add to project), opens like Application Explorer | Done |
| UX6 | Property grid: filter, categories, modified markers, description, reference and label pickers | Done: categories and "modified" from the server (`get_element_properties`), label find-or-create picker, element pickers (Alt+Down) |
| UX7 | Results view (panel) for Review My Changes, Pre-Deploy Checklist and Upgrade Readiness: clickable findings, rerun, export | Done (`src/results.ts`) |
| UX8 | Environment setup as a step checklist with retry; switching without reload; webview tabs survive a reload | Done: steps in the Environment group with Retry; designers and code views reload on a switch; element/form designers, label editor, data and trace tabs are restored after a reload (ERD, diagrams and security reports are not) |
| UX9 | Panel home for Infolog and Copilot Activity; quieter status bar outside F&O workspaces | Done (F&O Studio panel: Results, Infolog, Copilot Activity) |
| UX10 | Record inspector with relation drill-down in Browse Data | Done (relations from `table_erd`; opens the related table filtered) |

### Product (PM)

| ID | Item | Status |
|---|---|---|
| PM1 | Environment version drift guard: warn when the UDE runs a newer version than local metadata; Update Metadata; Clean Up Old Versions | Done (`src/versionGuard.ts`): compared without a sign-in prompt; Clean Up keeps the newest older version by default. Not verified live (no serviced UDE here) |
| PM2 | Open Team Repository: clone, junction packages into the custom metadata folder, build in dependency order, `.xpp/workspace.json` | Done (`src/teamRepository.ts`; clones through VS Code's Git extension). Not run against a real team repository yet |
| PM3 | Deployed-state awareness: built vs deployed per model, status bar, warnings before tests and debugging | Done (`src/deployState.ts`): deploys from this machine (pac) are recorded; Run Tests, the debugger and the test tool warn. Reading deployments from Dataverse (`msprov_fnopackage`) is not done (undocumented) |
| PM4 | Verify in the running app: smoke check plan from the change set, run through the Dynamics 365 ERP MCP server | Done: Verify Changes in the Running App builds the steps from the changed elements and hands them to Copilot agent mode, which runs them with the ERP MCP tools |
| PM5 | Standard-code drift report for wrapped methods between two installed versions | Done: `wrapper_drift` (earlier version read through its own disk provider), Standard Code Drift with diffs, `previousPackagesFolder` on `xpp_find_deprecations` |
| PM6 | Agent session checkpoints (revert a session's writes) and scope lock enforced on the server | Done: the MCP server reads `agentSession.json` before every write (scope refusal, state before the first change); revert through the metadata API plus the project and label files; `scripts/checkpoint-test.js` |
| PM7 | Integration Try It: OData entity queries and custom service calls, saved as `.http` | Done (`src/integration.ts`): active environment only, service calls ask first, the token is never written. Not verified against a live environment |
| PM8 | Data entity readiness rules (staging, mandatory fields, public names, keys, set-based) | Done in `analyze_performance` for data entities (Analyze Performance, Review My Changes, Pre-Deploy Checklist, `xpp_analyze_performance`); no findings on standard entities |
| PM9 | New Business Event wizard | Done (precedent HireActionBusinessEvent); compiles without warnings (`scripts/scaffold-test.js`) |
| PM10 | Add Number Sequence to Table wizard | Done (precedent NumberSeqModuleCustomer.loadModule); compiles without warnings |
| PM11 | New Feature (feature management) wizard and Show Feature Usage | Done; compiles without warnings. The opt-in Review rule for ungated changes is not done |
| PM12 | Generate tests with the Acceptance Test Library (ATL) | Done: Generate SysTest Class with Test Data (customers, vendors, items, warehouses, sites; adds the ATL references); `xpp_get_guide` topic "ATL" |
| PM13 | Extension collision report across models | Done: `extension_collisions` (fields, indexes, field groups, enum values, form controls; several wrappers of one method) in Upgrade Readiness and `xpp_find_deprecations` |
| PM14 | Report a Problem with a redacted diagnostics bundle | Done (`src/reportProblem.ts`; also on every command error) |
| PM15 | Visual Studio parity table in FEATURES.md | Done (Compared with Visual Studio) |

### Cut or simplify (CS)

| ID | Item | Status |
|---|---|---|
| CS1 | Replace the "Go to <X>" commands with Open Referenced Element and a picker | Done: one Go to Referenced Element… in the designer menu; the per-reference commands stay registered (no longer contributed) |
| CS2 | One environment menu on the status bar item; `pac` init on first deploy | Done |
| CS3 | Code intelligence toggles: settings plus one Configure command | Done (Configure Code Intelligence…) |
| CS4 | Telemetry Queries as snippets/documentation unless they run | Partly: the queries are also Kusto snippets (`fno-…`); the command stays for the form-specific queries |
| CS5 | Usage statistics feed Report a Problem (PM14) | Done |
| CS6 | Run Snippet's scratch runner excluded from packages and flagged by the Pre-Deploy Checklist | Done: the checklist fails while the runner is in the model |
