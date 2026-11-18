// Writes CHANGELOG.md from the "What's new in X.Y" sections of docs/FEATURES.md (the one place release notes are written).
// node scripts/changelog.js          writes CHANGELOG.md
// node scripts/changelog.js --check  fails when CHANGELOG.md is out of date (the release build runs this)
// The Marketplace shows CHANGELOG.md as the extension's Changelog tab; vsce packages it from the repository root.
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const featuresFile = path.join(root, 'docs', 'FEATURES.md');
const changelogFile = path.join(root, 'CHANGELOG.md');
const Blob = 'https://github.com/nazmolla/xpp-vscode/blob/HEAD/docs/';
const Raw = 'https://github.com/nazmolla/xpp-vscode/raw/HEAD/docs/';

/** Links in FEATURES.md are relative to docs/; the changelog sits at the root and is also shown on the Marketplace. */
function absoluteLinks(line) {
    return line.replace(/(!?)\[([^\]]*)\]\(([^)\s]+)\)/g, (all, bang, text, target) => {
        if (/^[a-z]+:/i.test(target)) return all;
        if (target.startsWith('#')) return `${bang}[${text}](${Blob}FEATURES.md${target})`;
        const clean = target.replace(/^\.\//, '');
        return `${bang}[${text}](${(bang || /\.(png|gif|jpe?g|svg)$/i.test(clean) ? Raw : Blob) + clean})`;
    });
}

function changelog(features) {
    const lines = features.replace(/\r\n/g, '\n').split('\n');
    const out = ['# Changelog', '', 'Release notes of F&O Studio for VS Code. They are generated from the "What\'s new" sections of [docs/FEATURES.md](' + Blob + 'FEATURES.md); edit them there.', ''];
    let inRelease = false;
    for (const line of lines) {
        const release = /^## What's new in (\d+\.\d+)\s*$/.exec(line);
        if (release) {
            inRelease = true;
            out.push(`## ${release[1]}`, '');
            continue;
        }
        if (/^## /.test(line)) {
            inRelease = false;
            continue;
        }
        if (!inRelease) continue;
        if (/^### Earlier releases\s*$/.test(line)) {
            out.push('## Earlier releases', '');
            continue;
        }
        // "**0.15.6: Title.** text" opens that version's notes.
        const version = /^\*\*(\d+\.\d+\.\d+):\s*(.+?)\*\*\s*(.*)$/.exec(line);
        if (version) {
            out.push(`### ${version[1]}`, '', `**${version[2]}**${version[3] ? ' ' + absoluteLinks(version[3]) : ''}`);
            continue;
        }
        if (/^Full history:/.test(line)) continue;
        out.push(absoluteLinks(line));
    }
    // One blank line between blocks, none at the end but the final newline.
    return out.join('\n').replace(/\n{3,}/g, '\n\n').trim() + '\n';
}

const text = changelog(fs.readFileSync(featuresFile, 'utf8'));
if (process.argv.includes('--check')) {
    const current = fs.existsSync(changelogFile) ? fs.readFileSync(changelogFile, 'utf8').replace(/\r\n/g, '\n') : '';
    if (current !== text) {
        console.error('CHANGELOG.md is out of date with docs/FEATURES.md. Run: node scripts/changelog.js');
        process.exit(1);
    }
    console.log('CHANGELOG.md is up to date.');
} else {
    fs.writeFileSync(changelogFile, text, 'utf8');
    console.log(`Wrote CHANGELOG.md (${text.split('\n').filter(l => /^## \d/.test(l)).length} releases).`);
}

module.exports = { changelog, absoluteLinks };
