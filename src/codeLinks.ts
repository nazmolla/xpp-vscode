import * as vscode from 'vscode';

const TableTypes = ['AxTable', 'AxView', 'AxDataEntityView', 'AxMap'];

/** Intrinsic functions and the element types their first argument names, most likely first. */
const IntrinsicTargets: Record<string, string[]> = {
    tablestr: TableTypes, tablenum: TableTypes, fieldstr: TableTypes, fieldnum: TableTypes, fieldpname: TableTypes, indexstr: TableTypes,
    indexnum: TableTypes, tablemethodstr: TableTypes, tablestaticmethodstr: TableTypes, tablefieldgroupstr: TableTypes, tablepname: TableTypes,
    classstr: ['AxClass'], classnum: ['AxClass'], methodstr: ['AxClass'], staticmethodstr: ['AxClass'], delegatestr: ['AxClass'], attributestr: ['AxClass'],
    formstr: ['AxForm'], formcontrolstr: ['AxForm'], formdatasourcestr: ['AxForm'], formdatafieldstr: ['AxForm'], formmethodstr: ['AxForm'],
    enumstr: ['AxEnum'], enumnum: ['AxEnum'], enumliteralstr: ['AxEnum'],
    extendedtypestr: ['AxEdt'], extendedtypenum: ['AxEdt'],
    querystr: ['AxQuery'], querydatasourcestr: ['AxQuery'], querymethodstr: ['AxQuery'],
    menuitemdisplaystr: ['AxMenuItemDisplay'], menuitemactionstr: ['AxMenuItemAction'], menuitemoutputstr: ['AxMenuItemOutput'],
    ssrsreportstr: ['AxReport'], dataentitydatasourcestr: ['AxDataEntityView'], viewstr: ['AxView'],
    configurationkeystr: ['AxConfigurationKey'], configurationkeynum: ['AxConfigurationKey'], licensecodestr: ['AxLicenseCode'],
    licensecodenum: ['AxLicenseCode'], menustr: ['AxMenu'], tilestr: ['AxTile'], workflowapprovalstr: ['AxWorkflowApproval'],
    workflowtaskstr: ['AxWorkflowTask'], workflowtypestr: ['AxWorkflowTemplate'], dutystr: ['AxSecurityDuty'], rolestr: ['AxSecurityRole'],
    privilegestr: ['AxSecurityPrivilege'],
};

const IntrinsicPattern = /\b([A-Za-z]+)\s*\(\s*([A-Za-z_]\w*)/g;
const LabelPattern = /(["'])(@(?:[A-Za-z]\w*:\w+|[A-Za-z]+\d+))\1/g;
// Strings are matched too, so that // inside a string (a URL) doesn't start a comment.
const CommentPattern = /"(?:[^"\\\n]|\\.)*"|'(?:[^'\\\n]|\\.)*'|\/\*[\s\S]*?\*\/|\/\/[^\n]*/g;

/**
 * Ctrl+click in code: the element named by an intrinsic function (tableStr(CustTable), formControlStr(Form, ...),
 * menuItemDisplayStr(...)) opens in its designer, a label literal ("@SYS123", "@File:Id") in the label editor.
 */
export class XppReferenceLinks implements vscode.DocumentLinkProvider {
    provideDocumentLinks(document: vscode.TextDocument): vscode.DocumentLink[] {
        const links: vscode.DocumentLink[] = [];
        const text = document.getText();
        const comments = [...text.matchAll(CommentPattern)].filter(m => m[0][0] === '/').map(m => [m.index!, m.index! + m[0].length]);
        const inComment = (offset: number) => comments.some(([start, end]) => offset >= start && offset < end);
        for (const m of text.matchAll(IntrinsicPattern)) {
            const types = IntrinsicTargets[m[1].toLowerCase()];
            if (!types || inComment(m.index!)) {
                continue;
            }
            const start = m.index! + m[0].length - m[2].length;
            links.push(link(document, start, m[2].length, [types, m[2]], `Open ${m[2]}`));
        }
        for (const m of text.matchAll(LabelPattern)) {
            if (inComment(m.index!)) {
                continue;
            }
            const label = m[2];
            const colon = label.indexOf(':');
            const file = colon > 0 ? label.slice(1, colon) : /^@([A-Za-z]+)/.exec(label)![1];
            const id = colon > 0 ? label.slice(colon + 1) : label.slice(1);
            links.push(link(document, m.index! + 1, label.length, [['AxLabelFile'], file, id], `Open label ${label}`));
        }
        return links;
    }
}

function link(document: vscode.TextDocument, offset: number, length: number, args: unknown[], tooltip: string): vscode.DocumentLink {
    const range = new vscode.Range(document.positionAt(offset), document.positionAt(offset + length));
    const result = new vscode.DocumentLink(range, vscode.Uri.parse(`command:xpp.openReference?${encodeURIComponent(JSON.stringify(args))}`));
    result.tooltip = tooltip;
    return result;
}
