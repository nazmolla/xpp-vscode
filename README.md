# X++ AI Companion for D365FO

Dynamics 365 Finance & Operations X++ development in VS Code, built for AI agents.

## Features

- **AI agent tools (MCP):** read, create, update and validate D365FO metadata. Also find objects, find references, manage labels, and add objects to the project. Covers security privileges/duties/roles and data entity extensions, including their staging tables. Copilot picks the tools up automatically.
- **Code view:** open any object (`Ctrl+Alt+O`) as an `.xpp` document. You can edit objects in your own model; all other objects open read-only.
- **Navigation:** go to definition and hover for object names.
- **Form designer:** browse a form's merged control tree. Add fields and groups, hide controls and set labels; changes are saved to a form extension in your model.
- **Build:** compile the model with the X++ compiler (`Ctrl+Shift+B`). Errors appear in the Problems panel at the right line.
- **Deploy:** deploy models and sync the database with the Power Platform CLI (`pac`).
- **Environment setup:** connect to a unified developer environment. This downloads the metadata, restores the cross-reference database and configures the machine.

## Requirements

- Windows, with the Dynamics 365 FinOps development tools (metadata and compiler) installed. Use **X++: Connect to Environment** to install them.
- SQL Server LocalDB, for cross references.
- [Power Platform CLI](https://learn.microsoft.com/power-platform/developer/cli/introduction), for deploy and database sync.

## Getting started

1. Open a folder that contains your `.rnrproj` project.
2. Open the **X++** view in the activity bar.
3. Use **Open Object**, **Build Model**, or ask Copilot in agent mode to work on your model.

## Settings

| Setting | Description |
| --- | --- |
| `xpp.project` | Project (.rnrproj) that new objects are added to |
| `xpp.environmentUrl` | Dataverse URL of your unified developer environment |
| `xpp.auth.clientId` | Optional Entra app ID for sign-in |
| `xpp.build.crossReference` | Update cross references while building |
| `xpp.build.runAppCheckerRules` | Run AppChecker rules during the build |
| `xpp.pacPath` | Path to `pac` |
