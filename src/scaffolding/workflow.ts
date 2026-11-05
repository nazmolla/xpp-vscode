import { doc, ElementSpec, lower } from './xpp';

export interface WorkflowInput {
    base: string;
    label: string;
    table: string;
    /** Display menu item that opens the document's form. */
    documentMenuItem: string;
    /** ModuleAxapta value of the workflow category. */
    module: string;
    previewFieldGroup?: string;
}

/** Statuses of the document as the workflow moves it along. */
export const WorkflowStatuses = ['Draft', 'Submitted', 'ChangeRequested', 'Approved', 'Rejected', 'Cancelled'];

/** The status enum, field and methods the workflow needs on its table. */
export function workflowTableChanges(input: WorkflowInput): { field: Record<string, unknown>; methods: string[] } {
    const { base, table } = input;
    const status = `${base}Status`, record = lower(table);
    return {
        field: { name: status, fieldType: 'Enum', enumType: status },
        methods: [
            `${doc('Whether the record can be submitted to the workflow: only drafts and documents sent back for changes.', '/// <param name="_workflowType">The workflow type.</param>\n/// <returns>true when it can be submitted.</returns>\n')}public boolean canSubmitToWorkflow(str _workflowType = '')\n{\n    return this.${status} == ${status}::Draft || this.${status} == ${status}::ChangeRequested || this.${status} == ${status}::Rejected;\n}`,
            `${doc('Sets the workflow status of a record.', '/// <param name="_recId">The record.</param>\n/// <param name="_status">The new status.</param>\n')}public static void setWorkflowStatus(RefRecId _recId, ${status} _status)\n{\n    ${table} ${record};\n\n    ttsbegin;\n    update_recordset ${record}\n        setting ${status} = _status\n        where ${record}.RecId == _recId;\n    ttscommit;\n}`,
        ],
    };
}

/**
 * The elements of an approval workflow on one table, the standard pattern (as in BankReconciliationApproval): status enum,
 * document query and class, workflow category, type and approval, event handlers, submit/resubmit class, the action
 * menu items of the workflow buttons and privileges for submitters and approvers.
 */
