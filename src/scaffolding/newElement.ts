import * as vscode from 'vscode';
import { elementOpenCommand, ExtensionBaseTypes, iconFor, typeName } from '../aot';
import { addCreatedToProject, pickElement, targetModel } from '../d365Tools';
import { XppServerClient } from '../serverClient';
import { NamePattern } from './xpp';

interface Kind {
    objectType: string;
    group: string;
    detail: string;
    /** Commands of the wizards, or of the code extension flow, instead of a plain element. */
    command?: string;
    label?: string;
}

const Kinds: Kind[] = [
    { group: 'Code', objectType: 'AxClass', detail: 'X++ class' },
    { group: 'Code', objectType: 'AxClass', label: 'Code extension (Chain of Command)', command: 'xpp.createCodeExtension', detail: 'final class wrapping a class, table or form' },
    { group: 'Data model', objectType: 'AxTable', detail: 'table with fields, indexes and relations' },
    { group: 'Data model', objectType: 'AxTableExtension', detail: 'add fields, indexes and field groups to a table' },
    { group: 'Data model', objectType: 'AxView', detail: 'view over tables' },
    { group: 'Data model', objectType: 'AxQuery', detail: 'reusable query for forms, reports and batch jobs' },
    { group: 'Data model', objectType: 'AxDataEntityViewExtension', detail: 'add fields to a data entity (and its staging table)' },
    { group: 'Data types', objectType: 'AxEnum', detail: 'enumeration' },
    { group: 'Data types', objectType: 'AxEdt', detail: 'extended data type: string, integer, real, date…' },
    { group: 'Data types', objectType: 'AxEnumExtension', detail: 'add values to an extensible enum' },
    { group: 'Data types', objectType: 'AxEdtExtension', detail: 'change label, help text or size of an EDT' },
    { group: 'User interface', objectType: 'AxForm', detail: 'form' },
    { group: 'User interface', objectType: 'AxFormExtension', detail: 'add controls and data sources to a form' },
    { group: 'User interface', objectType: 'AxMenuItemDisplay', detail: 'opens a form' },
    { group: 'User interface', objectType: 'AxMenuItemOutput', detail: 'runs a report' },
    { group: 'User interface', objectType: 'AxMenuItemAction', detail: 'runs a class' },
    { group: 'User interface', objectType: 'AxMenu', detail: 'menu' },
    { group: 'User interface', objectType: 'AxTile', detail: 'workspace tile' },
    { group: 'Security', objectType: 'AxSecurityPrivilege', detail: 'grants menu items and data entities' },
    { group: 'Security', objectType: 'AxSecurityDuty', detail: 'groups privileges' },
    { group: 'Security', objectType: 'AxSecurityRole', detail: 'groups duties' },
    { group: 'Security', objectType: 'AxSecurityDutyExtension', detail: 'add privileges to a standard duty' },
    { group: 'Security', objectType: 'AxSecurityRoleExtension', detail: 'add duties to a standard role' },
    { group: 'Configuration and services', objectType: 'AxConfigurationKey', detail: 'switches a feature on or off' },
    { group: 'Configuration and services', objectType: 'AxService', detail: 'service with operations' },
    { group: 'Configuration and services', objectType: 'AxServiceGroup', detail: 'publishes services' },
    { group: 'Wizards', objectType: 'AxClass', label: 'Batch job (SysOperation)', command: 'xpp.newSysOperationJob', detail: 'contract, service, controller, menu item, privilege' },
    { group: 'Wizards', objectType: 'AxReport', label: 'Report', command: 'xpp.newReport', detail: 'temporary table, data provider, contract, controller, design' },
    { group: 'Wizards', objectType: 'AxWorkflowTemplate', label: 'Approval workflow', command: 'xpp.newWorkflow', detail: 'workflow type, approval, handlers, menu items' },
    { group: 'Wizards', objectType: 'AxService', label: 'Custom service', command: 'xpp.newService', detail: 'service class, contracts, service group, privilege' },
    { group: 'Wizards', objectType: 'AxClass', label: 'Business event', command: 'xpp.newBusinessEvent', detail: 'contract, event class, table trigger' },
    { group: 'Wizards', objectType: 'AxEdt', label: 'Number sequence', command: 'xpp.addNumberSequence', detail: 'EDT, module reference, parameters method, table default' },
    { group: 'Wizards', objectType: 'AxClass', label: 'Feature (Feature management)', command: 'xpp.newFeature', detail: 'IFeatureMetadata class with isEnabled()' },
];

const EdtTypes = ['String', 'Int', 'Int64', 'Real', 'Date', 'UtcDateTime', 'Enum', 'Time', 'Guid', 'Container'];
const RecentKey = 'xpp.newElement.recent';

type KindItem = vscode.QuickPickItem & { element?: Kind };

const labelOf = (k: Kind) => k.label ?? typeName(k.objectType);
const keyOf = (k: Kind) => k.command ?? k.objectType;

