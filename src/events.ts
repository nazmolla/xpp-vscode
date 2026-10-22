import * as vscode from 'vscode';

/** One event of an element, a form part or a method, as Visual Studio's Events nodes show them. */
export interface EventSpec {
    kind: 'Data' | 'Form' | 'FormDataSource' | 'FormDataField' | 'FormControl' | 'Pre' | 'Post' | 'Delegate';
    /** Element the handler attribute names (base element for extensions). */
    element: string;
    /** AxTable, AxDataEntityView, AxForm, AxClass, ... (of the base element). */
    elementType: string;
    dataSource?: string;
    field?: string;
    control?: string;
    method?: string;
    isStatic?: boolean;
    /** Delegate parameters, e.g. "Common _record, EventHandlerResult _result". */
    parameters?: string;
    /** DataEventType / FormEventType / ... value, e.g. Inserted. */
    event?: string;
}

/** The platform's event enums (Dynamics.AX.Application.*EventType in Microsoft.Dynamics.AX.Xpp.Support). */
const TableEvents = ['Deleted', 'Deleting', 'InitializedRecord', 'InitializingRecord', 'Inserted', 'Inserting', 'ModifiedField',
    'ModifiedFieldValue', 'ModifyingField', 'ModifyingFieldValue', 'Updated', 'Updating', 'ValidatedDelete', 'ValidatedField',
    'ValidatedFieldValue', 'ValidatedWrite', 'ValidatingDelete', 'ValidatingField', 'ValidatingFieldValue', 'ValidatingWrite',
    'DefaultingField', 'DefaultedField', 'DefaultingRow', 'DefaultedRow'];
const EntityEvents = ['PersistingEntity', 'PersistedEntity', 'InitializingEntityDataSource', 'InitializedEntityDataSource',
    'MappingEntityToDataSource', 'MappedEntityToDataSource', 'MappingDataSourceToEntity', 'MappedDataSourceToEntity',
    'FindingEntityDataSource', 'FoundEntityDataSource', 'InsertingEntityDataSource', 'InsertedEntityDataSource',
    'UpdatingEntityDataSource', 'UpdatedEntityDataSource', 'DeletingEntityDataSource', 'DeletedEntityDataSource', 'PostingLoad', 'PostedLoad'];
const FormEvents = ['Activated', 'Closing', 'Initialized', 'Initializing', 'PostRun'];
const DataSourceEvents = ['Activated', 'Created', 'Creating', 'Deleted', 'Deleting', 'DisplayOptionInitialize', 'Initialized', 'InitValue',
    'LeavingRecord', 'LeftRecord', 'MarkChanged', 'PostLinkActive', 'QueryExecuted', 'QueryExecuting', 'Refreshed', 'Reread',
    'SelectionChanged', 'ValidatedDelete', 'ValidatedWrite', 'ValidatingDelete', 'ValidatingWrite', 'Writing', 'Written'];
const DataFieldEvents = ['JumpRef', 'Modified', 'Validated', 'Validating'];
const FocusEvents = ['Enter', 'GotFocus', 'Leaving', 'LostFocus'];
const InputEvents = ['JumpRef', 'Lookup', 'Modified', 'Validated', 'Validating', ...FocusEvents];

function controlEvents(type: string): string[] {
    if (/Button/i.test(type)) { return ['Clicked', ...FocusEvents]; }
    if (/TabPage/i.test(type)) { return ['AllowPageDeactivate', 'PageActivated', ...FocusEvents]; }
    if (/Tab/i.test(type)) { return ['TabChanged', ...FocusEvents]; }
    if (/Tree/i.test(type)) { return ['Expanded', 'Expanding', 'SelectionChanged', 'SelectionChanging', ...FocusEvents]; }
    if (/Grid|List/i.test(type)) { return ['SelectionChanged', 'SelectionChanging', ...FocusEvents]; }
    if (/String|Int|Real|Date|Time|Guid|ComboBox|CheckBox|Radio|Segmented|ReferenceGroup|Enum/i.test(type)) { return InputEvents; }
    return FocusEvents;
}

