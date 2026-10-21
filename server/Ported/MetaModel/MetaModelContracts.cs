using System.Collections.Generic;

namespace XppVsCode.Server.MetaModel
{
    // ── Metadata sub-DTOs ──

    public sealed class EnumValueDto
    {
        public string Name { get; set; }
        public int Value { get; set; }
        public string Label { get; set; }
    }

    public sealed class FieldDto
    {
        public string Name { get; set; }
        public string FieldType { get; set; }
        public string ExtendedDataType { get; set; }
        public string EnumType { get; set; }
        public string Label { get; set; }
    }

    public sealed class IndexDto
    {
        public string Name { get; set; }
        public bool AllowDuplicates { get; set; }
        // False when the caller did not pass allowDuplicates; validation then skips the check.
        public bool AllowDuplicatesSpecified { get; set; }
        public List<string> Fields { get; set; } = new List<string>();
    }

    public sealed class FieldGroupDto
    {
        public string Name { get; set; }
        public string Label { get; set; }
        public List<string> Fields { get; set; } = new List<string>();
    }

    public sealed class RelationConstraintDto
    {
        public string Field { get; set; }
        public string RelatedField { get; set; }
    }

    public sealed class RelationDto
    {
        public string Name { get; set; }
        public string RelatedTable { get; set; }
        public List<RelationConstraintDto> Constraints { get; set; } = new List<RelationConstraintDto>();
    }

    public sealed class EntryPointDto
    {
        public string Name { get; set; }
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string Grant { get; set; }
    }

    public sealed class DataEntityPermissionDto
    {
        public string Name { get; set; }
        public string Grant { get; set; }
        public string IntegrationMode { get; set; }
    }

    // A field added by a data entity extension, mapped to a field of one of the entity's data sources.
    public sealed class EntityFieldDto
    {
        public string Name { get; set; }
        public string DataSource { get; set; }
        public string DataField { get; set; }
        public string Label { get; set; }
        public bool? Mandatory { get; set; }
    }

    public sealed class QueryRangeDto
    {
        public string Field { get; set; }
        public string Value { get; set; }
        public string Name { get; set; }
    }

    public sealed class QueryDataSourceDto
    {
        public string Name { get; set; }
        public string Table { get; set; }
        public string ParentDataSource { get; set; }
        public string JoinMode { get; set; }
        public string LinkType { get; set; }
        public bool? DynamicFields { get; set; }
        public bool? Relations { get; set; }
        public bool? FirstOnly { get; set; }
        public List<QueryRangeDto> Ranges { get; set; } = new List<QueryRangeDto>();
        public List<QueryDataSourceDto> ChildDataSources { get; set; } = new List<QueryDataSourceDto>();
    }

    // A control added by a form extension. Only top-level controls carry placement
    // (Parent/Position/PreviousSibling); nested Controls are placed inside their container.
    public sealed class FormControlDto
    {
        public string Name { get; set; }
        public string ControlType { get; set; }
        public string Parent { get; set; }
        public string Position { get; set; }
        public string PreviousSibling { get; set; }
        public string DataSource { get; set; }
        public string DataField { get; set; }
        public string DataGroup { get; set; }
        public string Label { get; set; }
        public Dictionary<string, string> Properties { get; set; }
        public List<FormControlDto> Controls { get; set; } = new List<FormControlDto>();
    }

    public sealed class ControlModificationDto
    {
        public string Name { get; set; }
        public Dictionary<string, string> Properties { get; set; }
    }

    public sealed class MenuElementDto
    {
        public string Name { get; set; }
        public string MenuItemName { get; set; }
        public string MenuItemType { get; set; }
        public string Parent { get; set; }
        public string Position { get; set; }
        public string PreviousSibling { get; set; }
    }

    // ── Request DTOs ──

    public sealed class CreateObjectRequest
    {
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string TypedMetadataJson { get; set; }
        public string Declaration { get; set; }
        public string[] Methods { get; set; }
        public string ModelName { get; set; }
        public bool FormatCode { get; set; } = true;

