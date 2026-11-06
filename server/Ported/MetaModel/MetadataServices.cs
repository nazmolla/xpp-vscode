using System.Collections.Generic;

namespace XppVsCode.Server.MetaModel
{
    // The metadata operations by role. MetaModelBridge implements the element-centred roles; models, labels, project
    // packages and table relations have their own services. MetaModelBridgeServer routes requests to them.

    /// <summary>Create, read, change, check and delete elements.</summary>
    public interface IObjectStore
    {
        MetaModelResult CreateObject(CreateObjectRequest request);
        MetaModelResult UpdateObject(UpdateObjectRequest request);
        ReadObjectResult ReadObject(string objectType, string objectName);
        ReadObjectResult ReadObjectByPath(string filePath);
        ValidateObjectResult ValidateObject(ValidateObjectRequest request);
        string DeleteObjectJson(string objectType, string objectName, bool checkOnly = false);
        string DuplicateObjectJson(string objectType, string objectName, string newName, string modelName);
        string BestPracticeReport(string objectType, string objectName);
    }

    /// <summary>Find elements and describe types, forms and the environment.</summary>
    public interface IMetadataDiscovery
    {
        FindObjectResult FindObject(string objectName, string objectType, bool exactMatch, int maxResults);
        ListObjectsResult ListObjects(string modelName, string objectType, string nameFilter, int maxResults);
        FindReferencesResult FindReferences(string objectType, string objectName, string memberName, string referenceKind, int maxResults);
        ObjectTypeSchemaResult GetObjectTypeSchema(string objectType);
        ProxyGenerationResult GenerateProxies(ProxyGenerationRequest request);
        FormLayoutResult GetFormLayout(string formName, string rootControl, int maxDepth);
        string TypeMembersJson(string objectType, string objectName);
        string DumpElementJson(string objectType, string objectName);
        EnvironmentInfo GetEnvironmentInfo();
    }

    /// <summary>The element designer: node tree, properties, node edits, drops and methods.</summary>
    public interface IElementDesigner
    {
        string GetFormTreeJson(string formName);
        string GetTableFieldsJson(string tableName);
        string GetElementTreeJson(string objectType, string objectName);
        string GetElementPropertiesJson(string objectType, string objectName, string[] path);
        string SetElementPropertyJson(string objectType, string objectName, string[] path, string property, string value);
        string GetNodeTypesJson(string objectType, string objectName, string[] path);
        string AddElementNodeJson(string objectType, string objectName, string[] collectionPath, string typeName, string name, Dictionary<string, string> properties);
        string DeleteElementNodeJson(string objectType, string objectName, string[] path);
        string RenameElementNodeJson(string objectType, string objectName, string[] path, string newName);
        string DuplicateElementNodeJson(string objectType, string objectName, string[] path, string newName);
        string MoveElementNodeJson(string objectType, string objectName, string[] path, int offset);
        string DropOnNodeJson(string objectType, string objectName, string[] path, bool onBaseNode, string sourceType, string sourceName, string[] sourcePath);
        string DropOnPropertyJson(string objectType, string objectName, string[] path, string property, string sourceType, string sourceName);
        string GetElementMethodJson(string objectType, string objectName, string[] path);
        string SetElementMethodJson(string objectType, string objectName, string[] nodePath, string originalName, string source);
        string GetOverridesJson(string objectType, string objectName, string[] nodePath);
    }

    /// <summary>The active project and its items.</summary>
    public interface IProjectService
    {
        MetaModelResult AddToProject(string objectType, string objectName);
        List<ProjectItemInfo> ListProjectItems();
        bool HasActiveProject { get; }
        string RemoveFromProjectJson(string objectType, string objectName);
        string ListProjectsJson(string searchFolder);
        string SelectProjectJson(string projectFile);
    }

