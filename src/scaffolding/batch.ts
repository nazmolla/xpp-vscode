import * as vscode from 'vscode';
import { XppServerClient } from '../serverClient';
import { askName, askText, createElements, doc, ElementSpec, labelFor, projectModel, xppString } from './common';

/**
 * F&O Studio: New Batch Job (SysOperation): contract, service and controller classes, an action menu item and a privilege
 * that grants it, the standard SysOperation pattern.
 */
export async function newSysOperationJob(server: XppServerClient): Promise<void> {
    const model = await projectModel();
    const base = await askName('New batch job (SysOperation): name', 'Base name with your prefix; the classes get Contract, Service and Controller appended', 'ABCPostVendorInvoices');
    const caption = base && await askText('New batch job: caption', 'What users see on the menu and in the batch job list', 'Post vendor invoices');
    if (!base || !caption) {
        return;
    }
    const label = await labelFor(server, model, base, caption);
    const contract = `${base}Contract`, service = `${base}Service`, controller = `${base}Controller`, privilege = `${base}Generate`;
    const elements: ElementSpec[] = [
        {
            objectType: 'AxClass', objectName: contract, body: {
                declaration: `${doc(`Parameters of ${caption}.`)}[DataContractAttribute]\nclass ${contract}\n{\n    TransDate transDate;\n}`,
                methods: [`${doc('The date the job works for.')}[DataMemberAttribute]\npublic TransDate parmTransDate(TransDate _transDate = transDate)\n{\n    transDate = _transDate;\n    return transDate;\n}`],
            },
        },
        {
            objectType: 'AxClass', objectName: service, body: {
                declaration: `${doc(`${caption}: the work, run by ${controller} in batch or interactively.`)}class ${service} extends SysOperationServiceBase\n{\n}`,
                methods: [`${doc('Runs the job for the contract\'s parameters.', '/// <param name="_contract">The parameters.</param>\n')}public void process(${contract} _contract)\n{\n    // Write the job's work here for _contract.parmTransDate(), in one transaction per unit of work so a restart can continue.\n}`],
            },
        },
        {
            objectType: 'AxClass', objectName: controller, body: {
                declaration: `${doc(`Starts ${caption} (dialog, batch scheduling) for ${service}.`)}class ${controller} extends SysOperationServiceController\n{\n}`,
                methods: [
                    `${doc('Entry point of the menu item.', '/// <param name="_args">Menu item arguments.</param>\n')}public static void main(Args _args)\n{\n    ${controller} controller = new ${controller}(classStr(${service}), methodStr(${service}, process), SysOperationExecutionMode::Synchronous);\n    controller.parmArgs(_args);\n    controller.startOperation();\n}`,
                    `${doc('The caption of the dialog and the batch job.', '/// <returns>The caption.</returns>\n')}public ClassDescription defaultCaption()\n{\n    return ${xppString(label)};\n}`,
                ],
            },
        },
        { objectType: 'AxMenuItemAction', objectName: base, body: { properties: { Label: label, ObjectType: 'Class', Object: controller } } },
        {
            objectType: 'AxSecurityPrivilege', objectName: privilege, body: {
                properties: { Label: label },
                entryPoints: [{ name: base, objectType: 'MenuItemAction', objectName: base, grant: 'Invoke' }],
            },
        },
    ];
    await createElements(server, model, `Creating ${base}…`, elements);
    const choice = await vscode.window.showInformationMessage(
        `Created ${elements.map(e => e.objectName).join(', ')} in ${model}. Add the privilege ${privilege} to a duty, then write the job in ${service}.process.`, 'Open Service');
    if (choice) {
        await vscode.commands.executeCommand('xpp.openDesigner', 'AxClass', service);
    }
}
