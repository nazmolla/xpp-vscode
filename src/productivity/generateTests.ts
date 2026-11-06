import * as vscode from 'vscode';
import { objectUri } from '../codeView';
import { addCreatedToProject, elementOf, pickElement, targetModel, TreeArg } from '../d365Tools';
import { currentObject } from '../navigation';
import { call, Element, findExact, IdentifierPattern, indent, isPrimitive, ProductivityContext, parseSignature, upperFirst } from './common';

interface Member {
    kind: string;
    name: string;
    owner: string;
    static: boolean;
    signature?: string;
    access?: string;
}

/** Default values for test inputs of primitive types; other types stay uninitialized. */
const SampleValues: Record<string, string> = { boolean: 'true', int: '1', int64: '1', real: '1.0', str: '"Test"' };

/** Acceptance Test Library (ATL) data for tables a class works with: the variable and the expression that creates it. */
const AtlData: Record<string, { variable: string; expression: string }> = {
    CustTable: { variable: 'customer', expression: 'data.cust().customers().default().record()' },
    VendTable: { variable: 'vendor', expression: 'data.vend().vendors().default().record()' },
    InventTable: { variable: 'item', expression: 'data.products().items().default()' },
    InventLocation: { variable: 'warehouse', expression: 'data.invent().warehouses().default()' },
    InventSite: { variable: 'site', expression: 'data.invent().sites().default()' },
};

/** The packages a model needs to use the Acceptance Test Library. */
const AtlPackages = ['AtlFoundation', 'AtlApplicationSuite'];

/**
 * Creates <Class>Test extends SysTestCase with an arrange/act/assert test method per public method of the class. With atl,
 * the tables the class works with (customers, vendors, items, warehouses, sites) are arranged with the Acceptance Test Library.
 */
export async function generateTests(ctx: ProductivityContext, arg?: TreeArg, atl = false): Promise<void> {
    let element: Element | undefined = elementOf(arg);
    if (!element) {
        const editor = vscode.window.activeTextEditor;
        const current = editor ? currentObject(editor.document) : undefined;
        element = current?.objectType === 'AxClass' ? current : await pickElement(ctx.server, 'Generate SysTest class', ['AxClass']);
    }
    if (!element) {
        return;
    }
    if (element.objectType !== 'AxClass') {
        throw new Error('Tests can be generated for classes only.');
    }
    const target = element.objectName;
    const result = await call<{ members: Member[] }>(ctx.server, 'bridge', 'type_members', { objectType: 'AxClass', objectName: target });
    const methods = (result.members ?? []).filter(m => m.kind === 'method' && m.owner.toLowerCase() === target.toLowerCase()
        && (m.access ?? 'public') === 'public' && !/^(new|classDeclaration|main)$/i.test(m.name));
    if (methods.length === 0) {
        throw new Error(`${target} has no public methods to test.`);
    }
    const model = await targetModel('Generate SysTest class');
    if (!model) {
        return;
    }
    const testClass = await vscode.window.showInputBox({
        title: `Generate tests for ${target}`, prompt: 'Test class name', value: `${target}Test`, ignoreFocusOut: true,
        validateInput: v => IdentifierPattern.test(v) ? undefined : 'Use a valid class name.',
    });
    if (!testClass) {
        return;
    }
    if (await findExact(ctx.server, 'AxClass', testClass)) {
        throw new Error(`Class ${testClass} already exists. Choose another name.`);
    }

    const construct = methods.find(m => m.static && m.name.toLowerCase() === 'construct' && /construct\s*\(\s*\)/i.test(m.signature ?? ''));
    const instance = construct ? `${target}::construct()` : `new ${target}()`;
    let tables: string[] = [];
    if (atl) {
        const source = await call<{ declaration?: string; methods?: { source?: string }[] }>(ctx.server, 'bridge', 'read_object', { objectType: 'AxClass', objectName: target });
        const code = [source.declaration ?? '', ...(source.methods ?? []).map(m => m.source ?? '')].join('\n');
        tables = Object.keys(AtlData).filter(t => new RegExp(`\\b${t}\\b`).test(code));
        await ensureAtlReferences(ctx, model);
    }
    const used = new Set<string>();
    const sources = methods.map(m => testMethod(target, instance, m, used, tables)).filter((s): s is string => !!s).map(s => indent(s, '    '));
    if (atl) {
        sources.unshift(indent(['/// <summary>', '/// Test data from the Acceptance Test Library, created in the test\'s transaction and rolled back after it.', '/// </summary>',
            'public void setUp()', '{', '    super();', '    data = AtlDataRootNode::construct();', '}'].join('\n'), '    '));
    }
    const declaration = [
        `[SysTestTarget(classStr(${target}), UtilElementType::Class)]`,
        `public final class ${testClass} extends SysTestCase`,
        '{',
        ...atl ? ['    AtlDataRootNode data;'] : [],
        '}',
    ].join('\n');
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: `Creating ${testClass}…` }, async () => {
        await call(ctx.server, 'bridge', 'create_object', {
            objectType: 'AxClass', objectName: testClass, modelName: model, declaration, methods: sources, formatCode: false,
        });
        await addCreatedToProject(ctx.server, 'AxClass', testClass);
    });
    ctx.refreshTrees();
    await vscode.window.showTextDocument(objectUri('AxClass', testClass));
    vscode.window.setStatusBarMessage(`Created ${testClass} with ${sources.length} test method(s). Fill in the expected values, then run it with Run Tests.`, 8000);
}