    /// <summary>Builds a model or the project's elements.</summary>
    public interface IBuildService
    {
        string BuildJson(string modelName, bool projectOnly, bool syncDatabase);
        string BestPracticeJson(string modelName, bool projectOnly);
        string PackageJson(string[] modelNames, string version);
        string SyncTablesJson(string[] tables);
    }

    /// <summary>Models and packages.</summary>
    public interface IModelService
    {
        ModelInfoResult GetModelInfo(string modelName);
        List<ModelSummary> ListModels();
        string GetModelJson(string name);
        string CreateModelJson(string name, string displayName, string publisher, string layer, string description, string[] references);
        string UpdateModelJson(string name, string displayName, string publisher, string description, string[] references);
        string ListPackagesJson();
    }

    /// <summary>Label files and their labels.</summary>
    public interface ILabelService
    {
        LabelResult ReadLabel(string labelFileId, string language, string labelId, string searchText, int maxResults);
        MetaModelResult CreateLabel(string labelFileId, string language, string labelId, string text, string comment);
        MetaModelResult CreateLabels(string labelFileId, string language, List<LabelRequestDto> labels);
        string LabelFileEntriesJson(string labelFile, string search, List<string> otherLanguages, int limit);
        string SaveLabelJson(string labelFile, string labelId, Dictionary<string, string> texts, string comment);
        string DeleteLabelJson(string labelFile, string labelId);
    }

    /// <summary>Project export and import (.axpp).</summary>
    public interface IProjectPackageService
    {
        string ExportProjectJson(string projectFile, string outputFile);
        string InspectProjectPackageJson(string file);
        string ImportProjectJson(string file, string targetFolder, bool overwrite);
    }

    /// <summary>Tables with their relations, and the tables that relate to a table.</summary>
    public interface ITableRelations
    {
        string TableErdJson(string[] tableNames);
        string IncomingRelationsJson(string tableName, bool force);
    }

    /// <summary>How a table's rows read in F&O: labels, enum value names, per-company and date-effective flags.</summary>
    public interface ITableDataShape
    {
        string TableDataShapeJson(string tableName);
    }

    /// <summary>Static performance findings in an element's code.</summary>
    public interface IPerformanceAnalysis
    {
        string AnalyzePerformanceJson(string objectType, string objectName);
    }

    /// <summary>A model's extensions (Chain of Command, event handlers, metadata extensions) checked against the installed code.</summary>
    public interface IExtensionCompatibility
    {
        string ExtensionCompatibilityJson(string modelName);
        string WrapperDriftJson(string modelName, string previousFolder);
        string ExtensionCollisionsJson(string modelName);
    }

    /// <summary>Copilot's writes under the user's control: scope lock and checkpoints (see MetaModelBridge.AgentSession).</summary>
    public interface IAgentSession
    {
        string AgentScopeDenial(string objectType, string objectName);
        void BeforeAgentWrite(string objectType, string objectName);
        string CheckpointEntriesJson(string id);
        string RevertCheckpointJson(string id);
    }

    /// <summary>The services the request dispatcher routes to (composed in Program).</summary>
    public sealed class BridgeServices
    {
        public IObjectStore Objects { get; set; }
        public IMetadataDiscovery Discovery { get; set; }
        public IElementDesigner Designer { get; set; }
        public IProjectService Projects { get; set; }
        public IBuildService Builds { get; set; }
        public IModelService Models { get; set; }
        public ILabelService Labels { get; set; }
        public IProjectPackageService Packages { get; set; }
        public ITableRelations Relations { get; set; }
        public ITableDataShape DataShape { get; set; }
        public IPerformanceAnalysis Performance { get; set; }
        public IExtensionCompatibility Compatibility { get; set; }
        public IWritePolicy Policy { get; set; }
        /// <summary>Set only in the agent's (MCP) server process: its writes follow the user's scope and checkpoint.</summary>
        public IAgentSession AgentWrites { get; set; }
        /// <summary>Checkpoint listing and revert, for the extension's own process.</summary>
        public IAgentSession Checkpoints { get; set; }
    }
}
