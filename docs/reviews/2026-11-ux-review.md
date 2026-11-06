# F&O Studio UX review (round 2)

Scope: friction that is not covered by ROADMAP §2 (UI1–UI14, all Done). I reviewed `package.json` contributions, `src/d365Tools.ts`, `src/extension.ts`, `src/build.ts`, `src/project.ts`, `src/projectStatus.ts`, `src/navigation.ts`, `src/setup.ts`, `src/environmentPicker.ts`, `src/review.ts`, `src/checklist.ts`, `src/elementDesigner.ts` + `media/element.js`, `src/designer.ts`, `media/dataPanel.js`, and the screenshots in `docs/images` (aot, form-designer, trace, implementations). The walkthrough folder has only `copilot.md`. Every other walkthrough step uses `media/icon.png`.

## Findings

| # | Where | What the user experiences | Severity |
|---|---|---|---|
| F1 | Project view, [package.json](package.json) commands | You can't create a plain class, table, EDT, enum, form or menu item. There is no "Add > New Item", which is Visual Studio's most-used command. The only routes are the four wizards (Command Palette only), designer node **New…**, and the empty-project welcome text, which says "create them with the agent tools". | High |
| F2 | Dynamics 365 view ([src/d365Tools.ts](src/d365Tools.ts#L51-L116)) | The tree copies Visual Studio's 2022 menu and lists none of the 0.12 features. Wizards, Browse Data / New SQL Query / Request Credentials / Insights, Analyze Performance, Analyze Trace, Upgrade Readiness, Review My Changes, Translate/Unused Labels, Team Rules and Telemetry Queries are reachable only from the Command Palette or a buried context menu. | High |
| F3 | Build ([src/extension.ts](src/extension.ts#L284-L310), [src/build.ts](src/build.ts#L67)) | Unsaved code views are not saved before a build, so the compiler builds stale metadata (no `saveAll` anywhere in `src`). Each build calls `output.show(true)`, so the panel switches to Output, not Problems. The only result is a status bar message that disappears after 10 s. | High |
| F4 | Project view title bar ([package.json](package.json#L1330-L1440)) | Nine inline icons (Open Object, Build, Build Project, Run Tests, Refresh, Open, Open Recent, Create, Close) plus three in the overflow menu. The two build icons look alike, and the toolbar overflows in a normal-width sidebar. | Medium |
| F5 | All webview panels (no `registerWebviewPanelSerializer`) | Designers, data panels, label editors, ERDs and traces are gone after a reload. **Select Environment** and **Connect to Environment** both ask you to reload, so switching environments loses your open work. | Medium |
| F6 | [src/navigation.ts](src/navigation.ts#L267-L310) vs. the Application Explorer | The same element opens differently depending on where you start. Open Object (Ctrl+Alt+O) opens a form in the `[Design]` form-layout panel and a table in the code view. The Application Explorer opens both in the element designer (`[Form]`, `[Table]`). | Medium |
| F7 | Open Object quick pick | The list is empty until you type 3 characters. It shows no recent items, uses raw `AxClass` as the description, has no type filter and no per-item actions (designer / code / to the side / add to project). | Medium |
| F8 | Property grid ([media/element.js](media/element.js#L466-L575)) | There is no search box, no categories, no marker for non-default values and no description of the selected property. References (ExtendedDataType, EnumType, Table, Label) are plain text boxes. To set a label you must already know `@File:Id`. | Medium |
| F9 | [src/review.ts](src/review.ts#L27), [src/checklist.ts](src/checklist.ts#L67), `upgrade.ts` | Reports open as **untitled** raw markdown. Findings like `Class.method line 12` can't be clicked, closing the tab prompts "Save changes?", and running the report again means going back to the Command Palette. | Medium |
| F10 | [src/setup.ts](src/setup.ts#L42-L58) | Connect runs a multi-GB download, extract and restore inside one notification that shows only free text. There is no list of steps, no way to retry from the failed step, and it ends with "Reload the window". | Medium |
| F11 | Status bar, `activationEvents: onStartupFinished` | There are four items on the left (project, server, environment, database). The extension activates in every window, so a non-F&O workspace still shows "No D365FO project" and the environment item. | Medium |
| F12 | Sidebar views | Infolog is expanded in the sidebar by default and stays empty with no welcome text unless you are debugging. Copilot Activity is also empty with no welcome text. In Visual Studio the Infolog is a tool window next to Output and Debug. | Low |
| F13 | Element designer toolbar ([src/elementDesigner.ts](src/elementDesigner.ts#L563-L569)) | View code, Form layout and Refresh are buttons inside the webview, not editor title actions. A read-only standard element (the "Foundation · read-only" badge) gives no primary action such as **Create Extension**. | Low |
| F14 | Browse Data ([media/dataPanel.js](media/dataPanel.js)) | Wide tables can only be read by scrolling the grid sideways. There is no single-record view, and RefRecId / foreign-key values can't be followed to the related row. | Medium |
| F15 | Text polish | The command is titled "Create D365FO Project**?**" ([package.json](package.json#L1171)), the progress title reads "Finding event handlers**?**" ([src/codeIntel/providers.ts](src/codeIntel/providers.ts#L180)), and the Settings section is titled "X++", not "F&O Studio". | Low |

## Designs

### UX1: New Element (Add New Item)
**Problem:** F1. A brand-new element is the basic unit of work, and today only the agent creates one easily.

**Design:** a multi-step QuickPick. Entry points: the `+` in the Project view title, the folder context menu, and the Command Palette.
```
┌ New Element (1/2) ──────────────────────────────────────────┐
│ > tab                                                        │
│ ── Recently used ─────────────────────────────────────────── │
│   $(symbol-class) Class                                      │
│ ── Data model ────────────────────────────────────────────── │
│   $(table) Table            fields, indexes, find()/exist()  │
│   $(table) Table extension  extend a standard table          │
│ ── Data types ───────────────────────────────────────────── │
│   $(symbol-enum) Base Enum  $(symbol-field) EDT              │
│ ── User interface ───────────────────────────────────────── │
│   $(window) Form   $(link) Display menu item  ...            │
│ ── Wizards ─────────────────────────────────────────────── │
│   $(sparkle) Batch job · Report · Approval workflow · Service│
└──────────────────────────────────────────────────────────────┘
┌ New Table (2/2) ──────────────── model: testcust ───────────┐
│ CMCLoyaltyTier                                               │
│ ✓ Name is free. Prefix CMC matches team rules.               │
│ [Enter] create + open designer   [Alt+Enter] create only     │
└──────────────────────────────────────────────────────────────┘
```
**Interaction:**
- Empty: shows all types, grouped, with recently used types first.
- Loading: `busy` while it checks the name with `find_object exactMatch`.
- Error: `validationMessage` for a duplicate name, an invalid identifier or a missing prefix. Server failures go through the usual "Show Log" path.
- Success: the element is created in the project's model, added to the project and opened. Right-clicking a project folder pre-selects that folder's type.
- Extension types continue with a third step: pick the base element.
- Keyboard: type to filter; Alt+Enter creates without opening.
- Commands: `xpp.newElement` (args: `objectType?`, `folder?`). The wizards stay as items in the Wizards group.

**Accessibility:** native QuickPick. Group separators are announced, and each item has a `detail` describing the type.

**Implementation:** new `src/scaffolding/newElement.ts`, using the existing `create_object` + `add_to_project` bridge actions with minimal typed metadata (a table gets a primary index placeholder only if team rules require one). Contribute it to `view/title` for `xpp.project` and to `view/item/context` for folder nodes. Change the empty-project welcome text to `[New Element](command:xpp.newElement)`.

**Effort:** M

### UX2: Task-based Dynamics 365 view
**Problem:** F2. Features are hidden. The VS menu layout doesn't fit VS Code users who have no Visual Studio habits.

**Design:** the same `xpp.tools` tree, grouped by task. VS labels are kept as item names, so search still works.
```
DYNAMICS 365
├ $(server-environment) Environment        org2f1d2adc · 10.0.2790
├ $(add) Create
│   New Element…  New Batch Job…  New Report…  New Workflow…
│   New Custom Service…  Create Model…  Create Team Rules
├ $(tools) Build and check
│   Build Models…  Best Practice  Review My Changes
│   Pre-Deploy Checklist  Upgrade Readiness…
├ $(cloud-upload) Deploy             (pac items hidden on CHE/VHD)
├ $(database) Data                   Reader · expires 14:32
│   Browse Table Data…  New SQL Query  Insights…  Credentials…
├ $(pulse) Analyze
│   Analyze Performance…  Analyze Trace…  Telemetry Queries…
├ $(search) Find      Labels · Tags · References · Code · Message…
├ $(shield) Security  Gaps · SoD · Report · Who Can Access · License
├ $(symbol-string) Labels  Find · Translate Missing · Find Unused
├ $(project) Model and project · $(debug-alt) Debug · $(eye) View
├ $(lightbulb) Code Intelligence     indexing…
└ $(gear) Options…
```
**Interaction:**
- Empty or first run: the Environment group is expanded and shows "Not connected – Connect…".
- Loading: group descriptions show state ("indexing…", "expires 14:32").
- Error: the item description shows `$(warning)` with a tooltip explaining what to do.
- Groups start collapsed, except Environment and Build and check. Expansion state persists (stable ids are already in place).
- Keyboard: native tree type-to-navigate.

**Accessibility:** a native tree. Group descriptions are read by screen readers; avoid conveying state by icon alone.

**Implementation:** restructure `ToolsMenu` in [src/d365Tools.ts](src/d365Tools.ts). Let `src/database/connection.ts` contribute a `ToolsContribution` for the Data group description. Reuse the `udeOnly` hiding.

**Effort:** S

### UX3: Tight build–fix loop
**Problem:** F3. Stale builds, Output stealing focus from Problems, and a result that disappears.

**Design:**
```
Status bar:  $(sync~spin) Building testcust… (Esc cancels)        → during build
             $(error) TestProj  3 errors                          → after a failed build
Notification (failed build only):
  ┌───────────────────────────────────────────────────────────┐
  │ ⓧ Build failed: 3 errors, 2 warnings in TestProj           │
  │                              [Show Problems] [Show Output] │
  └───────────────────────────────────────────────────────────┘
Project view:   AxClass
                  CMCLoyaltyService      Class        3 ●   ← red badge, error count
```
**Interaction:**
- Before building, save every dirty `xpp:` document. Ask once, with "Always" remembered in the `xpp.build.saveBeforeBuild` setting.
- Loading: a window progress item, cancellable. Don't call `output.show` unless `xpp.build.revealOutput` is `always`.
- Success: status bar check mark, no notification.
- Error: the notification above. Show Problems runs `workbench.actions.view.problems`. F8 / Shift+F8 then step through the errors natively.
- Project items get an error-count badge.

**Accessibility:** the notification text carries the counts. Badges are paired with a tooltip: "3 compiler errors".

**Implementation:** [src/extension.ts](src/extension.ts) `xpp.build`/`xpp.buildProject`: call `vscode.workspace.saveAll(false)` filtered to `xpp:` documents first. Remove `this.output.show(true)` from `XppBuilder.build`. Extend `ChangeDecorations.provideFileDecoration` to merge `vscode.languages.getDiagnostics(uri)` error counts, and fire `onDidChangeDiagnostics`.

**Effort:** S

### UX4: Project view title bar diet
**Problem:** F4.

**Design:**
```
D365FO PROJECT  TestProj (testcust)      [+] [▶ Build] [⌕] [⋯]
                                          │              └ Refresh
                                          │                Project ▸ Open… / Open Recent… / Create… / Close
                                          │                Check   ▸ Best Practice / Review My Changes /
                                          │                          Pre-Deploy Checklist / Run Tests
                                          │                Build Model
                                          └ New Element… (UX1) + Wizards ▸
```
**Interaction:** three primary actions: New, Build Project, Open Object. Everything else goes into `submenus`. With no project open, the title shows only Open and Create, and the welcome text covers the rest. Ctrl+Shift+B and the status bar click should both run **Build Project** when a project is open, and Build Model when there is none. Today one builds the model and the other the project.

**Accessibility:** every action has a title, and submenus are keyboard reachable through the overflow.

**Implementation:** add `contributes.submenus` (`xpp.project.projectMenu`, `xpp.project.checkMenu`, `xpp.newMenu`) and regroup `view/title`. No code changes beyond the keybinding `when`.

**Effort:** S

### UX5: One "Open Element" with recents, filters and actions
**Problem:** F6, F7.

**Design:**
```
┌ Open Element ────────────────────────────────────────────────┐
│ > t:cust                                    $(filter) Tables  │
│ ── Recent ───────────────────────────────────────────────── │
│   $(table) CustTable        Table · Application Suite         │
│ ── Matches ──────────────────────────────────────────────── │
│   $(table) CustGroup        Table · Application Suite         │
│   $(table) CustTable.CMCExt Table extension · testcust (yours)│
│                               [$(code)] [$(split-horizontal)] [$(add)] │
└───────────────────────────────────────────────────────────────┘
```
**Interaction:**
- Empty: shows the last 15 opened elements (workspace state).
- Prefixes filter by type: `c:` classes, `t:` tables, `f:` forms, `e:` EDTs, `n:` enums, `m:` menu items.
- Item buttons: View Code, Open to the Side, Add to Project.
- Enter opens the element exactly as an Application Explorer click does (`elementOpenCommand`).
- Loading: `busy`. No match: one item, "No element named 'x' – New Element…" (UX1).

**Accessibility:** QuickPick item buttons have tooltips. Friendly type names (`typeLabel`) instead of `AxTable`.

**Implementation:** [src/navigation.ts](src/navigation.ts#L267) `openObject`: use `aot.elementOpenCommand`, `QuickPickItem.buttons`, `onDidTriggerItemButton`, and a recents list in `context.workspaceState`.

**Effort:** S

### UX6: Property grid that matches Visual Studio's
**Problem:** F8. Properties are the main way to edit metadata, and you can't search them, see which ones you changed, or pick a value.

**Design:** inside the element designer webview.
```
┌ Properties ─────────────────────────────────────────────────┐
│ Table field: LoyaltyTier                                      │
│ [⌕ Filter properties      ] [● Modified only] [Az|≡ Category] │
│ ▾ Data                                                        │
│   ExtendedDataType  │ CMCLoyaltyTierId         [⋯] [→]        │
│   EnumType          │                          [⋯]            │
│ ▾ Appearance                                                  │
│ ● Label             │ @CMC:LoyaltyTier         [⋯] [→]        │
│                     │ "Loyalty tier"                          │
│   HelpText          │                          [⋯]            │
├───────────────────────────────────────────────────────────────┤
│ Label: text shown for the field; use a label reference.       │
└───────────────────────────────────────────────────────────────┘
```
**Interaction:**
- Filter matches property names and values.
- ● marks values that differ from the default (bold, like VS).
- `[⋯]` opens a native QuickPick through `postMessage`:
  - EDT, Enum, Table, Class: the `find_object` picker from UX5, restricted to that type.
  - Label: searches the label files, plus "Create label '<typed text>' in CMC…" through `create_label`. The new reference is written back.
- The footer shows the property description.
- Empty: "No properties match 'x'". Error: an inline red row message that says which rule failed.
- Keyboard: Ctrl+F focuses the filter, Alt+Down opens the picker, Escape reverts the value.

**Accessibility:** the table has `role="grid"` with labelled headers. The footer is `aria-live="polite"`. ● is backed by `aria-label="modified"`.

**Implementation:** [media/element.js](media/element.js) `renderProperties`, plus `src/elementDesigner.ts` new messages (`pickReference`, `pickLabel`). The server's `get_element_properties` needs to return `category`, `isDefault` and `description`, all available through `[AxProperty]` reflection in `MetaModelBridge.ElementDesigner.cs`.

**Effort:** M

### UX7: Results view for checks and reports
**Problem:** F9. Untitled markdown is a dead end.

**Design:** a native tree view in a new panel container "F&O Studio", next to Problems and Output.
```
PANEL: PROBLEMS  OUTPUT  F&O STUDIO ▸ [Results] [Infolog] [Copilot Activity]
RESULTS  Review My Changes · since main · 14:02     [↻ Rerun] [⧉ Copy PR summary] [⤓ Export]
▾ $(pass) Pull request summary         12 elements in 1 model
▾ $(warning) Performance               3
    CMCLoyaltyService.run  line 42     select inside while select
▾ $(error) Chain of Command            1
    SalesFormLetter_CMC_Extension.run  does not call next
▾ $(pass) Best practice                0 errors, 4 warnings  → Problems
▸ History: Pre-Deploy Checklist 13:40 ✗ · Upgrade Readiness 11:05
```
**Interaction:**
- Clicking a finding opens the code at that line.
- Rerun repeats the last command with the same arguments. Export writes `.md`, and the PR summary copies the existing markdown.
- Loading: the view's `message` shows "Reviewing… CMCLoyaltyService (4/12)", with `withProgress({location: {viewId}})`.
- Empty: welcome text with buttons for Review My Changes and Pre-Deploy Checklist.
- Error: a top node with `$(error)` and its message.
- The Pre-Deploy Checklist root carries the inline action "Create Deployment Package" when every check passes.

**Accessibility:** native tree, with severity in the item label text, not only in the icon.

**Implementation:** new `src/results.ts` (`TreeDataProvider` keeping the last N runs). `review.ts`, `checklist.ts` and `upgrade.ts` return structured findings instead of strings, and markdown becomes an export format. `viewsContainers.panel`.

**Effort:** M

### UX8: Environment setup checklist and switching without reload
**Problem:** F5, F10.

**Design:** the Environment group of UX2 shows progress while connecting.
```
▾ $(server-environment) Environment     Connecting org2f1d2adc…
    $(pass)  Sign in to Dataverse
    $(pass)  Check SQL LocalDB
    $(sync~spin) Download metadata        6.1 / 9.8 GB · 63%
    $(circle-outline) Extract PackagesLocalDirectory
    $(circle-outline) Restore cross-reference database
    $(circle-outline) Configure machine
    [Cancel]                               (on failure: $(error) step · [Retry from here] [Show Log])
```
**Interaction:**
- The notification keeps a one-line summary ("Step 3/6 · 63%") with a link that focuses the view.
- Retry resumes at the failed step. `runSetup` already skips steps that are done, so this is cheap.
- Success: restart both servers and refresh the Application Explorer, project view and open designers in place. No reload prompt.
- Select Environment does the same.
- Webview panels register serializers, so a reload (if one is still needed) restores designers, data panels and label editors.

**Accessibility:** step state is in the item label ("Download metadata, in progress, 63 percent"). Progress is also reported through the native notification for screen readers.

**Implementation:** `src/setup.ts` emits step events, and `EnvironmentStatus.nodes()` renders them. `server.restart()` and `languageServer.restart()` are already in `xpp.restartServer`. Add a `designers.refreshAll()` call. Call `registerWebviewPanelSerializer` for `xppElementDesigner`, `xppFormDesigner`, `xppData`, `xppLabelEditor`, `xppTrace` and `xppErd` (state = element/type or file).

**Effort:** M

### UX9: Panel home for Infolog and Activity, quieter status bar
**Problem:** F11, F12.

**Design:**
```
Sidebar F&O STUDIO:  Application Explorer · D365FO Project · Security · Dynamics 365
Panel   F&O STUDIO:  Results (UX7) · Infolog · Copilot Activity
Status bar (F&O workspace):   $(server-environment) org2f1d2adc  $(check) TestProj  $(database) 14:32
Status bar (other workspace): (nothing)
```
**Interaction:**
- Infolog auto-reveals (preserving focus) on the first message of a debug session. Empty state: "Messages from the code you debug appear here. [Launch Debugger]".
- The server item appears only while starting or stopped. The database item appears only when credentials exist or a data panel is open.
- The project item is hidden unless the workspace has an `.rnrproj`, `xpp.project`, or an open `xpp:` editor.

**Accessibility:** status bar items keep their `name`s, and moving views doesn't change their ids.

**Implementation:** [package.json](package.json) `viewsContainers.panel` and move the `xpp.infolog` and `xpp.activity` view entries. `ProjectStatus.refresh()` hides the item under the conditions above. Add `viewsWelcome` entries for both views.

**Effort:** S

### UX10: Record inspector with relation drill-down in Browse Data
**Problem:** F14. Wide F&O tables are unreadable as a grid, and following a foreign key means writing SQL.

**Design:** the existing data panel gets a resizable side pane.
```
┌ CustTable [Data] ───────────────────────────────────────────────────────────┐
│ Company [usmf] Rows [100▾] [Refresh] [Open as SQL]                           │
├─────────────────────────────────────────┬────────────────────────────────────┤
│ ACCOUNTNUM │ CUSTGROUP │ PARTY  │ ...   │ Row 3 of 100   [⌕ field      ]  ◀ ▶ │
│ US-001     │ 10        │ 52565… │       │ AccountNum   US-001                 │
│▶US-002     │ 30        │ 52566… │       │ CustGroup    30         → CustGroup │
│ US-003     │ 10        │ 52567… │       │ Party        5637144577 → DirPartyTable│
│                                         │ Blocked      0 (No)                 │
│                                         │ [Copy as JSON] [Copy WHERE clause]  │
└─────────────────────────────────────────┴────────────────────────────────────┘
```
**Interaction:**
- Selecting a row fills the inspector. ◀ ▶ (or Up/Down in the grid) moves between rows.
- `→ Table` opens a new Browse Data tab on the related table, filtered by the relation's constraints from the `table_erd` metadata.
- The field filter narrows long field lists.
- Empty: "Select a row". Error: inline message if the relation can't be resolved.
- Keyboard: Enter on a grid row focuses the inspector, Ctrl+Enter follows the focused relation.

**Accessibility:** the inspector is a `<dl>` with field labels. Relation links are buttons with `aria-label="Open related CustGroup where CustGroup = 30"`. Uses theme colors and `a11y.css`.

**Implementation:** [media/dataPanel.js](media/dataPanel.js) and `media/dataPanel.css`. `src/database/dataPanel.ts` adds a `relations` message, built from the `table_erd` bridge action (already in the contract). The related browse reuses the existing parameterized filter (no SQL string concatenation).

**Effort:** M

## Quick wins

1. Fix the mojibake: "Create D365FO Project?" → "Create D365FO Project…" ([package.json](package.json#L1171)), and "Finding event handlers?" → "Finding event handlers…" ([src/codeIntel/providers.ts](src/codeIntel/providers.ts#L180)).
2. Change `contributes.configuration.title` from "X++" to "F&O Studio" to match UI14.
3. Add `viewsWelcome` for `xpp.infolog` and `xpp.activity`. Both are blank when empty today.
4. Use friendly type names (`typeLabel`) in project item descriptions ([src/project.ts](src/project.ts#L349)) and in the Open Object quick pick, instead of `AxClass`.
5. Make Open Object open elements with `elementOpenCommand`, exactly as an Application Explorer click does (tables → designer, forms → element designer).
6. Replace the walkthrough's `media/icon.png` step images with real cropped screenshots (`docs/images/aot.png`, `form-designer.png`, `trace.png`) and meaningful `altText`.
7. Move the element designer's View code / Form layout / Refresh into `editor/title` (`when: activeWebviewPanelId == xppElementDesigner`). Add **Create Extension** there when the element is read-only.
8. In the build commands, save dirty `xpp:` documents first, and stop `XppBuilder.build` from calling `output.show(true)`. Offer "Show Problems" on failure.
9. Make Ctrl+Shift+B and the project status bar item run the same build (project when a project is open, model otherwise).
10. Register webview panel serializers so designer, data, label, trace and ERD tabs survive a window reload.