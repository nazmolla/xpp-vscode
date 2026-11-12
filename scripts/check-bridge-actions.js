// Checks that src/bridgeContract.ts lists exactly the actions the server dispatches: node scripts/check-bridge-actions.js
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');

const contract = read('src/bridgeContract.ts');
const listed = new Set([...contract.slice(contract.indexOf('['), contract.indexOf('] as const')).matchAll(/'(\w+)'/g)].map(m => m[1]));

// Registrations: a.Add("name", ...) in the dispatcher, new MetaModel.BridgeAction("name", ...) elsewhere (cross-reference actions).
// test/server (BridgeRegistryTests) checks the same against the running registry.
const dispatcher = read('server/Ported/MetaModel/MetaModelBridgeServer.cs');
const body = dispatcher.slice(dispatcher.indexOf('private void RegisterActions('));
const served = new Set([...body.matchAll(/\ba\.Add\("(\w+)"/g)].map(m => m[1]));
for (const m of read('server/Ported/Tools/ToolRouter.cs').matchAll(/new MetaModel\.BridgeAction\("(\w+)"/g)) served.add(m[1]);

const used = new Set();
for (const file of walk(path.join(root, 'src'))) {
    for (const m of fs.readFileSync(file, 'utf8').matchAll(/(?:bridge|call\(\w+, 'bridge',|nodeAction)\s*(?:<[^>]*>)?\(?\s*'(\w+)'/g)) used.add(m[1]);
}

const problems = [
    ...[...listed].filter(a => !served.has(a)).map(a => `listed but not served: ${a}`),
    ...[...served].filter(a => !listed.has(a)).map(a => `served but not listed: ${a}`),
    ...[...used].filter(a => !served.has(a)).map(a => `used by the extension but not served: ${a}`),
];
console.log(problems.length ? problems.join('\n') : `OK: ${served.size} actions, ${used.size} used by the extension.`);
process.exit(problems.length ? 1 : 0);

function* walk(dir) {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) yield* walk(full);
        else if (full.endsWith('.ts')) yield full;
    }
}
