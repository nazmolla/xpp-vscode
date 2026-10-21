// End-to-end write test of the code view save path and the designer's form-extension calls (XvsTest* objects only).
const { spawn } = require('child_process');
const path = require('path');
const { composeSource, splitSource, methodName } = require(path.join(__dirname, '..', 'out', 'xppSource.js'));

const [exe, config] = process.argv.slice(2);
const child = spawn(exe, ['--config', config], { windowsHide: true });
let buffer = '';
const pending = new Map();
let id = 1;
child.stdout.on('data', d => {
  buffer += d.toString();
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i); buffer = buffer.slice(i + 1);
    if (line.trim()) { const msg = JSON.parse(line); pending.get(msg.id)?.(msg.result); }
  }
});
const rpc = (method, params) => new Promise(r => { const n = id++; pending.set(n, r); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n'); });
const bridge = async (action, body) => { const raw = await rpc('xpp/bridge', { action, body }); return typeof raw === 'string' ? JSON.parse(raw) : raw; };
const check = (label, ok, detail) => console.log(`${ok ? 'PASS' : 'FAIL'} ${label}${detail ? ' — ' + detail : ''}`);

(async () => {
  const timer = setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 400000);
  await rpc('initialize', {});

  // 1. Create a class with three methods.
  const cls = 'XvsTestCodeView';
  let r = await bridge('create_object', {
    objectType: 'AxClass', objectName: cls, modelName: 'testcust',
    declaration: `internal final class ${cls}\n{\n    int counter;\n}`,
    methods: [
      'public int first()\n{\n    return 1;\n}',
      '[SysObsolete("old", false)]\npublic int second()\n{\n    return 2;\n}',
      'public void third()\n{\n    counter++;\n}',
    ],
  });
  check('create class', r.success, r.message);

  // 2. Simulate an editor save: change first(), drop second(), add fourth().
  r = await bridge('read_object', { objectType: 'AxClass', objectName: cls });
  let text = composeSource(r.declaration, r.methods);
  text = text.replace('return 1;', 'return 10;');
  text = text.replace(/[ \t]*\[SysObsolete[\s\S]*?return 2;\n\s*\}\n*/, '');
  text = text.replace(/\}\n$/, '\n    public str fourth()\n    {\n        // has a { brace } and "a } string"\n        return "}";\n    }\n}\n');
  const split = splitSource(text, r.declaration);
  const names = split.methods.map(methodName);
  const removed = r.methods.map(m => m.name).filter(n => !names.includes(n));
  r = await bridge('update_object', { objectType: 'AxClass', objectName: cls, declaration: split.declaration, methods: split.methods, removeMethodNames: removed, formatCode: false });
  check('save edited source', r.success, r.message);

  r = await bridge('read_object', { objectType: 'AxClass', objectName: cls });
  const got = (r.methods || []).map(m => m.name).sort().join(',');
  check('methods after save', got === 'first,fourth,third', got);
  check('first() changed', (r.methods || []).some(m => m.name === 'first' && m.source.includes('return 10;')));
  check('fourth() kept braces in strings/comments', (r.methods || []).some(m => m.name === 'fourth' && m.source.includes('"a } string"')));

  // 3. Designer: new extension on a standard form, field at the end of a container, group after it, hide a control.
  const form = 'VendGroup';
  const tree = await bridge('get_form_tree', { formName: form });
  check('form tree', tree.success, tree.message);
  const containers = [];
  const walk = (n, parent) => { if (n.container && n.type !== 'Design' && n.type === 'Group') containers.push(n); n.children.forEach(c => walk(c, n)); };
  walk(tree.root, null);
  const target = containers[0];
  const ds = tree.dataSources[0];
  const fields = await bridge('get_table_fields', { tableName: ds.table });
  const field = fields.fields.find(f => f.name === 'Description') || fields.fields[0];
  const ext = `${form}.XvsTest`;
  r = await bridge('create_object', { objectType: 'AxFormExtension', objectName: ext, modelName: 'testcust',
    controls: [{ name: `${ds.name}_${field.name}_Xvs`, dataSource: ds.name, dataField: field.name, parent: target.name, position: 'end' }] });
  check(`add field ${ds.name}.${field.name} at end of ${target.name}`, r.success, r.message);
  r = await bridge('update_object', { objectType: 'AxFormExtension', objectName: ext,
    controls: [{ name: 'XvsTestGroup', controlType: 'Group', label: 'Test group', parent: target.name, position: 'after', previousSibling: `${ds.name}_${field.name}_Xvs` }] });
  check('add group after the new field', r.success, r.message);
  const firstBase = target.children.find(c => !c.addedBy);
  r = await bridge('update_object', { objectType: 'AxFormExtension', objectName: ext, controlModifications: [{ name: firstBase.name, properties: { Visible: 'No' } }] });
  check(`hide ${firstBase.name}`, r.success, r.message);

  const after = await bridge('get_form_tree', { formName: form });
  const find = (n, name) => n.name === name ? n : n.children.map(c => find(c, name)).find(Boolean);
  const placed = find(after.root, target.name).children.map(c => c.name);
  check('designer sees new controls in order', placed.slice(-2).join(',') === `${ds.name}_${field.name}_Xvs,XvsTestGroup`, placed.join(', '));
  check('designer marks them as added by the extension', find(after.root, 'XvsTestGroup')?.addedBy === ext);

  clearTimeout(timer);
  child.stdin.end();
  process.exit(0);
})();
