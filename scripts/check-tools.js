// Checks that tools/list is valid JSON and every tool has an object input schema: node check-tools.js <exe> <config>
const { spawn } = require('child_process');

const [exe, config] = process.argv.slice(2);
const child = spawn(exe, ['--config', config], { windowsHide: true });
let buffer = '';
const timer = setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 240000);
child.stdout.on('data', d => {
  buffer += d.toString();
  const i = buffer.indexOf('\n');
  if (i < 0) return;
  const line = buffer.slice(0, i);
  clearTimeout(timer);
  let tools;
  try {
    tools = JSON.parse(line).result.tools;
  } catch (e) {
    const m = /position (\d+)/.exec(e.message);
    console.log('FAIL tools/list is not valid JSON: ' + e.message);
    if (m) console.log(line.substr(Math.max(0, +m[1] - 150), 300));
    process.exit(1);
  }
  const bad = tools.filter(t => !t.inputSchema || t.inputSchema.type !== 'object' || !t.description);
  for (const t of bad) console.log('FAIL ' + t.name + ': missing description or object input schema');
  console.log(`${bad.length ? 'FAIL' : 'PASS'} ${tools.length} tools: ${tools.map(t => t.name).join(' ')}`);
  process.exit(bad.length ? 1 : 0);
});
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'tools/list', params: {} }) + '\n');
