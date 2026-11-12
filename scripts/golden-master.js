// Golden master of the server's behaviour: node scripts/golden-master.js <outputFile> [--compare <baselineFile>]
// Runs a fixed set of bridge actions and agent tools (reads of standard objects; writes only on XvsGm* objects in testcust and
// a test label that is removed again) and writes every normalized response. With --compare, prints the differences to the
// baseline and exits 1 when there are any. Use it around refactorings that must not change behaviour.
const { spawn, execSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const [outFile, flag, baselineFile] = process.argv.slice(2);
if (!outFile) { console.log('Usage: node scripts/golden-master.js <outputFile> [--compare <baselineFile>]'); process.exit(2); }
const reg = execSync('reg query "HKCU\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations" /v CurrentMetadataConfig', { encoding: 'utf8' });
const config = /CurrentMetadataConfig\s+REG_SZ\s+(.+)/.exec(reg)?.[1]?.trim();
const project = 'C:\\Users\\Mohamed Nazmi\\source\\repos\\TestProj\\TestProj.rnrproj';
const temp = path.join(os.tmpdir(), 'xpp-golden');
fs.rmSync(temp, { recursive: true, force: true });
fs.mkdirSync(temp, { recursive: true });

const M = 'testcust';
const C = { objectType: 'AxClass', objectName: 'XvsGmClass' };
const T = { objectType: 'AxTable', objectName: 'XvsGmTable' };
const F = { objectType: 'AxForm', objectName: 'XvsGmForm' };
const P = { objectType: 'AxSecurityPrivilege', objectName: 'XvsGmPriv' };
const L = 'HSFixedAssetRemoval';
const b = (action, body, options) => ['bridge', action, body, options];
const t = (name, args, options) => ['tool', name, args, options];
const m = (method, params, options) => ['method', method, params, options];
// Results that depend on the network, Git or the clock: only their shape is compared.
const loose = { loose: true };
const repo = path.resolve(__dirname, '..');
const referenceRoot = JSON.parse(fs.readFileSync(config, 'utf8')).ReferencePackagesPaths[0];
const G = { objectType: 'AxClass', objectName: 'XvsGmToolClass' };
const steps = [
    // MCP methods (discovery: what an agent sees before calling anything)
    m('initialize', { protocolVersion: '2025-06-18' }), m('ping', {}), m('tools/list', {}), m('no/such/method', {}),
    m('xpp/data', { action: 'test', body: {} }),
    m('xpp/trace', { action: 'analyze', body: { path: path.join(repo, 'test trace.etl') } }),
    m('xpp/lang', { action: 'status', body: {} }), m('xpp/lang', { action: 'subtypes', body: { objectType: 'AxClass', objectName: 'SalesFormLetter' } }),
    m('xpp/lang', { action: 'no_such_action', body: {} }),
    // Reads of standard and custom metadata
    b('get_environment', {}), b('list_models', {}), b('get_model_info', { modelName: 'Foundation' }), b('get_model_info', { modelName: M }),
    b('get_model', { name: M }), b('get_model', { name: 'NoSuchModel' }), b('list_packages', {}),
    b('read_object', { objectType: 'AxTable', objectName: 'CustGroup' }), b('read_object', { objectType: 'AxClass', objectName: 'CustVendSettle' }),
    b('read_object', { objectType: 'AxEnum', objectName: 'NoYes' }), b('read_object', { objectType: 'AxClass', objectName: 'NoSuchClassXvs' }),
    b('find_object', { objectName: 'CustGroup', objectType: 'AxTable', exactMatch: 'true' }), b('find_object', { objectName: 'CustGro', maxResults: '5' }),
    b('list_objects', { modelName: M, objectType: 'AxClass', maxResults: '20' }), b('find_references', { objectType: 'AxTable', objectName: 'CustGroup', maxResults: '5' }),
    b('get_object_type_schema', { objectType: 'AxEdt' }), b('get_form_layout', { formName: 'CustGroup' }), b('get_form_tree', { formName: 'CustGroup' }),
    b('get_table_fields', { tableName: 'CustGroup' }), b('get_element_tree', { objectType: 'AxTable', objectName: 'CustGroup' }),
    b('get_element_properties', { objectType: 'AxTable', objectName: 'CustGroup', path: ['Fields', 'Name'] }),
    b('get_node_types', { objectType: 'AxTable', objectName: 'CustGroup', path: ['Fields'] }),
    b('get_element_method', { objectType: 'AxTable', objectName: 'CustGroup', path: ['Methods', 'find'] }),
    b('get_overrides', { objectType: 'AxTable', objectName: 'CustGroup', path: [] }), b('type_members', { objectType: 'AxClass', objectName: 'CustVendSettle' }),
    b('dump_element', { objectType: 'AxEdt', objectName: 'CustGroupId' }), b('table_erd', { tables: ['CustGroup', 'CustTable', 'NoSuchTableXvs'] }),
    b('incoming_relations', { table: 'CustGroup' }), b('validate_object', { objectType: 'AxTable', objectName: 'CustGroup' }),
    b('read_label', { labelFileId: L, labelId: 'HSRemovalStatus' }), b('read_label', { labelFileId: 'SYS', search: 'customer group', maxResults: '3' }),
    b('label_file_entries', { labelFile: L, search: '', limit: '20' }), b('label_file_entries', { labelFile: 'NoSuchFileXvs', search: '' }),
    b('list_project_items', {}), b('select_project', { searchFolder: path.dirname(project) }),
    // Model management: failure paths only (models can't be deleted)
    b('create_model', { name: '1bad', displayName: 'x' }), b('create_model', { name: M }), b('update_model', { name: 'Foundation', displayName: 'x' }),
    b('update_model', { name: M, references: ['NoSuchPackageXvs'] }),
    // Build: the refusal paths (a real build takes minutes)
    b('build', { modelName: 'Foundation' }), b('build', { modelName: 'NoSuchModelXvs' }),
    // Project packages
    b('export_project', { projectFile: project, outputFile: path.join(temp, 'gm.axpp') }), b('inspect_project_package', { file: path.join(temp, 'gm.axpp') }),
    b('import_project', { file: path.join(temp, 'gm.axpp'), targetFolder: path.join(temp, 'import'), overwrite: 'false' }),
    b('inspect_project_package', { file: path.join(temp, 'missing.axpp') }),
    // Labels: write and remove one test label
    b('save_label', { labelFile: L + '_en-US', labelId: 'XvsGmLabel', texts: { 'en-US': 'Golden master' }, comment: 'temp' }),
    b('read_label', { labelFileId: L, labelId: 'XvsGmLabel' }), b('delete_label', { labelFile: L + '_en-US', labelId: 'XvsGmLabel' }),
    b('create_label', { labelFileId: L, labelId: 'XvsGmLabel2', text: 'Golden two', comment: '' }),
    b('delete_label', { labelFile: L + '_en-US', labelId: 'XvsGmLabel2' }), b('save_label', { labelFile: 'SYS', labelId: 'XvsGm', texts: { 'en-US': 'x' } }),
    // Object life cycle on own objects
    b('create_object', { ...C, modelName: M, declaration: 'class XvsGmClass\n{\n}', methods: ['public void run()\n{\n}'] }),
    b('update_object', { ...C, methods: ['public int calc()\n{\n    return 1;\n}'] }), b('read_object', C), b('validate_object', C),
    b('set_element_method', { ...C, path: ['Methods', 'calc'], originalName: 'calc', source: 'public int calc()\n{\n    return 2;\n}' }),
    b('create_object', { ...T, modelName: M }), b('drop_on_node', { ...T, path: [], sourceType: 'AxEdt', sourceName: 'CustGroupId' }),
    b('drop_on_node', { ...T, path: [], sourceType: 'AxEnum', sourceName: 'NoYes' }),
    b('add_element_node', { ...T, path: ['FieldGroups'], typeName: 'AxTableFieldGroup', name: 'Grp' }),
    b('drop_on_node', { ...T, path: ['FieldGroups', 'Grp'], sourcePath: ['Fields', 'CustGroupId'] }),
    b('set_element_property', { ...T, path: ['Fields', 'CustGroupId'], property: 'Mandatory', value: 'Yes' }),
    b('rename_element_node', { ...T, path: ['Fields', 'NoYes'], newName: 'Flag' }),
    b('duplicate_element_node', { ...T, path: ['Fields', 'Flag'], newName: 'Flag2' }), b('move_element_node', { ...T, path: ['Fields', 'Flag2'], offset: '-1' }),
    b('delete_element_node', { ...T, path: ['Fields', 'Flag2'] }), b('get_element_tree', T), b('dump_element', T), b('table_erd', { tables: [T.objectName] }),
    b('drop_on_property', { ...T, path: ['Fields', 'Flag'], property: 'ExtendedDataType', sourceType: 'AxEdt', sourceName: 'NoYesId' }),
    b('create_object', { ...F, modelName: M }), b('drop_on_node', { ...F, path: [], sourceType: 'AxTable', sourceName: T.objectName }), b('get_form_tree', { formName: F.objectName }),
    b('create_object', { ...P, modelName: M }), b('drop_on_node', { ...P, path: [], sourceType: 'AxMenuItemDisplay', sourceName: 'CustGroup' }), b('read_object', P),
    b('replace_references', { ...F, table: T.objectName, oldName: 'CustGroupId', newName: 'CustGroupIdGm' }),
    b('replace_references', { ...P, targetType: 'AxMenuItemDisplay', oldName: 'CustGroup', newName: 'CustGroupGm' }),
    b('replace_references', { objectType: 'AxTable', objectName: 'CustGroup', targetType: 'AxTable', oldName: 'X', newName: 'Y' }),
    b('analyze_performance', F), b('table_data_shape', { table: T.objectName }),
    b('duplicate_object', { ...C, newName: 'XvsGmClass2', modelName: M }), b('add_to_project', { objectType: 'AxClass', objectName: 'XvsGmClass2' }),
    b('remove_from_project', { objectType: 'AxClass', objectName: 'XvsGmClass2' }),
    b('delete_object', { objectType: 'AxClass', objectName: 'XvsGmClass2' }), b('delete_object', P), b('delete_object', F), b('delete_object', T), b('delete_object', C),
    // Remaining reads and analyses, and the refusal paths of long-running actions
    b('read_object_by_path', { filePath: path.join(referenceRoot, 'ApplicationSuite', 'Foundation', 'AxTable', 'CustGroup.xml') }),
    b('read_object_by_path', { filePath: path.join(temp, 'NoSuch.xml') }),
    b('table_data_shape', { table: 'CustGroup' }), b('analyze_performance', { objectType: 'AxForm', objectName: 'SalesTable' }),
    b('analyze_performance', { objectType: 'AxEdt', objectName: 'CustGroupId' }), b('extension_compatibility', { modelName: M }),
    b('extension_collisions', { modelName: M }), b('wrapper_drift', { modelName: M, previousFolder: path.join(temp, 'NoSuchFolder') }),
    b('checkpoint_entries', { id: 'none' }), b('revert_checkpoint', { id: 'none' }),
    b('best_practice', { modelName: 'NoSuchModelXvs' }), b('create_package', { modelNames: ['NoSuchModelXvs'] }), b('sync_tables', { tables: ['NoSuchTableXvs'] }),
    b('where_used', { objectType: 'AxTable', objectName: 'CustGroup', maxResults: '5' }), b('where_used', { objectType: 'AxTable', objectName: 'CustGroup', memberName: 'Name', maxResults: '5' }),
    b('where_used', { objectType: 'AxLabelFile', objectName: 'x' }), b('list_tags', { filter: 'Xvs' }), b('tag_references', { tag: 'NoSuchTagXvs' }),
    b('label_usage', { labelFileId: L }), b('label_usage', { labelFileId: '' }),
    b('element_dependencies', { objectType: 'AxTable', objectName: 'CustGroup', maxResults: '10' }), b('element_dependencies', { objectType: 'AxLabelFile', objectName: 'x' }),
    b('no_such_action', {}),
    // A read of every element type (each has its own read path)
    ...[['AxForm', 'CustGroup'], ['AxEdt', 'CustGroupId'], ['AxView', 'CustTableCube'], ['AxQuery', 'CustTable'], ['AxDataEntityView', 'CustCustomerGroupEntity'],
        ['AxMenu', 'AccountsReceivable'], ['AxMenuItemDisplay', 'CustGroup'], ['AxMenuItemOutput', 'CustAgingBalance'], ['AxMenuItemAction', 'CustCollectionLetterPost'],
        ['AxSecurityDuty', 'CredManCreditLimitCustGroupView'], ['AxSecurityRole', 'FMClerk'], ['AxService', 'CustRecurrenceInvoicePostService'],
        ['AxServiceGroup', 'WHSServices'], ['AxTile', 'CustTableListPageTile'], ['AxConfigurationKey', 'LedgerBasic'], ['AxTableExtension', 'AssetTable.HSFixedAssetRemoval'],
        ['AxEnumExtension', 'SalesPurchCopy.TAMDeduction'], ['AxEdtExtension', 'NoSuchEdtXvs.Extension'], ['AxFormExtension', 'CustGroup.SubBillDeferralExtension'],
        ['AxDataEntityViewExtension', 'SalesOrderHeaderV2Entity.HSFixedAssetRemoval'], ['AxSecurityDutyExtension', 'LedgerEntitiesView.Extension'],
        ['AxSecurityRoleExtension', 'LedgerAccountant.HSFixedAssetRemoval'], ['AxMenuExtension', 'AccountsReceivable.HSFixedAssetRemoval'],
        ['AxCompositeDataEntityView', 'SalesOrderV2Entity'], ['AxMap', 'TradePostalAddress'], ['AxReport', 'CustAgingReport'], ['AxWorkflowCategory', 'CustCategory'],
        ['AxLabelFile', 'HSFixedAssetRemoval_en-US'], ['AxNoSuchType', 'X']]
        .flatMap(([objectType, objectName]) => [b('read_object', { objectType, objectName }), b('validate_object', { objectType, objectName })]),
    b('get_object_type_schema', { objectType: 'AxForm' }), b('get_object_type_schema', { objectType: 'AxNoSuchType' }),
    // Agent tools
    t('xpp_get_model_info', { modelName: M }), t('xpp_list_models', {}), t('xpp_read_object', { objectType: 'AxTable', objectName: 'CustGroup' }),
    t('xpp_find_object', { objectName: 'CustGroup', objectType: 'AxTable', exactMatch: true }), t('xpp_read_label', { labelFileId: L, labelId: 'HSRemovalStatus' }),
    t('xpp_get_object_type_schema', { objectType: 'AxEnum' }), t('xpp_get_form_layout', { formName: 'CustGroup' }),
    t('xpp_list_model_objects', { modelName: M }), t('xpp_find_references', { objectType: 'AxTable', objectName: 'CustGroup', maxResults: 5 }),
    t('xpp_analyze_security', { op: 'license', objects: ['AxSecurityRole:CustCustomerService'] }),
    t('xpp_create_object', { ...G, modelName: M, declaration: 'class XvsGmToolClass\n{\n}', methods: ['public void run()\n{\n}'] }),
    t('xpp_update_object', { ...G, methods: ['public int calc()\n{\n    return 1;\n}'] }),
    t('xpp_update_current_object', { ...G, methods: ['public int calc()\n{\n    return 2;\n}'] }),
    t('xpp_validate_object', G), t('xpp_add_to_project', G), t('xpp_remove_from_project', G), t('xpp_delete_object', G),
    t('xpp_delete_object', { objectType: 'AxTable', objectName: 'CustGroup' }),
    t('xpp_list_objects', { nameFilter: 'CustGroup', objectType: 'AxTable', maxResults: 5 }),
    t('xpp_find_examples', { query: 'batch job with contract', maxResults: 3 }), t('xpp_get_guide', { topic: 'ssrs' }), t('xpp_get_guide', {}),
    t('xpp_find_message_source', { message: 'Field Customer group must be filled in.', maxResults: 3 }),
    t('xpp_search_code', { pattern: 'CustGroup::find(', model: M, maxResults: 5 }),
    t('xpp_create_label', { labelFileId: L, labelId: 'XvsGmToolLabel', text: 'Golden tool label' }),
    b('delete_label', { labelFile: L + '_en-US', labelId: 'XvsGmToolLabel' }),
    t('xpp_list_project_items', {}), t('xpp_get_environment', {}), t('xpp_search_docs', { query: 'X++ select statement' }, loose),
    t('xpp_select_project', { searchFolder: path.dirname(project) }), t('xpp_build', { modelName: 'Foundation' }),
    t('xpp_package', { modelNames: ['NoSuchModelXvs'] }), t('xpp_diff_changes', {}, loose), t('xpp_check_best_practice', { modelName: 'NoSuchModelXvs' }),
    t('xpp_find_deprecations', { modelName: M }), t('xpp_analyze_trace', { path: path.join(repo, 'test trace.etl') }),
    t('xpp_analyze_trace', { path: path.join(temp, 'missing.etl') }), t('xpp_analyze_performance', { objectType: 'AxClass', objectName: 'CustVendSettle' }),
    t('xpp_no_such_tool', {}),
];

const args = ['--config', config, '--project', project];
const child = spawn(path.resolve(__dirname, '..', 'server', 'bin', 'XppVsCode.Server.exe'), args, { windowsHide: true });
let buffer = '';
const pending = new Map();
child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines) {
        if (!line.trim()) continue;
        const msg = JSON.parse(line);
        pending.get(msg.id)?.(msg);
        pending.delete(msg.id);
    }
});
let nextId = 1;
const rpc = (method, params) => new Promise(resolve => {
    const id = nextId++;
    pending.set(id, resolve);
    child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
});

