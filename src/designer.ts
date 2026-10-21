import { randomBytes } from 'crypto';
import * as vscode from 'vscode';
import { readProject } from './project';
import { XppServerClient } from './serverClient';

interface DesignerNode {
    name: string;
    type: string;
    label?: string;
    dataSource?: string;
    dataField?: string;
    dataGroup?: string;
    visible?: string;
    addedBy?: string;
    container: boolean;
    children: DesignerNode[];
}

interface FormTree {
    success: boolean;
    message?: string;
    form: string;
    model: string;
    editable: boolean;
    extensions: string[];
    dataSources: { name: string; table: string; joinSource?: string; linkType?: string; addedBy?: string }[];
    root: DesignerNode;
}

interface AddRequest {
    target: string;
    parent: string;
    targetIsContainer: boolean;
}

/**
 * Visual form designer: shows the form with every extension merged and makes changes through a form
 * extension in your model (new fields/groups placed explicitly, property changes on existing controls).
 */
export class FormDesigner {
    private static readonly panels = new Map<string, FormDesigner>();
    private tree: FormTree | undefined;

    static open(context: vscode.ExtensionContext, server: XppServerClient, formName: string): void {
        const existing = FormDesigner.panels.get(formName.toLowerCase());
        if (existing) {
            existing.panel.reveal();
            return;
        }
        const panel = vscode.window.createWebviewPanel('xppFormDesigner', `${formName} [Design]`, vscode.ViewColumn.Active, {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
        FormDesigner.panels.set(formName.toLowerCase(), new FormDesigner(context, server, formName, panel));
    }

    private constructor(
        context: vscode.ExtensionContext,
        private readonly server: XppServerClient,
        private readonly formName: string,
        private readonly panel: vscode.WebviewPanel,
    ) {
        panel.webview.html = this.html(context, panel.webview);
        panel.onDidDispose(() => FormDesigner.panels.delete(formName.toLowerCase()));
        panel.webview.onDidReceiveMessage(message => this.onMessage(message).catch(err => this.post({ type: 'error', message: (err as Error).message })));
    }

    private post(message: object): void {
        void this.panel.webview.postMessage(message);
    }

    private async onMessage(message: any): Promise<void> {
        switch (message.command) {
            case 'ready':
            case 'refresh':
                return this.load();
            case 'addField':
                return this.addFields(message as AddRequest);
            case 'addGroup':
                return this.addGroup(message as AddRequest);
            case 'setVisible':
                return this.modify(message.name, { Visible: message.visible ? 'Yes' : 'No' });
            case 'setLabel': {
                const label = await vscode.window.showInputBox({ title: `Label for ${message.name}`, prompt: 'Text or label reference (@File:Id)', value: message.current ?? '' });
                return label === undefined ? undefined : this.modify(message.name, { Label: label });
            }
        }
    }

    private async load(): Promise<void> {
        this.post({ type: 'busy', on: true });
        try {
            const tree = await this.server.bridge<FormTree>('get_form_tree', { formName: this.formName });
            if (!tree.success) {
                throw new Error(tree.message ?? `Form ${this.formName} not found.`);
            }
            this.tree = tree;
            this.post({ type: 'tree', data: tree });
        } finally {
            this.post({ type: 'busy', on: false });
        }
    }

    private async addFields(request: AddRequest): Promise<void> {
        const tree = this.requireTree();
        const ds = await vscode.window.showQuickPick(
            tree.dataSources.map(d => ({ label: d.name, description: d.table, detail: d.addedBy ? `added by ${d.addedBy}` : undefined })),
            { title: 'Data source', placeHolder: 'Data source the new fields are bound to' });
        if (!ds) {
            return;
        }
        const fields = await this.server.bridge('get_table_fields', { tableName: ds.description });
        if (!fields.success) {
            throw new Error(fields.message);
        }
        const picked = await vscode.window.showQuickPick(
            (fields.fields as any[]).map(f => ({ label: f.name, description: [f.type, f.edt ?? f.enumType].filter(Boolean).join(' '), detail: f.label })),
            { title: `Fields of ${ds.description}`, canPickMany: true, matchOnDescription: true, matchOnDetail: true });
        if (!picked?.length) {
            return;
        }

        const existing = this.controlNames();
        const controls = picked.map(f => ({
            name: uniqueName(`${ds.label}_${f.label}`, existing),
            dataSource: ds.label,
            dataField: f.label,
        }));
        await this.placeControls(request, controls, `Added ${controls.length} field(s)`);
    }

    private async addGroup(request: AddRequest): Promise<void> {
        const name = await vscode.window.showInputBox({ title: 'New group name', validateInput: v => /^[A-Za-z_]\w*$/.test(v) ? undefined : 'Letters, digits and _ only' });
        if (!name) {
            return;
        }
        const label = await vscode.window.showInputBox({ title: 'Group caption (text or @File:Id)', value: '' });
        await this.placeControls(request, [{ name: uniqueName(name, this.controlNames()), controlType: 'Group', label: label || undefined }], `Added group ${name}`);
    }

    /** Places controls after the selected control, or at the end of the selected container. */
    private async placeControls(request: AddRequest, controls: object[], done: string): Promise<void> {
        const extension = await this.extensionName();
        if (!extension) {
            return;
        }
        let previous = request.targetIsContainer ? undefined : request.target;
        const parent = request.targetIsContainer ? request.target : request.parent;
        // One request per control when inserting after a sibling, so each one anchors on the previous.
        const batches = previous ? controls.map(c => [c]) : [controls];
        for (const batch of batches) {
            const placed = batch.map(c => ({ ...c, parent, position: previous ? 'after' : 'end', previousSibling: previous }));
            await this.saveExtension(extension, { controls: placed });
            previous = previous ? (batch[batch.length - 1] as any).name : undefined;
        }
        vscode.window.setStatusBarMessage(`${done} in ${extension}`, 5000);
        await this.load();
    }

    private async modify(control: string, properties: Record<string, string>): Promise<void> {
        const extension = await this.extensionName();
        if (!extension) {
            return;
        }
        await this.saveExtension(extension, { controlModifications: [{ name: control, properties }] });
        await this.load();
    }

    private async saveExtension(extension: string, changes: object): Promise<void> {
        const tree = this.requireTree();
        const exists = tree.extensions.some(e => e.toLowerCase() === extension.toLowerCase());
        const project = await readProject();
        const result = exists
            ? await this.server.bridge('update_object', { objectType: 'AxFormExtension', objectName: extension, ...changes })
            : await this.server.bridge('create_object', { objectType: 'AxFormExtension', objectName: extension, modelName: project?.model, ...changes });
        if (!result.success) {
            throw new Error(result.message ?? 'The form extension was not saved.');
        }
        if (!exists && project) {
            await this.server.bridge('add_to_project', { objectType: 'AxFormExtension', objectName: extension });
        }
    }

    /** <Form>.<ProjectModel>, or one of the form's existing extensions in a custom model. */
    private async extensionName(): Promise<string | undefined> {
        const tree = this.requireTree();
        const project = await readProject();
        if (!project?.model) {
            vscode.window.showErrorMessage('Open a D365FO project (.rnrproj) so changes go into a form extension in its model.');
            return undefined;
        }
        if (tree.editable && tree.model?.toLowerCase() === project.model.toLowerCase()) {
            vscode.window.showWarningMessage(`${this.formName} is in your own model; edit it directly. The designer only writes extensions for now.`);
            return undefined;
        }
        return `${this.formName}.${project.model}`;
    }

    private controlNames(): Set<string> {
        const names = new Set<string>();
        const walk = (n: DesignerNode) => {
            names.add(n.name.toLowerCase());
            n.children.forEach(walk);
        };
        walk(this.requireTree().root);
        return names;
    }

    private requireTree(): FormTree {
        if (!this.tree) {
            throw new Error('The form is still loading.');
        }
        return this.tree;
    }

    private html(context: vscode.ExtensionContext, webview: vscode.Webview): string {
        const nonce = randomBytes(16).toString('base64');
        const script = webview.asWebviewUri(vscode.Uri.joinPath(context.extensionUri, 'media', 'designer.js'));
        const previewScript = webview.asWebviewUri(vscode.Uri.joinPath(context.extensionUri, 'media', 'formPreview.js'));
        const style = webview.asWebviewUri(vscode.Uri.joinPath(context.extensionUri, 'media', 'designer.css'));
        const previewStyle = webview.asWebviewUri(vscode.Uri.joinPath(context.extensionUri, 'media', 'formPreview.css'));
        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<link rel="stylesheet" href="${previewStyle}">
<link rel="stylesheet" href="${style}">
<title>Form designer</title>
</head>
<body>
<header id="toolbar"></header>
<main>
  <nav id="outline" aria-label="Controls"></nav>
  <section id="canvas" aria-label="Form preview"></section>
  <aside id="properties" aria-label="Properties"></aside>
</main>
<script nonce="${nonce}" src="${previewScript}"></script>
<script nonce="${nonce}" src="${script}"></script>
</body>
</html>`;
    }
}

function uniqueName(base: string, taken: Set<string>): string {
    let name = base;
    for (let i = 2; taken.has(name.toLowerCase()); i++) {
        name = `${base}${i}`;
    }
    taken.add(name.toLowerCase());
    return name;
}
