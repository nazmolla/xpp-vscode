// Calls one MCP tool on the server and prints its text (images are saved next to outBase):
//   node scripts/tool-call.js <exe> <config> <tool> <argsJsonOrFile> [outBase] [maxChars]
const { spawn } = require('child_process');
const fs = require('fs');
const [exe, config, tool, argsArg, outBase, max] = process.argv.slice(2);
const args = JSON.parse(fs.existsSync(argsArg) ? fs.readFileSync(argsArg, 'utf8').replace(/^\uFEFF/, '') : argsArg);
const child = spawn(exe, ['--config', config], { windowsHide: true });
let buffer = '';
child.stdout.on('data', d => {
    buffer += d.toString();
    const lines = buffer.split('\n');
    buffer = lines.pop();
    for (const line of lines.filter(l => l.trim())) {
        const msg = JSON.parse(line);
        if (msg.id !== 2) {
            continue;
        }
        const content = msg.result?.content ?? [];
        for (const [i, c] of content.entries()) {
            if (c.type === 'text') {
                console.log(c.text.slice(0, Number(max || 6000)));
            } else if (c.type === 'image' && outBase) {
                fs.writeFileSync(`${outBase}.${i}.png`, Buffer.from(c.data, 'base64'));
                console.log(`[image ${i} saved to ${outBase}.${i}.png]`);
            }
        }
        if (msg.error) {
            console.log('ERROR ' + JSON.stringify(msg.error));
        }
        if (msg.result?.isError) {
            console.log('(isError)');
        }
        child.stdin.end();
    }
});
child.on('exit', () => process.exit(0));
setTimeout(() => { console.log('TIMEOUT'); child.kill(); process.exit(1); }, 300000);
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-06-18' } }) + '\n');
child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: tool, arguments: args } }) + '\n');
