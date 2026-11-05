using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace XppVsCode.Server.Analysis
{
    /// <summary>A method's header as declared in X++: modifiers, return type, parameter types and attributes.</summary>
    internal sealed class XppSignature
    {
        private static readonly Regex Header = new Regex(
            @"^(?<mods>(?:(?:public|protected|private|internal|static|final|abstract|client|server|display|edit|delegate|extern)\s+)*)(?<ret>[\w.]+(?:\s*\[\s*\])?)\s+(?<name>\w+)\s*\((?<params>.*)\)\s*$",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Attribute = new Regex(@"^\s*\[(?<a>(?:[^\[\]]|\[[^\]]*\])*)\]", RegexOptions.Compiled);

        public string Name;
        public HashSet<string> Modifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string ReturnType;
        public List<string> ParameterTypes = new List<string>();
        public List<string> Attributes = new List<string>();

        public bool IsStatic => Modifiers.Contains("static");
        public string Access => new[] { "private", "protected", "internal", "public" }.FirstOrDefault(Modifiers.Contains) ?? "public";

        /// <summary>The header of a method's source (doc comments and attributes before it), or null when it can't be read.</summary>
        public static XppSignature Parse(string source)
        {
            if (string.IsNullOrEmpty(source)) return null;
            int brace = source.IndexOf('{');
            string head = StripComments(brace < 0 ? source : source.Substring(0, brace)).Trim();
            var signature = new XppSignature();
            Match attribute;
            while ((attribute = Attribute.Match(head)).Success)
            {
                signature.Attributes.AddRange(SplitTopLevel(attribute.Groups["a"].Value).Select(a => a.Trim()).Where(a => a.Length > 0));
                head = head.Substring(attribute.Length).Trim();
            }
            var m = Header.Match(head);
            if (!m.Success) return null;
            signature.Name = m.Groups["name"].Value;
            foreach (string mod in Regex.Split(m.Groups["mods"].Value.Trim(), @"\s+").Where(s => s.Length > 0))
                signature.Modifiers.Add(mod);
            signature.ReturnType = Normalize(m.Groups["ret"].Value);
            foreach (string parameter in SplitTopLevel(m.Groups["params"].Value).Select(p => p.Trim()).Where(p => p.Length > 0))
            {
                string declaration = TopLevelBefore(parameter, '=').Trim();
                int lastSpace = declaration.LastIndexOfAny(new[] { ' ', '\t', '\r', '\n' });
                signature.ParameterTypes.Add(Normalize(lastSpace < 0 ? declaration : declaration.Substring(0, lastSpace)));
            }
            return signature;
        }

        /// <summary>True when the attribute list has the attribute (with "Attribute" suffix or not), e.g. Wrappable(false).</summary>
        public bool HasAttribute(string name, string argument = null)
        {
            foreach (string a in Attributes)
            {
                var m = Regex.Match(a, @"^(\w+?)(?:Attribute)?\s*(?:\((.*)\))?$", RegexOptions.Singleline);
                if (!m.Success || !string.Equals(m.Groups[1].Value, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (argument == null || string.Equals(m.Groups[2].Value.Trim(), argument, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Why a wrapper's signature no longer matches its target, or null when it matches.</summary>
        public static string Difference(XppSignature wrapper, XppSignature target)
        {
            if (wrapper.IsStatic != target.IsStatic) return target.IsStatic ? "the method is now static" : "the method is no longer static";
            if (!string.Equals(wrapper.ReturnType, target.ReturnType, StringComparison.OrdinalIgnoreCase))
                return "it now returns " + target.ReturnType + " (the wrapper returns " + wrapper.ReturnType + ")";
            if (wrapper.ParameterTypes.Count != target.ParameterTypes.Count)
                return "it now takes " + target.ParameterTypes.Count + " parameter(s) (" + string.Join(", ", target.ParameterTypes) + "), the wrapper " + wrapper.ParameterTypes.Count;
            for (int i = 0; i < wrapper.ParameterTypes.Count; i++)
                if (!string.Equals(wrapper.ParameterTypes[i], target.ParameterTypes[i], StringComparison.OrdinalIgnoreCase))
                    return "parameter " + (i + 1) + " is now " + target.ParameterTypes[i] + " (the wrapper has " + wrapper.ParameterTypes[i] + ")";
            return null;
        }

        internal static string StripComments(string text)
        {
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') i++;
                    sb.Append('\n');
                }
                else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 1;
                    sb.Append(' ');
                }
                else sb.Append(text[i]);
            }
            return sb.ToString();
        }

        private static string Normalize(string type) => Regex.Replace(type, @"\s+", "");

        private static IEnumerable<string> SplitTopLevel(string text)
        {
            int depth = 0, start = 0;
            bool quoted = false;
            char quote = '\0';
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quoted) { if (c == quote) quoted = false; continue; }
                if (c == '"' || c == '\'') { quoted = true; quote = c; }
                else if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == ',' && depth == 0) { yield return text.Substring(start, i - start); start = i + 1; }
            }
            yield return text.Substring(start);
        }

        private static string TopLevelBefore(string text, char separator)
        {
            int depth = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == separator && depth == 0) return text.Substring(0, i);
            }
            return text;
        }
    }

    /// <summary>What an extension class or event handler points at: [ExtensionOf(...)], [PostHandlerFor(...)], [SubscribesTo(...)], [DataEventHandler(...)].</summary>
    internal sealed class XppTarget
    {
        private static readonly Regex ExtensionOf = new Regex(
            @"\[\s*ExtensionOf\s*\(\s*(?<kind>classStr|tableStr|formStr|formDataSourceStr|formDataFieldStr|formControlStr|dataEntityViewStr|viewStr|mapStr)\s*\(\s*(?<a>\w+)\s*(?:,\s*(?<b>\w+)\s*)?(?:,\s*(?<c>\w+)\s*)?\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Handler = new Regex(
            @"(?<attr>PreHandlerFor|PostHandlerFor|SubscribesTo)\s*\(\s*(?<kind>classStr|tableStr|formStr|dataEntityViewStr|viewStr)\s*\(\s*(?<a>\w+)\s*\)\s*,\s*(?<mkind>\w+)\s*\(\s*\w+\s*,\s*(?<m>\w+)\s*\)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DataEvent = new Regex(
            @"DataEventHandler\s*\(\s*tableStr\s*\(\s*(?<a>\w+)\s*\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public string Kind;      // classStr, tableStr, formStr, formDataSourceStr, ...
        public string Element;
        public string Member;    // data source, field or control for form targets; method or delegate for handlers
        public string SubMember; // the field of formDataFieldStr
        public string Attribute; // ExtensionOf, PreHandlerFor, PostHandlerFor, SubscribesTo, DataEventHandler

        public static XppTarget ParseExtensionOf(string declaration)
        {
            var m = ExtensionOf.Match(XppSignature.StripComments(declaration ?? ""));
            if (!m.Success) return null;
            return new XppTarget
            {
                Attribute = "ExtensionOf",
                Kind = m.Groups["kind"].Value,
                Element = m.Groups["a"].Value,
                Member = m.Groups["b"].Success ? m.Groups["b"].Value : null,
                SubMember = m.Groups["c"].Success ? m.Groups["c"].Value : null,
            };
        }

        /// <summary>Event handler subscriptions in a method's attributes.</summary>
        public static IEnumerable<XppTarget> ParseHandlers(XppSignature signature)
        {
            foreach (string attribute in signature?.Attributes ?? new List<string>())
            {
                var m = Handler.Match(attribute);
                if (m.Success)
                {
                    yield return new XppTarget { Attribute = m.Groups["attr"].Value, Kind = m.Groups["kind"].Value, Element = m.Groups["a"].Value, Member = m.Groups["m"].Value };
                    continue;
                }
                var d = DataEvent.Match(attribute);
                if (d.Success) yield return new XppTarget { Attribute = "DataEventHandler", Kind = "tableStr", Element = d.Groups["a"].Value };
            }
        }

        /// <summary>True when a Chain of Command method calls next on itself.</summary>
        public static bool IsWrapper(string methodSource, string name) =>
            !string.IsNullOrEmpty(methodSource) && Regex.IsMatch(XppSignature.StripComments(methodSource), @"\bnext\s+" + Regex.Escape(name) + @"\s*\(", RegexOptions.IgnoreCase);
    }
}
