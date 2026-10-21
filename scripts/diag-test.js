// Breaks XvsTestCodeView on purpose, compiles testcust to a temp folder, and checks errors map to the right lines; then restores it.
const { spawn, execFileSync } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { composeSource, compilerLineToDocumentLine } = require(path.join(__dirname, '..', 'out', 'xppSource.js'));
const { parseDiagnostics, diagnosticTarget } = require(path.join(__dirname, '..', 'out', 'diagnostics.js'));

const [exe, configFile] = process.argv.slice(2);
const cfg = JSON.parse(fs.readFileSync(configFile, 'utf8').replace(/^\uFEFF/, ''));
const child = spawn(exe, ['--config', configFile], { windowsHide: true });
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

(async () => {
  await rpc('initialize', {});
  const cls = 'XvsTestCodeView';
  const broken = 'public void broken()\n{\n    int x;\n    x = "text";\n    this.noSuchMethod();\n}';
  let r = await bridge('update_object', { objectType: 'AxClass', objectName: cls, methods: [broken], formatCode: false });
  console.log('add broken method:', r.success, r.message);

  const out = fs.mkdtempSync(path.join(os.tmpdir(), 'xvsbuild-'));
  const bin = path.join(cfg.FrameworkDirectory, 'bin');
  const xml = path.join(out, 'xppc.xml');
  const args = [`-metadata=${cfg.ModelStoreFolder}`, `-compilermetadata=${cfg.RuntimePackagesDirectory}`, '-modelmodule=testcust', `-output=${out}`,
    `-xmllog=${xml}`, `-log=${path.join(out, 'xppc.log')}`, `-appBase=${bin}`, `-refPath=${bin}`, ...cfg.ReferencePackagesPaths.map(p => `-referenceFolder=${p}`), `-referenceFolder=${cfg.ModelStoreFolder}`];
  try { execFileSync(path.join(bin, 'xppc.exe'), args, { cwd: bin, stdio: 'ignore', timeout: 600000 }); } catch (e) { console.log('xppc exit code', e.status); }

  r = await bridge('read_object', { objectType: 'AxClass', objectName: cls });
  console.log('declaration lines:', r.declaration.split('\n').length);
  for (const m of r.methods) console.log(`method ${m.name}: ${m.source.split('\n').length} lines; first line: ${m.source.split('\n')[0].trim()}`);
  const text = composeSource(r.declaration, r.methods);
  const lines = text.split('\n');
  for (const d of parseDiagnostics(fs.readFileSync(xml, 'utf8')).filter(d => d.path.includes(cls))) {
    const t = diagnosticTarget(d.path);
    const line = t.isCode ? compilerLineToDocumentLine(text, d.line ?? 1) : 0;
    console.log(`${d.severity} ${d.path} (${d.line},${d.column}): ${d.message}\n   -> document line ${line + 1}: ${lines[line]?.trim()}`);
  }

  r = await bridge('update_object', { objectType: 'AxClass', objectName: cls, removeMethodNames: ['broken'], formatCode: false });
  console.log('removed broken method:', r.success);
  fs.rmSync(out, { recursive: true, force: true });
  child.stdin.end();
  process.exit(0);
})();
