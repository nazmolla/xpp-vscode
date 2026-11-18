// Configurations F&O Studio writes must be readable by Visual Studio: node scripts/xpp-config-test.js [<config.json> <FinOpsTools folder>]
// Offline checks always run; with a real configuration and the FinOps tools, Visual Studio's own parser
// (scripts/vs-config-probe.ps1) must reject the incomplete file and accept the repaired one.
const path = require('path');
const fs = require('fs');
const os = require('os');
const assert = require('assert');
const { execFileSync } = require('child_process');
const { completeConfiguration, repairGeneratedConfiguration, RequiredMembers, TemplateDefaults } = require(path.join(__dirname, '..', 'out', 'xppConfig.js'));

let failures = 0;
const check = async (name, fn) => {
    try { await fn(); console.log(`PASS ${name}`); } catch (e) { failures++; console.log(`FAIL ${name}: ${e.message}`); }
};
const probe = (tools, file) => {
    try {
        return execFileSync('powershell', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(__dirname, 'vs-config-probe.ps1'), tools, file]).toString().trim();
    } catch (e) {
        return (e.stdout || '').toString().trim() || e.message;
    }
};

(async () => {
    const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'xppconfig-'));
    await check('template has every required member', () => RequiredMembers.forEach(m => assert.ok(m in TemplateDefaults, m)));
    await check('completing keeps existing values', () => {
        const config = { AosWebsiteName: 'Mine', ModelStoreFolder: 'C:\\x' };
        const added = completeConfiguration(config);
        assert.strictEqual(config.AosWebsiteName, 'Mine');
        assert.ok(added.includes('WebRoleDeploymentFolder') && !added.includes('AosWebsiteName'));
    });
    await check('configurations F&O Studio did not generate are left alone', async () => {
        const file = path.join(temp, 'other.json');
        fs.writeFileSync(file, JSON.stringify({ Description: 'Made by hand', ModelStoreFolder: 'C:\\x' }));
        assert.deepStrictEqual(await repairGeneratedConfiguration(file), []);
        assert.ok(!('WebRoleDeploymentFolder' in JSON.parse(fs.readFileSync(file, 'utf8'))));
    });

    const [config, tools] = process.argv.slice(2);
    if (config && tools) {
        const copy = path.join(temp, path.basename(config));
        fs.copyFileSync(config, copy);
        const before = JSON.parse(fs.readFileSync(copy, 'utf8'));
        const incomplete = !RequiredMembers.every(m => m in before);
        if (incomplete) {
            await check('Visual Studio rejects the incomplete configuration', () => assert.match(probe(tools, copy), /^FAIL/));
        }
        const added = await repairGeneratedConfiguration(copy, path.dirname(tools));
        console.log(`added: ${added.join(', ') || '(nothing)'}`);
        await check('Visual Studio reads the repaired configuration', () => assert.strictEqual(probe(tools, copy), 'OK'));
        await check('repair changed no existing value', () => {
            const after = JSON.parse(fs.readFileSync(copy, 'utf8'));
            Object.keys(before).forEach(k => assert.deepStrictEqual(after[k], before[k], k));
        });
    }
    fs.rmSync(temp, { recursive: true, force: true });
    console.log(failures ? `${failures} failed` : 'all passed');
    process.exit(failures ? 1 : 0);
})();
