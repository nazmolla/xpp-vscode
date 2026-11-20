using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using Microsoft.Dynamics.AX.Metadata.Extensions.Reports;
using Microsoft.Dynamics.AX.Metadata.MetaModel;
using Microsoft.Dynamics.AX.Metadata.Providers;

namespace XppVsCode.Server.MetaModel
{
    /// <summary>
    /// A report dataset's Query, as Visual Studio's report designer sets it: pick a report data provider (class.table) or a
    /// query, pick its fields, and the dataset's Fields and Parameters and the report's parameters follow. The schema comes
    /// from Microsoft's own analyzers in Microsoft.Dynamics.AX.Metadata.Extensions (the user's PackagesLocalDirectory\bin),
    /// the ones Visual Studio's designer calls.
    /// </summary>
    public sealed partial class MetaModelBridge
    {
        private const string ReportsNamespace = "Microsoft.Dynamics.AX.Metadata.Extensions.Reports.";
        private const string DynamicParameterType = "Microsoft.Dynamics.AX.Framework.Services.Client.QueryMetadata";
        private static readonly Regex SelectQuery = new Regex(@"^\s*SELECT\s+(?<fields>.+?)\s+FROM\s+(?<source>[\w.]+)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);

        private static Type ReportsType(string name) => typeof(IReportDataInfoProvider).Assembly.GetType(ReportsNamespace + name, true);

        private static object CallInternal(Type type, object target, string method, params object[] args)
        {
            var m = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .First(x => x.Name == method && x.GetParameters().Length == args.Length);
            try { return m.Invoke(target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }

        private static object Member(object target, string name) =>
            target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(target);

        /// <summary>The data tables of a report data provider: the [SRSReportDataSetAttribute] methods of the class and its bases.</summary>
        private sealed class DataInfo : IReportDataInfoProvider
        {
            private readonly IMetadataProvider _provider;
            public DataInfo(IMetadataProvider provider) { _provider = provider; }

            public IEnumerable<Tuple<string, string, string>> GetReportDataInfoForClass(AxClass axclass)
            {
                var result = new List<Tuple<string, string, string>>();
                var metadata = ReportsType("AxReportProviderClassMetadata");
                for (AxClass c = axclass; c != null; c = string.IsNullOrEmpty(c.Extends) ? null : _provider.Classes.Read(c.Extends))
                    foreach (AxMethod method in c.Methods)
                    {
                        var attribute = method.Attributes.Cast<AxAttribute>().FirstOrDefault(a =>
                            a.Name.Replace("Attribute", "").Equals("SRSReportDataSet", StringComparison.OrdinalIgnoreCase));
                        if (attribute == null || method.ReturnType == null || method.ReturnType.Type.ToString() != "Record") continue;
                        string table = CallInternal(metadata, null, "GetAttributeValue", attribute) as string;
                        if (!string.IsNullOrEmpty(table)) result.Add(Tuple.Create(table, method.ReturnType.TypeName, method.Name));
                    }
                return result;
            }
        }

        private sealed class SchemaElement
        {
            public string Name, Alias, Type, Caption;
            public bool MultiValue, IsEnum;
        }

        private static string ClrName(object type) => (type as Type)?.FullName ?? "System.String";

        /// <summary>Fields and parameters of a data source: "Class.Table" for a report data provider, a query name for Query.</summary>
        private void AnalyzeDataSource(string dataSourceType, string source, out List<SchemaElement> fields, out List<SchemaElement> parameters)
        {
            object model;
            if (dataSourceType == "ReportDataProvider")
            {
                int dot = source.IndexOf('.');
                if (dot <= 0) throw new ArgumentException("A report data provider is Class.Table, e.g. InventPriceOverviewDP.InventPriceOverviewTmp.");
                var metadataType = ReportsType("AxReportProviderMetadata");
                object metadata = CallInternal(metadataType, null, "GetInstance", MetadataProvider);
                object analyzer = CallInternal(metadataType, metadata, "GetSchemaAnalyzer", source.Substring(0, dot), source.Substring(dot + 1), new DataInfo(MetadataProvider));
                model = Member(analyzer, "Model");
            }
            else if (dataSourceType == "Query")
            {
                var query = MetadataProvider.Queries.Read(source) ?? throw new ArgumentException("Query '" + source + "' not found.");
                var analyzerType = ReportsType("AxQuerySchemaAnalyzer");
                object analyzer = Activator.CreateInstance(analyzerType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new object[] { source, MetadataProvider }, null);
                object wrapper = CallInternal(ReportsType("QueryWrapper"), null, "Create", query, MetadataProvider);
                CallInternal(analyzerType, analyzer, "ParseQueryMetadata", wrapper);
                model = Member(analyzer, "Model");
            }
            else if (dataSourceType == "EnumProvider")
            {
                if (MetadataProvider.Enums.Read(source) == null) throw new ArgumentException("Enum '" + source + "' not found.");
                // As Visual Studio and the standard reports: the enum's Name, Value and Label, and the framework parameters.
                fields = new List<SchemaElement>
                {
                    new SchemaElement { Name = "Name", Alias = "Name", Type = "System.String", Caption = "@SYS31438" },
                    new SchemaElement { Name = "Value", Alias = "Value", Type = "System.Int32", Caption = "@SYS41043" },
                    new SchemaElement { Name = "Label", Alias = "Label", Type = "System.String", Caption = "@SYS25058" },
                };
                parameters = new[] { "AX_PartitionKey", "AX_CompanyName", "AX_UserContext", "AX_RenderingCulture", "AX_ReportContext" }
                    .Select(n => new SchemaElement { Name = n, Alias = n, Type = "System.String" }).ToList();
                return;
            }
            else throw new ArgumentException("dataSourceType is ReportDataProvider, Query or EnumProvider.");

            var parse = ReportsType("AxQueryParseUtility");
            SchemaElement Map(object e) => new SchemaElement
            {
                Name = Member(e, "Name") as string,
                Alias = Member(e, "Alias") as string,
                Type = ClrName(Member(e, "Type")),
                Caption = Member(e, "Caption") as string,
                MultiValue = Member(e, "MultiValue") is bool mv && mv,
                IsEnum = (bool)CallInternal(parse, null, "IsNameEnum", (Member(e, "Alias") as string) ?? ""),
            };
            var all = ((IEnumerable)Member(model, "Fields") ?? new object[0]).Cast<object>().ToList();
            fields = all.Where(e => (bool)CallInternal(e.GetType(), e, "IsDataSetFieldCompatible")).Select(Map).ToList();
            parameters = ((IEnumerable)Member(model, "Parameters") ?? new object[0]).Cast<object>().Select(Map).ToList();
        }

        /// <summary>The fields a data source offers, for the field picker (Visual Studio's Select Fields).</summary>
        public string ReportDataSourceSchemaJson(string dataSourceType, string source)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            try
            {
                if (dataSourceType == "ReportDataProvider" && !string.IsNullOrEmpty(source) && source.IndexOf('.') < 0)
                {
                    // A class: its data tables, as the first step of Visual Studio's picker.
                    var cls = MetadataProvider.Classes.Read(source);
                    if (cls == null) return json.Serialize(Failure("Class '" + source + "' not found."));
                    var tables = new DataInfo(MetadataProvider).GetReportDataInfoForClass(cls).Select(t => cls.Name + "." + t.Item1).Distinct().ToList();
                    return json.Serialize(new Dictionary<string, object> { ["success"] = true, ["tables"] = tables });
                }
                AnalyzeDataSource(dataSourceType, source, out var fields, out var parameters);
                return json.Serialize(new Dictionary<string, object>
                {
                    ["success"] = true,
                    ["fields"] = fields.Select(f => new Dictionary<string, object>
                    {
                        ["name"] = f.Name, ["alias"] = f.Alias, ["type"] = f.Type, ["caption"] = f.Caption, ["label"] = string.IsNullOrEmpty(f.Caption) ? null : ResolveLabel(f.Caption),
                    }).ToList(),
                    ["parameters"] = parameters.Where(p => p.Name != "ReportProviderDynamicFilter").Select(p => new Dictionary<string, object> { ["name"] = p.Name, ["type"] = p.Type }).ToList(),
                });
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                return json.Serialize(Failure("Could not read " + source + ": " + ex.Message));
            }
        }

        /// <summary>
        /// Sets a dataset's data source and Query, then refreshes its Fields and Parameters and the report's parameters from
        /// the source's schema, as Visual Studio's designer does when the Query property changes. fields: the fields to keep
        /// (null or empty: all, as SELECT *).
        /// </summary>
        public string SetReportDataSetQueryJson(string reportName, string dataSetName, string dataSourceType, string source, IList<string> selected)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
            var report = ReadEditableElement("AxReport", reportName, out string model, out string error) as AxReport;
            if (report == null) return json.Serialize(Failure(error ?? "Report '" + reportName + "' not found."));
            var dataSet = report.DataSets.Cast<AxReportDataSet>().FirstOrDefault(d => string.Equals(d.Name, dataSetName, StringComparison.OrdinalIgnoreCase));
            if (dataSet == null) return json.Serialize(Failure("Report '" + report.Name + "' has no dataset '" + dataSetName + "'."));
            if (string.IsNullOrWhiteSpace(source)) return json.Serialize(Failure("Pass the data source (Class.Table or a query name)."));

            List<SchemaElement> fields, parameters;
            try { AnalyzeDataSource(dataSourceType, source.Trim(), out fields, out parameters); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return json.Serialize(Failure("Could not read " + source + ": " + ex.Message)); }

            var keep = selected != null && selected.Count > 0 ? new HashSet<string>(selected, StringComparer.OrdinalIgnoreCase) : null;
            if (keep != null) fields = fields.Where(f => keep.Contains(f.Alias) || keep.Contains(f.Name)).ToList();
            if (fields.Count == 0) return json.Serialize(Failure(source + " has no fields to use" + (keep != null ? " among the selected ones." : ".")));

            dataSet.DataSourceType = (Microsoft.Dynamics.AX.Metadata.Core.MetaModel.DataSourceType)Enum.Parse(typeof(Microsoft.Dynamics.AX.Metadata.Core.MetaModel.DataSourceType), dataSourceType);
            dataSet.Query = dataSourceType == "EnumProvider" ? source.Trim()
                : "SELECT " + (keep == null ? "*" : string.Join(",", fields.Select(f => f.Alias))) + " FROM " + source.Trim();

            // Fields: matched by alias (existing names kept), new ones added, the rest removed unless the developer added them.
            var existing = dataSet.Fields.Cast<AxReportDataSetField>().ToList();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int added = 0;
            foreach (var f in fields)
            {
                var field = existing.FirstOrDefault(x => string.Equals(x.Alias, f.Alias, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                {
                    field = f.Alias.EndsWith("()", StringComparison.Ordinal) ? new AxReportDataSetCalculatedField() : new AxReportDataSetField();
                    field.Alias = f.Alias;
                    field.Name = UniqueFieldName(f.Name, names, existing.Select(x => x.Name));
                    field.Caption = f.Caption ?? f.Name;
                    if (f.IsEnum) field.DisableAutoCreateInDataRegion = true;
                    dataSet.Fields.Add(field);
                    added++;
                }
                field.DataType = f.Type;
                field.UserDefined = false;
                names.Add(field.Name);
            }
            int removed = 0;
            foreach (var old in existing.Where(x => !fields.Any(f => string.Equals(f.Alias, x.Alias, StringComparison.OrdinalIgnoreCase)) && !x.UserDefined).ToList())
            {
                dataSet.Fields.Remove(old);
                removed++;
            }

            // Parameters: the dataset's, each tied to a report parameter (framework AX_* ones hidden, as Visual Studio creates them).
            var group = report.DefaultParameterGroup ?? (report.DefaultParameterGroup = new AxReportParameterGroup { Name = "Parameters" });
            var reportParameters = group.ReportParameterBases.Cast<AxReportParameterBase>().OfType<AxReportParameter>().ToList();
            foreach (var p in parameters)
            {
                if (p.Name == "ReportProviderDynamicFilter")
                {
                    if (dataSet.DynamicFilters) AddDynamicParameter(dataSet, group, p, source.Trim().Split('.')[0]);
                    continue;
                }
                var dsParameter = dataSet.Parameters.Cast<AxReportDataSetParameter>().FirstOrDefault(x => string.Equals(x.Alias, p.Alias, StringComparison.OrdinalIgnoreCase));
                if (dsParameter == null)
                {
                    dsParameter = new AxReportDataSetParameter { Name = p.Name, Alias = p.Alias };
                    dataSet.Parameters.Add(dsParameter);
                }
                dsParameter.DataType = p.Type;
                dsParameter.MultiValue = p.MultiValue;
                var reportParameter = reportParameters.FirstOrDefault(r => string.Equals(r.Name, dsParameter.Parameter, StringComparison.OrdinalIgnoreCase))
                    ?? reportParameters.FirstOrDefault(r => string.Equals(r.Name, p.Name, StringComparison.OrdinalIgnoreCase));
                if (reportParameter == null)
                {
                    reportParameter = new AxReportParameter { Name = IsFrameworkParameter(p.Name) || !reportParameters.Any(r => r.Name == p.Name) ? p.Name : dataSet.Name + "_" + p.Name };
                    if (string.Equals(p.Name, "AX_CompanyName", StringComparison.OrdinalIgnoreCase))
                        reportParameter.UserVisibility = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.ParameterVisibility.Hidden;
                    else if (IsFrameworkParameter(p.Name))
                    {
                        reportParameter.AllowBlank = true;
                        reportParameter.Nullable = true;
                        reportParameter.UserVisibility = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.ParameterVisibility.Hidden;
                    }
                    group.ReportParameterBases.Add(reportParameter);
                    reportParameters.Add(reportParameter);
                }
                dsParameter.Parameter = reportParameter.Name;
                if (string.IsNullOrEmpty(reportParameter.PromptString) && !string.IsNullOrEmpty(p.Caption)) reportParameter.PromptString = p.Caption;
                reportParameter.DataType = p.Type;
                reportParameter.MultiValue = p.MultiValue;
            }
            if (dataSourceType == "Query" && dataSet.DynamicFilters)
                AddDynamicParameter(dataSet, group, new SchemaElement { Caption = dataSet.Query }, dataSet.Name);

            string saved = SaveElement(json, "AxReport", report, model, "Set " + report.Name + "." + dataSet.Name + " to " + source.Trim() + ": " + fields.Count + " field(s)"
                + (added > 0 ? ", " + added + " added" : "") + (removed > 0 ? ", " + removed + " removed" : "") + ", " + dataSet.Parameters.Count + " parameter(s).");
            return saved;
        }

        private static bool IsFrameworkParameter(string name) =>
            (bool)CallInternal(ReportsType("AxSchemaAnalyzerHelper"), null, "IsFrameworkParameter", name ?? "");

        /// <summary>Visual Studio's dynamic filter parameter for a report data provider with a query (SRSReportQueryAttribute): "&lt;provider class, 21 characters at most&gt;_DynamicParameter".</summary>
        private static void AddDynamicParameter(AxReportDataSet dataSet, AxReportParameterGroup group, SchemaElement schema, string providerClass)
        {
            string name = (providerClass.Length > 21 ? providerClass.Substring(0, 21) : providerClass) + "_DynamicParameter";
            if (!dataSet.Parameters.Cast<AxReportDataSetParameter>().Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                dataSet.Parameters.Add(new AxReportDataSetParameter { Name = name, Alias = name, DataType = DynamicParameterType, Parameter = name });
            var parameter = group.ReportParameterBases.Cast<AxReportParameterBase>().OfType<AxReportParameter>().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (parameter == null)
            {
                parameter = new AxReportParameter
                {
                    Name = name, AllowBlank = true, Nullable = true, DataType = DynamicParameterType,
                    UserVisibility = Microsoft.Dynamics.AX.Metadata.Core.MetaModel.ParameterVisibility.Hidden,
                };
                group.ReportParameterBases.Add(parameter);
            }
            parameter.AOTQuery = schema.Caption;
        }

        private static readonly Regex NonIdentifier = new Regex("[^a-z0-9_]", RegexOptions.IgnoreCase);

        private static string UniqueFieldName(string proposed, HashSet<string> taken, IEnumerable<string> existing)
        {
            string baseName = NonIdentifier.Replace(string.IsNullOrEmpty(proposed) ? "Field" : proposed, "_");
            var all = new HashSet<string>(taken.Concat(existing.Where(e => e != null)), StringComparer.OrdinalIgnoreCase);
            string name = baseName;
            for (int i = 1; all.Contains(name); i++) name = baseName + i;
            return name;
        }
    }
}
