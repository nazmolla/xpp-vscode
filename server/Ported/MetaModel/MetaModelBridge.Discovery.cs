using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Core.Collections;
using Microsoft.Dynamics.AX.Metadata.Core.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Service;
using XppVsCode.Server.Host;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>Finding and listing elements, type schemas and proxies.</summary>
    public sealed partial class MetaModelBridge
    {
        // ── Discovery ──

        // Scans run on the VS UI thread; must stay well under the bridge's 12s dispatch timeout.
        private const int SearchTimeBudgetMs = 6000;

        private static string BudgetExceededMessage(int found) =>
            "Search stopped after " + (SearchTimeBudgetMs / 1000) + "s with " + found
            + " result(s); results are PARTIAL. Narrow the search: pass objectType and a longer, more specific name.";

        public FindObjectResult FindObject(string objectName, string objectType, bool exactMatch, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new FindObjectResult { Message = "MetaModel service not available." };

            var result = new FindObjectResult { Success = true };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool budgetExceeded = false;

            try
            {
                // Collect names of every type first, then rank: exact name, then names starting with the
                // text, then shorter names. Stopping at the first maxResults hits buried e.g. the SalesTable table
                // under dozens of SalesTable* classes.
                const int candidateCap = 5000;
                string needle = (objectName ?? "").Trim();
                var candidates = new List<KeyValuePair<string, string>>();
                var typeSearchers = GetTypeSearchers(objectType);
                foreach (var searcher in typeSearchers)
                {
                    try
                    {
                        foreach (string name in searcher.GetNames())
                        {
                            if (sw.ElapsedMilliseconds > SearchTimeBudgetMs)
                            {
                                budgetExceeded = true;
                                break;
                            }

                            bool match = exactMatch
                                ? name.Equals(needle, StringComparison.OrdinalIgnoreCase)
                                : name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

                            if (match)
                            {
                                candidates.Add(new KeyValuePair<string, string>(searcher.TypeName, name));
                                if (candidates.Count >= candidateCap) break;
                            }
                        }
                        if (candidates.Count >= candidateCap || budgetExceeded) break;
                    }
                    catch (Exception typeEx)
                    {
                        // Log per-type error but continue searching other types
                        // This allows partial results even if one metadata extractor fails
                        System.Diagnostics.Debug.WriteLine("FindObject type search error for type '" + searcher.TypeName + "': " + typeEx.Message);
                    }
                }

                var ranked = candidates
                    .OrderBy(c => c.Value.Equals(needle, StringComparison.OrdinalIgnoreCase) ? 0
                        : c.Value.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
                    .ThenBy(c => c.Value.Length)
                    .ThenBy(c => c.Value, StringComparer.OrdinalIgnoreCase)
                    .Take(maxResults);
                foreach (var c in ranked)
                {
                    string modelName = GetObjectModelName(c.Key, c.Value);
                    result.Matches.Add(new FoundObject
                    {
                        ObjectType = c.Key,
                        ObjectName = c.Value,
                        ModelName = modelName,
                        IsCustom = IsCustomModel(modelName)
                    });
                }
            }
            catch (Exception ex)
            {
                string svcContext = MetaService == null ? "[MetaService unavailable]" : "[MetaService active]";
                result.Success = false;
                result.Message = ex is ArgumentException
                    ? ex.Message
                    : "Search error in FindObject (searching for '" + objectName + "' in type '" + objectType + "'): " + svcContext + " - " + ex.Message;
            }

            // Security objects are named by their label in requirements: match the label text too (#46).
            if (!exactMatch && result.Success && !string.IsNullOrEmpty(objectType) && LabelSearchTypes.Contains(objectType)
                && !string.IsNullOrWhiteSpace(objectName))
            {
                var index = SecurityLabelIndex(objectType);
                foreach (var m in result.Matches)
                    if (index.TryGetValue(m.ObjectName, out string own)) m.Label = own;
                var hits = FindSecurityByLabel(objectType, objectName.Trim(), maxResults, out bool allWords);
                if (hits.Count > 0 && !allWords && result.Matches.Count == 0)
                    result.Message = "No " + objectType + " name or label contains all of '" + objectName.Trim()
                        + "'; closest labels below. Requirements often use other wording: check the label and the duties/privileges before choosing.";
                foreach (var hit in hits)
                {
                    if (result.Matches.Count >= maxResults) break;
                    if (result.Matches.Any(m => string.Equals(m.ObjectName, hit.Key, StringComparison.OrdinalIgnoreCase))) continue;
                    string modelName = GetObjectModelName(objectType, hit.Key);
                    result.Matches.Add(new FoundObject { ObjectType = objectType, ObjectName = hit.Key, ModelName = modelName, IsCustom = IsCustomModel(modelName), Label = hit.Value });
                }
            }

            if (budgetExceeded)
                result.Message = BudgetExceededMessage(result.Matches.Count);
            else if (result.Matches.Count == 0 && result.Success && result.Message == null)
                result.Message = "No objects found matching '" + objectName + "'."
                    + (string.IsNullOrEmpty(objectType) ? "" : " (type: " + objectType + ")");

            return result;
        }

        public ListObjectsResult ListObjects(string modelName, string objectType, string nameFilter, int maxResults)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new ListObjectsResult { Message = "MetaModel service not available." };

            var result = new ListObjectsResult { Success = true };
            bool hasNameFilter = !string.IsNullOrEmpty(nameFilter);
            bool hasModelFilter = !string.IsNullOrEmpty(modelName);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool budgetExceeded = false;

            try
            {
                var typeSearchers = GetTypeSearchers(objectType);
                foreach (var searcher in typeSearchers)
                {
                    foreach (string name in searcher.GetNames())
                    {
                        if (sw.ElapsedMilliseconds > SearchTimeBudgetMs)
                        {
                            budgetExceeded = true;
                            break;
                        }

                        // Apply cheap name filter FIRST to avoid expensive model lookups
                        if (hasNameFilter &&
                            name.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;

                        // Only do the expensive model lookup when needed
                        string objModel = null;
                        if (hasModelFilter)
                        {
                            objModel = GetObjectModelName(searcher.TypeName, name);
                            if (!modelName.Equals(objModel, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }

                        // Lazy-resolve model name if not already fetched
                        if (objModel == null)
                            objModel = GetObjectModelName(searcher.TypeName, name);

                        result.Objects.Add(new FoundObject
                        {
                            ObjectType = searcher.TypeName,
                            ObjectName = name,
                            ModelName = objModel,
                            IsCustom = IsCustomModel(objModel)
                        });

                        if (result.Objects.Count >= maxResults) break;
                    }
                    if (result.Objects.Count >= maxResults || budgetExceeded) break;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = ex is ArgumentException ? ex.Message : "List error: " + ex.Message;
            }

            if (budgetExceeded)
                result.Message = BudgetExceededMessage(result.Objects.Count);

            return result;
        }

        public ObjectTypeSchemaResult GetObjectTypeSchema(string objectType)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (string.IsNullOrWhiteSpace(objectType))
                return new ObjectTypeSchemaResult
                {
                    Success = false,
                    ObjectType = objectType,
                    Message = "objectType is required."
                };

            try
            {
                Type metadataType = ResolveMetadataType(objectType);
                if (metadataType == null)
                {
                    return new ObjectTypeSchemaResult
                    {
                        Success = false,
                        ObjectType = objectType,
                        Message = "Unsupported or unknown object type: " + objectType
                    };
                }

                var visited = new HashSet<Type>();
                object schema = BuildTypeSchema(metadataType, 0, visited);

                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 64
                };

                return new ObjectTypeSchemaResult
                {
                    Success = true,
                    ObjectType = objectType,
                    SchemaJson = serializer.Serialize(schema),
                    Message = "Schema generated for " + metadataType.Name
                        + (metadataType == typeof(AxEdt)
                            ? ". AxEdt is abstract: create EDTs with properties.EdtType = String, Int, Int64, Real, Enum, Date, UtcDateTime, Time, Guid or Container "
                              + "(or Extends = a parent EDT, whose kind is used). Kind-specific properties (StringSize, EnumType, NoOfDecimals, ...): request the schema of AxEdtString, AxEdtEnum, AxEdtReal, ..."
                            : "")
                };
            }
            catch (Exception ex)
            {
                return new ObjectTypeSchemaResult
                {
                    Success = false,
                    ObjectType = objectType,
                    Message = "Schema generation failed: " + Cause(ex)
                };
            }
        }

        public ProxyGenerationResult GenerateProxies(ProxyGenerationRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var normalized = request ?? new ProxyGenerationRequest();
            var selectedTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

            var requested = (normalized.ObjectTypes != null && normalized.ObjectTypes.Count > 0)
                ? normalized.ObjectTypes
                : GetDefaultProxyObjectTypes();

            foreach (var objectType in requested)
            {
                if (string.IsNullOrWhiteSpace(objectType))
                    continue;

                var metadataType = ResolveMetadataType(objectType.Trim());
                if (metadataType != null)
                    selectedTypes[objectType.Trim()] = metadataType;
            }

            if (selectedTypes.Count == 0)
            {
                return new ProxyGenerationResult
                {
                    Success = false,
                    Message = "No valid object types were provided for proxy generation."
                };
            }

            var result = MetaModelProxyGenerator.Generate(selectedTypes, normalized);
            if (!result.Success)
                return result;

            string outputPath = normalized.OutputFilePath;
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = ResolveDefaultProxyOutputPath();

            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                try
                {
                    var directory = Path.GetDirectoryName(outputPath);
                    if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    File.WriteAllText(outputPath, result.GeneratedCode ?? string.Empty, Encoding.UTF8);
                    result.OutputFilePath = outputPath;
                    result.Message = result.Message + " Output written to '" + outputPath + "'.";
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.Message = "Proxy code generated but failed to write output file: " + ex.Message;
                }
            }
            else
            {
                result.Message = result.Message + " Generated in memory only (no writable project path detected).";
            }

            return result;
        }

        private string ResolveDefaultProxyOutputPath()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var project = GetActiveProject();
                string projectFile = project?.FullName;
                if (string.IsNullOrWhiteSpace(projectFile))
                    return null;

                string projectDirectory = Path.GetDirectoryName(projectFile);
                if (string.IsNullOrWhiteSpace(projectDirectory))
                    return null;

                return Path.Combine(projectDirectory, "MetaModel", "Generated", "MetadataProxies.g.cs");
            }
            catch
            {
                return null;
            }
        }

        private static object BuildTypeSchema(Type type, int depth, HashSet<Type> visited)
        {
            if (type == null || depth > 8) return null;

            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (IsSimpleType(underlying))
                return MapSimpleTypeToSchema(underlying);

            // Prevent cyclic loops in type graph
            if (!underlying.IsValueType && !visited.Add(underlying))
                return new Dictionary<string, object>
                {
                    { "kind", "object" },
                    { "type", underlying.Name },
                    { "recursive", true }
                };

            if (underlying != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(underlying))
            {
                Type itemType = GetEnumerableItemType(underlying) ?? typeof(object);
                return new Dictionary<string, object>
                {
                    { "kind", "array" },
                    { "item", BuildTypeSchema(itemType, depth + 1, visited) }
                };
            }

            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in PublicProperties(underlying))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                // Read-only scalars (e.g. AxEnum.HelpText, an alias of Help) cannot be set by create/update.
                Type propType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                if (!prop.CanWrite && (IsSimpleType(propType) || propType.IsEnum)) continue;
                var propSchema = BuildTypeSchema(prop.PropertyType, depth + 1, new HashSet<Type>(visited));
                if (propSchema != null)
                    properties[prop.Name] = propSchema;
            }

            return new Dictionary<string, object>
            {
                { "kind", "object" },
                { "type", underlying.Name },
                { "properties", properties }
            };
        }

        private static Type GetEnumerableItemType(Type enumerableType)
        {
            if (enumerableType.IsArray)
                return enumerableType.GetElementType();

            if (enumerableType.IsGenericType)
            {
                var args = enumerableType.GetGenericArguments();
                if (args.Length == 1) return args[0];
            }

            Type ienum = enumerableType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType
                    && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return ienum?.GetGenericArguments().FirstOrDefault();
        }

        private static List<string> GetDefaultProxyObjectTypes()
        {
            return new List<string>
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
                "AxConfigurationKey"
            };
        }

        private static Dictionary<string, object> MapSimpleTypeToSchema(Type type)
        {
            if (type == typeof(string))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "string" } };
            if (type == typeof(bool))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "boolean" } };
            if (type == typeof(int) || type == typeof(long) || type == typeof(short))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "integer" } };
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "number" } };
            if (type == typeof(DateTime))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "datetime" } };
            if (type == typeof(Guid))
                return new Dictionary<string, object> { { "kind", "scalar" }, { "type", "guid" } };
            if (type.IsEnum)
                return new Dictionary<string, object>
                {
                    { "kind", "enum" },
                    { "type", type.Name },
                    { "values", Enum.GetNames(type) }
                };

            return new Dictionary<string, object> { { "kind", "scalar" }, { "type", type.Name } };
        }

        public MetaModelResult AddToProject(string objectType, string objectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var projectNode = GetActiveProjectNode();
                if (projectNode == null)
                    return Fail("No active D365FO project. Select one with xpp_select_project.");

                if (!objectType.StartsWith("Ax", StringComparison.Ordinal))
                    return Fail("Unsupported object type for project add: " + objectType);
                if (SubProvider(objectType, out _) != null && !ObjectExists(objectType, objectName))
                    return Fail(objectType + " '" + objectName + "' does not exist.");

                // D365FO projects belong to one model; adding another model's objects causes build/designer errors.
                var projectModel = projectNode.GetProjectsModelInfo();
                string objectModel = ModelOf(objectType, objectName);
                if (projectModel != null && !string.IsNullOrEmpty(objectModel)
                    && !string.Equals(projectModel.Name, objectModel, StringComparison.OrdinalIgnoreCase))
                {
                    return Fail(objectType + " '" + objectName + "' belongs to model '" + objectModel
                        + "', but the active project targets model '" + projectModel.Name
                        + "'. Not added. Ask the user to set the project's Model property to '" + objectModel
                        + "' or open a project for that model.");
                }

                bool added = projectNode.AddItem(objectType, objectName);
                InvalidateCaches();

                return new MetaModelResult
                {
                    Success = true,
                    Message = added
                        ? "Added " + objectType + " '" + objectName + "' to project " + projectNode.Caption + "."
                        : objectType + " '" + objectName + "' is already in project " + projectNode.Caption + "."
                };
            }
            catch (Exception ex)
            {
                return Fail("Add to project failed: " + Cause(ex));
            }
        }

        public bool HasActiveProject => GetActiveProject() != null;

        public List<ProjectItemInfo> ListProjectItems()
        {
            try
            {
                var project = GetActiveProject();
                if (project == null) return new List<ProjectItemInfo>();
                var model = project.GetProjectsModelInfo();
                var items = project.GetItems();
                // The project only links the objects; point at where the metadata file really is.
                if (model != null && !string.IsNullOrEmpty(CustomFolder))
                    foreach (var item in items.Where(i => i.ObjectType != null))
                        item.FilePath = Path.Combine(CustomFolder, model.Module, model.Name, item.ObjectType, item.Name + ".xml");
                return items;
            }
            catch
            {
                return new List<ProjectItemInfo>();
            }
        }

        // ── Type name search helpers ──

        private List<TypeSearcher> GetTypeSearchers(string objectType)
        {
            var searchers = new List<TypeSearcher>();

            if (string.IsNullOrEmpty(objectType) || objectType == "AxClass")
                searchers.Add(new TypeSearcher("AxClass", () => MetaService.GetClassNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxTable")
                searchers.Add(new TypeSearcher("AxTable", () => MetaService.GetTableNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxForm")
                searchers.Add(new TypeSearcher("AxForm", () => MetaService.GetFormNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxEnum")
                searchers.Add(new TypeSearcher("AxEnum", () => MetaService.GetEnumNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxView")
                searchers.Add(new TypeSearcher("AxView", () => MetaService.GetViewNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxQuery")
                searchers.Add(new TypeSearcher("AxQuery", () => MetaService.GetQueryNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxEdt")
                searchers.Add(new TypeSearcher("AxEdt", () => MetaService.GetExtendedDataTypeNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxMenuItemDisplay")
                searchers.Add(new TypeSearcher("AxMenuItemDisplay", () => MetaService.GetMenuItemDisplayNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxMenuItemOutput")
                searchers.Add(new TypeSearcher("AxMenuItemOutput", () => MetaService.GetMenuItemOutputNames()));
            if (string.IsNullOrEmpty(objectType) || objectType == "AxMenuItemAction")
                searchers.Add(new TypeSearcher("AxMenuItemAction", () => MetaService.GetMenuItemActionNames()));

            // Any other type (data entities, security, extensions, label files, ...) through its provider.
            if (searchers.Count == 0 && !string.IsNullOrEmpty(objectType))
            {
                var names = NamesForType(objectType);
                if (names == null)
                    throw new ArgumentException("Unknown object type '" + objectType + "'. Use a metadata type name such as AxClass, AxTable, AxDataEntityView, AxSecurityRole or AxTableExtension.");
                searchers.Add(new TypeSearcher(objectType, () => names));
            }

            return searchers;
        }

        private sealed class TypeSearcher
        {
            public string TypeName { get; }
            private readonly Func<IEnumerable<string>> _getNamesFunc;

            public TypeSearcher(string typeName, Func<IEnumerable<string>> getNamesFunc)
            {
                TypeName = typeName;
                _getNamesFunc = getNamesFunc;
            }

            public IEnumerable<string> GetNames() => _getNamesFunc();
        }

        // ── Cross-Reference search ──

        // Where-used is answered from the cross-reference database by XRefQuery; this is only reached when that fails.
        public FindReferencesResult FindReferences(string objectType, string objectName, string memberName, string referenceKind, int maxResults)
        {
            return new FindReferencesResult
            {
                Success = false,
                Message = "Cross-reference search needs the cross-reference database named in the active configuration."
            };
        }
    }
}
