import { randomBytes } from 'crypto';
import { promises as fs } from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';
import { elementOf, pickElement, TreeArg } from '../d365Tools';
import { ElementDesigner } from '../elementDesigner';
import { showReportableError } from '../reportProblem';
import { XppServerClient } from '../serverClient';
import { registerPanelSerializer, stateAttribute } from '../webviewState';
import { ensureReportRuntime } from './reportRuntime';

interface DesignerState {
    report: string;
    design?: string;
}

interface ReportInfo {
    success: boolean;
    message?: string;
    name: string;
    model: string;
    isCustom: boolean;
    designs: { name: string; kind: string }[];
    dataSets?: { name: string; fields: { name: string }[] }[];
}

/** The element designer's right-click context on a report node (media/element.js). */
interface ReportNodeContext {
    xppReportName?: string;
    xppReportDesign?: string;
}

function reportOf(arg: unknown): { report?: string; design?: string } {
    if (typeof arg === 'string') {
        return { report: arg };
    }
    const node = arg as ReportNodeContext | undefined;
    if (node?.xppReportName) {
        return { report: node.xppReportName, design: node.xppReportDesign };
    }
    return { report: elementOf(arg as TreeArg)?.objectName };
}

const DesignNamePattern = /^[A-Za-z_]\w*$/;

const ImageFilters = { Images: ['png', 'jpg', 'jpeg', 'bmp', 'gif', 'tif', 'tiff'] };
const MimeTypes: Record<string, string> = { '.png': 'image/png', '.jpg': 'image/jpeg', '.jpeg': 'image/jpeg', '.bmp': 'image/bmp', '.gif': 'image/gif', '.tif': 'image/tiff', '.tiff': 'image/tiff' };

/** A blank precision design: Letter portrait, 0.5in margins, empty body (2016 schema, as Visual Studio's designer saves). */
export function blankDesign(dataSet?: string): string {
    return `<?xml version="1.0" encoding="utf-8"?>
<Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition" xmlns:rd="http://schemas.microsoft.com/SQLServer/reporting/reportdesigner">
  <ReportSections>
    <ReportSection>
      <Body>
        <Height>9in</Height>
        <Style />
      </Body>
      <Width>7.5in</Width>
      <Page>
        <PageHeight>11in</PageHeight>
        <PageWidth>8.5in</PageWidth>
        <LeftMargin>0.5in</LeftMargin>
        <RightMargin>0.5in</RightMargin>
        <TopMargin>0.5in</TopMargin>
        <BottomMargin>0.5in</BottomMargin>
        <Style />
      </Page>
    </ReportSection>
  </ReportSections>
  <rd:ReportUnitType>Inch</rd:ReportUnitType>${dataSet ? `\n  <!-- Dataset: ${dataSet} -->` : ''}
</Report>`;
}

/** F&O Studio: Report Designer: lays out a precision design (RDL) of an SSRS report and previews it with Microsoft's report engine. */
export function registerReportDesigner(context: vscode.ExtensionContext, command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient): void {
    command('xpp.reports.openDesigner', async (arg?: TreeArg | ReportNodeContext | string, designArg?: string) => {
        const target = reportOf(arg);
        const report = target.report
            ?? await vscode.window.showInputBox({ title: 'Report', prompt: 'Name of the report (AxReport)', placeHolder: 'Cheque_US' });
        if (!report) {
            return;
        }
        let design = typeof designArg === 'string' ? designArg : target.design;
        if (!design) {
            // Several designs: pick one, as Visual Studio lists them under Designs.
            const info = await server.bridge<ReportInfo>('report_info', { reportName: report });
            if (info.success && info.designs.length > 1) {
                const picked = await vscode.window.showQuickPick(info.designs.map(d => ({
                    label: d.name, description: d.kind === 'precision' ? 'precision design' : 'auto design: creates a precision design from it', design: d,
                })), { title: `${info.name}: open which design?` });
                if (!picked) {
                    return;
                }
                if (picked.design.kind !== 'precision') {
                    await createPrecisionDesign(context, server, info.name, picked.design.name);
                    return;
                }
                design = picked.design.name;
            }
        }
        await ReportDesignerPanel.open(context, server, report, design);
    });
    command('xpp.reports.createPrecisionDesign', async (arg?: ReportNodeContext) => {
        const target = reportOf(arg);
        if (target.report && target.design) {
            await createPrecisionDesign(context, server, target.report, target.design);
        }
    });
    command('xpp.reports.addDataRegion', async (arg?: ReportNodeContext) => {
        const target = reportOf(arg);
        if (target.report) {
            await addDataRegion(server, target.report, target.design);
        }
    });
    command('xpp.reports.installRuntime', async () => {
        if (await ensureReportRuntime()) {
            vscode.window.showInformationMessage('The report preview runtime is installed. Copilot\'s report tools (xpp_render_report, xpp_compare_layout) use it too.');
        }
    });
    registerPanelSerializer<DesignerState>(context, 'xppReportDesigner', (panel, state) => ReportDesignerPanel.revive(panel, context, server, state));
}