// Timings, temp paths and counters that change between runs.
const volatileKeys = new Set(['ms', 'elapsedMs', 'cached', 'duration', 'timestamp']);
function normalize(value) {
    if (Array.isArray(value)) return value.map(normalize);
    if (value && typeof value === 'object') {
        return Object.fromEntries(Object.entries(value).filter(([k]) => !volatileKeys.has(k)).map(([k, v]) => [k, normalize(v)]));
    }
    if (typeof value === 'string') {
        return value.split(temp).join('<temp>').replace(/\b\d+ ?ms\b/g, '<n> ms').replace(/\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?Z?/g, '<time>');
    }
    return value;
}

(async () => {
    await rpc('initialize', { protocolVersion: '2025-06-18' });
    const results = [];
    for (const [kind, name, body, options] of steps) {
        const r = kind === 'bridge' ? await rpc('xpp/bridge', { action: name, body })
            : kind === 'tool' ? await rpc('tools/call', { name, arguments: body })
            : await rpc(name, body);
        let result = r.result ?? r.error;
        if (typeof result === 'string') { try { result = JSON.parse(result); } catch { /* text */ } }
        if (kind === 'tool' && result?.content) {
            const text = result.content.map(c => c.text).join('\n');
            result = { isError: !!result.isError, text };
        }
        if (options?.loose) {
            result = result && typeof result === 'object' ? { shape: Object.keys(result).sort(), isError: result.isError, success: result.success } : typeof result;
        }
        results.push({ step: `${kind} ${name} ${JSON.stringify(body).slice(0, 120)}`, result: normalize(result) });
        process.stdout.write('.');
    }
    // Every bridge action of the contract and every tool the server lists must be exercised.
    const contract = fs.readFileSync(path.join(repo, 'src', 'bridgeContract.ts'), 'utf8');
    const actions = [...contract.slice(contract.indexOf('['), contract.indexOf('] as const')).matchAll(/'(\w+)'/g)].map(x => x[1]);
    const tools = (results.find(x => x.step.startsWith('method tools/list'))?.result?.tools ?? []).map(x => x.name);
    const called = new Set(steps.map(([kind, name]) => `${kind} ${name}`));
    const uncovered = [...actions.filter(a => !called.has(`bridge ${a}`)), ...tools.filter(x => !called.has(`tool ${x}`))];
    console.log(uncovered.length ? `\nNOT COVERED: ${uncovered.join(', ')}` : `\nCoverage: ${actions.length} bridge actions, ${tools.length} tools, all exercised.`);
    child.stdin.end();
    fs.writeFileSync(outFile, JSON.stringify(results, null, 1));
    console.log(`\n${results.length} responses written to ${outFile}`);
    if (flag === '--compare') {
        const baseline = JSON.parse(fs.readFileSync(baselineFile, 'utf8'));
        let differences = 0;
        results.forEach((r, i) => {
            const before = JSON.stringify(baseline[i]?.result), after = JSON.stringify(r.result);
            if (baseline[i]?.step !== r.step || before !== after) {
                differences++;
                console.log(`DIFF ${i}: ${r.step}\n  before: ${String(before).slice(0, 400)}\n  after:  ${String(after).slice(0, 400)}`);
            }
        });
        if (baseline.length !== results.length) { differences++; console.log(`DIFF count: ${baseline.length} -> ${results.length}`); }
        console.log(differences ? `${differences} difference(s).` : 'IDENTICAL to the baseline.');
        process.exit(differences || uncovered.length ? 1 : 0);
    }
    process.exit(uncovered.length ? 1 : 0);
})().catch(e => { console.log('HARNESS ERROR ' + e.stack); child.kill(); process.exit(1); });
setTimeout(() => { console.log('GLOBAL TIMEOUT'); child.kill(); process.exit(1); }, 1_200_000);
