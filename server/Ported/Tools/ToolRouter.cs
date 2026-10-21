using System;
using System.Text;

namespace XppVsCode.Server.Tools
{
    /// <summary>
    /// Routes MCP tool calls to the MetaModel bridge running inside the VSIX.
    /// All metadata operations go through the strongly-typed MetaModel API.
    /// </summary>
    internal sealed class ToolRouter
    {
        private readonly BridgeClient _bridge;
        private readonly DocSearchHandler _docSearch;
        private readonly ModelObjectLister _modelLister = new ModelObjectLister();
        private readonly XRefQuery _xref = new XRefQuery();

        /// <summary>Structured where-used for the extension's references list.</summary>
        public string WhereUsedJson(string body)
        {
            int max = int.TryParse(JsonHelpers.ExtractJsonString(body, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 1000)) : 500;
            return _xref.QueryJson(JsonHelpers.ExtractJsonString(body, "objectType"), JsonHelpers.ExtractJsonString(body, "objectName"),
                JsonHelpers.ExtractJsonString(body, "memberName"), max);
        }

        // Above this total method source size, reads return signatures only unless methodNames is given.
        private const int MaxInlineMethodSourceChars = 40000;


        public ToolRouter(BridgeClient bridge)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _docSearch = new DocSearchHandler();
        }

