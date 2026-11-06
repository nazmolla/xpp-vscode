import * as vscode from 'vscode';
import { XppServerClient } from '../serverClient';
import { askName, askText, createElements, labelFor, NamePattern, projectModel } from './common';
import { businessEventElements, EventField, featureElements, numberSequenceElements, numberSequenceInitValue } from './lifecycle';

/** Modules whose number sequence references a wizard can extend: the module class and its parameters table. */
const NumberSeqModules: { label: string; moduleClass: string; parametersTable: string }[] = [
    { label: 'Accounts receivable', moduleClass: 'NumberSeqModuleCustomer', parametersTable: 'CustParameters' },
    { label: 'Accounts payable', moduleClass: 'NumberSeqModuleVendor', parametersTable: 'VendParameters' },
    { label: 'Inventory management', moduleClass: 'NumberSeqModuleInventory', parametersTable: 'InventParameters' },
    { label: 'Sales and marketing', moduleClass: 'NumberSeqModuleSalesOrder', parametersTable: 'SalesParameters' },
    { label: 'Procurement and sourcing', moduleClass: 'NumberSeqModulePurchaseOrder', parametersTable: 'PurchParameters' },
    { label: 'Project management', moduleClass: 'NumberSeqModuleProject', parametersTable: 'ProjParameters' },
    { label: 'General ledger', moduleClass: 'NumberSeqModuleLedger', parametersTable: 'LedgerParameters' },
];

async function enumValues(server: XppServerClient, name: string): Promise<string[]> {
    const read = await server.bridge('read_object', { objectType: 'AxEnum', objectName: name });
    return ((read.enumValues ?? []) as { name?: string; Name?: string }[]).map(v => v.name ?? v.Name ?? '').filter(Boolean);
}

/** F&O Studio: New Business Event: contract, event class and the table trigger, for a table and the fields it carries. */
export async function newBusinessEvent(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const table = await askText('New business event: table', 'The table whose records the event is about', 'CustTable', v => NamePattern.test(v.trim()) ? undefined : 'A table name.');
    if (!table) {
        return;
    }
    const shape = await server.bridge('get_table_fields', { tableName: table });
    if (!shape.success) {
        throw new Error(shape.message ?? `Table ${table} not found.`);
    }
    const picked = await vscode.window.showQuickPick(((shape.fields ?? []) as EventField[]).map(f => ({ label: f.name, description: f.edt || f.enumType || f.type, field: f })),
        { title: `New business event: fields of ${shape.table} the event carries`, canPickMany: true, ignoreFocusOut: true });
    if (!picked?.length) {
        return;
    }
    const trigger = await vscode.window.showQuickPick([
        { label: 'When a record is created', trigger: 'insert' as const },
        { label: 'When a record is changed', trigger: 'update' as const },
    ], { title: 'New business event: when is it sent?' });
    const base = trigger && await askName('New business event: name', 'Base name with your prefix; the classes get BusinessEvent and BusinessEventContract appended', `ABC${shape.table}Created`);
    const caption = base && await askText('New business event: name in the catalog', 'What users see in the business event catalog', `${shape.table} created`);
    const description = caption && await askText('New business event: description', 'One sentence for the catalog', `Sent when a ${shape.table} record is created.`);
    const modules = description ? await enumValues(server, 'ModuleAxapta') : [];
    const module = description && await vscode.window.showQuickPick(modules.length ? modules : ['Customer'], { title: 'New business event: catalog category (module)' });
    if (!trigger || !base || !caption || !description || !module) {
        return;
    }
    const label = await labelFor(server, model, `${base}Name`, caption);
    const descriptionLabel = await labelFor(server, model, `${base}Description`, description);
    const elements = businessEventElements({ base, label, description: descriptionLabel, table: shape.table ?? table, fields: picked.map(p => p.field), module, trigger: trigger.trigger });
    await createElements(server, model, `Creating business event ${base}\u2026`, elements);
    const choice = await vscode.window.showInformationMessage(
        `Created ${elements.map(e => e.objectName).join(', ')} in ${model}. After deploying, rebuild the business event catalog (System administration > Business events > Manage > Rebuild business event catalog) and activate the event.`, 'Open Event');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxClass', `${base}BusinessEvent`);
    }
}

