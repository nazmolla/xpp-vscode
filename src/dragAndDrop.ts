import * as vscode from 'vscode';
import { elementOf, TreeArg } from './d365Tools';
import { XppServerClient } from './serverClient';

export interface DraggedElement {
    objectType: string;
    objectName: string;
}

/** Elements dragged from the Application Explorer or the project: xpp-element:/<type>/<name> in text/uri-list, and as JSON. */
export const ElementsMime = 'application/vnd.xpp.elements';
const ElementScheme = 'xpp-element';

/**
 * Drag source of the Application Explorer and the project view, and drop target of the project view (Add to Project).
 * Designers (webviews) only see what the operating system drag carries, so the last drag is also kept here for them.
 */
export class ElementDragAndDrop implements vscode.TreeDragAndDropController<unknown> {
    readonly dragMimeTypes = [ElementsMime, 'text/uri-list'];
    readonly dropMimeTypes = [ElementsMime];
    private lastDrag: { elements: DraggedElement[]; at: number } | undefined;

    constructor(private readonly server: XppServerClient, private readonly projectChanged: () => void) { }

    /** What was dragged last, for a drop that arrived without data (within a minute). */
    recentDrag(): DraggedElement[] | undefined {
        const drag = this.lastDrag;
        return drag && Date.now() - drag.at < 60000 ? drag.elements : undefined;
    }

    handleDrag(source: readonly unknown[], dataTransfer: vscode.DataTransfer): void {
        const elements = source.map(node => elementOf(node as TreeArg)).filter((e): e is DraggedElement => !!e);
        if (elements.length === 0) {
            return;
        }
        this.lastDrag = { elements, at: Date.now() };
        dataTransfer.set(ElementsMime, new vscode.DataTransferItem(JSON.stringify(elements)));
        dataTransfer.set('text/uri-list', new vscode.DataTransferItem(elements.map(e => elementUri(e).toString()).join('\r\n')));
    }

    async handleDrop(_target: unknown, dataTransfer: vscode.DataTransfer): Promise<void> {
        const elements = await draggedElements(dataTransfer);
        if (!elements?.length) {
            return;
        }
        const failed: string[] = [];
        for (const element of elements) {
            const result = await this.server.bridge('add_to_project', element);
            if (!result.success) {
                failed.push(`${element.objectName}: ${result.message}`);
            }
        }
        this.projectChanged();
        if (failed.length) {
            vscode.window.showWarningMessage(failed.join('\n'));
        } else {
            vscode.window.setStatusBarMessage(`Added ${elements.map(e => e.objectName).join(', ')} to the project.`, 5000);
        }
    }
}

export function elementUri(element: DraggedElement): vscode.Uri {
    return vscode.Uri.from({ scheme: ElementScheme, path: `/${element.objectType}/${element.objectName}` });
}

/** Elements in xpp-element: URIs (text/uri-list lines). */
export function elementsFromUris(uris: string[]): DraggedElement[] {
    return uris.map(u => u.trim()).filter(u => u.startsWith(`${ElementScheme}:`)).map(u => {
        const [, objectType, objectName] = vscode.Uri.parse(u).path.split('/');
        return { objectType, objectName };
    }).filter(e => e.objectType && e.objectName);
}

async function draggedElements(dataTransfer: vscode.DataTransfer): Promise<DraggedElement[] | undefined> {
    const json = await dataTransfer.get(ElementsMime)?.asString();
    if (json) {
        return JSON.parse(json);
    }
    const uris = await dataTransfer.get('text/uri-list')?.asString();
    return uris ? elementsFromUris(uris.split(/\r?\n/)) : undefined;
}

/** Dropping an element into X++ code inserts its name, as in Visual Studio. */
export class ElementDropIntoCode implements vscode.DocumentDropEditProvider {
    async provideDocumentDropEdits(_document: vscode.TextDocument, _position: vscode.Position, dataTransfer: vscode.DataTransfer): Promise<vscode.DocumentDropEdit | undefined> {
        const elements = await draggedElements(dataTransfer);
        return elements?.length ? new vscode.DocumentDropEdit(elements.map(e => e.objectName).join(', '), 'Insert element name') : undefined;
    }
}
