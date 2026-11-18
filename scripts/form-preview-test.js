// Form preview behaviour in a real browser engine: node scripts/form-preview-test.js
// Loads media/formPreview.js and .css into headless Edge with a synthetic form, clicks through it in the page and reads
// the PASS/FAIL lines back with --dump-dom. No server or environment needed.
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

let id = 0;
const n = (type, name, props = {}, children = []) => ({ type, name: name || `${type}${++id}`, visible: 'Yes', children, ...props });
const field = (name, label, props = {}) => n('String', name, { label, ...props });

// A details form (list + details), with what each check needs.
const form = {
    form: 'TestForm', caption: 'Test records', pattern: 'DetailsMaster',
    dataSources: [{ name: 'T', table: 'T' }],
    root: n('Design', 'Design', { pattern: 'DetailsMaster', style: 'DetailsFormMaster' }, [
        n('ActionPane', 'Pane', {}, [
            n('ButtonGroup', 'RootGroup', {}, [n('CommandButton', 'DeleteButton', { command: 'DeleteRecord' })]),
            n('ActionPaneTab', 'TabOne', { label: 'One' }, [
                n('ButtonGroup', 'GroupA', { label: 'Group A' }, [
                    n('MenuButton', 'Menu', { label: 'Contacts' }, [n('MenuFunctionButton', 'View', { label: 'View contacts' }), n('MenuFunctionButton', 'Add', { label: 'Add contacts' })]),
                    n('MenuFunctionButton', 'B2', { label: 'Two' }), n('MenuFunctionButton', 'B3', { label: 'Three' }), n('MenuFunctionButton', 'B4', { label: 'Four' }),
                    n('MenuFunctionButton', 'HiddenButton', { label: 'Hidden', visible: 'No' }),
                ]),
            ]),
            n('ActionPaneTab', 'TabTwo', { label: 'Two' }, [n('ButtonGroup', 'GroupB', { label: 'Group B' }, [n('MenuFunctionButton', 'Keyed', { label: 'Keyed', configurationKey: 'Rental', addedBy: 'RentalManagement' })])]),
        ]),
        n('Tab', 'Main', { style: 'Tabs' }, [
            n('TabPage', 'PageDetails', { style: 'DetailsFormDetails' }, [
                n('Group', 'Title', { style: 'DetailTitleContainer' }, [field('TitleField', 'Title')]),
                n('Tab', 'FastTabs', { style: 'FastTabs' }, [
                    n('TabPage', 'General', { label: 'General', columns: '1' }, [
                        n('Group', 'Upper', { columns: '-1', arrangeMethod: 'Vertical' }, [
                            n('Group', 'Left', { label: 'Left', columns: '1' }, [field('A', 'A', { fastTabSummary: true }), field('B', 'B')]),
                            n('Group', 'Right', { label: 'Right', columns: '1' }, [field('C', 'C')]),
                        ]),
                        n('Group', 'Stacked', { columns: '1' }, [n('Group', 'S1', { columns: '1', frameType: 'None' }, [field('D', 'D')]), n('Group', 'S2', { columns: '1', frameType: 'None' }, [field('E', 'E')])]),
                        n('Group', 'Across', { columns: '1', arrangeMethod: 'HorizontalLeft' }, [field('F', 'F'), field('G', 'G')]),
                        n('Group', 'Sizes', { columns: '1' }, [
                            field('Wide', 'Wide', { widthMode: 'Manual', width: 400 }),
                            field('Chars', 'Chars', { displayLength: 40 }),
                            field('Notes', 'Notes', { multiLine: true, displayHeight: 5 }),
                        ]),
                    ]),
                    n('TabPage', 'Second', { label: 'Second' }, [field('H', 'H', { fastTabSummary: true })]),
                ]),
            ]),
            n('TabPage', 'PageGrid', { style: 'DetailsFormGrid' }, [
                n('Grid', 'Grid', { visibleRows: 6 }, [field('Id', 'Id'), field('Name', 'Name')]),
            ]),
        ]),
    ]),
};

