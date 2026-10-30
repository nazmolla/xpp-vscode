using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;
using XppVsCode.Server.Host;

namespace XppVsCode.Server.MetaModel
{
    // Agent operations that Visual Studio offers through its UI: delete, exclude, project selection,
    // label files and menu extensions, plus checks that catch build errors before the build.
    public sealed partial class MetaModelBridge
    {
        private static readonly string[] SystemFields =
        {
            "RecId", "RecVersion", "DataAreaId", "Partition", "TableId", "CreatedDateTime", "ModifiedDateTime",
            "CreatedBy", "ModifiedBy", "CreatedTransactionId", "ModifiedTransactionId"
        };

        // ── Generic provider access ──

        private static System.Reflection.MethodInfo FindProviderMethod(Type type, string name, params Type[] args) =>
            type.GetMethod(name, args) ?? type.GetInterfaces().Select(i => i.GetMethod(name, args)).FirstOrDefault(m => m != null);

        /// <summary>The provider collection (Classes, Tables, LabelFiles, ...) whose Read returns <paramref name="objectType"/>.</summary>
        private object SubProvider(string objectType, out Type providerType)
        {
            providerType = null;
            var provider = MetadataProvider;
            if (provider == null || string.IsNullOrEmpty(objectType)) return null;
            foreach (var prop in typeof(IMetadataProvider).GetProperties())
            {
                var read = FindProviderMethod(prop.PropertyType, "Read", typeof(string));
                if (read == null || !string.Equals(read.ReturnType.Name, objectType, StringComparison.OrdinalIgnoreCase)) continue;
                providerType = prop.PropertyType;
                return prop.GetValue(provider);
            }
            return null;
        }

        private IEnumerable<string> NamesForType(string objectType)
        {
            object sub = SubProvider(objectType, out Type type);
            var keys = sub == null ? null : FindProviderMethod(type, "GetPrimaryKeys");
            return keys?.Invoke(sub, null) is IEnumerable names ? names.Cast<object>().Select(n => n.ToString()).ToList() : null;
        }

        private bool ObjectExists(string objectType, string objectName)
        {
            object sub = SubProvider(objectType, out Type type);
            var exists = sub == null ? null : FindProviderMethod(type, "Exists", typeof(string));
            return exists != null && (bool)exists.Invoke(sub, new object[] { objectName });
        }

        private string ModelOf(string objectType, string objectName)
        {
            object sub = SubProvider(objectType, out Type type);
            var getModelInfo = sub == null ? null : FindProviderMethod(type, "GetModelInfo", typeof(string));
            if (getModelInfo?.Invoke(sub, new object[] { objectName }) is IEnumerable models)
            {
                string name = models.OfType<ModelInfo>().FirstOrDefault()?.Name;
                if (name != null) return name;
            }
            return GetObjectModelName(objectType, objectName);
        }

        private static System.Reflection.MethodInfo SingleItemAddMethod(Type collectionType) =>
            collectionType.GetMethods()
                .Where(m => m.Name == "Add" && m.GetParameters().Length == 1)
                .OrderBy(m => m.GetParameters()[0].ParameterType == typeof(object) ? 1 : 0)
                .FirstOrDefault();

        // ── Default class declarations ──

