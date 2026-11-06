import * as vscode from 'vscode';
import { baseElementOf, elementOpenCommand } from './aot';
import { elementOf, TreeArg } from './d365Tools';
import { XppServerClient } from './serverClient';

/** An element a designer node or property refers to; label references carry the label ID. */
export interface ElementReference {
    objectType: string;
    objectName: string;
    labelId?: string;
}

/**
 * Keys of the references the server reports for designer nodes (MetaModelBridge.References.cs). The designer's context
 * menu offers Go to Referenced Element when a node or property has any; xpp.goTo.<key> opens one directly.
 */
export const GoToKeys = [
    'ExtendedDataType', 'EnumType', 'BaseEdt', 'BaseTable', 'BaseObject', 'Table', 'RelatedTable', 'ReferenceTable', 'MappingTable',
    'PrimaryTable', 'StagingTable', 'MenuItem', 'Form', 'Class', 'Report', 'Query', 'Part', 'Document', 'DocumentMenuItem', 'ActionMenuItem',
    'SubmitToWorkflowMenuItem', 'ResubmitMenuItem', 'DelegateMenuItem', 'CancelMenuItem', 'FormRef', 'ListPageRef', 'ReportRef',
    'PreviewPartRef', 'HyperLinkMenuItem', 'SourceEDT', 'EnumTypeParameter', 'Privilege', 'Duty', 'Role', 'DataEntity', 'Service', 'Menu',
    'Tile', 'KPI', 'WorkflowElement', 'WorkflowType', 'WorkflowCategory', 'ProviderClass', 'PresenceClass', 'ControlClass', 'InteractionClass',
    'FeatureClass', 'StartedEventHandler', 'CompletedEventHandler', 'CanceledEventHandler', 'WorkItemsCreatedEventHandler',
    'ConfigDataChangeEventHandler', 'ExecutionEventHandler', 'EventHandler', 'ConfigurationKey', 'CountryConfigurationKey', 'ParentKey',
    'LicenseCode', 'Label',
] as const;

/** A designer row's right-click context: its references by key, as JSON. */
interface GoToContext {
    xppRefs?: string;
}

export async function openElementReference(ref: ElementReference): Promise<void> {
    if (ref.objectType === 'AxLabelFile') {
        await vscode.commands.executeCommand('xpp.openDesigner', ref.objectType, ref.objectName, ref.labelId);
        return;
    }
    const open = elementOpenCommand(ref.objectType, ref.objectName);
    await vscode.commands.executeCommand(open.command, ...(open.arguments ?? []));
}

export function registerGoTo(command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient): void {
    // The per-reference commands stay for keybindings; menus show the one picker below.
    for (const key of GoToKeys) {
        command(`xpp.goTo.${key}`, (arg?: GoToContext & TreeArg) => goTo(key, arg));
    }
    command('xpp.goTo.referenced', (arg?: GoToContext & TreeArg) => goToReferenced(arg));
    command('xpp.openReference', (types: string[], name: string, labelId?: string) => openNamedElement(server, types, name, labelId));
}

/** Go to Referenced Element: the node's or property's references; one opens directly, several in a picker. */
async function goToReferenced(arg?: GoToContext & TreeArg): Promise<void> {
    const refs: Record<string, ElementReference> = arg?.xppRefs ? JSON.parse(arg.xppRefs) : {};
    const entries = Object.entries(refs);
    if (entries.length === 1) {
        await openElementReference(entries[0][1]);
        return;
    }
    const picked = await vscode.window.showQuickPick(entries.map(([key, ref]) => ({
        label: ref.objectType === 'AxLabelFile' ? `@${ref.objectName}:${ref.labelId}` : ref.objectName,
        description: key.replace(/([a-z])([A-Z])/g, '$1 $2'),
        ref,
    })), { title: 'Go to referenced element' });
    if (picked) {
        await openElementReference(picked.ref);
    }
}

/** Opens the first existing element among the candidate types (links in code: tableStr(X) may name a table, view or entity). */
async function openNamedElement(server: XppServerClient, types: string[], name: string, labelId?: string): Promise<void> {
    if (types[0] === 'AxLabelFile') {
        return openElementReference({ objectType: 'AxLabelFile', objectName: name, labelId });
    }
    const result = await server.bridge('find_object', { objectName: name, exactMatch: true, maxResults: 20 });
    const matches: { objectType: string; objectName: string }[] = result?.matches ?? [];
    const found = types.map(t => matches.find(m => m.objectType === t)).find(m => m);
    if (!found) {
        vscode.window.showInformationMessage(`${name} was not found.`);
        return;
    }
    await openElementReference(found);
}

async function goTo(key: string, arg?: GoToContext & TreeArg): Promise<void> {
    const refs: Record<string, ElementReference> = arg?.xppRefs ? JSON.parse(arg.xppRefs) : {};
    // Application Explorer and project items have no references of their own; an extension's base is known from its name.
    const element = elementOf(arg);
    const ref = refs[key] ?? (key === 'BaseObject' && element ? baseElementOf(element.objectType, element.objectName) : undefined);
    if (ref) {
        await openElementReference(ref);
    }
}
