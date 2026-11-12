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
    /// <summary>
    /// Wraps the D365FO IMetaModelService loaded headlessly from disk by <see cref="MetadataHost"/>.
    /// All metadata operations use strongly-typed APIs — no raw XML manipulation.
    /// Calls are serialized by <see cref="MetaModelBridgeServer"/>.
    /// </summary>
    public sealed partial class MetaModelBridge : IObjectStore, IMetadataDiscovery, IElementDesigner, IProjectService
    {
        private readonly MetadataHost _host;
        private readonly LabelStore _labels;
        private string _projectFile;

        internal MetaModelBridge(MetadataHost host, LabelStore labels, string projectFile)
        {
            _host = host;
            _labels = labels;
            _projectFile = projectFile;
        }
        
        // Cache for GetObjectModelName with 60-second TTL
        private class CachedModelName
        {
            public string ModelName { get; set; }
            public DateTime CachedAt { get; set; }
        }
        private System.Collections.Generic.Dictionary<string, CachedModelName> _objectModelNameCache =
            new System.Collections.Generic.Dictionary<string, CachedModelName>();
        private const int ModelNameCacheTtlSeconds = 60;

        private void InvalidateCaches()
        {
            _objectModelNameCache.Clear();
            _elementCacheKey = null;
            _typeNames.Clear();
        }

        private IMetaModelService MetaService => _host.MetaService;

        private IMetadataProvider MetadataProvider => _host.Provider;

        // What ApplyTableChanges did in the current update (reported after a successful save).
        private List<string> _tableChangeReport;

        // ── Object CRUD ──

        private static bool HasTypedUpdateSupport(string objectType) => ElementType(objectType)?.UpdateAfterCreate == true;

        public MetaModelResult CreateObject(CreateObjectRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return Fail("MetaModel service not available. Is D365FO tools extension loaded?");

            var saveInfo = GetModelSaveInfo(request.ModelName);
            if (saveInfo == null)
                return Fail("Could not resolve model '" + request.ModelName + "'. Ensure a D365FO project is open.");

            try
            {
                string preError = PreValidateCreate(request);
                if (preError != null) return Fail(preError);
                if (request.ObjectType != "AxLabelFile" && ObjectExists(request.ObjectType, request.ObjectName))
                    return Fail(request.ObjectType + " '" + request.ObjectName + "' already exists (model "
                        + ModelOf(request.ObjectType, request.ObjectName) + "). Nothing was changed; use xpp_update_object to modify it.");

                if (request.ObjectType == "AxTable" || request.ObjectType == "AxTableExtension")
                {
                    string tableName = request.ObjectType == "AxTable" ? request.ObjectName : request.ObjectName.Split('.')[0];
                    string relationError = ValidateRelations(tableName, request.Fields, request.Relations);
                    if (relationError != null) return Fail(relationError);
                    string fieldError = ValidateTableChanges(request.ObjectType, request.ObjectName, request.Fields, null);
                    if (fieldError != null) return Fail(fieldError);
                }

                var create = ElementType(request.ObjectType)?.Create;
                MetaModelResult result = create != null ? create(this, request, saveInfo) : Fail("Unsupported object type: " + request.ObjectType);

                if (result.Success)
                {
                    // Force MetaModel refresh so the Update path can find the new object.
                    _host.Reset();
                    InvalidateCaches();

                    // Apply all metadata through UpdateObject so Create and Update
                    // share a single code path for declaration, methods, properties,
                    // fields, indexes, relations, etc.
                    if (HasTypedUpdateSupport(request.ObjectType))
                    {
                        var updateResult = UpdateObject(request.ToUpdateRequest());
                        if (!updateResult.Success)
                        {
                            // Never leave a half-created object behind.
                            string rollback = DeleteObjectJson(request.ObjectType, request.ObjectName);
                            bool rolledBack = rollback.Contains("\"success\":true");
                            return Fail("Could not create " + request.ObjectType + " '" + request.ObjectName + "': "
                                + updateResult.Message + (rolledBack ? " Nothing was saved." : " The blank object could not be removed: " + rollback));
                        }

                        // Keep the update's report (e.g. form placement verification) visible to the caller.
                        int reportStart = updateResult.Message?.IndexOf('\n') ?? -1;
                        if (reportStart >= 0)
                            result.Message += updateResult.Message.Substring(reportStart);
                    }

                    result.Message += "\n" + AddCreatedObjectToProject(request.ObjectType, request.ObjectName);
                    result.Message += ObsoleteBaseWarning(request.ObjectType, request.ObjectName);
                }
                return result;
            }
            catch (Exception ex)
            {
                return Fail("Create failed: " + Cause(ex));
            }
        }

        public MetaModelResult UpdateObject(UpdateObjectRequest request)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return Fail("MetaModel service not available.");

            try
            {
                if (request.ObjectType == "AxTable" || request.ObjectType == "AxTableExtension")
                {
                    string tableName = request.ObjectType == "AxTable" ? request.ObjectName : request.ObjectName.Split('.')[0];
                    string relationError = ValidateRelations(tableName, request.Fields, request.Relations);
                    if (relationError != null) return Fail(relationError);
                    string fieldError = ValidateTableChanges(request.ObjectType, request.ObjectName, request.Fields, request);
                    if (fieldError != null) return Fail(fieldError);
                }
                _tableChangeReport = null;

                var update = ElementType(request.ObjectType)?.Update;
                MetaModelResult result = update != null ? update(this, request) : UpdateGenericByTypedApi(request);

                // Force cache refresh so subsequent ReadObject returns updated metadata
                // (MetaService.Update* writes to disk but may not hydrate child collections in cache)
                if (result.Success)
                {
                    _host.Reset();
                    InvalidateCaches();
                    if (_tableChangeReport != null && _tableChangeReport.Count > 0)
                        result.Message += "\n" + string.Join("\n", _tableChangeReport);
                }

                return result;
            }
            catch (Exception ex)
            {
                // Reflection wraps the real error ("Exception has been thrown by the target of an invocation").
                var inner = ex;
                while (inner is System.Reflection.TargetInvocationException && inner.InnerException != null) inner = inner.InnerException;
                return Fail("Update failed: " + inner.Message);
            }
        }

        public ReadObjectResult ReadObject(string objectType, string objectName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (MetaService == null)
                return new ReadObjectResult { Message = "MetaModel service not available." };

            try
            {
                var result = new ReadObjectResult { ObjectType = objectType, ObjectName = objectName, Success = true };

                var read = ElementType(objectType)?.Read;
                var failure = read != null ? read(this, result, objectType, objectName) : ReadGeneric(result, objectType, objectName);
                if (failure != null) return failure;

                result.IsCustom = IsCustomModel(result.ModelName);
                return result;
            }
            catch (Exception ex)
            {
                return new ReadObjectResult { Message = "Read failed: " + Cause(ex) };
            }
        }

        public ReadObjectResult ReadObjectByPath(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
                return new ReadObjectResult { Message = "filePath is required." };

            // Derive type and name from the path structure:
            // <root>\<Package>\<Model>\AxClass\MyClass.axclass → type=AxClass, name=MyClass
            string objectName = Path.GetFileNameWithoutExtension(filePath);
            string folder = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "";

            if (!string.IsNullOrEmpty(folder) && folder.StartsWith("Ax", StringComparison.OrdinalIgnoreCase))
            {
                var result = ReadObject(folder, objectName);
                if (result != null)
                {
                    result.FilePath = filePath;
                    return result;
                }
            }

            return new ReadObjectResult
            {
                Message = "Cannot read type '" + folder + "' via the MetaModel API. "
                        + "Use xpp_read_object with objectType and objectName instead."
            };
        }

        // Every IMetadataProvider sub-provider has Read(string) returning its Ax type; find the one for objectType.
        private object ReadViaMetadataProvider(string objectType, string objectName, out string modelName)
        {
            modelName = null;
            var provider = MetadataProvider;
            if (provider == null || string.IsNullOrEmpty(objectType)) return null;

            foreach (var prop in typeof(IMetadataProvider).GetProperties())
            {
                var read = prop.PropertyType.GetMethod("Read", new[] { typeof(string) })
                    ?? prop.PropertyType.GetInterfaces().Select(i => i.GetMethod("Read", new[] { typeof(string) })).FirstOrDefault(m => m != null);
                if (read == null || !string.Equals(read.ReturnType.Name, objectType, StringComparison.OrdinalIgnoreCase))
                    continue;

                object subProvider = prop.GetValue(provider);
                if (subProvider == null) return null;
                object axObj = read.Invoke(subProvider, new object[] { objectName });
                if (axObj == null) return null;

                var getModelInfo = subProvider.GetType().GetMethod("GetModelInfo", new[] { typeof(string) });
                if (getModelInfo?.Invoke(subProvider, new object[] { objectName }) is System.Collections.IEnumerable models)
                    modelName = models.OfType<ModelInfo>().FirstOrDefault()?.Name;
                return axObj;
            }
            return null;
        }

        private static void PopulateReadResultFromGenericObject(ReadObjectResult result, string objectType, object axObj)
        {
            if (result == null || axObj == null) return;

            result.Properties = ExtractProperties(axObj);
            result.TypedMetadataJson = BuildFullGraphJson(axObj) ?? BuildTypedMetadataJson(objectType, axObj);

            var declarationProp = axObj.GetType().GetProperty("Declaration");
            result.Declaration = declarationProp?.GetValue(axObj) as string;

            if (string.IsNullOrEmpty(result.Declaration))
            {
                var sourceCodeProp = axObj.GetType().GetProperty("SourceCode");
                var sourceCode = sourceCodeProp?.GetValue(axObj);
                if (sourceCode != null)
                {
                    var srcDeclProp = sourceCode.GetType().GetProperty("Declaration");
                    result.Declaration = srcDeclProp?.GetValue(sourceCode) as string;
                }
            }

            var methodsProp = axObj.GetType().GetProperty("Methods");
            var methods = methodsProp?.GetValue(axObj) as System.Collections.IEnumerable;
            if (methods != null)
            {
                foreach (var m in methods)
                {
                    if (m == null) continue;
                    string name = m.GetType().GetProperty("Name")?.GetValue(m) as string;
                    string source = m.GetType().GetProperty("Source")?.GetValue(m) as string;
                    if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(source))
                        result.Methods.Add(new MethodInfo { Name = name, Source = source });
                }
            }
        }

        private static string BuildTypedMetadataJson(string objectType, object axObj)
        {
            if (axObj == null) return null;
            try
            {
                var serializer = new JavaScriptSerializer
                {
                    MaxJsonLength = int.MaxValue,
                    RecursionLimit = 64
                };

                Type proxyType = ResolveProxyType(objectType);
                if (proxyType != null)
                {
                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                    object proxy = MapSourceToTargetType(axObj, proxyType, 0, visited);
                    return serializer.Serialize(proxy);
                }

                var visitedFallback = new HashSet<object>(ReferenceEqualityComparer.Instance);
                object graph = ExtractMetadataGraph(axObj, 0, visitedFallback);
                return serializer.Serialize(graph);
            }
            catch
            {
                return null;
            }
        }

        private static object ExtractMetadataGraph(object value, int depth, HashSet<object> visited, Type declaredType = null)
        {
            if (value == null) return null;
            if (depth > 12) return null;

            Type t = value.GetType();
            Type nt = Nullable.GetUnderlyingType(t) ?? t;

            if (nt.IsEnum) return value.ToString();
            if (nt.IsPrimitive || nt == typeof(string) || nt == typeof(decimal)
                || nt == typeof(DateTime) || nt == typeof(Guid))
                return value;

            if (!t.IsValueType)
            {
                if (!visited.Add(value)) return null;
            }

            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                Type itemType = t.IsGenericType ? t.GetGenericArguments().LastOrDefault() : null;
                var list = new List<object>();
                foreach (object item in enumerable)
                {
                    object extracted = ExtractMetadataGraph(item, depth + 1, visited, itemType);
                    if (extracted != null) list.Add(extracted);
                }
                return list;
            }

            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            // Polymorphic members (menu elements, controls, fields): name the concrete type so its extra properties make sense.
            if (declaredType != null && declaredType != t)
                dict["$type"] = t.Name;
            foreach (var prop in PublicProperties(t))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (prop.Name == "Parent" && !(value is AxMenuExtensionElement)) continue;
                if (prop.Name == "Owner") continue;

                object propValue;
                try { propValue = prop.GetValue(value); }
                catch { continue; }

                object extracted = ExtractMetadataGraph(propValue, depth + 1, visited, prop.PropertyType);
                if (extracted != null) dict[prop.Name] = extracted;
            }

            return dict;
        }

        // Full object graph (all properties, including those of subtypes) as JSON.
        private static string BuildFullGraphJson(object axObj)
        {
            try
            {
                var serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 };
                return serializer.Serialize(ExtractMetadataGraph(axObj, 0, new HashSet<object>(ReferenceEqualityComparer.Instance)));
            }
            catch
            {
                return null;
            }
        }

        private static Type ResolveProxyType(string objectType)
        {
            if (string.IsNullOrWhiteSpace(objectType)) return null;
            string fullName = "XppVsCode.Server.MetaModel.Generated." + objectType + "Proxy";

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false, true);
                if (t != null) return t;
            }

            return null;
        }

        private static object MapSourceToTargetType(object source, Type targetType, int depth, HashSet<object> visited)
        {
            if (targetType == null || depth > 12) return null;

            Type targetUnderlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
            if (source == null)
            {
                if (targetUnderlying.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
                    return Activator.CreateInstance(targetUnderlying);
                return null;
            }

            if (IsSimpleType(targetUnderlying))
                return ConvertSimpleValue(source, targetUnderlying) ?? source?.ToString();

            if (!source.GetType().IsValueType)
            {
                if (!visited.Add(source)) return null;
            }

            if (targetUnderlying != typeof(string)
                && typeof(System.Collections.IEnumerable).IsAssignableFrom(targetUnderlying))
            {
                Type itemType = GetEnumerableItemType(targetUnderlying) ?? typeof(object);
                var list = new List<object>();
                if (source is System.Collections.IEnumerable srcEnumerable)
                {
                    foreach (object item in srcEnumerable)
                    {
                        object mapped = MapSourceToTargetType(item, itemType, depth + 1, visited);
                        if (mapped != null) list.Add(mapped);
                    }
                }

                object targetList = Activator.CreateInstance(typeof(List<>).MakeGenericType(itemType));
                var add = targetList.GetType().GetMethod("Add");
                foreach (object item in list) add?.Invoke(targetList, new[] { item });
                return targetList;
            }

            object target = Activator.CreateInstance(targetUnderlying);
            var srcType = source.GetType();
            foreach (var tp in PublicProperties(targetUnderlying))
            {
                if (!tp.CanRead || tp.GetIndexParameters().Length > 0) continue;

                var sp = srcType.GetProperty(tp.Name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (sp == null || !sp.CanRead || sp.GetIndexParameters().Length > 0) continue;

                object srcValue;
                try { srcValue = sp.GetValue(source); }
                catch { continue; }

                object mapped = MapSourceToTargetType(srcValue, tp.PropertyType, depth + 1, visited);
                SetPropertyValue(tp, target, mapped);
            }

            return target;
        }

        private static void SetPropertyValue(PropertyInfo property, object target, object value)
        {
            try
            {
                var setter = property.GetSetMethod(true);
                if (setter != null)
                {
                    setter.Invoke(target, new[] { value });
                    return;
                }
            }
            catch { }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) => ReferenceEquals(x, y);
            public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }

        // ── Environment ──

        public EnvironmentInfo GetEnvironmentInfo()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var info = new EnvironmentInfo();

            try
            {
                var config = DynamicsConfigurationReader.ReadActiveConfiguration();
                info.CustomMetadataFolder = config.CustomMetadataFolder;
                info.ReferenceMetadataFolders = config.ReferenceMetadataFolders;

                var projectNode = GetActiveProjectNode();
                if (projectNode != null)
                {
                    info.ActiveProjectName = projectNode.Caption;
                    var modelInfo = projectNode.GetProjectsModelInfo();
                    if (modelInfo != null)
                    {
                        info.ActiveModelName = modelInfo.Name;
                        info.ActiveModelId = modelInfo.Id;
                        info.ActiveModelLayer = modelInfo.Layer.ToString();
                    }
                }
            }
            catch (Exception ex) { LogIgnored("Reading the active project's model", ex); }

            return info;
        }

        // ── Utility methods ──

        private void AddMethods(IList<AxMethod> target, string[] sources, bool formatCode)
        {
            if (sources == null) return;
            foreach (string src in sources)
            {
                if (string.IsNullOrWhiteSpace(src)) continue;
                string clean = PrepareSource(src, formatCode);
                string name = ExtractMethodName(clean);
                target.Add(new AxMethod { Name = name, Source = clean });
            }
        }

        private void UpdateMethods(IList<AxMethod> existing, string[] upserts, string[] removals, bool formatCode)
        {
            if (removals != null)
            {
                foreach (string removeName in removals)
                {
                    var toRemove = existing.FirstOrDefault(m =>
                        m.Name.Equals(removeName, StringComparison.OrdinalIgnoreCase));
                    if (toRemove != null) existing.Remove(toRemove);
                }
            }

            if (upserts != null)
            {
                foreach (string src in upserts)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    string clean = PrepareSource(src, formatCode);
                    string name = ExtractMethodName(clean);
                    if (formatCode) clean = IndentMethodSource(clean);
                    var found = existing.FirstOrDefault(m =>
                        m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (found != null)
                    {
                        found.Source = clean;
                    }
                    else
                    {
                        existing.Add(new AxMethod { Name = name, Source = clean });
                    }
                }
            }
        }

        private static string PrepareSource(string source, bool formatCode)
        {
            string clean = StripCData(source);
            return formatCode ? NormalizeXppWhitespace(clean) : clean;
        }

        /// <summary>
        /// Visual Studio stores a method one level inside the class (4 spaces) after a leading newline;
        /// sources written at column 0 are shifted so the code view and the compiler's line numbers match.
        /// </summary>
        private static string IndentMethodSource(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return source;
            var lines = source.Replace("\r\n", "\n").Split('\n').ToList();
            while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
            int minIndent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ').Length).DefaultIfEmpty(0).Min();
            if (minIndent >= 4) return "\n" + string.Join("\n", lines);
            string pad = new string(' ', 4 - minIndent);
            return "\n" + string.Join("\n", lines.Select(l => l.Trim().Length == 0 ? "" : pad + l));
        }

        private static string NormalizeXppWhitespace(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return source;

            string normalized = source.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalized.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Replace("\t", "    ");
                lines[i] = line.TrimEnd();
            }
            return string.Join("\n", lines).TrimEnd();
        }

        // The identifier before the first '(' after comments, [attributes] (possibly multi-line) and #macro lines.
        private static string ExtractMethodName(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) return "unknownMethod";
            string text = source.Replace("\r\n", "\n");
            int i = 0;
            while (true)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
                if (i >= text.Length) break;
                if (string.CompareOrdinal(text, i, "//", 0, 2) == 0 || (text[i] == '#' && IsMacroLine(text, i)))
                {
                    int end = text.IndexOf('\n', i);
                    i = end < 0 ? text.Length : end + 1;
                }
                else if (text[i] == '#')
                {
                    // A #word on the signature line (e.g. #obsolete) acts as a modifier.
                    for (i++; i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_'); i++) { }
                }
                else if (string.CompareOrdinal(text, i, "/*", 0, 2) == 0)
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 2;
                }
                else if (text[i] == '[')
                {
                    i = SkipBracketed(text, i);
                }
                else break;
            }
            int paren = text.IndexOf('(', Math.Min(i, text.Length));
            int brace = text.IndexOf('{', Math.Min(i, text.Length));
            // A class-style header ("public class X extends Y {") is how forms store their declaration.
            if (brace >= 0 && (paren < 0 || brace < paren)) return "classDeclaration";
            if (paren <= i) return "unknownMethod";
            string[] tokens = text.Substring(i, paren - i).Trim().Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 ? tokens[tokens.Length - 1] : "unknownMethod";
        }

        // Macro directives (#define.X(..), #if, #localmacro, ...) and macro library lines (#SysAdmin) take the whole line.
        private static bool IsMacroLine(string text, int start)
        {
            int end = text.IndexOf('\n', start);
            string line = text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
            return System.Text.RegularExpressions.Regex.IsMatch(line,
                @"^#(define|localmacro|globalmacro|endmacro|if|ifnot|endif|macrolib|undef|globaldefine|localdefine)\b|^#\w+\.\w+|^#\w+\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static int SkipBracketed(string text, int start)
        {
            int depth = 0;
            for (int i = start; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '"' || c == '\'')
                {
                    for (i++; i < text.Length && text[i] != c; i++)
                        if (text[i] == '\\') i++;
                }
                else if (c == '[') depth++;
                else if (c == ']' && --depth == 0) return i + 1;
            }
            return text.Length;
        }

        private ModelSaveInfo GetModelSaveInfo(string modelName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // If modelName specified, try to resolve it
            if (!string.IsNullOrEmpty(modelName))
            {
                var info = MetaService.GetModel(modelName);
                if (info != null)
                    return new ModelSaveInfo { Id = info.Id, Layer = info.Layer };
            }

            // Fallback: use active project's model
            var projectNode = GetActiveProjectNode();
            if (projectNode != null)
            {
                var projectModel = projectNode.GetProjectsModelInfo();
                if (projectModel != null)
                    return new ModelSaveInfo { Id = projectModel.Id, Layer = projectModel.Layer };
            }

            return null;
        }

        private string ResolveTargetModelName(string modelName)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (!string.IsNullOrEmpty(modelName))
            {
                var info = MetaService.GetModel(modelName);
                if (info != null)
                    return info.Name;
            }

            return GetActiveProjectNode()?.GetProjectsModelInfo()?.Name;
        }

        private ModelSaveInfo GetModelSaveInfoForObject(string objectType, string objectName)
        {
            string modelName = GetObjectModelName(objectType, objectName);
            return !string.IsNullOrEmpty(modelName) ? GetModelSaveInfo(modelName) : GetModelSaveInfo(null);
        }

        private string GetObjectModelName(string objectType, string objectName)
        {
            // Create cache key
            string cacheKey = objectType + ":" + objectName;
            
            // Check cache first
            if (_objectModelNameCache.TryGetValue(cacheKey, out CachedModelName cached))
            {
                // Check if cache is still fresh (within TTL)
                if ((DateTime.Now - cached.CachedAt).TotalSeconds < ModelNameCacheTtlSeconds)
                {
                    return cached.ModelName;
                }
                else
                {
                    // Expired cache entry - remove it
                    _objectModelNameCache.Remove(cacheKey);
                }
            }
            
            try
            {
                ModelInfo modelInfo = null;

                string typeKey = objectType.StartsWith("Ax", StringComparison.OrdinalIgnoreCase)
                    ? objectType.Substring(2)
                    : objectType;

                var methodCandidates = new List<string>
                {
                    "Get" + typeKey + "ModelInfo",
                    "Get" + typeKey + "sModelInfo"
                };

                if (string.Equals(objectType, "AxEdt", StringComparison.OrdinalIgnoreCase))
                    methodCandidates.Insert(0, "GetExtendedDataTypeModelInfo");
                if (string.Equals(objectType, "AxQuery", StringComparison.OrdinalIgnoreCase))
                    methodCandidates.Insert(0, "GetQueryModelInfo");
                if (string.Equals(objectType, "AxDataEntityView", StringComparison.OrdinalIgnoreCase))
                    methodCandidates.Insert(0, "GetDataEntityViewModelInfo");

                foreach (string methodName in methodCandidates.Distinct(StringComparer.Ordinal))
                {
                    var method = MetaService.GetType().GetMethod(methodName, new[] { typeof(string) });
                    if (method == null) continue;

                    object raw = method.Invoke(MetaService, new object[] { objectName });
                    if (raw == null) continue;

                    if (raw is System.Collections.IEnumerable enumerable)
                    {
                        foreach (var item in enumerable)
                        {
                            modelInfo = item as ModelInfo;
                            if (modelInfo != null) break;
                        }
                    }
                    else
                    {
                        modelInfo = raw as ModelInfo;
                    }

                    if (modelInfo != null) break;
                }

                // The service has no model lookup for some types (e.g. data entity extensions); every provider does.
                string modelName = modelInfo?.Name;
                if (modelName == null)
                    ReadViaMetadataProvider(objectType, objectName, out modelName);
                
                // Cache the result
                _objectModelNameCache[cacheKey] = new CachedModelName
                {
                    ModelName = modelName,
                    CachedAt = DateTime.Now
                };
                
                return modelName;
            }
            catch { return null; }
        }

        private XppProject GetActiveProjectNode() => GetActiveProject();

        private XppProject GetActiveProject() =>
            !string.IsNullOrEmpty(_projectFile) && File.Exists(_projectFile) ? new XppProject(_projectFile, MetaService) : null;

        private static Type ResolveMetadataType(string objectType)
        {
            switch (objectType)
            {
                case "AxClass": return typeof(AxClass);
                case "AxTable": return typeof(AxTable);
                case "AxForm": return typeof(AxForm);
                case "AxEdt": return typeof(AxEdt);
                case "AxEnum": return typeof(AxEnum);
                case "AxView": return typeof(AxView);
                case "AxQuery": return typeof(AxQuerySimple);
                case "AxMenu": return typeof(AxMenu);
                case "AxTile": return typeof(AxTile);
                case "AxMenuItemDisplay": return typeof(AxMenuItemDisplay);
                case "AxMenuItemOutput": return typeof(AxMenuItemOutput);
                case "AxMenuItemAction": return typeof(AxMenuItemAction);
                case "AxDataEntityView": return typeof(AxDataEntityView);
                case "AxSecurityPrivilege": return typeof(AxSecurityPrivilege);
                case "AxSecurityDuty": return typeof(AxSecurityDuty);
                case "AxSecurityRole": return typeof(AxSecurityRole);
                case "AxService": return typeof(AxService);
                case "AxServiceGroup": return typeof(AxServiceGroup);
                case "AxConfigurationKey": return typeof(AxConfigurationKey);
                case "AxTableExtension": return typeof(AxTableExtension);
                case "AxEnumExtension": return typeof(AxEnumExtension);
                case "AxEdtExtension": return typeof(AxEdtExtension);
                case "AxFormExtension": return typeof(AxFormExtension);

                default:
                    // Concrete kinds such as AxEdtString / AxEdtEnum, menu extensions, label files, ...
                    return typeof(AxEdt).Assembly.GetTypes().FirstOrDefault(t => t.IsPublic && !t.IsAbstract
                        && t.Namespace == typeof(AxEdt).Namespace && t.Name.Equals(objectType, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
