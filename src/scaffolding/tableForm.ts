import { doc, ElementSpec, lower } from './xpp';

export interface TableFormInput {
    /** Table, form and menu item name, e.g. ABCVehicleGroup; the key field and its EDT get "Id" appended. */
    base: string;
    /** Label of the table, form and menu item. */
    label: string;
    /** Label of the key field. */
    keyLabel: string;
}

/**
 * A setup table with its list form, the standard "group" pattern (CustGroup): key EDT, table with an alternate key, field
 * groups and find/exist, a SimpleList form with a quick filter and a grid, a display menu item, and View/Maintain privileges.
 */
export function tableFormElements({ base, label, keyLabel }: TableFormInput): ElementSpec[] {
    const id = `${base}Id`, index = `${id}Idx`, buffer = lower(base);
    const gridField = (field: string) => ({ $type: 'AxFormStringControl', Name: `Grid_${field}`, DataSource: base, DataField: field });
    return [
        { objectType: 'AxEdt', objectName: id, body: { properties: { Extends: 'SysGroup', Label: keyLabel } } },
        {
            objectType: 'AxTable', objectName: base, body: {
                fields: [
                    { name: id, fieldType: 'String', extendedDataType: id, properties: { Mandatory: 'Yes', AllowEdit: 'No' } },
                    { name: 'Description', fieldType: 'String', extendedDataType: 'Description' },
                ],
                indexes: [{ name: index, allowDuplicates: false, alternateKey: true, fields: [id] }],
                fieldGroups: [
                    { name: 'AutoLookup', fields: [id, 'Description'] },
                    { name: 'AutoIdentification', fields: [id] },
                    { name: 'Overview', fields: [id, 'Description'] },
                ],
                properties: {
                    Label: label, TableGroup: 'Group', CacheLookup: 'Found', PrimaryIndex: index, ClusterIndex: index, ReplacementKey: index,
                    TitleField1: id, TitleField2: 'Description',
                },
                methods: [
                    `${doc(`Finds a ${base} record by its key.`, `/// <param name="_${lower(id)}">The key.</param>\n/// <param name="_forUpdate">Select for update.</param>\n/// <returns>The record; empty when it does not exist.</returns>\n`)}`
                    + `public static ${base} find(${id} _${lower(id)}, boolean _forUpdate = false)\n{\n    ${base} ${buffer};\n\n    if (_${lower(id)})\n    {\n        ${buffer}.selectForUpdate(_forUpdate);\n`
                    + `        select firstonly ${buffer} where ${buffer}.${id} == _${lower(id)};\n    }\n    return ${buffer};\n}`,
                    `${doc(`Whether a ${base} record with the key exists.`, `/// <param name="_${lower(id)}">The key.</param>\n/// <returns>true when it exists.</returns>\n`)}`
                    + `public static boolean exist(${id} _${lower(id)})\n{\n    return _${lower(id)} && (select firstonly RecId from ${base} where ${base}.${id} == _${lower(id)}).RecId != 0;\n}`,
                ],
            },
        },
        {
            objectType: 'AxForm', objectName: base, body: {
                typedMetadata: {
                    DataSources: [{ Name: base, Table: base, InsertIfEmpty: 'No', Fields: [{ Name: id, DataField: id }, { Name: 'Description', DataField: 'Description' }] }],
                    Design: {
                        Caption: label, DataSource: base, TitleDataSource: base, Pattern: 'SimpleList', PatternVersion: '1.1', Style: 'SimpleList',
                        Controls: [
                            { $type: 'AxFormActionPaneControl', Name: 'ActionPane' },
                            {
                                $type: 'AxFormGroupControl', Name: 'CustomFilterGroup', Pattern: 'CustomAndQuickFilters', PatternVersion: '1.1',
                                Style: 'CustomFilter', ArrangeMethod: 'HorizontalLeft', FrameType: 'None', WidthMode: 'SizeToAvailable', ViewEditMode: 'Edit',
                                Controls: [{
                                    $type: 'AxFormControl', Name: 'QuickFilter',
                                    FormControlExtension: {
                                        Name: 'QuickFilterControl',
                                        ExtensionProperties: [
                                            { Name: 'targetControlName', Type: 'String', Value: 'Grid' },
                                            { Name: 'placeholderText', Type: 'String', Value: '' },
                                            { Name: 'defaultColumnName', Type: 'String', Value: `Grid_${id}` },
                                        ],
                                    },
                                }],
                            },
                            { $type: 'AxFormGridControl', Name: 'Grid', DataSource: base, Style: 'Tabular', WidthMode: 'SizeToAvailable', HeightMode: 'SizeToAvailable', Controls: [gridField(id), gridField('Description')] },
                        ],
                    },
                },
            },
        },
        { objectType: 'AxMenuItemDisplay', objectName: base, body: { properties: { Label: label, ObjectType: 'Form', Object: base } } },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${base}View`, body: {
                properties: { Label: label },
                entryPoints: [{ name: base, objectType: 'MenuItemDisplay', objectName: base, grant: 'Read' }],
            },
        },
        {
            objectType: 'AxSecurityPrivilege', objectName: `${base}Maintain`, body: {
                properties: { Label: label },
                entryPoints: [{ name: base, objectType: 'MenuItemDisplay', objectName: base, grant: 'Read,Update,Create,Delete' }],
            },
        },
    ];
}
