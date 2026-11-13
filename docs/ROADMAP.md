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
| V2 | Debugger: confirm a breakpoint hit end to end on a UDE | Done: confirmed by users debugging on UDEs |

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
| F3 | Rename (methods, fields, elements) with cross-reference updates | Done: methods (F2), fields (designer Rename on a table field) and elements (Rename… on your elements): X++ code in the refactor preview, metadata of your other elements through `replace_references`, references from the cross-reference database. Not run end to end in VS Code yet |
| F4 | New Object wizards: table + form + menu item + privilege; SysOperation batch; report; data entity; integration (entity, staging, service, security) | Done: batch job, report, approval workflow, custom service, table with form (key EDT, table, SimpleList form, menu item, View/Maintain privileges) and data entity (entity, staging table, privileges); `scripts/scaffold-test.js` builds all of them without errors or warnings |
| F5 | On-demand best-practice check (`xppbp`) into Problems, with quick fixes | Done (quick fixes come from the existing code actions) |
| F6 | Label tooling: translate missing languages, unused labels, import/export | Done: Export Labels… and Import Labels… (CSV: Id, Comment, one column per language) |
| F7 | Git-aware project view: changed elements, "changed in this branch", readable metadata diffs | Done |
| F8 | Database sync of selected tables | Done for cloud-hosted and VHD (SyncEngine); not run here (this machine has a UDE, which syncs through `pac`) |
| F9 | Trace Parser integration (open a trace, jump to code) | Done: replaced by Analyze Trace (P7); every method opens in the code view |
| F10 | Review My Changes: best practice, CoC without `next`, missing security, literal text, for the branch diff | Done |
| F11 | Team knowledge pack: per-repo naming, prefix and pattern rules the guide and validation follow | Done: `.xpp/agent-rules.md` joins the agent instructions |
| F12 | Copilot tools: `xpp_check_best_practice`, `xpp_diff_changes`, `xpp_find_deprecations`, `xpp_package` (asks first) | Done |
| F13 | Dependency diagram (Visual Studio's DGML): a model's package references, and an element's dependencies from the cross-reference database, exportable as SVG/Mermaid | Done: View Dependencies (`element_dependencies`), View Model Dependencies; every diagram exports Mermaid |
| F14 | Debugger on cloud-hosted and VHD environments: attach vsdbg to the local AOS (w3wp, batch) with the PDBs in PackagesLocalDirectory, no DebuggerProxy | Done: Launch Debugger attaches the C# extension's clr debugger to w3wp.exe or Batch.exe; local PDBs use the same e:\source paths as the cloud. Not verified on a cloud-hosted or VHD environment yet |

## 4. Workflows (W)

| ID | Item | Status |
|---|---|---|
| W1 | Pre-deploy checklist: build, best practice, tests, security gaps, missing labels, then package and deploy | Done (Pre-Deploy Checklist; tests stay a separate run because they need the deployed code) |
| W2 | VS Code tasks for builds (`type: xpp`, `buildProject` / `buildModel`) | Done, plus `deploy` (dbSync) and `syncDatabase` (mode, modules) tasks |
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
| L4 | Deploy / ALM | Pre-deploy checklist (W1), packaging tool, pipeline and work item links | Done: checklist, `xpp_package`, Link Work Item… (#id or AB#id in the commit message), Open Work Item…, Open Pipelines (Azure Pipelines or GitHub Actions); Review My Changes lists the branch's work items |
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
| P6 | Form data sources: OnlyFetchActive, link types, display methods without cacheAddMethod | Static | Done: display methods without cacheAddMethod; data sources that fetch every field but show a few in a grid (OnlyFetchActive); Delayed/Active data sources shown in the parent's grid instead of joined, and Active links |
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
| CS4 | Telemetry Queries as snippets/documentation unless they run | Done: the Telemetry Queries command only (the Kusto snippets were removed so nothing is contributed to a language another extension provides) |
| CS5 | Usage statistics feed Report a Problem (PM14) | Done |
| CS6 | Run Snippet's scratch runner excluded from packages and flagged by the Pre-Deploy Checklist | Done: the checklist fails while the runner is in the model |

## 8. Server structure (S)

Before this work the server routed requests through hard-coded switches: 63 bridge actions in `MetaModelBridgeServer.DispatchOnMainThread`,
5 more in `McpStdioServer`, 34 MCP tools in `ToolRouter.DispatchTool` (with their descriptions in one JSON string), and 7
switches on the element type in `MetaModelBridge.cs` (4,200 lines). Action names are also listed in `src/bridgeContract.ts` and
the write lists `EditActions`/`CreateActions`. Goal: one registration per action, tool and element type, with write rules
taken from the registration. Every step keeps behaviour identical: `scripts/golden-master.js` reports IDENTICAL, and the smoke
test, security probes, `npm run test:server`, `check-bridge-actions.js` and `run-smoke.js` pass.

| ID | Item | Status |
|---|---|---|
| S0 | Safety net: `scripts/golden-master.js` exercises every bridge action of `src/bridgeContract.ts`, every listed MCP tool (it fails when one is missing), `initialize`/`ping`/`tools/list`, `xpp/lang`, `xpp/trace`, the `xpp/data` refusal and a read and validate of every element type (220 steps); dispatch characterization tests in `test/server` (which actions exist, which are writes, which follow the agent's scope) | Done |
| S1 | Bridge action registry (`BridgeActions.cs`): each action is registered once with its write kind (Read, Edit, Create, Copy) and whether it uses the metadata API; the dispatcher applies the lock, write policy, agent scope and checkpoint and the error envelope from the registration (replaces `EditActions`/`CreateActions` and the switch). The cross-reference actions are registered by `ToolRouter.AddXRefActions` and run without the lock | Done |
| S2 | Contract from the registry: a unit test compares the running registry with `src/bridgeContract.ts`; `check-bridge-actions.js` reads the registrations. No `--list-actions` flag was needed | Done |
| S3 | MCP tool registry (`McpTools.cs`): each tool is registered with its handler and traits (parallel read, element write, unlisted alias); the definitions agents discover live in `server/Resources/Tools.json` and `tools/list` is generated from the registry. Startup fails when a definition has no tool or a listed tool no definition. The parallel list in `McpStdioServer` and the write list in `ActivityLog` come from the traits | Done |
| S4 | Element type table (`MetaModelBridge.ElementTypes.cs`): create, update, read and update-after-create per type replace the four type switches; per-type reads are methods. Create stays one orchestrator (create, reload, update, roll back) | Done |
| S5 | `MetaModelBridge.cs` split into partial files by section (Validation, Discovery, TypeOperations, Apply, Extract); code moved unchanged, 4,200 to 820 lines | Done |
| S6 | Dependency injection container (`Microsoft.Extensions.DependencyInjection`, runs on .NET Framework 4.8), only if hand wiring in `Program.Run` gets hard to follow | Optional |
| S7 | Services with their own state instead of partials of `MetaModelBridge` (along the interfaces in `MetadataServices.cs`, through `IMetadataCore`) | Open |

## 9. Agents (A)

Custom agents shipped with the extension (`contributes.chatAgents`, one `.agent.md` per persona, generated by
`npm run agents` from `scripts/agent-definitions.js`), with handoffs between them. `@fno` stays. Rules for every agent:

- **Live environment:** agents that need the running client (setup, forms, data as the user sees it) suggest configuring the
  Dynamics 365 ERP MCP server, or use the web browser.
- **Changes need permission and a non-production environment:** an agent changes data, setup or metadata only when the user
  allows it, and only in development, sandbox or build environments, never in production. Otherwise it suggests the change
  with the menu path or the code.

| ID | Agent | Audience | Status |
|---|---|---|---|
| A1 | X++ Developer: extensions first (CoC, event handlers), reuses standard patterns, builds and validates | Developer | Done (not yet tried in a chat session) |
| A2 | Code Reviewer: best practice, performance, security, deprecations, team rules on the branch (read-only) | Developer | Done (not yet tried in a chat session) |
| A3 | Support / Troubleshooter: error, call stack or trace to cause and fix | Developer | Done (not yet tried in a chat session) |
| A4 | Solution Designer: one requirement to a functional and technical design (no code) | Both | Done (not yet tried in a chat session) |
| A5 | Architect: overall solution (models, ISV and extension strategy, integrations, ALM, environments) | Both | Done (not yet tried in a chat session) |
| A6 | Security & License Analyst: roles, duties, privileges, licenses, segregation of duties | Both | Done (not yet tried in a chat session) |
| A7 | Performance Analyst: traces, query plans, performance rules | Developer | Done (not yet tried in a chat session) |
| A8 | Upgrade / ISV Reviewer: upgrade readiness, extension compatibility, deprecations | Developer | Done (not yet tried in a chat session) |
| A9 | Data Entity / Integration Specialist: OData and data management entities, staging, integration errors | Both | Done (not yet tried in a chat session) |
| A10 | Functional Explainer: how a form, field, parameter or process works, in business terms | Functional | Done (not yet tried in a chat session) |
| A11 | Setup & Configuration Advisor: checks parameters and setup, explains missing or odd setup | Functional | Done (not yet tried in a chat session) |
| A12 | Data Investigator: why a record looks like it does, across related tables | Functional | Done (not yet tried in a chat session) |
| A13 | Functional Triage: user error to setup or data cause; hands code causes to A3 | Functional | Done (not yet tried in a chat session) |
| A14 | Security Role Designer: roles for a job, license cost and conflicts; hands the build to A1 | Functional | Done (not yet tried in a chat session) |
| A15 | Test Scenario Writer: test cases and Task Recorder style scripts from a requirement or change | Functional | Done (not yet tried in a chat session) |
| A16 | Fit-Gap Analyst: standard, configuration, extension or ISV per requirement, with evidence; hands gaps to A4 | Functional | Done (not yet tried in a chat session) |
| A17 | Data Migration Advisor: entities, load order, mandatory fields per entity, import errors | Functional | Done (not yet tried in a chat session) |
| A18 | Release Notes & Change Communicator: user-facing notes from the branch's changes and work items | Functional | Done (not yet tried in a chat session) |
| A19 | Test Engineer: SysTest classes for changed code, run and fix | Developer | Done (not yet tried in a chat session) |
| A20 | Report Developer: SSRS (RDP, designs), Electronic Reporting, print management | Developer | Done (not yet tried in a chat session) |
| A21 | Workflow & Business Events Specialist | Developer | Done (not yet tried in a chat session) |
| A22 | Release Manager: pre-deploy checklist, package, deploy and sync, risk summary | Developer | Done (not yet tried in a chat session) |
| A23 | Code Archaeologist: explains legacy custom code, finds dead code and upgrade leftovers | Developer | Done (not yet tried in a chat session) |

Handoffs: A16 → A4 → A1 → A2 → A22; A13 → A3 → A1; A14 → A1 → A6.

**A0 Agent routing** (before A1): when the user hasn't picked one of these agents, the chat suggests the right one, or uses it
itself when that's clearly appropriate. Status: Done for suggestions and subagents (`agents/fno-agents.instructions.md`, the MCP server's instructions); `@fno` routing is not done.

- **Instructions everywhere:** the extension's `chatInstructions` and the MCP server's `initialize` instructions (any agent using
  the `xpp_*` tools gets them) list the agents and when each fits. The default agent names the matching agent and offers to
  switch.
- **Agents as subagents:** the agents allow model invocation, so the default agent can run one itself for a clearly matching
  task (for example the Code Reviewer after a change, Functional Triage for a pasted error) and report its result.
- **`@fno` routing:** `disambiguation` on the `@fno` chat participant lets VS Code send D365FO questions to it when no participant
  is chosen; `@fno` answers and offers the matching agent as a follow-up button.
- **Permission still applies:** an agent started automatically follows the same rules: it changes data, setup or metadata only
  after the user allows it, and never in production.
