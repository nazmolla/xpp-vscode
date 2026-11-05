import { promises as fs, unwatchFile, watchFile } from 'fs';
import * as os from 'os';
import * as path from 'path';
import * as vscode from 'vscode';

/** A tool call of Copilot's MCP server, as the server writes it to activity.jsonl (server/Host/ActivityLog.cs). */
interface Activity {
    time: string;
    tool: string;
    ms: number;
    ok: boolean;
    arguments?: string;
    objectType?: string;
    objectName?: string;
    write: boolean;
    /** Copy of the element file before the change (absent for creates). */
    before?: string;
    after?: string;
}

const MaxShown = 200;
const ActivityFile = path.join(process.env.LOCALAPPDATA ?? os.homedir(), 'XppVsCode', 'activity.jsonl');

/**
 * Copilot Activity: what the agent asked F&O Studio's tools, newest first; after the agent changes one of your elements,
 * a notification offers the change as a diff.
 */
export class ActivityView implements vscode.TreeDataProvider<Activity>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private entries: Activity[] = [];
    private seen = 0;

    constructor() {
        void this.load(true);
        watchFile(ActivityFile, { interval: 2000 }, () => void this.load(false));
    }

    private async load(initial: boolean): Promise<void> {
        const text = await fs.readFile(ActivityFile, 'utf8').catch(() => '');
        const all = text.split('\n').filter(l => l.trim()).flatMap(l => {
            try {
                return [JSON.parse(l) as Activity];
            } catch {
                return [];
            }
        });
        const fresh = initial || all.length < this.seen ? [] : all.slice(this.seen);
        this.seen = all.length;
        this.entries = all.slice(-MaxShown).reverse();
        this.changed.fire();
        for (const entry of fresh.filter(e => e.write && e.ok)) {
            void this.announce(entry);
        }
    }

    private async announce(entry: Activity): Promise<void> {
        const verb = entry.tool === 'xpp_delete_object' ? 'deleted' : entry.before ? 'changed' : 'created';
        const actions = [...(entry.before || entry.after ? ['View Changes'] : []), ...(entry.after ? ['Open'] : [])];
        const choice = await vscode.window.showInformationMessage(`Copilot ${verb} ${entry.objectName} (${(entry.objectType ?? '').replace(/^Ax/, '')}).`, ...actions);
        if (choice === 'View Changes') {
            await showChanges(entry);
        } else if (choice === 'Open') {
            await vscode.commands.executeCommand('xpp.openDesigner', entry.objectType, entry.objectName);
        }
    }

    getTreeItem(entry: Activity): vscode.TreeItem {
        const item = new vscode.TreeItem(entry.objectName ? `${entry.tool.replace(/^xpp_/, '')} ${entry.objectName}` : entry.tool.replace(/^xpp_/, ''));
        item.description = `${new Date(entry.time).toLocaleTimeString()} · ${entry.ms} ms`;
        item.iconPath = new vscode.ThemeIcon(!entry.ok ? 'error' : entry.write ? 'edit' : 'search');
        item.tooltip = new vscode.MarkdownString(`**${entry.tool}** ${entry.ok ? 'succeeded' : 'failed'} in ${entry.ms} ms\n\n\`\`\`json\n${entry.arguments ?? ''}\n\`\`\``);
        item.contextValue = entry.write && (entry.before || entry.after) ? 'xppActivityWrite' : 'xppActivity';
        if (item.contextValue === 'xppActivityWrite') {
            item.command = { command: 'xpp.activity.showChanges', title: 'View Changes', arguments: [entry] };
        }
        return item;
    }

    getChildren(entry?: Activity): Activity[] {
        return entry ? [] : this.entries;
    }

    async clear(): Promise<void> {
        await fs.writeFile(ActivityFile, '', 'utf8').catch(() => undefined);
        await this.load(true);
    }

    dispose(): void {
        unwatchFile(ActivityFile);
        this.changed.dispose();
    }
}

/** The element file before the agent's change against its current file (a create shows the new file). */
export async function showChanges(entry: Activity): Promise<void> {
    // The server writes element files and their snapshots; anything else in the log is not opened.
    const isXml = (file?: string) => !!file && path.isAbsolute(file) && file.toLowerCase().endsWith('.xml');
    const after = isXml(entry.after) ? vscode.Uri.file(entry.after!) : undefined;
    const before = isXml(entry.before) ? vscode.Uri.file(entry.before!) : undefined;
    if (before && after) {
        await vscode.commands.executeCommand('vscode.diff', before, after, `${entry.objectName} (before Copilot ↔ now)`);
    } else if (after) {
        await vscode.window.showTextDocument(after, { preview: true });
    } else if (before) {
        await vscode.window.showTextDocument(before, { preview: true });
    }
}
