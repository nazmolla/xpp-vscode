// End-to-end check of the scaffolding wizards: node scripts/scaffold-test.js <exe> <config> <model>
// Creates every wizard's elements (report, approval workflow on a scratch table, custom service) in <model> through the
// server, builds the model, reports PASS/FAIL, and deletes everything it created again (also when a step fails).
const Module = require('module');
const path = require('path');
const { spawn } = require('child_process');

// The element builders live in extension modules that import vscode; they don't call it when building elements.
const load = Module._load;
Module._load = function (request, ...rest) { return request === 'vscode' ? {} : load.call(this, request, ...rest); };
const out = path.join(__dirname, '..', 'out', 'scaffolding');
const { reportElements } = require(path.join(out, 'report.js'));
const { workflowElements, workflowTableChanges } = require(path.join(out, 'workflow.js'));
const { serviceElements } = require(path.join(out, 'service.js'));
const { businessEventElements, numberSequenceElements, featureElements } = require(path.join(out, 'lifecycle.js'));
const { tableFormElements } = require(path.join(out, 'tableForm.js'));
const { dataEntityElements } = require(path.join(out, 'dataEntity.js'));

const [exe, config, model] = process.argv.slice(2);
const child = spawn(exe, ['--config', config], { windowsHide: true });
const pending = new Map();
let buffer = '', id = 0, failed = 0;
child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines.filter(l => l.trim())) {
        const msg = JSON.parse(line);
        pending.get(msg.id)?.(msg);
    }
});
const bridge = (action, body) => new Promise(resolve => {
    pending.set(++id, msg => resolve(typeof msg.result === 'string' ? JSON.parse(msg.result) : msg.result ?? { success: false, message: msg.error?.message }));
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method: 'xpp/bridge', params: { action, body } }) + '\n');
});
const check = (name, ok, detail) => { if (!ok) failed++; console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ': ' + detail : ''}`); };
setTimeout(() => { console.log('TIMEOUT'); child.kill(); process.exit(1); }, 30 * 60 * 1000);

(async () => {
    await new Promise(resolve => { pending.set(0, resolve); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 0, method: 'initialize', params: {} }) + '\n'); });
    const created = [];
    const create = async (element, name) => {
        const result = await bridge('create_object', { objectType: element.objectType, objectName: element.objectName, modelName: model, ...element.body });
        if (result.success) created.push(element);
        check(`${name}: create ${element.objectType} ${element.objectName}`, result.success, result.success ? undefined : result.message);
        return result.success;
    };
    try {
        const table = await bridge('get_table_fields', { tableName: 'CustInvoiceJour' });
        const fields = table.fields.filter(f => ['InvoiceId', 'InvoiceDate', 'InvoiceAmount', 'InvoiceAccount', 'CurrencyCode'].includes(f.name));
        for (const element of reportElements({ base: 'XvsScafReport', label: 'Scaffold report', sourceTable: 'CustInvoiceJour', fields, dateField: fields.find(f => f.name === 'InvoiceDate') })) {
            await create(element, 'report');
        }
        const report = await bridge('read_object', { objectType: 'AxReport', objectName: 'XvsScafReport' });
        check('report: data set and auto design saved', report.success && JSON.stringify(report.typedMetadata).includes('AxReportTable')
            && JSON.stringify(report.typedMetadata).includes('=Fields!InvoiceId.Value'), report.success ? undefined : report.message);

        await create({ objectType: 'AxTable', objectName: 'XvsScafDoc', body: { properties: { Label: 'Scaffold document' }, fields: [{ name: 'Name', fieldType: 'String', extendedDataType: 'Name' }] } }, 'workflow');
        const input = { base: 'XvsScafWF', label: 'Scaffold approval', table: 'XvsScafDoc', documentMenuItem: 'CustTable', module: 'Cust', previewFieldGroup: undefined };
        for (const element of workflowElements(input)) {
            await create(element, 'workflow');
        }
        const changes = workflowTableChanges(input);
        const tableUpdate = await bridge('update_object', { objectType: 'AxTable', objectName: 'XvsScafDoc', fields: [changes.field], methods: changes.methods });
        check('workflow: table gets status field and methods', tableUpdate.success, tableUpdate.success ? undefined : tableUpdate.message);
        const template = await bridge('read_object', { objectType: 'AxWorkflowTemplate', objectName: 'XvsScafWFType' });
        check('workflow: type lists the approval', template.success && JSON.stringify(template.typedMetadata).includes('XvsScafWFApproval'));

        for (const element of serviceElements('XvsScafSvc', 'Scaffold service')) {
            await create(element, 'service');
        }

        const docFields = (await bridge('get_table_fields', { tableName: 'XvsScafDoc' })).fields.filter(f => f.name === 'Name');
        for (const element of businessEventElements({ base: 'XvsScafBE', label: 'Scaffold event', description: 'Sent in the scaffold test.', table: 'XvsScafDoc', fields: docFields, module: 'Customer', trigger: 'insert' })) {
            await create(element, 'business event');
        }
        for (const element of numberSequenceElements({ edt: 'XvsScafNumId', label: 'Scaffold number', moduleClass: 'NumberSeqModuleCustomer', parametersTable: 'CustParameters', prefix: 'XvsScaf' })) {
            await create(element, 'number sequence');
        }
        for (const element of featureElements({ name: 'XvsScafFeature', label: 'Scaffold feature', summary: 'Used by the scaffold test.', module: 'AccountsReceivable', enabledByDefault: false })) {
            await create(element, 'feature');
        }
        for (const element of tableFormElements({ base: 'XvsScafGroup', label: 'Scaffold group', keyLabel: 'Scaffold group ID' })) {
            await create(element, 'table and form');
        }
        const groupFields = (await bridge('get_table_fields', { tableName: 'XvsScafGroup' })).fields.filter(f => ['XvsScafGroupId', 'Description'].includes(f.name));
        for (const element of dataEntityElements({ entity: 'XvsScafGroupEntity', table: 'XvsScafGroup', label: 'Scaffold groups', publicName: 'XvsScafGroup',
            collectionName: 'XvsScafGroups', fields: groupFields, keyFields: ['XvsScafGroupId'], perCompany: true })) {
            await create(element, 'data entity');
        }

        const build = await bridge('build', { modelName: model });
        const ours = (build.diagnostics ?? []).filter(d => /XvsScaf/.test(`${d.objectName} ${d.path}`) && /error|fatal/i.test(d.severity));
        check('build: the generated code compiles', ours.length === 0, ours.length ? ours.map(d => `${d.objectName}.${d.member ?? ''} ${d.line ?? ''}: ${d.message}`).join(' | ') : (build.message ?? '').split('\n')[0]);
        const warnings = (build.diagnostics ?? []).filter(d => /XvsScaf/.test(`${d.objectName} ${d.path}`) && /warning/i.test(d.severity));
        for (const w of warnings) console.log(`  warning ${w.objectName}.${w.member ?? ''}: ${w.message}`);
    } finally {
        for (const element of created.reverse()) {
            const result = await bridge('delete_object', { objectType: element.objectType, objectName: element.objectName });
            if (!result.success) console.log(`NOT DELETED ${element.objectType} ${element.objectName}: ${result.message}`);
        }
        console.log(`cleaned up ${created.length} element(s)`);
    }
    console.log(failed ? `${failed} failure(s)` : 'All scaffolding checks passed.');
    child.stdin.end();
    process.exit(failed ? 1 : 0);
})();