export function workflowElements(input: WorkflowInput): ElementSpec[] {
    const { base, label, table, documentMenuItem, module, previewFieldGroup } = input;
    const status = `${base}Status`, query = `${base}Query`, document = `${base}Document`, typeHandler = `${base}EventHandler`,
        approvalHandler = `${base}ApprovalEventHandler`, submit = `${base}Submit`, category = `${base}Category`, approval = `${base}Approval`, type = `${base}Type`;
    const item = (name: string, labelId: string, object: string, extra: Record<string, string> = {}): ElementSpec =>
        ({ objectType: 'AxMenuItemAction', objectName: `${base}${name}`, body: { properties: { Label: labelId, ObjectType: 'Class', Object: object, ...extra } } });
    const statusUpdate = (value: string, args: string) => `    ${table}::setWorkflowStatus(${args}.parmWorkflowContext().parmRecId(), ${status}::${value});`;
    const outcome = (name: string, enabled: boolean, outcomeType: string, menuItem?: string) =>
        ({ Name: name, Enabled: enabled ? 'Yes' : 'No', Type: outcomeType, ...(enabled ? { ActionMenuItem: menuItem, EventHandler: approvalHandler } : {}) });
    const elementArgs = '/// <param name="_workflowElementEventArgs">The workflow element event.</param>\n';
    const typeArgs = '/// <param name="_workflowEventArgs">The workflow event.</param>\n';

    return [
        {
            objectType: 'AxEnum', objectName: status, body: {
                properties: { Label: `${label} status`, IsExtensible: 'true' },
                enumValues: WorkflowStatuses.map((name, value) => ({ name, value, label: name.replace(/([a-z])([A-Z])/g, '$1 $2') })),
            },
        },
        { objectType: 'AxQuery', objectName: query, body: { dataSources: [{ name: table, table }] } },
        {
            objectType: 'AxClass', objectName: document, body: {
                declaration: `${doc(`The workflow document of ${label}.`)}class ${document} extends WorkflowDocument\n{\n}`,
                methods: [`${doc('The query of the workflow document.', '/// <returns>The query name.</returns>\n')}public QueryName getQueryName()\n{\n    return queryStr(${query});\n}`],
            },
        },
        {
            objectType: 'AxClass', objectName: typeHandler, body: {
                declaration: `${doc(`Keeps the status of ${table} in step with the ${label} workflow.`)}class ${typeHandler} implements WorkflowStartedEventHandler, WorkflowCanceledEventHandler, WorkflowCompletedEventHandler\n{\n}`,
                methods: [
                    `${doc('The workflow started: the document is submitted.', typeArgs)}public void started(WorkflowEventArgs _workflowEventArgs)\n{\n${statusUpdate('Submitted', '_workflowEventArgs')}\n}`,
                    `${doc('The workflow was canceled: the document is a draft again.', typeArgs)}public void canceled(WorkflowEventArgs _workflowEventArgs)\n{\n${statusUpdate('Draft', '_workflowEventArgs')}\n}`,
                    `${doc('The workflow completed: the document is approved.', typeArgs)}public void completed(WorkflowEventArgs _workflowEventArgs)\n{\n${statusUpdate('Approved', '_workflowEventArgs')}\n}`,
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: approvalHandler, body: {
                declaration: `${doc(`Outcomes of the ${label} approval.`)}class ${approvalHandler} implements WorkflowElementCompletedEventHandler, WorkflowElementReturnedEventHandler, WorkflowElemChangeRequestedEventHandler\n{\n}`,
                methods: [
                    `${doc('An approver approved; the workflow completes the document.', elementArgs)}public void completed(WorkflowElementEventArgs _workflowElementEventArgs)\n{\n}`,
                    `${doc('An approver rejected the document.', elementArgs)}public void returned(WorkflowElementEventArgs _workflowElementEventArgs)\n{\n${statusUpdate('Rejected', '_workflowElementEventArgs')}\n}`,
                    `${doc('An approver asked for changes.', elementArgs)}public void changeRequested(WorkflowElementEventArgs _workflowElementEventArgs)\n{\n${statusUpdate('ChangeRequested', '_workflowElementEventArgs')}\n}`,
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: submit, body: {
                declaration: `${doc(`Submits and resubmits ${table} records to the ${label} workflow.`)}class ${submit}\n{\n}`,
                methods: [`${doc('Entry point of the Submit and Resubmit menu items (workflow bar of the form).', '/// <param name="_args">Menu item arguments: the record and the calling form.</param>\n')}public static void main(Args _args)\n{\n`
                    + `    ${table} record = _args.record();\n    Object caller = _args.caller();\n\n`
                    + `    if (!record || record.TableId != tableNum(${table}) || !caller)\n    {\n        throw error(Error::wrongUseOfFunction(funcName()));\n    }\n\n`
                    + `    if (_args.menuItemName() == menuItemActionStr(${base}Submit))\n    {\n`
                    + `        WorkflowSubmitDialog submitDialog = WorkflowSubmitDialog::construct(caller.getActiveWorkflowConfiguration());\n        submitDialog.run();\n`
                    + `        if (submitDialog.parmIsClosedOK())\n        {\n            Workflow::activateFromWorkflowType(workflowTypeStr(${type}), record.RecId, submitDialog.parmWorkflowComment(), NoYes::No);\n            ${table}::setWorkflowStatus(record.RecId, ${status}::Submitted);\n        }\n    }\n    else\n    {\n`
                    + `        WorkflowWorkItemTable workItem = caller.getActiveWorkflowWorkItem();\n`
                    + `        WorkflowWorkItemActionDialog actionDialog = WorkflowWorkItemActionDialog::construct(workItem, WorkflowWorkItemActionType::Resubmit, new MenuFunction(menuItemActionStr(${base}Resubmit), MenuItemType::Action));\n        actionDialog.run();\n`
                    + `        if (actionDialog.parmIsClosedOK())\n        {\n            WorkflowWorkItemActionManager::dispatchWorkItemAction(workItem, actionDialog.parmWorkflowComment(), actionDialog.parmTargetUser(), WorkflowWorkItemActionType::Resubmit, _args.menuItemName());\n            ${table}::setWorkflowStatus(record.RecId, ${status}::Submitted);\n        }\n    }\n\n`
                    + `    FormDataSource dataSource = FormDataUtil::getFormDataSource(record);\n    if (dataSource)\n    {\n        dataSource.research(true);\n    }\n    caller.updateWorkflowControls();\n}`],
            },
        },
        item('Submit', '@SYS68352', submit, { LinkedPermissionType: 'Form', LinkedPermissionObject: 'WorkflowSubmitDialog' }),
        item('Resubmit', '@SYS109048', submit),
        item('Cancel', '@SYS323585', 'WorkflowCancelManager', { LinkedPermissionType: 'Form', LinkedPermissionObject: 'WorkflowCancelWorkflow' }),
        item('Approve', '@SYS105443', 'WorkflowWorkItemActionManager'),
        item('Reject', '@SYS105445', 'WorkflowWorkItemActionManager'),
        item('RequestChange', '@SYS105446', 'WorkflowWorkItemActionManager'),
        item('Delegate', '@SYS109047', 'WorkflowWorkItemActionManager', { LinkedPermissionType: 'Form', LinkedPermissionObject: 'WorkflowWorkItemActionDialog' }),
        { objectType: 'AxWorkflowCategory', objectName: category, body: { properties: { Label: label, Module: module } } },
        {
            objectType: 'AxWorkflowApproval', objectName: approval, body: {
                typedMetadata: {
                    Label: label,
                    Document: document,
                    DocumentMenuItem: documentMenuItem,
                    ...(previewFieldGroup ? { DocumentPreviewFieldGroup: previewFieldGroup } : {}),
                    ResubmitMenuItem: `${base}Resubmit`,
                    DelegateMenuItem: `${base}Delegate`,
                    Approve: outcome('Approve', true, 'Complete', `${base}Approve`),
                    Reject: outcome('Reject', true, 'Return', `${base}Reject`),
                    RequestChange: outcome('RequestChange', true, 'RequestChange', `${base}RequestChange`),
                    Deny: outcome('Deny', false, 'Deny'),
                },
            },
        },
        {
            objectType: 'AxWorkflowTemplate', objectName: type, body: {
                typedMetadata: {
                    Label: label,
                    Category: category,
                    Document: document,
                    DocumentMenuItem: documentMenuItem,
                    StartedEventHandler: typeHandler,
                    CompletedEventHandler: typeHandler,
                    CanceledEventHandler: typeHandler,
                    SubmitToWorkflowMenuItem: `${base}Submit`,
                    CancelMenuItem: `${base}Cancel`,
                    SupportedElements: [{ Name: `Approval${approval}`, ElementName: approval, Type: 'Approval' }],
                },
            },
        },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${base}Submitter`, body: {
                properties: { Label: `Submit ${label}` },
                entryPoints: ['Submit', 'Resubmit', 'Cancel'].map(n => ({ name: `${base}${n}`, objectType: 'MenuItemAction', objectName: `${base}${n}`, grant: 'Invoke' })),
            },
        },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${base}Approver`, body: {
                properties: { Label: `Approve ${label}` },
                entryPoints: ['Approve', 'Reject', 'RequestChange', 'Delegate'].map(n => ({ name: `${base}${n}`, objectType: 'MenuItemAction', objectName: `${base}${n}`, grant: 'Invoke' })),
            },
        },
    ];
}
