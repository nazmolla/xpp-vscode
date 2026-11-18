// Checks Visual Studio FinOps tools detection: node scripts/vs-tools-test.js [<version folder>]
// Manifest parsing is checked offline; with a version folder (…\Dynamics365\<version>) it also reports this machine's state.
const path = require('path');
const assert = require('assert');
const { finOpsManifestVersion, mismatchedInstalls, visualStudioInstalls, environmentFinOpsTools, packFinOpsVsix } = require(path.join(__dirname, '..', 'out', 'vsFinOpsTools.js'));

let failures = 0;
function check(name, fn) {
    try { fn(); console.log(`PASS ${name}`); } catch (e) { failures++; console.log(`FAIL ${name}: ${e.message}`); }
}

const manifest = (id, version) => `<?xml version="1.0"?><PackageManifest Version="2.0.0"><Metadata><Identity Language="en-US" Id="${id}" Version="${version}" Publisher="Microsoft" /></Metadata></PackageManifest>`;
check('reads the FinOps tools version', () => assert.strictEqual(finOpsManifestVersion(manifest('DynamicsFnO.DeveloperTools', '7.0.8199.70')), '7.0.8199.70'));
check('ignores other extensions', () => assert.strictEqual(finOpsManifestVersion(manifest('XppAiCopilotCompanion', '0.5.40')), undefined));
check('attribute order does not matter', () => assert.strictEqual(finOpsManifestVersion('<Identity Version="7.0.1.2" Id="DynamicsFnO.DeveloperTools"/>'), '7.0.1.2'));
check('skips the commented-out template Identity', () => assert.strictEqual(finOpsManifestVersion(
    '<!-- <Identity Id="<vsixIdentifier>" Version="|%CurrentProject%;GetVsixVersion|" /> --><Identity Id="DynamicsFnO.DeveloperTools" Version="7.0.8199.70" />'), '7.0.8199.70'));
check('only installs with other tools mismatch', () => {
    const installs = [{ instanceId: 'a', finOpsTools: '7.0.8199.55' }, { instanceId: 'b', finOpsTools: '7.0.8199.70' }, { instanceId: 'c' }];
    assert.deepStrictEqual(mismatchedInstalls(installs, '7.0.8199.70').map(i => i.instanceId), ['a']);
});

(async () => {
    const folder = process.argv[2];
    if (folder) {
        const needed = await environmentFinOpsTools(folder);
        const installs = await visualStudioInstalls();
        console.log(`environment needs FinOps tools ${needed ?? '(none downloaded)'}`);
        installs.forEach(i => console.log(`  ${i.displayName} ${i.installationVersion}: FinOps tools ${i.finOpsTools ?? '(not installed)'}`));
        check('finds Visual Studio', () => assert.ok(installs.length > 0));
        if (needed) {
            console.log(mismatchedInstalls(installs, needed).length ? 'MISMATCH: Application Explorer will not load' : 'all FinOps tools match');
            const os = require('os');
            const fs = require('fs');
            const { execFileSync } = require('child_process');
            const target = path.join(os.tmpdir(), `vs-tools-test-${process.pid}.vsix`);
            await packFinOpsVsix(folder, target);
            const entries = execFileSync(path.join(process.env.SystemRoot, 'System32', 'tar.exe'), ['-tf', target], { maxBuffer: 1 << 24 }).toString().split(/\r?\n/).filter(Boolean);
            const count = dir => fs.readdirSync(dir, { withFileTypes: true }).reduce((n, e) => n + (e.isDirectory() ? count(path.join(dir, e.name)) : 1), 0);
            check('packed VSIX holds every file', () => assert.strictEqual(entries.length, count(path.join(folder, 'FinOpsTools'))));
            check('packed VSIX has the manifest at the root', () => assert.ok(entries.includes('extension.vsixmanifest') && entries.includes('[Content_Types].xml')));
            check('packed VSIX uses forward slashes, no folder entries', () => assert.ok(entries.every(e => !e.includes('\\') && !e.endsWith('/'))));
            fs.rmSync(target, { force: true });
        }
    }
    console.log(failures ? `${failures} failed` : 'all passed');
    process.exit(failures ? 1 : 0);
})();
