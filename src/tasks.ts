import * as vscode from 'vscode';
import { XppBuilder } from './build';
import { DeployState } from './deployState';
import { PacCommands } from './pac';
import { readProject } from './project';

export const TaskType = 'xpp';

const Actions = ['buildProject', 'buildModel', 'deploy', 'syncDatabase'] as const;

interface XppTaskDefinition extends vscode.TaskDefinition {
    action: typeof Actions[number];
    /** Model to build (buildModel); the project's model when omitted. */
    model?: string;
    /** deploy: database sync after the deploy (None, Incremental, Full). */
    dbSync?: string;
    /** syncDatabase on a unified environment: Full or Module. */
    mode?: string;
    /** syncDatabase with mode Module: comma-separated modules; the project's model when omitted. */
    modules?: string;
}

/**
 * Build, deploy and database sync tasks (type "xpp") for tasks.json, so they can be chained with other tasks and bound to
 * keys. They run the same code as the commands; build errors go to the Problems view directly, so no problem matcher is needed.
 */
export class XppTaskProvider implements vscode.TaskProvider {
    constructor(private readonly builder: XppBuilder, private readonly pac: PacCommands, private readonly deployState: DeployState) {}

    provideTasks(): vscode.Task[] {
        return [
            this.create({ type: TaskType, action: 'buildProject' }, 'Build project'),
            this.create({ type: TaskType, action: 'buildModel' }, 'Build model'),
            this.create({ type: TaskType, action: 'deploy', dbSync: 'Incremental' }, 'Deploy models'),
            this.create({ type: TaskType, action: 'syncDatabase', mode: 'Module' }, 'Synchronize database'),
        ];
    }

    resolveTask(task: vscode.Task): vscode.Task | undefined {
        const definition = task.definition as XppTaskDefinition;
        return Actions.includes(definition.action) ? this.create(definition, task.name) : undefined;
    }

    private create(definition: XppTaskDefinition, name: string): vscode.Task {
        const deploying = definition.action === 'deploy' || definition.action === 'syncDatabase';
        const execution = new vscode.CustomExecution(async () => deploying
            ? new DeployTerminal(this.pac, this.deployState, definition)
            : new BuildTerminal(this.builder, definition));
        const task = new vscode.Task(definition, vscode.TaskScope.Workspace, name, 'F&O Studio', execution, []);
        if (!deploying) {
            task.group = vscode.TaskGroup.Build;
        }
        return task;
    }
}

/** Deploys or synchronizes through the same pac (or SyncEngine) commands; their output shows in their own terminals. */
class DeployTerminal implements vscode.Pseudoterminal {
    private readonly written = new vscode.EventEmitter<string>();
    private readonly closed = new vscode.EventEmitter<number>();
    readonly onDidWrite = this.written.event;
    readonly onDidClose = this.closed.event;

    constructor(private readonly pac: PacCommands, private readonly deployState: DeployState, private readonly definition: XppTaskDefinition) {}

    open(): void {
        void this.run();
    }

    close(): void {
        // pac runs in its own task terminal, where it is stopped.
    }

    private async run(): Promise<void> {
        const line = (text: string) => this.written.fire(text + '\r\n');
        try {
            if (this.definition.action === 'deploy') {
                line(`Deploying (database sync: ${this.definition.dbSync ?? 'None'})…`);
                const models = await this.pac.deploy({ dbSync: this.definition.dbSync ?? 'None' });
                if (models?.length) {
                    await this.deployState.deployed(models);
                }
                line(models?.length ? `Deployed ${models.join(', ')}.` : 'Nothing was deployed.');
            } else {
                line('Synchronizing the database…');
                await this.pac.syncDatabase({ mode: this.definition.mode ?? 'Full', modules: this.definition.modules });
                line('Database synchronization finished.');
            }
            this.closed.fire(0);
        } catch (err) {
            line(`Failed: ${(err as Error).message}`);
            this.closed.fire(1);
        }
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
