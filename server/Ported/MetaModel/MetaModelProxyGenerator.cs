using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace XppVsCode.Server.MetaModel
{
    internal sealed class MetaModelProxyGenerator
    {
        private readonly bool _includeComments;
        private readonly Dictionary<Type, string> _typeToProxyName = new Dictionary<Type, string>();
        private readonly HashSet<string> _usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<GeneratedClass> _classes = new List<GeneratedClass>();

        private MetaModelProxyGenerator(bool includeComments)
        {
            _includeComments = includeComments;
        }

        public static ProxyGenerationResult Generate(Dictionary<string, Type> objectTypes, ProxyGenerationRequest request)
        {
            var result = new ProxyGenerationResult
            {
                Success = false,
                Namespace = string.IsNullOrWhiteSpace(request?.Namespace)
                    ? "XppVsCode.Server.MetaModel.Proxies"
                    : request.Namespace
            };

            if (objectTypes == null || objectTypes.Count == 0)
            {
                result.Message = "No supported object types were selected for proxy generation.";
                return result;
            }

            try
            {
                var generator = new MetaModelProxyGenerator(request?.IncludeComments ?? true);
                foreach (var kvp in objectTypes.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                {
                    string preferred = SanitizeIdentifier(kvp.Key) + "Proxy";
                    generator.EnsureProxyForType(kvp.Value, preferred);
                    result.GeneratedTypes.Add(kvp.Key);
                }

                result.GeneratedCode = generator.BuildCode(result.Namespace, result.GeneratedTypes);
                result.Success = true;
                result.Message = "Generated " + generator._classes.Count + " proxy classes for "
                    + result.GeneratedTypes.Count + " object types.";
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Proxy generation failed: " + ex.Message;
            }

            return result;
        }

        private string EnsureProxyForType(Type type, string preferredName)
        {
            if (type == null) return "object";

            Type underlying = Nullable.GetUnderlyingType(type) ?? type;
            if (IsSimpleType(underlying))
                return MapSimpleType(underlying);

            if (underlying != typeof(string) && typeof(IEnumerable).IsAssignableFrom(underlying))
            {
                Type itemType = GetEnumerableItemType(underlying) ?? typeof(object);
                return "List<" + EnsureProxyForType(itemType, itemType.Name + "Proxy") + ">";
            }

            if (_typeToProxyName.TryGetValue(underlying, out string existingName))
                return existingName;

            string proxyName = GetUniqueClassName(preferredName);
            _typeToProxyName[underlying] = proxyName;
            _classes.Add(BuildClass(underlying, proxyName));
            return proxyName;
        }

        private GeneratedClass BuildClass(Type type, string className)
        {
            var props = new List<GeneratedProperty>();
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            {
                if (!prop.CanRead || prop.GetIndexParameters().Length > 0) continue;
                if (string.Equals(prop.Name, "Parent", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(prop.Name, "Owner", StringComparison.OrdinalIgnoreCase)) continue;

                string propTypeName = EnsureProxyForType(prop.PropertyType, prop.Name + "Proxy");
                props.Add(new GeneratedProperty
                {
                    Name = SanitizeIdentifier(prop.Name),
                    TypeName = propTypeName,
                    CanWrite = prop.CanWrite,
                    SourcePropertyName = prop.Name
                });
            }

            return new GeneratedClass
            {
                Name = className,
                SourceTypeName = type.Name,
                Properties = props
            };
        }

        private string BuildCode(string ns, List<string> roots)
        {
            var sb = new StringBuilder();
            sb.AppendLine("using System;");
            sb.AppendLine("using System.Collections.Generic;");
            sb.AppendLine();
            sb.AppendLine("namespace " + ns);
            sb.AppendLine("{");
            if (_includeComments)
            {
                sb.AppendLine("    /// <summary>");
                sb.AppendLine("    /// Auto-generated metadata proxy classes for MCP transport and validation.");
                sb.AppendLine("    /// Regenerate when metadata assembly shape changes.");
                sb.AppendLine("    /// </summary>");
            }
            sb.AppendLine("    public static class MetadataProxyManifest");
            sb.AppendLine("    {");
            sb.AppendLine("        public static readonly string[] RootObjectTypes = new[]");
            sb.AppendLine("        {");
            for (int i = 0; i < roots.Count; i++)
            {
                sb.Append("            \"").Append(roots[i]).Append("\"");
                if (i < roots.Count - 1) sb.Append(",");
                sb.AppendLine();
            }
            sb.AppendLine("        };");
            sb.AppendLine("    }");
            sb.AppendLine();

            foreach (var cls in _classes.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (_includeComments)
                {
                    sb.AppendLine("    /// <summary>");
                    sb.AppendLine("    /// Proxy for " + cls.SourceTypeName + ".");
                    sb.AppendLine("    /// </summary>");
                }
                sb.AppendLine("    public sealed class " + cls.Name);
                sb.AppendLine("    {");
                foreach (var prop in cls.Properties.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (_includeComments && !string.Equals(prop.Name, prop.SourcePropertyName, StringComparison.Ordinal))
                        sb.AppendLine("        // Source property: " + prop.SourcePropertyName);
                    sb.Append("        public ").Append(prop.TypeName).Append(" ").Append(prop.Name)
                        .Append(" { get; set; }")
                        .AppendLine();
                }
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string MapSimpleType(Type type)
        {
            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "bool";
            if (type == typeof(byte)) return "byte";
            if (type == typeof(short)) return "short";
            if (type == typeof(int)) return "int";
            if (type == typeof(long)) return "long";
            if (type == typeof(float)) return "float";
            if (type == typeof(double)) return "double";
            if (type == typeof(decimal)) return "decimal";
            if (type == typeof(DateTime)) return "DateTime";
            if (type == typeof(Guid)) return "Guid";
            if (type.IsEnum) return "string";
            return "object";
        }

        private static bool IsSimpleType(Type t)
        {
            Type nt = Nullable.GetUnderlyingType(t) ?? t;
            return nt.IsPrimitive || nt.IsEnum || nt == typeof(string) || nt == typeof(decimal)
                || nt == typeof(DateTime) || nt == typeof(Guid);
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

            var ienum = enumerableType.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            return ienum?.GetGenericArguments().FirstOrDefault();
        }

        private string GetUniqueClassName(string name)
        {
            string candidate = SanitizeIdentifier(string.IsNullOrWhiteSpace(name) ? "MetadataProxy" : name);
            if (_usedNames.Add(candidate)) return candidate;

            int i = 2;
            while (!_usedNames.Add(candidate + i)) i++;
            return candidate + i;
        }

        private static string SanitizeIdentifier(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "MetadataProxy";

            var sb = new StringBuilder(name.Length);
            if (!char.IsLetter(name[0]) && name[0] != '_') sb.Append('_');

            foreach (char c in name)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            return sb.ToString();
        }

        private sealed class GeneratedClass
        {
            public string Name { get; set; }
            public string SourceTypeName { get; set; }
            public List<GeneratedProperty> Properties { get; set; } = new List<GeneratedProperty>();
        }

        private sealed class GeneratedProperty
        {
            public string Name { get; set; }
            public string SourcePropertyName { get; set; }
            public string TypeName { get; set; }
            public bool CanWrite { get; set; }
        }
    }
}
