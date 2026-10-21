// Shows where splitting a composed object goes wrong: node split-debug.js <exe> <config> <type> <name>
const { spawn } = require('child_process');
const path = require('path');
const { composeSource, splitSource, methodName } = require(path.join(__dirname, '..', 'out', 'xppSource.js'));
const [exe, config, objectType, objectName] = process.argv.slice(2);
const child = spawn(exe, ['--config', config], { windowsHide: true });
let out = '';
child.stdout.on('data', d => {
  out += d.toString();
  const lines = out.split('\n');
  for (const line of lines.slice(0, -1)) {
    if (!line.trim()) continue;
    const msg = JSON.parse(line);
    if (msg.id !== 2) continue;
    const r = msg.result;
    const text = composeSource(r.declaration || '', r.methods || []);
    const split = splitSource(text, r.declaration);
    const norm = s => (s || '').replace(/\r\n/g, '\n').trim();
    const a = norm(r.declaration), b = norm(split.declaration);
    let k = 0; while (k < a.length && a[k] === b[k]) k++;
    console.log('declaration differs at', k, '\nORIGINAL:', JSON.stringify(a.slice(Math.max(0, k - 150), k + 250)), '\nSPLIT:', JSON.stringify(b.slice(Math.max(0, k - 150), k + 250)));
    console.log('methods in:', (r.methods || []).length, 'out:', split.methods.length);
    split.methods.slice(0, 3).forEach((m, i) => console.log(`--- out ${i} (${methodName(m)}), ${m.length} chars:\n${m.slice(0, 300)}\n...\n${m.slice(-300)}`));
    const names = (r.methods || []).slice(0, 4).map(m => m.name);
    console.log('first input methods:', names.join(', '));
    child.stdin.end();
  }
  out = lines[lines.length - 1];
});
child.on('exit', () => process.exit(0));
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: {} }) + '\n');
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'xpp/bridge', params: { action: 'read_object', body: { objectType, objectName } } }) + '\n');
