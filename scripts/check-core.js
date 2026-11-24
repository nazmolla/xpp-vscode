// Checks that core/ (@fno-studio/core) stays free of VS Code: no import or require of 'vscode', no import that reaches
// out of core/ (into the extension's src/), no vscode dependency in core/package.json, and only Node's global types in
// the compiler settings. The compiler alone can't enforce it: 'vscode' resolves through the root node_modules/@types.
// Usage: node scripts/check-core.js
const fs = require('fs');
const path = require('path');

const core = path.resolve(__dirname, '..', 'core');
const problems = [];

function files(dir, extensions) {
    if (!fs.existsSync(dir)) {
        return [];
    }
    return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => {
        const full = path.join(dir, entry.name);
        if (entry.isDirectory()) {
            return entry.name === 'node_modules' ? [] : files(full, extensions);
        }
        return extensions.some(e => entry.name.endsWith(e)) ? [full] : [];
    });
}

const specifier = /(?:\bfrom\s*|\brequire\s*\(\s*|\bimport\s*\(\s*|^\s*import\s+)(['"])([^'"]+)\1/gm;
for (const file of [...files(path.join(core, 'src'), ['.ts']), ...files(path.join(core, 'dist'), ['.js', '.d.ts']), ...files(path.join(core, 'test'), ['.js'])]) {
    const text = fs.readFileSync(file, 'utf8');
    for (const match of text.matchAll(specifier)) {
        const name = match[2];
        const where = `${path.relative(core, file)}: '${name}'`;
        if (name === 'vscode' || name.startsWith('vscode/') || name.startsWith('@vscode/')) {
            problems.push(`${where} imports VS Code`);
        } else if (name.startsWith('.') && !path.resolve(path.dirname(file), name).startsWith(core + path.sep)) {
            problems.push(`${where} reaches outside core/`);
        }
    }
}

const pkg = JSON.parse(fs.readFileSync(path.join(core, 'package.json'), 'utf8'));
for (const section of ['dependencies', 'devDependencies', 'peerDependencies', 'optionalDependencies']) {
    for (const name of Object.keys(pkg[section] ?? {})) {
        if (/vscode/i.test(name)) {
            problems.push(`core/package.json ${section} has ${name}`);
        }
    }
}
if (pkg.engines?.vscode) {
    problems.push('core/package.json has engines.vscode');
}

const tsconfig = JSON.parse(fs.readFileSync(path.join(core, 'tsconfig.json'), 'utf8'));
const types = tsconfig.compilerOptions?.types;
if (!Array.isArray(types) || types.some(t => t !== 'node')) {
    problems.push('core/tsconfig.json must set compilerOptions.types to ["node"] so VS Code\'s global types stay out');
}

if (problems.length) {
    console.error(`core/ must not depend on VS Code:\n  ${problems.join('\n  ')}`);
    process.exit(1);
}
console.log('core/ is free of VS Code.');
