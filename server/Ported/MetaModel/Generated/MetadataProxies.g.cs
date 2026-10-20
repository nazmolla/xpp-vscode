using System;
using System.Collections.Generic;

namespace XppVsCode.Server.MetaModel.Generated
{
    /// <summary>
    /// Auto-generated static proxy contracts for D365FO metadata types.
    /// Generated from FinOps metadata assemblies in Samples.
    /// </summary>
    public static class MetadataProxyManifest
    {
        public static readonly string[] RootObjectTypes = new[]
        {
            "AxClass",
            "AxTable",
            "AxForm",
            "AxEdt",
            "AxEnum",
            "AxView",
            "AxQuery",
            "AxMenu",
            "AxTile",
            "AxMenuItemDisplay",
            "AxMenuItemOutput",
            "AxMenuItemAction",
            "AxDataEntityView",
            "AxSecurityPrivilege",
            "AxSecurityDuty",
            "AxSecurityRole",
            "AxService",
            "AxServiceGroup",
            "AxConfigurationKey",
            "AxTableExtension",
            "AxFormExtension",
            "AxEnumExtension",
            "AxEdtExtension",
            "AxViewExtension",
            "AxMenuExtension",
            "AxMenuItemDisplayExtension",
            "AxMenuItemOutputExtension",
            "AxMenuItemActionExtension",
            "AxQuerySimpleExtension",
            "AxSecurityDutyExtension",
            "AxSecurityRoleExtension"
        };
    }

    public sealed class AxAttributeParameterProxy
    {
        public List<AxAttributeParameterProxy> AdditionalData { get; set; }
        public string Tags { get; set; }
        public string Type { get; set; }
        public string TypeValue { get; set; }
    }