        /// <summary>The compiler needs a class declaration on queries, forms, views and entities; Visual Studio always writes one.</summary>
        private static void EnsureDeclaration(object axObject, string objectType)
        {
            string name = axObject.GetType().GetProperty("Name")?.GetValue(axObject) as string;
            string declaration;
            switch (objectType)
            {
                case "AxQuery": declaration = "[Query]\npublic class " + name + " extends QueryRun\n{\n}\n"; break;
                case "AxForm": declaration = "[Form]\npublic class " + name + " extends FormRun\n{\n}\n"; break;
                default: declaration = "public class " + name + " extends common\n{\n}\n"; break;
            }

            object holder = axObject;
            var declarationProperty = axObject.GetType().GetProperty("Declaration");
            if (declarationProperty == null)
            {
                holder = axObject.GetType().GetProperty("SourceCode")?.GetValue(axObject);
                declarationProperty = holder?.GetType().GetProperty("Declaration");
            }

            // Queries and forms keep their declaration as a method named classDeclaration; the compiler looks it up by that key.
            if (objectType == "AxQuery" || objectType == "AxForm")
            {
                var methods = axObject.GetType().GetProperty("Methods")?.GetValue(axObject);
                if (methods is IEnumerable existing
                    && !existing.Cast<object>().Any(m => string.Equals(m.GetType().GetProperty("Name")?.GetValue(m) as string, "classDeclaration", StringComparison.OrdinalIgnoreCase)))
                {
                    string source = declarationProperty?.GetValue(holder) as string;
                    SingleItemAddMethod(methods.GetType())?.Invoke(methods, new object[]
                    {
                        new AxMethod { Name = "classDeclaration", Source = string.IsNullOrWhiteSpace(source) ? declaration : source }
                    });
                }
                if (declarationProperty != null && declarationProperty.CanWrite)
                    declarationProperty.SetValue(holder, null);
                return;
            }

            if (declarationProperty != null && declarationProperty.CanWrite
                && string.IsNullOrWhiteSpace(declarationProperty.GetValue(holder) as string))
                declarationProperty.SetValue(holder, declaration);
        }

        // ── Project membership of new objects ──

        private string ObsoleteBaseWarning(string objectType, string objectName)
        {
            if (!objectType.EndsWith("Extension") || objectName.IndexOf('.') <= 0) return "";
            string baseType = objectType.Substring(0, objectType.Length - "Extension".Length);
            string baseName = objectName.Substring(0, objectName.IndexOf('.'));
            object baseObject = ReadViaMetadataProvider(baseType, baseName, out _);
            string obsolete = baseObject?.GetType().GetProperty("IsObsolete")?.GetValue(baseObject)?.ToString();
            return obsolete == "Yes"
                ? "\nWARNING: " + baseType + " '" + baseName + "' is marked obsolete; every build will warn. Prefer its current replacement (find it with xpp_find_object)."
                : "";
        }

        private string AddCreatedObjectToProject(string objectType, string objectName)
        {
            var project = GetActiveProject();
            if (project == null)
                return "Not added to a project: there is no active project (select one with xpp_select_project, then use xpp_add_to_project).";
            if (objectType == "AxLabelFile")
                return "";

            string objectModel = ModelOf(objectType, objectName);
            if (!string.Equals(project.ModelName, objectModel, StringComparison.OrdinalIgnoreCase))
                return "Not added to project " + project.Caption + ": it targets model '" + project.ModelName + "', the object is in '" + objectModel + "'.";
            return project.AddItem(objectType, objectName)
                ? "Added to project " + project.Caption + "."
                : "Already in project " + project.Caption + ".";
        }

        // ── Delete / exclude ──

        public string DeleteObjectJson(string objectType, string objectName, bool checkOnly = false)
        {
            var json = new JavaScriptSerializer();
            object sub = SubProvider(objectType, out Type type);
            if (sub == null)
                return json.Serialize(Failure("Unknown object type '" + objectType + "'."));
            if (!ObjectExists(objectType, objectName))
                return json.Serialize(Failure(objectType + " '" + objectName + "' does not exist."));

            string model = ModelOf(objectType, objectName);
            if (!IsCustomModel(model))
                return json.Serialize(Failure(objectType + " '" + objectName + "' belongs to model '" + model
                    + "', which is not one of your custom models. Only objects of your own models can be deleted."));
            var saveInfo = GetModelSaveInfo(model);
            if (saveInfo == null)
                return json.Serialize(Failure("Cannot resolve model '" + model + "'."));

            string labelContent = objectType == "AxLabelFile" ? LabelContentPath(objectName) : null;
            string stagingHint = objectType == "AxDataEntityViewExtension" ? StagingExtensionHint(objectName) : "";
            var delete = FindProviderMethod(type, "Delete", typeof(string), typeof(ModelSaveInfo));
            if (delete == null)
                return json.Serialize(Failure("Objects of type " + objectType + " cannot be deleted through the metadata API."));
            if (checkOnly)
                return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = "Can be deleted." });
            RememberFieldsOfDeleted(objectType, objectName);
            delete.Invoke(sub, new object[] { objectName, saveInfo });
            if (labelContent != null && File.Exists(labelContent))
                File.Delete(labelContent);

