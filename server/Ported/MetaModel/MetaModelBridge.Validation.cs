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
    /// <summary>Validation of an element against expected metadata (xpp_validate_object).</summary>
    public sealed partial class MetaModelBridge
    {
        // ── Validation ──

        /// <summary>Bool properties read back as True/False while callers (and the property sheet) use Yes/No.</summary>
        private static bool SamePropertyValue(string actual, string expected)
        {
            string Norm(string v)
            {
                v = (v ?? "").Trim();
                if (v.Equals("true", StringComparison.OrdinalIgnoreCase)) return "yes";
                if (v.Equals("false", StringComparison.OrdinalIgnoreCase)) return "no";
                return v.ToLowerInvariant();
            }
            return Norm(actual) == Norm(expected);
        }

        public ValidateObjectResult ValidateObject(ValidateObjectRequest req)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var result = new ValidateObjectResult { ObjectType = req.ObjectType, ObjectName = req.ObjectName };

            // 1. Read the object via typed API
            var readResult = ReadObject(req.ObjectType, req.ObjectName);
            if (!readResult.Success)
            {
                // Retry with reloaded metadata — the disk provider may have cached the old state
                _host.Reset();
                InvalidateCaches();
                readResult = ReadObject(req.ObjectType, req.ObjectName);
            }
            if (!readResult.Success)
            {
                result.Message = "Object not found: " + (readResult.Message ?? "unknown error");
                return result;
            }

            result.Exists = true;
            result.ModelName = readResult.ModelName;

            // 2. Project membership: the exact item (type\name) must be in the active project's file.
            var activeProjectNode = GetActiveProjectNode();
            bool hasProject = activeProjectNode != null;
            result.InProject = hasProject && activeProjectNode.Contains(req.ObjectType, req.ObjectName);

            // Only objects of the project's own model can be in the project; for others (standard, other packages)
            // membership is not a requirement.
            string projectModel = hasProject ? activeProjectNode.GetProjectsModelInfo()?.Name : null;
            string objectModel = string.IsNullOrEmpty(readResult.ModelName) ? ModelOf(req.ObjectType, req.ObjectName) : readResult.ModelName;
            bool membershipApplies = hasProject && (string.IsNullOrEmpty(projectModel) || string.IsNullOrEmpty(objectModel)
                || string.Equals(projectModel, objectModel, StringComparison.OrdinalIgnoreCase));

            // 3. Compare expected vs actual properties
            var mismatches = new List<string>();

            if (!string.IsNullOrWhiteSpace(req.TypedMetadataJson))
            {
                if (string.IsNullOrWhiteSpace(readResult.TypedMetadataJson))
                {
                    mismatches.Add("typedMetadata: expected object graph but read result returned none");
                }
                else
                {
                    try
                    {
                        object expected = ParseTypedMetadataToNode(req.ObjectType, req.TypedMetadataJson);
                        // With "$type" the request may set subtype properties that the proxy-based read omits;
                        // compare against the object's full graph then.
                        string actualJson = readResult.TypedMetadataJson;
                        object actual = null;
                        if (req.TypedMetadataJson.Contains("\"$type\""))
                        {
                            object live = ReadViaMetadataProvider(req.ObjectType, req.ObjectName, out _);
                            string full = live != null ? BuildFullGraphJson(live) : null;
                            if (full != null)
                                actual = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 64 }.DeserializeObject(full);
                        }
                        if (actual == null) actual = ParseTypedMetadataToNode(req.ObjectType, actualJson);
                        ValidateMetadataSubset(expected, actual, "typedMetadata", mismatches, 0);
                    }
                    catch (Exception ex)
                    {
                        mismatches.Add("typedMetadata: validation parse error: " + ex.Message);
                    }
                }
            }

            if (req.Properties != null)
            {
                object axObject = null;
                foreach (var kv in req.Properties)
                {
                    string actual = null;
                    if (readResult.Properties == null || !readResult.Properties.TryGetValue(kv.Key, out actual))
                    {
                        // The read omits properties at their default value; look at the object itself.
                        if (axObject == null) axObject = ReadViaMetadataProvider(req.ObjectType, req.ObjectName, out _);
                        actual = axObject?.GetType().GetProperty(kv.Key)?.GetValue(axObject)?.ToString();
                    }
                    if (actual == null)
                        mismatches.Add("Property '" + kv.Key + "': expected '" + kv.Value + "', not found on object");
                    else if (!SamePropertyValue(actual, kv.Value))
                        mismatches.Add("Property '" + kv.Key + "': expected '" + kv.Value + "', actual '" + actual + "'");
                }
            }

            if (req.Fields != null)
            {
                var actualFieldMap = readResult.Fields != null
                    ? readResult.Fields.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FieldDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in req.Fields)
                {
                    if (!actualFieldMap.TryGetValue(f.Name ?? "", out var actual))
                    {
                        mismatches.Add("Field '" + f.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(f.FieldType) &&
                        !string.Equals(f.FieldType, actual.FieldType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.FieldType: expected '" + f.FieldType + "', actual '" + (actual.FieldType ?? "") + "'");
                    if (!string.IsNullOrEmpty(f.ExtendedDataType) &&
                        !string.Equals(f.ExtendedDataType, actual.ExtendedDataType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.ExtendedDataType: expected '" + f.ExtendedDataType + "', actual '" + (actual.ExtendedDataType ?? "") + "'");
                    if (!string.IsNullOrEmpty(f.EnumType) &&
                        !string.Equals(f.EnumType, actual.EnumType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.EnumType: expected '" + f.EnumType + "', actual '" + (actual.EnumType ?? "") + "'");
                    if (!string.IsNullOrEmpty(f.Label) &&
                        !string.Equals(f.Label, actual.Label, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Field '" + f.Name + "'.Label: expected '" + f.Label + "', actual '" + (actual.Label ?? "") + "'");
                }
            }

            if (req.EnumValues != null)
            {
                var actualEnumMap = readResult.EnumValues != null
                    ? readResult.EnumValues.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, EnumValueDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var ev in req.EnumValues)
                {
                    if (!actualEnumMap.TryGetValue(ev.Name ?? "", out var actual))
                    {
                        mismatches.Add("EnumValue '" + ev.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (ev.Value != actual.Value)
                        mismatches.Add("EnumValue '" + ev.Name + "'.Value: expected " + ev.Value + ", actual " + actual.Value);
                    if (!string.IsNullOrEmpty(ev.Label) &&
                        !string.Equals(ev.Label, actual.Label, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("EnumValue '" + ev.Name + "'.Label: expected '" + ev.Label + "', actual '" + (actual.Label ?? "") + "'");
                }
            }

            if (req.Indexes != null)
            {
                var actualIndexMap = readResult.Indexes != null
                    ? readResult.Indexes.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, IndexDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var idx in req.Indexes)
                {
                    if (!actualIndexMap.TryGetValue(idx.Name ?? "", out var actual))
                    {
                        mismatches.Add("Index '" + idx.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (idx.AllowDuplicatesSpecified && idx.AllowDuplicates != actual.AllowDuplicates)
                        mismatches.Add("Index '" + idx.Name + "'.AllowDuplicates: expected " + idx.AllowDuplicates + ", actual " + actual.AllowDuplicates);
                    if (idx.Fields != null && idx.Fields.Count > 0)
                    {
                        var actualFields = actual.Fields ?? new List<string>();
                        var expectedSet = new HashSet<string>(idx.Fields, StringComparer.OrdinalIgnoreCase);
                        var actualSet = new HashSet<string>(actualFields, StringComparer.OrdinalIgnoreCase);
                        foreach (var expField in idx.Fields)
                        {
                            if (!actualSet.Contains(expField))
                                mismatches.Add("Index '" + idx.Name + "'.Fields: expected field '" + expField + "' not found");
                        }
                    }
                }
            }

            if (req.Relations != null)
            {
                var actualRelMap = readResult.Relations != null
                    ? readResult.Relations.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, RelationDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var rel in req.Relations)
                {
                    if (!actualRelMap.TryGetValue(rel.Name ?? "", out var actual))
                    {
                        mismatches.Add("Relation '" + rel.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(rel.RelatedTable) &&
                        !string.Equals(rel.RelatedTable, actual.RelatedTable, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Relation '" + rel.Name + "'.RelatedTable: expected '" + rel.RelatedTable + "', actual '" + (actual.RelatedTable ?? "") + "'");
                    if (rel.Constraints != null && rel.Constraints.Count > 0)
                    {
                        var actualConstraints = actual.Constraints ?? new List<RelationConstraintDto>();
                        foreach (var c in rel.Constraints)
                        {
                            bool found = actualConstraints.Any(ac =>
                                string.Equals(ac.Field, c.Field, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(ac.RelatedField, c.RelatedField, StringComparison.OrdinalIgnoreCase));
                            if (!found)
                                mismatches.Add("Relation '" + rel.Name + "'.Constraint: expected " + (c.Field ?? "") + " -> " + (c.RelatedField ?? "") + ", not found");
                        }
                    }
                }
            }

            if (req.FieldGroups != null)
            {
                var actualFgMap = readResult.FieldGroups != null
                    ? readResult.FieldGroups.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FieldGroupDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var fg in req.FieldGroups)
                {
                    if (!actualFgMap.TryGetValue(fg.Name ?? "", out var actual))
                    {
                        mismatches.Add("FieldGroup '" + fg.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(fg.Label) &&
                        !string.Equals(fg.Label, actual.Label, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("FieldGroup '" + fg.Name + "'.Label: expected '" + fg.Label + "', actual '" + (actual.Label ?? "") + "'");
                    if (fg.Fields != null && fg.Fields.Count > 0)
                    {
                        var actualFields = actual.Fields ?? new List<string>();
                        var actualSet = new HashSet<string>(actualFields, StringComparer.OrdinalIgnoreCase);
                        foreach (var expField in fg.Fields)
                        {
                            if (!actualSet.Contains(expField))
                                mismatches.Add("FieldGroup '" + fg.Name + "'.Fields: expected field '" + expField + "' not found");
                        }
                    }
                }
            }

            if (req.EntryPoints != null)
            {
                var actualEpMap = readResult.EntryPoints != null
                    ? readResult.EntryPoints.Where(x => x.Name != null)
                        .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, EntryPointDto>(StringComparer.OrdinalIgnoreCase);

                foreach (var ep in req.EntryPoints)
                {
                    if (!actualEpMap.TryGetValue(ep.Name ?? "", out var actual))
                    {
                        mismatches.Add("EntryPoint '" + ep.Name + "': expected to exist, not found");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(ep.ObjectType) &&
                        !string.Equals(ep.ObjectType, actual.ObjectType, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("EntryPoint '" + ep.Name + "'.ObjectType: expected '" + ep.ObjectType + "', actual '" + (actual.ObjectType ?? "") + "'");
                    if (!string.IsNullOrEmpty(ep.ObjectName) &&
                        !string.Equals(ep.ObjectName, actual.ObjectName, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("EntryPoint '" + ep.Name + "'.ObjectName: expected '" + ep.ObjectName + "', actual '" + (actual.ObjectName ?? "") + "'");
                    if (!string.IsNullOrEmpty(ep.Grant) && !GrantsEqual(ep.Grant, actual.Grant))
                        mismatches.Add("EntryPoint '" + ep.Name + "'.Grant: expected '" + ep.Grant + "', actual '" + (actual.Grant ?? "") + "'");
                }
            }

            if (req.DataSources != null)
            {
                ValidateDataSources(req.DataSources, readResult.DataSources, "", mismatches);
            }

            if (req.Controls != null)
            {
                var readControls = readResult.Controls;
                // xpp_read_object leaves a form's design out (it can be thousands of nodes); compare against the design here.
                if ((readControls == null || readControls.Count == 0) && req.ObjectType == "AxForm"
                    && MetaService.GetForm(req.ObjectName)?.Design is AxFormDesign design)
                    readControls = design.Controls.Select(ExtractFormControl).Where(c => c != null).ToList();
                var actualControls = new Dictionary<string, FormControlDto>(StringComparer.OrdinalIgnoreCase);
                FlattenControls(readControls, actualControls);
                var expectedControls = new Dictionary<string, FormControlDto>(StringComparer.OrdinalIgnoreCase);
                FlattenControls(req.Controls, expectedControls);
                foreach (var exp in expectedControls.Values)
                {
                    if (!actualControls.TryGetValue(exp.Name, out var act))
                        mismatches.Add("Control '" + exp.Name + "': expected to exist, not found");
                    else if (!string.IsNullOrEmpty(exp.DataField)
                        && !string.Equals(exp.DataField, act.DataField, StringComparison.OrdinalIgnoreCase))
                        mismatches.Add("Control '" + exp.Name + "'.DataField: expected '" + exp.DataField + "', actual '" + (act.DataField ?? "") + "'");
                }
            }

            result.Mismatches = mismatches;
            result.Valid = (result.InProject || !membershipApplies) && mismatches.Count == 0;
            string projectNote = !hasProject
                ? " Project membership NOT checked: no active project (set one with xpp_select_project)."
                : !membershipApplies
                    ? " Project membership not checked: the object belongs to model " + objectModel + ", and project "
                      + activeProjectNode.Caption + " holds objects of model " + projectModel + " only."
                    : (result.InProject ? " Object is in project " + activeProjectNode.Caption + "." : "");
            if (result.Valid)
            {
                result.Message = "Validation passed. All specified metadata is present." + projectNote;
            }
            else if (!result.InProject && mismatches.Count == 0)
            {
                result.Message = "Object metadata is correct but it is NOT in the active project " + activeProjectNode.Caption
                    + ". Add it with xpp_add_to_project.";
            }
            else
            {
                var msgBuilder = new System.Text.StringBuilder();
                msgBuilder.Append("Validation failed (" + mismatches.Count + " mismatch(es), InProject=" + result.InProject + "):");
                foreach (var m in mismatches)
                {
                    msgBuilder.Append("\n  - " + m);
                }
                result.Message = msgBuilder.ToString();
            }

            return result;
        }

        private static void ValidateMetadataSubset(object expected, object actual, string path, List<string> mismatches, int depth)
        {
            if (depth > 10) return;
            if (expected == null) return;
            if (actual == null)
            {
                mismatches.Add(path + ": expected value present, actual is null");
                return;
            }

            if (expected is Dictionary<string, object> expDict)
            {
                var actDict = actual as Dictionary<string, object>;
                if (actDict == null)
                {
                    mismatches.Add(path + ": expected object, actual is " + actual.GetType().Name);
                    return;
                }

                foreach (var kv in expDict)
                {
                    // "$type" names the concrete subtype; the actual graph carries it only for polymorphic members.
                    if (kv.Key == "$type" || kv.Key == "__type") continue;
                    if (!actDict.TryGetValue(kv.Key, out object actVal))
                    {
                        mismatches.Add(path + "." + kv.Key + ": missing");
                        continue;
                    }
                    ValidateMetadataSubset(kv.Value, actVal, path + "." + kv.Key, mismatches, depth + 1);
                }
                return;
            }

            var expArray = ToObjectArray(expected);
            if (expArray != null)
            {
                var actArray = ToObjectArray(actual);
                if (actArray == null)
                {
                    mismatches.Add(path + ": expected array, actual is " + actual.GetType().Name);
                    return;
                }

                if (actArray.Length < expArray.Length)
                    mismatches.Add(path + ": expected at least " + expArray.Length + " items, actual " + actArray.Length);

                int compareCount = Math.Min(expArray.Length, actArray.Length);
                for (int i = 0; i < compareCount; i++)
                    ValidateMetadataSubset(expArray[i], actArray[i], path + "[" + i + "]", mismatches, depth + 1);

                return;
            }

            string expText = Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture);
            string actText = Convert.ToString(actual, System.Globalization.CultureInfo.InvariantCulture);
            if (!string.Equals(expText, actText, StringComparison.OrdinalIgnoreCase))
                mismatches.Add(path + ": expected '" + expText + "', actual '" + actText + "'");
        }

        private static object[] ToObjectArray(object node)
        {
            if (node == null) return null;
            if (node is object[] arr) return arr;
            if (node is System.Collections.ArrayList al) return al.ToArray();
            if (node is System.Collections.IEnumerable en && !(node is string))
            {
                var list = new List<object>();
                foreach (object item in en) list.Add(item);
                return list.ToArray();
            }
            return null;
        }

        private static void ValidateDataSources(List<QueryDataSourceDto> expected, List<QueryDataSourceDto> actual, string prefix, List<string> mismatches)
        {
            if (expected == null || expected.Count == 0) return;

            var actualMap = actual != null
                ? actual.Where(x => x.Name != null)
                    .ToDictionary(x => x.Name, x => x, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, QueryDataSourceDto>(StringComparer.OrdinalIgnoreCase);

            foreach (var ds in expected)
            {
                string dsPath = string.IsNullOrEmpty(prefix)
                    ? "DataSource '" + ds.Name + "'"
                    : prefix + ".DataSource '" + ds.Name + "'";

                if (!actualMap.TryGetValue(ds.Name ?? "", out var act))
                {
                    mismatches.Add(dsPath + ": expected to exist, not found");
                    continue;
                }
                if (!string.IsNullOrEmpty(ds.Table) &&
                    !string.Equals(ds.Table, act.Table, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(dsPath + ".Table: expected '" + ds.Table + "', actual '" + (act.Table ?? "") + "'");
                if (!string.IsNullOrEmpty(ds.JoinMode) &&
                    !string.Equals(ds.JoinMode, act.JoinMode, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(dsPath + ".JoinMode: expected '" + ds.JoinMode + "', actual '" + (act.JoinMode ?? "") + "'");
                if (!string.IsNullOrEmpty(ds.LinkType) &&
                    !string.Equals(ds.LinkType, act.LinkType, StringComparison.OrdinalIgnoreCase))
                    mismatches.Add(dsPath + ".LinkType: expected '" + ds.LinkType + "', actual '" + (act.LinkType ?? "") + "'");
                if (ds.DynamicFields.HasValue && ds.DynamicFields != act.DynamicFields)
                    mismatches.Add(dsPath + ".DynamicFields: expected " + ds.DynamicFields + ", actual " + act.DynamicFields);
                if (ds.Relations.HasValue && ds.Relations != act.Relations)
                    mismatches.Add(dsPath + ".Relations: expected " + ds.Relations + ", actual " + act.Relations);
                if (ds.FirstOnly.HasValue && ds.FirstOnly != act.FirstOnly)
                    mismatches.Add(dsPath + ".FirstOnly: expected " + ds.FirstOnly + ", actual " + act.FirstOnly);
                if (ds.Ranges != null && ds.Ranges.Count > 0)
                {
                    var actualRanges = act.Ranges ?? new List<QueryRangeDto>();
                    foreach (var r in ds.Ranges)
                    {
                        bool found = actualRanges.Any(ar =>
                            string.Equals(ar.Field, r.Field, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(ar.Value, r.Value, StringComparison.OrdinalIgnoreCase));
                        if (!found)
                            mismatches.Add(dsPath + ".Range: expected Field='" + (r.Field ?? "") + "' Value='" + (r.Value ?? "") + "', not found");
                    }
                }
                if (ds.ChildDataSources != null && ds.ChildDataSources.Count > 0)
                    ValidateDataSources(ds.ChildDataSources, act.ChildDataSources, dsPath, mismatches);
            }
        }
    }
}