const script = fs.readFileSync(path.join(__dirname, '..', 'media', 'formPreview.js'), 'utf8');
const css = fs.readFileSync(path.join(__dirname, '..', 'media', 'formPreview.css'), 'utf8');
const checks = `
const results = [];
const check = (name, fn) => { try { const ok = fn(); results.push((ok ? 'PASS ' : 'FAIL ') + name); } catch (e) { results.push('FAIL ' + name + ': ' + e.message); } };
const target = document.getElementById('target');
let selected = null;
const preview = window.XppFormPreview({ onSelect: n => { selected = n; }, selectedName: () => selected });
preview.render(target, FORM);
const q = s => target.querySelector(s);
const qa = s => [...target.querySelectorAll(s)];
const byName = name => q('[data-name="' + name + '"]');
const rect = e => e.getBoundingClientRect();

check('details form opens on the list (grid) view', () => !!byName('Grid') && !q('.fo-breadcrumb') && !byName('General'));
check('grid shows VisibleRows placeholder rows', () => qa('.grid tr').length === 1 + 6);
check('root buttons sit in the toolbar with their command caption', () => /Delete/.test(q('.ap-toolbar').textContent));
check('toolbar dividers are visible', () => qa('.ap-divider').every(d => rect(d).width >= 1));
check('hidden controls are not shown', () => !byName('HiddenButton'));
check('button groups lay out in columns of three', () => {
    const items = qa('[data-name="GroupA"] .ap-button').map(rect);
    return items.length === 4 && items[0].left === items[2].left && items[3].left > items[0].left;
});
check('menu button opens its list below it', () => {
    q('[data-menu="Menu"]').click();
    const menu = q('.fo-menu');
    return !!menu && /View contacts/.test(menu.textContent) && rect(menu).top >= rect(q('[data-menu="Menu"]')).bottom - 1;
});
check('a click outside closes the list', () => { target.querySelector('.form-header').click(); return true; });
const afterClose = () => new Promise(r => setTimeout(r, 20));
(async () => {
    await afterClose();
    check('… and the list is closed', () => !q('.fo-menu'));
    check('action pane tabs switch', () => { qa('.ap-tab').find(t => t.dataset.name === 'TabTwo').click(); return !!byName('GroupB') && !byName('GroupA'); });
    check('configuration keys are named in the tooltip', () => byName('Keyed').title.includes('configuration key Rental'));
    check('no markers by default', () => !q('.keyed') && !q('.added'));
    check('markers show keyed and extension controls', () => { preview.setMarkers(true); return !!q('.keyed') && !!q('.added'); });
    check('markers show hidden controls', () => { qa('.ap-tab').find(t => t.dataset.name === 'TabOne').click(); return !!byName('HiddenButton') && byName('HiddenButton').classList.contains('hidden'); });
    preview.setMarkers(false);
    check('the record link opens the details view', () => { q('.grid-bar.link').click(); return !!q('.fo-breadcrumb') && !!byName('General'); });
    check('title container is not drawn as fields', () => !byName('TitleField'));
    check('only the first fast tab is open', () => byName('General').classList.contains('open') && !byName('Second').classList.contains('open') && !byName('H'));
    check('fast tab headers show summary fields', () => byName('Second').querySelector('.fasttab-summary-value').title === 'H: (blank)');
    check('a fast tab opens on click', () => { byName('Second').querySelector('.fasttab-header').click(); return byName('Second').classList.contains('open') && !!byName('H'); });
    check('a fast tab closes on click', () => { byName('Second').querySelector('.fasttab-header').click(); return !byName('Second').classList.contains('open') && !byName('H'); });
    check('groups with Columns Auto sit side by side', () => rect(byName('Right')).left > rect(byName('Left')).left && Math.abs(rect(byName('Right')).top - rect(byName('Left')).top) < 2);
    check('groups with one column stack', () => rect(byName('E')).top > rect(byName('D')).top && Math.abs(rect(byName('E')).left - rect(byName('D')).left) < 2);
    check('ArrangeMethod HorizontalLeft puts fields in a row', () => rect(byName('G')).left > rect(byName('F')).left && Math.abs(rect(byName('G')).top - rect(byName('F')).top) < 2);
    check('Manual Width is pixels', () => Math.round(rect(byName('Wide')).width) === 400);
    check('DisplayLength widens the field', () => rect(byName('Chars')).width > rect(byName('B')).width + 100);
    check('multi-line text is DisplayHeight lines high', () => rect(byName('Notes').querySelector('.input')).height >= 100);
    check('selecting a control in the tree opens its closed fast tab', () => { preview.select('H'); return !!byName('H') && byName('Second').classList.contains('open'); });
    check('selecting a grid control returns to the list', () => { preview.select('Name'); return !!byName('Grid'); });
    document.getElementById('results').textContent = results.join('\\n');
})();
`;
const html = `<!DOCTYPE html><html><head><meta charset="UTF-8"><style>${css}</style></head>
<body style="margin:0;width:1400px"><div id="target"></div><pre id="results">RUNNING</pre>
<script>const FORM = ${JSON.stringify(form)};</script><script>${script}</script><script>${checks}</script></body></html>`;

const file = path.join(os.tmpdir(), `form-preview-test-${process.pid}.html`);
fs.writeFileSync(file, html);
let dom;
try {
    dom = execFileSync(edge, ['--headless', '--disable-gpu', '--no-first-run', `--user-data-dir=${path.join(os.tmpdir(), 'form-preview-test-edge')}`,
        '--window-size=1400,1000', '--virtual-time-budget=5000', '--dump-dom', 'file:///' + file.replace(/\\/g, '/')], { timeout: 60_000, maxBuffer: 1 << 26 }).toString();
} finally {
    fs.rmSync(file, { force: true });
}
const results = (/<pre id="results">([\s\S]*?)<\/pre>/.exec(dom)?.[1] ?? 'no results').replace(/&amp;/g, '&').replace(/&lt;/g, '<').replace(/&gt;/g, '>');
console.log(results);
const failed = results.split('\n').filter(l => !l.startsWith('PASS')).length;
console.log(failed ? `${failed} failed` : 'all passed');
process.exit(failed ? 1 : 0);
