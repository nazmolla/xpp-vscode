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
