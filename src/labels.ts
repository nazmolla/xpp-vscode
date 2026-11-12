import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels } from './aot';
import { listLabelFiles } from './d365Tools';
import { XppServerClient } from './serverClient';

interface LabelEntry { id: string; texts: Record<string, string>; comment: string }

const BatchSize = 40;

async function pickLabelFile(title: string): Promise<{ id: string; languages: string[] } | undefined> {
    const files = await listLabelFiles(true);
    const pick = await vscode.window.showQuickPick([...files.entries()].sort(([a], [b]) => a.localeCompare(b))
        .map(([id, languages]) => ({ label: id, description: [...languages].sort().join(', '), languages: [...languages] })), { title });
    return pick && { id: pick.label, languages: pick.languages };
}

async function entries(server: XppServerClient, labelFileId: string, languages: string[]): Promise<{ language: string; entries: LabelEntry[] }> {
    const result = await server.bridge('label_file_entries', { labelFile: labelFileId, limit: '100000', languages });
    if (!result.success) {
        throw new Error(result.message ?? 'Could not read the label file.');
    }
    return { language: result.language, entries: result.entries };
}

/** F&O Studio: Translate Missing Labels: texts missing in a label file's other languages, translated by the chat model and saved after a confirmation. */
export async function translateMissingLabels(server: XppServerClient): Promise<void> {
    const file = await pickLabelFile('Translate missing labels: label file');
    if (!file) {
        return;
    }
    const { language, entries: all } = await entries(server, file.id, file.languages);
    const targets = file.languages.filter(l => l !== language);
    const missing = targets.map(l => ({ language: l, items: all.filter(e => e.texts[language] && !e.texts[l]) })).filter(m => m.items.length);
    if (!missing.length) {
        vscode.window.showInformationMessage(targets.length ? `Every label of ${file.id} has a text in ${targets.join(', ')}.` : `${file.id} has only ${language}.`);
        return;
    }
    const total = missing.reduce((n, m) => n + m.items.length, 0);
    const ok = await vscode.window.showWarningMessage(`Translate ${total} missing text(s) of ${file.id} (${missing.map(m => `${m.language}: ${m.items.length}`).join(', ')}) with the chat model and save them?`,
        { modal: true, detail: 'Machine translations: review them in the label editor afterwards.' }, 'Translate');
    if (ok !== 'Translate') {
        return;
    }
    const [model] = await vscode.lm.selectChatModels({ vendor: 'copilot' });
    if (!model) {
        throw new Error('No chat model is available. Sign in to GitHub Copilot.');
    }
    let saved = 0;
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Translating ${file.id}`, cancellable: true }, async (progress, token) => {
        for (const target of missing) {
            for (let i = 0; i < target.items.length && !token.isCancellationRequested; i += BatchSize) {
                const batch = target.items.slice(i, i + BatchSize);
                progress.report({ message: `${target.language} ${i + batch.length}/${target.items.length}` });
                const source = Object.fromEntries(batch.map(e => [e.id, e.texts[language]]));
                const prompt = `Translate these Dynamics 365 Finance & Operations user interface labels from ${language} to ${target.language}. `
                    + 'Keep placeholders such as %1 and %2, keep label references (@File:Id) unchanged, use the terminology of Microsoft business software, '
                    + 'and answer with only a JSON object mapping each key to its translation.\n\n' + JSON.stringify(source, null, 1);
                const response = await model.sendRequest([vscode.LanguageModelChatMessage.User(prompt)], {}, token);
                let text = '';
                for await (const chunk of response.text) {
                    text += chunk;
                }
                const json = /\{[\s\S]*\}/.exec(text)?.[0];
                const translated: Record<string, unknown> = json ? JSON.parse(json) : {};
                for (const entry of batch) {
                    const value = translated[entry.id];
                    if (typeof value === 'string' && value.trim()) {
                        // Opened as the primary language, so the target language's comment stays as it is.
                        const result = await server.bridge('save_label', { labelFile: `${file.id}_${language}`, labelId: entry.id, texts: { [target.language]: value.trim() }, comment: entry.comment });
                        saved += result.success ? 1 : 0;
                    }
                }
            }
        }
    });
    vscode.window.showInformationMessage(`Saved ${saved} of ${total} translation(s) in ${file.id}. Review them in the label editor.`);
}

/** Label IDs of a label file referenced (@File:Id) in the metadata files of your models, label files excluded. */
async function metadataLabelUses(labelFileId: string): Promise<Set<string>> {
    const ids = new Set<string>();
    const pattern = new RegExp(`@${labelFileId.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}:(\\w+)`, 'gi');
    const walk = async (folder: string): Promise<void> => {
        for (const entry of await fs.readdir(folder, { withFileTypes: true }).catch(() => [])) {
            const full = path.join(folder, entry.name);
            if (entry.isDirectory() && entry.name !== 'AxLabelFile') {
                await walk(full);
            } else if (entry.isFile() && entry.name.toLowerCase().endsWith('.xml')) {
                for (const m of (await fs.readFile(full, 'utf8').catch(() => '')).matchAll(pattern)) {
                    ids.add(m[1].toLowerCase());
                }
            }
        }
    };
    for (const model of (await listModels()).filter(m => m.isCustom)) {
        await walk(model.folder);
    }
    return ids;
}

/** F&O Studio: Find Unused Labels: labels of your label files used neither in code (cross references) nor in your models' metadata. */
export async function findUnusedLabels(server: XppServerClient): Promise<void> {
    const file = await pickLabelFile('Find unused labels: label file');
    if (!file) {
        return;
    }
    const [{ language, entries: all }, usage] = await Promise.all([
        entries(server, file.id, []),
        server.bridge('label_usage', { labelFileId: file.id }),
    ]);
    if (!usage.success) {
        throw new Error(usage.message);
    }
    const used = new Set((usage.used as string[]).map(u => u.toLowerCase()));
    // The cross-reference database records uses in code only; labels in properties (field, menu item, form) are in the XML.
    for (const id of await metadataLabelUses(file.id)) {
        used.add(id);
    }
    const unused = all.filter(e => !used.has(e.id.toLowerCase()));
    if (!unused.length) {
        vscode.window.showInformationMessage(`Every label of ${file.id} is used.`);
        return;
    }
    const picks = await vscode.window.showQuickPick(unused.map(e => ({ label: e.id, description: e.texts[language], detail: e.comment || undefined })), {
        title: `${unused.length} label(s) of ${file.id} used neither in code (last build with cross references) nor in your models' metadata. Select labels to delete.`,
        canPickMany: true, matchOnDescription: true,
    });
    if (!picks?.length) {
        return;
    }
    const ok = await vscode.window.showWarningMessage(`Delete ${picks.length} label(s) from every language of ${file.id}?`,
        { modal: true, detail: 'Uses the cross-reference database cannot see (texts built at run time, other packages not built here) would show the raw label ID.' }, 'Delete');
    if (ok !== 'Delete') {
        return;
    }
    let deleted = 0;
    for (const pick of picks) {
        deleted += (await server.bridge('delete_label', { labelFile: file.id, labelId: pick.label })).success ? 1 : 0;
    }
    vscode.window.showInformationMessage(`Deleted ${deleted} label(s) from ${file.id}.`);
}

