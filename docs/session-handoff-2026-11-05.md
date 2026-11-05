# Session handoff (2026-11-05): product review and roadmap for F&O Studio

This file records a Copilot session that started in the wrong repository. Use it to continue the work in this repo, `xpp-vscode`.

## Important context

- **Product name:** **F&O Studio**, the VS Code extension in this repo (`nazmolla/xpp-vscode`, local clone `C:\Users\Mohamed Nazmi\source\repos\xpp-vscode`, version 0.11.0, display name "F&O Studio for VS Code").
- **Mistake made:** the reviews below were run against the **wrong product**: the Visual Studio 2022 VSIX in `nazmolla/XppAiCopilotCompanion` (`...\Documents\DevExt\vsix-companion`). They have **not** been checked against F&O Studio. Many items may already exist here (for example `xpp_run_tests` is already a language model tool in `package.json`, and there are `src/productivity/generateTests.ts`, `obsolete.ts`, `findMessageSource.ts`, a security panel, designers, ERD and so on).
- **What to do with the old content:** treat it as a list of ideas only. Re-run the designer and product manager reviews on this repo, and keep only what is new or better.

## What the user asked for, in order

1. "A designer and product manager agent to review the VS Code extension and suggest: 1) new UI / better UI, 2) new features, 3) new functionalities."
2. "I never asked for quick wins and top 5. I asked for all." (Lists must be complete, not condensed.)
3. "T5 (performance) needs more research; performance can go deeper. Do more research online."
4. "The whole enhance UI section, I am not sure I understand. Keep its work separate." (UI work is its own track, explained in plain language.)
5. "Lifecycle gaps: of this extension, or of its work in Dynamics?" Answer: it means **enabling the extension to support the D365FO development lifecycle** (requirements → design → develop → test → code review → deploy/ALM → upgrade → support), not developing the extension itself.
6. "I agree, it needs these tools. Figure out how best they can be implemented."
7. "The product manager needs to write a better Marketplace write-up that covers the whole extension's functionality plus the new features. The current one is extra long and lists a lot of maybe-not-important information."
8. "List all the suggested work (including all performance tools and lifecycle work) in a file, then work on all of it with a proper plan. Follow coding best practices, architectural patterns and security, and do a proper code review at the end."
9. "Wait, I mean the VS Code extension (F&O Studio), not the VSIX. Start over. Dump this conversation into a file so I can switch directory."

## Next steps (start over in this repo)

