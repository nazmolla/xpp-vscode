# Changelog

Release notes of F&O Studio for VS Code. They are generated from the "What's new" sections of [docs/FEATURES.md](https://github.com/nazmolla/xpp-vscode/blob/HEAD/docs/FEATURES.md); edit them there.

## 0.17

### 0.17.1

**Debugging the local AOS when the C# extension is disabled.**
- Debugging a cloud-hosted or VHD environment uses the C# extension's .NET debugger. When that extension was installed but disabled, F&O Studio kept offering to install it and then asked to reload the window, again and again. It now says the extension is disabled and opens its page so you can enable it. It no longer reports an install it didn't see, and it shows the error when an install fails ([#16](https://github.com/nazmolla/xpp-vscode/issues/16)).

### 0.17.0

**SSRS reports without Visual Studio: design them yourself, or let Copilot build them from a scanned form.** Reports were the last part of F&O development that needed Visual Studio. Cheques, purchase orders, invoices and other preprinted forms can now be laid out to the millimetre, previewed, built and deployed from VS Code.

*Design it yourself, as in Visual Studio:*
- **New Element… → Report** creates an empty report and opens it; build it from scratch. **Report (wizard)** still creates the whole chain (data provider, contract, controller, menu item, privilege) when you want it.
- **The report in the element designer looks like Visual Studio's:** Datasets, Designs, Images and Parameters, with Visual Studio's property names, categories and hidden properties. Page size, margins and the other sizes are expandable rows in the Properties pane.
- **Datasets:** New → Data Set, then the **Query** property's `…` picks the source for the Data Source Type: a report data provider class and its table, a query or an enum, then the fields. The dataset's fields and parameters and the report's parameters (the hidden `AX_*` ones, the contract's, the dynamic filter) are filled in by Microsoft's own schema code, as in Visual Studio.
- **Auto designs:** New → Auto Design, **Add Data Region from Data Set…** for a table or list, then **Create Precision Design…** turns it into a precision design with Microsoft's report generator, as Visual Studio does.
- **Report Designer** for precision designs (right-click a precision design → **Open Report Designer**, or double-click it):
  - a page with rulers in mm or inches, grid, snap and zoom; move and resize with the mouse, or by 0.1 mm with `Ctrl`+arrows; align, same size, distribute;
  - textboxes, rectangles, lines, images and tablixes; fields, parameters and labels dragged from the report data;
  - warnings while you design: a body wider than the page (blank pages), items outside the body, growing textboxes that push others down, fonts D365FO doesn't render;
  - **Scan overlay** and **measure** tool: lay a scanned form under the page to place each field on preprinted stationery;
  - **Preview** with Microsoft's report engine (downloaded from nuget.org on first use, after you accept Microsoft's licence), with sample data or your own rows.
- **Build** compiles the reports (`reportsc`) into the package, as Visual Studio does, so **Deploy** puts them on the environment.

