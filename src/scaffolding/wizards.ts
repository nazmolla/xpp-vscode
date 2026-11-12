import * as vscode from 'vscode';
import { XppServerClient } from '../serverClient';
import { askName, askText, createElements, labelFor, NamePattern, projectModel } from './common';
import { dataEntityElements, entityFieldSupported } from './dataEntity';
import { reportElements, reportFieldsSupported, SourceField } from './report';
import { serviceElements } from './service';
import { tableFormElements } from './tableForm';
import { WorkflowInput, workflowElements, workflowTableChanges } from './workflow';

/** F&O Studio: New Report: asks for the table, fields and date filter, then creates the report elements. */
export async function newReport(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const base = await askName('New report: name', 'Report name with your prefix; the classes and table get DP, Controller, Contract and Tmp appended', 'ABCCustomerList');
    const caption = base && await askText('New report: title', 'What users see on the menu and as the report title', 'Customer list');
    const sourceTable = caption && await askText('New report: table', 'The table the report lists', 'CustTable', v => NamePattern.test(v.trim()) ? undefined : 'A table name.');
    if (!base || !caption || !sourceTable) {
        return;
    }
    const table = await server.bridge('get_table_fields', { tableName: sourceTable });
    if (!table.success) {
        throw new Error(table.message ?? `Table ${sourceTable} not found.`);
    }
    const available = ((table.fields ?? []) as SourceField[]).filter(reportFieldsSupported);
    const picked = await vscode.window.showQuickPick(available.map(f => ({ label: f.name, description: f.edt || f.enumType || f.type, field: f })),
        { title: `New report: fields of ${table.table}`, canPickMany: true, ignoreFocusOut: true });
    if (!picked?.length) {
        return;
    }
    const dates = available.filter(f => f.type === 'Date' || f.type === 'UtcDateTime' || f.type === 'DateTime');
    const none = { label: 'No date range', field: undefined as SourceField | undefined };
    const dateChoice = dates.length
        ? await vscode.window.showQuickPick([none, ...dates.map(f => ({ label: f.name, description: 'From/To date filter', field: f as SourceField | undefined }))],
            { title: 'New report: filter by a date?', ignoreFocusOut: true })
        : none;
    if (!dateChoice) {
        return;
    }
    const label = await labelFor(server, model, base, caption);
    const elements = reportElements({ base, label, sourceTable: table.table ?? sourceTable, fields: picked.map(p => p.field), dateField: dateChoice.field });
    await createElements(server, model, `Creating report ${base}…`, elements);
    const choice = await vscode.window.showInformationMessage(
        `Created ${elements.map(e => e.objectName).join(', ')} in ${model}. Add the privilege ${base}View to a duty and the menu item to a menu, build, then deploy the report.`, 'Open Data Provider');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxClass', `${base}DP`);
    }
}

