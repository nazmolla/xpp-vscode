import { doc, ElementSpec, lower } from './xpp';

export interface SourceField { name: string; type: string; edt?: string | null; enumType?: string | null }

export interface ReportInput {
    base: string;
    label: string;
    sourceTable: string;
    fields: SourceField[];
    /** Date or UtcDateTime field the From/To dates filter on; none: no contract. */
    dateField?: SourceField;
}

const TmpFieldTypes: Record<string, string> = {
    String: 'String', Int: 'Int', Integer: 'Int', Int64: 'Int64', Real: 'Real', Date: 'Date', UtcDateTime: 'DateTime', DateTime: 'DateTime',
    Enum: 'Enum', Guid: 'Guid', Time: 'Time',
};
const ReportDataTypes: Record<string, string> = {
    String: 'System.String', Int: 'System.Int32', Integer: 'System.Int32', Int64: 'System.Int64', Real: 'System.Double', Date: 'System.DateTime',
    UtcDateTime: 'System.DateTime', DateTime: 'System.DateTime', Enum: 'System.String', Guid: 'System.Guid', Time: 'System.Int32',
};
const SystemParameters = ['AX_PartitionKey', 'AX_CompanyName', 'AX_UserContext', 'AX_RenderingCulture', 'AX_ReportContext', 'AX_RdpPreProcessedId'];

export const reportFieldsSupported = (f: SourceField) => !!TmpFieldTypes[f.type];

/**
 * The elements of a report over one table with the report data provider (RDP) pattern: a TempDB table, an optional
 * date-range contract, the data provider, the controller, the report with an auto design, an output menu item and a privilege.
 */
