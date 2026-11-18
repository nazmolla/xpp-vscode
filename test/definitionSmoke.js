// X++ and C# navigation in a real extension host: node scripts/run-smoke.js definitionSmoke
// X++ code going to the C# source of the solution's projects (definition, completion, hover), and C# code using X++ types
// (Dynamics.AX.Application) going to their X++ code. Results go to XPP_SMOKE_OUT (default %TEMP%\xpp-definition.json).
const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

async function run() {
    const results = {};
    const out = process.env.XPP_SMOKE_OUT || path.join(os.tmpdir(), 'xpp-definition.json');
    const save = () => fs.writeFileSync(out, JSON.stringify(results, null, 2));
    const check = async (name, fn) => {
        try { results[name] = { ok: true, value: await fn() }; }
        catch (err) { results[name] = { ok: false, error: String(err && err.stack || err) }; }
        save();
    };
    await vscode.extensions.getExtension('MNazmi.xpp-vscode').activate();
    const { clrIndex } = require(path.resolve(__dirname, '..', 'out', 'clrNavigation.js'));

    const root = path.join(os.tmpdir(), 'xpp-definition-smoke');
    fs.rmSync(root, { recursive: true, force: true });
    const write = (file, text) => { fs.mkdirSync(path.dirname(path.join(root, file)), { recursive: true }); fs.writeFileSync(path.join(root, file), text); return path.join(root, file); };
    const csproj = write('Lib/Lib.csproj', '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net48</TargetFramework></PropertyGroup></Project>');
    const greeter = write('Lib/Greeter.cs', [
        'namespace Contoso.Integration',
        '{',
        '    public static class Greeter',
        '    {',
        '        public const int Answer = 42;',
        '        public static string Hello(string name) { return "Hello {" + name; }',
        '    }',
        '    public class Client',
        '    {',
        '        public Client(string url) { }',
        '        public int Port { get; set; }',
        '        public string Send(string id) => id;',
        '    }',
        '    public enum Mode { Fast, Slow }',
        '    namespace Inner { public class Deep { public void Go() { } } }',
        '}',
    ].join('\r\n'));
    clrIndex.useProjects(async () => [csproj]);
    const xppLines = [
        'using Contoso.Integration;',
        'class XvsDefinitions',
        '{',
        '    public static void main(Args _args)',
        '    {',
        "        str s = Contoso.Integration.Greeter::Hello('x');",
        "        Client client = new Client('u');",
        "        client.Send('1');",
        '        Contoso.Integration.Inner.Deep deep;',
        '        Mode m = Mode::Slow;',
        '        int a = Greeter::Answer;',
        '    }',
        '}',
    ];
    const xpp = await vscode.workspace.openTextDocument(write('XvsDefinitions.xpp', xppLines.join('\r\n')));
    const at = (line, text, offset = 0) => new vscode.Position(line, xppLines[line].indexOf(text) + offset);
    const definition = async (document, position) => {
        const locations = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', document.uri, position);
        return (locations ?? []).map(l => `${path.basename((l.uri ?? l.targetUri).fsPath)}:${(l.range ?? l.targetSelectionRange ?? l.targetRange).start.line + 1}`);
    };
    const expect = async (document, position, wanted) => {
        const found = await definition(document, position);
        if (!found.includes(wanted)) throw new Error(`wanted ${wanted}, got ${JSON.stringify(found)}`);
        return found;
    };

    await check('X++ Namespace.Type::Method goes to the C# method', () => expect(xpp, at(5, 'Hello'), 'Greeter.cs:6'));
    await check('X++ Namespace.Type goes to the C# type', () => expect(xpp, at(5, 'Greeter'), 'Greeter.cs:3'));
    await check('X++ new Type() of a using-directive goes to the constructor', () => expect(xpp, at(6, "Client('u')"), 'Greeter.cs:10'));
    await check('X++ variable.method of a .NET variable goes to the C# method', () => expect(xpp, at(7, 'Send'), 'Greeter.cs:12'));
    await check('X++ nested namespace type goes to the C# type', () => expect(xpp, at(8, 'Deep'), 'Greeter.cs:15'));
    await check('X++ Enum::Value goes to the C# enum value', () => expect(xpp, at(9, 'Slow', 0), 'Greeter.cs:14'));
    await check('X++ Type::Constant of a using-directive goes to the C# constant', () => expect(xpp, at(10, 'Answer'), 'Greeter.cs:5'));

    const complete = async prefix => {
        // Completion at the end of a line that ends with the prefix.
        const document = await vscode.workspace.openTextDocument({ language: 'xpp', content: [...xppLines.slice(0, 5), `        ${prefix}`, '    }', '}'].join('\n') });
        const list = await vscode.commands.executeCommand('vscode.executeCompletionItemProvider', document.uri, new vscode.Position(5, 8 + prefix.length));
        return list.items.map(i => typeof i.label === 'string' ? i.label : i.label.label);
    };
    await check('X++ completion after Namespace.Type:: lists static members', async () => {
        const labels = await complete('Contoso.Integration.Greeter::');
        if (!labels.includes('Hello') || !labels.includes('Answer')) throw new Error(JSON.stringify(labels));
        return labels;
    });
    await check('X++ completion after a namespace lists its types and namespaces', async () => {
        const labels = await complete('Contoso.Integration.');
        for (const l of ['Greeter', 'Client', 'Mode', 'Inner']) if (!labels.includes(l)) throw new Error(JSON.stringify(labels));
        return labels;
    });
    await check('X++ hover on a C# method shows its declaration', async () => {
        const hovers = await vscode.commands.executeCommand('vscode.executeHoverProvider', xpp.uri, at(5, 'Hello'));
        const text = hovers.flatMap(h => h.contents.map(c => c.value ?? c)).join('\n');
        if (!/public static string Hello\(string name\)/.test(text)) throw new Error(text);
        return text;
    });

    const csLines = [
        'using Dynamics.AX.Application;',
        'namespace Lib',
        '{',
        '    public static class Customers',
        '    {',
        '        public static string Name(string account)',
        '        {',
        '            CustTable cust = CustTable.find(account, false);',
        '            Dynamics.AX.Application.SalesFormLetter letter = null;',
        '            return cust.name();',
        '        }',
        '    }',
        '}',
    ];
    const cs = await vscode.workspace.openTextDocument(write('Lib/Customers.cs', csLines.join('\r\n')));
    const csAt = (line, text) => new vscode.Position(line, csLines[line].indexOf(text));
    const xppTarget = async (position, pattern) => {
        const locations = await vscode.commands.executeCommand('vscode.executeDefinitionProvider', cs.uri, position);
        const found = (locations ?? []).map(l => `${(l.uri ?? l.targetUri).toString()}#${(l.range ?? l.targetRange).start.line + 1}`);
        if (!found.some(f => pattern.test(decodeURIComponent(f)))) throw new Error(JSON.stringify(found));
        return found;
    };
    await check('C# X++ type goes to the X++ code view', () => xppTarget(csAt(7, 'CustTable'), /^xpp:.*AxTable.*CustTable/i));
    await check('C# Type.method goes to the X++ method', () => xppTarget(csAt(7, 'find'), /^xpp:.*CustTable.*#([2-9]|\d{2,})/i));
    await check('C# variable.method goes to the X++ method', () => xppTarget(csAt(9, 'name'), /^xpp:.*CustTable.*#([2-9]|\d{2,})/i));
    await check('C# Dynamics.AX.Application.Class goes to the X++ class', () => xppTarget(csAt(8, 'SalesFormLetter'), /^xpp:.*AxClass.*SalesFormLetter/i));

    await vscode.commands.executeCommand('workbench.action.closeAllEditors');
    fs.rmSync(root, { recursive: true, force: true });
    results.finished = true;
    save();
}

module.exports = { run };
