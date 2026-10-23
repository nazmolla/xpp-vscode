import * as vscode from 'vscode';
import { elementOpenCommand, iconFor, typeLabel } from '../aot';
import { call, ProductivityContext } from '../productivity/common';

/** Element types Who Can Access works on. */
export const AccessTypes = ['AxMenuItemDisplay', 'AxMenuItemOutput', 'AxMenuItemAction', 'AxForm', 'AxTable', 'AxDataEntityView'];
/** Element types Analyze License works on. */
export const LicenseTypes = ['AxSecurityRole', 'AxSecurityDuty', 'AxSecurityPrivilege'];

const KindTypes: Record<string, string> = {
    Role: 'AxSecurityRole', Duty: 'AxSecurityDuty', Privilege: 'AxSecurityPrivilege', DataEntity: 'AxDataEntityView',
};

/** The metadata type of a security kind ('Role', 'MenuItemDisplay', 'DataEntity', ...). */
export function objectTypeOf(kind: string): string {
    if (kind.startsWith('Ax')) {
        return kind;
    }
    return KindTypes[kind] ?? (/^(Display|Output|Action)$/.test(kind) ? `AxMenuItem${kind}` : `Ax${kind}`);
}

/** Calls the language server's security action; a failure becomes an error with the server's message. */
export function security<T = any>(ctx: ProductivityContext, op: string, args: object = {}): Promise<T & { success: boolean; message?: string }> {
    return call<T>(ctx.languageServer, 'lang', 'security', { op, ...args });
}

/** Colour hint for a user license: Operations orange, Activity yellow, Team Members green. */
export function licenseColor(license: string | null | undefined): vscode.ThemeColor | undefined {
    if (!license) {
        return undefined;
    }
    if (/operations/i.test(license)) {
        return new vscode.ThemeColor('charts.orange');
    }
    if (/activity/i.test(license)) {
        return new vscode.ThemeColor('charts.yellow');
    }
    if (/team members/i.test(license)) {
        return new vscode.ThemeColor('charts.green');
    }
    return undefined;
}

export interface RoleInfo {
    name: string;
    label?: string;
    model?: string;
    custom?: boolean;
    extended?: boolean;
    license?: string | null;
}

interface ChildInfo {
    kind: string;
    name: string;
    label?: string;
    model?: string;
    detail?: string;
}

export type SecurityNode =
    | { kind: 'group'; group: 'mine' | 'all' }
    | {
        kind: 'element';
        id: string;
        secKind: string;
        name: string;
        label?: string;
        model?: string;
        detail?: string;
        license?: string | null;
        extended?: boolean;
        custom?: boolean;
        /** Lets the shared element commands (elementOf) read the node. */
        item: { objectType: string; objectName: string };
    }
    | { kind: 'message'; text: string; icon: string };

const Expandable = new Set(['Role', 'Duty', 'Privilege']);

/** The Security view: your roles with their license, all roles, and their duties, privileges and entry points. */
export class SecurityTreeProvider implements vscode.TreeDataProvider<SecurityNode> {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChangeTreeData = this.changed.event;
    private readonly cache = new Map<string, Promise<SecurityNode[]>>();
    view?: vscode.TreeView<SecurityNode>;
    filter = '';

    constructor(private readonly ctx: ProductivityContext) { }

    refresh(): void {
        this.cache.clear();
        this.changed.fire();
    }

    setFilter(filter: string): void {
        this.filter = filter.trim();
        void vscode.commands.executeCommand('setContext', 'xpp.security.filtered', this.filter.length > 0);
        if (this.view) {
            this.view.description = this.filter ? `filter: ${this.filter}` : undefined;
        }
        this.cache.delete('all');
        this.changed.fire();
    }

