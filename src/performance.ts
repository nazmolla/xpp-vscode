import * as vscode from 'vscode';
import { elementOf, pickElement, TreeArg } from './d365Tools';
import { objectUri, parseObjectUri, XppScheme } from './codeView';
import { XppServerClient } from './serverClient';
import { methodSpans } from './xppSource';

interface Finding { rule: string; severity: string; method: string; line: number; message: string; fix: string }

const CodeTypes = ['AxClass', 'AxTable', 'AxForm', 'AxDataEntityView'];

/** F&O Studio: Analyze Performance: static performance findings of a class, table, form or data entity (with its readiness for OData and data management), shown in the Problems view on its code. */
export function registerPerformance(context: vscode.ExtensionContext, command: (id: string, handler: (...args: any[]) => unknown) => void, server: XppServerClient): void {
    const diagnostics = vscode.languages.createDiagnosticCollection('xpp-performance');
    context.subscriptions.push(diagnostics);

    command('xpp.analyzePerformance', async (arg?: TreeArg | vscode.Uri) => {
        const element = arg instanceof vscode.Uri
            ? (arg.scheme === XppScheme ? parseObjectUri(arg) : undefined)
            : elementOf(arg) ?? await pickElement(server, 'Analyze performance', CodeTypes);
        if (!element || !CodeTypes.includes(element.objectType)) {
            return;
        }
        const result = await vscode.window.withProgress({ location: vscode.ProgressLocation.Window, title: `Analyzing ${element.objectName}` },
            () => server.bridge('analyze_performance', { objectType: element.objectType, objectName: element.objectName }));
        if (!result.success) {
            throw new Error(result.message ?? 'The analysis failed.');
        }
        const findings: Finding[] = result.findings ?? [];
        const uri = objectUri(element.objectType, element.objectName);
        const spans = methodSpans((await vscode.workspace.openTextDocument(uri)).getText());
        diagnostics.set(uri, findings.map(f => {
            const start = spans.find(s => s.name.toLowerCase() === f.method.toLowerCase())?.startLine ?? 0;
            const line = start + Math.max(0, f.line - 1);
            const diagnostic = new vscode.Diagnostic(new vscode.Range(line, 0, line, 1000), `${f.message} ${f.fix}`,
                f.severity === 'Warning' ? vscode.DiagnosticSeverity.Warning : vscode.DiagnosticSeverity.Information);
            diagnostic.source = 'X++ performance';
            diagnostic.code = f.rule;
            return diagnostic;
        }));
        const warnings = findings.filter(f => f.severity === 'Warning').length;
        const choice = await vscode.window.showInformationMessage(
            `${element.objectName}: ${findings.length} performance finding(s), ${warnings} warning(s), in ${result.methods} method(s).`,
            ...(findings.length ? ['Show Problems'] : []));
        if (choice) {
            await vscode.commands.executeCommand('workbench.actions.view.problems');
        }
    });
}
