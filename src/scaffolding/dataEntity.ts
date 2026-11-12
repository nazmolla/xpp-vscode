import { SourceField } from './report';
import { ElementSpec } from './xpp';

export interface DataEntityInput {
    /** Entity name, e.g. ABCVehicleGroupEntity; the staging table gets Staging appended. */
    entity: string;
    /** The table the entity reads and writes. */
    table: string;
    label: string;
    /** OData names: singular entity and plural collection. */
    publicName: string;
    collectionName: string;
    /** Fields of the table the entity exposes. */
    fields: SourceField[];
    /** The table's natural key fields (the entity key). */
    keyFields: string[];
    /** The table saves data per company (the entity then has a company context). */
    perCompany: boolean;
}

const StagingFieldTypes: Record<string, string> = {
    String: 'String', Int: 'Int', Integer: 'Int', Real: 'Real', Date: 'Date', UtcDateTime: 'UtcDateTime', DateTime: 'UtcDateTime',
    Enum: 'Enum', Guid: 'Guid', Time: 'Time',
};

/** Fields an entity can stage: RecId references (Int64) and containers are not staged. */
export const entityFieldSupported = (f: SourceField) => !!StagingFieldTypes[f.type];

/**
 * A data entity over one table, enabled for OData and data management, with its staging table, as Visual Studio's data
 * entity wizard creates it: entity fields named like the table fields, the natural key as entity key, and a staging table
 * with the data management fields (DefinitionGroup, ExecutionId, IsSelected, TransferStatus) and the same fields.
 */
export function dataEntityElements(input: DataEntityInput): ElementSpec[] {
    const { entity, table, label, publicName, collectionName, fields, keyFields, perCompany } = input;
    const staging = `${entity}Staging`;
    const stagingField = (f: SourceField) => ({
        name: f.name, fieldType: StagingFieldTypes[f.type],
        ...(f.edt ? { extendedDataType: f.edt } : {}), ...(f.type === 'Enum' && f.enumType && !f.edt ? { enumType: f.enumType } : {}),
    });
    return [
        {
            objectType: 'AxTable', objectName: staging, body: {
                fields: [
                    { name: 'DefinitionGroup', fieldType: 'String', extendedDataType: 'DMFDefinitionGroupName' },
                    { name: 'ExecutionId', fieldType: 'String', extendedDataType: 'DMFExecutionId' },
                    { name: 'IsSelected', fieldType: 'Enum', extendedDataType: 'DMFIsSelected' },
                    { name: 'TransferStatus', fieldType: 'Enum', enumType: 'DMFTransferStatus' },
                    ...fields.map(stagingField),
                ],
                indexes: [{ name: 'StagingIdx', allowDuplicates: false, alternateKey: true, fields: ['DefinitionGroup', 'ExecutionId', ...keyFields] }],
                properties: {
                    Label: label, TableGroup: 'Staging', TableType: 'Regular', SaveDataPerCompany: 'No',
                    PrimaryIndex: 'StagingIdx', ClusterIndex: 'StagingIdx', ReplacementKey: 'StagingIdx',
                    TitleField1: keyFields[0], DeveloperDocumentation: '@DMF:StagingDeveloperDocumentation',
                },
            },
        },
        {
            objectType: 'AxDataEntityView', objectName: entity, body: {
                typedMetadata: {
                    Label: label, PublicEntityName: publicName, PublicCollectionName: collectionName, IsPublic: 'Yes',
                    DataManagementEnabled: 'Yes', DataManagementStagingTable: staging, EntityCategory: 'Reference',
                    PrimaryKey: 'EntityKey', ...(perCompany ? { PrimaryCompanyContext: 'DataAreaId' } : {}),
                    ViewMetadata: { Name: entity, DataSources: [{ $type: 'AxQuerySimpleRootDataSource', Name: table, Table: table, DynamicFields: 'Yes' }] },
                    Fields: fields.map(f => ({ $type: 'AxDataEntityViewMappedField', Name: f.name, DataSource: table, DataField: f.name })),
                    Keys: [{ Name: 'EntityKey', Fields: keyFields.map(k => ({ DataField: k })) }],
                },
            },
        },
        { objectType: 'AxSecurityPrivilege', objectName: `${entity}View`, body: { properties: { Label: label }, dataEntityPermissions: [{ name: entity, grant: 'Read' }] } },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${entity}Maintain`,
            body: { properties: { Label: label }, dataEntityPermissions: [{ name: entity, grant: 'Read,Update,Create,Delete' }] },
        },
    ];
}