/** F&O Studio: New Approval Workflow: asks for the table and its form's menu item, then creates the workflow and wires the table and form. */
export async function newWorkflow(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const table = await askText('New approval workflow: table', 'A table of your model; it gets the workflow status field and methods', 'ABCExpenseRequest',
        v => NamePattern.test(v.trim()) ? undefined : 'A table name.');
    if (!table) {
        return;
    }
    const tableRead = await server.bridge('read_object', { objectType: 'AxTable', objectName: table });
    if (!tableRead.success) {
        throw new Error(`Table ${table} not found.`);
    }
    if ((tableRead.modelName ?? '').toLowerCase() !== model.toLowerCase()) {
        throw new Error(`${table} belongs to ${tableRead.modelName}; the workflow wizard changes the table, so pick a table of ${model}.`);
    }
    if ((tableRead.methods ?? []).some((m: { name?: string; Name?: string }) => (m.name ?? m.Name ?? '').toLowerCase() === 'cansubmittoworkflow')) {
        throw new Error(`${table} already has canSubmitToWorkflow: it is already used by a workflow.`);
    }
    const base = await askName('New approval workflow: name', 'Base name with your prefix; the elements get Type, Approval, Document, Submit... appended', `${table}Approval`);
    const caption = base && await askText('New approval workflow: label', 'What users see in the workflow editor', 'Expense request approval');
    const documentMenuItem = caption && await askText('New approval workflow: document menu item', 'The display menu item that opens the form of the records', table,
        v => NamePattern.test(v.trim()) ? undefined : 'A menu item name.');
    if (!base || !caption || !documentMenuItem) {
        return;
    }
    const menuItem = await server.bridge('read_object', { objectType: 'AxMenuItemDisplay', objectName: documentMenuItem });
    if (!menuItem.success) {
        throw new Error(`Display menu item ${documentMenuItem} not found.`);
    }
    const modules = await server.bridge('read_object', { objectType: 'AxEnum', objectName: 'ModuleAxapta' });
    const moduleNames: string[] = (modules.enumValues ?? []).map((v: { name?: string; Name?: string }) => v.name ?? v.Name ?? '').filter((n: string) => n);
    const module = await vscode.window.showQuickPick(moduleNames.length ? moduleNames : ['Basic'], { title: 'New approval workflow: module (workflow category)', ignoreFocusOut: true });
    if (!module) {
        return;
    }
    const fields = await server.bridge('get_table_fields', { tableName: table });
    const groups: string[] = fields.fieldGroups ?? [];
    const preview = groups.length
        ? await vscode.window.showQuickPick(['(none)', ...groups], { title: 'New approval workflow: field group shown to approvers', ignoreFocusOut: true })
        : '(none)';
    if (!preview) {
        return;
    }

    const label = await labelFor(server, model, base, caption);
    const input: WorkflowInput = { base, label, table, documentMenuItem, module, previewFieldGroup: preview === '(none)' ? undefined : preview };
    await createElements(server, model, `Creating workflow ${base}…`, workflowElements(input));

    const notes: string[] = [];
    const changes = workflowTableChanges(input);
    const tableResult = await server.bridge('update_object', { objectType: 'AxTable', objectName: table, fields: [changes.field], methods: changes.methods });
    notes.push(tableResult.success ? `${table} got the ${base}Status field, canSubmitToWorkflow and setWorkflowStatus.` : `${table} could not be changed (${tableResult.message}); add the ${base}Status field and the two methods by hand.`);
    notes.push(await enableFormWorkflow(server, model, String(menuItem.properties?.Object ?? ''), table, `${base}Type`));
    const choice = await vscode.window.showInformationMessage(
        `Created the ${base} workflow in ${model}. ${notes.join(' ')} Add the privileges ${base}Submitter and ${base}Approver to duties, build, then configure the workflow in the web client.`, 'Open Workflow Type');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxWorkflowTemplate', `${base}Type`);
    }
}

/** Sets WorkflowEnabled, WorkflowDatasource and WorkflowType on the form's design when the form is in the model. */
async function enableFormWorkflow(server: XppServerClient, model: string, form: string, table: string, type: string): Promise<string> {
    const manual = `On the form, set Design > WorkflowEnabled = Yes, WorkflowDatasource = the ${table} data source, WorkflowType = ${type}.`;
    if (!form) {
        return manual;
    }
    const read = await server.bridge('read_object', { objectType: 'AxForm', objectName: form, sections: ['dataSources'] });
    if (!read.success || (read.modelName ?? '').toLowerCase() !== model.toLowerCase()) {
        return `Form ${form} is not in ${model}. ${manual}`;
    }
    const dataSource = ((read.dataSources ?? []) as { name?: string; Name?: string; table?: string; Table?: string }[])
        .find(d => (d.table ?? d.Table ?? '').toLowerCase() === table.toLowerCase());
    const dsName = dataSource?.name ?? dataSource?.Name;
    if (!dsName) {
        return `Form ${form} has no ${table} data source. ${manual}`;
    }
    for (const [property, value] of [['WorkflowEnabled', 'Yes'], ['WorkflowDatasource', dsName], ['WorkflowType', type]]) {
        const result = await server.bridge('set_element_property', { objectType: 'AxForm', objectName: form, path: ['Design'], property, value });
        if (!result.success) {
            return `Form ${form}: ${property} could not be set (${result.message}). ${manual}`;
        }
    }
    return `Form ${form} shows the workflow bar.`;
}

/** F&O Studio: New Custom Service: creates a service endpoint with contracts, service group and privilege. */
export async function newService(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const base = await askName('New custom service: name', 'Base name with your prefix; the elements get Service, Request, Response and ServiceGroup appended', 'ABCOrderImport');
    const caption = base && await askText('New custom service: description', 'What the service does', 'Imports orders from the web shop');
    if (!base || !caption) {
        return;
    }
    const label = await labelFor(server, model, base, caption);
    const elements = serviceElements(base, label);
    await createElements(server, model, `Creating service ${base}…`, elements);
    const choice = await vscode.window.showInformationMessage(
        `Created ${elements.map(e => e.objectName).join(', ')} in ${model}. After deployment the operation is at /api/services/${base}ServiceGroup/${base}Service/process; add the privilege ${base}Invoke to a role for the calling app.`, 'Open Service Class');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxClass', `${base}Service`);
    }
}