/** Visual Studio's Add > New Item: pick a type, name it; the element is created in the project's model, added and opened. */
export async function newElement(context: vscode.ExtensionContext, server: XppServerClient, refreshTrees: () => void, objectType?: string): Promise<void> {
    const kind = (objectType && Kinds.find(k => k.objectType === objectType && !k.command)) || await pickKind(context);
    if (!kind) {
        return;
    }
    void context.globalState.update(RecentKey, [keyOf(kind), ...context.globalState.get<string[]>(RecentKey, []).filter(k => k !== keyOf(kind))].slice(0, 5));
    if (kind.command) {
        await vscode.commands.executeCommand(kind.command);
        return;
    }
    const base = ExtensionBaseTypes[kind.objectType];
    if (base) {
        const element = await pickElement(server, `New ${typeName(kind.objectType).toLowerCase()}: element to extend`, [base]);
        if (element) {
            await vscode.commands.executeCommand('xpp.createExtension', { objectType: element.objectType, entry: { name: element.objectName } });
        }
        return;
    }
    const model = await targetModel(`New ${typeName(kind.objectType)}`);
    if (!model) {
        return;
    }
    const name = await askFreeName(server, kind.objectType, model);
    if (!name) {
        return;
    }
    const body: Record<string, unknown> = {};
    if (kind.objectType === 'AxClass') {
        body.declaration = `class ${name}\n{\n}`;
    } else if (kind.objectType === 'AxEdt') {
        const edtType = await vscode.window.showQuickPick(EdtTypes, { title: `New extended data type ${name}: base type`, ignoreFocusOut: true });
        if (!edtType) {
            return;
        }
        body.properties = { EdtType: edtType };
    }
    const result = await server.bridge('create_object', { objectType: kind.objectType, objectName: name, modelName: model, ...body });
    if (!result.success) {
        throw new Error(result.message);
    }
    await addCreatedToProject(server, kind.objectType, name);
    refreshTrees();
    const open = elementOpenCommand(kind.objectType, name);
    await vscode.commands.executeCommand(open.command, ...(open.arguments ?? []));
}

async function pickKind(context: vscode.ExtensionContext): Promise<Kind | undefined> {
    const recent = context.globalState.get<string[]>(RecentKey, []).map(key => Kinds.find(k => keyOf(k) === key)).filter((k): k is Kind => !!k);
    const item = (k: Kind): KindItem => ({ label: `$(${k.group === 'Wizards' ? 'sparkle' : iconFor(k.objectType)}) ${labelOf(k)}`, detail: k.detail, element: k });
    const items: KindItem[] = [];
    if (recent.length) {
        items.push({ label: 'Recently used', ...separator() }, ...recent.map(item));
    }
    for (const group of [...new Set(Kinds.map(k => k.group))]) {
        items.push({ label: group, ...separator() }, ...Kinds.filter(k => k.group === group).map(item));
    }
    const picked = await vscode.window.showQuickPick(items, { title: 'New Element', placeHolder: 'Type of the new element', matchOnDetail: true });
    return picked?.element;
}

function separator(): Pick<vscode.QuickPickItem, 'kind'> {
    return { kind: vscode.QuickPickItemKind.Separator };
}

/** Asks for the name and checks, while typing, that no element of that type has it yet. */
function askFreeName(server: XppServerClient, objectType: string, model: string): Promise<string | undefined> {
    return new Promise(resolve => {
        const input = vscode.window.createInputBox();
        input.title = `New ${typeName(objectType)} in ${model}`;
        input.prompt = 'Name with your prefix';
        input.ignoreFocusOut = true;
        let timer: NodeJS.Timeout | undefined;
        let taken = false;
        let done = false;
        const check = (value: string) => {
            clearTimeout(timer);
            taken = false;
            const name = value.trim();
            if (!name) {
                input.validationMessage = undefined;
                return;
            }
            if (!NamePattern.test(name)) {
                input.validationMessage = 'Letters, digits and _, starting with a letter.';
                return;
            }
            input.validationMessage = undefined;
            timer = setTimeout(async () => {
                input.busy = true;
                const found = await server.bridge('find_object', { objectName: name, objectType, exactMatch: true, maxResults: 1 }).catch(() => undefined);
                input.busy = false;
                if (input.value.trim() === name && found?.matches?.length) {
                    taken = true;
                    input.validationMessage = `${found.matches[0].objectName} already exists in ${found.matches[0].modelName}.`;
                }
            }, 250);
        };
        input.onDidChangeValue(check);
        input.onDidAccept(() => {
            const name = input.value.trim();
            if (!name || taken || !NamePattern.test(name)) {
                return;
            }
            done = true;
            resolve(name);
            input.hide();
        });
        input.onDidHide(() => {
            clearTimeout(timer);
            if (!done) {
                resolve(undefined);
            }
            input.dispose();
        });
        input.show();
    });
}
