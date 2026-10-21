// Round-trips real objects through composeSource/splitSource: node roundtrip.js <exe> <config> Type:Name ...
const { spawn } = require('child_process');
const path = require('path');
const { composeSource, splitSource, methodName, methodSpans } = require(path.join(__dirname, '..', 'out', 'xppSource.js'));

const [exe, config, ...objects] = process.argv.slice(2);
const child = spawn(exe, ['--config', config], { windowsHide: true });
let buffer = '';
const pending = new Map();
let id = 1;
child.stdout.on('data', d => {
  buffer += d.toString();
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i); buffer = buffer.slice(i + 1);
    if (!line.trim()) continue;
    const msg = JSON.parse(line);
    pending.get(msg.id)?.(msg.result);
  }
});
const call = (method, params) => new Promise(r => { const n = id++; pending.set(n, r); child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n'); });
// Compares text without the uniform method indent the code view adds to sources stored at column 0.
const norm = s => {
  const lines = (s || '').replace(/\r\n/g, '\n').trim().split('\n');
  const indents = lines.slice(1).filter(l => l.trim()).map(l => l.length - l.trimStart().length);
  const cut = Math.min(4, ...indents, 4);
  return lines.map((l, i) => i === 0 ? l : l.slice(Math.min(cut, l.length - l.trimStart().length))).join('\n');
};

(async () => {
  const timer = setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 240000);
  await call('initialize', {});
  let failures = 0;
  for (const spec of objects) {
    const [objectType, objectName] = spec.split(':');
    const raw = await call('xpp/bridge', { action: 'read_object', body: { objectType, objectName } });
    const r = typeof raw === 'string' ? JSON.parse(raw) : raw;
    if (!r.success) { console.log(`SKIP ${spec}: ${r.message}`); continue; }
    const methods = r.methods || [];
    const text = composeSource(r.declaration || '', methods);
    const declIsMethod = !norm(r.declaration) && (methods[0]?.name || '').toLowerCase() === 'classdeclaration';
    let problem = null;
    try {
      const split = splitSource(text, declIsMethod ? methods[0].source : r.declaration);
      const back = declIsMethod ? { declaration: '', methods: [split.declaration, ...split.methods] } : split;
      if (norm(back.declaration) !== norm(r.declaration)) problem = 'declaration differs';
      else if (back.methods.length !== methods.length) problem = `method count ${back.methods.length} vs ${methods.length}`;
      else {
        for (let k = 0; k < methods.length; k++) {
          if (norm(back.methods[k]) !== norm(methods[k].source)) { problem = `method ${methods[k].name} source differs`; break; }
          if (methodName(back.methods[k]).toLowerCase() !== methods[k].name.toLowerCase()) { problem = `method name '${methodName(back.methods[k])}' vs '${methods[k].name}'`; break; }
        }
        const spans = methodSpans(text);
        if (!problem && spans.length !== methods.length - (declIsMethod ? 1 : 0)) problem = `spans ${spans.length}`;
      }
    } catch (e) { problem = 'split threw: ' + e.message; }
    if (problem) failures++;
    console.log(`${problem ? 'FAIL' : 'PASS'} ${spec}: ${methods.length} methods, ${text.length} chars${problem ? ' — ' + problem : ''}`);
  }
  console.log(`failures: ${failures}`);
  clearTimeout(timer);
  child.stdin.end();
  process.exit(0);
})();
