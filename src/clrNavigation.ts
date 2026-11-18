import * as vscode from 'vscode';
import { ClrIndex, ClrMember, ClrType, clrDeclaredType, memberOf, xppUsings } from './clrSymbols';
import { declaredType, XppNavigation } from './navigation';
import { readProject, solutionCsharpProjects } from './project';

/**
 * X++ and C# side by side: Go to Definition, completion and hover in X++ code on the .NET types of the solution's C#
 * projects (they open the C# source), and Go to Definition in C# code on X++ types (Dynamics.AX.Application), which
 * opens the X++ code view.
 */

/** The C# projects of the open X++ project's solution and references. */
export const clrIndex = new ClrIndex(async () => {
    const project = await readProject().catch(() => undefined);
    return project ? (await solutionCsharpProjects(project.file)).projects.map(p => p.file) : [];
});

function location(target: { file: string; line: number; column: number }): vscode.Location {
    return new vscode.Location(vscode.Uri.file(target.file), new vscode.Position(target.line, target.column));
}

/** The .NET type or member at a position of X++ code. */
export async function clrSymbolAt(index: ClrIndex, document: vscode.TextDocument, position: vscode.Position): Promise<{ type: ClrType; member?: ClrMember } | undefined> {
    const range = document.getWordRangeAtPosition(position, /[A-Za-z_]\w*/);
    if (!range) {
        return undefined;
    }
    const word = document.getText(range);
    const before = document.lineAt(position.line).text.slice(0, range.start.character);
    const text = document.getText();
    const usings = xppUsings(text);
    const withMember = (type: ClrType | undefined) => type ? { type, member: memberOf(type, word) } : undefined;
    let m: RegExpExecArray | null;
    if ((m = /([A-Za-z_][\w.]*)\s*::\s*$/.exec(before))) {
        return withMember(await index.resolve(m[1], usings));
    }
    if ((m = /([A-Za-z_][\w.]*)\s*(\(\s*\))?\s*\.\s*$/.exec(before))) {
        const qualifier = m[1];
        if (!m[2]) {
            const nested = await index.resolve(`${qualifier}.${word}`, usings);
            if (nested) {
                return { type: nested };
            }
            const owner = await index.resolve(qualifier, usings);
            if (owner) {
                return withMember(owner);
            }
        }
        const variable = qualifier.split('.').pop()!;
        const declared = clrDeclaredType(text, variable, document.offsetAt(position));
        return declared ? withMember(await index.resolve(declared, usings)) : undefined;
    }
    const type = await index.resolve(word, usings);
    if (type) {
        return { type, member: /^\s*\(/.test(document.lineAt(position.line).text.slice(range.end.character)) && /\bnew\s+$/.test(before) ? memberOf(type, word) : undefined };
    }
    return undefined;
}

export async function clrDefinition(index: ClrIndex, document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Location | undefined> {
    const symbol = await clrSymbolAt(index, document, position);
    return symbol ? location(symbol.member ?? symbol.type) : undefined;
}

export async function clrHover(index: ClrIndex, document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Hover | undefined> {
    const symbol = await clrSymbolAt(index, document, position);
    if (!symbol) {
        return undefined;
    }
    const md = new vscode.MarkdownString(symbol.member
        ? `**${symbol.type.name}.${symbol.member.name}** · ${symbol.member.kind} of ${symbol.type.kind} \`${symbol.type.fullName}\` (C#)`
        : `**${symbol.type.name}** · ${symbol.type.kind} \`${symbol.type.fullName}\` (C#)`);
    if (symbol.member) {
        md.appendCodeblock(symbol.member.signature, 'csharp');
    }
    return new vscode.Hover(md, document.getWordRangeAtPosition(position, /[A-Za-z_]\w*/));
}

const MemberKinds: Record<ClrMember['kind'], vscode.CompletionItemKind> = {
    method: vscode.CompletionItemKind.Method,
    constructor: vscode.CompletionItemKind.Constructor,
    property: vscode.CompletionItemKind.Property,
    field: vscode.CompletionItemKind.Field,
    enumValue: vscode.CompletionItemKind.EnumMember,
};

/**
 * Completion after Type:: (static members, enum values), namespace. (types and namespaces under it) and variable.
 * (instance members of its .NET type); undefined when the qualifier is not .NET.
 */
export async function clrCompletions(index: ClrIndex, document: vscode.TextDocument, position: vscode.Position): Promise<vscode.CompletionItem[] | undefined> {
    const before = document.lineAt(position.line).text.slice(0, position.character);
    const m = /([A-Za-z_][\w.]*)\s*(\.|::)\s*(\w*)$/.exec(before);
    if (!m) {
        return undefined;
    }
    const [, qualifier, separator] = m;
    const text = document.getText();
    const usings = xppUsings(text);
    const memberItems = (type: ClrType, statics: boolean) => type.members
        .filter(x => x.kind !== 'constructor' && x.isStatic === statics)
        .map(x => {
            const item = new vscode.CompletionItem({ label: x.name, detail: x.kind === 'method' ? '()' : undefined, description: `${type.name} (C#)` }, MemberKinds[x.kind]);
            item.detail = x.signature;
            if (x.kind === 'method') {
                item.insertText = new vscode.SnippetString(`${x.name}($0)`);
                item.command = { command: 'editor.action.triggerParameterHints', title: 'Parameter hints' };
            }
            return item;
        });
    if (separator === '::') {
        const type = await index.resolve(qualifier, usings);
        return type ? memberItems(type, true) : undefined;
    }
    const prefix = qualifier.toLowerCase();
    const types = await index.types();
    const namespaces = await index.namespaces();
    // Types directly under a namespace, or nested in a type.
    const inNamespace = types.filter(t => t.fullName.toLowerCase() === `${prefix}.${t.name.toLowerCase()}`);
    const childNamespaces = [...namespaces].filter(n => n.toLowerCase().startsWith(prefix + '.') && !n.slice(prefix.length + 1).includes('.'));
    if (inNamespace.length || childNamespaces.length) {
        return [
            ...childNamespaces.map(n => new vscode.CompletionItem({ label: n.slice(prefix.length + 1), description: 'namespace' }, vscode.CompletionItemKind.Module)),
            ...inNamespace.map(t => {
                const item = new vscode.CompletionItem({ label: t.name, description: `${t.kind} (C#)` }, t.kind === 'enum' ? vscode.CompletionItemKind.Enum
                    : t.kind === 'interface' ? vscode.CompletionItemKind.Interface : t.kind === 'struct' ? vscode.CompletionItemKind.Struct : vscode.CompletionItemKind.Class);
                item.detail = t.fullName;
                return item;
            }),
        ];
    }
    const declared = clrDeclaredType(text, qualifier.split('.').pop()!, document.offsetAt(position));
    const type = declared ? await index.resolve(declared, usings) : undefined;
    return type ? memberItems(type, false) : undefined;
}

/**
 * Go to Definition in C# code on X++ types and their members (Dynamics.AX.Application.CustTable, CustTable.find(...),
 * cust.name()): the X++ code view. Only in files that use the X++ namespace.
 */
export class CSharpToXppDefinitions implements vscode.DefinitionProvider {
    constructor(private readonly navigation: XppNavigation) { }

    async provideDefinition(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.Location | undefined> {
        const text = document.getText();
        if (!/\bDynamics\.AX\.Application\b/.test(text)) {
            return undefined;
        }
        const range = document.getWordRangeAtPosition(position, /[A-Za-z_]\w*/);
        if (!range) {
            return undefined;
        }
        const word = document.getText(range);
        const before = document.lineAt(position.line).text.slice(0, range.start.character);
        const imported = /^\s*using\s+(global::)?Dynamics\.AX\.Application\s*;/m.test(text);
        const qualified = /(?:global::)?([A-Za-z_][\w.]*)\s*\.\s*$/.exec(before);
        if (!qualified) {
            if (!imported) {
                return undefined;
            }
            const type = await this.navigation.findType(word);
            return type ? (await this.navigation.definition(type)).location : undefined;
        }
        const qualifier = qualified[1];
        if (/^Dynamics\.AX\.Application$/i.test(qualifier)) {
            const type = await this.navigation.findType(word);
            return type ? (await this.navigation.definition(type)).location : undefined;
        }
        // Type.member (static), or variable.member with the variable's declared X++ type.
        const typeName = /^Dynamics\.AX\.Application\.(\w+)$/i.exec(qualifier)?.[1] ?? (imported && /^[A-Z]/.test(qualifier) ? qualifier : undefined);
        let owner = typeName ? await this.navigation.findType(typeName) : undefined;
        if (!owner && /^\w+$/.test(qualifier)) {
            const declared = declaredType(document, qualifier, position);
            owner = declared ? await this.navigation.findType(declared) : undefined;
        }
        return owner ? (await this.navigation.definition({ ...owner, member: word })).location : undefined;
    }
}
