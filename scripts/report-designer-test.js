// Report designer behaviour in a real browser engine: node scripts/report-designer-test.js
// Loads media/reportDesigner.js and .css into headless Edge with a synthetic precision design, edits it through mouse and
// keyboard events, saves (Ctrl+S) and checks the RDL that comes out. No server or environment needed.
const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync } = require('child_process');

const edge = [
    path.join(process.env['ProgramFiles(x86)'] || '', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
    path.join(process.env.ProgramFiles || '', 'Microsoft', 'Edge', 'Application', 'msedge.exe'),
].find(p => fs.existsSync(p));
if (!edge) {
    console.log('SKIP Microsoft Edge was not found');
    process.exit(0);
}

const rdl = `<?xml version="1.0" encoding="utf-8"?>
<Report xmlns="http://schemas.microsoft.com/sqlserver/reporting/2016/01/reportdefinition" xmlns:rd="http://schemas.microsoft.com/SQLServer/reporting/reportdesigner">
  <ReportSections>
    <ReportSection>
      <Body>
        <ReportItems>
          <Textbox Name="TbName">
            <CanGrow>true</CanGrow>
            <Paragraphs><Paragraph><TextRuns><TextRun><Value>=Fields!Name.Value</Value><Style><FontFamily>Segoe UI</FontFamily><FontSize>10pt</FontSize></Style></TextRun></TextRuns><Style /></Paragraph></Paragraphs>
            <Top>0.5in</Top><Left>1in</Left><Height>0.25in</Height><Width>2in</Width>
            <Style><Border><Style>None</Style></Border></Style>
          </Textbox>
          <Textbox Name="TbBelow">
            <Paragraphs><Paragraph><TextRuns><TextRun><Value>Fixed</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs>
            <Top>72pt</Top><Left>72pt</Left><Height>18pt</Height><Width>144pt</Width><ZIndex>1</ZIndex>
            <Style />
          </Textbox>
          <Rectangle Name="Rect1">
            <ReportItems>
              <Textbox Name="InRect">
                <Paragraphs><Paragraph><TextRuns><TextRun><Value>=Labels!@SYS7149</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs>
                <Top>2mm</Top><Left>2mm</Left><Height>5mm</Height><Width>30mm</Width>
                <Style />
              </Textbox>
            </ReportItems>
            <Top>40mm</Top><Left>10mm</Left><Height>20mm</Height><Width>60mm</Width><ZIndex>2</ZIndex>
            <Style><Border><Style>Solid</Style></Border></Style>
          </Rectangle>
          <Line Name="Line1"><Top>70mm</Top><Left>10mm</Left><Height>0mm</Height><Width>100mm</Width><ZIndex>3</ZIndex><Style><Border><Style>Solid</Style></Border></Style></Line>
          <Chart Name="Chart1"><ChartSeriesHierarchy /><Top>80mm</Top><Left>120mm</Left><Height>30mm</Height><Width>50mm</Width><ZIndex>4</ZIndex><CustomProperties><CustomProperty><Name>Keep</Name><Value>me</Value></CustomProperty></CustomProperties></Chart>
          <Tablix Name="Table1">
            <TablixBody>
              <TablixColumns><TablixColumn><Width>30mm</Width></TablixColumn><TablixColumn><Width>40mm</Width></TablixColumn></TablixColumns>
              <TablixRows>
                <TablixRow><Height>6mm</Height><TablixCells>
                  <TablixCell><CellContents><Textbox Name="H1"><Paragraphs><Paragraph><TextRuns><TextRun><Value>Item</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs><Style /></Textbox></CellContents></TablixCell>
                  <TablixCell><CellContents><Textbox Name="H2"><Paragraphs><Paragraph><TextRuns><TextRun><Value>Amount</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs><Style /></Textbox></CellContents></TablixCell>
                </TablixCells></TablixRow>
                <TablixRow><Height>5mm</Height><TablixCells>
                  <TablixCell><CellContents><Textbox Name="D1"><Paragraphs><Paragraph><TextRuns><TextRun><Value>=Fields!ItemId.Value</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs><Style /></Textbox></CellContents></TablixCell>
                  <TablixCell><CellContents><Textbox Name="D2"><Paragraphs><Paragraph><TextRuns><TextRun><Value>=Fields!Amount.Value</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs><Style /></Textbox></CellContents></TablixCell>
                </TablixCells></TablixRow>
              </TablixRows>
            </TablixBody>
            <TablixColumnHierarchy><TablixMembers><TablixMember /><TablixMember /></TablixMembers></TablixColumnHierarchy>
            <TablixRowHierarchy><TablixMembers><TablixMember><KeepWithGroup>After</KeepWithGroup></TablixMember><TablixMember><Group Name="Details" /></TablixMember></TablixMembers></TablixRowHierarchy>
            <DataSetName>Lines</DataSetName>
            <Top>120mm</Top><Left>10mm</Left><Height>11mm</Height><Width>70mm</Width><ZIndex>5</ZIndex>
          </Tablix>
        </ReportItems>
        <Height>200mm</Height>
        <Style />
      </Body>
      <Width>190mm</Width>
      <Page>
        <PageHeader><Height>15mm</Height><PrintOnFirstPage>true</PrintOnFirstPage><PrintOnLastPage>true</PrintOnLastPage>
          <ReportItems><Textbox Name="HdrTitle"><Paragraphs><Paragraph><TextRuns><TextRun><Value>=ucase(Labels!@SYS7149)</Value><Style /></TextRun></TextRuns><Style /></Paragraph></Paragraphs><Top>2mm</Top><Left>0mm</Left><Height>6mm</Height><Width>80mm</Width><Style /></Textbox></ReportItems>
          <Style /></PageHeader>
        <PageHeight>279.4mm</PageHeight><PageWidth>215.9mm</PageWidth>
        <LeftMargin>12mm</LeftMargin><RightMargin>12mm</RightMargin><TopMargin>10mm</TopMargin><BottomMargin>10mm</BottomMargin>
        <Style />
      </Page>
    </ReportSection>
  </ReportSections>
  <CustomProperties><CustomProperty><Name>AXDataType</Name><Value>keep</Value></CustomProperty></CustomProperties>
  <rd:ReportUnitType>Inch</rd:ReportUnitType>
  <rd:ReportID>11111111-2222-3333-4444-555555555555</rd:ReportID>
</Report>`;

const design = {
    success: true, rdl, isCustom: true, labels: { '@SYS7149': 'Customer' },
    fonts: [{ name: 'Segoe UI', installed: true }, { name: 'MICR E13B 2.1', installed: false }],
    images: {}, warnings: [],
    dataSets: [{ name: 'Header', dataSourceType: 'ReportDataProvider', fields: [{ name: 'Name', type: 'System.String' }] },
        { name: 'Lines', dataSourceType: 'ReportDataProvider', fields: [{ name: 'ItemId', type: 'System.String' }, { name: 'Amount', type: 'System.Double' }] }],
    parameters: [],
};
const info = { success: true, name: 'TestReport', model: 'testcust', isCustom: true, designs: [{ name: 'Report', kind: 'precision' }] };

const script = fs.readFileSync(path.join(__dirname, '..', 'media', 'reportDesigner.js'), 'utf8');
const css = fs.readFileSync(path.join(__dirname, '..', 'media', 'reportDesigner.css'), 'utf8');

const checks = String.raw`
const posted = window.__posted;
const results = [];
window.addEventListener('error', e => { results.push('FAIL page error: ' + e.message + ' line ' + e.lineno); document.getElementById('results').textContent = results.join('\n'); });
const check = (name, fn) => { try { results.push((fn() ? 'PASS ' : 'FAIL ') + name); } catch (e) { results.push('FAIL ' + name + ' (' + e.message + ')'); } };
const q = s => document.querySelector(s);
const qa = s => Array.from(document.querySelectorAll(s));
const item = name => qa('.it').find(d => d.title.endsWith(' ' + name));
const PX = 96 / 25.4;
const send = data => window.dispatchEvent(new MessageEvent('message', { data }));
const wait = ms => new Promise(r => setTimeout(r, ms));
function mouse(target, type, x, y, opts) {
    const ev = new MouseEvent(type, Object.assign({ bubbles: true, cancelable: true, clientX: x, clientY: y, button: 0 }, opts || {}));
    (type === 'mousedown' ? target : window).dispatchEvent(ev);
}
function key(k, opts) { q('.surface').dispatchEvent(new KeyboardEvent('keydown', Object.assign({ key: k, bubbles: true, cancelable: true }, opts || {}))); }
function saved() {
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true, cancelable: true }));
    const m = posted.filter(p => p.type === 'save').pop();
    return new DOMParser().parseFromString(m.rdl, 'application/xml');
}
const byName = (doc, name) => Array.from(doc.getElementsByTagName('*')).find(e => e.getAttribute('Name') === name);
const val = (el, tag) => { const c = Array.from(el.children).find(x => x.localName === tag); return c ? c.textContent : null; };
function center(div) { const r = div.getBoundingClientRect(); return { x: r.left + r.width / 2, y: r.top + r.height / 2 }; }
function clickItem(name, opts) { const c = center(item(name)); mouse(item(name), 'mousedown', c.x, c.y, opts); mouse(window, 'mouseup', c.x, c.y); }

(async () => {
    send({ type: 'load', info: ${JSON.stringify(info)}, design: ${JSON.stringify(design)}, state: { report: 'TestReport', design: 'Report' } });
    await wait(50);
    check('the design draws every item', () => ['TbName', 'TbBelow', 'Rect1', 'InRect', 'Line1', 'Chart1', 'H1', 'D2', 'HdrTitle'].every(n => !!item(n)));
    check('fields show as «Field»', () => item('TbName').textContent.includes('\u00abName\u00bb'));
    check('labels show their text', () => item('InRect').textContent.includes('Customer') && item('HdrTitle').textContent.includes('CUSTOMER'));
    check('items sit at their position (mm to px)', () => {
        const sheet = q('.sheet').getBoundingClientRect(), r = item('TbBelow').getBoundingClientRect();
        // 72pt = 1in = 25.4 mm inside the body, which starts at the left margin (12 mm) and below the header (10 + 15 mm).
        return Math.abs(r.left - sheet.left - (12 + 25.4) * PX) < 1.5 && Math.abs(r.top - sheet.top - (25 + 25.4) * PX) < 1.5;
    });
    check('a growing textbox above another is warned about', () => /TbName can grow and pushes TbBelow/.test(q('.warnings').textContent));
    check('unknown report items are drawn as placeholders', () => item('Chart1').classList.contains('placeholder'));

    clickItem('TbName');
    check('a click selects an item', () => qa('.sel').length === 1 && /TbName/.test(q('.prop-title').textContent));
    key('ArrowRight');
    let doc = saved();
    check('arrow moves by the grid (1 mm) and keeps inches', () => /^1\.03937in$/.test(val(byName(doc, 'TbName'), 'Left')));
    key('ArrowRight', { ctrlKey: true });
    doc = saved();
    check('Ctrl+arrow moves 0.1 mm', () => Math.abs(parseFloat(val(byName(doc, 'TbName'), 'Left')) * 25.4 - 26.5) < 0.01);

    clickItem('TbBelow');
    const c = center(item('TbBelow'));
    mouse(item('TbBelow'), 'mousedown', c.x, c.y);
    mouse(window, 'mousemove', c.x + 10 * PX, c.y + 5 * PX);
    mouse(window, 'mouseup', c.x + 10 * PX, c.y + 5 * PX);
    doc = saved();
    check('dragging moves by the mouse and keeps points', () => {
        const left = val(byName(doc, 'TbBelow'), 'Left'), top = val(byName(doc, 'TbBelow'), 'Top');
        return /pt$/.test(left) && Math.abs(parseFloat(left) / 72 * 25.4 - 35.4) < 0.6 && Math.abs(parseFloat(top) / 72 * 25.4 - 30.4) < 0.6;
    });
    const handle = q('.handle-e').getBoundingClientRect();
    mouse(q('.handle-e'), 'mousedown', handle.left + 4, handle.top + 4);
    mouse(window, 'mousemove', handle.left + 4 + 20 * PX, handle.top + 4);
    mouse(window, 'mouseup', handle.left + 4 + 20 * PX, handle.top + 4);
    doc = saved();
    check('the east handle widens the item', () => Math.abs(parseFloat(val(byName(doc, 'TbBelow'), 'Width')) / 72 * 25.4 - 70.8) < 0.6);

    qa('.toolbar .tb').find(b => /Add Textbox/.test(b.title)).click();
    const body = q('.section-Body').getBoundingClientRect();
    mouse(q('.section-Body'), 'mousedown', body.left + 150 * PX, body.top + 10 * PX);
    doc = saved();
    const added = Array.from(doc.getElementsByTagName('Textbox')).find(t => /^Textbox\d+$/.test(t.getAttribute('Name')));
    check('a new textbox is placed where clicked, fixed and without padding', () => added && val(added, 'CanGrow') === 'false'
        && Math.abs(parseFloat(val(added, 'Left')) * 25.4 - 150) < 0.6 && /0pt/.test(added.getElementsByTagName('PaddingLeft')[0].textContent));

    clickItem('H2');
    check('a tablix cell selects its textbox and shows cell actions', () => /H2/.test(q('.prop-title').textContent) && !!qa('.props .tb').find(b => /Insert column right/.test(b.title)));
    qa('.props .tb').find(b => /Insert column right/.test(b.title)).click();
    doc = saved();
    const t1 = byName(doc, 'Table1');
    check('a column is added to columns, every row and the column hierarchy', () => t1.getElementsByTagName('TablixColumn').length === 3
        && Array.from(t1.getElementsByTagName('TablixRow')).every(r => r.getElementsByTagName('TablixCell').length === 3)
        && t1.getElementsByTagName('TablixColumnHierarchy')[0].getElementsByTagName('TablixMember').length === 3);
    clickItem('H1');
    qa('.props .tb').find(b => /Insert row below/.test(b.title)).click();
    doc = saved();
    check('a fixed row is added below the header row', () => byName(doc, 'Table1').getElementsByTagName('TablixRow').length === 3
        && byName(doc, 'Table1').getElementsByTagName('TablixRowHierarchy')[0].getElementsByTagName('TablixMember').length === 3);
    clickItem('D1');
    const before = byName(saved(), 'Table1').getElementsByTagName('TablixRow').length;
    qa('.props .tb').find(b => /Insert row below/.test(b.title)).click();
    check('rows inside a group are left to the RDL', () => byName(saved(), 'Table1').getElementsByTagName('TablixRow').length === before);

    clickItem('InRect');
    key('Delete');
    doc = saved();
    check('deleting the last item of a rectangle removes its empty ReportItems', () => !byName(doc, 'InRect') && byName(doc, 'Rect1').getElementsByTagName('ReportItems').length === 0);
    key('z', { ctrlKey: true });
    doc = saved();
    check('undo brings it back', () => !!byName(doc, 'InRect'));

    check('elements the designer does not edit are kept', () => {
        const d = saved();
        const chart = byName(d, 'Chart1');
        return !!chart && chart.getElementsByTagName('ChartSeriesHierarchy').length === 1 && /me/.test(chart.textContent)
            && /AXDataType/.test(new XMLSerializer().serializeToString(d)) && /11111111-2222/.test(new XMLSerializer().serializeToString(d));
    });
    check('the XML declaration and namespace are kept', () => {
        const m = posted.filter(p => p.type === 'save').pop();
        return m.rdl.startsWith('<?xml') && m.rdl.includes('reporting/2016/01/reportdefinition') && !m.rdl.includes('xmlns=""');
    });

    key('Escape');
    const width = qa('.props .prop').find(p => /^Width/.test(p.textContent) && p.closest('fieldset').querySelector('legend').textContent.startsWith('Body'));
    const input = width.querySelector('input');
    input.value = '200';
    input.dispatchEvent(new Event('change'));
    check('a body wider than the page is warned about', () => /wider than the page/.test(q('.warnings').textContent));

    await wait(400);
    check('changes are reported as unsaved', () => posted.some(p => p.type === 'dirty' && p.dirty === true));

    send({ type: 'load', info: Object.assign({}, ${JSON.stringify(info)}, { isCustom: false, model: 'Foundation' }), design: ${JSON.stringify(design)}, state: { report: 'TestReport', design: 'Report' } });
    await wait(20);
    check('a standard report opens read-only with a duplicate offer', () => !!q('.banner') && /Duplicate/.test(q('.banner').textContent));
    clickItem('TbName');
    key('ArrowRight');
    const before2 = posted.length;
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true, bubbles: true }));
    check('read-only designs are not changed or saved', () => posted.length === before2);

    // Auto designs, as Visual Studio: choosing one asks the extension to create a precision design from it.
    const withAuto = Object.assign({}, ${JSON.stringify(info)}, { designs: [{ name: 'Report', kind: 'precision' }, { name: 'AutoReport', kind: 'auto' }] });
    send({ type: 'load', info: withAuto, design: ${JSON.stringify(design)}, state: { report: 'TestReport', design: 'Report' } });
    await wait(20);
    const select = q('.design-select');
    select.value = 'AutoReport';
    select.dispatchEvent(new Event('change'));
    check('choosing an auto design asks to create a precision design and keeps the current one selected',
        () => posted.some(p => p.type === 'convertDesign' && p.design === 'AutoReport') && select.value === 'Report');
    send({ type: 'load', info: Object.assign({}, withAuto, { designs: [{ name: 'AutoReport', kind: 'auto' }] }), design: null, state: { report: 'TestReport' } });
    await wait(20);
    const convert = qa('button').find(b => /Create precision design from AutoReport/.test(b.textContent));
    if (convert) convert.click();
    check('a report with only an auto design offers to create a precision design from it',
        () => !!convert && posted.filter(p => p.type === 'convertDesign' && p.design === 'AutoReport').length === 2);
    document.getElementById('results').textContent = results.join('\n');
})().catch(e => { results.push('FAIL test stopped: ' + e.message + ' ' + (e.stack || '').split('\n')[1]); document.getElementById('results').textContent = results.join('\n'); });
window.addEventListener('error', e => { results.push('FAIL page error: ' + e.message + ' line ' + e.lineno); document.getElementById('results').textContent = results.join('\n'); });
`;
const html = `<!DOCTYPE html><html><head><meta charset="UTF-8"><style>${css}</style></head>
<body style="margin:0;width:1400px;height:900px" data-xpp-state="{}"><div id="app" class="app"></div><pre id="results">RUNNING</pre>
<script>window.__posted = []; window.acquireVsCodeApi = () => ({ postMessage: m => window.__posted.push(m), getState: () => undefined, setState: () => undefined });</script>
<script>${script}</script><script>${checks}</script></body></html>`;

const file = path.join(os.tmpdir(), `report-designer-test-${process.pid}.html`);
fs.writeFileSync(file, html);
let dom;
try {
    dom = execFileSync(edge, ['--headless', '--disable-gpu', '--no-first-run', `--user-data-dir=${path.join(os.tmpdir(), 'report-designer-test-edge')}`,
        '--window-size=1400,900', '--virtual-time-budget=10000', '--dump-dom', 'file:///' + file.replace(/\\/g, '/')], { timeout: 60_000, maxBuffer: 1 << 26 }).toString();
} finally {
    fs.rmSync(file, { force: true });
}
const results = (/<pre id="results">([\s\S]*?)<\/pre>/.exec(dom)?.[1] ?? 'no results').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"');
console.log(results);
const failed = results.split('\n').filter(l => !l.startsWith('PASS')).length;
console.log(failed ? `${failed} failed` : 'all passed');
process.exit(failed ? 1 : 0);