/** F&O Studio: New Table with Form: a setup table, its list form, a menu item and View/Maintain privileges. */
export async function newTableWithForm(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const base = await askName('New table with form: name', 'Table, form and menu item name with your prefix; the key field gets Id appended', 'ABCVehicleGroup');
    const caption = base && await askText('New table with form: label', 'What users see as the form caption and menu item', 'Vehicle groups');
    const keyCaption = caption && await askText('New table with form: key field label', 'The label of the key field', 'Vehicle group');
    if (!base || !caption || !keyCaption) {
        return;
    }
    const label = await labelFor(server, model, base, caption);
    const keyLabel = await labelFor(server, model, `${base}Id`, keyCaption);
    const elements = tableFormElements({ base, label, keyLabel });
    await createElements(server, model, `Creating ${base}…`, elements);
    const choice = await vscode.window.showInformationMessage(
        `Created ${elements.map(e => e.objectName).join(', ')} in ${model}. Add fields to ${base} and its Overview field group, the menu item to a menu, and the privileges ${base}View and ${base}Maintain to duties.`, 'Open Table', 'Open Form');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', choice === 'Open Form' ? 'AxForm' : 'AxTable', base);
    }
}

/** F&O Studio: New Data Entity: an OData and data management entity over a table, with its staging table and privileges. */
export async function newDataEntity(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const table = await askText('New data entity: table', 'The table the entity reads and writes', 'ABCVehicleGroup', v => NamePattern.test(v.trim()) ? undefined : 'A table name.');
    if (!table) {
        return;
    }
    const read = await server.bridge('read_object', { objectType: 'AxTable', objectName: table });
    const tableFields = await server.bridge('get_table_fields', { tableName: table });
    if (!read.success || !tableFields.success) {
        throw new Error(`Table ${table} not found.`);
    }
    const entity = await askName('New data entity: name', 'Entity name with your prefix', `${table}Entity`);
    const caption = entity && await askText('New data entity: label', 'What users see in data management', table);
    const publicName = caption && await askName('New data entity: public name (OData, singular)', 'The OData entity name', table.replace(/^[A-Z]{2,4}(?=[A-Z][a-z])/, ''));
    const collectionName = publicName && await askName('New data entity: public collection name (OData, plural)', 'The OData collection name', `${publicName}s`);
    if (!entity || !caption || !publicName || !collectionName) {
        return;
    }
    const available = ((tableFields.fields ?? []) as SourceField[]).filter(entityFieldSupported);
    const picked = await vscode.window.showQuickPick(available.map(f => ({ label: f.name, description: f.edt || f.enumType || f.type, picked: true, field: f })),
        { title: `New data entity: fields of ${table}`, canPickMany: true, ignoreFocusOut: true });
    if (!picked?.length) {
        return;
    }
    // The table's replacement (natural) key, else its primary index, gives the entity key.
    const keyIndex = String(read.properties?.ReplacementKey || read.properties?.PrimaryIndex || '');
    const indexFields: string[] = ((read.indexes ?? []) as { name?: string; Name?: string; fields?: string[] }[])
        .find(i => (i.name ?? i.Name ?? '').toLowerCase() === keyIndex.toLowerCase())?.fields ?? [];
    const keyFields = indexFields.length ? indexFields
        : (await vscode.window.showQuickPick(picked.map(p => p.label), { title: `New data entity: key fields (${table} has no natural key index)`, canPickMany: true, ignoreFocusOut: true })) ?? [];
    const missing = keyFields.filter(k => !picked.some(p => p.label === k));
    if (!keyFields.length || missing.length) {
        throw new Error(keyFields.length ? `The entity must include the key field(s) ${missing.join(', ')}.` : 'The entity needs at least one key field.');
    }
    const label = await labelFor(server, model, entity, caption);
    const perCompany = String(read.properties?.SaveDataPerCompany ?? 'Yes') !== 'No';
    const elements = dataEntityElements({ entity, table, label, publicName, collectionName, fields: picked.map(p => p.field), keyFields, perCompany });
    await createElements(server, model, `Creating ${entity}…`, elements);
    const choice = await vscode.window.showInformationMessage(
        `Created ${elements.map(e => e.objectName).join(', ')} in ${model}. Add the privileges to duties, build and synchronize the database; the entity is then at /data/${collectionName} and in data management.`, 'Open Entity');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxDataEntityView', entity);
    }
}
