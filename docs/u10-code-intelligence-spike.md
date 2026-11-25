# U10 spike: X++ code intelligence in VS Code

Status: research only (2026-10-22). No feature in this document is implemented except where marked "exists".
Goal: decide what X++ code features VS Code can offer, which existing engines can back them, and in what order to build them.

## 1. What exists today

| Feature | State | Where |
|---|---|---|
| Code view (`xpp:` documents: declaration + methods as one file, save through the metadata API) | exists | `src/codeView.ts`, `src/xppSource.ts` |
| Syntax highlighting (TextMate grammar) | exists | `syntaxes/` |
| Go to / Peek Definition: types, `Type::member`, `this.`/`super.`/`variable.member`, calls (walks `extends`) | exists (U9) | `src/navigation.ts` |
| Find All References (XRef where-used, member level) | exists (U9) | `src/navigation.ts` -> `where_used` |
| Hover: object type and model; method signature | exists (U9) | `src/navigation.ts` |
| Build diagnostics in Problems (xppc, project or model) | exists | `src/build.ts` |
| Class/table hierarchy and references diagrams (webview) | exists | `src/diagrams.ts` |
| Everything else in section 3 | missing | |

## 2. Engines available on a developer machine

| Engine | Where | What it gives | Notes |
|---|---|---|---|
| **MetaModel API** (`Microsoft.Dynamics.AX.Metadata*.dll`) | package `bin` (always) | Every element and member: classes, methods (source), tables, fields, EDTs, enums, relations, labels, extensions | Already used by the server. Reads of single elements are fast; enumerating a type is slow (seconds to minutes for 20k objects, see #46). |
| **Cross-reference DB** (`XRef_<org>` on LocalDB) | configuration `CrossReferencesDb*` | `Names` (6.2M paths) and `References` (28.4M rows: SourceId, TargetId, **Kind**, Line, Column) | Already used. Kinds: MethodCall, TypeReference, InterfaceImplementation, ClassExtended, TestCall, Property, Attribute, TestHelperCall, Tag, MethodOverride. Indexed; queries take milliseconds. Only as fresh as the last build with cross-references (now cleaned on delete). |
| **Compiler front end** (`Xlnt.XppParser.dll`, `Xlnt.XppCore.dll`: `Scanner`, `Ast`, `CompilationUnit`, `AstToXml`) | package `bin` (always) | Tokens, full AST of a method or class, syntax errors | Internal, undocumented API; version-specific. No VS needed. |
| **Compiler semantic pass** (`Xlnt.XppParser.Pass2.dll`, `Xlnt.TypeUniverse.dll`) | package `bin` | Bound types of expressions, symbol resolution, semantic errors | What xppc runs. Needs a type universe over the metadata provider; heavy but gives real types (completion, accurate definition). Highest effort and risk. |
| **xppc.exe** | package `bin` | Authoritative diagnostics; project-scope build takes about 10 s here | Already used for Build. Usable for diagnostics on save. |
| **VS X++ language service** (`Microsoft.Dynamics.Framework.Tools.LanguageService*.17.0.dll`: `XppAstParser`, `XppScanner`, `SourceCodeFormatter`, `DelayedHeaderParser`) | Visual Studio extension folder only | Editor-grade parser, header parser (fast member lists), **formatter** | Only where Visual Studio with the D365 tools is installed; not redistributable. Load at runtime if present, never ship. |
| **Pattern engine** (`Metadata.Patterns.dll`) and **compiler validators** (`Metadata.Validation.dll`) | package `bin` | Form pattern rules, extension rules | Already used by #41/#43. |

## 3. Feature feasibility

Effort: S (days), M (1-2 weeks), L (several weeks). Risk: how likely the approach breaks on platform updates or is slow.

| # | Feature | Approach | Engine | Effort | Risk | Notes |
|---|---|---|---|---|---|---|
| 1 | Document symbols / Outline / breadcrumbs | Methods from `methodSpans` (exists); members from the declaration | text (exists) | S | low | Quick win; also enables "Go to Symbol in Editor". |
| 2 | Workspace symbols (Ctrl+T) | `find_object` (names) + XRef `Names` (members: `/Classes/X/Methods/m`) with prefix search | XRef + MetaModel | S | low | Query XRef `Names` with `LIKE 'prefix%'` on the last segment; cap results. |
| 3 | Go to Implementation (overrides, CoC, event handlers) | XRef kinds MethodOverride, ClassExtended; CoC via Attribute references to `ExtensionOf`; handlers via Attribute references to the handled method | XRef | S-M | low | CoC detection must be verified against real `[ExtensionOf(classStr(X))]` rows. |
| 4 | Call hierarchy, incoming | XRef MethodCall where target = method | XRef | S | low | Outgoing: XRef rows with source = method. Both cheap. |
| 5 | Type hierarchy (supertypes/subtypes, extensions) | `extends` from declaration / metadata; subtypes via XRef ClassExtended; table/form/enum extensions via provider keys `Base.*` | XRef + MetaModel | S | low | Existing hierarchy diagram code can be reused. |
| 6 | Code lens: "N references", "extended by N", "M CoC" | Same XRef queries, batched per document | XRef | S | low (perf) | One query per document with all member paths; refresh on save. |
| 7 | Hover: label text, EDT base type, enum values, doc comment | Resolve `@File:Id` (LabelStore exists), read EDT/enum through MetaModel, doc comment from source above the method | MetaModel + labels | S | low | Hover on a label literal shows the text in the user's language. |
| 8 | Diagnostics on save | Run xppc for the project (about 10 s) after save, debounced, and map like Build | xppc | S-M | low | Already have parsing of build output. Optional "on save" setting. |
| 9 | Live syntax diagnostics (as you type) | Parse the method/class with the compiler front end in the server; report syntax errors only | XppParser | M | medium | Validates the parser API; foundation for 10-12. |
| 10 | Completion: members after `.` and `::` | Static type from declared variable (exists in U9) + MetaModel members (methods, fields, enum values), inherited members via `extends`; keywords and snippets | MetaModel (+ U9 resolver) | M | low-medium | Good enough without Pass2 for most code; misses expression types (`a.b().c`). |
| 11 | Completion: labels, EDTs, tables in property-like positions (`@` triggers, `tableStr(`, `fieldNum(X, ` ...) | Label index (exists for #46), name indexes | MetaModel + labels | S-M | low | Very useful for agents and people writing labels. |
| 12 | Signature help | Method signature from MetaModel source (`MethodSignature` exists in the router) | MetaModel | S | low | Parameter index from commas before the cursor. |
| 13 | Semantic highlighting | Classify identifiers as type / field / method / enum using the U9 resolver and caches | MetaModel | M | medium (perf) | Must batch lookups; TextMate already covers keywords. |
| 14 | Exact types everywhere (completion on any expression, accurate definition, semantic errors as you type) | Compiler Pass2 with TypeUniverse | Pass2 | L | high | Real language-server quality; heavy memory and startup, internal API. Do only after 9 proves the API is workable. |
| 15 | Rename symbol | XRef references + edits through the code view and metadata (properties referencing the name) | XRef + MetaModel | M-L | medium | Must also rename metadata references (form data sources, menu items, security). Restrict to methods and locals first. |
| 16 | Formatting | VS `SourceCodeFormatter` when Visual Studio is installed; otherwise a simple indenter (braces, 4 spaces) | VS LS / text | S (indenter) / M (VS) | medium (VS dependency) | Project rule already requires 4-space Allman style. |
| 17 | Visualizations: class view, relation graph, ERD (Mermaid, N levels of table relations), extension map | MetaModel relations + XRef; render as Mermaid in a webview (diagrams.ts exists) | MetaModel + XRef | S-M each | low | ERD: start table, follow relations (and reverse via XRef TypeReference from tables) to depth N. |

## 4. Architecture

- Keep one server process and add language methods (`xpp/lang/symbols`, `.../completion`, `.../implementations`, `.../calls`, `.../hierarchy`, `.../codeLens`) next to `xpp/bridge`; the VS Code side registers standard providers (no LSP client needed; the server already speaks JSON-RPC).
- The metadata provider must be used from one thread (the server's "UI thread"). Long requests (indexes, Pass2) block tool calls; give language features their own server process (a second `XppVsCode.Server` started with `--language`), so agent tools and editing never wait on each other.
- Caches: per element (members, signatures, base class), invalidated by the file system watcher on the custom metadata folder; label and name indexes built once in the background (the #46 index pattern: 2-5 s per type).
- XRef is the backbone for references, implementations, call and type hierarchy, and code lens. It is only as fresh as the last build: show "as of last build" in results, and keep the delete cleanup (done) in mind for renames.
- Internal compiler APIs (9, 14) are loaded by reflection with a capability check at startup; when they fail, fall back to the text resolver from U9 and say so once in the log.

## 5. Prioritised backlog

| Priority | Item | Depends on | Acceptance |
|---|---|---|---|
| P1 | Outline / document symbols / breadcrumbs (1) | - | Methods and members listed for classes, tables, forms (nested data source and control methods). |
| P1 | Go to Implementation and code lens "overrides / CoC / handlers" (3, 6) | - | On `SalesFormLetter.run` shows overriding classes and CoC extensions; counts match XRef. |
| P1 | Call hierarchy incoming/outgoing (4) | - | Incoming calls of a method within 200 ms on a warm XRef. |
| P1 | Completion after `.` / `::` with static types (10) + signature help (12) | U9 resolver | Members of `CustTable custTable; custTable.` include fields and methods incl. inherited; `CustTable::` lists static methods. |
| P1 | Hover for labels, EDTs, enums (7) | - | Hover on `"@SYS7149"` shows the text; on an EDT its base type and size. |
| P2 | Diagnostics on save via xppc (8) | - | Errors of the saved object appear in Problems within ~15 s; setting to disable. |
| P2 | Workspace symbols (2) | - | Ctrl+T `XvsDemo` finds objects and methods across models in under 1 s. |
| P2 | Type hierarchy (5) + ERD and extension map visualizations (17) | - | ERD of `SalesTable` depth 2 renders as Mermaid; extension map lists table/form extensions and CoC classes. |
| P2 | Label/EDT/table completion in literals (11) | label index | Typing `"@` offers labels of the project's label files with text. |
| P3 | Live syntax diagnostics with the compiler front end (9) | capability spike | Syntax errors appear while typing without a build; no measurable typing lag. |
| P3 | Formatting (16) | - | Format Document produces the 4-space Allman layout; uses the VS formatter when present. |
| P3 | Semantic highlighting (13) | 10 caches | Types, fields, methods and enum values colored distinctly on a 2,000-line class without lag. |
| P4 | Pass2 semantic model (14) | 9 | Completion on chained expressions, semantic errors as you type. Go/no-go after a 3-day spike on memory/startup. |
| P4 | Rename (15) | 14 or XRef + metadata reference finder | Rename a private method updates all callers and leaves the build clean. |

## 6. Open questions and next spikes

1. Compiler front end: can `Xlnt.XppParser` parse a single method body without a full compilation context? (1-2 days; decides P3 live diagnostics.)
2. CoC and event handler rows in XRef: confirm the Kind and Path shapes for `[ExtensionOf]` and `[SubscribesTo]` / `[PostHandlerFor]` attributes. (half a day)
3. XRef freshness: does `xppc -xref` for a project update rows for that project only, and how long does it take? Determines whether code lens can refresh on save.
4. Second server process: memory cost of two metadata providers (expect several hundred MB each); alternatively one process with a request queue and priorities.
5. Licensing: the VS language service assemblies are part of the D365 tools for Visual Studio; use them only when installed, never copy them.

## Implementation findings (round 3)

- **No open-source X++ parser, grammar or formatter** exists that is usable; everything builds on the installed compiler and the cross-reference database.
- **Syntax:** the compiler's `Pass1.ParseMethod` runs standalone (no metadata) and returns real syntax errors in milliseconds.
- **Formatting:** Visual Studio's `SourceCodeFormatter` needs a running Visual Studio, so F&O Studio has its own formatter (Allman braces, re-indent; idempotent).
- **Chain of Command:** in XRef, an `ExtensionOf` attribute reference (kind 7) plus a type reference (kind 2) on the same line gives the extended element; a wrapper method is a method call (kind 1) to the same-named method. Pre/Post handlers and `SubscribesTo` follow the same shape. The whole index builds in about 6 s and is cached on disk (`%LOCALAPPDATA%\XppVsCode\cache`); custom models are rescanned from disk on each refresh. Reverse lookups ("which CoC wraps this method") are dictionary lookups and never block the editor.
- **Form and data event handlers** are not in XRef; a class-file scan (about 50 s cold, 4 s warm) finds them and is cached. It is a separate, switchable feature.
- Each feature is a setting (`xpp.codeIntelligence.*`) with checkboxes in the Dynamics 365 view; semantic highlighting and compile on save are off by default.
