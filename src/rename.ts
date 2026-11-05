import * as vscode from 'vscode';
import { XppScheme } from './codeView';
import { XppNavigation } from './navigation';
import { XppServerClient } from './serverClient';

/**
 * Rename (F2) of methods of your own classes, tables and forms: the declaration and every call the cross-reference
 * database knows (as of the last build with cross references), shown in the refactor preview before anything changes.
 * Saving the changed code views writes them through the metadata API.
 */
export class XppRename implements vscode.RenameProvider {
    constructor(private readonly navigation: XppNavigation, private readonly server: XppServerClient) {}

    async prepareRename(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Range | { range: vscode.Range; placeholder: string }> {
        const target = await this.target(document, position);
        return { range: target.range, placeholder: target.member };
    }

    async provideRenameEdits(document: vscode.TextDocument, position: vscode.Position, newName: string): Promise<vscode.WorkspaceEdit> {
        if (!/^[A-Za-z_]\w*$/.test(newName)) {
            throw new Error('A method name is letters, digits and _, not starting with a digit.');
        }
        const target = await this.target(document, position);
        const locations = await this.navigation.provideReferences(document, position, { includeDeclaration: true });
        const edit = new vscode.WorkspaceEdit();
        const metadata = { needsConfirmation: true, label: `Rename ${target.owner}.${target.member} to ${newName}` };
        const call = new RegExp(`\\b${target.member}\\b(?=\\s*\\()`, 'gi');
        const named = new RegExp(`(\\w*MethodStr\\s*\\(\\s*\\w+\\s*,\\s*)${target.member}\\b`, 'gi');
        const seen = new Set<string>();
        for (const location of locations) {
            const key = `${location.uri.toString()}#${location.range.start.line}`;
            if (seen.has(key) || location.uri.scheme !== XppScheme) {
                continue;
            }
            seen.add(key);
            const doc = await vscode.workspace.openTextDocument(location.uri);
            const line = location.range.start.line;
            // A reference can point a line or two before the call (attributes, wrapped statements).
            for (let l = line; l < Math.min(doc.lineCount, line + 3); l++) {
                const text = doc.lineAt(l).text;
                let found = false;
                for (const pattern of [call, named]) {
                    for (const m of text.matchAll(pattern)) {
                        const start = m.index! + (pattern === named ? m[1].length : 0);
                        edit.replace(location.uri, new vscode.Range(l, start, l, start + target.member.length), newName, metadata);
                        found = true;
                    }
                }
                if (found) {
                    break;
                }
            }
        }
        return edit;
    }

    private async target(document: vscode.TextDocument, position: vscode.Position): Promise<{ member: string; owner: string; range: vscode.Range }> {
        const range = document.getWordRangeAtPosition(position, /[A-Za-z_]\w*/);
        const symbol = await this.navigation.resolve(document, position);
        if (!range || !symbol?.member) {
            throw new Error('Rename works on method names.');
        }
        const found = await this.navigation.definition(symbol);
        if (!found.signature) {
            throw new Error(`${symbol.member} is not a method in code.`);
        }
        const owner = found.owner;
        const result = await this.server.bridge('find_object', { objectName: owner.objectName, exactMatch: true, maxResults: 10 });
        const match = (result.matches ?? []).find((m: { objectType: string }) => m.objectType === owner.objectType);
        if (!match?.isCustom) {
            throw new Error(`${owner.objectName}.${symbol.member} belongs to a standard model; only methods of your models can be renamed.`);
        }
        return { member: symbol.member, owner: owner.objectName, range };
    }
}
