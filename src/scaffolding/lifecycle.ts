import { doc, ElementSpec, lower, xppString } from './xpp';

export interface EventField { name: string; type: string; edt?: string | null; enumType?: string | null }

export interface BusinessEventInput {
    base: string;
    /** Label reference (@File:Id) or text. */
    label: string;
    description: string;
    table: string;
    fields: EventField[];
    /** ModuleAxapta value (the business event catalog's category). */
    module: string;
    /** insert or update: where the event is sent. */
    trigger: 'insert' | 'update';
}

const XppTypes: Record<string, string> = {
    String: 'str', Int: 'int', Integer: 'int', Int64: 'int64', Real: 'real', Date: 'date', UtcDateTime: 'utcdatetime', DateTime: 'utcdatetime',
    Guid: 'guid', Time: 'TimeOfDay',
};

/** The X++ type of a table field: its EDT, its enum, or the primitive type. */
export function fieldType(f: EventField): string {
    return f.edt || (f.type === 'Enum' && f.enumType ? f.enumType : XppTypes[f.type] ?? 'str');
}

const labelId = (label: string) => label.startsWith('@') ? label.slice(1) : label;

/**
 * A business event for a table, the standard pattern (precedent: HireActionBusinessEvent): the contract with one data
 * member per field, the event class with the catalog attribute, and a Chain of Command extension of the table's insert
 * or update that sends the event when it is enabled.
 */
