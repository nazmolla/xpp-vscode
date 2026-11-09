using System;
using System.Collections.Generic;
using System.Linq;
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
        internal XRefQuery XRef => _xref;
        private readonly CodeSearch _codeSearch = new CodeSearch();
        private CodeExamples _examples;
        private CodeExamples Examples => _examples ?? (_examples = new CodeExamples(_xref));
        private readonly SecurityAnalysis _security = new SecurityAnalysis();
        private MessageTrace _messageTrace;
        private MessageTrace Messages => _messageTrace ?? (_messageTrace = new MessageTrace(_xref));

        /// <summary>Structured where-used for the extension's references list.</summary>
        public string WhereUsedJson(string body)
        {
            int max = int.TryParse(JsonHelpers.ExtractJsonString(body, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 1000)) : 500;
            return _xref.QueryJson(JsonHelpers.ExtractJsonString(body, "objectType"), JsonHelpers.ExtractJsonString(body, "objectName"),
                JsonHelpers.ExtractJsonString(body, "memberName"), max, JsonHelpers.ExtractJsonString(body, "referenceKind"));
        }

        public string TagsJson(string body) => _xref.TagsJson(JsonHelpers.ExtractJsonString(body, "filter"), 500);

        public string TagReferencesJson(string body) => _xref.TagReferencesJson(JsonHelpers.ExtractJsonString(body, "tag"), 1000);

        /// <summary>The label IDs of a label file that the cross-reference database records a use of.</summary>
        public string LabelUsageJson(string body)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string file = JsonHelpers.ExtractJsonString(body, "labelFileId");
            if (string.IsNullOrWhiteSpace(file) || !System.Text.RegularExpressions.Regex.IsMatch(file, @"^\w+$"))
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "labelFileId is required." });
            using (var conn = _xref.TryOpen(out string problem))
            {
                if (conn == null)
                    return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "The cross-reference database is not available: " + problem });
                var used = new List<string>();
                using (var cmd = new System.Data.SqlClient.SqlCommand("SELECT t.Path FROM Names t WHERE t.Path LIKE @p AND EXISTS (SELECT 1 FROM [References] r WHERE r.TargetId = t.Id)", conn) { CommandTimeout = 120 })
                {
                    cmd.Parameters.AddWithValue("@p", "/Labels/@" + file + ":%");
                    using (var r = cmd.ExecuteReader())
                        while (r.Read()) used.Add(r.GetString(0).Substring(("/Labels/@" + file + ":").Length));
                }
                return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["used"] = used });
            }
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
      ""description"": ""Creates a new D365FO X++ metadata object using the MetaModel API. CRITICAL: This is the ONLY correct way to create X++ objects. NEVER create metadata files directly via terminal, file writes, or Set-Content — that bypasses the MetaModel API and produces corrupted/unregistered objects. ALL parameters MUST be JSON — any XML or CDATA content will be REJECTED with an error. Use 'properties' for scalar metadata (Label, IsExtensible, TableGroup, etc.), 'enumValues' for AxEnum values, 'fields'/'indexes'/'fieldGroups'/'relations' for AxTable structure, 'entryPoints' for security types. Use xpp_read_object on existing objects to see the exact JSON format. The object is automatically added to the active VS project. EXTENSIONS: to change a standard/foreign table, enum, or EDT, create AxTableExtension / AxEnumExtension / AxEdtExtension named '<BaseObject>.<Suffix>' (e.g. 'CustTable.MyModelExtension') in your own custom model; fields/indexes/fieldGroups/relations/enumValues are added to the base object, and 'properties' become property modifications of the base object. FORM EXTENSIONS (AxFormExtension): ALWAYS call xpp_get_form_layout on the base form first, then pass 'controls' with explicit placement (parent + position, and previousSibling for position 'after') and optional 'dataSources' / 'controlModifications'. Placement is validated against the base form plus all its existing extensions before saving, and verified after saving. DATA ENTITY EXTENSIONS (AxDataEntityViewExtension '<Entity>.<Suffix>'): pass 'entityFields' (name, dataSource, dataField); each dataField must already exist on that data source's table or a table extension. The same fields are added automatically to an extension of the entity's staging table ('<StagingTable>.<Suffix>') with the source field's type/EDT, unless skipStagingTable=true. SECURITY: privileges take 'entryPoints' (objectType MenuItemDisplay/MenuItemOutput/MenuItemAction, objectName, grant e.g. 'Read' or 'Read,Update,Create,Delete' or 'Invoke') and 'dataEntityPermissions'; duties take 'privileges'; roles take 'duties' and 'privileges'. Standard privileges cannot be extended: create your own privilege and add it to a standard duty/role with AxSecurityDutyExtension / AxSecurityRoleExtension ('<Base>.<Suffix>'). EDTs: set properties.EdtType (String, Int, Int64, Real, Enum, Date, UtcDateTime, Time, Guid, Container) or properties.Extends (kind taken from the parent); then StringSize / EnumType / NoOfDecimals as needed. LABEL FILES (AxLabelFile): objectName = label file ID (e.g. 'MyModelLabels'), properties.Languages = 'en-US,fr-CA'; one label file per language is created, then add labels with xpp_create_label and reference them as '@<Id>:<LabelId>' in Label/HelpText properties. MENU EXTENSIONS (AxMenuExtension '<Menu>.<Suffix>', e.g. 'AccountsReceivable.MyModel'): pass 'menuElements' (name, menuItemName, menuItemType Display|Output|Action, parent = existing submenu, position end|begin|after, previousSibling). Tables and table extensions: every relation is checked (related table and both fields must exist) before anything is saved. The create is all-or-nothing: if any part fails, nothing is saved. The new object is added to the active project (the response says so) and the response ends with a 'Validation:' line; check it. If the call times out on your side, the server may still have completed it: check with xpp_find_object / xpp_read_object before retrying."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": {
            ""type"": ""string"",
            ""description"": ""The D365FO object type to create."",
            ""enum"": [""AxClass"", ""AxTable"", ""AxView"", ""AxDataEntityView"", ""AxEdt"", ""AxEnum"", ""AxForm"", ""AxTile"", ""AxMenu"", ""AxMenuItemDisplay"", ""AxMenuItemOutput"", ""AxMenuItemAction"", ""AxQuery"", ""AxSecurityPrivilege"", ""AxSecurityDuty"", ""AxSecurityRole"", ""AxService"", ""AxServiceGroup"", ""AxConfigurationKey"", ""AxTableExtension"", ""AxEnumExtension"", ""AxEdtExtension"", ""AxFormExtension"", ""AxDataEntityViewExtension"", ""AxSecurityDutyExtension"", ""AxSecurityRoleExtension"", ""AxLabelFile"", ""AxMenuExtension"", ""AxCompositeDataEntityView"", ""AxWorkflowCategory"", ""AxWorkflowTemplate"", ""AxWorkflowApproval"", ""AxReport""]
          },
          ""objectName"": { ""type"": ""string"", ""description"": ""Name of the object. Must follow the model's naming prefix convention."" },
          ""typedMetadata"": { ""type"": ""object"", ""description"": ""Full strongly-typed metadata graph for the target object type. Preferred for complete metadata writes across all object types."" },
          ""declaration"": { ""type"": ""string"", ""description"": ""Raw X++ class/table declaration code block. Plain code only — NO XML, NO CDATA wrappers."" },
          ""methods"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Array of complete X++ method source strings. Plain code only — NO XML, NO CDATA wrappers. Any XML content will be rejected with an error."" },
          ""properties"": { ""type"": ""object"", ""description"": ""Key-value pairs for scalar metadata properties set via reflection. Keys are property names (e.g. Label, IsExtensible, TableGroup, ObjectType, Object, FormRef). Values are strings — type coercion is automatic (bool, int, enum). Use xpp_read_object on an existing object to discover available property names."" },
          ""enumValues"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""value"": { ""type"": ""integer"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""value""] }, ""description"": ""Enum values for AxEnum. Each entry creates an AxEnumValue with Name, Value (integer), and optional Label."" },
          ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""fieldType"": { ""type"": ""string"", ""enum"": [""String"", ""Int"", ""Real"", ""Date"", ""DateTime"", ""Enum"", ""Int64"", ""Container"", ""Guid"", ""Time""] }, ""extendedDataType"": { ""type"": ""string"" }, ""enumType"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""fieldType""] }, ""description"": ""Table fields for AxTable. fieldType determines the field class (AxTableFieldString, AxTableFieldInt, etc.)."" },
          ""indexes"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""allowDuplicates"": { ""type"": ""boolean"" }, ""alternateKey"": { ""type"": ""boolean"", ""description"": ""Unique index (allowDuplicates false) that is the table's natural key."" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Table indexes for AxTable. Each index has a name and array of field names."" },
          ""fieldGroups"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Table field groups for AxTable. Each group has a name and array of field names."" },
          ""relations"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""relatedTable"": { ""type"": ""string"" }, ""constraints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""field"": { ""type"": ""string"" }, ""relatedField"": { ""type"": ""string"" } } } } }, ""required"": [""name"", ""relatedTable""] }, ""description"": ""Table relations for AxTable. Each relation maps fields to related table fields."" },
          ""dataSources"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""table"": { ""type"": ""string"" }, ""parentDataSource"": { ""type"": ""string"" }, ""joinMode"": { ""type"": ""string"" }, ""linkType"": { ""type"": ""string"" }, ""dynamicFields"": { ""type"": ""boolean"", ""description"": ""AxQuery: default true when no fields are given; false requires fields."" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxQuery: field names of the table to select (with dynamicFields false)."" }, ""relations"": { ""type"": ""boolean"" }, ""firstOnly"": { ""type"": ""boolean"" }, ""ranges"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""field"": { ""type"": ""string"" }, ""value"": { ""type"": ""string"" } } } } }, ""required"": [""name""] }, ""description"": ""Query data sources for AxQuery. Use this to define table sources and optional ranges. For AxFormExtension: data sources to add to the form (name, table, parentDataSource = join source on the form, linkType e.g. InnerJoin/OuterJoin/ExistJoin/Delayed/Active)."" },
          ""controls"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""Unique control name on the whole form."" }, ""controlType"": { ""type"": ""string"", ""description"": ""String, Integer, Int64, Real, Date, DateTime, Time, Guid, CheckBox, ComboBox, RadioButton, StaticText, ReferenceGroup, Group, Tab, TabPage, Grid, ButtonGroup, Button, CommandButton, MenuFunctionButton, MenuButton, ActionPaneTab. Optional when dataField is set (inferred from the field type)."" }, ""parent"": { ""type"": ""string"", ""description"": ""TOP-LEVEL controls only: name of the existing container (Group, TabPage, Grid, ButtonGroup, ...) taken from xpp_get_form_layout. Use 'Design' for the form's top level."" }, ""position"": { ""type"": ""string"", ""enum"": [""end"", ""begin"", ""after""], ""description"": ""TOP-LEVEL controls only. end (default) = last child of parent; begin = first child; after = directly after previousSibling."" }, ""previousSibling"": { ""type"": ""string"", ""description"": ""Required with position 'after': an existing control that is a DIRECT child of parent."" }, ""dataSource"": { ""type"": ""string"", ""description"": ""Form data source name (inherited from the parent container when omitted)."" }, ""dataField"": { ""type"": ""string"", ""description"": ""Field on the data source's table (table extension fields included)."" }, ""dataGroup"": { ""type"": ""string"", ""description"": ""Table field group (for Group controls bound to a field group)."" }, ""label"": { ""type"": ""string"" }, ""properties"": { ""type"": ""object"", ""description"": ""Extra control properties, e.g. {\""Visible\"":\""No\"", \""MenuItemName\"":\""MyMenuItem\""}."" }, ""controls"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""Child controls of a container, same shape but WITHOUT parent/position/previousSibling; placed in list order."" } }, ""required"": [""name""] }, ""description"": ""AxFormExtension only: controls to add. Survey the form with xpp_get_form_layout first and choose parent/position from it."" },
          ""controlModifications"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""Existing control of the base form (or of another extension)."" }, ""properties"": { ""type"": ""object"", ""description"": ""Property changes, e.g. {\""Visible\"":\""No\""}."" } }, ""required"": [""name"", ""properties""] }, ""description"": ""AxFormExtension only: property changes on controls of the base form or of other extensions. Allowed properties (D365FO rule): Text, Label, HelpText, Caption, Visible, Enabled, EnableFormRef, CountryRegionCodes, Pattern, PatternVersion, NeedsRecord, SaveRecord, AllowEdit, NeededPermission, Style, DataGroup; anything else fails the build with UnsupportedPropertyForModification and is rejected. Never use it for controls this extension adds: set their properties directly (controls[].properties, or updateControls later)."" },
          ""entryPoints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""objectType"": { ""type"": ""string"" }, ""objectName"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Security entry points for AxSecurityPrivilege/Duty/Role."" },
          ""privileges"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityDuty, AxSecurityRole and their extensions: privilege names to add."" },
          ""duties"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityRole / AxSecurityRoleExtension: duty names to add."" },
          ""dataEntityPermissions"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""Data entity name."" }, ""grant"": { ""type"": ""string"", ""description"": ""Read, Update, Create, Correct, Delete or All; comma-separated for several."" }, ""integrationMode"": { ""type"": ""string"", ""enum"": [""All"", ""DataServices"", ""DataManagement""] } }, ""required"": [""name""] }, ""description"": ""AxSecurityPrivilege only: data entity permissions."" },
          ""entityFields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""New entity field name (unique on the entity and its extensions)."" }, ""dataSource"": { ""type"": ""string"", ""description"": ""Entity data source name (read the entity first)."" }, ""dataField"": { ""type"": ""string"", ""description"": ""Field on the data source: a table field (incl. table extensions), or, when the data source is another entity or a view, a field of that entity/view or of its extensions."" }, ""label"": { ""type"": ""string"" }, ""mandatory"": { ""type"": ""boolean"" } }, ""required"": [""name"", ""dataSource"", ""dataField""] }, ""description"": ""AxDataEntityViewExtension only: mapped fields to add. The staging table gets the same fields automatically."" },
          ""skipStagingTable"": { ""type"": ""boolean"", ""description"": ""AxDataEntityViewExtension only: do not extend the staging table. Default false."" },
          ""rootDataEntities"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""AxCompositeDataEntityView only: one root reference {name, dataEntity, embeddedDataEntities: [{name, dataEntity, relation, embeddedDataEntities}]}. relation is a relation on the embedded entity that points to its parent entity. Entities, relations and the staging tables (RowId/ParentRowId, relation to the parent staging table) are checked."" },
          ""modelName"": { ""type"": ""string"", ""description"": ""Target model name. If omitted, uses the active project's model."" },
          ""menuElements"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""menuItemName"": { ""type"": ""string"", ""description"": ""Defaults to name."" }, ""menuItemType"": { ""type"": ""string"", ""enum"": [""Display"", ""Output"", ""Action""] }, ""parent"": { ""type"": ""string"", ""description"": ""Existing submenu of the base menu (e.g. 'Periodic'); omit for the menu's top level."" }, ""position"": { ""type"": ""string"", ""enum"": [""end"", ""begin"", ""after""] }, ""previousSibling"": { ""type"": ""string"" } }, ""required"": [""name"", ""menuItemType""] }, ""description"": ""AxMenuExtension only: menu items to add. The menu item and the parent submenu must exist."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_read_object"",
      ""description"": ""Reads a D365FO X++ object by type and name using the MetaModel API. CRITICAL: This is the ONLY correct way to read X++ objects. NEVER read metadata files directly via Get-Content, the terminal, or file tools — this tool returns everything the XML contains: declaration, every method's signature (with access modifiers such as internal/private/public) and full source, and strongly-typed metadata (typedMetadata graph plus properties, enumValues, fields, indexes, fieldGroups, relations, dataSources), model name and editability. Works for ANY metadata type. For classes with very large source, methods are listed by signature first; call again with methodNames to get their full source. Large objects (entities, forms, big tables) return a summary first; ask for what you need with sections."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""Any D365FO metadata type: AxClass, AxTable, AxForm, AxEnum, AxEdt, AxView, AxQuery, AxMap, AxReport, AxDataEntityView, AxTableExtension, AxFormExtension, etc."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""The object name."" },
          ""methodNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Return the full source of only these methods (use after a read that listed signatures only)."" },
          ""sections"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Optional. Return only these sections, e.g. [\""properties\"", \""dataSources\"", \""entityFields\"", \""fields\"", \""relations\"", \""indexes\"", \""fieldGroups\"", \""methods\"", \""declaration\"", \""controls\"", \""typedMetadata\""]; [\""summary\""] lists the sections with their sizes; [\""all\""] returns everything. Without it, objects over 25,000 characters return their small sections plus a list of the large ones."" },
          ""filePath"": { ""type"": ""string"", ""description"": ""Absolute path to the metadata file, if you have it. Prefer objectType + objectName."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_update_object"",
      ""description"": ""Updates an existing D365FO X++ object using the MetaModel API. CRITICAL: This is the ONLY correct way to modify X++ objects. NEVER edit metadata files directly. ALL parameters MUST be JSON — any XML or CDATA content will be REJECTED with an error. Can update declaration, add/replace/remove methods, set properties, and add enum values/fields/indexes/relations. Use xpp_read_object first to see current state. ONLY works on custom model objects. If the call times out on your side, the server may still have saved the change: read the object with xpp_read_object before retrying."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""The object type (AxClass, AxTable, AxForm, AxEdt, AxEnum, AxView, AxQuery, AxDataEntityView, AxMenu, AxSecurityPrivilege, AxSecurityDuty, AxSecurityRole, AxTableExtension, AxEnumExtension, AxEdtExtension, AxFormExtension, AxDataEntityViewExtension, AxSecurityDutyExtension, AxSecurityRoleExtension, AxMenuExtension, AxCompositeDataEntityView). For extensions, 'properties' are written as property modifications of the base object."" },
          ""objectName"": { ""type"": ""string"", ""description"": ""The object name to update."" },
          ""typedMetadata"": { ""type"": ""object"", ""description"": ""Full strongly-typed metadata graph for complete metadata updates across all object types."" },
          ""declaration"": { ""type"": ""string"", ""description"": ""New raw X++ declaration code. Replaces existing. Plain code only — NO XML, NO CDATA."" },
          ""methods"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Methods to add or replace (matched by name). Plain X++ code only — NO XML, NO CDATA. Any XML content will be rejected."" },
          ""removeMethodNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Method names to remove."" },
          ""properties"": { ""type"": ""object"", ""description"": ""Key-value pairs for scalar metadata properties to set or update. Same format as xpp_create_object."" },
          ""enumValues"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""value"": { ""type"": ""integer"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""value""] }, ""description"": ""Enum values to add (for AxEnum). Values with matching names are skipped."" },
          ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""fieldType"": { ""type"": ""string"" }, ""extendedDataType"": { ""type"": ""string"" }, ""enumType"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" } }, ""required"": [""name"", ""fieldType""] }, ""description"": ""AxTable / AxTableExtension: new fields. A name that already exists is refused (change it with updateFields). The EDT's base type must match fieldType. A field name removed earlier cannot come back with another base type (the database sync cannot convert the column)."" },
          ""updateFields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""extendedDataType"": { ""type"": ""string"" }, ""enumType"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""helpText"": { ""type"": ""string"" }, ""properties"": { ""type"": ""object"", ""description"": ""Other field properties, e.g. {\""Mandatory\"":\""Yes\"",\""StringSize\"":\""30\""}."" } }, ""required"": [""name""] }, ""description"": ""AxTable / AxTableExtension: change fields this object defines: EDT, enum type, label, help text, properties. The base type never changes (like Visual Studio: Int to String breaks the database sync); the new EDT must have the field's base type. For another base type add a new field with another name and remove the old one (removeFields)."" },
          ""removeFields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxTable / AxTableExtension: names of fields this object defines to remove (also taken out of its field groups)."" },
          ""removeIndexes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxTable / AxTableExtension: index names to remove."" },
          ""removeFieldGroups"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxTable / AxTableExtension: field group names to remove."" },
          ""removeRelations"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxTable / AxTableExtension: relation names to remove."" },
          ""indexes"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""allowDuplicates"": { ""type"": ""boolean"" }, ""alternateKey"": { ""type"": ""boolean"", ""description"": ""Unique index (allowDuplicates false) that is the table's natural key."" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Indexes to add (AxTable / AxTableExtension); an existing index of this object with the same name is replaced."" },
          ""fieldGroups"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name"", ""fields""] }, ""description"": ""Field groups to add (AxTable / AxTableExtension); an existing field group of this object with the same name is replaced."" },
          ""relations"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""relatedTable"": { ""type"": ""string"" }, ""constraints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""field"": { ""type"": ""string"" }, ""relatedField"": { ""type"": ""string"" } } } } }, ""required"": [""name"", ""relatedTable""] }, ""description"": ""Relations to add (AxTable / AxTableExtension); an existing relation of this object with the same name is replaced."" },
          ""dataSources"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""table"": { ""type"": ""string"" }, ""parentDataSource"": { ""type"": ""string"" }, ""joinMode"": { ""type"": ""string"" }, ""linkType"": { ""type"": ""string"" }, ""dynamicFields"": { ""type"": ""boolean"", ""description"": ""AxQuery: default true when no fields are given; false requires fields."" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxQuery: field names of the table to select (with dynamicFields false)."" }, ""relations"": { ""type"": ""boolean"" }, ""firstOnly"": { ""type"": ""boolean"" }, ""ranges"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""field"": { ""type"": ""string"" }, ""value"": { ""type"": ""string"" } } } } }, ""required"": [""name""] }, ""description"": ""Query data sources for AxQuery updates. For AxFormExtension: data sources to add to the form (name, table, parentDataSource, linkType)."" },
          ""controls"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""AxFormExtension only: controls to add — same shape as in xpp_create_object (name, controlType, parent, position end|begin|after, previousSibling, dataSource, dataField, dataGroup, label, properties, controls). Call xpp_get_form_layout first."" },
          ""controlModifications"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""properties"": { ""type"": ""object"" } }, ""required"": [""name"", ""properties""] }, ""description"": ""AxFormExtension only: property changes on controls of the base form or of other extensions. Allowed properties (D365FO rule): Text, Label, HelpText, Caption, Visible, Enabled, EnableFormRef, CountryRegionCodes, Pattern, PatternVersion, NeedsRecord, SaveRecord, AllowEdit, NeededPermission, Style, DataGroup; anything else fails the build with UnsupportedPropertyForModification and is rejected. Never use it for controls this extension adds: set their properties directly (controls[].properties, or updateControls later)."" },
          ""updateControls"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"", ""description"": ""A control this extension added (also nested ones)."" }, ""properties"": { ""type"": ""object"", ""description"": ""Any property of the control, e.g. {\""ColumnsMode\"":\""Fill\""}."" }, ""label"": { ""type"": ""string"" }, ""dataSource"": { ""type"": ""string"" }, ""dataField"": { ""type"": ""string"" }, ""dataGroup"": { ""type"": ""string"" }, ""parent"": { ""type"": ""string"", ""description"": ""Move: new parent (\""Design\"" for the top level)."" }, ""position"": { ""type"": ""string"", ""enum"": [""end"", ""begin"", ""after""] }, ""previousSibling"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""AxFormExtension only: change controls this extension added: properties, label, binding, and placement (give parent/position/previousSibling to move). Validated before saving; moves are verified after."" },
          ""removeControls"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxFormExtension only: names of controls this extension added to remove (with their children). Base-form controls cannot be removed; hide them with controlModifications Visible=No."" },
          ""removeControlModifications"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxFormExtension only: drop control modifications of this extension, as \""Control\"" (all its property changes) or \""Control.Property\""."" },
          ""entryPoints"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""objectType"": { ""type"": ""string"" }, ""objectName"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" } }, ""required"": [""name""] }, ""description"": ""Entry points to add or update by name (for AxSecurityPrivilege). grant: Read, Update, Create, Correct, Delete, Invoke or All; comma-separated for several."" },
          ""privileges"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityDuty, AxSecurityRole and their extensions: privilege names to add."" },
          ""duties"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""AxSecurityRole / AxSecurityRoleExtension: duty names to add."" },
          ""dataEntityPermissions"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""grant"": { ""type"": ""string"" }, ""integrationMode"": { ""type"": ""string"", ""enum"": [""All"", ""DataServices"", ""DataManagement""] } }, ""required"": [""name""] }, ""description"": ""AxSecurityPrivilege only: data entity permissions to add or update."" },
          ""removeSecurityItems"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Security objects: names of entry points, data entity permissions, privileges or duties to remove."" },
          ""entityFields"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""dataSource"": { ""type"": ""string"" }, ""dataField"": { ""type"": ""string"" }, ""label"": { ""type"": ""string"" }, ""mandatory"": { ""type"": ""boolean"" } }, ""required"": [""name"", ""dataSource"", ""dataField""] }, ""description"": ""AxDataEntityViewExtension only: mapped fields to add; the staging table extension gets the same fields."" },
          ""menuElements"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""AxMenuExtension only: menu items to add or replace by name (same shape as in xpp_create_object)."" },
          ""skipStagingTable"": { ""type"": ""boolean"", ""description"": ""AxDataEntityViewExtension only: do not extend the staging table."" },
          ""rootDataEntities"": { ""type"": ""array"", ""items"": { ""type"": ""object"" }, ""description"": ""AxCompositeDataEntityView only: the full reference tree (replaces the current one), same shape as in xpp_create_object."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_find_object"",
      ""description"": ""Security roles, duties and privileges (objectType AxSecurityRole/AxSecurityDuty/AxSecurityPrivilege, partial match) are also found by their label text, e.g. 'Sales manager' or 'Maintain fixed assets'; each match shows its label, and when no label has all the words the closest labels are listed. Searches for D365FO metadata objects by name across ALL loaded models and packages using the MetaModel API. CRITICAL: Always use this instead of Get-ChildItem/dir/file searches on metadata folders. NEVER search the file system for X++ objects. Returns matching objects with type, model, and editability. Searches scan the loaded metadata and block other tool calls while they run: search for ONE specific object at a time, always pass objectType, and use exactMatch=true when you know the full name. Partial names must be at least 3 characters. Scans stop after a few seconds and return PARTIAL results with a message — if that happens, narrow the search instead of retrying the same query."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectName"": { ""type"": ""string"", ""description"": ""Full or partial name to search for (case-insensitive). Partial names need at least 3 characters; prefer specific names."" },
          ""objectType"": { ""type"": ""string"", ""description"": ""Strongly recommended: limits search to 1 type (5-10x faster). Any metadata type: AxClass, AxTable, AxForm, AxEnum, AxEdt, AxView, AxQuery, AxDataEntityView, AxMap, AxMenu, AxMenuItemDisplay/Output/Action, AxSecurityRole/Duty/Privilege, AxTableExtension, AxFormExtension, AxLabelFile, ... An unknown type is reported as an error, never as 'not found'."" },
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
          ""objectType"": { ""type"": ""string"", ""description"": ""Strongly recommended; required with modelName. Any metadata type, e.g. AxClass, AxTable, AxForm, AxDataEntityView, AxSecurityRole, AxTableExtension, AxMenuItemDisplay."" },
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
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Max results. Default 25. Max 100."" },
          ""includeCode"": { ""type"": ""boolean"", ""description"": ""Also show the code lines where each reference is used (how existing code calls the API). Max 25 references."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_find_examples"",
      ""description"": ""Finds standard (Microsoft) implementations to learn from, reuse or copy - the first step of every X++ design. Give a task in words (query: 'batch job with a data contract', 'number sequence for a new table', 'custom lookup', 'business event', 'report data provider', 'service operation', 'workflow', 'form with the SimpleList pattern', 'details master form', ...) and/or an object to resemble (objectType + objectName: objects that share its base class, interfaces, attributes, called APIs and tables). Returns ranked examples with the reason (small, focused examples first for framework patterns). Then read the best ones with xpp_read_object and follow their structure; call or extend them where possible, copy only when they cannot be reused."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""query"": { ""type"": ""string"", ""description"": ""What you need to implement, in words."" },
          ""objectType"": { ""type"": ""string"", ""description"": ""With objectName: find objects similar to this one (AxClass, AxTable, AxForm, ...)."" },
          ""objectName"": { ""type"": ""string"" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Default 15, max 50."" }
        }
      }
    },
    {
      ""name"": ""xpp_analyze_security"",
      ""description"": ""Security and license analysis of the metadata (all packages, no environment needed). op=license: the user license a role, duty or privilege requires (Team Members / Activity / Operations), the menu items and grants that drive it, and which grants Read-only would lower. op=access: who reaches a menu item, form, table or data entity (privileges, duties, roles with grants). op=placement: the standard roles to give a new duty, ranked by whether they already reach the same standard objects (the forms the duty's menu items open, plus objects you give) and by label similarity to the requirement's role name (requirements use job titles that often have no standard role of that name). op=gaps: security gaps in your models (menu items no privilege grants, privileges in no duty, duties in no role, over-granting, missing objects, data entities without permissions). op=sod: segregation-of-duties conflicts in your roles (or one role) against standard rules or rulesJson. op=report: a Markdown or CSV security report for roles or a model, for client sign-off. Use it when designing security: least privilege (separate View and Maintain privileges, Read on display items when users only look), know the license impact of every grant, and run op=gaps and op=sod before finishing."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""op"": { ""type"": ""string"", ""enum"": [""license"", ""access"", ""placement"", ""gaps"", ""sod"", ""report""] },
          ""objectType"": { ""type"": ""string"", ""description"": ""license: AxSecurityRole, AxSecurityDuty or AxSecurityPrivilege. access: AxMenuItemDisplay / AxMenuItemOutput / AxMenuItemAction, AxForm, AxTable or AxDataEntityView."" },
          ""objectName"": { ""type"": ""string"" },
          ""duty"": { ""type"": ""string"", ""description"": ""placement: your new duty."" },
          ""objects"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""placement: standard objects of the same job as Type:Name, e.g. [\""AxForm:AssetTable\"", \""AxMenuItemDisplay:SalesTableListPage\""]."" },
          ""models"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""gaps: models to check (default: your custom models)."" },
          ""role"": { ""type"": ""string"", ""description"": ""sod: one role (default: roles of your models and roles your models extend). placement: the requirement's role wording, e.g. 'Fixed asset accountant'."" },
          ""rulesJson"": { ""type"": ""string"", ""description"": ""sod: optional rules [{\""Name\"":...,\""First\"":duty,\""Second\"":duty,\""Severity\"":\""High\"",\""Reason\"":...}]."" },
          ""roles"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""report: roles to report."" },
          ""model"": { ""type"": ""string"", ""description"": ""report: all roles of this model and roles it extends."" },
          ""format"": { ""type"": ""string"", ""enum"": [""markdown"", ""csv""] }
        },
        ""required"": [""op""]
      }
    },
    {
      ""name"": ""xpp_get_guide"",
      ""description"": ""The full F&O Studio development guide (the short instructions you have are its summary): rules with examples and code templates, SSRS report patterns, Chain of Command per object type, event handlers, the X++ language reference, object types and properties, display and edit methods, naming conventions, best practices. Without topic: the list of topics. Read the topic before writing code of a kind you have not written in this session."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""topic"": { ""type"": ""string"", ""description"": ""Words of a topic title, e.g. 'ssrs', 'chain of command', 'event handlers', 'display methods', 'language reference'."" }
        }
      }
    },
    {
      ""name"": ""xpp_find_message_source"",
      ""description"": ""Finds the X++ code that raises a message (error, warning or info text). Paste the message as shown, values included: it is matched against every label text (placeholders %1..%9 match any value), and the labels' usages come from the cross-reference database with the code line (raising code first, tests last). Messages written as literal strings are found by a code search. Use it to debug an error a user reports, or to find the standard check you must satisfy or extend."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""message"": { ""type"": ""string"", ""description"": ""The message text as the user saw it, e.g. 'Customer 1001 is on hold. Cannot post the order.'"" },
          ""language"": { ""type"": ""string"", ""description"": ""Language of the message (default en-US), e.g. fr-CA."" },
          ""maxLabels"": { ""type"": ""integer"", ""description"": ""Default 5."" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Usages per label, default 15."" }
        },
        ""required"": [""message""]
      }
    },
    {
      ""name"": ""xpp_search_code"",
      ""description"": ""Searches the X++ source of every package (standard and custom): method code and declarations of classes, tables, forms, views, data entities, queries and maps. Use it to find how standard code does something (an API call, a pattern, a string, a label ID), where a field or method name appears in code, or precedents to copy. Literal text by default (case-insensitive); regex=true for a .NET regular expression. Results: object, method, line and the matching line. Narrow big searches with objectTypes or model. Until the files are in the disk cache, a search over everything reads them from disk (30-60 s); objectTypes=[\""AxClass\""] or a model/package makes it much faster."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""pattern"": { ""type"": ""string"", ""description"": ""Text (or regex) to find, at least 3 characters, e.g. 'NumberSeq::newGetNum(' or 'SysOperationServiceController'."" },
          ""regex"": { ""type"": ""boolean"" },
          ""caseSensitive"": { ""type"": ""boolean"" },
          ""objectTypes"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Limit to these types, e.g. [\""AxClass\""]. Default: all code types."" },
          ""model"": { ""type"": ""string"", ""description"": ""Limit to one model or package (a package such as ApplicationSuite covers all its models)."" },
          ""maxResults"": { ""type"": ""integer"", ""description"": ""Default 50, max 200."" }
        },
        ""required"": [""pattern""]
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
      ""description"": ""Creates labels in a label file of your own model: one label (labelId + text) or many at once (labels array; prefer this over repeated calls), each in the given language and optionally in more languages (translations). Each label file is written once per call; the result lists every label and language as created, unchanged (same text already there) or FAILED. The label file must exist: create it with xpp_create_object objectType=AxLabelFile (objectName = label file ID, properties.Languages = 'en-US,fr-CA'). The response gives the reference to use in properties: @<labelFileId>:<labelId>."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""labelFileId"": { ""type"": ""string"", ""description"": ""The label file ID."" },
          ""language"": { ""type"": ""string"", ""description"": ""Language code of 'text'. Default en-US."" },
          ""labelId"": { ""type"": ""string"", ""description"": ""The label ID to create (letters and digits, e.g. HSRemovalStatus). Omit when using labels."" },
          ""text"": { ""type"": ""string"", ""description"": ""The label text."" },
          ""translations"": { ""type"": ""object"", ""description"": ""Optional texts in other languages of the same label file, keyed by language code, e.g. fr-CA."" },
          ""comment"": { ""type"": ""string"", ""description"": ""Optional comment."" },
          ""labels"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""labelId"": { ""type"": ""string"" }, ""text"": { ""type"": ""string"", ""description"": ""Text in 'language'."" }, ""comment"": { ""type"": ""string"" }, ""translations"": { ""type"": ""object"", ""description"": ""Texts in other languages, keyed by language code."" } }, ""required"": [""labelId"", ""text""] }, ""description"": ""Several labels in one call (instead of labelId/text)."" }
        },
        ""required"": [""labelFileId""]
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
          ""indexes"": { ""type"": ""array"", ""items"": { ""type"": ""object"", ""properties"": { ""name"": { ""type"": ""string"" }, ""allowDuplicates"": { ""type"": ""boolean"" }, ""alternateKey"": { ""type"": ""boolean"", ""description"": ""Unique index (allowDuplicates false) that is the table's natural key."" }, ""fields"": { ""type"": ""array"", ""items"": { ""type"": ""string"" } } }, ""required"": [""name""] }, ""description"": ""Expected indexes. Deep-compares name, allowDuplicates, and field list when provided."" },
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
      ""name"": ""xpp_delete_object"",
      ""description"": ""Deletes an object COMPLETELY from its model (the metadata file is removed) and from the active project, like Visual Studio's Delete. Only objects of your own custom models; standard and other-ISV objects are refused. Before deleting, where-used is checked: if other objects reference it the delete is refused and the referencing objects are listed, unless force=true. Use it to clean up wrong, probe or broken objects instead of touching files. To keep the object but drop it from the project use xpp_remove_from_project."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""description"": ""Metadata type, e.g. AxClass, AxQuery, AxTableExtension, AxLabelFile (label files are deleted per language: '<Id>_<lang>')."" },
          ""objectName"": { ""type"": ""string"" },
          ""force"": { ""type"": ""boolean"", ""description"": ""Delete even when other objects still reference it (they will no longer compile). Default false."" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_remove_from_project"",
      ""description"": ""Excludes an object from the active D365FO project (.rnrproj) and keeps it in the model, like Visual Studio's Exclude From Project."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"" },
          ""objectName"": { ""type"": ""string"" }
        },
        ""required"": [""objectType"", ""objectName""]
      }
    },
    {
      ""name"": ""xpp_select_project"",
      ""description"": ""Lists the D365FO projects (.rnrproj) that can be opened, or makes one the active project for all tools (new objects are added to it, scope=project builds use it). Without projectFile it lists candidates (the active one is marked '*') from searchFolder, the current project's repos folder and %USERPROFILE%\\source\\repos. The VS Code project view follows the selection."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""projectFile"": { ""type"": ""string"", ""description"": ""Full path of the .rnrproj to activate. Omit to list projects."" },
          ""searchFolder"": { ""type"": ""string"", ""description"": ""Extra folder to search for .rnrproj files (3 levels deep)."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_build"",
      ""description"": ""Builds a custom model exactly like Dynamics 365 > Build: compiles label files (LabelC), then X++ (xppc) with cross-references, and returns the result with every error, fatal error and warning (object, method, line, message). Call it after creating or changing code and fix every error before you report success; a build is only successful when it says BUILD SUCCEEDED. scope=project reports only the diagnostics of objects in the active project (xppc still compiles the whole package). syncDatabase=true synchronizes the database after a successful build where a local database exists; on unified developer environments the database is synchronized by deployment instead and the result says so. Builds take minutes."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""modelName"": { ""type"": ""string"", ""description"": ""Model to build. Defaults to the active project's model."" },
          ""scope"": { ""type"": ""string"", ""enum"": [""model"", ""project""], ""description"": ""Default model."" },
          ""syncDatabase"": { ""type"": ""boolean"", ""description"": ""Synchronize the database after a successful build. Default false."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_package"",
      ""description"": ""Creates a deployable package (.zip) of custom models with the same script Visual Studio's Create Deployment Package runs, from their last build (run xpp_build first). Returns the package path. Only call it when the user asked for a package; it does not deploy anything."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""modelNames"": { ""type"": ""array"", ""items"": { ""type"": ""string"" }, ""description"": ""Custom models to package. Defaults to the active project's model."" },
          ""version"": { ""type"": ""string"", ""description"": ""Package version, e.g. 1.0.0.0 (default)."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_diff_changes"",
      ""description"": ""Lists the elements of your models that changed in Git: uncommitted changes, and with baseBranch also what the current branch changed since it left that branch (added, modified, deleted, renamed). Use it to review or summarize a change set (pull request description, code review), then read the elements with xpp_read_object and check them with xpp_check_best_practice and xpp_analyze_performance."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""baseBranch"": { ""type"": ""string"", ""description"": ""Branch to compare with, e.g. main. Omit for uncommitted changes only."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_check_best_practice"",
      ""description"": ""Runs the X++ best-practice rules (xppbp, the checks Visual Studio runs and the ones partner code reviews use) on a custom model and returns every warning and error with element, member, line, rule moniker and message. scope=project reports only the active project's elements. Run it after creating or changing code, next to xpp_build, and fix what it reports or say why a warning stays. Takes a minute or more."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""modelName"": { ""type"": ""string"", ""description"": ""Model to check. Defaults to the active project's model."" },
          ""scope"": { ""type"": ""string"", ""enum"": [""model"", ""project""], ""description"": ""Default model."" }
        },
        ""required"": []
      }
    },
    {
      ""name"": ""xpp_find_deprecations"",
      ""description"": ""Upgrade readiness of a model: (1) every place its code uses a class, table or method marked [SysObsolete], with the attribute's message (often the replacement to use) and whether it is marked as an error (it will stop compiling), read from the cross-reference database; (2) its extensions checked against the installed application: Chain of Command wrappers whose target method is gone, changed its signature (static, return type, parameters) or is no longer wrappable (private, final, Wrappable/Hookable(false), internal); event handlers whose class, method, delegate or table is gone; table, form and enum extensions whose base element, fields, field groups or controls are gone. With previousPackagesFolder (the PackagesLocalDirectory of the version before the update) it also lists wrapped methods whose standard code changed, so behaviour around next may differ. Run it after installing a new version (or on the version you upgrade to) and before building."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""modelName"": { ""type"": ""string"", ""description"": ""The model to check (its package's code is read)."" },
          ""previousPackagesFolder"": { ""type"": ""string"", ""description"": ""Optional: the PackagesLocalDirectory folder of the earlier application version (under %LOCALAPPDATA%/Microsoft/Dynamics365/<version>)."" }
        },
        ""required"": [""modelName""]
      }
    },
    {
      ""name"": ""xpp_analyze_trace"",
      ""description"": ""Analyzes a trace (.etl) taken in the web client (Settings > Trace) or with the trace tools. A trace covers every AOS and session that ran while it was recorded (other users, dual-write and OData integrations, batch). The report lists the AOS instances and the sessions with their user, UTC time range and errors; errors and exceptions with the full X++ call stack (calls to Global::error/checkFailed/warning, and frames unwound by an exception with the method that caught it); platform messages; the slowest requests with their hot call path; methods by exclusive time; SQL by total time; pauses. To investigate one scenario, call it again with aos, session or user (from the session list), method (requests whose call tree has it) and from/to (UTC times such as 20:05:53), then read the methods it names."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""path"": { ""type"": ""string"", ""description"": ""Full path of the .etl file."" },
          ""minRequestMs"": { ""type"": ""number"", ""description"": ""Ignore requests shorter than this when nothing is filtered. Default 50."" },
          ""aos"": { ""type"": ""string"", ""description"": ""Only this AOS (machine name, or part of it)."" },
          ""session"": { ""type"": ""string"", ""description"": ""Only this X++ session id."" },
          ""user"": { ""type"": ""string"", ""description"": ""Only sessions of this user (user id or GUID, or part of it)."" },
          ""method"": { ""type"": ""string"", ""description"": ""Only requests whose call tree has a method containing this text (e.g. SalesOrderHeaderCDSEntity or PriceDisc); shows the path to it."" },
          ""from"": { ""type"": ""string"", ""description"": ""Only requests running at or after this time: UTC time (20:05:50), UTC date and time, or seconds from the start of the trace."" },
          ""to"": { ""type"": ""string"", ""description"": ""Only requests that started at or before this time (same formats as from)."" }
        },
        ""required"": [""path""]
      }
    },
    {
      ""name"": ""xpp_analyze_performance"",
      ""description"": ""Static performance review of the X++ methods of a class, table or form (no environment needed): queries and row-by-row insert/update/delete inside loops, select firstonly inside while select (use a join or exists join), while select of every field, select forupdate without a write, unbalanced ttsbegin/ttscommit, crossCompany queries, and insert_recordset/update_recordset/delete_from that quietly run row by row because the table overrides insert/update/delete, has delete actions or is date-effective. Each finding gives the method, line, why it is slow and the fix. Findings are heuristics: confirm before changing code. Database log and alerts, which also force row-by-row, are environment setup and are not checked. For a data entity (AxDataEntityView) it also checks readiness for OData and data management: public entity and collection names, an entity key, staging table fields missing for entity fields, mandatory fields of the root table that are not mapped, and set-based processing over views, entities or temporary tables."",
      ""inputSchema"": {
        ""type"": ""object"",
        ""properties"": {
          ""objectType"": { ""type"": ""string"", ""enum"": [""AxClass"", ""AxTable"", ""AxForm"", ""AxDataEntityView""] },
          ""objectName"": { ""type"": ""string"" }
        },
        ""required"": [""objectType"", ""objectName""]
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
            if (!Host.ActivityLog.Enabled)
                return DispatchTool(idToken, json, toolName);
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractJsonString(args ?? "{}", "objectType");
            string objectName = JsonHelpers.ExtractJsonString(args ?? "{}", "objectName");
            string snapshot = Host.ActivityLog.IsWrite(toolName) ? Host.ActivityLog.Snapshot(objectType, objectName) : null;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            string response = DispatchTool(idToken, json, toolName);
            bool failed = response == null || response.Contains("\"isError\":true") || response.Contains("\"error\":{");
            Host.ActivityLog.Write(toolName, args, watch.ElapsedMilliseconds, !failed, objectType, objectName, snapshot);
            return response;
        }

        private string DispatchTool(string idToken, string json, string toolName)
        {
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
                case "xpp_find_examples": return HandleFindExamples(idToken, json);
                case "xpp_search_code": return HandleSearchCode(idToken, json);
                case "xpp_analyze_security": return HandleAnalyzeSecurity(idToken, json);
                case "xpp_find_message_source": return HandleFindMessageSource(idToken, json);
                case "xpp_get_guide": return JsonHelpers.BuildToolResult(idToken,
                    AgentGuide.Get("XppVsCode.Server.Guide.txt", JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "topic")));
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
                case "xpp_delete_object": return HandleDeleteObject(idToken, json);
                case "xpp_remove_from_project": return DelegateToBridge(idToken, json, "remove_from_project");
                case "xpp_select_project": return DelegateToBridge(idToken, json, "select_project");
                case "xpp_build": return DelegateToBridge(idToken, json, "build");
                case "xpp_check_best_practice": return DelegateToBridge(idToken, json, "best_practice");
                case "xpp_package": return DelegateToBridge(idToken, json, "create_package");
                case "xpp_diff_changes": return JsonHelpers.BuildToolResult(idToken,
                    ChangedElementsReport.Report(JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "baseBranch")));
                case "xpp_analyze_performance": return HandleAnalyzePerformance(idToken, json);
                case "xpp_find_deprecations": return HandleFindDeprecations(idToken, json);
                case "xpp_analyze_trace": return HandleAnalyzeTrace(idToken, json);

                // ── Local-only tools ──
                case "xpp_search_docs": return _docSearch.Handle(idToken, json);
                case "xpp_list_model_objects": return _modelLister.Handle(idToken, json);

                default:
                    return JsonHelpers.BuildError(idToken, -32602, "Unknown tool: " + toolName);
            }
        }

        private static string HandleAnalyzeTrace(string idToken, string json)
        {
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            string min = JsonHelpers.ExtractJsonValueToken(args, "minRequestMs");
            double minRequestMs = double.TryParse(min, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 50;
            try
            {
                var filter = Host.TraceFilter.Create(key => JsonHelpers.ExtractArgString(args, key));
                return JsonHelpers.BuildToolResult(idToken, Host.TraceService.AnalyzeText(JsonHelpers.ExtractArgString(args, "path"), minRequestMs, filter));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is System.IO.IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException || ex is FormatException)
            {
                return JsonHelpers.BuildToolResult(idToken, ex.Message, isError: true);
            }
        }

        private string HandleFindDeprecations(string idToken, string json)
        {
            string model = JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "modelName");
            string previous = JsonHelpers.ExtractArgString(JsonHelpers.ExtractToolArgumentsObject(json), "previousPackagesFolder");
            if (string.IsNullOrWhiteSpace(model))
                return JsonHelpers.BuildToolResult(idToken, "Pass modelName.", isError: true);
            // The cross-reference database groups code by package; a model's package usually has its name.
            string body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new Dictionary<string, string> { ["modelName"] = model });
            string info = _bridge.IsAvailable() ? _bridge.Call("get_model_info", body) : null;
            string package = (info == null ? null : JsonHelpers.ExtractJsonString(info, "package") ?? JsonHelpers.ExtractJsonString(info, "Package")) ?? model;
            string report = new DeprecationReport(_xref, Examples).Report(package);
            if (_bridge.IsAvailable())
                report += "\n\n" + CompatibilityText(_bridge.Call("extension_compatibility", body))
                    + "\n\n" + CollisionsText(_bridge.Call("extension_collisions", body));
            if (_bridge.IsAvailable() && !string.IsNullOrWhiteSpace(previous))
                report += "\n\n" + DriftText(_bridge.Call("wrapper_drift", new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(
                    new Dictionary<string, string> { ["modelName"] = model, ["previousFolder"] = Environment.ExpandEnvironmentVariables(previous) })));
            return JsonHelpers.BuildToolResult(idToken, report);
        }

        /// <summary>Clashes with other models' extensions of the same elements.</summary>
        internal static string CollisionsText(string resultJson)
        {
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(resultJson);
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return "Collisions with other models: " + (result.TryGetValue("message", out object m) ? m : "not checked") + ".";
            var findings = ((System.Collections.ArrayList)result["findings"]).Cast<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append("Collisions with other models: ").Append(result["extensions"]).Append(" extension(s) compared with the other installed models' extensions of the same elements; ")
              .Append(findings.Count == 0 ? "no clashes." : findings.Count + " finding(s):");
            foreach (var f in findings.Take(200))
                sb.Append("\n- ").Append(f["severity"]).Append(" ").Append(f["rule"]).Append(": ").Append(f["objectName"])
                  .Append(f["member"] is string member && member.Length > 0 ? "." + member : "").Append(": ").Append(f["message"]);
            return sb.ToString();
        }

        /// <summary>Wrapped standard methods whose code changed since the earlier version (names and size of the change; no code).</summary>
        internal static string DriftText(string resultJson)
        {
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(resultJson);
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return "Standard code drift: " + (result.TryGetValue("message", out object m) ? m : "not checked") + ".";
            var changed = ((System.Collections.ArrayList)result["changed"]).Cast<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append("Standard code drift: ").Append(result["wrappers"]).Append(" wrapper(s); ").Append(changed.Count).Append(" target method(s) changed since the earlier version, ")
              .Append(result["unchanged"]).Append(" unchanged").Append(Convert.ToInt32(result["notFound"]) > 0 ? ", " + result["notFound"] + " not found in one of the versions" : "").Append('.');
            foreach (var c in changed.Take(100))
                sb.Append("\n- ").Append(c["className"]).Append('.').Append(c["method"]).Append(" wraps ").Append(c["target"]).Append('.').Append(c["method"])
                  .Append(": ").Append(c["changedLines"]).Append(" line(s) differ; read both versions and check the logic around next.");
            return sb.ToString();
        }

        /// <summary>Readable extension compatibility findings (Chain of Command, handlers, metadata extensions).</summary>
        internal static string CompatibilityText(string resultJson)
        {
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(resultJson);
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return "Extension compatibility: " + (result.TryGetValue("message", out object m) ? m : "not checked") + ".";
            var findings = ((System.Collections.ArrayList)result["findings"]).Cast<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append("Extension compatibility: ").Append(result["wrappers"]).Append(" Chain of Command wrapper(s), ").Append(result["handlers"])
                .Append(" event handler(s) and ").Append(result["extensions"]).Append(" metadata extension(s) checked against the installed code; ")
                .Append(findings.Count == 0 ? "no problems found." : findings.Count + " finding(s):");
            foreach (var f in findings.Take(300))
                sb.Append("\n- ").Append(f["severity"]).Append(" ").Append(f["rule"]).Append(": ").Append(f["objectName"])
                    .Append(f["member"] is string member && member.Length > 0 ? "." + member : "").Append(": ").Append(f["message"]);
            return sb.ToString();
        }

        private string HandleAnalyzePerformance(string idToken, string json)
        {
            string args = JsonHelpers.ExtractToolArgumentsObject(json);
            if (!_bridge.IsAvailable())
                return JsonHelpers.BuildToolResult(idToken, "MetaModel bridge is not available.", isError: true);
            var result = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .Deserialize<Dictionary<string, object>>(_bridge.Call("analyze_performance", args));
            if (!(result.TryGetValue("success", out object ok) && ok is bool success && success))
                return JsonHelpers.BuildToolResult(idToken, result.TryGetValue("message", out object m) ? m as string : "Analysis failed.", isError: true);
            var findings = (result["findings"] as System.Collections.ArrayList ?? new System.Collections.ArrayList()).OfType<Dictionary<string, object>>().ToList();
            var sb = new StringBuilder();
            sb.Append(result["objectName"]).Append(": ").Append(findings.Count).Append(" performance finding(s) in ").Append(result["methods"]).Append(" method(s).");
            foreach (var f in findings.Take(100))
                sb.Append("\n- [").Append(f["severity"]).Append("] ").Append(f["rule"]).Append(" in ").Append(f["method"]).Append(" line ").Append(f["line"])
                  .Append(": ").Append(f["message"]).Append(" Fix: ").Append(f["fix"]);
            if (findings.Count > 100) sb.Append("\n... ").Append(findings.Count - 100).Append(" more.");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string DelegateToBridge(string idToken, string json, string action, Func<string, string> postText = null)
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

            string validation = JsonHelpers.ExtractNestedObject(bridgeResponse, "validation");
            if (isSuccess && !string.IsNullOrEmpty(validation))
            {
                bool valid = ReadBool(validation, "valid");
                sb.AppendLine("Validation: " + (valid ? "PASSED" : "FAILED") + " - " + JsonHelpers.ExtractJsonString(validation, "message"));
                if (!valid)
                {
                    // The object is saved but does not match the request: report it as an error so it is not missed.
                    isError = true;
                    sb.AppendLine("The object was saved, but it does not contain everything requested (see the mismatches above). "
                        + "Fix it with xpp_update_object, or delete it with xpp_delete_object.");
                }
            }

            if (isSuccess && (action == "list_objects" || action == "find_object")
                && ReadObjectArray(bridgeResponse, action == "find_object" ? "matches" : "objects").Count == 0
                && string.IsNullOrEmpty(message))
                sb.AppendLine("0 matches.");
            if (isSuccess && action == "list_project_items" && ReadObjectArray(bridgeResponse, "items").Count == 0)
                sb.AppendLine(ReadBool(bridgeResponse, "hasProject")
                    ? "The active project is empty."
                    : "No active project. Select one with xpp_select_project.");

            string layout = JsonHelpers.ExtractJsonString(bridgeResponse, "layout");
            if (!string.IsNullOrEmpty(layout))
                sb.AppendLine(layout);

            // Append matches/objects/items/labels if present
            AppendArrayField(sb, bridgeResponse, "matches", "objectType", "objectName", "modelName", "isCustom", "label");
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
                else sb.AppendLine("ActiveProject: none. New objects are not added to a project and scope=project builds are unavailable; list and select one with xpp_select_project.");
                if (!string.IsNullOrEmpty(activeModel)) sb.AppendLine("ActiveModel: " + activeModel);
                if (!string.IsNullOrEmpty(layer)) sb.AppendLine("Layer: " + layer);
                foreach (string folder in JsonHelpers.ExtractJsonStringArray(bridgeResponse, "referenceMetadataFolders") ?? new string[0])
                    sb.AppendLine("ReferenceMetadataFolder: " + folder);
            }

            if (action == "get_model_info" && isSuccess)
            {
                foreach (string key in new[] { "name", "displayName", "publisher", "version", "layer", "modelId", "package", "dependencies" })
                {
                    string value = JsonHelpers.ExtractJsonString(bridgeResponse, key)
                        ?? JsonHelpers.ExtractJsonValueToken(bridgeResponse, key);
                    if (!string.IsNullOrEmpty(value))
                        sb.AppendLine(char.ToUpperInvariant(key[0]) + key.Substring(1) + ": " + value);
                }
            }

            string text = sb.ToString().Trim();
            if (string.IsNullOrEmpty(text)) text = isError ? "Operation failed." : "Operation completed.";
            if (postText != null && !isError) text = postText(text);

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

            var wanted = methodNames == null || methodNames.Length == 0 ? null
                : new System.Collections.Generic.HashSet<string>(methodNames, StringComparer.OrdinalIgnoreCase);
            // With methodNames the caller already knows the signatures; listing all of them again only floods the context.
            if (wanted == null)
            {
                sb.AppendLine("\n=== Methods (" + methods.Count + ") ===");
                foreach (var m in methods)
                    sb.AppendLine("  " + (MethodSignature(Str(m, "source")) ?? Str(m, "name")));
            }
            else
                sb.AppendLine("\n=== Methods (requested " + wanted.Count + " of " + methods.Count + ") ===");
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
                if (sig.Length == 0 && (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("[", StringComparison.Ordinal)
                    || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith("*", StringComparison.Ordinal)))
                    continue;
                if (line.StartsWith("{", StringComparison.Ordinal)) break;
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
            string readType = JsonHelpers.ExtractJsonString(bridgeResponse, "objectType") ?? "";
            bool compositeQuery = readType == "AxQuery"
                && !string.IsNullOrEmpty(JsonHelpers.ExtractJsonString(JsonHelpers.ExtractNestedObject(bridgeResponse, "properties") ?? "{}", "Query"));
            if (TypesWithDedicatedOutput.Contains(readType) && !compositeQuery) return;

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

        private string HandleDeleteObject(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName");
            if (string.IsNullOrEmpty(objectType) || string.IsNullOrEmpty(objectName))
                return JsonHelpers.BuildToolResult(idToken, "Parameters 'objectType' and 'objectName' are required.", isError: true);

            string note = "";
            // Ownership and existence come first, so a standard object is refused for that reason, not for its where-used.
            string check = _bridge.Call("delete_object", "{\"objectType\":\"" + JsonHelpers.EscapeJsonString(objectType)
                + "\",\"objectName\":\"" + JsonHelpers.EscapeJsonString(objectName) + "\",\"checkOnly\":\"true\"}");
            if (!ReadBool(check, "success"))
                return JsonHelpers.BuildToolResult(idToken, JsonHelpers.ExtractJsonString(check, "message") ?? "Delete failed.", isError: true);

            if (!ReadBool(argsJson, "force"))
            {
                string refs = _xref.QueryJson(objectType, objectName, null, 25);
                var users = ReadObjectArray(refs, "references")
                    .Where(r => !(string.Equals(Str(r, "objectType"), objectType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(Str(r, "objectName"), objectName, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => Str(r, "objectType") + " " + Str(r, "objectName"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (users.Count > 0)
                    return JsonHelpers.BuildToolResult(idToken, objectType + " '" + objectName + "' is still referenced by "
                        + users.Count + " object(s): " + string.Join(", ", users) + ". Not deleted. Remove the references first, or pass force=true.", isError: true);
                if (!ReadBool(refs, "success"))
                    note = "\n(Where-used could not be checked: " + JsonHelpers.ExtractJsonString(refs, "message") + ")";
            }

            string response = _bridge.Call("delete_object", argsJson);
            bool deleted = ReadBool(response, "success");
            if (deleted)
            {
                string xrefNote = _xref.RemoveObject(objectType, objectName);
                if (xrefNote != null) note += "\n" + xrefNote;
            }
            return JsonHelpers.BuildToolResult(idToken, (JsonHelpers.ExtractJsonString(response, "message") ?? "Delete failed.") + note,
                isError: !deleted);
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
            {
                var sections = JsonHelpers.ExtractJsonStringArray(argsJson, "sections")
                    ?? JsonHelpers.ExtractArgString(argsJson, "sections")?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                bool methodsAsked = (JsonHelpers.ExtractJsonStringArray(argsJson, "methodNames")?.Length ?? 0) > 0;
                return DelegateToBridge(idToken, json, "read_object", text => SelectReadSections(text, sections, methodsAsked));
            }

            return JsonHelpers.BuildToolResult(idToken,
                "Either (objectType + objectName) or filePath is required.", isError: true);
        }

        // Reads above this size return the small sections and an index of the large ones (#45).
        private const int ReadSummaryThreshold = 25000;
        private const int SummaryInlineSectionChars = 3000;

        private sealed class ReadSection
        {
            public string Title;
            public string Key;
            public string Body;
        }

        /// <summary>
        /// Filters a formatted read result to the requested sections ("=== Title ===" blocks). Without sections, a result
        /// larger than ReadSummaryThreshold keeps its small sections and lists the large ones with their size.
        /// </summary>
        private static string SelectReadSections(string text, string[] wanted, bool methodsAsked)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            var header = new StringBuilder();
            var sections = new System.Collections.Generic.List<ReadSection>();
            foreach (string line in lines)
            {
                if (line.StartsWith("=== ", StringComparison.Ordinal) && line.EndsWith(" ===", StringComparison.Ordinal))
                {
                    string title = line.Substring(4, line.Length - 8);
                    string key = new string(title.Split('(')[0].Where(char.IsLetter).ToArray()).ToLowerInvariant();
                    if (key == "metadatatypedmetadata") key = "typedmetadata";
                    if (key.StartsWith("entityfields", StringComparison.Ordinal)) key = "entityfields";
                    sections.Add(new ReadSection { Title = title, Key = key, Body = "" });
                }
                else if (sections.Count == 0) header.AppendLine(line);
                else sections[sections.Count - 1].Body += line + "\n";
            }

            var requested = (wanted ?? new string[0]).Select(w => new string(w.Where(char.IsLetter).ToArray()).ToLowerInvariant())
                .Where(w => w.Length > 0).ToList();
            if (methodsAsked && requested.Count > 0 && !requested.Contains("methods")) requested.Add("methods");
            if (methodsAsked && requested.Count == 0 && text.Length > ReadSummaryThreshold) requested.Add("methods");
            if (requested.Contains("all") || sections.Count == 0 || (requested.Count == 0 && text.Length <= ReadSummaryThreshold))
                return text;

            string Describe(ReadSection sec)
            {
                string body = sec.Body.Trim();
                int items = body.StartsWith("[", StringComparison.Ordinal) ? CountTopLevelObjects(body) : -1;
                return sec.Key + " (" + sec.Title + "): " + body.Length + " chars" + (items >= 0 ? ", " + items + " item(s)" : "");
            }

            var sb = new StringBuilder(header.ToString().TrimEnd()).AppendLine();
            if (requested.Count == 0 || requested.Contains("summary"))
            {
                bool inlineSmall = requested.Count == 0;
                sb.AppendLine();
                sb.AppendLine(inlineSmall
                    ? "Large object (" + text.Length + " chars): small sections are shown in full, large ones only listed. Call again with sections=[...] (keys below) or sections=[\"all\"]."
                    : "Sections (call again with sections=[...]):");
                foreach (var sec in sections.Where(x => !inlineSmall || x.Body.Trim().Length > SummaryInlineSectionChars))
                    sb.AppendLine("  " + Describe(sec));
                if (inlineSmall)
                    foreach (var sec in sections.Where(x => x.Body.Trim().Length <= SummaryInlineSectionChars))
                        sb.AppendLine().AppendLine("=== " + sec.Title + " ===").Append(sec.Body.TrimEnd()).AppendLine();
                return sb.ToString().TrimEnd();
            }

            var unknown = requested.Where(r => !sections.Any(x => x.Key == r || x.Key.StartsWith(r, StringComparison.Ordinal))).ToList();
            foreach (var sec in sections.Where(x => requested.Any(r => x.Key == r || x.Key.StartsWith(r, StringComparison.Ordinal))))
                sb.AppendLine().AppendLine("=== " + sec.Title + " ===").Append(sec.Body.TrimEnd()).AppendLine();
            if (unknown.Count > 0)
                sb.AppendLine().AppendLine("Not in this object's output: " + string.Join(", ", unknown) + ". Available sections: "
                    + string.Join(", ", sections.Select(x => x.Key)) + ".");
            return sb.ToString().TrimEnd();
        }

        private static int CountTopLevelObjects(string json)
        {
            int depth = 0, count = 0;
            bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '[' || c == '{') { if (depth == 1 && c == '{') count++; depth++; }
                else if (c == ']' || c == '}') depth--;
            }
            return count;
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

            if (ArgFlag(argsJson, "includeCode"))
            {
                string withCode = ReferencesWithCode(argsJson, objectType, objectName);
                if (withCode != null) return JsonHelpers.BuildToolResult(idToken, withCode);
            }

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

        /// <summary>Where-used with the code lines that use the target (how existing code calls the API).</summary>
        private string ReferencesWithCode(string argsJson, string objectType, string objectName)
        {
            string member = JsonHelpers.ExtractArgString(argsJson, "memberName");
            int max = int.TryParse(JsonHelpers.ExtractArgString(argsJson, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 25)) : 15;
            var parsed = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue }
                .DeserializeObject(_xref.QueryJson(objectType, objectName, member, max * 4, JsonHelpers.ExtractArgString(argsJson, "referenceKind"))) as Dictionary<string, object>;
            if (parsed == null || !parsed.TryGetValue("success", out object ok) || !(ok is bool okFlag) || !okFlag) return null;
            var refs = (parsed["references"] as object[] ?? new object[0]).Cast<Dictionary<string, object>>().ToList();
            string token = string.IsNullOrEmpty(member) ? objectName : member;
            var tokenRx = new System.Text.RegularExpressions.Regex(@"\b" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            // Members: accessed as .member / ::member, not a same-named enum or EDT used as Name::Value.
            var strictRx = string.IsNullOrEmpty(member) ? tokenRx : new System.Text.RegularExpressions.Regex(
                @"(\.|::)\s*" + System.Text.RegularExpressions.Regex.Escape(token) + @"\b(?!\s*::)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var sb = new StringBuilder();
            sb.AppendLine("Code using " + objectName + (string.IsNullOrEmpty(member) ? "" : "." + member) + " (" + refs.Count + (parsed.ContainsKey("more") && parsed["more"] is bool more && more ? "+" : "") + " references; showing up to " + max + " places):");
            var sourceCache = new Dictionary<string, List<KeyValuePair<string[], string>>>(StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int shown = 0;
            foreach (var r in refs)
            {
                string type = r.TryGetValue("objectType", out object t) ? t as string : null;
                string name = r.TryGetValue("objectName", out object n) ? n as string : null;
                var path = (r.TryGetValue("memberPath", out object mp) ? mp as object[] : null)?.Select(x => x as string == "DataFields" ? "Fields" : x as string).ToArray() ?? new string[0];
                if (type == null || name == null || !seen.Add(type + "/" + name + "/" + string.Join("/", path))) continue;
                if (!sourceCache.TryGetValue(type + "/" + name, out var sources)) sourceCache[type + "/" + name] = sources = CodeSearch.SourcesOf(type, name);
                var source = sources?.FirstOrDefault(sx => path.Length >= 2 && sx.Key.Length >= 2
                    && string.Equals(sx.Key[sx.Key.Length - 1], path[path.Length - 1], StringComparison.OrdinalIgnoreCase)
                    && (path.Length == 2 || sx.Key.Length < 3 || string.Equals(sx.Key[sx.Key.Length - 3], path[path.Length - 3], StringComparison.OrdinalIgnoreCase)));
                var lines = source?.Value?.Replace("\r\n", "\n").Split('\n');
                var hits = lines == null ? new List<int>() : Enumerable.Range(0, lines.Length).Where(i => strictRx.IsMatch(lines[i])).Take(2).ToList();
                if (hits.Count == 0 && lines != null)
                    hits = Enumerable.Range(0, lines.Length).Where(i => tokenRx.IsMatch(lines[i])).Take(2).ToList();
                sb.AppendLine("- " + type + " " + name + (path.Length > 0 ? " / " + string.Join("/", path) : "") + " [" + (r.TryGetValue("kind", out object k) ? k : "") + "]");
                foreach (int i in hits)
                    sb.AppendLine("    line " + (i + 1) + ": " + lines[i].Trim());
                if (++shown >= max) break;
            }
            sb.AppendLine("Read a whole method with xpp_read_object (methodNames) when you want to copy or adapt it.");
            return sb.ToString();
        }

        // Booleans arrive as JSON tokens (true) or, from some clients, as strings ("true").
        private static bool ArgFlag(string argsJson, string key) =>
            "true".Equals(JsonHelpers.ExtractJsonValueToken(argsJson, key) ?? JsonHelpers.ExtractArgString(argsJson, key), StringComparison.OrdinalIgnoreCase);

        private string HandleFindExamples(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string query = JsonHelpers.ExtractArgString(argsJson, "query");
            string objectType = JsonHelpers.ExtractArgString(argsJson, "objectType");
            string objectName = JsonHelpers.ExtractArgString(argsJson, "objectName");
            if (string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(objectName))
                return JsonHelpers.BuildToolResult(idToken, "Give a query (what you need to implement) and/or objectType + objectName (an object to resemble).", isError: true);
            int max = int.TryParse(JsonHelpers.ExtractArgString(argsJson, "maxResults"), out int m) ? m : 15;
            var result = Examples.Find(query, objectType, objectName, max);
            if (!(result["success"] is bool ok && ok)) return JsonHelpers.BuildToolResult(idToken, result["message"] as string ?? "Search failed.", isError: true);
            var items = (List<Dictionary<string, object>>)result["items"];
            var sb = new StringBuilder();
            sb.AppendLine("Standard examples: " + result["interpretation"]);
            if (items.Count == 0) sb.AppendLine("(none found; rephrase the query with framework words, or give an object to resemble)");
            foreach (var i in items)
                sb.AppendLine("- " + i["objectType"] + " " + i["objectName"] + (i["model"] != null ? " [" + i["model"] + "]" : "") + " - " + i["reason"]);
            sb.AppendLine("Next: xpp_read_object the closest one or two, then reuse (call), extend (CoC / events) or, if neither is possible, copy and adapt; name the precedent in your plan.");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string HandleFindMessageSource(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            int Num(string key, int fallback) => int.TryParse(JsonHelpers.ExtractArgString(argsJson, key), out int v) ? v : fallback;
            var r = Messages.Trace(JsonHelpers.ExtractArgString(argsJson, "message", "text"), JsonHelpers.ExtractArgString(argsJson, "language"),
                Num("maxLabels", 5), Num("maxResults", 15));
            if (!(r["success"] is bool ok && ok)) return JsonHelpers.BuildToolResult(idToken, r["message"] as string, isError: true);
            var sb = new StringBuilder();
            var labels = (List<Dictionary<string, object>>)r["labels"];
            var literal = (List<Dictionary<string, object>>)r["literal"];
            string Where(Dictionary<string, object> u) => u["objectType"] == null ? "metadata " + u["objectName"]
                : u["objectType"] + " " + u["objectName"] + (((string[])u["memberPath"]).Length > 0 ? " / " + string.Join("/", (string[])u["memberPath"]) : "")
                  + " line " + u["line"] + (u["text"] is string t && t.Length > 0 ? ": " + t : "");
            if (labels.Count == 0) sb.AppendLine("No " + r["language"] + " label text matches this message.");
            foreach (var l in labels)
            {
                var usages = (List<Dictionary<string, object>>)l["usages"];
                var ids = (List<string>)l["labels"];
                sb.AppendLine(string.Join(", ", ids.Take(6)) + (ids.Count > 6 ? " (+" + (ids.Count - 6) + " more with the same text)" : "")
                    + " \"" + l["text"] + "\"" + ((bool)l["whole"] ? " (the whole message)" : " (part of the message)") + ":");
                if ((int)l["usageCount"] < 0) sb.AppendLine("  (where-used unavailable: " + r["xrefProblem"] + ")");
                else if (usages.Count == 0) sb.AppendLine("  not used in X++ or metadata of the indexed packages: the platform (kernel) raises it, or its package was not built with cross-reference data.");
                foreach (var u in usages) sb.AppendLine("  - " + Where(u));
            }
            if (literal.Count > 0)
            {
                sb.AppendLine("Code with the message as literal text:");
                foreach (var u in literal) sb.AppendLine("  - " + Where(u));
            }
            sb.AppendLine("Read the raising method with xpp_read_object (methodNames) to see the condition; the caller chain is in xpp_find_references.");
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
        }

        private string HandleAnalyzeSecurity(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string op = JsonHelpers.ExtractArgString(argsJson, "op");
            var result = _security.Handle(op, k => JsonHelpers.ExtractArgString(argsJson, k),
                k => JsonHelpers.ExtractJsonStringArray(argsJson, k), k => ArgFlag(argsJson, k));
            if (!(result.TryGetValue("success", out object ok) && ok is bool okFlag && okFlag))
                return JsonHelpers.BuildToolResult(idToken, result.TryGetValue("message", out object m) ? m as string : "Failed.", isError: true);
            if (op == "report") return JsonHelpers.BuildToolResult(idToken, result["text"] as string);
            return JsonHelpers.BuildToolResult(idToken, SecurityText(op, result));
        }

        /// <summary>Readable text of a security analysis result for the agent.</summary>
        private static string SecurityText(string op, Dictionary<string, object> r)
        {
            var sb = new StringBuilder();
            switch (op)
            {
                case "license":
                    sb.AppendLine(r["kind"] + " " + r["name"] + " requires license: " + r["license"]);
                    sb.AppendLine("Entry points by license: " + string.Join(", ", ((Dictionary<string, object>)r["byLicense"]).Select(kv => kv.Key + " " + kv.Value))
                        + "; " + r["duties"] + " duties, " + r["privileges"] + " privileges.");
                    var drivers = (List<Dictionary<string, object>>)r["drivers"];
                    if (drivers.Count > 0)
                    {
                        sb.AppendLine("Menu items that require " + r["license"] + " (" + r["driverCount"] + "):");
                        foreach (var d in drivers.Take(30))
                            sb.AppendLine("- " + d["menuItem"] + " [" + d["type"] + ", " + d["grant"] + "; view " + d["viewLicense"] + " / maintain " + d["maintainLicense"] + "] via " + string.Join("; ", (List<string>)d["via"]));
                    }
                    var hints = (List<string>)r["lowerWithReadOnly"];
                    if (hints.Count > 0) { sb.AppendLine("Read-only would lower the license for:"); foreach (string h in hints) sb.AppendLine("- " + h); }
                    var missing = (List<string>)r["missing"];
                    if (missing.Count > 0) sb.AppendLine("Missing objects: " + string.Join("; ", missing));
                    break;
                case "access":
                    foreach (var m in (List<Dictionary<string, object>>)r["menuItems"])
                        sb.AppendLine("Menu item " + m["type"] + " " + m["name"] + ": view " + m["viewLicense"] + ", maintain " + m["maintainLicense"]);
                    var viaForms = r.TryGetValue("viaForms", out object vf) ? (List<string>)vf : new List<string>();
                    if (viaForms.Count > 0) sb.AppendLine("Through the forms using this table: " + string.Join(", ", viaForms.Take(40)) + (viaForms.Count > 40 ? ", ..." : ""));
                    var rows = (List<Dictionary<string, object>>)r["privileges"];
                    sb.AppendLine(rows.Count + " privilege(s), " + r["roleCount"] + " role(s) reach " + r["objectType"] + " " + r["name"] + ":");
                    foreach (var p in rows.Take(80))
                        sb.AppendLine("- " + p["privilege"] + " [" + p["model"] + "] " + p["grant"] + "; duties: " + string.Join(", ", (List<string>)p["duties"])
                            + "; roles: " + string.Join(", ", ((List<string>)p["roles"]).Take(15)) + (((List<string>)p["roles"]).Count > 15 ? ", ..." : ""));
                    break;
                case "placement":
                    sb.AppendLine("Standard roles for this duty (related objects: " + string.Join(", ", (List<string>)r["related"]) + "):");
                    foreach (var role in (List<Dictionary<string, object>>)r["roles"])
                        sb.AppendLine("- " + role["name"] + " '" + role["label"] + "' [" + role["model"] + ", license " + role["license"] + ", score " + role["score"] + "]: "
                            + string.Join("; ", ((List<string>)role["reasons"]).Take(4)));
                    sb.AppendLine("Extend the role(s) that do the job with a role extension (Role.YourSuffix) that adds the duty; if the requirement's role "
                        + "name has no standard role, state the mapping as an assumption in your summary. Check the license with op=license afterwards.");
                    break;
                case "gaps":
                    var findings = (List<Dictionary<string, object>>)r["findings"];
                    sb.AppendLine(findings.Count + " security finding(s) in " + string.Join(", ", (List<string>)r["models"]) + (findings.Count == 0 ? "." : ":"));
                    foreach (var f in findings.Take(150)) sb.AppendLine("- " + f["severity"] + " " + f["kind"] + " " + f["objectType"] + " " + f["name"] + ": " + f["message"]);
                    break;
                case "sod":
                    var conflicts = (List<Dictionary<string, object>>)r["conflicts"];
                    sb.AppendLine("Segregation of duties: " + r["rulesChecked"] + " rule(s) checked on " + ((List<string>)r["roles"]).Count + " role(s); " + conflicts.Count + " conflict(s).");
                    foreach (var c in conflicts) sb.AppendLine("- " + c["severity"] + " " + c["role"] + ": " + c["rule"] + " (" + c["first"] + " + " + c["second"] + "): " + c["reason"]);
                    var skipped = (List<string>)r["rulesSkipped"];
                    if (skipped.Count > 0) sb.AppendLine("Rules skipped (duties not in this version): " + string.Join("; ", skipped));
                    break;
                default:
                    sb.Append(new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(r));
                    break;
            }
            return sb.ToString();
        }

        private string HandleSearchCode(string idToken, string json)
        {
            string argsJson = JsonHelpers.ExtractToolArgumentsObject(json);
            string pattern = JsonHelpers.ExtractArgString(argsJson, "pattern");
            bool regex = ArgFlag(argsJson, "regex");
            bool caseSensitive = ArgFlag(argsJson, "caseSensitive");
            int max = int.TryParse(JsonHelpers.ExtractArgString(argsJson, "maxResults"), out int m) ? Math.Max(1, Math.Min(m, 200)) : 50;
            var types = JsonHelpers.ExtractJsonStringArray(argsJson, "objectTypes");
            var result = _codeSearch.Search(pattern, regex, caseSensitive, types, JsonHelpers.ExtractArgString(argsJson, "model"), max);
            if (result.Error != null && result.Hits.Count == 0) return JsonHelpers.BuildToolResult(idToken, result.Error, isError: true);
            var sb = new StringBuilder();
            sb.AppendLine(result.Hits.Count + (result.Truncated ? "+" : "") + " match(es) for '" + pattern + "' (" + result.Scanned + " files, " + result.Ms + " ms)"
                + (result.Truncated ? "; stopped at the first matches found by the parallel scan (which ones, and the file count, vary between calls); "
                    + "more exist: narrow with objectTypes, model (a model or package) or a more specific pattern" : "") + ":");
            foreach (var h in result.Hits)
                sb.AppendLine("- " + h.ObjectType + " " + h.ObjectName + " [" + h.Model + "] " + string.Join("/", h.MemberPath) + " line " + h.Line + ": " + h.Text);
            if (result.Error != null) sb.AppendLine(result.Error);
            return JsonHelpers.BuildToolResult(idToken, sb.ToString());
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
            bool hasList = argsJson != null && argsJson.Contains("\"labels\"");

            if (string.IsNullOrEmpty(labelFileId) || (!hasList && (string.IsNullOrEmpty(labelId) || string.IsNullOrEmpty(text))))
                return JsonHelpers.BuildToolResult(idToken,
                    "Parameter 'labelFileId' is required, plus either 'labels' (array of {labelId, text, comment?, translations?}) or 'labelId' and 'text'.", isError: true);

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
            AppendJsonArray(sb, json, "entityFields", "Entity fields (name, dataSource, dataField)");
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
