// Calls the server's xpp/bridge method: node bridge-call.js <exe> <config> <action> <bodyJsonOrFile> [maxChars]
const { spawn } = require('child_process');
const fs = require('fs');
const [exe, config, action, bodyArg, max] = process.argv.slice(2);
const body = fs.existsSync(bodyArg) ? fs.readFileSync(bodyArg, 'utf8').replace(/^\uFEFF/, '') : bodyArg;
const child = spawn(exe, ['--config', config], { windowsHide: true });
let out = '';
child.stdout.on('data', d => {
  out += d.toString();
  const lines = out.split('\n');
  for (const line of lines.slice(0, -1)) {
    if (!line.trim()) continue;
    const msg = JSON.parse(line);
    if (msg.id === 2) {
      const text = typeof msg.result === 'string' ? msg.result : JSON.stringify(msg.result);
      console.log(text.slice(0, Number(max || 3000)));
      console.log('... total chars: ' + text.length);
      child.stdin.end();
    }
  }
  out = lines[lines.length - 1];
});
child.on('exit', () => process.exit(0));
setTimeout(() => { console.log('TIMEOUT'); child.kill(); process.exit(1); }, 120000);
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } }) + '\n');
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'xpp/bridge', params: { action, body: JSON.parse(body) } }) + '\n');
