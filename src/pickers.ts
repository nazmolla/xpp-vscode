import * as vscode from 'vscode';
import { iconFor, typeName } from './aot';
import { listLabelFiles } from './d365Tools';
import { XppServerClient } from './serverClient';

interface Match { objectType: string; objectName: string; modelName: string; isCustom?: boolean }

/** Searches elements of the given types while typing; returns the picked name. */
export function pickReference(server: XppServerClient, objectTypes: string[], title: string, value = ''): Promise<string | undefined> {
    return new Promise(resolve => {
        const pick = vscode.window.createQuickPick<vscode.QuickPickItem & { name?: string }>();
        pick.title = title;
        pick.placeholder = `${objectTypes.map(typeName).join(' or ')} name (at least 3 characters)`;
        pick.value = value;
        let timer: NodeJS.Timeout | undefined;
        let accepted = false;
        const search = (text: string) => {
            clearTimeout(timer);
            if (text.trim().length < 3) {
                pick.items = [];
                return;
            }
            timer = setTimeout(async () => {
                pick.busy = true;
                try {
                    const result = await server.bridge('find_object', { objectName: text.trim(), maxResults: 50, ...objectTypes.length === 1 ? { objectType: objectTypes[0] } : {} });
                    pick.items = ((result.matches ?? []) as Match[]).filter(m => objectTypes.includes(m.objectType)).map(m => ({
                        label: `$(${iconFor(m.objectType)}) ${m.objectName}`, description: `${typeName(m.objectType)} · ${m.modelName}`, name: m.objectName, alwaysShow: true,
                    }));
                } finally {
                    pick.busy = false;
                }
            }, 250);
        };
        pick.onDidChangeValue(search);
        pick.onDidAccept(() => {
            const name = pick.selectedItems[0]?.name;
            if (name) {
                accepted = true;
                resolve(name);
                pick.hide();
            }
        });
        pick.onDidHide(() => {
            clearTimeout(timer);
            if (!accepted) {
                resolve(undefined);
            }
            pick.dispose();
        });
        pick.show();
        search(value);
    });
}

type LabelItem = vscode.QuickPickItem & { reference?: string; create?: { fileId: string; text: string } };

/**
 * Finds a label by its text in your label files (en-US), or creates one: returns the reference (@File:Id). A typed
 * reference such as @SYS12345 is taken as it is.
 */
export async function pickLabel(server: XppServerClient, title: string, current = ''): Promise<string | undefined> {
    const files = [...(await listLabelFiles(true)).entries()].filter(([, languages]) => languages.has('en-US')).map(([id]) => id).sort();
    return new Promise(resolve => {
        const pick = vscode.window.createQuickPick<LabelItem>();
        pick.title = title;
        pick.placeholder = 'Label text to find or create, or a reference such as @SYS12345';
        pick.value = current.startsWith('@') ? '' : current;
        let timer: NodeJS.Timeout | undefined;
        let done = false;
        const finish = (value: string | undefined) => {
            done = true;
            resolve(value);
            pick.hide();
        };
        const search = (text: string) => {
            clearTimeout(timer);
            const typed = text.trim();
            const creates: LabelItem[] = typed && !typed.startsWith('@')
                ? files.map(fileId => ({ label: `$(add) Create label "${typed}" in ${fileId}`, alwaysShow: true, create: { fileId, text: typed } }))
                : [];
            if (/^@\w+:?\w+$/.test(typed)) {
                pick.items = [{ label: `$(symbol-string) Use ${typed}`, reference: typed, alwaysShow: true }];
                return;
            }
            pick.items = creates;
            if (typed.length < 3) {
                return;
            }
            timer = setTimeout(async () => {
                pick.busy = true;
                try {
                    const found: LabelItem[] = [];
                    for (const fileId of files) {
                        const result = await server.bridge('read_label', { labelFileId: fileId, language: 'en-US', searchText: typed, maxResults: '20' }).catch(() => undefined);
                        for (const l of (result?.labels ?? []) as { id: string; text: string }[]) {
                            const reference = l.id.startsWith('@') ? l.id : `@${fileId}:${l.id}`;
                            found.push({ label: l.text, description: reference, reference, alwaysShow: true });
                        }
                    }
                    if (pick.value.trim() === typed) {
                        pick.items = [...found, ...found.length ? [{ label: '', kind: vscode.QuickPickItemKind.Separator } as LabelItem] : [], ...creates];
                    }
                } finally {
                    pick.busy = false;
                }
            }, 300);
        };
        pick.onDidChangeValue(search);
        pick.onDidAccept(async () => {
            const item = pick.selectedItems[0];
            if (item?.reference) {
                finish(item.reference);
            } else if (item?.create) {
                pick.busy = true;
                const reference = await createLabel(server, item.create.fileId, item.create.text).catch(err => {
                    vscode.window.showErrorMessage((err as Error).message);
                    return undefined;
                });
                finish(reference);
            }
        });
        pick.onDidHide(() => {
            clearTimeout(timer);
            if (!done) {
                resolve(undefined);
            }
            pick.dispose();
        });
        pick.show();
        search(pick.value);
    });
}

/** A label ID from the text (CustomerLoyaltyTier), numbered when the ID is taken. */
async function createLabel(server: XppServerClient, fileId: string, text: string): Promise<string> {
    const base = text.replace(/[^A-Za-z0-9 ]/g, ' ').split(/\s+/).filter(Boolean).map(w => w[0].toUpperCase() + w.slice(1)).join('').slice(0, 40) || 'Label';
    const id = /^[A-Za-z]/.test(base) ? base : `L${base}`;
    let last = '';
    for (let i = 0; i < 20; i++) {
        const labelId = i ? `${id}${i}` : id;
        const result = await server.bridge('create_label', { labelFileId: fileId, language: 'en-US', labelId, text });
        if (result.success) {
            return `@${fileId}:${labelId}`;
        }
        last = result.message ?? '';
        if (!/exist/i.test(last)) {
            break;
        }
    }
    throw new Error(`The label could not be created: ${last}`);
}
