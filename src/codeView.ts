import * as vscode from 'vscode';
import { XppServerClient } from './serverClient';
import { composeSource, methodName, splitSource } from './xppSource';

export const XppScheme = 'xpp';

/** Object types whose code can be edited (declaration + methods). */
const EditableTypes = new Set(['AxClass', 'AxTable', 'AxForm', 'AxView', 'AxDataEntityView', 'AxQuery', 'AxMap']);

export function objectUri(objectType: string, objectName: string): vscode.Uri {
    return vscode.Uri.from({ scheme: XppScheme, path: `/${objectType}/${objectName}.xpp` });
}

export function parseObjectUri(uri: vscode.Uri): { objectType: string; objectName: string } {
    const parts = uri.path.replace(/^\//, '').split('/');
    if (parts.length !== 2 || !parts[1].endsWith('.xpp')) {
        throw vscode.FileSystemError.FileNotFound(uri);
    }
    return { objectType: parts[0], objectName: parts[1].slice(0, -4) };
}

interface CachedObject {
    text: string;
    methodNames: string[];
    /** Forms store the class declaration as a method named classDeclaration. */
    declarationIsMethod: boolean;
    declaration: string;
    editable: boolean;
    mtime: number;
}

/**
 * Presents each X++ object as a virtual .xpp document. Saving writes the declaration and
 * methods back through the metadata API (never the XML on disk), like Visual Studio's code view.
 */
export class XppCodeFileSystem implements vscode.FileSystemProvider {
    private readonly changed = new vscode.EventEmitter<vscode.FileChangeEvent[]>();
    readonly onDidChangeFile = this.changed.event;
    private readonly cache = new Map<string, CachedObject>();

    constructor(private readonly server: XppServerClient) { }

    watch(): vscode.Disposable {
        return new vscode.Disposable(() => undefined);
    }

    async stat(uri: vscode.Uri): Promise<vscode.FileStat> {
        const obj = await this.load(uri);
        return {
            type: vscode.FileType.File,
            ctime: obj.mtime,
            mtime: obj.mtime,
            size: Buffer.byteLength(obj.text),
            permissions: obj.editable ? undefined : vscode.FilePermission.Readonly,
        };
    }

    async readFile(uri: vscode.Uri): Promise<Uint8Array> {
        return Buffer.from((await this.load(uri, true)).text, 'utf8');
    }

    async writeFile(uri: vscode.Uri, content: Uint8Array): Promise<void> {
        const { objectType, objectName } = parseObjectUri(uri);
        const current = await this.load(uri);
        if (!current.editable) {
            throw vscode.FileSystemError.NoPermissions(`${objectType} ${objectName} is not in a custom model. Change it with an extension or Chain of Command instead.`);
        }

        const source = splitSource(Buffer.from(content).toString('utf8'), current.declaration);
        if (source.methods.some(m => methodName(m) === 'classDeclaration')) {
            throw vscode.FileSystemError.NoPermissions('Nested classes (data source and control methods) can\'t be edited in this view yet.');
        }
        const methods = current.declarationIsMethod && source.declaration ? [source.declaration, ...source.methods] : source.methods;
        const newNames = methods.map(methodName).filter(n => n);
        const removed = current.methodNames.filter(n => !newNames.some(m => m.toLowerCase() === n.toLowerCase()));

        const result = await this.server.bridge('update_object', {
            objectType,
            objectName,
            declaration: current.declarationIsMethod ? '' : source.declaration,
            methods,
            removeMethodNames: removed,
            formatCode: false,
        });
        if (!result.success) {
            throw vscode.FileSystemError.Unavailable(result.message ?? 'Save failed.');
        }
        this.cache.delete(uri.toString());
        this.changed.fire([{ type: vscode.FileChangeType.Changed, uri }]);
    }

    /** Drops the cached copy so the next read comes from metadata. */
    refresh(uri: vscode.Uri): void {
        this.cache.delete(uri.toString());
        this.changed.fire([{ type: vscode.FileChangeType.Changed, uri }]);
    }

    readDirectory(): [string, vscode.FileType][] {
        return [];
    }
    createDirectory(): void {
        throw vscode.FileSystemError.NoPermissions('Create objects with the X++ tools.');
    }
    delete(): void {
        throw vscode.FileSystemError.NoPermissions('Deleting objects is not supported.');
    }
    rename(): void {
        throw vscode.FileSystemError.NoPermissions('Renaming objects is not supported.');
    }

    private async load(uri: vscode.Uri, fresh = false): Promise<CachedObject> {
        const key = uri.toString();
        const cached = this.cache.get(key);
        if (cached && !fresh) {
            return cached;
        }
        const { objectType, objectName } = parseObjectUri(uri);
        const result = await this.server.bridge('read_object', { objectType, objectName });
        if (!result.success) {
            throw vscode.FileSystemError.FileNotFound(result.message ?? uri);
        }
        const methods: { name: string; source: string }[] = result.methods ?? [];
        const text = composeSource(result.declaration ?? '', methods);
        const declarationIsMethod = !(result.declaration ?? '').trim() && methods[0]?.name.toLowerCase() === 'classdeclaration';
        const obj: CachedObject = {
            text,
            methodNames: methods.map(m => m.name),
            declarationIsMethod,
            declaration: declarationIsMethod ? methods[0].source : result.declaration ?? '',
            editable: !!result.isCustom && EditableTypes.has(objectType),
            mtime: cached && cached.text === text ? cached.mtime : Date.now(),
        };
        this.cache.set(key, obj);
        return obj;
    }
}
