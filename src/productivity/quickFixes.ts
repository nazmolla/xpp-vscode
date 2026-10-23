import * as vscode from 'vscode';
import { CodeExtensionFunctions } from '../aot';
import { objectUri, parseObjectUri, XppScheme } from '../codeView';
import { addCreatedToProject, listLabelFiles, targetModel } from '../d365Tools';
import { XppDocumentSelector } from '../navigation';
import { methodSpans } from '../xppSource';
import {
    call, CommandRegistrar, findExact, headerStart, IdentifierPattern, indent, insertMethodsEdit, isWritable, lowerFirst, methodHeader,
    parseSignature, pascalCase, ProductivityContext,
} from './common';

const LabelFileKey = 'xpp.extractLabel.labelFile';

/** A string literal on a line: [start, end) including the quotes (and a leading @ of a verbatim string). */
interface Literal {
    start: number;
    end: number;
    quote: string;
    verbatim: boolean;
    value: string;
}

/**
 * Quick fixes for X++ code: extract a string to a label, generate a table's find() and exist(), implement missing
 * interface methods, and create a Chain of Command wrapper for a method of a standard object.
 */
export class XppQuickFixes implements vscode.CodeActionProvider {
    static readonly kinds = [vscode.CodeActionKind.QuickFix, vscode.CodeActionKind.RefactorExtract, vscode.CodeActionKind.Refactor];

    constructor(private readonly ctx: ProductivityContext, private readonly state: vscode.Memento) { }

    register(context: vscode.ExtensionContext, command: CommandRegistrar): void {
        context.subscriptions.push(vscode.languages.registerCodeActionsProvider(XppDocumentSelector, this, { providedCodeActionKinds: XppQuickFixes.kinds }));
        command('xpp.quickFix.extractLabel', (uri: vscode.Uri, line: number, literal: Literal) => this.extractLabel(uri, line, literal));
        command('xpp.quickFix.findExist', (uri: vscode.Uri) => this.generateFindExist(uri));
        command('xpp.quickFix.implementInterfaces', (uri: vscode.Uri) => this.implementInterfaces(uri));
        command('xpp.quickFix.cocWrapper', (uri: vscode.Uri, method: string) => this.createWrapper(uri, method));
    }

