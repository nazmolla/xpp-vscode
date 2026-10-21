# F&O Studio for VS Code

A full Dynamics 365 Finance & Operations development environment in VS Code, with AI built in. It covers what the Visual Studio Dynamics 365 tools do: Application Explorer, designers, code editing, build, deploy, debugging, plus agent tools for Copilot.

It works against a unified developer environment (UDE) and uses the same metadata API, compiler and scripts as Visual Studio, so projects and models move freely between the two.

## Features

### Application Explorer
- **Classic view** (elements of all models grouped by category and type) and **model view** (each model by display name), with a name filter.
- Click a class to open its code; any other element opens its designer.
- **Right-click actions:**
  - Open Designer, View Code, Open Form Layout.
  - Add to Project, Create Extension, Create Code Extension (Chain of Command).
  - Find References, View References, View Hierarchy.
  - Compare Code, Compare Metadata.
  - Open in Browser, Open Table Browser.

### Element designer
- **Node tree and Properties grid** for every element type, generated from the metadata classes Visual Studio's designer uses.
- **Right-click actions:** New (any node type a folder accepts, such as fields, field groups, indexes, relations, data sources and controls), New Method, Override Method, Delete.
- **Forms:** a preview rendered like the web client, with Table of Contents and FastTab patterns, column layout, field labels from EDTs and toggles for Yes/No fields.
- **Read-only for others' elements:** elements outside your models can be viewed but not changed; extend them instead.

### Code
- **Code view** shows a class the way Visual Studio does: the declaration, then the methods inside its braces. Objects in your own models are editable.
- **Methods of nested nodes** (data sources, fields, controls) open in their own editor.
- **Navigation:** go to definition and hover for element names.

### Build, deploy and debug
- **Build Model** with the X++ compiler (`Ctrl+Shift+B`), and **Build Project**, which reports only the problems in the project's objects. Errors appear in the Problems panel at the right line. A compiler crash (Fatal) or a missing output assembly fails the build.
- **Run Tests:** runs SysTest test classes on the environment the way Visual Studio's Test Explorer does, and shows each test's result. Tests run against the deployed code, so build and deploy first.
- **Create Deployment Package** (the same script Visual Studio runs), Deploy Models and Synchronize Database (with `pac`).
- **Launch Debugger:** attaches to the environment through the FinOps tools' debugger proxy, as Visual Studio does. Breakpoints can be set in the code view.
- **Infolog:** messages raised by the code being debugged.

### Dynamics 365 tools
- **Model management:** Create Model, Update Model Parameters, View Model Dependencies.
- **Projects:** Select Project, Export Project and Import Project (`.axpp`, compatible with Visual Studio). In the project view, **Exclude from Project** keeps an element in the model; **Delete** removes it from the model (your own models only).
- **Search:** Find Labels, Find Tag References, Metadata Search, Find References.
- **Environment:** Connect to Environment (downloads metadata, restores the cross-reference database, configures the machine), Check Connection, Sign in.

### AI agent tools (MCP)
Copilot can:
- read, create, update and validate metadata (every create is all-or-nothing and reports its validation result);
- find elements and their references;
- create label files and labels with translations, and add elements to the project;
- create menu extensions, and work with security privileges, duties and roles;
- create data entity extensions, including their staging tables;
- delete elements of your own models (refused while other elements reference them) or exclude them from the project;
- select the active project, build the model and read every compiler error;
- run X++ unit tests on the environment (`xpp_run_tests`);
- search the Microsoft Learn documentation.

The tools are registered automatically. `node scripts/tool-regression.js --project <your .rnrproj>` checks them against the active environment.

## Requirements

- Windows.
- A Dynamics 365 Finance & Operations unified developer environment. **X++: Connect to Environment** installs the metadata, compiler and FinOps tools.
- SQL Server LocalDB, for cross references.
- [Power Platform CLI](https://learn.microsoft.com/power-platform/developer/cli/introduction) (`pac`), for deploy and database sync.

## Getting started

1. Open the **X++** view in the activity bar.
2. If the machine isn't set up yet, run **Connect to Environment** from the Dynamics 365 view.
3. Browse **Application Explorer**, or pick a project with **Select D365FO Project**; a project is optional.
4. Open elements, build, debug, or ask Copilot in agent mode to work on your model.

## Settings

| Setting | Description |
| --- | --- |
| `xpp.project` | Project (`.rnrproj`) that new elements are added to |
| `xpp.environmentUrl` | Dataverse URL of your unified developer environment |
| `xpp.financeAndOperationsUrl` | Web client URL, for Open in Browser and Open Table Browser |
| `xpp.auth.clientId` | Optional Entra app ID for sign-in |
| `xpp.build.crossReference` | Update cross references while building |
| `xpp.build.runAppCheckerRules` | Run AppChecker rules during the build |
| `xpp.pacPath` | Path to `pac` |
| `xpp.debuggerProxyPath` | Path to `Microsoft.Dynamics.AX.DebuggerProxy.exe`, if not in the default location |

## Status

Preview. The debugger has not yet been verified end to end against a live environment.

*F&O Studio is an independent project and is not affiliated with or endorsed by Microsoft. Dynamics 365 is a trademark of Microsoft Corporation.*
