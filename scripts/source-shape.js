// Prints how declarations and method sources are stored: node source-shape.js <exe> <config> <type> <name>
const { spawn } = require('child_process');
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
    console.log('declaration:', JSON.stringify(r.declaration));
    for (const m of (r.methods || []).slice(0, 3)) console.log(`method ${m.name}:`, JSON.stringify(m.source.slice(0, 160)), '... ends', JSON.stringify(m.source.slice(-20)));
    child.stdin.end();
  }
  out = lines[lines.length - 1];
});
child.on('exit', () => process.exit(0));
setTimeout(() => { console.log('TIMEOUT'); child.kill(); process.exit(1); }, 120000);
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } }) + '\n');
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'xpp/bridge', params: { action: 'read_object', body: { objectType, objectName } } }) + '\n');
