using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;
using Microsoft.Dynamics.AX.Metadata.Service;
using Microsoft.Dynamics.AX.Metadata.Storage;
using Microsoft.Dynamics.AX.Metadata.Storage.DiskProvider;
using XppVsCode.Server.Analysis;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// Upgrade readiness of a model's extensions against the installed platform and application: Chain of Command wrappers
    /// whose target method is gone, changed its signature or is no longer wrappable; event handlers whose target is gone;
    /// metadata extensions of elements, fields, field groups and controls that no longer exist.
    /// </summary>
    internal sealed class ExtensionCompatibilityService : IExtensionCompatibility
    {
        // Methods every table, form and form data source has from the kernel; wrapping them needs no X++ override.
        private static readonly HashSet<string> KernelTableMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "insert", "update", "delete", "validateWrite", "validateDelete", "validateField", "validateFieldValue", "modifiedField", "modifiedFieldValue",
            "initValue", "postLoad", "aosValidateInsert", "aosValidateUpdate", "aosValidateDelete", "aosValidateRead", "defaultField", "defaultRow",
            "renamePrimaryKey", "toolTipRecord", "toolTipField", "caption", "clear", "merge", "reread", "write", "doInsert", "doUpdate", "doDelete",
        };
        private static readonly HashSet<string> KernelFormMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "init", "run", "close", "canClose", "closeOk", "closeCancel", "activate", "task", "firstField", "lastField", "resize", "selectControl", "loadUserSetting", "saveUserSetting",
        };
        private static readonly HashSet<string> KernelDataSourceMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "init", "executeQuery", "active", "write", "validateWrite", "validateDelete", "create", "delete", "linkActive", "research", "refresh", "reread",
            "initValue", "selectionChanged", "markChanged", "leaveRecord", "cacheAddMethod", "first", "last", "next", "prev", "filter", "findRecord", "removeFilter",
        };

        private readonly IMetadataCore _core;
        private readonly IObjectStore _objects;

        public ExtensionCompatibilityService(IMetadataCore core, IObjectStore objects)
        {
            _core = core;
            _objects = objects;
        }

        private sealed class Finding
        {
            public string Severity, Rule, ObjectType, ObjectName, Member, Message;
        }

        private const int MaxDriftSource = 20000;

        /// <summary>
        /// Clashes between a model's extensions and the other installed models' extensions of the same elements: the same
        /// field, index or field group added to a table twice, the same enum value, form controls added twice or both
        /// changing one control, and methods several of your models wrap (their order is undefined).
        /// </summary>
        public string ExtensionCollisionsJson(string modelName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string folder = ModelFolder(modelName);
            if (folder == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Model '" + modelName + "' is not one of your models in the custom metadata folder." });
            var findings = new List<Finding>();
            void Add(string severity, string rule, string type, string name, string member, string message) =>
                findings.Add(new Finding { Severity = severity, Rule = rule, ObjectType = type, ObjectName = name, Member = member, Message = message });
            string ModelOf(string type, string name) => _objects.ReadObject(type, name)?.ModelName ?? "?";
            int checkedExtensions = 0;

            foreach (string name in Names(folder, "AxTableExtension"))
            {
                var mine = _core.MetaService.GetTableExtension(name);
                if (mine == null || name.IndexOf('.') <= 0) continue;
                checkedExtensions++;
                string baseName = name.Substring(0, name.IndexOf('.'));
                var myNames = new Dictionary<string, HashSet<string>>
                {
                    ["field"] = Set(mine.Fields?.Select(f => f.Name)),
                    ["index"] = Set(mine.Indexes?.Select(i => i.Name)),
                    ["field group"] = Set(mine.FieldGroups?.Select(g => g.Name)),
                };
                foreach (string other in MetaModelBridge.GetExtensionNames(_core.MetadataProvider.TableExtensions.GetPrimaryKeys(), baseName).Where(o => !o.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    var ext = _core.MetadataProvider.TableExtensions.Read(other);
                    if (ext == null) continue;
                    var theirs = new Dictionary<string, IEnumerable<string>>
                    {
                        ["field"] = ext.Fields?.Select(f => f.Name), ["index"] = ext.Indexes?.Select(i => i.Name), ["field group"] = ext.FieldGroups?.Select(g => g.Name),
                    };
                    foreach (var kind in theirs)
                        foreach (string clash in (kind.Value ?? Enumerable.Empty<string>()).Where(n => myNames[kind.Key].Contains(n)))
                            Add("Error", "DuplicateTableExtensionMember", "AxTableExtension", name, clash,
                                "Table " + baseName + ": " + other + " (model " + ModelOf("AxTableExtension", other) + ") also adds " + kind.Key + " " + clash + "; the packages can't be deployed together.");
                }
            }

            foreach (string name in Names(folder, "AxEnumExtension"))
            {
                var mine = _core.MetadataProvider.EnumExtensions.Read(name);
                if (mine == null || name.IndexOf('.') <= 0) continue;
                checkedExtensions++;
                string baseName = name.Substring(0, name.IndexOf('.'));
                var myValues = Set(mine.EnumValues?.Select(v => v.Name));
                foreach (string other in MetaModelBridge.GetExtensionNames(_core.MetadataProvider.EnumExtensions.GetPrimaryKeys(), baseName).Where(o => !o.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    foreach (string clash in (_core.MetadataProvider.EnumExtensions.Read(other)?.EnumValues?.Select(v => v.Name) ?? Enumerable.Empty<string>()).Where(myValues.Contains))
                        Add("Error", "DuplicateEnumValue", "AxEnumExtension", name, clash,
                            "Enum " + baseName + ": " + other + " (model " + ModelOf("AxEnumExtension", other) + ") also adds value " + clash + ".");
            }

            foreach (string name in Names(folder, "AxFormExtension"))
            {
                var mine = _core.MetaService.GetFormExtension(name);
                if (mine == null || name.IndexOf('.') <= 0) continue;
                checkedExtensions++;
                string baseName = name.Substring(0, name.IndexOf('.'));
                var myAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var added in mine.Controls ?? Enumerable.Empty<AxFormExtensionControl>())
                    FormExtensionMerger.Collect(added.FormControl, c => myAdded.Add(c));
                var myModified = Set(Names(mine, "ControlModifications"));
                foreach (string other in MetaModelBridge.GetExtensionNames(_core.MetadataProvider.FormExtensions.GetPrimaryKeys(), baseName).Where(o => !o.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    var ext = _core.MetaService.GetFormExtension(other);
                    if (ext == null) continue;
                    var theirAdded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var added in ext.Controls ?? Enumerable.Empty<AxFormExtensionControl>())
                        FormExtensionMerger.Collect(added.FormControl, c => theirAdded.Add(c));
                    foreach (string clash in theirAdded.Where(myAdded.Contains))
                        Add("Error", "DuplicateFormControl", "AxFormExtension", name, clash,
                            "Form " + baseName + ": " + other + " (model " + ModelOf("AxFormExtension", other) + ") also adds control " + clash + ".");
                    foreach (string clash in Names(ext, "ControlModifications").Where(myModified.Contains))
                        Add("Warning", "SameControlModified", "AxFormExtension", name, clash,
                            "Form " + baseName + ": " + other + " (model " + ModelOf("AxFormExtension", other) + ") also changes control " + clash + "; which change wins depends on the deployment.");
                }
            }

            // Chain of Command: methods your models wrap more than once (the order of the wrappers is not defined).
            var wrappers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string root = MetaModelBridge.CustomFolder;
            foreach (string package in Directory.Exists(root) ? Directory.EnumerateDirectories(root) : Enumerable.Empty<string>())
                foreach (string model in Directory.EnumerateDirectories(package))
                    foreach (string name in Names(model, "AxClass"))
                    {
                        var cls = _core.MetaService.GetClass(name);
                        var target = cls == null ? null : XppTarget.ParseExtensionOf(cls.Declaration);
                        if (target == null) continue;
                        foreach (var method in (cls.Methods ?? Enumerable.Empty<AxMethod>()).Where(m => XppTarget.IsWrapper(m.Source, m.Name)))
                        {
                            string key = Describe(target) + "." + method.Name;
                            if (!wrappers.TryGetValue(key, out var list)) wrappers[key] = list = new List<string>();
                            list.Add(cls.Name + " (" + Path.GetFileName(model) + ")");
                        }
                    }
            var myClasses = Set(Names(folder, "AxClass"));
            foreach (var group in wrappers.Where(w => w.Value.Count > 1 && w.Value.Any(v => myClasses.Contains(v.Split(' ')[0]))))
                Add("Info", "SeveralWrappers", "AxClass", group.Value.First(v => myClasses.Contains(v.Split(' ')[0])).Split(' ')[0], group.Key.Substring(group.Key.LastIndexOf('.') + 1),
                    group.Key + " is wrapped by " + string.Join(", ", group.Value) + ": the order they run in is not defined, so none may depend on another's changes.");

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true, ["model"] = modelName, ["extensions"] = checkedExtensions,
                ["findings"] = findings.OrderBy(f => f.Severity == "Error" ? 0 : f.Severity == "Warning" ? 1 : 2).ThenBy(f => f.ObjectName).Select(f => new Dictionary<string, object>
                {
                    ["severity"] = f.Severity, ["rule"] = f.Rule, ["objectType"] = f.ObjectType, ["objectName"] = f.ObjectName, ["member"] = f.Member, ["message"] = f.Message,
                }).ToList(),
            });
        }

        private static HashSet<string> Set(IEnumerable<string> names) => new HashSet<string>((names ?? Enumerable.Empty<string>()).Where(n => !string.IsNullOrEmpty(n)), StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Chain of Command wrappers whose target method's code differs between the installed application and an earlier
        /// PackagesLocalDirectory (read through its own disk provider): the wrapper still compiles, but the behaviour
        /// around next may have changed.
        /// </summary>
        public string WrapperDriftJson(string modelName, string previousFolder)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string folder = ModelFolder(modelName);
            if (folder == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Model '" + modelName + "' is not one of your models in the custom metadata folder." });
            if (string.IsNullOrWhiteSpace(previousFolder) || !Directory.Exists(previousFolder)
                || !string.Equals(Path.GetFileName(previousFolder.TrimEnd('\\', '/')), "PackagesLocalDirectory", StringComparison.OrdinalIgnoreCase))
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Pass the PackagesLocalDirectory folder of the earlier version." });

            var disk = new DiskProviderConfiguration();
            disk.AddMetadataPath(previousFolder);
            var previous = new MetaModelServiceFactory().Create(new MetadataProviderFactory().CreateDiskProvider(disk));

            var results = new List<Dictionary<string, object>>();
            int wrappers = 0, unchanged = 0, notFound = 0;
            foreach (string name in Names(folder, "AxClass"))
            {
                var cls = _core.MetaService.GetClass(name);
                var target = cls == null ? null : XppTarget.ParseExtensionOf(cls.Declaration);
                if (target == null) continue;
                foreach (var method in cls.Methods ?? Enumerable.Empty<AxMethod>())
                {
                    if (!XppTarget.IsWrapper(method.Source, method.Name)) continue;
                    wrappers++;
                    string now = FindMethodSource(_core.MetaService, target, method.Name);
                    string before = FindMethodSource(previous, target, method.Name);
                    if (now == null || before == null)
                    {
                        notFound++;
                        continue;
                    }
                    if (Normalize(now) == Normalize(before))
                    {
                        unchanged++;
                        continue;
                    }
                    if (results.Count >= 200) continue;
                    var nowLines = Lines(now);
                    var beforeLines = new HashSet<string>(Lines(before));
                    results.Add(new Dictionary<string, object>
                    {
                        ["className"] = cls.Name,
                        ["method"] = method.Name,
                        ["target"] = Describe(target),
                        ["changedLines"] = nowLines.Count(l => !beforeLines.Contains(l)) + Lines(before).Count(l => !nowLines.Contains(l)),
                        ["current"] = Cap(now),
                        ["previous"] = Cap(before),
                    });
                }
            }
            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true, ["model"] = modelName, ["wrappers"] = wrappers, ["unchanged"] = unchanged, ["notFound"] = notFound,
                ["changed"] = results.OrderByDescending(r => (int)r["changedLines"]).ToList(),
            });
        }

        private static string Cap(string source) => source.Length > MaxDriftSource ? source.Substring(0, MaxDriftSource) + "\n// ... truncated" : source;

        private static List<string> Lines(string source)
            => source.Replace("\r", "").Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " ")).Where(l => l.Length > 0).ToList();

        // Formatting-only changes (indentation, blank lines) are not drift.
        private static string Normalize(string source) => string.Join("\n", Lines(source));

        /// <summary>The code of the method a wrapper targets, from the given metadata (the element, its data source or its base classes).</summary>
        private static string FindMethodSource(IMetaModelService service, XppTarget target, string methodName)
        {
            IEnumerable<AxMethod> methods;
            switch (target.Kind.ToLowerInvariant())
            {
                case "classstr":
                    var all = new List<AxMethod>();
                    var cls = service.GetClass(target.Element);
                    for (int depth = 0; cls != null && depth < 30; depth++)
                    {
                        all.AddRange(cls.Methods ?? Enumerable.Empty<AxMethod>());
                        cls = string.IsNullOrEmpty(cls.Extends) ? null : service.GetClass(cls.Extends);
                    }
                    methods = all;
                    break;
                case "tablestr": methods = service.GetTable(target.Element)?.Methods; break;
                case "formstr": methods = service.GetForm(target.Element)?.Methods; break;
                case "dataentityviewstr": methods = service.GetDataEntityView(target.Element)?.Methods; break;
                case "viewstr": methods = service.GetView(target.Element)?.Methods; break;
                case "formdatasourcestr":
                    methods = service.GetForm(target.Element)?.DataSources?.FirstOrDefault(d => string.Equals(d.Name, target.Member, StringComparison.OrdinalIgnoreCase))?.Methods;
                    break;
                default: return null;
            }
            return methods?.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase))?.Source;
        }

        public string ExtensionCompatibilityJson(string modelName)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            string folder = ModelFolder(modelName);
            if (folder == null)
                return json.Serialize(new Dictionary<string, object> { ["success"] = false, ["message"] = "Model '" + modelName + "' is not one of your models in the custom metadata folder." });

            var findings = new List<Finding>();
            int wrappers = 0, handlers = 0, extensions = 0;
            foreach (string name in Names(folder, "AxClass"))
            {
                var cls = _core.MetaService.GetClass(name);
                if (cls == null) continue;
                var target = XppTarget.ParseExtensionOf(cls.Declaration);
                foreach (var method in cls.Methods ?? Enumerable.Empty<AxMethod>())
                {
                    var signature = XppSignature.Parse(method.Source);
                    foreach (var handler in XppTarget.ParseHandlers(signature))
                    {
                        handlers++;
                        CheckHandler(findings, cls.Name, method.Name, handler);
                    }
                    if (target != null && XppTarget.IsWrapper(method.Source, method.Name))
                    {
                        wrappers++;
                        CheckWrapper(findings, cls.Name, method.Name, signature, target);
                    }
                }
            }
            foreach (string type in Directory.EnumerateDirectories(folder, "Ax*Extension").Select(Path.GetFileName))
                foreach (string name in Names(folder, type))
                {
                    extensions++;
                    CheckMetadataExtension(findings, type, name);
                }

            return json.Serialize(new Dictionary<string, object>
            {
                ["success"] = true,
                ["model"] = modelName,
                ["wrappers"] = wrappers,
                ["handlers"] = handlers,
                ["extensions"] = extensions,
                ["findings"] = findings.OrderBy(f => f.Severity == "Error" ? 0 : f.Severity == "Warning" ? 1 : 2).ThenBy(f => f.ObjectName).Select(f => new Dictionary<string, object>
                {
                    ["severity"] = f.Severity, ["rule"] = f.Rule, ["objectType"] = f.ObjectType, ["objectName"] = f.ObjectName, ["member"] = f.Member, ["message"] = f.Message,
                }).ToList(),
            });
        }

        private void CheckWrapper(List<Finding> findings, string className, string methodName, XppSignature wrapper, XppTarget target)
        {
            void Add(string severity, string rule, string message) =>
                findings.Add(new Finding { Severity = severity, Rule = rule, ObjectType = "AxClass", ObjectName = className, Member = methodName, Message = message });

            string what = Describe(target);
            if (!TargetMethods(target, out var methods, out var kernel, out string missing))
            {
                Add("Error", "CocTargetMissing", missing);
                return;
            }
            var found = methods.FirstOrDefault(m => string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase));
            if (found == null)
            {
                if (kernel.Contains(methodName) || kernel.Contains("*")) return;
                Add("Error", "CocMethodMissing", what + " has no method " + methodName + " any more (also not in its base classes): the wrapper no longer compiles.");
                return;
            }
            var targetSignature = XppSignature.Parse(found.Source);
            if (targetSignature == null || wrapper == null)
            {
                Add("Info", "CocNotChecked", "The signature of " + what + "." + methodName + " could not be read; check it by hand.");
                return;
            }
            if (targetSignature.HasAttribute("Wrappable", "false") || targetSignature.HasAttribute("Hookable", "false"))
                Add("Error", "CocNotWrappable", what + "." + methodName + " is now [" + (targetSignature.HasAttribute("Hookable", "false") ? "Hookable(false)" : "Wrappable(false)") + "]: it can no longer be wrapped.");
            else if (targetSignature.Access == "private")
                Add("Error", "CocNotWrappable", what + "." + methodName + " is now private: it can no longer be wrapped.");
            else if (targetSignature.Modifiers.Contains("final") && !targetSignature.HasAttribute("Wrappable", "true"))
                Add("Error", "CocNotWrappable", what + "." + methodName + " is now final: it can no longer be wrapped.");
            else if (targetSignature.Access == "internal")
                Add("Warning", "CocInternal", what + "." + methodName + " is internal: it can only be wrapped from its own package.");
            string difference = XppSignature.Difference(wrapper, targetSignature);
            // Overrides of kernel methods (SalesLine.active(boolean = false)) can be wrapped with the kernel's signature.
            if (difference != null && !kernel.Contains(methodName))
                Add("Error", "CocSignatureChanged", "The signature of " + what + "." + methodName + " changed: " + difference + ". Update the wrapper's signature.");
            if (targetSignature.HasAttribute("SysObsolete"))
                Add("Warning", "CocTargetObsolete", what + "." + methodName + " is obsolete (" + targetSignature.Attributes.First(a => a.StartsWith("SysObsolete", StringComparison.OrdinalIgnoreCase)) + "); move the logic to its replacement.");
        }

        private void CheckHandler(List<Finding> findings, string className, string methodName, XppTarget handler)
        {
            void Add(string message) =>
                findings.Add(new Finding { Severity = "Error", Rule = "HandlerTargetMissing", ObjectType = "AxClass", ObjectName = className, Member = methodName, Message = message });

            if (handler.Attribute == "DataEventHandler")
            {
                if (_core.MetaService.GetTable(handler.Element) == null) Add("Table " + handler.Element + " no longer exists.");
                return;
            }
            var target = new XppTarget { Kind = handler.Kind, Element = handler.Element };
            if (!TargetMethods(target, out var methods, out var kernel, out string missing))
            {
                Add(missing);
                return;
            }
            var found = methods.FirstOrDefault(m => string.Equals(m.Name, handler.Member, StringComparison.OrdinalIgnoreCase));
            if (found == null && !kernel.Contains(handler.Member) && !kernel.Contains("*"))
                Add(Describe(target) + " has no " + (handler.Attribute == "SubscribesTo" ? "delegate " : "method ") + handler.Member + " any more.");
            else if (found != null && XppSignature.Parse(found.Source)?.HasAttribute("Hookable", "false") == true)
                Add(Describe(target) + "." + handler.Member + " is now [Hookable(false)]: pre/post handlers are no longer called.");
        }

        /// <summary>The methods a target can wrap: the element's own methods and, for classes, its base classes'.</summary>
        private bool TargetMethods(XppTarget target, out List<AxMethod> methods, out HashSet<string> kernel, out string missing)
        {
            methods = new List<AxMethod>();
            kernel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            missing = null;
            switch (target.Kind.ToLowerInvariant())
            {
                case "classstr":
                    var cls = _core.MetaService.GetClass(target.Element);
                    if (cls == null) { missing = "Class " + target.Element + " no longer exists."; return false; }
                    for (int depth = 0; cls != null && depth < 30; depth++)
                    {
                        methods.AddRange(cls.Methods ?? Enumerable.Empty<AxMethod>());
                        string baseName = cls.Extends;
                        cls = string.IsNullOrEmpty(baseName) ? null : _core.MetaService.GetClass(baseName);
                        // A kernel base class has no X++ metadata, so its methods can't be checked.
                        if (cls == null && !string.IsNullOrEmpty(baseName)) kernel.Add("*");
                    }
                    kernel.Add("new");
                    return true;
                case "tablestr":
                    var table = _core.MetaService.GetTable(target.Element);
                    if (table == null) { missing = "Table " + target.Element + " no longer exists."; return false; }
                    methods.AddRange(table.Methods ?? Enumerable.Empty<AxMethod>());
                    kernel.UnionWith(KernelTableMethods);
                    return true;
                case "dataentityviewstr":
                    var entity = _core.MetaService.GetDataEntityView(target.Element);
                    if (entity == null) { missing = "Data entity " + target.Element + " no longer exists."; return false; }
                    methods.AddRange(entity.Methods ?? Enumerable.Empty<AxMethod>());
                    kernel.UnionWith(KernelTableMethods);
                    return true;
                case "viewstr":
                    var view = _core.MetaService.GetView(target.Element);
                    if (view == null) { missing = "View " + target.Element + " no longer exists."; return false; }
                    methods.AddRange(view.Methods ?? Enumerable.Empty<AxMethod>());
                    kernel.UnionWith(KernelTableMethods);
                    return true;
                case "formstr":
                    var form = _core.MetaService.GetForm(target.Element);
                    if (form == null) { missing = "Form " + target.Element + " no longer exists."; return false; }
                    methods.AddRange(form.Methods ?? Enumerable.Empty<AxMethod>());
                    kernel.UnionWith(KernelFormMethods);
                    return true;
                case "formdatasourcestr":
                    var dsForm = _core.MetaService.GetForm(target.Element);
                    if (dsForm == null) { missing = "Form " + target.Element + " no longer exists."; return false; }
                    var ds = dsForm.DataSources?.FirstOrDefault(d => string.Equals(d.Name, target.Member, StringComparison.OrdinalIgnoreCase));
                    if (ds == null)
                    {
                        if (!FormExtensionsOf(target.Element).Any(e => Names(e, "DataSources").Contains(target.Member, StringComparer.OrdinalIgnoreCase)))
                        {
                            missing = "Form " + target.Element + " has no data source " + target.Member + " any more.";
                            return false;
                        }
                        // A data source another extension adds: its methods are not checked.
                        kernel.Add("*");
                        return true;
                    }
                    methods.AddRange(ds.Methods ?? Enumerable.Empty<AxMethod>());
                    kernel.UnionWith(KernelDataSourceMethods);
                    return true;
                default:
                    // Form fields and controls: only the element is checked.
                    if (target.Kind.StartsWith("form", StringComparison.OrdinalIgnoreCase) && _core.MetaService.GetForm(target.Element) == null)
                    {
                        missing = "Form " + target.Element + " no longer exists.";
                        return false;
                    }
                    kernel.Add("*");
                    return true;
            }
        }

        private void CheckMetadataExtension(List<Finding> findings, string type, string name)
        {
            void Add(string severity, string rule, string message) =>
                findings.Add(new Finding { Severity = severity, Rule = rule, ObjectType = type, ObjectName = name, Message = message });

            int dot = name.IndexOf('.');
            if (dot <= 0) return;
            string baseName = name.Substring(0, dot);
            string baseType = type == "AxQuerySimpleExtension" ? "AxQuery" : type.Substring(0, type.Length - "Extension".Length);
            var read = _objects.ReadObject(baseType, baseName);
            if (read == null || !read.Success)
            {
                Add("Error", "ExtensionBaseMissing", baseType.Substring(2) + " " + baseName + " no longer exists.");
                return;
            }
            switch (type)
            {
                case "AxTableExtension":
                    var table = _core.MetaService.GetTable(baseName);
                    var extension = _core.MetaService.GetTableExtension(name);
                    if (table == null || extension == null) return;
                    var fields = new HashSet<string>((table.Fields ?? Enumerable.Empty<AxTableField>()).Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    var groups = new HashSet<string>((table.FieldGroups ?? Enumerable.Empty<AxTableFieldGroup>()).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (string field in Names(extension, "FieldModifications").Where(f => !fields.Contains(f)))
                        Add("Error", "ExtensionFieldMissing", "It modifies field " + field + ", which " + baseName + " no longer has.");
                    foreach (string group in Names(extension, "FieldGroupExtensions").Where(g => !groups.Contains(g)))
                        Add("Error", "ExtensionFieldGroupMissing", "It adds fields to field group " + group + ", which " + baseName + " no longer has.");
                    break;
                case "AxFormExtension":
                    var form = _core.MetaService.GetForm(baseName);
                    var formExtension = _core.MetaService.GetFormExtension(name);
                    if (form == null || formExtension == null) return;
                    var controls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var control in form.Design?.Controls ?? Enumerable.Empty<AxFormControl>())
                        FormExtensionMerger.Collect(control, c => controls.Add(c));
                    // Controls this or other extensions add can be parents and anchors too.
                    foreach (var other in FormExtensionsOf(baseName))
                        foreach (var added in other.Controls ?? Enumerable.Empty<AxFormExtensionControl>())
                            FormExtensionMerger.Collect(added.FormControl, c => controls.Add(c));
                    foreach (string modified in Names(formExtension, "ControlModifications"))
                        if (!controls.Contains(modified))
                            Add("Error", "ExtensionControlMissing", "It changes control " + modified + ", which form " + baseName + " no longer has.");
                    foreach (var added in formExtension.Controls ?? Enumerable.Empty<AxFormExtensionControl>())
                    {
                        if (!string.IsNullOrEmpty(added.Parent) && !controls.Contains(added.Parent))
                            Add("Error", "ExtensionParentMissing", "Control " + added.FormControl?.Name + " goes into " + added.Parent + ", which form " + baseName + " no longer has.");
                        if (!string.IsNullOrEmpty(added.PreviousSibling) && !controls.Contains(added.PreviousSibling))
                            Add("Warning", "ExtensionAnchorMissing", "Control " + added.FormControl?.Name + " is placed after " + added.PreviousSibling + ", which form " + baseName + " no longer has.");
                    }
                    break;
                case "AxEnumExtension":
                    var enumType = _core.MetaService.GetEnum(baseName);
                    string extensible = enumType?.GetType().GetProperty("IsExtensible")?.GetValue(enumType)?.ToString();
                    if (extensible != null && extensible != "Yes" && extensible != "True")
                        Add("Error", "EnumNotExtensible", "Enum " + baseName + " is no longer extensible.");
                    break;
            }
        }

        private IEnumerable<AxFormExtension> FormExtensionsOf(string formName)
        {
            string root = MetaModelBridge.CustomFolder;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) yield break;
            foreach (string package in Directory.EnumerateDirectories(root))
                foreach (string model in Directory.EnumerateDirectories(package))
                    foreach (string extension in Names(model, "AxFormExtension").Where(n => n.StartsWith(formName + ".", StringComparison.OrdinalIgnoreCase)))
                    {
                        var ext = _core.MetaService.GetFormExtension(extension);
                        if (ext != null) yield return ext;
                    }
        }

        private static string Describe(XppTarget target)
        {
            switch (target.Kind.ToLowerInvariant())
            {
                case "classstr": return "Class " + target.Element;
                case "tablestr": return "Table " + target.Element;
                case "formstr": return "Form " + target.Element;
                case "formdatasourcestr": return "Data source " + target.Element + "." + target.Member;
                case "dataentityviewstr": return "Data entity " + target.Element;
                case "viewstr": return "View " + target.Element;
                default: return target.Element;
            }
        }

        private string ModelFolder(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName) || !_core.IsCustomModel(modelName)) return null;
            string root = MetaModelBridge.CustomFolder;
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return null;
            return Directory.EnumerateDirectories(root)
                .Select(package => Path.Combine(package, modelName))
                .FirstOrDefault(Directory.Exists);
        }

        private static IEnumerable<string> Names(string modelFolder, string type)
        {
            string folder = Path.Combine(modelFolder, type);
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*.xml").Select(Path.GetFileNameWithoutExtension) : Enumerable.Empty<string>();
        }

        /// <summary>Names of a collection property the metadata API exposes (read by reflection; absent collections give nothing).</summary>
        private static IEnumerable<string> Names(object element, string collection)
        {
            var items = element.GetType().GetProperty(collection)?.GetValue(element) as System.Collections.IEnumerable;
            if (items == null) yield break;
            foreach (var item in items)
                if (item?.GetType().GetProperty("Name")?.GetValue(item) is string name && name.Length > 0)
                    yield return name;
        }
    }
}