interface TreeNode {
    kind: string;
    type?: string;
    name: string;
    path: string[];
    children: TreeNode[];
    eventSpec?: EventSpec;
}

const BaseTypes: Record<string, string> = {
    AxTableExtension: 'AxTable', AxFormExtension: 'AxForm', AxDataEntityViewExtension: 'AxDataEntityView',
};

/** Adds Events folders to an element tree, like Visual Studio's designer: element, form data sources, data source fields and controls. */
export function addEventNodes(objectType: string, objectName: string, root: TreeNode): void {
    const elementType = BaseTypes[objectType] ?? objectType;
    const element = objectName.split('.')[0];
    const folder = (owner: TreeNode, specs: EventSpec[]): void => {
        if (specs.length === 0) { return; }
        const path = [...owner.path, 'Events'];
        // Right after Methods, like Visual Studio (Methods, Events, then the rest).
        const methods = owner.children.findIndex(c => c.kind === 'folder' && c.type === 'Methods');
        owner.children.splice(methods >= 0 ? methods + 1 : owner.children.length, 0, {
            kind: 'folder', type: 'Events', name: 'Events', path,
            children: specs.map(spec => ({ kind: 'event', type: 'Event', name: eventName(spec), path: [...path, eventName(spec)], children: [], eventSpec: spec })),
        });
    };
    if (elementType === 'AxTable') {
        folder(root, TableEvents.map(event => ({ kind: 'Data', element, elementType, event })));
    } else if (elementType === 'AxDataEntityView') {
        folder(root, [...TableEvents, ...EntityEvents].map(event => ({ kind: 'Data', element, elementType, event })));
    } else if (elementType === 'AxForm') {
        if (objectType === 'AxForm') {
            folder(root, FormEvents.map(event => ({ kind: 'Form', element, elementType, event })));
        }
        const walk = (node: TreeNode, parent?: TreeNode, grand?: TreeNode): void => {
            for (const child of [...node.children]) {
                walk(child, node, parent);
            }
            if (node.kind !== 'item' || !parent || node.path.length < 2) { return; }
            const p = node.path;
            if (p[0] === 'DataSources' && p.length === 2) {
                folder(node, DataSourceEvents.map(event => ({ kind: 'FormDataSource', element, elementType, dataSource: p[1], event })));
            } else if (p[0] === 'DataSources' && p.length === 4 && p[2] === 'Fields') {
                folder(node, DataFieldEvents.map(event => ({ kind: 'FormDataField', element, elementType, dataSource: p[1], field: p[3], event })));
            } else if (parent.type === 'Controls' && grand) {
                folder(node, controlEvents(node.type ?? '').map(event => ({ kind: 'FormControl', element, elementType, control: node.name, event })));
            }
        };
        walk(root);
    }
}

function eventName(spec: EventSpec): string {
    return spec.kind === 'Data' ? `on${spec.event}` : `On${spec.event}`;
}

const Str: Record<string, { element: string; method: string; staticMethod: string }> = {
    AxClass: { element: 'classStr', method: 'methodStr', staticMethod: 'staticMethodStr' },
    AxTable: { element: 'tableStr', method: 'tableMethodStr', staticMethod: 'tableStaticMethodStr' },
    AxDataEntityView: { element: 'tableStr', method: 'tableMethodStr', staticMethod: 'tableStaticMethodStr' },
    AxView: { element: 'tableStr', method: 'tableMethodStr', staticMethod: 'tableStaticMethodStr' },
    AxForm: { element: 'formStr', method: 'formMethodStr', staticMethod: 'formMethodStr' },
};