        public string BuildToolsListResponse(string idToken)
        {
            string result = @"{
  ""tools"": [
    {
      ""name"": ""xpp_create_object"",
      ""description"": ""Creates a new D365FO X++ metadata object using the MetaModel API. CRITICAL: This is the ONLY correct way to create X++ objects. NEVER create metadata files directly via terminal, file writes, or Set-Content — that bypasses the MetaModel API and produces corrupted/unregistered objects. ALL parameters MUST be JSON — any XML or CDATA content will be REJECTED with an error. Use 'properties' for scalar metadata (Label, IsExtensible, TableGroup, etc.), 'enumValues' for AxEnum values, 'fields'/'indexes'/'fieldGroups'/'relations' for AxTable structure, 'entryPoints' for security types. Use xpp_read_object on existing objects to see the exact JSON format. The object is automatically added to the active VS project. EXTENSIONS: to change a standard/foreign table, enum, or EDT, create AxTableExtension / AxEnumExtension / AxEdtExtension named '<BaseObject>.<Suffix>' (e.g. 'CustTable.MyModelExtension') in your own custom model; fields/indexes/fieldGroups/relations/enumValues are added to the base object, and 'properties' become property modifications of the base object. FORM EXTENSIONS (AxFormExtension): ALWAYS call xpp_get_form_layout on the base form first, then pass 'controls' with explicit placement (parent + position, and previousSibling for position 'after') and optional 'dataSources' / 'controlModifications'. Placement is validated against the base form plus all its existing extensions before saving, and verified after saving. DATA ENTITY EXTENSIONS (AxDataEntityViewExtension '<Entity>.<Suffix>'): pass 'entityFields' (name, dataSource, dataField); each dataField must already exist on that data source's table or a table extension. The same fields are added automatically to an extension of the entity's staging table ('<StagingTable>.<Suffix>') with the source field's type/EDT, unless skipStagingTable=true. SECURITY: privileges take 'entryPoints' (objectType MenuItemDisplay/MenuItemOutput/MenuItemAction, objectName, grant e.g. 'Read' or 'Read,Update,Create,Delete' or 'Invoke') and 'dataEntityPermissions'; duties take 'privileges'; roles take 'duties' and 'privileges'. Standard privileges cannot be extended: create your own privilege and add it to a standard duty/role with AxSecurityDutyExtension / AxSecurityRoleExtension ('<Base>.<Suffix>')."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": {
            ""type"": ""string"",
            ""description"": ""The D365FO object type to create."",
            ""enum"": [""AxClass"", ""AxTable"", ""AxView"", ""AxDataEntityView"", ""AxEdt"", ""AxEnum"", ""AxForm"", ""AxTile"", ""AxMenu"", ""AxMenuItemDisplay"", ""AxMenuItemOutput"", ""AxMenuItemAction"", ""AxQuery"", ""AxSecurityPrivilege"", ""AxSecurityDuty"", ""AxSecurityRole"", ""AxService"", ""AxServiceGroup"", ""AxConfigurationKey"", ""AxTableExtension"", ""AxEnumExtension"", ""AxEdtExtension"", ""AxFormExtension"", ""AxDataEntityViewExtension"", ""AxSecurityDutyExtension"", ""AxSecurityRoleExtension""]
          },
          ""objectName"": { ""type"": ""string"", ""description"": ""Name of the object. Must follow the model's naming prefix convention."" },
          ""typedMetadata"": { ""type"": ""object"", ""description"": ""Full strongly-typed metadata graph for the target object type. Preferred for complete metadata writes across all object types."" },
          ""declaration"": { ""type"": ""string"", ""description"": ""Raw X++ class/table declaration code block. Plain code only — NO XML, NO CDATA wrappers."" },
          ""methods"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Array of complete X++ method source strings. Plain code only — NO XML, NO CDATA wrappers. Any XML content will be rejected with an error."" },
          ""properties"": { ""type"": ""object"", ""description"": ""Key-value pairs for scalar metadata properties set via reflection. Keys are property names (e.g. Label, IsExtensible, TableGroup, ObjectType, Object, FormRef). Values are strings — type coercion is automatic (bool, int, enum). Use xpp_read_object on an existing object to discover available property names."" },
          ""enumValues"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""value"": { ""type"": ""integer"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""value""] }, ""description"": ""Enum values for AxEnum. Each entry creates an AxEnumValue with Name, Value (integer), and optional Label."" },
          ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""fieldType"": { ""type"": ""string"", ""enum"": [""String"", ""Int"", ""Real"", ""Date"", ""DateTime"", ""Enum"", ""Int64"", ""Container"", ""Guid"", ""Time""] }, ""extendedDataType"": { ""type"": ""string"" }, ""enumType"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""fieldType""] }, ""description"": ""Table fields for AxTable. fieldType determines the field class (AxTableFieldString, AxTableFieldInt, etc.)."" },
          ""indexes"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""allowDuplicates"": { ""type"": ""boolean"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Table indexes for AxTable. Each index has a name and array of field names."" },
          ""fieldGroups"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Table field groups for AxTable. Each group has a name and array of field names."" },
          ""relations"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""relatedTable"": { ""type"": ""string"" }, ""constraints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""field"": { ""type"": ""string"" }, ""relatedField"": { ""type"": ""string"" } } } } }, ""required"": [""name"", ""relatedTable""] }, ""description"": ""Table relations for AxTable. Each relation maps fields to related table fields."" },
          ""dataSources"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""table"": { ""type"": ""string"" }, ""parentDataSource"": { ""type"": ""string"" }, ""joinMode"": { ""type"": ""string"" }, ""linkType"": { ""type"": ""string"" }, ""dynamicFields"": { ""type"": ""boolean"" }, ""relations"": { ""type"": ""boolean"" }, ""firstOnly"": { ""type"": ""boolean"" }, ""ranges"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""field"": { ""type"": ""string"" }, ""value"": { ""type"": ""string"" } } } } }, ""required"": [""name""] }, ""description"": ""Query data sources for AxQuery. Use this to define table sources and optional ranges. For AxFormExtension: data sources to add to the form (name, table, parentDataSource = join source on the form, linkType e.g. InnerJoin/OuterJoin/ExistJoin/Delayed/Active)."" },
          ""controls"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""Unique control name on the whole form."" }, ""controlType"": { ""type"": ""string"", ""description"": ""String, Integer, Int64, Real, Date, DateTime, Time, Guid, CheckBox, ComboBox, RadioButton, StaticText, ReferenceGroup, Group, Tab, TabPage, Grid, ButtonGroup, Button, CommandButton, MenuFunctionButton, MenuButton, ActionPaneTab. Optional when dataField is set (inferred from the field type)."" }, ""parent"": { ""type"": ""string"", ""description"": ""TOP-LEVEL controls only: name of the existing container (Group, TabPage, Grid, ButtonGroup, ...) taken from xpp_get_form_layout. Use 'Design' for the form's top level."" }, ""position"": { ""type"": ""string"", ""enum"": [""end"", ""begin"", ""after""], ""description"": ""TOP-LEVEL controls only. end (default) = last child of parent; begin = first child; after = directly after previousSibling."" }, ""previousSibling"": { ""type"": ""string"", ""description"": ""Required with position 'after': an existing control that is a DIRECT child of parent."" }, ""dataSource"": { ""type"": ""string"", ""description"": ""Form data source name (inherited from the parent container when omitted)."" }, ""dataField"": { ""type"": ""string"", ""description"": ""Field on the data source's table (table extension fields included)."" }, ""dataGroup"": { ""type"": ""string"", ""description"": ""Table field group (for Group controls bound to a field group)."" }, ""label"": { ""type"": ""string"" }, ""properties"": { ""type"": ""object"", ""description"": ""Extra control properties, e.g. {\""Visible\"":\""No\"", \""MenuItemName\"":\""MyMenuItem\""}."" }, ""controls"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""Child controls of a container, same shape but WITHOUT parent/position/previousSibling; placed in list order."" } }, ""required"": [""name""] }, ""description"": ""AxFormExtension only: controls to add. Survey the form with xpp_get_form_layout first and choose parent/position from it."" },
          ""controlModifications"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""Existing control on the form."" }, ""properties"": { ""type"": ""object"", ""description"": ""Property changes, e.g. {\""Visible\"":\""No\""}."" } }, ""required"": [""name"", ""properties""] }, ""description"": ""AxFormExtension only: property changes on existing controls of the base form."" },
          ""entryPoints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""objectType"": { ""type"": ""string"" }, ""objectName"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Security entry points for AxSecurityPrivilege/Duty/Role."" },
          ""privileges"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityDuty, AxSecurityRole and their extensions: privilege names to add."" },
          ""duties"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityRole / AxSecurityRoleExtension: duty names to add."" },
          ""dataEntityPermissions"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""Data entity name."" }, ""grant"": { ""type"": ""string"", ""description"": ""Read, Update, Create, Correct, Delete or All; comma-separated for several."" }, ""integrationMode"": { ""type"": ""string"", ""enum"": [""All"", ""DataServices"", ""DataManagement""] } }, ""required"": [""name""] }, ""description"": ""AxSecurityPrivilege only: data entity permissions."" },
          ""entityFields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""New entity field name (unique on the entity and its extensions)."" }, ""dataSource"": { ""type"": ""string"", ""description"": ""Entity data source name (read the entity first)."" }, ""dataField"": { ""type"": ""string"", ""description"": ""Field on the data source's table or one of its table extensions."" }, ""label"": { ""type"": ""string"" }, ""mandatory"": { ""type"": ""boolean"" } }, ""required"": [""name"", ""dataSource"", ""dataField""] }, ""description"": ""AxDataEntityViewExtension only: mapped fields to add. The staging table gets the same fields automatically."" },
          ""skipStagingTable"": { ""type"": ""boolean"", ""description"": ""AxDataEntityViewExtension only: do not extend the staging table. Default false."" },
          ""modelName"": { ""type"": ""string"", ""description"": ""Target model name. If omitted, uses the active project's model."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_read_object"",
      ""description"": ""Reads a D365FO X++ object by type and name using the MetaModel API. CRITICAL: This is the ONLY correct way to read X++ objects. NEVER read metadata files directly via Get-Content, the terminal, or file tools — this tool returns everything the XML contains: declaration, every method's signature (with access modifiers such as internal/private/public) and full source, and strongly-typed metadata (typedMetadata graph plus properties, enumValues, fields, indexes, fieldGroups, relations, dataSources), model name and editability. Works for ANY metadata type. For classes with very large source, methods are listed by signature first; call again with methodNames to get their full source."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""Any D365FO metadata type: AxClass, AxTable, AxForm, AxEnum, AxEdt, AxView, AxQuery, AxMap, AxReport, AxDataEntityView, AxTableExtension, AxFormExtension, etc."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""The object name."" },
          ""methodNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Return the full source of only these methods (use after a read that listed signatures only)."" },
          ""filePath"": { ""type"": ""string"", ""description"": ""Absolute path to the metadata file, if you have it. Prefer objectType + objectName."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_update_object"",
      ""description"": ""Updates an existing D365FO X++ object using the MetaModel API. CRITICAL: This is the ONLY correct way to modify X++ objects. NEVER edit metadata files directly. ALL parameters MUST be JSON — any XML or CDATA content will be REJECTED with an error. Can update declaration, add/replace/remove methods, set properties, and add enum values/fields/indexes/relations. Use xpp_read_object first to see current state. ONLY works on custom model objects."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""The object type (AxClass, AxTable, AxForm, AxEdt, AxEnum, AxView, AxQuery, AxDataEntityView, AxMenu, AxSecurityPrivilege, AxSecurityDuty, AxSecurityRole, AxTableExtension, AxEnumExtension, AxEdtExtension, AxFormExtension, AxDataEntityViewExtension, AxSecurityDutyExtension, AxSecurityRoleExtension). For extensions, 'properties' are written as property modifications of the base object."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""The object name to update."" },
          ""typedMetadata"": { ""type"": ""object"", ""description"": ""Full strongly-typed metadata graph for complete metadata updates across all object types."" },
          ""declaration"": { ""type"": ""string"", ""description"": ""New raw X++ declaration code. Replaces existing. Plain code only — NO XML, NO CDATA."" },
          ""methods"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Methods to add or replace (matched by name). Plain X++ code only — NO XML, NO CDATA. Any XML content will be rejected."" },
          ""removeMethodNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Method names to remove."" },
          ""properties"": { ""type"": ""object"", ""description"": ""Key-value pairs for scalar metadata properties to set or update. Same format as xpp_create_object."" },
          ""enumValues"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""value"": { ""type"": ""integer"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""value""] }, ""description"": ""Enum values to add (for AxEnum). Values with matching names are skipped."" },
          ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""fieldType"": { ""type"": ""string"" }, ""extendedDataType"": { ""type"": ""string"" }, ""enumType"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""fieldType""] }, ""description"": ""Table fields to add (for AxTable)."" },
          ""indexes"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""allowDuplicates"": { ""type"": ""boolean"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Indexes to add (for AxTable)."" },
          ""fieldGroups"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Field groups to add (for AxTable)."" },
          ""relations"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""relatedTable"": { ""type"": ""string"" }, ""constraints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""field"": { ""type"": ""string"" }, ""relatedField"": { ""type"": ""string"" } } } } }, ""required"": [""name"", ""relatedTable""] }, ""description"": ""Relations to add (for AxTable)."" },
          ""dataSources"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""table"": { ""type"": ""string"" }, ""parentDataSource"": { ""type"": ""string"" }, ""joinMode"": { ""type"": ""string"" }, ""linkType"": { ""type"": ""string"" }, ""dynamicFields"": { ""type"": ""boolean"" }, ""relations"": { ""type"": ""boolean"" }, ""firstOnly"": { ""type"": ""boolean"" }, ""ranges"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""field"": { ""type"": ""string"" }, ""value"": { ""type"": ""string"" } } } } }, ""required"": [""name""] }, ""description"": ""Query data sources for AxQuery updates. For AxFormExtension: data sources to add to the form (name, table, parentDataSource, linkType)."" },
          ""controls"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""AxFormExtension only: controls to add — same shape as in xpp_create_object (name, controlType, parent, position end|begin|after, previousSibling, dataSource, dataField, dataGroup, label, properties, controls). Call xpp_get_form_layout first."" },
          ""controlModifications"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""properties"": { ""type"": ""object"" } }, ""required"": [""name"", ""properties""] }, ""description"": ""AxFormExtension only: property changes on existing controls of the base form."" },
          ""entryPoints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""objectType"": { ""type"": ""string"" }, ""objectName"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Entry points to add or update by name (for AxSecurityPrivilege). grant: Read, Update, Create, Correct, Delete, Invoke or All; comma-separated for several."" },
          ""privileges"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityDuty, AxSecurityRole and their extensions: privilege names to add."" },
          ""duties"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityRole / AxSecurityRoleExtension: duty names to add."" },
          ""dataEntityPermissions"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" }, ""integrationMode"": { ""type"": ""string"", ""enum"": [""All"", ""DataServices"", ""DataManagement""] } }, ""required"": [""name""] }, ""description"": ""AxSecurityPrivilege only: data entity permissions to add or update."" },
          ""removeSecurityItems"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Security objects: names of entry points, data entity permissions, privileges or duties to remove."" },
          ""entityFields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""dataSource"": { ""type"": ""string"" }, ""dataField"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""mandatory"": { ""type"": ""boolean"" } }, ""required"": [""name"", ""dataSource"", ""dataField""] }, ""description"": ""AxDataEntityViewExtension only: mapped fields to add; the staging table extension gets the same fields."" },
          ""skipStagingTable"": { ""type"": ""boolean"", ""description"": ""AxDataEntityViewExtension only: do not extend the staging table."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_find_object"",
      ""description"": ""Searches for D365FO metadata objects by name across ALL loaded models and packages using the MetaModel API. CRITICAL: Always use this instead of Get-ChildItem/dir/file searches on metadata folders. NEVER search the file system for X++ objects. Returns matching objects with type, model, and editability. Searches scan the loaded metadata and block other tool calls while they run: search for ONE specific object at a time, always pass objectType, and use exactMatch=true when you know the full name. Partial names must be at least 3 characters. Scans stop after a few seconds and return PARTIAL results with a message — if that happens, narrow the search instead of retrying the same query."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectName"": { ""type"": ""string"", ""description"": ""Full or partial name to search for (case-insensitive). Partial names need at least 3 characters; prefer specific names."" },
          ""objectType"": { ""type"": ""string"", ""description"": ""Strongly recommended: limits search to 1 type (5-10x faster). Values: AxClass, AxTable, AxForm, AxEnum, AxView, AxQuery, AxEdt, AxMenuItemDisplay, AxMenuItemOutput, AxMenuItemAction."" },
          ""exactMatch"": { ""type"": ""boolean"", ""description"": ""If true, only exact name matches. Default false. Use true when you know the full name."" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Max results to return. Default 10. Max 50. Keep small (1-10)."" }
        },
        ""required"": [""objectName""]
      }
    },
    {
      ""name"": ""xpp_list_objects"",
      ""description"": ""Lists a HANDFUL of D365FO metadata objects whose names contain a specific substring. NOT for enumerating whole models or types — use xpp_list_model_objects for that. CRITICAL: Always use this instead of Get-ChildItem/dir/file searches on metadata folders. Scans block other tool calls while they run: nameFilter must be at least 3 characters and specific (e.g. 'CustTable', not 'a'); always pass objectType; objectType is REQUIRED when modelName is set. Scans stop after a few seconds and return PARTIAL results with a message — if that happens, narrow the filter instead of retrying."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""nameFilter"": { ""type"": ""string"", ""description"": ""Required, min 3 characters. Specific substring to match object names (case-insensitive). Example: 'CustTable', 'SalesOrder'."" },
          ""objectType"": { ""type"": ""string"", ""description"": ""Strongly recommended; required with modelName. Filter by type: AxClass, AxTable, AxForm, AxEnum, AxView, AxQuery, AxEdt, AxMenuItemDisplay, AxMenuItemOutput, AxMenuItemAction."" },
          ""modelName"": { ""type"": ""string"", ""description"": ""Optional model filter. Requires objectType. Model filtering is checked per object, so it does not make broad filters cheap."" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Max results. Default 10. Max 50. Keep small (1-10)."" }
        },
        ""required"": [""nameFilter""]
      }
    },
    {
      ""name"": ""xpp_list_model_objects"",
      ""description"": ""Lists the objects contained in ONE D365FO model, fast (reads the model's metadata index on disk without loading the metadata). Use this — not xpp_list_objects — whenever you need to know what a model contains. Without objectType it returns object counts per type; with objectType it returns sorted object names for that type, paged with offset/limit. Returns names only: call xpp_read_object for details. Reflects saved objects only."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""modelName"": { ""type"": ""string"", ""description"": ""Required. Model name (e.g. 'Foundation', 'ApplicationPlatform', or a custom model)."" },
          ""objectType"": { ""type"": ""string"", ""description"": ""Optional. AxClass, AxTable, AxForm, AxEdt, AxEnum, AxTableExtension, etc. Omit to get counts per type first."" },
          ""nameFilter"": { ""type"": ""string"", ""description"": ""Optional case-insensitive substring filter on object names."" },
          ""offset"": { ""type"": ""integer"", ""description"": ""Paging offset. Default 0."" },
          ""limit"": { ""type"": ""integer"", ""description"": ""Page size. Default 200. Max 1000."" }
        },
        ""required"": [""modelName""]
      }
    },
    {
      ""name"": ""xpp_find_references"",
      ""description"": ""Where-used: finds every object and member that USES or REFERENCES a given object, or one specific method/field of it (memberName). Queries the cross-reference database from the active D365FO configuration (indexed, milliseconds). Use it to find PUBLIC entry points (controllers, services, forms, other classes) that call an INTERNAL or private member you cannot call yourself, to see how existing code uses an API, and before changing an object. If the cross-reference database is missing or incomplete the result says exactly why — report that to the user; never fall back to searching XML files."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""Required. Type of the TARGET object: AxClass, AxTable, AxForm, AxEnum, AxEdt, AxView, AxQuery, AxDataEntityView, AxMap, AxMenuItemDisplay, AxMenuItemAction, AxMenuItemOutput, AxTableExtension, AxEnumExtension, AxReport, AxSecurityRole, AxSecurityPrivilege, AxSecurityDuty."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""Required. Exact name of the target object."" },
          ""memberName"": { ""type"": ""string"", ""description"": ""Optional. A method or field of the target, e.g. 'runProcess' — finds only who calls/uses that member."" },
          ""referenceKind"": { ""type"": ""string"", ""description"": ""Optional filter: Any (default), MethodCall, TypeReference, InterfaceImplementation, ClassExtended, TestCall, Property, Attribute, TestHelperCall, Tag, MethodOverride."" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Max results. Default 25. Max 100."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_get_model_info"",
      ""description"": ""Returns information about a D365FO model: name, publisher, version, layer, dependencies."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""modelName"": { ""type"": ""string"", ""description"": ""The model name to inspect."" }
        },
        ""required"": [""modelName""]
      }
    },
    {
      ""name"": ""xpp_list_models"",
      ""description"": ""Lists all D365FO models in the current environment with their editability status."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {},
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_read_label"",
      ""description"": ""Reads labels from a D365FO label file using the Label API. Look up specific label IDs or search by text."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""labelFileId"": { ""type"": ""string"", ""description"": ""The label file ID (e.g. 'MyLabels')."" },
          ""language"": { ""type"": ""string"", ""description"": ""Language code (e.g. 'en-US'). Default en-US."" },
          ""labelId"": { ""type"": ""string"", ""description"": ""Specific label ID to look up."" },
          ""searchText"": { ""type"": ""string"", ""description"": ""Search labels containing this text."" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Max results. Default 25. Max 100."" }
        },
        ""required"": [""labelFileId""]
      }
    },
    {
      ""name"": ""xpp_create_label"",
      ""description"": ""Creates a new label in a D365FO label file using the Label API."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""labelFileId"": { ""type"": ""string"", ""description"": ""The label file ID."" },
          ""language"": { ""type"": ""string"", ""description"": ""Language code. Default en-US."" },
          ""labelId"": { ""type"": ""string"", ""description"": ""The label ID to create."" },
          ""text"": { ""type"": ""string"", ""description"": ""The label text."" },
          ""comment"": { ""type"": ""string"", ""description"": ""Optional comment."" }
        },
        ""required"": [""labelFileId"", ""labelId"", ""text""]
      }
    },
    {
      ""name"": ""xpp_add_to_project"",
      ""description"": ""Adds a metadata object to the active D365FO project (.rnrproj) in the workspace."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""The object type (AxClass, AxTable, etc.)."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""The object name."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_list_project_items"",
      ""description"": ""Lists all items in the active D365FO project (.rnrproj) in the workspace."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {},
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_get_environment"",
      ""description"": ""Returns the D365FO development environment info: metadata folders, active project, active model name/ID/layer. Use this to understand the project context. CRITICAL RULE: In D365FO/X++ projects, NEVER use terminal commands to create, read, update, search, or list metadata files. Always use the xpp_* tools — they use the MetaModel API which correctly registers objects, maintains cross-references, and respects model boundaries."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {},
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_search_docs"",
      ""description"": ""Searches Microsoft Learn documentation for D365FO, X++ language, extensibility, and technical references. ALWAYS use this when unsure about X++ syntax, table structures, API signatures, or extensibility rules."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""url"": { ""type"": ""string"", ""description"": ""A specific Microsoft Learn URL to fetch."" },
          ""query"": { ""type"": ""string"", ""description"": ""Search query for Microsoft Learn D365FO docs."" },
          ""maxLength"": { ""type"": ""integer"", ""description"": ""Max character length of returned content. Default 12000."" }
        },
        ""required"": [""query""]
      }
    },
    {
      ""name"": ""xpp_validate_object"",
      ""description"": ""Validates that a D365FO object exists, is added to the active D365FO project, and has the expected metadata. Performs deep property-level comparison on every supplied section. Use after xpp_create_object or xpp_update_object to confirm all metadata was applied correctly. Returns valid=true only when ALL checks pass. On failure, returns a mismatches array with the exact expected vs actual value for every discrepancy. Pass typedMetadata for comprehensive object-type-specific validation (form controls, design nodes, etc.)."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""The object type (AxClass, AxTable, AxEnum, AxEdt, AxView, AxQuery, AxForm, AxSecurityRole, AxSecurityPrivilege, AxSecurityDuty, AxMenuItemDisplay, AxMenuItemOutput, AxMenuItemAction, AxMenu, etc.)."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""The object name to validate."" },
          ""typedMetadata"": { ""type"": ""object"", ""description"": ""Full typed metadata graph for deep recursive comparison against the current object. Use this as the primary comprehensive validator for object-type-specific metadata (AxForm controls and design, AxQuery data sources, AxSecurityRole privileges, etc.). Every property present in this graph is compared against the actual object."" },
          ""properties"": { ""type"": ""object"", ""description"": ""Expected key-value property pairs. Each entry is compared (case-insensitive) against the actual object. Example: {Label: '@MyLabel', IsExtensible: 'true'}."" },
          ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""fieldType"": { ""type"": ""string"" }, ""extendedDataType"": { ""type"": ""string"" }, ""enumType"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Expected table/view fields. Deep-compares name, fieldType, extendedDataType, enumType, and label when provided."" },
          ""enumValues"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""value"": { ""type"": ""integer"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Expected enum values. Deep-compares name, integer value, and label when provided."" },
          ""indexes"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""allowDuplicates"": { ""type"": ""boolean"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name""] }, ""description"": ""Expected indexes. Deep-compares name, allowDuplicates, and field list when provided."" },
          ""relations"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""relatedTable"": { ""type"": ""string"" }, ""constraints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""field"": { ""type"": ""string"" }, ""relatedField"": { ""type"": ""string"" } } } } }, ""required"": [""name""] }, ""description"": ""Expected relations. Deep-compares name, relatedTable, and constraint field mappings when provided."" },
          ""fieldGroups"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name""] }, ""description"": ""Expected field groups. Deep-compares name, label, and field list when provided."" },
          ""entryPoints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""objectType"": { ""type"": ""string"" }, ""objectName"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Expected security entry points. Deep-compares name, objectType, objectName, and grant when provided."" },
          ""dataSources"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""table"": { ""type"": ""string"" }, ""joinMode"": { ""type"": ""string"" }, ""linkType"": { ""type"": ""string"" }, ""dynamicFields"": { ""type"": ""boolean"" }, ""ranges"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } }, ""childDataSources"": { ""type"": ""array"", ""items"": { ""type"": ""object"" } } }, ""required"": [""name""] }, ""description"": ""Expected query/view data sources. Deep-compares table, joinMode, linkType, ranges, and child data sources recursively."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_get_form_layout"",
      ""description"": ""Shows the EFFECTIVE layout of a form: the base form with ALL its existing extensions applied (using D365FO's own extension applier). Lists data sources and the control tree with each control's type, data binding, and which extension added it. REQUIRED before creating or updating an AxFormExtension: pick 'parent', 'position' and 'previousSibling' for new controls only from this output. Output is depth-limited; containers with hidden children show (+N children) — call again with rootControl to expand that branch."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""formName"": { ""type"": ""string"", ""description"": ""The base form name (e.g. 'CustTable'), not the extension name."" },
          ""rootControl"": { ""type"": ""string"", ""description"": ""Optional. Show only the subtree under this control."" },
          ""maxDepth"": { ""type"": ""integer"", ""description"": ""Tree depth to show. Default 4. Max 12."" }
        },
        ""required"": [""formName""]
      }
    },
    {
      ""name"": ""xpp_get_object_type_schema"",
      ""description"": ""Returns a generated JSON schema for a D365FO metadata object type based on reflective type inspection. Use this to discover full strongly-typed typedMetadata shape for create/update/validate payloads."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""The object type to inspect (for example AxClass, AxTable, AxForm, AxEnum, AxQuery, AxView)."" }
        },
        ""required"": [""objectType""]
      }
    }
  ]
}";
            return JsonHelpers.BuildResult(idToken, result);
        }

        public string HandleToolCall(string idToken, string json)
        {
            string toolName = JsonHelpers.ExtractNestedString(json, "params", "name");
            McpLogger.Log("tools/call name=" + (toolName ?? "<null>"));

            switch (toolName)
            {
                // ── Bridge-delegated tools ──
                case "xpp_create_object": return DelegateToBridge(idToken, json, "create_object");
                case "xpp_read_object": return HandleReadObject(idToken, json);
                case "xpp_update_object":
                case "xpp_update_current_object": return DelegateToBridge(idToken, json, "update_object");
                case "xpp_find_object": return HandleFindObject(idToken, json);
                case "xpp_list_objects": return HandleListObjects(idToken, json);
                case "xpp_find_references": return HandleFindReferences(idToken, json);
                case "xpp_get_model_info": return HandleGetModelInfo(idToken, json);
                case "xpp_list_models": return DelegateToBridge(idToken, json, "list_models");
                case "xpp_read_label": return HandleReadLabel(idToken, json);
                case "xpp_create_label": return HandleCreateLabel(idToken, json);
                case "xpp_add_to_project": return HandleAddToProject(idToken, json);
                case "xpp_list_project_items": return DelegateToBridge(idToken, json, "list_project_items");
                case "xpp_get_environment": return DelegateToBridge(idToken, json, "get_environment");
                case "xpp_validate_object": return HandleValidateObject(idToken, json);
                case "xpp_get_object_type_schema": return HandleGetObjectTypeSchema(idToken, json);
                case "xpp_get_form_layout": return HandleGetFormLayout(idToken, json);

                // ── Local-only tools ──
                case "xpp_search_docs": return _docSearch.Handle(idToken, json);
                case "xpp_list_model_objects": return _modelLister.Handle(idToken, json);

                default:
                    return JsonHelpers.BuildError(idToken, -32602, "Unknown tool: " + toolName);
            }
        }

        private string DelegateToBridge(string idToken, string json, string action)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);

            // Log full arguments for create/update to aid debugging
            if (action == "create_object" || action == "update_object")
                McpLogger.Log("bridge args (" + action + "): " + argsJson);

            if (!_bridge.IsAvailable())
            {
                return JsonHelpers.BuildToolResult(idToken,
                    "MetaModel bridge is not available. Check that the active D365FO configuration points to an extracted PackagesLocalDirectory.",
                    isError: true);
            }

            string bridgeResponse = _bridge.Call(action, argsJson);

            // Parse bridge response and format as MCP tool result
            string message = JsonHelpers.ExtractJsonString(bridgeResponse, "message");

            // validate_object reports its outcome as "valid" rather than "success".
            bool isSuccess = ReadBool(bridgeResponse, action == "validate_object" ? "valid" : "success");
            bool isError = !isSuccess;

            // For rich results, pass through relevant data
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(message))
                sb.AppendLine(message);

            string layout = JsonHelpers.ExtractJsonString(bridgeResponse, "layout");
            if (!string.IsNullOrEmpty(layout))
                sb.AppendLine(layout);

            // Append matches/objects/items/labels if present
            AppendArrayField(sb, bridgeResponse, "matches", "objectType", "objectName", "modelName", "isCustom");
            AppendArrayField(sb, bridgeResponse, "objects", "objectType", "objectName", "modelName", "isCustom");
            AppendArrayField(sb, bridgeResponse, "models", "name", "displayName", "isCustom");
            AppendArrayField(sb, bridgeResponse, "labels", "id", "text");
            AppendArrayField(sb, bridgeResponse, "items", "name", "filePath");

            // Append read result fields
            string declaration = JsonHelpers.ExtractJsonString(bridgeResponse, "declaration");
            if (!string.IsNullOrEmpty(declaration))
            {
                sb.AppendLine("\n=== Declaration ===");
                sb.AppendLine(declaration);
            }

            if (action == "read_object" || action == "read_object_by_path")
            {
                AppendMethods(sb, bridgeResponse, JsonHelpers.ExtractJsonStringArray(argsJson, "methodNames"));
                AppendGenericMetadata(sb, bridgeResponse);
            }
            if (action == "find_references")
                AppendReferences(sb, bridgeResponse);

            // Append typed metadata (properties, enumValues, fields, indexes, etc.)
            AppendTypedMetadata(sb, bridgeResponse);

            string objectType = JsonHelpers.ExtractJsonString(bridgeResponse, "objectType");
            string objectName = JsonHelpers.ExtractJsonString(bridgeResponse, "objectName");
            string modelName = JsonHelpers.ExtractJsonString(bridgeResponse, "modelName");
            string isCustom = ReadBool(bridgeResponse, "isCustom") ? "true" : "false";
            bool hasEditability = JsonHelpers.ExtractJsonValueToken(bridgeResponse, "isCustom") != null;

            if (!string.IsNullOrEmpty(objectType))
                sb.Insert(0, "ObjectType: " + objectType + "\nName: " + objectName
                    + "\nModel: " + modelName
                    + (hasEditability ? "\nEditable: " + (isCustom == "true" ? "YES" : "NO") : "") + "\n\n");

            // Environment info
            string customFolder = JsonHelpers.ExtractJsonString(bridgeResponse, "customMetadataFolder");
            if (!string.IsNullOrEmpty(customFolder))
            {
                sb.AppendLine("CustomMetadataFolder: " + customFolder);
                string projectName = JsonHelpers.ExtractJsonString(bridgeResponse, "activeProjectName");
                string activeModel = JsonHelpers.ExtractJsonString(bridgeResponse, "activeModelName");
                string layer = JsonHelpers.ExtractJsonString(bridgeResponse, "activeModelLayer");
                if (!string.IsNullOrEmpty(projectName)) sb.AppendLine("ActiveProject: " + projectName);
                if (!string.IsNullOrEmpty(activeModel)) sb.AppendLine("ActiveModel: " + activeModel);
                if (!string.IsNullOrEmpty(layer)) sb.AppendLine("Layer: " + layer);
            }

            if (action == "get_model_info" && isSuccess)
            {
                foreach (string key in new[] { "name", "displayName", "publisher", "version", "layer", "modelId" })
                {
                    string value = JsonHelpers.ExtractJsonString(bridgeResponse, key)
                        ?? JsonHelpers.ExtractJsonValueToken(bridgeResponse, key);
                    if (!string.IsNullOrEmpty(value))
                        sb.AppendLine(char.ToUpperInvariant(key[0]) + key.Substring(1) + ": " + value);
                }
            }

            string text = sb.ToString().Trim();
            if (string.IsNullOrEmpty(text)) text = isError ? "Operation failed." : "Operation completed.";

            return JsonHelpers.BuildToolResult(idToken, text, isError);
        }

        // Bridge returns booleans as JSON true/false; older paths may send them as strings.
        private static bool ReadBool(string json, string key)
        {
            string token = JsonHelpers.ExtractJsonValueToken(json, key);
            if (string.IsNullOrWhiteSpace(token)) return false;
            return "true".Equals(token.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        }

        private static System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> ReadObjectArray(string json, string key)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();
            try
            {
                var serializer = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                if (serializer.DeserializeObject(json) is System.Collections.Generic.Dictionary<string, object> root
                    && root.TryGetValue(key, out object value) && value is object[] items)
                    foreach (var item in items)
                        if (item is System.Collections.Generic.Dictionary<string, object> d) result.Add(d);
            }
            catch (Exception ex)
            {
                McpLogger.Log("ReadObjectArray(" + key + ") failed: " + ex.Message);
            }
            return result;
        }

        private static string Str(System.Collections.Generic.Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out object v) ? v?.ToString() : null;

        private static void AppendMethods(StringBuilder sb, string bridgeResponse, string[] methodNames)
        {
            var methods = ReadObjectArray(bridgeResponse, "methods");
            if (methods.Count == 0) return;

            sb.AppendLine("\n=== Methods (" + methods.Count + ") ===");
            foreach (var m in methods)
                sb.AppendLine("  " + (MethodSignature(Str(m, "source")) ?? Str(m, "name")));

            var wanted = methodNames == null ? null
                : new System.Collections.Generic.HashSet<string>(methodNames, StringComparer.OrdinalIgnoreCase);
            var selected = wanted == null ? methods : methods.FindAll(m => wanted.Contains(Str(m, "name") ?? ""));
            int totalChars = 0;
            foreach (var m in selected) totalChars += (Str(m, "source") ?? "").Length;

            if (wanted != null && selected.Count < wanted.Count)
            {
                var found = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in selected) found.Add(Str(m, "name") ?? "");
                sb.AppendLine("\nNot found on this object: " + string.Join(", ", System.Linq.Enumerable.Where(methodNames, n => !found.Contains(n))));
            }

            if (wanted == null && totalChars > MaxInlineMethodSourceChars)
            {
                sb.AppendLine("\nMethod source omitted: " + totalChars + " characters in total. "
                    + "Call xpp_read_object again with methodNames=[...] (from the list above) to get the full source of the methods you need.");
                return;
            }

            foreach (var m in selected)
            {
                sb.AppendLine("\n--- " + Str(m, "name") + " ---");
                sb.AppendLine(Str(m, "source"));
            }
        }

        // First declaration line(s) of a method, without doc comments and attributes, so access modifiers are visible.
        private static string MethodSignature(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            var sig = new StringBuilder();
            int depth = 0;
            bool opened = false;
            foreach (string raw in source.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                if (sig.Length == 0 && (line.Length == 0 || line.StartsWith("//") || line.StartsWith("[")
                    || line.StartsWith("/*") || line.StartsWith("*")))
                    continue;
                if (line.StartsWith("{")) break;
                sig.Append(sig.Length > 0 ? " " : "").Append(line);
                foreach (char c in line)
                {
                    if (c == '(') { depth++; opened = true; }
                    else if (c == ')') depth--;
                }
                if (opened && depth <= 0) break;
            }
            return sig.Length > 0 ? sig.ToString() : null;
        }

        // Types without dedicated extractors (maps, reports, workflows...) would otherwise hide their structure.
        private const int MaxGenericMetadataChars = 30000;
        private static readonly System.Collections.Generic.HashSet<string> TypesWithDedicatedOutput =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AxClass", "AxTable", "AxForm", "AxEnum", "AxEdt", "AxView", "AxQuery", "AxDataEntityView",
                "AxTableExtension", "AxEnumExtension", "AxEdtExtension", "AxFormExtension"
            };

        private static void AppendGenericMetadata(StringBuilder sb, string bridgeResponse)
        {
            if (TypesWithDedicatedOutput.Contains(JsonHelpers.ExtractJsonString(bridgeResponse, "objectType") ?? "")) return;

            string typed = JsonHelpers.ExtractNestedObject(bridgeResponse, "typedMetadata");
            if (string.IsNullOrEmpty(typed) || typed.Length < 3) return;

            sb.AppendLine("\n=== Metadata (typedMetadata) ===");
            if (typed.Length > MaxGenericMetadataChars)
                sb.AppendLine(typed.Substring(0, MaxGenericMetadataChars) + "\n[truncated at " + MaxGenericMetadataChars
                    + " characters; the full graph is available to xpp_update_object via typedMetadata]");
            else
                sb.AppendLine(typed);
        }

        private static void AppendReferences(StringBuilder sb, string bridgeResponse)
        {
            var refs = ReadObjectArray(bridgeResponse, "references");
            if (refs.Count == 0) return;
            sb.AppendLine("\n=== References (" + refs.Count + ", via metadata provider) ===");
            foreach (var r in refs)
                sb.AppendLine("  " + (Str(r, "sourceObjectType") ?? "?") + " " + (Str(r, "sourceObjectName") ?? Str(r, "sourcePath"))
                    + (Str(r, "sourceMember") != null ? "." + Str(r, "sourceMember") + "()" : "")
                    + " line " + Str(r, "lineNumber") + ": " + Str(r, "kind")
                    + (Str(r, "sourceModule") != null ? "  [" + Str(r, "sourceModule") + "]" : ""));
        }

        private string HandleReadObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string filePath = JsonHelpers.ExtractArgString(argsJson, "filePath", "path");
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            // If filePath provided, use read_object_by_path
            if (!string.IsNullOrEmpty(filePath))
                return DelegateToBridge(idToken, json, "read_object_by_path");

            // Otherwise use typed API
            if (!string.IsNullOrEmpty(objectType) && !string.IsNullOrEmpty(objectName))
                return DelegateToBridge(idToken, json, "read_object");

            return JsonHelpers.BuildToolResult(idToken,
                "Either (objectType + objectName) or filePath is required.", isError: true);
        }

        private string HandleFindObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            if (string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectName' is required.", isError: true);

            return DelegateToBridge(idToken, json, "find_object");
        }

        private string HandleListObjects(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string nameFilter = JsonHelpers.ExtractArgString(argsJson, "nameFilter");
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string modelName = JsonHelpers.ExtractArgString(argsJson, "modelName");

            if (string.IsNullOrWhiteSpace(nameFilter) || nameFilter.Trim().Length < 3)
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'nameFilter' must be at least 3 characters and specific (e.g., 'CustTable', 'SalesOrder'). "
                    + "To see what a model contains, use xpp_list_model_objects instead.", isError: true);

            if (!string.IsNullOrWhiteSpace(modelName) && string.IsNullOrWhiteSpace(objectType))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectType' is required when 'modelName' is set (e.g., AxClass, AxTable).", isError: true);

            return DelegateToBridge(idToken, json, "list_objects");
        }

        private string HandleFindReferences(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName");

            if (string.IsNullOrEmpty(objectType))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectType' is required (e.g. AxTable, AxClass).", isError: true);
            if (string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectName' is required.", isError: true);

            string direct = _xref.TryHandle(idToken, json, out string unusableReason);
            if (direct != null) return direct;

            McpLogger.Log("XRef database unusable: " + unusableReason);
            string viaVs = DelegateToBridge(idToken, json, "find_references");
            if (!viaVs.Contains("\"isError\": true")) return viaVs;

            return JsonHelpers.BuildToolResult(idToken,
                "Where-used is unavailable.\n- Cross-reference database: " + unusableReason
                + "\n- No other cross-reference source is available."
                + "\nTell the user exactly this. Do NOT search metadata XML files or use the terminal as a substitute.",
                isError: true);
        }

        private string HandleGetModelInfo(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string modelName = JsonHelpers.ExtractArgString(argsJson, "modelName");

            if (string.IsNullOrEmpty(modelName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'modelName' is required.", isError: true);

            return DelegateToBridge(idToken, json, "get_model_info");
        }

        private string HandleReadLabel(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string labelFileId = JsonHelpers.ExtractArgString(argsJson, "labelFileId");

            if (string.IsNullOrEmpty(labelFileId))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'labelFileId' is required.", isError: true);

            return DelegateToBridge(idToken, json, "read_label");
        }

        private string HandleCreateLabel(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string labelFileId = JsonHelpers.ExtractArgString(argsJson, "labelFileId");
            string labelId = JsonHelpers.ExtractArgString(argsJson, "labelId");
            string text = JsonHelpers.ExtractArgString(argsJson, "text");

            if (string.IsNullOrEmpty(labelFileId) || string.IsNullOrEmpty(labelId) || string.IsNullOrEmpty(text))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameters 'labelFileId', 'labelId', and 'text' are required.", isError: true);

            return DelegateToBridge(idToken, json, "create_label");
        }

        private string HandleAddToProject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameters 'objectType' and 'objectName' are required.", isError: true);

            return DelegateToBridge(idToken, json, "add_to_project");
        }

        private string HandleValidateObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName", "name");

            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameters 'objectType' and 'objectName' are required.", isError: true);

            return DelegateToBridge(idToken, json, "validate_object");
        }

        private string HandleGetFormLayout(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string formName = JsonHelpers.ExtractArgString(argsJson, "formName", "objectName");

            if (string.IsNullOrWhiteSpace(formName))
                return JsonHelpers.BuildToolResult(idToken, "Parameter 'formName' is required.", isError: true);
            if (formName.Contains("."))
                return JsonHelpers.BuildToolResult(idToken,
                    "Pass the base form name (e.g. 'CustTable'), not an extension name like '" + formName + "'.", isError: true);

            return DelegateToBridge(idToken, json, "get_form_layout");
        }

        private string HandleGetObjectTypeSchema(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType", "type");

            if (string.IsNullOrEmpty(objectType))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'objectType' is required.", isError: true);

            return DelegateToBridge(idToken, json, "get_object_type_schema");
        }

        private static void AppendArrayField(StringBuilder sb, string json, string arrayKey,
            params string[] fields)
        {
            // Simple heuristic: check if the array key exists and has content
            string marker = "\"" + arrayKey + "\":[";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return;

            // Find closing bracket
            int start = idx + marker.Length;
            if (start >= json.Length || json[start] == ']') return;

            // Extract items by parsing simple JSON objects
            int depth = 1;
            int pos = start;
            while (pos < json.Length && depth > 0)
            {
                if (json[pos] == '[') depth++;
                if (json[pos] == ']') depth--;
                pos++;
            }
            if (pos <= start) return;

            string arrayContent = json.Substring(start, pos - start - 1);

            // Parse each object in the array
            int objStart = arrayContent.IndexOf('{');
            int count = 0;
            while (objStart >= 0 && count < 200)
            {
                int objEnd = arrayContent.IndexOf('}', objStart);
                if (objEnd < 0) break;
                string obj = arrayContent.Substring(objStart, objEnd - objStart + 1);

                var line = new StringBuilder();
                foreach (string field in fields)
                {
                    string val = JsonHelpers.ExtractJsonString(obj, field);
                    if (val != null)
                    {
                        if (line.Length > 0) line.Append(" | ");
                        line.Append(field + "=" + val);
                    }
                }
                if (line.Length > 0)
                    sb.AppendLine("  " + line);

                objStart = arrayContent.IndexOf('{', objEnd);
                count++;
            }
        }

        /// <summary>
        /// Extracts typed metadata (properties, enumValues, fields, indexes, etc.)
        /// from the bridge JSON response and formats them for display.
        /// The output uses the same JSON parameter names that xpp_create_object and
        /// xpp_update_object accept, enabling round-trip usage.
        /// </summary>
        private static void AppendTypedMetadata(StringBuilder sb, string json)
        {
            // Properties (JSON object)
            string propsObj = JsonHelpers.ExtractNestedObject(json, "properties");
            if (!string.IsNullOrEmpty(propsObj) && propsObj != "{}")
            {
                sb.AppendLine("\n=== Properties (use in \"properties\" parameter) ===");
                sb.AppendLine(propsObj);
            }

            // Enum Values
            AppendJsonArray(sb, json, "enumValues", "Enum Values");
            // Fields
            AppendJsonArray(sb, json, "fields", "Fields");
            // Indexes
            AppendJsonArray(sb, json, "indexes", "Indexes");
            // Field Groups
            AppendJsonArray(sb, json, "fieldGroups", "Field Groups");
            // Relations
            AppendJsonArray(sb, json, "relations", "Relations");
            // Entry Points
            AppendJsonArray(sb, json, "entryPoints", "Entry Points");
            // Query Data Sources
            AppendJsonArray(sb, json, "dataSources", "Data Sources");
            // Form extension controls
            AppendJsonArray(sb, json, "controls", "Controls");
            AppendJsonArray(sb, json, "controlModifications", "Control Modifications");
        }

        private static void AppendJsonArray(StringBuilder sb, string json, string key, string label)
        {
            string marker = "\"" + key + "\":[";

            // Find the marker only at the top level of the JSON object (depth <= 1)
            // to avoid matching nested arrays with the same key name (e.g. "fields"
            // inside index or fieldGroup objects).
            int idx = -1;
            {
                int d = 0; bool inS = false; bool es = false;
                for (int i = 0; i < json.Length - marker.Length; i++)
                {
                    char c = json[i];
                    if (es) { es = false; continue; }
                    if (c == '\\') { es = true; continue; }

                    // Check for marker BEFORE toggling string state, so that the
                    // opening quote of a JSON key at the top level is matched.
                    if (!inS && d == 1 && c == '"'
                        && string.CompareOrdinal(json, i, marker, 0, marker.Length) == 0)
                    {
                        idx = i;
                        break;
                    }

                    if (c == '"') { inS = !inS; continue; }
                    if (inS) continue;
                    if (c == '{' || c == '[') d++;
                    if (c == '}' || c == ']') d--;
                }
            }
            if (idx < 0) return;

            // Find the full array including brackets
            int arrStart = idx + marker.Length - 1; // position of [
            int depth = 0; bool inStr = false; bool esc = false; int arrEnd = -1;
            for (int i = arrStart; i < json.Length; i++)
            {
                char c = json[i];
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[') depth++;
                if (c == ']') { depth--; if (depth == 0) { arrEnd = i; break; } }
            }
            if (arrEnd < 0 || arrEnd == arrStart + 1) return; // empty array

            string arrayJson = json.Substring(arrStart, arrEnd - arrStart + 1);
            sb.AppendLine("\n=== " + label + " (use in \"" + key + "\" parameter) ===");
            sb.AppendLine(arrayJson);
        }
    }
}
