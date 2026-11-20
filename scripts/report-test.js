// Report designer and preview checks against a running server (needs the Report Viewer runtime installed):
//   node scripts/report-test.js <exe> <config> [report] [design] [outDir] [customModel]
// Reads the report's metadata and design, renders the first pages with sample data to PNG and the report to PDF, and checks
// the item map, labels and the helper-function stand-ins. With customModel, also runs the auto design workflow (data region,
// Create Precision Design) on a copy of InventPriceOverview in that model and deletes it. Prints PASS/FAIL per check.
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const [exe, config, report = 'Cheque_US', design = 'Report', outDir = path.join(os.tmpdir(), 'fos-report-test'), autoModel] = process.argv.slice(2);
if (!exe || !config) {
    console.error('Usage: node scripts/report-test.js <exe> <config> [report] [design] [outDir]');
    process.exit(2);
}
fs.mkdirSync(outDir, { recursive: true });

const child = spawn(exe, ['--config', config], { windowsHide: true });
let buffer = '';
const pending = new Map();
let nextId = 1;
child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines.filter(l => l.trim())) {
        const msg = JSON.parse(line);
        pending.get(msg.id)?.(msg);
        pending.delete(msg.id);
    }
});
const timer = setTimeout(() => { console.log('FAIL timeout'); child.kill(); process.exit(1); }, 300000);
function call(method, params) {
    const id = nextId++;
    return new Promise(resolve => {
        pending.set(id, resolve);
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, params }) + '\n');
    });
}
async function bridge(action, body) {
    const msg = await call('xpp/bridge', { action, body });
    return typeof msg.result === 'string' ? JSON.parse(msg.result) : msg.result;
}