    async provideCodeActions(document: vscode.TextDocument, range: vscode.Range | vscode.Selection): Promise<vscode.CodeAction[]> {
        const actions: vscode.CodeAction[] = [];
        const line = range.start.line;
        const writable = document.uri.scheme !== XppScheme || await isWritable(document.uri);
        const element = this.element(document);

        const literal = literalAt(document.lineAt(line).text, range.start.character);
        if (literal && writable && literal.value.trim() && !literal.value.startsWith('@')) {
            const action = new vscode.CodeAction('Extract to label', vscode.CodeActionKind.RefactorExtract);
            action.command = { command: 'xpp.quickFix.extractLabel', title: 'Extract to label', arguments: [document.uri, line, literal] };
            actions.push(action);
        }

        const text = document.getText();
        const lines = text.split(/\r?\n/);
        const spans = methodSpans(text);
        const names = new Set(spans.map(s => s.name.toLowerCase()));

        if (element && !element.methodPath && element.objectType === 'AxTable' && writable && (!names.has('find') || !names.has('exist'))) {
            const missing = ['find', 'exist'].filter(n => !names.has(n)).map(n => n + '()').join(' and ');
            const action = new vscode.CodeAction(`Generate ${missing}`, vscode.CodeActionKind.Refactor);
            action.command = { command: 'xpp.quickFix.findExist', title: action.title, arguments: [document.uri] };
            actions.push(action);
        }

        const declarationEnd = lines.findIndex(l => l.includes('{'));
        if (writable && declarationEnd >= 0 && line <= declarationEnd
            && /\bclass\s+\w+[^{]*\bimplements\b/.test(lines.slice(0, declarationEnd + 1).join('\n'))) {
            const action = new vscode.CodeAction('Implement missing interface methods', vscode.CodeActionKind.QuickFix);
            action.command = { command: 'xpp.quickFix.implementInterfaces', title: action.title, arguments: [document.uri] };
            actions.push(action);
        }

        if (element && !element.methodPath && !writable && CodeExtensionFunctions[element.objectType]) {
            const span = spans.find((s, i) => {
                const start = headerStart(lines, s.startLine);
                const header = methodHeader(lines, start);
                return s.name !== 'classDeclaration' && header && line >= s.startLine && line <= header.endLine
                    && (i + 1 >= spans.length || spans[i + 1].startLine > line);
            });
            const header = span ? methodHeader(lines, headerStart(lines, span.startLine)) : undefined;
            const signature = header ? parseSignature(header.text) : undefined;
            if (span && signature && !signature.modifiers.includes('private')) {
                const action = new vscode.CodeAction(`Create Chain of Command wrapper for ${span.name}`, vscode.CodeActionKind.Refactor);
                action.command = { command: 'xpp.quickFix.cocWrapper', title: action.title, arguments: [document.uri, span.name] };
                actions.push(action);
            }
        }
        return actions;
    }

    private element(document: vscode.TextDocument): { objectType: string; objectName: string; methodPath?: string[] } | undefined {
        if (document.uri.scheme !== XppScheme) {
            return undefined;
        }
        try {
            return parseObjectUri(document.uri);
        } catch {
            return undefined;
        }
    }

    // ── Extract to label ──

    private async extractLabel(uri: vscode.Uri, line: number, literal: Literal): Promise<void> {
        const document = await vscode.workspace.openTextDocument(uri);
        const files = [...(await listLabelFiles(true)).keys()].sort((a, b) => a.localeCompare(b));
        if (files.length === 0) {
            throw new Error('None of your models has a label file. Create one (Label File) in your model first.');
        }
        const last = this.state.get<string>(LabelFileKey);
        const ordered = last && files.includes(last) ? [last, ...files.filter(f => f !== last)] : files;
        const labelFile = ordered.length === 1 ? ordered[0] : await vscode.window.showQuickPick(ordered, { title: 'Extract to label: label file' });
        if (!labelFile) {
            return;
        }
        await this.state.update(LabelFileKey, labelFile);

        const info = await call<{ allLanguages: string[]; language: string; editable: boolean }>(this.ctx.server, 'bridge', 'label_file_entries', { labelFile, search: '', limit: '1' });
        if (!info.editable) {
            throw new Error(`Label file ${labelFile} can't be changed.`);
        }
        let labelId = await vscode.window.showInputBox({
            title: `Extract to label in ${labelFile}`, prompt: `Label ID for "${literal.value}"`, value: pascalCase(literal.value), ignoreFocusOut: true,
            validateInput: v => IdentifierPattern.test(v) ? undefined : 'Letters, digits or _, starting with a letter.',
        });
        if (!labelId) {
            return;
        }
        const existing = await call<{ entries: { id: string; texts: Record<string, string> }[] }>(this.ctx.server, 'bridge', 'label_file_entries', { labelFile, search: labelId, limit: '50' });
        const match = (existing.entries ?? []).find(e => e.id.toLowerCase() === labelId!.toLowerCase());
        if (match) {
            const current = match.texts[info.language] ?? Object.values(match.texts)[0] ?? '';
            if (current !== literal.value) {
                const choice = await vscode.window.showWarningMessage(`@${labelFile}:${match.id} already exists with the text "${current}".`, { modal: true }, 'Use It', 'Overwrite');
                if (!choice) {
                    return;
                }
                if (choice === 'Overwrite') {
                    await this.saveLabel(labelFile, match.id, info.allLanguages, literal.value);
                }
            }
            labelId = match.id;
        } else {
            await this.saveLabel(labelFile, labelId, info.allLanguages, literal.value);
        }

        // The line may have changed while the dialogs were open; find the literal again.
        const again = literalAt(document.lineAt(line).text, literal.start + 1);
        if (!again || again.value !== literal.value) {
            throw new Error(`The label @${labelFile}:${labelId} is ready, but the string moved; replace it by hand.`);
        }
        const edit = new vscode.WorkspaceEdit();
        // Labels are resolved in double-quoted strings only.
        edit.replace(uri, new vscode.Range(line, again.start, line, again.end), `"@${labelFile}:${labelId}"`);
        await vscode.workspace.applyEdit(edit);
    }

    private async saveLabel(labelFile: string, labelId: string, languages: string[], text: string): Promise<void> {
        const texts: Record<string, string> = {};
        for (const language of languages) {
            texts[language] = text;
        }
        const result = await call(this.ctx.server, 'bridge', 'save_label', { labelFile, labelId, texts, comment: '' });
        vscode.window.setStatusBarMessage(result.message ?? `Created @${labelFile}:${labelId}`, 5000);
    }

    // ── Table find() and exist() ──

    private async generateFindExist(uri: vscode.Uri): Promise<void> {
        const document = await vscode.workspace.openTextDocument(uri);
        const { objectName: table } = parseObjectUri(uri);
        const result = await call<{ tables: { name: string; primaryKey: string[]; fields: { name: string; type: string }[] }[] }>(
            this.ctx.server, 'bridge', 'table_erd', { tables: [table] });
        const info = result.tables?.[0];
        if (!info) {
            throw new Error(`Table ${table} not found.`);
        }
        const keyFields = info.primaryKey?.length ? info.primaryKey : ['RecId'];
        const keys = keyFields.map(field => ({
            field,
            type: info.fields.find(f => f.name.toLowerCase() === field.toLowerCase())?.type ?? (field === 'RecId' ? 'RecId' : 'str'),
            param: '_' + lowerFirst(field),
        }));
        const buffer = lowerFirst(info.name);
        const names = new Set(methodSpans(document.getText()).map(s => s.name.toLowerCase()));
        const params = keys.map(k => `${k.type} ${k.param}`).join(', ');
        const guard = keys.map(k => k.param).join(' && ');
        const where = (alias: string) => keys.map((k, i) => `${i === 0 ? 'where' : '   &&'} ${alias}.${k.field} == ${k.param}`);
        const methods: string[] = [];
        if (!names.has('find')) {
            methods.push([
                '/// <summary>',
                `/// Finds the ${info.name} record by its primary key.`,
                '/// </summary>',
                ...keys.map(k => `/// <param name = "${k.param}">The ${k.field} value.</param>`),
                '/// <param name = "_forUpdate">true to select the record for update; optional.</param>',
                `/// <returns>The ${info.name} record, or an empty buffer when none is found.</returns>`,
                `public static ${info.name} find(${params}, boolean _forUpdate = false)`,
                '{',
                `    ${info.name} ${buffer};`,
                '',
                `    if (${guard})`,
                '    {',
                `        ${buffer}.selectForUpdate(_forUpdate);`,
                '',
                `        select firstonly ${buffer}`,
                ...where(buffer).map(w => `            ${w}`).map((w, i, all) => i === all.length - 1 ? w + ';' : w),
                '    }',
                '',
                `    return ${buffer};`,
                '}',
            ].join('\n'));
        }
        if (!names.has('exist')) {
            const conditions = where(info.name);
            methods.push([
                '/// <summary>',
                `/// Checks whether a ${info.name} record exists.`,
                '/// </summary>',
                ...keys.map(k => `/// <param name = "${k.param}">The ${k.field} value.</param>`),
                '/// <returns>true if the record exists; otherwise, false.</returns>',
                `public static boolean exist(${params})`,
                '{',
                `    return ${guard}`,
                `        && (select firstonly RecId from ${info.name}`,
                ...conditions.map(w => `            ${w}`).map((w, i, all) => i === all.length - 1 ? w + ').RecId != 0;' : w),
                '}',
            ].join('\n'));
        }
        if (methods.length === 0) {
            vscode.window.showInformationMessage(`${info.name} already has find() and exist().`);
            return;
        }
        await this.insertMethods(document, methods);
    }

    private async insertMethods(document: vscode.TextDocument, methods: string[]): Promise<void> {
        const edit = insertMethodsEdit(document, methods);
        if (!edit) {
            throw new Error('The class\'s closing brace was not found at the end of the document.');
        }
        await vscode.workspace.applyEdit(edit);
    }

    // ── Implement interface methods ──

    private async implementInterfaces(uri: vscode.Uri): Promise<void> {
        const document = await vscode.workspace.openTextDocument(uri);
        const text = document.getText();
        const head = text.slice(0, text.indexOf('{'));
        const declaration = /\bclass\s+(\w+)[^{]*?\bimplements\s+([\w\s,]+)/.exec(head.replace(/\/\/[^\n]*/g, ''));
        if (!declaration) {
            return;
        }
        const className = declaration[1];
        const interfaces = declaration[2].split(',').map(s => s.trim()).filter(s => IdentifierPattern.test(s));
        const own = new Set(methodSpans(text).map(s => s.name.toLowerCase()));
        // Methods inherited from base classes implement interface methods too.
        const classMembers = await this.ctx.server.bridge('type_members', { objectType: 'AxClass', objectName: className }).catch(() => undefined);
        for (const m of (classMembers?.success ? classMembers.members : []) as { kind: string; name: string }[]) {
            if (m.kind === 'method') {
                own.add(m.name.toLowerCase());
            }
        }
        const stubs: string[] = [];
        const problems: string[] = [];
        for (const name of interfaces) {
            const result = await this.ctx.server.bridge('type_members', { objectType: 'AxClass', objectName: name }).catch(err => ({ success: false, message: (err as Error).message }));
            if (!result?.success) {
                problems.push(`${name}: ${result?.message ?? 'not found'}`);
                continue;
            }
            for (const m of result.members as { kind: string; name: string; signature?: string }[]) {
                if (m.kind !== 'method' || own.has(m.name.toLowerCase())) {
                    continue;
                }
                own.add(m.name.toLowerCase());
                const signature = parseSignature(m.signature ?? `public void ${m.name}()`);
                const modifiers = (signature?.modifiers ?? []).filter(x => x !== 'abstract');
                if (!modifiers.some(x => ['public', 'protected', 'private', 'internal'].includes(x))) {
                    modifiers.unshift('public');
                }
                const params = (signature?.parameters ?? []).map(p => `${p.type} ${p.name}${p.defaultValue !== undefined ? ' = ' + p.defaultValue : ''}`).join(', ');
                stubs.push([
                    `${modifiers.join(' ')} ${signature?.returnType ?? 'void'} ${m.name}(${params})`,
                    '{',
                    '    throw error(Error::wrongUseOfFunction(funcName()));',
                    '}',
                ].join('\n'));
            }
        }
        if (stubs.length > 0) {
            await this.insertMethods(document, stubs);
        }
        const message = stubs.length ? `Added ${stubs.length} method(s).` : 'All interface methods are implemented.';
        if (problems.length) {
            vscode.window.showWarningMessage(`${message} Not checked: ${problems.join('; ')}`);
        } else {
            vscode.window.setStatusBarMessage(message, 5000);
        }
    }

    // ── Chain of Command wrapper ──

    private async createWrapper(uri: vscode.Uri, method: string): Promise<void> {
        const document = await vscode.workspace.openTextDocument(uri);
        const { objectType, objectName: base } = parseObjectUri(uri);
        const intrinsic = CodeExtensionFunctions[objectType];
        const lines = document.getText().split(/\r?\n/);
        const span = methodSpans(document.getText()).find(s => s.name.toLowerCase() === method.toLowerCase());
        const header = span ? methodHeader(lines, headerStart(lines, span.startLine)) : undefined;
        const signature = header ? parseSignature(header.text) : undefined;
        if (!intrinsic || !signature) {
            throw new Error(`Could not read the header of ${method}.`);
        }
        const model = await targetModel('Create Chain of Command wrapper');
        if (!model) {
            return;
        }
        const className = await vscode.window.showInputBox({
            title: `Chain of Command wrapper for ${base}.${signature.name}`, prompt: 'Extension class (created in your model if it does not exist)',
            value: `${base}_${model}_Extension`, ignoreFocusOut: true,
            validateInput: v => /^[A-Za-z_]\w*_Extension$/.test(v) ? undefined : 'Use a valid class name ending with _Extension.',
        });
        if (!className) {
            return;
        }
        const modifiers = signature.modifiers.filter(m => !['abstract', 'final', 'server', 'client'].includes(m));
        const params = signature.parameters.map(p => `${p.type} ${p.name}`).join(', ');
        const args = signature.parameters.map(p => p.name).join(', ');
        const isVoid = signature.returnType.toLowerCase() === 'void';
        const source = indent([
            `${[...modifiers, signature.returnType].join(' ')} ${signature.name}(${params})`,
            '{',
            ...(isVoid
                ? [`    next ${signature.name}(${args});`]
                : [`    ${signature.returnType} result = next ${signature.name}(${args});`, '', '    return result;']),
            '}',
        ].join('\n'), '    ');

        const existing = await findExact(this.ctx.server, 'AxClass', className);
        if (existing && !existing.isCustom) {
            throw new Error(`${className} exists in ${existing.modelName}, which is not one of your models.`);
        }
        if (existing) {
            const read = await call<{ declaration?: string; methods?: { name: string }[] }>(this.ctx.server, 'bridge', 'read_object', { objectType: 'AxClass', objectName: className });
            if (!new RegExp(`ExtensionOf\\s*\\(\\s*\\w+\\s*\\(\\s*${base}\\s*\\)`, 'i').test(read.declaration ?? '')) {
                throw new Error(`${className} exists but is not an extension of ${base}.`);
            }
            if ((read.methods ?? []).some(m => m.name.toLowerCase() === signature.name.toLowerCase())) {
                vscode.window.showInformationMessage(`${className} already wraps ${signature.name}.`);
            } else {
                await call(this.ctx.server, 'bridge', 'update_object', { objectType: 'AxClass', objectName: className, methods: [source], formatCode: false });
            }
        } else {
            const declaration = `[ExtensionOf(${intrinsic}(${base}))]\nfinal class ${className}\n{\n}`;
            await call(this.ctx.server, 'bridge', 'create_object', {
                objectType: 'AxClass', objectName: className, modelName: model, declaration, methods: [source], formatCode: false,
            });
        }
        await addCreatedToProject(this.ctx.server, 'AxClass', className);
        this.ctx.refreshTrees();
        const target = objectUri('AxClass', className);
        this.ctx.refreshCode(target);
        const opened = await vscode.workspace.openTextDocument(target);
        const at = methodSpans(opened.getText()).find(s => s.name.toLowerCase() === signature.name.toLowerCase())?.startLine ?? 0;
        const position = new vscode.Position(at, 0);
        await vscode.window.showTextDocument(opened, { selection: new vscode.Range(position, position) });
    }
}

/** The string literal at a character position of a line, outside comments. */
export function literalAt(line: string, character: number): Literal | undefined {
    for (let i = 0; i < line.length; i++) {
        const c = line[i];
        if (c === '/' && line[i + 1] === '/') {
            return undefined;
        }
        if (c !== '"' && c !== '\'') {
            continue;
        }
        const verbatim = i > 0 && line[i - 1] === '@';
        const start = verbatim ? i - 1 : i;
        let value = '';
        let j = i + 1;
        for (; j < line.length; j++) {
            if (!verbatim && line[j] === '\\' && j + 1 < line.length) {
                value += line[j + 1];
                j++;
                continue;
            }
            if (line[j] === c) {
                if (verbatim && line[j + 1] === c) {
                    value += c;
                    j++;
                    continue;
                }
                break;
            }
            value += line[j];
        }
        if (j >= line.length) {
            return undefined;
        }
        const end = j + 1;
        if (character >= start && character <= end) {
            return { start, end, quote: c, verbatim, value };
        }
        i = j;
    }
    return undefined;
}