    getTreeItem(node: SecurityNode): vscode.TreeItem {
        switch (node.kind) {
            case 'group': {
                const mine = node.group === 'mine';
                // Nothing loads until a group is expanded: the first load reads all security metadata (up to a minute).
                // Only a filter the user just typed opens All roles by itself.
                const item = new vscode.TreeItem(mine ? 'Your roles' : 'All roles',
                    !mine && this.filter ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);
                // A new id when the filter changes makes the filtered list open by itself.
                item.id = mine ? 'mine' : `all|${this.filter}`;
                item.iconPath = new vscode.ThemeIcon(mine ? 'account' : 'organization');
                item.description = mine ? 'roles of your models, with license (expand to load)'
                    : this.filter ? `matching '${this.filter}'` : 'expand to load';
                item.tooltip = 'Expanding reads the security metadata of all packages; the first time takes up to a minute.';
                item.contextValue = 'xppSecGroup';
                return item;
            }
            case 'message': {
                const item = new vscode.TreeItem(node.text, vscode.TreeItemCollapsibleState.None);
                item.iconPath = new vscode.ThemeIcon(node.icon);
                item.tooltip = node.text;
                return item;
            }
            case 'element': {
                const objectType = node.item.objectType;
                const item = new vscode.TreeItem(node.name,
                    Expandable.has(node.secKind) ? vscode.TreeItemCollapsibleState.Collapsed : vscode.TreeItemCollapsibleState.None);
                item.id = node.id;
                const color = licenseColor(node.license ?? node.detail);
                const isRole = node.secKind === 'Role';
                item.iconPath = new vscode.ThemeIcon(isRole ? 'shield' : iconFor(objectType), color);
                if (node.license !== undefined) {
                    item.description = [node.license ?? 'license unknown', node.extended ? 'ext' : ''].filter(Boolean).join(' · ');
                } else {
                    item.description = node.detail || (node.label && node.label !== node.name ? node.label : undefined);
                }
                item.tooltip = [
                    node.label && node.label !== node.name ? node.label : '',
                    `${node.name} (${typeLabel(objectType).replace(/s$/, '')})`,
                    node.model ? `Model: ${node.model}` : '',
                    node.license !== undefined ? `License: ${node.license ?? 'unknown'}` : '',
                    node.extended ? 'Extended by your models' : '',
                    node.detail ? node.detail : '',
                ].filter(Boolean).join('\n');
                item.contextValue = contextValue(node.secKind, objectType);
                if (!Expandable.has(node.secKind) && AccessTypes.includes(objectType)) {
                    item.command = elementOpenCommand(objectType, node.name);
                }
                return item;
            }
        }
    }

    getChildren(node?: SecurityNode): Promise<SecurityNode[]> | SecurityNode[] {
        if (!node) {
            return [{ kind: 'group', group: 'mine' }, { kind: 'group', group: 'all' }];
        }
        if (node.kind === 'message') {
            return [];
        }
        const key = node.kind === 'group' ? node.group : node.id;
        let children = this.cache.get(key);
        if (!children) {
            children = this.load(node).catch(err => {
                this.cache.delete(key);
                return [{ kind: 'message', text: (err as Error).message, icon: 'error' } as SecurityNode];
            });
            this.cache.set(key, children);
        }
        return children;
    }

    private async load(node: SecurityNode): Promise<SecurityNode[]> {
        if (node.kind === 'group') {
            const mine = node.group === 'mine';
            const result = await vscode.window.withProgress(
                { location: { viewId: 'xpp.security' }, title: 'Reading security roles…' },
                () => security<{ roles: RoleInfo[] }>(this.ctx, 'roles',
                    mine ? { customOnly: true, withLicense: true } : { filter: this.filter || undefined }));
            const roles = result.roles ?? [];
            if (roles.length === 0) {
                return [{
                    kind: 'message', icon: 'info',
                    text: mine ? 'Your models have no roles and extend none.' : this.filter ? `No roles match '${this.filter}'.` : 'No roles found.',
                }];
            }
            return roles.map(r => ({
                kind: 'element', id: `${node.group}/Role:${r.name}`, secKind: 'Role', name: r.name, label: r.label, model: r.model,
                license: mine ? r.license ?? null : undefined, extended: r.extended, custom: r.custom,
                item: { objectType: 'AxSecurityRole', objectName: r.name },
            }));
        }
        if (node.kind !== 'element') {
            return [];
        }
        const result = await security<{ items: ChildInfo[] }>(this.ctx, 'children', { kind: node.secKind, name: node.name });
        const items = result.items ?? [];
        if (items.length === 0) {
            return [{ kind: 'message', text: 'Empty', icon: 'circle-slash' }];
        }
        return items.map(c => ({
            kind: 'element', id: `${node.id}/${c.kind}:${c.name}`, secKind: c.kind, name: c.name, label: c.label, model: c.model, detail: c.detail,
            item: { objectType: objectTypeOf(c.kind), objectName: c.name },
        }));
    }
}

function contextValue(secKind: string, objectType: string): string {
    switch (secKind) {
        case 'Role': return 'xppSec role';
        case 'Duty': return 'xppSec duty';
        case 'Privilege': return 'xppSec privilege';
        default: return AccessTypes.includes(objectType) ? 'xppSec access' : 'xppSec';
    }
}
