import * as vscode from 'vscode';

/** One switchable code intelligence feature: a boolean setting xpp.codeIntelligence.<key>. */
export interface FeatureSetting {
    key: string;
    label: string;
    description: string;
    /** Costs noticeable time or resources; shown with a marker in the Dynamics 365 view. */
    slow: boolean;
    default: boolean;
}

export const Features: FeatureSetting[] = [
    { key: 'outline', label: 'Outline and breadcrumbs', description: 'Methods and members of the open object in Outline, breadcrumbs and Go to Symbol in Editor.', slow: false, default: true },
    { key: 'navigation', label: 'Implementations, call and type hierarchy', description: 'Go to Implementations (overrides, Chain of Command, event handlers, subclasses), call hierarchy and type hierarchy, from the cross-reference database.', slow: false, default: true },
    { key: 'workspaceSymbols', label: 'Go to Symbol in Workspace', description: 'Ctrl+T over elements and methods of all models (cross-reference database).', slow: false, default: true },
    { key: 'completion', label: 'Completion and signature help', description: 'Members after . and ::, label references in strings, keywords; parameter hints.', slow: false, default: true },
    { key: 'hover', label: 'Hover details', description: 'Label texts, EDT and enum details and method signatures on hover.', slow: false, default: true },
    { key: 'codeLens', label: 'Code lens', description: 'Reference, override, Chain of Command and handler counts above methods; what a CoC or handler method wraps.', slow: false, default: true },
    { key: 'syntaxCheck', label: 'Live syntax check', description: 'Syntax errors while you type, with the X++ compiler\'s own parser.', slow: false, default: true },
    { key: 'obsoleteWarnings', label: 'Obsolete API warnings', description: 'Warnings (or errors) where code calls methods or uses classes and tables marked [SysObsolete].', slow: false, default: true },
    { key: 'formatting', label: 'Format document', description: '4-space Allman layout for X++ code (Format Document / Format Selection).', slow: false, default: true },
    { key: 'eventHandlerIndex', label: 'Form and data event handlers', description: 'Include [FormControlEventHandler], [DataEventHandler] and similar handlers, which the cross-reference database does not record. The first scan of all packages takes about a minute; it is cached afterwards.', slow: true, default: true },
    { key: 'semanticHighlighting', label: 'Semantic highlighting', description: 'Colour classes, tables, enums, EDTs, methods and fields by what they are. Looks up every identifier of the open document.', slow: true, default: false },
    { key: 'diagnosticsOnSave', label: 'Compile on save', description: 'Build the project with the X++ compiler after saving code, so semantic errors reach Problems (about 10 s per save).', slow: true, default: false },
];

const Section = 'xpp.codeIntelligence';

export function enabled(key: string): boolean {
    const feature = Features.find(f => f.key === key);
    return vscode.workspace.getConfiguration(Section).get<boolean>(key, feature?.default ?? true);
}

export async function setEnabled(key: string, value: boolean): Promise<void> {
    await vscode.workspace.getConfiguration(Section).update(key, value, vscode.ConfigurationTarget.Global);
}

export function affects(e: vscode.ConfigurationChangeEvent, key?: string): boolean {
    return e.affectsConfiguration(key ? `${Section}.${key}` : Section);
}
