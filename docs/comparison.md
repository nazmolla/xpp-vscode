# F&O Studio for VS Code vs. Microsoft's Visual Studio tools for Dynamics 365 F&O

## TL;DR
F&O Studio runs on the same engine as Microsoft's Visual Studio extension:
- the same metadata API, X++ compiler (xppc), label compiler and best practice checker (xppbp);
- the same cross-reference database, debugger proxy and deployment scripts;
- the same project and package formats (`.rnrproj`, `.axpp`).

So the code you build in one opens and builds in the other. The difference is what's built on top. F&O Studio adds AI agent tools that work on real metadata, data and SQL inside the editor, trace and performance analysis, security and license analysis, and upgrade and release checks. Visual Studio is still ahead on the SSRS report designer, the data entity wizard, dependency diagrams, and debugging on cloud-hosted and VHD environments; all but the report designer are on F&O Studio's roadmap.

## At a glance

✅ available · 🟡 partly, or another way · 🗓️ planned (on the [roadmap](ROADMAP.md)) · ❌ not available

| Feature | F&O Studio | Visual Studio |
|---|:---:|:---:|
| **Cost and platform** | | |
| Free and open source (Apache 2.0) | ✅ | ❌ |
| No Visual Studio license needed | ✅ | ❌ |
| Unified developer environments (UDE) | ✅ | ✅ |
| Cloud-hosted and VHD environments | ✅ | ✅ |
| **Application Explorer and projects** | | |
| Classic view (elements by type) | ✅ | ✅ |
| Model view (elements by model) | ✅ | ✅ |
| Name filter | ✅ | ✅ |
| Open Element with recent elements | ✅ | ❌ |
| Type prefixes in Open Element (`t:`, `c:`, `f:`) | ✅ | 🟡 metadata search syntax |
| Create Extension from the context menu | ✅ | ✅ |
| Create Code Extension (Chain of Command) | ✅ | ✅ |
| Drag and drop elements into designers | ✅ | ✅ |
| Projects (`.rnrproj`) | ✅ | ✅ |
| Import and export projects (`.axpp`) | ✅ | ✅ |
| Git change markers on project items | ✅ | ✅ |
| Create and update models | ✅ | ✅ |
| View model dependencies | ✅ | ✅ |
| **Create elements** | | |
| New Element (Add New Item) | ✅ | ✅ |
| Batch job wizard (SysOperation) | ✅ | ❌ |
| Report wizard with a ready-to-run design | ✅ | ❌ |
| Approval workflow wizard | ✅ | ❌ |
| Custom service wizard | ✅ | ❌ |
| Business event wizard | ✅ | ❌ |
| Number sequence wizard | ✅ | ❌ |
| Feature management wizard | ✅ | ❌ |
| Table + form + menu item + privilege wizard | 🗓️ | ❌ |
| Data entity wizard | 🗓️ | ✅ |
| **Designers** | | |
| Element designer with node tree | ✅ | ✅ |
| Properties grid with categories and search | ✅ | ✅ |
| Changed property values marked | ✅ | ✅ |
| Label picker that finds or creates labels | ✅ | 🟡 lookup only |
| Undo and redo in the designer | ✅ | ✅ |
| Copy event handler method | ✅ | ✅ |
| Find event handlers | ✅ | ✅ |
| Form preview like the web client | ✅ | ✅ |
| Click a section or control in the preview to select it in the designer | ✅ | ✅ |
| SSRS report designer | ❌ | ✅ |
| Label editor | ✅ | ✅ |
| Entity relationship diagram (SVG and Mermaid export) | ✅ | ❌ |
| Class and table hierarchy diagram | ✅ | 🟡 DGML |
| Dependency diagram | 🗓️ | ✅ DGML |
| **Code editor** | | |
| Code view with declaration and methods | ✅ | ✅ |
| Syntax highlighting | ✅ | ✅ |
| Code folding | ✅ | ✅ |
| Completion (IntelliSense) | ✅ | ✅ |
| Completion of methods added by Chain of Command | ✅ | ✅ |
| Signature help | ✅ | ✅ |
| Hover with label text | ✅ | ✅ |
| Go to definition | ✅ | ✅ |
| Find all references (cross-reference database) | ✅ | ✅ |
| Go to implementations, including CoC and event handlers | ✅ | ❌ |
| Code lens with references, CoC classes and subclasses | ✅ | ❌ |
| Call hierarchy | ✅ | ❌ |
| Type hierarchy | ✅ | ❌ |
| Outline and breadcrumbs | ✅ | 🟡 navigation bar |
| Syntax errors while typing | ✅ | ✅ |
| Format document | ✅ | ✅ |
| Code snippets | ✅ | ✅ |
| Quick fix: extract to label | ✅ | ❌ |
| Quick fix: generate `find()` and `exist()` | ✅ | ❌ |
| Quick fix: implement interface methods | ✅ | ❌ |
| Quick fix: create a CoC wrapper method | ✅ | ❌ |
| Rename a method and its calls | ✅ | ❌ |
| Rename a field and its references | 🗓️ | ❌ |
| Rename an element and its references | 🗓️ | ❌ |
| **Search** | | |
| Metadata search | ✅ | ✅ |
| Search code in all packages (text or regex) | ✅ | 🟡 Find in Files |
| Find labels | ✅ | ✅ |
| Find tag references | ✅ | ✅ |
| Find Message Source (paste an error, find the code) | ✅ | ❌ |
| Find similar standard implementations | ✅ | ❌ |
| **Build, deploy, debug and test** | | |
| Build project | ✅ | ✅ |
| Build model | ✅ | ✅ |
| Compiler errors at the right line | ✅ | ✅ |
| Build from `tasks.json` | ✅ | ❌ |
| Deploy and database sync from `tasks.json` | 🗓️ | ❌ |
| Best practice checks (xppbp) | ✅ | ✅ |
| Create deployment package | ✅ | ✅ |
| Deploy models to a UDE | ✅ | ✅ |
| Database synchronization | ✅ | ✅ |
| Warning when the build is newer than the deploy | ✅ | ❌ |
| Debugger on UDE | ✅ | ✅ |
| Debugger on cloud-hosted and VHD environments | 🗓️ | ✅ |
| Infolog | ✅ | ✅ |
| Run SysTest tests on the environment | ✅ | ✅ |
| Run tests on cloud-hosted and VHD environments | ❌ | ✅ |
| Generate a SysTest class | ✅ | ❌ |
| Generate test data with the Acceptance Test Library | ✅ | ❌ |
| Run a snippet of X++ on the environment | ✅ | 🟡 startup object |
| **Data** | | |
| Temporary SQL credentials for UDE | ✅ | ✅ |
| Browse table data in the IDE | ✅ | ❌ |
| Record inspector that follows relations | ✅ | ❌ |
| SQL editor, read-only by default | ✅ | ❌ |
| Query plan (Explain Plan) | ✅ | ❌ |
| Open the database in SSMS | ✅ | ❌ |
| Open the table browser in the web client | ✅ | ✅ |
| **Performance** | | |
| Analyze `.etl` traces without an import database | ✅ | ❌ |
| Errors with full X++ call stacks from a trace | ✅ | ❌ |
| Trace Parser | ❌ | 🟡 separate tool |
| Static performance analysis of X++ | ✅ | ❌ |
| Form data source checks (OnlyFetchActive, link types) | 🗓️ | ❌ |
| Database performance insights | ✅ | ❌ |
| Application Insights (KQL) queries | ✅ | ❌ |
| **Security and licensing** | | |
| Security view with the license of each role | ✅ | ❌ |
| License analysis of a role, duty or privilege | ✅ | ❌ |
| Who Can Access an element | ✅ | ❌ |
| Find security gaps | ✅ | ❌ |
| Segregation of duties check | ✅ | ❌ |
| Security report for sign-off | ✅ | ❌ |
| **Upgrades, review and release** | | |
| Upgrade readiness (obsolete APIs, broken extensions) | ✅ | ❌ |
| Collisions with other models' extensions | ✅ | ❌ |
| Standard code drift of wrapped methods | ✅ | ❌ |
| Review My Changes against a branch | ✅ | ❌ |
| Pre-deploy checklist | ✅ | ❌ |
| Compare code | ✅ | ✅ |
| Compare metadata | ✅ | ✅ |
| Links to Azure DevOps work items | 🗓️ | ✅ |
| Links to Azure DevOps pipelines | 🗓️ | ❌ |
| **Labels** | | |
| Translate missing labels | ✅ | ❌ |
| Find unused labels | ✅ | ❌ |
| Import and export labels | 🗓️ | ❌ |
| **Integration** | | |
| Try a data entity (OData) | ✅ | ❌ |
| Try a service operation | ✅ | ❌ |
| **Environments** | | |
| Connect to a UDE (metadata, cross references, configuration) | ✅ | ✅ |
| Retry a failed setup step | ✅ | ❌ |
| Switch environments without restarting | ✅ | ❌ |
| A different environment in each window | ✅ | ❌ |
| Warning when the environment's version is newer | ✅ | ❌ |
| Open a team repository and build its packages | ✅ | ❌ |
| **AI** | | |
| GitHub Copilot chat and completions | ✅ | ✅ |
| X++ agent tools on the real metadata (33) | ✅ | ❌ |
| `@fno` chat participant | ✅ | ❌ |
| Checkpoints to revert Copilot's changes | ✅ | ❌ |
| Scope that limits what Copilot may change | ✅ | ❌ |
| Activity log of every agent tool call | ✅ | ❌ |
| Team rules for the agent | ✅ | ❌ |
| Read-only SQL for the agent, after your confirmation | ✅ | ❌ |