    public sealed class AxAttributeProxy
    {
        public string Name { get; set; }
        public List<AxAttributeParameterProxy> Parameters { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxClassMemberVariableProxy
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public bool IsArray { get; set; }
        public bool IsConst { get; set; }
        public bool IsReadOnly { get; set; }
        public bool IsStatic { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Type { get; set; }
        public string TypeName { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxClassProxy
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public CompilerMetadataProxy CompilerMetadata { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string Declaration { get; set; }
        public string Extends { get; set; }
        public List<string> Implements { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsFinal { get; set; }
        public bool IsInterface { get; set; }
        public bool IsInternal { get; set; }
        public bool IsKernelClass { get; set; }
        public string IsObsolete { get; set; }
        public bool IsPrivate { get; set; }
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }
        public List<AxClassMemberVariableProxy> Members { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public string RunOn { get; set; }
        public SourceCodeProxy SourceCode { get; set; }
        public string Tags { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxConfigurationKeyProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public string Description { get; set; }
        public string Enabled { get; set; }
        public string EnabledByDefault { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string LicenseCode { get; set; }
        public string Name { get; set; }
        public string ParentKey { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxConflictProxy
    {
    }

    public sealed class AxDataEntityDeleteActionProxy
    {
        public string DeleteAction { get; set; }
        public string Name { get; set; }
        public string Relation { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxDataEntityViewFieldProxy
    {
        public string AccessModifier { get; set; }
        public string AllowEdit { get; set; }
        public string AllowEditOnCreate { get; set; }
        public string ConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CountryRegionContextField { get; set; }
        public string FeatureClass { get; set; }
        public string GroupPrompt { get; set; }
        public string HelpText { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Mandatory { get; set; }
        public string Name { get; set; }
        public string RelationContext { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxDataEntityViewKeyFieldProxy
    {
        public string DataField { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxDataEntityViewKeyProxy
    {
        public string ConfigurationKey { get; set; }
        public string Enabled { get; set; }
        public List<AxDataEntityViewKeyFieldProxy> Fields { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string ValidTimeStateKey { get; set; }
        public string ValidTimeStateMode { get; set; }
    }

    public sealed class AxDataEntityViewProxy
    {
        public string AllowArchival { get; set; }
        public string AllowRetention { get; set; }
        public string AllowRowVersionChangeTracking { get; set; }
        public string AosAuthorization { get; set; }
        public List<AxAttributeProxy> Attributes { get; set; }
        public string AutoCreateDataverse { get; set; }
        public SourceCodeProxy CompilerMetadata { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CountryRegionContextField { get; set; }
        public string DataManagementEnabled { get; set; }
        public string DataManagementStagingTable { get; set; }
        public string Declaration { get; set; }
        public List<AxDataEntityDeleteActionProxy> DeleteActions { get; set; }
        public string DeveloperDocumentation { get; set; }
        public string EnableDataverseSearch { get; set; }
        public string EnableSetBasedSqlOperations { get; set; }
        public string EntityCategory { get; set; }
        public string EntityRelationshipType { get; set; }
        public List<AxTableFieldGroupProxy> FieldGroups { get; set; }
        public List<AxDataEntityViewFieldProxy> Fields { get; set; }
        public string FormRef { get; set; }
        public string IsObsolete { get; set; }
        public string IsPublic { get; set; }
        public string IsReadOnly { get; set; }
        public List<AxDataEntityViewKeyProxy> Keys { get; set; }
        public string Label { get; set; }
        public string ListPageRef { get; set; }
        public List<AxTableMappingProxy> Mappings { get; set; }
        public string MessagingRole { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Modules { get; set; }
        public string Name { get; set; }
        public string OperationalDomain { get; set; }
        public string PreviewPartRef { get; set; }
        public string PrimaryCompanyContext { get; set; }
        public string PrimaryKey { get; set; }
        public string PublicCollectionName { get; set; }
        public string PublicEntityName { get; set; }
        public string Query { get; set; }
        public List<AxDataEntityViewRangeProxy> Ranges { get; set; }
        public List<AxDataEntityViewRelationProxy> Relations { get; set; }
        public string ReportRef { get; set; }
        public string SingularLabel { get; set; }
        public SourceCodeProxy SourceCode { get; set; }
        public List<AxStateMachineProxy> StateMachines { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string SupportsSetBasedSqlOperations { get; set; }
        public string TableGroup { get; set; }
        public string Tags { get; set; }
        public string TitleField1 { get; set; }
        public string TitleField2 { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
        public string ValidTimeStateEnabled { get; set; }
        public ViewMetadataProxy ViewMetadata { get; set; }
        public string Visibility { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxDataEntityViewRangeProxy
    {
        public string Enabled { get; set; }
        public string Field { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string Tags { get; set; }
        public string Value { get; set; }
    }

    public sealed class AxDataEntityViewRelationConstraintProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxDataEntityViewRelationProxy
    {
        public string Cardinality { get; set; }
        public List<AxDataEntityViewRelationConstraintProxy> Constraints { get; set; }
        public string Name { get; set; }
        public string RelatedDataEntity { get; set; }
        public string RelatedDataEntityCardinality { get; set; }
        public string RelatedDataEntityRole { get; set; }
        public string RelationshipType { get; set; }
        public string Role { get; set; }
        public string Tags { get; set; }
        public string UseDefaultRoleNames { get; set; }
        public string Validate { get; set; }
    }

    public sealed class AxEdtArrayElementProxy
    {
        public string CollectionLabel { get; set; }
        public string HelpText { get; set; }
        public int Index { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public List<AxEdtRelationProxy> Relations { get; set; }
        public List<AxEdtTableReferenceProxy> TableReferences { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxEdtExtensionProxy
    {
        public List<AxEdtArrayElementProxy> ArrayElements { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxEdtProxy
    {
        public string Alignment { get; set; }
        public List<AxEdtArrayElementProxy> ArrayElements { get; set; }
        public string ButtonImage { get; set; }
        public string CollectionLabel { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string ControlClass { get; set; }
        public string CountryRegionCodes { get; set; }
        public string DataInteractorFactory { get; set; }
        public string Direction { get; set; }
        public int DisplayLength { get; set; }
        public string EnforceHierarchy { get; set; }
        public string Extends { get; set; }
        public string FormHelp { get; set; }
        public string HelpText { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Literals { get; set; }
        public string Name { get; set; }
        public string PresenceClass { get; set; }
        public string PresenceIndicatorAllowed { get; set; }
        public string PresenceMethod { get; set; }
        public string ReferenceTable { get; set; }
        public List<AxEdtRelationProxy> Relations { get; set; }
        public List<AxEdtTableReferenceProxy> TableReferences { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxEdtRelationProxy
    {
        public string RelatedField { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxEdtTableReferenceProxy
    {
        public string RelatedField { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxEnumExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxEnumValueProxy> EnumValues { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public List<AxExtensionModificationProxy> ValueModifications { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxEnumProxy
    {
        public string AnalysisUsage { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CountryRegionCodes { get; set; }
        public int DisplayLength { get; set; }
        public List<AxEnumValueProxy> EnumValues { get; set; }
        public string Help { get; set; }
        public string HelpText { get; set; }
        public bool IsExtensible { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Literals { get; set; }
        public string Name { get; set; }
        public string Style { get; set; }
        public string Tags { get; set; }
        public string UseEnumValue { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxEnumValueProxy
    {
        public string ConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string FeatureClass { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public int Value { get; set; }
    }

    public sealed class AxEventHandlerProxy
    {
        public string ClassMethod { get; set; }
        public string ClassName { get; set; }
        public string HandlerSignal { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxExtensionModificationProxy
    {
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormControlExtensionComponentProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormControlExtensionPropertyProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Type { get; set; }
        public string TypeName { get; set; }
        public string Value { get; set; }
    }

    public sealed class AxFormControlPropertyCollectionProxy
    {
        public List<AxMethodPropertyCollectionProxy> Methods { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
    }

    public sealed class AxFormControlProxy
    {
        public string AlignControl { get; set; }
        public string AllowEdit { get; set; }
        public string AutoDeclaration { get; set; }
        public string ConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string DataRelationPath { get; set; }
        public DeltaMethodsProxy DeltaMethods { get; set; }
        public string DragDrop { get; set; }
        public int ElementPosition { get; set; }
        public string Enabled { get; set; }
        public string EnableFormRef { get; set; }
        public string ExtendedStyle { get; set; }
        public string FilterDataSource { get; set; }
        public string FilterExpression { get; set; }
        public string FilterField { get; set; }
        public FormControlExtensionProxy FormControlExtension { get; set; }
        public int Height { get; set; }
        public string HeightMode { get; set; }
        public string HelpText { get; set; }
        public string HyperLinkDataSource { get; set; }
        public string HyperLinkMenuItem { get; set; }
        public int Left { get; set; }
        public string LeftMode { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public string NeededPermission { get; set; }
        public string Pattern { get; set; }
        public string PatternVersion { get; set; }
        public string PreviewPartRef { get; set; }
        public string Skip { get; set; }
        public string Tags { get; set; }
        public int Top { get; set; }
        public string TopMode { get; set; }
        public string Type { get; set; }
        public int VerticalSpacing { get; set; }
        public string VerticalSpacingMode { get; set; }
        public string Visible { get; set; }
        public int Width { get; set; }
        public string WidthMode { get; set; }
    }

    public sealed class AxFormDataSourceDerivedProxy
    {
        public List<AxFormDataSourceDerivedProxy> DerivedDataSources { get; set; }
        public List<AxFormDataSourceFieldProxy> Fields { get; set; }
        public string Name { get; set; }
        public List<AxFormDataSourceReferencedProxy> ReferencedDataSources { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormDataSourceFieldPropertyCollectionProxy
    {
        public string DataField { get; set; }
        public List<AxMethodPropertyCollectionProxy> Methods { get; set; }
        public string Name { get; set; }
    }

    public sealed class AxFormDataSourceFieldProxy
    {
        public string AllowAdd { get; set; }
        public string AllowEdit { get; set; }
        public string DataField { get; set; }
        public DeltaMethodsProxy DeltaMethods { get; set; }
        public string Enabled { get; set; }
        public string Mandatory { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public string Skip { get; set; }
        public string Tags { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxFormDataSourceProperyCollectionProxy
    {
        public List<AxFormDataSourceFieldPropertyCollectionProxy> Fields { get; set; }
        public List<AxMethodPropertyCollectionProxy> Methods { get; set; }
        public string Name { get; set; }
    }

    public sealed class AxFormDataSourceReferencedProxy
    {
        public string AllowDeferredLoad { get; set; }
        public string AutoNotify { get; set; }
        public string AutoQuery { get; set; }
        public string AutoSearch { get; set; }
        public string CrossCompanyAutoQuery { get; set; }
        public string DelayActive { get; set; }
        public List<AxFormDataSourceFieldProxy> Fields { get; set; }
        public string JoinRelation { get; set; }
        public string JoinSource { get; set; }
        public string LinkType { get; set; }
        public int MaxRecordsToLoad { get; set; }
        public string Name { get; set; }
        public string OnlyFetchActive { get; set; }
        public List<AxFormDataSourceReferencedProxy> ReferencedDataSources { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormDataSourceRootLinkProxy
    {
        public string LinkType { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormDataSourceRootProxy
    {
        public string AllowCheck { get; set; }
        public string AllowCreate { get; set; }
        public string AllowDelete { get; set; }
        public string AllowEdit { get; set; }
        public string AutoNotify { get; set; }
        public string AutoQuery { get; set; }
        public string AutoSearch { get; set; }
        public string CounterField { get; set; }
        public string CrossCompanyAutoQuery { get; set; }
        public List<AxFormDataSourceRootLinkProxy> DataSourceLinks { get; set; }
        public string DelayActive { get; set; }
        public DeltaMethodsProxy DeltaMethods { get; set; }
        public List<AxFormDataSourceDerivedProxy> DerivedDataSources { get; set; }
        public List<AxFormDataSourceFieldProxy> Fields { get; set; }
        public string Index { get; set; }
        public string InsertAtEnd { get; set; }
        public string InsertIfEmpty { get; set; }
        public string JoinSource { get; set; }
        public string LinkType { get; set; }
        public string MaxAccessRight { get; set; }
        public int MaxRecordsToLoad { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public string OnlyFetchActive { get; set; }
        public string OptionalRecordMode { get; set; }
        public List<AxFormDataSourceReferencedProxy> ReferencedDataSources { get; set; }
        public string StartPosition { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
        public string ValidTimeStateAutoQuery { get; set; }
        public string ValidTimeStateUpdate { get; set; }
    }

    public sealed class AxFormExtensionControlProxy
    {
        public AxFormControlProxy FormControl { get; set; }
        public string Name { get; set; }
        public string PositionType { get; set; }
        public string PreviousSibling { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormExtensionDataSourceReferenceProxy
    {
        public AxFormDataSourceReferencedProxy FormDataSourceReferenced { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormExtensionPartReferenceProxy
    {
        public AxFormPartReferenceProxy FormPartReference { get; set; }
        public string Name { get; set; }
        public string PositionType { get; set; }
        public string PreviousSibling { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxFormExtensionProxy
    {
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxExtensionModificationProxy> ControlModifications { get; set; }
        public List<AxFormExtensionControlProxy> Controls { get; set; }
        public List<AxExtensionModificationProxy> DataSourceModifications { get; set; }
        public List<AxFormExtensionDataSourceReferenceProxy> DataSourceReferences { get; set; }
        public List<AxFormDataSourceRootProxy> DataSources { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxFormExtensionPartReferenceProxy> Parts { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxFormPartReferenceProxy
    {
        public string DataSource { get; set; }
        public string DataSourceRelation { get; set; }
        public int ElementPosition { get; set; }
        public string IsLinked { get; set; }
        public string MenuItemName { get; set; }
        public string Name { get; set; }
        public string PartLocation { get; set; }
        public string RunMode { get; set; }
        public string Tags { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxFormProxy
    {
        public string AllowPreLoading { get; set; }
        public List<AxAttributeProxy> Attributes { get; set; }
        public string AutoCacheUpdate { get; set; }
        public SourceCodeProxy2 CompilerMetadata { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string DataSourceChangeGroupMode { get; set; }
        public string DataSourceQuery { get; set; }
        public List<AxFormDataSourceRootProxy> DataSources { get; set; }
        public DesignProxy Design { get; set; }
        public string FormTemplate { get; set; }
        public string InteractionClass { get; set; }
        public string IsObsolete { get; set; }
        public List<AxClassMemberVariableProxy> Members { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public List<AxFormPartReferenceProxy> Parts { get; set; }
        public SourceCodeProxy2 SourceCode { get; set; }
        public string Tags { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuCustomizationElementProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxMenuElementProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxMenuExtensionElementProxy
    {
        public AxMenuElementProxy MenuElement { get; set; }
        public string Name { get; set; }
        public string PositionType { get; set; }
        public string PreviousSibling { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxMenuExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxMenuCustomizationElementProxy> Customizations { get; set; }
        public List<AxMenuExtensionElementProxy> Elements { get; set; }
        public string IsObsolete { get; set; }
        public List<AxExtensionModificationProxy> MenuElementModifications { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuItemActionExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuItemActionProxy
    {
        public string AllowRootNavigation { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CopyCallerQuery { get; set; }
        public string CorrectPermissions { get; set; }
        public string CountryConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CreatePermissions { get; set; }
        public string DeletePermissions { get; set; }
        public string DisabledImage { get; set; }
        public string DisabledImageLocation { get; set; }
        public string DisabledResource { get; set; }
        public string EnumParameter { get; set; }
        public string EnumTypeParameter { get; set; }
        public string ExtendedDataSecurity { get; set; }
        public string FeatureClass { get; set; }
        public string FormViewOption { get; set; }
        public string HelpText { get; set; }
        public string ImageLocation { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string LinkedPermissionObject { get; set; }
        public string LinkedPermissionObjectChild { get; set; }
        public string LinkedPermissionType { get; set; }
        public string MaintainUserLicense { get; set; }
        public string MultiSelect { get; set; }
        public string Name { get; set; }
        public string NeedsRecord { get; set; }
        public string NormalImage { get; set; }
        public string NormalResource { get; set; }
        public string Object { get; set; }
        public string ObjectType { get; set; }
        public string OpenMode { get; set; }
        public string OperationalDomain { get; set; }
        public string Parameters { get; set; }
        public string Query { get; set; }
        public string ReadPermissions { get; set; }
        public string ReportDesign { get; set; }
        public string StateMachine { get; set; }
        public string StateMachineDataSource { get; set; }
        public string StateMachineTransitionTo { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string Tags { get; set; }
        public string UpdatePermissions { get; set; }
        public string ViewUserLicense { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuItemDisplayExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuItemDisplayProxy
    {
        public string AllowRootNavigation { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CopyCallerQuery { get; set; }
        public string CorrectPermissions { get; set; }
        public string CountryConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CreatePermissions { get; set; }
        public string DeletePermissions { get; set; }
        public string DisabledImage { get; set; }
        public string DisabledImageLocation { get; set; }
        public string DisabledResource { get; set; }
        public string EnumParameter { get; set; }
        public string EnumTypeParameter { get; set; }
        public string ExtendedDataSecurity { get; set; }
        public string FeatureClass { get; set; }
        public string FormViewOption { get; set; }
        public string HelpText { get; set; }
        public string ImageLocation { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string LinkedPermissionObject { get; set; }
        public string LinkedPermissionObjectChild { get; set; }
        public string LinkedPermissionType { get; set; }
        public string MaintainUserLicense { get; set; }
        public string MultiSelect { get; set; }
        public string Name { get; set; }
        public string NeedsRecord { get; set; }
        public string NormalImage { get; set; }
        public string NormalResource { get; set; }
        public string Object { get; set; }
        public string ObjectType { get; set; }
        public string OpenMode { get; set; }
        public string OperationalDomain { get; set; }
        public string Parameters { get; set; }
        public string Query { get; set; }
        public string ReadPermissions { get; set; }
        public string ReportDesign { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string Tags { get; set; }
        public string UpdatePermissions { get; set; }
        public string ViewUserLicense { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuItemOutputExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuItemOutputProxy
    {
        public string AllowRootNavigation { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CopyCallerQuery { get; set; }
        public string CorrectPermissions { get; set; }
        public string CountryConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CreatePermissions { get; set; }
        public string DeletePermissions { get; set; }
        public string DisabledImage { get; set; }
        public string DisabledImageLocation { get; set; }
        public string DisabledResource { get; set; }
        public string EnumParameter { get; set; }
        public string EnumTypeParameter { get; set; }
        public string ExtendedDataSecurity { get; set; }
        public string FeatureClass { get; set; }
        public string FormViewOption { get; set; }
        public string HelpText { get; set; }
        public string ImageLocation { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string LinkedPermissionObject { get; set; }
        public string LinkedPermissionObjectChild { get; set; }
        public string LinkedPermissionType { get; set; }
        public string MaintainUserLicense { get; set; }
        public string MultiSelect { get; set; }
        public string Name { get; set; }
        public string NeedsRecord { get; set; }
        public string NormalImage { get; set; }
        public string NormalResource { get; set; }
        public string Object { get; set; }
        public string ObjectType { get; set; }
        public string OpenMode { get; set; }
        public string OperationalDomain { get; set; }
        public string Parameters { get; set; }
        public string Query { get; set; }
        public string ReadPermissions { get; set; }
        public string ReportDesign { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string Tags { get; set; }
        public string UpdatePermissions { get; set; }
        public string ViewUserLicense { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMenuProxy
    {
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CountryRegionCodes { get; set; }
        public string DisabledImage { get; set; }
        public string DisabledImageLocation { get; set; }
        public List<AxMenuElementProxy> Elements { get; set; }
        public string FeatureClass { get; set; }
        public string ImageLocation { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string MenuItemName { get; set; }
        public string MenuItemType { get; set; }
        public string Name { get; set; }
        public string NormalImage { get; set; }
        public string Parameters { get; set; }
        public string SetCompany { get; set; }
        public string ShortCut { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxMethodParameterProxy
    {
        public string DefaultValue { get; set; }
        public bool IsArray { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Type { get; set; }
        public string TypeName { get; set; }
    }

    public sealed class AxMethodPropertyCollectionProxy
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public List<AxEventHandlerProxy> EventHandlers { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsDelegate { get; set; }
        public bool IsDisplay { get; set; }
        public bool IsEdit { get; set; }
        public bool IsFinal { get; set; }
        public bool IsStatic { get; set; }
        public string Name { get; set; }
        public List<AxMethodParameterProxy> Parameters { get; set; }
        public ReturnTypeProxy ReturnType { get; set; }
        public string Source { get; set; }
        public List<string> TypeParameters { get; set; }
        public List<AxMethodVariableProxy> Variables { get; set; }
        public string Visibility { get; set; }
        public string XMLDocumentation { get; set; }
    }

    public sealed class AxMethodProxy
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public List<AxEventHandlerProxy> EventHandlers { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsDelegate { get; set; }
        public bool IsDisplay { get; set; }
        public bool IsEdit { get; set; }
        public bool IsExtendedMethod { get; set; }
        public bool IsFinal { get; set; }
        public bool IsStatic { get; set; }
        public string Name { get; set; }
        public List<AxMethodParameterProxy> Parameters { get; set; }
        public ReturnTypeProxy ReturnType { get; set; }
        public string Source { get; set; }
        public string Tags { get; set; }
        public List<string> TypeParameters { get; set; }
        public List<AxMethodVariableProxy> Variables { get; set; }
        public string Visibility { get; set; }
        public string XMLDocumentation { get; set; }
    }

    public sealed class AxMethodVariableProxy
    {
        public bool IsArray { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Type { get; set; }
        public string TypeName { get; set; }
    }

    public sealed class AxPropertyModificationProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Value { get; set; }
    }

    public sealed class AxQueryExtensionEmbeddedDataSourceProxy
    {
        public AxQuerySimpleEmbeddedDataSourceProxy DataSource { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQueryExtensionQueryDataSourceFieldProxy
    {
        public string Name { get; set; }
        public AxQuerySimpleDataSourceFieldProxy QueryDataSourceField { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQueryExtensionQueryDataSourceRangeProxy
    {
        public string Name { get; set; }
        public AxQuerySimpleDataSourceRangeProxy QueryDataSourceRange { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQueryExtensionQueryDataSourceRelationProxy
    {
        public string Name { get; set; }
        public AxQuerySimpleDataSourceRelationProxy QueryDataSourceRelation { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQueryExtensionQueryOrderByFieldProxy
    {
        public string Name { get; set; }
        public AxQuerySimpleOrderByFieldProxy QueryOrderByField { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQuerySimpleDataSourceFieldProxy
    {
        public string DerivedTable { get; set; }
        public string Field { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQuerySimpleDataSourceRangeProxy
    {
        public string DerivedTable { get; set; }
        public string Enabled { get; set; }
        public string Field { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string Tags { get; set; }
        public string Value { get; set; }
    }

    public sealed class AxQuerySimpleDataSourceRelationProxy
    {
        public string DerivedTable { get; set; }
        public string Field { get; set; }
        public string JoinDataSource { get; set; }
        public string JoinDerivedTable { get; set; }
        public string JoinRelationName { get; set; }
        public string Name { get; set; }
        public string RelatedField { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQuerySimpleDerivedDataSourceProxy
    {
        public string AllowAdd { get; set; }
        public string ApplyDateFilter { get; set; }
        public string ChangeTrackingEnabled { get; set; }
        public string Company { get; set; }
        public string ConcurrencyModel { get; set; }
        public List<AxQuerySimpleEmbeddedDataSourceProxy> DataSources { get; set; }
        public List<AxQuerySimpleDerivedDataSourceProxy> DerivedDataSources { get; set; }
        public string DynamicFields { get; set; }
        public string Enabled { get; set; }
        public List<AxQuerySimpleDataSourceFieldProxy> Fields { get; set; }
        public string FirstFast { get; set; }
        public string FirstOnly { get; set; }
        public string IsReadOnly { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string PolicyContext { get; set; }
        public List<AxQuerySimpleDataSourceRangeProxy> Ranges { get; set; }
        public string SelectWithRepeatableRead { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
        public string UnionType { get; set; }
        public string Update { get; set; }
        public string ValidTimeStateUpdate { get; set; }
    }

    public sealed class AxQuerySimpleEmbeddedDataSourceProxy
    {
        public string AllowAdd { get; set; }
        public string ApplyDateFilter { get; set; }
        public string ChangeTrackingEnabled { get; set; }
        public string Company { get; set; }
        public string ConcurrencyModel { get; set; }
        public List<AxQuerySimpleEmbeddedDataSourceProxy> DataSources { get; set; }
        public List<AxQuerySimpleDerivedDataSourceProxy> DerivedDataSources { get; set; }
        public string DynamicFields { get; set; }
        public string Enabled { get; set; }
        public string FetchMode { get; set; }
        public List<AxQuerySimpleDataSourceFieldProxy> Fields { get; set; }
        public string FirstFast { get; set; }
        public string FirstOnly { get; set; }
        public string IsReadOnly { get; set; }
        public string JoinMode { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string PolicyContext { get; set; }
        public List<AxQuerySimpleDataSourceRangeProxy> Ranges { get; set; }
        public List<AxQuerySimpleDataSourceRelationProxy> Relations { get; set; }
        public string SelectWithRepeatableRead { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
        public string UnionType { get; set; }
        public string Update { get; set; }
        public string UseRelations { get; set; }
        public string ValidTimeStateUpdate { get; set; }
    }

    public sealed class AxQuerySimpleExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxQueryExtensionEmbeddedDataSourceProxy> DataSources { get; set; }
        public List<AxQueryExtensionQueryDataSourceFieldProxy> Fields { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxQueryExtensionQueryOrderByFieldProxy> OrderByFields { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public List<AxExtensionModificationProxy> RangeModifications { get; set; }
        public List<AxQueryExtensionQueryDataSourceRangeProxy> Ranges { get; set; }
        public List<AxQueryExtensionQueryDataSourceRelationProxy> Relations { get; set; }
        public List<AxQuerySimpleRootDataSourceProxy> RootDataSources { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxQuerySimpleGroupByFieldProxy
    {
        public string DataSource { get; set; }
        public string DerivedTable { get; set; }
        public string Field { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQuerySimpleHavingPredicateProxy
    {
        public string DataSource { get; set; }
        public string DerivedTable { get; set; }
        public string Enabled { get; set; }
        public string Field { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Status { get; set; }
        public string Tags { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
    }

    public sealed class AxQuerySimpleOrderByFieldProxy
    {
        public string DataSource { get; set; }
        public string DerivedTable { get; set; }
        public string Direction { get; set; }
        public string Field { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxQuerySimpleRootDataSourceProxy
    {
        public string AllowAdd { get; set; }
        public string ApplyDateFilter { get; set; }
        public string ChangeTrackingEnabled { get; set; }
        public string Company { get; set; }
        public string ConcurrencyModel { get; set; }
        public List<AxQuerySimpleEmbeddedDataSourceProxy> DataSources { get; set; }
        public List<AxQuerySimpleDerivedDataSourceProxy> DerivedDataSources { get; set; }
        public string DynamicFields { get; set; }
        public string Enabled { get; set; }
        public List<AxQuerySimpleDataSourceFieldProxy> Fields { get; set; }
        public string FirstFast { get; set; }
        public string FirstOnly { get; set; }
        public List<AxQuerySimpleGroupByFieldProxy> GroupBy { get; set; }
        public List<AxQuerySimpleHavingPredicateProxy> Having { get; set; }
        public string IsReadOnly { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public List<AxQuerySimpleOrderByFieldProxy> OrderBy { get; set; }
        public string PolicyContext { get; set; }
        public List<AxQuerySimpleDataSourceRangeProxy> Ranges { get; set; }
        public string SelectWithRepeatableRead { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
        public string UnionType { get; set; }
        public string Update { get; set; }
        public string ValidTimeStateUpdate { get; set; }
    }

    public sealed class AxSecurityDataEntityFieldPermissionProxy
    {
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityDataEntityFieldReferenceProxy
    {
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityDataEntityMethodPermissionProxy
    {
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityDataEntityPermissionProxy
    {
        public List<AxSecurityDataEntityFieldPermissionProxy> Fields { get; set; }
        public List<string> Grant { get; set; }
        public string IntegrationMode { get; set; }
        public List<AxSecurityDataEntityMethodPermissionProxy> Methods { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityDataEntityReferenceProxy
    {
        public List<AxSecurityDataEntityFieldReferenceProxy> Fields { get; set; }
        public List<string> Grant { get; set; }
        public List<string> GrantCurrentData { get; set; }
        public List<string> GrantFutureData { get; set; }
        public List<string> GrantPastData { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityDutyExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxSecurityPrivilegeReferenceProxy> Privileges { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxSecurityDutyProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public string ContextString { get; set; }
        public string Description { get; set; }
        public string Enabled { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public List<AxSecurityPrivilegeReferenceProxy> Privileges { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxSecurityDutyReferenceProxy
    {
        public string Enabled { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityEntryPointReferenceFormControlProxy
    {
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityEntryPointReferenceFormDataSourceFieldProxy
    {
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityEntryPointReferenceFormDataSourceProxy
    {
        public List<AxSecurityEntryPointReferenceFormDataSourceFieldProxy> Fields { get; set; }
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityEntryPointReferenceFormProxy
    {
        public List<AxSecurityEntryPointReferenceFormControlProxy> Controls { get; set; }
        public List<AxSecurityEntryPointReferenceFormDataSourceProxy> DataSources { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityEntryPointReferenceProxy
    {
        public List<AxSecurityEntryPointReferenceFormProxy> Forms { get; set; }
        public List<string> Grant { get; set; }
        public List<string> GrantCurrentData { get; set; }
        public List<string> GrantFutureData { get; set; }
        public List<string> GrantPastData { get; set; }
        public string Name { get; set; }
        public string ObjectChildName { get; set; }
        public string ObjectName { get; set; }
        public string ObjectType { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityFormControlReferenceCollectionProxy
    {
        public List<AxSecurityFormControlReferenceProxy> Controls { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityFormControlReferenceProxy
    {
        public List<string> Grant { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityPrivilegeProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxSecurityDataEntityPermissionProxy> DataEntityPermissions { get; set; }
        public string Description { get; set; }
        public List<AxSecurityDataEntityReferenceProxy> DirectAccessPermissions { get; set; }
        public string Enabled { get; set; }
        public List<AxSecurityEntryPointReferenceProxy> EntryPoints { get; set; }
        public List<AxSecurityFormControlReferenceCollectionProxy> FormControlOverrides { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxSecurityPrivilegeReferenceProxy
    {
        public string Enabled { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxSecurityRoleExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxSecurityDataEntityReferenceProxy> DirectAccessPermissions { get; set; }
        public List<AxSecurityDutyReferenceProxy> Duties { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxSecurityPrivilegeReferenceProxy> Privileges { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxSecurityRoleProxy
    {
        public string CanBeDeletedFromUI { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string ContextString { get; set; }
        public string Description { get; set; }
        public List<AxSecurityDataEntityReferenceProxy> DirectAccessPermissions { get; set; }
        public List<AxSecurityDutyReferenceProxy> Duties { get; set; }
        public string Enabled { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public List<AxSecurityPrivilegeReferenceProxy> Privileges { get; set; }
        public List<AxSecurityRoleReferenceProxy> SubRoles { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxSecurityRoleReferenceProxy
    {
        public string Enabled { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxServiceGroupProxy
    {
        public string AutoDeploy { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string Description { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public List<AxServiceGroupServiceProxy> Services { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxServiceGroupServiceProxy
    {
        public string Name { get; set; }
        public string Service { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxServiceOperationProxy
    {
        public string EnableIdempotence { get; set; }
        public string Method { get; set; }
        public string Name { get; set; }
        public string OperationalDomain { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxServiceProxy
    {
        public string Class { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string Description { get; set; }
        public string ExternalName { get; set; }
        public string IsObsolete { get; set; }
        public string Name { get; set; }
        public string Namespace { get; set; }
        public List<AxServiceOperationProxy> ServiceOperations { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxStateMachineProxy
    {
        public string DataField { get; set; }
        public string Description { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public List<AxStateMachineStateProxy> States { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxStateMachineStateProxy
    {
        public string Description { get; set; }
        public int EnumValue { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string StateKind { get; set; }
        public string Tags { get; set; }
        public List<AxStateMachineStateTransitionProxy> Transitions { get; set; }
    }

    public sealed class AxStateMachineStateTransitionProxy
    {
        public string Description { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string TransitionToState { get; set; }
    }

    public sealed class AxTableDeleteActionProxy
    {
        public string DeleteAction { get; set; }
        public string Name { get; set; }
        public string Relation { get; set; }
        public string Table { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxTableFieldGroupExtensionProxy> FieldGroupExtensions { get; set; }
        public List<AxTableFieldGroupProxy> FieldGroups { get; set; }
        public List<AxExtensionModificationProxy> FieldModifications { get; set; }
        public List<AxTableFieldProxy> Fields { get; set; }
        public string FormRef { get; set; }
        public List<AxTableFullTextIndexProxy> FullTextIndexes { get; set; }
        public List<AxTableIndexProxy> Indexes { get; set; }
        public string IsObsolete { get; set; }
        public List<AxTableMappingProxy> Mappings { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public List<AxTableRelationExtensionProxy> RelationExtensions { get; set; }
        public List<AxExtensionModificationProxy> RelationModifications { get; set; }
        public List<AxTableRelationProxy> Relations { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxTableFieldGroupExtensionProxy
    {
        public List<AxTableFieldGroupFieldProxy> Fields { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableFieldGroupFieldProxy
    {
        public string DataField { get; set; }
        public string Name { get; set; }
        public string PositionType { get; set; }
        public string PreviousSibling { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableFieldGroupProxy
    {
        public string AutoPopulate { get; set; }
        public List<AxTableFieldGroupFieldProxy> Fields { get; set; }
        public string IsManuallyUpdated { get; set; }
        public string IsSystemGenerated { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableFieldProxy
    {
        public string AliasFor { get; set; }
        public string AllowEdit { get; set; }
        public string AllowEditOnCreate { get; set; }
        public string AosAuthorization { get; set; }
        public string AssetClassification { get; set; }
        public string ConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CountryRegionContextField { get; set; }
        public string ExtendedDataType { get; set; }
        public string FeatureClass { get; set; }
        public string GeneralDataProtectionRegulation { get; set; }
        public string GroupPrompt { get; set; }
        public string HelpText { get; set; }
        public string IgnoreEDTRelation { get; set; }
        public string IsManuallyUpdated { get; set; }
        public bool IsNullable { get; set; }
        public string IsObsolete { get; set; }
        public string IsSystemGenerated { get; set; }
        public string Label { get; set; }
        public string Mandatory { get; set; }
        public string MinReadAccess { get; set; }
        public string Name { get; set; }
        public string Null { get; set; }
        public string RelationContext { get; set; }
        public string SaveContents { get; set; }
        public string SysSharingType { get; set; }
        public string Tags { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxTableFullTextIndexProxy
    {
        public string ChangeTracking { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxTableIndexFieldProxy> Fields { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableIndexFieldProxy
    {
        public string DataField { get; set; }
        public string IncludedColumn { get; set; }
        public string Name { get; set; }
        public string Optional { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableIndexProxy
    {
        public string AllowDuplicates { get; set; }
        public string AllowPageLocks { get; set; }
        public string AlternateKey { get; set; }
        public string ConfigurationKey { get; set; }
        public string Enabled { get; set; }
        public List<AxTableIndexFieldProxy> Fields { get; set; }
        public string IndexType { get; set; }
        public string IsManuallyUpdated { get; set; }
        public string IsSystemGenerated { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string ValidTimeStateKey { get; set; }
        public string ValidTimeStateMode { get; set; }
    }

    public sealed class AxTableMappingConnectionProxy
    {
        public string MapField { get; set; }
        public string MapFieldTo { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableMappingProxy
    {
        public List<AxTableMappingConnectionProxy> Connections { get; set; }
        public string MappingTable { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableProxy
    {
        public string Abstract { get; set; }
        public string AllowArchival { get; set; }
        public string AllowChangeTracking { get; set; }
        public string AllowOverride { get; set; }
        public string AllowRetention { get; set; }
        public string AllowRowVersionChangeTracking { get; set; }
        public string AosAuthorization { get; set; }
        public List<AxAttributeProxy> Attributes { get; set; }
        public string CacheLookup { get; set; }
        public string ClusteredIndex { get; set; }
        public SourceCodeProxy CompilerMetadata { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CountryRegionContextField { get; set; }
        public string CreatedBy { get; set; }
        public string CreatedDateTime { get; set; }
        public string CreatedTransactionId { get; set; }
        public string CreateRecIdIndex { get; set; }
        public string DataSharingType { get; set; }
        public string Declaration { get; set; }
        public List<AxTableDeleteActionProxy> DeleteActions { get; set; }
        public string DeveloperDocumentation { get; set; }
        public string DisableDatabaseLogging { get; set; }
        public string DisableLockEscalation { get; set; }
        public string Durability { get; set; }
        public string EntityRelationshipType { get; set; }
        public string Extends { get; set; }
        public List<AxTableFieldGroupProxy> FieldGroups { get; set; }
        public List<AxTableFieldProxy> Fields { get; set; }
        public string FormRef { get; set; }
        public List<AxTableFullTextIndexProxy> FullTextIndexes { get; set; }
        public List<AxTableIndexProxy> Indexes { get; set; }
        public string InstanceRelationType { get; set; }
        public bool IsKernelTable { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string ListPageRef { get; set; }
        public List<AxTableMappingProxy> Mappings { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string ModifiedBy { get; set; }
        public string ModifiedDateTime { get; set; }
        public string ModifiedTransactionId { get; set; }
        public string Modules { get; set; }
        public string Name { get; set; }
        public string OccEnabled { get; set; }
        public string OperationalDomain { get; set; }
        public string PreviewPartRef { get; set; }
        public string PrimaryIndex { get; set; }
        public List<AxTableRelationProxy> Relations { get; set; }
        public string ReplacementKey { get; set; }
        public string ReportRef { get; set; }
        public string SaveDataPerCompany { get; set; }
        public string SaveDataPerPartition { get; set; }
        public string SingularLabel { get; set; }
        public SourceCodeProxy SourceCode { get; set; }
        public List<AxStateMachineProxy> StateMachines { get; set; }
        public string StorageMode { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string SupportInheritance { get; set; }
        public string SystemTable { get; set; }
        public string TableContents { get; set; }
        public string TableGroup { get; set; }
        public string TableType { get; set; }
        public string Tags { get; set; }
        public string TitleField1 { get; set; }
        public string TitleField2 { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
        public string ValidTimeStateFieldType { get; set; }
        public string Visibility { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxTableRelationConstraintProxy
    {
        public string Name { get; set; }
        public string SourceEDT { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableRelationExtensionProxy
    {
        public string Name { get; set; }
        public List<AxTableRelationConstraintProxy> RelationConstraints { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxTableRelationProxy
    {
        public string Cardinality { get; set; }
        public List<AxTableRelationConstraintProxy> Constraints { get; set; }
        public string CreateNavigationPropertyMethods { get; set; }
        public string EDTRelation { get; set; }
        public string EntityRelationshipRole { get; set; }
        public string IsManuallyUpdated { get; set; }
        public string IsSystemGenerated { get; set; }
        public string Name { get; set; }
        public string NavigationPropertyMethodNameOverride { get; set; }
        public string OnDelete { get; set; }
        public string RelatedTable { get; set; }
        public string RelatedTableCardinality { get; set; }
        public string RelatedTableRole { get; set; }
        public string RelationshipType { get; set; }
        public string Role { get; set; }
        public string Tags { get; set; }
        public string UseDefaultRoleNames { get; set; }
        public string Validate { get; set; }
    }

    public sealed class AxTileProxy
    {
        public string AllowUserCacheRefresh { get; set; }
        public string ApplyFilter { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CopyCallerQuery { get; set; }
        public string CountryRegionCodes { get; set; }
        public string FormViewOption { get; set; }
        public string HelpText { get; set; }
        public string ImageLocation { get; set; }
        public string IsObsolete { get; set; }
        public string KPI { get; set; }
        public string Label { get; set; }
        public string MenuItemName { get; set; }
        public string MenuItemType { get; set; }
        public string Name { get; set; }
        public string NormalImage { get; set; }
        public string OpenMode { get; set; }
        public string Parameters { get; set; }
        public string Query { get; set; }
        public string RefreshFrequency { get; set; }
        public string Size { get; set; }
        public string Tags { get; set; }
        public string TileDisplay { get; set; }
        public string Type { get; set; }
        public string URL { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxViewExtensionProxy
    {
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxQueryExtensionEmbeddedDataSourceProxy> DataSources { get; set; }
        public List<AxTableFieldGroupExtensionProxy> FieldGroupExtensions { get; set; }
        public List<AxTableFieldGroupProxy> FieldGroups { get; set; }
        public List<AxExtensionModificationProxy> FieldModifications { get; set; }
        public List<AxViewFieldProxy> Fields { get; set; }
        public string IsObsolete { get; set; }
        public List<AxTableMappingProxy> Mappings { get; set; }
        public string Name { get; set; }
        public List<AxPropertyModificationProxy> PropertyModifications { get; set; }
        public List<AxQueryExtensionQueryDataSourceRangeProxy> Ranges { get; set; }
        public string Tags { get; set; }
        public string Visibility { get; set; }
    }

    public sealed class AxViewFieldProxy
    {
        public string AccessModifier { get; set; }
        public string AosAuthorization { get; set; }
        public string ConfigurationKey { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CountryRegionContextField { get; set; }
        public string FeatureClass { get; set; }
        public string GroupPrompt { get; set; }
        public string HelpText { get; set; }
        public string IsObsolete { get; set; }
        public string Label { get; set; }
        public string Name { get; set; }
        public string RelationContext { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxViewIndexFieldProxy
    {
        public string DataField { get; set; }
        public string IncludedColumn { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxViewIndexProxy
    {
        public string AllowDuplicates { get; set; }
        public string AlternateKey { get; set; }
        public string ConfigurationKey { get; set; }
        public string Enabled { get; set; }
        public List<AxViewIndexFieldProxy> Fields { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
        public string ValidTimeStateKey { get; set; }
        public string ValidTimeStateMode { get; set; }
    }

    public sealed class AxViewProxy
    {
        public string AosAuthorization { get; set; }
        public List<AxAttributeProxy> Attributes { get; set; }
        public string CollectionName { get; set; }
        public SourceCodeProxy CompilerMetadata { get; set; }
        public string ConfigurationKey { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public string CountryRegionCodes { get; set; }
        public string CountryRegionContextField { get; set; }
        public string Declaration { get; set; }
        public string DeveloperDocumentation { get; set; }
        public string EntityRelationshipType { get; set; }
        public List<AxTableFieldGroupProxy> FieldGroups { get; set; }
        public List<AxViewFieldProxy> Fields { get; set; }
        public string FormRef { get; set; }
        public List<AxViewIndexProxy> Indexes { get; set; }
        public string IsObsolete { get; set; }
        public string IsPublic { get; set; }
        public string IsStaged { get; set; }
        public string Label { get; set; }
        public string ListPageRef { get; set; }
        public List<AxTableMappingProxy> Mappings { get; set; }
        public string MessagingRole { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public string OperationalDomain { get; set; }
        public string PreviewPartRef { get; set; }
        public string Query { get; set; }
        public List<AxViewRelationProxy> Relations { get; set; }
        public string ReplacementKey { get; set; }
        public string ReportRef { get; set; }
        public string SingularLabel { get; set; }
        public SourceCodeProxy SourceCode { get; set; }
        public List<AxStateMachineProxy> StateMachines { get; set; }
        public List<string> SubscriberAccessLevel { get; set; }
        public string TableGroup { get; set; }
        public string Tags { get; set; }
        public string TitleField1 { get; set; }
        public string TitleField2 { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
        public string Updatable { get; set; }
        public string ValidTimeStateEnabled { get; set; }
        public string Version { get; set; }
        public ViewMetadataProxy ViewMetadata { get; set; }
        public string Visibility { get; set; }
        public string Visible { get; set; }
    }

    public sealed class AxViewRelationConstraintProxy
    {
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class AxViewRelationProxy
    {
        public string Cardinality { get; set; }
        public List<AxViewRelationConstraintProxy> Constraints { get; set; }
        public string Name { get; set; }
        public string RelatedTable { get; set; }
        public string RelatedTableCardinality { get; set; }
        public string RelatedTableRole { get; set; }
        public string RelationshipType { get; set; }
        public string Role { get; set; }
        public string Tags { get; set; }
        public string UseDefaultRoleNames { get; set; }
    }

    public sealed class BackgroundColorRGBProxy
    {
    }

    public sealed class CompilerMetadataProxy
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public string Extends { get; set; }
        public List<string> Implements { get; set; }
        public bool IsAbstract { get; set; }
        public bool IsFinal { get; set; }
        public bool IsInterface { get; set; }
        public bool IsInternal { get; set; }
        public bool IsKernelClass { get; set; }
        public bool IsPrivate { get; set; }
        public bool IsPublic { get; set; }
        public bool IsStatic { get; set; }
        public List<AxClassMemberVariableProxy> Members { get; set; }
        public List<AxMethodPropertyCollectionProxy> Methods { get; set; }
        public string RunOn { get; set; }
        public List<string> TypeParameters { get; set; }
    }

    public sealed class DeltaMethodsProxy
    {
        public List<AxMethodProxy> Methods { get; set; }
    }

    public sealed class DesignProxy
    {
        public string AlignChild { get; set; }
        public string AlignChildren { get; set; }
        public string AllowDocking { get; set; }
        public string AllowFormCompanyChange { get; set; }
        public string AllowUserSetup { get; set; }
        public string AlwaysOnTop { get; set; }
        public string ArrangeMethod { get; set; }
        public string ArrangeWhen { get; set; }
        public string BackgroundColor { get; set; }
        public BackgroundColorRGBProxy BackgroundColorRGB { get; set; }
        public string Bold { get; set; }
        public int BottomMargin { get; set; }
        public string BottomMarginMode { get; set; }
        public string Caption { get; set; }
        public string ColorScheme { get; set; }
        public int Columns { get; set; }
        public string ColumnsMode { get; set; }
        public int ColumnSpace { get; set; }
        public string ColumnSpaceMode { get; set; }
        public List<AxFormControlProxy> Controls { get; set; }
        public string DataSource { get; set; }
        public string DefaultAction { get; set; }
        public string DialogSize { get; set; }
        public string Font { get; set; }
        public int FontSize { get; set; }
        public string Frame { get; set; }
        public int Height { get; set; }
        public string HeightMode { get; set; }
        public string HideIfEmpty { get; set; }
        public string HideToolbar { get; set; }
        public string ImageMode { get; set; }
        public string ImageName { get; set; }
        public int ImageResource { get; set; }
        public string Italic { get; set; }
        public string LabelBold { get; set; }
        public string LabelFont { get; set; }
        public int LabelFontSize { get; set; }
        public string LabelItalic { get; set; }
        public string LabelUnderline { get; set; }
        public int Left { get; set; }
        public int LeftMargin { get; set; }
        public string LeftMarginMode { get; set; }
        public string LeftMode { get; set; }
        public string MaximizeBox { get; set; }
        public string MinimizeBox { get; set; }
        public string Mode { get; set; }
        public string Name { get; set; }
        public string NewRecordAction { get; set; }
        public string Pattern { get; set; }
        public string PatternVersion { get; set; }
        public int RightMargin { get; set; }
        public string RightMarginMode { get; set; }
        public string SaveSize { get; set; }
        public string Scrollbars { get; set; }
        public string SetCompany { get; set; }
        public string ShowDeleteButton { get; set; }
        public string ShowNewButton { get; set; }
        public string StatusBarStyle { get; set; }
        public string Style { get; set; }
        public string Tags { get; set; }
        public string TitleDataSource { get; set; }
        public int Top { get; set; }
        public int TopMargin { get; set; }
        public string TopMarginMode { get; set; }
        public string TopMode { get; set; }
        public string Underline { get; set; }
        public string UseCaptionFromMenuItem { get; set; }
        public string ViewEditMode { get; set; }
        public string Visible { get; set; }
        public int Width { get; set; }
        public string WidthMode { get; set; }
        public string WindowResize { get; set; }
        public string WindowType { get; set; }
        public string WorkflowDataSource { get; set; }
        public string WorkflowEnabled { get; set; }
        public string WorkflowType { get; set; }
    }

    public sealed class FormControlExtensionProxy
    {
        public List<AxFormControlExtensionComponentProxy> ExtensionComponents { get; set; }
        public List<AxFormControlExtensionPropertyProxy> ExtensionProperties { get; set; }
        public string Name { get; set; }
        public string Tags { get; set; }
    }

    public sealed class ReturnTypeProxy
    {
        public string Tags { get; set; }
        public string Type { get; set; }
        public string TypeName { get; set; }
    }

    public sealed class SourceCodeProxy
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public string Declaration { get; set; }
        public List<AxMethodPropertyCollectionProxy> Methods { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
    }

    public sealed class SourceCodeProxy2
    {
        public List<AxAttributeProxy> Attributes { get; set; }
        public List<AxFormControlPropertyCollectionProxy> DataControls { get; set; }
        public List<AxFormDataSourceProperyCollectionProxy> DataSources { get; set; }
        public string Declaration { get; set; }
        public List<AxClassMemberVariableProxy> Members { get; set; }
        public List<AxMethodPropertyCollectionProxy> Methods { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
    }

    public sealed class ViewMetadataProxy
    {
        public string AllowCheck { get; set; }
        public string AllowCrossCompany { get; set; }
        public List<AxAttributeProxy> Attributes { get; set; }
        public SourceCodeProxy CompilerMetadata { get; set; }
        public List<AxConflictProxy> Conflicts { get; set; }
        public List<AxQuerySimpleRootDataSourceProxy> DataSources { get; set; }
        public string Description { get; set; }
        public string Form { get; set; }
        public string Importable { get; set; }
        public string Interactive { get; set; }
        public string IsObsolete { get; set; }
        public string Literals { get; set; }
        public List<AxMethodProxy> Methods { get; set; }
        public string Name { get; set; }
        public string QueryType { get; set; }
        public string Searchable { get; set; }
        public SourceCodeProxy SourceCode { get; set; }
        public string Tags { get; set; }
        public string Title { get; set; }
        public List<string> TypeParameters { get; set; }
        public string UnparsableSource { get; set; }
        public string UserUpdate { get; set; }
        public string Visibility { get; set; }
    }

}
