import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

const Template = `# Team rules for Copilot (F&O Studio)

Copilot reads this file with F&O Studio's own instructions (they are restarted when it changes). Write the rules your
team expects in every change; be specific. Delete the examples that don't apply.

## Naming
- Prefix every new element with \`ABC\` (tables, classes, EDTs, enums, menu items, security objects).
- Chain of Command classes: \`<BaseObject>_ABC_Extension\`. Metadata extensions: \`<BaseObject>.ABCExtension\`.

## Models
- New elements go to the model of the open project; never create elements in another model.

## Code
- Every user-visible text is a label in the label file \`ABCLabels\`.
- Batch work uses SysOperation (contract, service, controller); no RunBaseBatch.
- No direct SQL; no \`doInsert\`/\`doUpdate\` unless the reason is commented.

## Security
- Every new menu item gets View and Maintain privileges, added to the duty \`ABC...\`.

## Reviews
- Before finishing, run \`xpp_analyze_performance\` on changed classes and fix every Warning.
`;

/** Creates .xpp/agent-rules.md in the workspace (the team's rules the MCP server adds to Copilot's instructions). */
export async function createTeamRules(): Promise<void> {
    const folder = vscode.workspace.workspaceFolders?.[0];
    if (!folder) {
        throw new Error('Open a folder (your repository) first: the rules live in it, so the whole team shares them.');
    }
    const file = path.join(folder.uri.fsPath, '.xpp', 'agent-rules.md');
    const exists = await fs.stat(file).then(() => true, () => false);
    if (!exists) {
        await fs.mkdir(path.dirname(file), { recursive: true });
        await fs.writeFile(file, Template, 'utf8');
    }
    await vscode.window.showTextDocument(vscode.Uri.file(file));
}
