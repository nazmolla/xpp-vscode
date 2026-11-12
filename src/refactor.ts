import * as vscode from 'vscode';
import { CodeTypes, typeName } from './aot';
import { methodUri, objectUri } from './codeView';
import { XppServerClient } from './serverClient';
import { compilerLineToDocumentLine } from './xppSource';

interface Reference { objectType?: string; objectName: string; memberPath: string[]; line: number; kind: string }

const NamePattern = /^[A-Za-z_]\w*$/;
const escape = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');

/** Elements that use the target, split into code (changed in the code views) and metadata (changed through replace_references). */
async function references(server: XppServerClient, objectType: string, objectName: string, memberName?: string): Promise<Reference[]> {
    const result = await server.bridge('where_used', { objectType, objectName, ...memberName ? { memberName } : {}, maxResults: '1000' });
    if (!result.success) {
        throw new Error(`${result.message} Rename needs the cross-reference database: build the model with cross references first.`);
    }
    if (result.more) {
        throw new Error(`${memberName ?? objectName} has more than 1000 references; rename it in smaller steps.`);
    }
    return result.references as Reference[];
}

async function isCustom(server: XppServerClient, objectType: string, objectName: string): Promise<{ custom: boolean; model?: string }> {
    const result = await server.bridge('find_object', { objectName, exactMatch: true, maxResults: 20 });
    const match = (result.matches ?? []).find((m: { objectType: string }) => m.objectType === objectType);
    return { custom: !!match?.isCustom, model: match?.modelName };
}

/** Adds an edit for every match of the pattern near each code reference (the reference's line and the two after it). */
async function codeEdits(refs: Reference[], pattern: RegExp, replacement: string, edit: vscode.WorkspaceEdit, label: string): Promise<number> {
    let count = 0;
    const seen = new Set<string>();
    const metadata = { needsConfirmation: true, label };
    for (const ref of refs) {
        if (!ref.objectType || !CodeTypes.has(ref.objectType) || ref.line <= 0) {
            continue;
        }
        const path = ref.memberPath ?? [];
        const nested = path.length > 2 && path[path.length - 2] === 'Methods';
        const uri = nested ? methodUri(ref.objectType, ref.objectName, path) : objectUri(ref.objectType, ref.objectName);
        const document = await vscode.workspace.openTextDocument(uri);
        // Methods of nested nodes have their own document: the whole method is searched.
        const first = nested ? 0 : compilerLineToDocumentLine(document.getText(), ref.line);
        const last = nested ? document.lineCount - 1 : Math.min(document.lineCount - 1, first + 2);
        for (let line = first; line <= last; line++) {
            const key = `${uri.toString()}#${line}`;
            if (seen.has(key)) {
                continue;
            }
            seen.add(key);
            const text = document.lineAt(line).text;
            for (const m of text.matchAll(pattern)) {
                const start = m.index! + (m[1]?.length ?? 0);
                edit.replace(uri, new vscode.Range(line, start, line, start + m[0].length - (m[1]?.length ?? 0)), replacement, metadata);
                count++;
            }
        }
    }
    return count;
}

/** Elements with metadata references (properties, not code) to change; standard elements can't be changed. */
async function metadataTargets(server: XppServerClient, refs: Reference[]): Promise<{ ours: { objectType: string; objectName: string }[]; others: string[] }> {
    const elements = new Map<string, { objectType: string; objectName: string }>();
    for (const ref of refs) {
        if (ref.objectType && (ref.line <= 0 || !CodeTypes.has(ref.objectType))) {
            elements.set(`${ref.objectType}/${ref.objectName}`.toLowerCase(), { objectType: ref.objectType, objectName: ref.objectName });
        }
    }
    const ours: { objectType: string; objectName: string }[] = [];
    const others: string[] = [];
    for (const element of elements.values()) {
        ((await isCustom(server, element.objectType, element.objectName)).custom ? ours.push(element) : others.push(`${typeName(element.objectType)} ${element.objectName}`));
    }
    return { ours, others };
}