function csvField(value: string): string {
    return /[",\r\n]/.test(value) ? `"${value.replace(/"/g, '""')}"` : value;
}

/** RFC 4180: quoted fields may hold commas, quotes ("") and line breaks. */
export function parseCsv(text: string): string[][] {
    const rows: string[][] = [];
    let row: string[] = [];
    let field = '';
    let quoted = false;
    for (let i = 0; i < text.length; i++) {
        const c = text[i];
        if (quoted) {
            if (c === '"' && text[i + 1] === '"') {
                field += '"';
                i++;
            } else if (c === '"') {
                quoted = false;
            } else {
                field += c;
            }
        } else if (c === '"') {
            quoted = true;
        } else if (c === ',') {
            row.push(field);
            field = '';
        } else if (c === '\n' || c === '\r') {
            if (c === '\r' && text[i + 1] === '\n') {
                i++;
            }
            row.push(field);
            rows.push(row);
            row = [];
            field = '';
        } else {
            field += c;
        }
    }
    if (field || row.length) {
        row.push(field);
        rows.push(row);
    }
    return rows.filter(r => r.some(f => f !== ''));
}

/** F&O Studio: Export Labels…: a label file's labels in every language as CSV (Id, Comment, one column per language), e.g. for translators. */
export async function exportLabels(server: XppServerClient): Promise<void> {
    const files = await listLabelFiles();
    const pick = await vscode.window.showQuickPick([...files.entries()].sort(([a], [b]) => a.localeCompare(b))
        .map(([id, languages]) => ({ label: id, description: [...languages].sort().join(', '), languages: [...languages].sort() })), { title: 'Export labels: label file' });
    if (!pick) {
        return;
    }
    const target = await vscode.window.showSaveDialog({ title: `Export ${pick.label}`, filters: { 'CSV': ['csv'] },
        defaultUri: vscode.Uri.file(path.join(vscode.workspace.workspaceFolders?.[0]?.uri.fsPath ?? '', `${pick.label}.csv`)) });
    if (!target) {
        return;
    }
    const { language, entries: all } = await entries(server, pick.label, pick.languages);
    const languages = [language, ...pick.languages.filter(l => l !== language)];
    const lines = [['Id', 'Comment', ...languages], ...all.map(e => [e.id, e.comment ?? '', ...languages.map(l => e.texts[l] ?? '')])];
    // The byte order mark makes Excel read the file as UTF-8.
    await fs.writeFile(target.fsPath, '\ufeff' + lines.map(l => l.map(csvField).join(',')).join('\r\n') + '\r\n', 'utf8');
    const open = await vscode.window.showInformationMessage(`Exported ${all.length} label(s) of ${pick.label} in ${languages.length} language(s).`, 'Open');
    if (open) {
        await vscode.commands.executeCommand('vscode.open', target);
    }
}

/**
 * F&O Studio: Import Labels…: a CSV with the export's columns (Id, Comment, then languages) into one of your label files.
 * New labels are created and changed texts updated, after a summary; labels missing from the CSV are kept.
 */
export async function importLabels(server: XppServerClient): Promise<void> {
    const file = await pickLabelFile('Import labels: label file (your models)');
    if (!file) {
        return;
    }
    const source = (await vscode.window.showOpenDialog({ title: `Import into ${file.id}`, filters: { 'CSV': ['csv'] }, canSelectMany: false }))?.[0];
    if (!source) {
        return;
    }
    const rows = parseCsv((await fs.readFile(source.fsPath, 'utf8')).replace(/^\ufeff/, ''));
    const header = rows.shift()?.map(h => h.trim()) ?? [];
    const idColumn = header.findIndex(h => h.toLowerCase() === 'id');
    const commentColumn = header.findIndex(h => h.toLowerCase() === 'comment');
    const languageColumns = header.map((h, i) => ({ language: h, i })).filter(c => c.i !== idColumn && c.i !== commentColumn && c.language);
    if (idColumn < 0 || !languageColumns.length) {
        throw new Error('The CSV needs an "Id" column and one column per language, as Export Labels writes it.');
    }
    const unknown = languageColumns.filter(c => !file.languages.includes(c.language)).map(c => c.language);
    if (unknown.length) {
        throw new Error(`${file.id} has no ${unknown.join(', ')} language. Its languages: ${file.languages.join(', ')}.`);
    }
    const { language, entries: existing } = await entries(server, file.id, file.languages);
    const byId = new Map(existing.map(e => [e.id.toLowerCase(), e]));
    const changes: { id: string; texts: Record<string, string>; comment: string; isNew: boolean }[] = [];
    for (const row of rows) {
        const id = row[idColumn]?.trim();
        if (!id || !/^\w+$/.test(id)) {
            continue;
        }
        const current = byId.get(id.toLowerCase());
        const texts: Record<string, string> = {};
        for (const c of languageColumns) {
            const text = row[c.i] ?? '';
            if (text && text !== current?.texts[c.language]) {
                texts[c.language] = text;
            }
        }
        const comment = commentColumn >= 0 ? row[commentColumn] ?? '' : current?.comment ?? '';
        const commentChanged = !!current && comment !== (current.comment ?? '');
        if (commentChanged && !texts[language]) {
            // The server saves the comment with the primary language's text.
            const primary = languageColumns.find(c => c.language === language);
            texts[language] = (primary && row[primary.i]) || current!.texts[language] || '';
        }
        if (Object.values(texts).some(t => t)) {
            changes.push({ id: current?.id ?? id, texts, comment, isNew: !current });
        }
    }
    if (!changes.length) {
        vscode.window.showInformationMessage(`${file.id} already has every text of ${path.basename(source.fsPath)}.`);
        return;
    }
    const added = changes.filter(c => c.isNew).length;
    const ok = await vscode.window.showWarningMessage(`Import into ${file.id}: ${added} new label(s), ${changes.length - added} changed label(s)?`,
        { modal: true, detail: 'Labels that are not in the CSV stay as they are.' }, 'Import');
    if (ok !== 'Import') {
        return;
    }
    let saved = 0;
    const failed: string[] = [];
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Importing labels into ${file.id}`, cancellable: true }, async (progress, token) => {
        for (const [n, change] of changes.entries()) {
            if (token.isCancellationRequested) {
                break;
            }
            progress.report({ message: `${n + 1}/${changes.length}`, increment: 100 / changes.length });
            const result = await server.bridge('save_label', { labelFile: `${file.id}_${language}`, labelId: change.id, texts: change.texts, comment: change.comment });
            if (result.success) {
                saved++;
            } else {
                failed.push(`${change.id}: ${result.message}`);
            }
        }
    });
    (failed.length ? vscode.window.showWarningMessage : vscode.window.showInformationMessage)(
        `Imported ${saved} of ${changes.length} label(s) into ${file.id}.${failed.length ? ` Failed: ${failed.slice(0, 3).join('; ')}` : ''}`);
}
