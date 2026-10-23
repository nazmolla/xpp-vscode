import * as vscode from 'vscode';
import { typeLabel } from '../aot';
import { addCreatedToProject, elementOf, pickElement, targetModel, TreeArg } from '../d365Tools';
import { call, Element, FindMatch, findExact, IdentifierPattern, ProductivityContext } from './common';

const MenuItemTypes = ['AxMenuItemDisplay', 'AxMenuItemOutput', 'AxMenuItemAction'];
const Grants = ['Read', 'Update', 'Create', 'Correct', 'Delete', 'Invoke'];

/** Adds a menu item to a security privilege of your models (an existing one or a new View/Maintain privilege). */
export async function addToPrivilege(ctx: ProductivityContext, arg?: TreeArg): Promise<void> {
    const element: Element | undefined = elementOf(arg) ?? await pickElement(ctx.server, 'Add to privilege', MenuItemTypes);
    if (!element) {
        return;
    }
    if (!MenuItemTypes.includes(element.objectType)) {
        throw new Error('Only menu items can be added to a privilege.');
    }
    const choice = await vscode.window.showQuickPick([
        { label: '$(add) New privilege…', id: 'new' },
        { label: '$(search) Existing privilege of your models…', id: 'existing' },
    ], { title: `Add ${element.objectName} to a privilege` });
    if (!choice) {
        return;
    }
    const privilege = choice.id === 'new' ? await newPrivilegeName(ctx, element) : await pickPrivilege(ctx);
    if (!privilege) {
        return;
    }
    const isDisplay = element.objectType === 'AxMenuItemDisplay';
    const defaults = isDisplay ? (privilege.name.endsWith('Maintain') ? ['Read', 'Update', 'Create', 'Delete'] : ['Read']) : ['Invoke'];
    const grants = await vscode.window.showQuickPick(Grants.map(g => ({ label: g, picked: defaults.includes(g) })), {
        title: `Access ${privilege.name} grants to ${element.objectName}`, canPickMany: true,
    });
    if (!grants?.length) {
        return;
    }
    const entryPoint = {
        name: element.objectName,
        objectType: element.objectType.replace(/^Ax/, ''),
        objectName: element.objectName,
        grant: grants.map(g => g.label).join(','),
    };
    if (privilege.isNew) {
        const model = await targetModel('Add to privilege');
        if (!model) {
            return;
        }
        await call(ctx.server, 'bridge', 'create_object', { objectType: 'AxSecurityPrivilege', objectName: privilege.name, modelName: model, entryPoints: [entryPoint] });
    } else {
        await call(ctx.server, 'bridge', 'update_object', { objectType: 'AxSecurityPrivilege', objectName: privilege.name, entryPoints: [entryPoint] });
    }
    await addCreatedToProject(ctx.server, 'AxSecurityPrivilege', privilege.name);
    ctx.refreshTrees();
    const open = await vscode.window.showInformationMessage(
        `${typeLabel(element.objectType)} ${element.objectName} is in privilege ${privilege.name} (${entryPoint.grant}).`, 'Open Privilege');
    if (open) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxSecurityPrivilege', privilege.name);
    }
}

async function newPrivilegeName(ctx: ProductivityContext, element: Element): Promise<{ name: string; isNew: boolean } | undefined> {
    const suffix = await vscode.window.showQuickPick(['View', 'Maintain'], { title: 'New privilege: kind' });
    if (!suffix) {
        return undefined;
    }
    const name = await vscode.window.showInputBox({
        title: 'New privilege', prompt: 'Privilege name', value: `${element.objectName}${suffix}`, ignoreFocusOut: true,
        validateInput: v => IdentifierPattern.test(v) ? undefined : 'Letters, digits or _, starting with a letter.',
    });
    if (!name) {
        return undefined;
    }
    const existing = await findExact(ctx.server, 'AxSecurityPrivilege', name);
    if (existing && !existing.isCustom) {
        throw new Error(`Privilege ${name} exists in ${existing.modelName}, which is not one of your models.`);
    }
    return { name: existing?.objectName ?? name, isNew: !existing };
}

async function pickPrivilege(ctx: ProductivityContext): Promise<{ name: string; isNew: boolean } | undefined> {
    const filter = await vscode.window.showInputBox({
        title: 'Existing privilege', prompt: 'Privilege name (or part of it, at least 3 characters)', ignoreFocusOut: true,
        validateInput: v => v.trim().length >= 3 ? undefined : 'Enter at least 3 characters.',
    });
    if (!filter) {
        return undefined;
    }
    const result = await call<{ matches: FindMatch[] }>(ctx.server, 'bridge', 'find_object', {
        objectName: filter.trim(), objectType: 'AxSecurityPrivilege', exactMatch: false, maxResults: 50,
    });
    const mine = (result.matches ?? []).filter(m => m.objectType === 'AxSecurityPrivilege' && m.isCustom);
    if (mine.length === 0) {
        vscode.window.showInformationMessage(`None of your models has a privilege matching '${filter}'.`);
        return undefined;
    }
    const picked = await vscode.window.showQuickPick(mine.map(m => ({ label: m.objectName, description: m.modelName })), { title: 'Privilege' });
    return picked ? { name: picked.label, isNew: false } : undefined;
}