        // Strongly-typed metadata
        public Dictionary<string, string> Properties { get; set; }
        public List<EnumValueDto> EnumValues { get; set; }
        public List<FieldDto> Fields { get; set; }
        public List<IndexDto> Indexes { get; set; }
        public List<FieldGroupDto> FieldGroups { get; set; }
        public List<RelationDto> Relations { get; set; }
        public List<EntryPointDto> EntryPoints { get; set; }
        public List<QueryDataSourceDto> DataSources { get; set; }
        public List<FormControlDto> Controls { get; set; }
        public List<ControlModificationDto> ControlModifications { get; set; }
        public List<string> Privileges { get; set; }
        public List<string> Duties { get; set; }
        public List<DataEntityPermissionDto> DataEntityPermissions { get; set; }
        public List<EntityFieldDto> EntityFields { get; set; }
        public bool SkipStagingTable { get; set; }
        public List<MenuElementDto> MenuElements { get; set; }

        /// <summary>
        /// Convert to an UpdateObjectRequest so Create can delegate metadata
        /// application to the Update path (single code path).
        /// </summary>
        public UpdateObjectRequest ToUpdateRequest()
        {
            return new UpdateObjectRequest
            {
                ObjectType = ObjectType,
                ObjectName = ObjectName,
                TypedMetadataJson = TypedMetadataJson,
                Declaration = Declaration,
                Methods = Methods,
                FormatCode = FormatCode,
                Properties = Properties,
                EnumValues = EnumValues,
                Fields = Fields,
                Indexes = Indexes,
                FieldGroups = FieldGroups,
                Relations = Relations,
                EntryPoints = EntryPoints,
                DataSources = DataSources,
                Controls = Controls,
                ControlModifications = ControlModifications,
                Privileges = Privileges,
                Duties = Duties,
                DataEntityPermissions = DataEntityPermissions,
                EntityFields = EntityFields,
                SkipStagingTable = SkipStagingTable,
                MenuElements = MenuElements
            };
        }
    }

    public sealed class UpdateObjectRequest
    {
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string TypedMetadataJson { get; set; }
        public string Declaration { get; set; }
        public string[] Methods { get; set; }
        public string[] RemoveMethodNames { get; set; }
        public bool FormatCode { get; set; } = true;

        // Strongly-typed metadata
        public Dictionary<string, string> Properties { get; set; }
        public List<EnumValueDto> EnumValues { get; set; }
        public List<FieldDto> Fields { get; set; }
        public List<IndexDto> Indexes { get; set; }
        public List<FieldGroupDto> FieldGroups { get; set; }
        public List<RelationDto> Relations { get; set; }
        public List<EntryPointDto> EntryPoints { get; set; }
        public List<QueryDataSourceDto> DataSources { get; set; }
        public List<FormControlDto> Controls { get; set; }
        public List<ControlModificationDto> ControlModifications { get; set; }
        public List<string> Privileges { get; set; }
        public List<string> Duties { get; set; }
        public List<DataEntityPermissionDto> DataEntityPermissions { get; set; }
        public List<EntityFieldDto> EntityFields { get; set; }
        public bool SkipStagingTable { get; set; }
        public List<MenuElementDto> MenuElements { get; set; }
        // Entry points, privileges, duties or data entity permissions to remove, by name.
        public List<string> RemoveSecurityItems { get; set; }
    }

    // ── Result DTOs ──