export function reportElements(input: ReportInput): ElementSpec[] {
    const { base, label, sourceTable, fields, dateField } = input;
    const tmp = `${base}Tmp`, contract = `${base}Contract`, dp = `${base}DP`, controller = `${base}Controller`;
    const names = fields.map(f => f.name);
    const range = dateField
        ? dateField.type === 'Date'
            ? `\n        where source.${dateField.name} >= contract.parmFromDate() && source.${dateField.name} <= contract.parmToDate()`
            : `\n        where source.${dateField.name} >= DateTimeUtil::newDateTime(contract.parmFromDate(), 0, DateTimeUtil::getCompanyTimeZone())`
            + `\n           && source.${dateField.name} < DateTimeUtil::newDateTime(contract.parmToDate() + 1, 0, DateTimeUtil::getCompanyTimeZone())`
        : '';
    const elements: ElementSpec[] = [{
        objectType: 'AxTable', objectName: tmp, body: {
            properties: { Label: label, TableType: 'TempDB', TableGroup: 'Miscellaneous' },
            fields: fields.map(f => ({ name: f.name, fieldType: TmpFieldTypes[f.type], ...(f.edt ? { extendedDataType: f.edt } : {}), ...(f.type === 'Enum' && f.enumType ? { enumType: f.enumType } : {}) })),
        },
    }];
    if (dateField) {
        elements.push({
            objectType: 'AxClass', objectName: contract, body: {
                declaration: `${doc(`Parameters of the ${base} report.`)}[DataContractAttribute]\nclass ${contract}\n{\n    FromDate fromDate;\n    ToDate toDate;\n}`,
                methods: [
                    `${doc('The first date of the report.')}[DataMemberAttribute('FromDate')]\npublic FromDate parmFromDate(FromDate _fromDate = fromDate)\n{\n    fromDate = _fromDate;\n    return fromDate;\n}`,
                    `${doc('The last date of the report.')}[DataMemberAttribute('ToDate')]\npublic ToDate parmToDate(ToDate _toDate = toDate)\n{\n    toDate = _toDate;\n    return toDate;\n}`,
                ],
            },
        });
    }
    elements.push(
        {
            objectType: 'AxClass', objectName: dp, body: {
                declaration: `${doc(`Data of the ${base} report: ${sourceTable} rows in ${tmp}.`)}${dateField ? `[SRSReportParameterAttribute(classStr(${contract}))]\n` : ''}class ${dp} extends SRSReportDataProviderBase\n{\n    ${tmp} ${lower(tmp)};\n}`,
                methods: [
                    `${doc(`The rows of the report.`, '/// <returns>The report data.</returns>\n')}[SRSReportDataSetAttribute(tableStr(${tmp}))]\npublic ${tmp} get${tmp}()\n{\n    select ${lower(tmp)};\n    return ${lower(tmp)};\n}`,
                    `${doc('Fills the report data in one set-based statement.')}public void processReport()\n{\n`
                    + (dateField ? `    ${contract} contract = this.parmDataContract() as ${contract};\n` : '')
                    + `    ${sourceTable} source;\n\n    insert_recordset ${lower(tmp)} (${names.join(', ')})\n        select ${names.join(', ')} from source${range};\n}`,
                ],
            },
        },
        {
            objectType: 'AxClass', objectName: controller, body: {
                declaration: `${doc(`Runs the ${base} report.`)}class ${controller} extends SrsReportRunController\n{\n}`,
                methods: [`${doc('Entry point of the menu item.', '/// <param name="_args">Menu item arguments.</param>\n')}public static void main(Args _args)\n{\n    ${controller} controller = new ${controller}();\n    controller.parmReportName(ssrsReportStr(${base}, Report));\n    controller.parmArgs(_args);\n    controller.startOperation();\n}`],
            },
        },
        {
            objectType: 'AxReport', objectName: base, body: {
                typedMetadata: {
                    DataSets: [{
                        Name: tmp,
                        Query: `SELECT * FROM ${dp}.${tmp}`,
                        DataSourceType: 'ReportDataProvider',
                        DefaultLayout: 'Table',
                        DynamicFilters: true,
                        Fields: fields.map(f => ({
                            Name: f.name,
                            DataType: ReportDataTypes[f.type],
                            Alias: `${tmp}.1.${f.name}${f.type === 'Enum' && f.enumType ? `:LABEL(${f.enumType})` : ''}`,
                            FieldType: 'Data',
                        })),
                        Parameters: [...SystemParameters, ...(dateField ? ['FromDate', 'ToDate'] : [])].map(p => ({
                            Name: p, Alias: p, DataType: p.endsWith('Date') ? 'System.DateTime' : 'System.String', Parameter: p,
                        })),
                    }],
                    DefaultParameterGroup: {
                        Name: 'Parameters',
                        ReportParameterBases: [
                            ...SystemParameters.map(p => ({
                                $type: 'AxReportParameter', Name: p, DataType: 'System.String', AllowBlank: p !== 'AX_CompanyName', Nullable: p !== 'AX_CompanyName', UserVisibility: 'Hidden',
                            })),
                            ...(dateField ? ['FromDate', 'ToDate'].map(p => ({ $type: 'AxReportParameter', Name: p, DataType: 'System.DateTime', UserVisibility: 'Visible' })) : []),
                        ],
                    },
                    Designs: [{
                        $type: 'AxReportAutoDesign',
                        Name: 'Report',
                        Title: label,
                        LayoutTemplate: 'ReportLayoutStyleTemplate',
                        PageSize: { Width: '8.5in', Height: '11in' },
                        InteractiveSize: { Width: '8.5in', Height: '11in' },
                        Margin: { Left: '0.5in', Right: '0.5in', Top: '0.5in', Bottom: '0.5in' },
                        DataRegions: [{
                            $type: 'AxReportTable',
                            Name: tmp,
                            DataSet: tmp,
                            StyleTemplate: 'TableStyleTemplate',
                            Data: { Name: 'Data', DetailData: fields.map(f => ({ $type: 'AxReportTableDetailData', Name: f.name, Expression: `=Fields!${f.name}.Value` })) },
                        }],
                    }],
                },
            },
        },
        { objectType: 'AxMenuItemOutput', objectName: base, body: { properties: { Label: label, ObjectType: 'Class', Object: controller } } },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${base}View`, body: {
                properties: { Label: label },
                entryPoints: [{ name: base, objectType: 'MenuItemOutput', objectName: base, grant: 'Read' }],
            },
        },
    );
    return elements;
}

