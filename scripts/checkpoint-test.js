// Copilot checkpoint and scope, end to end: node scripts/checkpoint-test.js <exe> <config> <model>
// Creates two classes in <model>; the agent's server (--activity-log) changes one inside a checkpoint and is refused the
// other outside the scope; the extension's server (--allow-data) lists and reverts the checkpoint. Everything is removed
// afterwards and agentSession.json restored.
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const [exe, config, model] = process.argv.slice(2);
const testClass = 'XvsCheckpointTest';
const otherType = 'AxClass';
const otherName = 'XvsCheckpointOther';
const folder = path.join(process.env.LOCALAPPDATA || os.homedir(), 'XppVsCode');
const sessionFile = path.join(folder, 'agentSession.json');
const id = 'checkpoint-test-' + Date.now();
const probe = 'xvsCheckpointProbe';
let failures = 0;

function check(name, ok, detail) {
  console.log(`${ok ? 'PASS' : 'FAIL'} ${name}${ok ? '' : ': ' + detail}`);
  if (!ok) failures++;
}

function session(args) {
  const child = spawn(exe, ['--config', config, ...args], { windowsHide: true });
  const pending = new Map();
  let buffer = '';
  let next = 2;
  child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines) {
      if (!line.trim()) continue;
      const msg = JSON.parse(line);
      const resolve = pending.get(msg.id);
      if (resolve) { pending.delete(msg.id); resolve(typeof msg.result === 'string' ? JSON.parse(msg.result) : msg.result); }
    }
  });
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } }) + '\n');
  return {
    call(action, body) {
      const callId = next++;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => reject(new Error(`timeout: ${action}`)), 180000);
        pending.set(callId, r => { clearTimeout(timer); resolve(r); });
        child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: callId, method: 'xpp/bridge', params: { action, body } }) + '\n');
      });
    },
    close() { child.stdin.end(); setTimeout(() => child.kill(), 2000); },
  };
}

(async () => {
  const backup = fs.existsSync(sessionFile) ? fs.readFileSync(sessionFile, 'utf8') : undefined;
  fs.mkdirSync(folder, { recursive: true });
  const agent = session(['--activity-log']);
  const extension = session(['--allow-data']);
  try {
    for (const name of [testClass, otherName]) {
      const created = await extension.call('create_object', { objectType: 'AxClass', objectName: name, modelName: model, declaration: `class ${name}\n{\n}` });
      check(`create ${name}`, created.success, created.message);
    }
    const before = await extension.call('read_object', { objectType: 'AxClass', objectName: testClass });
    check('test class exists', before.success, JSON.stringify(before).slice(0, 300));
    const hasProbe = r => (r.methods || []).some(m => (m.name || m.Name || '').toLowerCase() === probe.toLowerCase());
    check('test class has no probe method yet', !hasProbe(before), 'remove ' + probe + ' first');

    fs.writeFileSync(sessionFile, JSON.stringify({ checkpoint: id, scope: [`AxClass/${testClass}`] }));
    const update = await agent.call('update_object', { objectType: 'AxClass', objectName: testClass, methods: [`public void ${probe}()\n{\n}`] });
    check('agent may change an element in scope', update.success, update.message);
    const refused = await agent.call('update_object', { objectType: otherType, objectName: otherName, properties: { NoSuchProperty: 'x' } });
    check('agent is refused an element outside the scope', !refused.success && /outside the elements/.test(refused.message || ''), JSON.stringify(refused).slice(0, 300));

    const entries = await extension.call('checkpoint_entries', { id });
    check('checkpoint lists the changed element', entries.success && entries.entries.some(e => e.objectName === testClass && e.existed), JSON.stringify(entries).slice(0, 300));
    const changed = await extension.call('read_object', { objectType: 'AxClass', objectName: testClass });
    check('the change was saved', hasProbe(changed), 'probe method missing after update');

    const revert = await extension.call('revert_checkpoint', { id });
    check('revert succeeds', revert.success, revert.message);
    const after = await extension.call('read_object', { objectType: 'AxClass', objectName: testClass });
    check('revert restored the element', after.success && !hasProbe(after), 'probe method still there');
  } catch (err) {
    check('run', false, err.message);
  } finally {
    if (backup === undefined) fs.rmSync(sessionFile, { force: true }); else fs.writeFileSync(sessionFile, backup);
    for (const name of [testClass, otherName]) {
      const removed = await extension.call('delete_object', { objectType: 'AxClass', objectName: name }).catch(e => ({ success: false, message: e.message }));
      check(`clean up ${name}`, removed.success, removed.message);
    }
    agent.close();
    extension.close();
    fs.rmSync(path.join(folder, 'checkpoints', id), { recursive: true, force: true });
    console.log(failures ? `${failures} FAILED` : 'ALL PASS');
    setTimeout(() => process.exit(failures ? 1 : 0), 2500);
  }
})();