function testMethod(target: string, instance: string, member: Member, used: Set<string>, atlTables: string[] = []): string | undefined {
    const signature = parseSignature(member.signature ?? `public void ${member.name}()`);
    if (!signature) {
        return undefined;
    }
    let name = `test${upperFirst(member.name)}`;
    for (let i = 2; used.has(name.toLowerCase()); i++) {
        name = `test${upperFirst(member.name)}${i}`;
    }
    used.add(name.toLowerCase());

    const arrange: string[] = [];
    const atlVariables = new Map<string, string>();
    for (const table of atlTables) {
        arrange.push(`${table} ${AtlData[table].variable} = ${AtlData[table].expression};`);
        atlVariables.set(table.toLowerCase(), AtlData[table].variable);
    }
    if (!member.static) {
        arrange.push(`${target} target = ${instance};`);
    }
    const args: string[] = [];
    for (const p of signature.parameters) {
        const atlVariable = atlVariables.get(p.type.toLowerCase());
        if (atlVariable) {
            args.push(atlVariable);
            continue;
        }
        const local = p.name.replace(/^_+/, '') || 'value';
        const safe = /^(target|result|expected)$/i.test(local) ? local + 'Value' : local;
        const value = p.defaultValue ?? SampleValues[p.type.toLowerCase()];
        arrange.push(`${p.type} ${safe}${value !== undefined ? ' = ' + value : ''};`);
        args.push(safe);
    }
    const invocation = `${member.static ? `${target}::` : 'target.'}${member.name}(${args.join(', ')})`;
    const returnType = signature.returnType;
    const isVoid = returnType.toLowerCase() === 'void';
    const act: string[] = [];
    const assert: string[] = [];
    if (isVoid) {
        act.push(`${invocation};`);
        assert.push('// Check the expected side effects of the call here.');
    } else if (isPrimitive(returnType)) {
        arrange.push(`${returnType} expected;`);
        act.push(`${returnType} result = ${invocation};`);
        assert.push(`this.assertEquals(expected, result, '${member.name} returns the expected value');`);
    } else {
        act.push(`${returnType} result = ${invocation};`);
        assert.push(`this.assertNotNull(result, '${member.name} returns a value');`);
    }
    const block = (title: string, lines: string[]) => lines.length ? [`    // ${title}`, ...lines.map(l => '    ' + l)] : [];
    return [
        '[SysTestMethod]',
        `public void ${name}()`,
        '{',
        ...block('Arrange', arrange),
        ...(arrange.length ? [''] : []),
        ...block('Act', act),
        '',
        ...block('Assert', assert),
        '}',
    ].join('\n');
}

/** Offers to add the ATL packages to the model's references when they are missing. */
async function ensureAtlReferences(ctx: ProductivityContext, model: string): Promise<void> {
    const info = await call<{ references?: string[]; displayName?: string; description?: string }>(ctx.server, 'bridge', 'get_model', { name: model });
    const references = info.references ?? [];
    const missing = AtlPackages.filter(p => !references.some(r => r.toLowerCase() === p.toLowerCase()));
    if (!missing.length) {
        return;
    }
    const choice = await vscode.window.showInformationMessage(`${model} needs references to ${missing.join(' and ')} to use the Acceptance Test Library.`, 'Add References');
    if (choice) {
        await call(ctx.server, 'bridge', 'update_model', { name: model, displayName: info.displayName ?? model, description: info.description ?? '', references: [...references, ...missing] });
    }
}