async function saveDocuments(edit: vscode.WorkspaceEdit): Promise<void> {
    for (const [uri] of edit.entries()) {
        const document = vscode.workspace.textDocuments.find(d => d.uri.toString() === uri.toString());
        if (document?.isDirty) {
            await document.save();
        }
    }
}

async function replaceInMetadata(server: XppServerClient, targets: { objectType: string; objectName: string }[], body: Record<string, string>): Promise<{ changed: number; failed: string[] }> {
    let changed = 0;
    const failed: string[] = [];
    for (const target of targets) {
        const result = await server.bridge('replace_references', { ...target, ...body });
        if (result.success) {
            changed += Number(result.changed ?? 0);
        } else {
            failed.push(`${target.objectName}: ${result.message}`);
        }
    }
    return { changed, failed };
}

function report(title: string, lines: string[]): void {
    const problems = lines.filter(l => l.startsWith('Not changed') || l.startsWith('Failed'));
    (problems.length ? vscode.window.showWarningMessage : vscode.window.showInformationMessage)(`${title} ${lines.join(' ')}`);
}

/**
 * Renames a field of one of your tables everywhere: the field and what names it in the table (field groups, indexes,
 * relations), X++ code that uses it (refactor preview first) and the metadata of your other elements (form controls, data
 * sources, entity fields, relations). References come from the cross-reference database as of the last build.
 */
export async function renameFieldEverywhere(server: XppServerClient, table: string, field: string, newName?: string): Promise<boolean> {
    if (!(await isCustom(server, 'AxTable', table)).custom) {
        throw new Error(`${table} is not in one of your models; add a field through a table extension instead.`);
    }
    newName ??= await vscode.window.showInputBox({ title: `Rename ${table}.${field}`, value: field, validateInput: v => NamePattern.test(v) ? undefined : 'Letters, digits and _.' });
    if (!newName || newName === field) {
        return false;
    }
    const refs = await references(server, 'AxTable', table, field);
    const edit = new vscode.WorkspaceEdit();
    const label = `Rename ${table}.${field} to ${newName}`;
    const intrinsic = new RegExp(`(\\b(?:fieldNum|fieldStr|fieldPName)\\s*\\(\\s*${escape(table)}\\s*,\\s*)${escape(field)}\\b`, 'gi');
    const member = new RegExp(`(\\.)${escape(field)}\\b(?!\\s*\\()`, 'g');
    const codeCount = await codeEdits(refs, intrinsic, newName, edit, label) + await codeEdits(refs, member, newName, edit, label);
    const { ours, others } = await metadataTargets(server, refs);
    const ok = await vscode.window.showWarningMessage(`${label}?`, {
        modal: true,
        detail: `${codeCount} place(s) in X++ code (shown in the refactor preview), the metadata of ${ours.length} other element(s) of your models`
            + `${others.length ? `, and ${others.length} element(s) outside your models that can't be changed (${others.slice(0, 5).join(', ')})` : ''}. `
            + 'The references come from the cross-reference database as of the last build with cross references.',
    }, 'Rename');
    if (ok !== 'Rename') {
        return false;
    }
    if (codeCount && !await vscode.workspace.applyEdit(edit, { isRefactoring: true })) {
        return false;
    }
    const renamed = await server.bridge('rename_element_node', { objectType: 'AxTable', objectName: table, path: ['Fields', field], newName });
    if (!renamed.success) {
        throw new Error(`${table}.${field} was not renamed: ${renamed.message} Undo the code changes (Ctrl+Z) before saving them.`);
    }
    await saveDocuments(edit);
    const metadata = await replaceInMetadata(server, ours, { table, oldName: field, newName });
    report(`Renamed ${table}.${field} to ${newName}.`, [
        `${codeCount} code change(s), ${metadata.changed} metadata reference(s) in ${ours.length} element(s).`,
        ...metadata.failed.length ? [`Failed: ${metadata.failed.join('; ')}.`] : [],
        ...others.length ? [`Not changed (outside your models): ${others.join(', ')}.`] : [],
        'Build to check.',
    ]);
    return true;
}

/**
 * Renames one of your elements everywhere: a copy under the new name (its own code's references to itself follow), X++ code
 * that uses it (refactor preview first), the metadata of your elements that name it, then the original is deleted.
 */