    public sealed class MetaModelResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string FilePath { get; set; }
    }

    public sealed class ReadObjectResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string TypedMetadataJson { get; set; }
        public string Declaration { get; set; }
        public List<MethodInfo> Methods { get; set; } = new List<MethodInfo>();
        public string FilePath { get; set; }
        public bool IsCustom { get; set; }
        public string ModelName { get; set; }

        // Strongly-typed metadata (same shape as create/update for round-trip)
        public Dictionary<string, string> Properties { get; set; }
        public List<EnumValueDto> EnumValues { get; set; }
        public List<FieldDto> Fields { get; set; }
        public List<IndexDto> Indexes { get; set; }
        public List<FieldGroupDto> FieldGroups { get; set; }
        public List<RelationDto> Relations { get; set; }
        public List<EntryPointDto> EntryPoints { get; set; }
        public List<QueryDataSourceDto> DataSources { get; set; }
        public List<FormControlDto> Controls { get; set; }
        public List<ControlModificationDto> ControlModifications { get; set; }
        public List<EntityFieldDto> EntityFields { get; set; }
    }

    public sealed class MethodInfo
    {
        public string Name { get; set; }
        public string Source { get; set; }
    }

    public sealed class FindObjectResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<FoundObject> Matches { get; set; } = new List<FoundObject>();
    }

    public sealed class FoundObject
    {
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string ModelName { get; set; }
        public string PackageName { get; set; }
        public string FilePath { get; set; }
        public bool IsCustom { get; set; }
    }

    public sealed class ListObjectsResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<FoundObject> Objects { get; set; } = new List<FoundObject>();
    }

    public sealed class ModelInfoResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string Layer { get; set; }
        public long ModelId { get; set; }
        public string Package { get; set; }
        public List<string> Dependencies { get; set; } = new List<string>();
        public Dictionary<string, int> ObjectCounts { get; set; } = new Dictionary<string, int>();
    }

    public sealed class ModelSummary
    {
        public string Name { get; set; }
        public string DisplayName { get; set; }
        public string PackageName { get; set; }
        public bool IsCustom { get; set; }
    }

    public sealed class LabelResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public List<LabelEntry> Labels { get; set; } = new List<LabelEntry>();
    }

    public sealed class LabelEntry
    {
        public string Id { get; set; }
        public string Text { get; set; }
        public string Comment { get; set; }
    }

    public sealed class ProjectItemInfo
    {
        public string Name { get; set; }
        public string ObjectType { get; set; }
        public string FilePath { get; set; }
    }

    public sealed class EnvironmentInfo
    {
        public string CustomMetadataFolder { get; set; }
        public List<string> ReferenceMetadataFolders { get; set; } = new List<string>();
        public string ActiveProjectName { get; set; }
        public string ActiveModelName { get; set; }
        public long ActiveModelId { get; set; }
        public string ActiveModelLayer { get; set; }
    }

    public sealed class ValidateObjectRequest
    {
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string TypedMetadataJson { get; set; }
        public Dictionary<string, string> Properties { get; set; }
        public List<EnumValueDto> EnumValues { get; set; }
        public List<FieldDto> Fields { get; set; }
        public List<IndexDto> Indexes { get; set; }
        public List<RelationDto> Relations { get; set; }
        public List<FieldGroupDto> FieldGroups { get; set; }
        public List<EntryPointDto> EntryPoints { get; set; }
        public List<QueryDataSourceDto> DataSources { get; set; }
        public List<FormControlDto> Controls { get; set; }
    }

    public sealed class ValidateObjectResult
    {
        public bool Valid { get; set; }
        public bool Exists { get; set; }
        public bool InProject { get; set; }
        public string ObjectType { get; set; }
        public string ObjectName { get; set; }
        public string ModelName { get; set; }
        public string Message { get; set; }
        public List<string> Mismatches { get; set; } = new List<string>();
    }

    public sealed class ObjectTypeSchemaResult
    {
        public bool Success { get; set; }
        public string ObjectType { get; set; }
        public string SchemaJson { get; set; }
        public string Message { get; set; }
    }

    public sealed class FormLayoutResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Layout { get; set; }
    }

    public sealed class CrossReferenceItem
    {
        /// <summary>Raw XRef path, e.g. "/Classes/BankStatementImportModule/Methods/import"</summary>
        public string SourcePath { get; set; }
        /// <summary>Converted to AxClass/AxTable/etc. when recognised</summary>
        public string SourceObjectType { get; set; }
        public string SourceObjectName { get; set; }
        /// <summary>Method or member name when the source is a member (e.g. a method call site)</summary>
        public string SourceMember { get; set; }
        /// <summary>TypeReference, MethodCall, ClassExtended, InterfaceImplementation, etc.</summary>
        public string Kind { get; set; }
        public int LineNumber { get; set; }
        public string SourceModule { get; set; }
    }

    public sealed class FindReferencesResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string TargetPath { get; set; }
        public List<CrossReferenceItem> References { get; set; } = new List<CrossReferenceItem>();
    }

    public sealed class ProxyGenerationRequest
    {
        public List<string> ObjectTypes { get; set; } = new List<string>();
        public string Namespace { get; set; }
        public string OutputFilePath { get; set; }
        public bool IncludeComments { get; set; } = true;
    }

    public sealed class ProxyGenerationResult
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string Namespace { get; set; }
        public string OutputFilePath { get; set; }
        public string GeneratedCode { get; set; }
        public List<string> GeneratedTypes { get; set; } = new List<string>();
    }
}
