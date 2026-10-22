// Regression check for the agent (MCP) tools against the built server and the active D365FO configuration.
// Usage: node scripts/tool-regression.js [--project <path to .rnrproj>] [--cases <json file>] [--issue <regex on the issue tag>]
// Each case: { name, arguments, expect: regex the response must match, isError?: expected error flag },
// or { method, params, ... } to send a raw JSON-RPC request (the response JSON is matched).
// Probe objects created by the cases are deleted by later cases; run against a project of your own custom model.
const { spawn, execSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const argv = process.argv.slice(2);
const opt = name => { const i = argv.indexOf(name); return i >= 0 ? argv[i + 1] : undefined; };
const root = path.resolve(__dirname, '..');
const casesFile = opt('--cases') ?? path.join(__dirname, 'tool-regression.json');
const project = opt('--project');
const issueFilter = opt('--issue') ? new RegExp(opt('--issue')) : null;
const cases = JSON.parse(fs.readFileSync(casesFile, 'utf8')).filter(c => !issueFilter || issueFilter.test(c.issue ?? ''));

const reg = execSync('reg query "HKCU\\Software\\Microsoft\\Dynamics\\AX7\\Development\\Configurations" /v CurrentMetadataConfig', { encoding: 'utf8' });
const config = /CurrentMetadataConfig\s+REG_SZ\s+(.+)/.exec(reg)?.[1]?.trim();
if (!config) { console.log('No active D365FO configuration.'); process.exit(2); }

const args = ['--config', config, ...(project ? ['--project', project] : [])];
const child = spawn(path.join(root, 'server', 'bin', 'XppVsCode.Server.exe'), args, { windowsHide: true });
let buffer = '';
const pending = new Map();
child.stdout.on('data', d => {
  buffer += d.toString();
  let i;
  while ((i = buffer.indexOf('\n')) >= 0) {
    const line = buffer.slice(0, i).trim();
    buffer = buffer.slice(i + 1);
    if (!line) continue;
    const msg = JSON.parse(line);
    pending.get(msg.id)?.(msg);
    pending.delete(msg.id);
  }
});
let id = 0;
const call = (method, params) => new Promise(resolve => {
  const n = ++id;
  pending.set(n, resolve);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }) + '\n');
});

(async () => {
  const timer = setTimeout(() => { console.log('TIMEOUT'); process.exit(1); }, 15 * 60 * 1000);
  await call('initialize', { protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'tool-regression', version: '1' } });
  let failed = 0;
  for (const c of cases) {
    // A case with "method" sends that JSON-RPC request directly (e.g. xpp/bridge actions used by the extension UI).
    const res = c.method ? await call(c.method, c.params ?? {}) : await call('tools/call', { name: c.name, arguments: c.arguments ?? {} });
    const text = res.error ? 'RPC ERROR ' + res.error.message
      : c.method ? (typeof res.result === 'string' ? res.result : JSON.stringify(res.result)) : res.result.content.map(p => p.text).join('\n');
    const isError = !!(res.error || (c.method ? /"success":false/.test(text) : res.result.isError));
    const problems = [];
    if (c.expect && !new RegExp(c.expect, 'm').test(text)) problems.push(`expected /${c.expect}/`);
    if (c.isError !== undefined && c.isError !== isError) problems.push(`expected isError=${c.isError}, got ${isError}`);
    const label = `${c.name ?? c.params?.action ?? c.method} ${JSON.stringify(c.arguments ?? c.params?.body ?? {}).slice(0, 100)}`;
    if (problems.length) {
      failed++;
      console.log(`FAIL ${c.issue ?? ''} ${label}\n  ${problems.join('; ')}\n  got: ${text.slice(0, 600).replace(/\n/g, '\n       ')}`);
    } else {
      console.log(`PASS ${c.issue ?? ''} ${label}`);
    }
  }
  clearTimeout(timer);
  console.log(`${cases.length - failed}/${cases.length} passed`);
  child.stdin.end();
  process.exit(failed ? 1 : 0);
})();
