import { execFile } from 'child_process';
import { existsSync, promises as fs } from 'fs';
import * as path from 'path';

/**
 * The solution around an X++ project, as Visual Studio's Solution Explorer shows it: the solution's projects (X++ and
 * C#), the X++ project's references and the files of each C# project. No vscode dependency.
 */

export interface SolutionInfo {
    file: string;
    name: string;
    /** Full paths of the projects in the solution (.rnrproj, .csproj, ...). */
    projects: string[];
}

export interface ReferenceInfo {
    kind: 'project' | 'assembly';
    /** The item's Name (project name, or assembly name). */
    name: string;
    /** The .csproj of a project reference, or the assembly (HintPath). */
    path?: string;
    exists: boolean;
}

export interface FileNode {
    name: string;
    path: string;
    /** Set for folders. */
    children?: FileNode[];
}

const MaxFiles = 3000;
const SkippedFolders = /^(bin|obj|node_modules|packages|TestResults)$/i;

/** The projects a solution file lists, as full paths; solution folders are left out. */
export function parseSolution(file: string, text: string): string[] {
    const dir = path.dirname(file);
    const paths = file.toLowerCase().endsWith('.slnx')
        ? [...text.matchAll(/<Project\b[^>]*\bPath="([^"]+)"/gi)].map(m => m[1])
        : [...text.matchAll(/^Project\("\{[^}]+\}"\)\s*=\s*"[^"]*",\s*"([^"]+)"/gm)].map(m => m[1]).filter(p => /\.\w*proj$/i.test(p));
    return paths.map(p => path.resolve(dir, p.replace(/\//g, '\\')));
}

/** The solution (.slnx, .sln) that lists the project: in its folder or up to three folders above. */
export async function findSolution(projectFile: string): Promise<SolutionInfo | undefined> {
    const target = path.resolve(projectFile).toLowerCase();
    let dir = path.dirname(path.resolve(projectFile));
    for (let level = 0; level < 4; level++) {
        const files = (await fs.readdir(dir).catch(() => [] as string[])).filter(f => /\.slnx?$/i.test(f)).sort((a, b) => a.localeCompare(b));
        for (const name of files) {
            const file = path.join(dir, name);
            const projects = parseSolution(file, await fs.readFile(file, 'utf8').catch(() => ''));
            if (projects.some(p => p.toLowerCase() === target)) {
                return { file, name: name.replace(/\.slnx?$/i, ''), projects };
            }
        }
        const parent = path.dirname(dir);
        if (parent === dir) {
            break;
        }
        dir = parent;
    }
    return undefined;
}

/** The X++ project's references to C# projects (ProjectReference) and assemblies (Reference with a HintPath). */
export function projectReferences(projectFile: string, xml: string): ReferenceInfo[] {
    const dir = path.dirname(projectFile);
    const refs: ReferenceInfo[] = [];
    for (const m of xml.matchAll(/<ProjectReference\s+Include="([^"]+)"\s*(?:\/>|>([\s\S]*?)<\/ProjectReference>)/g)) {
        const file = path.resolve(dir, decodeXml(m[1]));
        refs.push({ kind: 'project', name: decodeXml(/<Name>([^<]*)<\/Name>/.exec(m[2] ?? '')?.[1] ?? path.basename(file).replace(/\.\w+$/, '')), path: file, exists: existsSync(file) });
    }
    for (const m of xml.matchAll(/<Reference\s+Include="([^"]+)"\s*(?:\/>|>([\s\S]*?)<\/Reference>)/g)) {
        const hint = /<HintPath>([^<]*)<\/HintPath>/.exec(m[2] ?? '')?.[1];
        const file = hint ? path.resolve(dir, decodeXml(hint)) : undefined;
        refs.push({ kind: 'assembly', name: decodeXml(/<Name>([^<]*)<\/Name>/.exec(m[2] ?? '')?.[1] ?? m[1].split(',')[0].trim()), path: file, exists: !!file && existsSync(file) });
    }
    return refs;
}

/** The assembly a C# project builds (AssemblyName, else the project file's name). */
export function assemblyNameOf(csproj: string, xml: string): string {
    return /<AssemblyName>\s*([^<$]+?)\s*<\/AssemblyName>/.exec(xml)?.[1] ?? path.basename(csproj).replace(/\.\w+$/, '');
}

/**
 * A C# project's files as Solution Explorer shows them: SDK-style projects include every file under their folder
 * (bin, obj and hidden folders aside); older projects list their items (Compile, Content, None, ...) and folders.
 */
export async function csharpProjectTree(csproj: string): Promise<FileNode[]> {
    const xml = await fs.readFile(csproj, 'utf8').catch(() => '');
    const dir = path.dirname(csproj);
    const sdk = /<Project\b[^>]*\bSdk=/i.test(xml) || /<Sdk\s+Name=/i.test(xml) || /<Import\b[^>]*\bSdk=/i.test(xml);
    const files: string[] = [];
    const folders: string[] = [];
    if (sdk) {
        await walk(dir, '', files, folders);
    } else {
        for (const m of xml.matchAll(/<(Compile|Content|None|EmbeddedResource|Page|Resource|ApplicationDefinition|EntityDeploy)\s+Include="([^"]+)"\s*(?:\/>|>([\s\S]*?)<\/\1>)/g)) {
            const include = decodeXml(m[2]);
            if (/[*?]|\$\(/.test(include)) {
                continue;
            }
            const link = /<Link>([^<]*)<\/Link>/.exec(m[3] ?? '')?.[1];
            const shown = link ? decodeXml(link) : include;
            if (!shown.startsWith('..')) {
                files.push(shown);
            }
        }
        for (const m of xml.matchAll(/<Folder\s+Include="([^"]+)"/g)) {
            folders.push(decodeXml(m[1]).replace(/[\\/]+$/, ''));
        }
    }
    return toTree(dir, files, folders);
}

async function walk(root: string, relative: string, files: string[], folders: string[]): Promise<void> {
    const entries = await fs.readdir(path.join(root, relative), { withFileTypes: true }).catch(() => []);
    for (const entry of entries) {
        if (files.length >= MaxFiles) {
            return;
        }
        const rel = relative ? path.join(relative, entry.name) : entry.name;
        if (entry.isDirectory()) {
            // A folder with a project of its own is that project's, not this one's.
            const own = (await fs.readdir(path.join(root, rel)).catch(() => [] as string[])).some(f => /\.csproj$/i.test(f));
            if (!entry.name.startsWith('.') && !SkippedFolders.test(entry.name) && !own) {
                folders.push(rel);
                await walk(root, rel, files, folders);
            }
        } else if (entry.isFile() && !/\.(csproj|user)$/i.test(entry.name)) {
            files.push(rel);
        }
    }
}

/** Folders first, then files, each by name (case-insensitive), as Solution Explorer sorts. */
function toTree(root: string, files: string[], folders: string[]): FileNode[] {
    const top: FileNode = { name: '', path: root, children: [] };
    const folderOf = (rel: string): FileNode => {
        let node = top;
        for (const part of rel.split(/[\\/]/).filter(Boolean)) {
            let next = node.children!.find(c => c.children && c.name.toLowerCase() === part.toLowerCase());
            if (!next) {
                next = { name: part, path: path.join(node.path, part), children: [] };
                node.children!.push(next);
            }
            node = next;
        }
        return node;
    };
    folders.forEach(folderOf);
    for (const rel of files) {
        const parts = rel.split(/[\\/]/).filter(Boolean);
        const parent = folderOf(parts.slice(0, -1).join('\\'));
        if (!parent.children!.some(c => !c.children && c.name.toLowerCase() === parts[parts.length - 1].toLowerCase())) {
            parent.children!.push({ name: parts[parts.length - 1], path: path.join(root, rel) });
        }
    }
    const sort = (nodes: FileNode[]) => {
        nodes.sort((a, b) => (a.children ? 0 : 1) - (b.children ? 0 : 1) || a.name.localeCompare(b.name, undefined, { sensitivity: 'base' }));
        nodes.forEach(n => n.children && sort(n.children));
    };
    sort(top.children!);
    return top.children!;
}

/** Adds a project to the solution: .slnx files are edited directly, .sln files with dotnet sln. */
export async function addToSolution(solutionFile: string, projectFile: string): Promise<void> {
    if (parseSolution(solutionFile, await fs.readFile(solutionFile, 'utf8')).some(p => p.toLowerCase() === path.resolve(projectFile).toLowerCase())) {
        return;
    }
    if (solutionFile.toLowerCase().endsWith('.slnx')) {
        const text = await fs.readFile(solutionFile, 'utf8');
        const rel = path.relative(path.dirname(solutionFile), projectFile).replace(/\//g, '\\');
        const eol = text.includes('\r\n') ? '\r\n' : '\n';
        const close = text.lastIndexOf('</Solution>');
        if (close < 0) {
            throw new Error(`${path.basename(solutionFile)} is not a solution file.`);
        }
        await fs.writeFile(solutionFile, `${text.slice(0, close)}  <Project Path="${encodeXml(rel)}" />${eol}${text.slice(close)}`, 'utf8');
        return;
    }
    await new Promise<void>((resolve, reject) => execFile('dotnet', ['sln', solutionFile, 'add', projectFile], { windowsHide: true, timeout: 120_000 },
        (err, stdout, stderr) => err ? reject(new Error((stderr || stdout || err.message).toString().trim())) : resolve()));
}

function decodeXml(text: string): string {
    return text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, '\'').replace(/&amp;/g, '&');
}

function encodeXml(text: string): string {
    return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}
