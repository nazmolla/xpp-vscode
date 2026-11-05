import * as vscode from 'vscode';
import { XppBuilder } from './build';
import { readProject } from './project';

export const TaskType = 'xpp';

interface XppTaskDefinition extends vscode.TaskDefinition {
    action: 'buildProject' | 'buildModel';
    /** Model to build (buildModel); the project's model when omitted. */
    model?: string;
}

/**
 * Build tasks (type "xpp") for tasks.json, so builds can be chained with other tasks and bound to keys. They run the
 * same build as the commands; errors go to the Problems view directly, so no problem matcher is needed.
 */
export class XppTaskProvider implements vscode.TaskProvider {
    constructor(private readonly builder: XppBuilder) {}

    provideTasks(): vscode.Task[] {
        return [
            this.create({ type: TaskType, action: 'buildProject' }, 'Build project'),
            this.create({ type: TaskType, action: 'buildModel' }, 'Build model'),
        ];
    }

    resolveTask(task: vscode.Task): vscode.Task | undefined {
        const definition = task.definition as XppTaskDefinition;
        return definition.action === 'buildProject' || definition.action === 'buildModel'
            ? this.create(definition, task.name)
            : undefined;
    }

    private create(definition: XppTaskDefinition, name: string): vscode.Task {
        const task = new vscode.Task(definition, vscode.TaskScope.Workspace, name, 'F&O Studio',
            new vscode.CustomExecution(async () => new BuildTerminal(this.builder, definition)), []);
        task.group = vscode.TaskGroup.Build;
        return task;
    }
}

/** Runs one build and reports its result; the compiler output stays in the X++ Build output. */
class BuildTerminal implements vscode.Pseudoterminal {
    private readonly written = new vscode.EventEmitter<string>();
    private readonly closed = new vscode.EventEmitter<number>();
    private readonly cancel = new vscode.CancellationTokenSource();
    readonly onDidWrite = this.written.event;
    readonly onDidClose = this.closed.event;

    constructor(private readonly builder: XppBuilder, private readonly definition: XppTaskDefinition) {}

    open(): void {
        void this.run();
    }

    close(): void {
        this.cancel.cancel();
    }

    private async run(): Promise<void> {
        const line = (text: string) => this.written.fire(text + '\r\n');
        try {
            const project = await readProject();
            const model = this.definition.model ?? project?.model;
            if (!model) {
                throw new Error('Open a D365FO project or set "model" in the task.');
            }
            const projectOnly = this.definition.action === 'buildProject';
            if (projectOnly && !project) {
                throw new Error('Open a D365FO project first.');
            }
            line(`Building ${projectOnly ? `project ${project!.name}` : `model ${model}`}… (compiler output: X++ Build)`);
            const ok = await this.builder.build(model, this.cancel.token, projectOnly ? project!.items : undefined);
            line(ok ? 'Build succeeded.' : 'Build failed; see the Problems view.');
            this.finish(ok ? 0 : 1);
        } catch (err) {
            line(`Build failed: ${(err as Error).message}`);
            this.finish(1);
        }
    }

    private finish(code: number): void {
        this.closed.fire(code);
        this.written.dispose();
        this.closed.dispose();
        this.cancel.dispose();
    }
}