export function businessEventElements(input: BusinessEventInput): ElementSpec[] {
    const { base, label, description, table, fields, module, trigger } = input;
    const contract = `${base}BusinessEventContract`, event = `${base}BusinessEvent`, extension = `${table}_${base}_Extension`;
    const record = `${lower(table)}Record`;
    const vars = fields.map(f => ({ ...f, variable: lower(f.name), xppType: fieldType(f) }));
    return [
        {
            objectType: 'AxClass', objectName: contract, body: {
                declaration: `${doc(`Data of the ${event} business event.`)}[DataContract]\npublic final class ${contract} extends BusinessEventsContract\n{\n${vars.map(v => `    private ${v.xppType} ${v.variable};`).join('\n')}\n}`,
                methods: [
                    `${doc(`A contract filled from a ${table} record.`, `/// <param name="_${record}">The record the event is about.</param>\n/// <returns>The contract.</returns>\n`)}public static ${contract} newFrom${table}(${table} _${record})\n{\n    ${contract} contract = new ${contract}();\n    contract.initialize(_${record});\n    return contract;\n}`,
                    `${doc('Use newFrom' + table + '.')}private void new()\n{\n}`,
                    `${doc(`Copies the fields of the ${table} record.`, `/// <param name="_${record}">The record the event is about.</param>\n`)}private void initialize(${table} _${record})\n{\n${vars.map(v => `    ${v.variable} = _${record}.${v.name};`).join('\n')}\n}`,
                    ...vars.map(v => `${doc(`${v.name} of the ${table} record.`, `/// <param name="_${v.variable}">The value to set.</param>\n/// <returns>The value.</returns>\n`)}[DataMember(${xppString(v.name)}), BusinessEventsDataMember(${xppString(v.name)})]\npublic ${v.xppType} parm${v.name}(${v.xppType} _${v.variable} = ${v.variable})\n{\n    ${v.variable} = _${v.variable};\n    return ${v.variable};\n}`),
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: event, body: {
                declaration: `${doc(`Business event sent when a ${table} record is ${trigger === 'insert' ? 'created' : 'changed'}.`)}[BusinessEvents(classStr(${contract}), ${xppString(labelId(label))}, ${xppString(labelId(description))}, ModuleAxapta::${module})]\npublic final class ${event} extends BusinessEventsBase\n{\n    private ${table} ${record};\n}`,
                methods: [
                    `${doc(`The event for a ${table} record.`, `/// <param name="_${record}">The record the event is about.</param>\n/// <returns>The event.</returns>\n`)}public static ${event} newFrom${table}(${table} _${record})\n{\n    return new ${event}(_${record});\n}`,
                    `${doc('Use newFrom' + table + '.', `/// <param name="_${record}">The record the event is about.</param>\n`)}private void new(${table} _${record})\n{\n    ${record} = _${record};\n}`,
                    `${doc('The data the event carries.', '/// <returns>The contract.</returns>\n')}[Hookable(false)]\npublic BusinessEventsContract buildContract()\n{\n    return ${contract}::newFrom${table}(${record});\n}`,
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: extension, body: {
                declaration: `${doc(`Sends ${event} after a ${table} record is ${trigger === 'insert' ? 'inserted' : 'updated'}.`)}[ExtensionOf(tableStr(${table}))]\nfinal class ${extension}\n{\n}`,
                methods: [
                    `${doc(`Sends ${event} when it is enabled in the business event catalog.`)}public void ${trigger}()\n{\n    next ${trigger}();\n    if (BusinessEventsConfigurationReader::isBusinessEventEnabled(classStr(${event})))\n    {\n        ${event}::newFrom${table}(this).send();\n    }\n}`,
                ],
            },
        },
    ];
}

export interface NumberSequenceInput {
    /** The new EDT, e.g. ABCRequestId. */
    edt: string;
    label: string;
    /** NumberSeqModule class of the module whose parameters hold the reference, e.g. NumberSeqModuleCustomer. */
    moduleClass: string;
    /** The module's parameters table, e.g. CustParameters. */
    parametersTable: string;
    /** Your table and field that get the number; the field is created when it doesn't exist. */
    table?: string;
    field?: string;
    prefix: string;
}

/**
 * A number sequence reference for a new ID (precedent: NumberSeqModuleCustomer.loadModule): the EDT, the module's
 * loadModule extended with the data type, and numRef<EDT>() on the module's parameters table.
 */
export function numberSequenceElements(input: NumberSequenceInput): ElementSpec[] {
    const { edt, label, moduleClass, parametersTable, prefix } = input;
    return [
        { objectType: 'AxEdt', objectName: edt, body: { properties: { EdtType: 'String', Extends: 'Num', Label: label, HelpText: label } } },
        {
            objectType: 'AxClass', objectName: `${moduleClass}_${prefix}_Extension`, body: {
                declaration: `${doc(`Adds the ${edt} number sequence reference to ${moduleClass}.`)}[ExtensionOf(classStr(${moduleClass}))]\nfinal class ${moduleClass}_${prefix}_Extension\n{\n}`,
                methods: [
                    `${doc(`Adds ${edt} to the module's references (Number sequences > Generate).`)}protected void loadModule()\n{\n    next loadModule();\n\n    NumberSeqDatatype datatype = NumberSeqDatatype::construct();\n    datatype.parmDatatypeId(extendedTypeNum(${edt}));\n    datatype.parmReferenceHelp(literalStr(${xppString(label)}));\n    datatype.parmWizardIsContinuous(false);\n    datatype.parmWizardIsManual(NoYes::No);\n    datatype.parmWizardIsChangeDownAllowed(NoYes::No);\n    datatype.parmWizardIsChangeUpAllowed(NoYes::No);\n    datatype.parmWizardHighest(999999);\n    datatype.parmSortField(1000);\n    datatype.addParameterType(NumberSeqParameterType::DataArea, true, false);\n    this.create(datatype);\n}`,
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: `${parametersTable}_${prefix}_Extension`, body: {
                declaration: `${doc(`The ${edt} number sequence reference on ${parametersTable}.`)}[ExtensionOf(tableStr(${parametersTable}))]\nfinal class ${parametersTable}_${prefix}_Extension\n{\n}`,
                methods: [
                    `${doc(`The number sequence reference of ${edt}.`, '/// <returns>The reference.</returns>\n')}public static NumberSequenceReference numRef${edt}()\n{\n    return NumberSeqReference::findReference(extendedTypeNum(${edt}));\n}`,
                ],
            },
        },
    ];
}

/** initValue of your table, getting the next number (merged into an existing initValue by the wizard). */
export function numberSequenceInitValue(input: NumberSequenceInput): string {
    return `${doc(`Defaults the record; ${input.field} gets the next number of ${input.edt}.`)}public void initValue()\n{\n    super();\n    this.${input.field} = NumberSeq::newGetNum(${input.parametersTable}::numRef${input.edt}()).num();\n}`;
}

export interface FeatureInput {
    name: string;
    label: string;
    summary: string;
    /** FeatureModuleV0 value. */
    module: string;
    enabledByDefault: boolean;
}

/** A feature in Feature management (precedent: QMSApprovedCustomerListFeature): IFeatureMetadata with an isEnabled() check. */
export function featureElements(input: FeatureInput): ElementSpec[] {
    const { name, label, summary, module, enabledByDefault } = input;
    const hookable = '[Hookable(false)]\n';
    return [{
        objectType: 'AxClass', objectName: name, body: {
            declaration: `using System.ComponentModel.Composition;\nusing Microsoft.Dynamics.ApplicationPlatform.FeatureExposure;\n\n${doc(`Feature management: ${label.startsWith('@') ? name : label}. Check it with ${name}::isEnabled().`)}[ExportAttribute(identifierStr(Microsoft.Dynamics.ApplicationPlatform.FeatureExposure.IFeatureMetadata))]\npublic final class ${name} implements IFeatureMetadata\n{\n    private static ${name} instance = new ${name}();\n}`,
            methods: [
                `${doc('Use instance().')}private void new()\n{\n}`,
                `${doc('The feature (the instance Feature management knows).', '/// <returns>The feature.</returns>\n')}${hookable}public static ${name} instance()\n{\n    return ${name}::instance;\n}`,
                `${doc('Whether the feature is on in Feature management.', '/// <returns>true when it is enabled.</returns>\n')}${hookable}public static boolean isEnabled()\n{\n    return Microsoft.Dynamics.ApplicationPlatform.FeatureExposure.FeatureStateProvider::isFeatureEnabled(${name}::instance());\n}`,
                `${doc('The feature\'s name in Feature management.', '/// <returns>The label.</returns>\n')}${hookable}public FeatureLabelId label()\n{\n    return literalStr(${xppString(label)});\n}`,
                `${doc('The module it is listed under.', '/// <returns>The module.</returns>\n')}${hookable}public int module()\n{\n    return FeatureModuleV0::${module};\n}`,
                `${doc('What the feature does.', '/// <returns>The label.</returns>\n')}${hookable}public FeatureLabelId summary()\n{\n    return literalStr(${xppString(summary)});\n}`,
                `${doc('Documentation link.', '/// <returns>The URL.</returns>\n')}${hookable}public WebSiteURL learnMoreUrl()\n{\n    return '';\n}`,
                `${doc('Whether new environments have it on.', '/// <returns>The default.</returns>\n')}${hookable}public boolean isEnabledByDefault()\n{\n    return ${enabledByDefault};\n}`,
                `${doc('Whether users can turn it off again.', '/// <returns>true.</returns>\n')}${hookable}public boolean canDisable()\n{\n    return true;\n}`,
            ],
        },
    }];
}
