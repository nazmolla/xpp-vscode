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

/** Creates <Class>Test extends SysTestCase with an arrange/act/assert test method per public method of the class. */
export async function generateTests(ctx: ProductivityContext, arg?: TreeArg): Promise<void> {
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
    const used = new Set<string>();
    const sources = methods.map(m => testMethod(target, instance, m, used)).filter((s): s is string => !!s).map(s => indent(s, '    '));
    const declaration = [
        `[SysTestTarget(classStr(${target}), UtilElementType::Class)]`,
        `public final class ${testClass} extends SysTestCase`,
        '{',
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

function testMethod(target: string, instance: string, member: Member, used: Set<string>): string | undefined {
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
    if (!member.static) {
        arrange.push(`${target} target = ${instance};`);
    }
    const args: string[] = [];
    for (const p of signature.parameters) {
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
