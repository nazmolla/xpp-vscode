export interface CompilerDiagnostic {
    type: string;
    severity: string;
    path: string;
    moniker: string;
    message: string;
    line?: number;
    column?: number;
    endLine?: number;
    endColumn?: number;
}

/** Reads the diagnostics xppc writes with -xmllog. */
export function parseDiagnostics(xml: string): CompilerDiagnostic[] {
    const result: CompilerDiagnostic[] = [];
    const tag = (block: string, name: string) => {
        const m = new RegExp(`<${name}>([\\s\\S]*?)</${name}>`).exec(block);
        return m ? decodeXml(m[1].trim()) : undefined;
    };
    const num = (block: string, name: string) => {
        const v = tag(block, name);
        return v !== undefined && /^\d+$/.test(v) ? Number(v) : undefined;
    };
    for (const m of xml.matchAll(/<Diagnostic>([\s\S]*?)<\/Diagnostic>/g)) {
        const block = m[1];
        result.push({
            type: tag(block, 'DiagnosticType') ?? '',
            severity: tag(block, 'Severity') ?? 'Error',
            path: tag(block, 'Path') ?? '',
            moniker: tag(block, 'Moniker') ?? '',
            message: tag(block, 'Message') ?? '',
            line: num(block, 'Line'),
            column: num(block, 'Column'),
            endLine: num(block, 'EndLine'),
            endColumn: num(block, 'EndColumn'),
        });
    }
    return result;
}

/** Object type and name a diagnostic belongs to; code diagnostics use dynamics://Class/Name/Method/m paths. */
export function diagnosticTarget(diagnosticPath: string): { objectType: string; objectName: string; isCode: boolean } | undefined {
    const isCode = diagnosticPath.startsWith('dynamics://');
    const segments = diagnosticPath.replace(/^dynamics:\/\//, '').split('/').filter(s => s);
    if (segments.length < 2) {
        return undefined;
    }
    const objectType = segments[0].startsWith('Ax') ? segments[0] : 'Ax' + segments[0];
    return { objectType, objectName: segments[1], isCode };
}

function decodeXml(text: string): string {
    return text.replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&quot;/g, '"').replace(/&apos;/g, '\'').replace(/&amp;/g, '&');
}
