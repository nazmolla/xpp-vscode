import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { listModels } from '../aot';
import { readProject } from '../project';
import { XppServerClient } from '../serverClient';
import { ElementSpec, NamePattern } from './xpp';

export { doc, ElementSpec, NamePattern, xppString } from './xpp';

/** The active project's model: wizards create their elements there. */
export async function projectModel(): Promise<string> {
    const project = await readProject();
    if (!project?.model) {
        throw new Error('Open a D365FO project first: the new elements go to its model.');
    }
    return project.model;
}

export async function askText(title: string, prompt: string, placeHolder: string, validate?: (v: string) => string | undefined): Promise<string | undefined> {
    const value = await vscode.window.showInputBox({ title, prompt, placeHolder, ignoreFocusOut: true, validateInput: validate });
    return value?.trim() || undefined;
}

export const askName = (title: string, prompt: string, placeHolder: string) =>
    askText(title, prompt, placeHolder, v => NamePattern.test(v.trim()) ? undefined : 'A name of letters, digits and _, starting with a letter.');

/** A label in the model's label file when it has one (the text otherwise, which best practice reports). */
export async function labelFor(server: XppServerClient, modelName: string, id: string, text: string): Promise<string> {
    const model = (await listModels()).find(m => m.name.toLowerCase() === modelName.toLowerCase());
    const files = model ? await fs.readdir(path.join(model.folder, 'AxLabelFile')).catch(() => [] as string[]) : [];
    const match = files.map(f => /^(.+)_en-US\.xml$/i.exec(f)).find(m => m);
    if (!match) {
        return text;
    }
    const result = await server.bridge('create_label', { labelFileId: match[1], language: 'en-US', labelId: id, text });
    return result.success ? `@${match[1]}:${id}` : text;
}

/**
 * Creates the elements in order and adds them to the project. When one fails, the ones already created are deleted again,
 * so a wizard leaves either all of its elements or none.
 */
export async function createElements(server: XppServerClient, model: string, title: string, elements: ElementSpec[]): Promise<void> {
    const created: ElementSpec[] = [];
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title }, async progress => {
        for (const element of elements) {
            progress.report({ message: element.objectName, increment: 100 / elements.length });
            const result = await server.bridge('create_object', { objectType: element.objectType, objectName: element.objectName, modelName: model, ...element.body });
            if (!result.success) {
                const rolledBack = await rollback(server, created);
                throw new Error(`${element.objectType.replace(/^Ax/, '')} ${element.objectName}: ${result.message}`
                    + (created.length ? rolledBack.length === created.length ? ' The elements created before it were removed again.' : ` Not removed: ${created.filter(c => !rolledBack.includes(c)).map(c => c.objectName).join(', ')}.` : ''));
            }
            await server.bridge('add_to_project', { objectType: element.objectType, objectName: element.objectName });
            created.push(element);
        }
    });
}

async function rollback(server: XppServerClient, created: ElementSpec[]): Promise<ElementSpec[]> {
    const removed: ElementSpec[] = [];
    for (const element of [...created].reverse()) {
        const result = await server.bridge('delete_object', { objectType: element.objectType, objectName: element.objectName }).catch(() => undefined);
        if (result?.success) {
            removed.push(element);
        }
    }
    return removed;
}