/** The handler method Visual Studio's "Copy event handler method" puts on the clipboard. */
export function handlerText(spec: EventSpec): string {
    const e = spec.element;
    const doc = (params: string[]) => ['/// <summary>', '///', '/// </summary>', ...params.map(p => `/// <param name="${p}"></param>`)];
    const method = (attribute: string, name: string, params: string, paramNames: string[]) =>
        [...doc(paramNames), `[${attribute}]`, `public static void ${name}(${params})`, '{', '', '}', ''].join('\n');
    const str = Str[spec.elementType] ?? Str.AxClass;
    switch (spec.kind) {
        case 'Data':
            return method(`DataEventHandler(tableStr(${e}), DataEventType::${spec.event})`, `${e}_on${spec.event}`, 'Common sender, DataEventArgs e', ['sender', 'e']);
        case 'Form':
            return method(`FormEventHandler(formStr(${e}), FormEventType::${spec.event})`, `${e}_On${spec.event}`, 'xFormRun sender, FormEventArgs e', ['sender', 'e']);
        case 'FormDataSource':
            return method(`FormDataSourceEventHandler(formDataSourceStr(${e}, ${spec.dataSource}), FormDataSourceEventType::${spec.event})`,
                `${spec.dataSource}_On${spec.event}`, 'FormDataSource sender, FormDataSourceEventArgs e', ['sender', 'e']);
        case 'FormDataField':
            return method(`FormDataFieldEventHandler(formDataFieldStr(${e}, ${spec.dataSource}, ${spec.field}), FormDataFieldEventType::${spec.event})`,
                `${spec.field}_On${spec.event}`, 'FormDataObject sender, FormDataFieldEventArgs e', ['sender', 'e']);
        case 'FormControl':
            return method(`FormControlEventHandler(formControlStr(${e}, ${spec.control}), FormControlEventType::${spec.event})`,
                `${spec.control}_On${spec.event}`, 'FormControl sender, FormControlEventArgs e', ['sender', 'e']);
        case 'Pre':
        case 'Post': {
            const methodStr = spec.isStatic ? str.staticMethod : str.method;
            return method(`${spec.kind}HandlerFor(${str.element}(${e}), ${methodStr}(${e}, ${spec.method}))`,
                `${e}_${spec.kind}_${spec.method}`, 'XppPrePostArgs args', ['args']);
        }
        case 'Delegate': {
            const params = spec.parameters ?? '';
            const names = params.split(',').map(p => p.trim().split(/\s+/).pop() ?? '').filter(Boolean);
            return method(`SubscribesTo(${str.element}(${e}), delegateStr(${e}, ${spec.method}))`, `${e}_${spec.method}`, params, names);
        }
    }
}

/** The handled element path in the extension index (ExtensionIndex.TargetPath), and the event filter. */
export function handlerQuery(spec: EventSpec, wholeElement = false): { path: string; event?: string; includeChildren: boolean } {
    const folder = spec.elementType === 'AxClass' ? 'Classes'
        : spec.elementType === 'AxForm' ? 'Forms' : 'Tables';
    const base = `/${folder}/${spec.element}`;
    switch (spec.kind) {
        case 'FormDataSource': return { path: `${base}/DataSources/${spec.dataSource}`, event: wholeElement ? undefined : spec.event, includeChildren: wholeElement };
        case 'FormDataField': return { path: `${base}/DataSources/${spec.dataSource}/Fields/${spec.field}`, event: wholeElement ? undefined : spec.event, includeChildren: false };
        case 'FormControl': return { path: `${base}/Controls/${spec.control}`, event: wholeElement ? undefined : spec.event, includeChildren: false };
        case 'Pre':
        case 'Post':
        case 'Delegate': return { path: `/${spec.elementType === 'AxClass' ? 'Classes' : folder}/${spec.element}/Methods/${spec.method}`, event: wholeElement ? undefined : spec.kind, includeChildren: false };
        default: return { path: base, event: wholeElement ? undefined : spec.event, includeChildren: wholeElement };
    }
}

/** Copies the handler to the clipboard (Visual Studio's "Copy event handler method"). */
export async function copyEventHandler(spec: EventSpec): Promise<void> {
    await vscode.env.clipboard.writeText(handlerText(spec));
    vscode.window.setStatusBarMessage(`$(clippy) Event handler for ${eventLabel(spec)} copied`, 4000);
}

export function eventLabel(spec: EventSpec): string {
    const owner = [spec.element, spec.dataSource, spec.field, spec.control, spec.method].filter(Boolean).join('.');
    return `${owner} ${spec.kind === 'Pre' || spec.kind === 'Post' ? spec.kind + '-event' : spec.kind === 'Delegate' ? 'delegate' : spec.event}`;
}