            var project = GetActiveProject();
            bool removed = project != null && project.RemoveItem(objectType, objectName);
            _host.Reset();
            InvalidateCaches();

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = "Deleted " + objectType + " '" + objectName + "' from model " + model + "."
                    + (removed ? " Removed it from project " + project.Caption + "." : "") + stagingHint,
            });
        }

        public string RemoveFromProjectJson(string objectType, string objectName)
        {
            var json = new JavaScriptSerializer();
            var project = GetActiveProject();
            if (project == null)
                return json.Serialize(Failure("No active project. Select one with xpp_select_project."));
            bool removed = project.RemoveItem(objectType, objectName);
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = removed,
                ["message"] = removed
                    ? "Excluded " + objectType + " '" + objectName + "' from project " + project.Caption + ". It is still in the model."
                    : objectType + " '" + objectName + "' is not in project " + project.Caption + ".",
            });
        }

        // ── Project selection ──

        /// <summary>File the VS Code extension watches so its views follow an agent's project switch.</summary>
        public static string ActiveProjectStateFile =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XppVsCode", "activeProject.txt");

        public string ListProjectsJson(string searchFolder)
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(searchFolder)) roots.Add(searchFolder);
            if (!string.IsNullOrEmpty(_projectFile)) roots.Add(Path.GetDirectoryName(Path.GetDirectoryName(_projectFile)));
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "source", "repos"));

            var found = new SortedDictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (string file in FindProjectFiles(root, 3))
                {
                    if (found.ContainsKey(file)) continue;
                    var project = new XppProject(file, MetaService);
                    found[file] = new Dictionary<string, object>
                    {
                        ["name"] = project.Caption,
                        ["projectFile"] = file,
                        ["model"] = project.ModelName ?? "",
                        ["active"] = string.Equals(file, _projectFile, StringComparison.OrdinalIgnoreCase),
                    };
                }

            var text = new StringBuilder();
            text.AppendLine(found.Count + " D365FO project(s) found:");
            foreach (var p in found.Values)
                text.AppendLine(((bool)p["active"] ? "* " : "  ") + p["name"] + "  model=" + p["model"] + "  " + p["projectFile"]);
            if (found.Count == 0)
                text.AppendLine("Pass searchFolder with the folder that contains your .rnrproj files.");
            return new JavaScriptSerializer().Serialize(new Dictionary<string, object> { ["success"] = true, ["message"] = text.ToString().TrimEnd() });
        }

        private static IEnumerable<string> FindProjectFiles(string folder, int depth)
        {
            string[] files;
            try { files = Directory.GetFiles(folder, "*.rnrproj"); }
            catch { yield break; }
            foreach (string f in files) yield return f;
            if (depth <= 0) yield break;
            string[] dirs;
            try { dirs = Directory.GetDirectories(folder); }
            catch { yield break; }
            foreach (string d in dirs.Where(d => !Path.GetFileName(d).StartsWith(".") && !Path.GetFileName(d).Equals("bin", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(d).Equals("obj", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(d).Equals("node_modules", StringComparison.OrdinalIgnoreCase)))
                foreach (string f in FindProjectFiles(d, depth - 1)) yield return f;
        }

        public string SelectProjectJson(string projectFile)
        {
            var json = new JavaScriptSerializer();
            if (string.IsNullOrWhiteSpace(projectFile) || !File.Exists(projectFile) || !projectFile.EndsWith(".rnrproj", StringComparison.OrdinalIgnoreCase))
                return json.Serialize(Failure("'" + projectFile + "' is not an existing .rnrproj file. List candidates with xpp_select_project without projectFile."));

            var project = new XppProject(Path.GetFullPath(projectFile), MetaService);
            var model = project.GetProjectsModelInfo();
            if (model == null)
                return json.Serialize(Failure("Project " + project.Caption + " targets model '" + project.ModelName + "', which does not exist in this environment."));

            _projectFile = project.FullName;
            InvalidateCaches();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ActiveProjectStateFile));
                File.WriteAllText(ActiveProjectStateFile, _projectFile);
            }
            catch { }

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["message"] = "Active project: " + project.Caption + " (model " + model.Name + ", " + (IsCustomModel(model.Name) ? "custom" : "NOT custom") + ")\n" + _projectFile,
            });
        }

        // ── Label files ──

        private string LabelContentPath(string labelFileObjectName)
        {
            var labelFile = MetaService.GetLabelFile(labelFileObjectName);
            return labelFile == null || string.IsNullOrEmpty(labelFile.RelativeUriInModelStore) || string.IsNullOrEmpty(CustomFolder)
                ? null
                : Path.Combine(CustomFolder, labelFile.RelativeUriInModelStore);
        }

        /// <summary>Creates a label file the way Visual Studio does: one AxLabelFile per language and an empty text resource each.</summary>
        private MetaModelResult CreateLabelFile(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string id = req.ObjectName;
            var languages = new List<string>();
            string languageList = null;
            if (req.Properties != null && !req.Properties.TryGetValue("Languages", out languageList))
                req.Properties.TryGetValue("Language", out languageList);
            int underscore = id.IndexOf('_');
            if (underscore > 0)
            {
                if (string.IsNullOrEmpty(languageList)) languageList = id.Substring(underscore + 1);
                id = id.Substring(0, underscore);
            }
            languages.AddRange((languageList ?? "en-US").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

            if (!System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9]*$"))
                return Fail("Label file ID '" + id + "' must start with a letter and contain only letters and digits.");

            string modelName = ResolveTargetModelName(req.ModelName);
            var model = modelName == null ? null : MetaService.GetModel(modelName);
            if (model == null || !IsCustomModel(model.Name))
                return Fail("Label files can only be created in your own model; '" + (modelName ?? "<unresolved>") + "' is not one. Pass modelName.");
            if (string.IsNullOrEmpty(CustomFolder))
                return Fail("No custom metadata folder in the active configuration.");

            var created = new List<string>();
            foreach (string language in languages)
            {
                string name = id + "_" + language;
                if (MetaService.GetLabelFile(name) != null)
                {
                    created.Add(name + " (already existed)");
                    continue;
                }
                string fileName = id + "." + language + ".label.txt";
                string relative = Path.Combine(model.Module, model.Name, "AxLabelFile", "LabelResources", language, fileName);
                string full = Path.Combine(CustomFolder, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                if (!File.Exists(full)) File.WriteAllText(full, "", new UTF8Encoding(true));

                MetaService.CreateLabelFile(new AxLabelFile
                {
                    Name = name,
                    LabelFileId = id,
                    Language = language,
                    LabelContentFileName = fileName,
                    RelativeUriInModelStore = relative,
                }, saveInfo);
                created.Add(name);
                var project = GetActiveProject();
                if (project != null && string.Equals(project.ModelName, model.Name, StringComparison.OrdinalIgnoreCase))
                    project.AddItem("AxLabelFile", name);
            }

            return Ok("Created label file " + id + " for " + string.Join(", ", languages) + ": " + string.Join(", ", created)
                + ". Add labels with xpp_create_label (labelFileId '" + id + "'); reference them as @" + id + ":<LabelId>.");
        }

        // ── Menu extensions ──

        private MetaModelResult CreateMenuExtension(CreateObjectRequest req, ModelSaveInfo saveInfo)
        {
            string error = ValidateNewExtension(req, "AxMenu",
                baseName => MetaService.GetMenu(baseName) != null,
                () => MetadataProvider.MenuExtensions.Exists(req.ObjectName));
            if (error != null) return Fail(error);

            var extension = new AxMenuExtension { Name = req.ObjectName };
            string report = ApplyMenuElements(extension, req.MenuElements, out error);
            if (error != null) return Fail(error);

            MetadataProvider.MenuExtensions.Create(extension, saveInfo);
            return Ok("Created AxMenuExtension '" + req.ObjectName + "'." + report);
        }

        private MetaModelResult UpdateMenuExtension(UpdateObjectRequest req)
        {
            var extension = MetadataProvider.MenuExtensions.Read(req.ObjectName);
            if (extension == null) return Fail("AxMenuExtension '" + req.ObjectName + "' not found.");
            var saveInfo = GetExtensionSaveInfo("AxMenuExtension", req.ObjectName, out string error);
            if (saveInfo == null) return Fail(error);
            if (!string.IsNullOrWhiteSpace(req.TypedMetadataJson))
                return Fail("typedMetadata is not supported for AxMenuExtension; pass menuElements (name, menuItemName, menuItemType, parent) instead. Nothing was saved.");

            string report = ApplyMenuElements(extension, req.MenuElements, out error);
            if (error != null) return Fail(error);

            MetadataProvider.MenuExtensions.Update(extension, saveInfo);
            return Ok("Updated AxMenuExtension '" + req.ObjectName + "'." + report);
        }

        private string ApplyMenuElements(AxMenuExtension extension, List<MenuElementDto> elements, out string error)
        {
            error = null;
            if (elements == null || elements.Count == 0) return "";

            string baseName = extension.Name.Substring(0, extension.Name.IndexOf('.'));
            var menu = MetaService.GetMenu(baseName);
            var submenus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectSubmenus(menu, submenus, 0);

            var report = new StringBuilder("\nMenu elements:");
            foreach (var e in elements)
            {
                string type = (e.MenuItemType ?? "Display").Trim();
                if (type.StartsWith("MenuItem", StringComparison.OrdinalIgnoreCase)) type = type.Substring(8);
                if (!new[] { "Display", "Output", "Action" }.Contains(type, StringComparer.OrdinalIgnoreCase))
                {
                    error = "menuElements '" + e.Name + "': menuItemType must be Display, Output or Action.";
                    return null;
                }
                string itemName = string.IsNullOrEmpty(e.MenuItemName) ? e.Name : e.MenuItemName;
                if (!ObjectExists("AxMenuItem" + type, itemName))
                {
                    error = "menuElements '" + e.Name + "': menu item " + type + " '" + itemName + "' does not exist. Create it first.";
                    return null;
                }
                if (!string.IsNullOrEmpty(e.Parent) && !submenus.Contains(e.Parent))
                {
                    error = "menuElements '" + e.Name + "': submenu '" + e.Parent + "' is not on menu " + baseName
                        + ". Submenus: " + string.Join(", ", submenus.OrderBy(s => s).Take(40)) + ".";
                    return null;
                }

                var element = new AxMenuExtensionElement();
                var menuItem = new AxMenuElementMenuItem { Name = e.Name, MenuItemName = itemName };
                ApplyProperties(menuItem, new Dictionary<string, string> { ["MenuItemType"] = type });
                // Name forwards to MenuElement, so the element must be set first.
                element.MenuElement = menuItem;
                element.Name = e.Name;
                element.Parent = e.Parent ?? "";
                element.PreviousSibling = e.PreviousSibling ?? "";
                string position = string.Equals(e.Position, "after", StringComparison.OrdinalIgnoreCase) ? "AfterItem"
                    : string.Equals(e.Position, "begin", StringComparison.OrdinalIgnoreCase) ? "Begin" : "End";
                ApplyProperties(element, new Dictionary<string, string> { ["PositionType"] = position });

                var existing = extension.Elements.FirstOrDefault(x => string.Equals(x.Name, e.Name, StringComparison.OrdinalIgnoreCase));
                if (existing != null) extension.Elements.Remove(existing);
                extension.Elements.Add(element);
                report.Append("\n  - ").Append(e.Name).Append(": MenuItem").Append(type).Append(' ').Append(itemName)
                    .Append(" in ").Append(string.IsNullOrEmpty(e.Parent) ? baseName : e.Parent).Append(" (").Append(position).Append(')');
            }
            return report.ToString();
        }

        private static void CollectSubmenus(object node, HashSet<string> names, int depth)
        {
            if (node == null || depth > 8) return;
            if (!(node.GetType().GetProperty("Elements")?.GetValue(node) is IEnumerable children)) return;
            foreach (object child in children)
            {
                if (child == null) continue;
                if (child.GetType().Name.Contains("SubMenu") || child.GetType().Name.Contains("MenuReference"))
                {
                    if (child.GetType().GetProperty("Name")?.GetValue(child) is string name) names.Add(name);
                    CollectSubmenus(child, names, depth + 1);
                }
            }
        }

        // ── Relations ──

        /// <summary>Checks that related tables and both sides of every constraint exist (the compiler would reject them otherwise).</summary>
        private string ValidateRelations(string tableName, IEnumerable<FieldDto> newFields, List<RelationDto> relations)
        {
            if (relations == null || relations.Count == 0) return null;
            var errors = new List<string>();
            var added = new HashSet<string>((newFields ?? Enumerable.Empty<FieldDto>()).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var relation in relations)
            {
                if (string.IsNullOrEmpty(relation.RelatedTable) || MetaService.GetTable(relation.RelatedTable) == null)
                {
                    errors.Add("Relation '" + relation.Name + "': related table '" + relation.RelatedTable + "' does not exist.");
                    continue;
                }
                foreach (var c in relation.Constraints ?? new List<RelationConstraintDto>())
                {
                    if (!string.IsNullOrEmpty(c.Field) && !added.Contains(c.Field) && !TableHasField(tableName, c.Field))
                        errors.Add("Relation '" + relation.Name + "': field '" + c.Field + "' is not on " + tableName + " (or its extensions, or this request's fields).");
                    if (!string.IsNullOrEmpty(c.RelatedField) && !TableHasField(relation.RelatedTable, c.RelatedField))
                        errors.Add("Relation '" + relation.Name + "': field '" + c.RelatedField + "' is not on " + relation.RelatedTable + ".");
                }
            }
            return errors.Count == 0 ? null : "Nothing was saved:\n  - " + string.Join("\n  - ", errors);
        }

        private bool TableHasField(string tableName, string fieldName)
        {
            if (SystemFields.Contains(fieldName, StringComparer.OrdinalIgnoreCase)) return true;
            var table = MetaService.GetTable(tableName);
            if (table == null) return false;
            if (table.Fields.Any(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase))) return true;
            foreach (string extension in NamesForType("AxTableExtension") ?? Enumerable.Empty<string>())
            {
                if (!extension.StartsWith(tableName + ".", StringComparison.OrdinalIgnoreCase)) continue;
                var ext = MetaService.GetTableExtension(extension);
                if (ext?.Fields.Any(f => string.Equals(f.Name, fieldName, StringComparison.OrdinalIgnoreCase)) == true) return true;
            }
            // Tables that inherit (SupportInheritance) carry the base table's fields.
            return !string.IsNullOrEmpty(table.Extends) && TableHasField(table.Extends, fieldName);
        }

        // ── Model descriptor ──

        private static long? DescriptorModelId(ModelInfo model)
        {
            var config = DynamicsConfigurationReader.ReadActiveConfiguration();
            foreach (string root in config.MetadataRoots())
            {
                string descriptor = Path.Combine(root, model.Module ?? model.Name, "Descriptor", model.Name + ".xml");
                if (!File.Exists(descriptor)) continue;
                try
                {
                    var id = System.Xml.Linq.XDocument.Load(descriptor).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Id");
                    if (id != null && long.TryParse(id.Value, out long value)) return value;
                }
                catch { }
            }
            return null;
        }
    }
}
