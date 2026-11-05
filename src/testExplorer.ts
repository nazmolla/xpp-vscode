import * as vscode from 'vscode';
import { objectUri } from './codeView';
import { readActiveEnvironment } from './environment';
import { XppServerClient } from './serverClient';
import { discoverTests, runTests, TestOutcome } from './tests';

/**
 * The project's SysTest classes in VS Code's Test Explorer: discovered from the active project, run on the environment's
 * cloud runtime (the same runner as Run Tests and the agent tool), with the result of every test method.
 */
export function registerTestExplorer(context: vscode.ExtensionContext, server: XppServerClient): vscode.TestController {
    const controller = vscode.tests.createTestController('xppTests', 'X++ tests');
    context.subscriptions.push(controller);

    const discover = async () => {
        const tests = await discoverTests(server, {}, () => undefined);
        const classes = new Map<string, vscode.TestItem>();
        for (const test of tests) {
            let item = classes.get(test.className);
            if (!item) {
                item = controller.createTestItem(test.className, test.className, objectUri('AxClass', test.className));
                classes.set(test.className, item);
            }
            item.children.add(controller.createTestItem(`${test.className}.${test.method}`, test.method, objectUri('AxClass', test.className)));
        }
        controller.items.replace([...classes.values()]);
    };

    controller.resolveHandler = async item => {
        if (!item) {
            await discover().catch(err => controller.items.replace([controller.createTestItem('error', `Discovery failed: ${(err as Error).message}`)]));
        }
    };
    controller.refreshHandler = () => discover();

    controller.createRunProfile('Run on environment', vscode.TestRunProfileKind.Run, async (request, token) => {
        const run = controller.createTestRun(request);
        const selected = collect(request.include ?? [...gather(controller.items)], request.exclude ?? []);
        selected.forEach(t => run.enqueued(t));
        try {
            if ((await readActiveEnvironment()).local) {
                throw new Error('Tests run through Dataverse, which only unified developer environments have.');
            }
            selected.forEach(t => run.started(t));
            const classes = [...new Set(selected.map(t => t.id.split('.')[0]))];
            const methods = selected.map(t => t.id.split('.')[1]);
            const result = await runTests(server, { classes, methods }, line => run.appendOutput(line + '\r\n'), token);
            run.appendOutput(result.summary.replace(/\n/g, '\r\n') + '\r\n');
            for (const test of selected) {
                report(run, test, result.results.find(r => matches(r.name, test.id)));
            }
        } catch (err) {
            const message = new vscode.TestMessage((err as Error).message);
            selected.forEach(t => run.errored(t, message));
        } finally {
            run.end();
        }
    }, true);

    return controller;
}

function* gather(items: vscode.TestItemCollection): Iterable<vscode.TestItem> {
    for (const [, item] of items) {
        yield item;
    }
}

/** The test methods a request covers: classes expand to their methods, excluded items drop out. */
function collect(include: readonly vscode.TestItem[], exclude: readonly vscode.TestItem[]): vscode.TestItem[] {
    const excluded = new Set(exclude.map(t => t.id));
    const methods: vscode.TestItem[] = [];
    for (const item of include) {
        if (excluded.has(item.id)) {
            continue;
        }
        if (item.children.size === 0) {
            methods.push(item);
        } else {
            item.children.forEach(child => excluded.has(child.id) ? undefined : methods.push(child));
        }
    }
    return methods;
}

/** TRX test names are "Class.method", possibly with a namespace in front. */
function matches(trxName: string, id: string): boolean {
    const name = trxName.toLowerCase();
    const wanted = id.toLowerCase();
    return name === wanted || name.endsWith('.' + wanted);
}

function report(run: vscode.TestRun, test: vscode.TestItem, outcome: TestOutcome | undefined): void {
    if (!outcome) {
        run.skipped(test);
        return;
    }
    const duration = durationMs(outcome.duration);
    switch (outcome.outcome) {
        case 'Passed':
            run.passed(test, duration);
            return;
        case 'NotExecuted':
        case 'Skipped':
            run.skipped(test);
            return;
        default: {
            const message = new vscode.TestMessage([outcome.message, outcome.infolog ? `Infolog: ${outcome.infolog}` : ''].filter(Boolean).join('\n') || outcome.outcome);
            run.failed(test, message, duration);
        }
    }
}

/** TRX durations are hh:mm:ss.fffffff. */
function durationMs(duration?: string): number | undefined {
    const m = /^(\d+):(\d+):(\d+(?:\.\d+)?)$/.exec(duration ?? '');
    return m ? ((Number(m[1]) * 60 + Number(m[2])) * 60 + Number(m[3])) * 1000 : undefined;
}
