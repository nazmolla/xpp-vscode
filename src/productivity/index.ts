import * as vscode from 'vscode';
import { TreeArg } from '../d365Tools';
import { addToPrivilege } from './addToPrivilege';
import { CommandRegistrar, ProductivityContext } from './common';
import { findExamples } from './findExamples';
import { findMessageSource } from './findMessageSource';
import { generateTests } from './generateTests';
import { ObsoleteWarnings } from './obsolete';
import { XppQuickFixes } from './quickFixes';
import { runSnippet } from './runSnippet';
import { searchCode } from './searchCode';

/** Code search, similar implementations, obsolete API warnings, quick fixes, test generation and snippet runs. */
export function registerProductivity(context: vscode.ExtensionContext, command: CommandRegistrar, ctx: ProductivityContext): void {
    context.subscriptions.push(new ObsoleteWarnings(ctx));
    new XppQuickFixes(ctx, context.globalState).register(context, command);
    command('xpp.searchCode', () => searchCode(ctx, context.globalState));
    command('xpp.findMessageSource', (entry?: { message?: string }) => findMessageSource(ctx, entry));
    command('xpp.findExamples', (arg?: TreeArg) => findExamples(ctx, arg));
    command('xpp.addToPrivilege', (arg?: TreeArg) => addToPrivilege(ctx, arg));
    command('xpp.generateTests', (arg?: TreeArg) => generateTests(ctx, arg));
    command('xpp.runSnippet', () => runSnippet(ctx));
}