/** F&O Studio: Add Number Sequence: an EDT with a number sequence reference in a module, and optionally a field of your table that gets the number. */
export async function addNumberSequence(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const edt = await askName('Add number sequence: ID type (EDT)', 'The new extended data type with your prefix', 'ABCRequestId');
    const caption = edt && await askText('Add number sequence: label', 'What the ID is called', 'Request number');
    const module = caption && await vscode.window.showQuickPick([...NumberSeqModules.map(m => ({ ...m, description: m.parametersTable })),
        { label: 'Other module\u2026', moduleClass: '', parametersTable: '', description: '' }], { title: 'Add number sequence: module whose parameters hold the reference' });
    if (!edt || !caption || !module) {
        return;
    }
    let { moduleClass, parametersTable } = module;
    if (!moduleClass) {
        moduleClass = await askText('Module class', 'The NumberSeqModule class', 'NumberSeqModuleCustomer', v => NamePattern.test(v.trim()) ? undefined : 'A class name.') ?? '';
        parametersTable = await askText('Parameters table', 'The module\'s parameters table', 'CustParameters', v => NamePattern.test(v.trim()) ? undefined : 'A table name.') ?? '';
        if (!moduleClass || !parametersTable) {
            return;
        }
    }
    for (const [type, name] of [['AxClass', moduleClass], ['AxTable', parametersTable]]) {
        if (!(await server.bridge('find_object', { objectName: name, objectType: type, exactMatch: true, maxResults: 1 })).matches?.length) {
            throw new Error(`${name} was not found.`);
        }
    }
    const table = await askText('Add number sequence: your table (optional)', 'A table of your model whose field gets the next number in initValue; empty: none', '');
    let field: string | undefined;
    if (table) {
        field = await askText('Add number sequence: field', 'The field that gets the number (created with the new EDT when it doesn\'t exist)', edt.replace(/^[A-Z]{2,4}/, ''),
            v => NamePattern.test(v.trim()) ? undefined : 'A field name.');
        if (!field) {
            return;
        }
    }
    const label = await labelFor(server, model, edt, caption);
    const prefix = edt.replace(/[a-z].*$/, '').slice(0, -1) || model;
    const input = { edt, label, moduleClass, parametersTable, table, field, prefix };
    await createElements(server, model, `Adding number sequence ${edt}\u2026`, numberSequenceElements(input));
    const notes: string[] = [];
    if (table && field) {
        const read = await server.bridge('read_object', { objectType: 'AxTable', objectName: table });
        if (!read.success || (read.modelName ?? '').toLowerCase() !== model.toLowerCase()) {
            notes.push(`${table} is not a table of ${model}: set ${field} with NumberSeq::newGetNum(${parametersTable}::numRef${edt}()).num() yourself.`);
        } else if ((read.methods ?? []).some((m: { name: string }) => m.name.toLowerCase() === 'initvalue')) {
            notes.push(`${table} already has initValue: add this.${field} = NumberSeq::newGetNum(${parametersTable}::numRef${edt}()).num(); to it.`);
        } else {
            const hasField = ((await server.bridge('get_table_fields', { tableName: table })).fields ?? []).some((f: { name: string }) => f.name.toLowerCase() === field!.toLowerCase());
            const update = await server.bridge('update_object', {
                objectType: 'AxTable', objectName: table, methods: [numberSequenceInitValue(input)],
                ...hasField ? {} : { fields: [{ name: field, fieldType: 'String', extendedDataType: edt }] },
            });
            notes.push(update.success ? `${table}.${field} gets the next number in initValue.` : `${table} could not be changed (${update.message}).`);
        }
    }
    vscode.window.showInformationMessage(`Added ${edt} to ${moduleClass}. ${notes.join(' ')} After deploying, run Number sequences > Generate (or set up the reference on the ${parametersTable} form's Number sequences tab).`);
}

/** F&O Studio: New Feature: a Feature management class with isEnabled() to guard new behaviour. */
export async function newFeature(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const name = await askName('New feature: class name', 'Class name with your prefix, ending with Feature', 'ABCFastInvoicingFeature');
    const caption = name && await askText('New feature: name in Feature management', 'What administrators see', 'Fast invoicing');
    const summary = caption && await askText('New feature: summary', 'What turning it on changes', 'Posts invoices in one step.');
    const modules = summary ? await enumValues(server, 'FeatureModuleV0') : [];
    const module = summary && await vscode.window.showQuickPick(modules.length ? modules : ['AccountsReceivable'], { title: 'New feature: module' });
    const byDefault = module && await vscode.window.showQuickPick(['Off by default', 'On by default'], { title: 'New feature: default' });
    if (!name || !caption || !summary || !module || !byDefault) {
        return;
    }
    const label = await labelFor(server, model, name, caption);
    const summaryLabel = await labelFor(server, model, `${name}Summary`, summary);
    await createElements(server, model, `Creating feature ${name}\u2026`, featureElements({ name, label, summary: summaryLabel, module, enabledByDefault: byDefault.startsWith('On') }));
    const choice = await vscode.window.showInformationMessage(`Created ${name}. Guard new behaviour with if (${name}::isEnabled()) { ... }; after deploying, Feature management > Check for updates lists it.`, 'Copy Check');
    if (choice) {
        await vscode.env.clipboard.writeText(`if (${name}::isEnabled())\n{\n}`);
    }
}