/** Visual Studio's "Select a Report Data Provider" / "Select a Query" and "Select Fields": sets a dataset's Query, fields and parameters. */
export async function pickDataSetQuery(server: XppServerClient, report: string, dataSet: string, dataSourceType: string): Promise<boolean> {
    let source: string | undefined;
    if (dataSourceType === 'ReportDataProvider') {
        const providers = await server.lang<{ success: boolean; message?: string; classes?: string[] }>('report_providers', {});
        if (!providers.success) {
            throw new Error(`${providers.message ?? 'The cross-reference database is not available.'} The report data provider list comes from it.`);
        }
        const cls = await vscode.window.showQuickPick(providers.classes ?? [], { title: `${dataSet}: select a report data provider`, placeHolder: 'Classes that extend SrsReportDataProviderBase' });
        if (!cls) {
            return false;
        }
        const tables = await server.bridge<{ success: boolean; message?: string; tables?: string[] }>('report_datasource_schema', { dataSourceType, source: cls });
        if (!tables.success) {
            throw new Error(tables.message);
        }
        if (!tables.tables?.length) {
            vscode.window.showWarningMessage(`${cls} has no data tables: a report data provider returns them from methods with [SRSReportDataSetAttribute].`);
            return false;
        }
        source = tables.tables.length === 1 ? tables.tables[0] : await vscode.window.showQuickPick(tables.tables, { title: `${dataSet}: data table of ${cls}` });
    } else if (dataSourceType === 'Query' || dataSourceType === 'EnumProvider') {
        const picked = await pickElement(server, `${dataSet}: select ${dataSourceType === 'Query' ? 'a query' : 'an enum'}`, [dataSourceType === 'Query' ? 'AxQuery' : 'AxEnum']);
        source = picked?.objectName;
    } else {
        vscode.window.showInformationMessage(`F&O Studio picks the Query of Report Data Provider, Query and Enum Provider datasets. For ${dataSourceType}, type it in the property.`);
        return false;
    }
    if (!source) {
        return false;
    }
    let fields: string[] | undefined;
    if (dataSourceType !== 'EnumProvider') {
        const schema = await server.bridge<{ success: boolean; message?: string; fields?: { name: string; alias: string; type: string; label?: string }[] }>('report_datasource_schema', { dataSourceType, source });
        if (!schema.success) {
            throw new Error(schema.message);
        }
        const items = (schema.fields ?? []).map(f => ({ label: f.name, description: f.label ?? '', detail: `${f.alias} (${f.type.replace(/^System\./, '')})`, alias: f.alias, picked: true }));
        const chosen = await vscode.window.showQuickPick(items, { title: `${dataSet}: select fields of ${source}`, canPickMany: true, matchOnDetail: true });
        if (!chosen?.length) {
            return false;
        }
        fields = chosen.length === items.length ? [] : chosen.map(c => c.alias);
    }
    const result = await server.bridge('set_report_dataset_query', { objectType: 'AxReport', objectName: report, dataSet, dataSourceType, source, fields });
    if (!result.success) {
        throw new Error(result.message);
    }
    vscode.window.setStatusBarMessage(result.message ?? '', 8000);
    return true;
}