let failures = 0;
function check(name, ok, detail = '') {
    console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${detail ? ' - ' + detail : ''}`);
    if (!ok) failures++;
}

(async () => {
    await call('initialize', { protocolVersion: '2025-06-18' });

    const info = await bridge('report_info', { reportName: report });
    check('report_info', info.success && info.designs.length > 0 && info.dataSets.length > 0,
        info.success ? `${info.designs.length} design(s), ${info.dataSets.length} dataset(s), ${info.parameters.length} parameter(s)` : info.message);

    const got = await bridge('get_report_design', { reportName: report, designName: design });
    check('get_report_design', got.success && got.rdl.length > 0 && got.items.length > 0,
        got.success ? `${got.items.length} items, page ${got.page.width} x ${got.page.height} mm, ${got.warnings.length} warning(s)` : got.message);
    if (got.success) {
        const outside = got.items.filter(i => i.x < 0 || i.y < 0 || i.x > got.page.width || i.y > got.page.height * 3);
        check('item map positions are on the page', outside.length === 0, outside.map(i => i.name).join(', '));
    }

    const t0 = Date.now();
    const rendered = await bridge('render_report', {
        reportName: report, designName: design, maxPages: 2, dpi: 100, sampleRows: 1, language: 'en-US',
        pdfPath: path.join(outDir, `${report}.${design}.pdf`),
    });
    check('render_report', rendered.success && rendered.pages.length > 0,
        rendered.success ? `${rendered.pages.length} of ${rendered.totalPages} page(s) in ${Date.now() - t0} ms` : rendered.message);
    if (rendered.success) {
        rendered.pages.forEach((png, i) => fs.writeFileSync(path.join(outDir, `${report}.${design}.page${i + 1}.png`), Buffer.from(png, 'base64')));
        check('PDF written', fs.existsSync(rendered.pdfPath) && fs.statSync(rendered.pdfPath).size > 1000, rendered.pdfPath);
        for (const w of rendered.warnings) {
            console.log(`  warning: ${w}`);
        }
        check('no expression errors', !rendered.warnings.some(w => /rsRuntimeErrorInExpression|rsCompilerError/i.test(w)));
    }

    // A label reference and a helper call in unsaved text are resolved by the preview.
    if (got.success) {
        const marker = got.rdl.replace(/<Value>[^<]*<\/Value>/, '<Value>=Labels!@SYS7149 &amp; " " &amp; Microsoft.Dynamics.Framework.Reports.BuiltInMethods.ToDisplayStringAmount("en-US", 1234.5, true)</Value>');
        const probe = await bridge('render_report', { reportName: report, rdl: marker, maxPages: 1, dpi: 50 });
        check('labels and helper stand-ins render', probe.success && !probe.warnings.some(w => /rsRuntimeErrorInExpression|rsCompilerError/i.test(w)),
            probe.success ? '' : probe.message);
    }

    const bad = await bridge('render_report', { reportName: report, rdl: '<NotAReport/>' });
    check('invalid design text is refused', !bad.success, bad.message);

    // Visual Studio's auto design workflow on a copy in the custom model: data region from a dataset, then Create Precision Design.
    if (autoModel) {
        const copy = 'XvsTestAutoDesign';
        await bridge('delete_object', { objectType: 'AxReport', objectName: copy });
        const dup = await bridge('duplicate_object', { objectType: 'AxReport', objectName: 'InventPriceOverview', newName: copy, modelName: autoModel });
        check('duplicate a report with an auto design', dup.success, dup.message);
        try {
            const copyInfo = await bridge('report_info', { reportName: copy });
            const dataSet = copyInfo.dataSets[0].name;
            const region = await bridge('add_report_data_region', { objectType: 'AxReport', objectName: copy, designName: 'ListDesign', dataSet, layout: 'List' });
            check('add_report_data_region creates an auto design with a list', region.success, region.message);
            const tree = await bridge('get_element_tree', { objectType: 'AxReport', objectName: copy });
            const designs = tree.root.children.find(c => c.name === 'Designs');
            const list = designs.children.find(d => d.name === 'ListDesign');
            const regionNode = list?.children.find(c => c.type === 'DataRegions')?.children[0];
            check('auto design tree: data region, Data with the fields, no size nodes',
                !!regionNode && regionNode.type === 'ReportList' && list.children.every(c => c.type !== 'ReportSize' && c.type !== 'ReportMargin')
                && regionNode.children.find(c => c.name === 'Data')?.children.some(c => c.type === 'Fields' && c.inline && c.children.length > 0),
                JSON.stringify(list?.children.map(c => c.type)));
            const props = await bridge('get_element_properties', { objectType: 'AxReport', objectName: copy, path: ['Designs', 'ListDesign'] });
            check('auto design page size shows as properties', props.success && props.properties.some(p => p.name === 'PageSize.Width'));
            const setWidth = await bridge('set_element_property', { objectType: 'AxReport', objectName: copy, path: ['Designs', 'ListDesign'], property: 'Margin.Left', value: '0.75in' });
            const after = await bridge('get_element_properties', { objectType: 'AxReport', objectName: copy, path: ['Designs', 'ListDesign'] });
            check('a sub-object property saves', setWidth.success && after.properties.some(p => p.name === 'Margin.Left' && p.value === '0.75in'), setWidth.message);

            const converted = await bridge('create_precision_design', { objectType: 'AxReport', objectName: copy, designName: 'Report', newDesignName: 'ReportPrecision' });
            check('create_precision_design generates the RDL', converted.success, converted.message);
            const precision = await bridge('get_report_design', { reportName: copy, designName: 'ReportPrecision' });
            check('the generated precision design has a tablix with the fields', precision.success && /<Tablix\b/.test(precision.rdl) && precision.items.length > 0,
                precision.success ? `${precision.items.length} items, ${precision.rdl.length} chars` : precision.message);
            const pprops = await bridge('get_element_properties', { objectType: 'AxReport', objectName: copy, path: ['Designs', 'ReportPrecision'] });
            check('the RDL text is not a property in the grid', pprops.success && !pprops.properties.some(p => p.name === 'Text'));
            const shown = await bridge('render_report', { reportName: copy, designName: 'ReportPrecision', maxPages: 1, dpi: 80, sampleRows: 3 });
            check('the generated precision design renders', shown.success && shown.pages.length > 0 && !shown.warnings.some(w => /rsRuntimeErrorInExpression|rsCompilerError/i.test(w)),
                shown.success ? shown.warnings.slice(0, 3).join(' | ') : shown.message);
            if (shown.success) fs.writeFileSync(path.join(outDir, `${copy}.ReportPrecision.png`), Buffer.from(shown.pages[0], 'base64'));
            const again = await bridge('create_precision_design', { objectType: 'AxReport', objectName: copy, designName: 'Report', newDesignName: 'ReportPrecision' });
            check('an existing design name is refused', !again.success, again.message);
            const standard = await bridge('create_precision_design', { objectType: 'AxReport', objectName: 'InventPriceOverview', designName: 'Report' });
            check('standard reports are refused', !standard.success, standard.message);
        } finally {
            await bridge('delete_object', { objectType: 'AxReport', objectName: copy });
        }

        // A report from scratch, as in Visual Studio: empty report, dataset whose Query is a report data provider, auto design, precision design.
        const blank = { objectType: 'AxReport', objectName: 'XvsTestBlankReport' };
        await bridge('delete_object', blank);
        const created = await bridge('create_object', { ...blank, modelName: autoModel });
        check('an empty report is created without the wizard', created.success, created.message);
        try {
            const emptyTree = await bridge('get_element_tree', blank);
            check('an empty report shows Datasets, Designs, Images, Parameters', emptyTree.root.children.map(c => c.name).join(',') === 'Datasets,Designs,Images,Parameters',
                emptyTree.root.children.map(c => c.name).join(','));
            const types = await bridge('get_node_types', { ...blank, path: ['Designs'] });
            check('New under Designs offers Auto Design and Precision Design', JSON.stringify(types.collections[0].types.map(t => t.label)) === '["Auto Design","Precision Design"]');
            await bridge('add_element_node', { ...blank, path: ['DataSets'], typeName: 'AxReportDataSet', name: 'DataSet1' });
            const dsProps = await bridge('get_element_properties', { ...blank, path: ['DataSets', 'DataSet1'] });
            check('dataset properties as Visual Studio shows them', dsProps.properties.map(p => `${p.category}:${p.label}`).sort().join(',')
                === 'Behavior:Default Layout,Behavior:Dynamic Filters,Data:Data Source Type,Data:Name,Data:Query,Data:Tags'
                && dsProps.properties.find(p => p.name === 'DynamicFilters').value === 'False', dsProps.properties.map(p => `${p.category}:${p.label}=${p.value}`).join(', '));
            const q = await bridge('set_report_dataset_query', { ...blank, dataSet: 'DataSet1', dataSourceType: 'ReportDataProvider', source: 'AssetBalancesDP.AssetBalancesTmp' });
            const blankInfo = await bridge('report_info', { reportName: blank.objectName });
            check('the Query fills the dataset fields and the report parameters', q.success && blankInfo.dataSets[0].fields.length > 10
                && ['AX_PartitionKey', 'AX_CompanyName', 'SysVersion'].every(n => blankInfo.parameters.some(p => p.name === n)), q.message);
            const region = await bridge('add_report_data_region', { ...blank, designName: 'AutoDesign1', dataSet: 'DataSet1', layout: 'Table' });
            check('a table named Table1 is added to a new auto design', region.success && region.region === 'Table1', region.message);
            const autoProps = await bridge('get_element_properties', { ...blank, path: ['Designs', 'AutoDesign1'] });
            check('auto design sizes are expandable properties, not nodes', autoProps.properties.some(p => p.kind === 'object' && p.label === 'Page Size')
                && autoProps.properties.some(p => p.parent === 'PageSize' && p.label === 'Width'));
            const precision = await bridge('add_element_node', { ...blank, path: ['Designs'], typeName: 'AxReportPrecisionDesign', name: 'PrecisionDesign1' });
            const opened = await bridge('get_report_design', { reportName: blank.objectName, designName: 'PrecisionDesign1' });
            check('a new precision design opens in the report designer as an empty page', precision.success && opened.success && opened.page.width > 200, opened.message);
            const pprops = await bridge('get_element_properties', { ...blank, path: ['Designs', 'PrecisionDesign1'] });
            check('a precision design shows no RDL text property', !pprops.properties.some(p => p.name === 'Text'));
        } finally {
            await bridge('delete_object', blank);
        }
    }

    console.log(`Output: ${outDir}`);
    clearTimeout(timer);
    child.stdin.end();
    process.exit(failures ? 1 : 0);
})().catch(error => {
    console.log('FAIL ' + error.message);
    child.kill();
    process.exit(1);
});
