import { existsSync } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { objectUri } from '../codeView';
import { addCreatedToProject, openInBrowser, targetModel } from '../d365Tools';
import { selectedEnvironmentUrl } from '../environment';
import { readProject } from '../project';
import { methodName, splitSource } from '../xppSource';
import { call, findExact, indent, ProductivityContext } from './common';

const MainPattern = /\bstatic\s+void\s+main\s*\(\s*Args\s+\w+\s*\)/i;

/**
 * X++: Run Snippet in Environment: puts the code of an untitled (or plain .xpp file) class with a main method into the
 * class <Model>ScratchRunner of the project's model, builds the project, optionally deploys, and runs it through
 * SysClassRunner in the browser. Without such a document, opens a new one from a template.
 */
export async function runSnippet(ctx: ProductivityContext): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    const document = editor?.document;
    if (!document || document.languageId !== 'xpp' || !['untitled', 'file'].includes(document.uri.scheme) || !MainPattern.test(document.getText())) {
        await openTemplate();
        return;
    }
    const model = await targetModel('Run snippet');
    if (!model) {
        return;
    }
    const runner = `${model.replace(/\W/g, '')}ScratchRunner`;
    const { declaration, methods } = scratchSource(document.getText(), runner);

    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Saving the snippet to ${runner}…` }, async () => {
        const existing = await findExact(ctx.server, 'AxClass', runner);
        if (existing && !existing.isCustom) {
            throw new Error(`${runner} exists in ${existing.modelName}, which is not one of your models.`);
        }
        if (existing) {
            const read = await call<{ methods?: { name: string }[] }>(ctx.server, 'bridge', 'read_object', { objectType: 'AxClass', objectName: runner });
            const names = new Set(methods.map(m => methodName(m).toLowerCase()));
            const removeMethodNames = (read.methods ?? []).map(m => m.name).filter(n => n !== 'classDeclaration' && !names.has(n.toLowerCase()));
            await call(ctx.server, 'bridge', 'update_object', { objectType: 'AxClass', objectName: runner, declaration, methods, removeMethodNames, formatCode: false });
        } else {
            await call(ctx.server, 'bridge', 'create_object', { objectType: 'AxClass', objectName: runner, modelName: model, declaration, methods, formatCode: false });
        }
        await addCreatedToProject(ctx.server, 'AxClass', runner);
    });
    ctx.refreshCode(objectUri('AxClass', runner));
    ctx.refreshTrees();

    const built = await vscode.commands.executeCommand<boolean | undefined>('xpp.buildProject');
    if (built !== true) {
        vscode.window.showErrorMessage(`The build of ${model} failed or was cancelled; ${runner} was not run. See Problems and the X++ Build output.`);
        return;
    }
    if (needsDeploy()) {
        const choice = await vscode.window.showInformationMessage(
            `${runner} is built. Deploy ${model} to the online environment before running it?`, { modal: true },
            'Deploy and Run', 'Run Without Deploying');
        if (!choice) {
            return;
        }
        if (choice === 'Deploy and Run') {
            await vscode.commands.executeCommand('xpp.pac.deploy');
        }
    }
    await openInBrowser({ item: { objectType: 'AxClass', objectName: runner } }, false, ctx.server);
    vscode.window.showInformationMessage(`Running ${runner} in the browser. When you no longer need it, remove it with Delete in the project view.`);
}

/** The scratch runner's declaration (members of the snippet's class kept) and the snippet's methods, indented for the class body. */
function scratchSource(text: string, runner: string): { declaration: string; methods: string[] } {
    const source = splitSource(text);
    const methods = source.methods.filter(m => methodName(m) !== 'classDeclaration').map(m => {
        const lines = m.replace(/\s+$/, '').split('\n');
        const min = Math.min(...lines.filter(l => l.trim()).map(l => l.length - l.trimStart().length));
        return min >= 4 ? m.replace(/\s+$/, '') : indent(m.replace(/\s+$/, ''), ' '.repeat(4 - min));
    });
    if (!methods.some(m => MainPattern.test(m))) {
        throw new Error('The snippet needs a method public static void main(Args _args).');
    }
    const decl = source.declaration;
    const open = decl.indexOf('{');
    const close = decl.lastIndexOf('}');
    const members = open >= 0 && close > open ? decl.slice(open + 1, close).replace(/^\s*\n/, '').replace(/\s+$/, '') : '';
    return {
        declaration: `internal final class ${runner}\n{\n${members ? members + '\n' : ''}}`,
        methods,
    };
}

/** A UDE's runtime only sees code after a deploy; a deployment solution or a Dataverse URL marks such an environment. */
function needsDeploy(): boolean {
    const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
    const solution = !!root && existsSync(path.join(root, '.erp', 'xpp.json'));
    return solution || !!selectedEnvironmentUrl();
}

async function openTemplate(): Promise<void> {
    const project = await readProject().catch(() => undefined);
    const prefix = (project?.model ?? 'My').replace(/\W/g, '');
    const content = [
        `internal final class ${prefix}Scratch`,
        '{',
        '    public static void main(Args _args)',
        '    {',
        '        info("Hello");',
        '    }',
        '}',
        '',
    ].join('\n');
    const document = await vscode.workspace.openTextDocument({ language: 'xpp', content });
    await vscode.window.showTextDocument(document);
    vscode.window.showInformationMessage('Write the code to run in main, then run X++: Run Snippet in Environment again.');
}