/** Visual Studio's Create Precision Design: Microsoft's generator turns the auto design into RDL, saved as a new precision design. */
async function createPrecisionDesign(context: vscode.ExtensionContext, server: XppServerClient, report: string, autoDesign: string): Promise<boolean> {
    const name = await vscode.window.showInputBox({
        title: `${report}.${autoDesign}: create a precision design`,
        prompt: 'Name of the new precision design. The auto design stays as it is.',
        value: `${autoDesign}Precision`,
        validateInput: v => DesignNamePattern.test(v) ? undefined : 'Use letters, digits and underscores.',
    });
    if (!name) {
        return false;
    }
    const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Generating ${report}.${name} from ${autoDesign}…` },
        () => server.bridge('create_precision_design', { objectType: 'AxReport', objectName: report, designName: autoDesign, newDesignName: name }));
    if (!result.success) {
        throw new Error(result.message);
    }
    ElementDesigner.refreshElement('AxReport', report);
    vscode.window.setStatusBarMessage(result.message ?? '', 8000);
    await ReportDesignerPanel.open(context, server, report, result.design ?? name);
    return true;
}

/** Visual Studio's drop of a dataset on an auto design: a table or list with the dataset's fields; on Designs it creates the auto design. */
async function addDataRegion(server: XppServerClient, report: string, design?: string): Promise<void> {
    const info = await server.bridge<ReportInfo>('report_info', { reportName: report });
    if (!info.success) {
        throw new Error(info.message);
    }
    if (!info.dataSets?.length) {
        vscode.window.showWarningMessage(`${info.name} has no datasets yet: add one under Data Sets first (data provider class, query or enum provider).`);
        return;
    }
    let designName = design;
    if (!designName) {
        const taken = new Set(info.designs.map(d => d.name.toLowerCase()));
        designName = await vscode.window.showInputBox({
            title: `${info.name}: new auto design`, prompt: 'Name of the auto design',
            value: taken.has('report') ? 'Report1' : 'Report',
            validateInput: v => !DesignNamePattern.test(v) ? 'Use letters, digits and underscores.' : taken.has(v.toLowerCase()) ? `${v} already exists.` : undefined,
        });
        if (!designName) {
            return;
        }
    }
    const dataSet = info.dataSets.length === 1 ? info.dataSets[0] : (await vscode.window.showQuickPick(
        info.dataSets.map(d => ({ label: d.name, description: `${d.fields.length} fields`, d })), { title: `${info.name}.${designName}: data set of the data region` }))?.d;
    if (!dataSet) {
        return;
    }
    const layout = await vscode.window.showQuickPick([
        { label: 'Table', description: 'one row per record, a column per field' },
        { label: 'List', description: 'fields side by side, a block per record' },
    ], { title: `${info.name}.${designName}: layout of ${dataSet.name}` });
    if (!layout) {
        return;
    }
    const result = await server.bridge('add_report_data_region', { objectType: 'AxReport', objectName: info.name, designName, dataSet: dataSet.name, layout: layout.label });
    if (!result.success) {
        throw new Error(result.message);
    }
    ElementDesigner.refreshElement('AxReport', info.name);
    vscode.window.setStatusBarMessage(result.message ?? '', 8000);
}

class ReportDesignerPanel {
    private static readonly panels = new Map<string, ReportDesignerPanel>();
    private dirty = false;
    private disposed = false;
    private isReadOnlyReport = false;

    private constructor(private readonly panel: vscode.WebviewPanel, private readonly context: vscode.ExtensionContext,
        private readonly server: XppServerClient, private state: DesignerState) {
        panel.webview.html = this.html();
        panel.iconPath = new vscode.ThemeIcon('graph');
        this.updateTitle();
        panel.webview.onDidReceiveMessage(message => this.onMessage(message).catch(err => {
            void this.post({ type: 'busy', busy: false });
            showReportableError('reportDesigner', err);
        }));
        panel.onDidDispose(() => {
            this.disposed = true;
            if (ReportDesignerPanel.panels.get(this.key) === this) {
                ReportDesignerPanel.panels.delete(this.key);
            }
            if (this.dirty) {
                vscode.window.showWarningMessage(`${this.state.report}${this.state.design ? '.' + this.state.design : ''}: the designer was closed with unsaved changes. Reopen it to restore them.`);
            }
        });
        ReportDesignerPanel.panels.set(this.key, this);
    }

    private get key(): string {
        return `${this.state.report}.${this.state.design ?? ''}`.toLowerCase();
    }

    /** Switches the panel to another design, keeping the open-panels map keyed on the design shown. */
    private setState(state: DesignerState): void {
        if (ReportDesignerPanel.panels.get(this.key) === this) {
            ReportDesignerPanel.panels.delete(this.key);
        }
        this.state = state;
        ReportDesignerPanel.panels.set(this.key, this);
        this.updateTitle();
    }

    static async open(context: vscode.ExtensionContext, server: XppServerClient, report: string, design?: string): Promise<void> {
        const existing = ReportDesignerPanel.panels.get(`${report}.${design ?? ''}`.toLowerCase());
        if (existing && !existing.disposed) {
            existing.panel.reveal();
            return;
        }
        const panel = vscode.window.createWebviewPanel('xppReportDesigner', report, vscode.ViewColumn.Active, {
            enableScripts: true,
            retainContextWhenHidden: true,
            localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, 'media')],
        });
        new ReportDesignerPanel(panel, context, server, { report, design });
    }

    static revive(panel: vscode.WebviewPanel, context: vscode.ExtensionContext, server: XppServerClient, state: DesignerState): void {
        new ReportDesignerPanel(panel, context, server, state);
    }

    private updateTitle(): void {
        this.panel.title = `${this.dirty ? '● ' : ''}${this.state.report}${this.state.design ? '.' + this.state.design : ''} [Report]`;
    }

    private post(message: object): Thenable<boolean> {
        return this.disposed ? Promise.resolve(false) : this.panel.webview.postMessage(message);
    }

    private get draftKey(): string {
        return `xpp.reportDraft.${this.key}`;
    }

    private async onMessage(message: any): Promise<void> {
        switch (message.type) {
            case 'ready':
                await this.load();
                break;
            case 'openDesign': {
                const existing = ReportDesignerPanel.panels.get(`${this.state.report}.${message.design}`.toLowerCase());
                if (existing && existing !== this && !existing.disposed) {
                    existing.panel.reveal();
                    break;
                }
                if (this.dirty && await vscode.window.showWarningMessage(`${this.state.report}.${this.state.design} has unsaved changes.`, { modal: true }, 'Discard and Switch') !== 'Discard and Switch') {
                    break;
                }
                await this.context.workspaceState.update(this.draftKey, undefined);
                this.dirty = false;
                this.setState({ report: this.state.report, design: message.design });
                await this.load();
                break;
            }
            case 'dirty':
                this.dirty = !!message.dirty;
                await this.context.workspaceState.update(this.draftKey, this.dirty ? message.rdl : undefined);
                this.updateTitle();
                break;
            case 'save':
                await this.save(message.rdl);
                break;
            case 'createDesign':
                await this.createDesign(message.name);
                break;
            case 'convertDesign':
                if (this.isReadOnlyReport) {
                    vscode.window.showInformationMessage(`${this.state.report} belongs to a model you can't change: duplicate it into your model first.`);
                } else {
                    await createPrecisionDesign(this.context, this.server, this.state.report, message.design);
                }
                break;
            case 'preview':
                await this.preview(message);
                break;
            case 'pickScan':
                await this.pickImage('scan', 'Scanned form (whole sheet)');
                break;
            case 'pickImage':
                await this.pickImage('image', 'Image to embed in the report');
                break;
            case 'viewRdl':
                await vscode.window.showTextDocument(await vscode.workspace.openTextDocument({ language: 'xml', content: message.rdl }));
                break;
            case 'duplicate':
                await vscode.commands.executeCommand('xpp.duplicateObject', { item: { objectType: 'AxReport', objectName: this.state.report } });
                break;
            case 'properties':
                await vscode.commands.executeCommand('xpp.openDesigner', 'AxReport', this.state.report);
                break;
            case 'installRuntime':
                await vscode.commands.executeCommand('xpp.reports.installRuntime');
                break;
            case 'error':
                vscode.window.showErrorMessage(message.message);
                break;
            case 'info':
                vscode.window.showInformationMessage(message.message);
                break;
        }
    }

    private async load(): Promise<void> {
        await this.post({ type: 'busy', busy: true, text: 'Loading the report…' });
        const info = await this.server.bridge<ReportInfo>('report_info', { reportName: this.state.report });
        if (!info.success) {
            throw new Error(info.message ?? `Report ${this.state.report} not found.`);
        }
        this.isReadOnlyReport = !info.isCustom;
        const precision = info.designs.filter(d => d.kind === 'precision');
        if (!this.state.design && precision.length > 0) {
            const existing = ReportDesignerPanel.panels.get(`${info.name}.${precision[0].name}`.toLowerCase());
            if (existing && existing !== this && !existing.disposed) {
                // That design is already open: show it there instead of a second editor on the same RDL.
                existing.panel.reveal();
                this.panel.dispose();
                return;
            }
            this.setState({ report: info.name, design: precision[0].name });
        }
        let design: any;
        if (this.state.design && precision.some(d => d.name.toLowerCase() === this.state.design!.toLowerCase())) {
            design = await this.server.bridge('get_report_design', { reportName: info.name, designName: this.state.design });
            if (!design.success) {
                throw new Error(design.message);
            }
            const draft = this.context.workspaceState.get<string>(this.draftKey);
            if (draft && draft !== design.rdl) {
                const restore = await vscode.window.showInformationMessage(`${info.name}.${this.state.design} has unsaved changes from an earlier session.`, 'Restore Changes', 'Discard');
                if (restore === 'Restore Changes') {
                    design.draft = draft;
                } else {
                    await this.context.workspaceState.update(this.draftKey, undefined);
                }
            }
        }
        await this.post({ type: 'load', info, design, state: this.state });
    }

    private async save(rdl: string): Promise<void> {
        await this.post({ type: 'busy', busy: true, text: 'Saving…' });
        const result = await this.server.bridge('set_report_design', { objectType: 'AxReport', objectName: this.state.report, designName: this.state.design, rdl });
        await this.post({ type: 'saved', success: result.success, message: result.message, warnings: result.warnings ?? [] });
        if (!result.success) {
            throw new Error(result.message);
        }
        this.dirty = false;
        await this.context.workspaceState.update(this.draftKey, undefined);
        this.updateTitle();
        ElementDesigner.refreshElement('AxReport', this.state.report);
        vscode.window.setStatusBarMessage(`${result.message} Build the model to compile the report.`, 8000);
    }

    private async createDesign(name: string): Promise<void> {
        const result = await this.server.bridge('set_report_design', { objectType: 'AxReport', objectName: this.state.report, designName: name, rdl: blankDesign() });
        if (!result.success) {
            throw new Error(result.message);
        }
        this.setState({ report: this.state.report, design: name });
        ElementDesigner.refreshElement('AxReport', this.state.report);
        await this.load();
    }

    private async preview(message: { rdl: string; data?: object; sampleRows?: number; language?: string; company?: string; maxPages?: number; dpi?: number; firstPage?: number }): Promise<void> {
        if (!await ensureReportRuntime()) {
            await this.post({ type: 'preview', success: false, message: 'The preview needs Microsoft\'s report engine (Install Report Preview Runtime).' });
            return;
        }
        await this.post({ type: 'busy', busy: true, text: 'Rendering with the report engine…' });
        const result = await this.server.bridge('render_report', {
            reportName: this.state.report, designName: this.state.design, rdl: message.rdl, data: message.data, sampleRows: message.sampleRows ?? 1,
            language: message.language || 'en-US', company: message.company || 'USMF', firstPage: message.firstPage ?? 1,
            maxPages: Math.min(Math.max(message.maxPages ?? 3, 1), 20), dpi: message.dpi ?? 110,
        });
        await this.post({ type: 'preview', ...result });
    }

    private async pickImage(kind: 'scan' | 'image', title: string): Promise<void> {
        const picked = await vscode.window.showOpenDialog({ title, filters: ImageFilters, canSelectMany: false });
        if (!picked?.[0]) {
            return;
        }
        const file = picked[0].fsPath;
        const mime = MimeTypes[path.extname(file).toLowerCase()] ?? 'image/png';
        const data = (await fs.readFile(file)).toString('base64');
        await this.post({ type: kind, name: path.basename(file, path.extname(file)), path: file, mimeType: mime, data });
    }

    private html(): string {
        const webview = this.panel.webview;
        const nonce = randomBytes(16).toString('base64');
        const media = (file: string) => webview.asWebviewUri(vscode.Uri.joinPath(this.context.extensionUri, 'media', file));
        return `<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data:; style-src ${webview.cspSource}; font-src ${webview.cspSource}; script-src 'nonce-${nonce}';">
<meta name="viewport" content="width=device-width, initial-scale=1.0">
<link href="${media('codicons/codicon.css')}" rel="stylesheet">
<link href="${media('reportDesigner.css')}" rel="stylesheet">
<link href="${media('a11y.css')}" rel="stylesheet">
</head>
<body${stateAttribute(this.state)}>
<div id="app" class="app"><div class="loading"><span class="codicon codicon-loading codicon-modifier-spin" aria-hidden="true"></span> Loading…</div></div>
<script nonce="${nonce}" src="${media('reportDesigner.js')}"></script>
</body>
</html>`;
    }
}
