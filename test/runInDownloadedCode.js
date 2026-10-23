// Runs a test file (default test/productivitySmoke.js) in a VS Code downloaded by @vscode/test-electron (.vscode-test),
// independent of the installed VS Code (e.g. while it waits to install an update).
const path = require('path');
const { runTests } = require('@vscode/test-electron');
const root = path.resolve(__dirname, '..');
const test = path.resolve(root, process.argv[2] || 'test/productivitySmoke.js');
runTests({
    extensionDevelopmentPath: root,
    extensionTestsPath: test,
    launchArgs: ['--disable-extensions', '--user-data-dir=' + path.join(require('os').tmpdir(), 'xpp-test-electron-ud')],
}).then(() => process.exit(0), err => { console.error(err); process.exit(1); });