*Let Copilot build it from a scanned form:*
- **F&O Report Builder** agent: give it a scan or sample of the form (cheque, PO, invoice, preprinted stationery). It measures the scan, decides between a new report and a copy of the standard one, lists the elements it will create (only the ones needed: for a cheque, the copy of the standard cheque and one controller extension), writes the precision design to the measured positions, renders it and compares it with the scan until every field lines up, then builds.
- **New agent tools:** `xpp_get_report_design`, `xpp_set_report_design` (save a design, copy a standard report, or generate a precision design from an auto design), `xpp_render_report` (pages as images, PDF), `xpp_measure_image` (scale, deskew, boxes, rules and text positions in mm, with Windows' local OCR) and `xpp_compare_layout` (overlay of the render on the scan, offset of each field in mm).
- The F&O Report Developer agent and the development guide now cover the report data sources, extending standard reports and print management, and create only the elements a report needs.
- *Tested:* a Canadian cheque built by the agent from a scan, deployed to a UDE and printed there with **Print test**: the fields land within half a millimetre of the scan. *Not tested yet:* MICR lines on a real printer.

## 0.16

### 0.16.0

**C# projects in your solution, like Visual Studio's Solution Explorer.**
- **The project view shows the solution:** your X++ project with its **References**, the other X++ projects of the solution (**Open This Project** switches to one), and the C# projects of the solution or that your project references, with their files. Click a `.cs` file to edit it; the C# projects are folders of the project's workspace, so the C# extension gives you IntelliSense.
- **Add Reference…** to a C# project of the solution, any `.csproj`, or an assembly (`.dll`); **Remove Reference**. As in Visual Studio, this writes the reference into the `.rnrproj` and an `AxReference` into your model, through which X++ code uses the assembly's types.
- **New C# Project…** creates a .NET Framework 4.8 class library next to your X++ project, adds it to the solution and references it. It compiles against Finance and Operations' assemblies (X++ types are in `Dynamics.AX.Application`) without copying them, and finds them through the active configuration, so it builds on every developer machine.
- **Build** (and `xpp_build` for agents) builds the referenced C# projects first with MSBuild and copies what they need into your package's `bin` folder before X++ is compiled: the assemblies with their dependencies, PDB and XML files and satellite assemblies, and files marked *Copy to Output Directory*. Finance and Operations' own assemblies are never copied. **Deploy** refreshes them first, so the package carries them.
- **Debugging** loads the symbols of the assemblies your projects reference, so breakpoints in their C# code bind next to your X++ breakpoints.
- MSBuild comes from Visual Studio or the Build Tools, else from the .NET SDK; `xpp.build.msbuildPath` picks another one.
- **Packages linked into your metadata folder** with a junction or symbolic link are offered for deploy and build ([#14](https://github.com/nazmolla/xpp-vscode/issues/14)).
- **Analyze Trace** opens long traces: call trees no longer fail with "RecursionLimit exceeded", and the calls the report leaves out load when you expand a call or click **Show N more calls** ([#12](https://github.com/nazmolla/xpp-vscode/issues/12)).
- The code view, formatter and syntax colouring handle `@'...'` verbatim strings and interfaces, so the trace viewer and Go to Definition open those classes ([#11](https://github.com/nazmolla/xpp-vscode/issues/11)).
- **`@fno` is removed:** VS Code retires chat participants. Pick an F&O agent in the chat's agent picker instead (see Copilot below, [#13](https://github.com/nazmolla/xpp-vscode/issues/13)).
- **Form preview, redrawn after the web client** ([#15](https://github.com/nazmolla/xpp-vscode/issues/15)): the action pane card with its toolbar (New, Delete and the other root buttons), tabs and button groups in columns of three; menu buttons open their list; details forms open on the list and the record link opens the details view; only the first fast tab is open and the others show their summary fields; table of contents, grids, filters and fields with the web client's sizes. Width, Height, DisplayLength, multi-line text, ArrangeMethod, Columns and VisibleRows change the drawing as you edit them. Hidden controls, what extensions added and controls behind a configuration key show on request.
- **Go to Definition across X++ and C#:** from X++ to C# types and members of the referenced projects, from C# to X++ elements (`Dynamics.AX.Application`), with hover and completion of .NET types in X++.
- **Code view of classes with tab-indented methods** no longer re-indents them on save (round trip of `PurchFormLetter`, `TaxCalculation` and others).
- **Visual Studio side by side:** configurations F&O Studio writes now carry every member of Microsoft's template, so Visual Studio lists them in *Manage local XPP configurations* and loads metadata with them; existing ones are completed when the extension starts. **Check Visual Studio's FinOps Tools Version** (and a one-time prompt, only when a Visual Studio with the FinOps tools is installed) offers to install the tools that match the environment, because Visual Studio's Application Explorer does not open when they differ. F&O Studio itself never needs Visual Studio.
- The first form opened after start no longer fails with "An item with the same key has already been added".
- *Tested:* X++ calling C# and C# calling X++ (`CustTable`) build together on this machine's UDE configuration; the solution view in an extension host. *Not tested yet:* hitting a C# breakpoint on a UDE or VHD.

## 0.15

### 0.15.6

**Deploy and debug your own code like Visual Studio, on unified, VHD and cloud-hosted environments.**

*Unified developer environments (UDE):*
- **Deploy Models to Environment** uploads your packages as you built them, through the environment's Dataverse, as Visual Studio's Deploy Models to Online Environment does. Nothing is recompiled, so the environment runs the build you debug. Before, the command recompiled an empty copy of your model with pac's own compiler.
- Pick the packages to deploy (your project's package is preselected) and the database sync: None, Module (the deployed packages) or Full. Progress shows in the **X++ Deploy** output.
- **Synchronize Database** also runs through Dataverse. The Power Platform CLI (`pac`) is no longer needed for deploy or database sync.
- The `deploy` task takes `"dbSync": "None" | "Module" | "Full"`; `Incremental` (which needed an argument file with pac) now runs a Module sync.
- Breakpoints in your own code stop after the deploy: they bind, step, show variables, and the stop shows in your code view.

*Cloud-hosted and VHD environments, like Visual Studio's F5:*
- Start debugging and F&O Studio attaches to the AOS of the runtime host type set in Visual Studio's Dynamics 365 options: the AOS site's application pool in IIS, or IIS Express. No process list to pick from. To debug the batch service, set `"processName": "Batch.exe"`.
- Breakpoints in your own code bind and stop.
- After a successful build, the AOS hosts that are running (IIS Express or the IIS application pool, and the batch service) restart so they load the new build, as Visual Studio does. The debugger detaches first. The batch service is ended and started again right away; batch tasks that were running are cut off. Turn this off with `xpp.build.restartLocalAos`.
- Stopping the debugger only detaches; the AOS keeps running.
- PackagesLocalDirectory and the AOS are found from Visual Studio's Dynamics 365 options, the batch service and the AOS web.config, on any drive.
- Needs the C# extension (its .NET debugger); F&O Studio offers to install it and waits for its debugger download.

*All environments:* the compiler writes Windows PDBs, which the VS Code debugger can't read, so every successful build converts them to portable PDBs with Microsoft's Pdb2Pdb (included). Visual Studio debugs the converted PDBs too.

*Tested on:*
- **UDE** (10.0.2790.108): deploy without a database sync, Synchronize Database (Module), then a breakpoint in a class of your own bound, stopped in the code view, stepped and showed variables and the Infolog; standard-code breakpoints as in 0.15.4–0.15.5.
- **VHD** (10.0.46, on IIS and on IIS Express): build converts the PDBs and restarts the AOS, attach without a process list, breakpoints in your own and in standard code stop, step, continue, Infolog; stopping leaves the AOS running. The new way of restarting the batch service has not been run on a VHD yet.
- **Cloud-hosted environments** use the same code path as VHD environments but have **not been tested** yet.
- Not tested yet: deploying with a Module or Full sync, several packages in one deploy, packages larger than 100 MB.

### 0.15.5

**Debugging shows your code view, and the debugger is configurable.**
- When the debugger stops in an element, it shows the X++ code view you set the breakpoint in (for example `PurchTable.xpp`), not a separate debug copy of the file.
- Configure the debugger in `.vscode/launch.json`: `justMyCode`, `additionalModules`, `symbolOptions` and `logging` (see Debugger options under Settings).

### 0.15.4

**Debugging Microsoft's code uses Visual Studio's debugger settings.**
- Breakpoints in standard code are set up the way Visual Studio sets them up: Just My Code stays on and only the module that holds the element loads symbols. Before, the environment could stop responding while the debugger was attached.
- Tip: open the form or run the code once before you start the debugger, so the breakpoint binds right away.

### 0.15.3

**The debugger never leaves the environment frozen.**
- While the debugger is stopped (at a breakpoint or after Pause), the environment's server is suspended for every user. Disconnecting, closing or reloading the window now resumes it first and only detaches, never ends it.
- A debugger left attached by a VS Code window that crashed is stopped the next time F&O Studio starts.

### 0.15.2

**Breakpoints in Microsoft's code (and in your own) bind and stop on unified developer environments.**
- A breakpoint in a standard element's code view, for example the `init` method of the PurchTable form, now binds, stops, steps and shows its variables. Before, it stayed hollow with "No symbols have been loaded for this document".
- The debug source is the element's code as the compiler numbers it (for forms, with their data source and control classes), so breakpoints land on the right line.
- The breakpoint you set in the code view shows the same state as the one the debugger binds, and breakpoints you add while debugging are picked up right away.

### 0.15.1

**Fixes from an end-to-end trial of every tool and command.**
- Run Best Practice Checks no longer fails with "line must be non-negative" (metadata findings without a line).
- Forms: `xpp_read_object` returns their data sources, and `xpp_validate_object` checks form data sources and controls correctly.
- Copilot checkpoints no longer block a change when the element's earlier state can't be saved; Revert names that element.
- Browse Table Data without database credentials asks in a dialog instead of waiting on a notification.
- New labels show their text in the designer and form preview right away.
- `xpp_read_label` accepts full references (`@SYS7149`, `@SCM:SalesStatusLabel`); model and object lists mark your own models; exact name matches come first.
- **Report Problem** on every unexpected error, and it can file the GitHub issue directly with your GitHub sign-in.
- The Kusto snippets were removed (they needed a Kusto extension); the Telemetry Queries command has the same queries.

- **Copilot agents for developers and functional consultants.** 23 agents in the Copilot agent picker, each with its own instructions and the next agent to hand off to:
  - **Developers:** F&O Developer, Code Reviewer, Support, Performance, Upgrade Reviewer, Test Engineer, Report Developer, Workflow & Events, Release Manager, Code Archaeologist.
  - **Developers and consultants:** Solution Designer, Architect, Security & License, Integration.
  - **Functional consultants:** Functional Explainer, Setup Advisor, Data Investigator, Functional Triage, Role Designer, Test Scenarios, Fit-Gap Analyst, Data Migration, Release Notes. They answer in business terms and use the live environment through the Dynamics 365 ERP MCP server or the browser.
  - When you haven't picked an agent, Copilot suggests the one that fits, or runs it for a self-contained part of the task. Agents change data, setup or metadata only after you allow it, and never in production.
- **Find Message Source for mandatory fields.** For "Field '…' must be filled in." it first lists the mandatory table fields with that label (the platform raises it when a field whose Mandatory property is Yes is empty), then code that raises it for one of those fields, and keeps code that reuses the text for other fields apart. `xpp_find_message_source` does the same for agents.

## 0.14

### 0.14.1

**Server internals and a security analysis fix.** Bridge actions, agent tools and element types are each registered once; `tools/list` comes from the same registry that serves the calls, and write checks come from each action's registration. Behaviour is unchanged (every action and tool is checked against a recorded baseline). `xpp_analyze_security` with `op=license` or `op=access` and no `objectName` now says what is missing instead of failing with an internal error.

- **New wizards:** **New Table with Form…** (key EDT, table with an alternate key and find/exist, a SimpleList form, menu item, View and Maintain privileges) and **New Data Entity…** (entity for OData and data management, staging table, privileges).
- **Rename everywhere:** rename a field of your table in the designer, or **Rename…** one of your elements in the Application Explorer. X++ code that uses it changes in the refactor preview, and the metadata of your other elements (menu items, form controls, data sources, entity fields, relations, security) follows.
- **View Dependencies:** what an element uses, from the cross-reference database. Every diagram exports Mermaid.
- **Debugger on cloud-hosted and VHD environments** (*preview*): Launch Debugger attaches to the local AOS (w3wp.exe or Batch.exe) with the C# extension's .NET debugger.
- **Export Labels… / Import Labels…** as CSV with one column per language.
- **Tasks:** `deploy` (with database sync) and `syncDatabase` next to the build tasks.
- **Analyze Performance** checks form data sources: OnlyFetchActive for grids that show few fields, and Delayed or Active data sources shown in the parent's grid instead of joined.
- **Azure DevOps:** **Link Work Item…** adds the work item to the commit message, Review My Changes lists the branch's work items, **Open Pipelines** opens Azure Pipelines or GitHub Actions.

## 0.13

### 0.13.3

**Analyze Trace on VHD environments, Copilot opens code in the editor.** Analyze Trace finds the event manifests on local VHD environments (`AosService\WebRoot\Monitoring` or the registered trace providers), or in the folder set in `xpp.trace.manifestFolder` / `manifestFolder` ([#10](https://github.com/nazmolla/xpp-vscode/issues/10)). The new `xpp_open_element` tool lets Copilot show an element in the X++ code editor or designer, at a method and line, instead of opening its XML. **Open Element…** opens the element when you press Enter before the results appear, and has its own icon.

### 0.13.2

**Build compiles label files.** LabelC now gets the label file names with their language (`Id_en-US,Id_de` instead of `Id`), so models with label files build, and Build and best practice checks find a model whose package has a different name ([#9](https://github.com/nazmolla/xpp-vscode/issues/9)).

### 0.13.1

**Analyze Trace for errors and integrations.** The trace analyzer lists every AOS and session in the trace with its user (dual-write, OData, batch, other users), finds errors and exceptions with their full X++ call stack, shows UTC times to match integration logs, and filters by AOS, session, user, method or time window (also in `xpp_analyze_trace`).

Features marked *preview* have not yet been verified on a live environment.

**Daily loop**
- **Code folding** for methods, statements, `case` blocks, comments and `// region` blocks.
- **Build Project** from an X++ editor with `Ctrl+Shift+B`. Unsaved code is saved first, and a failed build offers **Show Problems**.
- **New Element…** works like Visual Studio's Add New Item. It covers every element and extension type plus the wizards, from `+` on the project view.
- **Open Element…** (`Ctrl+Alt+O`) shows recent elements and filters by type prefix (`t:` tables, `c:` classes, `f:` forms).
- **F&O Studio panel:** Results, Infolog and Copilot Activity in one place.
  - Review My Changes, the Pre-Deploy Checklist and Upgrade Readiness show their findings in Results. Click a finding to open it; use **Run Again**, **Copy Report as Markdown** or **Export Report…**.
- **Dynamics 365 view grouped by task:** Environment, Create, Build and check, Deploy, Data, Analyze, Find, Security, Labels, Model and project, Copilot, Debug.
- **Properties grid:** filter, categories, changed values marked, a label picker that finds or creates labels, and element pickers (`Alt+Down`). **Go to Referenced Element…** replaces the separate Go to commands.
- **Browse Table Data** has a record inspector that follows relations to the related rows.
- Element and form designers, the label editor, data tabs and trace tabs come back after a restart.

**Environment and team**
- **Version drift guard** (*preview*): you get a warning when the environment runs a newer application version than the metadata on this machine.
  - **Update Metadata** downloads the new version.
  - **Clean Up Old Application Versions…** frees disk space and keeps the newest older version for Standard Code Drift.
- **Environment setup** shows its steps in the Dynamics 365 view and can retry a failed step. Switching environments no longer needs a window reload.
- **Open Team Repository…** (*preview*) clones a repository, links its packages into your metadata folder and builds them in dependency order.
- **Deployed or not:** the project's status bar item shows when the build is newer than the last deploy from this machine. Run Tests, the debugger and `xpp_run_tests` warn first and offer **Deploy First**.

**Upgrades and review**
- **Standard Code Drift of Wrapped Methods…** shows the standard methods your Chain of Command code wraps that changed between two installed versions, with a diff for each.
- **Upgrade Readiness** also reports collisions with other models' extensions: the same field, index, field group, enum value or form control, and several wrappers of one method.
- **Data entity readiness** in Analyze Performance, Review My Changes and the Pre-Deploy Checklist: public names, keys, staging fields, mandatory fields and set-based processing.
- The **Pre-Deploy Checklist** fails while Run Snippet's scratch class is still in the model.

**Create and test**
- New wizards: **New Business Event…**, **Add Number Sequence…** and **New Feature (Feature Management)…**. **Show Feature Usage** finds where a feature is checked in code.
- **Generate SysTest Class with Test Data (ATL)** builds test data (customers, vendors, items, warehouses) with the Acceptance Test Library and adds the model references it needs.
- **Try Data Entity (OData)…** and **Try Service Operation…** (*preview*) call the active environment and save each request as an `.http` file. Service calls ask first, and the access token is never written to the file.

**Copilot**
- **Checkpoints:** **Start Copilot Checkpoint**, then **Revert Copilot Changes Since the Checkpoint…** to undo every element, label and project change Copilot made.
- **Set Copilot Scope…** limits the elements Copilot may change. The server enforces the scope.
- **Verify Changes in the Running App (Copilot)** (*preview*) turns your changed elements into check steps. Copilot agent mode then runs them with the Dynamics 365 ERP MCP server, which you set up separately.

**Support**
- **Report a Problem…** builds a redacted diagnostics bundle for you to preview and send yourself; nothing is sent automatically. Every command error offers it.
- **Configure Code Intelligence…** turns code intelligence features on or off in one place.

## Earlier releases

- **0.12:** Browse Table Data and SQL queries (temporary UDE credentials, local AxDB), Analyze Performance with live database insights, Analyze Trace, Upgrade Readiness, wizards (batch job, report, approval workflow, custom service), best practice checks, Review My Changes, the Pre-Deploy Checklist, `@fno` in chat, Copilot Activity, team rules, Test Explorer, build tasks and an accessibility pass.
- **0.11:** Go to referenced elements, F12 in code, element history, drag and drop from the Application Explorer, one workspace per project, server-side write checks.