## Same engine, same results
F&O Studio doesn't reimplement Microsoft's toolchain. It loads it from your installation:
- The **metadata API** (`IMetaModelService`) for every read and write. AOT XML is never written by hand.
- **xppc / LabelC / xppbp** with the same arguments Visual Studio passes. Compiler errors map to the right line.
- The **cross-reference database** for Find References, where-used and tags.
- The **FinOps debugger proxy** for debugging on UDEs. Developers use it to hit breakpoints in X++ on UDEs.
- **RunPackageCreationAll.ps1** for deployment packages, and **pac** for deploy and DB sync on UDE.
- The **`.rnrproj` and `.axpp`** formats, so a project moves between the two IDEs unchanged.

## What F&O Studio adds

**AI that works on the real metadata**
- 33 MCP tools that Copilot (or any MCP client) calls to read, create, extend, validate, build and package elements through the metadata API.
- Guardrails:
  - writes are limited to your own models and are all or nothing;
  - an existing name is never overwritten;
  - best practice findings come back after every write.
- **Checkpoints:** revert every element, label and project change Copilot made since the checkpoint. **Scope:** the server limits which elements Copilot may change.
- `@fno` commands: `/design`, `/extend`, `/explain`, `/review`, `/support`, `/performance`. **Team rules** add your conventions to everyone's agent instructions.
- Agent tools that go beyond writing code:
  - find standard precedents;
  - find the code behind an error message;
  - analyze security and licenses, traces and upgrade impact;
  - run read-only SQL after you confirm;
  - run tests;
  - open an element in the editor at the right line.