1. Explore this repo: `README.md`, `package.json` (commands, views, language model tools), `src/`, `server/` (C# MCP/metadata server, `Ported/Tools/ToolRouter.cs`), `docs/` (`review-findings.md`, `bug-fix-progress.md`, `ai-tools-test-log.md`), `test/`, `scripts/`.
2. Run the **designer review** (UI/UX of the VS Code extension: tree views, designers, webviews, commands, status bar, settings, walkthroughs, accessibility, theming). Keep the UI work as a separate track, written in plain language. List everything, with no "top 5" condensing.
3. Run the **product manager review** (features and functions; gaps in the D365FO lifecycle). List everything.
4. Map the **performance tool family** (below) and **lifecycle items** (below) onto this codebase. Drop what already exists.
5. Product manager: write a **shorter, clearer Marketplace README** for F&O Studio covering existing and new features.
6. Write the full backlog to a file (for example `docs/ROADMAP.md`), plan it, implement it with tests, and run a code review and security review at the end.

---

## Appendix A: Performance research (applies to both products; was "T5")

Sources: Microsoft Learn first, plus community blogs (marked). It was researched for a tool family, `xpp_analyze_performance`.

### A1. When set-based operations quietly fall back to row by row (Microsoft Learn: xpp-data-perf)

| Cause | delete_from | update_recordset | insert_recordset | RecordInsertList/SortedList | Can be turned off with |
|---|---|---|---|---|---|
| Delete actions | Yes | – | – | – | `skipDeleteActions` |
| Database log enabled | Yes | Yes | Yes | – | `skipDatabaseLog` |
| Overridden insert/update/delete | Yes | Yes | Yes | Yes | `skipDataMethods` |
| Alerts set up on the table | Yes | Yes | Yes | – | `skipEvents` |
| ValidTimeStateFieldType ≠ None | Yes | Yes | Yes | Yes | **Cannot be turned off** |

If a cause exists and isn't turned off, the operation runs row by row without any warning. This can be found statically by cross-referencing the table's overridden methods, DatabaseLog, alerts and ValidTimeState settings against every set-based statement on that table.
https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/dev-ref/xpp-data/xpp-data-perf

### A2. Level 1: Code patterns found by reading the source (no environment needed)
- select, insert, update or delete inside loops (best-practice rule names include InsertInLoop, DeleteInLoop, UpdateInLoop, SelectInLoop, per AppChecker; community source).
- A nested `select firstonly` inside a `while select` (one query per outer row) where an `exists join` or `notexists join` would compile to SQL EXISTS.
- Missing `firstOnly`; missing field lists.
- `crossCompany` usage.
- `forupdate` missing before `update()`, or used when nothing is updated (rule SelectForUpdateAbsent).
- Unbalanced or long `ttsbegin`/`ttscommit` (BalancedTtsStatement).
- Display/edit methods in grids without `cacheAddMethod`.
- OR conditions in query ranges (community source).
- Joins on fields that no index covers.
- Repeated identical lookups that should use SysGlobalObjectCache.

### A3. Level 2: Object settings checks (no environment needed)
- **Indexes:** clustered index, alternate key, included (covering) columns, duplicate or overlapping indexes, too many indexes on tables written often.
- **Table caching (CacheLookup):** EntireTable clears the whole cache on any write. Flag it on large or volatile tables and suggest NotInTTS or Found. Use TableGroup and row counts as context.
- **Data entities:** whether set-based processing is possible (not with views, unions, temp tables or complex joins); change tracking overhead during bulk loads.
  https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/data-entities/optimize-data-migration , .../entity-change-track , .../entity-export-performance-tips
- **Forms:** OnlyFetchActive, LinkType (Active/Delayed/Passive) on joined child data sources.
- **Batch:** RunBaseBatch (legacy) vs SysOperation; task modeling (individual tasks, bundling, top picking).
  https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/perf-test/batch-execution-breakup-parallelism
- **Number sequences:** Continuous=Yes without a reason; preallocation below 5 (community source).

### A4. Level 3: Measuring a running environment
- **Trace Parser:** `.etl` traces; call tree, X++ and SQL views. https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/perf-test/trace-parser
- **LCS SQL Insights / Query Store / Query Cookbook** (blocking, expensive queries, plan guides).
- **DMVs** `sys.dm_db_missing_index_*` against AxDB. They reset when SQL Server restarts and can over-suggest wide indexes.
- **Monitoring and telemetry to Azure Application Insights:** Microsoft's current direction (form load, batch, exceptions, custom X++ telemetry, KQL).
  https://learn.microsoft.com/en-us/dynamics365/fin-ops-core/dev-itpro/monitoring-telemetry/monitoring-overview
- **PerfSDK** is deprecated (not available in VS 2022+). Microsoft points to JMeter for load testing.
- **Customization Analysis Report (CAR)** runs best-practice checks over a model.

### A5. Static vs. live, and false alarms
- **Static (levels 1 and 2):** some false alarms. crossCompany, EntireTable caching on tiny tables, or nested selects over a few rows can all be intentional. Findings need a confidence level, and AxDB row counts should raise or lower severity when available.
- **Live (level 3):** few false alarms, but needs SQL, LCS or Application Insights access.

### A6. Proposed tools
| # | Tool | Level | What it returns |
|---|---|---|---|
| P1 | `xpp_analyze_performance` (orchestrator) | All | Combined findings: problem, severity, file and line, fix |
| P2 | `xpp_detect_rowbyrow_fallback` | 1+2 | Set-based statements that will run row by row, with the exact cause |
| P3 | `xpp_query_shape_analyzer` | 1 | Loop, join, field-list and range problems, with suggested rewrites |
| P4 | `xpp_index_advisor` | 2+3 | Ranked index suggestions with a warning about the cost on writes |
| P5 | `xpp_table_cache_reviewer` | 2(+3) | Recommended CacheLookup setting and why |
| P6 | `xpp_form_datasource_analyzer` | 2 | Performance checklist for each form |
| P7 | `xpp_trace_analyzer` | 3 | Slowest methods and SQL from a Trace Parser trace, linked to the source |
| P8 | `xpp_query_plan_explainer` | 3 | Query plan in plain language |
| P9 | `xpp_batch_job_analyzer` | 2+3 | How the job splits its work; bundle-size tuning |
| P10 | `xpp_telemetry_kql_helper` | 3 | Ready-made Application Insights (KQL) queries |
| P11 | `xpp_number_sequence_reviewer` | 2 | Number sequence settings that cause locking |

Suggested order: P2 and P3 first, then P4–P6 and P11, then P7–P10.
Check before relying on: OR-condition plan claims, number sequence preallocation, and best-practice rule names (community sources).

---

## Appendix B: Lifecycle gaps (what the extension should help with in D365FO development)

Assessed against the VSIX. **Re-check each against F&O Studio**, which already has tests, an obsolete-API check and more.

| Stage | Gap the extension could fill |
|---|---|
| Requirements | Turn a requirement or user story into a design |
| Design | Technical design document: objects to create or extend, extension points, security and license impact |
| Develop | Create SSRS reports, workflows, maps, KPIs, batch jobs (SysOperation contract/controller/service) |
| Test | Generate and run SysTest (F&O Studio may already have this) |
| Code review | Run the compiler's best-practice rules; review a change set or diff |
| Deploy/ALM | Deployable package; pipeline and work-item links |
| Upgrade/One Version | Deprecated APIs, obsolete attributes, broken extensions/CoC (F&O Studio has `obsolete.ts`) |
| Support | Help with call stacks and batch failures; message source tracing (F&O Studio has `findMessageSource.ts`) |

---

## Appendix C: Feature and tool ideas from the product manager (VSIX review; re-check for F&O Studio)

**Features**
- F1 **Review My Changes:** review the project or git diff for best-practice violations, CoC wrappers that don't call `next`, missing security, and text that isn't in labels; produce a report.
- F2 **SysTest scaffolding:** generate, build and run tests.
- F3 **Upgrade readiness report:** deprecated and obsolete calls, wrappers of methods that changed, breaking changes, per model.
- F4 **Integration scaffolding wizard:** data entity, OData/DMF settings, staging, custom service and security in one flow.
- F5 **Technical design document from a requirement.**
- F6 **Team knowledge pack:** per-repo naming, prefix and pattern rules that the guide and validation follow.

**Tools**
- T1 `xpp_run_tests` (already exists in F&O Studio)
- T2 `xpp_check_best_practice` (compiler best-practice rules, e.g. xppbp)
- T3 `xpp_diff_changes` (git/TFVC changed objects and methods)
- T4 `xpp_find_deprecations` (SysObsolete and deprecated APIs via cross-reference data)
- T5 performance (replaced by P1–P11 above)
- T6 create SSRS reports, workflows and batch jobs
- T7 security access grants (already implemented in the VSIX; the README there is out of date. Check F&O Studio)
- T8 `xpp_package` (deployable package; ask the user first)
- T9 opt-in usage telemetry (never record code; local-only is the safest default)

**Roadmap (VSIX version):** Now: T7, T2, T3 then F1, one product name, refresh docs and test evidence. Next: T1+F2, F4, F6, T9. Later: T4+F3, performance, T6, T8, F5.
**Success metrics:** at least 95% tool success without retry; fewer best-practice warnings at the gated build; 30% weekly use of review or test tools.

---

## Appendix D: UI ideas from the designer (VSIX review; the VS Code UI is different, redo it)

These were written for Visual Studio 2022 (menus, WPF dialogs, Output pane, InfoBar, Error List). Ideas that may carry over to VS Code:
- A **status indicator** (status bar item: ready, starting, broken; click for diagnostics and restart).
- A **dashboard or side panel** showing setup status, a live log of Copilot tool calls, and the context sent.
- A **first-run setup** (VS Code walkthrough: detect environment → check prerequisites → open Copilot Chat with sample prompts).
- **Review agent changes:** after the AI creates or changes an object, offer View diff / Open / Undo.
- **Build and validation results in the Problems panel**, with click-through to the source.
- **Progress for long work** (withProgress, cancellable).
- **Consistent error messages:** what failed → why → what to do next.
- **One product name everywhere:** "F&O Studio".
- **Accessibility and theming** of every webview (theme colors, focus, ARIA labels, keyboard use).

Findings specific to the VSIX, not this repo: Generate Code wipes the open file (`CopilotLanguageModelService.TryCallLanguageModel` returns null and `GenerateAndApplyToOpenDocument` applies `string.Empty`). That is a VSIX bug to fix separately.