export async function renameElementEverywhere(server: XppServerClient, objectType: string, objectName: string): Promise<void> {
    const owner = await isCustom(server, objectType, objectName);
    if (!owner.custom || !owner.model) {
        throw new Error(`${objectName} is not in one of your models; only your own elements can be renamed.`);
    }
    if (objectName.includes('.')) {
        throw new Error('Extensions are renamed by duplicating them under the new suffix and deleting the original.');
    }
    const newName = await vscode.window.showInputBox({ title: `Rename ${typeName(objectType)} ${objectName}`, value: objectName,
        validateInput: v => NamePattern.test(v) ? undefined : 'Letters, digits and _.' });
    if (!newName || newName === objectName) {
        return;
    }
    const refs = await references(server, objectType, objectName).catch(err => {
        // Types the cross-reference database doesn't index (e.g. menu items of some kinds) still rename, without references.
        if (/not available for/i.test(String(err.message))) {
            return [] as Reference[];
        }
        throw err;
    });
    const edit = new vscode.WorkspaceEdit();
    const label = `Rename ${objectName} to ${newName}`;
    const usage = new RegExp(`(^|[^.\\w])${escape(objectName)}\\b`, 'g');
    const codeCount = await codeEdits(refs.filter(r => !(r.objectType === objectType && r.objectName === objectName)), usage, newName, edit, label);
    const { ours, others } = await metadataTargets(server, refs);
    const ok = await vscode.window.showWarningMessage(`${label}?`, {
        modal: true,
        detail: `${objectName} is copied to ${newName} in ${owner.model} and then deleted. ${codeCount} place(s) in X++ code change (refactor preview), `
            + `and the metadata of ${ours.length} element(s) of your models`
            + `${others.length ? `; ${others.length} element(s) outside your models can't be changed (${others.slice(0, 5).join(', ')})` : ''}. `
            + (objectType === 'AxTable' ? 'Data in the old table is not moved: the next database sync drops it. ' : '')
            + 'The references come from the cross-reference database as of the last build with cross references.',
    }, 'Rename');
    if (ok !== 'Rename') {
        return;
    }
    if (codeCount && !await vscode.workspace.applyEdit(edit, { isRefactoring: true })) {
        return;
    }
    const copy = await server.bridge('duplicate_object', { objectType, objectName, newName, modelName: owner.model });
    if (!copy.success) {
        throw new Error(`${objectName} was not renamed: ${copy.message} Undo the code changes (Ctrl+Z) before saving them.`);
    }
    await server.bridge('add_to_project', { objectType, objectName: newName });
    // The copy's own code may still name the original (Old::find(), new Old()).
    if (CodeTypes.has(objectType)) {
        const own = await vscode.workspace.openTextDocument(objectUri(objectType, newName));
        const selfEdit = new vscode.WorkspaceEdit();
        own.getText().split('\n').forEach((text, line) => {
            for (const m of text.matchAll(usage)) {
                const start = m.index! + m[1].length;
                selfEdit.replace(own.uri, new vscode.Range(line, start, line, start + objectName.length), newName);
            }
        });
        if (selfEdit.size && await vscode.workspace.applyEdit(selfEdit)) {
            await own.save();
        }
    }
    await saveDocuments(edit);
    const metadata = await replaceInMetadata(server, ours, { targetType: objectType, oldName: objectName, newName });
    const deleted = await server.bridge('delete_object', { objectType, objectName });
    report(`Renamed ${typeName(objectType)} ${objectName} to ${newName}.`, [
        `${codeCount} code change(s), ${metadata.changed} metadata reference(s) in ${ours.length} element(s).`,
        ...deleted.success ? [] : [`Failed: the original was not deleted (${deleted.message}).`],
        ...metadata.failed.length ? [`Failed: ${metadata.failed.join('; ')}.`] : [],
        ...others.length ? [`Not changed (outside your models): ${others.join(', ')}.`] : [],
        'Build to check.',
    ]);
    await vscode.commands.executeCommand('xpp.openDesigner', objectType, newName);
}