**Data in the IDE**
- Browse Table Data with enum names, company filter, as-of date for date-effective tables, and a record inspector that follows relations.
- A SQL editor that is read-only by default, with Explain Plan.

**Performance and diagnostics**
- **Analyze Trace** reads `.etl` files directly, with no Trace Parser import. It shows:
  - every AOS and session with its user, including integrations and batch;
  - errors with their full X++ call stacks;
  - hot paths and SQL per method;
  - pauses.
- **Analyze Performance** flags queries in loops, set-based statements that silently run row by row, missing indexes and cache settings, and data entity readiness problems.
- **Database Performance Insights:** missing indexes, the most expensive queries, batch history and number sequence bottlenecks.

**Security and licensing**
- License impact per role, duty or privilege, and what drives it.
- Who Can Access, security gaps, segregation of duties checks.
- A sign-off report in Markdown or CSV.

**Upgrades and release**
- **Upgrade Readiness:** obsolete APIs, broken extensions and CoC wrappers, and collisions with other models' extensions.
- **Standard Code Drift of Wrapped Methods:** diffs of the standard methods you wrap, between two versions.
- **Review My Changes** against your base branch, and a **Pre-Deploy Checklist** in one report.

**Productivity**
- **Wizards:** batch job (SysOperation), report, approval workflow, custom service, business event, number sequence, feature.
- **Search Code in All Packages**, **Find Message Source** (paste the error the user saw), Find Similar Standard Implementations.
- **Quick fixes:** extract to label, generate `find`/`exist`, create a CoC wrapper, implement interface methods. **Rename** for methods.
- **Labels:** translate missing labels, find unused labels.
- **Try Data Entity (OData)** and **Try Service Operation**, both saved as `.http` files.
- KQL snippets for F&O telemetry in Application Insights.

**Environment and team**
- **Connect to Environment** sets up a UDE end to end (metadata, cross-reference database, configuration) and can retry a failed step.
- **Version drift guard** warns when the environment's version is newer than your metadata.
- **Open Team Repository:** clone, link packages, build in dependency order.

## Where Visual Studio is still ahead
- The **SSRS report designer**. Report layouts are still designed in Visual Studio.
- The **data entity wizard**. In F&O Studio, the agent tools and `New Element…` cover it today; a dedicated wizard is planned.
- **Debugging on cloud-hosted and VHD environments** (planned) and **running tests there**. In F&O Studio both are UDE-only today.
- **DGML** dependency diagrams. A dependency diagram is planned.
- **Official Microsoft support.** F&O Studio is an independent, community project and still a preview.

## Requirements
- **Both:** Windows, the D365 metadata, SQL LocalDB for cross references.
- **F&O Studio:** VS Code 1.101+. It needs pac for deploy on UDE, and GitHub Copilot for the AI features.

*F&O Studio is an independent project and is not affiliated with or endorsed by Microsoft. Dynamics 365 is a trademark of Microsoft Corporation.*
